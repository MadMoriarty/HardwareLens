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
namespace HardwareLens.App.Pages;

public sealed class ProcessRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; init; } = "";
    public ProcessSample S { get; private set; } = null!;
    string app = "", name = "", cpu = "", ws = "", pws = "", commit = "", ior = "", iow = "", gpu = "", gpumem = "", threads = "", handles = "", pid = "";
    void Set(ref string f, string v, string n) { if (f == v) return; f = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }
    public string App { get => app; set => Set(ref app, value, nameof(App)); }
    public string Name { get => name; set => Set(ref name, value, nameof(Name)); }
    public string Pid { get => pid; set => Set(ref pid, value, nameof(Pid)); }
    public string Cpu { get => cpu; set => Set(ref cpu, value, nameof(Cpu)); }
    public string Ws { get => ws; set => Set(ref ws, value, nameof(Ws)); }
    public string Pws { get => pws; set => Set(ref pws, value, nameof(Pws)); }
    public string Commit { get => commit; set => Set(ref commit, value, nameof(Commit)); }
    public string IoRead { get => ior; set => Set(ref ior, value, nameof(IoRead)); }
    public string IoWrite { get => iow; set => Set(ref iow, value, nameof(IoWrite)); }
    public string Gpu { get => gpu; set => Set(ref gpu, value, nameof(Gpu)); }
    public string GpuMem { get => gpumem; set => Set(ref gpumem, value, nameof(GpuMem)); }
    public string Threads { get => threads; set => Set(ref threads, value, nameof(Threads)); }
    public string Handles { get => handles; set => Set(ref handles, value, nameof(Handles)); }

    public void Update(ProcessSample p, bool self, string appName)
    {
        S = p; App = appName;
        Name = p.Name + (self ? "  (this app)" : ""); Pid = p.Pid.ToString();
        Cpu = p.CpuPercent is double c ? c.ToString("0.0") : "—";
        Ws = Fmt.Bytes(p.WorkingSetBytes); Pws = Fmt.Bytes(p.PrivateWorkingSetBytes); Commit = Fmt.Bytes(p.CommitBytes);
        IoRead = p.IoReadBps is double r ? Fmt.Throughput(r) : "—"; IoWrite = p.IoWriteBps is double w ? Fmt.Throughput(w) : "—";
        Gpu = p.GpuPercent is double g ? g.ToString("0.#") : "—"; GpuMem = p.GpuDedicatedBytes is double gm ? Fmt.Bytes(gm) : "—";
        Threads = p.Threads.ToString(); Handles = p.Handles.ToString();
    }
}


/// <summary>Raw process list (Task Manager's "Details" tab): one row per process with exact numbers.</summary>
public sealed class DetailsView : UserControl
{
    readonly AppModel M;
    readonly DataGrid grid = new();
    readonly ObservableCollection<ProcessRow> rows = new();
    readonly Dictionary<string, ProcessRow> map = new();
    string sort; bool desc; public string Filter { get; set; } = "";
    public ProcessSample? Selected => (grid.SelectedItem as ProcessRow)?.S;
    public event Action? SelectionChanged;

    public DetailsView(AppModel m)
    {
        M = m; sort = M.Settings.ProcessSort is "cpu" or "ws" or "pws" or "commit" or "ior" or "iow" or "gpu" or "gpumem" or "threads" or "handles" or "pid" or "name" or "app" ? M.Settings.ProcessSort : "cpu"; desc = M.Settings.ProcessSortDescending;
        grid.ItemsSource = rows; grid.IsReadOnly = true; grid.CanUserSortColumns = true; grid.CanUserResizeColumns = true; grid.CanUserReorderColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single; grid.RowHeight = 26;
        void Col(string header, string prop, string key, double width, bool right = true, string? tip = null)
        {
            var hdr = new TextBlock { Text = header }; var c = new DataGridTemplateColumn { Header = hdr, Width = new DataGridLength(width), Tag = key, CanUserSort = true, SortMemberPath = key };
            c.CellTemplate = new FuncDataTemplate<ProcessRow>((_, _) =>
            {
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis };
                t[!TextBlock.TextProperty] = new Binding(prop); return t;
            });
            if (tip != null) ToolTip.SetTip(hdr, tip);
            grid.Columns.Add(c);
        }
        Col("Name", "Name", "name", 200, false, "The process (program file) name"); Col("App", "App", "app", 190, false, "Plain-English name of the program this process belongs to"); Col("PID", "Pid", "pid", 60);
        Col("CPU %", "Cpu", "cpu", 70, true, "Share of ALL logical processors (100% = every thread busy)");
        Col("Working set", "Ws", "ws", 95, true, "Physical RAM the process currently holds, including pages shared with others");
        Col("Private WS", "Pws", "pws", 95, true, "Part of the working set that is not shared with other processes");
        Col("Commit", "Commit", "commit", 90, true, "Private bytes: virtual memory committed by the process (may not be in RAM)");
        Col("I/O read", "IoRead", "ior", 95, true, "General read I/O requested by the process (files, pipes, devices, network), not physical-disk traffic");
        Col("I/O write", "IoWrite", "iow", 95, true, "General write I/O requested by the process, not physical-disk traffic");
        Col("GPU %", "Gpu", "gpu", 65, true, "Busiest GPU engine used by this process");
        Col("GPU mem", "GpuMem", "gpumem", 85, true, "Dedicated GPU memory used by this process");
        Col("Threads", "Threads", "threads", 65); Col("Handles", "Handles", "handles", 70);
        grid.Sorting += (_, e) =>
        {
            e.Handled = true; string key = (string)(e.Column.Tag ?? "cpu");
            if (key == sort) desc = !desc; else { sort = key; desc = key is not ("name" or "pid" or "app"); }
            M.Settings.ProcessSort = sort; M.Settings.ProcessSortDescending = desc; M.Save(); UpdateHeaders(); Refresh();
        };
        grid.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        Content = new Border { Child = grid, BorderBrush = UI.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        UpdateHeaders();
    }

    void UpdateHeaders()
    {
        foreach (var c in grid.Columns)
        {
            if (c.Header is not TextBlock tb) continue; var key = (string?)c.Tag; string h = (tb.Text ?? "").TrimEnd(' ', '▲', '▼');
            tb.Text = key == sort ? h + (desc ? " ▼" : " ▲") : h;
        }
    }

    static double SortKey(ProcessSample p, string k) => k switch
    {
        "pid" => p.Pid, "cpu" => p.CpuPercent ?? -1, "ws" => p.WorkingSetBytes, "pws" => p.PrivateWorkingSetBytes, "commit" => p.CommitBytes,
        "ior" => p.IoReadBps ?? -1, "iow" => p.IoWriteBps ?? -1, "gpu" => p.GpuPercent ?? -1, "gpumem" => p.GpuDedicatedBytes ?? -1, "threads" => p.Threads, "handles" => p.Handles, _ => 0
    };

    public int Shown { get; private set; }

    public void Refresh()
    {
        var s = M.ViewSample; if (s == null) return;
        string q = Filter.Trim();
        var named = s.Processes.Where(p => p.Pid != 0).Select(p => (p, app: AppGrouper.DisplayName(p, M.Info.Get(p)))).ToList();
        IEnumerable<(ProcessSample p, string app)> src = named;
        if (q.Length > 0) src = src.Where(x => x.p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.app.Contains(q, StringComparison.OrdinalIgnoreCase) || x.p.Pid.ToString().StartsWith(q));
        var list = (sort switch
        {
            "name" => desc ? src.OrderByDescending(x => x.p.Name, StringComparer.OrdinalIgnoreCase) : src.OrderBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase),
            "app" => desc ? src.OrderByDescending(x => x.app, StringComparer.OrdinalIgnoreCase) : src.OrderBy(x => x.app, StringComparer.OrdinalIgnoreCase),
            _ => desc ? src.OrderByDescending(x => SortKey(x.p, sort)) : src.OrderBy(x => SortKey(x.p, sort))
        }).ThenBy(x => x.p.Pid).Take(800).ToList();
        Shown = list.Count;
        var want = new List<ProcessRow>(list.Count); var seen = new HashSet<string>();
        foreach (var (p, app) in list)
        {
            if (!map.TryGetValue(p.Key, out var r)) map[p.Key] = r = new ProcessRow { Key = p.Key };
            r.Update(p, p.Pid == M.Collector.SelfPid, app); want.Add(r); seen.Add(p.Key);
        }
        foreach (var k in map.Keys.Where(k => !seen.Contains(k)).ToList()) map.Remove(k);
        for (int i = 0; i < want.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], want[i])) continue;
            int j = rows.IndexOf(want[i]); if (j >= 0) rows.Move(j, i); else rows.Insert(i, want[i]);
        }
        while (rows.Count > want.Count) rows.RemoveAt(rows.Count - 1);
    }
}
