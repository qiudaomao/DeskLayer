// The wallpaper presentation contract both backends implement. The engine
// renders full frames in Bgra8888 premultiplied and neither knows nor cares
// whether they land on a Wayland layer surface or an X11 DESKTOP window.

using SkiaSharp;

namespace DeskLayer.LinuxApp.Surfaces;

public interface IWallpaperSurface : IDisposable
{
    string BackendName { get; }
    int WidthPx { get; }
    int HeightPx { get; }
    int Scale { get; }
    /// True when the compositor shows its own wallpaper through transparent
    /// pixels (layer-shell). False means the engine must paint a base layer
    /// itself (X11: the root pixmap, read once at startup).
    bool SupportsTransparency { get; }
    /// The base image to composite under items when transparency is
    /// unsupported; null = solid fallback.
    SKBitmap? BaseImage { get; }
    /// Pump display-server events; false = connection lost.
    bool Dispatch();
    bool Present(SKBitmap frame);
}

public static class BackendSelector
{
    /// layer-shell when the compositor offers it, X11 otherwise (covers
    /// plain X11 sessions and GNOME Wayland via XWayland). Overridden with
    /// DESKLAYER_WALLPAPER_BACKEND=x11|layer-shell.
    public static IWallpaperSurface? Create(Action<string> log)
    {
        var forced = Environment.GetEnvironmentVariable("DESKLAYER_WALLPAPER_BACKEND");
        if (forced is "x11") return X11Surface.TryCreate(log);
        if (forced is "layer-shell") return LayerShellSurface.TryCreate(log);

        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
        {
            var wayland = LayerShellSurface.TryCreate(log);
            if (wayland != null) return wayland;
            log("falling back to X11 (XWayland)");
        }
        return X11Surface.TryCreate(log);
    }
}
