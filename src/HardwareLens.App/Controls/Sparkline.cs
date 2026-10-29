using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HardwareLens.Core;
namespace HardwareLens.App.Controls;

/// <summary>Tiny trend line for tiles. NaN breaks the line; scale is fixed (e.g. 0-100) or follows the data.</summary>
public sealed class Sparkline : Control
{
    SeriesData data = SeriesData.Empty; Color color = Colors.White; double? fixedMax; DateTimeOffset now; TimeSpan window = TimeSpan.FromMinutes(2);
    public Sparkline() { Height = 30; IsHitTestVisible = false; }
    public void Update(SeriesData d, string hex, double? max, DateTimeOffset nowTime, TimeSpan win) { data = d; color = Color.Parse(hex); fixedMax = max; now = nowTime; window = win; InvalidateVisual(); }
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height; if (w < 10 || data.Count == 0) return;
        double max = fixedMax ?? 0; if (fixedMax == null) { foreach (var v in data.Values) if (!double.IsNaN(v)) max = Math.Max(max, v); max = Math.Max(max * 1.15, 1e-9); }
        long t1 = now.UtcTicks, t0 = t1 - window.Ticks; double span = window.Ticks;
        var pen = new Pen(new SolidColorBrush(color), 1.5, lineJoin: PenLineJoin.Round);
        var geo = new StreamGeometry(); var fill = new StreamGeometry();
        using (var g = geo.Open()) using (var f = fill.Open())
        {
            bool open = false; double lastX = 0;
            for (int i = 0; i < data.Count; i++)
            {
                double v = data.Values[i];
                if (double.IsNaN(v)) { if (open) { g.EndFigure(false); f.LineTo(new Point(lastX, h)); f.EndFigure(true); open = false; } continue; }
                double x = w * (data.Ticks[i] - t0) / span; if (x < 0) continue; double y = h - (h - 2) * Math.Clamp(v / max, 0, 1) - 1;
                if (!open) { g.BeginFigure(new Point(x, y), false); f.BeginFigure(new Point(x, h), true); f.LineTo(new Point(x, y)); open = true; }
                else { g.LineTo(new Point(x, y)); f.LineTo(new Point(x, y)); }
                lastX = x;
            }
            if (open) { g.EndFigure(false); f.LineTo(new Point(lastX, h)); f.EndFigure(true); }
        }
        ctx.DrawGeometry(new SolidColorBrush(color, 0.12), null, fill); ctx.DrawGeometry(null, pen, geo);
    }
}
