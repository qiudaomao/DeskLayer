//
//  PluginAuthorSession.swift
//  DeskLayer
//
//  Runs the conversation that writes a plugin: system prompt built from the
//  bundled API docs, then a tool-calling loop until the model stops asking
//  for tools or the turn limit is reached.
//
//  Nothing reaches the plugins folder until the run finishes and the result
//  validates. That keeps a half-written plugin off the desktop, and avoids
//  waking the folder watcher on every write — which would rebuild every
//  running item, repeatedly.
//

import Combine
import Foundation
import os

@MainActor
final class PluginAuthorSession: ObservableObject {
    /// One line in the sheet's progress list.
    struct Step: Identifiable, Equatable {
        let id = UUID()
        var text: String
        var detail: String?
        var isError = false
    }

    @Published private(set) var steps: [Step] = []
    @Published private(set) var isRunning = false
    /// Set when a run finishes with a plugin installed.
    @Published private(set) var installedPluginID: String?
    @Published var error: String?

    @Published var settings: LLMSettings = .load() {
        didSet { settings.save() }
    }

    private let registry: PluginRegistry
    private let client = ChatClient()
    private var task: Task<Void, Never>?
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "llm")

    init(registry: PluginRegistry) {
        self.registry = registry
    }

    var apiKey: String {
        get { LLMSettings.apiKey ?? "" }
        set { LLMSettings.apiKey = newValue.isEmpty ? nil : newValue }
    }

    func cancel() {
        task?.cancel()
        task = nil
        isRunning = false
        add("Stopped.")
    }

    /// Asks the model for a plugin and installs what it produces.
    func start(prompt: String) {
        guard !isRunning else { return }
        guard settings.isConfigured else {
            error = String(localized: "Set the base URL and model first.")
            return
        }
        guard PluginDocs.isAvailable else {
            error = String(localized: "This build is missing the plugin API documentation.")
            return
        }
        steps = []
        error = nil
        installedPluginID = nil
        isRunning = true

        let tools = PluginTools(registry: registry)
        task = Task { [weak self] in
            await self?.run(prompt: prompt, tools: tools)
            tools.cleanUp()
        }
    }

    private func run(prompt: String, tools: PluginTools) async {
        defer { isRunning = false }
        let key = LLMSettings.apiKey ?? ""
        var messages: [ChatMessage] = [
            .system(systemPrompt()),
            .user(prompt),
        ]

        add(String(localized: "Asking \(settings.model)…"))

        for turn in 1...max(settings.maxTurns, 1) {
            if Task.isCancelled { return }

            let result = await client.send(messages: messages, tools: PluginTools.specs,
                                           settings: settings, apiKey: key)
            if Task.isCancelled { return }

            switch result {
            case .failed(let message):
                error = message
                add(String(localized: "Failed"), detail: message, isError: true)
                return

            case .text(let text):
                // No more tools wanted: the model is done talking.
                await finish(text: text, tools: tools)
                return

            case .toolCalls(let calls, let assistant):
                messages.append(assistant)
                for call in calls {
                    if Task.isCancelled { return }
                    add(describe(call))
                    let output = tools.run(call)
                    messages.append(.toolResult(output, callID: call.id))
                    if output.hasPrefix("error:") {
                        // Visible in the log, and the model gets it too.
                        steps[steps.count - 1].detail = output
                        steps[steps.count - 1].isError = true
                    }
                }
                if turn == max(settings.maxTurns, 1) {
                    add(String(localized: "Reached the turn limit."), isError: true)
                    await finish(text: "", tools: tools)
                    return
                }
            }
        }
    }

    /// Installs whatever validated, or explains why nothing did.
    private func finish(text: String, tools: PluginTools) async {
        guard let name = tools.written.first else {
            error = text.isEmpty
                ? String(localized: "The model didn't write a plugin.")
                : text
            add(String(localized: "No plugin was written."), detail: text, isError: true)
            return
        }
        guard let staged = tools.stagedURL(for: name),
              let source = try? String(contentsOf: staged, encoding: .utf8) else {
            error = String(localized: "Couldn't read the generated plugin.")
            return
        }
        let check = PluginMetadata.validate(source: source)
        guard check.isOK else {
            error = check.message
            add(String(localized: "The generated plugin isn't valid."), detail: check.message, isError: true)
            return
        }

        let destination = PluginRegistry.directoryURL.appendingPathComponent("\(name).js")
        // Never overwrite a working plugin without a copy to go back to.
        if FileManager.default.fileExists(atPath: destination.path) {
            let backup = destination.appendingPathExtension("bak")
            try? FileManager.default.removeItem(at: backup)
            try? FileManager.default.copyItem(at: destination, to: backup)
            add(String(localized: "Kept the previous version as \(name).js.bak"))
        }
        do {
            try source.write(to: destination, atomically: true, encoding: .utf8)
        } catch {
            self.error = error.localizedDescription
            return
        }
        registry.rescan()
        installedPluginID = name

        let meta = registry.metadata(for: name)
        let permissions = registry.declaredPermissions(for: name)
        var detail = check.message
        if !permissions.isEmpty {
            // Worth reading before placing it: these are real powers.
            detail += " " + String(localized: "Requests: \(permissions.sorted().joined(separator: ", ")).")
        }
        if let version = meta.version { detail += " v\(version)" }
        add(String(localized: "Installed \(name)"), detail: detail)
        log.info("authored plugin \(name, privacy: .public)")
    }

    // MARK: - Prompt

    private func systemPrompt() -> String {
        """
        You write plugins for DeskLayer, a macOS app that renders JavaScript \
        onto the desktop wallpaper, into floating windows, or as widgets.

        Work like this:
        1. Write the plugin with write_plugin.
        2. Call validate_plugin and fix anything it reports.
        3. Reply with one short sentence describing what you made.

        Rules that matter:
        - The runtime is JavaScriptCore. There is no DOM, no window, no \
        document, no require, no Node API. Only the APIs in the declarations exist.
        - render() must RETURN its view tree: `render = () => view([...])`. \
        A block body needs an explicit `return`.
        - render(ctx) with an argument means canvas mode and draws instead.
        - Declare `permissions` only for host APIs you actually call \
        (shell, applescript, ssh, server). Prefer none.
        - Give plugin.export a version, author, description, and a sensible \
        width/height in points.
        - Read plugin.d.ts or plugin-guide.md when unsure. Do not invent APIs.

        Here are the TypeScript declarations for everything available:

        \(PluginDocs.declarations)

        A complete working plugin, for shape:

        ```js
        \(exampleSource())
        ```
        """
    }

    /// A real installed plugin reads better as an example than an invented
    /// one, and proves the shape actually runs in this app.
    private func exampleSource() -> String {
        let preferred = ["HelloCard", "AnalogClock"]
        for id in preferred {
            if let descriptor = registry.descriptor(for: id),
               let source = try? String(contentsOf: descriptor.sourceURL, encoding: .utf8) {
                return source
            }
        }
        return PluginDocs.example()
    }

    private func describe(_ call: ToolCall) -> String {
        let name = JSONValue.string("name", in: call.function.arguments)
        switch call.function.name {
        case "list_plugins": return String(localized: "Listing installed plugins…")
        case "read_file": return String(localized: "Reading \(name ?? "a file")…")
        case "write_plugin": return String(localized: "Writing \(name ?? "the plugin")…")
        case "validate_plugin": return String(localized: "Validating \(name ?? "the plugin")…")
        default: return call.function.name
        }
    }

    private func add(_ text: String, detail: String? = nil, isError: Bool = false) {
        steps.append(Step(text: text, detail: detail, isError: isError))
    }
}
