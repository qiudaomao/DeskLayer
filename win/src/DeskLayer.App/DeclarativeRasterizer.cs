// Renders a declarative tree to premultiplied-BGRA pixels on the UI thread —
// the wallpaper path for declarative items. M0 finding: layered windows
// don't composite under WorkerW, so wallpaper widgets can't be WPF windows;
// instead the WPF visual tree is rasterized (RenderTargetBitmap) and the
// pixels composite into the D2D scene like any canvas item. Floating-window
// items (M2 follow-up) host the live interactive tree instead.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

public static class DeclarativeRasterizer
{
    /// Must run on the STA UI thread. Returns width*height*4 premultiplied
    /// BGRA bytes, or null when the tree fails to decode. `deviceScale` is
    /// device pixels per point: the tree lays out in points (so a fontSize
    /// of 13 is 13 points, mac parity) and rasterizes at the display's real
    /// pixel density instead of a 1:1 96-dpi mapping that looks tiny on a
    /// scaled 4K display.
    public static byte[]? Rasterize(string treeJson, int width, int height, double deviceScale, Action<string> log,
        bool autoSizeWidth = false, bool autoSizeHeight = false)
    {
        var node = ViewNode.Decode(treeJson);
        if (node == null)
        {
            log("declarative tree failed to decode");
            return null;
        }
        if (deviceScale <= 0) deviceScale = 1;
        var widthPts = width / deviceScale;
        var heightPts = height / deviceScale;

        var root = NodeInterpreter.Build(node, onAction: null, log);
        // Wallpaper-item defaults: readable over arbitrary wallpapers.
        TextElement.SetFontSize(root, 13);
        TextElement.SetForeground(root, Brushes.White);

        // autoSize axes follow the content's natural size instead of being
        // stretched across the item frame (mac parity: RemoteMonitor hugs
        // its server list). Content anchors top-left; the slack stays
        // transparent. Capped at the frame so it never overflows the surface.
        var usedWidth = widthPts;
        var usedHeight = heightPts;
        if (autoSizeWidth || autoSizeHeight)
        {
            root.Measure(new Size(
                autoSizeWidth ? double.PositiveInfinity : widthPts,
                autoSizeHeight ? double.PositiveInfinity : heightPts));
            if (autoSizeWidth) usedWidth = Math.Min(root.DesiredSize.Width, widthPts);
            if (autoSizeHeight) usedHeight = Math.Min(root.DesiredSize.Height, heightPts);
        }
        root.Width = usedWidth;
        root.Height = usedHeight;

        root.Measure(new Size(usedWidth, usedHeight));
        root.Arrange(new Rect(0, 0, usedWidth, usedHeight));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96 * deviceScale, 96 * deviceScale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// Writes rasterized BGRA pixels to a PNG — the headless way to inspect
    /// what a wallpaper item actually drew (a remote/locked session can't
    /// always be screen-captured). Must run on the STA UI thread.
    public static void DumpPng(byte[] pixels, int width, int height, string path, Action<string> log)
    {
        try
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
            log($"dumped item raster to {path}");
        }
        catch (Exception ex)
        {
            log($"item dump failed: {ex.Message}");
        }
    }
}
