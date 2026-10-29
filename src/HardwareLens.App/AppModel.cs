using HardwareLens.Core;
using HardwareLens.Windows;
namespace HardwareLens.App;

/// <summary>Shared application state used by the window and all pages.</summary>
public sealed class AppModel : IDisposable
{
    public SettingsStore Store { get; } = new();
    public AppSettings Settings { get; }
    public LiveCollector Collector { get; }
    public Inventory? Inventory { get; private set; }
    public string? InventoryError { get; private set; }
    public bool InventoryLoading { get; private set; }
    public bool DisplayPaused { get; private set; }
    public DateTimeOffset FrozenAt { get; private set; }
    LiveSample? frozenSample;
    public string? StatusMessage { get; set; }
    public bool IncludeIdentifiers { get; set; }   // never persisted: identifiers stay opt-in for every export
    public ProcessInfoService Info { get; } = new();
    public IncidentLog Incidents { get; } = new();
    public List<AppGroup> Groups { get; private set; } = new();
    public List<AppGroup> ViewGroups { get; private set; } = new();
    public List<Finding> Findings { get; private set; } = new();
    public DeviceSnapshot? Devices { get; private set; }
    public bool DevicesLoading { get; private set; }
    public List<ProblemEvent>? Problems { get; private set; }
    public bool ProblemsLoading { get; private set; }
    public DateTimeOffset ProblemsAt { get; private set; }
    public bool Elevated { get; } = Elevation.IsElevated();
    public event Action? DevicesChanged;
    public Action<string>? Navigate;
    LiveSample? lastProcessed;
    public event Action? StateChanged;
    public event Action? InventoryChanged;

    public AppModel()
    {
        Settings = Store.Load();
        Collector = new LiveCollector(TimeSpan.FromSeconds(Settings.RefreshSeconds), TimeSpan.FromMinutes(Settings.HistoryMinutes));
        Collector.AppNamer = Info.FriendlyName;
        Collector.Start();
    }

    public DateTimeOffset ViewNow => DisplayPaused ? FrozenAt : DateTimeOffset.Now;
    public TimeSpan ViewWindow => TimeSpan.FromMinutes(Settings.ViewMinutes);
    public LiveSample? ViewSample => DisplayPaused ? frozenSample : Collector.LatestData;
    public bool CollectionSuspended => Collector.Suspended;
    public CsvRecorder? Recorder => Collector.Recorder;
    public bool Recording => Collector.Recorder is { Active: true };

    public async Task LoadInventoryAsync()
    {
        InventoryLoading = true; InventoryError = null; InventoryChanged?.Invoke();
        try
        {
            var inv = await Task.Run(InventoryCollector.Collect);
            Inventory = inv; Collector.SetInventory(inv); Findings = HardwareChecks.Evaluate(inv);
        }
        catch (Exception e) { InventoryError = e.Message; }
        InventoryLoading = false; InventoryChanged?.Invoke();
    }

    public void TogglePause()
    {
        DisplayPaused = !DisplayPaused;
        if (DisplayPaused) { FrozenAt = DateTimeOffset.Now; frozenSample = Collector.LatestData; ViewGroups = Groups; }
        StatusMessage = null; StateChanged?.Invoke();
    }

    public void ToggleSuspend()
    {
        Collector.Suspended = !Collector.Suspended;
        StatusMessage = Collector.Suspended ? "Collection suspended: no new readings are gathered, and a gap is marked in the history." : "Collection resumed. The first sample after a pause only sets a baseline.";
        StateChanged?.Invoke();
    }

    public void ToggleRecord()
    {
        try
        {
            if (Recording) { var r = Collector.StopRecording(); StatusMessage = r == null ? null : $"Recording saved ({Fmt.Bytes(r.BytesWritten)}): {Path.GetFileName(r.DevicePath)}"; }
            else { var r = Collector.StartRecording(Store.RecordingsFolder, Settings.RecordingMaxMB * 1024L * 1024L); StatusMessage = $"Recording to {Path.GetFileName(r.DevicePath)} (stops at {Settings.RecordingMaxMB} MB)"; }
        }
        catch (Exception e) { StatusMessage = "Recording failed: " + e.Message; }
        StateChanged?.Invoke();
    }

    public void SetRefresh(int seconds) { Settings.RefreshSeconds = seconds; Collector.Interval = TimeSpan.FromSeconds(seconds); Save(); StateChanged?.Invoke(); }
    public void SetViewMinutes(int minutes) { Settings.ViewMinutes = Math.Min(minutes, Settings.HistoryMinutes); Save(); StateChanged?.Invoke(); }
    public void Save() { try { Store.Save(Settings); } catch (Exception e) { StatusMessage = "Could not save settings: " + e.Message; } }
    public void Notify() => StateChanged?.Invoke();

    public ReportData BuildReport(bool includeIdentifiers)
    {
        var inv = Inventory ?? new Inventory { CollectedAt = DateTimeOffset.Now };
        var obs = Collector.Observations.ToList();
        LiveSummary? live = Collector.LatestData == null ? null : LiveSummaryBuilder.Build(Collector.Store, DateTimeOffset.Now, Collector.LatestData, Collector.Interval.TotalSeconds, Collector.LogicalProcessors, Collector.Health);
        if (live != null && live.CollectedSeconds < 1) live = null;
        var asm = typeof(AppModel).Assembly.GetName().Version;
        return new ReportData { Inventory = inv, GeneratedAt = DateTimeOffset.Now, AppVersion = asm == null ? "1.0.0" : $"{asm.Major}.{asm.Minor}.{asm.Build}", IncludeIdentifiers = includeIdentifiers, Live = live, Observations = obs };
    }

    /// <summary>Called every UI tick (even while the display is paused): resolves app names, groups processes and records incidents.</summary>
    public void Update()
    {
        var live = Collector.LatestData;
        if (live == null || ReferenceEquals(live, lastProcessed)) return;
        lastProcessed = live;
        try
        {
            Info.Update(live.Processes);
            Groups = AppGrouper.Group(live.Processes, Info.Get, Collector.SelfPid);
            if (!DisplayPaused) ViewGroups = Groups;
            var fresh = Incidents.Update(DateTimeOffset.Now, Collector.Observations, () => Groups.OrderByDescending(g => g.Cpu).Take(5).Select(g => new IncidentApp { Name = g.Name, Cpu = g.Cpu, MemoryBytes = g.MemoryBytes, Gpu = g.Gpu }).ToList());
            if (fresh.Count > 0) PersistIncidents(fresh);
        }
        catch (Exception e) { StatusMessage = "App grouping error: " + e.Message; }
    }

    void PersistIncidents(List<Incident> fresh)
    {
        try
        {
            Directory.CreateDirectory(Store.RecordingsFolder); string f = Path.Combine(Store.RecordingsFolder, "incidents.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) return;
            File.AppendAllLines(f, fresh.Select(i => System.Text.Json.JsonSerializer.Serialize(i)));
        }
        catch { }
    }

    public async Task LoadDevicesAsync()
    {
        if (DevicesLoading) return; DevicesLoading = true; DevicesChanged?.Invoke();
        try { Devices = await Task.Run(DeviceInventory.Collect); } catch (Exception e) { Devices = new DeviceSnapshot { Problems = { e.Message } }; }
        DevicesLoading = false; DevicesChanged?.Invoke();
    }

    public async Task LoadProblemsAsync(bool force = false)
    {
        if (ProblemsLoading || (!force && Problems != null && (DateTimeOffset.Now - ProblemsAt).TotalMinutes < 5)) return;
        ProblemsLoading = true;
        try { Problems = await Task.Run(() => EventProblems.Recent(24)); ProblemsAt = DateTimeOffset.Now; } catch { Problems ??= new(); }
        ProblemsLoading = false; Notify();
    }

    public void Dispose() { Info.Dispose(); Collector.Dispose(); Save(); }
}
