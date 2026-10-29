using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
namespace HardwareLens.App;

public static class Dialogs
{
    /// <summary>Modal message with the same chrome language as the main window (pink close dot, charcoal surfaces).</summary>
    public static async Task Show(Window owner, string title, Control body, params (string label, Action action, bool accent)[] actions)
    {
        var win = new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(300, owner.Bounds.Height - 60), CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, SystemDecorations = SystemDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome, ExtendClientAreaTitleBarHeightHint = 36,
            Background = UI.Bg, ShowInTaskbar = false,
        };
        var close = new Button { Width = 13, Height = 13, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(7), Background = UI.Pink, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close"); Avalonia.Automation.AutomationProperties.SetName(close, "Close dialog"); close.Click += (_, _) => win.Close();
        var bar = new Border { Background = UI.B("#1B1C22"), BorderBrush = UI.Line, BorderThickness = new Thickness(0, 0, 0, 1), Height = 36, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16, 0), Children = { close, UI.Label(title.ToUpperInvariant(), null, 11, UI.Text, FontWeight.SemiBold) } } };
        bar.PointerPressed += (_, e) => { if (e.GetCurrentPoint(win).Properties.IsLeftButtonPressed && e.Source is not Button) win.BeginMoveDrag(e); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, action, accent) in actions) { var b = new Button { Content = label }; if (accent) b.Classes.Add("accent"); b.Click += (_, _) => { try { action(); } catch { } win.Close(); }; buttons.Children.Add(b); }
        var ok = new Button { Content = "Close" }; ok.Click += (_, _) => win.Close(); buttons.Children.Add(ok);
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        content.Children.Add(bar);
        var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(18, 14), Child = body }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); content.Children.Add(scroll);
        var foot = new Border { Padding = new Thickness(18, 4, 18, 14), Child = buttons }; Grid.SetRow(foot, 2); content.Children.Add(foot);
        win.Content = new Border { BorderBrush = UI.Line, BorderThickness = new Thickness(1), Child = content };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        await win.ShowDialog(owner);
    }

    public static Task Message(Window owner, string title, string text, params (string label, Action action, bool accent)[] actions)
        => Show(owner, title, UI.Label(text, null, 12, UI.Text, wrap: true), actions);
}
