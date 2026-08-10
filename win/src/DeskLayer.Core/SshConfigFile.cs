// Reads Host aliases out of ~/.ssh/config — the Windows twin of the mac
// SSHConfigFile. Windows' bundled OpenSSH reads the same file from
// %USERPROFILE%\.ssh\config, so an alias picked here resolves hostname,
// user, port, and identity exactly as `ssh <alias>` would.

using System.IO;

namespace DeskLayer.Core;

public static class SshConfigFile
{
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// Concrete Host aliases, in file order. Pattern entries (*, ?, !) are
    /// match rules, not destinations, and are skipped.
    public static IReadOnlyList<string> Aliases()
    {
        var aliases = new List<string>();
        try
        {
            if (!File.Exists(ConfigPath)) return aliases;
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("Host\t", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var name in line[5..].Split(' ', '\t'))
                {
                    if (name.Length == 0) continue;
                    if (name.IndexOfAny(new[] { '*', '?', '!' }) >= 0) continue;
                    if (!aliases.Contains(name)) aliases.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return aliases;
    }
}
