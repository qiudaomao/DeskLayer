// Wayland layer-shell wallpaper surface, over the desklayer-wl C shim
// (linux/native/desklayer-wl). Layer `bottom`: composites transparently
// above the compositor wallpaper and below every window — proven on
// Hyprland in spike 2.
//
// The shim keeps the .NET side to a handful of imports; buffers are wl_shm
// double-buffered ARGB8888 premultiplied, which on little-endian is exactly
// SkiaSharp's Bgra8888/Premul byte order, so Present is one memcpy.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace DeskLayer.LinuxApp.Surfaces;

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

    static Dlwl()
    {
        // The shim ships next to the executable (publish copies it there).
        NativeLibrary.SetDllImportResolver(typeof(Dlwl).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return nint.Zero;
            var local = Path.Combine(AppContext.BaseDirectory, Lib);
            return File.Exists(local) ? NativeLibrary.Load(local) : NativeLibrary.Load(Lib);
        });
    }
}

public sealed class LayerShellSurface : IWallpaperSurface
{
    private readonly nint handle;

    public string BackendName => "layer-shell";
    public int WidthPx { get; }
    public int HeightPx { get; }
    public int Scale { get; }
    public bool SupportsTransparency => true;
    public SKBitmap? BaseImage => null;

    private LayerShellSurface(nint handle, int widthPx, int heightPx, int scale)
    {
        this.handle = handle;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Scale = scale;
    }

    /// Connects and claims output 0. Returns null when there is no Wayland
    /// display or the compositor lacks layer-shell (GNOME) — callers fall
    /// back to the X11 backend.
    public static LayerShellSurface? TryCreate(Action<string> log)
    {
        var rc = Dlwl.dlwl_connect();
        if (rc != 0)
        {
            log(rc switch
            {
                -1 => "wayland: no display",
                -3 => "wayland: compositor has no layer-shell (GNOME?)",
                _ => $"wayland: connect failed ({rc})",
            });
            return null;
        }
        if (Dlwl.dlwl_output_count() == 0) { log("wayland: no outputs"); return null; }
        Dlwl.dlwl_output_info(0, out var w, out var h, out var scale);
        var surface = Dlwl.dlwl_surface_create(0);
        if (surface == nint.Zero) { log("wayland: layer surface refused"); return null; }
        log($"layer-shell surface up: {w}x{h}px scale {scale}");
        return new LayerShellSurface(surface, w, h, scale <= 0 ? 1 : scale);
    }

    /// Pump compositor events; call once per frame tick.
    public bool Dispatch() => Dlwl.dlwl_dispatch() >= 0;

    /// Present a full frame. `frame` must be WidthPx×HeightPx Bgra8888
    /// premultiplied. Returns false when both buffers are busy (frame skip).
    public unsafe bool Present(SKBitmap frame)
    {
        var slot = Dlwl.dlwl_buffer_acquire(handle, out var pixels, out var width, out var height, out var stride);
        if (slot < 0) return false;
        var src = frame.GetPixels();
        var srcStride = frame.RowBytes;
        var rows = Math.Min(height, frame.Height);
        var copy = Math.Min(stride, srcStride);
        for (var y = 0; y < rows; y++)
            Buffer.MemoryCopy((void*)(src + y * srcStride), (void*)(pixels + y * stride), stride, copy);
        Dlwl.dlwl_commit(handle, slot);
        return true;
    }

    public void Dispose()
    {
        Dlwl.dlwl_surface_destroy(handle);
        Dlwl.dlwl_disconnect();
    }
}
