namespace HardwareLens.Core;

public sealed record ProcessFinding(string Key, int Pid, string Name, double CoverageSeconds, double Mean, double FractionAtOrAbove, double Max);

/// <summary>
/// Keeps compact, bounded per-process history so "consistently high" can be judged over a window instead of from one reading.
/// Processes are identified by PID plus creation time, so a reused PID is a different process.
/// </summary>
public sealed class ProcessTracker
{
    sealed class Track
    {
        public int Pid; public string Name = "";
        public readonly List<long> T = new();
        public readonly List<float> Cpu = new(), Ws = new(), Io = new(), Gpu = new();
        public long LastSeen;
    }
    readonly object gate = new();
    readonly Dictionary<string, Track> map = new();
    public TimeSpan Horizon { get; set; } = TimeSpan.FromMinutes(10);
    public const int MaxTracked = 3000, MaxPointsPerTrack = 2400;

    public int TrackedCount { get { lock (gate) return map.Count; } }

    public void Update(DateTimeOffset now, IReadOnlyList<ProcessSample> procs)
    {
        long nt = now.UtcTicks, cutoff = nt - Horizon.Ticks;
        lock (gate)
        {
            foreach (var p in procs)
            {
                if (!map.TryGetValue(p.Key, out var tr))
                {
                    if (map.Count >= MaxTracked) continue;
                    map[p.Key] = tr = new Track { Pid = p.Pid, Name = p.Name };
                }
                tr.LastSeen = nt;
                tr.T.Add(nt);
                tr.Cpu.Add(p.CpuPercent is double c ? (float)c : float.NaN);
                tr.Ws.Add((float)p.WorkingSetBytes);
                tr.Io.Add(p.IoReadBps is double r && p.IoWriteBps is double w ? (float)(r + w) : float.NaN);
                tr.Gpu.Add(p.GpuPercent is double g ? (float)g : float.NaN);
                int drop = 0; while (drop < tr.T.Count && tr.T[drop] < cutoff) drop++;
                int over = tr.T.Count - drop - MaxPointsPerTrack; if (over > 0) drop += over;
                if (drop > 0) { tr.T.RemoveRange(0, drop); tr.Cpu.RemoveRange(0, drop); tr.Ws.RemoveRange(0, drop); tr.Io.RemoveRange(0, drop); tr.Gpu.RemoveRange(0, drop); }
            }
            // forget processes that exited more than 30 s ago
            var gone = map.Where(k => nt - k.Value.LastSeen > TimeSpan.FromSeconds(30).Ticks).Select(k => k.Key).ToList();
            foreach (var k in gone) map.Remove(k);
        }
    }

    public enum Metric { Cpu, WorkingSet, Io, Gpu }

    /// <summary>Processes whose metric was at/above the threshold for at least <paramref name="minFraction"/> of samples inside the window.
    /// Coverage tells how long the process has actually been observed within the window.</summary>
    public List<ProcessFinding> Query(DateTimeOffset now, TimeSpan window, Metric metric, double threshold, double intervalSeconds)
    {
        long cutoff = now.UtcTicks - window.Ticks; var res = new List<ProcessFinding>();
        lock (gate)
        {
            foreach (var (key, tr) in map)
            {
                var list = metric switch { Metric.Cpu => tr.Cpu, Metric.WorkingSet => tr.Ws, Metric.Io => tr.Io, _ => tr.Gpu };
                int n = 0, above = 0; double sum = 0, max = 0, cov = 0; long prev = 0; bool havePrev = false;
                double cap = Math.Max(intervalSeconds * 3, 3);
                for (int i = 0; i < tr.T.Count; i++)
                {
                    if (tr.T[i] < cutoff) continue;
                    float v = list[i];
                    if (float.IsNaN(v)) { havePrev = false; continue; }
                    n++; sum += v; if (v > max) max = v; if (v >= threshold) above++;
                    if (havePrev) { double dt = (tr.T[i] - prev) / (double)TimeSpan.TicksPerSecond; if (dt <= cap) cov += dt; }
                    prev = tr.T[i]; havePrev = true;
                }
                if (n == 0 || above == 0) continue;
                res.Add(new ProcessFinding(key, tr.Pid, tr.Name, cov, sum / n, (double)above / n, max));
            }
        }
        return res.OrderByDescending(r => r.Mean).ToList();
    }
}
