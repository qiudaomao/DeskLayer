// Spike 4 (informative only): can an Avalonia X11 window be retyped to
// _NET_WM_WINDOW_TYPE_DESKTOP after show? The production wallpaper path is
// raw surfaces either way; a "yes" here only means floating panels could
// double as wallpaper surfaces in a pinch.
//
// Run on an X11 session (or XWayland): dotnet run

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = "DeskLayer Avalonia probe",
                SystemDecorations = SystemDecorations.None,
                Background = Brushes.DarkSlateBlue,
                Width = 800,
                Height = 600,
                Content = new TextBlock
                {
                    Text = "Attempting DESKTOP retype 2s after show…",
                    Foreground = Brushes.White,
                    Margin = new Thickness(24),
                },
            };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                await Task.Delay(2000);
                var handle = window.TryGetPlatformHandle();
                if (handle is null || handle.HandleDescriptor != "XID")
                {
                    Console.WriteLine($"no X11 handle (descriptor: {handle?.HandleDescriptor ?? "none"}) — not an X11/XWayland session");
                    return;
                }
                var display = Xlib.XOpenDisplay(nint.Zero);
                var xid = handle.Handle;
                var type = Xlib.XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
                var desktopAtom = Xlib.XInternAtom(display, "_NET_WM_WINDOW_TYPE_DESKTOP", false);
                var atomType = Xlib.XInternAtom(display, "ATOM", false);
                var atoms = new[] { desktopAtom };
                var pin = GCHandle.Alloc(atoms, GCHandleType.Pinned);
                Xlib.XChangeProperty(display, xid, type, atomType, 32, 0, pin.AddrOfPinnedObject(), 1);
                pin.Free();
                // Most WMs only honor a type change across an unmap/map cycle.
                Xlib.XUnmapWindow(display, xid);
                Xlib.XMapWindow(display, xid);
                Xlib.XLowerWindow(display, xid);
                Xlib.XFlush(display);
                Console.WriteLine("retyped + remapped: observe whether the window dropped to the desktop layer");
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static partial class Xlib
{
    private const string Lib = "libX11.so.6";
    [LibraryImport(Lib)] public static partial nint XOpenDisplay(nint name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(Lib)] public static partial int XChangeProperty(nint display, nint window, nint property,
        nint type, int format, int mode, nint data, int elements);
    [LibraryImport(Lib)] public static partial int XMapWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XUnmapWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XLowerWindow(nint display, nint window);
    [LibraryImport(Lib)] public static partial int XFlush(nint display);
}
