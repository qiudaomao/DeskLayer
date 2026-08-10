// $system.stats() — Windows twin of the mac SystemStats (HostBindings.swift).
// Same dictionary shape: {time, cpu 0…1 (delta since last call), cores,
// memory{total,used,free}, disk{total,free}, network{rxBytes,txBytes},
// uptime, thermalState}. thermalState is always 0 ("nominal") — Windows has
// no reliable public thermal API (port-plan decision). Network counters are
// 0 in the M1 skeleton (GetIfTable2 lands with the M4 bindings pass).

using System.Runtime.InteropServices;

namespace DeskLayer.App;

public sealed class SystemStatsBinding
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    private readonly object gate = new();
    private (long idle, long busy)? lastTicks;

    // Lowercase name: this is the JS-facing $system.stats().
    public Dictionary<string, object> stats()
    {
        lock (gate)
        {
            return new Dictionary<string, object>
            {
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["cpu"] = CpuUsage(),
                ["cores"] = (double)Environment.ProcessorCount,
                ["memory"] = Memory(),
                ["disk"] = Disk(),
                ["network"] = new Dictionary<string, object> { ["rxBytes"] = 0.0, ["txBytes"] = 0.0 },
                ["uptime"] = Environment.TickCount64 / 1000.0,
                ["thermalState"] = 0.0,
            };
        }
    }

    /// Overall CPU usage 0…1 since the previous stats() call (0 on first) —
    /// same delta semantics as the mac's host_statistics ticks.
    private double CpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        // kernel time includes idle; busy = (kernel - idle) + user.
        var busy = (kernel - idle) + user;
        var previous = lastTicks;
        lastTicks = (idle, busy);
        if (previous == null) return 0;
        var idleDelta = idle - previous.Value.idle;
        var busyDelta = busy - previous.Value.busy;
        var total = idleDelta + busyDelta;
        return total > 0 ? Math.Clamp((double)busyDelta / total, 0, 1) : 0;
    }

    private static Dictionary<string, object> Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return new Dictionary<string, object> { ["total"] = 0.0, ["used"] = 0.0, ["free"] = 0.0 };
        return new Dictionary<string, object>
        {
            ["total"] = (double)status.TotalPhys,
            ["used"] = (double)(status.TotalPhys - status.AvailPhys),
            ["free"] = (double)status.AvailPhys,
        };
    }

    private static Dictionary<string, object> Disk()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!GetDiskFreeSpaceExW(home, out _, out var total, out var free))
            return new Dictionary<string, object> { ["total"] = 0.0, ["free"] = 0.0 };
        return new Dictionary<string, object> { ["total"] = (double)total, ["free"] = (double)free };
    }
}
