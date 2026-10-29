using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
using HardwareLens.Windows;
namespace HardwareLens.App.Pages;

public sealed class AppRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; init; } = ""; public bool IsHeader, IsChild; public AppGroup? Group; public ProcessSample? Proc; public AppKind HeaderKind;
    string name = "", cpu = "", mem = "", gpu = "", disk = "", count = "", glyph = ""; IBrush nameFg = UI.Text; FontWeight weight = FontWeight.Normal; Thickness indent;
    void Set<T>(ref T f, T v, string n) { if (EqualityComparer<T>.Default.Equals(f, v)) return; f = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }
    public string Name { get => name; set => Set(ref name, value, nameof(Name)); }
    public string Cpu { get => cpu; set => Set(ref cpu, value, nameof(Cpu)); }
    public string Mem { get => mem; set => Set(ref mem, value, nameof(Mem)); }
    public string Gpu { get => gpu; set => Set(ref gpu, value, nameof(Gpu)); }
    public string Disk { get => disk; set => Set(ref disk, value, nameof(Disk)); }
    public string Count { get => count; set => Set(ref count, value, nameof(Count)); }
    public string Glyph { get => glyph; set => Set(ref glyph, value, nameof(Glyph)); }
    public IBrush NameFg { get => nameFg; set => Set(ref nameFg, value, nameof(NameFg)); }
    public FontWeight Weight { get => weight; set => Set(ref weight, value, nameof(Weight)); }
    public Thickness Indent { get => indent; set => Set(ref indent, value, nameof(Indent)); }
}

/// <summary>Plain-English process view: programs grouped as Apps / Background / Windows, with End task and a Details tab.</summary>
public sealed class AppsPage : PageBase
{
    readonly DataGrid grid = new(); readonly DetailsView details;
    readonly TextBox search = new() { Watermark = "Search apps and processes (Ctrl+F)", MinWidth = 280 };
    readonly TextBlock summary = UI.Label("", "muted", 11.5, wrap: true);
    readonly ObservableCollection<AppRow> rows = new(); readonly Dictionary<string, AppRow> map = new();
    readonly HashSet<string> expanded = new(), collapsedKinds = new();
    readonly Button endBtn = new(), locBtn = new(), prioBtn = new(); readonly StackPanel about = new() { Spacing = 6 };
    readonly Border aboutCard = new(); readonly Grid host = new();
    string sort = "cpu"; bool desc = true; bool detailsMode;
    string aboutSig = ""; Control gridBox;

    public AppsPage(AppModel m) : base(m)
    {
        details = new DetailsView(m);
        sort = M.Settings.ProcessSort is "cpu" or "mem" or "gpu" or "disk" or "name" ? M.Settings.ProcessSort : "cpu"; desc = M.Settings.ProcessSort is "name" ? M.Settings.ProcessSortDescending : true;
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.CanUserSortColumns = true; grid.CanUserResizeColumns = true; grid.SelectionMode = DataGridSelectionMode.Single; grid.RowHeight = 28;
        var nameCol = new DataGridTemplateColumn { Header = new TextBlock { Text = "Name" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 260, Tag = "name", CanUserSort = true, SortMemberPath = "name" };
        nameCol.CellTemplate = new FuncDataTemplate<AppRow>((_, _) =>
        {
            var glyph = new TextBlock { Width = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = UI.Muted, Cursor = new Cursor(StandardCursorType.Hand) }; glyph[!TextBlock.TextProperty] = new Binding("Glyph");
            var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; t[!TextBlock.TextProperty] = new Binding("Name"); t[!TextBlock.ForegroundProperty] = new Binding("NameFg"); t[!TextBlock.FontWeightProperty] = new Binding("Weight");
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent }; sp[!Layoutable.MarginProperty] = new Binding("Indent"); sp.Children.Add(glyph); sp.Children.Add(t);
            sp.PointerPressed += (s, e) => { if ((s as Control)?.DataContext is AppRow r && (r.IsHeader || r.Glyph.Length > 0)) { Toggle(r); e.Handled = true; } };
            return sp;
        });
        grid.Columns.Add(nameCol);
        void Col(string header, string prop, string key, double width, string tip)
        {
            var hdr = new TextBlock { Text = header }; ToolTip.SetTip(hdr, tip);
            var c = new DataGridTemplateColumn { Header = hdr, Width = new DataGridLength(width), Tag = key, CanUserSort = true, SortMemberPath = key };
            c.CellTemplate = new FuncDataTemplate<AppRow>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Right }; t[!TextBlock.TextProperty] = new Binding(prop); return t; });
            grid.Columns.Add(c);
        }
        Col("Processor", "Cpu", "cpu", 92, "Share of ALL cores combined (100% = every thread busy)");
        Col("Memory", "Mem", "mem", 100, "Private memory in use (the part of RAM that belongs only to this program). Shared pages are not double-counted.");
        Col("Graphics", "Gpu", "gpu", 130, "Busiest graphics engine the program is using, and its dedicated graphics memory");
        Col("Disk activity", "Disk", "disk", 110, "General read+write I/O the program asks for (files, pipes, network). Not the same as physical disk traffic.");
        Col("Processes", "Count", "count", 96, "How many separate Windows processes make up this entry");
        grid.Sorting += (_, e) =>
        {
            e.Handled = true; string key = (string)(e.Column.Tag ?? "cpu"); if (key == "count") return;
            if (key == sort) desc = !desc; else { sort = key; desc = key != "name"; }
            M.Settings.ProcessSort = sort; M.Settings.ProcessSortDescending = desc; M.Save(); UpdateHeaders(); Refresh();
        };
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is AppRow { IsHeader: true }) grid.SelectedItem = null; UpdateAbout(true); };
        details.SelectionChanged += () => UpdateAbout(true);
        search.TextChanged += (_, _) => { details.Filter = search.Text ?? ""; Refresh(); };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Text = ""; e.Handled = true; } };
        Avalonia.Automation.AutomationProperties.SetName(search, "Search processes");

        SetBtn(endBtn, "x", "End task"); endBtn.Classes.Add("danger"); ToolTip.SetTip(endBtn, "Close the selected app. Windows apps are asked to close first; you are asked before anything is forced."); endBtn.IsEnabled = false; endBtn.Click += async (_, _) => await EndSelectedAsync();
        SetBtn(locBtn, "folder", "Open file location"); locBtn.IsEnabled = false; locBtn.Click += (_, _) => { var p = TargetPath(); if (p != null) StartupManager.ShowInFolder(p); };
        SetBtn(prioBtn, "services", "Priority ▾"); prioBtn.IsEnabled = false; prioBtn.Flyout = BuildPriorityMenu(); ToolTip.SetTip(prioBtn, "Change how much attention Windows gives this program. Lasts until it restarts.");

        var seg = UI.Segmented(new[] { "Apps", "Details" }, 0, i => { detailsMode = i == 1; grid.IsVisible = !detailsMode; details.IsVisible = detailsMode; Refresh(); UpdateAbout(true); });
        ToolTip.SetTip(seg, "Apps: grouped in plain English. Details: every process with exact numbers.");
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        bar.Children.Add(seg); Grid.SetColumn(search, 1); bar.Children.Add(search);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; btns.Children.Add(prioBtn); btns.Children.Add(locBtn); btns.Children.Add(endBtn); Grid.SetColumn(btns, 3); bar.Children.Add(btns);

        aboutCard.Classes.Add("card"); aboutCard.Child = about; aboutCard.IsVisible = false; aboutCard.Margin = new Thickness(0, 10, 0, 0);
        gridBox = new Border { Child = host, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        host.Children.Add(grid); host.Children.Add(details); details.IsVisible = false;
        var dock = new DockPanel { Margin = new Thickness(18, 14, 12, 12) };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) }; head.Children.Add(UI.Label("Apps", "h1")); head.Children.Add(bar); head.Children.Add(summary);
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(aboutCard, Dock.Bottom); dock.Children.Add(head); dock.Children.Add(aboutCard); dock.Children.Add(gridBox);
        Content = dock; UpdateHeaders();
    }

    static void SetBtn(Button b, string icon, string text) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; sp.Children.Add(UI.Icon(icon, 13, UI.Text)); sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); b.Content = sp; }

    public void FocusSearch() { search.Focus(); search.SelectAll(); }

    void UpdateHeaders()
    {
        foreach (var c in grid.Columns) { if (c.Header is not TextBlock tb) continue; var key = (string?)c.Tag; string h = (tb.Text ?? "").TrimEnd(' ', '▲', '▼'); tb.Text = key == sort ? h + (desc ? " ▼" : " ▲") : h; }
    }

    void Toggle(AppRow r)
    {
        if (r.IsHeader) { if (!collapsedKinds.Remove(r.Key)) collapsedKinds.Add(r.Key); }
        else if (r.Group != null) { if (!expanded.Remove(r.Group.Key)) expanded.Add(r.Group.Key); }
        Refresh();
    }

    double SortKey(AppGroup g) => sort switch { "mem" => g.MemoryBytes, "gpu" => (g.Gpu ?? 0) * 1e12 + g.GpuMemBytes, "disk" => g.IoBps, _ => g.Cpu };

    public override void Refresh()
    {
        if (detailsMode) { details.Refresh(); var sm = M.ViewSample; summary.Text = sm == null ? "Waiting for the first sample…" : $"{details.Shown} processes shown · {sm.ProcessCount} total · {sm.ThreadCount:N0} threads · {sm.HandleCount:N0} handles"; return; }
        var s = M.ViewSample; if (s == null) { summary.Text = "Waiting for the first process sample…"; return; }
        string q = (search.Text ?? "").Trim();
        var groups = M.ViewGroups.AsEnumerable();
        if (q.Length > 0) groups = groups.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (g.Company?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || g.Procs.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString().StartsWith(q)));
        var all = groups.ToList(); var want = new List<AppRow>();
        foreach (var kind in new[] { AppKind.App, AppKind.Background, AppKind.Windows })
        {
            var inKind = all.Where(g => g.Kind == kind).ToList(); if (inKind.Count == 0) continue;
            string hk = "h:" + kind; bool collapsed = collapsedKinds.Contains(hk) && q.Length == 0;
            var hr = Row(hk, () => new AppRow { Key = hk, IsHeader = true, HeaderKind = kind });
            hr.Name = (kind switch { AppKind.App => "APPS", AppKind.Background => "BACKGROUND PROGRAMS", _ => "WINDOWS" }) + $"  ({inKind.Count})"; hr.NameFg = UI.Muted; hr.Weight = FontWeight.Bold; hr.Glyph = collapsed ? "▸" : "▾"; hr.Indent = default;
            hr.Cpu = Fmt.Percent(inKind.Sum(g => g.Cpu), 1); hr.Mem = Fmt.Bytes(inKind.Sum(g => g.MemoryBytes), 1); hr.Gpu = ""; hr.Disk = ""; hr.Count = ""; want.Add(hr);
            if (collapsed) continue;
            var ordered = (sort == "name" ? (desc ? inKind.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase) : inKind.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) : (desc ? inKind.OrderByDescending(SortKey) : inKind.OrderBy(SortKey))).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var g in ordered)
            {
                string gk = "g:" + g.Key; var gr = Row(gk, () => new AppRow { Key = gk }); gr.Group = g; gr.Proc = null; gr.IsChild = false;
                gr.Name = g.Name + (g.IsSelf ? "  (this app)" : ""); gr.NameFg = UI.Text; gr.Weight = FontWeight.Normal; gr.Indent = new Thickness(8, 0, 0, 0);
                gr.Glyph = g.Count > 1 ? (expanded.Contains(g.Key) ? "▾" : "▸") : "";
                gr.Cpu = g.Cpu.ToString("0.0") + "%"; gr.Mem = Fmt.Bytes(g.MemoryBytes); gr.Gpu = g.Gpu is double gg ? (gg >= 0.05 ? gg.ToString("0.#") + "%" : "0%") + (g.GpuMemBytes > 0 ? " · " + Fmt.Bytes(g.GpuMemBytes, 0) : "") : (g.GpuMemBytes > 0 ? Fmt.Bytes(g.GpuMemBytes, 0) : "—");
                gr.Disk = g.IoBps >= 1024 ? Fmt.Throughput(g.IoBps) : (g.IoBps > 0 ? "<1 KiB/s" : "0"); gr.Count = g.Count.ToString(); want.Add(gr);
                if (g.Count > 1 && (expanded.Contains(g.Key) || (q.Length > 0 && g.Procs.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) && !g.Name.Contains(q, StringComparison.OrdinalIgnoreCase))))
                    foreach (var p in g.Procs.OrderByDescending(p => p.PrivateWorkingSetBytes).Take(200))
                    {
                        string pk = "p:" + p.Key; var pr = Row(pk, () => new AppRow { Key = pk, IsChild = true }); pr.Proc = p; pr.Group = g;
                        pr.Name = $"{p.Name}  ·  PID {p.Pid}"; pr.NameFg = UI.Muted; pr.Weight = FontWeight.Normal; pr.Indent = new Thickness(36, 0, 0, 0); pr.Glyph = "";
                        pr.Cpu = (p.CpuPercent ?? 0).ToString("0.0") + "%"; pr.Mem = Fmt.Bytes(p.PrivateWorkingSetBytes); pr.Gpu = p.GpuPercent is double g2 ? g2.ToString("0.#") + "%" : "—"; pr.Disk = Fmt.Throughput((p.IoReadBps ?? 0) + (p.IoWriteBps ?? 0)); pr.Count = ""; want.Add(pr);
                    }
            }
        }
        var seen = want.Select(r => r.Key).ToHashSet(); foreach (var k in map.Keys.Where(k => !seen.Contains(k)).ToList()) map.Remove(k);
        for (int i = 0; i < want.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], want[i])) continue;
            int j = rows.IndexOf(want[i]); if (j >= 0) rows.Move(j, i); else rows.Insert(i, want[i]);
        }
        while (rows.Count > want.Count) rows.RemoveAt(rows.Count - 1);
        double cpuSum = s.Processes.Sum(p => p.CpuPercent ?? 0);
        summary.Text = $"{all.Count(g => g.Kind == AppKind.App)} apps · {all.Count(g => g.Kind == AppKind.Background)} background programs · {all.Count(g => g.Kind == AppKind.Windows)} Windows items · {s.ProcessCount} processes in total · processor use adds up to {cpuSum:0.0}% (device total {Fmt.Percent(s.CpuPercent, 1)})";
        UpdateAbout(false);
    }

    AppRow Row(string key, Func<AppRow> make) { if (!map.TryGetValue(key, out var r)) map[key] = r = make(); return r; }

    // ---------------- selection ----------------
    sealed record Target(string Name, List<ProcessSample> Procs, AppGroup? Group, ProcessSample? Single);

    Target? Current()
    {
        if (detailsMode) { var p = details.Selected; if (p == null) return null; var m = M.Info.Get(p); return new Target(AppGrouper.DisplayName(p, m), new List<ProcessSample> { p }, null, p); }
        if (grid.SelectedItem is not AppRow r || r.IsHeader) return null;
        if (r.IsChild && r.Proc != null) return new Target(r.Proc.Name, new List<ProcessSample> { r.Proc }, r.Group, r.Proc);
        return r.Group == null ? null : new Target(r.Group.Name, r.Group.Procs.ToList(), r.Group, null);
    }

    string? TargetPath() { var t = Current(); if (t == null) return null; return t.Group?.Path ?? M.Info.Get(t.Procs[0])?.Path; }

    void UpdateAbout(bool force)
    {
        var t = Current(); endBtn.IsEnabled = t != null; locBtn.IsEnabled = t != null && TargetPath() != null; prioBtn.IsEnabled = t != null;
        if (t == null) { aboutCard.IsVisible = false; aboutSig = ""; return; }
        string sig = t.Name + "|" + t.Procs.Count + "|" + (t.Single?.Pid ?? 0); if (!force && sig == aboutSig) return; aboutSig = sig;
        aboutCard.IsVisible = true; about.Children.Clear();
        var m0 = M.Info.Get(t.Procs[0]); var g = t.Group; string? path = TargetPath();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(UI.Label(t.Name, "h2")); var kind = g?.Kind ?? AppGrouper.Classify(t.Procs[0], m0);
        var chip = UI.Chip(kind switch { AppKind.App => "App", AppKind.Background => "Background program", _ => "Part of Windows" }, UI.Muted, kind == AppKind.Windows ? UI.Faint : UI.Accent); Grid.SetColumn(chip, 1); head.Children.Add(chip); about.Children.Add(head);
        var kv = new List<(string, string?)>
        {
            ("Publisher", g?.Company ?? m0?.Company), ("Program file", path), ("Processes", t.Procs.Count == 1 ? $"1 (PID {t.Procs[0].Pid})" : $"{t.Procs.Count} separate processes"),
        };
        var svc = (g?.Services ?? m0?.Services ?? new List<string>()); if (svc.Count > 0) kv.Add(("Runs Windows services", string.Join(", ", svc.Take(8)) + (svc.Count > 8 ? $" and {svc.Count - 8} more" : "")));
        if (g?.WindowTitle != null || m0?.WindowTitle != null) kv.Add(("Window", g?.WindowTitle ?? m0?.WindowTitle));
        about.Children.Add(UI.KeyValues(kv, 150));
        var conn = UI.Label("Checking network connections…", "muted", 12); about.Children.Add(conn);
        var sigRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; var sigText = UI.Label("", null, 12, UI.Text, wrap: true);
        if (path != null && File.Exists(path)) { var b = new Button { Content = "Check digital signature" }; b.Click += async (_, _) => { b.IsEnabled = false; sigText.Text = "Checking…"; var r = await Task.Run(() => Authenticode.Check(path)); sigText.Text = r.Status + (r.Publisher != null ? " · publisher: " + r.Publisher : ""); sigText.Foreground = r.Valid ? UI.Ok : UI.Warn; }; sigRow.Children.Add(b); sigRow.Children.Add(sigText); about.Children.Add(sigRow); }
        var pids = t.Procs.Select(p => p.Pid).ToHashSet();
        _ = Task.Run(() => NetworkInfo.Connections().Where(c => pids.Contains(c.Pid)).ToList()).ContinueWith(task =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (aboutSig != sig) return; var cs = task.Result; var est = cs.Where(c => !c.Listening).ToList();
                conn.Text = cs.Count == 0 ? "No network connections right now." : $"Network: {est.Count(c => c.Scope == "Internet")} to the internet · {est.Count(c => c.Scope == "Local network")} on your local network · {cs.Count(c => c.Listening)} waiting for incoming connections. Open Network for the list.";
            }));
    }

    MenuFlyout BuildPriorityMenu()
    {
        var fly = new MenuFlyout();
        foreach (var (label, cls) in ProcessControl.Priorities)
        {
            var mi = new MenuItem { Header = label }; uint c = cls;
            mi.Click += (_, _) =>
            {
                var t = Current(); if (t == null) return; int ok = 0; string last = "";
                foreach (var p in t.Procs) { var (good, msg) = ProcessControl.SetPriority(p, c); if (good) ok++; last = msg; }
                M.StatusMessage = ok == t.Procs.Count ? $"{t.Name}: priority set to {label.ToLowerInvariant()}. It lasts until the program restarts." : ok > 0 ? $"{t.Name}: changed {ok} of {t.Procs.Count} processes. {last}" : $"{t.Name}: {last}"; M.Notify();
            };
            fly.Items.Add(mi);
        }
        return fly;
    }

    // ---------------- End task ----------------
    async Task EndSelectedAsync()
    {
        var t = Current(); var owner = UI.OwnerOf(this); if (t == null || owner == null) return;
        if (t.Procs.Any(p => p.Pid == Environment.ProcessId)) { await Dialogs.Message(owner, "Can't end this", "That is HardwareLens itself. Use the pink close button to quit it."); return; }
        foreach (var p in t.Procs)
        {
            var why = ProcessControl.WhyNotEnd(p.Pid, p.Name, M.Info.Get(p)?.Critical == true);
            if (why != null) { await Dialogs.Message(owner, "Windows needs this", $"“{t.Name}” cannot be ended from here.\n\n{why}"); return; }
        }
        bool windows = (t.Group?.Kind ?? AppGrouper.Classify(t.Procs[0], M.Info.Get(t.Procs[0]))) == AppKind.Windows;
        string what = t.Procs.Count == 1 ? $"“{t.Name}”" : $"“{t.Name}” and all {t.Procs.Count} of its processes";
        string body = $"End {what}?\n\nAnything it has not saved will be lost." + (windows ? "\n\nThis is part of Windows. Features that depend on it may stop working until it restarts or you restart the PC." : "");
        bool go = false; await Dialogs.Show(owner, "End task", UI.Label(body, null, 12.5, UI.Text, wrap: true), ("End task", () => go = true, true)); if (!go) return;

        M.StatusMessage = $"Ending {t.Name}…"; M.Notify();
        var withWin = t.Procs; // graceful first (apps with windows get a close request), the rest are ended directly
        var outcomes = await Task.Run(() =>
        {
            var res = new List<EndOutcome>();
            foreach (var p in withWin.OrderByDescending(p => M.Info.Get(p)?.HasWindow == true)) res.Add(ProcessControl.EndTask(p, false));
            return res;
        });
        var stuck = outcomes.Where(o => o.Result == EndResult.StillRunning).ToList(); var denied = outcomes.Where(o => o.Result == EndResult.AccessDenied).ToList();
        int done = outcomes.Count(o => o.Success || o.Result == EndResult.NotFound);
        if (stuck.Count > 0)
        {
            bool force = false;
            await Dialogs.Show(owner, "It didn't close", UI.Label($"{stuck.Count} process{(stuck.Count > 1 ? "es" : "")} of “{t.Name}” did not close when asked. It may be showing a “save changes?” prompt, or it may be frozen.\n\nForce it to end? Unsaved work will be lost.", null, 12.5, UI.Text, wrap: true), ("Force end", () => force = true, true));
            if (force) { var pids = stuck.Select(s => s.Pid).ToHashSet(); var again = await Task.Run(() => t.Procs.Where(p => pids.Contains(p.Pid)).Select(p => ProcessControl.EndTask(p, true)).ToList()); done += again.Count(o => o.Success || o.Result == EndResult.NotFound); denied.AddRange(again.Where(o => o.Result == EndResult.AccessDenied)); }
        }
        if (denied.Count > 0)
        {
            var acts = M.Elevated ? Array.Empty<(string, Action, bool)>() : new (string, Action, bool)[] { ("Restart as administrator", () => { if (Elevation.StartElevatedCopy()) owner.Close(); }, true) };
            await Dialogs.Show(owner, "Windows said no", UI.Label($"Windows would not let HardwareLens end {denied.Count} process{(denied.Count > 1 ? "es" : "")}. " + (M.Elevated ? "Even as administrator, Windows protects it." : "Restarting HardwareLens as administrator may allow it."), null, 12.5, UI.Text, wrap: true), acts);
        }
        M.StatusMessage = $"{t.Name}: ended {done} of {t.Procs.Count} process{(t.Procs.Count > 1 ? "es" : "")}."; M.Notify();
    }
}
