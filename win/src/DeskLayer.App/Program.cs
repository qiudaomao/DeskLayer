// DeskLayer for Windows — M1 walking skeleton entry point.
//
// UI thread: tray icon, wallpaper host form, attach/recovery watchdog.
// Render thread (WallpaperEngine): D2D + Jint, driven by layout.json and
// the plugins directory (%APPDATA%\DeskLayer), both hand-editable — the
// Manager UI arrives in M3.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

internal static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    public static (IntPtr target, string strategy) FindWallpaperHost()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return (IntPtr.Zero, "none");
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);

        IntPtr sibling = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                sibling = FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        if (sibling != IntPtr.Zero) return (sibling, "sibling-WorkerW");

        var child = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero) return (child, "child-WorkerW");
        return (progman, "progman-direct");
    }
}

internal static class Program
{
    private static readonly StringBuilder logBuffer = new();
    private static readonly object logGate = new();

    private static void Log(string line)
    {
        lock (logGate)
        {
            logBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            try { File.WriteAllText(Path.Combine(LayoutStore.DataDirectory, "app-log.txt"), logBuffer.ToString()); }
            catch { }
        }
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Directory.CreateDirectory(LayoutStore.DataDirectory);

        using var store = new LayoutStore();
        using var registry = new PluginRegistry();
        if (store.Layout.Items.Count == 0 && registry.Plugins.Count > 0)
            store.Update(_ => DefaultLayout(registry));

        var screen = Screen.PrimaryScreen!.Bounds;
        using var engine = new WallpaperEngine(store, registry, screen, Log);
        registry.DidChange += engine.RequestRebuild;
        store.OnChange += engine.RequestRebuild;

        // Dedicated UI-thread marshal target for WPF rasterization (the host
        // form is recreated on Explorer restarts, so it can't be the anchor).
        var uiAnchor = new Control();
        _ = uiAnchor.Handle;
        engine.PostToUi = action => uiAnchor.BeginInvoke(action);
        engine.Start();

        // Wallpaper host form + attach/recovery watchdog (M0 pattern: on
        // Explorer restart the form dies with its WorkerW parent — recreate,
        // and defer the attach until a wallpaper host exists again).
        Form? host = null;
        var attached = false;
        IntPtr attachTarget = IntPtr.Zero;

        void EnsureHost()
        {
            if (host == null || host.IsDisposed || !Native.IsWindow(host.Handle))
            {
                host?.Dispose();
                host = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = screen,
                    ShowInTaskbar = false,
                    Text = "DeskLayer Wallpaper",
                };
                _ = host.Handle; // force creation without showing
                attached = false;
                attachTarget = IntPtr.Zero;
            }
            if (!attached || !Native.IsWindow(attachTarget))
            {
                var (target, strategy) = Native.FindWallpaperHost();
                if (target == IntPtr.Zero) return; // Explorer down; retry next tick
                Native.SetParent(host.Handle, target);
                if (!host.Visible) host.Show();
                attachTarget = target;
                attached = true;
                Log($"wallpaper host attached via {strategy}");
                engine.SetHwnd(host.Handle);
            }
        }

        EnsureHost();
        var watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        watchdog.Tick += (_, _) => EnsureHost();
        watchdog.Start();

        using var tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DeskLayer",
            Visible = true,
        };
        ManagerWindow? manager = null;
        void OpenManager()
        {
            if (manager is { IsLoaded: true })
            {
                manager.Activate();
                return;
            }
            manager = new ManagerWindow(store, registry, screen);
            manager.Show();
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Manager…", null, (_, _) => OpenManager());
        menu.Items.Add("Reload", null, (_, _) => { registry.Rescan(); engine.RequestRebuild(); });
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenManager();
        if (Environment.GetEnvironmentVariable("DESKLAYER_OPEN_MANAGER") == "1") OpenManager();

        Log($"started — screen {screen.Width}x{screen.Height}, {registry.Plugins.Count} plugins, {store.Layout.Items.Count} items");
        Application.Run();
        watchdog.Stop();
        host?.Dispose();
    }

    /// First run: place every installed plugin in a two-column grid on the
    /// right half of the screen (normalized, bottom-left origin).
    private static Layout DefaultLayout(PluginRegistry registry)
    {
        var items = new List<LayoutItem>();
        var index = 0;
        foreach (var plugin in registry.Plugins)
        {
            var column = index % 2;
            var row = index / 2;
            items.Add(new LayoutItem
            {
                Id = Guid.NewGuid(),
                PluginId = plugin.Id,
                DisplayUuid = "PRIMARY",
                NormalizedFrame = new NormalizedFrame(0.52 + column * 0.24, 0.66 - row * 0.30, 0.22, 0.26),
                ZOrder = index,
            });
            index++;
        }
        return new Layout { Items = items };
    }
}
