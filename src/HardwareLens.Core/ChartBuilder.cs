namespace HardwareLens.Core;

/// <summary>Builds chart definitions (shared by the live UI and the exported report) from the time-series store.</summary>
public static class ChartBuilder
{
    const double GiB = 1073741824.0, MiB = 1048576.0;

    static ChartSeriesSpec S(string name, string color, SeriesData d) => new() { Name = name, Color = color, Data = d };
    static bool HasData(SeriesData d) => d.Count > 0 && d.Values.Any(v => !double.IsNaN(v));

    public static ChartSpec Cpu(TimeSeriesStore st, DateTimeOffset now, TimeSpan w)
    {
        var c = new ChartSpec { Title = "CPU", Unit = "%", FixedMax = 100 };
        c.Series.Add(S("Total CPU (all threads)", Palette.Cpu, st.Get(SeriesKeys.Cpu, now, w)));
        var u = st.Get(SeriesKeys.CpuUtility, now, w);
        if (HasData(u)) c.Series.Add(S("Utility (frequency-adjusted)", Palette.CpuAlt, u));
        return c;
    }

    public static ChartSpec Memory(TimeSeriesStore st, DateTimeOffset now, TimeSpan w)
    {
        var c = new ChartSpec { Title = "Memory", Unit = "%", FixedMax = 100 };
        c.Series.Add(S("Memory in use (of RAM)", Palette.Memory, st.Get(SeriesKeys.MemUsedPct, now, w)));
        c.Series.Add(S("Commit charge (of limit)", Palette.Commit, st.Get(SeriesKeys.MemCommitPct, now, w)));
        return c;
    }

    public static ChartSpec GpuEngine(TimeSeriesStore st, DateTimeOffset now, TimeSpan w, IReadOnlyList<GpuSample> gpus)
    {
        var c = new ChartSpec { Title = "GPU engine utilization", Unit = "%", FixedMax = 100 };
        for (int i = 0; i < gpus.Count; i++) c.Series.Add(S(gpus[i].Name + " (busiest engine)", Palette.Series[(i + 1) % Palette.Series.Length], st.Get(SeriesKeys.GpuUtil(gpus[i].Id), now, w)));
        return c;
    }

    public static ChartSpec GpuMemory(TimeSeriesStore st, DateTimeOffset now, TimeSpan w, IReadOnlyList<GpuSample> gpus)
    {
        var c = new ChartSpec { Title = "GPU memory", Unit = "GiB", Scale = 1 / GiB };
        for (int i = 0; i < gpus.Count; i++)
        {
            c.Series.Add(S(gpus[i].Name + " dedicated", Palette.Series[(2 * i) % Palette.Series.Length], st.Get(SeriesKeys.GpuDedBytes(gpus[i].Id), now, w)));
            c.Series.Add(S(gpus[i].Name + " shared", Palette.Series[(2 * i + 1) % Palette.Series.Length], st.Get(SeriesKeys.GpuShared(gpus[i].Id), now, w)));
        }
        return c;
    }

    public static ChartSpec DiskActive(TimeSeriesStore st, DateTimeOffset now, TimeSpan w, IReadOnlyList<DiskSample> disks)
    {
        var c = new ChartSpec { Title = "Disk active time", Unit = "%", FixedMax = 100 };
        for (int i = 0; i < disks.Count; i++) c.Series.Add(S($"Disk {disks[i].Number} {disks[i].Name}", Palette.Series[i % Palette.Series.Length], st.Get(SeriesKeys.DiskActive(disks[i].Number), now, w)));
        return c;
    }

    /// <summary>Throughput for one physical disk, or the sum of all when <paramref name="disk"/> is null.</summary>
    public static ChartSpec DiskThroughput(TimeSeriesStore st, DateTimeOffset now, TimeSpan w, int? disk)
    {
        var c = new ChartSpec { Title = "Disk throughput", Unit = "MiB/s", Scale = 1 / MiB };
        c.Series.Add(S("Read", Palette.Read, st.Get(disk is int d ? SeriesKeys.DiskRead(d) : SeriesKeys.DiskTotalRead, now, w)));
        c.Series.Add(S("Write", Palette.Write, st.Get(disk is int d2 ? SeriesKeys.DiskWrite(d2) : SeriesKeys.DiskTotalWrite, now, w)));
        return c;
    }

    public static ChartSpec Network(TimeSeriesStore st, DateTimeOffset now, TimeSpan w, string? adapterKey)
    {
        var c = new ChartSpec { Title = "Network throughput", Unit = "Mbit/s", Scale = 8 / 1e6 };
        c.Series.Add(S("Receive", Palette.Receive, st.Get(adapterKey is null ? SeriesKeys.NetTotalRx : SeriesKeys.NetRx(adapterKey), now, w)));
        c.Series.Add(S("Send", Palette.Send, st.Get(adapterKey is null ? SeriesKeys.NetTotalTx : SeriesKeys.NetTx(adapterKey), now, w)));
        return c;
    }
}

public static class LiveSummaryBuilder
{
    public static LiveSummary Build(TimeSeriesStore st, DateTimeOffset now, LiveSample? last, double interval, int logicalProcessors, SourceHealth? health)
    {
        var win = st.Horizon;
        var sum = new LiveSummary { RefreshSeconds = interval, LogicalProcessors = logicalProcessors };
        void M(string key, string label, string unit)
        {
            var d = st.Get(key, now, win); var s = SeriesMath.Stats(d, null, interval);
            if (s.Count == 0) { sum.Metrics.Add(new MetricSummary { Key = key, Label = label, Unit = unit }); return; }
            sum.Metrics.Add(new MetricSummary { Key = key, Label = label, Unit = unit, Latest = s.Latest, Average = s.Mean, Max = s.Max, Samples = s.Count, CoverageSeconds = s.CoverageSeconds });
        }
        M(SeriesKeys.Cpu, "CPU total (100% = all logical processors)", "percent");
        M(SeriesKeys.CpuUtility, "CPU utility (frequency-adjusted, may exceed 100%)", "percent");
        M(SeriesKeys.CpuClock, "CPU clock (average across processors)", "MHz");
        M(SeriesKeys.MemUsedPct, "Memory in use (of installed RAM)", "percent");
        M(SeriesKeys.MemAvailBytes, "Memory available", "bytes");
        M(SeriesKeys.MemCommitPct, "Commit charge (of commit limit)", "percent");
        foreach (var g in last?.Gpus ?? Array.Empty<GpuSample>())
        {
            M(SeriesKeys.GpuUtil(g.Id), $"{g.Name}: busiest engine utilization", "percent");
            M(SeriesKeys.GpuDedBytes(g.Id), $"{g.Name}: dedicated memory in use", "bytes");
            M(SeriesKeys.GpuShared(g.Id), $"{g.Name}: shared system memory in use", "bytes");
        }
        foreach (var d in last?.Disks ?? Array.Empty<DiskSample>())
        {
            string n = $"Disk {d.Number} ({d.Name})";
            M(SeriesKeys.DiskActive(d.Number), n + ": active time", "percent");
            M(SeriesKeys.DiskRead(d.Number), n + ": read", "bytes/s");
            M(SeriesKeys.DiskWrite(d.Number), n + ": write", "bytes/s");
            M(SeriesKeys.DiskLatency(d.Number), n + ": average response time", "ms");
        }
        foreach (var n in last?.Nets ?? Array.Empty<NetSample>())
        {
            M(SeriesKeys.NetRx(n.Key), $"{n.Name}: receive" + (n.InTotal ? "" : " (not in total)"), "bytes/s");
            M(SeriesKeys.NetTx(n.Key), $"{n.Name}: send" + (n.InTotal ? "" : " (not in total)"), "bytes/s");
        }
        M(SeriesKeys.NetTotalRx, "Network total receive (connected physical adapters)", "bytes/s");
        M(SeriesKeys.NetTotalTx, "Network total send (connected physical adapters)", "bytes/s");
        sum.CollectedSeconds = sum.Metrics.Count == 0 ? 0 : sum.Metrics.Max(m => m.CoverageSeconds);

        var gp = last?.Gpus ?? Array.Empty<GpuSample>(); var dk = last?.Disks ?? Array.Empty<DiskSample>();
        sum.Charts.Add(ChartBuilder.Cpu(st, now, win)); sum.Charts.Add(ChartBuilder.Memory(st, now, win));
        if (gp.Count > 0) { sum.Charts.Add(ChartBuilder.GpuEngine(st, now, win, gp)); sum.Charts.Add(ChartBuilder.GpuMemory(st, now, win, gp)); }
        if (dk.Count > 0) { sum.Charts.Add(ChartBuilder.DiskActive(st, now, win, dk)); sum.Charts.Add(ChartBuilder.DiskThroughput(st, now, win, null)); }
        sum.Charts.Add(ChartBuilder.Network(st, now, win, null));
        if (last != null) sum.TopProcesses = last.Processes.Where(p => p.CpuPercent != null).OrderByDescending(p => p.CpuPercent).Take(10).ToList();
        if (health != null) foreach (var kv in health.Errors()) sum.SourceProblems[kv.Key] = kv.Value;
        return sum;
    }
}
