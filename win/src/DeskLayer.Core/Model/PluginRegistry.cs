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

    /// What a rename did, or why it didn't — a value rather than a throw, the
    /// same shape UpdateResult uses (mac RenameOutcome parity).
    public enum RenameOutcome { Renamed, Unchanged, NotFound, InvalidName, NameTaken, Failed }

    public sealed record RenameResult(RenameOutcome Outcome, string? Name = null, string? Message = null)
    {
        public bool IsOK => Outcome is RenameOutcome.Renamed or RenameOutcome.Unchanged;
    }

    /// A plugin id is a file name: keep it one path component, and let the
    /// user type "Name" or "Name.js" indifferently. Null when the result
    /// would not be a usable file name.
    public static string? NormalizedName(string proposed)
    {
        var name = proposed.Trim();
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) name = name[..^3].Trim();
        if (name.Length == 0 || name.StartsWith('.')) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return null;
        return name;
    }

    /// Renames the plugin's file (or its .deskplugin folder). The caller
    /// repoints placed items — nothing here knows about the layout — and is
    /// responsible for refusing store-installed plugins, which keep their
    /// catalog name so updates can still find them.
    public RenameResult Rename(string id, string proposed)
    {
        var descriptor = Plugin(id);
        if (descriptor == null) return new RenameResult(RenameOutcome.NotFound, Message: "That plugin is no longer installed.");
        if (NormalizedName(proposed) is not { } name)
            return new RenameResult(RenameOutcome.InvalidName, Message: "Use a name without \u201C/\u201D or \u201C:\u201D.");
        if (name == descriptor.Id) return new RenameResult(RenameOutcome.Unchanged, name);
        // Windows file names are case-insensitive, so compare that way.
        if (Plugins.Any(p => p.Id != descriptor.Id && string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)))
            return new RenameResult(RenameOutcome.NameTaken, Message: "Another plugin already has that name.");

        var isFolder = descriptor.AssetsDirectory != null;
        var source = isFolder ? descriptor.AssetsDirectory! : descriptor.SourcePath;
        var destination = Path.Combine(PluginsDirectory, name + (isFolder ? ".deskplugin" : ".js"));
        try
        {
            if (isFolder) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenameResult(RenameOutcome.Failed, Message: ex.Message);
        }
        Rescan();
        return new RenameResult(RenameOutcome.Renamed, name);
    }

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
