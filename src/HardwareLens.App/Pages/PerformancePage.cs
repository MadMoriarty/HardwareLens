using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.App.Controls;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>Performance: CPU, memory, GPU, disk and network charts with a bounded in-memory history.</summary>
public sealed class PerformancePage : PageBase
{
    readonly Button pauseBtn = new(), recBtn = new(), folderBtn; MenuItem suspendItem = null!;
    readonly ComboBox refreshBox = new() { ItemsSource = new[] { "1 s", "2 s", "5 s", "10 s" }, MinWidth = 70 };
    readonly ComboBox viewBox = new() { ItemsSource = new[] { "1 min", "5 min", "15 min", "20 min" }, MinWidth = 82 };
    readonly int[] refreshValues = { 1, 2, 5, 10 }, viewValues = { 1, 5, 15, 20 };
    readonly Border banner = new(); readonly TextBlock bannerText = UI.Label("", null, 12, UI.Text, wrap: true);
    readonly UniformGrid grid = new() { Columns = 2 };
    readonly ChartCard cpu, mem, gpuEng, gpuMem, diskAct, diskTp, net;
    readonly CoreStrip cores = new();
    readonly TextBlock cpuNote = UI.Label("", "muted", 10.5, wrap: true), memNote = UI.Label("", "muted", 10.5, wrap: true), gpuNote = UI.Label("", "muted", 10.5, wrap: true),
        diskNote = UI.Label("", "muted", 10.5, wrap: true), netNote = UI.Label("", "muted", 10.5, wrap: true), gpuMemNote = UI.Label("", "muted", 10.5, wrap: true);
    readonly ComboBox diskSel = new() { MinWidth = 150 }, netSel = new() { MinWidth = 150 };
    readonly StackPanel healthList = new() { Spacing = 5 };
    string diskSig = "", netSig = "", lastKey = ""; bool building;
    List<string> netKeys = new(); List<int> diskNums = new();

    public PerformancePage(AppModel m) : base(m)
    {
        folderBtn = new Button();
        refreshBox.SelectedIndex = Math.Max(0, Array.IndexOf(refreshValues, M.Settings.RefreshSeconds));
        viewBox.SelectedIndex = Math.Max(0, Array.IndexOf(viewValues, M.Settings.ViewMinutes));
        ToolTip.SetTip(refreshBox, "How often readings are taken"); ToolTip.SetTip(viewBox, "How much history the charts show (up to 20 minutes are kept in memory)");
        Avalonia.Automation.AutomationProperties.SetName(refreshBox, "Refresh interval"); Avalonia.Automation.AutomationProperties.SetName(viewBox, "Chart time range");
        refreshBox.SelectionChanged += (_, _) => { if (!building && refreshBox.SelectedIndex >= 0) M.SetRefresh(refreshValues[refreshBox.SelectedIndex]); };
        viewBox.SelectionChanged += (_, _) => { if (!building && viewBox.SelectedIndex >= 0) M.SetViewMinutes(viewValues[viewBox.SelectedIndex]); };
        pauseBtn.Click += (_, _) => M.TogglePause(); recBtn.Click += (_, _) => M.ToggleRecord();
        folderBtn.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(M.Store.RecordingsFolder); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + M.Store.RecordingsFolder + "\"") { UseShellExecute = true }); }
            catch (Exception e) { M.StatusMessage = "Could not open the folder: " + e.Message; M.Notify(); }
        };
        diskSel.SelectionChanged += (_, _) => { if (!building) lastKey = ""; if (!M.DisplayPaused) Refresh(); else RefreshNow(); };
        netSel.SelectionChanged += (_, _) => { if (!building) lastKey = ""; if (!M.DisplayPaused) Refresh(); else RefreshNow(); };

        var more = new Button { Content = UI.Icon("x", 0) }; // replaced below
        var dots = new TextBlock { Text = "•••", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        more = new Button { Content = dots, MinWidth = 36 }; ToolTip.SetTip(more, "More: suspend collection, open the recordings folder"); Avalonia.Automation.AutomationProperties.SetName(more, "More actions");
        var fly = new MenuFlyout(); suspendItem = new MenuItem(); suspendItem.Click += (_, _) => M.ToggleSuspend();
        var folderItem = new MenuItem { Header = "Open recordings folder" }; folderItem.Click += (_, _) => folderBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        fly.Items.Add(suspendItem); fly.Items.Add(folderItem); more.Flyout = fly;
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 6); c.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(c); }
        Add(pauseBtn); Add(recBtn); Add(UI.Label("Every", "muted")); Add(refreshBox); Add(UI.Label("Show last", "muted")); Add(viewBox); Add(more);

        banner.Classes.Add("card"); banner.Child = bannerText; banner.IsVisible = false; banner.BorderBrush = UI.Warn;

        cpu = new ChartCard("CPU", "100% means every logical processor is fully busy. 'Utility' follows Task Manager's frequency-adjusted measure and can exceed 100% when cores boost.");
        cpu.Extra.Children.Add(UI.Label("PER LOGICAL PROCESSOR", "caption")); cpu.Extra.Children.Add(cores);
        mem = new ChartCard("Memory");
        gpuEng = new ChartCard("GPU engine utilization", "For each adapter the busiest engine (3D, compute, copy, video…) is shown, matching the way Task Manager reports GPU load.");
        gpuMem = new ChartCard("GPU memory", "Dedicated = video memory on the card. Shared = system RAM the GPU is using; integrated GPUs have little or no dedicated memory.");
        diskAct = new ChartCard("Disk active time", "Percentage of time the physical disk had at least one request outstanding (100 − idle time). A fast SSD can show 100% and still have headroom.");
        diskTp = new ChartCard("Disk throughput", "Bytes read and written at the physical-disk level, from Windows performance counters.", diskSel);
        net = new ChartCard("Network throughput", "Mbit/s (decimal), per adapter or summed over connected physical adapters.", netSel);
                foreach (Control c in new Control[] { cpu, mem, gpuEng, gpuMem, diskAct, diskTp, net }) { c.Margin = new Thickness(0, 0, 10, 10); grid.Children.Add(c); }

        var root = new StackPanel { Margin = new Thickness(16, 14, 6, 10), Spacing = 0 };
        root.Children.Add(UI.Label("Performance", "h1")); root.Children.Add(new Border { Height = 8 });
        root.Children.Add(bar); banner.Margin = new Thickness(0, 0, 10, 10); root.Children.Add(banner); root.Children.Add(grid);
        var exp = new Expander { Header = "Data source status", Content = healthList, Margin = new Thickness(0, 0, 10, 10) }; root.Children.Add(exp);
        var sv = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = sv;
        SizeChanged += (_, e) => grid.Columns = e.NewSize.Width < 900 ? 1 : 2;
    }

    public override bool RefreshWhenPaused => true;

    static void SetBtn(Button b, string icon, string text, bool accent = false, bool active = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(UI.Icon(icon, 14, accent ? UI.B("#1A0D0A") : active ? UI.Accent : UI.Text)); var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }; if (accent) tb.Foreground = UI.B("#1A0D0A"); sp.Children.Add(tb);
        b.Content = sp; b.Classes.Set("accent", accent); Avalonia.Automation.AutomationProperties.SetName(b, text);
    }

    void UpdateToolbar()
    {
        building = true;
        SetBtn(pauseBtn, M.DisplayPaused ? "play" : "pause", M.DisplayPaused ? "Resume" : "Pause", M.DisplayPaused);
        ToolTip.SetTip(pauseBtn, "Freeze the charts. Collection keeps running in the background so no history is lost (Ctrl+P).");
        suspendItem.Header = M.CollectionSuspended ? "Resume collection" : "Suspend collection (stop gathering readings)";
        SetBtn(recBtn, "record", M.Recording ? "Stop recording" : "Record", M.Recording);
        ToolTip.SetTip(recBtn, $"Write device and process readings to CSV files beside the app (stops at {M.Settings.RecordingMaxMB} MB). Ctrl+R.");
        int ri = Array.IndexOf(refreshValues, M.Settings.RefreshSeconds); if (ri >= 0 && refreshBox.SelectedIndex != ri) refreshBox.SelectedIndex = ri;
        int vi = Array.IndexOf(viewValues, M.Settings.ViewMinutes); if (vi >= 0 && viewBox.SelectedIndex != vi) viewBox.SelectedIndex = vi;
        building = false;
        if (M.CollectionSuspended) { banner.IsVisible = true; banner.BorderBrush = UI.Warn; bannerText.Text = "Collection is suspended: no readings are being gathered and a gap is marked in the history. Resume collection to continue." + (M.DisplayPaused ? " The display is also paused." : ""); }
        else if (M.DisplayPaused) { banner.IsVisible = true; banner.BorderBrush = UI.Ok; bannerText.Text = $"Display paused at {M.FrozenAt:HH:mm:ss}. Collection is still running in the background, so history (and any recording) keeps accumulating. Resume the display to see it."; }
        else banner.IsVisible = false;
    }

    void RefreshNow() { lastKey = ""; Refresh(); }

    (string, string?) H(string src) => M.Collector.Health.Get(src, DateTimeOffset.Now, Math.Max(6, M.Collector.Interval.TotalSeconds * 3 + 1));

    public override void Refresh()
    {
        UpdateToolbar();
        var s = M.ViewSample; var now = M.ViewNow; var win = M.ViewWindow; var st = M.Collector.Store;
        string key = $"{M.DisplayPaused}|{M.FrozenAt.Ticks}|{win}|{diskSel.SelectedIndex}|{netSel.SelectedIndex}|{M.Inventory?.CollectedAt.Ticks}";
        if (M.DisplayPaused && key == lastKey) return; lastKey = key;
        string none = "Waiting for the first readings…";
        if (s == null) { cpu.Update(ChartBuilder.Cpu(st, now, win), now, win, none, "unavailable", null, none); return; }

        // CPU
        var (cs, cn) = H("cpu");
        cpu.Update(ChartBuilder.Cpu(st, now, win), now, win,
            s.CpuPercent is null ? "Collecting a baseline… the next sample will show a value." : $"{Fmt.Percent(s.CpuPercent, 1)} total · {Fmt.Mhz(s.CpuClockMHz)} average clock · {M.Collector.LogicalProcessors} logical processors · {s.ProcessCount} processes · {s.ThreadCount} threads",
            cs, cn, "CPU counters are unavailable");
        cores.Update(s.CoreBusy);
        ToolTip.SetTip(cpu, "Total CPU is normalized: 100% = all logical processors busy. Utility is frequency-adjusted and can read higher.");

        // Memory
        var (ms, mn) = H("memory");
        if (s.Memory is { } mm)
        {
            mem.Update(ChartBuilder.Memory(st, now, win), now, win, $"{Fmt.Bytes(mm.UsedBytes)} in use of {Fmt.Bytes(mm.TotalBytes)} ({mm.UsedPercent:0}%) · {Fmt.Bytes(mm.AvailableBytes)} available", ms, mn);
            ToolTip.SetTip(mem, $"Commit charge {Fmt.Bytes(mm.CommitUsedBytes)} of {Fmt.Bytes(mm.CommitLimitBytes)} limit ({mm.CommitPercent:0}%)" + (mm.CacheBytes is double cb ? $" · system cache {Fmt.Bytes(cb)}" : "") + (mm.PagesPerSec is double pg ? $" · {pg:0} pages/s" : "") + ". 'Available' already counts reusable cache, so a high 'in use' figure with plenty available is normal.");
        }
        else { mem.Update(ChartBuilder.Memory(st, now, win), now, win, "Memory readings unavailable", "unavailable", mn, "Memory readings unavailable"); ToolTip.SetTip(mem, ""); }

        // GPU
        var (gs, gn) = H("gpu");
        var gp = s.Gpus;
        string gh = gp.Count == 0 ? "No GPU performance counters reported by Windows/the display driver" : string.Join(" · ", gp.Select(g => $"{Short(g.Name)}: {(g.EngineUtilPercent is double u ? u.ToString("0") + "%" + (g.BusyEngine != null ? " (" + g.BusyEngine + ")" : "") : "unavailable")}"));
        gpuEng.Update(ChartBuilder.GpuEngine(st, now, win, gp), now, win, gh, gp.Count == 0 && gs == "live" ? "unavailable" : gs, gp.Count == 0 ? "GPU counters not provided" : gn, "GPU utilization is unavailable on this system");
        ToolTip.SetTip(gpuEng, gp.Count > 1 ? "Multiple adapters are charted separately; each shows its own busiest engine." : "Busiest engine on the adapter; per-engine detail is not shown.");
        string gm = gp.Count == 0 ? "No GPU memory counters reported" : string.Join("  |  ", gp.Select(g =>
        {
            var info = M.Inventory?.Gpus.FirstOrDefault(i => i.LuidKey == g.Id);
            string ded = g.DedicatedUsedBytes is double d ? Fmt.Bytes(d) : "unavailable"; double? tot = g.DedicatedTotalBytes ?? (info?.DedicatedVramBytes is ulong vb ? vb : null);
            return $"{Short(g.Name)}: dedicated {ded}{(tot is double t && t > 0 ? " of " + Fmt.Bytes(t) : "")}, shared {(g.SharedUsedBytes is double sh ? Fmt.Bytes(sh) : "unavailable")}";
        }));
        gpuMem.Update(ChartBuilder.GpuMemory(st, now, win, gp), now, win, gm, gp.Count == 0 && gs == "live" ? "unavailable" : gs, gp.Count == 0 ? "GPU counters not provided" : gn, "GPU memory is unavailable on this system");
        ToolTip.SetTip(gpuMem, "Integrated GPUs use shared system RAM, so their dedicated figure is small or zero. Shared memory is not extra VRAM.");

        // Disk
        var (ds, dn) = H("disk");
        diskAct.Update(ChartBuilder.DiskActive(st, now, win, s.Disks), now, win,
            s.Disks.Count == 0 ? "No physical-disk counters reported" : string.Join(" · ", s.Disks.Select(d => $"Disk {d.Number}: {(d.ActivePercent is double a ? a.ToString("0") + "%" : "—")}" + (d.LatencyMs is double l ? $" ({l:0.0} ms)" : ""))), ds, dn, "Disk counters are unavailable");
        ToolTip.SetTip(diskAct, "Latency in brackets is average response time per transfer. These are device-level counters; process I/O on the Apps page (Details view) is a different measure.");
        BuildDiskSel(s);
        int? disk = diskSel.SelectedIndex > 0 && diskSel.SelectedIndex - 1 < diskNums.Count ? diskNums[diskSel.SelectedIndex - 1] : null;
        double rd = 0, wr = 0; bool anyIo = false;
        foreach (var d in s.Disks) if (disk == null || d.Number == disk) { if (d.ReadBps is double r) { rd += r; anyIo = true; } if (d.WriteBps is double w) { wr += w; anyIo = true; } }
        diskTp.Update(ChartBuilder.DiskThroughput(st, now, win, disk), now, win, anyIo ? $"Read {Fmt.Throughput(rd)} · Write {Fmt.Throughput(wr)}" : "No throughput reported", ds, dn, "Disk counters are unavailable");

        // Network
        var (ns, nn) = H("network");
        BuildNetSel(s);
        string? ak = netSel.SelectedIndex > 0 && netSel.SelectedIndex - 1 < netKeys.Count ? netKeys[netSel.SelectedIndex - 1] : null;
        double rx = 0, tx = 0; bool anyN = false;
        foreach (var n in s.Nets) if (ak == null ? n.InTotal : n.Key == ak) { if (n.RxBps is double a) { rx += a; anyN = true; } if (n.TxBps is double b) { tx += b; anyN = true; } }
        net.Update(ChartBuilder.Network(st, now, win, ak), now, win, anyN ? $"Receive {Fmt.Mbit(rx)} · Send {Fmt.Mbit(tx)}" : "No connected adapter reported traffic", ns, nn, "Network counters are unavailable");
        ToolTip.SetTip(net, ak == null ? "Total of connected physical adapters (virtual adapters are left out to avoid double counting). Per-process network usage is not measured: Windows only offers it through administrator-level tracing." : "Single adapter.");

        BuildHealth();
    }

    static string Short(string n) => n.Length <= 28 ? n : n[..27] + "…";

    void BuildDiskSel(LiveSample s)
    {
        string sig = string.Join(",", s.Disks.Select(d => d.Number)); if (sig == diskSig) return;
        building = true; int keep = diskSel.SelectedIndex; diskSig = sig; diskNums = s.Disks.Select(d => d.Number).ToList();
        diskSel.ItemsSource = new[] { "All disks (sum)" }.Concat(s.Disks.Select(d => $"Disk {d.Number} · {Short(d.Name)}")).ToList();
        diskSel.SelectedIndex = keep > 0 && keep <= diskNums.Count ? keep : 0; building = false;
    }

    void BuildNetSel(LiveSample s)
    {
        string sig = string.Join(",", s.Nets.Select(n => n.Key)); if (sig == netSig) return;
        building = true; int keep = netSel.SelectedIndex; netSig = sig; netKeys = s.Nets.Select(n => n.Key).ToList();
        netSel.ItemsSource = new[] { "Connected physical adapters (sum)" }.Concat(s.Nets.Select(n => Short(n.Name) + (n.InTotal ? "" : " (not in sum)"))).ToList();
        netSel.SelectedIndex = keep > 0 && keep <= netKeys.Count ? keep : 0; building = false;
    }

    void BuildHealth()
    {
        healthList.Children.Clear();
        foreach (var (src, label) in new[] { ("cpu", "CPU"), ("memory", "Memory"), ("processes", "Processes"), ("gpu", "GPU"), ("disk", "Disks"), ("network", "Network") })
        {
            var (state, note) = H(src);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,Auto,*"), ColumnSpacing = 8 };
            row.Children.Add(UI.Label(label, null, 12));
            var chip = UI.Chip(state, state == "live" ? UI.Muted : state == "stale" ? UI.Warn : UI.Pink, state == "live" ? UI.Ok : state == "stale" ? UI.Warn : UI.Pink); Grid.SetColumn(chip, 1); row.Children.Add(chip);
            var n = UI.Label(note ?? "", "muted", 11, wrap: true); Grid.SetColumn(n, 2); row.Children.Add(n); healthList.Children.Add(row);
        }
        var c = M.Collector;
        healthList.Children.Add(UI.Label($"Last collection took {c.LastCollectMilliseconds:0} ms · {c.Store.SeriesCount} series, {c.Store.TotalPoints:N0} points held (history is bounded to {M.Settings.HistoryMinutes} min) · {c.Tracker.TrackedCount} processes tracked", "muted", 11, wrap: true));
        if (M.Store.Warning.Length > 0) healthList.Children.Add(UI.Label(M.Store.Warning, null, 11, UI.Warn, wrap: true));
    }
}
