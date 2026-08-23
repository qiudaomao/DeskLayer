// The Linux wallpaper engine v0 (M1 walking skeleton): loads the
// wire-compatible layout.json, boots canvas-mode plugins on Jint (Core),
// renders each into a persistent per-item SKBitmap on its declared cadence,
// composites onto a transparent base (layer-shell shows the compositor's
// own wallpaper through), and presents full frames to the surface.
//
// Reference: win/src/DeskLayer.App/WallpaperEngine.cs (1198 LOC). This v0
// carries only what M1 needs; declarative/floating/reconcile land in M2+.
//
// v1 simplifications (tracked):
// - single output; every enabled wallpaper item renders regardless of its
//   DisplayUuid (mac/win layouts carry their own display ids).
// - layout edits rebuild every item (JS state resets) rather than the
//   mac/win in-place reconcile — acceptable while the Manager is young.

using System.Diagnostics;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;
using DeskLayer.LinuxApp.Platform;
using DeskLayer.LinuxApp.Rendering;
using DeskLayer.LinuxApp.Surfaces;
using SkiaSharp;

namespace DeskLayer.LinuxApp;

public sealed class WallpaperEngine : IDisposable
{
    private sealed class Item
    {
        public required LayoutItem Layout;
        public required PluginInstance Instance;
        public required SKBitmap Bitmap;
        public required SKCanvas Canvas;
        public SkiaCanvas? Bridge;          // canvas mode only
        public string? LastTreeJson;        // declarative: skip unchanged renders
        public SKRect DestRect;
        public double NextDue;
        public bool RenderedOnce;
    }

    private readonly IWallpaperSurface surface;
    private readonly Action<string> log;
    private readonly List<Item> items = new();
    private readonly SKBitmap frame;
    private readonly SKCanvas frameCanvas;
    private readonly SystemStatsBinding systemStats = new();
    private FileSystemWatcher? watcher;
    private long layoutDirty;

    public WallpaperEngine(IWallpaperSurface surface, Action<string> log)
    {
        this.surface = surface;
        this.log = log;
        frame = new SKBitmap(new SKImageInfo(surface.WidthPx, surface.HeightPx,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        frameCanvas = new SKCanvas(frame);
    }

    /// Watches layout.json so Manager edits (a separate process) apply
    /// without restarting the service. Debounced; the loop rebuilds.
    public void WatchLayout()
    {
        var dir = LayoutStore.DataDirectory;
        Directory.CreateDirectory(dir);
        watcher = new FileSystemWatcher(dir, "layout.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler mark = (_, _) => Interlocked.Exchange(ref layoutDirty, 1);
        watcher.Changed += mark;
        watcher.Created += mark;
        watcher.Renamed += (_, _) => Interlocked.Exchange(ref layoutDirty, 1);
    }

    private void RebuildFromLayout()
    {
        log("layout.json changed — rebuilding items");
        foreach (var item in items)
        {
            item.Instance.Dispose();
            item.Bridge?.Dispose();
            item.Canvas.Dispose();
            item.Bitmap.Dispose();
        }
        items.Clear();
        Boot();
        // Repaint immediately, even if nothing is due yet.
        Compose();
        surface.Present(frame);
    }

    public int Boot()
    {
        var store = new LayoutStore();
        var registry = new PluginRegistry(watch: false);
        log($"data dir: {LayoutStore.DataDirectory} — {registry.Plugins.Count} plugins, {store.Layout.Items.Count} items");

        foreach (var layoutItem in store.Layout.Items)
        {
            if (!layoutItem.IsEnabled || layoutItem.Target != RenderTarget.Wallpaper) continue;
            var plugin = registry.Plugin(layoutItem.PluginId);
            if (plugin == null)
            {
                log($"[{layoutItem.PluginId}] not installed — skipped");
                continue;
            }
            var source = File.ReadAllText(plugin.SourcePath);
            var instance = PluginInstance.Boot(layoutItem.PluginId, source,
                layoutItem.PropertyOverrides, m => log($"[{layoutItem.PluginId}] {m}"),
                hostStats: systemStats);
            if (instance == null) continue;
            if (instance.Permissions.Contains("ssh"))
            {
                // Same resolution as win: alias entries lean on ~/.ssh/config,
                // manual ones carry host/port/user/key.
                instance.ConfigureSsh(layoutItem.SshHosts.Select(h => h.UsesAlias
                    ? new HostBindings.ResolvedSsh(h.Name, h.Host, 22, "", null)
                    : new HostBindings.ResolvedSsh(h.Name, h.Host, h.Port, h.User,
                        h.KeyPath.Length == 0 ? null : h.KeyPath)).ToList());
            }
            if (instance.Mode == RenderMode.Webview)
            {
                log($"[{layoutItem.PluginId}] webview mode is not supported on Linux yet — skipped");
                instance.Dispose();
                continue;
            }

            var f = layoutItem.NormalizedFrame;
            var wPx = Math.Max(8, (int)(f.W * surface.WidthPx));
            var hPx = Math.Max(8, (int)(f.H * surface.HeightPx));
            var x = (float)(f.X * surface.WidthPx);
            var y = (float)((1 - f.Y - f.H) * surface.HeightPx);

            var bitmap = new SKBitmap(new SKImageInfo(wPx, hPx, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(SKColors.Transparent);
            var canvas = new SKCanvas(bitmap);
            SkiaCanvas? bridge = null;
            if (instance.Mode == RenderMode.Canvas)
            {
                bridge = new SkiaCanvas(canvas, wPx, hPx, surface.Scale);
                var byName = instance.Properties.ToDictionary(p => p.Name, p => p.Value.BridgeValue);
                bridge.PropertyProvider = name => byName.TryGetValue(name, out var v) ? v : null;
            }

            items.Add(new Item
            {
                Layout = layoutItem,
                Instance = instance,
                Bitmap = bitmap,
                Canvas = canvas,
                Bridge = bridge,
                DestRect = SKRect.Create(x, y, wPx, hPx),
            });
            log($"[{layoutItem.PluginId}] up — {wPx}x{hPx}px at ({x:F0},{y:F0}), " +
                $"every {(double.IsPositiveInfinity(instance.RenderInterval) ? "∞" : instance.RenderInterval.ToString("F2"))}s");
        }
        return items.Count;
    }

    /// Runs until cancelled. One thread owns every Jint engine and all Skia
    /// work — the per-plugin watchdog in Core keeps a runaway plugin from
    /// freezing the loop for more than 2s.
    public void Run(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var dumped = false;
        while (!ct.IsCancellationRequested)
        {
            if (!surface.Dispatch())
            {
                log("wayland connection lost — exiting");
                return;
            }

            if (Interlocked.Exchange(ref layoutDirty, 0) == 1)
            {
                Thread.Sleep(300); // let the writer finish (LayoutStore debounces saves)
                Interlocked.Exchange(ref layoutDirty, 0);
                RebuildFromLayout();
            }

            var now = clock.Elapsed.TotalSeconds;
            var drewSomething = false;
            foreach (var item in items)
            {
                if (item.Instance.IsErrored) continue;
                item.Instance.Pump();
                var due = !item.RenderedOnce
                    || (!double.IsPositiveInfinity(item.Instance.RenderInterval) && now >= item.NextDue);
                if (!due) continue;
                if (RenderItem(item))
                {
                    item.RenderedOnce = true;
                    item.NextDue = now + item.Instance.RenderInterval;
                    drewSomething = true;
                }
                else if (item.Instance.IsErrored)
                {
                    log($"[{item.Layout.PluginId}] stopped: {item.Instance.ErrorMessage}");
                }
            }

            if (drewSomething)
            {
                Compose();
                surface.Present(frame);
                if (!dumped && Environment.GetEnvironmentVariable("DESKLAYER_DUMP_ITEM") is { Length: > 0 } dumpDir)
                {
                    DumpItems(dumpDir);
                    dumped = true;
                }
            }
            Thread.Sleep(16);
        }
    }

    /// Renders one due item into its bitmap. Returns true when new pixels
    /// were produced.
    private bool RenderItem(Item item)
    {
        if (item.Bridge != null)
        {
            item.Bridge.BeginFrame();
            return item.Instance.CallRender(item.Bridge);
        }

        // Declarative: identical trees skip the raster entirely (the win/mac
        // JSON-comparison rule).
        var json = item.Instance.CallRenderTree();
        if (json == null) return false;
        if (json == item.LastTreeJson) return item.RenderedOnce;
        var tree = ViewNode.Decode(json);
        if (tree == null) return false;
        item.LastTreeJson = json;
        item.Canvas.Clear(SKColors.Transparent);
        item.Canvas.Save();
        item.Canvas.Scale(surface.Scale);
        NodeRenderer.Render(tree, item.Canvas,
            item.Bitmap.Width / (double)surface.Scale,
            item.Bitmap.Height / (double)surface.Scale,
            m => log($"[{item.Layout.PluginId}] {m}"));
        item.Canvas.Restore();
        item.Canvas.Flush();
        return true;
    }

    private void Compose()
    {
        // layer-shell: transparent base, the compositor wallpaper shows
        // through (spike 2). X11: the window is opaque — paint the captured
        // root-pixmap wallpaper (or a solid) as base, the win model.
        if (surface.SupportsTransparency)
        {
            frameCanvas.Clear(SKColors.Transparent);
        }
        else if (surface.BaseImage is { } baseImage)
        {
            frameCanvas.DrawBitmap(baseImage, SKRect.Create(0, 0, surface.WidthPx, surface.HeightPx));
        }
        else
        {
            frameCanvas.Clear(new SKColor(24, 26, 32));
        }
        foreach (var item in items.OrderBy(i => i.Layout.ZOrder))
        {
            if (!item.RenderedOnce) continue;
            frameCanvas.DrawBitmap(item.Bitmap, item.DestRect);
        }
        frameCanvas.Flush();
    }

    /// DESKLAYER_DUMP_ITEM=<dir>: write each item's raster once, for
    /// ssh-driven verification without screen capture.
    private void DumpItems(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var item in items.Where(i => i.RenderedOnce))
        {
            using var image = SKImage.FromBitmap(item.Bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.Combine(dir, $"desklayer-{item.Layout.PluginId}-{item.Layout.Id}.png");
            using var file = File.OpenWrite(path);
            data.SaveTo(file);
            log($"dumped {path}");
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        foreach (var item in items)
        {
            item.Instance.Dispose();
            item.Bridge?.Dispose();
            item.Canvas.Dispose();
            item.Bitmap.Dispose();
        }
        frameCanvas.Dispose();
        frame.Dispose();
    }
}
