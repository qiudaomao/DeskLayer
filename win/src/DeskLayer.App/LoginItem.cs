// Start-with-Windows toggle via the per-user Run key — the Windows twin of
// the mac SMAppService login item. No admin needed (HKCU), and it points at
// the current executable so it survives a move/reinstall to a new path.

using System.IO;
using Microsoft.Win32;

namespace DeskLayer.App;

public static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskLayer";

    private static string ExecutablePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string path && PathsEqual(Unquote(path), ExecutablePath);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key == null) return;
        if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
