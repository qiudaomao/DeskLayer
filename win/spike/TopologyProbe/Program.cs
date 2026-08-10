// DeskLayer M0 spike — desktop window topology probe.
//
// Sends Progman the undocumented 0x052C message (spawns the WorkerW that
// Explorer uses for wallpaper cross-fades) and reports which of the three
// attach strategies from the port plan this machine actually exposes:
//   1. sibling WorkerW  — next top-level WorkerW after the SHELLDLL_DefView
//                         owner (classic, pre-24H2)
//   2. child WorkerW    — WorkerW as a direct child of Progman (24H2+)
//   3. Progman direct   — attach to Progman itself, below SHELLDLL_DefView
// Read-only: enumerates and prints, attaches nothing.

using System;
using System.Runtime.InteropServices;
using System.Text;

internal static class Probe
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, 256);
        return sb.ToString();
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";
        GetWindowRect(hwnd, out var r);
        return $"0x{hwnd:X8} {ClassName(hwnd),-18} [{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}] visible={IsWindowVisible(hwnd)}";
    }

    private static void Main()
    {
        Console.WriteLine($"OS: {Environment.OSVersion.VersionString}");
        // SM_XVIRTUALSCREEN/YVIRTUALSCREEN/CXVIRTUALSCREEN/CYVIRTUALSCREEN
        Console.WriteLine($"virtual screen: [{GetSystemMetrics(76)},{GetSystemMetrics(77)} {GetSystemMetrics(78)}x{GetSystemMetrics(79)}], monitors={GetSystemMetrics(80)}");

        var progman = FindWindowW("Progman", null);
        Console.WriteLine($"Progman: {Describe(progman)}");
        if (progman == IntPtr.Zero)
        {
            Console.WriteLine("FATAL: no Progman — wrong desktop/window station?");
            return;
        }

        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
        Console.WriteLine("sent 0x052C to Progman");

        IntPtr defViewOwner = IntPtr.Zero, siblingWorkerW = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                defViewOwner = hwnd;
                siblingWorkerW = FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
                Console.WriteLine($"SHELLDLL_DefView 0x{defView:X8} owned by: {Describe(hwnd)}");
            }
            return true;
        }, IntPtr.Zero);
        if (defViewOwner == IntPtr.Zero) Console.WriteLine("no SHELLDLL_DefView found anywhere");

        Console.WriteLine(siblingWorkerW != IntPtr.Zero
            ? $"strategy 1 (sibling WorkerW): AVAILABLE {Describe(siblingWorkerW)}"
            : "strategy 1 (sibling WorkerW): not present");

        var childWorkerW = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        Console.WriteLine(childWorkerW != IntPtr.Zero
            ? $"strategy 2 (child WorkerW):   AVAILABLE {Describe(childWorkerW)}"
            : "strategy 2 (child WorkerW):   not present");

        Console.WriteLine($"strategy 3 (Progman direct): defview-owner-is-progman={defViewOwner == progman}");

        Console.WriteLine("all top-level WorkerW windows:");
        EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) == "WorkerW") Console.WriteLine($"  {Describe(hwnd)}");
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("Progman children:");
        EnumChildWindows(progman, (hwnd, _) =>
        {
            Console.WriteLine($"  {Describe(hwnd)}");
            return true;
        }, IntPtr.Zero);
    }
}
