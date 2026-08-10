// Typed value for plugin properties — port of the mac PropertyValue.
// Wire format (layout.json): {"type": "number", "value": 30}. Coercion goes
// by the DECLARED valueType, never by the JSON type.

using System.Globalization;
using System.Text.Json;

namespace DeskLayer.Core.Model;

public enum PropertyKind { String, Number, Bool, Color }

public readonly record struct PropertyValue
{
    public PropertyKind Kind { get; }
    private readonly string? text;
    private readonly double number;
    private readonly bool flag;

    private PropertyValue(PropertyKind kind, string? text, double number, bool flag)
    {
        Kind = kind; this.text = text; this.number = number; this.flag = flag;
    }

    public static PropertyValue String(string s) => new(PropertyKind.String, s, 0, false);
    public static PropertyValue Number(double n) => new(PropertyKind.Number, null, n, false);
    public static PropertyValue Bool(bool b) => new(PropertyKind.Bool, null, 0, b);
    public static PropertyValue Color(string s) => new(PropertyKind.Color, s, 0, false);

    public string StringValue => Kind switch
    {
        PropertyKind.String or PropertyKind.Color => text!,
        PropertyKind.Number => FormatNumber(number),
        _ => flag ? "true" : "false",
    };

    public double? DoubleValue => Kind switch
    {
        PropertyKind.Number => number,
        PropertyKind.String => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null,
        PropertyKind.Bool => flag ? 1 : 0,
        _ => null,
    };

    public bool? BoolValue => Kind switch
    {
        PropertyKind.Bool => flag,
        PropertyKind.Number => number != 0,
        PropertyKind.String => text!.ToLowerInvariant() is "true" or "1" or "yes",
        _ => null,
    };

    /// The value as a JS-bridgeable object (string / double / bool).
    public object BridgeValue => Kind switch
    {
        PropertyKind.String or PropertyKind.Color => text!,
        PropertyKind.Number => number,
        _ => flag,
    };

    private static string FormatNumber(double n) =>
        n == Math.Truncate(n) && Math.Abs(n) < 1e15
            ? ((long)n).ToString(CultureInfo.InvariantCulture)
            : n.ToString(CultureInfo.InvariantCulture);

    /// Coerce a raw JS/JSON value by the declared valueType (mac parity).
    public static PropertyValue? Coerce(object? raw, string valueType)
    {
        switch (valueType)
        {
            case "number":
                return raw switch
                {
                    double d => Number(d),
                    bool b => Number(b ? 1 : 0),
                    string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => Number(v),
                    _ => null,
                };
            case "boolean" or "bool":
                return raw switch
                {
                    double d => Bool(d != 0),
                    bool b => Bool(b),
                    string s => Bool(s.ToLowerInvariant() is "true" or "1" or "yes"),
                    _ => null,
                };
            case "color":
                return raw is string c ? Color(c) : null;
            default: // "string" and anything unknown
                return raw switch
                {
                    string s => String(s),
                    double d => String(FormatNumber(d)),
                    bool b => String(b ? "1" : "0"),
                    _ => null,
                };
        }
    }

    public static object? FromJsonElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // ---- layout.json wire format ----

    public static PropertyValue ReadJson(JsonElement e)
    {
        var type = e.GetProperty("type").GetString();
        var value = e.GetProperty("value");
        return type switch
        {
            "number" => Number(value.GetDouble()),
            "bool" => Bool(value.GetBoolean()),
            "color" => Color(value.GetString() ?? ""),
            _ => String(value.GetString() ?? ""),
        };
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", Kind switch
        {
            PropertyKind.Number => "number",
            PropertyKind.Bool => "bool",
            PropertyKind.Color => "color",
            _ => "string",
        });
        switch (Kind)
        {
            case PropertyKind.Number: w.WriteNumber("value", number); break;
            case PropertyKind.Bool: w.WriteBoolean("value", flag); break;
            default: w.WriteString("value", text); break;
        }
        w.WriteEndObject();
    }
}

/// A property as declared by a plugin: name + valueType + current value.
public sealed record PluginProperty(string Name, string ValueType, PropertyValue Value)
{
    public PluginProperty With(PropertyValue value) => this with { Value = value };
}
