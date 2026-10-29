using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HardwareLens.Core;
namespace HardwareLens.App;

/// <summary>Small always-on-top readout: total and top app for processor, memory and graphics, plus network.</summary>
public sealed class MiniWindow : Window
{
    readonly AppModel M; readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TextBlock[] val = new TextBlock[4], sub = new TextBlock[4];

    public MiniWindow(AppModel m)
    {
        M = m; Title = "HardwareLens mini"; Width = 300; SizeToContent = SizeToContent.Height; CanResize = false; Topmost = true; ShowInTaskbar = true;
        SystemDecorations = SystemDecorations.BorderOnly; ExtendClientAreaToDecorationsHint = true; ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome; ExtendClientAreaTitleBarHeightHint = 30; Background = UI.Bg;
        var close = new Button { Width = 13, Height = 13, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(7), Background = UI.Pink, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close the mini monitor"); Avalonia.Automation.AutomationProperties.SetName(close, "Close mini monitor"); close.Click += (_, _) => Close();
        var bar = new Border { Background = UI.B("#1B1C22"), Height = 30, BorderBrush = UI.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 0), Children = { close, UI.Label("RIGHT NOW", null, 10.5, UI.Text, FontWeight.SemiBold) } } };
        bar.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e); };
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10, 14, 12) };
        string[] names = { "Processor", "Memory", "Graphics", "Network" };
        for (int i = 0; i < 4; i++)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var l = new StackPanel(); l.Children.Add(UI.Label(names[i], "muted", 10.5)); sub[i] = UI.Label("", null, 11.5, UI.Text); sub[i].TextTrimming = TextTrimming.CharacterEllipsis; l.Children.Add(sub[i]);
            val[i] = UI.Label("—", null, 18, UI.Text, FontWeight.SemiBold); val[i].VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(val[i], 1);
            g.Children.Add(l); g.Children.Add(val[i]); body.Children.Add(g);
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") }; root.Children.Add(bar); Grid.SetRow(body, 1); root.Children.Add(body);
        Content = new Border { BorderBrush = UI.Line, BorderThickness = new Thickness(1), Child = root };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        timer.Tick += (_, _) => Update(); Opened += (_, _) => { timer.Start(); Update(); }; Closed += (_, _) => timer.Stop();
    }

    void Update()
    {
        var s = M.Collector.LatestData; if (s == null) return;
        var gs = M.Groups;
        string Top(Func<AppGroup, double> f, Func<double, bool> ok) { var g = gs.OrderByDescending(f).FirstOrDefault(); return g != null && ok(f(g)) ? "Top: " + g.Name : "Nothing stands out"; }
        val[0].Text = s.CpuPercent is double c ? c.ToString("0") + "%" : "—"; sub[0].Text = Top(g => g.Cpu, v => v >= 1);
        val[1].Text = s.Memory is { } m ? m.UsedPercent.ToString("0") + "%" : "—"; sub[1].Text = s.Memory is { } mm ? $"{Fmt.Bytes(mm.UsedBytes)} of {Fmt.Bytes(mm.TotalBytes)} · " + Top(g => g.MemoryBytes, v => v > 0) : "unavailable";
        var gpu = s.Gpus.Select(g => g.EngineUtilPercent).Where(x => x != null).Select(x => x!.Value).DefaultIfEmpty(double.NaN).Max();
        val[2].Text = double.IsNaN(gpu) ? "—" : gpu.ToString("0") + "%"; sub[2].Text = double.IsNaN(gpu) ? "unavailable" : Top(g => g.Gpu ?? 0, v => v >= 1);
        double rx = 0, tx = 0; bool any = false; foreach (var n in s.Nets) if (n.InTotal) { if (n.RxBps is double a) { rx += a; any = true; } if (n.TxBps is double b) { tx += b; any = true; } }
        val[3].Text = any ? Fmt.Mbit(rx + tx) : "—"; sub[3].Text = any ? $"↓ {Fmt.Mbit(rx)}  ↑ {Fmt.Mbit(tx)}" : "unavailable";
    }
}
