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
using System.IO;
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
    private readonly HookServer hookServer;
    private readonly System.Drawing.Rectangle screenBounds;
    private readonly Action<string> log;

    private Thread? renderThread;
    private volatile bool running = true;
    private volatile bool rebuildRequested = true;
    private IntPtr pendingHwnd;
    private volatile bool hwndChanged;

    /// Device pixels per point on the primary display (2.0 at 200% scale).
    /// The plugin contract is in points — declared sizes, canvas drawing,
    /// declarative layout — and rendering multiplies by this, mac parity.
    private readonly double dpiScale;

    /// Debug: DESKLAYER_DUMP_ITEM=<pluginId>:<path> writes that declarative
    /// item's next raster to a PNG. A remote or locked session can't always
    /// be screen-captured, and this shows exactly what the item drew.
    private readonly string? dumpItemPlugin;
    private string? dumpItemPath;

    public WallpaperEngine(LayoutStore store, PluginRegistry registry, System.Drawing.Rectangle screenBounds, Action<string> log)
    {
        this.store = store;
        this.registry = registry;
        this.screenBounds = screenBounds;
        this.log = log;
        var dipWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
        dpiScale = dipWidth > 0 ? Math.Clamp(screenBounds.Width / dipWidth, 0.5, 4.0) : 1.0;
        // First colon separates the plugin id from the path — a Windows path
        // carries its own colon ("C:\..."), so LastIndexOf would split wrong.
        if (Environment.GetEnvironmentVariable("DESKLAYER_DUMP_ITEM") is { Length: > 0 } spec &&
            spec.IndexOf(':') is > 0 and var split)
        {
            dumpItemPlugin = spec[..split];
            dumpItemPath = spec[(split + 1)..];
        }
        // Binds lazily on the resolved port ($server handlers only); mac parity.
        hookServer = new HookServer(log);
    }

    public void RequestRebuild() => rebuildRequested = true;

    /// Current render policy (set by the UI-thread PowerController). Paused
    /// skips rendering; throttled caps the per-item cadence. A torn read of
    /// this small struct is harmless — worst case the policy applies one
    /// frame late.
    private RenderPolicy policy = RenderPolicy.Run;
    public void SetPolicy(RenderPolicy value) => policy = value;

    /// Posts work to the STA UI thread (WPF rasterization for declarative
    /// wallpaper items). Set by Program before Start().
    public Action<Action>? PostToUi { get; set; }

    /// Marshals UI events (button clicks, text edits) onto the render
    /// thread, where every Jint instance lives — the mac per-plugin-queue
    /// contract. Drained once per frame.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> renderQueue = new();
    public void PostToRender(Action action) => renderQueue.Enqueue(action);

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

    /// The boot-affecting fields of a layout item. When these are unchanged,
    /// an edit can be applied live (position, z-order, background, most
    /// property overrides) without restarting the plugin — the mac
    /// SpawnIdentity/applyLiveEdits contract. Cadence overrides (fps/interval)
    /// are baked into the instance at boot, so they belong here.
    private readonly record struct SpawnIdentity(
        string PluginId, string DisplayUuid, RenderTarget Target, bool ClickThrough,
        string? Fps, string? Interval)
    {
        public SpawnIdentity(LayoutItem item) : this(
            item.PluginId, item.DisplayUuid, item.Target, item.ClickThrough,
            item.PropertyOverrides.TryGetValue("fps", out var f) ? f.StringValue : null,
            item.PropertyOverrides.TryGetValue("interval", out var i) ? i.StringValue : null)
        { }
    }

    private sealed class Item : IDisposable
    {
        public required LayoutItem Layout;
        public required SpawnIdentity Identity;
        public required PluginInstance Instance;
        public required ID2D1Bitmap1 Surface;
        public D2DCanvas? Canvas; // canvas mode only
        public ImageCache? Images; // .deskplugin folder assets, if any
        public required System.Drawing.RectangleF DestRect;
        public required int PixelWidth;
        public required int PixelHeight;
        public Color4? Background;
        public double NextDue;
        public bool RenderedOnce;

        // Declarative mode: the last tree JSON (skip identical renders), the
        // freshly rasterized pixels awaiting upload, and lifecycle guards
        // for the UI-thread round trip.
        public string? LastTree;
        public byte[]? PendingPixels;
        public bool RasterInFlight;
        /// Declared metadata (autoSize axes), read once at spawn.
        public DeskLayer.Core.PluginMetadata.PluginInfo? Info;
        public volatile bool Disposed;
        public readonly object Gate = new();

        public void Dispose()
        {
            Disposed = true;
            Canvas?.Dispose();
            Images?.Dispose();
            Surface.Dispose();
            Instance.Dispose();
        }
    }

    /// A floating-window item: the Jint instance lives on the render thread,
    /// its live interactive WPF tree lives in a FloatingPanel on the UI
    /// thread; the two only ever exchange tree JSON and posted actions.
    private sealed class FloatingItem
    {
        public required LayoutItem Layout;
        public required PluginInstance Instance;
        public string? LastTree;
        public double NextDue;
        public bool RenderedOnce;
        public bool UpdateInFlight;
        public volatile bool Disposed;
        public FloatingPanel? Panel; // UI thread only
    }

    private readonly List<FloatingItem> floatingItems = new();

    /// A webview item: config resolved at boot (the Jint engine is disposed
    /// right after), hosted entirely on the UI thread.
    private sealed class WebItem
    {
        public required LayoutItem Layout;
        public required WebViewConfig Config;
        public WebViewHostWindow? Host; // UI thread only
    }

    private readonly List<WebItem> webItems = new();
    private string lastFloatWebFingerprint = "";

    /// Changes when a floating/webview host must rebuild: any item's presence,
    /// plugin, target, or click-through (catches a new wallpaper-webview,
    /// whose mode isn't known without booting), plus a floating item's frame
    /// or background. A pure frame/background edit of a *wallpaper* item does
    /// NOT change this, so it reconciles in place without disturbing panels.
    /// Quiet drag writebacks don't fire OnChange, so dragging a panel doesn't
    /// rebuild it either.
    private string FloatWebFingerprint() => string.Join(";", store.Layout.Items
        .Where(i => i.IsEnabled)
        .OrderBy(i => i.Id)
        .Select(i => i.Target == RenderTarget.FloatingWindow
            ? $"{i.Id}|{i.PluginId}|F|{i.ClickThrough}|{i.NormalizedFrame.X:F4},{i.NormalizedFrame.Y:F4},{i.NormalizedFrame.W:F4},{i.NormalizedFrame.H:F4}|{i.BackgroundColor}"
            : $"{i.Id}|{i.PluginId}|W|{i.ClickThrough}"));

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
                    // Wallpaper items reconcile in place — position/z-order/
                    // background/property edits keep the plugin running.
                    ReconcileItems(items, dc, d2dFactory, dwrite);

                    // Floating/webview items are hosted on the UI thread with
                    // heavier lifecycle; rebuild them only when their set
                    // actually changed (a pure wallpaper edit leaves them be).
                    var floatWebPrint = FloatWebFingerprint();
                    if (floatWebPrint != lastFloatWebFingerprint)
                    {
                        lastFloatWebFingerprint = floatWebPrint;
                        DisposeFloatingItems();
                        DisposeWebItems();
                        BuildFloatingItems();
                        BuildWebItems();
                    }
                    log($"reconciled: {items.Count} wallpaper + {floatingItems.Count} floating + {webItems.Count} webview items");
                }

                // UI events destined for Jint (actions, drag writebacks).
                while (renderQueue.TryDequeue(out var queued)) queued();

                // Power policy: paused stops all rendering (screen locked /
                // system suspended); async callbacks still drain so state
                // stays consistent when we resume.
                var currentPolicy = policy;
                if (currentPolicy.Kind == PolicyKind.Paused)
                {
                    foreach (var item in items) item.Instance.Pump();
                    foreach (var floating in floatingItems) floating.Instance.Pump();
                    Thread.Sleep(200);
                    continue;
                }
                // Throttled (battery saver): clamp every item's cadence.
                var minInterval = currentPolicy.Kind == PolicyKind.Throttled && currentPolicy.MaxFps > 0
                    ? 1.0 / currentPolicy.MaxFps
                    : 0;

                // Timers and completed fetch/WebSocket callbacks (Jint runs
                // only here, on its owning thread).
                foreach (var item in items) item.Instance.Pump();
                foreach (var floating in floatingItems) floating.Instance.Pump();

                var now = clock.Elapsed.TotalSeconds;
                foreach (var item in items)
                {
                    if (item.Instance.IsErrored) continue;
                    var due = !item.RenderedOnce
                        || (double.IsFinite(item.Instance.RenderInterval) && now >= item.NextDue);
                    if (!due) continue;

                    if (item.Canvas is { } canvas)
                    {
                        dc.Target = item.Surface;
                        dc.BeginDraw();
                        canvas.BeginFrame();
                        if (!item.Instance.CallRender(canvas))
                            log($"{item.Layout.PluginId} errored: {item.Instance.ErrorMessage}");
                        dc.EndDraw();
                    }
                    else if (!item.RasterInFlight)
                    {
                        // Declarative: Jint runs here; identical trees skip
                        // the WPF round trip entirely (mac diffing parity).
                        var json = item.Instance.CallRenderTree();
                        if (json == null)
                        {
                            log($"{item.Layout.PluginId} errored: {item.Instance.ErrorMessage}");
                        }
                        else if (json != item.LastTree)
                        {
                            item.LastTree = json;
                            item.RasterInFlight = true;
                            var (w, h) = (item.PixelWidth, item.PixelHeight);
                            PostToUi?.Invoke(() =>
                            {
                                var pixels = DeclarativeRasterizer.Rasterize(json, w, h, dpiScale,
                                    message => log($"[{item.Layout.PluginId}] {message}"),
                                    item.Info?.AutoSizeWidth ?? false,
                                    item.Info?.AutoSizeHeight ?? false);
                                // Overwritten on every raster, so the file
                                // holds the most recent frame at exit — the
                                // first one is usually still "connecting…".
                                if (pixels != null && dumpItemPath is { } dumpPath &&
                                    item.Layout.PluginId == dumpItemPlugin)
                                    DeclarativeRasterizer.DumpPng(pixels, w, h, dumpPath, log);
                                lock (item.Gate)
                                {
                                    if (!item.Disposed) item.PendingPixels = pixels;
                                    item.RasterInFlight = false;
                                }
                            });
                        }
                    }
                    item.RenderedOnce = true;
                    item.NextDue = now + Math.Max(item.Instance.RenderInterval, minInterval);
                }

                // Floating items: run Jint here, push changed trees to the
                // UI thread where the live interactive panel rebuilds.
                foreach (var floating in floatingItems)
                {
                    if (floating.Instance.IsErrored || floating.UpdateInFlight) continue;
                    var floatingDue = !floating.RenderedOnce
                        || (double.IsFinite(floating.Instance.RenderInterval) && now >= floating.NextDue);
                    if (!floatingDue) continue;
                    var json = floating.Instance.CallRenderTree();
                    floating.RenderedOnce = true;
                    floating.NextDue = now + Math.Max(Math.Max(floating.Instance.RenderInterval, 1.0 / 30.0), minInterval);
                    if (json == null)
                    {
                        log($"{floating.Layout.PluginId} errored: {floating.Instance.ErrorMessage}");
                        continue;
                    }
                    if (json == floating.LastTree) continue;
                    floating.LastTree = json;
                    floating.UpdateInFlight = true;
                    PostToUi?.Invoke(() =>
                    {
                        try
                        {
                            if (!floating.Disposed) UpdateFloatingPanel(floating, json);
                        }
                        finally
                        {
                            floating.UpdateInFlight = false;
                        }
                    });
                }

                // Upload freshly rasterized declarative pixels.
                foreach (var item in items)
                {
                    byte[]? pixels;
                    lock (item.Gate)
                    {
                        pixels = item.PendingPixels;
                        item.PendingPixels = null;
                    }
                    if (pixels == null) continue;
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        dc.Target = item.Surface;
                        dc.BeginDraw();
                        // The device context is shared with canvas items,
                        // which leave their DPI base transform set; these
                        // pixels are already at device resolution, so a
                        // leftover scale would draw them double-size.
                        dc.Transform = System.Numerics.Matrix3x2.Identity;
                        dc.Clear(new Color4(0f, 0f, 0f, 0f));
                        using var uploaded = dc.CreateBitmap(
                            new System.Drawing.Size(item.PixelWidth, item.PixelHeight),
                            handle.AddrOfPinnedObject(), item.PixelWidth * 4,
                            new BitmapProperties1(
                                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                                96, 96, BitmapOptions.None));
                        dc.DrawBitmap(uploaded, new System.Drawing.RectangleF(0, 0, item.PixelWidth, item.PixelHeight),
                            1f, BitmapInterpolationMode.NearestNeighbor, null);
                        dc.EndDraw();
                    }
                    finally
                    {
                        handle.Free();
                    }
                }

                dc.Target = backBuffer;
                dc.BeginDraw();
                dc.Transform = System.Numerics.Matrix3x2.Identity;
                dc.Clear(new Color4(0f, 0f, 0f, 1f));
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
            DisposeFloatingItems();
            DisposeWebItems();
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

    /// Reconcile the wallpaper items against the current layout without
    /// tearing down plugins whose identity is unchanged (mac reconcile):
    /// respawn only on an identity change, live-edit the rest, spawn the new.
    private void ReconcileItems(List<Item> items, ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite)
    {
        // Skip ids already hosted as webview wallpaper items, so a webview
        // plugin isn't re-booted every reconcile just to detect its mode.
        var webIds = webItems.Select(w => w.Layout.Id).ToHashSet();
        var wanted = store.Layout.Items
            .Where(i => i.IsEnabled && i.Target == RenderTarget.Wallpaper
                        && registry.Plugin(i.PluginId) != null && !webIds.Contains(i.Id))
            .ToDictionary(i => i.Id, i => i);

        // Drop items no longer wanted or whose identity changed.
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (!wanted.TryGetValue(item.Layout.Id, out var want) || new SpawnIdentity(want) != item.Identity)
            {
                hookServer.RemoveHandlers(item.Layout.Id);
                item.Dispose();
                items.RemoveAt(index);
            }
        }

        var running = items.Select(i => i.Layout.Id).ToHashSet();
        foreach (var layoutItem in wanted.Values)
        {
            if (running.Contains(layoutItem.Id))
            {
                ApplyLiveEdits(items.First(i => i.Layout.Id == layoutItem.Id), layoutItem);
            }
            else if (SpawnItem(layoutItem, dc, factory, dwrite) is { } spawned)
            {
                items.Add(spawned);
            }
        }
    }

    /// Absorb an edit the running item can keep: position/size, z-order,
    /// background, and non-cadence property overrides (pushed into JS live).
    private void ApplyLiveEdits(Item item, LayoutItem layout)
    {
        var frame = layout.NormalizedFrame;
        var w = Math.Max(8, (int)(frame.W * screenBounds.Width));
        var h = Math.Max(8, (int)(frame.H * screenBounds.Height));
        var x = (float)(frame.X * screenBounds.Width);
        var y = (float)((1 - frame.Y - frame.H) * screenBounds.Height);
        // Position/size update the dest rect; the existing surface scales
        // (mac contentsGravity=.resize parity — no buffer rebuild, no state loss).
        item.DestRect = new System.Drawing.RectangleF(x, y, w, h);
        item.Background = layout.BackgroundColor is { } css && CssColor.TryParse(css, out var bg) ? bg : null;

        // Push changed overrides (except cadence, which is in the identity).
        foreach (var (name, value) in layout.PropertyOverrides)
        {
            if (name is "fps" or "interval") continue;
            if (!item.Layout.PropertyOverrides.TryGetValue(name, out var old) || !old.Equals(value))
                item.Instance.ApplyOverride(name, value);
        }
        // SSH destinations aren't part of the spawn identity — re-resolve so
        // an inspector edit reaches the running instance without a respawn.
        WireSsh(layout, item.Instance);
        item.Layout = layout;
    }

    /// Resolve an item's SSH destinations for an ssh-permitted instance. An
    /// alias entry stays an alias — the bundled OpenSSH resolves hostname,
    /// user, port, and identity from ~/.ssh/config, same as `ssh <alias>`.
    private static void WireSsh(LayoutItem layout, PluginInstance instance)
    {
        if (!instance.Permissions.Contains("ssh")) return;
        instance.ConfigureSsh(layout.SshHosts.Select(h => h.UsesAlias
            ? new HostBindings.ResolvedSsh(h.Name, h.Host, 22, "", null)
            : new HostBindings.ResolvedSsh(h.Name, h.Host, h.Port, h.User,
                h.KeyPath.Length == 0 ? null : h.KeyPath)).ToList());
    }

    private Item? SpawnItem(LayoutItem layoutItem, ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite)
    {
        {
            var plugin = registry.Plugin(layoutItem.PluginId);
            if (plugin == null)
            {
                log($"{layoutItem.PluginId}: not installed, item offline");
                return null;
            }

            var source = File.ReadAllText(plugin.SourcePath);
            var instance = PluginInstance.Boot(
                layoutItem.PluginId,
                source,
                layoutItem.PropertyOverrides,
                message => log($"[{layoutItem.PluginId}] {message}"),
                hostStats: systemStats);
            if (instance == null || instance.Mode == RenderMode.Webview)
            {
                if (instance == null) log($"{layoutItem.PluginId}: boot failed");
                instance?.Dispose(); // webview items are built by BuildWebItems
                return null;
            }
            WireHooks(layoutItem, instance);
            WireSsh(layoutItem, instance);
            var info = DeskLayer.Core.PluginMetadata.ExtractInfo(source);

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
            dc.Clear(new Color4(0f, 0f, 0f, 0f));
            dc.EndDraw();

            // Folder plugins (Name.deskplugin/) carry image assets for
            // ctx.drawImage; bare .js plugins have none.
            var imageCache = plugin.AssetsDirectory is { } assets ? new ImageCache(dc, assets) : null;
            var canvas = instance.Mode == RenderMode.Canvas
                ? new D2DCanvas(dc, factory, dwrite, w, h, dpiScale)
                {
                    PropertyProvider = name => instance.PropertyNamed(name)?.BridgeValue,
                    ImageProvider = imageCache != null ? imageCache.Image : null,
                }
                : null;

            return new Item
            {
                Layout = layoutItem,
                Identity = new SpawnIdentity(layoutItem),
                Info = info,
                Instance = instance,
                Surface = surface,
                Canvas = canvas,
                Images = imageCache,
                DestRect = new System.Drawing.RectangleF(x, y, w, h),
                PixelWidth = w,
                PixelHeight = h,
                Background = layoutItem.BackgroundColor is { } css && CssColor.TryParse(css, out var bg) ? bg : null,
            };
        }
    }

    /// Wire a $server-permitted instance's hook registrations to the loopback
    /// server. The HookServer delivers on a connection thread; the registered
    /// deliver callback (from HostBindings) just enqueues onto the plugin's
    /// completion queue, so JS still runs on the render thread at Pump.
    private void WireHooks(LayoutItem layout, PluginInstance instance)
    {
        if (!instance.Permissions.Contains("server"))
        {
            instance.ConfigureHookRegistrar(null);
            return;
        }
        instance.ConfigureHookRegistrar((method, deliver) =>
            hookServer.AddHandler(new HookServer.Handler(layout.Id, method, deliver)));
    }

    // ---- floating items (render thread builds, UI thread hosts) ----

    private void BuildFloatingItems()
    {
        foreach (var layoutItem in store.Layout.Items)
        {
            if (!layoutItem.IsEnabled || layoutItem.Target != RenderTarget.FloatingWindow) continue;
            var plugin = registry.Plugin(layoutItem.PluginId);
            if (plugin == null)
            {
                log($"{layoutItem.PluginId}: not installed, floating item offline");
                continue;
            }
            var instance = PluginInstance.Boot(
                layoutItem.PluginId,
                File.ReadAllText(plugin.SourcePath),
                layoutItem.PropertyOverrides,
                message => log($"[{layoutItem.PluginId}] {message}"),
                hostStats: systemStats);
            if (instance == null || instance.Mode != RenderMode.Declarative)
            {
                if (instance is { Mode: RenderMode.Canvas })
                    log($"{layoutItem.PluginId}: floating canvas not supported yet (M3 follow-up)");
                instance?.Dispose(); // webview floats are built by BuildWebItems
                continue;
            }
            WireHooks(layoutItem, instance);
            WireSsh(layoutItem, instance);
            floatingItems.Add(new FloatingItem { Layout = layoutItem, Instance = instance });
        }
    }

    private void DisposeFloatingItems()
    {
        foreach (var floating in floatingItems)
        {
            floating.Disposed = true;
            hookServer.RemoveHandlers(floating.Layout.Id);
            var panel = floating;
            PostToUi?.Invoke(() => { panel.Panel?.Close(); panel.Panel = null; });
            floating.Instance.Dispose();
        }
        floatingItems.Clear();
    }

    /// UI thread: (re)build the live interactive tree inside the panel.
    private void UpdateFloatingPanel(FloatingItem floating, string treeJson)
    {
        var node = ViewNode.Decode(treeJson);
        if (node == null) return;

        NodeInterpreter.ActionHandler onAction = (id, payload) =>
        {
            log($"[{floating.Layout.PluginId}] action {id} fired: {payload}");
            PostToRender(() =>
            {
                if (floating.Disposed || floating.Instance.IsErrored) return;
                floating.Instance.InvokeAction(id, payload);
                if (floating.Instance.IsErrored)
                    log($"[{floating.Layout.PluginId}] action {id} errored: {floating.Instance.ErrorMessage}");
                floating.RenderedOnce = false; // re-render promptly with new state
            });
        };

        var content = NodeInterpreter.Build(node, onAction, message => log($"[{floating.Layout.PluginId}] {message}"));
        System.Windows.Documents.TextElement.SetFontSize(content, 13);
        System.Windows.Documents.TextElement.SetForeground(content, System.Windows.Media.Brushes.White);

        System.Windows.FrameworkElement rootContent = content;
        if (floating.Layout.BackgroundColor is { } css && CssColor.TryParse(css, out var bg))
        {
            rootContent = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
                    (byte)(bg.A * 255), (byte)(bg.R * 255), (byte)(bg.G * 255), (byte)(bg.B * 255))),
                Child = content,
            };
        }

        if (floating.Panel == null)
        {
            // WPF windows position in DIPs; the layout frame is physical px.
            var scale = dpiScale;
            var frame = floating.Layout.NormalizedFrame;
            var widthPx = frame.W * screenBounds.Width;
            var heightPx = frame.H * screenBounds.Height;
            var leftPx = frame.X * screenBounds.Width;
            var topPx = (1 - frame.Y - frame.H) * screenBounds.Height;

            var panel = new FloatingPanel(floating.Layout.ClickThrough)
            {
                Left = leftPx / scale,
                Top = topPx / scale,
                Width = widthPx / scale,
                Height = heightPx / scale,
            };
            panel.OnMovedDip = (leftDip, topDip) =>
                PersistMove(floating, leftDip * scale, topDip * scale, heightPx);
            floating.Panel = panel;
            // Modeless WPF on a WinForms loop: without keyboard interop a
            // TextField in the panel never receives typed characters.
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(panel);
            panel.Show();
        }
        floating.Panel.Content = rootContent;
    }

    /// UI thread, after a drag: write the new normalized frame back without
    /// waking the rebuild path (quiet update — mac suppressRebuild parity).
    private void PersistMove(FloatingItem floating, double leftPx, double topPx, double heightPx)
    {
        var frame = floating.Layout.NormalizedFrame;
        var moved = frame with
        {
            X = leftPx / screenBounds.Width,
            Y = 1 - (topPx + heightPx) / screenBounds.Height,
        };
        floating.Layout = floating.Layout with { NormalizedFrame = moved };
        var itemId = floating.Layout.Id;
        store.Update(layout => layout with
        {
            Items = layout.Items
                .Select(item => item.Id == itemId ? item with { NormalizedFrame = moved } : item)
                .ToList(),
        }, quiet: true);
    }

    // ---- webview items ----

    private void BuildWebItems()
    {
        foreach (var layoutItem in store.Layout.Items)
        {
            if (!layoutItem.IsEnabled) continue;
            var plugin = registry.Plugin(layoutItem.PluginId);
            if (plugin == null) continue;

            // Cheap mode probe: boot, harvest the config, discard the engine.
            using var instance = PluginInstance.Boot(
                layoutItem.PluginId,
                File.ReadAllText(plugin.SourcePath),
                layoutItem.PropertyOverrides,
                message => log($"[{layoutItem.PluginId}] {message}"));
            if (instance?.Mode != RenderMode.Webview || instance.WebviewConfig == null) continue;

            var webItem = new WebItem { Layout = layoutItem, Config = instance.WebviewConfig };
            webItems.Add(webItem);
            PostToUi?.Invoke(() => CreateWebHost(webItem));
        }
    }

    private void DisposeWebItems()
    {
        foreach (var webItem in webItems)
        {
            var captured = webItem;
            PostToUi?.Invoke(() => { captured.Host?.Dispose(); captured.Host = null; });
        }
        webItems.Clear();
    }

    /// UI thread: create the window, then reparent wallpaper-target ones
    /// under WorkerW (they sit above the D2D wallpaper window as a later
    /// sibling).
    private void CreateWebHost(WebItem webItem)
    {
        var frame = webItem.Layout.NormalizedFrame;
        var pixelRect = new System.Drawing.RectangleF(
            (float)(frame.X * screenBounds.Width),
            (float)((1 - frame.Y - frame.H) * screenBounds.Height),
            (float)(frame.W * screenBounds.Width),
            (float)(frame.H * screenBounds.Height));
        var scale = dpiScale;

        var host = new WebViewHostWindow(webItem.Config, webItem.Layout, pixelRect, scale,
            message => log($"[{webItem.Layout.PluginId}] {message}"));
        webItem.Host = host;
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(host.Window);
        host.Window.Show();

        if (webItem.Layout.Target == RenderTarget.Wallpaper)
        {
            var (target, strategy) = Native.FindWallpaperHost();
            if (target == IntPtr.Zero)
            {
                log($"[{webItem.Layout.PluginId}] no wallpaper host for webview yet");
                return;
            }
            var hwnd = new System.Windows.Interop.WindowInteropHelper(host.Window).Handle;
            Native.SetParent(hwnd, target);
            log($"[{webItem.Layout.PluginId}] webview attached via {strategy}");
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
        hookServer.Dispose();
    }
}
