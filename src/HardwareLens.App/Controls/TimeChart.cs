using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Controls;

/// <summary>Lightweight multi-series time chart. NaN breaks the line (gap or unavailable); hover shows exact values.</summary>
public sealed class TimeChart : Control
{
    ChartSpec? spec; DateTimeOffset now = DateTimeOffset.Now; TimeSpan window = TimeSpan.FromMinutes(5); Point? hover;
    public string EmptyText { get; set; } = "No data in this view yet";
    static readonly Typeface Face = new("Segoe UI");
    static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#2A2D35")), LabelBrush = new SolidColorBrush(Color.Parse("#8E929F")), TipBg = new SolidColorBrush(Color.Parse("#EE23262E")), TipBorder = new SolidColorBrush(Color.Parse("#4A4E5A"));
    const double L = 54, R = 8, T = 8, B = 18;

    public TimeChart()
    {
        Height = 132; MinWidth = 180; ClipToBounds = true; Focusable = false;
        PointerMoved += (_, e) => { hover = e.GetPosition(this); InvalidateVisual(); };
        PointerExited += (_, _) => { hover = null; InvalidateVisual(); };
    }

    public void Update(ChartSpec s, DateTimeOffset nowTime, TimeSpan win) { spec = s; now = nowTime; window = win; InvalidateVisual(); }

    static FormattedText Text(string s, double size, IBrush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    string Val(double raw)
    {
        double v = raw * (spec?.Scale ?? 1);
        return (spec?.Unit ?? "") switch
        {
            "%" => v.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            "GiB" => v.ToString("0.00", CultureInfo.InvariantCulture) + " GiB",
            "MiB/s" => v.ToString("0.0", CultureInfo.InvariantCulture) + " MiB/s",
            "Mbit/s" => v.ToString("0.0", CultureInfo.InvariantCulture) + " Mbit/s",
            "ms" => v.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
            var u => v.ToString("0.##", CultureInfo.InvariantCulture) + " " + u
        };
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (spec == null || w < 60 || h < 40) return;
        var plot = new Rect(L, T, Math.Max(20, w - L - R), Math.Max(20, h - T - B));
        long t1 = now.UtcTicks, t0 = t1 - window.Ticks; double span = window.Ticks;

        double max = 0; bool any = false;
        foreach (var s in spec.Series) for (int i = 0; i < s.Data.Count; i++) { double v = s.Data.Values[i]; if (double.IsNaN(v)) continue; any = true; max = Math.Max(max, v * spec.Scale); }
        double ymax = spec.FixedMax ?? ReportBuilder.NiceMax(Math.Max(max * 1.05, 0.0001));

        var gridPen = new Pen(GridBrush, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - plot.Height * i / 4.0; ctx.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            double val = ymax * i / 4.0; string lab = (val >= 100 ? val.ToString("0", CultureInfo.InvariantCulture) : val >= 10 ? val.ToString("0.#", CultureInfo.InvariantCulture) : val.ToString("0.##", CultureInfo.InvariantCulture)) + (i == 4 ? " " + spec.Unit : "");
            var ft = Text(lab, 10, LabelBrush); ctx.DrawText(ft, new Point(plot.Left - 6 - ft.Width, y - ft.Height / 2));
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + plot.Width * i / 4.0; double secAgo = window.TotalSeconds * (4 - i) / 4.0;
            string lab = i == 4 ? "now" : TimeSpan.FromSeconds(Math.Round(secAgo)).ToString(secAgo >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            var ft = Text(lab, 10, LabelBrush); double tx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width); ctx.DrawText(ft, new Point(tx, plot.Bottom + 3));
            if (i > 0 && i < 4) ctx.DrawLine(new Pen(GridBrush, 1) { DashStyle = DashStyle.Dash }, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        if (!any)
        {
            var ft = Text(EmptyText, 11, LabelBrush); ctx.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2)); return;
        }

        using (ctx.PushClip(plot.Inflate(2)))
        {
            for (int si = 0; si < spec.Series.Count; si++)
            {
                var s = spec.Series[si]; var color = Color.Parse(s.Color); var pen = new Pen(new SolidColorBrush(color), 1.6, lineJoin: PenLineJoin.Round);
                var pts = Points(s.Data, plot, t0, span, ymax);
                var geo = new StreamGeometry(); var fill = si == 0 && spec.Series.Count <= 2 ? new StreamGeometry() : null;
                using (var g = geo.Open())
                {
                    var fg = fill?.Open();
                    int run = 0; Point first = default, last = default;
                    void CloseRun()
                    {
                        if (run >= 1) g.EndFigure(false);
                        if (run == 1) ctx.DrawEllipse(pen.Brush, null, first, 1.8, 1.8);
                        if (fg != null && run >= 1) { if (run >= 2) fg.LineTo(new Point(last.X, plot.Bottom)); fg.EndFigure(true); }
                        run = 0;
                    }
                    foreach (var p in pts)
                    {
                        if (p == null) { CloseRun(); continue; }
                        var pt = p.Value;
                        if (run == 0) { g.BeginFigure(pt, false); fg?.BeginFigure(new Point(pt.X, plot.Bottom), true); fg?.LineTo(pt); first = pt; }
                        else { g.LineTo(pt); fg?.LineTo(pt); }
                        last = pt; run++;
                    }
                    CloseRun(); fg?.Dispose();
                }
                if (fill != null) ctx.DrawGeometry(new SolidColorBrush(color, 0.10), null, fill);
                ctx.DrawGeometry(null, pen, geo);
            }
        }

        if (hover is Point hp && hp.X >= plot.Left && hp.X <= plot.Right)
        {
            long ht = t0 + (long)((hp.X - plot.Left) / plot.Width * span);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#8E929F")), 1), new Point(hp.X, plot.Top), new Point(hp.X, plot.Bottom));
            var lines = new List<(string text, IBrush color)>();
            var when = new DateTimeOffset(ht, TimeSpan.Zero).ToLocalTime();
            lines.Add((when.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Brushes.White));
            foreach (var s in spec.Series)
            {
                int idx = Nearest(s.Data, ht); string txt = "unavailable";
                if (idx >= 0 && Math.Abs(s.Data.Ticks[idx] - ht) <= TimeSpan.FromSeconds(8).Ticks && !double.IsNaN(s.Data.Values[idx])) txt = Val(s.Data.Values[idx]);
                lines.Add(($"{s.Name}: {txt}", new SolidColorBrush(Color.Parse(s.Color))));
            }
            var fts = lines.Select(l => Text(l.text, 11, l.color)).ToList();
            double bw = fts.Max(f => f.Width) + 14, bh = fts.Sum(f => f.Height) + 10; double bx = hp.X + 12; if (bx + bw > w) bx = hp.X - 12 - bw; double by = Math.Clamp(hp.Y - bh / 2, 0, Math.Max(0, h - bh));
            ctx.DrawRectangle(TipBg, new Pen(TipBorder, 1), new Rect(bx, by, bw, bh), 4, 4);
            double ty = by + 5; foreach (var f in fts) { ctx.DrawText(f, new Point(bx + 7, ty)); ty += f.Height; }
        }
    }

    static int Nearest(SeriesData d, long t)
    {
        int n = d.Count; if (n == 0) return -1; int lo = 0, hi = n - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (d.Ticks[mid] < t) lo = mid + 1; else hi = mid; }
        if (lo > 0 && Math.Abs(d.Ticks[lo - 1] - t) < Math.Abs(d.Ticks[lo] - t)) lo--; return lo;
    }

    /// <summary>Maps data to plot points, taking the maximum of each pixel bucket so short spikes stay visible. Null = break in the line.</summary>
    List<Point?> Points(SeriesData d, Rect plot, long t0, double span, double ymax)
    {
        var res = new List<Point?>(); int n = d.Count; if (n == 0) return res;
        int step = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, plot.Width * 0.75)));
        for (int i = 0; i < n; i += step)
        {
            int end = Math.Min(n, i + step); double best = double.NaN; long bt = d.Ticks[i];
            for (int j = i; j < end; j++) { double v = d.Values[j]; if (!double.IsNaN(v) && (double.IsNaN(best) || v > best)) { best = v; bt = d.Ticks[j]; } }
            if (double.IsNaN(best)) { if (res.Count > 0 && res[^1] != null) res.Add(null); continue; }
            double x = plot.Left + plot.Width * (bt - t0) / span; if (x < plot.Left - 4) continue;
            double y = plot.Bottom - plot.Height * Math.Min(1, Math.Max(0, best * (spec!.Scale) / ymax));
            res.Add(new Point(x, y));
        }
        return res;
    }
}

/// <summary>Small strip of per-logical-processor load bars.</summary>
public sealed class CoreStrip : Control
{
    double[]? v;
    public CoreStrip() { Height = 30; }
    public void Update(double[]? values) { v = values; InvalidateVisual(); }
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (v == null || v.Length == 0) { var ft = new FormattedText("Per-core load unavailable", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.Parse("#5E6270"))); ctx.DrawText(ft, new Point(0, 6)); return; }
        double gap = v.Length > 32 ? 1 : 2; double bw = Math.Max(2, (w - gap * (v.Length - 1)) / v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            double x = i * (bw + gap); double bh = Math.Max(1.5, h * Math.Clamp(v[i], 0, 100) / 100.0);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#23262D")), null, new Rect(x, 0, bw, h), 1, 1);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#5AA9FF")), null, new Rect(x, h - bh, bw, bh), 1, 1);
        }
    }
}
