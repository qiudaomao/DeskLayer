// DeskLayer M0 spike — wallpaper-layer attach proof.
//
// Creates a borderless window with a live ticking clock, reparents it into
// the WorkerW behind SHELLDLL_DefView (strategy chain: sibling WorkerW →
// child WorkerW → Progman direct), and logs a self-check. Runs ~30s then
// exits so a screenshot taken meanwhile can verify it renders *behind* the
// desktop icons yet *above* the wallpaper, updating live.

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    // GetParent returns null for a reparented window without WS_CHILD;
    // GetAncestor(GA_PARENT) reports the real parent either way.
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GaParent = 1;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, 256);
        return sb.ToString();
    }

    /// Strategy chain from the port plan. Returns the attach target and which
    /// strategy produced it.
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

internal sealed class SpikeForm : Form
{
    private readonly Label clock;

    public SpikeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(200, 150, 600, 400);
        BackColor = Color.Magenta;
        ShowInTaskbar = false;

        clock = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 32, FontStyle.Bold),
            ForeColor = Color.White,
            Text = "DeskLayer M0",
        };
        Controls.Add(clock);

        var tick = new System.Windows.Forms.Timer { Interval = 250 };
        tick.Tick += (_, _) => clock.Text = $"DeskLayer M0\n{DateTime.Now:HH:mm:ss.f}";
        tick.Start();

        var quit = new System.Windows.Forms.Timer { Interval = 30_000 };
        quit.Tick += (_, _) => Application.Exit();
        quit.Start();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "attach-log.txt");
        var (target, strategy) = Native.FindWallpaperHost();
        var log = new StringBuilder();
        log.AppendLine($"attach target: 0x{target:X8} ({Native.ClassName(target)}) via {strategy}");

        Application.EnableVisualStyles();
        var form = new SpikeForm();
        form.Shown += (_, _) =>
        {
            var previous = Native.SetParent(form.Handle, target);
            var parentNow = Native.GetAncestor(form.Handle, Native.GaParent);
            log.AppendLine($"SetParent: previous=0x{previous:X8}, parent-now=0x{parentNow:X8} ({Native.ClassName(parentNow)})");
            log.AppendLine($"self-check: reparent-ok={parentNow == target}");
            File.WriteAllText(logPath, log.ToString());
        };
        Application.Run(form);
    }
}
