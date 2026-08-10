//
//  PluginDocs.swift
//  DeskLayer
//
//  The plugin API, as shipped inside the app, for teaching a model to write
//  one. Kept identical to shared/spec/ by scripts/check-docs-sync.sh.
//
//  The declarations are bundled as plugin-dts.txt, not plugin.d.ts: Xcode's
//  synchronized groups skip .ts files entirely — they never reach
//  Contents/Resources, and the app would silently ship without them.
//

import Foundation

nonisolated enum PluginDocs {
    /// TypeScript declarations for every API a plugin can reach.
    static var declarations: String { load("plugin-dts", "txt") }
    /// The authoring guide: modes, elements, modifiers, host APIs.
    static var guide: String { load("plugin-guide", "md") }

    /// A short plugin to show the shape rather than describe it. The caller
    /// may pass a real installed one so the example matches what the user
    /// already has; otherwise this built-in card is used.
    static func example(installed source: String? = nil) -> String {
        if let source, !source.isEmpty { return source }
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
        """
    }

    /// True when the bundle actually carries the docs — a build that dropped
    /// them should say so rather than quietly prompting with nothing.
    static var isAvailable: Bool { !declarations.isEmpty && !guide.isEmpty }

    private static func load(_ name: String, _ ext: String) -> String {
        guard let url = Bundle.main.url(forResource: name, withExtension: ext),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return "" }
        return text
    }
}
