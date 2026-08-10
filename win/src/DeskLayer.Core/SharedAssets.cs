// Loads the shared/runtime lookup tables (symbol-map, font-aliases) bundled
// as embedded resources, so the runtime never depends on repo layout. The
// tables are the cross-platform contract's Windows adaptation layer:
// SF Symbol names → Fluent glyphs, mac font families → Windows families.

using System.Reflection;
using System.Text.Json;

namespace DeskLayer.Core;

public static class SharedAssets
{
    private static readonly Dictionary<string, string> Symbols = Load("symbol-map.json");
    private static readonly Dictionary<string, string> Fonts = Load("font-aliases.json");
    private static readonly HashSet<string> WarnedSymbols = new();

    private static Dictionary<string, string> Load(string name)
    {
        var result = new Dictionary<string, string>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DeskLayer.Core.{name}");
        if (stream == null) return result;
        using var doc = JsonDocument.Parse(stream);
        foreach (var property in doc.RootElement.EnumerateObject())
            if (!property.Name.StartsWith('_') && property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString()!;
        return result;
    }

    /// SF Symbol name → Fluent glyph, or a neutral dot with a one-time
    /// warning for an unmapped name.
    public static string SymbolGlyph(string name, Action<string> log)
    {
        if (Symbols.TryGetValue(name, out var glyph)) return glyph;
        if (WarnedSymbols.Add(name)) log($"no symbol mapping for \"{name}\", using placeholder");
        return "●"; // ●
    }

    /// mac font family → Windows family (identity if unmapped).
    public static string FontFamily(string family) => Fonts.GetValueOrDefault(family, family);
}
