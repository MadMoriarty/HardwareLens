namespace HardwareLens.Core;

public sealed record Finding(ObservationLevel Level, string Title, string Detail, string Area);

/// <summary>Rule-based checks on the hardware inventory (no live data). Each states what was seen and what it could mean, without claiming a diagnosis.</summary>
public static class HardwareChecks
{
    public static List<Finding> Evaluate(Inventory inv)
    {
        var f = new List<Finding>();
        var mods = inv.Memory.Modules.Where(m => m.CapacityBytes > 0).ToList();
        var slow = mods.Where(m => m.ConfiguredSpeedMTs is int c && m.RatedSpeedMTs is int r && r > 0 && c < r * 0.9).ToList();
        if (slow.Count > 0)
        {
            var m = slow[0];
            f.Add(new(ObservationLevel.Notice, "Memory may be running slower than it is rated for", $"The modules report running at {m.ConfiguredSpeedMTs} MT/s but are rated for {m.RatedSpeedMTs} MT/s. This is often because the memory profile (XMP or EXPO) is switched off in the BIOS, or because the processor or board limits it. It is not a fault by itself.", "Memory"));
        }
        if (inv.Memory.SlotsPopulated == 1 && (inv.Memory.SlotsTotal ?? 0) >= 2)
            f.Add(new(ObservationLevel.Info, "Only one memory module is installed", $"{inv.Memory.SlotsPopulated} of {inv.Memory.SlotsTotal} slots are used. A single module runs in single-channel mode, which has less memory bandwidth than two matching modules (this matters most for integrated graphics and memory-heavy work).", "Memory"));
        if (mods.Count > 1 && mods.Select(m => m.CapacityBytes).Distinct().Count() > 1)
            f.Add(new(ObservationLevel.Info, "Memory modules differ in size", "Mixed sizes can force part of the memory to run in single-channel mode.", "Memory"));
        foreach (var d in inv.Disks)
        {
            string nm = $"Disk {d.Number} ({d.Name})";
            if (d.WindowsHealthStatus is "Warning" or "Unhealthy") f.Add(new(ObservationLevel.Attention, nm + ": Windows reports a health problem", $"Windows' basic health flag says “{d.WindowsHealthStatus}”" + (d.OperationalStatus != null ? $" (status: {d.OperationalStatus})" : "") + ". Back up important files and check the drive with its maker's tool.", "Storage"));
            if (d.WearPercentUsed is int w && w >= 80) f.Add(new(w >= 95 ? ObservationLevel.Attention : ObservationLevel.Notice, nm + ": SSD is heavily worn", $"The drive reports {w}% of its rated write life used. SSDs usually keep working past this, but it is a sign to plan a replacement and keep backups.", "Storage"));
            if (d.TemperatureC is int t && t >= 70) f.Add(new(t >= 80 ? ObservationLevel.Attention : ObservationLevel.Notice, nm + ": running hot", $"The drive reports {t} °C. Sustained heat can make drives slow themselves down; check airflow.", "Storage"));
        }
        if (inv.Battery?.WearPercent is double bw && bw >= 40)
            f.Add(new(ObservationLevel.Notice, "Battery has lost a lot of capacity", $"The battery holds about {100 - bw:0}% of its original design capacity.", "Battery"));
        if (inv.System.UptimeSeconds is double up && up > 14 * 86400)
            f.Add(new(ObservationLevel.Info, "Windows has not been restarted for a while", $"It has been running for {Fmt.Duration(up)}. A restart (not Shut down, if Fast Startup is on) clears leaked memory and finishes pending updates.", "System"));
        foreach (var n in inv.Nics.Where(n => n.IsPhysical && !n.IsVirtual && n.Kind == "Ethernet" && n.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) && n.ReceiveLinkBps is ulong r && r is > 0 and <= 100_000_000UL))
            f.Add(new(ObservationLevel.Notice, $"{n.Name} is linked at only {Fmt.LinkSpeed(n.ReceiveLinkBps)}", "A wired connection normally negotiates 1 Gbit/s or more. A low speed often points at a damaged cable, a worn connector or an old switch/router port.", "Network"));
        return f.OrderByDescending(x => x.Level).ToList();
    }
}

public sealed class IncidentApp { public string Name { get; set; } = ""; public double Cpu { get; set; } public double MemoryBytes { get; set; } public double? Gpu { get; set; } }
public sealed class Incident
{
    public DateTimeOffset Time { get; set; }
    public string ObservationId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Measurement { get; set; } = "";
    public List<IncidentApp> TopApps { get; set; } = new();
}

/// <summary>Keeps a small bounded list of "what was busy when something sustained was detected".</summary>
public sealed class IncidentLog
{
    readonly List<Incident> items = new(); readonly HashSet<string> active = new();
    public const int Max = 50;
    public IReadOnlyList<Incident> Items { get { lock (items) return items.ToList(); } }

    /// <summary>Call with the current observations and top apps; returns incidents newly recorded (observation just became Possible/Observed).</summary>
    public List<Incident> Update(DateTimeOffset now, IReadOnlyList<Observation> obs, Func<List<IncidentApp>> topApps)
    {
        var fresh = new List<Incident>();
        lock (items)
        {
            var nowActive = obs.Where(o => o.State is ObservationState.Possible or ObservationState.Observed && o.Id != "freespace").Select(o => o.Id).ToHashSet();
            foreach (var o in obs.Where(o => nowActive.Contains(o.Id) && !active.Contains(o.Id)))
            {
                var inc = new Incident { Time = now, ObservationId = o.Id, Title = o.Title, Measurement = o.Measurement, TopApps = topApps() };
                items.Insert(0, inc); fresh.Add(inc);
            }
            active.Clear(); foreach (var id in nowActive) active.Add(id);
            while (items.Count > Max) items.RemoveAt(items.Count - 1);
        }
        return fresh;
    }
}

public sealed record HeadlineInfo(ObservationLevel Level, string Text, string Sub);

public static class Headline
{
    public static HeadlineInfo Build(IReadOnlyList<Observation> obs, LiveSample? s, double collectedSeconds)
    {
        string nums = s == null ? "" : $"CPU {Fmt.Percent(s.CpuPercent)} · memory {(s.Memory != null ? s.Memory.UsedPercent.ToString("0") + "%" : "—")}" + (s.Gpus.Count > 0 && s.Gpus.Max(g => g.EngineUtilPercent) is double gu ? $" · GPU {gu:0}%" : "");
        var active = obs.Where(o => o.State is ObservationState.Possible or ObservationState.Observed).OrderByDescending(o => o.Level).ToList();
        if (s == null) return new(ObservationLevel.Info, "Starting up…", "Gathering the first readings.");
        if (active.Count == 0)
            return new(ObservationLevel.Info, collectedSeconds < 120 ? "Looking good so far" : "Everything looks normal", (collectedSeconds < 300 ? "Only a few minutes of readings so far, so sustained problems cannot be judged yet. " : "No resource has been saturated for a sustained period. ") + nums);
        var top = active[0];
        string plain = top.Id switch
        {
            "cpu" => "The processor has been working flat out", "memory" => "Memory is running short", "vram" => "Graphics memory is nearly full", "disk" => "A drive has been busy non-stop",
            "freespace" => "A drive is almost full", "process" => "Some apps have been using a lot of resources", _ => top.Title
        };
        string more = active.Count > 1 ? $" ({active.Count - 1} more thing{(active.Count > 2 ? "s" : "")} noted)" : "";
        return new(top.Level, plain + more, top.Measurement + (top.Duration.Length > 0 ? " · " + top.Duration : ""));
    }
}
