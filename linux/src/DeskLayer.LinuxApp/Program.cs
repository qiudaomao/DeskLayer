// DeskLayer for Linux — entry point (M1 walking skeleton).
//
// v0 backend: Wayland layer-shell only (proven on Hyprland, spike 2). The
// X11 backend and the Avalonia manager arrive in later milestones; when the
// compositor has no layer-shell this exits with a clear message instead of
// guessing.
//
// Env hooks (win-port tradition, they all paid off there):
//   DESKLAYER_DATA_DIR      data directory (default ~/.config/DeskLayer)
//   DESKLAYER_DUMP_ITEM     directory: write each item's raster once as PNG
//   DESKLAYER_EXIT_AFTER    seconds: exit on a timer (headless verification)

using DeskLayer.LinuxApp;
using DeskLayer.LinuxApp.Surfaces;

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";
void Log(string message) => Console.WriteLine($"[desklayer] {message}");
Log($"DeskLayer for Linux {version} starting");

var surface = LayerShellSurface.TryCreate(Log);
if (surface == null)
{
    Log("no usable wallpaper surface (layer-shell required in M1; X11 backend lands next)");
    return 1;
}

using var _ = surface;
using var engine = new WallpaperEngine(surface, Log);
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
