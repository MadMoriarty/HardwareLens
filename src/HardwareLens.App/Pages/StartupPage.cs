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

public sealed class StartupRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public StartupItem Item { get; init; } = null!;
    public string Name => Item.Name; public string Publisher => Item.Publisher ?? "—"; public string Where => Item.Location;
    public string Status => Item.Enabled ? "On" : "Off"; public IBrush StatusBrush => Item.Enabled ? UI.Ok : UI.Faint;
    public string Action => Item.Enabled ? "Turn off" : "Turn on";
    public void Changed() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}

/// <summary>Programs that start with Windows. Turn them on or off; nothing is deleted.</summary>
public sealed class StartupPage : PageBase
{
    readonly ObservableCollection<StartupRow> rows = new(); readonly DataGrid grid = new();
    readonly TextBlock summary = UI.Label("", "muted", 12, wrap: true), note = UI.Label("", null, 12, UI.Text, wrap: true);
    readonly TextBox search = new() { Watermark = "Search startup programs", MinWidth = 260 };
    List<StartupItem> all = new(); bool loaded;

    public StartupPage(AppModel m) : base(m)
    {
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.SelectionMode = DataGridSelectionMode.Single; grid.RowHeight = 34; grid.CanUserResizeColumns = true;
        void Text(string header, string prop, double w, bool star = false)
        {
            var c = new DataGridTemplateColumn { Header = header, Width = star ? new DataGridLength(w, DataGridLengthUnitType.Star) : new DataGridLength(w) };
            c.CellTemplate = new FuncDataTemplate<StartupRow>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis }; t[!TextBlock.TextProperty] = new Binding(prop); return t; }); grid.Columns.Add(c);
        }
        Text("Program", "Name", 2.2, true); Text("Publisher", "Publisher", 1.5, true); Text("Where it starts from", "Where", 220);
        var st = new DataGridTemplateColumn { Header = "Status", Width = new DataGridLength(70) };
        st.CellTemplate = new FuncDataTemplate<StartupRow>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), FontWeight = FontWeight.SemiBold }; t[!TextBlock.TextProperty] = new Binding("Status"); t[!TextBlock.ForegroundProperty] = new Binding("StatusBrush"); return t; }); grid.Columns.Add(st);
        var act = new DataGridTemplateColumn { Header = "", Width = new DataGridLength(110) };
        act.CellTemplate = new FuncDataTemplate<StartupRow>((_, _) =>
        {
            var b = new Button { MinHeight = 24, Padding = new Thickness(10, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0) }; b[!ContentControl.ContentProperty] = new Binding("Action");
            b.Click += async (s, _) => { if ((s as Control)?.DataContext is StartupRow r) await Toggle(r); }; return b;
        }); grid.Columns.Add(act);
        search.TextChanged += (_, _) => Fill();
        var refresh = UI.IconButton("refresh", "Refresh", "Read the startup list again"); refresh.Click += async (_, _) => await Load();
        var admin = UI.IconButton("shield", "Restart as administrator", "Needed only to change programs that start for every user of this PC"); admin.IsVisible = !M.Elevated;
        admin.Click += (_, _) => { if (Elevation.StartElevatedCopy()) UI.OwnerOf(this)?.Close(); else { M.StatusMessage = "Administrator restart was cancelled."; M.Notify(); } };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; bar.Children.Add(search); bar.Children.Add(refresh); bar.Children.Add(admin);
        var dock = new DockPanel { Margin = new Thickness(18, 14, 12, 12) };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) }; head.Children.Add(UI.Label("Startup", "h1")); head.Children.Add(UI.Label("Programs that start by themselves when you sign in. Turning one off stops it from starting next time; it does not uninstall anything or stop it if it is already running.", "muted", 12, wrap: true)); head.Children.Add(bar); head.Children.Add(summary);
        var foot = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) }; foot.Children.Add(note);
        foot.Children.Add(UI.Label("Startup impact (how much a program slows boot) is not shown: Windows keeps those measurements to itself. Store-app startup tasks and scheduled tasks are not listed here.", "muted", 10.5, wrap: true));
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom); dock.Children.Add(head); dock.Children.Add(foot);
        dock.Children.Add(new Border { Child = grid, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        Content = dock;
    }

    public override void Refresh() { }
    public override void OnShown() { if (!loaded) _ = Load(); }

    async Task Load()
    {
        summary.Text = "Reading startup entries…"; List<string> probs = new();
        all = await Task.Run(() => { var l = StartupManager.List(out var p); probs = p; return l; }); loaded = true; Fill();
        if (probs.Count > 0) note.Text = "Some places could not be read: " + string.Join("; ", probs);
    }

    void Fill()
    {
        string q = (search.Text ?? "").Trim(); rows.Clear();
        foreach (var it in all.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (i.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))) rows.Add(new StartupRow { Item = it });
        summary.Text = $"{all.Count} program{(all.Count == 1 ? "" : "s")} start with Windows · {all.Count(i => i.Enabled)} on · {all.Count(i => !i.Enabled)} off";
    }

    async Task Toggle(StartupRow r)
    {
        var it = r.Item; bool enable = !it.Enabled; var owner = UI.OwnerOf(this);
        if (it.NeedsAdmin && !M.Elevated)
        {
            if (owner != null) await Dialogs.Show(owner, "Administrator needed", UI.Label($"“{it.Name}” starts for every user of this PC, so Windows only lets an administrator change it. Restart HardwareLens as administrator to continue.", null, 12.5, UI.Text, wrap: true), ("Restart as administrator", () => { if (Elevation.StartElevatedCopy()) owner.Close(); }, true));
            return;
        }
        var (ok, msg) = await Task.Run(() => StartupManager.SetEnabled(it, enable)); r.Changed(); Fill(); note.Text = (ok ? "" : "Not changed: ") + msg; note.Foreground = ok ? UI.Ok : UI.Warn; M.StatusMessage = $"{it.Name}: {msg}"; M.Notify();
    }
}
