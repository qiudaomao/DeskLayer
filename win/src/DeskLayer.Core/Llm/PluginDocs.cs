// The plugin API, as shipped inside the assembly, for teaching a model to
// write one — the Windows twin of the mac PluginDocs. The files are the very
// ones in shared/spec/, embedded at build time.

using System.Reflection;

namespace DeskLayer.Core.Llm;

public static class PluginDocs
{
    /// TypeScript declarations for every API a plugin can reach.
    public static string Declarations { get; } = Load("plugin.d.ts");
    /// The authoring guide: modes, elements, modifiers, host APIs.
    public static string Guide { get; } = Load("plugin-guide.md");

    /// True when the assembly actually carries the docs — a build that
    /// dropped them should say so rather than quietly prompting with nothing.
    public static bool IsAvailable => Declarations.Length > 0 && Guide.Length > 0;

    /// A short plugin to show the shape rather than describe it. The caller
    /// may pass a real installed one so the example matches what the user
    /// already has; otherwise this built-in card is used.
    public static string Example(string? installed = null)
    {
        if (!string.IsNullOrEmpty(installed)) return installed;
        return """
        let properties = [
            { "name": "fps", "valueType": "number", "value": "1" },
            { "name": "label", "valueType": "string", "value": "Hello" }
        ];

        const prop = n => properties.find(p => p.name === n).value;

        render = () => view([
            VStack([
                Text(String(prop("label"))).fontSize(18).bold().textColor("white"),
                Text("a second line").fontSize(12).textColor("#FFFFFF99")
            ]).spacing(6).padding(14).background("#101418E6").cornerRadius(12)
        ]);

        plugin.export = {
            version: "1.0.0",
            author: "You",
            description: "A card with two lines of text.",
            width: 200, height: 90,
            properties,
            render
        };
        """;
    }

    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"DeskLayer.Core.{name}");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
