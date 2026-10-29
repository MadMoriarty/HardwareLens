namespace HardwareLens.Core;

public sealed record MemorySample(double TotalBytes, double AvailableBytes, double CommitUsedBytes, double CommitLimitBytes, double? CacheBytes, double? PagesPerSec)
{
    public double UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes / TotalBytes * 100;
    public double CommitPercent => CommitLimitBytes <= 0 ? 0 : CommitUsedBytes / CommitLimitBytes * 100;
}

public sealed record GpuSample(string Id, string Name, double? EngineUtilPercent, string? BusyEngine, double? DedicatedUsedBytes, double? SharedUsedBytes, double? DedicatedTotalBytes);
public sealed record DiskSample(int Number, string Name, string Instance, double? ActivePercent, double? ReadBps, double? WriteBps, double? LatencyMs);
public sealed record NetSample(string Key, string Name, bool InTotal, double? RxBps, double? TxBps, double? LinkBps);

public sealed record ProcessSample(
    int Pid, long StartKey, string Name,
    double? CpuPercent,
    double WorkingSetBytes, double PrivateWorkingSetBytes, double CommitBytes,
    double? IoReadBps, double? IoWriteBps,
    double? GpuPercent, double? GpuDedicatedBytes,
    int Threads, int Handles)
{
    public string Key => Pid + ":" + StartKey;
}

public sealed class LiveSample
{
    public DateTimeOffset Time { get; init; }
    public double IntervalSeconds { get; init; }
    public double? CpuPercent { get; init; }
    public double? CpuUtilityPercent { get; init; }
    public double? CpuClockMHz { get; init; }
    public double[]? CoreBusy { get; init; }
    public MemorySample? Memory { get; init; }
    public IReadOnlyList<GpuSample> Gpus { get; init; } = Array.Empty<GpuSample>();
    public IReadOnlyList<DiskSample> Disks { get; init; } = Array.Empty<DiskSample>();
    public IReadOnlyList<NetSample> Nets { get; init; } = Array.Empty<NetSample>();
    public IReadOnlyList<ProcessSample> Processes { get; init; } = Array.Empty<ProcessSample>();
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    /// <summary>True when this sample is the first after a pause/sleep and carries no rate data.</summary>
    public bool IsBaseline { get; init; }
}

/// <summary>Per-source liveness shown on cards ("live", "stale", "unavailable").</summary>
public sealed class SourceHealth
{
    readonly object gate = new();
    readonly Dictionary<string, (DateTimeOffset lastOk, string? error)> map = new();
    public void Ok(string source, DateTimeOffset t) { lock (gate) map[source] = (t, null); }
    public void Fail(string source, string error)
    {
        lock (gate) { map[source] = (map.TryGetValue(source, out var v) ? v.lastOk : DateTimeOffset.MinValue, error); }
    }
    /// <summary>live | stale | unavailable</summary>
    public (string state, string? note) Get(string source, DateTimeOffset now, double staleAfterSeconds)
    {
        lock (gate)
        {
            if (!map.TryGetValue(source, out var v)) return ("unavailable", "Not collected yet");
            if (v.error != null && (now - v.lastOk).TotalSeconds > staleAfterSeconds) return (v.lastOk == DateTimeOffset.MinValue ? "unavailable" : "stale", v.error);
            if ((now - v.lastOk).TotalSeconds > staleAfterSeconds) return ("stale", $"No update for {(now - v.lastOk).TotalSeconds:0} s");
            return ("live", null);
        }
    }
    public Dictionary<string, string?> Errors() { lock (gate) return map.Where(k => k.Value.error != null).ToDictionary(k => k.Key, k => k.Value.error); }
}

public static class SeriesKeys
{
    public const string Cpu = "cpu.total", CpuUtility = "cpu.utility", CpuClock = "cpu.clock";
    public const string MemUsedPct = "mem.used.pct", MemAvailBytes = "mem.avail.bytes", MemCommitPct = "mem.commit.pct";
    public const string DiskTotalRead = "disk.total.read", DiskTotalWrite = "disk.total.write";
    public const string NetTotalRx = "net.total.rx", NetTotalTx = "net.total.tx";
    public static string GpuUtil(string id) => $"gpu.{id}.util";
    public static string GpuDedBytes(string id) => $"gpu.{id}.ded.bytes";
    public static string GpuDedPct(string id) => $"gpu.{id}.ded.pct";
    public static string GpuShared(string id) => $"gpu.{id}.shared.bytes";
    public static string DiskActive(int n) => $"disk.{n}.active";
    public static string DiskRead(int n) => $"disk.{n}.read";
    public static string DiskWrite(int n) => $"disk.{n}.write";
    public static string DiskLatency(int n) => $"disk.{n}.lat";
    public static string NetRx(string k) => $"net.{k}.rx";
    public static string NetTx(string k) => $"net.{k}.tx";
}
