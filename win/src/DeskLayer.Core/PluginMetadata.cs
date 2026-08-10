// Extracts a plugin's declared version + updateURL from its source without
// running the real runtime — the Windows twin of the mac PluginMetadata. A
// throwaway Jint engine with inert stubs evaluates the source and reads
// plugin.export; a plugin that throws at load simply yields nulls.

using Jint;
using Jint.Runtime;

namespace DeskLayer.Core;

public static class PluginMetadata
{
    public static (string? version, string? updateUrl) Extract(string source)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2)));
        try
        {
            // Inert stubs so top-level code that touches host APIs doesn't throw.
            engine.Execute("""
                var plugin = { export: null };
                var console = { log: function () {}, error: function () {}, warn: function () {} };
                function noop() { return noop; }
                var $system = { stats: noop }, $server = { on: noop }, $ssh = { hosts: [] };
                var shell = noop, applescript = noop, ssh = noop, fetch = noop;
                function setTimeout() {} function setInterval() {}
                function clearTimeout() {} function clearInterval() {}
                function WebSocket() {} var $platform = 'windows';
                """);
            engine.Execute(source);
            var version = engine.Evaluate(
                "(plugin.export && typeof plugin.export.version === 'string') ? plugin.export.version : null");
            var updateUrl = engine.Evaluate(
                "(plugin.export && typeof plugin.export.updateURL === 'string') ? plugin.export.updateURL : null");
            return (
                version.IsString() ? version.AsString() : null,
                updateUrl.IsString() ? updateUrl.AsString() : null);
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException or TimeoutException)
        {
            return (null, null);
        }
        finally { engine.Dispose(); }
    }
}
