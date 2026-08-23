// Spike 2: drive the desklayer-wl C shim — an animated gradient on the
// wlr-layer-shell `bottom` layer. Run on KDE Wayland and sway; verify the
// gradient shows transparently above the compositor wallpaper and below all
// windows, at a steady frame rate with stable memory.
//
// Build the shim first:  make -C ../../native/desklayer-wl
// Run:  LD_LIBRARY_PATH=../../native/desklayer-wl dotnet run

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class Dlwl
{
    private const string Lib = "libdesklayer-wl.so";

    [LibraryImport(Lib)] public static partial int dlwl_connect();
    [LibraryImport(Lib)] public static partial int dlwl_output_count();
    [LibraryImport(Lib)] public static partial int dlwl_output_info(int i, out int widthPx, out int heightPx, out int scale);
    [LibraryImport(Lib)] public static partial nint dlwl_surface_create(int i);
    [LibraryImport(Lib)] public static partial int dlwl_buffer_acquire(nint surface, out nint pixels, out int width, out int height, out int stride);
    [LibraryImport(Lib)] public static partial void dlwl_commit(nint surface, int slot);
    [LibraryImport(Lib)] public static partial int dlwl_dispatch();
    [LibraryImport(Lib)] public static partial void dlwl_surface_destroy(nint surface);
    [LibraryImport(Lib)] public static partial void dlwl_disconnect();
}

internal static class Program
{
    private static int Main()
    {
        var rc = Dlwl.dlwl_connect();
        if (rc != 0)
        {
            Console.Error.WriteLine(rc switch
            {
                -1 => "no Wayland display",
                -3 => "compositor has no zwlr_layer_shell_v1 (GNOME?) — X11/XWayland fallback territory",
                _ => $"connect failed ({rc})",
            });
            return 1;
        }

        var outputs = Dlwl.dlwl_output_count();
        Console.WriteLine($"{outputs} output(s)");
        for (var i = 0; i < outputs; i++)
        {
            Dlwl.dlwl_output_info(i, out var w, out var h, out var scale);
            Console.WriteLine($"  output {i}: {w}x{h}px scale {scale}");
        }

        var surface = Dlwl.dlwl_surface_create(0);
        if (surface == nint.Zero)
        {
            Console.Error.WriteLine("layer surface refused");
            return 1;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_SPIKE_SECONDS"), out var s) ? s : 60;
        Console.WriteLine($"presenting on layer `bottom` for {seconds}s — check stacking + transparency now");

        var clock = Stopwatch.StartNew();
        var frames = 0;
        var skipped = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (Dlwl.dlwl_dispatch() < 0)
            {
                Console.Error.WriteLine("wayland connection lost");
                break;
            }
            var slot = Dlwl.dlwl_buffer_acquire(surface, out var pixels, out var width, out var height, out var stride);
            if (slot < 0) { skipped++; Thread.Sleep(4); continue; }
            DrawGradient(pixels, width, height, stride, clock.Elapsed.TotalSeconds);
            Dlwl.dlwl_commit(surface, slot);
            frames++;
            Thread.Sleep(16); // ~60fps target
        }

        Console.WriteLine($"{frames} frames, {skipped} skipped, {frames / clock.Elapsed.TotalSeconds:F1} fps");
        Dlwl.dlwl_surface_destroy(surface);
        Dlwl.dlwl_disconnect();
        Console.WriteLine("destroyed — compositor wallpaper should be untouched");
        return 0;
    }

    private static unsafe void DrawGradient(nint pixels, int width, int height, int stride, double t)
    {
        var p = (byte*)pixels;
        var phase = (int)(t * 60) % 512;
        for (var y = 0; y < height; y++)
        {
            var row = p + y * stride;
            // Premultiplied ARGB, half-transparent so compositor wallpaper
            // shows through — that's the transparency being verified.
            var g = (byte)(y * 127 / Math.Max(height - 1, 1));
            for (var x = 0; x < width; x++)
            {
                var b = (byte)((x + phase) * 127 / Math.Max(width - 1, 1));
                row[x * 4 + 0] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = 32;
                row[x * 4 + 3] = 128;
            }
        }
    }
}
