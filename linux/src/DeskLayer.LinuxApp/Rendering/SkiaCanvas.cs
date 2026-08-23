// The real ctx → Skia bridge — the Linux twin of the win D2DCanvas and the
// mac CanvasContext. Draws into a persistent per-item bitmap so Canvas2D
// content carries across frames. Mirrors D2DCanvas's public member surface
// exactly: Jint duck-types the bridge, so parity of names IS the contract.
//
// Members are lowercase: Jint binds them to the JS contract case-sensitively.
// Same documented shortcuts as win M1: arcs flattened to 64-segment
// polylines, path consumed on fill/stroke, drawImage requires an
// ImageProvider (bare .js plugins draw nothing).

using DeskLayer.Core;
using SkiaSharp;

namespace DeskLayer.LinuxApp.Rendering;

public sealed class SkiaCanvas : IDisposable
{
    private readonly SKCanvas canvas;
    private readonly double widthPts;
    private readonly double heightPts;
    private readonly float deviceScale;
    private readonly Dictionary<string, SKFont> fonts = new();
    private readonly SKPaint paint = new() { IsAntialias = true };

    public Func<string, object?>? PropertyProvider { get; set; }
    public Func<string, SKImage?>? ImageProvider { get; set; }

    private SKColor fillColor = SKColors.Black;
    private SKColor strokeColor = SKColors.Black;
    private string fontSpec = "13px Noto Sans";

    private readonly List<(List<SKPoint> pts, bool closed)> figures = new();
    private List<SKPoint>? openFigure;
    private readonly Stack<(SKMatrix m, SKColor fill, SKColor stroke, double lw, string font)> stack = new();

    public SkiaCanvas(SKCanvas canvas, double widthPx, double heightPx, double deviceScale = 1)
    {
        this.canvas = canvas;
        this.deviceScale = (float)deviceScale;
        widthPts = widthPx / deviceScale;
        heightPts = heightPx / deviceScale;
    }

    /// Reset per-frame JS-visible state; pixel content persists (Canvas2D).
    public void BeginFrame()
    {
        canvas.RestoreToCount(0);
        canvas.ResetMatrix();
        canvas.Scale(deviceScale);
        stack.Clear();
        figures.Clear();
        openFigure = null;
    }

    // ---- state ----

    private string fillStyleString = "#000000";
    private string strokeStyleString = "#000000";
    public string fillStyle
    {
        get => fillStyleString;
        set { fillStyleString = value; if (Css.TryParse(value, out var c)) fillColor = c; }
    }
    public string strokeStyle
    {
        get => strokeStyleString;
        set { strokeStyleString = value; if (Css.TryParse(value, out var c)) strokeColor = c; }
    }
    public double lineWidth { get; set; } = 1;
    public string lineCap { get; set; } = "butt";
    public string lineJoin { get; set; } = "miter";
    public double globalAlpha { get; set; } = 1;
    public string font { get => fontSpec; set => fontSpec = value; }
    public double width => widthPts;
    public double height => heightPts;

    public void save()
    {
        stack.Push((canvas.TotalMatrix, fillColor, strokeColor, lineWidth, fontSpec));
    }

    public void restore()
    {
        if (stack.Count == 0) return;
        (var m, fillColor, strokeColor, lineWidth, fontSpec) = stack.Pop();
        canvas.SetMatrix(m);
    }

    public void translate(double x, double y) => canvas.Translate((float)x, (float)y);
    public void rotate(double angle) => canvas.RotateRadians((float)angle);
    public void scale(double x, double y) => canvas.Scale((float)x, (float)y);

    // ---- rects ----

    public void clearRect(double x, double y, double w, double h)
    {
        paint.BlendMode = SKBlendMode.Clear;
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
        paint.BlendMode = SKBlendMode.SrcOver;
    }

    public void fillRect(double x, double y, double w, double h)
    {
        ConfigureFill();
        canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
    }

    public void strokeRect(double x, double y, double w, double h)
    {
        ConfigureStroke();
        canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
    }

    // ---- paths ----

    public void beginPath() { figures.Clear(); openFigure = null; }

    public void closePath()
    {
        if (openFigure is { Count: > 1 }) { figures.Add((openFigure, true)); openFigure = null; }
    }

    public void moveTo(double x, double y)
    {
        FinishOpenFigure();
        openFigure = new List<SKPoint> { new((float)x, (float)y) };
    }

    public void lineTo(double x, double y)
    {
        openFigure ??= new List<SKPoint> { new((float)x, (float)y) };
        openFigure.Add(new SKPoint((float)x, (float)y));
    }

    public void arc(double cx, double cy, double r, double start, double end, bool anticlockwise)
    {
        // Same 64-segment flattening as the win bridge — parity over polish.
        const double tau = Math.PI * 2;
        double sweep;
        if (!anticlockwise)
            sweep = end - start >= tau ? tau : (((end - start) % tau) + tau) % tau;
        else
            sweep = end - start <= -tau ? -tau : -((((start - end) % tau) + tau) % tau);

        const int steps = 64;
        for (var i = 0; i <= steps; i++)
        {
            var a = start + sweep * i / steps;
            var p = new SKPoint((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
            if (i == 0)
            {
                if (openFigure != null) openFigure.Add(p); // Canvas2D: line to arc start
                else moveTo(p.X, p.Y);
            }
            else
            {
                openFigure!.Add(p);
            }
        }
    }

    public void fill() => DrawPath(filled: true);
    public void stroke() => DrawPath(filled: false);

    private void FinishOpenFigure()
    {
        if (openFigure is { Count: > 1 }) figures.Add((openFigure, false));
        openFigure = null;
    }

    private void DrawPath(bool filled)
    {
        FinishOpenFigure();
        if (figures.Count == 0) return;
        using var path = new SKPath();
        foreach (var (pts, closed) in figures)
        {
            path.MoveTo(pts[0]);
            for (var i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
            if (filled || closed) path.Close();
        }
        if (filled) ConfigureFill(); else ConfigureStroke();
        canvas.DrawPath(path, paint);
        figures.Clear(); // bridge parity: path consumed
    }

    // ---- text ----

    public void fillText(string text, double x, double y)
    {
        var skFont = FontFor(fontSpec);
        ConfigureFill();
        // Canvas2D fillText's y is the alphabetic baseline — Skia's native
        // origin, no offset needed (the win bridge approximates this from a
        // top-left origin instead).
        canvas.DrawText(text, (float)x, (float)y, skFont, paint);
    }

    public MeasureResult measureText(string text)
    {
        var skFont = FontFor(fontSpec);
        using var measure = new SKPaint(skFont);
        return new MeasureResult(measure.MeasureText(text));
    }

    public void drawImage(string name, double x, double y, double w, double h)
    {
        var image = ImageProvider?.Invoke(name);
        if (image == null) return; // missing asset: draw nothing (parity)
        paint.Style = SKPaintStyle.Fill;
        paint.Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(globalAlpha, 0, 1)));
        canvas.DrawImage(image, SKRect.Create((float)x, (float)y, (float)w, (float)h), paint);
        paint.Color = SKColors.Black;
    }

    public object? getProp(string name) => PropertyProvider?.Invoke(name);

    // ---- internals ----

    private void ConfigureFill()
    {
        paint.Style = SKPaintStyle.Fill;
        paint.Color = WithAlpha(fillColor);
        paint.StrokeWidth = 0;
    }

    private void ConfigureStroke()
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.Color = WithAlpha(strokeColor);
        paint.StrokeWidth = (float)lineWidth;
        paint.StrokeCap = lineCap switch
        {
            "round" => SKStrokeCap.Round,
            "square" => SKStrokeCap.Square,
            _ => SKStrokeCap.Butt,
        };
        paint.StrokeJoin = lineJoin switch
        {
            "round" => SKStrokeJoin.Round,
            "bevel" => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Miter,
        };
    }

    private SKColor WithAlpha(SKColor c) =>
        c.WithAlpha((byte)(c.Alpha * Math.Clamp(globalAlpha, 0, 1)));

    private SKFont FontFor(string spec)
    {
        double size = 13;
        var family = "Noto Sans";
        var bold = false;
        var tokens = new List<string>(spec.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (tokens.Count > 0 && tokens[0] is "bold" or "italic")
        {
            bold = tokens[0] == "bold";
            tokens.RemoveAt(0);
        }
        var sizeToken = tokens.Find(t => t.EndsWith("px") || t.EndsWith("pt"));
        if (sizeToken != null && double.TryParse(sizeToken[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            size = v;
            tokens.Remove(sizeToken);
        }
        if (tokens.Count > 0) family = SharedAssets.FontFamily(string.Join(' ', tokens));

        var key = $"{(bold ? "bold " : "")}{size}px {family}";
        if (!fonts.TryGetValue(key, out var skFont))
        {
            var typeface = SKTypeface.FromFamilyName(family,
                bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
            skFont = new SKFont(typeface, (float)size);
            fonts[key] = skFont;
        }
        return skFont;
    }

    public void Dispose()
    {
        paint.Dispose();
        foreach (var f in fonts.Values) f.Dispose();
    }
}

public sealed class MeasureResult
{
    public double width { get; }
    public MeasureResult(double width) => this.width = width;
}

/// CSS color parsing, same subset as the win bridge (hex 3/4/6/8, rgb[a](),
/// the named handful, transparent).
public static class Css
{
    public static bool TryParse(string s, out SKColor color)
    {
        color = SKColors.Black;
        s = s.Trim().ToLowerInvariant();
        try
        {
            if (s.StartsWith('#'))
            {
                var h = s[1..];
                if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
                if (h.Length == 4) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}{h[3]}{h[3]}";
                if (h.Length == 6) h += "ff";
                if (h.Length != 8) return false;
                color = new SKColor(
                    (byte)Convert.ToInt32(h[..2], 16), (byte)Convert.ToInt32(h[2..4], 16),
                    (byte)Convert.ToInt32(h[4..6], 16), (byte)Convert.ToInt32(h[6..8], 16));
                return true;
            }
            if (s.StartsWith("rgb"))
            {
                var parts = s[(s.IndexOf('(') + 1)..s.IndexOf(')')].Split(',');
                var a = parts.Length > 3
                    ? float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                color = new SKColor(
                    (byte)float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    (byte)float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    (byte)float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                    (byte)(a * 255));
                return true;
            }
            color = s switch
            {
                "white" => SKColors.White,
                "black" => SKColors.Black,
                "red" => new SKColor(255, 0, 0),
                "green" => new SKColor(0, 128, 0),
                "blue" => new SKColor(0, 0, 255),
                "yellow" => new SKColor(255, 255, 0),
                "orange" => new SKColor(255, 165, 0),
                "gray" or "grey" => new SKColor(128, 128, 128),
                "transparent" => SKColors.Transparent,
                _ => throw new FormatException(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
