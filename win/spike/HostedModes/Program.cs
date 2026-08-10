// DeskLayer M0 spike — declarative/webview feasibility + Explorer recovery.
//
// Proves the two retained-mode hosts work under the wallpaper layer:
//   - a WPF window (transparent, rounded card with a live-updating clock —
//     the shape a declarative-mode widget will take)
//   - a WPF window hosting WebView2 navigated to a real page
// and that the app can RECOVER from an Explorer restart: killing Explorer
// destroys WorkerW and every window reparented under it, so recovery means
// detecting the death and recreating + re-attaching the windows. A 1s
// watchdog does exactly that; hosted-log.txt records each recreation.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

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
        if (progman == IntPtr.Zero) return (IntPtr.Zero, "none: no Progman");
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);

        IntPtr sibling = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                sibling = FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        if (sibling != IntPtr.Zero) return (sibling, "1:sibling-WorkerW");

        var child = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero) return (child, "2:child-WorkerW");
        return (progman, "3:progman-direct");
    }
}

/// A wallpaper-layer slot: builds its window, attaches it, and recreates the
/// pair when Explorer's restart destroys the parent WorkerW (and us with it).
internal sealed class HostedSlot
{
    private readonly Func<Window> factory;
    private readonly Action<string> log;
    private readonly string name;
    private Window? window;
    private IntPtr handle;
    private IntPtr target;
    public int Recreations { get; private set; }

    public HostedSlot(string name, Func<Window> factory, Action<string> log)
    {
        this.name = name;
        this.factory = factory;
        this.log = log;
        Create();
    }

    private void Create()
    {
        window = factory();
        window.Show();
        handle = new WindowInteropHelper(window).Handle;
        target = IntPtr.Zero;
        TryAttach();
    }

    /// Right after an Explorer restart there is no Progman/WorkerW to attach
    /// to yet — leave `target` unset and let the watchdog retry next tick.
    private void TryAttach()
    {
        var (found, strategy) = Native.FindWallpaperHost();
        if (found == IntPtr.Zero)
        {
            log($"{name}: attach deferred — no wallpaper host yet");
            return;
        }
        Native.SetParent(handle, found);
        target = found;
        log($"{name}: attached 0x{handle:X8} via {strategy}");
    }

    /// Call every second: recreate if Explorer took the window down with its
    /// WorkerW; (re)attach once a wallpaper host exists again.
    public void CheckAlive()
    {
        if (!Native.IsWindow(handle))
        {
            Recreations++;
            log($"{name}: window destroyed (Explorer restart?) — recreating (#{Recreations})");
            try { window?.Close(); } catch { }
            Create();
            return;
        }
        if (target == IntPtr.Zero || !Native.IsWindow(target)) TryAttach();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "hosted-log.txt");
        var log = new StringBuilder();
        void Log(string line)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            File.WriteAllText(logPath, log.ToString());
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Opaque vs layered: AllowsTransparency makes a WS_EX_LAYERED window,
        // which does NOT composite under WorkerW (found empirically) — keep
        // both variants so the run records the difference.
        var cardSlot = new HostedSlot("wpf-card-opaque", () => MakeCard(layered: false, top: 70), Log);
        var layeredSlot = new HostedSlot("wpf-card-layered", () => MakeCard(layered: true, top: 240), Log);
        var webSlot = new HostedSlot("webview2", MakeWeb, Log);

        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watchdog.Tick += (_, _) => { cardSlot.CheckAlive(); layeredSlot.CheckAlive(); webSlot.CheckAlive(); };
        watchdog.Start();

        var quit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        quit.Tick += (_, _) =>
        {
            Log($"exit: card-recreations={cardSlot.Recreations} web-recreations={webSlot.Recreations}");
            app.Shutdown();
        };
        quit.Start();

        Log("started");
        app.Run();
    }

    /// The declarative-widget shape: rounded card with a live clock.
    /// Coordinates are DIPs — this display runs 125% scale, so ×1.25 for px.
    private static Window MakeCard(bool layered, double top)
    {
        var time = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
        };
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        tick.Tick += (_, _) => time.Text = DateTime.Now.ToString("HH:mm:ss");
        tick.Start();

        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 60, Foreground = Brushes.LimeGreen };
        var barTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        barTick.Tick += (_, _) => bar.Value = DateTime.Now.Second;
        barTick.Start();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = layered ? "WPF layered (transparent)" : "WPF opaque host",
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(time);
        panel.Children.Add(bar);

        return new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = layered,
            Background = layered ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 1150, Top = top, Width = 280, Height = 140,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(16),
                Child = panel,
            },
        };
    }

    private static Window MakeWeb()
    {
        var webView = new WebView2 { Source = new Uri("https://example.com") };
        return new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 1150, Top = 420, Width = 380, Height = 280,
            Content = webView,
        };
    }
}
