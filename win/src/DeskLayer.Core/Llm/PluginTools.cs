// The functions the model may call while writing a plugin — the Windows twin
// of the mac PluginTools. Everything it writes lands in a staging directory,
// never in the plugins folder: a half-written plugin must not go live, and
// writing into the real folder wakes the folder watcher, which rebuilds
// every running item.
//
// Reads are confined to the plugins folder and the bundled docs; writes to
// staging. Paths are resolved before they are checked, so `..` cannot walk
// out of either.

using System.IO;
using System.Text.Json.Nodes;
using DeskLayer.Core.Model;

namespace DeskLayer.Core.Llm;

public sealed class PluginTools
{
    /// Where the model's work in progress lives until it is installed.
    public string StagingDirectory { get; }
    private readonly PluginRegistry registry;

    /// Plugin files the model has written this run, by name (insertion order).
    public IReadOnlyList<string> Written => written;
    private readonly List<string> written = new();

    public PluginTools(PluginRegistry registry)
    {
        this.registry = registry;
        StagingDirectory = Path.Combine(Path.GetTempPath(), $"desklayer-author-{Guid.NewGuid()}");
        Directory.CreateDirectory(StagingDirectory);
    }

    public void CleanUp()
    {
        try { Directory.Delete(StagingDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // MARK: - Specs

    public static IReadOnlyList<ToolSpec> Specs { get; } = new[]
    {
        new ToolSpec(
            "list_plugins",
            "List the plugins already installed, with their versions. Use this before editing one.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new ToolSpec(
            "read_file",
            "Read a reference document or an installed plugin's source. " +
            "Use name=\"plugin.d.ts\" for the API declarations, name=\"plugin-guide.md\" " +
            "for the authoring guide, or the id of an installed plugin (e.g. \"AnalogClock\").",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "plugin.d.ts, plugin-guide.md, or an installed plugin id",
                    },
                },
                ["required"] = new JsonArray("name"),
            }),
        new ToolSpec(
            "write_plugin",
            "Write the plugin's JavaScript. Call this once the code is complete; " +
            "call it again to correct mistakes that validate_plugin reports.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Plugin name without .js, e.g. \"Weather Card\"",
                    },
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The complete file contents",
                    },
                },
                ["required"] = new JsonArray("name", "source"),
            }),
        new ToolSpec(
            "validate_plugin",
            "Check a plugin you have written: does it parse, does it assign " +
            "plugin.export, is render a function. Returns the error to fix, if any.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The name passed to write_plugin",
                    },
                },
                ["required"] = new JsonArray("name"),
            }),
    };

    // MARK: - Execution

    /// Runs one call and returns the text the model sees. Never throws: a
    /// failure is a result the model can read and react to.
    public string Run(ToolCall call)
    {
        switch (call.Name)
        {
            case "list_plugins":
                return ListPlugins();
            case "read_file":
            {
                var name = call.StringArgument("name");
                return name == null ? "error: missing \"name\"" : ReadFile(name);
            }
            case "write_plugin":
            {
                var name = call.StringArgument("name");
                var source = call.StringArgument("source");
                if (name == null || source == null)
                    return "error: write_plugin needs \"name\" and \"source\"";
                return WritePlugin(name, source);
            }
            case "validate_plugin":
            {
                var name = call.StringArgument("name");
                return name == null ? "error: missing \"name\"" : Validate(name);
            }
            default:
                return $"error: no such tool \"{call.Name}\"";
        }
    }

    private string ListPlugins()
    {
        if (registry.Plugins.Count == 0) return "No plugins are installed.";
        return string.Join("\n", registry.Plugins.Select(plugin =>
        {
            var version = TryRead(plugin.SourcePath) is { } source
                ? PluginMetadata.Extract(source).version ?? "—" : "—";
            return $"{plugin.Id} ({version})";
        }));
    }

    private string ReadFile(string name)
    {
        switch (name)
        {
            case "plugin.d.ts" or "plugin-dts.txt" or "plugin.d.ts.txt":
                return PluginDocs.Declarations.Length > 0
                    ? PluginDocs.Declarations : "error: the declarations aren't bundled in this build";
            case "plugin-guide.md" or "guide":
                return PluginDocs.Guide.Length > 0
                    ? PluginDocs.Guide : "error: the guide isn't bundled in this build";
        }
        // An installed plugin, by id. Never a path — the model doesn't get to
        // name files, only plugins the registry already knows about.
        var id = name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        var descriptor = registry.Plugin(id);
        if (descriptor == null)
            return $"error: no plugin named \"{id}\". Call list_plugins to see what exists.";
        return TryRead(descriptor.SourcePath) ?? $"error: couldn't read {id}";
    }

    private string WritePlugin(string name, string source)
    {
        var url = StagedPath(name);
        if (url == null) return $"error: \"{name}\" isn't a usable plugin name";
        try { File.WriteAllText(url, source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
        var written2 = Path.GetFileNameWithoutExtension(url);
        if (!written.Contains(written2)) written.Add(written2);
        var (ok, message) = PluginMetadata.Validate(source);
        return ok
            ? $"Wrote {Path.GetFileName(url)} ({source.Length} bytes). {message}"
            : $"Wrote {Path.GetFileName(url)}, but it is not valid yet: {message}";
    }

    private string Validate(string name)
    {
        var url = StagedPath(name);
        var source = url == null ? null : TryRead(url);
        if (source == null) return "error: nothing written under that name yet";
        return PluginMetadata.Validate(source).Message;
    }

    // MARK: - Confinement

    /// A staging path for a plugin name, or null if the name could escape.
    /// The name is reduced to its file name and re-resolved, then checked to
    /// be a direct child of the staging directory — so "../..", an absolute
    /// path, or a drive prefix all fail.
    public string? StagedPath(string name)
    {
        var basename = Path.GetFileName(name.Replace('\\', '/')).Trim();
        foreach (var bad in Path.GetInvalidFileNameChars()) basename = basename.Replace(bad.ToString(), "");
        if (basename.Length == 0 || basename == "." || basename == "..") return null;
        var file = basename.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? basename : basename + ".js";
        var full = Path.GetFullPath(Path.Combine(StagingDirectory, file));
        var parent = Path.GetDirectoryName(full);
        if (!string.Equals(parent, Path.GetFullPath(StagingDirectory), StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
