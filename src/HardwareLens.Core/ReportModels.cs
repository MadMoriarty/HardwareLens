namespace HardwareLens.Core;

public static class Palette
{
    /// <summary>Chart series colours: chosen to stay distinguishable on charcoal and reserved apart from the coral UI accent and the pink/orange window controls.</summary>
    public static readonly string[] Series = { "#5AA9FF", "#3FD0A0", "#DCA16A", "#ED829F", "#A98BFF", "#F2D45C", "#35C9C0" };
    public const string Cpu = "#5AA9FF", CpuAlt = "#F2D45C", Memory = "#A98BFF", Commit = "#F2D45C";
    public const string Read = "#35C9C0", Write = "#DCA16A", Receive = "#5AA9FF", Send = "#ED829F";
    public const string Accent = "#FF6B57";
}

public sealed class ChartSeriesSpec
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#5AA9FF";
    public SeriesData Data { get; set; } = SeriesData.Empty;
}

public sealed class ChartSpec
{
    public string Title { get; set; } = "";
    /// <summary>Axis unit label, e.g. "%", "GiB", "MiB/s", "Mbit/s", "ms".</summary>
    public string Unit { get; set; } = "";
    /// <summary>Multiply stored values by this to get the axis unit.</summary>
    public double Scale { get; set; } = 1;
    public double? FixedMax { get; set; }
    public List<ChartSeriesSpec> Series { get; set; } = new();
}

public sealed class MetricSummary
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>percent | bytes | bytes/s | ms | MHz</summary>
    public string Unit { get; set; } = "";
    public double? Latest { get; set; }
    public double? Average { get; set; }
    public double? Max { get; set; }
    public int Samples { get; set; }
    public double CoverageSeconds { get; set; }
}

public sealed class LiveSummary
{
    public double CollectedSeconds { get; set; }
    public double RefreshSeconds { get; set; }
    public int LogicalProcessors { get; set; }
    public List<MetricSummary> Metrics { get; set; } = new();
    public List<ChartSpec> Charts { get; set; } = new();
    public List<ProcessSample> TopProcesses { get; set; } = new();
    public Dictionary<string, string?> SourceProblems { get; set; } = new();
}

public sealed class ReportData
{
    public Inventory Inventory { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public string AppVersion { get; set; } = "1.0.0";
    public bool IncludeIdentifiers { get; set; }
    public LiveSummary? Live { get; set; }
    public List<Observation> Observations { get; set; } = new();
}

public static class Limitations
{
    public static readonly string[] All =
    {
        "Hardware names come from Windows (WMI/CIM, SMBIOS, DXGI). Where Windows reports a blank or generic value, it is shown as unavailable; nothing is inferred (for example a graphics-card board partner, a RAM product family or a drive type).",
        "A drive 'Healthy' status is Windows's basic health flag, not a comprehensive diagnostic. Where the drive reports them, temperature, SSD wear, power-on hours and error counts are shown from Windows' storage reliability counters; they are not a full SMART test or surface scan, and many USB drives report none of them. Fan speeds and power draw are not collected.",
        "Memory speeds are the SMBIOS values in MT/s (mega-transfers per second), not MHz. 'Configured' is the speed the modules are running at; 'rated' is the module's maximum.",
        "CPU percentages are normalized: 100% means every logical processor was fully busy. A process using one thread of a 24-thread CPU therefore shows about 4%. Per-process CPU is computed from kernel+user time between samples.",
        "Process memory columns: working set = physical RAM the process currently holds, including pages shared with other processes; private working set = the part of that not shared; commit (private bytes) = virtual memory the process has committed, which may not be in RAM.",
        "Process I/O counts every read/write the process requests (files, pipes, devices, network). It is not physical-disk traffic and is not comparable with the disk throughput readings.",
        "Per-app network usage is shown as open connections (which app is talking to which address), not as bytes: Windows reports per-process data volume only through event tracing (ETW) that needs administrator rights, which HardwareLens does not use. Network throughput is measured per adapter. Your public IP address and other devices on your network are not looked up, because that would need an outside service or network scanning.",
        "End task, startup and service changes act only on your explicit click, check the process or entry again immediately before acting, refuse Windows-critical processes, and never delete anything. Startup items are turned off through the same switch Task Manager uses. Startup impact (how much a program slows boot) is not shown because Windows keeps those measurements to itself; Store-app startup tasks and scheduled tasks are not listed.",
        "Apps view: processes are grouped by program using their file description and whether they own a visible window. Background services hosted inside svchost are named after the Windows services running in them; the grouping is a plain-English aid, so use Details for the raw process list.",
        "Devices are grouped per physical product using Windows' container ID and named from the most specific name the device reports. Some devices only report generic text; those are marked. Battery levels appear only for devices that report one. PCIe link speed and CPU throttling are not checked because they could not be read reliably.",
        "GPU readings come from Windows GPU performance counters supplied by the display driver. Engine utilization is the busiest engine (3D, compute, copy, video) on each adapter. Some drivers, virtual machines and older Windows builds do not provide them; those readings are shown as unavailable rather than zero.",
        "Readings are sampled at the chosen refresh interval; spikes shorter than the interval can be missed. After sleep/resume or a paused collection, a gap is marked and the first sample is discarded because its rates would span the whole gap.",
        "System uptime reflects the Windows session start. With Fast Startup enabled, 'Shut down' does not reset it; only 'Restart' does.",
        "Observations apply fixed, published thresholds to measured windows. They describe conditions that may limit performance; they are not diagnoses, and a brief spike is never reported as a problem.",
        "HardwareLens runs as a normal user. Ending protected programs, controlling services and changing all-users startup items need administrator rights; you are offered a restart as administrator only when you try one of those."
    };
}
