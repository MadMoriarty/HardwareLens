using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
namespace HardwareLens.App;

/// <summary>Shared colours, text helpers and small control factories so every page looks consistent.</summary>
public static class UI
{
    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public static readonly IBrush Bg = B("#15171C"), Panel = B("#1B1D23"), Panel2 = B("#20232A"), Line = B("#2C2F38"), Text = B("#E1E3EA"), Muted = B("#8E929F"), Faint = B("#5E6270");
    public static readonly IBrush Accent = B("#FF6B57"), Pink = B("#ED829F"), Orange = B("#DCA16A"), Ok = B("#3FD0A0"), Warn = B("#DCA16A"), Info = B("#5AA9FF");

    public static TextBlock Label(string text, string? cls = null, double? size = null, IBrush? fg = null, FontWeight? weight = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (cls != null) foreach (var c in cls.Split(' ')) t.Classes.Add(c);
        if (size != null) t.FontSize = size.Value; if (fg != null) t.Foreground = fg; if (weight != null) t.FontWeight = weight.Value;
        return t;
    }

    public static Border Card(Control content, string? title = null, string? subtitle = null, Control? right = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        if (title != null)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = new StackPanel { Spacing = 1 }; left.Children.Add(Label(title, "h2"));
            if (subtitle != null) left.Children.Add(Label(subtitle, "muted", 11, wrap: true));
            head.Children.Add(left); if (right != null) { Grid.SetColumn(right, 1); head.Children.Add(right); }
            stack.Children.Add(head);
        }
        stack.Children.Add(content);
        var b = new Border { Child = stack }; b.Classes.Add("card"); return b;
    }

    public static Border Chip(string text, IBrush? fg = null, IBrush? dot = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (dot != null) sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = fg ?? Text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = sp, VerticalAlignment = VerticalAlignment.Center }; b.Classes.Add("chip"); return b;
    }

    /// <summary>Key/value list. Missing values are shown as a muted "Not reported by Windows" rather than hidden.</summary>
    public static Grid KeyValues(IEnumerable<(string key, string? value)> rows, double keyWidth = 150)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{keyWidth},*"), RowSpacing = 3, ColumnSpacing = 12 };
        int r = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kt = Label(k, "muted", 12, wrap: true); Grid.SetRow(kt, r); g.Children.Add(kt);
            bool missing = string.IsNullOrWhiteSpace(v);
            var vt = Label(missing ? "Not reported by Windows" : v!, null, 12, missing ? Faint : Text, wrap: true); if (missing) vt.FontStyle = FontStyle.Italic;
            Grid.SetRow(vt, r); Grid.SetColumn(vt, 1); g.Children.Add(vt); r++;
        }
        return g;
    }

    /// <summary>Quiet section: small caption, then content, with no box around it.</summary>
    public static Control Section(string title, Control body, Control? right = null, string? hint = null)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        var t = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; t.Children.Add(Label(title.ToUpperInvariant(), "caption"));
        if (hint != null) { var q = Label("ⓘ", "muted", 11); ToolTip.SetTip(q, hint); t.Children.Add(q); }
        head.Children.Add(t); if (right != null) { Grid.SetColumn(right, 1); head.Children.Add(right); }
        var sp = new StackPanel { Spacing = 0 }; sp.Children.Add(head); sp.Children.Add(body); return sp;
    }

    /// <summary>Thin horizontal proportion bar (fraction 0..1).</summary>
    public static Control Bar(double fraction, IBrush? fill = null, double height = 4)
    {
        fraction = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0.0, 1.0); if (fraction > 0 && fraction < 0.02) fraction = 0.02;
        var g = new Grid { Height = height, ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(Math.Max(fraction, 1e-4), GridUnitType.Star), new ColumnDefinition(Math.Max(1 - fraction, 1e-4), GridUnitType.Star) } };
        var track = new Border { Background = B("#262932"), CornerRadius = new CornerRadius(height / 2) }; Grid.SetColumnSpan(track, 2); g.Children.Add(track);
        g.Children.Add(new Border { Background = fill ?? Accent, CornerRadius = new CornerRadius(height / 2) }); return g;
    }

    public static Button LinkButton(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Content = text, Foreground = Accent }; b.Classes.Add("ghost"); b.Padding = new Thickness(6, 2); b.MinHeight = 22; b.Click += (_, _) => onClick(); if (tip != null) ToolTip.SetTip(b, tip); return b;
    }

    /// <summary>Row of mutually exclusive options.</summary>
    public static Control Segmented(string[] options, int selected, Action<int> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }; var btns = new List<Button>();
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i; var b = new Button { Content = options[i] }; b.Classes.Add("seg"); b.Classes.Set("selected", i == selected);
            b.Click += (_, _) => { for (int k = 0; k < btns.Count; k++) btns[k].Classes.Set("selected", k == idx); onChange(idx); };
            btns.Add(b); row.Children.Add(b);
        }
        return new Border { Child = row, Background = B("#1B1D23"), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
    }

    public static Window? OwnerOf(Control c) => TopLevel.GetTopLevel(c) as Window;

    public static Control Icon(string key, double size = 16, IBrush? stroke = null)
        => new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(Icons.Get(key)), Stroke = stroke ?? Muted, StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };

    public static Button IconButton(string icon, string text, string? tooltip = null, bool accent = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }; if (accent) tb.Foreground = B("#1A0D0A");
        sp.Children.Add(Icon(icon, 14, accent ? B("#1A0D0A") : Text)); sp.Children.Add(tb);
        var b = new Button { Content = sp }; if (accent) b.Classes.Add("accent");
        if (tooltip != null) ToolTip.SetTip(b, tooltip); Avalonia.Automation.AutomationProperties.SetName(b, text); return b;
    }
}

public static class Icons
{
    // 24x24 outline icons (Feather-style geometry, drawn as strokes with round caps)
    static readonly Dictionary<string, string> D = new()
    {
        ["hardware"] = "M6,6 L18,6 L18,18 L6,18 Z M9.5,9.5 L14.5,9.5 L14.5,14.5 L9.5,14.5 Z M9,2 L9,6 M15,2 L15,6 M9,18 L9,22 M15,18 L15,22 M2,9 L6,9 M2,15 L6,15 M18,9 L22,9 M18,15 L22,15",
        ["live"] = "M22,12 L18,12 L15,21 L9,3 L6,12 L2,12",
        ["processes"] = "M8,6 L21,6 M8,12 L21,12 M8,18 L21,18 M3,6 L3.01,6 M3,12 L3.01,12 M3,18 L3.01,18",
        ["observations"] = "M1,12 C3,7.5 7,5 12,5 C17,5 21,7.5 23,12 C21,16.5 17,19 12,19 C7,19 3,16.5 1,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 Z",
        ["about"] = "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M12,16 L12,12 M12,8 L12.01,8",
        ["export"] = "M21,15 L21,19 A2,2 0 0 1 19,21 L5,21 A2,2 0 0 1 3,19 L3,15 M7,10 L12,15 L17,10 M12,15 L12,3",
        ["pause"] = "M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z",
        ["play"] = "M6,4 L20,12 L6,20 Z",
        ["stop"] = "M6,6 L18,6 L18,18 L6,18 Z",
        ["record"] = "M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 Z",
        ["refresh"] = "M21,4 L21,10 L15,10 M3,20 L3,14 L9,14 M5.6,9 A8,8 0 0 1 19.5,7.5 L21,10 M3,14 L4.5,16.5 A8,8 0 0 0 18.4,15",
        ["folder"] = "M3,6 A2,2 0 0 1 5,4 L9,4 L11,7 L19,7 A2,2 0 0 1 21,9 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 Z",
        ["search"] = "M19,11 A8,8 0 1 1 3,11 A8,8 0 1 1 19,11 Z M21,21 L16.65,16.65",
        ["keyboard"] = "M3,6 L21,6 A1,1 0 0 1 22,7 L22,17 A1,1 0 0 1 21,18 L3,18 A1,1 0 0 1 2,17 L2,7 A1,1 0 0 1 3,6 Z M6,10 L6.01,10 M10,10 L10.01,10 M14,10 L14.01,10 M18,10 L18.01,10 M7,14 L17,14",
        ["shield"] = "M12,22 C12,22 20,18 20,12 L20,5 L12,2 L4,5 L4,12 C4,18 12,22 12,22 Z",
        ["overview"] = "M3,3 L10,3 L10,12 L3,12 Z M14,3 L21,3 L21,8 L14,8 Z M14,12 L21,12 L21,21 L14,21 Z M3,16 L10,16 L10,21 L3,21 Z",
        ["apps"] = "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z",
        ["startup"] = "M18.4,6.6 A9,9 0 1 1 5.6,6.6 M12,2 L12,12",
        ["services"] = "M4,21 L4,14 M4,10 L4,3 M12,21 L12,12 M12,8 L12,3 M20,21 L20,16 M20,12 L20,3 M1,14 L7,14 M9,8 L15,8 M17,16 L23,16",
        ["network"] = "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M2,12 L22,12 M12,2 C15,5 16,8.5 16,12 C16,15.5 15,19 12,22 C9,19 8,15.5 8,12 C8,8.5 9,5 12,2 Z",
        ["devices"] = "M9,2 L9,8 M15,2 L15,8 M6,8 L18,8 L18,12 A6,6 0 0 1 6,12 Z M12,18 L12,22",
        ["storage"] = "M22,12 L2,12 M5.45,5.11 L2,12 L2,18 A2,2 0 0 0 4,20 L20,20 A2,2 0 0 0 22,18 L22,12 L18.55,5.11 A2,2 0 0 0 16.76,4 L7.24,4 A2,2 0 0 0 5.45,5.11 Z M6,16 L6.01,16 M10,16 L10.01,16",
        ["compare"] = "M21,18 A3,3 0 1 1 15,18 A3,3 0 1 1 21,18 Z M9,6 A3,3 0 1 1 3,6 A3,3 0 1 1 9,6 Z M13,6 L18,6 A2,2 0 0 1 20,8 L20,15 M6,9 L6,21",
        ["mini"] = "M3,5 L21,5 L21,19 L3,19 Z M12,12 L19,12 L19,17 L12,17 Z",
        ["x"] = "M18,6 L6,18 M6,6 L18,18",
        ["file"] = "M14,2 L6,2 A2,2 0 0 0 4,4 L4,20 A2,2 0 0 0 6,22 L18,22 A2,2 0 0 0 20,20 L20,8 Z M14,2 L14,8 L20,8",
    };
    public static string Get(string key) => D.TryGetValue(key, out var v) ? v : D["about"];
}
