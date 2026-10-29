using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HardwareLens.App.Pages;
using HardwareLens.Core;
using HardwareLens.Windows;
namespace HardwareLens.App;

public partial class MainWindow : Window
{
    readonly AppModel M = new();
    readonly DispatcherTimer timer = new();
    readonly Dictionary<string, PageBase> pages = new();
    readonly Dictionary<string, Button> navButtons = new();
    string current = "";
    bool closing;
    Control? maxGlyph;
    MenuItem? idItem;

    static readonly (string group, (string name, string icon)[] items)[] Nav =
    {
        ("Monitor", new[] { ("Overview", "overview"), ("Apps", "apps"), ("Performance", "live") }),
        ("Manage", new[] { ("Startup", "startup"), ("Services", "services") }),
        ("This computer", new[] { ("Hardware", "hardware"), ("Devices", "devices"), ("Storage", "storage"), ("Network", "network") }),
        ("Insights", new[] { ("Observations", "observations"), ("Data & limits", "about") }),
    };
    MiniWindow? mini;

    public MainWindow()
    {
        InitializeComponent();
        // restore the user's last size/position (never forced to another monitor)
        var s = M.Settings;
        if (s.WindowWidth is double w && s.WindowHeight is double h) { Width = w; Height = h; }
        if (s.WindowX is double x && s.WindowY is double y) { WindowStartupLocation = WindowStartupLocation.Manual; Position = new PixelPoint((int)x, (int)y); }

        // maximize/restore glyph (outline square) inside the grey control
        maxGlyph = new Avalonia.Controls.Shapes.Rectangle { Width = 6, Height = 6, Stroke = UI.Text, StrokeThickness = 1, RadiusX = 1, RadiusY = 1, IsHitTestVisible = false };
        MaximizeButton.Content = maxGlyph;
        ExportButton.Content = ExportContent();
        ExportButton.Flyout = BuildExportMenu();
        ShortcutsButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { UI.Icon("keyboard", 14, UI.Text), new TextBlock { Text = "Keyboard shortcuts" } } };
        ToolTip.SetTip(ShortcutsButton, "Show keyboard shortcuts (F1)");

        int num = 0; bool firstGroup = true;
        foreach (var (group, items) in Nav)
        {
            var cap = UI.Label(group.ToUpperInvariant(), "caption"); cap.Margin = new Thickness(12, firstGroup ? 2 : 14, 0, 4); firstGroup = false; NavHost.Children.Add(cap);
            foreach (var (name, icon) in items)
            {
                num++; string key = num <= 9 ? "Ctrl+" + num : num == 10 ? "Ctrl+0" : "";
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(UI.Icon(icon, 16, UI.Muted)); row.Children.Add(UI.Label(name, null, 12.5));
                var b = new Button { Content = row }; b.Classes.Add("nav"); string n = name; b.Click += (_, _) => Navigate(n);
                ToolTip.SetTip(b, key.Length > 0 ? $"{name} ({key})" : name); Avalonia.Automation.AutomationProperties.SetName(b, name);
                navButtons[name] = b; NavHost.Children.Add(b);
            }
        }
        MiniButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { UI.Icon("mini", 14, UI.Text), new TextBlock { Text = "Mini monitor" } } }; ToolTip.SetTip(MiniButton, "Small always-on-top readout (Ctrl+Shift+M)");
        AdminButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { UI.Icon("shield", 14, UI.Text), new TextBlock { Text = "Restart as administrator" } } }; AdminButton.IsVisible = !M.Elevated;
        ToolTip.SetTip(AdminButton, "Needed to end protected programs, change services, or change startup items for all users");

        pages["Overview"] = new OverviewPage(M); pages["Apps"] = new AppsPage(M); pages["Performance"] = new PerformancePage(M); pages["Startup"] = new StartupPage(M); pages["Services"] = new ServicesPage(M);
        pages["Hardware"] = new HardwarePage(M); pages["Devices"] = new DevicesPage(M); pages["Storage"] = new StoragePage(M); pages["Network"] = new NetworkPage(M);
        pages["Observations"] = new ObservationsPage(M); pages["Data & limits"] = new AboutPage(M);
        foreach (var p in pages.Values) { p.IsVisible = false; ContentHost.Children.Add(p); }
        M.Navigate = n => Dispatcher.UIThread.Post(() => Navigate(n));

        KeyDown += OnKeyDown;
        Closing += OnClosing;
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateMaxButton(); };
        Opened += async (_, _) => await OnOpened();
        M.StateChanged += () => Dispatcher.UIThread.Post(Tick);
        M.InventoryChanged += () => Dispatcher.UIThread.Post(() => { foreach (var p in pages.Values) p.OnInventoryChanged(); Tick(); });
        timer.Tick += (_, _) => Tick();
        SetTimer();
        Navigate(pages.ContainsKey(s.Section) ? s.Section : "Overview");
    }

    async Task OnOpened()
    {
        // If the saved position is no longer on any monitor, fall back to centred on the primary screen.
        try
        {
            var screens = Screens; var all = screens?.All;
            if (all != null && all.Count > 0 && Position is var pos && !all.Any(sc => sc.WorkingArea.Contains(new PixelPoint(pos.X + 40, pos.Y + 20))))
            {
                var p = screens!.Primary ?? all[0]; Position = new PixelPoint(p.WorkingArea.X + Math.Max(0, (p.WorkingArea.Width - (int)(Width * p.Scaling)) / 2), p.WorkingArea.Y + Math.Max(0, (p.WorkingArea.Height - (int)(Height * p.Scaling)) / 2));
            }
        }
        catch { }
        if (M.Settings.Maximized) WindowState = WindowState.Maximized;
        await M.LoadInventoryAsync();
    }

    void SetTimer() { timer.Interval = TimeSpan.FromSeconds(Math.Clamp(M.Settings.RefreshSeconds, 1, 2)); timer.Stop(); timer.Start(); }

    Control ExportContent()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(UI.Icon("export", 14, UI.B("#1A0D0A"))); sp.Children.Add(new TextBlock { Text = "Export report ▾", VerticalAlignment = VerticalAlignment.Center, Foreground = UI.B("#1A0D0A") }); return sp;
    }

    MenuFlyout BuildExportMenu()
    {
        var fly = new MenuFlyout();
        var html = new MenuItem { Header = "HTML report (opens in any browser)…", InputGesture = new KeyGesture(Key.E, KeyModifiers.Control) }; html.Click += async (_, _) => await ExportAsync(true);
        var json = new MenuItem { Header = "JSON data (structured)…", InputGesture = new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift) }; json.Click += async (_, _) => await ExportAsync(false);
        idItem = new MenuItem { Header = "Include device identifiers (serial numbers, MAC, computer name)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = false };
        idItem.Click += (_, _) => { M.IncludeIdentifiers = idItem!.IsChecked; M.StatusMessage = M.IncludeIdentifiers ? "Identifiers will be INCLUDED in the next export. Do not share such files publicly." : "Identifiers are withheld from exports (default)."; Tick(); };
        var note = new MenuItem { Header = "Off by default. Identifiers can single out this computer.", IsEnabled = false, FontSize = 10 };
        var cmp = new MenuItem { Header = "Compare two exported JSON reports…" }; cmp.Click += async (_, _) => await CompareAsync();
        fly.Items.Add(html); fly.Items.Add(json); fly.Items.Add(new Separator()); fly.Items.Add(cmp); fly.Items.Add(new Separator()); fly.Items.Add(idItem); fly.Items.Add(note);
        return fly;
    }

    // ---------------- navigation / refresh ----------------
    public void Navigate(string name)
    {
        if (!pages.ContainsKey(name)) return;
        current = name;
        foreach (var kv in pages) kv.Value.IsVisible = kv.Key == name;
        foreach (var kv in navButtons) kv.Value.Classes.Set("selected", kv.Key == name);
        M.Settings.Section = name;
        pages[name].OnShown(); Tick();
    }

    void Tick()
    {
        if (closing || WindowState == WindowState.Minimized) return;
        try
        {
            M.Update();
            var page = pages[current];
            if (!M.DisplayPaused || page.RefreshWhenPaused) page.Refresh();
            UpdateStatus();
        }
        catch (Exception e) { MessageText.Text = "Display error: " + e.Message; }
    }

    void UpdateStatus()
    {
        var c = M.Collector; var latest = c.Latest; double iv = c.Interval.TotalSeconds;
        if (M.CollectionSuspended) StateChip.Content = UI.Chip("Collection suspended", UI.Warn, UI.Warn);
        else if (M.DisplayPaused) StateChip.Content = UI.Chip("Display paused · collection continues", UI.Warn, UI.Ok);
        else StateChip.Content = UI.Chip($"Collecting every {iv:0} s", UI.Text, UI.Ok);
        if (M.Recording) StateChip.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { (Control)StateChip.Content!, UI.Chip("● Recording " + Fmt.Bytes(M.Recorder?.BytesWritten ?? 0), UI.Pink, UI.Pink) } };
        else if (M.Recorder is { Active: false, StoppedReason: { } why } && why.StartsWith("Size limit")) M.StatusMessage ??= "Recording stopped: " + why + ".";
        string msg = M.StatusMessage ?? (latest == null ? "Starting up…" : M.DisplayPaused ? $"Display frozen at {M.FrozenAt:HH:mm:ss}; collection is still recording history (latest sample {latest.Time:HH:mm:ss})." : $"Latest sample {latest.Time:HH:mm:ss} · {M.Inventory?.Elevation ?? "…"}");
        MessageText.Text = msg;
        var me = c.LatestData?.Processes.FirstOrDefault(p => p.Pid == c.SelfPid);
        SelfText.Text = me == null ? "" : $"HardwareLens itself: {Fmt.Percent(me.CpuPercent, 1)} CPU · {Fmt.Bytes(me.WorkingSetBytes, 0)} · sample took {c.LastCollectMilliseconds:0} ms";
    }

    // ---------------- window controls ----------------
    void TitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
        { if (e.ClickCount == 2) ToggleMaximize(); else BeginMoveDrag(e); }
    }
    void CloseClick(object? sender, RoutedEventArgs e) => Close();
    void MinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void UpdateMaxButton()
    {
        bool max = WindowState == WindowState.Maximized;
        ToolTip.SetTip(MaximizeButton, max ? "Restore (F11)" : "Maximize (F11)");
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, max ? "Restore HardwareLens" : "Maximize HardwareLens");
        if (maxGlyph is Avalonia.Controls.Shapes.Rectangle r) { r.Width = max ? 5 : 6; r.Height = max ? 5 : 6; r.Margin = max ? new Thickness(0, 2, 2, 0) : default; }
    }

    async void ShortcutsClick(object? sender, RoutedEventArgs e) => await ShowShortcuts();
    async Task ShowShortcuts()
    {
        var rows = new (string, string)[]
        {
            ("Ctrl+1 … Ctrl+0", "Jump to a page, in the order they are listed on the left"), ("Ctrl+Shift+M", "Mini monitor (always on top)"), ("Ctrl+P", "Pause or resume the display (collection continues)"), ("Ctrl+Shift+P", "Suspend or resume collection itself"), ("Ctrl+R", "Start or stop recording to a local file"),
            ("Ctrl+E", "Export HTML report"), ("Ctrl+Shift+E", "Export JSON data"), ("F5", "Re-read the hardware inventory"), ("Ctrl+F", "Search apps"), ("Esc", "Clear search"),
            ("F11 / double-click title bar", "Maximize or restore"), ("Ctrl+M", "Minimize"), ("Ctrl+W / Alt+F4", "Close"), ("F1", "This list"),
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), RowSpacing = 6, ColumnSpacing = 12 };
        for (int i = 0; i < rows.Length; i++) { g.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); var a = UI.Label(rows[i].Item1, null, 12, UI.Text, FontWeight.SemiBold); Grid.SetRow(a, i); g.Children.Add(a); var b = UI.Label(rows[i].Item2, "muted", 12, wrap: true); Grid.SetRow(b, i); Grid.SetColumn(b, 1); g.Children.Add(b); }
        await Dialogs.Show(this, "Keyboard shortcuts", g);
    }

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F11) { ToggleMaximize(); e.Handled = true; return; }
        if (e.Key == Key.F1) { await ShowShortcuts(); e.Handled = true; return; }
        if (e.Key == Key.F5) { await M.LoadInventoryAsync(); e.Handled = true; return; }
        if (!ctrl) return;
        switch (e.Key)
        {
            case >= Key.D0 and <= Key.D9 when !shift: { int n = e.Key == Key.D0 ? 10 : e.Key - Key.D0; var all = Nav.SelectMany(g => g.items).ToArray(); if (n >= 1 && n <= all.Length) Navigate(all[n - 1].name); break; }
            case Key.P: if (shift) M.ToggleSuspend(); else M.TogglePause(); break;
            case Key.R: M.ToggleRecord(); break;
            case Key.E: await ExportAsync(!shift); break;
            case Key.F: Navigate("Apps"); (pages["Apps"] as AppsPage)?.FocusSearch(); break;
            case Key.M when shift: ToggleMini(); break;
            case Key.M: WindowState = WindowState.Minimized; break;
            case Key.W: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    void ToggleMini() { if (mini != null) { mini.Close(); mini = null; return; } mini = new MiniWindow(M) { WindowStartupLocation = WindowStartupLocation.Manual }; mini.Closed += (_, _) => mini = null;
        try { var sc = Screens.ScreenFromWindow(this) ?? Screens.Primary; if (sc != null) { double k = sc.Scaling; mini.Position = new PixelPoint(sc.WorkingArea.Right - (int)(300 * k) - 24, sc.WorkingArea.Y + 24); } } catch { }
        mini.Show(); }
    void MiniClick(object? sender, RoutedEventArgs e) => ToggleMini();
    void AdminClick(object? sender, RoutedEventArgs e) { if (Elevation.StartElevatedCopy()) Close(); else { M.StatusMessage = "Administrator restart was cancelled."; Tick(); } }

    async Task CompareAsync()
    {
        try
        {
            var opt = new FilePickerOpenOptions { Title = "Pick the OLDER report, then the NEWER one (JSON exports)", AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType("HardwareLens JSON") { Patterns = new[] { "*.json" } } } };
            var files = await StorageProvider.OpenFilePickerAsync(opt);
            if (files.Count != 2) { if (files.Count > 0) await Dialogs.Message(this, "Compare reports", "Select exactly two JSON files at once (hold Ctrl): the older one and the newer one."); return; }
            var ordered = files.Select(f => (f, p: f.TryGetLocalPath()!)).Where(x => x.p != null).OrderBy(x => File.GetLastWriteTime(x.p)).ToList();
            var a = await File.ReadAllTextAsync(ordered[0].p); var b = await File.ReadAllTextAsync(ordered[1].p);
            var diff = await Task.Run(() => ReportDiff.Compare(a, b));
            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(UI.Label($"Older: {Path.GetFileName(ordered[0].p)}\nNewer: {Path.GetFileName(ordered[1].p)}\nTimes, live readings and free space are ignored.", "muted", 11, wrap: true));
            if (diff.Count == 0) sp.Children.Add(UI.Label("No hardware or configuration differences found.", null, 12.5, UI.Ok));
            foreach (var d in diff.Take(150))
            {
                string t = d.Kind == "changed" ? $"{d.Before}  →  {d.After}" : d.Kind == "added" ? "Added: " + d.After : "Removed: " + d.Before;
                var row = new StackPanel { Spacing = 0 }; row.Children.Add(UI.Label(d.Path, "muted", 10.5, wrap: true)); row.Children.Add(UI.Label(t, null, 12, d.Kind == "added" ? UI.Ok : d.Kind == "removed" ? UI.Pink : UI.Text, wrap: true)); sp.Children.Add(row);
            }
            if (diff.Count > 150) sp.Children.Add(UI.Label($"…and {diff.Count - 150} more.", "muted"));
            await Dialogs.Show(this, $"Compare reports · {diff.Count} difference{(diff.Count == 1 ? "" : "s")}", sp);
        }
        catch (Exception e) { await Dialogs.Message(this, "Compare failed", e.Message); }
    }

    // ---------------- export ----------------
    async Task ExportAsync(bool html)
    {
        try
        {
            if (M.Inventory == null) { M.StatusMessage = M.InventoryLoading ? "The hardware inventory is still being read. Try again in a moment." : "No inventory available to export: " + M.InventoryError; Tick(); return; }
            bool ids = M.IncludeIdentifiers;
            string name = $"HardwareLens-report-{DateTime.Now:yyyyMMdd-HHmm}{(ids ? "-with-identifiers" : "")}";
            IStorageFolder? start = null;
            try { start = await StorageProvider.TryGetFolderFromPathAsync(M.Settings.LastExportFolder is { } lf && Directory.Exists(lf) ? lf : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); } catch { }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = html ? "Export HTML report" : "Export JSON data", SuggestedFileName = name, DefaultExtension = html ? "html" : "json", SuggestedStartLocation = start, ShowOverwritePrompt = true,
                FileTypeChoices = new[] { html ? new FilePickerFileType("HTML report") { Patterns = new[] { "*.html" } } : new FilePickerFileType("JSON data") { Patterns = new[] { "*.json" } } }
            });
            string? path = file?.TryGetLocalPath(); if (path == null) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != (html ? ".html" : ".json")) path += html ? ".html" : ".json";
            if (path.Equals(M.Store.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a different file name; the application settings file cannot be replaced by an export.");
            var report = M.BuildReport(ids);
            string text = await Task.Run(() => html ? ReportBuilder.Html(report) : ReportBuilder.Json(report));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(tmp, text, new System.Text.UTF8Encoding(false)); File.Move(tmp, path, true); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
            M.Settings.LastExportFolder = Path.GetDirectoryName(path); M.Save();
            M.StatusMessage = "Report saved: " + path; Tick();
            string note = (ids ? "Identifiers (serial numbers, MAC addresses, computer name) ARE included. Do not share this file publicly.\n\n" : "Serial numbers, MAC addresses and the computer name were left out.\n\n") + path;
            var acts = new List<(string, Action, bool)>();
            if (html) acts.Add(("Open report", () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }), true));
            acts.Add(("Show in folder", () => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }), false));
            await Dialogs.Message(this, "Report saved", note, acts.ToArray());
        }
        catch (Exception e) { M.StatusMessage = "Export failed: " + e.Message; Tick(); await Dialogs.Message(this, "Export failed", e.Message); }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closing) return; closing = true; timer.Stop();
        try
        {
            var s = M.Settings; s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { s.WindowX = Position.X; s.WindowY = Position.Y; s.WindowWidth = Width; s.WindowHeight = Height; }
            M.Dispose();
        }
        catch { }
    }
}
