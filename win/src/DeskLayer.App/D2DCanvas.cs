// The real ctx → Direct2D bridge — the production twin of the mac
// CanvasContext (CGContext) and of the conformance RecordingCanvas. Draws
// into a persistent per-item bitmap, so Canvas2D content carries across
// frames exactly like the mac's IOSurface copy-forward, without the copy.
//
// Members are lowercase: Jint binds them to the JS contract case-sensitively.
// M1 shortcuts (tracked for M2+): arcs flattened to 64-segment polylines,
// baseline approximated at 0.8 × font size, drawImage is a no-op for bare
// .js plugins (folder assets land with the .deskplugin loader).

using System.Numerics;
using DeskLayer.Core.Model;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace DeskLayer.App;

public sealed class D2DCanvas : IDisposable
{
    private readonly ID2D1DeviceContext dc;
    private readonly ID2D1Factory1 factory;
    private readonly IDWriteFactory dwrite;
    private readonly ID2D1SolidColorBrush brush;
    private readonly double widthPts;
    private readonly double heightPts;
    private readonly Dictionary<string, IDWriteTextFormat> formats = new();

    public Func<string, object?>? PropertyProvider { get; set; }

    private Matrix3x2 current = Matrix3x2.Identity;
    private readonly Stack<(Matrix3x2 m, Color4 fill, Color4 stroke, double lw, string font)> stack = new();
    private Color4 fillColor = new(0f, 0f, 0f, 1f);
    private Color4 strokeColor = new(0f, 0f, 0f, 1f);
    private string fontSpec = "13px Segoe UI";

    private readonly List<(List<Vector2> pts, bool closed)> figures = new();
    private List<Vector2>? openFigure;

    public D2DCanvas(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite, double width, double height)
    {
        this.dc = dc;
        this.factory = factory;
        this.dwrite = dwrite;
        widthPts = width;
        heightPts = height;
        brush = dc.CreateSolidColorBrush(fillColor);
    }

    /// Reset per-frame JS-visible state; pixel content persists (Canvas2D).
    public void BeginFrame()
    {
        current = Matrix3x2.Identity;
        stack.Clear();
        dc.Transform = Matrix3x2.Identity;
        figures.Clear();
        openFigure = null;
    }

    private void Apply() => dc.Transform = current;

    // ---- state ----

    private string fillStyleString = "#000000";
    private string strokeStyleString = "#000000";
    public string fillStyle
    {
        get => fillStyleString;
        set { fillStyleString = value; if (CssColor.TryParse(value, out var c)) fillColor = c; }
    }
    public string strokeStyle
    {
        get => strokeStyleString;
        set { strokeStyleString = value; if (CssColor.TryParse(value, out var c)) strokeColor = c; }
    }
    public double lineWidth { get; set; } = 1;
    public string lineCap { get; set; } = "butt";
    public string lineJoin { get; set; } = "miter";
    public double globalAlpha { get; set; } = 1;
    public string font { get => fontSpec; set => fontSpec = value; }
    public double width => widthPts;
    public double height => heightPts;

    public void save() => stack.Push((current, fillColor, strokeColor, lineWidth, fontSpec));

    public void restore()
    {
        if (stack.Count == 0) return;
        (current, fillColor, strokeColor, lineWidth, fontSpec) = stack.Pop();
        Apply();
    }

    public void translate(double x, double y) { current = Matrix3x2.CreateTranslation((float)x, (float)y) * current; Apply(); }
    public void rotate(double angle) { current = Matrix3x2.CreateRotation((float)angle) * current; Apply(); }
    public void scale(double x, double y) { current = Matrix3x2.CreateScale((float)x, (float)y) * current; Apply(); }

    // ---- rects ----

    public void clearRect(double x, double y, double w, double h)
    {
        dc.PushAxisAlignedClip(new System.Drawing.RectangleF((float)x, (float)y, (float)w, (float)h), AntialiasMode.Aliased);
        dc.Clear(new Color4(0f, 0f, 0f, 0f));
        dc.PopAxisAlignedClip();
    }

    public void fillRect(double x, double y, double w, double h)
    {
        brush.Color = WithAlpha(fillColor);
        dc.FillRectangle(new System.Drawing.RectangleF((float)x, (float)y, (float)w, (float)h), brush);
    }

    public void strokeRect(double x, double y, double w, double h)
    {
        brush.Color = WithAlpha(strokeColor);
        dc.DrawRectangle(new System.Drawing.RectangleF((float)x, (float)y, (float)w, (float)h), brush, (float)lineWidth);
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
        openFigure = new List<Vector2> { new((float)x, (float)y) };
    }

    public void lineTo(double x, double y)
    {
        openFigure ??= new List<Vector2> { new((float)x, (float)y) };
        openFigure.Add(new Vector2((float)x, (float)y));
    }

    public void arc(double cx, double cy, double r, double start, double end, bool anticlockwise)
    {
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
            var p = new Vector2((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
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
        using var geometry = factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            foreach (var (pts, closed) in figures)
            {
                sink.BeginFigure(pts[0], filled ? FigureBegin.Filled : FigureBegin.Hollow);
                for (var i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
                sink.EndFigure(filled || closed ? FigureEnd.Closed : FigureEnd.Open);
            }
            sink.Close();
        }
        brush.Color = WithAlpha(filled ? fillColor : strokeColor);
        if (filled) dc.FillGeometry(geometry, brush);
        else dc.DrawGeometry(geometry, brush, (float)lineWidth);
        figures.Clear(); // mac-bridge parity: path consumed (documented trade-off)
    }

    // ---- text ----

    public void fillText(string text, double x, double y)
    {
        var (format, size) = FormatFor(fontSpec);
        using var layout = dwrite.CreateTextLayout(text, format, 8192, 8192);
        brush.Color = WithAlpha(fillColor);
        dc.DrawTextLayout(new Vector2((float)x, (float)(y - size * 0.8)), layout, brush);
    }

    public MeasureResult measureText(string text)
    {
        var (format, _) = FormatFor(fontSpec);
        using var layout = dwrite.CreateTextLayout(text, format, 8192, 8192);
        return new MeasureResult(layout.Metrics.Width);
    }

    public void drawImage(string name, double x, double y, double w, double h)
    {
        // Folder assets arrive with the .deskplugin loader (M2); bare .js
        // plugins have none. Draw a faint placeholder so misuse is visible.
        brush.Color = new Color4(1, 1, 1, 0.1f);
        dc.FillRectangle(new System.Drawing.RectangleF((float)x, (float)y, (float)w, (float)h), brush);
    }

    public object? getProp(string name) => PropertyProvider?.Invoke(name);

    private (IDWriteTextFormat format, double size) FormatFor(string spec)
    {
        double size = 13;
        var family = "Segoe UI";
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
        if (tokens.Count > 0) family = FontAliases.Resolve(string.Join(' ', tokens));

        var key = $"{(bold ? "bold " : "")}{size}px {family}";
        if (!formats.TryGetValue(key, out var format))
        {
            format = dwrite.CreateTextFormat(family, bold ? FontWeight.Bold : FontWeight.Normal,
                                             Vortice.DirectWrite.FontStyle.Normal, (float)size);
            formats[key] = format;
        }
        return (format, size);
    }

    private Color4 WithAlpha(Color4 c) => new(c.R, c.G, c.B, (float)(c.A * Math.Clamp(globalAlpha, 0, 1)));

    public void Dispose()
    {
        brush.Dispose();
        foreach (var format in formats.Values) format.Dispose();
    }
}

public sealed class MeasureResult
{
    public double width { get; }
    public MeasureResult(double width) => this.width = width;
}

/// Mac PostScript font names → Windows families (shared/runtime alias table
/// grows in M4; these cover the shipped plugins).
public static class FontAliases
{
    public static string Resolve(string family) => family switch
    {
        "Helvetica" or "HelveticaNeue" or "Helvetica Neue" or "SF Pro" or "Avenir" or "Avenir Next" => "Segoe UI",
        "Menlo" or "SF Mono" or "Monaco" => "Cascadia Mono",
        "Times" or "Times New Roman" => "Georgia",
        _ => family,
    };
}

public static class CssColor
{
    public static bool TryParse(string s, out Color4 color)
    {
        color = new Color4(0f, 0f, 0f, 1f);
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
                color = new Color4(
                    Convert.ToInt32(h[..2], 16) / 255f, Convert.ToInt32(h[2..4], 16) / 255f,
                    Convert.ToInt32(h[4..6], 16) / 255f, Convert.ToInt32(h[6..8], 16) / 255f);
                return true;
            }
            if (s.StartsWith("rgb"))
            {
                var parts = s[(s.IndexOf('(') + 1)..s.IndexOf(')')].Split(',');
                var a = parts.Length > 3 ? float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                color = new Color4(
                    float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) / 255f,
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) / 255f,
                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) / 255f, a);
                return true;
            }
            color = s switch
            {
                "white" => new Color4(1f, 1f, 1f, 1f),
                "black" => new Color4(0f, 0f, 0f, 1f),
                "red" => new Color4(1f, 0f, 0f, 1f),
                "green" => new Color4(0, 0.5f, 0, 1),
                "blue" => new Color4(0f, 0f, 1f, 1f),
                "yellow" => new Color4(1f, 1f, 0f, 1f),
                "orange" => new Color4(1, 0.647f, 0, 1),
                "gray" or "grey" => new Color4(0.5f, 0.5f, 0.5f, 1),
                "transparent" => new Color4(0f, 0f, 0f, 0f),
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
