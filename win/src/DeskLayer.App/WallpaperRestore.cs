// Fixes the desktop going blank when DeskLayer exits. Our wallpaper window is
// an opaque child of WorkerW that draws the wallpaper itself (layered windows
// don't composite under WorkerW — M0 finding). When we exit, the now-empty
// sibling WorkerW can stay on top of Explorer's own wallpaper layer, leaving
// the desktop white. Re-applying the wallpaper via SystemParametersInfo makes
// Explorer repaint and tear down the extra WorkerW — the fix Lively/Wallpaper
// Engine use too.
//
// Capture the path at startup; Restore on every exit path (graceful,
// ApplicationExit, ProcessExit). A solid-color desktop returns an empty path
// (nothing to restore); an image wallpaper — the common case — is restored.

using System.Runtime.InteropServices;

namespace DeskLayer.App;

public static class WallpaperRestore
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint param, char[] buffer, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint param, string buffer, uint winIni);

    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    private static string? capturedPath;
    private static bool restored;
    private static readonly object gate = new();

    public static void Capture()
    {
        var buffer = new char[520];
        if (SystemParametersInfoW(SPI_GETDESKWALLPAPER, (uint)buffer.Length, buffer, 0))
            capturedPath = new string(buffer).TrimEnd('\0');
    }

    /// Idempotent — safe to call from multiple exit hooks.
    public static void Restore()
    {
        lock (gate)
        {
            if (restored) return;
            restored = true;
        }
        // SPI_SETDESKWALLPAPER repaints the desktop by itself. Do NOT pass
        // SPIF_SENDWININICHANGE: that broadcasts WM_SETTINGCHANGE synchronously
        // to every top-level window, and on exit our message loop is already
        // gone — the broadcast would deadlock on our own windows (observed as
        // a hung process on quit).
        if (!string.IsNullOrEmpty(capturedPath) && System.IO.File.Exists(capturedPath))
            SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, capturedPath, SPIF_UPDATEINIFILE);
    }
}
