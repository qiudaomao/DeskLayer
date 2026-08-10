// One running plugin — port of the mac PluginInstance, on Jint.
//
// Engine choice (M0 bench, 2026-08-10): Jint is the default — canvas
// rendering is interop-bound and Jint's in-process calls beat ClearScript
// V8's native crossings ~18x on real fixtures, with no native dll to ship.
// The public surface here is engine-agnostic so a V8 adapter can slot in
// later for compute-heavy plugins.
//
// Boot mirrors the mac sequence exactly: prologue (plugin/console) →
// [bindings: TODO timers/fetch] → prelude.js → plugin source → parse
// plugin.export (render/properties/cadence/mode/permissions), apply
// persisted overrides, push coerced values back into the JS array.
//
// Threading: an instance is single-threaded by contract — all calls must
// come from one thread (the app gives each item's scheduler a dedicated
// lane, mirroring the mac per-plugin serial queue).

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DeskLayer.Core.Model;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace DeskLayer.Core.Js;

public enum RenderMode { Canvas, Declarative, Webview }

/// A webview plugin's configuration, resolved from plugin.export.webview and
/// (for the live-editable bits) the plugin's properties — mac parity.
public sealed record WebViewConfig(
    string Url,
    string? UserAgent,
    IReadOnlyDictionary<string, string> Headers,
    double OffsetX,
    double OffsetY,
    double Zoom);

public sealed class PluginInstance : IDisposable
{
    public string PluginId { get; }
    public RenderMode Mode { get; private set; }
    /// Seconds between renders; +∞ = render once (fps: 0). Same derivation
    /// as the mac: `interval` (seconds) wins over `fps`; neither → 30fps.
    public double RenderInterval { get; private set; } = 1.0 / 30.0;
    public bool HasDeclaredCadence { get; private set; }
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();
    public bool IsErrored { get; private set; }
    public string? ErrorMessage { get; private set; }
    public double DeclaredWidth { get; private set; }
    public double DeclaredHeight { get; private set; }
    /// Present only for webview-mode plugins.
    public WebViewConfig? WebviewConfig { get; private set; }

    private readonly Engine engine;
    private readonly JsValue renderFunction;
    private List<PluginProperty> properties = new();
    public IReadOnlyList<PluginProperty> Properties => properties;

    public static string PreludeSource { get; } = LoadPrelude();

    private static string LoadPrelude()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskLayer.Core.prelude.js")
            ?? throw new InvalidOperationException("prelude.js missing from DeskLayer.Core resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private PluginInstance(string pluginId, Engine engine, JsValue renderFunction)
    {
        PluginId = pluginId;
        this.engine = engine;
        this.renderFunction = renderFunction;
    }

    /// Boots the plugin source. Returns null when it doesn't produce a
    /// usable plugin.export (mirrors the mac failable init).
    public static PluginInstance? Boot(string pluginId, string source,
                                       IReadOnlyDictionary<string, PropertyValue>? overrides = null,
                                       Action<string>? log = null,
                                       Action<Engine>? configureEngine = null)
    {
        var engine = new Engine();
        try
        {
            var logSink = log ?? (_ => { });
            engine.SetValue("__dl_log", logSink);
            engine.Execute("var plugin = { export: null }; var console = { log: __dl_log, error: __dl_log, warn: __dl_log };");
            engine.Execute(PreludeSource);
            configureEngine?.Invoke(engine); // host bindings ($system, …)
            engine.Execute(source);

            var export = engine.Evaluate("plugin.export");
            if (export.IsNull() || export.IsUndefined())
            {
                logSink($"[{pluginId}] plugin.export missing");
                return null;
            }

            var declaredMode = engine.Evaluate("typeof plugin.export.mode === 'string' ? plugin.export.mode : null");
            var isWebview = (declaredMode.IsString() && declaredMode.AsString() == "webview")
                || engine.Evaluate("typeof plugin.export.webview === 'object' && plugin.export.webview !== null").AsBoolean();

            var render = engine.Evaluate("plugin.export.render");
            if ((render.IsNull() || render.IsUndefined()) && !isWebview)
            {
                logSink($"[{pluginId}] plugin.export.render missing");
                return null;
            }

            var instance = new PluginInstance(pluginId, engine, render);

            // Mode: explicit plugin.export.mode wins; else render's arity —
            // render(ctx) is canvas, render() is declarative (mac parity).
            if (isWebview) instance.Mode = RenderMode.Webview;
            else if (declaredMode.IsString() && declaredMode.AsString() == "canvas") instance.Mode = RenderMode.Canvas;
            else if (declaredMode.IsString() && declaredMode.AsString() == "declarative") instance.Mode = RenderMode.Declarative;
            else
            {
                var arity = engine.Evaluate("plugin.export.render.length").AsNumber();
                instance.Mode = arity >= 1 ? RenderMode.Canvas : RenderMode.Declarative;
            }

            instance.ParseProperties(overrides ?? new Dictionary<string, PropertyValue>());
            instance.ParseCadence();
            instance.ParseDeclaredSize();
            if (instance.Mode == RenderMode.Webview) instance.ParseWebviewConfig();

            var permsJson = engine.Evaluate(
                "JSON.stringify(Array.isArray(plugin.export.permissions) ? plugin.export.permissions : [])").AsString();
            instance.Permissions = JsonSerializer.Deserialize<string[]>(permsJson)!
                .Select(p => p.ToLowerInvariant()).ToHashSet();

            return instance;
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            (log ?? (_ => { }))($"[{pluginId}] boot failed: {ex.Message}");
            engine.Dispose();
            return null;
        }
    }

    private void ParseProperties(IReadOnlyDictionary<string, PropertyValue> overrides)
    {
        var json = engine.Evaluate(
            "JSON.stringify(Array.isArray(plugin.export.properties) ? plugin.export.properties : [])").AsString();
        using var doc = JsonDocument.Parse(json);
        var declared = new List<PluginProperty>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
            var name = nameEl.GetString()!;
            var valueType = entry.TryGetProperty("valueType", out var vt) && vt.ValueKind == JsonValueKind.String
                ? vt.GetString()! : "string";
            var raw = entry.TryGetProperty("value", out var v) ? PropertyValue.FromJsonElement(v) : null;
            var coerced = PropertyValue.Coerce(raw, valueType);
            if (coerced == null) continue;
            declared.Add(new PluginProperty(name, valueType,
                overrides.TryGetValue(name, out var over) ? over : coerced.Value));
        }
        properties = declared;
        PushPropertiesToJs();
    }

    /// Mutates plugin.export.properties[i].value in place — the plugin
    /// author's mental model, matching the mac push.
    private void PushPropertiesToJs()
    {
        var setter = engine.Evaluate(
            "(function (name, value) {" +
            "  var list = plugin.export.properties;" +
            "  if (!Array.isArray(list)) { return; }" +
            "  for (var i = 0; i < list.length; i++) {" +
            "    if (list[i] && list[i].name === name) { list[i].value = value; }" +
            "  }" +
            "})");
        foreach (var property in properties)
            engine.Invoke(setter, property.Name, property.Value.BridgeValue);
    }

    private void ParseCadence()
    {
        var fps = PropertyNamed("fps")?.DoubleValue;
        var interval = PropertyNamed("interval")?.DoubleValue;
        if (interval is > 0)
        {
            RenderInterval = Math.Min(Math.Max(interval.Value, 1.0 / 120.0), 86_400);
            HasDeclaredCadence = true;
        }
        else if (fps != null)
        {
            RenderInterval = fps.Value <= 0
                ? double.PositiveInfinity
                : 1.0 / Math.Min(Math.Max(fps.Value, 1.0 / 86_400.0), 120);
            HasDeclaredCadence = true;
        }
    }

    private void ParseDeclaredSize()
    {
        DeclaredWidth = engine.Evaluate("typeof plugin.export.width === 'number' ? plugin.export.width : 0").AsNumber();
        DeclaredHeight = engine.Evaluate("typeof plugin.export.height === 'number' ? plugin.export.height : 0").AsNumber();
    }

    /// Static plugin.export.webview merged with live properties: url,
    /// offsetX, offsetY, and zoom stay inspector-editable (mac parity).
    /// Cookies are a mac-only extra for now (WebView2 cookie API lands with
    /// the M4 bindings pass).
    private void ParseWebviewConfig()
    {
        var json = engine.Evaluate(
            "JSON.stringify(typeof plugin.export.webview === 'object' && plugin.export.webview !== null ? plugin.export.webview : {})").AsString();
        using var doc = JsonDocument.Parse(json);
        var cfg = doc.RootElement;

        string? CfgString(string key) =>
            cfg.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? CfgNumber(string key) =>
            cfg.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var headers = new Dictionary<string, string>();
        if (cfg.TryGetProperty("headers", out var rawHeaders) && rawHeaders.ValueKind == JsonValueKind.Object)
            foreach (var h in rawHeaders.EnumerateObject())
                headers[h.Name] = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString()! : h.Value.ToString();

        WebviewConfig = new WebViewConfig(
            Url: PropertyNamed("url")?.StringValue ?? CfgString("url") ?? "",
            UserAgent: CfgString("userAgent"),
            Headers: headers,
            OffsetX: PropertyNamed("offsetX")?.DoubleValue ?? CfgNumber("offsetX") ?? 0,
            OffsetY: PropertyNamed("offsetY")?.DoubleValue ?? CfgNumber("offsetY") ?? 0,
            Zoom: PropertyNamed("zoom")?.DoubleValue ?? CfgNumber("zoom") ?? 1);
    }

    public PropertyValue? PropertyNamed(string name) =>
        properties.FirstOrDefault(p => p.Name == name)?.Value;

    /// Canvas mode: invoke render(ctx). Returns false when the plugin threw.
    public bool CallRender(object canvasBridge)
    {
        if (IsErrored) return false;
        try
        {
            engine.Invoke(renderFunction, canvasBridge);
            return true;
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            MarkErrored(ex.Message);
            return false;
        }
    }

    /// Declarative mode: reset the action table, invoke render(), return the
    /// tree as JSON (mac callRenderTree parity).
    public string? CallRenderTree()
    {
        if (IsErrored) return null;
        try
        {
            engine.Execute("__dl_resetActions();");
            var result = engine.Invoke(renderFunction);
            if (result.IsNull() || result.IsUndefined()) return null;
            engine.SetValue("__dl_lastTree", result);
            return engine.Evaluate("JSON.stringify(__dl_lastTree)").AsString();
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            MarkErrored(ex.Message);
            return null;
        }
    }

    public void InvokeAction(int id, string payloadJson)
    {
        if (IsErrored) return;
        try
        {
            engine.Invoke(engine.Evaluate("__dl_invokeAction"), id, payloadJson);
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            MarkErrored(ex.Message);
        }
    }

    /// Live inspector edit: update the CLR copy and the JS array in place.
    public void ApplyOverride(string name, PropertyValue value)
    {
        var index = properties.FindIndex(p => p.Name == name);
        if (index >= 0) properties[index] = properties[index].With(value);
        PushPropertiesToJs();
    }

    private void MarkErrored(string message)
    {
        IsErrored = true;
        ErrorMessage = message;
    }

    public void Dispose() => engine.Dispose();
}
