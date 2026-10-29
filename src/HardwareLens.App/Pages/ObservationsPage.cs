using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Pages;

/// <summary>Rule-based observations. Every card shows the measurement, the duration and what it does not prove.</summary>
public sealed class ObservationsPage : PageBase
{
    readonly StackPanel list = new() { Spacing = 10 };
    readonly StackPanel root = new() { Margin = new Thickness(16, 14, 8, 14), Spacing = 10 };
    string sig = "";

    public ObservationsPage(AppModel m) : base(m)
    {
        root.Children.Add(UI.Label("Observations", "h1"));
        root.Children.Add(UI.Label("Plain rules applied to measured windows — no AI, no scoring. An observation says what was measured and for how long. “Possible bottleneck” means a resource was saturated for a sustained period; it is not a confirmed diagnosis, and a brief spike is never reported as a problem.", "muted", 11.5, wrap: true));
        root.Children.Add(list);
        var rules = new StackPanel { Spacing = 6 };
        foreach (var r in ObservationRules.Describe())
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,70"), ColumnSpacing = 12 };
            g.Children.Add(UI.Label(r.Name, null, 12, UI.Text, FontWeight.SemiBold, true));
            var c = new StackPanel { Spacing = 1 }; c.Children.Add(UI.Label(r.Condition, null, 11.5, wrap: true)); c.Children.Add(UI.Label(r.Notes, "muted", 10.5, wrap: true)); Grid.SetColumn(c, 1); g.Children.Add(c);
            var w = UI.Label(r.Window, "muted", 11.5); Grid.SetColumn(w, 2); g.Children.Add(w); rules.Children.Add(g);
        }
        rules.Children.Add(UI.Label($"A rule fires only when at least {ObservationRules.MinCoverage:P0} of its window was actually collected and the condition held in at least {ObservationRules.MinFractionAbove:P0} of those samples. Suspended collection or sleep leaves gaps, which are never counted as readings.", "muted", 10.5, wrap: true));
        root.Children.Add(UI.Card(rules, "How observations are decided", "Fixed thresholds, published here exactly as the code applies them."));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    public override bool RefreshWhenPaused => false;

    public override void Refresh()
    {
        var obs = M.Collector.Observations;
        string s = string.Join("§", obs.Select(o => o.Id + o.State + o.Measurement + o.Duration + string.Join(",", o.Details)));
        if (s == sig) return; sig = s;
        list.Children.Clear();
        if (obs.Count == 0) { list.Children.Add(UI.Card(UI.Label("Observations appear after the first few minutes of collection. Keep the app running during the activity you want evidence about.", "muted", 12, wrap: true))); return; }
        var active = obs.Where(o => o.State is ObservationState.Possible or ObservationState.Observed).OrderByDescending(o => o.Level).ToList();
        var other = obs.Except(active).OrderByDescending(o => o.State == ObservationState.Brief).ToList();
        if (active.Count == 0) list.Children.Add(UI.Card(UI.Label("Nothing sustained was observed in the windows collected so far. That is not proof the PC is healthy — it only means none of the rules below matched.", null, 12, UI.Ok, wrap: true)));
        foreach (var o in active) list.Children.Add(Card(o));
        if (other.Count > 0)
        {
            list.Children.Add(UI.Label("Other rules", "caption"));
            foreach (var o in other) list.Children.Add(Card(o));
        }
    }

    static Control Card(Observation o)
    {
        var (fg, dot) = o.State switch
        {
            ObservationState.Possible => (UI.Pink, UI.Pink), ObservationState.Observed => (UI.Warn, UI.Warn),
            ObservationState.Brief => (UI.Info, UI.Info), ObservationState.Collecting => (UI.Muted, UI.Faint), _ => (UI.Muted, UI.Ok)
        };
        var body = new StackPanel { Spacing = 5 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(UI.Label(o.Title, "h2", wrap: true)); var chip = UI.Chip(o.StateLabel, fg, dot); Grid.SetColumn(chip, 1); head.Children.Add(chip); body.Children.Add(head);
        body.Children.Add(UI.Label(o.Category, "caption"));
        if (o.Measurement.Length > 0) body.Children.Add(Row("Measured", o.Measurement));
        if (o.Duration.Length > 0) body.Children.Add(Row("Duration", o.Duration));
        if (o.Explanation.Length > 0) body.Children.Add(Row("What it means", o.Explanation));
        if (o.Caveat.Length > 0) body.Children.Add(Row("Not proven", o.Caveat));
        foreach (var d in o.Details) body.Children.Add(UI.Label("• " + d, null, 11.5, UI.Text, wrap: true));
        var b = new Border { Child = body }; b.Classes.Add("card");
        if (o.State == ObservationState.Possible) b.BorderBrush = UI.Pink;
        return b;
    }

    static Control Row(string k, string v)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), ColumnSpacing = 10 };
        g.Children.Add(UI.Label(k, "muted", 11.5)); var t = UI.Label(v, null, 12, UI.Text, wrap: true); Grid.SetColumn(t, 1); g.Children.Add(t); return g;
    }
}
