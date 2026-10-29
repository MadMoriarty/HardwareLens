using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
using HardwareLens.Windows;
namespace HardwareLens.App.Pages;

public sealed class DeviceRow
{
    public DeviceItem Item { get; init; } = null!;
    public string Name => Item.Name; public string Type => Item.Category; public string Maker => Item.Manufacturer ?? "—"; public string Connection => Item.Connection;
    public string Status => Item.HasProblem ? "Problem" : Item.Status; public IBrush StatusBrush => Item.HasProblem ? UI.Pink : UI.Muted;
    public string Extra => Item.Battery is int b ? $"Battery {b}%" : "";
}

/// <summary>Connected devices (mouse, keyboard, printers, monitors, audio…) shown by product name, not by generic driver text.</summary>
public sealed class DevicesPage : PageBase
{
    readonly ObservableCollection<DeviceRow> rows = new(); readonly DataGrid grid = new();
    readonly StackPanel detail = new() { Spacing = 8 }, extras = new() { Spacing = 14 };
    readonly TextBlock summary = UI.Label("", "muted", 12, wrap: true);
    readonly TextBox search = new() { Watermark = "Search devices", MinWidth = 240 };
    readonly CheckBox showInternal = new() { Content = "Include built-in devices" };
    DeviceSnapshot? snap;

    public DevicesPage(AppModel m) : base(m)
    {
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.SelectionMode = DataGridSelectionMode.Single; grid.RowHeight = 32; grid.CanUserResizeColumns = true; grid.MaxHeight = 420;
        void Col(string h, string p, double w, bool star = false)
        {
            var c = new DataGridTemplateColumn { Header = h, Width = star ? new DataGridLength(w, DataGridLengthUnitType.Star) : new DataGridLength(w) };
            c.CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DeviceRow>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis }; t[!TextBlock.TextProperty] = new Binding(p); if (p == "Status") t[!TextBlock.ForegroundProperty] = new Binding("StatusBrush"); return t; }); grid.Columns.Add(c);
        }
        Col("Device", "Name", 2, true); Col("Type", "Type", 130); Col("Maker", "Maker", 1.2, true); Col("Connected by", "Connection", 120); Col("", "Extra", 90); Col("Status", "Status", 80);
        grid.SelectionChanged += (_, _) => ShowDetail(grid.SelectedItem as DeviceRow);
        search.TextChanged += (_, _) => Fill(); showInternal.IsCheckedChanged += (_, _) => Fill();
        var refresh = UI.IconButton("refresh", "Refresh", "Read the device list again"); refresh.Click += async (_, _) => await M.LoadDevicesAsync();
        var dm = UI.IconButton("devices", "Device Manager", "Open Windows Device Manager"); dm.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); } catch { } };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; bar.Children.Add(search); bar.Children.Add(showInternal); bar.Children.Add(refresh); bar.Children.Add(dm);
        var root = new StackPanel { Margin = new Thickness(18, 14, 12, 14), Spacing = 12 };
        root.Children.Add(UI.Label("Devices", "h1"));
        root.Children.Add(UI.Label("Things plugged into or paired with this PC, named the way the manufacturer names them. Windows sometimes only reports a generic name (such as “USB Input Device”); those are marked, and the real product name is used wherever the device reports one.", "muted", 12, wrap: true));
        root.Children.Add(bar); root.Children.Add(summary);
        root.Children.Add(new Border { Child = grid, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        var d = new Border { Child = detail }; d.Classes.Add("card"); root.Children.Add(d);
        root.Children.Add(extras);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        M.DevicesChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Fill);
    }

    public override void Refresh() { }
    public override void OnShown() { if (M.Devices == null) _ = M.LoadDevicesAsync(); else Fill(); }

    void Fill()
    {
        snap = M.Devices;
        if (snap == null) { summary.Text = M.DevicesLoading ? "Reading devices…" : "No device information yet."; return; }
        string q = (search.Text ?? "").Trim();
        var src = snap.Peripherals.Concat(showInternal.IsChecked == true ? snap.Internal : Enumerable.Empty<DeviceItem>());
        var sel = (grid.SelectedItem as DeviceRow)?.Item;
        rows.Clear();
        foreach (var d in src.Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Category.Contains(q, StringComparison.OrdinalIgnoreCase) || (d.Manufacturer?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).OrderBy(d => d.Category).ThenBy(d => d.Name)) rows.Add(new DeviceRow { Item = d });
        int bad = snap.Peripherals.Count(p => p.HasProblem);
        summary.Text = (M.DevicesLoading ? "Refreshing… " : "") + $"{snap.Peripherals.Count} connected device{(snap.Peripherals.Count == 1 ? "" : "s")}" + (bad > 0 ? $" · {bad} reporting a problem" : "") + $" · read in {snap.Seconds:0.0} s" + (snap.Problems.Count > 0 ? " · some details could not be read: " + string.Join("; ", snap.Problems.Take(3)) : "");
        if (sel != null) grid.SelectedItem = rows.FirstOrDefault(r => r.Item.InstanceIds.FirstOrDefault() == sel.InstanceIds.FirstOrDefault());
        if (grid.SelectedItem == null) ShowDetail(null);
        BuildExtras();
    }

    void ShowDetail(DeviceRow? r)
    {
        detail.Children.Clear();
        if (r == null) { detail.Children.Add(UI.Label("Select a device to see its details.", "muted")); return; }
        var d = r.Item;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; head.Children.Add(UI.Label(d.Name, "h2")); head.Children.Add(UI.Chip(d.Category, UI.Muted));
        if (d.NameIsGeneric) { var c = UI.Chip("generic name from Windows", UI.Warn, UI.Warn); ToolTip.SetTip(c, "The device only reports a generic description; no more specific product name was available."); head.Children.Add(c); }
        detail.Children.Add(head);
        var kv = new List<(string, string?)> { ("Maker", d.Manufacturer), ("Model", d.Model), ("Connected by", d.Connection), ("Windows status", d.HasProblem ? d.Status : "Working"), ("USB ID (vendor:product)", d.VidPid), ("Where plugged in", d.Location),
            ("Driver", d.DriverProvider == null ? null : $"{d.DriverProvider} {d.DriverVersion}{(d.DriverDate is DateTime dd ? " · " + dd.ToString("d MMM yyyy") : "")}"), ("Battery", d.Battery is int b ? b + "%" : null) };
        if (M.IncludeIdentifiers) kv.Add(("Serial number", d.Serial)); else if (d.Serial != null) kv.Add(("Serial number", "Withheld (turn on identifiers in Export to show it)"));
        if (d.Note != null) kv.Add(("Note", d.Note));
        detail.Children.Add(UI.KeyValues(kv.Where(k => k.Item2 != null || k.Item1 is "Maker" or "Model" or "Driver"), 170));
        if (d.HardwareIds.Count > 0) detail.Children.Add(UI.Label("Hardware IDs: " + string.Join("  ", d.HardwareIds.Take(3)), "muted", 10.5, wrap: true));
        if (d.Nodes > 1) detail.Children.Add(UI.Label($"This product shows up in Windows as {d.Nodes} parts (for example a keyboard and a mouse receiver); they are combined here.", "muted", 10.5, wrap: true));
    }

    void BuildExtras()
    {
        extras.Children.Clear(); if (snap == null) return;
        if (snap.Printers.Count > 0)
        {
            var sp = new StackPanel { Spacing = 6 };
            foreach (var p in snap.Printers.Where(p => !p.Virtual).Concat(snap.Printers.Where(p => p.Virtual)))
                sp.Children.Add(Line(p.Name + (p.IsDefault ? "  (default)" : ""), $"{(p.Virtual ? "Virtual · " : "")}{(p.Offline ? "Offline" : p.Status)} · {(p.Port.Length > 36 ? p.Port[..36] + "…" : p.Port)} · {p.Driver}", p.Offline ? UI.Warn : null));
            extras.Children.Add(UI.Section("Printers", sp, null, "Includes software printers such as “Microsoft Print to PDF”, marked virtual."));
        }
        if (snap.Monitors.Count > 0)
        {
            var sp = new StackPanel { Spacing = 6 };
            foreach (var mo in snap.Monitors) sp.Children.Add(Line(mo.Name + (string.IsNullOrEmpty(mo.Maker) ? "" : " · " + mo.Maker), string.Join(" · ", new[] { mo.Mode, mo.Size, mo.Connection, mo.Year == null ? null : "made " + mo.Year }.Where(x => !string.IsNullOrEmpty(x))), null));
            extras.Children.Add(UI.Section("Monitors", sp));
        }
        if (snap.Audio.Count > 0)
        {
            var sp = new StackPanel { Spacing = 6 };
            foreach (var a in snap.Audio) sp.Children.Add(Line(a.Name, a.Direction + " · " + a.State, a.State == "Active" ? null : UI.Faint));
            extras.Children.Add(UI.Section("Sound", sp, null, "Output and input devices Windows can use. Unplugged ones are shown as not present."));
        }
    }

    static Control Line(string title, string sub, IBrush? sc)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 12 };
        g.Children.Add(UI.Label(title, null, 12.5)); var s = UI.Label(sub, "muted", 12, sc, wrap: true); Grid.SetColumn(s, 1); g.Children.Add(s); return g;
    }
}
