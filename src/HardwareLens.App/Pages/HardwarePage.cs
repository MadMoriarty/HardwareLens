using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>Hardware inventory. Values come straight from Windows; anything Windows does not report is shown as such.</summary>
public sealed class HardwarePage : PageBase
{
    readonly StackPanel root = new() { Margin = new Thickness(16, 14, 6, 14), Spacing = 10 };
    public HardwarePage(AppModel m) : base(m) { Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; }
    public override void Refresh() { }
    public override void OnShown() => Build();
    public override void OnInventoryChanged() => Build();
    string builtFor = "";

    static string? S(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string? N<T>(T? v, Func<T, string> f) where T : struct => v.HasValue ? f(v.Value) : null;

    static Grid Table(string[] heads, IEnumerable<string?[]> rows, string widths)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 12, RowSpacing = 4 };
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < heads.Length; c++) { var h = UI.Label(heads[c], "caption"); Grid.SetColumn(h, c); g.Children.Add(h); }
        int r = 1;
        foreach (var row in rows)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < heads.Length; c++)
            {
                string? v = c < row.Length ? row[c] : null; bool miss = string.IsNullOrWhiteSpace(v);
                var t = UI.Label(miss ? "not reported" : v!, null, 12, miss ? UI.Faint : UI.Text, wrap: true); if (miss) t.FontStyle = FontStyle.Italic;
                Grid.SetRow(t, r); Grid.SetColumn(t, c); g.Children.Add(t);
            }
            r++;
        }
        return g;
    }

    Border Cap(string title, Control body, string? sub = null, string? icon = null)
    {
        var b = UI.Card(body, title, sub); b.Margin = new Thickness(0, 0, 10, 0); return b;
    }

    void Build()
    {
        var inv = M.Inventory;
        string sig = inv == null ? (M.InventoryLoading ? "loading" : "none:" + M.InventoryError) : inv.CollectedAt.Ticks + "|" + M.Settings.ShowEmptyReaders;
        if (sig == builtFor) return; builtFor = sig;
        root.Children.Clear();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(UI.Label("Hardware inventory", "h1"));
        var re = UI.IconButton("refresh", "Re-read hardware", "Collect the inventory again (F5)"); re.Click += async (_, _) => await M.LoadInventoryAsync(); Grid.SetColumn(re, 1); head.Children.Add(re);
        root.Children.Add(head);

        if (inv == null)
        {
            root.Children.Add(UI.Card(UI.Label(M.InventoryLoading ? "Reading hardware information from Windows…" : "The inventory could not be collected: " + M.InventoryError, null, 12, M.InventoryLoading ? UI.Muted : UI.Pink, wrap: true)));
            return;
        }
        root.Children.Add(UI.Label($"Collected {inv.CollectedAt:yyyy-MM-dd HH:mm:ss} in {inv.CollectionSeconds:0.0} s · {inv.Elevation} · values are exactly what Windows reports; blanks are never guessed.", "muted", 11, wrap: true));

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control c, double w = 430) { c.Width = w; c.Margin = new Thickness(0, 0, 10, 10); wrap.Children.Add(c); }
        var sys = inv.System;
        Add(UI.Card(UI.KeyValues(new (string, string?)[]
        {
            ("Computer", string.Join(" ", new[] { S(sys.Manufacturer), S(sys.Model) }.Where(x => x != null))), ("Form factor", S(sys.FormFactor)),
            ("Windows", S(sys.OsName)), ("Edition", S(sys.OsEdition)), ("Version / build", string.Join(" · ", new[] { S(sys.OsVersion), S(sys.OsBuild) }.Where(x => x != null))), ("Architecture", S(sys.OsArchitecture)),
            ("Last boot", sys.LastBoot?.ToString("yyyy-MM-dd HH:mm")), ("Uptime", N(sys.UptimeSeconds, Fmt.Duration)), ("Usable RAM (Windows)", sys.UsableMemoryBytes is ulong u ? Fmt.Bytes(u) : null),
        }, 130), "System"));
        foreach (var c in inv.Cpus)
            Add(UI.Card(UI.KeyValues(new (string, string?)[]
            {
                ("Model", S(c.Name)), ("Manufacturer", S(c.Manufacturer)), ("Cores", c.Cores?.ToString()), ("Threads (logical)", c.LogicalProcessors?.ToString()), ("Maximum clock", c.MaxClockMHz is int mc ? Fmt.Mhz(mc) : null),
                ("Socket", S(c.Socket)), ("L2 cache", c.L2CacheKB is int l2 ? Fmt.Bytes(l2 * 1024.0, 0) : null), ("L3 cache", c.L3CacheKB is int l3 ? Fmt.Bytes(l3 * 1024.0, 0) : null),
            }, 130), inv.Cpus.Count > 1 ? "Processor" : "Processor"));
        Add(UI.Card(UI.KeyValues(new (string, string?)[]
        {
            ("Board manufacturer", S(inv.Board.Manufacturer)), ("Board model", S(inv.Board.Model)), ("Board version", S(inv.Board.Version)),
            ("BIOS vendor", S(inv.Bios.Vendor)), ("BIOS version", S(inv.Bios.Version)), ("BIOS date", inv.Bios.ReleaseDate?.ToString("yyyy-MM-dd")), ("SMBIOS", S(inv.Bios.SmbiosVersion)),
        }, 130), "Motherboard and BIOS"));
        root.Children.Add(wrap);

        // Memory
        var mem = inv.Memory; var mb = new StackPanel { Spacing = 8 };
        mb.Children.Add(UI.Label($"{(mem.TotalModuleBytes is ulong t ? Fmt.Bytes(t, 0) : "Total not reported")} installed · {mem.SlotsPopulated} of {(mem.SlotsTotal?.ToString() ?? "?")} slots populated" + (S(mem.Type) != null ? " · " + mem.Type : "") + (mem.MaxCapacityBytes is ulong mx ? $" · board supports up to {Fmt.Bytes(mx, 0)}" : ""), null, 12, UI.Text, wrap: true));
        if (mem.Modules.Count > 0)
            mb.Children.Add(Table(new[] { "SLOT", "MANUFACTURER", "PART NUMBER", "CAPACITY", "CONFIGURED", "RATED", "TYPE" },
                mem.Modules.Select(x => new string?[] { S(x.Slot) ?? S(x.Bank), S(x.Manufacturer) is string mf ? mf + (S(x.ManufacturerNote) != null ? " (" + x.ManufacturerNote + ")" : "") : S(x.ManufacturerNote), S(x.PartNumber), x.CapacityBytes is ulong cb ? Fmt.Bytes(cb, 0) : null, x.ConfiguredSpeedMTs is int cs ? cs + " MT/s" : null, x.RatedSpeedMTs is int rs ? rs + " MT/s" : null, string.Join(" ", new[] { S(x.Type), S(x.FormFactor) }.Where(q => q != null)) }),
                "70,1.2*,1.6*,90,100,100,90"));
        else mb.Children.Add(UI.Label("Windows did not report per-module details.", "muted"));
        mb.Children.Add(UI.Label("Speeds are in MT/s (mega-transfers per second) as reported by SMBIOS: 'configured' is what the modules run at now, 'rated' is the module maximum. Manufacturer and part number are shown only as the module reports them.", "muted", 10.5, wrap: true));
        root.Children.Add(UI.Card(mb, "Memory (RAM)"));

        // GPUs
        var gw = new WrapPanel { Orientation = Orientation.Horizontal };
        if (inv.Gpus.Count == 0) gw.Children.Add(UI.Card(UI.Label("No display adapters were reported.", "muted"), "Graphics"));
        foreach (var g in inv.Gpus)
        {
            var kind = g.IsSoftware ? "Software renderer" : g.Architecture;
            var card = UI.Card(UI.KeyValues(new (string, string?)[]
            {
                ("Type", kind + (S(g.ArchitectureNote) != null ? " — " + g.ArchitectureNote : "")), ("Vendor", S(g.Vendor)),
                ("Dedicated VRAM", g.DedicatedVramBytes is ulong d ? (d == 0 ? "None (uses system memory)" : Fmt.Bytes(d, 1)) : null),
                ("Shared system memory", g.SharedMemoryBytes is ulong sh ? Fmt.Bytes(sh, 1) + " (system RAM, shared, not VRAM)" : null),
                ("Driver", string.Join(" · ", new[] { S(g.DriverVersion), g.DriverDate?.ToString("yyyy-MM-dd") }.Where(x => x != null))),
                ("PCI ID", S(g.PciVendorId) != null ? $"{g.PciVendorId}:{g.PciDeviceId}" : null),
            }, 140), g.Name);
            card.Width = 430; card.Margin = new Thickness(0, 0, 10, 10); gw.Children.Add(card);
        }
        root.Children.Add(gw);
        if (inv.HiddenSoftwareAdapters > 0) root.Children.Add(UI.Label($"{inv.HiddenSoftwareAdapters} software display adapter(s) (such as the Microsoft Basic Render Driver) are not listed.", "muted", 11));

        // Disks
        root.Children.Add(UI.Label("Storage", "h2"));
        foreach (var d in inv.Disks.Where(d => M.Settings.ShowEmptyReaders || d.MediaPresent))
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(UI.KeyValues(new (string, string?)[]
            {
                ("Connection", d.Connection + (d.IsExternalOrRemovable ? " · external / removable" : "")), ("Media type", d.MediaType), ("Manufacturer", S(d.Manufacturer)), ("Capacity", d.MediaPresent ? Fmt.Capacity(d.SizeBytes) : "No media"),
                ("Partition style", S(d.PartitionStyle)), ("Firmware", S(d.Firmware)), ("Spindle speed", d.SpindleRpm is int rpm and > 1 ? rpm + " rpm" : null),
                ("Windows basic status", S(d.WindowsHealthStatus) is string hs ? hs + (S(d.OperationalStatus) != null ? " / " + d.OperationalStatus : "") + " — Windows's basic flag, not a SMART or surface test" : null),
                ("Temperature", d.TemperatureC is int tc ? tc + " °C" : null), ("SSD life used", d.WearPercentUsed is int wp ? wp + "%" : null),
                ("Powered on", d.PowerOnHours is uint ph ? $"{ph:N0} hours (about {ph / 24:N0} days)" : null),
                ("Read / write errors", d.ReadErrorsTotal != null || d.WriteErrorsTotal != null ? $"{d.ReadErrorsTotal ?? 0:N0} / {d.WriteErrorsTotal ?? 0:N0}" : null),
            }.Where(r => r.Item2 != null || !(r.Item1 is "Temperature" or "SSD life used" or "Powered on" or "Read / write errors") || d.ReliabilityNote == null), 140));
            if (d.ReliabilityNote != null) body.Children.Add(UI.Label(d.ReliabilityNote.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "Drive temperature, SSD wear and power-on hours need administrator rights; Windows hides them from standard users. Use “Restart as administrator” in the sidebar to read them." : d.ReliabilityNote, "muted", 11, wrap: true));
            if (d.Volumes.Count > 0)
                body.Children.Add(Table(new[] { "VOLUME", "LABEL", "FILE SYSTEM", "SIZE", "FREE", "FREE %" },
                    d.Volumes.Select(v => new string?[] { v.Letter, S(v.Label), S(v.FileSystem), Fmt.Bytes(v.SizeBytes), Fmt.Bytes(v.FreeBytes), v.SizeBytes == 0 ? null : (v.FreeBytes * 100.0 / v.SizeBytes).ToString("0.#") + "%" }), "70,*,100,100,100,70"));
            else body.Children.Add(UI.Label(d.MediaPresent ? "No lettered volumes on this drive." : "No media present.", "muted", 11));
            var c = UI.Card(body, $"Disk {d.Number} · {(S(d.Model) ?? d.Name)}"); c.Margin = new Thickness(0, 0, 10, 0); root.Children.Add(c);
        }
        if (inv.HiddenEmptyReaders > 0) root.Children.Add(UI.Label($"{inv.HiddenEmptyReaders} empty card-reader slot(s) with no media are hidden.", "muted", 11));
        if (inv.Disks.Count == 0) root.Children.Add(UI.Card(UI.Label("No physical drives were reported.", "muted")));

        // NICs
        var nic = Table(new[] { "ADAPTER", "KIND", "STATUS", "RECEIVE LINK", "SEND LINK" },
            inv.Nics.Select(n => new string?[] { n.Name + (n.IsVirtual ? " (virtual)" : ""), n.Kind, n.Status, n.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ? Fmt.LinkSpeed(n.ReceiveLinkBps) : null, n.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ? Fmt.LinkSpeed(n.TransmitLinkBps) : null }), "2*,100,100,110,110");
        var nb = new StackPanel { Spacing = 6, Children = { nic, UI.Label("Link speed is the negotiated rate of the connection, not measured throughput. Disconnected adapters have no negotiated speed.", "muted", 10.5, wrap: true) } };
        var nc = UI.Card(nb, "Network adapters"); root.Children.Add(nc);

        // Battery
        var bat = inv.Battery;
        root.Children.Add(UI.Card(bat == null ? UI.Label(S(inv.BatteryNote) ?? "No battery reported (typical for a desktop PC).", "muted", 12, wrap: true)
            : UI.KeyValues(new (string, string?)[]
            {
                ("Name", S(bat.Name)), ("Charge", bat.ChargePercent is int cp ? cp + "%" : null), ("Status", S(bat.Status)), ("Chemistry", S(bat.Chemistry)),
                ("Design capacity", bat.DesignCapacityMWh is int dc ? dc + " mWh" : null), ("Full-charge capacity", bat.FullChargeCapacityMWh is int fc ? fc + " mWh" : null),
                ("Wear", bat.WearPercent is double w ? w.ToString("0.#") + "%" : null), ("Cycle count", bat.CycleCount?.ToString()), ("Estimated runtime", bat.EstimatedRuntimeMinutes is int er ? Fmt.Duration(er * 60) : null),
            }, 150), "Battery"));

        if (inv.Unavailable.Count > 0)
        {
            var ub = new StackPanel { Spacing = 3 };
            foreach (var un in inv.Unavailable) ub.Children.Add(UI.Label($"{un.Field}: {un.Reason}", "muted", 11, wrap: true));
            root.Children.Add(UI.Card(ub, "Not available on this computer"));
        }
    }
}
