// DeskLayer for Linux — entry point (M1 walking skeleton).
//
// Backends: Wayland layer-shell (proven on Hyprland) with X11 DESKTOP-type
// fallback (plain X11 sessions and GNOME Wayland via XWayland). Override
// with DESKLAYER_WALLPAPER_BACKEND=x11|layer-shell.
//
// Env hooks (win-port tradition, they all paid off there):
//   DESKLAYER_DATA_DIR      data directory (default ~/.config/DeskLayer)
//   DESKLAYER_DUMP_ITEM     directory: write each item's raster once as PNG
//   DESKLAYER_EXIT_AFTER    seconds: exit on a timer (headless verification)

using DeskLayer.LinuxApp;
using DeskLayer.LinuxApp.Surfaces;

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";
void Log(string message) => Console.WriteLine($"[desklayer] {message}");

// The Manager is its own process (the engine runs as a service); both edit
// the same layout.json, which the engine watches.
if (args.Contains("--manager"))
    return DeskLayer.LinuxApp.Ui.ManagerApp.Run(args.Where(a => a != "--manager").ToArray());

Log($"DeskLayer for Linux {version} starting");

var surface = BackendSelector.Create(Log);
if (surface == null)
{
    Log("no usable wallpaper surface (need Wayland layer-shell or an X11 session)");
    return 1;
}
Log($"backend: {surface.BackendName}");

using var surfaceLifetime = surface;
using var engine = new WallpaperEngine(surface, Log);
engine.WatchLayout();
var count = engine.Boot();
if (count == 0)
{
    Log("nothing to render — add items to layout.json (wire-compatible with mac/win)");
    return 0;
}

// Not `using`: ProcessExit fires after Main returns, and cancelling a
// disposed source throws. The process is ending; leak it deliberately.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
if (double.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_EXIT_AFTER"), out var seconds) && seconds > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(seconds));

Log($"{count} item(s) running — Ctrl-C to exit (surface teardown restores the desktop)");
engine.Run(cts.Token);
Log("bye");
return 0;
