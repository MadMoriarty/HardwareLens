using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.App.Controls;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>The one-stop summary: plain-English status, five live tiles, what is using the most, and what needs attention.</summary>
public sealed class OverviewPage : PageBase
{
    readonly StackPanel root = new() { Margin = new Thickness(20, 16, 10, 16), Spacing = 18 };
    readonly Border dot = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock headline = UI.Label("", null, 21, UI.Text, FontWeight.Bold), headSub = UI.Label("", "muted", 12, wrap: true);
    readonly UniformGrid tiles = new() { Columns = 5 }, top = new() { Columns = 3 };
    readonly (TextBlock value, TextBlock sub, Sparkline spark)[] tile = new (TextBlock, TextBlock, Sparkline)[5];
    readonly StackPanel attention = new() { Spacing = 8 }, incidents = new() { Spacing = 8 }, topHost = new();
    readonly StackPanel[] topLists = { new() { Spacing = 7 }, new() { Spacing = 7 }, new() { Spacing = 7 } };
    string attSig = "", topSig = "", incSig = "init";

    public OverviewPage(AppModel m) : base(m)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        head.Children.Add(dot); var ht = new StackPanel { Spacing = 2 }; ht.Children.Add(headline); ht.Children.Add(headSub); Grid.SetColumn(ht, 1); head.Children.Add(ht);
        root.Children.Add(head);

        string[] names = { "PROCESSOR", "MEMORY", "GRAPHICS", "DISK", "NETWORK" }; string[] colors = { Palette.Cpu, Palette.Memory, "#3FD0A0", Palette.Read, Palette.Receive };
        for (int i = 0; i < 5; i++)
        {
            var v = UI.Label("—", null, 24, UI.Text, FontWeight.Bold); var sub = UI.Label("", "muted", 11); var sp = new Sparkline();
            var st = new StackPanel { Spacing = 3 }; st.Children.Add(UI.Label(names[i], "caption")); st.Children.Add(v); st.Children.Add(sub); st.Children.Add(new Border { Height = 4 }); st.Children.Add(sp);
            var b = new Border { Child = st, Margin = new Thickness(0, 0, 10, 0), Cursor = new Cursor(StandardCursorType.Hand) }; b.Classes.Add("card"); b.PointerPressed += (_, _) => M.Navigate?.Invoke("Performance");
            ToolTip.SetTip(b, "Open Performance for the full charts"); tiles.Children.Add(b); tile[i] = (v, sub, sp); sp.Tag = colors[i];
        }
        root.Children.Add(tiles);

        string[] tn = { "Using the most memory", "Using the most processor", "Using the most graphics" };
        for (int i = 0; i < 3; i++)
        {
            var c = new StackPanel { Spacing = 8 }; c.Children.Add(UI.Label(tn[i], "h2")); c.Children.Add(topLists[i]);
            var b = new Border { Child = c, Margin = new Thickness(0, 0, 10, 0) }; b.Classes.Add("card"); top.Children.Add(b);
        }
        var seeAll = UI.LinkButton("See all apps →", () => M.Navigate?.Invoke("Apps"));
        root.Children.Add(UI.Section("What is using your computer right now", top, seeAll, "Apps are grouped by program: all of Chrome's processes count as one. Memory is each program's private memory; processor is a share of all cores combined."));
        root.Children.Add(UI.Section("Needs attention", attention, UI.LinkButton("Open EventLens", OpenEventLens, "Opens the separate EventLens app to browse the full Windows event log"), "Checks on the hardware inventory, device problems, and errors Windows logged in the last 24 hours."));
        root.Children.Add(UI.Section("What was busy when something sustained was detected", incidents, null, "Saved automatically the moment a sustained condition (for example, processor flat out) is first seen. Stored only on this computer."));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SizeChanged += (_, e) => { tiles.Columns = e.NewSize.Width < 980 ? 3 : 5; top.Columns = e.NewSize.Width < 900 ? 1 : 3; };
    }

    public override bool RefreshWhenPaused => true;
    public override void OnShown() { _ = M.LoadProblemsAsync(); if (M.Devices == null) _ = M.LoadDevicesAsync(); Refresh(); }
    public override void OnInventoryChanged() { attSig = ""; }

    static string Short(string n, int len = 30) => n.Length <= len ? n : n[..(len - 1)] + "…";

    public override void Refresh()
    {
        var s = M.ViewSample; var now = M.ViewNow; var st = M.Collector.Store; var win = TimeSpan.FromMinutes(2);
        var hl = Headline.Build(M.Collector.Observations, s, st.CoverageSeconds(SeriesKeys.Cpu, now, TimeSpan.FromMinutes(20), M.Collector.Interval.TotalSeconds));
        dot.Background = hl.Level switch { ObservationLevel.Attention => UI.Pink, ObservationLevel.Notice => UI.Warn, _ => UI.Ok };
        headline.Text = hl.Text; headSub.Text = hl.Sub;
        if (s == null) return;

        // tiles
        var cpu = tile[0]; cpu.value.Text = Fmt.Percent(s.CpuPercent); cpu.sub.Text = $"{Fmt.Mhz(s.CpuClockMHz)} · {s.ProcessCount} processes";
        cpu.spark.Update(st.Get(SeriesKeys.Cpu, now, win), Palette.Cpu, 100, now, win);
        var mem = tile[1]; if (s.Memory is { } mm) { mem.value.Text = mm.UsedPercent.ToString("0") + "%"; mem.sub.Text = $"{Fmt.Bytes(mm.UsedBytes)} of {Fmt.Bytes(mm.TotalBytes)}"; } else { mem.value.Text = "—"; mem.sub.Text = "unavailable"; }
        mem.spark.Update(st.Get(SeriesKeys.MemUsedPct, now, win), Palette.Memory, 100, now, win);
        var gpu = tile[2]; var g = s.Gpus.Where(x => x.EngineUtilPercent != null).OrderByDescending(x => x.EngineUtilPercent).FirstOrDefault();
        if (g != null) { gpu.value.Text = Fmt.Percent(g.EngineUtilPercent); gpu.sub.Text = Short(g.Name.Replace("NVIDIA ", "").Replace("GeForce ", ""), 22) + (g.DedicatedUsedBytes is double du ? " · " + Fmt.Bytes(du) + " VRAM" : ""); gpu.spark.Update(st.Get(SeriesKeys.GpuUtil(g.Id), now, win), "#3FD0A0", 100, now, win); }
        else { gpu.value.Text = "—"; gpu.sub.Text = "no GPU data"; gpu.spark.Update(SeriesData.Empty, "#3FD0A0", 100, now, win); }
        var dk = tile[3]; var bd = s.Disks.Where(x => x.ActivePercent != null).OrderByDescending(x => x.ActivePercent).FirstOrDefault();
        double rd = s.Disks.Sum(x => x.ReadBps ?? 0), wr = s.Disks.Sum(x => x.WriteBps ?? 0);
        dk.value.Text = bd == null ? "—" : Fmt.Percent(bd.ActivePercent); dk.sub.Text = bd == null ? "no disk data" : $"busiest disk · R {Fmt.Throughput(rd)} W {Fmt.Throughput(wr)}";
        if (bd != null) dk.spark.Update(st.Get(SeriesKeys.DiskActive(bd.Number), now, win), Palette.Read, 100, now, win); else dk.spark.Update(SeriesData.Empty, Palette.Read, 100, now, win);
        var nt = tile[4]; double rx = s.Nets.Where(n => n.InTotal).Sum(n => n.RxBps ?? 0), tx = s.Nets.Where(n => n.InTotal).Sum(n => n.TxBps ?? 0);
        nt.value.Text = s.Nets.Any(n => n.InTotal) ? "↓ " + Fmt.Mbit(rx).Replace(" Mbit/s", "") : "—"; nt.sub.Text = s.Nets.Any(n => n.InTotal) ? $"↑ {Fmt.Mbit(tx)} · Mbit/s" : "no connected adapter";
        nt.spark.Update(st.Get(SeriesKeys.NetTotalRx, now, win), Palette.Receive, null, now, win);

        BuildTop(); BuildAttention(); BuildIncidents();
    }

    void BuildTop()
    {
        var groups = M.ViewGroups; double ram = M.ViewSample?.Memory?.TotalBytes ?? 1;
        var byMem = groups.OrderByDescending(x => x.MemoryBytes).Take(4).ToList(); var byCpu = groups.OrderByDescending(x => x.Cpu).Take(4).ToList();
        var byGpu = groups.Where(x => (x.Gpu ?? 0) > 0.05 || x.GpuMemBytes > 0).OrderByDescending(x => x.Gpu ?? 0).ThenByDescending(x => x.GpuMemBytes).Take(4).ToList();
        string sig = string.Join("|", byMem.Select(x => x.Name + (long)(x.MemoryBytes / 1e6))) + string.Join("|", byCpu.Select(x => x.Name + (int)(x.Cpu * 10))) + string.Join("|", byGpu.Select(x => x.Name + (int)((x.Gpu ?? 0) * 10)));
        if (sig == topSig) return; topSig = sig;
        Fill(topLists[0], byMem, x => Fmt.Bytes(x.MemoryBytes), x => x.MemoryBytes / ram, "No data yet");
        Fill(topLists[1], byCpu, x => x.Cpu.ToString("0.0") + "%", x => x.Cpu / 100.0 * 4, "No data yet");
        Fill(topLists[2], byGpu, x => (x.Gpu is double gg ? gg.ToString("0.#") + "%" : "") + (x.GpuMemBytes > 0 ? (x.Gpu != null ? " · " : "") + Fmt.Bytes(x.GpuMemBytes) : ""), x => (x.Gpu ?? 0) / 100.0, "Nothing is using the graphics card right now");
    }

    void Fill(StackPanel host, List<AppGroup> items, Func<AppGroup, string> value, Func<AppGroup, double> frac, string empty)
    {
        host.Children.Clear();
        if (items.Count == 0) { host.Children.Add(UI.Label(empty, "muted", 12, wrap: true)); return; }
        foreach (var x in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
            row.Children.Add(UI.Label(Short(x.Name) + (x.Count > 1 ? $"  ×{x.Count}" : ""), null, 12, UI.Text));
            var v = UI.Label(value(x), "muted", 12); Grid.SetColumn(v, 1); row.Children.Add(v);
            var bar = UI.Bar(frac(x), x.Kind == AppKind.Windows ? UI.B("#5E6270") : UI.Accent); Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 2); row.Children.Add(bar);
            ToolTip.SetTip(row, x.Name + (x.Company != null ? " — " + x.Company : "") + $"\n{x.Count} process(es) · {x.Kind switch { AppKind.App => "app", AppKind.Background => "background program", _ => "part of Windows" }}");
            host.Children.Add(row);
        }
    }

    void BuildAttention()
    {
        var lines = new List<(IBrush color, string text, string? sub)>();
        foreach (var o in M.Collector.Observations.Where(o => o.State is ObservationState.Possible or ObservationState.Observed).OrderByDescending(o => o.Level))
            lines.Add((o.Level == ObservationLevel.Attention ? UI.Pink : UI.Warn, o.Title, o.Measurement));
        foreach (var f in M.Findings) lines.Add((f.Level == ObservationLevel.Attention ? UI.Pink : f.Level == ObservationLevel.Notice ? UI.Warn : UI.Info, f.Title, f.Detail));
        if (M.Devices != null)
            foreach (var d in M.Devices.Peripherals.Concat(M.Devices.Internal).Where(d => d.HasProblem && !d.Status.Contains("code 45") && !d.Status.Contains("disabled")).Take(4))
                lines.Add((UI.Warn, d.Name + ": " + d.Status.Replace("Needs attention: ", ""), "Windows reports a device problem. Open Devices for details."));
        if (M.Problems is { Count: > 0 } pr)
            foreach (var g in pr.Where(p => !p.Plain.StartsWith("A background Windows component")).GroupBy(p => p.Plain).OrderByDescending(g => g.Max(x => x.Time)).Take(4))
                lines.Add((UI.Info, g.Count() > 1 ? $"{g.Key} ({g.Count()} times in 24 h)" : g.Key, "Latest " + g.Max(x => x.Time).ToLocalTime().ToString("HH:mm") + " · from the Windows event log"));
        string sig = string.Join("|", lines.Select(l => l.text)) + (M.ProblemsLoading ? "L" : ""); if (sig == attSig) return; attSig = sig;
        attention.Children.Clear();
        if (lines.Count == 0) { attention.Children.Add(UI.Label(M.Inventory == null ? "Waiting for the hardware inventory…" : "Nothing needs attention. No hardware warnings, device problems or recent crashes were found.", "muted", 12, wrap: true)); return; }
        foreach (var (c, t, sub) in lines.Take(10))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = c, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
            var tx = new StackPanel { Spacing = 1 }; tx.Children.Add(UI.Label(t, null, 12.5, UI.Text, FontWeight.SemiBold, true)); if (sub != null) tx.Children.Add(UI.Label(sub, "muted", 11.5, wrap: true));
            Grid.SetColumn(tx, 1); row.Children.Add(tx); attention.Children.Add(row);
        }
    }

    void BuildIncidents()
    {
        var items = M.Incidents.Items.Take(4).ToList(); string sig = string.Join("|", items.Select(i => i.Time.Ticks)); if (sig == incSig) return; incSig = sig;
        incidents.Children.Clear();
        if (items.Count == 0) { incidents.Children.Add(UI.Label("Nothing sustained has been detected since HardwareLens started.", "muted", 12)); return; }
        foreach (var i in items)
        {
            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(UI.Label($"{i.Time:HH:mm:ss} · {i.Title}", null, 12.5, UI.Text, FontWeight.SemiBold, true));
            row.Children.Add(UI.Label(i.Measurement, "muted", 11.5, wrap: true));
            row.Children.Add(UI.Label("Busiest then: " + string.Join(", ", i.TopApps.Take(4).Select(a => $"{a.Name} ({a.Cpu:0.#}% CPU, {Fmt.Bytes(a.MemoryBytes, 0)})")), null, 11.5, UI.Text, wrap: true));
            incidents.Children.Add(row);
        }
    }

    void OpenEventLens()
    {
        try
        {
            string here = AppContext.BaseDirectory; var cands = new List<string>();
            for (var d = new DirectoryInfo(here); d != null && cands.Count < 12; d = d.Parent) { cands.Add(Path.Combine(d.FullName, "EventLens.exe")); cands.Add(Path.Combine(d.FullName, "EventLens", "artifacts", "portable", "EventLens.exe")); }
            var found = cands.FirstOrDefault(File.Exists);
            if (found == null) { M.StatusMessage = "EventLens was not found next to HardwareLens. Put EventLens.exe in the same folder to enable this link."; M.Notify(); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(found) { UseShellExecute = true });
        }
        catch (Exception e) { M.StatusMessage = "Could not open EventLens: " + e.Message; M.Notify(); }
    }
}
