using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>Privacy statement, data-source status, limitations and file locations.</summary>
public sealed class AboutPage : PageBase
{
    readonly StackPanel root = new() { Margin = new Thickness(16, 14, 8, 14), Spacing = 10 };
    string sig = "";
    public AboutPage(AppModel m) : base(m) { Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; }
    public override void OnInventoryChanged() { sig = ""; }
    public override void Refresh()
    {
        string s = (M.Inventory?.CollectedAt.Ticks ?? 0) + "|" + M.Collector.CounterSources.Count; if (s == sig) return; sig = s;
        root.Children.Clear();
        root.Children.Add(UI.Label("Data and limits", "h1"));
        var asm = typeof(AboutPage).Assembly.GetName().Version;
        root.Children.Add(UI.Card(UI.Label("HardwareLens reads hardware and performance information from Windows on this computer and shows it here. It sends no telemetry, needs no account and does not upload reports. Serial numbers, MAC addresses and the computer name are left out of reports unless you tick the option in the Export menu. Settings and detected incidents are saved locally; CSV recording is optional. It starts as a normal user and installs no drivers. Actions you choose in Apps, Startup and Services can end processes, change process priority, enable or disable startup entries, and start, stop or restart services. Some actions require restarting as administrator. Review each action before confirming it.", null, 12, UI.Text, wrap: true), "Privacy and management actions", $"Version {asm?.Major}.{asm?.Minor}.{asm?.Build}"));

        var src = new StackPanel { Spacing = 6 };
        var all = new List<SourceStatus>(); if (M.Inventory != null) all.AddRange(M.Inventory.Sources); all.AddRange(M.Collector.CounterSources);
        if (all.Count == 0) src.Children.Add(UI.Label("Sources appear once the inventory has been read.", "muted"));
        foreach (var x in all)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,Auto,*"), ColumnSpacing = 10 };
            g.Children.Add(UI.Label(x.Name, null, 12, UI.Text, FontWeight.SemiBold, true));
            var chip = UI.Chip(x.State, x.State == "ok" ? UI.Muted : x.State == "partial" ? UI.Warn : UI.Pink, x.State == "ok" ? UI.Ok : x.State == "partial" ? UI.Warn : UI.Pink); Grid.SetColumn(chip, 1); chip.VerticalAlignment = VerticalAlignment.Top; g.Children.Add(chip);
            var c = new StackPanel { Spacing = 1 }; c.Children.Add(UI.Label(x.Api, "muted", 11, wrap: true)); if (!string.IsNullOrWhiteSpace(x.Note)) c.Children.Add(UI.Label(x.Note!, null, 11, UI.Text, wrap: true));
            Grid.SetColumn(c, 2); g.Children.Add(c); src.Children.Add(g);
        }
        root.Children.Add(UI.Card(src, "Where the numbers come from", "Status of each source on this computer."));

        var lim = new StackPanel { Spacing = 6 };
        foreach (var l in Limitations.All) lim.Children.Add(UI.Label("• " + l, null, 11.5, UI.Text, wrap: true));
        root.Children.Add(UI.Card(lim, "Limitations"));

        var files = new[] { ("Settings file", M.Store.Path), ("Recordings folder", M.Store.RecordingsFolder), ("Elevation", M.Inventory?.Elevation ?? "Standard user") };
        var fk = UI.KeyValues(files.Select(f => (f.Item1, (string?)f.Item2)), 140);
        var fb = new StackPanel { Spacing = 6, Children = { fk } };
        fb.Children.Add(UI.Label("Recordings are CSV files (device readings, per-process readings and a small info file). Recording stops by itself at the size limit in settings so it cannot fill the disk. Delete the settings file to reset the app.", "muted", 10.5, wrap: true));
        if (M.Store.Warning.Length > 0) fb.Children.Add(UI.Label(M.Store.Warning, null, 11, UI.Warn, wrap: true));
        root.Children.Add(UI.Card(fb, "Files"));
    }
}
