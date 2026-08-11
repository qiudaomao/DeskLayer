// A color well that opens a picker — the Windows stand-in for the mac
// ColorPicker. WPF ships no picker, and the system (WinForms) one has no
// alpha channel, which plugin colors lean on constantly ("#FFFFFF99" for
// secondary text, "#141414F2" for a card). So: saturation/value square,
// hue and alpha sliders, and a hex field, all editing one #RRGGBBAA value.
//
// Edits apply live, like the mac inspector: the desktop updates as the
// handle moves rather than on a Done click.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace DeskLayer.App;

public sealed class ColorField : Border
{
    private readonly Action<string?> onChange;
    private readonly bool allowNone;
    private readonly Border swatch;
    private readonly TextBlock label;

    private Color current = Colors.White;
    private bool isNone;

    /// `initial` accepts anything CssColor parses (hex, rgb(), a name);
    /// every edit is emitted as #RRGGBBAA. With `allowNone`, the picker
    /// offers a None button and emits null for it.
    public ColorField(string? initial, bool allowNone, Action<string?> onChange)
    {
        this.onChange = onChange;
        this.allowNone = allowNone;

        isNone = string.IsNullOrWhiteSpace(initial);
        if (!isNone) current = Parse(initial!) ?? Colors.White;

        swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = Checkerboard,
            Child = new Border { CornerRadius = new CornerRadius(4) },
        };
        label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(label);

        Child = row;
        Padding = new Thickness(6, 5, 8, 5);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        HorizontalAlignment = HorizontalAlignment.Left;
        Loaded += (_, _) => ApplyTheme();
        MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenPicker(); };
        Refresh();
    }

    private void ApplyTheme()
    {
        Background = (Brush)FindResource("FieldBg");
        BorderBrush = (Brush)FindResource("CardBorder");
        swatch.BorderBrush = (Brush)FindResource("CardBorder");
        label.Foreground = (Brush)FindResource("TextPrimary");
    }

    private void Refresh()
    {
        ((Border)swatch.Child).Background = isNone ? Brushes.Transparent : new SolidColorBrush(current);
        label.Text = isNone ? "None" : Format(current);
    }

    private void Commit(Color color)
    {
        current = color;
        isNone = false;
        Refresh();
        onChange(Format(color));
    }

    private void CommitNone()
    {
        isNone = true;
        Refresh();
        onChange(null);
    }

    // ---- picker popup ----

    /// Returns the popup so a scripted capture can render it — a Popup is
    /// its own window, so it never appears in a dump of the Manager.
    internal Popup OpenPicker()
    {
        var (hue, sat, val) = ToHsv(current);
        var alpha = isNone ? (byte)255 : current.A;
        var updating = false;

        var panel = new StackPanel { Margin = new Thickness(12) };
        var svArea = new Grid { Width = 200, Height = 130 };
        var hueBar = new Border { Width = 16, Height = 130, CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        var alphaBar = new Border { Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        var hex = new TextBox { Width = 110, FontFamily = new FontFamily("Cascadia Mono, Consolas") };

        // Saturation (x) × value (y), tinted by the current hue.
        var svFill = new Border { CornerRadius = new CornerRadius(4), Cursor = Cursors.Cross };
        var svShade = new Border
        {
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90),
        };
        // Two rings: white alone vanishes on a pale swatch, black alone on a
        // dark one.
        var markerHalo = new System.Windows.Shapes.Ellipse
        {
            Width = 14, Height = 14, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), StrokeThickness = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = 12, Height = 12, IsHitTestVisible = false,
            Stroke = Brushes.White, StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        svArea.Children.Add(svFill);
        svArea.Children.Add(svShade);
        svArea.Children.Add(markerHalo);
        svArea.Children.Add(marker);

        var hueMarker = new Border
        {
            Height = 3, IsHitTestVisible = false, Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0),
        };
        var hueHost = new Grid { Width = 16, Height = 130, Margin = new Thickness(10, 0, 0, 0) };
        hueHost.Children.Add(hueBar);
        hueHost.Children.Add(hueMarker);

        // `commit` is false when the popup merely opens: showing a picker is
        // not an edit, and committing there would turn a None background into
        // white just because the user looked at it.
        void Render(bool commit, bool pushHex = true)
        {
            updating = true;
            var color = FromHsv(hue, sat, val, alpha);
            svFill.Background = new LinearGradientBrush(Colors.White, FromHsv(hue, 1, 1, 255), 0);
            var markerX = sat * svArea.Width;
            var markerY = (1 - val) * svArea.Height;
            marker.Margin = new Thickness(markerX - 6, markerY - 6, 0, 0);
            markerHalo.Margin = new Thickness(markerX - 7, markerY - 7, 0, 0);
            hueMarker.Margin = new Thickness(0, hue / 360.0 * hueHost.Height - 1, 0, 0);
            alphaBar.Background = new LinearGradientBrush(
                Color.FromArgb(0, color.R, color.G, color.B),
                Color.FromArgb(255, color.R, color.G, color.B), 0);
            if (pushHex) hex.Text = Format(color);
            updating = false;
            if (commit) Commit(color);
        }

        void PickSv(Point p)
        {
            sat = Math.Clamp(p.X / svArea.Width, 0, 1);
            val = Math.Clamp(1 - p.Y / svArea.Height, 0, 1);
            Render(commit: true);
        }
        svFill.MouseLeftButtonDown += (_, e) => { svFill.CaptureMouse(); PickSv(e.GetPosition(svArea)); };
        svFill.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickSv(e.GetPosition(svArea)); };
        svFill.MouseLeftButtonUp += (_, _) => svFill.ReleaseMouseCapture();

        void PickHue(Point p) { hue = Math.Clamp(p.Y / hueHost.Height, 0, 1) * 360; Render(commit: true); }
        hueBar.MouseLeftButtonDown += (_, e) => { hueBar.CaptureMouse(); PickHue(e.GetPosition(hueHost)); };
        hueBar.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(hueHost)); };
        hueBar.MouseLeftButtonUp += (_, _) => hueBar.ReleaseMouseCapture();

        void PickAlpha(Point p) { alpha = (byte)Math.Round(Math.Clamp(p.X / Math.Max(alphaBar.ActualWidth, 1), 0, 1) * 255); Render(commit: true); }
        alphaBar.MouseLeftButtonDown += (_, e) => { alphaBar.CaptureMouse(); PickAlpha(e.GetPosition(alphaBar)); };
        alphaBar.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickAlpha(e.GetPosition(alphaBar)); };
        alphaBar.MouseLeftButtonUp += (_, _) => alphaBar.ReleaseMouseCapture();

        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            if (Parse(hex.Text) is not { } parsed) return;
            (hue, sat, val) = ToHsv(parsed);
            alpha = parsed.A;
            Render(commit: true, pushHex: false);
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(svArea);
        top.Children.Add(hueHost);
        panel.Children.Add(top);

        // The alpha track sits on a checkerboard so "half transparent" reads
        // as transparency rather than as a lighter color.
        alphaBar.Margin = new Thickness(0);
        panel.Children.Add(new Border
        {
            Background = Checkerboard,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 10, 0, 0),
            Child = alphaBar,
        });

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(hex);
        if (allowNone)
        {
            var none = new Button { Content = "None", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            none.Click += (_, _) => CommitNone();
            bottom.Children.Add(none);
        }
        panel.Children.Add(bottom);

        var card = new Border
        {
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
        hueBar.BorderBrush = (Brush)FindResource("CardBorder");
        alphaBar.BorderBrush = (Brush)FindResource("CardBorder");
        hueBar.Background = HueGradient;

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = card,
        };
        popup.Opened += (_, _) => Render(commit: false);
        popup.IsOpen = true;
        return popup;
    }

    // ---- color helpers ----

    /// #RRGGBBAA — the form plugins are written in, and the only form we emit.
    public static string Format(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    /// Anything the renderer accepts (hex, rgb(), a name), so an existing
    /// value written by hand still seeds the picker.
    public static Color? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!CssColor.TryParse(text, out var parsed)) return null;
        return Color.FromArgb(
            (byte)Math.Round(Math.Clamp(parsed.A, 0f, 1f) * 255),
            (byte)Math.Round(Math.Clamp(parsed.R, 0f, 1f) * 255),
            (byte)Math.Round(Math.Clamp(parsed.G, 0f, 1f) * 255),
            (byte)Math.Round(Math.Clamp(parsed.B, 0f, 1f) * 255));
    }

    private static Color FromHsv(double hue, double sat, double val, byte alpha)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = val * sat;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = val - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(alpha,
            (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Sat, double Val) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * ((g - b) / delta % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static readonly LinearGradientBrush HueGradient = BuildHueGradient();

    private static LinearGradientBrush BuildHueGradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (var i = 0; i <= 6; i++)
            brush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6.0));
        brush.Freeze();
        return brush;
    }

    /// Alpha is only legible against a checkerboard.
    private static readonly DrawingBrush Checkerboard = BuildCheckerboard();

    private static DrawingBrush BuildCheckerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        var gray = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}
