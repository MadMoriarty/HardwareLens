using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HardwareLens.Core;
namespace HardwareLens.Windows;

/// <summary>
/// Background sampler. Runs on its own low-priority thread (never the UI thread), feeds a bounded <see cref="TimeSeriesStore"/>,
/// and publishes immutable <see cref="LiveSample"/> objects. Unavailable counters yield null readings, never zero.
/// </summary>
public sealed class LiveCollector : IDisposable
{
    public TimeSeriesStore Store { get; } = new();
    public ProcessTracker Tracker { get; } = new();
    public SourceHealth Health { get; } = new();
    public event Action<LiveSample>? SampleReady;
    public event Action<IReadOnlyList<Observation>>? ObservationsReady;
    public LiveSample? Latest { get; private set; }
    /// <summary>Latest sample that carries data (not a post-gap baseline).</summary>
    public LiveSample? LatestData { get; private set; }
    public IReadOnlyList<Observation> Observations { get; private set; } = Array.Empty<Observation>();
    public int LogicalProcessors { get; } = Math.Max(1, (int)Native.GetActiveProcessorCount(0xFFFF));
    public double LastCollectMilliseconds { get; private set; }
    public int SelfPid { get; } = Environment.ProcessId;
    public string? ProcessSourceError { get; private set; }
    public Func<string, int, string, string>? AppNamer { get; set; }
    public List<SourceStatus> CounterSources { get; } = new();

    readonly ObservationEngine engine;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    volatile bool stop, suspended, resetBaseline = true;
    TimeSpan interval = TimeSpan.FromSeconds(2);
    Inventory? inventory;
    volatile IReadOnlyList<VolumeSnapshot> volumes = Array.Empty<VolumeSnapshot>();
    readonly object recLock = new();
    CsvRecorder? recorder;

    PdhQuery? q;
    PdhQuery.Counter? cCpu, cUtil, cFreq, cPages, cDiskIdle, cDiskRead, cDiskWrite, cDiskLat, cNetRx, cNetTx, cNetBw, cGpuEng, cGpuDed, cGpuShared, cGpuProcDed;
    readonly List<KeyValuePair<string, double>> tmp = new();

    // process delta state
    byte[]? scratch; readonly List<Native.RawProcess> raw = new();
    sealed record Prev(ulong Cpu, ulong Read, ulong Write);
    Dictionary<(int, long), Prev> prevProc = new();
    long prevTs;

    public LiveCollector(TimeSpan interval, TimeSpan history)
    {
        this.interval = interval; Store.Horizon = history; Tracker.Horizon = TimeSpan.FromMinutes(10);
        engine = new ObservationEngine(Store, Tracker);
        thread = new Thread(Loop) { IsBackground = true, Name = "HardwareLens collector", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => thread.Start();
    public TimeSpan Interval { get => interval; set { interval = value; wake.Set(); } }
    public bool Suspended { get => suspended; set { suspended = value; if (!value) resetBaseline = true; wake.Set(); } }
    public void SetInventory(Inventory inv) { inventory = inv; }
    public CsvRecorder? Recorder { get { lock (recLock) return recorder; } }

    public CsvRecorder StartRecording(string folder, long maxBytes)
    {
        lock (recLock) { recorder?.Dispose(); recorder = new CsvRecorder(folder, maxBytes, interval.TotalSeconds, LogicalProcessors); return recorder; }
    }
    public CsvRecorder? StopRecording(string reason = "Stopped by user")
    {
        lock (recLock) { var r = recorder; r?.Stop(reason); return r; }
    }

    void OpenCounters()
    {
        q = new PdhQuery();
        cCpu = q.Add(@"\Processor Information(*)\% Processor Time");
        cUtil = q.Add(@"\Processor Information(*)\% Processor Utility");
        cFreq = q.Add(@"\Processor Information(*)\Processor Frequency");
        cPages = q.Add(@"\Memory\Pages/sec");
        cDiskIdle = q.Add(@"\PhysicalDisk(*)\% Idle Time");
        cDiskRead = q.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        cDiskWrite = q.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        cDiskLat = q.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        cNetRx = q.Add(@"\Network Interface(*)\Bytes Received/sec");
        cNetTx = q.Add(@"\Network Interface(*)\Bytes Sent/sec");
        cNetBw = q.Add(@"\Network Interface(*)\Current Bandwidth");
        cGpuEng = q.Add(@"\GPU Engine(*)\Utilization Percentage");
        cGpuDed = q.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        cGpuShared = q.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        cGpuProcDed = q.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        void Src(string name, string api, params PdhQuery.Counter?[] cs)
        {
            var bad = cs.Where(c => c != null && !c.Ok).ToList();
            string state = bad.Count == 0 ? "ok" : bad.Count == cs.Length ? "unavailable" : "partial";
            CounterSources.Add(new SourceStatus(name, api, state, bad.Count == 0 ? null : string.Join("; ", bad.Select(b => b!.Path + ": " + PdhQuery.Describe(b.AddStatus)))));
            if (state == "unavailable") Health.Fail(name.ToLowerInvariant().Split(' ')[0], PdhQuery.Describe(bad[0]!.AddStatus));
        }
        Src("CPU load and clock", @"PDH \Processor Information(*)", cCpu, cUtil, cFreq);
        Src("Disk activity", @"PDH \PhysicalDisk(*)", cDiskIdle, cDiskRead, cDiskWrite, cDiskLat);
        Src("Network throughput", @"PDH \Network Interface(*)", cNetRx, cNetTx, cNetBw);
        Src("GPU engine and memory", @"PDH \GPU Engine(*), \GPU Adapter Memory(*), \GPU Process Memory(*)", cGpuEng, cGpuDed, cGpuShared, cGpuProcDed);
        CounterSources.Add(new SourceStatus("Memory", "GlobalMemoryStatusEx, GetPerformanceInfo; PDH \\Memory\\Pages/sec", cPages!.Ok ? "ok" : "partial", cPages.Ok ? null : "Pages/sec unavailable"));
        CounterSources.Add(new SourceStatus("Processes", "NtQuerySystemInformation(SystemProcessInformation)", "ok", "Same call .NET's Process class uses; the structure is only partly documented by Microsoft. Layout is sanity-checked each sample."));
    }

    void Loop()
    {
        try { OpenCounters(); } catch (Exception e) { Health.Fail("cpu", e.Message); }
        long freq = Stopwatch.Frequency; long due = Stopwatch.GetTimestamp();
        var volTimer = Stopwatch.StartNew(); var obsTimer = Stopwatch.StartNew(); volumes = SafeVolumes();
        q?.Collect();
        while (!stop)
        {
            long now = Stopwatch.GetTimestamp();
            double waitMs = (due - now) * 1000.0 / freq;
            if (waitMs > 1) { wake.WaitOne((int)Math.Min(waitMs, 1000)); if (stop) break; continue; }
            if (suspended) { due = now + (long)(freq * 0.5); resetBaseline = true; continue; }
            var sw = Stopwatch.StartNew();
            try { SampleOnce(); }
            catch (Exception e) { Health.Fail("processes", "Sampling error: " + e.Message); }
            LastCollectMilliseconds = sw.Elapsed.TotalMilliseconds;
            if (volTimer.Elapsed.TotalSeconds >= 30) { volumes = SafeVolumes(); volTimer.Restart(); }
            if (obsTimer.Elapsed.TotalSeconds >= 5 || Observations.Count == 0)
            {
                obsTimer.Restart();
                try
                {
                    var cx = new ObservationContext { Inventory = inventory, Volumes = volumes, SelfPid = SelfPid, IntervalSeconds = interval.TotalSeconds, PhysicalMemoryBytes = Latest?.Memory?.TotalBytes ?? 0, AppName = AppNamer };
                    Observations = engine.Evaluate(DateTimeOffset.Now, cx); ObservationsReady?.Invoke(Observations);
                    Store.Prune(DateTimeOffset.Now);
                }
                catch (Exception e) { Health.Fail("observations", e.Message); }
            }
            due += (long)(interval.TotalSeconds * freq);
            if (due < Stopwatch.GetTimestamp() - freq) due = Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * freq);
        }
        q?.Dispose();
    }

    static List<VolumeSnapshot> SafeVolumes() { try { return InventoryCollector.ReadVolumes(); } catch { return new(); } }

    // ---------------------------------------------------------------------------------------------
    void SampleOnce()
    {
        var time = DateTimeOffset.Now; long ts = Stopwatch.GetTimestamp();
        double dt = prevTs == 0 ? 0 : (ts - prevTs) / (double)Stopwatch.Frequency;
        bool gap = prevTs != 0 && dt > Math.Max(interval.TotalSeconds * 3, 6);
        uint cr = q?.Collect() ?? 1;
        bool baseline = resetBaseline || gap || prevTs == 0;
        if (baseline)
        {
            if (prevTs != 0) Store.AppendGapToAll(time); // sleep/resume or paused collection: leave a visible gap
            resetBaseline = false; prevTs = ts;
            SnapshotProcesses(ts, 0, null, null, null, out _, out _, out _, baselineOnly: true);
            Publish(new LiveSample { Time = time, IntervalSeconds = 0, IsBaseline = true, Memory = ReadMemory(null) });
            return;
        }
        prevTs = ts;
        bool pdhOk = cr == 0 || cr == 0x800007D5;

        // ---- CPU ----
        double? cpu = null, util = null, clock = null; double[]? cores = null; uint s1 = 0;
        if (pdhOk && q != null && cCpu != null && q.Read(cCpu, tmp, out s1))
        {
            cpu = Clamp(tmp.FirstOrDefault(k => k.Key == "_Total").Value, tmp.Any(k => k.Key == "_Total"));
            cores = tmp.Where(k => Regex.IsMatch(k.Key, @"^\d+,\d+$")).OrderBy(k => int.Parse(k.Key.Split(',')[0])).ThenBy(k => int.Parse(k.Key.Split(',')[1])).Select(k => Math.Clamp(k.Value, 0, 100)).ToArray();
            if (cores.Length == 0) cores = null;
            Health.Ok("cpu", time);
        }
        else if (cCpu?.Ok == true) Health.Fail("cpu", pdhOk ? PdhQuery.Describe(s1) : "PdhCollectQueryData failed");
        if (pdhOk && q != null && cUtil != null && q.Read(cUtil, tmp, out _) && tmp.Any(k => k.Key == "_Total")) util = Math.Max(0, tmp.First(k => k.Key == "_Total").Value);
        if (pdhOk && q != null && cFreq != null && q.Read(cFreq, tmp, out _) && tmp.Any(k => k.Key == "_Total")) { var f = tmp.First(k => k.Key == "_Total").Value; if (f > 0) clock = f; }

        // ---- Memory ----
        double? pages = null;
        if (pdhOk && q != null && cPages != null && q.Read(cPages, tmp, out _) && tmp.Count > 0) pages = tmp[0].Value;
        var mem = ReadMemory(pages);
        if (mem != null) Health.Ok("memory", time); else Health.Fail("memory", "GlobalMemoryStatusEx failed");

        // ---- GPU ----
        var gpuProcPct = new Dictionary<int, double>(); var gpuProcMem = new Dictionary<int, double>();
        var gpus = ReadGpu(pdhOk, time, gpuProcPct, gpuProcMem);

        // ---- Disks ----
        var disks = ReadDisks(pdhOk, time);
        // ---- Network ----
        var nets = ReadNets(pdhOk, time);

        // ---- Processes ----
        var procs = SnapshotProcesses(ts, dt, gpuProcPct, gpuProcMem, null, out int pcount, out int threads, out int handles, baselineOnly: false);

        // ---- store ----
        var S = Store;
        S.Append(SeriesKeys.Cpu, time, cpu); S.Append(SeriesKeys.CpuUtility, time, util); S.Append(SeriesKeys.CpuClock, time, clock);
        if (mem != null) { S.Append(SeriesKeys.MemUsedPct, time, mem.UsedPercent); S.Append(SeriesKeys.MemAvailBytes, time, mem.AvailableBytes); S.Append(SeriesKeys.MemCommitPct, time, mem.CommitPercent); }
        foreach (var g in gpus)
        {
            S.Append(SeriesKeys.GpuUtil(g.Id), time, g.EngineUtilPercent); S.Append(SeriesKeys.GpuDedBytes(g.Id), time, g.DedicatedUsedBytes); S.Append(SeriesKeys.GpuShared(g.Id), time, g.SharedUsedBytes);
            S.Append(SeriesKeys.GpuDedPct(g.Id), time, g.DedicatedUsedBytes is double du && g.DedicatedTotalBytes is double dtt && dtt > 0 ? Math.Min(100, du / dtt * 100) : null);
        }
        double? sumR = null, sumW = null;
        foreach (var d in disks)
        {
            S.Append(SeriesKeys.DiskActive(d.Number), time, d.ActivePercent); S.Append(SeriesKeys.DiskRead(d.Number), time, d.ReadBps); S.Append(SeriesKeys.DiskWrite(d.Number), time, d.WriteBps); S.Append(SeriesKeys.DiskLatency(d.Number), time, d.LatencyMs);
            if (d.ReadBps is double r) sumR = (sumR ?? 0) + r; if (d.WriteBps is double w) sumW = (sumW ?? 0) + w;
        }
        S.Append(SeriesKeys.DiskTotalRead, time, sumR); S.Append(SeriesKeys.DiskTotalWrite, time, sumW);
        double? totRx = null, totTx = null;
        foreach (var n in nets)
        {
            S.Append(SeriesKeys.NetRx(n.Key), time, n.RxBps); S.Append(SeriesKeys.NetTx(n.Key), time, n.TxBps);
            if (n.InTotal) { if (n.RxBps is double a) totRx = (totRx ?? 0) + a; if (n.TxBps is double b) totTx = (totTx ?? 0) + b; }
        }
        S.Append(SeriesKeys.NetTotalRx, time, totRx); S.Append(SeriesKeys.NetTotalTx, time, totTx);
        Tracker.Update(time, procs);

        Publish(new LiveSample { Time = time, IntervalSeconds = dt, CpuPercent = cpu, CpuUtilityPercent = util, CpuClockMHz = clock, CoreBusy = cores, Memory = mem, Gpus = gpus, Disks = disks, Nets = nets, Processes = procs, ProcessCount = pcount, ThreadCount = threads, HandleCount = handles });
    }

    static double? Clamp(double v, bool present) => present ? Math.Clamp(v, 0, 100) : null;

    void Publish(LiveSample s)
    {
        Latest = s; if (!s.IsBaseline) LatestData = s;
        try { lock (recLock) recorder?.Write(s); } catch (Exception e) { StopRecording("Recording stopped: " + e.Message); }
        SampleReady?.Invoke(s);
    }

    MemorySample? ReadMemory(double? pages)
    {
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref ms)) return null;
        double commitUsed = ms.ullTotalPageFile - ms.ullAvailPageFile, commitLimit = ms.ullTotalPageFile, cache = double.NaN;
        if (Native.GetPerformanceInfo(out var pi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
        {
            commitUsed = (double)pi.CommitTotal * (double)pi.PageSize; commitLimit = (double)pi.CommitLimit * (double)pi.PageSize; cache = (double)pi.SystemCache * (double)pi.PageSize;
        }
        return new MemorySample(ms.ullTotalPhys, ms.ullAvailPhys, commitUsed, commitLimit, double.IsNaN(cache) ? null : cache, pages);
    }

    // ---------------- GPU ----------------
    static string NormLuid(string h, string l) => ("0x" + h.Substring(2).ToUpperInvariant().PadLeft(8, '0') + "_0x" + l.Substring(2).ToUpperInvariant().PadLeft(8, '0'));

    List<GpuSample> ReadGpu(bool pdhOk, DateTimeOffset time, Dictionary<int, double> procPct, Dictionary<int, double> procMem)
    {
        var devs = inventory?.Gpus.Where(g => !g.IsSoftware && g.LuidKey.Length > 0).ToList() ?? new();
        var engineSum = new Dictionary<(string luid, string engine), double>(); var engineType = new Dictionary<(string, string), string>();
        var ded = new Dictionary<string, double>(); var shr = new Dictionary<string, double>();
        bool engOk = false, dedOk = false, shrOk = false;
        if (pdhOk && q != null)
        {
            if (cGpuEng != null && q.Read(cGpuEng, tmp, out _))
            {
                engOk = true;
                foreach (var kv in tmp)
                {
                    var t = kv.Key.Split('_');
                    if (t.Length < 11 || t[0] != "pid" || t[2] != "luid" || t[5] != "phys" || t[7] != "eng") continue;
                    if (!int.TryParse(t[1], out int pid)) continue;
                    string luid = NormLuid(t[3], t[4]); string engKey = t[6] + "_" + t[8]; string type = string.Join("_", t.Skip(10));
                    double v = Math.Max(0, kv.Value);
                    engineSum[(luid, engKey)] = engineSum.GetValueOrDefault((luid, engKey)) + v; engineType[(luid, engKey)] = type;
                    procPct[pid] = Math.Max(procPct.GetValueOrDefault(pid), Math.Min(100, v));
                }
            }
            if (cGpuDed != null && q.Read(cGpuDed, tmp, out _))
            {
                dedOk = true;
                foreach (var kv in tmp) { var t = kv.Key.Split('_'); if (t.Length >= 3 && t[0] == "luid") { string l = NormLuid(t[1], t[2]); ded[l] = ded.GetValueOrDefault(l) + kv.Value; } }
            }
            if (cGpuShared != null && q.Read(cGpuShared, tmp, out _))
            {
                shrOk = true;
                foreach (var kv in tmp) { var t = kv.Key.Split('_'); if (t.Length >= 3 && t[0] == "luid") { string l = NormLuid(t[1], t[2]); shr[l] = shr.GetValueOrDefault(l) + kv.Value; } }
            }
            if (cGpuProcDed != null && q.Read(cGpuProcDed, tmp, out _))
                foreach (var kv in tmp) { var t = kv.Key.Split('_'); if (t.Length >= 2 && t[0] == "pid" && int.TryParse(t[1], out int pid)) procMem[pid] = procMem.GetValueOrDefault(pid) + kv.Value; }
        }
        if (engOk || dedOk || shrOk) Health.Ok("gpu", time); else if (cGpuEng?.Ok == true) Health.Fail("gpu", "GPU counters returned no data");

        // luids known from counters but missing from inventory (inventory not loaded yet) are skipped: names are unknown.
        var list = new List<GpuSample>();
        foreach (var g in devs)
        {
            double? util = null; string? busy = null;
            if (engOk)
            {
                var mine = engineSum.Where(e => e.Key.luid == g.LuidKey).ToList();
                if (mine.Count > 0) { var top = mine.OrderByDescending(e => e.Value).First(); util = Math.Min(100, top.Value); busy = engineType[top.Key]; }
                else util = 0; // counter set present and the adapter reported no active engines: genuinely idle
            }
            list.Add(new GpuSample(g.LuidKey, g.Name, util, busy, dedOk ? ded.GetValueOrDefault(g.LuidKey) : null, shrOk ? shr.GetValueOrDefault(g.LuidKey) : null, g.DedicatedVramBytes));
        }
        return list;
    }

    // ---------------- Disks ----------------
    List<DiskSample> ReadDisks(bool pdhOk, DateTimeOffset time)
    {
        var res = new List<DiskSample>();
        if (!pdhOk || q == null || cDiskIdle == null) return res;
        var idle = new Dictionary<string, double>(); var rd = new Dictionary<string, double>(); var wr = new Dictionary<string, double>(); var lat = new Dictionary<string, double>();
        bool ok = q.Read(cDiskIdle, tmp, out _); if (ok) foreach (var kv in tmp) idle[kv.Key] = kv.Value;
        if (cDiskRead != null && q.Read(cDiskRead, tmp, out _)) foreach (var kv in tmp) rd[kv.Key] = kv.Value;
        if (cDiskWrite != null && q.Read(cDiskWrite, tmp, out _)) foreach (var kv in tmp) wr[kv.Key] = kv.Value;
        if (cDiskLat != null && q.Read(cDiskLat, tmp, out _)) foreach (var kv in tmp) lat[kv.Key] = kv.Value;
        if (ok) Health.Ok("disk", time); else { Health.Fail("disk", "Disk counters returned no data"); return res; }
        foreach (var inst in idle.Keys.Where(k => k != "_Total").OrderBy(k => k))
        {
            var m = Regex.Match(inst, @"^(\d+)"); if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value);
            string name = inventory?.Disks.FirstOrDefault(d => d.Number == n)?.Name ?? "";
            res.Add(new DiskSample(n, name, inst, Math.Clamp(100 - idle[inst], 0, 100), rd.TryGetValue(inst, out var r) ? r : null, wr.TryGetValue(inst, out var w) ? w : null, lat.TryGetValue(inst, out var l) ? l * 1000 : null));
        }
        return res;
    }

    // ---------------- Network ----------------
    static string Sanitize(string s) => Regex.Replace(s.ToLowerInvariant().Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('\\', '_').Replace('/', '_'), @"[^a-z0-9\[\]]", "");
    static readonly Regex VirtualHint = new("isatap|loopback|teredo|6to4|vethernet|virtual|vmware|hyper-v|tunnel|bluetooth|miniport|wan |pseudo", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    Dictionary<string, bool> nicUp = new(); DateTime nicUpAt = DateTime.MinValue;
    /// <summary>Fallback to .NET's adapter list: true when the named adapter is a connected Ethernet/Wi-Fi adapter that is not obviously virtual.</summary>
    bool NicUp(string baseName)
    {
        if ((DateTime.UtcNow - nicUpAt).TotalSeconds > 10)
        {
            var d = new Dictionary<string, bool>();
            try
            {
                foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    bool kind = n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211;
                    bool up = n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && kind && !VirtualHint.IsMatch(n.Description);
                    d[Sanitize(n.Description)] = up;
                }
            }
            catch { }
            nicUp = d; nicUpAt = DateTime.UtcNow;
        }
        return nicUp.TryGetValue(Sanitize(baseName), out var v) && v;
    }

    List<NetSample> ReadNets(bool pdhOk, DateTimeOffset time)
    {
        var res = new List<NetSample>();
        if (!pdhOk || q == null || cNetRx == null) return res;
        var rx = new Dictionary<string, double>(); var tx = new Dictionary<string, double>(); var bw = new Dictionary<string, double>();
        bool ok = q.Read(cNetRx, tmp, out _); if (ok) foreach (var kv in tmp) rx[kv.Key] = kv.Value;
        if (cNetTx != null && q.Read(cNetTx, tmp, out _)) foreach (var kv in tmp) tx[kv.Key] = kv.Value;
        if (cNetBw != null && q.Read(cNetBw, tmp, out _)) foreach (var kv in tmp) bw[kv.Key] = kv.Value;
        if (ok) Health.Ok("network", time); else { Health.Fail("network", "Network counters returned no data"); return res; }
        foreach (var inst in rx.Keys.OrderBy(k => k))
        {
            string baseName = Regex.Replace(inst, @"\s#\d+$", "");
            var match = inventory?.Nics.FirstOrDefault(n => Sanitize(n.Description) == Sanitize(baseName));
            bool inTotal = match != null ? match.IsPhysical && !match.IsVirtual && string.Equals(match.Status, "Up", StringComparison.OrdinalIgnoreCase)
                                         : inventory == null && !VirtualHint.IsMatch(inst);
            if (!inTotal) { var ni = NicUp(baseName); if (ni) inTotal = true; }
            res.Add(new NetSample(Sanitize(inst), match?.Description ?? inst, inTotal, rx[inst], tx.TryGetValue(inst, out var t) ? t : null, bw.TryGetValue(inst, out var b) && b > 0 ? b : null));
        }
        return res;
    }

    // ---------------- Processes ----------------
    List<ProcessSample> SnapshotProcesses(long ts, double dt, Dictionary<int, double>? gpuPct, Dictionary<int, double>? gpuMem, object? _, out int count, out int threads, out int handles, bool baselineOnly)
    {
        count = 0; threads = 0; handles = 0;
        var result = new List<ProcessSample>();
        if (!Native.TrySnapshotProcesses(ref scratch, raw, out string? err)) { ProcessSourceError = err; Health.Fail("processes", err ?? "failed"); return result; }
        ProcessSourceError = null; Health.Ok("processes", DateTimeOffset.Now);
        var next = new Dictionary<(int, long), Prev>(raw.Count);
        double wall100ns = dt * 1e7;
        foreach (var p in raw)
        {
            if (p.Pid == 0) continue; // System Idle Process: idle time, not a consumer
            var key = (p.Pid, p.CreateTime);
            next[key] = new Prev(p.CpuTime100ns, p.ReadBytes, p.WriteBytes);
            threads += p.Threads; handles += p.Handles; count++;
            if (baselineOnly) continue;
            double? cpu = null, rr = null, wr = null;
            if (prevProc.TryGetValue(key, out var pv) && dt > 0)
            {
                cpu = Math.Clamp((p.CpuTime100ns - pv.Cpu) / (wall100ns * LogicalProcessors) * 100.0, 0, 100);
                rr = p.ReadBytes >= pv.Read ? (p.ReadBytes - pv.Read) / dt : null; wr = p.WriteBytes >= pv.Write ? (p.WriteBytes - pv.Write) / dt : null;
            }
            result.Add(new ProcessSample(p.Pid, p.CreateTime, p.Name, cpu, p.WorkingSet, p.PrivateWorkingSet, p.Commit, rr, wr,
                gpuPct != null && gpuPct.TryGetValue(p.Pid, out var gp) ? gp : (gpuPct != null && gpuPct.Count > 0 ? 0 : null),
                gpuMem != null && gpuMem.TryGetValue(p.Pid, out var gm) ? gm : null, p.Threads, p.Handles));
        }
        prevProc = next;
        return result;
    }

    public void Dispose()
    {
        stop = true; wake.Set();
        try { thread.Join(2000); } catch { }
        StopRecording("Application closed");
    }
}
