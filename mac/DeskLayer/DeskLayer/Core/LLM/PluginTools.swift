//
//  PluginTools.swift
//  DeskLayer
//
//  The functions the model may call while writing a plugin. Everything it
//  writes lands in a staging directory, never in the plugins folder: a
//  half-written plugin must not go live, and writing into the real folder
//  wakes the folder watcher, which rebuilds every running item.
//
//  Reads are confined to the plugins folder and the bundled docs; writes to
//  staging. Paths are resolved before they are checked, so `..` cannot walk
//  out of either.
//

import Foundation

@MainActor
final class PluginTools {
    /// Where the model's work in progress lives until it is installed.
    let stagingURL: URL
    private let registry: PluginRegistry

    init(registry: PluginRegistry) {
        self.registry = registry
        stagingURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("desklayer-author-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: stagingURL, withIntermediateDirectories: true)
    }

    func cleanUp() {
        try? FileManager.default.removeItem(at: stagingURL)
    }

    /// Plugin files the model has written this run, by name.
    private(set) var written: Set<String> = []

    // MARK: - Specs

    static var specs: [ToolSpec] {
        [
            ToolSpec(function: .init(
                name: "list_plugins",
                description: "List the plugins already installed, with their versions. Use this before editing one.",
                parameters: .object(["type": .string("object"), "properties": .object([:])])
            )),
            ToolSpec(function: .init(
                name: "read_file",
                description: """
                Read a reference document or an installed plugin's source. \
                Use name="plugin.d.ts" for the API declarations, name="plugin-guide.md" \
                for the authoring guide, or the id of an installed plugin (e.g. "AnalogClock").
                """,
                parameters: .object([
                    "type": .string("object"),
                    "properties": .object([
                        "name": .object([
                            "type": .string("string"),
                            "description": .string("plugin.d.ts, plugin-guide.md, or an installed plugin id"),
                        ]),
                    ]),
                    "required": .array([.string("name")]),
                ])
            )),
            ToolSpec(function: .init(
                name: "write_plugin",
                description: """
                Write the plugin's JavaScript. Call this once the code is complete; \
                call it again to correct mistakes that validate_plugin reports.
                """,
                parameters: .object([
                    "type": .string("object"),
                    "properties": .object([
                        "name": .object([
                            "type": .string("string"),
                            "description": .string("Plugin name without .js, e.g. \"Weather Card\""),
                        ]),
                        "source": .object([
                            "type": .string("string"),
                            "description": .string("The complete file contents"),
                        ]),
                    ]),
                    "required": .array([.string("name"), .string("source")]),
                ])
            )),
            ToolSpec(function: .init(
                name: "validate_plugin",
                description: """
                Check a plugin you have written: does it parse, does it assign \
                plugin.export, is render a function. Returns the error to fix, if any.
                """,
                parameters: .object([
                    "type": .string("object"),
                    "properties": .object([
                        "name": .object([
                            "type": .string("string"),
                            "description": .string("The name passed to write_plugin"),
                        ]),
                    ]),
                    "required": .array([.string("name")]),
                ])
            )),
        ]
    }

    // MARK: - Execution

    /// Runs one call and returns the text the model sees. Never throws: a
    /// failure is a result the model can read and react to.
    func run(_ call: ToolCall) -> String {
        let arguments = call.function.arguments
        switch call.function.name {
        case "list_plugins":
            return listPlugins()
        case "read_file":
            guard let name = JSONValue.string("name", in: arguments) else {
                return "error: missing \"name\""
            }
            return readFile(named: name)
        case "write_plugin":
            guard let name = JSONValue.string("name", in: arguments),
                  let source = JSONValue.string("source", in: arguments) else {
                return "error: write_plugin needs \"name\" and \"source\""
            }
            return writePlugin(named: name, source: source)
        case "validate_plugin":
            guard let name = JSONValue.string("name", in: arguments) else {
                return "error: missing \"name\""
            }
            return validate(named: name)
        default:
            return "error: no such tool \"\(call.function.name)\""
        }
    }

    private func listPlugins() -> String {
        guard !registry.plugins.isEmpty else { return "No plugins are installed." }
        return registry.plugins.map { descriptor in
            let version = registry.metadata(for: descriptor.id).version ?? "—"
            return "\(descriptor.id) (\(version))"
        }.joined(separator: "\n")
    }

    private func readFile(named name: String) -> String {
        switch name {
        case "plugin.d.ts", "plugin-dts.txt", "plugin.d.ts.txt":
            let text = PluginDocs.declarations
            return text.isEmpty ? "error: the declarations aren't bundled in this build" : text
        case "plugin-guide.md", "guide":
            let text = PluginDocs.guide
            return text.isEmpty ? "error: the guide isn't bundled in this build" : text
        default:
            break
        }
        // An installed plugin, by id. Never a path — the model doesn't get to
        // name files, only plugins the registry already knows about.
        let id = name.hasSuffix(".js") ? String(name.dropLast(3)) : name
        guard let descriptor = registry.descriptor(for: id) else {
            return "error: no plugin named \"\(id)\". Call list_plugins to see what exists."
        }
        guard let source = try? String(contentsOf: descriptor.sourceURL, encoding: .utf8) else {
            return "error: couldn't read \(id)"
        }
        return source
    }

    private func writePlugin(named name: String, source: String) -> String {
        guard let url = stagedURL(for: name) else {
            return "error: \"\(name)\" isn't a usable plugin name"
        }
        do {
            try source.write(to: url, atomically: true, encoding: .utf8)
        } catch {
            return "error: \(error.localizedDescription)"
        }
        written.insert(url.deletingPathExtension().lastPathComponent)
        let check = PluginMetadata.validate(source: source)
        return check.isOK
            ? "Wrote \(url.lastPathComponent) (\(source.count) bytes). \(check.message)"
            : "Wrote \(url.lastPathComponent), but it is not valid yet: \(check.message)"
    }

    private func validate(named name: String) -> String {
        guard let url = stagedURL(for: name),
              let source = try? String(contentsOf: url, encoding: .utf8) else {
            return "error: nothing written under that name yet"
        }
        return PluginMetadata.validate(source: source).message
    }

    // MARK: - Confinement

    /// A staging path for a plugin name, or nil if the name could escape.
    /// The name is reduced to its last path component and re-resolved, then
    /// checked to be a direct child of the staging directory — so "../..",
    /// an absolute path, or a symlink target all fail.
    func stagedURL(for name: String) -> URL? {
        let base = (name as NSString).lastPathComponent
            .replacingOccurrences(of: "/", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty, base != ".", base != ".." else { return nil }
        let file = base.hasSuffix(".js") ? base : base + ".js"
        let url = stagingURL.appendingPathComponent(file).standardizedFileURL
        guard url.deletingLastPathComponent().standardizedFileURL.path
                == stagingURL.standardizedFileURL.path else { return nil }
        return url
    }
}
