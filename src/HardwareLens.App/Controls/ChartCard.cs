using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Controls;

/// <summary>Card with title, headline reading, source-status chip, chart and a labelled legend (swatch, series name, latest value).</summary>
public sealed class ChartCard : Border
{
    readonly TextBlock headline = UI.Label("", null, 12, UI.Text, wrap: true);
    readonly ContentControl status = new();
    readonly TimeChart chart = new();
    readonly WrapPanel legend = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel body = new() { Spacing = 6 };
    readonly List<TextBlock> valueTexts = new(); string signature = "";
    public StackPanel Extra { get; } = new() { Spacing = 4 };
    public TimeChart Chart => chart;

    public ChartCard(string title, string? tooltip = null, Control? headerRight = null)
    {
        Classes.Add("card");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        var t = UI.Label(title, "h2"); head.Children.Add(t);
        if (headerRight != null) { Grid.SetColumn(headerRight, 1); head.Children.Add(headerRight); }
        Grid.SetColumn(status, 2); head.Children.Add(status);
        if (tooltip != null) ToolTip.SetTip(t, tooltip);
        body.Children.Add(head); body.Children.Add(headline); body.Children.Add(chart); body.Children.Add(legend); body.Children.Add(Extra);
        Child = body;
    }

    public void Update(ChartSpec spec, DateTimeOffset now, TimeSpan window, string headlineText, string state, string? note, string? emptyText = null)
    {
        headline.Text = headlineText; if (emptyText != null) chart.EmptyText = emptyText;
        chart.Update(spec, now, window);
        status.Content = state switch
        {
            "live" => UI.Chip("live", UI.Muted, UI.Ok),
            "stale" => UI.Chip("stale", UI.Warn, UI.Warn),
            _ => UI.Chip("unavailable", UI.Pink, UI.Pink)
        };
        ToolTip.SetTip(status, note ?? (state == "live" ? "Receiving fresh readings" : state));
        string sig = string.Join("|", spec.Series.Select(s => s.Name + s.Color));
        if (sig != signature)
        {
            signature = sig; legend.Children.Clear(); valueTexts.Clear();
            foreach (var s in spec.Series)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 14, 0) };
                sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = UI.B(s.Color), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(UI.Label(s.Name, "muted", 11));
                var vt = UI.Label("", null, 11, UI.Text, FontWeight.SemiBold); sp.Children.Add(vt); valueTexts.Add(vt); legend.Children.Add(sp);
            }
        }
        for (int i = 0; i < spec.Series.Count && i < valueTexts.Count; i++)
        {
            var d = spec.Series[i].Data; double last = double.NaN;
            for (int k = d.Count - 1; k >= 0; k--) if (!double.IsNaN(d.Values[k])) { last = d.Values[k]; break; }
            valueTexts[i].Text = double.IsNaN(last) ? "unavailable" : Format(spec, last);
        }
    }

    public static string Format(ChartSpec spec, double raw)
    {
        double v = raw * spec.Scale;
        return spec.Unit switch { "%" => v.ToString("0.#") + "%", "GiB" => v.ToString("0.00") + " GiB", "MiB/s" => v.ToString("0.0") + " MiB/s", "Mbit/s" => v.ToString("0.0") + " Mbit/s", "ms" => v.ToString("0.0") + " ms", _ => v.ToString("0.##") + " " + spec.Unit };
    }
}
