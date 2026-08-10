// The serialized view tree a declarative plugin returns from render() —
// C# port of DeskLayerKit/ViewNode.swift. Plain data produced by the shared
// prelude's pure-JS builders; one JSON.stringify per render. Trees compare
// by their JSON string, so unchanged renders skip the UI update entirely.

using System.Text.Json;

namespace DeskLayer.Core.Model;

public sealed record ViewNode(
    string Type,
    string? Text,
    IReadOnlyList<NodeModifier> Modifiers,
    IReadOnlyList<ViewNode> Children)
{
    public static ViewNode? Decode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Read(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ViewNode Read(JsonElement e)
    {
        var modifiers = new List<NodeModifier>();
        if (e.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Array)
            foreach (var m in mods.EnumerateArray())
            {
                var args = new List<JsonVal>();
                if (m.TryGetProperty("args", out var argArr) && argArr.ValueKind == JsonValueKind.Array)
                    foreach (var a in argArr.EnumerateArray()) args.Add(JsonVal.Read(a));
                modifiers.Add(new NodeModifier(m.GetProperty("name").GetString() ?? "", args));
            }

        var children = new List<ViewNode>();
        if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var child in kids.EnumerateArray()) children.Add(Read(child));

        return new ViewNode(
            e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
            e.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
            modifiers,
            children);
    }

    public NodeModifier? Modifier(string name) => Modifiers.FirstOrDefault(m => m.Name == name);
    public string? ModifierString(string name) => Modifier(name)?.FirstString;
    public double? ModifierDouble(string name) => Modifier(name)?.FirstDouble;

    /// The action id carried by a modifier (onTap / onTapGesture / onChange).
    public int? ActionId(string name)
    {
        var value = ModifierDouble(name);
        return value == null ? null : (int)value.Value;
    }
}

public sealed record NodeModifier(string Name, IReadOnlyList<JsonVal> Args)
{
    public double? FirstDouble => Args.Count > 0 ? Args[0].DoubleValue : null;
    public string? FirstString => Args.Count > 0 ? Args[0].StringValue : null;
}

/// Heterogeneous modifier argument (string | number | bool | null) —
/// mirror of the Swift JSONValue.
public readonly struct JsonVal
{
    private enum Kind { String, Number, Bool, Null }
    private readonly Kind kind;
    private readonly string? text;
    private readonly double number;
    private readonly bool flag;

    private JsonVal(Kind kind, string? text, double number, bool flag)
    {
        this.kind = kind; this.text = text; this.number = number; this.flag = flag;
    }

    public static JsonVal Read(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => new JsonVal(Kind.String, e.GetString(), 0, false),
        JsonValueKind.Number => new JsonVal(Kind.Number, null, e.GetDouble(), false),
        JsonValueKind.True => new JsonVal(Kind.Bool, null, 0, true),
        JsonValueKind.False => new JsonVal(Kind.Bool, null, 0, false),
        _ => new JsonVal(Kind.Null, null, 0, false),
    };

    public bool IsNull => kind == Kind.Null;
    public bool IsNumber => kind == Kind.Number;

    public double? DoubleValue => kind switch
    {
        Kind.Number => number,
        Kind.String => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null,
        Kind.Bool => flag ? 1 : 0,
        _ => null,
    };

    public string? StringValue => kind switch
    {
        Kind.String => text,
        Kind.Number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Kind.Bool => flag ? "true" : "false",
        _ => null,
    };
}
