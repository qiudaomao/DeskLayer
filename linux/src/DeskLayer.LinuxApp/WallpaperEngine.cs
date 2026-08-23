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
using DeskLayer.Core;
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
        public double NextSnapshotDue;
        /// Declared metadata (autoSize axes, limits), read once at spawn —
        /// declarative items only, canvas plugins draw whatever they like.
        public PluginMetadata.PluginInfo? Info;
    }

    private readonly IWallpaperSurface surface;
    private readonly Action<string> log;
    private readonly List<Item> items = new();
    private readonly SKBitmap frame;
    private readonly SKCanvas frameCanvas;
    private readonly SystemStatsBinding systemStats = new();
    private FileSystemWatcher? watcher;
    private long layoutDirty;
    /// The engine's view of layout.json — replaced on every rebuild so it
    /// reads fresh from disk, kept as a field so a pending debounced save
    /// (content-size adoption) can't be garbage-collected away.
    private LayoutStore? layoutStore;

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
        // Geometry-only edits (Manager drags, content-size adoption) keep
        // every plugin instance alive — a full rebuild resets JS state,
        // which for a state-dependent auto-size plugin oscillates forever:
        // fresh boot renders the small "connecting" tree, adopts small,
        // rebuild, the connected tree adopts big, rebuild, repeat.
        var fresh = new LayoutStore();
        if (TryGeometryOnlyUpdate(fresh))
        {
            log("layout.json changed — geometry updated in place");
            layoutStore?.Dispose();
            layoutStore = fresh;
            Compose();
            surface.Present(frame);
            return;
        }
        fresh.Dispose();

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
        PruneSnapshots();
        // Repaint immediately, even if nothing is due yet.
        Compose();
        surface.Present(frame);
    }

    public int Boot()
    {
        layoutStore?.Dispose();
        var store = layoutStore = new LayoutStore();
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

            PluginMetadata.PluginInfo? info = null;
            if (instance.Mode == RenderMode.Declarative)
            {
                try { info = PluginMetadata.ExtractInfo(source); }
                catch { }
            }

            items.Add(new Item
            {
                Layout = layoutItem,
                Instance = instance,
                Bitmap = bitmap,
                Canvas = canvas,
                Bridge = bridge,
                DestRect = SKRect.Create(x, y, wPx, hPx),
                Info = info,
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
        var pausedSentinel = Path.Combine(LayoutStore.DataDirectory, ".paused");
        var paused = false;
        var pausedCheck = 0.0;
        while (!ct.IsCancellationRequested)
        {
            if (!surface.Dispatch())
            {
                log("wayland connection lost — exiting");
                return;
            }

            // Tray "Pause Wallpaper" drops a sentinel file; the wallpaper
            // freezes on its last frame (no teardown — resume is instant).
            var tick = clock.Elapsed.TotalSeconds;
            if (tick >= pausedCheck)
            {
                pausedCheck = tick + 1;
                var exists = File.Exists(pausedSentinel);
                if (exists != paused)
                {
                    paused = exists;
                    log(paused ? "paused (sentinel present)" : "resumed");
                }
            }
            if (paused)
            {
                Thread.Sleep(250);
                continue;
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
                WriteSnapshots(now);
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
        // DESKLAYER_DUMP_TREE=<dir>: latest tree JSON per item, for
        // measuring the exact same tree off-box when layout math misbehaves.
        if (Environment.GetEnvironmentVariable("DESKLAYER_DUMP_TREE") is { Length: > 0 } treeDir)
        {
            try
            {
                Directory.CreateDirectory(treeDir);
                File.WriteAllText(Path.Combine(treeDir, $"{item.Layout.PluginId}.json"), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Content-driven axes: measure first, and if the frame must change,
        // write it back and let the rebuild respawn at the right size —
        // drawing now would just paint the clipped frame again.
        if (item.Info is { } info && (info.AutoSizeWidth || info.AutoSizeHeight)
            && AdoptContentSize(item, tree, info))
            return false;
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

    /// True when the fresh layout differs from the running items only in
    /// geometry-safe ways (frame, z-order, background) — applied in place,
    /// instances kept alive. Anything else falls back to the full rebuild.
    private bool TryGeometryOnlyUpdate(LayoutStore fresh)
    {
        var incoming = fresh.Layout.Items
            .Where(i => i.IsEnabled && i.Target == RenderTarget.Wallpaper)
            .ToList();
        if (incoming.Count != items.Count) return false;
        var byId = items.ToDictionary(i => i.Layout.Id);
        foreach (var layoutItem in incoming)
        {
            if (!byId.TryGetValue(layoutItem.Id, out var item)) return false;
            var old = item.Layout;
            if (old.PluginId != layoutItem.PluginId) return false;
            if (!OverridesEqual(old.PropertyOverrides, layoutItem.PropertyOverrides)) return false;
            if (!SshEqual(old.SshHosts, layoutItem.SshHosts)) return false;
        }
        foreach (var layoutItem in incoming)
            ApplyGeometry(byId[layoutItem.Id], layoutItem);
        return true;
    }

    private static bool OverridesEqual(IReadOnlyDictionary<string, PropertyValue> a,
        IReadOnlyDictionary<string, PropertyValue> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (name, value) in a)
        {
            if (!b.TryGetValue(name, out var other)) return false;
            if (value != other) return false;   // record struct: value equality
        }
        return true;
    }

    private static bool SshEqual(IReadOnlyList<SshConfig> a, IReadOnlyList<SshConfig> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Id != y.Id || x.Name != y.Name || x.Host != y.Host
                || x.Port != y.Port || x.User != y.User || x.KeyPath != y.KeyPath
                || x.UsesAlias != y.UsesAlias) return false;
        }
        return true;
    }

    /// Moves/resizes a live item. A size change swaps the raster surface
    /// (and canvas bridge) but never the plugin instance — JS state, ssh
    /// connections, timers all survive; the next loop tick re-renders.
    private void ApplyGeometry(Item item, LayoutItem layoutItem)
    {
        var f = layoutItem.NormalizedFrame;
        var wPx = Math.Max(8, (int)(f.W * surface.WidthPx));
        var hPx = Math.Max(8, (int)(f.H * surface.HeightPx));
        var x = (float)(f.X * surface.WidthPx);
        var y = (float)((1 - f.Y - f.H) * surface.HeightPx);
        item.Layout = layoutItem;
        item.DestRect = SKRect.Create(x, y, wPx, hPx);
        if (wPx == item.Bitmap.Width && hPx == item.Bitmap.Height) return;

        item.Bridge?.Dispose();
        item.Canvas.Dispose();
        item.Bitmap.Dispose();
        item.Bitmap = new SKBitmap(new SKImageInfo(wPx, hPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        item.Bitmap.Erase(SKColors.Transparent);
        item.Canvas = new SKCanvas(item.Bitmap);
        item.Bridge = null;
        if (item.Instance.Mode == RenderMode.Canvas)
        {
            item.Bridge = new SkiaCanvas(item.Canvas, wPx, hPx, surface.Scale);
            var byName = item.Instance.Properties.ToDictionary(p => p.Name, p => p.Value.BridgeValue);
            item.Bridge.PropertyProvider = name => byName.TryGetValue(name, out var v) ? v : null;
        }
        item.LastTreeJson = null;
        item.RenderedOnce = false;
    }

    /// Grows (or shrinks) an item to its content's natural size on the axes
    /// the plugin declares content-driven — the win/mac adoptContentSize.
    /// The top edge stays put (frames are stored bottom-left, so the stored
    /// y moves with the height); limits still apply; the store write-back is
    /// what makes the Manager's overview agree with the desktop. Returns
    /// true when a resize was written: the debounced save lands in
    /// layout.json, the watcher fires, and the rebuild respawns the item at
    /// the new size (the v1 rebuild model doubles as the surface resize).
    private bool AdoptContentSize(Item item, ViewNode tree, PluginMetadata.PluginInfo info)
    {
        if (layoutStore == null || surface.WidthPx <= 0 || surface.HeightPx <= 0) return false;

        var widthPts = item.Bitmap.Width / (double)surface.Scale;
        var heightPts = item.Bitmap.Height / (double)surface.Scale;
        var natural = NodeRenderer.MeasureNatural(tree, widthPts, heightPts,
            info.AutoSizeWidth, info.AutoSizeHeight, m => log($"[{item.Layout.PluginId}] {m}"));
        var (limitedW, limitedH) = info.Clamp(natural.Width, natural.Height);

        var frame = item.Layout.NormalizedFrame;
        var wanted = new NormalizedFrame(
            frame.X, frame.Y,
            info.AutoSizeWidth ? Math.Min(limitedW * surface.Scale / surface.WidthPx, 1) : frame.W,
            info.AutoSizeHeight ? Math.Min(limitedH * surface.Scale / surface.HeightPx, 1) : frame.H);

        // A point either way is invisible and would ping-pong with rounding.
        var epsilonW = 1.0 * surface.Scale / surface.WidthPx;
        var epsilonH = 1.0 * surface.Scale / surface.HeightPx;
        if (Math.Abs(wanted.W - frame.W) < epsilonW && Math.Abs(wanted.H - frame.H) < epsilonH) return false;

        var top = frame.Y + frame.H;
        var updated = wanted with { Y = Math.Max(top - wanted.H, 0) };
        var itemId = item.Layout.Id;
        item.Layout = item.Layout with { NormalizedFrame = updated };
        layoutStore.Update(layout => layout with
        {
            Items = layout.Items
                .Select(i => i.Id == itemId ? i with { NormalizedFrame = updated } : i)
                .ToList(),
        });
        log($"[{item.Layout.PluginId}] adopted content size {limitedW:F0}×{limitedH:F0} pt");
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
            if (item.Layout.BackgroundColor is { Length: > 0 } bg && Css.TryParse(bg, out var color))
            {
                using var paint = new SKPaint { Color = color, IsAntialias = true };
                frameCanvas.DrawRoundRect(item.DestRect, 8, 8, paint);
            }
            frameCanvas.DrawBitmap(item.Bitmap, item.DestRect);
        }
        frameCanvas.Flush();
    }

    /// Per-item live snapshots for the Manager's desktop overview (the mac
    /// behavior). File-based like the rest of the engine↔Manager channel:
    /// throttled PNGs in <data>/.snapshots/<itemId>.png, written atomically
    /// so the Manager never reads a half-encoded file.
    public static string SnapshotsDirectory => Path.Combine(LayoutStore.DataDirectory, ".snapshots");

    private void WriteSnapshots(double now)
    {
        foreach (var item in items)
        {
            if (!item.RenderedOnce || now < item.NextSnapshotDue) continue;
            item.NextSnapshotDue = now + 2;
            try
            {
                Directory.CreateDirectory(SnapshotsDirectory);
                var path = Path.Combine(SnapshotsDirectory, $"{item.Layout.Id}.png");
                using (var image = SKImage.FromBitmap(item.Bitmap))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 80))
                using (var file = File.Create(path + ".tmp"))
                    data.SaveTo(file);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// Snapshots for items that no longer exist would show stale ghosts in
    /// the overview — drop everything the current layout doesn't carry.
    private void PruneSnapshots()
    {
        try
        {
            if (!Directory.Exists(SnapshotsDirectory)) return;
            var live = items.Select(i => $"{i.Layout.Id}.png").ToHashSet();
            foreach (var file in Directory.GetFiles(SnapshotsDirectory))
                if (!live.Contains(Path.GetFileName(file)))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        layoutStore?.Dispose();
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
