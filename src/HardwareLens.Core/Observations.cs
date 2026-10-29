using System.Globalization;
namespace HardwareLens.Core;

public enum ObservationState { NotObserved, Collecting, Brief, Possible, Observed }
public enum ObservationLevel { Info = 0, Notice = 1, Attention = 2 }

public sealed class Observation
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public ObservationState State { get; set; }
    public ObservationLevel Level { get; set; }
    /// <summary>The measurement supporting the observation (what was seen).</summary>
    public string Measurement { get; set; } = "";
    /// <summary>How long / over what window it was seen.</summary>
    public string Duration { get; set; } = "";
    public string Explanation { get; set; } = "";
    /// <summary>What the observation does NOT prove.</summary>
    public string Caveat { get; set; } = "";
    public List<string> Details { get; set; } = new();
    public string StateLabel => State switch
    {
        ObservationState.Possible => "Possible bottleneck",
        ObservationState.Observed => "Observed",
        ObservationState.Brief => "Brief reading, not sustained",
        ObservationState.Collecting => "Collecting data",
        _ => "Not observed"
    };
}

public sealed class VolumeSnapshot
{
    public string Letter { get; set; } = ""; public string? Label { get; set; }
    public ulong SizeBytes { get; set; } public ulong FreeBytes { get; set; }
}

public sealed class ObservationContext
{
    public Inventory? Inventory { get; set; }
    public IReadOnlyList<VolumeSnapshot> Volumes { get; set; } = Array.Empty<VolumeSnapshot>();
    public int SelfPid { get; set; }
    public double IntervalSeconds { get; set; } = 2;
    public double PhysicalMemoryBytes { get; set; }
    public bool CollectionActive { get; set; } = true;
    /// <summary>Turns a process (key, name) into a plain-English app name for display.</summary>
    public Func<string, int, string, string>? AppName { get; set; }
}

public sealed record RuleInfo(string Id, string Name, string Condition, string Window, string Notes);

/// <summary>All thresholds are plain constants so the rules are fully transparent and testable.</summary>
public static class ObservationRules
{
    public static readonly TimeSpan CpuWindow = TimeSpan.FromMinutes(5);
    public const double CpuThreshold = 90;
    public static readonly TimeSpan MemoryWindow = TimeSpan.FromMinutes(3);
    public const double MemoryUsedThreshold = 90, CommitThreshold = 90;
    public static readonly TimeSpan VramWindow = TimeSpan.FromMinutes(3);
    public const double VramThreshold = 90, VramLowComputeBelow = 25;
    public static readonly TimeSpan DiskWindow = TimeSpan.FromMinutes(5);
    public const double DiskActiveThreshold = 90;
    public const double FreeSpaceNoticePercent = 10, FreeSpaceAttentionPercent = 5;
    public const double FreeSpaceNoticeBytes = 10.0 * 1024 * 1024 * 1024, FreeSpaceAttentionBytes = 3.0 * 1024 * 1024 * 1024;
    public static readonly TimeSpan ProcessWindow = TimeSpan.FromMinutes(5);
    public const double ProcessCpuThreshold = 25, ProcessMemoryFractionOfRam = 0.15, ProcessIoBytesPerSec = 50.0 * 1024 * 1024, ProcessGpuThreshold = 50;
    /// <summary>A reading must be at/above the threshold in at least this share of samples across the whole window.</summary>
    public const double MinFractionAbove = 0.80;
    /// <summary>A rule only fires once at least this share of its window has actually been collected.</summary>
    public const double MinCoverage = 0.90;

    public static IReadOnlyList<RuleInfo> Describe() => new[]
    {
        new RuleInfo("cpu", "Sustained CPU saturation", $"Total CPU ≥ {CpuThreshold:0}% in ≥ {MinFractionAbove:P0} of samples", "5 min", "Needs ≥ 90% of the window collected. A short spike never qualifies."),
        new RuleInfo("memory", "Memory pressure", $"Memory in use ≥ {MemoryUsedThreshold:0}% of RAM (available < {100 - MemoryUsedThreshold:0}%), or commit charge ≥ {CommitThreshold:0}% of the commit limit, in ≥ {MinFractionAbove:P0} of samples", "3 min", "Available memory already counts reusable file cache, so ordinary caching does not trigger this."),
        new RuleInfo("vram", "High VRAM allocation", $"Dedicated video memory in use ≥ {VramThreshold:0}% on a discrete GPU in ≥ {MinFractionAbove:P0} of samples; GPU engine load is reported next to it", "3 min", "Allocation is not the same as GPU load. Integrated GPUs share system RAM and are not judged by this rule."),
        new RuleInfo("disk", "Sustained disk activity", $"Disk active time ≥ {DiskActiveThreshold:0}% in ≥ {MinFractionAbove:P0} of samples, per physical disk", "5 min", "Active time means the disk had requests outstanding; fast SSDs/NVMe can show 100% while still having headroom. Latency is shown as evidence."),
        new RuleInfo("freespace", "Low free disk space", $"Volume free space < {FreeSpaceNoticePercent:0}% or < 10 GiB (notice); < {FreeSpaceAttentionPercent:0}% or < 3 GiB (attention)", "Instant", "A fact about current free space, not a trend."),
        new RuleInfo("process", "Processes consistently using substantial resources", $"Per process, in ≥ {MinFractionAbove:P0} of samples: CPU ≥ {ProcessCpuThreshold:0}% of all threads; working set ≥ {ProcessMemoryFractionOfRam:P0} of RAM; general I/O ≥ 50 MiB/s; GPU engine ≥ {ProcessGpuThreshold:0}%", "5 min", "The process must have been observed for most of the window. Process I/O counts all I/O (files, pipes, network), not only the physical disk."),
    };
}

public sealed class ObservationEngine
{
    readonly TimeSeriesStore store; readonly ProcessTracker tracker;
    public ObservationEngine(TimeSeriesStore store, ProcessTracker tracker) { this.store = store; this.tracker = tracker; }
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    (ObservationState state, WindowStats st) Judge(SeriesData d, double thr, TimeSpan win, double interval)
    {
        var st = SeriesMath.Stats(d, thr, interval);
        if (st.Count == 0) return (ObservationState.Collecting, st);
        if (st.CoverageSeconds < win.TotalSeconds * ObservationRules.MinCoverage) return (ObservationState.Collecting, st);
        if (st.FractionAtOrAbove >= ObservationRules.MinFractionAbove) return (ObservationState.Possible, st);
        if (st.Max >= thr) return (ObservationState.Brief, st);
        return (ObservationState.NotObserved, st);
    }

    static string Win(WindowStats st, TimeSpan win)
        => $"last {Fmt.Duration(win.TotalSeconds)} (observed {Fmt.Duration(st.CoverageSeconds)})";
    static string Progress(WindowStats st, TimeSpan win)
        => $"Collected {Fmt.Duration(st.CoverageSeconds)} of the {Fmt.Duration(win.TotalSeconds)} this rule needs";

    public List<Observation> Evaluate(DateTimeOffset now, ObservationContext cx)
    {
        var list = new List<Observation>();
        double iv = Math.Max(cx.IntervalSeconds, 0.5);
        list.Add(Cpu(now, iv));
        list.Add(Memory(now, iv));
        list.AddRange(Vram(now, iv, cx));
        list.AddRange(Disks(now, iv, cx));
        list.Add(FreeSpace(cx));
        list.Add(Processes(now, iv, cx));
        return list;
    }

    Observation Cpu(DateTimeOffset now, double iv)
    {
        var win = ObservationRules.CpuWindow; var (state, st) = Judge(store.Get(SeriesKeys.Cpu, now, win), ObservationRules.CpuThreshold, win, iv);
        var o = new Observation { Id = "cpu", Category = "CPU", Title = "Sustained CPU saturation", State = state, Level = ObservationLevel.Notice,
            Caveat = "This is not a diagnosis. Heavy CPU use is normal during builds, renders, scans, updates and games; it only matters if the machine feels slow while it lasts. The Processes view shows what is responsible." };
        if (st.Count == 0) { o.Measurement = "No CPU readings yet."; o.Duration = Progress(st, win); return o; }
        o.Measurement = $"Total CPU averaged {st.Mean:0}% (peak {st.Max:0}%); {st.FractionAtOrAbove:P0} of samples were at or above {ObservationRules.CpuThreshold:0}%.";
        o.Duration = state == ObservationState.Collecting ? Progress(st, win) : Win(st, win);
        o.Explanation = state switch
        {
            ObservationState.Possible => "Processor time stayed saturated for the whole window, so work that needs the CPU may have been waiting. This is a possible CPU bottleneck, not a confirmed cause of any slowness.",
            ObservationState.Brief => "CPU reached the threshold but did not stay there. Short bursts are normal and are not treated as a problem.",
            ObservationState.Collecting => "Not enough history yet to judge. A single busy moment is never reported as a problem.",
            _ => "CPU was not saturated during this window."
        };
        return o;
    }

    Observation Memory(DateTimeOffset now, double iv)
    {
        var win = ObservationRules.MemoryWindow;
        var (s1, st1) = Judge(store.Get(SeriesKeys.MemUsedPct, now, win), ObservationRules.MemoryUsedThreshold, win, iv);
        var (s2, st2) = Judge(store.Get(SeriesKeys.MemCommitPct, now, win), ObservationRules.CommitThreshold, win, iv);
        ObservationState state = s1 == ObservationState.Possible || s2 == ObservationState.Possible ? ObservationState.Possible
            : s1 == ObservationState.Collecting && s2 == ObservationState.Collecting ? ObservationState.Collecting
            : s1 == ObservationState.Brief || s2 == ObservationState.Brief ? ObservationState.Brief : ObservationState.NotObserved;
        var o = new Observation { Id = "memory", Category = "Memory", Title = "Memory pressure", State = state, Level = ObservationLevel.Notice,
            Caveat = "Windows deliberately fills spare RAM with reusable cache, and that cache counts as available. High usage by itself is normal; this rule looks at what is left available and at the commit limit. Not a diagnosis of a leak or failing RAM." };
        if (st1.Count == 0 && st2.Count == 0) { o.Measurement = "No memory readings yet."; o.Duration = Progress(st1, win); return o; }
        o.Measurement = $"Memory in use averaged {st1.Mean:0}% of RAM (peak {st1.Max:0}%; available ≈ {100 - st1.Mean:0}% on average). Commit charge averaged {st2.Mean:0}% of the commit limit (peak {st2.Max:0}%).";
        o.Duration = state == ObservationState.Collecting ? Progress(st1, win) : Win(st1, win);
        o.Explanation = state switch
        {
            ObservationState.Possible => "Little memory stayed available (or the commit limit was nearly reached) for the whole window. Windows may have been paging; this is a possible memory bottleneck, not a confirmed one.",
            ObservationState.Brief => "Memory use touched the threshold briefly but did not stay there.",
            ObservationState.Collecting => "Not enough history yet to judge.",
            _ => "Plenty of memory stayed available during this window."
        };
        return o;
    }

    IEnumerable<Observation> Vram(DateTimeOffset now, double iv, ObservationContext cx)
    {
        var win = ObservationRules.VramWindow;
        var gpus = cx.Inventory?.Gpus.Where(g => !g.IsSoftware && g.Architecture == "Discrete" && g.DedicatedVramBytes > 0).ToList() ?? new();
        if (gpus.Count == 0)
        {
            yield return new Observation { Id = "vram", Category = "GPU", Title = "High VRAM allocation", State = ObservationState.NotObserved, Level = ObservationLevel.Info,
                Measurement = "No discrete GPU with dedicated video memory was identified.", Duration = "—",
                Explanation = "Integrated GPUs use shared system memory, so this rule does not apply to them.", Caveat = "" };
            yield break;
        }
        foreach (var g in gpus)
        {
            var (state, st) = Judge(store.Get(SeriesKeys.GpuDedPct(g.LuidKey), now, win), ObservationRules.VramThreshold, win, iv);
            var util = SeriesMath.Stats(store.Get(SeriesKeys.GpuUtil(g.LuidKey), now, win), null, iv);
            var o = new Observation { Id = "vram:" + g.LuidKey, Category = "GPU", Title = "High VRAM allocation — " + g.Name, State = state, Level = ObservationLevel.Notice,
                Caveat = "Allocated video memory is not GPU load: applications often reserve more VRAM than they actively use. This is only a possible limit if a graphics workload also stutters or spills into shared memory." };
            if (st.Count == 0) { o.Measurement = "No VRAM readings (GPU memory counters unavailable or no data yet)."; o.Duration = Progress(st, win); yield return o; continue; }
            string comp = util.Count > 0 ? $"GPU engine load averaged {util.Mean:0}% (busiest engine) over the same window." : "GPU engine load was not available.";
            o.Measurement = $"Dedicated VRAM in use averaged {st.Mean:0}% of {Fmt.Bytes(g.DedicatedVramBytes)} (peak {st.Max:0}%). {comp}";
            o.Duration = state == ObservationState.Collecting ? Progress(st, win) : Win(st, win);
            o.Explanation = state switch
            {
                ObservationState.Possible when util.Count > 0 && util.Mean < ObservationRules.VramLowComputeBelow => "Most video memory is allocated while the GPU itself is mostly idle. That points to reserved memory (for example by a game, browser or editor) rather than heavy GPU work.",
                ObservationState.Possible => "Most video memory stayed allocated while the GPU was also working. If graphics performance is poor, VRAM capacity may be a possible limit.",
                ObservationState.Brief => "VRAM allocation touched the threshold briefly.",
                ObservationState.Collecting => "Not enough history yet to judge.",
                _ => "Video memory allocation stayed below the threshold."
            };
            yield return o;
        }
    }

    IEnumerable<Observation> Disks(DateTimeOffset now, double iv, ObservationContext cx)
    {
        var win = ObservationRules.DiskWindow;
        var keys = store.KeysWithPrefix("disk.").Where(k => k.EndsWith(".active", StringComparison.Ordinal)).ToList();
        if (keys.Count == 0)
        {
            yield return new Observation { Id = "disk", Category = "Disk", Title = "Sustained disk activity", State = ObservationState.Collecting, Measurement = "No disk activity readings yet (or the counters are unavailable).", Duration = "—",
                Caveat = "Active time is not throughput; see the rule description." };
            yield break;
        }
        foreach (var key in keys)
        {
            string numText = key.Split('.')[1]; int.TryParse(numText, out int num);
            string name = cx.Inventory?.Disks.FirstOrDefault(d => d.Number == num)?.Name ?? ("Disk " + numText);
            var (state, st) = Judge(store.Get(key, now, win), ObservationRules.DiskActiveThreshold, win, iv);
            var lat = SeriesMath.Stats(store.Get(SeriesKeys.DiskLatency(num), now, win), null, iv);
            var rd = SeriesMath.Stats(store.Get(SeriesKeys.DiskRead(num), now, win), null, iv);
            var wr = SeriesMath.Stats(store.Get(SeriesKeys.DiskWrite(num), now, win), null, iv);
            var o = new Observation { Id = "disk:" + num, Category = "Disk", Title = $"Sustained disk activity — Disk {num} ({name})", State = state, Level = ObservationLevel.Notice,
                Caveat = "Active time only says requests were outstanding. NVMe/SSD drives can sit at 100% active time while moving data at a fraction of their speed. Judge impact by latency and by whether the machine actually waits on the disk." };
            if (st.Count == 0) { o.Measurement = "No readings yet."; o.Duration = Progress(st, win); yield return o; continue; }
            string ev = $" Throughput averaged {Fmt.Throughput(rd.Count > 0 ? rd.Mean : null)} read / {Fmt.Throughput(wr.Count > 0 ? wr.Mean : null)} write." + (lat.Count > 0 ? $" Average response time {lat.Mean:0.0} ms." : "");
            o.Measurement = $"Active time averaged {st.Mean:0}% (peak {st.Max:0}%); {st.FractionAtOrAbove:P0} of samples were at or above {ObservationRules.DiskActiveThreshold:0}%." + ev;
            o.Duration = state == ObservationState.Collecting ? Progress(st, win) : Win(st, win);
            o.Explanation = state switch
            {
                ObservationState.Possible => "This disk was busy almost continuously. It is a possible storage bottleneck, especially if response times are high or programs wait on file access.",
                ObservationState.Brief => "The disk was briefly saturated but not for long.",
                ObservationState.Collecting => "Not enough history yet to judge.",
                _ => "Disk activity was not sustained at a high level."
            };
            yield return o;
        }
    }

    Observation FreeSpace(ObservationContext cx)
    {
        var o = new Observation { Id = "freespace", Category = "Disk", Title = "Low free disk space", Caveat = "Free space is a point-in-time fact. Low space can slow Windows updates, caching and paging, but it does not by itself mean a drive is unhealthy." };
        var vols = cx.Volumes.Where(v => v.SizeBytes > 0).ToList();
        if (vols.Count == 0) { o.State = ObservationState.Collecting; o.Measurement = "No volume information yet."; o.Duration = "Current"; return o; }
        var low = new List<(VolumeSnapshot v, double pct, ObservationLevel lvl)>();
        foreach (var v in vols)
        {
            double pct = v.FreeBytes * 100.0 / v.SizeBytes;
            if (pct < ObservationRules.FreeSpaceAttentionPercent || v.FreeBytes < ObservationRules.FreeSpaceAttentionBytes) low.Add((v, pct, ObservationLevel.Attention));
            else if (pct < ObservationRules.FreeSpaceNoticePercent || v.FreeBytes < ObservationRules.FreeSpaceNoticeBytes) low.Add((v, pct, ObservationLevel.Notice));
        }
        o.Duration = "Current reading (refreshed about every 30 s)";
        if (low.Count == 0)
        {
            var min = vols.OrderBy(v => v.FreeBytes * 100.0 / v.SizeBytes).First();
            o.State = ObservationState.NotObserved; o.Level = ObservationLevel.Info;
            o.Measurement = $"All {vols.Count} volume(s) have comfortable free space; the lowest is {min.Letter} with {min.FreeBytes * 100.0 / min.SizeBytes:0}% ({Fmt.Bytes(min.FreeBytes)}) free.";
            o.Explanation = "No volume is below the free-space thresholds.";
            return o;
        }
        o.State = ObservationState.Observed; o.Level = low.Max(l => l.lvl);
        o.Measurement = string.Join("; ", low.Select(l => $"{l.v.Letter} {l.pct:0.#}% free ({Fmt.Bytes(l.v.FreeBytes)} of {Fmt.Bytes(l.v.SizeBytes)})"));
        o.Explanation = "These volumes are running low on free space.";
        foreach (var l in low) o.Details.Add($"{l.v.Letter} {(string.IsNullOrWhiteSpace(l.v.Label) ? "" : "(" + l.v.Label + ") ")}— {Fmt.Bytes(l.v.FreeBytes)} free of {Fmt.Bytes(l.v.SizeBytes)}");
        return o;
    }

    Observation Processes(DateTimeOffset now, double iv, ObservationContext cx)
    {
        var win = ObservationRules.ProcessWindow; double minFrac = ObservationRules.MinFractionAbove;
        var o = new Observation { Id = "process", Category = "Processes", Title = "Processes consistently using substantial resources", State = ObservationState.NotObserved, Level = ObservationLevel.Info,
            Caveat = "A busy process is not necessarily a problem process; it may be doing exactly what you asked. Per-process I/O counts all I/O (files, pipes, network) and is not physical-disk traffic." };
        bool anyShort = false;
        void Add(string what, List<ProcessFinding> found, Func<ProcessFinding, string> fmt)
        {
            foreach (var f in found.Where(f => f.FractionAtOrAbove >= minFrac).Take(5))
            {
                bool full = f.CoverageSeconds >= win.TotalSeconds * ObservationRules.MinCoverage;
                if (!full) { anyShort = true; o.Details.Add($"{what}: {Label(f, cx)} — watching ({Fmt.Duration(f.CoverageSeconds)} of {Fmt.Duration(win.TotalSeconds)} observed so far; {fmt(f)})"); }
                else { o.State = ObservationState.Possible; o.Details.Add($"{what}: {Label(f, cx)} — {fmt(f)} over the last {Fmt.Duration(win.TotalSeconds)}"); }
            }
        }
        Add("CPU", tracker.Query(now, win, ProcessTracker.Metric.Cpu, ObservationRules.ProcessCpuThreshold, iv), f => $"average {f.Mean:0.#}% of all threads, ≥ {ObservationRules.ProcessCpuThreshold:0}% in {f.FractionAtOrAbove:P0} of samples");
        if (cx.PhysicalMemoryBytes > 0)
            Add("Memory", tracker.Query(now, win, ProcessTracker.Metric.WorkingSet, ObservationRules.ProcessMemoryFractionOfRam * cx.PhysicalMemoryBytes, iv), f => $"working set averaged {Fmt.Bytes(f.Mean)} ({f.Mean / cx.PhysicalMemoryBytes:P0} of RAM)");
        Add("General I/O", tracker.Query(now, win, ProcessTracker.Metric.Io, ObservationRules.ProcessIoBytesPerSec, iv), f => $"averaged {Fmt.Throughput(f.Mean)} read+write (all I/O types)");
        Add("GPU", tracker.Query(now, win, ProcessTracker.Metric.Gpu, ObservationRules.ProcessGpuThreshold, iv), f => $"GPU engine averaged {f.Mean:0}%");
        if (o.State == ObservationState.Possible)
        {
            o.Level = ObservationLevel.Notice;
            o.Measurement = $"{o.Details.Count(d => !d.Contains("— watching"))} process/metric combination(s) stayed above the thresholds for most of the last {Fmt.Duration(win.TotalSeconds)}.";
            o.Duration = $"Window: last {Fmt.Duration(win.TotalSeconds)}";
            o.Explanation = "These processes used a large share of a resource almost continuously. If the system feels slow, they are the first candidates to inspect.";
        }
        else if (anyShort)
        {
            o.State = ObservationState.Collecting; o.Measurement = "Some processes are currently heavy but have not been observed long enough to qualify.";
            o.Duration = $"Needs {Fmt.Duration(win.TotalSeconds)} of observation per process"; o.Explanation = "Nothing is reported until a process stays heavy for most of the window.";
        }
        else
        {
            bool haveHistory = tracker.TrackedCount > 0;
            o.State = haveHistory ? ObservationState.NotObserved : ObservationState.Collecting;
            o.Measurement = haveHistory ? "No process stayed above the CPU, memory, I/O or GPU thresholds for most of the window." : "No process data yet.";
            o.Duration = $"Window: last {Fmt.Duration(win.TotalSeconds)}"; o.Explanation = "";
        }
        return o;
    }

    static string Label(ProcessFinding f, ObservationContext cx) { string nice = cx.AppName?.Invoke(f.Key, f.Pid, f.Name) ?? f.Name; return nice.Equals(f.Name, StringComparison.OrdinalIgnoreCase) || nice.Equals(AppGrouper.ExeName(f.Name), StringComparison.OrdinalIgnoreCase) ? $"{nice} (PID {f.Pid}){(f.Pid == cx.SelfPid ? " [this app]" : "")}" : $"{nice} — {f.Name}, PID {f.Pid}{(f.Pid == cx.SelfPid ? " [this app]" : "")}"; }
}
