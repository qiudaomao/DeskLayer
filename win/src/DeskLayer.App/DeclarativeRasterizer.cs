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
    /// BGRA bytes, or null when the tree fails to decode.
    public static byte[]? Rasterize(string treeJson, int width, int height, Action<string> log)
    {
        var node = ViewNode.Decode(treeJson);
        if (node == null)
        {
            log("declarative tree failed to decode");
            return null;
        }

        var root = NodeInterpreter.Build(node, onAction: null, log);
        // Wallpaper-item defaults: readable over arbitrary wallpapers.
        TextElement.SetFontSize(root, 13);
        TextElement.SetForeground(root, Brushes.White);
        root.Width = width;
        root.Height = height;

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
