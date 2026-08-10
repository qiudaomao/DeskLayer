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

    /// Everything the inspector shows about a plugin, read from plugin.export
    /// the same inert way Extract reads version/updateURL — including the
    /// resize policy (mac PluginMetadata parity).
    public sealed record PluginInfo(
        string? Version, string? UpdateUrl, string? Author, string? Description,
        double? Width, double? Height,
        bool Resizable = true, bool? LockAspect = null,
        double? MinWidth = null, double? MaxWidth = null,
        double? MinHeight = null, double? MaxHeight = null)
    {
        /// Whether a corner drag should keep the aspect ratio: an explicit
        /// scaleMode/lockAspect wins; a declared size implies a natural
        /// aspect worth keeping.
        public bool KeepsAspect => LockAspect ?? (Width is > 0 && Height is > 0);

        /// Clamp a point size to the declared limits.
        public (double W, double H) Clamp(double w, double h)
        {
            if (MinWidth is { } minW) w = Math.Max(w, minW);
            if (MaxWidth is { } maxW) w = Math.Min(w, maxW);
            if (MinHeight is { } minH) h = Math.Max(h, minH);
            if (MaxHeight is { } maxH) h = Math.Min(h, maxH);
            return (w, h);
        }

        public enum SizeAxis { Width, Height }

        /// The size an editor should settle on after the user changes one:
        /// limits applied, and for aspect-locked plugins the untouched axis
        /// follows the edited one. Out-of-range input snaps to the limit
        /// rather than being kept (mac resolvedSize).
        public (double W, double H) ResolvedSize(double w, double h, SizeAxis? edited, double floor = 8)
        {
            if (edited is { } axis && KeepsAspect && Width is > 0 && Height is > 0)
            {
                var ratio = Width.Value / Height.Value;
                if (axis == SizeAxis.Width) h = w / ratio;
                else w = h * ratio;
                // Clamp, then re-derive the edited axis: its partner may have
                // hit a limit first, and the aspect has to survive that.
                (w, h) = Clamp(w, h);
                if (axis == SizeAxis.Width) w = h * ratio;
                else h = w / ratio;
            }
            (w, h) = Clamp(w, h);
            return (Math.Max(w, floor), Math.Max(h, floor));
        }
    }

    public static PluginInfo ExtractInfo(string source)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2)));
        try
        {
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
            string? Str(string field)
            {
                var value = engine.Evaluate(
                    $"(plugin.export && typeof plugin.export.{field} === 'string') ? plugin.export.{field} : null");
                return value.IsString() ? value.AsString() : null;
            }
            double? Num(string field)
            {
                var value = engine.Evaluate(
                    $"(plugin.export && typeof plugin.export.{field} === 'number') ? plugin.export.{field} : null");
                return value.IsNumber() ? value.AsNumber() : null;
            }
            bool? Flag(string field)
            {
                var value = engine.Evaluate(
                    $"(plugin.export && typeof plugin.export.{field} === 'boolean') ? plugin.export.{field} : null");
                return value.IsBoolean() ? value.AsBoolean() : null;
            }
            // scaleMode: "ratio" | "free" (alias: lockAspect: true/false).
            var lockAspect = Str("scaleMode")?.ToLowerInvariant() is { } mode
                ? mode is "ratio" or "aspect" or "locked"
                : Flag("lockAspect");
            return new PluginInfo(Str("version"), Str("updateURL"), Str("author"),
                Str("description"), Num("width"), Num("height"),
                Resizable: Flag("resizable") ?? true,
                LockAspect: lockAspect,
                MinWidth: Num("minWidth"), MaxWidth: Num("maxWidth"),
                MinHeight: Num("minHeight"), MaxHeight: Num("maxHeight"));
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException or TimeoutException)
        {
            return new PluginInfo(null, null, null, null, null, null);
        }
        finally { engine.Dispose(); }
    }

    /// Checks source the way the app will read it, without running it for
    /// real — the Windows twin of the mac PluginMetadata.validate. Written
    /// for generated code — the model gets the message back and can fix its
    /// own mistake — but it is the right gate for any untrusted plugin.
    public static (bool IsOK, string Message) Validate(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return (false, "The file is empty.");
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2)));
        try
        {
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
            try { engine.Execute(source); }
            catch (JavaScriptException ex) { return (false, $"JavaScript error: {ex.Message}"); }

            var hasExport = engine.Evaluate(
                "plugin.export !== null && plugin.export !== undefined && typeof plugin.export === 'object'");
            if (!hasExport.IsBoolean() || !hasExport.AsBoolean())
                return (false, "The script never assigns plugin.export.");

            // A webview plugin has no render(); everything else must have one.
            var isWebview = engine.Evaluate(
                "plugin.export.webview !== null && typeof plugin.export.webview === 'object'");
            if (isWebview.IsBoolean() && isWebview.AsBoolean()) return (true, "Valid webview plugin.");

            var renderIsFunction = engine.Evaluate("typeof plugin.export.render === 'function'");
            if (!renderIsFunction.IsBoolean() || !renderIsFunction.AsBoolean())
                return (false, "plugin.export.render is missing or is not a function.");

            // render(ctx) draws on a canvas; render() returns a view tree.
            var arity = engine.Evaluate("plugin.export.render.length");
            var canvas = arity.IsNumber() && arity.AsNumber() >= 1;
            return (true, canvas ? "Valid canvas plugin." : "Valid declarative plugin.");
        }
        catch (Exception ex) when (ex is JintException or TimeoutException)
        {
            return (false, $"JavaScript error: {ex.Message}");
        }
        finally { engine.Dispose(); }
    }
}
