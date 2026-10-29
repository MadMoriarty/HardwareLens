using System.Collections.Concurrent;
using System.Diagnostics;
using HardwareLens.Core;
namespace HardwareLens.Windows;

/// <summary>
/// Resolves plain-English facts about running programs (product name, publisher, install path, windows, hosted services).
/// Disk and WMI lookups run on one background thread; the UI only reads cached results.
/// </summary>
public sealed class ProcessInfoService : IDisposable
{
    readonly ConcurrentDictionary<string, ProcMeta> cache = new();
    readonly ConcurrentDictionary<string, (string? product, string? desc, string? company)> byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly BlockingCollection<ProcessSample> queue = new(new ConcurrentQueue<ProcessSample>(), 4000);
    readonly HashSet<string> queued = new();
    readonly Thread worker; volatile bool stop;
    volatile Dictionary<int, List<string>> serviceMap = new();
    Dictionary<int, string> windowMap = new(); HashSet<int> appWindowPids = new();
    readonly string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    DateTime lastServices = DateTime.MinValue;
    public string? ServiceError { get; private set; }

    public ProcessInfoService()
    {
        worker = new Thread(Work) { IsBackground = true, Name = "HardwareLens.ProcessInfo", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>Call from the UI thread each refresh with the current process list.</summary>
    public void Update(IReadOnlyList<ProcessSample> procs)
    {
        // windows (cheap) -- refreshed every call
        try
        {
            var wm = new Dictionary<int, string>(); var pids = new HashSet<int>();
            foreach (var w in NativeEx.TopLevelWindows()) if (w.appWindow) { pids.Add(w.pid); if (!wm.ContainsKey(w.pid)) wm[w.pid] = w.title; }
            windowMap = wm; appWindowPids = pids;
        }
        catch { }
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;
            if (!cache.ContainsKey(p.Key) && queued.Add(p.Key)) { if (!queue.TryAdd(p)) queued.Remove(p.Key); }
        }
        if (queued.Count > 6000) queued.RemoveWhere(k => !cache.ContainsKey(k));
        if (cache.Count > 5000) { var live = procs.Select(p => p.Key).ToHashSet(); foreach (var k in cache.Keys.Where(k => !live.Contains(k)).ToList()) { cache.TryRemove(k, out _); queued.Remove(k); } }
    }

    /// <summary>Cached facts for a process, with live window/service information merged in. Null until first looked at.</summary>
    public ProcMeta? Get(ProcessSample p)
    {
        if (!cache.TryGetValue(p.Key, out var m)) m = null;
        var sm = serviceMap;
        if (m == null)
        {
            if (sm.TryGetValue(p.Pid, out var sv0)) return new ProcMeta { Services = sv0, HasWindow = appWindowPids.Contains(p.Pid), WindowTitle = windowMap.GetValueOrDefault(p.Pid) };
            return appWindowPids.Contains(p.Pid) ? new ProcMeta { HasWindow = true, WindowTitle = windowMap.GetValueOrDefault(p.Pid) } : null;
        }
        m.HasWindow = appWindowPids.Contains(p.Pid); m.WindowTitle = windowMap.GetValueOrDefault(p.Pid);
        m.Services = sm.TryGetValue(p.Pid, out var sv) ? sv : new List<string>();
        return m;
    }

    /// <summary>Thread-safe plain-English name for a process, usable from the collector thread.</summary>
    public string FriendlyName(string key, int pid, string name)
    {
        try
        {
            cache.TryGetValue(key, out var m); var sm = serviceMap;
            var copy = new ProcMeta { Resolved = m?.Resolved ?? false, ProductName = m?.ProductName, Description = m?.Description, Company = m?.Company, Path = m?.Path, InWindowsDir = m?.InWindowsDir ?? false, Services = sm.TryGetValue(pid, out var sv) ? sv : new List<string>() };
            return AppGrouper.DisplayName(pid, name, copy);
        }
        catch { return name; }
    }

    void Work()
    {
        while (!stop)
        {
            try
            {
                if ((DateTime.UtcNow - lastServices).TotalSeconds > 15) { RefreshServices(); lastServices = DateTime.UtcNow; }
                if (!queue.TryTake(out var p, 1000)) continue;
                var meta = Resolve(p); cache[p.Key] = meta;
            }
            catch (InvalidOperationException) { }
            catch { }
        }
    }

    void RefreshServices()
    {
        try
        {
            var map = new Dictionary<int, List<string>>();
            foreach (var r in Wmi.Query(@"root\cimv2", "SELECT DisplayName, ProcessId, State FROM Win32_Service WHERE ProcessId > 0", 10))
            {
                int pid = r.Int("ProcessId") ?? 0; string? dn = r.Str("DisplayName"); if (pid <= 0 || dn == null) continue;
                if (!map.TryGetValue(pid, out var l)) map[pid] = l = new List<string>(); l.Add(dn);
            }
            foreach (var l in map.Values) l.Sort(StringComparer.OrdinalIgnoreCase);
            serviceMap = map; ServiceError = null;
        }
        catch (Exception e) { ServiceError = e.Message; }
    }

    ProcMeta Resolve(ProcessSample p)
    {
        var m = new ProcMeta();
        nint h = NativeEx.OpenProcess(NativeEx.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == 0) { m.Resolved = true; return m; } // protected process: no path available to a standard user
        try
        {
            if (NativeEx.GetProcessTimes(h, out long create, out _, out _, out _) && create != p.StartKey) { return new ProcMeta(); } // PID reused; leave unresolved
            m.Path = NativeEx.ImagePath(h);
            if (NativeEx.IsProcessCritical(h, out bool crit)) m.Critical = crit;
        }
        finally { NativeEx.CloseHandle(h); }
        if (m.Path != null)
        {
            m.InWindowsDir = m.Path.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase);
            var info = byPath.GetOrAdd(m.Path, path =>
            {
                try { var v = FileVersionInfo.GetVersionInfo(path); return (v.ProductName, v.FileDescription, v.CompanyName); } catch { return (null, null, null); }
            });
            m.ProductName = info.product; m.Description = info.desc; m.Company = info.company;
            if (byPath.Count > 4000) byPath.Clear();
        }
        m.Resolved = true; return m;
    }

    public void Dispose() { stop = true; try { queue.CompleteAdding(); } catch { } }
}
