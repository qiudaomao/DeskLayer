// Recursive interpreter: ViewNode tree → WPF elements — the Windows twin of
// the mac NodeView.swift. Never throws on plugin input: unknown node types
// render a visible placeholder, unknown modifiers are logged.
//
// Layout model: SwiftUI stacks map to a Grid — hugging children get Auto
// tracks, greedy children get equal Star tracks (SwiftUI's equal-split
// rule, which plugins like SystemMonitor lean on for bars). A child is
// greedy on an axis when its frame(...) passes null for that dimension, or
// by type (Spacer/Rect/ProgressBar/…), or when a nested stack contains a
// greedy child. Negative stack spacing is honored via margins.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

public static class NodeInterpreter
{
    public delegate void ActionHandler(int actionId, string payloadJson);

    public static FrameworkElement Build(ViewNode node, ActionHandler? onAction, Action<string> log)
    {
        var element = BuildBase(node, onAction, log);
        return ApplyModifiers(element, node, onAction, log);
    }

    // ---- node types ----

    private static FrameworkElement BuildBase(ViewNode node, ActionHandler? onAction, Action<string> log)
    {
        switch (node.Type)
        {
            case "Root" or "ZStack":
            {
                var grid = new Grid();
                foreach (var child in node.Children)
                {
                    var el = Build(child, onAction, log);
                    el.HorizontalAlignment = IsGreedyH(child) ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
                    el.VerticalAlignment = IsGreedyV(child) ? VerticalAlignment.Stretch : VerticalAlignment.Center;
                    grid.Children.Add(el);
                }
                return grid;
            }
            case "VStack":
                return BuildStack(node, onAction, log, horizontal: false);
            case "HStack":
                return BuildStack(node, onAction, log, horizontal: true);
            case "Text":
                return new TextBlock { Text = node.Text ?? "", VerticalAlignment = VerticalAlignment.Center };
            case "Image":
            {
                var name = node.Text ?? "";
                if (Uri.TryCreate(name, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "file")
                {
                    var image = new Image { Stretch = Stretch.Uniform };
                    try
                    {
                        image.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
                    }
                    catch (Exception ex)
                    {
                        log($"image load failed: {ex.Message}");
                    }
                    return image;
                }
                // Symbol name → Fluent glyph (shared symbol-map grows in M4);
                // unmapped names render a neutral dot, matching size via font.
                return new TextBlock
                {
                    Text = DeskLayer.Core.SharedAssets.SymbolGlyph(name, log),
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            case "Spacer":
                return new Border();
            case "Button":
            {
                var button = new Button { Content = node.Text ?? "", Padding = new Thickness(8, 3, 8, 3) };
                if (node.ActionId("onTap") is { } id && onAction != null)
                    button.Click += (_, _) => onAction(id, "{}");
                return button;
            }
            case "Rect":
                return new Border(); // color arrives via .background(...)
            case "Ring":
            {
                var parts = (node.Text ?? "0,0").Split(',');
                double P(int i) => parts.Length > i && double.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                var from = parts.Length > 1 ? Math.Clamp(P(0), 0, 1) : 0;
                var to = Math.Clamp(parts.Length > 1 ? P(1) : P(0), 0, 1);
                return new RingElement
                {
                    From = from,
                    To = Math.Max(to, from),
                    StrokeWidth = node.ModifierDouble("lineWidth") ?? 8,
                    RingBrush = BrushFor(node.ModifierString("ringColor")) ?? Brushes.LimeGreen,
                    TrackBrush = BrushFor(node.ModifierString("trackColor")),
                };
            }
            case "Spinner":
                return new ProgressBar { IsIndeterminate = true, Width = 60, Height = 4 };
            case "ProgressBar":
            {
                double.TryParse(node.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value);
                return new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(value, 0, 1), Height = 6 };
            }
            case "TextField":
            {
                var box = new TextBox { Text = node.ModifierString("value") ?? "", MinWidth = 80 };
                // Placeholder shown while empty (WPF has no built-in hint).
                if (box.Text.Length == 0 && (node.Text ?? "").Length > 0)
                    box.Tag = node.Text;
                if (node.ActionId("onChange") is { } id && onAction != null)
                    box.TextChanged += (_, _) => onAction(id,
                        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = box.Text }));
                return box;
            }
            case "Video":
            {
                if (!Uri.TryCreate(node.Text ?? "", UriKind.Absolute, out var videoUri))
                    return Placeholder("bad video url", log);
                var media = new MediaElement
                {
                    Source = videoUri,
                    LoadedBehavior = MediaState.Play,
                    IsMuted = node.ModifierString("muted") != "false",
                    Stretch = Stretch.Uniform,
                };
                if (node.ModifierString("loop") == "true")
                    media.MediaEnded += (_, _) => { media.Position = TimeSpan.Zero; media.Play(); };
                return media;
            }
            default:
                return Placeholder($"unknown {node.Type}", log);
        }
    }

    private static FrameworkElement BuildStack(ViewNode node, ActionHandler? onAction, Action<string> log, bool horizontal)
    {
        var spacing = node.ModifierDouble("spacing") ?? 8; // SwiftUI-ish default
        var grid = new Grid();
        var index = 0;
        foreach (var child in node.Children)
        {
            var greedy = horizontal ? IsGreedyH(child, true) : IsGreedyV(child, true);
            if (horizontal)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = greedy ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
                });
            else
                grid.RowDefinitions.Add(new RowDefinition
                {
                    Height = greedy ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
                });

            var el = Build(child, onAction, log);
            if (index > 0)
                el.Margin = horizontal
                    ? new Thickness(spacing, 0, 0, 0)
                    : new Thickness(0, spacing, 0, 0);
            if (horizontal)
            {
                Grid.SetColumn(el, index);
                el.HorizontalAlignment = greedy ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
                el.VerticalAlignment = IsGreedyV(child, false) ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            }
            else
            {
                Grid.SetRow(el, index);
                el.VerticalAlignment = greedy ? VerticalAlignment.Stretch : VerticalAlignment.Center;
                el.HorizontalAlignment = IsGreedyH(child, false) ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            }
            grid.Children.Add(el);
            index++;
        }
        return grid;
    }

    // ---- SwiftUI greediness rules ----

    // A Spacer expands only along its parent stack's axis (SwiftUI): a
    // Spacer inside an HStack is horizontally greedy but adds no height —
    // treating it as greedy on both axes made every row containing one
    // vertically star-split, spreading content apart and breaking natural-
    // size measurement for autoSize. `inAxisStack` says whether the node
    // sits in a stack whose axis is the one being asked about (true at the
    // top level and in ZStacks, where a Spacer fills the space it's given).

    private static bool IsGreedyH(ViewNode node, bool inAxisStack = true)
    {
        var frame = node.Modifier("frame");
        if (frame != null && frame.Args.Count > 0)
            return !frame.Args[0].IsNumber; // explicit width → hugging; null → greedy
        return node.Type switch
        {
            "Spacer" => inAxisStack,
            "Rect" or "ProgressBar" or "TextField" or "Video" or "Ring" => true,
            "Root" or "ZStack" => node.Children.Any(c => IsGreedyH(c, true)),
            "HStack" => node.Children.Any(c => IsGreedyH(c, true)),
            "VStack" => node.Children.Any(c => IsGreedyH(c, false)),
            _ => false,
        };
    }

    private static bool IsGreedyV(ViewNode node, bool inAxisStack = true)
    {
        var frame = node.Modifier("frame");
        if (frame != null && frame.Args.Count > 1)
            return !frame.Args[1].IsNumber;
        return node.Type switch
        {
            "Spacer" => inAxisStack,
            "Rect" or "Video" or "Ring" => true,
            "Root" or "ZStack" => node.Children.Any(c => IsGreedyV(c, true)),
            "VStack" => node.Children.Any(c => IsGreedyV(c, true)),
            "HStack" => node.Children.Any(c => IsGreedyV(c, false)),
            _ => false,
        };
    }

    // ---- modifiers (applied in plugin-declared order) ----

    private static FrameworkElement ApplyModifiers(FrameworkElement element, ViewNode node,
                                                   ActionHandler? onAction, Action<string> log)
    {
        foreach (var modifier in node.Modifiers)
        {
            switch (modifier.Name)
            {
                case "textColor" or "foregroundColor":
                    if (BrushFor(modifier.FirstString) is { } brush)
                        TextElement.SetForeground(element, brush); // inherits to text below
                    break;
                case "fontSize" or "font":
                    TextElement.SetFontSize(element, modifier.FirstDouble ?? 13);
                    break;
                case "bold":
                    TextElement.SetFontWeight(element, FontWeights.Bold);
                    break;
                case "padding":
                    element = new Border { Padding = new Thickness(modifier.FirstDouble ?? 16), Child = element };
                    break;
                case "background":
                    element = new Border { Background = BrushFor(modifier.FirstString) ?? Brushes.Transparent, Child = element };
                    break;
                case "cornerRadius":
                {
                    var radius = modifier.FirstDouble ?? 8;
                    if (element is Border border && border.CornerRadius == default)
                    {
                        border.CornerRadius = new CornerRadius(radius);
                    }
                    else
                    {
                        var wrapper = new Border { CornerRadius = new CornerRadius(radius), Child = element };
                        element = wrapper;
                    }
                    // Clip children to the rounded rect (Border only rounds
                    // its own background).
                    var clipped = element;
                    clipped.SizeChanged += (_, e) => clipped.Clip = new RectangleGeometry(
                        new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
                    break;
                }
                case "frame":
                {
                    var width = modifier.Args.Count > 0 ? (modifier.Args[0].IsNumber ? modifier.Args[0].DoubleValue : null) : null;
                    var height = modifier.Args.Count > 1 ? (modifier.Args[1].IsNumber ? modifier.Args[1].DoubleValue : null) : null;
                    var alignment = modifier.Args.Count > 2 ? modifier.Args[2].StringValue : null;
                    element.HorizontalAlignment = alignment switch
                    {
                        "leading" => HorizontalAlignment.Left,
                        "trailing" => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Center,
                    };
                    if (element is TextBlock tb)
                        tb.TextAlignment = alignment switch
                        {
                            "leading" => TextAlignment.Left,
                            "trailing" => TextAlignment.Right,
                            _ => tb.TextAlignment,
                        };
                    var host = new Border { Child = element };
                    if (width is { } w) host.Width = w;
                    if (height is { } h) host.Height = h;
                    element = host;
                    break;
                }
                case "lineLimit":
                    if (FirstTextBlock(element) is { } text)
                    {
                        var limit = (int)(modifier.FirstDouble ?? 1);
                        text.TextTrimming = TextTrimming.CharacterEllipsis;
                        text.TextWrapping = limit > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    }
                    break;
                case "opacity":
                    element.Opacity = modifier.FirstDouble ?? 1;
                    break;
                case "onTapGesture":
                    if (modifier.FirstDouble is { } rawId && onAction != null)
                    {
                        var id = (int)rawId;
                        element.MouseLeftButtonUp += (_, e) =>
                        {
                            var p = e.GetPosition(element);
                            onAction(id, $"{{\"x\":{p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":{p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
                        };
                    }
                    break;
                case "spacing" or "onTap" or "onChange" or "value" or "loop" or "muted"
                    or "lineWidth" or "ringColor" or "trackColor":
                    break; // consumed by node construction
                default:
                    log($"unknown modifier {modifier.Name}");
                    break;
            }
        }
        return element;
    }

    private static TextBlock? FirstTextBlock(FrameworkElement element) => element switch
    {
        TextBlock tb => tb,
        Border { Child: FrameworkElement child } => FirstTextBlock(child),
        _ => null,
    };

    private static Brush? BrushFor(string? css)
    {
        if (css == null || !CssColor.TryParse(css, out var c)) return null;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(c.A * 255), (byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255)));
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement Placeholder(string message, Action<string> log)
    {
        log($"NodeView: {message}");
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Child = new TextBlock { Text = "⚠ " + message, FontSize = 10, Foreground = Brushes.Yellow },
        };
    }
}
