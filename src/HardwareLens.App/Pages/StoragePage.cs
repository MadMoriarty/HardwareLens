using Avalonia.Platform.Storage;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>What is using disk space. Scans only when asked, only reads, never deletes.</summary>
public sealed class StoragePage : PageBase
{
    readonly StackPanel volumes = new() { Spacing = 8 }, results = new() { Spacing = 4 }, crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock status = UI.Label("", "muted", 12, wrap: true);
    readonly Button scanBtn, cancelBtn; CancellationTokenSource? cts; bool scanning;
    readonly Stack<FolderNode> trail = new(); readonly List<FolderNode> path = new();
    readonly ComboBox where = new() { MinWidth = 260 };
    List<string> roots = new();

    public StoragePage(AppModel m) : base(m)
    {
        scanBtn = UI.IconButton("search", "Scan this location", "Adds up file sizes under the chosen location. Read-only; it can take a minute on a big drive.", true);
        cancelBtn = UI.IconButton("x", "Cancel"); cancelBtn.IsVisible = false;
        scanBtn.Click += async (_, _) => await Scan(); cancelBtn.Click += (_, _) => cts?.Cancel();
        var open = UI.IconButton("folder", "Pick a folder…", "Scan a different folder"); open.Click += async (_, _) => await Pick();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; bar.Children.Add(where); bar.Children.Add(scanBtn); bar.Children.Add(cancelBtn); bar.Children.Add(open);
        var root = new StackPanel { Margin = new Thickness(18, 14, 12, 14), Spacing = 14 };
        root.Children.Add(UI.Label("Storage", "h1"));
        root.Children.Add(UI.Section("Drives", volumes));
        var sc = new StackPanel { Spacing = 8 }; sc.Children.Add(UI.Label("Find out which folders are using the space. Nothing is changed or deleted; you can open a folder in Explorer to decide for yourself.", "muted", 12, wrap: true)); sc.Children.Add(bar); sc.Children.Add(status); sc.Children.Add(crumbs); sc.Children.Add(results);
        root.Children.Add(UI.Section("What is using the space", sc));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public override void Refresh() { }
    public override void OnShown() { BuildVolumes(); }
    public override void OnInventoryChanged() { BuildVolumes(); }

    void BuildVolumes()
    {
        volumes.Children.Clear(); roots.Clear();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network))
            {
                double used = d.TotalSize - d.AvailableFreeSpace, frac = used / Math.Max(1.0, d.TotalSize); roots.Add(d.RootDirectory.FullName);
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,230"), ColumnSpacing = 12 };
                g.Children.Add(UI.Label($"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}", null, 12.5)); var bar = UI.Bar(frac, frac > 0.9 ? UI.Pink : frac > 0.8 ? UI.Warn : UI.Accent, 8); bar.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(bar, 1); g.Children.Add(bar);
                var t = UI.Label($"{Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)} · {d.DriveFormat}", "muted", 11.5); Grid.SetColumn(t, 2); g.Children.Add(t); volumes.Children.Add(g);
            }
        }
        catch (Exception e) { volumes.Children.Add(UI.Label("Could not read drives: " + e.Message, "muted")); }
        var items = roots.Select(r => r).ToList(); var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var opts = new List<string> { user }; opts.AddRange(items.Where(r => !string.Equals(r, user, StringComparison.OrdinalIgnoreCase)));
        int keep = where.SelectedIndex; where.ItemsSource = opts; where.SelectedIndex = keep >= 0 && keep < opts.Count ? keep : 0;
    }

    async Task Pick()
    {
        var owner = UI.OwnerOf(this); if (owner == null) return;
        var f = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Folder to scan", AllowMultiple = false });
        if (f.Count > 0 && f[0].TryGetLocalPath() is { } p) { var l = (where.ItemsSource as List<string>) ?? new(); if (!l.Contains(p)) { l = new List<string>(l) { p }; where.ItemsSource = l; } where.SelectedItem = p; await Scan(); }
    }

    async Task Scan()
    {
        if (scanning || where.SelectedItem is not string root) return; scanning = true; scanBtn.IsEnabled = false; cancelBtn.IsVisible = true; results.Children.Clear(); crumbs.Children.Clear(); path.Clear();
        cts = new CancellationTokenSource(); var ct = cts.Token; status.Text = "Scanning " + root + " …"; var sw = Stopwatch.StartNew();
        string cur = ""; long cnt = 0; var tm = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; tm.Tick += (_, _) => status.Text = $"Scanning… {cnt:N0} files so far · {cur}"; tm.Start();
        try
        {
            var node = await Task.Run(() => FolderScanner.Scan(root, 4, ct, (p, c) => { cur = p.Length > 90 ? "…" + p[^88..] : p; cnt = c; }), ct);
            path.Add(node); Show(node);
            status.Text = $"{Fmt.Bytes(node.Bytes)} in {node.Files:N0} files · scanned in {sw.Elapsed.TotalSeconds:0} s" + (node.Skipped > 0 ? $" · {node.Skipped} folder(s) could not be read (no permission)" : "") + ". Sizes are file sizes; shortcuts to other places are not followed.";
        }
        catch (OperationCanceledException) { status.Text = "Scan cancelled."; }
        catch (Exception e) { status.Text = "Scan failed: " + e.Message; }
        tm.Stop(); scanning = false; scanBtn.IsEnabled = true; cancelBtn.IsVisible = false;
    }

    void Show(FolderNode n)
    {
        results.Children.Clear(); crumbs.Children.Clear();
        for (int i = 0; i < path.Count; i++)
        {
            int idx = i; if (i > 0) crumbs.Children.Add(UI.Label("›", "muted"));
            crumbs.Children.Add(UI.LinkButton(i == 0 ? path[0].Path : path[i].Name, () => { path.RemoveRange(idx + 1, path.Count - idx - 1); Show(path[idx]); }));
        }
        var rows = n.Children.Select(c => (name: c.Name, bytes: c.Bytes, node: (FolderNode?)c)).ToList();
        if (n.OwnFilesBytes > 0) rows.Add(("(files directly in this folder)", n.OwnFilesBytes, null));
        rows = rows.OrderByDescending(r => r.bytes).Take(25).ToList(); long max = Math.Max(1, rows.Count == 0 ? 1 : rows[0].bytes);
        if (rows.Count == 0) results.Children.Add(UI.Label("Nothing to show here.", "muted"));
        foreach (var r in rows)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*,90,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
            var nm = UI.Label(r.name, null, 12.5); nm.TextTrimming = TextTrimming.CharacterEllipsis; g.Children.Add(nm);
            var bar = UI.Bar((double)r.bytes / max, UI.Accent, 6); bar.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(bar, 1); g.Children.Add(bar);
            var sz = UI.Label(Fmt.Bytes(r.bytes), null, 12, UI.Text); sz.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(sz, 2); g.Children.Add(sz);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (r.node is { } child)
            {
                if (child.Children.Count > 0) acts.Children.Add(UI.LinkButton("Open", () => { path.Add(child); Show(child); }, "Look inside this folder"));
                acts.Children.Add(UI.LinkButton("Show in Explorer", () => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + child.Path + "\"") { UseShellExecute = true }); } catch { } }));
            }
            Grid.SetColumn(acts, 3); g.Children.Add(acts); results.Children.Add(g);
        }
    }
}
