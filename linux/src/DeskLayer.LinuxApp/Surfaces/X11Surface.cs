// X11 wallpaper surface: a _NET_WM_WINDOW_TYPE_DESKTOP window covering the
// screen, presented with XPutImage. Serves plain X11 sessions (XFCE, KDE
// X11, GNOME Xorg) and GNOME Wayland via XWayland where layer-shell is
// unavailable. Spike 1 findings: wlroots compositors tile DESKTOP-typed X
// windows — the BackendSelector never picks this path when layer-shell
// exists.
//
// No compositor transparency here: the window is opaque, so the engine
// composites over BaseImage — the current wallpaper read from the root
// pixmap (_XROOTPMAP_ID / ESETROOT_PMAP_ID, the mechanism feh/nitrogen/
// XFCE use), falling back to a solid color. MIT-SHM presentation is a
// tracked optimization; XPutImage is fine at plugin cadences.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace DeskLayer.LinuxApp.Surfaces;

public sealed class X11Surface : IWallpaperSurface
{
    public string BackendName => "x11";
    public int WidthPx { get; }
    public int HeightPx { get; }
    public int Scale => 1; // X11 HiDPI (Xft.dpi) is a tracked follow-up
    public bool SupportsTransparency => false;
    public SKBitmap? BaseImage { get; }

    private readonly nint display;
    private readonly nint window;
    private readonly nint gc;
    private readonly nint ximage;
    private readonly nint pixels;

    private X11Surface(nint display, nint window, nint gc, nint ximage, nint pixels,
                       int width, int height, SKBitmap? baseImage)
    {
        this.display = display;
        this.window = window;
        this.gc = gc;
        this.ximage = ximage;
        this.pixels = pixels;
        WidthPx = width;
        HeightPx = height;
        BaseImage = baseImage;
    }

    public static X11Surface? TryCreate(Action<string> log)
    {
        var display = Xlib.XOpenDisplay(nint.Zero);
        if (display == nint.Zero)
        {
            log("x11: cannot open DISPLAY");
            return null;
        }
        var screen = Xlib.XDefaultScreen(display);
        var root = Xlib.XRootWindow(display, screen);
        var width = Xlib.XDisplayWidth(display, screen);
        var height = Xlib.XDisplayHeight(display, screen);
        var window = Xlib.XCreateSimpleWindow(display, root, 0, 0, (uint)width, (uint)height, 0, 0, 0);

        SetAtoms(display, window, "_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_DESKTOP");
        SetAtoms(display, window, "_NET_WM_STATE",
            "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER",
            "_NET_WM_STATE_STICKY", "_NET_WM_STATE_BELOW");
        Xlib.XStoreName(display, window, "DeskLayer");
        Xlib.XSelectInput(display, window, Xlib.ExposureMask | Xlib.StructureNotifyMask);
        Xlib.XMapWindow(display, window);
        Xlib.XLowerWindow(display, window);
        Xlib.XFlush(display);

        var gc = Xlib.XCreateGC(display, window, 0, nint.Zero);
        var stride = width * 4;
        var pixels = Marshal.AllocHGlobal(stride * height);
        var visual = Xlib.XDefaultVisual(display, screen);
        var depth = Xlib.XDefaultDepth(display, screen);
        var ximage = Xlib.XCreateImage(display, visual, (uint)depth, Xlib.ZPixmap, 0,
                                       pixels, (uint)width, (uint)height, 32, stride);
        if (ximage == nint.Zero)
        {
            log("x11: XCreateImage failed");
            Xlib.XCloseDisplay(display);
            return null;
        }

        var baseImage = ReadRootWallpaper(display, root, width, height, log);
        log($"x11 desktop window up: {width}x{height} (base wallpaper {(baseImage != null ? "captured" : "unavailable")})");
        return new X11Surface(display, window, gc, ximage, pixels, width, height, baseImage);
    }

    /// The current wallpaper: the root pixmap advertised by _XROOTPMAP_ID /
    /// ESETROOT_PMAP_ID, copied once. DEs that don't publish it (or purely
    /// compositor-drawn setups) return null and the engine paints a solid
    /// base instead.
    private static SKBitmap? ReadRootWallpaper(nint display, nint root, int width, int height, Action<string> log)
    {
        foreach (var name in new[] { "_XROOTPMAP_ID", "ESETROOT_PMAP_ID" })
        {
            var atom = Xlib.XInternAtom(display, name, true);
            if (atom == nint.Zero) continue;
            if (Xlib.XGetWindowProperty(display, root, atom, 0, 1, false, Xlib.AnyPropertyType,
                    out _, out _, out var count, out _, out var data) != 0 || data == nint.Zero || count == 0)
                continue;
            var pixmap = (nint)Marshal.ReadIntPtr(data);
            Xlib.XFree(data);
            if (pixmap == nint.Zero) continue;

            var image = Xlib.XGetImage(display, pixmap, 0, 0, (uint)width, (uint)height,
                                       ~0UL, Xlib.ZPixmap);
            if (image == nint.Zero) continue;
            try
            {
                var xi = Marshal.PtrToStructure<Xlib.XImageHeader>(image);
                if (xi.bits_per_pixel != 32 && xi.bits_per_pixel != 24) continue;
                var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                unsafe
                {
                    var dst = (byte*)bitmap.GetPixels();
                    var src = (byte*)xi.data;
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(src + y * xi.bytes_per_line, dst + y * bitmap.RowBytes,
                                          bitmap.RowBytes, Math.Min(xi.bytes_per_line, bitmap.RowBytes));
                }
                return bitmap;
            }
            finally
            {
                Xlib.XDestroyImage(image);
            }
        }
        log("x11: no root pixmap property — using solid base");
        return null;
    }

    public bool Dispatch()
    {
        while (Xlib.XPending(display) > 0)
            Xlib.XNextEvent(display, out _);
        return true;
    }

    public unsafe bool Present(SKBitmap frame)
    {
        var src = (byte*)frame.GetPixels();
        var stride = WidthPx * 4;
        var rows = Math.Min(HeightPx, frame.Height);
        for (var y = 0; y < rows; y++)
            Buffer.MemoryCopy(src + y * frame.RowBytes, (byte*)(pixels + y * stride),
                              stride, Math.Min(stride, frame.RowBytes));
        Xlib.XPutImage(display, window, gc, ximage, 0, 0, 0, 0, (uint)WidthPx, (uint)HeightPx);
        Xlib.XFlush(display);
        return true;
    }

    private static void SetAtoms(nint display, nint window, string property, params string[] values)
    {
        var propertyAtom = Xlib.XInternAtom(display, property, false);
        var atomType = Xlib.XInternAtom(display, "ATOM", false);
        var atoms = values.Select(v => Xlib.XInternAtom(display, v, false)).ToArray();
        var handle = GCHandle.Alloc(atoms, GCHandleType.Pinned);
        try
        {
            Xlib.XChangeProperty(display, window, propertyAtom, atomType, 32,
                                 Xlib.PropModeReplace, handle.AddrOfPinnedObject(), atoms.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        // Unmap so the DE repaints its own desktop, then tear down.
        Xlib.XUnmapWindow(display, window);
        Xlib.XFlush(display);
        Marshal.FreeHGlobal(pixels);
        Xlib.XCloseDisplay(display);
        BaseImage?.Dispose();
    }
}

internal static partial class Xlib
{
    private const string Lib = "libX11.so.6";

    public const long ExposureMask = 1L << 15;
    public const long StructureNotifyMask = 1L << 17;
    public const int ZPixmap = 2;
    public const int PropModeReplace = 0;
    public static readonly nint AnyPropertyType = 0;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XEvent
    {
        public int type;
        public fixed long pad[24];
    }

    /// The leading fields of XImage — enough to read geometry and the data
    /// pointer; the struct is larger but only prefix access is needed.
    [StructLayout(LayoutKind.Sequential)]
    public struct XImageHeader
    {
        public int width, height, xoffset, format;
        public nint data;
        public int byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad;
        public int depth, bytes_per_line, bits_per_pixel;
    }

    [LibraryImport(Lib)] public static partial nint XOpenDisplay(nint name);
    [LibraryImport(Lib)] public static partial int XCloseDisplay(nint display);
    [LibraryImport(Lib)] public static partial int XDefaultScreen(nint display);
    [LibraryImport(Lib)] public static partial nint XRootWindow(nint display, int screen);
    [LibraryImport(Lib)] public static partial int XDisplayWidth(nint display, int screen);
    [LibraryImport(Lib)] public static partial int XDisplayHeight(nint display, int screen);
    [LibraryImport(Lib)] public static partial nint XDefaultVisual(nint display, int screen);
    [LibraryImport(Lib)] public static partial int XDefaultDepth(nint display, int screen);
    [LibraryImport(Lib)] public static partial nint XCreateSimpleWindow(nint display, nint parent,
        int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(Lib)] public static partial int XChangeProperty(nint display, nint window, nint property,
        nint type, int format, int mode, nint data, int elements);
    [LibraryImport(Lib)] public static partial int XGetWindowProperty(nint display, nint window, nint property,
        long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, nint reqType,
        out nint actualType, out int actualFormat, out ulong itemCount, out ulong bytesAfter, out nint data);
    [LibraryImport(Lib)] public static partial int XFree(nint data);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int XStoreName(nint display, nint window, string name);
    [LibraryImport(Lib)] public static partial int XSelectInput(nint display, nint window, long mask);
    [LibraryImport(Lib)] public static partial int XMapWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XUnmapWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XLowerWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XFlush(nint display);
    [LibraryImport(Lib)] public static partial int XPending(nint display);
    [LibraryImport(Lib)] public static partial int XNextEvent(nint display, out XEvent ev);
    [LibraryImport(Lib)] public static partial nint XCreateGC(nint display, nint drawable, nuint mask, nint values);
    [LibraryImport(Lib)] public static partial nint XCreateImage(nint display, nint visual, uint depth, int format,
        int offset, nint data, uint width, uint height, int bitmapPad, int bytesPerLine);
    [LibraryImport(Lib)] public static partial int XPutImage(nint display, nint drawable, nint gc, nint image,
        int srcX, int srcY, int destX, int destY, uint width, uint height);
    [LibraryImport(Lib)] public static partial nint XGetImage(nint display, nint drawable,
        int x, int y, uint width, uint height, ulong planeMask, int format);
    [LibraryImport(Lib)] public static partial int XDestroyImage(nint image);
}
