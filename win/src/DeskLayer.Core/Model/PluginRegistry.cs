// Discovers installed plugins — port of the mac PluginRegistry. Two forms in
// <data dir>\Plugins: a bare Name.js, or a folder Name.deskplugin\ holding
// main.js plus image assets. Hot reload via FileSystemWatcher; a change only
// fires DidChange when the id|mtime|size fingerprint actually moved.

namespace DeskLayer.Core.Model;

public sealed record InstalledPlugin(string Id, string SourcePath, string? AssetsDirectory);

public sealed class PluginRegistry : IDisposable
{
    public static string PluginsDirectory => Path.Combine(LayoutStore.DataDirectory, "Plugins");

    private readonly FileSystemWatcher? watcher;
    private readonly Timer debounce;
    private string fingerprint = "";
    public IReadOnlyList<InstalledPlugin> Plugins { get; private set; } = Array.Empty<InstalledPlugin>();
    public event Action? DidChange;

    public PluginRegistry(bool watch = true)
    {
        Directory.CreateDirectory(PluginsDirectory);
        debounce = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
        Rescan();
        if (watch)
        {
            watcher = new FileSystemWatcher(PluginsDirectory)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler kick = (_, _) => debounce.Change(300, Timeout.Infinite);
            watcher.Created += kick;
            watcher.Changed += kick;
            watcher.Deleted += kick;
            watcher.Renamed += (_, _) => debounce.Change(300, Timeout.Infinite);
        }
    }

    public InstalledPlugin? Plugin(string id) => Plugins.FirstOrDefault(p => p.Id == id);

    public void Rescan()
    {
        var found = new List<InstalledPlugin>();
        foreach (var path in Directory.EnumerateFileSystemEntries(PluginsDirectory).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (File.Exists(path) && path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new InstalledPlugin(Path.GetFileNameWithoutExtension(path), path, null));
            }
            else if (Directory.Exists(path) && path.EndsWith(".deskplugin", StringComparison.OrdinalIgnoreCase))
            {
                var main = Path.Combine(path, "main.js");
                if (File.Exists(main))
                    found.Add(new InstalledPlugin(Path.GetFileNameWithoutExtension(path), main, path));
            }
        }

        var print = string.Join("\n", found.Select(p =>
        {
            var info = new FileInfo(p.SourcePath);
            return $"{p.Id}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }));

        if (print == fingerprint) return;
        fingerprint = print;
        Plugins = found;
        DidChange?.Invoke();
    }

    public void Dispose()
    {
        watcher?.Dispose();
        debounce.Dispose();
    }
}
