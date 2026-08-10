// Persisted layout model — port of the mac Layout.swift, wire-compatible
// with layout.json written by the mac app:
//   - normalizedFrame encodes as [[x, y], [w, h]] (Swift CGRect Codable)
//   - UUIDs are uppercase strings
//   - missing fields decode to the same defaults the mac decoder applies
//     (a hand-editable file must never be invalidated by an app update)

using System.Text.Json;

namespace DeskLayer.Core.Model;

public enum RenderTarget { Wallpaper, FloatingWindow }

public enum SshAuth { None, Password, Key }

/// 0…1 within the screen frame, bottom-left origin (mac convention kept so
/// the file round-trips; Windows callers flip Y when mapping to pixels).
public readonly record struct NormalizedFrame(double X, double Y, double W, double H)
{
    public static NormalizedFrame ReadJson(JsonElement e)
    {
        var origin = e[0];
        var size = e[1];
        return new NormalizedFrame(origin[0].GetDouble(), origin[1].GetDouble(), size[0].GetDouble(), size[1].GetDouble());
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        w.WriteStartArray(); w.WriteNumberValue(X); w.WriteNumberValue(Y); w.WriteEndArray();
        w.WriteStartArray(); w.WriteNumberValue(W); w.WriteNumberValue(H); w.WriteEndArray();
        w.WriteEndArray();
    }
}

public sealed record SshConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "default";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string User { get; init; } = "";
    public SshAuth Auth { get; init; } = SshAuth.None;
    public string KeyPath { get; init; } = "";
    public bool UsesAlias { get; init; } = true;

    public static SshConfig ReadJson(JsonElement e)
    {
        string Str(string key, string fallback) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        var user = Str("user", "");
        var port = e.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 22;
        var auth = Str("auth", "none") switch { "password" => SshAuth.Password, "key" => SshAuth.Key, _ => SshAuth.None };
        return new SshConfig
        {
            Id = e.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var g) ? g : Guid.NewGuid(),
            Name = Str("name", "default"),
            Host = Str("host", ""),
            Port = port,
            User = user,
            Auth = auth,
            KeyPath = Str("keyPath", ""),
            UsesAlias = e.TryGetProperty("usesAlias", out var ua) && ua.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ua.GetBoolean()
                : user.Length == 0 && port == 22 && auth == SshAuth.None,
        };
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("auth", Auth switch { SshAuth.Password => "password", SshAuth.Key => "key", _ => "none" });
        w.WriteString("host", Host);
        w.WriteString("id", Id.ToString("D").ToUpperInvariant());
        w.WriteString("keyPath", KeyPath);
        w.WriteString("name", Name);
        w.WriteNumber("port", Port);
        w.WriteString("user", User);
        w.WriteBoolean("usesAlias", UsesAlias);
        w.WriteEndObject();
    }
}

public sealed record LayoutItem
{
    public required Guid Id { get; init; }
    public required string PluginId { get; init; }
    /// Stable per-display identifier. Mac: CGDisplayCreateUUIDFromDisplayID;
    /// Windows: the display's device path from QueryDisplayConfig.
    public required string DisplayUuid { get; init; }
    public required NormalizedFrame NormalizedFrame { get; init; }
    public RenderTarget Target { get; init; } = RenderTarget.Wallpaper;
    public IReadOnlyDictionary<string, PropertyValue> PropertyOverrides { get; init; } =
        new Dictionary<string, PropertyValue>();
    public bool IsEnabled { get; init; } = true;
    public int ZOrder { get; init; }
    public bool ClickThrough { get; init; }
    public string? BackgroundColor { get; init; }
    public IReadOnlyList<SshConfig> SshHosts { get; init; } = Array.Empty<SshConfig>();

    public static LayoutItem ReadJson(JsonElement e)
    {
        var overrides = new Dictionary<string, PropertyValue>();
        if (e.TryGetProperty("propertyOverrides", out var po) && po.ValueKind == JsonValueKind.Object)
            foreach (var p in po.EnumerateObject())
                overrides[p.Name] = PropertyValue.ReadJson(p.Value);

        List<SshConfig> hosts = new();
        if (e.TryGetProperty("sshHosts", out var sh) && sh.ValueKind == JsonValueKind.Array)
            foreach (var h in sh.EnumerateArray()) hosts.Add(SshConfig.ReadJson(h));
        else if (e.TryGetProperty("ssh", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            hosts.Add(SshConfig.ReadJson(legacy)); // pre-multi-host layouts

        return new LayoutItem
        {
            Id = Guid.Parse(e.GetProperty("id").GetString()!),
            PluginId = e.GetProperty("pluginID").GetString()!,
            DisplayUuid = e.GetProperty("displayUUID").GetString()!,
            NormalizedFrame = NormalizedFrame.ReadJson(e.GetProperty("normalizedFrame")),
            Target = e.TryGetProperty("target", out var t) && t.GetString() == "floatingWindow"
                ? RenderTarget.FloatingWindow : RenderTarget.Wallpaper,
            PropertyOverrides = overrides,
            IsEnabled = !e.TryGetProperty("isEnabled", out var en) || en.GetBoolean(),
            ZOrder = e.TryGetProperty("zOrder", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt32() : 0,
            ClickThrough = e.TryGetProperty("clickThrough", out var ct) && ct.ValueKind == JsonValueKind.True,
            BackgroundColor = e.TryGetProperty("backgroundColor", out var bg) && bg.ValueKind == JsonValueKind.String
                ? bg.GetString() : null,
            SshHosts = hosts,
        };
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (BackgroundColor != null) w.WriteString("backgroundColor", BackgroundColor);
        w.WriteBoolean("clickThrough", ClickThrough);
        w.WriteString("displayUUID", DisplayUuid);
        w.WriteString("id", Id.ToString("D").ToUpperInvariant());
        w.WriteBoolean("isEnabled", IsEnabled);
        w.WritePropertyName("normalizedFrame");
        NormalizedFrame.WriteJson(w);
        w.WriteString("pluginID", PluginId);
        w.WritePropertyName("propertyOverrides");
        w.WriteStartObject();
        foreach (var key in PropertyOverrides.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            w.WritePropertyName(key);
            PropertyOverrides[key].WriteJson(w);
        }
        w.WriteEndObject();
        w.WritePropertyName("sshHosts");
        w.WriteStartArray();
        foreach (var host in SshHosts) host.WriteJson(w);
        w.WriteEndArray();
        w.WriteString("target", Target == RenderTarget.FloatingWindow ? "floatingWindow" : "wallpaper");
        w.WriteNumber("zOrder", ZOrder);
        w.WriteEndObject();
    }
}

public sealed record Layout
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<LayoutItem> Items { get; init; } = Array.Empty<LayoutItem>();

    public static Layout ReadJson(JsonElement root)
    {
        var items = new List<LayoutItem>();
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) items.Add(LayoutItem.ReadJson(e));
        return new Layout
        {
            Version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1,
            Items = items,
        };
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var item in Items) item.WriteJson(w);
        w.WriteEndArray();
        w.WriteNumber("version", Version);
        w.WriteEndObject();
    }
}
