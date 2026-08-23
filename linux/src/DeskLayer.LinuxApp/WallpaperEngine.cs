// The Linux wallpaper engine v0 (M1 walking skeleton): loads the
// wire-compatible layout.json, boots canvas-mode plugins on Jint (Core),
// renders each into a persistent per-item SKBitmap on its declared cadence,
// composites onto a transparent base (layer-shell shows the compositor's
// own wallpaper through), and presents full frames to the surface.
//
// Reference: win/src/DeskLayer.App/WallpaperEngine.cs (1198 LOC). This v0
// carries only what M1 needs; declarative/floating/reconcile land in M2+.
//
// v0 simplifications (tracked):
// - single output; every enabled wallpaper item renders regardless of its
//   DisplayUuid (mac/win layouts carry their own display ids).
// - no file watching, no live reconcile: layout is read once at start.

using System.Diagnostics;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;
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
        public required SkiaCanvas Bridge;
        public SKRect DestRect;
        public double NextDue;
        public bool RenderedOnce;
    }

    private readonly LayerShellSurface surface;
    private readonly Action<string> log;
    private readonly List<Item> items = new();
    private readonly SKBitmap frame;
    private readonly SKCanvas frameCanvas;

    public WallpaperEngine(LayerShellSurface surface, Action<string> log)
    {
        this.surface = surface;
        this.log = log;
        frame = new SKBitmap(new SKImageInfo(surface.WidthPx, surface.HeightPx,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        frameCanvas = new SKCanvas(frame);
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
                layoutItem.PropertyOverrides, m => log($"[{layoutItem.PluginId}] {m}"));
            if (instance == null) continue;
            if (instance.Mode != RenderMode.Canvas)
            {
                log($"[{layoutItem.PluginId}] {instance.Mode} mode — lands in M2, skipped");
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
            var bridge = new SkiaCanvas(canvas, wPx, hPx, surface.Scale);
            var byName = instance.Properties.ToDictionary(p => p.Name, p => p.Value.BridgeValue);
            bridge.PropertyProvider = name => byName.TryGetValue(name, out var v) ? v : null;

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

            var now = clock.Elapsed.TotalSeconds;
            var drewSomething = false;
            foreach (var item in items)
            {
                if (item.Instance.IsErrored) continue;
                item.Instance.Pump();
                var due = !item.RenderedOnce
                    || (!double.IsPositiveInfinity(item.Instance.RenderInterval) && now >= item.NextDue);
                if (!due) continue;
                item.Bridge.BeginFrame();
                if (item.Instance.CallRender(item.Bridge))
                {
                    item.RenderedOnce = true;
                    item.NextDue = now + item.Instance.RenderInterval;
                    drewSomething = true;
                }
                else
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

    private void Compose()
    {
        // Transparent base: the compositor wallpaper shows through the
        // layer-bottom surface (spike 2). Items composite in z-order.
        frameCanvas.Clear(SKColors.Transparent);
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
        foreach (var item in items)
        {
            item.Instance.Dispose();
            item.Bridge.Dispose();
            item.Canvas.Dispose();
            item.Bitmap.Dispose();
        }
        frameCanvas.Dispose();
        frame.Dispose();
    }
}
