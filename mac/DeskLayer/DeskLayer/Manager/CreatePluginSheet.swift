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
    let onClose: () -> Void

    @State private var prompt = ""
    @State private var apiKey = ""
    @State private var showsEndpoint = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Create Plugin").font(.headline)
            Text("Describe what you want. The model is given DeskLayer's plugin API and writes the JavaScript; nothing is installed until it passes validation.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextEditor(text: $prompt)
                .font(.body)
                .frame(height: 70)
                .overlay(alignment: .topLeading) {
                    if prompt.isEmpty {
                        Text("A clock with a sweeping second hand and the date underneath")
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
                    TextField("Model", text: $author.settings.model)
                    SecureField("API key", text: $apiKey)
                        .onChange(of: apiKey) { _, newValue in author.apiKey = newValue }
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
                    Button("Create") { author.start(prompt: prompt) }
                        .keyboardShortcut(.defaultAction)
                        .disabled(prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            apiKey = author.apiKey
            // Nudge the user to the settings when there is nothing to call.
            showsEndpoint = author.apiKey.isEmpty || !author.settings.isConfigured
        }
    }
}
