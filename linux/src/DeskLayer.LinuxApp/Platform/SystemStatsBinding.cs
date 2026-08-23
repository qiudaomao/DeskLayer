// $system.stats() — Linux twin of the mac/win SystemStats. Same dictionary
// shape: {time, cpu 0…1 (delta since last call), cores, memory{total,used,
// free}, disk{total,free}, network{rxBytes,txBytes}, uptime, thermalState}.
//
// Sources: /proc/stat (cpu delta), /proc/meminfo (MemTotal/MemAvailable —
// "used" is total-available, the figure users expect, not total-free),
// statvfs on $HOME, /sys/class/net/*/statistics (cumulative counters the
// plugin diffs itself, loopback excluded), /proc/uptime, and the hottest
// /sys/class/thermal zone mapped onto the mac's 0…3 thermalState scale.

using System.Runtime.InteropServices;
using DeskLayer.Core.Js;

namespace DeskLayer.LinuxApp.Platform;

public sealed class SystemStatsBinding : HostBindings.SystemStats
{
    public IDictionary<string, object> Snapshot() => stats();

    private readonly object gate = new();
    private (long idle, long busy)? lastTicks;

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
                ["network"] = Network(),
                ["uptime"] = Uptime(),
                ["thermalState"] = ThermalState(),
            };
        }
    }

    private double CpuUsage()
    {
        try
        {
            // "cpu  user nice system idle iowait irq softirq steal ..."
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long At(int i) => fields.Length > i && long.TryParse(fields[i], out var v) ? v : 0;
            var idle = At(4) + At(5);
            var busy = At(1) + At(2) + At(3) + At(6) + At(7) + At(8);
            var previous = lastTicks;
            lastTicks = (idle, busy);
            if (previous == null) return 0;
            var idleDelta = idle - previous.Value.idle;
            var busyDelta = busy - previous.Value.busy;
            var total = idleDelta + busyDelta;
            return total > 0 ? Math.Clamp((double)busyDelta / total, 0, 1) : 0;
        }
        catch (IOException) { return 0; }
    }

    private static Dictionary<string, object> Memory()
    {
        double total = 0, available = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = KbValue(line);
                else if (line.StartsWith("MemAvailable:")) available = KbValue(line);
                if (total > 0 && available > 0) break;
            }
        }
        catch (IOException) { }
        return new Dictionary<string, object>
        {
            ["total"] = total,
            ["used"] = Math.Max(0, total - available),
            ["free"] = available,
        };
    }

    private static double KbValue(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && double.TryParse(parts[1], out var kb) ? kb * 1024 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatVfs
    {
        public ulong f_bsize, f_frsize, f_blocks, f_bfree, f_bavail;
        public ulong f_files, f_ffree, f_favail, f_fsid;
        public ulong f_flag, f_namemax;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] __spare;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statvfs(string path, out StatVfs buf);

    private static Dictionary<string, object> Disk()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (statvfs(home, out var vfs) == 0)
                return new Dictionary<string, object>
                {
                    ["total"] = (double)(vfs.f_blocks * vfs.f_frsize),
                    ["free"] = (double)(vfs.f_bavail * vfs.f_frsize),
                };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return new Dictionary<string, object> { ["total"] = 0.0, ["free"] = 0.0 };
    }

    private static Dictionary<string, object> Network()
    {
        double rx = 0, tx = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/net"))
            {
                var name = Path.GetFileName(dir);
                if (name == "lo") continue;
                rx += ReadCounter(Path.Combine(dir, "statistics/rx_bytes"));
                tx += ReadCounter(Path.Combine(dir, "statistics/tx_bytes"));
            }
        }
        catch (IOException) { }
        return new Dictionary<string, object> { ["rxBytes"] = rx, ["txBytes"] = tx };
    }

    private static double ReadCounter(string path)
    {
        try { return double.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static double Uptime()
    {
        try
        {
            var first = File.ReadAllText("/proc/uptime").Split(' ')[0];
            return double.TryParse(first, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch (IOException) { return 0; }
    }

    /// Hottest thermal zone → the mac's 0…3 scale (nominal/fair/serious/
    /// critical) at 70/85/100°C. Boxes without zones report 0.
    private static double ThermalState()
    {
        var max = 0.0;
        try
        {
            foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal"))
            {
                if (!Path.GetFileName(zone).StartsWith("thermal_zone")) continue;
                var milli = ReadCounter(Path.Combine(zone, "temp"));
                max = Math.Max(max, milli / 1000.0);
            }
        }
        catch (IOException) { }
        return max switch { >= 100 => 3, >= 85 => 2, >= 70 => 1, _ => 0 };
    }
}
