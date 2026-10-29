namespace HardwareLens.Core;

public enum AppKind { App, Background, Windows }

/// <summary>Descriptive facts about a running program (from its executable and Windows), resolved lazily and cached.</summary>
public sealed class ProcMeta
{
    public bool Resolved;
    public string? Path, ProductName, Description, Company;
    public bool InWindowsDir, Critical;
    // dynamic (refreshed each tick)
    public bool HasWindow; public string? WindowTitle;
    public List<string> Services = new();
}

/// <summary>One row in the plain-English Apps view: all processes belonging to the same program combined.</summary>
public sealed class AppGroup
{
    public string Key = "", Name = "";
    public AppKind Kind;
    public string? Company, Path, WindowTitle;
    public List<ProcessSample> Procs = new();
    public double Cpu, MemoryBytes, WorkingSetBytes, IoBps, GpuMemBytes;
    public double? Gpu;
    public bool IsSelf, HasWindow, AnyCritical;
    public List<string> Services = new();
    public int Count => Procs.Count;
}

public static class AppGrouper
{
    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "Memory Compression", "Secure System", "fontdrvhost", "dwm", "MsMpEng", "MpDefenderCoreService", "NisSrv", "SecurityHealthService", "spoolsv", "taskhostw", "SearchIndexer", "sihost", "ctfmon", "audiodg", "WmiPrvSE", "conhost", "dllhost", "RuntimeBroker", "taskhostw", "SearchIndexer", "spoolsv", "MsMpEng", "NisSrv", "SecurityHealthService", "SgrmBroker", "WUDFHost", "LsaIso", "smartscreen", "backgroundTaskHost", "ApplicationFrameHost" };

    static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dwm"] = "Desktop Window Manager", ["csrss"] = "Client Server Runtime", ["lsass"] = "Local Security Authority", ["lsaiso"] = "Credential Guard", ["smss"] = "Session Manager", ["wininit"] = "Windows Start-Up", ["winlogon"] = "Windows Logon",
        ["services"] = "Services Control Manager", ["MsMpEng"] = "Microsoft Defender Antivirus", ["MpDefenderCoreService"] = "Microsoft Defender Core", ["NisSrv"] = "Defender Network Inspection", ["fontdrvhost"] = "Font Driver Host", ["sihost"] = "Shell Infrastructure Host",
        ["ctfmon"] = "Text Input Services", ["audiodg"] = "Windows Audio Device Graph", ["WmiPrvSE"] = "WMI Provider Host", ["conhost"] = "Console Window Host", ["dllhost"] = "COM Surrogate", ["RuntimeBroker"] = "Runtime Broker", ["spoolsv"] = "Print Spooler",
        ["SearchIndexer"] = "Windows Search Indexer", ["taskhostw"] = "Task Host", ["SecurityHealthService"] = "Windows Security Service", ["Registry"] = "Registry",
    };

    static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Replace("®", "").Replace("™", "").Replace("©", "").Replace("(R)", "").Replace("(TM)", "").Trim();
    }

    public static string ExeName(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Plain-English name for a process: product name, else file description, else the file name. Service hosts are named after the services they run.</summary>
    public static string DisplayName(ProcessSample p, ProcMeta? m) => DisplayName(p.Pid, p.Name, m);

    public static string DisplayName(int pid, string name, ProcMeta? m)
    {
        string exe = ExeName(name);
        if (pid == 4) return "System (Windows kernel)";
        bool noPath = m == null || !m.Resolved || m.Path == null || m.InWindowsDir;
        if (noPath && Friendly.TryGetValue(exe, out var fn)) return fn;
        if (m == null || !m.Resolved && m.Services.Count == 0) return exe;
        if (exe.Equals("svchost", StringComparison.OrdinalIgnoreCase) && m.Services.Count > 0)
            return m.Services[0] + (m.Services.Count > 1 ? $" and {m.Services.Count - 1} more service{(m.Services.Count > 2 ? "s" : "")}" : "");
        if (exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.WindowTitle)) return m.WindowTitle!;
        string prod = Clean(m.ProductName);
        if (prod.Length == 0 || prod.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) prod = Clean(m.Description);
        if (prod.Length == 0 || prod.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) prod = exe;
        return prod;
    }

    public static AppKind Classify(ProcessSample p, ProcMeta? m)
    {
        string exe = ExeName(p.Name);
        if (p.Pid == 4 || (SystemNames.Contains(exe) && (m == null || m.InWindowsDir || !m.Resolved || m.Path == null))) return exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && m?.HasWindow == true ? AppKind.App : AppKind.Windows;
        if (m == null) return AppKind.Background;
        if (m.HasWindow) return AppKind.App;
        if (m.InWindowsDir) return AppKind.Windows;
        return AppKind.Background;
    }

    public static List<AppGroup> Group(IReadOnlyList<ProcessSample> procs, Func<ProcessSample, ProcMeta?> meta, int selfPid)
    {
        var map = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue; // System Idle Process is not a program
            var m = meta(p); var name = DisplayName(p, m); var kind = Classify(p, m);
            bool svchost = ExeName(p.Name).Equals("svchost", StringComparison.OrdinalIgnoreCase);
            string key = svchost ? "svc:" + p.Key : name;
            if (!map.TryGetValue(key, out var g)) map[key] = g = new AppGroup { Key = key, Name = name, Kind = kind, Company = m?.Company, Path = m?.Path, WindowTitle = m?.WindowTitle };
            g.Procs.Add(p);
            g.Cpu += p.CpuPercent ?? 0; g.MemoryBytes += p.PrivateWorkingSetBytes; g.WorkingSetBytes += p.WorkingSetBytes;
            g.IoBps += (p.IoReadBps ?? 0) + (p.IoWriteBps ?? 0); g.GpuMemBytes += p.GpuDedicatedBytes ?? 0;
            if (p.GpuPercent is double gp) g.Gpu = Math.Min(100, (g.Gpu ?? 0) + gp);
            if (m?.HasWindow == true) { g.HasWindow = true; g.WindowTitle ??= m.WindowTitle; }
            if (m?.Critical == true) g.AnyCritical = true;
            if (p.Pid == selfPid) g.IsSelf = true;
            if (m != null) foreach (var s in m.Services) if (!g.Services.Contains(s)) g.Services.Add(s);
            if (g.Path == null && m?.Path != null) { g.Path = m.Path; g.Company ??= m.Company; }
            if (kind == AppKind.App) g.Kind = AppKind.App;
            else if (g.Kind == AppKind.Windows && kind == AppKind.Background) g.Kind = AppKind.Background;
        }
        return map.Values.ToList();
    }
}
