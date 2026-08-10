// The wallpaper renderer: one D3D/D2D device owned by a dedicated render
// thread, a flip-model swap chain bound to the (recreatable) wallpaper HWND,
// and one persistent premultiplied bitmap per layout item — the Windows twin
// of the mac ScreenManager + FrameScheduler + ItemRenderer stack.
//
// Because the swap chain is opaque under WorkerW (M0: layered windows don't
// composite there), the engine draws the user's actual wallpaper image as
// its own base layer, then the items above it.
//
// Threading: the UI thread owns the host form + attach watchdog and only
// posts flags; everything D2D/Jint runs on the render thread (Jint instances
// are single-threaded by contract, matching the mac per-plugin queue).

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskLayer.App;

public sealed class WallpaperEngine : IDisposable
{
    private readonly LayoutStore store;
    private readonly PluginRegistry registry;
    private readonly SystemStatsBinding systemStats = new();
    private readonly System.Drawing.Rectangle screenBounds;
    private readonly Action<string> log;

    private Thread? renderThread;
    private volatile bool running = true;
    private volatile bool rebuildRequested = true;
    private IntPtr pendingHwnd;
    private volatile bool hwndChanged;

    public WallpaperEngine(LayoutStore store, PluginRegistry registry, System.Drawing.Rectangle screenBounds, Action<string> log)
    {
        this.store = store;
        this.registry = registry;
        this.screenBounds = screenBounds;
        this.log = log;
    }

    public void RequestRebuild() => rebuildRequested = true;

    /// UI thread: the wallpaper HWND changed (first attach or Explorer-restart
    /// recreation). The render thread rebinds its swap chain on next tick.
    public void SetHwnd(IntPtr hwnd)
    {
        pendingHwnd = hwnd;
        hwndChanged = true;
    }

    public void Start()
    {
        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "desklayer-render" };
        renderThread.Start();
    }

    private sealed class Item : IDisposable
    {
        public required LayoutItem Layout;
        public required PluginInstance Instance;
        public required ID2D1Bitmap1 Surface;
        public required D2DCanvas Canvas;
        public required System.Drawing.RectangleF DestRect;
        public Color4? Background;
        public double NextDue;
        public bool RenderedOnce;

        public void Dispose()
        {
            Canvas.Dispose();
            Surface.Dispose();
            Instance.Dispose();
        }
    }

    private void RenderLoop()
    {
        ID3D11Device? d3d = null;
        try
        {
            D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out d3d).CheckError();
            using var dxgiDevice = d3d!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var dxgiFactory = adapter.GetParent<IDXGIFactory2>();
            using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
            using var dc = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            using var dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();

            IDXGISwapChain1? swapChain = null;
            ID2D1Bitmap1? backBuffer = null;
            var wallpaper = LoadWallpaperBitmap(dc);
            var items = new List<Item>();
            var clock = Stopwatch.StartNew();

            void ReleaseSwapChain()
            {
                dc.Target = null;
                backBuffer?.Dispose(); backBuffer = null;
                swapChain?.Dispose(); swapChain = null;
            }

            while (running)
            {
                if (hwndChanged)
                {
                    hwndChanged = false;
                    ReleaseSwapChain();
                    var hwnd = pendingHwnd;
                    if (hwnd != IntPtr.Zero)
                    {
                        swapChain = dxgiFactory.CreateSwapChainForHwnd(d3d, hwnd, new SwapChainDescription1
                        {
                            Width = screenBounds.Width,
                            Height = screenBounds.Height,
                            Format = Format.B8G8R8A8_UNorm,
                            BufferCount = 2,
                            BufferUsage = Usage.RenderTargetOutput,
                            SampleDescription = new SampleDescription(1, 0),
                            SwapEffect = SwapEffect.FlipDiscard,
                            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                            Scaling = Scaling.Stretch,
                        });
                        using var surface = swapChain.GetBuffer<IDXGISurface>(0);
                        backBuffer = dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                            96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
                        log($"swap chain bound to 0x{hwnd:X8}");
                    }
                }

                if (swapChain == null || backBuffer == null)
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (rebuildRequested)
                {
                    rebuildRequested = false;
                    foreach (var item in items) item.Dispose();
                    items.Clear();
                    items.AddRange(BuildItems(dc, d2dFactory, dwrite));
                    log($"spawned {items.Count} items");
                }

                var now = clock.Elapsed.TotalSeconds;
                foreach (var item in items)
                {
                    if (item.Instance.IsErrored) continue;
                    var due = !item.RenderedOnce
                        || (double.IsFinite(item.Instance.RenderInterval) && now >= item.NextDue);
                    if (!due) continue;
                    dc.Target = item.Surface;
                    dc.BeginDraw();
                    item.Canvas.BeginFrame();
                    if (!item.Instance.CallRender(item.Canvas))
                        log($"{item.Layout.PluginId} errored: {item.Instance.ErrorMessage}");
                    dc.EndDraw();
                    item.RenderedOnce = true;
                    item.NextDue = now + item.Instance.RenderInterval;
                }

                dc.Target = backBuffer;
                dc.BeginDraw();
                dc.Transform = System.Numerics.Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 1));
                if (wallpaper != null) DrawWallpaper(dc, wallpaper);
                foreach (var item in items.OrderBy(i => i.Layout.ZOrder))
                {
                    if (item.Background is { } bg)
                    {
                        using var brush = dc.CreateSolidColorBrush(bg);
                        dc.FillRectangle(item.DestRect, brush);
                    }
                    dc.DrawBitmap(item.Surface, item.DestRect, 1f, BitmapInterpolationMode.Linear, null);
                }
                dc.EndDraw();
                swapChain.Present(1, PresentFlags.None);
            }

            foreach (var item in items) item.Dispose();
            wallpaper?.Dispose();
            ReleaseSwapChain();
        }
        catch (Exception ex)
        {
            log("RENDER THREAD FAILED: " + ex);
        }
        finally
        {
            d3d?.Dispose();
        }
    }

    private IEnumerable<Item> BuildItems(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite)
    {
        foreach (var layoutItem in store.Layout.Items)
        {
            if (!layoutItem.IsEnabled) continue;
            if (layoutItem.Target != RenderTarget.Wallpaper) continue; // floating: M1 follow-up
            var plugin = registry.Plugin(layoutItem.PluginId);
            if (plugin == null)
            {
                log($"{layoutItem.PluginId}: not installed, item offline");
                continue;
            }

            var instance = PluginInstance.Boot(
                layoutItem.PluginId,
                File.ReadAllText(plugin.SourcePath),
                layoutItem.PropertyOverrides,
                message => log($"[{layoutItem.PluginId}] {message}"),
                engine => engine.SetValue("$system", systemStats));
            if (instance == null || instance.Mode != RenderMode.Canvas)
            {
                log($"{layoutItem.PluginId}: skipped ({(instance == null ? "boot failed" : "non-canvas: M2")})");
                instance?.Dispose();
                continue;
            }

            // Bottom-left-origin normalized frame → top-left pixel rect.
            var frame = layoutItem.NormalizedFrame;
            var w = Math.Max(8, (int)(frame.W * screenBounds.Width));
            var h = Math.Max(8, (int)(frame.H * screenBounds.Height));
            var x = (float)(frame.X * screenBounds.Width);
            var y = (float)((1 - frame.Y - frame.H) * screenBounds.Height);

            var surface = dc.CreateBitmap(new System.Drawing.Size(w, h), IntPtr.Zero, 0, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target));
            dc.Target = surface;
            dc.BeginDraw();
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.EndDraw();

            var canvas = new D2DCanvas(dc, factory, dwrite, w, h)
            {
                PropertyProvider = name => instance.PropertyNamed(name)?.BridgeValue,
            };

            yield return new Item
            {
                Layout = layoutItem,
                Instance = instance,
                Surface = surface,
                Canvas = canvas,
                DestRect = new System.Drawing.RectangleF(x, y, w, h),
                Background = layoutItem.BackgroundColor is { } css && CssColor.TryParse(css, out var bg) ? bg : null,
            };
        }
    }

    // ---- wallpaper base layer ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint param, char[] buffer, uint winIni);

    private ID2D1Bitmap1? LoadWallpaperBitmap(ID2D1DeviceContext dc)
    {
        try
        {
            var buffer = new char[520];
            if (!SystemParametersInfoW(0x0073 /*SPI_GETDESKWALLPAPER*/, (uint)buffer.Length, buffer, 0)) return null;
            var path = new string(buffer).TrimEnd('\0');
            if (path.Length == 0 || !File.Exists(path)) return null;

            using var gdi = new System.Drawing.Bitmap(path);
            using var converted = gdi.Clone(new System.Drawing.Rectangle(0, 0, gdi.Width, gdi.Height), PixelFormat.Format32bppPArgb);
            var data = converted.LockBits(new System.Drawing.Rectangle(0, 0, converted.Width, converted.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                return dc.CreateBitmap(new System.Drawing.Size(converted.Width, converted.Height), data.Scan0, data.Stride,
                    new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                        96, 96, BitmapOptions.None));
            }
            finally
            {
                converted.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            log("wallpaper load failed: " + ex.Message);
            return null;
        }
    }

    /// Aspect-fill the wallpaper into the screen (the default "Fill" style).
    private void DrawWallpaper(ID2D1DeviceContext dc, ID2D1Bitmap1 wallpaper)
    {
        var src = wallpaper.Size;
        float screenAspect = (float)screenBounds.Width / screenBounds.Height;
        float sourceAspect = src.Width / src.Height;
        System.Drawing.RectangleF sourceRect;
        if (sourceAspect > screenAspect)
        {
            var cropWidth = src.Height * screenAspect;
            sourceRect = new System.Drawing.RectangleF((src.Width - cropWidth) / 2, 0, cropWidth, src.Height);
        }
        else
        {
            var cropHeight = src.Width / screenAspect;
            sourceRect = new System.Drawing.RectangleF(0, (src.Height - cropHeight) / 2, src.Width, cropHeight);
        }
        dc.DrawBitmap(wallpaper, new System.Drawing.RectangleF(0, 0, screenBounds.Width, screenBounds.Height),
            1f, BitmapInterpolationMode.Linear, sourceRect);
    }

    public void Dispose()
    {
        running = false;
        renderThread?.Join(2000);
    }
}
