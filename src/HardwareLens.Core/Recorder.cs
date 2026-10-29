using System.Globalization;
using System.Text;
namespace HardwareLens.Core;

/// <summary>
/// Lightweight local recording: two append-only CSV files (device metrics in long format, and the busiest processes) plus a small
/// description file. Growth is bounded: recording stops when the size cap is reached. Unavailable readings are written as empty fields.
/// </summary>
public sealed class CsvRecorder : IDisposable
{
    readonly StreamWriter device, process;
    readonly long maxBytes; long bytes; DateTime lastFlush = DateTime.UtcNow;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public string DevicePath { get; }
    public string ProcessPath { get; }
    public string InfoPath { get; }
    public long BytesWritten => bytes;
    public bool Active { get; private set; } = true;
    public string? StoppedReason { get; private set; }
    public int Rows { get; private set; }

    public CsvRecorder(string folder, long maxBytes, double intervalSeconds, int logicalProcessors)
    {
        Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder, "HardwareLens-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", C));
        DevicePath = stem + "-device.csv"; ProcessPath = stem + "-processes.csv"; InfoPath = stem + "-info.json";
        this.maxBytes = maxBytes;
        device = new StreamWriter(new FileStream(DevicePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        process = new StreamWriter(new FileStream(ProcessPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        File.WriteAllText(InfoPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            app = "HardwareLens", startedUtc = DateTime.UtcNow.ToString("o", C), refreshSeconds = intervalSeconds, logicalProcessors,
            sizeCapBytes = maxBytes,
            deviceFile = Path.GetFileName(DevicePath), processFile = Path.GetFileName(ProcessPath),
            notes = new[]
            {
                "Device file is long format: time_utc, metric, instance, value, unit. Empty value = reading unavailable (never zero-filled).",
                "cpu_total_pct and process cpu_pct are normalized: 100 = all logical processors fully busy.",
                "Process file lists the 8 busiest processes by CPU plus the 3 largest by working set per sample. io_* are all I/O the process requested (files, pipes, devices, network), not physical disk traffic.",
                "Network throughput is per adapter; per-process network is not measured.",
                "Recording stops automatically when the size cap is reached. Nothing is uploaded anywhere."
            }
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        WriteDevice("time_utc,metric,instance,value,unit"); WriteProcess("time_utc,pid,name,cpu_pct,working_set_bytes,private_working_set_bytes,io_read_bytes_per_s,io_write_bytes_per_s,gpu_engine_pct");
    }

    void WriteDevice(string line) { device.WriteLine(line); bytes += Encoding.UTF8.GetByteCount(line) + 2; }
    void WriteProcess(string line) { process.WriteLine(line); bytes += Encoding.UTF8.GetByteCount(line) + 2; }
    static string Esc(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    static string V(double? v) => v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? "" : v.Value.ToString("0.###", C);

    public void Write(LiveSample s)
    {
        if (!Active || s.IsBaseline) return;
        if (bytes >= maxBytes) { Stop("Size limit of " + Fmt.Bytes(maxBytes, 0) + " reached"); return; }
        string t = s.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", C);
        void D(string metric, string inst, double? v, string unit) => WriteDevice($"{t},{metric},{Esc(inst)},{V(v)},{unit}");
        D("cpu_total_pct", "", s.CpuPercent, "percent"); D("cpu_utility_pct", "", s.CpuUtilityPercent, "percent"); D("cpu_clock_mhz", "", s.CpuClockMHz, "MHz");
        if (s.Memory is { } m) { D("mem_used_pct", "", m.UsedPercent, "percent"); D("mem_available_bytes", "", m.AvailableBytes, "bytes"); D("mem_commit_pct", "", m.CommitPercent, "percent"); }
        foreach (var g in s.Gpus) { D("gpu_engine_pct", g.Name, g.EngineUtilPercent, "percent"); D("gpu_dedicated_bytes", g.Name, g.DedicatedUsedBytes, "bytes"); D("gpu_shared_bytes", g.Name, g.SharedUsedBytes, "bytes"); }
        foreach (var d in s.Disks) { string n = "Disk " + d.Number; D("disk_active_pct", n, d.ActivePercent, "percent"); D("disk_read_bytes_per_s", n, d.ReadBps, "bytes/s"); D("disk_write_bytes_per_s", n, d.WriteBps, "bytes/s"); D("disk_latency_ms", n, d.LatencyMs, "ms"); }
        foreach (var n in s.Nets.Where(x => x.InTotal)) { D("net_receive_bytes_per_s", n.Name, n.RxBps, "bytes/s"); D("net_send_bytes_per_s", n.Name, n.TxBps, "bytes/s"); }
        var top = s.Processes.Where(p => p.CpuPercent != null).OrderByDescending(p => p.CpuPercent).Take(8)
            .Concat(s.Processes.OrderByDescending(p => p.WorkingSetBytes).Take(3)).DistinctBy(p => p.Key);
        foreach (var p in top)
            WriteProcess($"{t},{p.Pid},{Esc(p.Name)},{V(p.CpuPercent)},{p.WorkingSetBytes:0},{p.PrivateWorkingSetBytes:0},{V(p.IoReadBps)},{V(p.IoWriteBps)},{V(p.GpuPercent)}");
        Rows++;
        if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 5) { device.Flush(); process.Flush(); lastFlush = DateTime.UtcNow; }
    }

    public void Stop(string reason)
    {
        if (!Active) return;
        Active = false; StoppedReason = reason;
        try { device.Flush(); process.Flush(); } catch { }
        device.Dispose(); process.Dispose();
    }

    public void Dispose() => Stop(StoppedReason ?? "Stopped");
}
