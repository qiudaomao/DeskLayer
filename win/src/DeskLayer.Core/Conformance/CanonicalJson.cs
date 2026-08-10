// Deterministic JSON — the C# twin of the mac CanonicalJSON: object keys
// sorted ordinally, integral doubles (|v| < 10^15) as integers, other doubles
// in shortest round-trip form, minimal string escaping, compact. Byte-parity
// with the Swift serializer is what makes the goldens cross-platform.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskLayer.Core.Conformance;

public static class CanonicalJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return sb.ToString();
    }

    private static void Write(object? value, StringBuilder sb)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(s, sb);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                WriteNumber(d, sb);
                break;
            case int i:
                WriteNumber(i, sb);
                break;
            case long l:
                WriteNumber(l, sb);
                break;
            case JsonElement e:
                WriteElement(e, sb);
                break;
            case IReadOnlyDictionary<string, object> dict:
                WriteDict(dict, sb);
                break;
            case IDictionary<string, object> dict:
                WriteDict(dict.ToDictionary(kv => kv.Key, kv => kv.Value), sb);
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                var first = true;
                foreach (var element in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(element, sb);
                }
                sb.Append(']');
                break;
            default:
                throw new InvalidOperationException($"unserializable value: {value.GetType()}");
        }
    }

    private static void WriteDict(IReadOnlyDictionary<string, object> dict, StringBuilder sb)
    {
        sb.Append('{');
        var first = true;
        foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(key, sb);
            sb.Append(':');
            Write(dict[key], sb);
        }
        sb.Append('}');
    }

    private static void WriteElement(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined: sb.Append("null"); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.String: WriteString(e.GetString()!, sb); break;
            case JsonValueKind.Number: WriteNumber(e.GetDouble(), sb); break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var element in e.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteElement(element, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Object:
                sb.Append('{');
                var firstKey = true;
                foreach (var property in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstKey) sb.Append(',');
                    firstKey = false;
                    WriteString(property.Name, sb);
                    sb.Append(':');
                    WriteElement(property.Value, sb);
                }
                sb.Append('}');
                break;
        }
    }

    private static void WriteNumber(double d, StringBuilder sb)
    {
        if (double.IsFinite(d) && d == Math.Truncate(d) && Math.Abs(d) < 1e15)
            sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(d.ToString(CultureInfo.InvariantCulture)); // shortest round-trip in .NET Core 3.0+
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
