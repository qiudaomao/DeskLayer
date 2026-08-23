// ViewNode tree → Skia, directly — the Linux declarative renderer.
//
// The mac renders trees with SwiftUI and Windows with WPF because those
// toolkits were already in the process; on Linux the wallpaper path stays
// toolkit-free, so this file implements the same layout contract as
// win/src/DeskLayer.App/NodeInterpreter.cs (the reference for every rule
// here) as a two-pass measure/draw over Skia:
//
// - stacks: hugging children get their natural size, greedy children split
//   the remaining space equally (the WPF Star rule); spacing defaults to 8.
// - greediness: same per-type + frame(null) rules, including the Spacer
//   axis subtlety (greedy only along its parent stack's axis).
// - modifiers wrap in plugin-declared order (padding/background/
//   cornerRadius/frame/opacity), styling (textColor/fontSize/bold)
//   inherits downward.
//
// Never throws on plugin input: unknown node types draw a visible
// placeholder, unknown modifiers are logged once.

using DeskLayer.Core;
using DeskLayer.Core.Model;
using SkiaSharp;

namespace DeskLayer.LinuxApp.Rendering;

public static class NodeRenderer
{
    /// Render a tree into a canvas area of `width`×`height` points.
    public static void Render(ViewNode root, SKCanvas canvas, double width, double height, Action<string> log)
    {
        var element = Build(root, log);
        var style = TextStyle.Default;
        element.Measure(new SKSize((float)width, (float)height), style);
        element.Draw(canvas, SKRect.Create(0, 0, (float)width, (float)height), style);
    }

    /// The tree's natural size in points — the WPF Measure pass the win
    /// rasterizer leans on for autoSize axes. Auto axes measure against a
    /// huge (finite — the element math never sees infinity) budget; fixed
    /// axes keep the item's frame so wrapping stays honest.
    public static SKSize MeasureNatural(ViewNode root, double width, double height,
        bool autoWidth, bool autoHeight, Action<string> log)
    {
        const float generous = 100_000f;
        var element = Build(root, log);
        var natural = element.Measure(
            new SKSize(autoWidth ? generous : (float)width, autoHeight ? generous : (float)height),
            TextStyle.Default);
        return new SKSize(
            autoWidth ? natural.Width : (float)width,
            autoHeight ? natural.Height : (float)height);
    }

    // ---- element model ----

    private sealed record TextStyle(SKColor Color, float FontSize, bool Bold)
    {
        public static readonly TextStyle Default = new(SKColors.Black, 13, false);
    }

    private abstract class El
    {
        public ViewNode Node = null!;
        /// Natural (hugging) size given the available space.
        public abstract SKSize Measure(SKSize avail, TextStyle style);
        public abstract void Draw(SKCanvas canvas, SKRect rect, TextStyle style);
    }

    // ---- build ----

    private static El Build(ViewNode node, Action<string> log)
    {
        El element = node.Type switch
        {
            "Root" or "ZStack" => new ZStackEl(node.Children.Select(c => Build(c, log)).ToList()),
            "VStack" => new StackEl(false, node.ModifierDouble("spacing") ?? 8,
                node.Children.Select(c => Build(c, log)).ToList()),
            "HStack" => new StackEl(true, node.ModifierDouble("spacing") ?? 8,
                node.Children.Select(c => Build(c, log)).ToList()),
            "Text" => new TextEl(node.Text ?? ""),
            "Image" => new ImageEl(node.Text ?? "", log),
            "Spacer" => new SpacerEl(),
            "Rect" => new RectEl(),
            "Button" => new ButtonEl(node.Text ?? ""),
            "Ring" => RingEl.From(node),
            "Spinner" => new SpinnerEl(),
            "ProgressBar" => new ProgressEl(ParseDouble(node.Text)),
            "TextField" => new TextFieldEl(node.ModifierString("value") ?? node.Text ?? ""),
            "Video" => new PlaceholderEl("video (wallpaper video lands later)"),
            _ => new PlaceholderEl($"unknown {node.Type}"),
        };
        element.Node = node;

        foreach (var modifier in node.Modifiers)
        {
            switch (modifier.Name)
            {
                case "textColor" or "foregroundColor":
                    if (modifier.FirstString is { } cs && Css.TryParse(cs, out var color))
                        element = Wrap(node, new StyleEl(element, s => s with { Color = color }));
                    break;
                case "fontSize" or "font":
                    element = Wrap(node, new StyleEl(element, s => s with { FontSize = (float)(modifier.FirstDouble ?? 13) }));
                    break;
                case "bold":
                    element = Wrap(node, new StyleEl(element, s => s with { Bold = true }));
                    break;
                case "padding":
                    element = Wrap(node, new PaddingEl(element, (float)(modifier.FirstDouble ?? 16)));
                    break;
                case "background":
                    element = Wrap(node, new BackgroundEl(element,
                        modifier.FirstString is { } bg && Css.TryParse(bg, out var b) ? b : SKColors.Transparent));
                    break;
                case "cornerRadius":
                    element = Wrap(node, new CornerRadiusEl(element, (float)(modifier.FirstDouble ?? 8)));
                    break;
                case "frame":
                {
                    float? w = modifier.Args.Count > 0 && modifier.Args[0].IsNumber ? (float?)modifier.Args[0].DoubleValue : null;
                    float? h = modifier.Args.Count > 1 && modifier.Args[1].IsNumber ? (float?)modifier.Args[1].DoubleValue : null;
                    var alignment = modifier.Args.Count > 2 ? modifier.Args[2].StringValue : null;
                    element = Wrap(node, new FrameEl(element, w, h, alignment,
                        InteriorGreedyH(node), InteriorGreedyV(node)));
                    break;
                }
                case "opacity":
                    element = Wrap(node, new OpacityEl(element, (float)Math.Clamp(modifier.FirstDouble ?? 1, 0, 1)));
                    break;
                case "lineLimit" or "onTapGesture" or "spacing" or "onTap" or "onChange"
                    or "value" or "loop" or "muted" or "lineWidth" or "ringColor" or "trackColor":
                    break; // consumed elsewhere or wallpaper-inert
                default:
                    log($"unknown modifier {modifier.Name}");
                    break;
            }
        }
        return element;
    }

    private static El Wrap(ViewNode node, El wrapper) { wrapper.Node = node; return wrapper; }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ---- greediness (ported verbatim from NodeInterpreter) ----

    private static bool IsGreedyH(ViewNode node, bool inAxisStack = true)
    {
        var frame = node.Modifier("frame");
        if (frame != null && frame.Args.Count > 0) return !frame.Args[0].IsNumber;
        return InteriorGreedyH(node, inAxisStack);
    }

    private static bool IsGreedyV(ViewNode node, bool inAxisStack = true)
    {
        var frame = node.Modifier("frame");
        if (frame != null && frame.Args.Count > 1) return !frame.Args[1].IsNumber;
        return InteriorGreedyV(node, inAxisStack);
    }

    private static bool InteriorGreedyH(ViewNode node, bool inAxisStack = true) => node.Type switch
    {
        "Spacer" => inAxisStack,
        "Rect" or "ProgressBar" or "TextField" or "Video" or "Ring" => true,
        "Root" or "ZStack" => node.Children.Any(c => IsGreedyH(c, true)),
        "HStack" => node.Children.Any(c => IsGreedyH(c, true)),
        "VStack" => node.Children.Any(c => IsGreedyH(c, false)),
        _ => false,
    };

    private static bool InteriorGreedyV(ViewNode node, bool inAxisStack = true) => node.Type switch
    {
        "Spacer" => inAxisStack,
        "Rect" or "Video" or "Ring" => true,
        "Root" or "ZStack" => node.Children.Any(c => IsGreedyV(c, true)),
        "VStack" => node.Children.Any(c => IsGreedyV(c, true)),
        "HStack" => node.Children.Any(c => IsGreedyV(c, false)),
        _ => false,
    };

    // ---- fonts ----

    private static readonly Dictionary<string, SKFont> Fonts = new();

    private static SKFont FontFor(TextStyle style)
    {
        var key = $"{(style.Bold ? "b" : "r")}{style.FontSize}";
        if (!Fonts.TryGetValue(key, out var font))
        {
            var typeface = SKTypeface.FromFamilyName(SharedAssets.FontFamily("Helvetica Neue"),
                style.Bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
            font = new SKFont(typeface, style.FontSize);
            Fonts[key] = font;
        }
        return font;
    }

    private static SKSize MeasureText(string text, TextStyle style)
    {
        var font = FontFor(style);
        using var paint = new SKPaint(font);
        var width = paint.MeasureText(text);
        return new SKSize(width, font.Spacing);
    }

    private static void DrawText(SKCanvas canvas, string text, SKRect rect, TextStyle style, SKTextAlign align = SKTextAlign.Left)
    {
        var font = FontFor(style);
        using var paint = new SKPaint(font) { Color = style.Color, IsAntialias = true, TextAlign = align };
        var x = align switch
        {
            SKTextAlign.Center => rect.MidX,
            SKTextAlign.Right => rect.Right,
            _ => rect.Left,
        };
        // Vertically centered baseline within the assigned rect.
        var baseline = rect.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        canvas.DrawText(text, x, baseline, paint);
    }

    // ---- leaves ----

    private sealed class TextEl(string text) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => MeasureText(text, style);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) =>
            DrawText(canvas, text, rect, style);
    }

    private sealed class ImageEl(string name, Action<string> log) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) =>
            MeasureText(SharedAssets.SymbolGlyph(name, log), style);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) =>
            DrawText(canvas, SharedAssets.SymbolGlyph(name, log), rect, style);
    }

    private sealed class SpacerEl : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => SKSize.Empty;
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) { }
    }

    private sealed class RectEl : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => SKSize.Empty;
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) { } // color arrives via .background
    }

    private sealed class ButtonEl(string label) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            var text = MeasureText(label, style);
            return new SKSize(text.Width + 16, text.Height + 6);
        }
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var bg = new SKPaint { Color = new SKColor(255, 255, 255, 40), IsAntialias = true };
            canvas.DrawRoundRect(rect, 5, 5, bg);
            DrawText(canvas, label, rect, style, SKTextAlign.Center);
        }
    }

    private sealed class RingEl(double from, double to, float strokeWidth, SKColor ring, SKColor? track) : El
    {
        public static RingEl From(ViewNode node)
        {
            var parts = (node.Text ?? "0,0").Split(',');
            double P(int i) => parts.Length > i && double.TryParse(parts[i],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var from = parts.Length > 1 ? Math.Clamp(P(0), 0, 1) : 0;
            var to = Math.Clamp(parts.Length > 1 ? P(1) : P(0), 0, 1);
            var ringColor = node.ModifierString("ringColor") is { } rc && Css.TryParse(rc, out var r)
                ? r : new SKColor(50, 205, 50);
            SKColor? trackColor = node.ModifierString("trackColor") is { } tc && Css.TryParse(tc, out var t)
                ? t : null;
            return new RingEl(from, Math.Max(to, from), (float)(node.ModifierDouble("lineWidth") ?? 8),
                ringColor, trackColor);
        }

        public override SKSize Measure(SKSize avail, TextStyle style) => SKSize.Empty; // greedy
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            var side = Math.Min(rect.Width, rect.Height);
            var inset = strokeWidth / 2;
            var box = SKRect.Create(rect.MidX - side / 2 + inset, rect.MidY - side / 2 + inset,
                                    side - strokeWidth, side - strokeWidth);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
            };
            if (track is { } trackColor)
            {
                paint.Color = trackColor;
                canvas.DrawOval(box, paint);
            }
            paint.Color = ring;
            using var path = new SKPath();
            path.AddArc(box, (float)(-90 + from * 360), (float)((to - from) * 360));
            canvas.DrawPath(path, paint);
        }
    }

    private sealed class SpinnerEl : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => new(60, 4);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var paint = new SKPaint { Color = style.Color.WithAlpha(120), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            var r = Math.Min(rect.Width, rect.Height) / 2 - 2;
            using var path = new SKPath();
            path.AddArc(SKRect.Create(rect.MidX - r, rect.MidY - r, r * 2, r * 2), 0, 270);
            canvas.DrawPath(path, paint);
        }
    }

    private sealed class ProgressEl(double value) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => new(0, 6); // h-greedy
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            var bar = SKRect.Create(rect.Left, rect.MidY - 3, rect.Width, 6);
            using var paint = new SKPaint { IsAntialias = true };
            paint.Color = new SKColor(128, 128, 128, 80);
            canvas.DrawRoundRect(bar, 3, 3, paint);
            paint.Color = style.Color;
            var filled = SKRect.Create(bar.Left, bar.Top, (float)(bar.Width * Math.Clamp(value, 0, 1)), bar.Height);
            if (filled.Width > 0) canvas.DrawRoundRect(filled, 3, 3, paint);
        }
    }

    private sealed class TextFieldEl(string value) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            var text = MeasureText(value.Length > 0 ? value : "M", style);
            return new SKSize(Math.Max(80, text.Width + 12), text.Height + 8);
        }
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var box = new SKPaint { Color = new SKColor(128, 128, 128, 60), IsAntialias = true };
            canvas.DrawRoundRect(rect, 4, 4, box);
            var inner = SKRect.Create(rect.Left + 6, rect.Top, rect.Width - 12, rect.Height);
            DrawText(canvas, value, inner, style);
        }
    }

    private sealed class PlaceholderEl(string message) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) =>
            MeasureText("⚠ " + message, style with { FontSize = 10 });
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var bg = new SKPaint { Color = new SKColor(255, 0, 0, 77), IsAntialias = true };
            canvas.DrawRoundRect(rect, 4, 4, bg);
            DrawText(canvas, "⚠ " + message, rect, style with { FontSize = 10, Color = SKColors.Yellow });
        }
    }

    // ---- containers ----

    private sealed class ZStackEl(List<El> children) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            var size = SKSize.Empty;
            foreach (var child in children)
            {
                var s = child.Measure(avail, style);
                size = new SKSize(Math.Max(size.Width, s.Width), Math.Max(size.Height, s.Height));
            }
            return size;
        }

        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            foreach (var child in children)
            {
                var target = rect;
                if (!IsGreedyH(child.Node) || !IsGreedyV(child.Node))
                {
                    var s = child.Measure(rect.Size, style);
                    var w = IsGreedyH(child.Node) ? rect.Width : Math.Min(s.Width, rect.Width);
                    var h = IsGreedyV(child.Node) ? rect.Height : Math.Min(s.Height, rect.Height);
                    target = SKRect.Create(rect.MidX - w / 2, rect.MidY - h / 2, w, h);
                }
                child.Draw(canvas, target, style);
            }
        }
    }

    private sealed class StackEl(bool horizontal, double spacing, List<El> children) : El
    {
        private bool ChildGreedy(El child) => horizontal
            ? IsGreedyH(child.Node, true)
            : IsGreedyV(child.Node, true);

        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            float main = 0, cross = 0;
            var first = true;
            foreach (var child in children)
            {
                var s = child.Measure(avail, style);
                if (!first) main += (float)spacing;
                first = false;
                if (horizontal) { main += s.Width; cross = Math.Max(cross, s.Height); }
                else { main += s.Height; cross = Math.Max(cross, s.Width); }
            }
            return horizontal ? new SKSize(main, cross) : new SKSize(cross, main);
        }

        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            var mainAvail = horizontal ? rect.Width : rect.Height;
            var totalSpacing = (float)spacing * Math.Max(children.Count - 1, 0);
            var sizes = new SKSize[children.Count];
            float huggedMain = 0;
            var greedyCount = 0;
            for (var i = 0; i < children.Count; i++)
            {
                sizes[i] = children[i].Measure(rect.Size, style);
                if (ChildGreedy(children[i])) greedyCount++;
                else huggedMain += horizontal ? sizes[i].Width : sizes[i].Height;
            }
            var starEach = greedyCount > 0
                ? Math.Max(0, (mainAvail - totalSpacing - huggedMain) / greedyCount)
                : 0;

            var cursor = horizontal ? rect.Left : rect.Top;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var greedy = ChildGreedy(child);
                var mainSize = greedy ? starEach : (horizontal ? sizes[i].Width : sizes[i].Height);
                SKRect cell = horizontal
                    ? SKRect.Create(cursor, rect.Top, mainSize, rect.Height)
                    : SKRect.Create(rect.Left, cursor, rect.Width, mainSize);

                // Cross-axis: stretch when greedy on that axis, else center.
                var target = cell;
                if (horizontal && !IsGreedyV(child.Node, false))
                {
                    var h = Math.Min(sizes[i].Height, cell.Height);
                    target = SKRect.Create(cell.Left, cell.MidY - h / 2, cell.Width, h);
                }
                else if (!horizontal && !IsGreedyH(child.Node, false))
                {
                    var w = Math.Min(sizes[i].Width, cell.Width);
                    target = SKRect.Create(cell.MidX - w / 2, cell.Top, w, cell.Height);
                }
                child.Draw(canvas, target, style);
                cursor += mainSize + (float)spacing;
            }
        }
    }

    // ---- wrappers ----

    private sealed class StyleEl(El inner, Func<TextStyle, TextStyle> apply) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => inner.Measure(avail, apply(style));
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) => inner.Draw(canvas, rect, apply(style));
    }

    private sealed class PaddingEl(El inner, float pad) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            var s = inner.Measure(new SKSize(Math.Max(0, avail.Width - pad * 2), Math.Max(0, avail.Height - pad * 2)), style);
            return new SKSize(s.Width + pad * 2, s.Height + pad * 2);
        }
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style) =>
            inner.Draw(canvas, SKRect.Create(rect.Left + pad, rect.Top + pad,
                Math.Max(0, rect.Width - pad * 2), Math.Max(0, rect.Height - pad * 2)), style);
    }

    private sealed class BackgroundEl(El inner, SKColor color) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => inner.Measure(avail, style);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawRect(rect, paint);
            inner.Draw(canvas, rect, style);
        }
    }

    private sealed class CornerRadiusEl(El inner, float radius) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => inner.Measure(avail, style);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(rect, radius), antialias: true);
            inner.Draw(canvas, rect, style);
            canvas.Restore();
        }
    }

    private sealed class FrameEl(El inner, float? width, float? height, string? alignment,
                                 bool contentGreedyH, bool contentGreedyV) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style)
        {
            var s = inner.Measure(new SKSize(width ?? avail.Width, height ?? avail.Height), style);
            return new SKSize(width ?? s.Width, height ?? s.Height);
        }
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            var box = rect;
            if (width is { } w) box.Right = box.Left + Math.Min(w, rect.Width);
            if (height is { } h) box.Bottom = box.Top + Math.Min(h, rect.Height);
            // The frame's box sits at rect's origin already (parent aligned
            // us); content fills the box when greedy, else aligns inside it.
            var target = box;
            if (!contentGreedyH || !contentGreedyV)
            {
                var s = inner.Measure(box.Size, style);
                var cw = contentGreedyH ? box.Width : Math.Min(s.Width, box.Width);
                var ch = contentGreedyV ? box.Height : Math.Min(s.Height, box.Height);
                var x = alignment switch
                {
                    "leading" => box.Left,
                    "trailing" => box.Right - cw,
                    _ => box.MidX - cw / 2,
                };
                target = SKRect.Create(x, box.MidY - ch / 2, cw, ch);
            }
            inner.Draw(canvas, target, style);
        }
    }

    private sealed class OpacityEl(El inner, float opacity) : El
    {
        public override SKSize Measure(SKSize avail, TextStyle style) => inner.Measure(avail, style);
        public override void Draw(SKCanvas canvas, SKRect rect, TextStyle style)
        {
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(opacity * 255)) };
            canvas.SaveLayer(paint);
            inner.Draw(canvas, rect, style);
            canvas.Restore();
        }
    }
}
