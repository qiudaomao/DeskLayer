// Draws a monochrome stacked-layers tray icon, echoing the mac menubar's
// SF Symbol "square.3.layers.3d.down.left" — three isometric layers seen at
// an angle. Rendered white for the (dark) Windows 11 notification area, which
// is the convention for tray glyphs. The colorful app.ico stays the exe icon.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeskLayer.App;

public static class TrayGlyph
{
    public static Icon Create()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Three flattened isometric diamonds stacked bottom-to-top, the
            // top one brightest — reads as a 3D stack of layers.
            const float cx = 16f, halfW = 11f, halfH = 5.2f;
            float[] centers = { 22f, 16f, 10f };
            byte[] alphas = { 150, 200, 255 };
            for (var i = 0; i < centers.Length; i++)
            {
                var cy = centers[i];
                var diamond = new[]
                {
                    new PointF(cx, cy - halfH),
                    new PointF(cx + halfW, cy),
                    new PointF(cx, cy + halfH),
                    new PointF(cx - halfW, cy),
                };
                using var fill = new SolidBrush(Color.FromArgb(alphas[i], 255, 255, 255));
                g.FillPolygon(fill, diamond);
                // A faint gap between layers keeps them legible at 16px.
                using var edge = new Pen(Color.FromArgb(90, 0, 0, 0), 1f);
                g.DrawPolygon(edge, diamond);
            }
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
