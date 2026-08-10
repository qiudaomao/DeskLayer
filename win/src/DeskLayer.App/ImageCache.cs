// Loads and caches a .deskplugin folder's images for ctx.drawImage — the
// Windows twin of the mac ItemRenderer.ImageCache. Lookup is by file name
// within the folder only (no path traversal). Bitmaps are created on the
// render thread's device context and live as long as the cache.

using System.IO;
using System.Drawing.Imaging;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace DeskLayer.App;

public sealed class ImageCache : IDisposable
{
    private readonly ID2D1DeviceContext dc;
    private readonly string directory;
    private readonly Dictionary<string, ID2D1Bitmap?> cache = new();

    public ImageCache(ID2D1DeviceContext dc, string directory)
    {
        this.dc = dc;
        this.directory = directory;
    }

    public ID2D1Bitmap? Image(string name)
    {
        if (cache.TryGetValue(name, out var hit)) return hit;
        ID2D1Bitmap? loaded = null;
        try
        {
            var fileName = Path.GetFileName(name); // strip any path
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                using var gdi = new System.Drawing.Bitmap(path);
                using var converted = gdi.Clone(
                    new System.Drawing.Rectangle(0, 0, gdi.Width, gdi.Height), PixelFormat.Format32bppPArgb);
                var data = converted.LockBits(
                    new System.Drawing.Rectangle(0, 0, converted.Width, converted.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    loaded = dc.CreateBitmap(new System.Drawing.Size(converted.Width, converted.Height),
                        data.Scan0, data.Stride, new BitmapProperties(
                            new Vortice.DCommon.PixelFormat(
                                Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                            96, 96));
                }
                finally
                {
                    converted.UnlockBits(data);
                }
            }
        }
        catch
        {
            loaded = null; // unreadable asset → nothing drawn
        }
        cache[name] = loaded;
        return loaded;
    }

    public void Dispose()
    {
        foreach (var bitmap in cache.Values) bitmap?.Dispose();
        cache.Clear();
    }
}
