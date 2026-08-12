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
using DeskLayer.Core;
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

        // Restore the desktop wallpaper on exit so the screen never stays
        // blank after we detach from WorkerW. The explicit restore at the end
        // of Main (after the render thread stops and our window is destroyed)
        // is the real one; ProcessExit is a last-resort safety net for
        // non-graceful terminations. Restore() is idempotent (first wins).
        WallpaperRestore.Capture();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WallpaperRestore.Restore();

        using var store = new LayoutStore();
        using var registry = new PluginRegistry();
        if (store.Layout.Items.Count == 0 && registry.Plugins.Count > 0)
            store.Update(_ => DefaultLayout(registry));

        var storeRegistry = new PluginStoreRegistry(Log);
        var pluginUpdater = new PluginUpdater(Log);
        _ = CheckPluginAutoUpdates(registry, pluginUpdater); // fire-and-forget on launch

        var screen = Screen.PrimaryScreen!.Bounds;
        var engine = new WallpaperEngine(store, registry, screen, Log);
        registry.DidChange += engine.RequestRebuild;
        store.OnChange += engine.RequestRebuild;

        // Dedicated UI-thread marshal target for WPF rasterization (the host
        // form is recreated on Explorer restarts, so it can't be the anchor).
        var uiAnchor = new Control();
        _ = uiAnchor.Handle;
        engine.PostToUi = action =>
        {
            // Teardown order: the message loop ends (destroying this anchor's
            // handle) before engine.Dispose() runs, so the render thread's
            // last marshals arrive too late. Dropping them is right — the
            // windows they would touch are going away with the process — but
            // throwing would abort the rest of the engine's cleanup.
            if (!uiAnchor.IsHandleCreated || uiAnchor.IsDisposed) return;
            try { uiAnchor.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        };

        // Power/session policy (lock/suspend/battery-saver → pause/throttle).
        var power = new PowerController(Log);
        engine.SetPolicy(power.Policy);
        power.DidWake += () => { engine.SetPolicy(power.Policy); engine.RequestRebuild(); };
        var policyPoll = new System.Windows.Forms.Timer { Interval = 2000 };
        policyPoll.Tick += (_, _) => engine.SetPolicy(power.Refresh()); // catches battery-saver toggles
        policyPoll.Start();

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

        using var trayGlyph = TrayGlyphSafe();
        using var tray = new NotifyIcon
        {
            Icon = trayGlyph ?? System.Drawing.SystemIcons.Application,
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
            try
            {
                manager = new ManagerWindow(store, registry, storeRegistry, pluginUpdater, screen,
                    reopenToggled: () => { manager = null; OpenManager(); });
                // A modeless WPF window on a WinForms message loop gets no
                // keyboard input (typing dead, paste-by-mouse fine) unless
                // keyboard interop is enabled for it.
                System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(manager);
                manager.Show();
            }
            catch (Exception ex)
            {
                manager = null;
                Log($"manager failed to open: {ex}");
            }
        }

        // DESKLAYER_FEED_URL points the updater at a test feed, so the whole
        // download → install → relaunch path can be rehearsed before a
        // release goes out. Unset in normal use.
        var feedOverride = Environment.GetEnvironmentVariable("DESKLAYER_FEED_URL");
        using var updater = new UpdateController(
            string.IsNullOrWhiteSpace(feedOverride) ? null : feedOverride, log: Log);

        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Manager…"), null, (_, _) => OpenManager());
        menu.Items.Add(L.T("Reload"), null, (_, _) => { registry.Rescan(); engine.RequestRebuild(); });
        menu.Items.Add(L.T("About DeskLayer"), null, (_, _) =>
        {
            MessageBox.Show(
                $"DeskLayer {UpdateController.DisplayVersion}\n\n" +
                L.T("Plugins live in {0}", PluginRegistry.PluginsDirectory),
                L.T("About DeskLayer"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        // Where the mac menu bar keeps it: after the actions, before the
        // settings toggles. Reuses the Manager folder button's string.
        menu.Items.Add(L.T("Open plugins folder"), null, (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", PluginRegistry.PluginsDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { Log($"opening the plugins folder failed: {ex.Message}"); }
        });
        var startup = new ToolStripMenuItem(L.T("Start with Windows")) { Checked = LoginItem.IsEnabled };
        startup.Click += (_, _) => { LoginItem.SetEnabled(!startup.Checked); startup.Checked = LoginItem.IsEnabled; };
        menu.Items.Add(startup);
        menu.Items.Add(L.T("Check for updates…"), null, async (_, _) =>
        {
            try { await updater.CheckAtUserRequest(); }
            catch (Exception ex) { Log($"update check failed: {ex.Message}"); }
        });
        menu.Items.Add(L.T("Exit"), null, (_, _) => Application.Exit());
        tray.ContextMenuStrip = menu;
        // Debug: the tray menu's labels. A NotifyIcon's menu is its own
        // window — it appears in no screenshot and in no automation tree
        // until the user opens it, so this is the only way to check it.
        if (Environment.GetEnvironmentVariable("DESKLAYER_DUMP_TRAY") is { Length: > 0 } trayDump)
        {
            try
            {
                var labels = menu.Items.OfType<ToolStripItem>()
                    .Select(item => (item.Enabled ? "" : "(disabled) ") + item.Text);
                File.WriteAllText(trayDump, string.Join("\n", labels) + "\n");
            }
            catch (Exception ex) { Log($"tray dump failed: {ex.Message}"); }
        }
        tray.DoubleClick += (_, _) => OpenManager();
        // Deferred so it opens once the message loop is pumping — a WPF window
        // shown before Application.Run never renders its first frame.
        if (Environment.GetEnvironmentVariable("DESKLAYER_OPEN_MANAGER") == "1")
        {
            var openTimer = new System.Windows.Forms.Timer { Interval = 400 };
            openTimer.Tick += (_, _) => { openTimer.Stop(); OpenManager(); };
            openTimer.Start();
        }

        // Quiet launch check (unless disabled for scripted runs).
        if (Environment.GetEnvironmentVariable("DESKLAYER_NO_UPDATE_CHECK") != "1")
        {
            try { updater.StartQuietCheck(); }
            catch (Exception ex) { Log($"update loop failed to start: {ex.Message}"); }
        }

        // Test hook: graceful auto-exit after N seconds (verifies the
        // wallpaper-restore path without a UI click).
        if (int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_EXIT_AFTER"), out var exitAfter) && exitAfter > 0)
        {
            var exitTimer = new System.Windows.Forms.Timer { Interval = exitAfter * 1000 };
            exitTimer.Tick += (_, _) => { exitTimer.Stop(); Application.Exit(); };
            exitTimer.Start();
        }

        Log($"started — DeskLayer {UpdateController.DisplayVersion}, screen {screen.Width}x{screen.Height}, "
            + $"{registry.Plugins.Count} plugins, {store.Layout.Items.Count} items");
        Application.Run();
        // Exit in order: stop the render thread (stops presenting to the
        // wallpaper HWND), destroy our window, THEN repaint the real wallpaper.
        watchdog.Stop();
        engine.Dispose();
        host?.Dispose();
        WallpaperRestore.Restore();
    }

    /// On launch, update every plugin the user marked auto-update.
    private static async Task CheckPluginAutoUpdates(PluginRegistry registry, PluginUpdater updater)
    {
        var updated = false;
        foreach (var plugin in registry.Plugins.ToList())
        {
            if (!updater.IsAutoUpdate(plugin.Id)) continue;
            try
            {
                var result = await updater.Check(plugin.Id, File.ReadAllText(plugin.SourcePath), plugin.SourcePath);
                if (result.Outcome == UpdateOutcome.Updated) { updated = true; Log($"[{plugin.Id}] {result.Message}"); }
            }
            catch (Exception ex) { Log($"[{plugin.Id}] auto-update failed: {ex.Message}"); }
        }
        if (updated) registry.Rescan();
    }

    /// The monochrome stacked-layers tray glyph (mac menubar style).
    private static System.Drawing.Icon? TrayGlyphSafe()
    {
        try { return TrayGlyph.Create(); }
        catch { return null; }
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
