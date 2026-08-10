// DeskLayer M0 spike — D2D swap-chain render loop driven by ClearScript V8.
//
// Proves the remaining M0 unknowns compose:
//   - flip-model DXGI swap chain on a wallpaper-attached HWND, presented at
//     vsync (the Windows analogue of IOSurface → CALayer.contents)
//   - ClearScript V8 evaluating REAL plugin sources (one conformance fixture
//     verbatim + one animated spike plugin), each in its own engine
//   - a minimal ctx → Direct2D bridge covering the plugin canvas subset
//     (state, transforms, rects, flattened-arc paths, text)
// Logs fps + process CPU% to render-log.txt every 5s; exits after ~40s.
//
// Spike shortcuts, resolved in M1: arcs flattened to polylines instead of
// ArcSegments, text baseline approximated as 0.8 × size, no drawImage/
// measureText-driven layout, fixed window size, DPI 96 assumed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.ClearScript.V8;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

internal static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public static (IntPtr target, string strategy) FindWallpaperHost()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return (IntPtr.Zero, "none: no Progman");
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);

        IntPtr sibling = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                sibling = FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        if (sibling != IntPtr.Zero) return (sibling, "1:sibling-WorkerW");

        var child = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero) return (child, "2:child-WorkerW");
        return (progman, "3:progman-direct");
    }
}

/// The ctx handed to plugin render(ctx). Members are lowercase to match the
/// JS contract exactly — ClearScript exposes them case-sensitively.
public sealed class D2DCanvasBridge
{
    private readonly ID2D1DeviceContext dc;
    private readonly ID2D1Factory1 factory;
    private readonly IDWriteFactory dwrite;
    private readonly ID2D1SolidColorBrush brush;
    private readonly Matrix3x2 baseTransform;
    private readonly double widthPts;
    private readonly double heightPts;
    private readonly Dictionary<string, object> props;
    private readonly Dictionary<string, IDWriteTextFormat> formats = new();

    private Matrix3x2 current = Matrix3x2.Identity;
    private readonly Stack<(Matrix3x2 m, Color4 fill, Color4 stroke, double lw, string font)> stack = new();
    private Color4 fillColor = new(0, 0, 0, 1);
    private Color4 strokeColor = new(0, 0, 0, 1);
    private string fontSpec = "13px Segoe UI";

    private readonly List<(List<Vector2> pts, bool closed)> figures = new();
    private List<Vector2>? openFigure;

    public D2DCanvasBridge(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite,
                           Matrix3x2 baseTransform, double width, double height, Dictionary<string, object> props)
    {
        this.dc = dc;
        this.factory = factory;
        this.dwrite = dwrite;
        this.baseTransform = baseTransform;
        widthPts = width;
        heightPts = height;
        this.props = props;
        brush = dc.CreateSolidColorBrush(fillColor);
    }

    public void BeginFrame()
    {
        current = Matrix3x2.Identity;
        stack.Clear();
        Apply();
    }

    private void Apply() => dc.Transform = current * baseTransform;

    // ---- state (JS-visible) ----

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
        stack.Push((current, fillColor, strokeColor, lineWidth, fontSpec));
    }

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
        brush.Color = new Color4(0.05f, 0.06f, 0.08f, 1);
        dc.FillRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h), brush);
    }

    public void fillRect(double x, double y, double w, double h)
    {
        brush.Color = WithAlpha(fillColor);
        dc.FillRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h), brush);
    }

    public void strokeRect(double x, double y, double w, double h)
    {
        brush.Color = WithAlpha(strokeColor);
        dc.DrawRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h), brush, (float)lineWidth);
    }

    // ---- paths (arcs flattened; real ArcSegments come in M1) ----

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
        double sweep = end - start;
        const double tau = Math.PI * 2;
        if (!anticlockwise)
        {
            sweep = sweep >= tau ? tau : ((sweep % tau) + tau) % tau;
        }
        else
        {
            sweep = end - start <= -tau ? -tau : -((((start - end) % tau) + tau) % tau);
        }
        const int steps = 64;
        for (int i = 0; i <= steps; i++)
        {
            double a = start + sweep * i / steps;
            var p = new Vector2((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
            if (i == 0)
            {
                // Canvas2D: line from the current point to the arc start.
                if (openFigure != null) openFigure.Add(p); else moveTo(p.X, p.Y);
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
                for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
                sink.EndFigure(filled || closed ? FigureEnd.Closed : FigureEnd.Open);
            }
            sink.Close();
        }
        brush.Color = WithAlpha(filled ? fillColor : strokeColor);
        if (filled) dc.FillGeometry(geometry, brush);
        else dc.DrawGeometry(geometry, brush, (float)lineWidth);
        // Canvas2D keeps the path after fill/stroke; the reference mac bridge
        // consumes it (documented v1 trade-off) — mirror the mac behavior.
        figures.Clear();
    }

    // ---- text ----

    public void fillText(string text, double x, double y)
    {
        var (format, size) = FormatFor(fontSpec);
        using var layout = dwrite.CreateTextLayout(text, format, 4096, 4096);
        brush.Color = WithAlpha(fillColor);
        // Canvas2D y is the baseline; approximate baseline at 0.8 × size.
        dc.DrawTextLayout(new Vector2((float)x, (float)(y - size * 0.8)), layout, brush);
    }

    public object measureText(string text)
    {
        var (format, _) = FormatFor(fontSpec);
        using var layout = dwrite.CreateTextLayout(text, format, 4096, 4096);
        return new Dictionary<string, object> { ["width"] = (double)layout.Metrics.Width };
    }

    public void drawImage(string name, double x, double y, double w, double h)
    {
        // Not in this spike; draw a placeholder so fixtures using it still run.
        brush.Color = new Color4(1, 1, 1, 0.15f);
        dc.FillRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h), brush);
    }

    public object? getProp(string name) => props.TryGetValue(name, out var v) ? v : null;

    private (IDWriteTextFormat format, double size) FormatFor(string spec)
    {
        if (!formats.TryGetValue(spec, out var format))
        {
            double size = 13;
            string family = "Segoe UI";
            bool bold = false;
            var tokens = new List<string>(spec.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (tokens.Count > 0 && (tokens[0] == "bold" || tokens[0] == "italic"))
            {
                bold = tokens[0] == "bold";
                tokens.RemoveAt(0);
            }
            var sizeToken = tokens.Find(t => t.EndsWith("px") || t.EndsWith("pt"));
            if (sizeToken != null && double.TryParse(sizeToken[..^2], out var v))
            {
                size = v;
                tokens.Remove(sizeToken);
            }
            if (tokens.Count > 0) family = string.Join(' ', tokens);
            format = dwrite.CreateTextFormat(family, bold ? FontWeight.Bold : FontWeight.Normal,
                                             Vortice.DirectWrite.FontStyle.Normal, (float)size);
            formats[spec] = format;
        }
        var parsedSize = 13.0;
        var px = spec.Split(' ');
        foreach (var t in px) if ((t.EndsWith("px") || t.EndsWith("pt")) && double.TryParse(t[..^2], out var s)) parsedSize = s;
        return (format, parsedSize);
    }

    private Color4 WithAlpha(Color4 c) => new(c.R, c.G, c.B, (float)(c.A * Math.Clamp(globalAlpha, 0, 1)));
}

internal static class Css
{
    public static bool TryParse(string s, out Color4 color)
    {
        color = new Color4(0, 0, 0, 1);
        s = s.Trim().ToLowerInvariant();
        try
        {
            if (s.StartsWith('#'))
            {
                var h = s[1..];
                if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
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
                float a = parts.Length > 3 ? float.Parse(parts[3]) : 1f;
                color = new Color4(float.Parse(parts[0]) / 255f, float.Parse(parts[1]) / 255f, float.Parse(parts[2]) / 255f, a);
                return true;
            }
            switch (s)
            {
                case "white": color = new Color4(1, 1, 1, 1); return true;
                case "black": color = new Color4(0, 0, 0, 1); return true;
                case "red": color = new Color4(1, 0, 0, 1); return true;
            }
        }
        catch { }
        return false;
    }
}

/// One plugin: its own V8 engine + bridge, drawn at an offset/scale in the
/// shared device context — mirrors the mac one-VM-per-plugin isolation.
internal sealed class SpikeItem : IDisposable
{
    private readonly V8ScriptEngine engine;
    private readonly D2DCanvasBridge bridge;
    private readonly dynamic render;
    public string Name { get; }

    public SpikeItem(string name, string source, ID2D1DeviceContext dc, ID2D1Factory1 factory,
                     IDWriteFactory dwrite, Matrix3x2 placement, double width, double height,
                     Dictionary<string, object> props)
    {
        Name = name;
        engine = new V8ScriptEngine(name);
        engine.Execute("var plugin = { export: null }; var console = { log: function () {}, error: function () {}, warn: function () {} };");
        engine.Execute(source);
        render = ((dynamic)engine.Script).plugin.export.render;
        bridge = new D2DCanvasBridge(dc, factory, dwrite, placement, width, height, props);
    }

    public void RenderFrame()
    {
        bridge.BeginFrame();
        render(bridge);
    }

    public void Dispose() => engine.Dispose();
}

internal static class Program
{
    private const int WinX = 1180, WinY = 560, WinW = 720, WinH = 420;

    [STAThread]
    private static void Main()
    {
        var baseDir = AppContext.BaseDirectory;
        var logPath = Path.Combine(baseDir, "render-log.txt");
        var log = new StringBuilder();
        void Log(string line)
        {
            log.AppendLine(line);
            File.WriteAllText(logPath, log.ToString());
        }

        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new System.Drawing.Rectangle(WinX, WinY, WinW, WinH),
            ShowInTaskbar = false,
        };

        var running = true;
        form.Shown += (_, _) =>
        {
            var (target, strategy) = Native.FindWallpaperHost();
            Native.SetParent(form.Handle, target);
            var parent = Native.GetAncestor(form.Handle, 1);
            Log($"attached via {strategy}, reparent-ok={parent == target}");

            var hwnd = form.Handle;
            new Thread(() => RenderThread(hwnd, () => running, Log)) { IsBackground = true }.Start();
        };

        var quit = new System.Windows.Forms.Timer { Interval = 40_000 };
        quit.Tick += (_, _) => { running = false; Application.Exit(); };
        quit.Start();
        Application.Run(form);
    }

    private static void RenderThread(IntPtr hwnd, Func<bool> keepRunning, Action<string> log)
    {
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out var d3dDevice).CheckError();
            using var dxgiDevice = d3dDevice!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var dxgiFactory = adapter.GetParent<IDXGIFactory2>();

            var scDesc = new SwapChainDescription1
            {
                Width = WinW,
                Height = WinH,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Scaling = Scaling.Stretch,
            };
            using var swapChain = dxgiFactory.CreateSwapChainForHwnd(d3dDevice, hwnd, scDesc);

            using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
            using var dc = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            using var backBuffer = swapChain.GetBuffer<IDXGISurface>(0);
            using var targetBitmap = dc.CreateBitmapFromDxgiSurface(backBuffer, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
            dc.Target = targetBitmap;

            using var dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();

            // Item A: the clock-face conformance fixture, verbatim, scaled 1.6×.
            var fixtureSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "clock-face.js"));
            using var itemA = new SpikeItem("clock-face", fixtureSource, dc, d2dFactory, dwrite,
                Matrix3x2.CreateScale(1.6f) * Matrix3x2.CreateTranslation(20, 60), 200, 100,
                new Dictionary<string, object>());

            // Item B: animated spike plugin (Date-driven second hand + frame counter).
            var animSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spike-anim.js"));
            using var itemB = new SpikeItem("spike-anim", animSource, dc, d2dFactory, dwrite,
                Matrix3x2.CreateTranslation(390, 60), 300, 300,
                new Dictionary<string, object>());

            log("D2D + V8 initialized, entering render loop");

            var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            long frames = 0, lastFrames = 0;
            var lastReport = TimeSpan.Zero;

            while (keepRunning())
            {
                dc.BeginDraw();
                dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0.05f, 0.06f, 0.08f, 1));
                itemA.RenderFrame();
                itemB.RenderFrame();
                dc.EndDraw();
                swapChain.Present(1, PresentFlags.None); // vsync-paced
                frames++;

                if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(5))
                {
                    process.Refresh();
                    var cpuPct = (process.TotalProcessorTime - cpuStart).TotalSeconds
                                 / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
                    var fps = (frames - lastFrames) / (clock.Elapsed - lastReport).TotalSeconds;
                    log($"t={clock.Elapsed.TotalSeconds:F0}s fps={fps:F1} cpu={cpuPct:F1}% frames={frames}");
                    lastReport = clock.Elapsed;
                    lastFrames = frames;
                }
            }
        }
        catch (Exception ex)
        {
            log("RENDER THREAD FAILED: " + ex);
        }
    }
}
