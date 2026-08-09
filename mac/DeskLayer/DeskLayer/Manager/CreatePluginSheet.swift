//
//  CreatePluginSheet.swift
//  DeskLayer
//
//  Describe a plugin, and a model writes it. The endpoint settings live here
//  rather than in a Preferences window because the app has none — store URLs
//  are configured the same way.
//

import SwiftUI

struct CreatePluginSheet: View {
    @EnvironmentObject private var author: PluginAuthorSession
    @EnvironmentObject private var selection: ManagerSelection
    @EnvironmentObject private var registry: PluginRegistry
    let onClose: () -> Void

    @State private var prompt = ""
    @State private var apiKey = ""
    @State private var showsEndpoint = false
    /// Empty means "write something new"; otherwise the plugin to change.
    @State private var basePluginID = ""
    @State private var replacesBase = true
    /// Set when the user wants a model the endpoint didn't list.
    @State private var typesModel = false

    private var subject: PluginAuthorSession.Subject {
        guard !basePluginID.isEmpty else { return .newPlugin }
        // Store plugins are copied, never replaced — the session enforces the
        // same rule, this just keeps the UI honest about it.
        guard replacesBase, !baseIsFromStore else { return .copy(of: basePluginID) }
        return .replace(basePluginID)
    }

    /// The store a base plugin was installed from, if any.
    private var baseStoreName: String? {
        basePluginID.isEmpty ? nil : PluginStoreRegistry.storeName(forPlugin: basePluginID)
    }

    private var baseIsFromStore: Bool { baseStoreName != nil }

    /// Fetched models, with whatever is currently selected kept in the list so
    /// a hand-typed name doesn't vanish when the picker appears.
    private var modelChoices: [String] {
        var list = author.settings.cachedModels
        let current = author.settings.model.trimmingCharacters(in: .whitespaces)
        if !current.isEmpty, !list.contains(current) { list.insert(current, at: 0) }
        return list
    }

    private var canFetchModels: Bool {
        author.settings.modelsURL != nil && !apiKey.isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Create Plugin").font(.headline)
            Text("Describe what you want. The model is given DeskLayer's plugin API and writes the JavaScript; nothing is installed until it passes validation.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Picker("Start from", selection: $basePluginID) {
                Text("A new plugin").tag("")
                ForEach(registry.plugins) { plugin in
                    Text(plugin.id).tag(plugin.id)
                }
            }
            .disabled(author.isRunning)

            if !basePluginID.isEmpty {
                if let store = baseStoreName {
                    // Replacing in place would be undone by the store's next
                    // update, silently. Only the copy is offered.
                    Text("Result: a copy")
                    Text("\(basePluginID) was installed from \(store). An update from the store would overwrite changes made here, so the rewrite is saved as a separate plugin.")
                        .font(.caption2).foregroundStyle(.tertiary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Picker("Result", selection: $replacesBase) {
                        Text("Replace \(basePluginID)").tag(true)
                        Text("Keep both, make a copy").tag(false)
                    }
                    .pickerStyle(.radioGroup)
                    .disabled(author.isRunning)
                    if replacesBase {
                        Text("The current version is kept as \(basePluginID).js.bak.")
                            .font(.caption2).foregroundStyle(.tertiary)
                    }
                }
            }

            TextEditor(text: $prompt)
                .font(.body)
                .frame(height: 70)
                .overlay(alignment: .topLeading) {
                    if prompt.isEmpty {
                        Text(basePluginID.isEmpty
                             ? "A clock with a sweeping second hand and the date underneath"
                             : "Make the bars thinner and show the temperature too")
                            .foregroundStyle(.tertiary)
                            .padding(.top, 8).padding(.leading, 5)
                            .allowsHitTesting(false)
                    }
                }
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.quaternary))
                .disabled(author.isRunning)

            DisclosureGroup(isExpanded: $showsEndpoint) {
                VStack(alignment: .leading, spacing: 6) {
                    TextField("Base URL", text: $author.settings.baseURL)
                    SecureField("API key", text: $apiKey)
                        .onChange(of: apiKey) { _, newValue in author.apiKey = newValue }
                    // Below the key: fetching the list needs it.
                    HStack(spacing: 6) {
                        if author.settings.cachedModels.isEmpty || typesModel {
                            TextField("Model", text: $author.settings.model)
                        } else {
                            Picker("Model", selection: $author.settings.model) {
                                ForEach(modelChoices, id: \.self) { Text($0).tag($0) }
                            }
                            .labelsHidden()
                        }
                        if !author.settings.cachedModels.isEmpty {
                            Button {
                                typesModel.toggle()
                            } label: {
                                Image(systemName: typesModel ? "list.bullet" : "pencil")
                            }
                            .help(typesModel ? "Choose from the fetched models"
                                             : "Type a model name instead")
                        }
                        Button(author.isFetchingModels ? "Fetching…" : "Fetch Models") {
                            typesModel = false
                            author.fetchModels()
                        }
                        .disabled(!canFetchModels || author.isFetchingModels)
                    }
                    Text("Any OpenAI-compatible endpoint. The key is stored in your login Keychain.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
                .textFieldStyle(.roundedBorder)
                .padding(.top, 4)
            } label: {
                Text("Endpoint").font(.caption)
            }

            if !author.steps.isEmpty {
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(author.steps) { step in
                            HStack(alignment: .firstTextBaseline, spacing: 6) {
                                Image(systemName: step.isError
                                      ? "exclamationmark.triangle.fill" : "checkmark.circle")
                                    .font(.caption2)
                                    .foregroundStyle(step.isError ? AnyShapeStyle(.orange) : AnyShapeStyle(.tertiary))
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(step.text).font(.caption)
                                    if let detail = step.detail {
                                        Text(detail).font(.caption2).foregroundStyle(.secondary)
                                            .fixedSize(horizontal: false, vertical: true)
                                    }
                                }
                                Spacer()
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(height: 120)
            }

            if let error = author.error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                if let installed = author.installedPluginID {
                    Button("Show \(installed)") {
                        selection.pluginID = installed
                        onClose()
                    }
                }
                Spacer()
                Button("Close", action: onClose)
                if author.isRunning {
                    Button("Stop") { author.cancel() }
                } else {
                    Button(basePluginID.isEmpty ? "Create" : "Rewrite") {
                        author.start(prompt: prompt, subject: subject)
                    }
                        .keyboardShortcut(.defaultAction)
                        .disabled(prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        .padding(20)
        .frame(width: 460)
        .onChange(of: basePluginID) { _, _ in author.clearResult() }
        .onAppear {
            apiKey = author.apiKey
            // Selecting a plugin in the library first is the natural way to
            // say "change this one".
            if let selected = selection.pluginID, registry.descriptor(for: selected) != nil {
                basePluginID = selected
            }
            // Nudge the user to the settings when there is nothing to call.
            showsEndpoint = author.apiKey.isEmpty || !author.settings.isConfigured
        }
    }
}
