// UI strings, keyed by their English text — the same shape as the mac's
// String(localized:) / Localizable.xcstrings, so a string that exists on
// both platforms is translated once and reads identically. An untranslated
// string falls back to the English key rather than to a missing-key marker,
// so a half-finished translation degrades to plain English.
//
// Culture comes from Windows (CurrentUICulture); DESKLAYER_LANG overrides it
// for screenshots and testing. It lives in Core so the plugin-authoring
// progress lines localize with the rest of the UI.

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DeskLayer.Core;

public static class L
{
    // Declaration order is load-bearing: static initializers run top to
    // bottom, and Load() reads the resolved language. Resolving after the
    // table would leave every lookup unlocalized.
    private static readonly string? Resolved = Resolve();
    private static readonly Dictionary<string, string> Table = Load();

    /// The localized form of `english`, or `english` itself.
    public static string T(string english) => Table.GetValueOrDefault(english, english);

    /// Localize, then fill {0}, {1}… — the translation carries the
    /// placeholders, so a language can reorder them.
    public static string T(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// "zh-Hans" | "ja" | null (English). Simplified Chinese only: the
    /// translations are Simplified, so zh-Hant must fall back to English
    /// rather than show the wrong script.
    public static string? Language => Resolved;

    private static string? Resolve()
    {
        var requested = Environment.GetEnvironmentVariable("DESKLAYER_LANG");
        var name = string.IsNullOrWhiteSpace(requested)
            ? CultureInfo.CurrentUICulture.Name
            : requested;
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? null : "zh-Hans";
        }
        return null;
    }

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var language = Language;
        if (language == null) return table;
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DeskLayer.Core.strings.json");
            if (stream == null) return table;
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (entry.Value.TryGetProperty(language, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } translated)
                    table[entry.Name] = translated;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken catalog must not stop the app from opening in English.
        }
        return table;
    }
}
