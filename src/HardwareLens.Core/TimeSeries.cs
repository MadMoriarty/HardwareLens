namespace HardwareLens.Core;

public readonly record struct SeriesData(long[] Ticks, double[] Values)
{
    public static readonly SeriesData Empty = new(Array.Empty<long>(), Array.Empty<double>());
    public int Count => Ticks.Length;
}

public readonly record struct WindowStats(int Count, double CoverageSeconds, double Mean, double Max, double Latest, double FractionAtOrAbove);

/// <summary>Thread-safe, time- and count-bounded store of numeric series. NaN marks an unavailable reading or a gap (sleep/suspend); it is never plotted as zero.</summary>
public sealed class TimeSeriesStore
{
    sealed class Series { public readonly List<long> T = new(); public readonly List<double> V = new(); }
    readonly object gate = new();
    readonly Dictionary<string, Series> map = new();
    public TimeSpan Horizon { get; set; } = TimeSpan.FromMinutes(20);
    public int MaxPointsPerSeries { get; set; } = 7300;
    public const int MaxSeries = 400;

    public void Append(string key, DateTimeOffset t, double? value)
    {
        double v = value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) ? double.NaN : value.Value;
        lock (gate)
        {
            if (!map.TryGetValue(key, out var s))
            {
                if (map.Count >= MaxSeries) return;
                map[key] = s = new Series();
            }
            s.T.Add(t.UtcTicks); s.V.Add(v);
            Trim(s, t.UtcTicks);
        }
    }

    void Trim(Series s, long nowTicks)
    {
        long cutoff = nowTicks - Horizon.Ticks;
        int drop = 0;
        while (drop < s.T.Count && s.T[drop] < cutoff) drop++;
        int over = s.T.Count - drop - MaxPointsPerSeries;
        if (over > 0) drop += over;
        if (drop > 0) { s.T.RemoveRange(0, drop); s.V.RemoveRange(0, drop); }
    }

    /// <summary>Marks a discontinuity (sleep/resume, collection suspended) in every series.</summary>
    public void AppendGapToAll(DateTimeOffset t)
    {
        lock (gate)
            foreach (var s in map.Values)
            {
                if (s.V.Count > 0 && double.IsNaN(s.V[^1])) continue;
                s.T.Add(t.UtcTicks); s.V.Add(double.NaN);
            }
    }

    /// <summary>Removes series that have not received data for longer than the horizon (device removed).</summary>
    public void Prune(DateTimeOffset now)
    {
        lock (gate)
        {
            var dead = map.Where(k => k.Value.T.Count == 0 || now.UtcTicks - k.Value.T[^1] > Horizon.Ticks).Select(k => k.Key).ToList();
            foreach (var k in dead) map.Remove(k);
        }
    }

    public SeriesData Get(string key, DateTimeOffset now, TimeSpan window)
    {
        lock (gate)
        {
            if (!map.TryGetValue(key, out var s) || s.T.Count == 0) return SeriesData.Empty;
            long cutoff = now.UtcTicks - window.Ticks;
            int start = 0;
            // binary search for first index >= cutoff
            int lo = 0, hi = s.T.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (s.T[mid] < cutoff) lo = mid + 1; else hi = mid; }
            start = lo;
            int n = s.T.Count - start;
            if (n <= 0) return SeriesData.Empty;
            var t = new long[n]; var v = new double[n];
            s.T.CopyTo(start, t, 0, n); s.V.CopyTo(start, v, 0, n);
            return new SeriesData(t, v);
        }
    }

    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        lock (gate) return map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public int SeriesCount { get { lock (gate) return map.Count; } }
    public int TotalPoints { get { lock (gate) return map.Values.Sum(s => s.T.Count); } }
    public bool Has(string key) { lock (gate) return map.ContainsKey(key); }

    /// <summary>Seconds of real data the store holds for a key (gaps excluded).</summary>
    public double CoverageSeconds(string key, DateTimeOffset now, TimeSpan window, double expectedIntervalSeconds)
        => SeriesMath.Stats(Get(key, now, window), null, expectedIntervalSeconds).CoverageSeconds;
}

public static class SeriesMath
{
    /// <summary>Statistics ignoring NaN. Coverage sums the time between consecutive valid points, ignoring any spacing larger than 3x the expected interval (a gap).</summary>
    public static WindowStats Stats(SeriesData d, double? threshold, double expectedIntervalSeconds)
    {
        int n = 0, above = 0; double sum = 0, max = double.NegativeInfinity, latest = double.NaN, cov = 0;
        long prevTicks = 0; bool havePrev = false;
        double cap = Math.Max(expectedIntervalSeconds * 3, 3);
        for (int i = 0; i < d.Count; i++)
        {
            double v = d.Values[i];
            if (double.IsNaN(v)) { havePrev = false; continue; }
            n++; sum += v; if (v > max) max = v; latest = v;
            if (threshold is double th && v >= th) above++;
            if (havePrev) { double dt = (d.Ticks[i] - prevTicks) / (double)TimeSpan.TicksPerSecond; if (dt <= cap) cov += dt; }
            prevTicks = d.Ticks[i]; havePrev = true;
        }
        if (n == 0) return new WindowStats(0, 0, double.NaN, double.NaN, double.NaN, 0);
        return new WindowStats(n, cov, sum / n, max, latest, threshold is null ? 0 : (double)above / n);
    }
}
