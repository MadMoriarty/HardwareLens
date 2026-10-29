using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Windows;
namespace HardwareLens.App.Pages;

public sealed class ServiceView
{
    public ServiceRow S { get; init; } = null!;
    public string Name => S.DisplayName; public string State => S.Running ? "Running" : S.State; public IBrush StateBrush => S.Running ? UI.Ok : UI.Muted;
    public string Start => S.StartMode switch { "Auto" => "Automatic", "Manual" => "Manual", "Disabled" => "Disabled", "Boot" or "System" => "Driver", _ => S.StartMode };
    public string Pid => S.Pid > 0 ? S.Pid.ToString() : "";
    public string Desc => S.Description;
}

/// <summary>Windows services: see what is running and start, stop or restart them (needs administrator rights).</summary>
public sealed class ServicesPage : PageBase
{
    readonly ObservableCollection<ServiceView> rows = new(); readonly DataGrid grid = new();
    readonly TextBox search = new() { Watermark = "Search services", MinWidth = 260 }; readonly TextBlock summary = UI.Label("", "muted", 12, wrap: true);
    readonly Button startBtn, stopBtn, restartBtn; int filter; List<ServiceRow> all = new(); bool loaded;

    public ServicesPage(AppModel m) : base(m)
    {
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.SelectionMode = DataGridSelectionMode.Single; grid.RowHeight = 28; grid.CanUserResizeColumns = true;
        void Text(string header, string prop, double w, bool star = false, IBrush? fg = null)
        {
            var c = new DataGridTemplateColumn { Header = header, Width = star ? new DataGridLength(w, DataGridLengthUnitType.Star) : new DataGridLength(w) };
            c.CellTemplate = new FuncDataTemplate<ServiceView>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis }; t[!TextBlock.TextProperty] = new Binding(prop); if (prop == "State") t[!TextBlock.ForegroundProperty] = new Binding("StateBrush"); return t; }); grid.Columns.Add(c);
        }
        Text("Service", "Name", 1.6, true); Text("Status", "State", 90); Text("Starts", "Start", 90); Text("PID", "Pid", 60); Text("What it does", "Desc", 2.4, true);
        search.TextChanged += (_, _) => Fill(); grid.SelectionChanged += (_, _) => UpdateButtons();
        startBtn = UI.IconButton("play", "Start"); stopBtn = UI.IconButton("stop", "Stop"); restartBtn = UI.IconButton("refresh", "Restart");
        startBtn.Click += async (_, _) => await Act("start"); stopBtn.Click += async (_, _) => await Act("stop"); restartBtn.Click += async (_, _) => await Act("restart");
        var refresh = UI.IconButton("refresh", "Refresh"); refresh.Click += async (_, _) => await Load();
        var admin = UI.IconButton("shield", "Restart as administrator", "Windows only lets administrators start or stop services"); admin.IsVisible = !M.Elevated; admin.Click += (_, _) => { if (Elevation.StartElevatedCopy()) UI.OwnerOf(this)?.Close(); };
        var seg = UI.Segmented(new[] { "All", "Running", "Stopped" }, 0, i => { filter = i; Fill(); });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (Control c in new Control[] { seg, search, startBtn, stopBtn, restartBtn, refresh, admin }) bar.Children.Add(c);
        var dock = new DockPanel { Margin = new Thickness(18, 14, 12, 12) };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) }; head.Children.Add(UI.Label("Services", "h1"));
        head.Children.Add(UI.Label("Background programs Windows runs for you. Stopping the wrong one can break features, so HardwareLens refuses to stop the few that Windows itself depends on, and asks first for the rest.", "muted", 12, wrap: true)); head.Children.Add(bar); head.Children.Add(summary);
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        dock.Children.Add(new Border { Child = grid, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        Content = dock; UpdateButtons();
    }

    public override void Refresh() { }
    public override void OnShown() { if (!loaded) _ = Load(); }
    ServiceRow? Sel => (grid.SelectedItem as ServiceView)?.S;

    void UpdateButtons() { var s = Sel; startBtn.IsEnabled = s != null && !s.Running; stopBtn.IsEnabled = s != null && s.Running; restartBtn.IsEnabled = s != null && s.Running; }

    async Task Load()
    {
        summary.Text = "Reading services…";
        try { all = await Task.Run(ServiceManager.List); loaded = true; Fill(); } catch (Exception e) { summary.Text = "Could not read services: " + e.Message; }
    }

    void Fill()
    {
        string q = (search.Text ?? "").Trim(); var keep = Sel?.Name; rows.Clear();
        foreach (var s in all.Where(s => (filter == 0 || (filter == 1) == s.Running) && (q.Length == 0 || s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))) rows.Add(new ServiceView { S = s });
        if (keep != null) grid.SelectedItem = rows.FirstOrDefault(r => r.S.Name == keep);
        summary.Text = $"{all.Count} services · {all.Count(s => s.Running)} running · {all.Count(s => !s.Running)} stopped" + (M.Elevated ? "" : " · standard user: you can look, but starting and stopping needs administrator rights");
    }

    async Task Act(string action)
    {
        var s = Sel; var owner = UI.OwnerOf(this); if (s == null || owner == null) return;
        if (!M.Elevated) { await Dialogs.Show(owner, "Administrator needed", UI.Label("Windows only lets an administrator start or stop services. Restart HardwareLens as administrator to continue.", null, 12.5, UI.Text, wrap: true), ("Restart as administrator", () => { if (Elevation.StartElevatedCopy()) owner.Close(); }, true)); return; }
        if (action != "start")
        {
            var why = ServiceManager.WhyNotStop(s); if (why != null) { await Dialogs.Message(owner, "Windows needs this", why); return; }
            bool go = false; await Dialogs.Show(owner, action == "stop" ? "Stop service" : "Restart service", UI.Label($"{(action == "stop" ? "Stop" : "Restart")} “{s.DisplayName}”?\n\n{(string.IsNullOrWhiteSpace(s.Description) ? "" : s.Description + "\n\n")}Features that depend on it may stop working while it is stopped.", null, 12.5, UI.Text, wrap: true), (action == "stop" ? "Stop" : "Restart", () => go = true, true)); if (!go) return;
        }
        M.StatusMessage = $"{action}ing {s.DisplayName}…".Replace("stoping", "stopping"); M.Notify();
        var (ok, msg) = await Task.Run(() => ServiceManager.Control(s, action)); M.StatusMessage = msg; M.Notify(); await Load();
        if (!ok) await Dialogs.Message(owner, "Not changed", msg);
    }
}
