// Spike 1 (+3): a _NET_WM_WINDOW_TYPE_DESKTOP window painting an animated
// gradient via XPutImage. Run per DE and record: does it sit above the
// wallpaper? below desktop icons? below normal windows? survive workspace
// switches? Under GNOME Wayland this same binary runs against XWayland and
// answers spike 3 (mutter's stacking of DESKTOP-type X clients).
//
// Deliberately simple: no MIT-SHM (that's an M1 optimization), one screen,
// software gradient. The point is stacking behavior, not throughput.
//
// Usage: DESKLAYER_SPIKE_SECONDS=30 dotnet run
// Findings go into linux/README.md.

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private static int Main()
    {
        var display = Xlib.XOpenDisplay(nint.Zero);
        if (display == nint.Zero)
        {
            Console.Error.WriteLine("cannot open DISPLAY");
            return 1;
        }

        var screen = Xlib.XDefaultScreen(display);
        var root = Xlib.XRootWindow(display, screen);
        var width = Xlib.XDisplayWidth(display, screen);
        var height = Xlib.XDisplayHeight(display, screen);
        Console.WriteLine($"display {width}x{height}, root=0x{root:x}");

        var visual = Xlib.XDefaultVisual(display, screen);
        var depth = Xlib.XDefaultDepth(display, screen);
        var window = Xlib.XCreateSimpleWindow(display, root, 0, 0, (uint)width, (uint)height, 0, 0, 0);

        // Type and state BEFORE mapping — most WMs only honor them at map time.
        SetAtomProperty(display, window, "_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_DESKTOP");
        SetAtomProperty(display, window, "_NET_WM_STATE",
            "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY",
            "_NET_WM_STATE_BELOW");
        Xlib.XStoreName(display, window, "DeskLayer X11 spike");

        Xlib.XSelectInput(display, window, Xlib.ExposureMask | Xlib.StructureNotifyMask | Xlib.ButtonPressMask);
        Xlib.XMapWindow(display, window);
        Xlib.XLowerWindow(display, window);
        Xlib.XFlush(display);

        var gc = Xlib.XCreateGC(display, window, 0, nint.Zero);
        var stride = width * 4;
        var pixels = Marshal.AllocHGlobal(stride * height);
        var ximage = Xlib.XCreateImage(display, visual, (uint)depth, Xlib.ZPixmap, 0,
                                       pixels, (uint)width, (uint)height, 32, stride);
        if (ximage == nint.Zero)
        {
            Console.Error.WriteLine("XCreateImage failed");
            return 1;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_SPIKE_SECONDS"), out var s) ? s : 60;
        Console.WriteLine($"animating for {seconds}s — observe stacking vs wallpaper/icons/windows now");

        var clock = Stopwatch.StartNew();
        var frames = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            // Drain events without blocking.
            while (Xlib.XPending(display) > 0)
            {
                Xlib.XNextEvent(display, out var ev);
                if (ev.type == Xlib.ButtonPress)
                    Console.WriteLine("ButtonPress reached the spike window (desktop clicks land on us)");
            }

            DrawGradient(pixels, width, height, stride, clock.Elapsed.TotalSeconds);
            Xlib.XPutImage(display, window, gc, ximage, 0, 0, 0, 0, (uint)width, (uint)height);
            Xlib.XFlush(display);
            frames++;
            Thread.Sleep(33); // ~30fps is plenty for a stacking probe
        }

        Console.WriteLine($"done: {frames} frames in {clock.Elapsed.TotalSeconds:F1}s " +
                          $"({frames / clock.Elapsed.TotalSeconds:F1} fps)");
        // Restore: unmap so the DE repaints its own desktop; report what happens.
        Xlib.XUnmapWindow(display, window);
        Xlib.XFlush(display);
        Console.WriteLine("unmapped — check that the original wallpaper is back");
        Thread.Sleep(1000);
        Xlib.XCloseDisplay(display);
        return 0;
    }

    private static unsafe void DrawGradient(nint pixels, int width, int height, int stride, double t)
    {
        var p = (byte*)pixels;
        var phase = (int)(t * 60) % 512;
        for (var y = 0; y < height; y++)
        {
            var row = p + y * stride;
            var g = (byte)(y * 255 / Math.Max(height - 1, 1));
            for (var x = 0; x < width; x++)
            {
                var b = (byte)((x + phase) * 255 / Math.Max(width - 1, 1));
                row[x * 4 + 0] = b;          // B
                row[x * 4 + 1] = g;          // G
                row[x * 4 + 2] = (byte)64;   // R
                row[x * 4 + 3] = 255;        // X (ignored at depth 24)
            }
        }
    }

    private static void SetAtomProperty(nint display, nint window, string property, params string[] values)
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
}

internal static partial class Xlib
{
    private const string Lib = "libX11.so.6";

    public const long ExposureMask = 1L << 15;
    public const long StructureNotifyMask = 1L << 17;
    public const long ButtonPressMask = 1L << 2;
    public const int ButtonPress = 4;
    public const int ZPixmap = 2;
    public const int PropModeReplace = 0;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XEvent
    {
        public int type;
        // Padded to the union's real size so XNextEvent never writes past us.
        public fixed long pad[24];
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
}
