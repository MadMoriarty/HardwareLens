using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.App.Controls;
using HardwareLens.Core;
using HardwareLens.Windows;
namespace HardwareLens.App.Pages;

public sealed class ConnRow
{
    public string App { get; init; } = ""; public string Proto { get; init; } = ""; public string Local { get; init; } = ""; public string Remote { get; init; } = "";
    public string Service { get; init; } = ""; public string Scope { get; init; } = ""; public string State { get; init; } = ""; public bool Listening { get; init; }
}

/// <summary>Adapters, Wi-Fi, current throughput and which apps hold network connections. Read-only; nothing leaves the PC.</summary>
public sealed class NetworkPage : PageBase
{
    readonly StackPanel adapters = new() { Spacing = 10 };
    readonly ChartCard chart = new("Network throughput", "Mbit/s (decimal), summed over connected physical adapters.");
    readonly ObservableCollection<ConnRow> rows = new(); readonly DataGrid grid = new();
    readonly StackPanel talkers = new() { Spacing = 4 };
    readonly TextBlock connSummary = UI.Label("", "muted", 12, wrap: true), pingText = UI.Label("", null, 12, UI.Text, wrap: true);
    readonly CheckBox showVirtual = new() { Content = "Show virtual adapters" };
    int filter; List<AdapterInfo> ads = new(); List<ConnectionRow> conns = new(); DateTime lastRead = DateTime.MinValue; bool loading;
    string adSig = "";

    public NetworkPage(AppModel m) : base(m)
    {
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.RowHeight = 28; grid.CanUserResizeColumns = true; grid.MaxHeight = 380;
        void Col(string h, string p, double w, bool star = false)
        {
            var c = new DataGridTemplateColumn { Header = h, Width = star ? new DataGridLength(w, DataGridLengthUnitType.Star) : new DataGridLength(w) };
            c.CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ConnRow>((_, _) => { var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis }; t[!TextBlock.TextProperty] = new Binding(p); return t; }); grid.Columns.Add(c);
        }
        Col("App", "App", 1.4, true); Col("Protocol", "Proto", 70); Col("This PC", "Local", 170); Col("Remote", "Remote", 190); Col("Service", "Service", 100); Col("Where", "Scope", 90); Col("State", "State", 100);
        showVirtual.IsCheckedChanged += (_, _) => { adSig = ""; BuildAdapters(); };
        var refresh = UI.IconButton("refresh", "Refresh", "Read adapters and connections again"); refresh.Click += async (_, _) => await Load();
        var test = UI.IconButton("network", "Test connection", "Pings your router and DNS server a few times. Runs only when you click; nothing else is contacted."); test.Click += async (_, _) => await Ping();

        var root = new StackPanel { Margin = new Thickness(18, 14, 12, 14), Spacing = 14 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; head.Children.Add(UI.Label("Network", "h1"));
        var act = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; act.Children.Add(test); act.Children.Add(refresh); Grid.SetColumn(act, 1); head.Children.Add(act);
        root.Children.Add(head);
        root.Children.Add(pingText);
        root.Children.Add(UI.Section("Connections", adapters, showVirtual, "Adapters are the network cards and Wi-Fi radios in or attached to this PC."));
        chart.Margin = new Thickness(0); root.Children.Add(UI.Section("Traffic now", chart));
        var seg = UI.Segmented(new[] { "All", "Internet", "Listening" }, 0, i => { filter = i; Fill(); });
        var tl = new StackPanel { Spacing = 10 }; tl.Children.Add(connSummary); tl.Children.Add(talkers);
        root.Children.Add(UI.Section("Which apps are talking", tl, null, "Counted from open connections, not from how much data they send. Windows only reports per-app data volume to administrator-level tracing, which HardwareLens does not use."));
        var tbl = new StackPanel { Spacing = 6 }; tbl.Children.Add(seg); tbl.Children.Add(new Border { Child = grid, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        root.Children.Add(UI.Section("All connections", tbl));
        root.Children.Add(UI.Label("Not shown: your public IP address (needs an outside lookup) and other devices on your network (needs scanning). HardwareLens does neither.", "muted", 10.5, wrap: true));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public override bool RefreshWhenPaused => true;
    public override void OnShown() { _ = Load(); Refresh(); }

    public override void Refresh()
    {
        var s = M.ViewSample; var now = M.ViewNow; var win = M.ViewWindow;
        double rx = 0, tx = 0; bool any = false;
        if (s != null) foreach (var n in s.Nets) if (n.InTotal) { if (n.RxBps is double a) { rx += a; any = true; } if (n.TxBps is double b) { tx += b; any = true; } }
        chart.Update(ChartBuilder.Network(M.Collector.Store, now, win, null), now, win, any ? $"Receive {Fmt.Mbit(rx)} · Send {Fmt.Mbit(tx)}" : "No connected adapter reported traffic", s == null ? "unavailable" : "live", null, "Network counters are unavailable");
        if ((DateTime.Now - lastRead).TotalSeconds > 5 && !loading && !M.DisplayPaused) _ = Load();
    }

    async Task Load()
    {
        if (loading) return; loading = true;
        try
        {
            var (a, c) = await Task.Run(() => (NetworkInfo.Adapters(), NetworkInfo.Connections()));
            ads = a; conns = c; lastRead = DateTime.Now; BuildAdapters(); Fill();
        }
        catch (Exception e) { connSummary.Text = "Could not read network information: " + e.Message; }
        loading = false;
    }

    void BuildAdapters()
    {
        var list = ads.Where(a => showVirtual.IsChecked == true || !a.Virtual).OrderByDescending(a => a.Up).ThenBy(a => a.Name).ToList();
        string sig = string.Join("|", list.Select(a => a.Id + a.Up + string.Join(",", a.IPv4) + (a.Wifi?.SignalPercent ?? -1)));
        if (sig == adSig) return; adSig = sig; adapters.Children.Clear();
        if (list.Count == 0) { adapters.Children.Add(UI.Label("No network adapters were reported.", "muted")); return; }
        foreach (var a in list) adapters.Children.Add(AdapterCard(a));
    }

    Control AdapterCard(AdapterInfo a)
    {
        string speed = a.LinkBps is ulong b && b > 0 ? (b >= 1_000_000_000 ? $"{b / 1e9:0.#} Gbit/s" : $"{b / 1e6:0.#} Mbit/s") : null!;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(UI.Label(a.Name, "h2")); head.Children.Add(UI.Chip(a.Kind, UI.Muted)); head.Children.Add(UI.Chip(a.Up ? "Connected" : a.Status, a.Up ? UI.Text : UI.Muted, a.Up ? UI.Ok : UI.Faint));
        if (a.Virtual) head.Children.Add(UI.Chip("virtual", UI.Muted));
        var kv = new List<(string, string?)> { ("Adapter", a.Description) };
        if (a.Up)
        {
            kv.Add(("IPv4 address", a.IPv4.Count > 0 ? string.Join(", ", a.IPv4) : null)); if (a.IPv6.Count > 0) kv.Add(("IPv6 address", a.IPv6[0] + (a.IPv6.Count > 1 ? $" (+{a.IPv6.Count - 1} more)" : "")));
            kv.Add(("Router (gateway)", string.IsNullOrEmpty(a.Gateway) ? null : a.Gateway)); kv.Add(("DNS servers", string.IsNullOrEmpty(a.Dns) ? null : a.Dns));
            kv.Add(("Address from", a.DhcpEnabled ? "Automatic (DHCP)" + (string.IsNullOrEmpty(a.DhcpServer) ? "" : " · " + a.DhcpServer) : "Set manually"));
            kv.Add(("Link speed", speed));
        }
        if (a.Wifi is { } w)
        {
            kv.Add(("Wi-Fi network", w.NameWithheld ? "Hidden by Windows (location permission off)" : w.Ssid)); kv.Add(("Signal", w.SignalPercent > 0 ? w.SignalPercent + "%" : null)); kv.Add(("Standard", w.Standard)); kv.Add(("Security", w.Security));
            if (w.RxMbps != null || w.TxMbps != null) kv.Add(("Wi-Fi rate", $"{w.RxMbps:0} down / {w.TxMbps:0} up Mbit/s (negotiated, not measured)"));
        }
        var body = new StackPanel { Spacing = 8 }; body.Children.Add(head); body.Children.Add(UI.KeyValues(kv.Where(k => k.Item2 != null || k.Item1 == "IPv4 address" || k.Item1 == "Router (gateway)"), 130));
        var b2 = new Border { Child = body }; b2.Classes.Add("card"); return b2;
    }

    void Fill()
    {
        string App(int pid) { var g = M.Groups.FirstOrDefault(x => x.Procs.Any(p => p.Pid == pid)); return g?.Name ?? (pid == 0 ? "System" : $"Process {pid}"); }
        var all = conns.Select(c => new ConnRow { App = App(c.Pid), Proto = c.Proto, Local = c.Local, Remote = c.Listening ? "—" : c.Remote, Service = c.Service, Scope = c.Scope, State = c.State, Listening = c.Listening }).ToList();
        rows.Clear();
        foreach (var r in all.Where(r => filter == 0 || (filter == 1 ? r.Scope == "Internet" : r.Listening)).OrderBy(r => r.App).Take(600)) rows.Add(r);
        int internet = all.Count(r => r.Scope == "Internet"), listen = all.Count(r => r.Listening);
        connSummary.Text = $"{all.Count} open connections · {internet} to the internet · {listen} waiting for incoming connections";
        talkers.Children.Clear();
        var top = all.Where(r => r.Scope == "Internet").GroupBy(r => r.App).Select(g => (g.Key, n: g.Count(), svc: g.Select(x => x.Service).Where(x => x.Length > 0).Distinct().Take(3).ToList())).OrderByDescending(x => x.n).Take(8).ToList();
        if (top.Count == 0) talkers.Children.Add(UI.Label("No app currently has an internet connection open.", "muted"));
        int max = top.Count == 0 ? 1 : top[0].n;
        foreach (var t in top)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,300"), ColumnSpacing = 10 };
            g.Children.Add(UI.Label(t.Key, null, 12)); var bar = UI.Bar((double)t.n / max, UI.Info); Grid.SetColumn(bar, 1); g.Children.Add(bar);
            var l = UI.Label($"{t.n} connection{(t.n == 1 ? "" : "s")}" + (t.svc.Count > 0 ? " · " + string.Join(", ", t.svc) : ""), "muted", 11); Grid.SetColumn(l, 2); g.Children.Add(l); talkers.Children.Add(g);
        }
    }

    async Task Ping()
    {
        pingText.Text = "Testing…"; var gw = ads.Where(a => a.Up && !a.Virtual && !string.IsNullOrEmpty(a.Gateway)).Select(a => a.Gateway.Split(',')[0].Trim()).FirstOrDefault();
        var dns = ads.Where(a => a.Up && !a.Virtual && !string.IsNullOrEmpty(a.Dns)).Select(a => a.Dns.Split(',', ' ')[0].Trim()).FirstOrDefault();
        var parts = new List<string>();
        foreach (var (label, addr) in new[] { ("Router", gw), ("DNS server", dns) })
        {
            if (string.IsNullOrEmpty(addr)) { parts.Add($"{label}: no address found"); continue; }
            var r = await NetworkInfo.PingAsync(label, addr, 4);
            parts.Add(r.Error != null ? $"{label} ({addr}): {r.Error}" : r.Received == 0 ? $"{label} ({addr}): no reply" : $"{label} ({addr}): {r.Received}/{r.Sent} replies, {r.AvgMs:0} ms average" + (r.Received < r.Sent ? " — some packets were lost" : ""));
        }
        pingText.Text = string.Join("   ·   ", parts);
    }
}
