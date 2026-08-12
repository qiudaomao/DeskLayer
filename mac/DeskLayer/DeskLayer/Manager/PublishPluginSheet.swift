//
//  PublishPluginSheet.swift
//  DeskLayer
//
//  Publishes a local plugin to the community store. Signing in happens in
//  the browser (forum account, device-code flow); publishing is one click
//  and auto-creates the plugin's forum showcase topic.
//

import AppKit
import SwiftUI

struct PublishPluginSheet: View {
    let pluginID: String
    let onClose: () -> Void
    @EnvironmentObject private var account: CommunityAccount
    @EnvironmentObject private var registry: PluginRegistry

    @State private var version = ""
    @State private var descriptionText = ""
    @State private var isPublishing = false
    @State private var result: PublishResult?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Publish to Community").font(.headline)
            Text("Publishing shares \(pluginID) in the community store and opens a forum topic under your account, where people can discuss and cheer it.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if account.isSignedIn, let user = account.user {
                LabeledContent("Account") {
                    HStack(spacing: 8) {
                        Text(user.name?.isEmpty == false ? user.name! : user.username)
                        Button("Sign Out") { account.signOut() }
                            .buttonStyle(.borderless)
                            .font(.caption)
                    }
                }

                LabeledContent("Plugin", value: pluginID)
                TextField("Version", text: $version)
                    .textFieldStyle(.roundedBorder)
                TextField("Description", text: $descriptionText, axis: .vertical)
                    .lineLimit(2...4)
                    .textFieldStyle(.roundedBorder)
                let permissions = registry.declaredPermissions(for: pluginID)
                if !permissions.isEmpty {
                    // Shown up front because the listing will show it too.
                    LabeledContent("Permissions", value: permissions.sorted().joined(separator: ", "))
                        .font(.caption)
                }
            } else if account.isLoggingIn {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Waiting for the browser sign-in… Finish signing in on the forum page that just opened.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text("Publishing uses your forum account (bbs.byteplayer.app). Signing in opens the forum in your browser — no password ever passes through the app.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button {
                    account.signIn()
                } label: {
                    Label("Sign In with the Forum…", systemImage: "person.crop.circle.badge.checkmark")
                }
            }

            if let error = account.loginError {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            switch result {
            case .published(let slug, let version, let topicUrl):
                VStack(alignment: .leading, spacing: 6) {
                    Label(String(localized: "Published \(slug) \(version)"), systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    if let topicUrl, let url = URL(string: topicUrl) {
                        Button {
                            NSWorkspace.shared.open(url)
                        } label: {
                            Label("Open the Forum Topic", systemImage: "bubble.left.and.bubble.right")
                        }
                        .buttonStyle(.borderless)
                    }
                }
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            case nil:
                EmptyView()
            }

            HStack {
                Spacer()
                Button("Close") {
                    account.cancelSignIn()
                    onClose()
                }
                if account.isSignedIn, case .published = result {
                    // Done; Close is the only sensible action left.
                } else if account.isSignedIn {
                    Button(isPublishing ? "Publishing…" : "Publish") { publish() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(isPublishing
                                  || version.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .padding(20)
        .frame(width: 440)
        .onAppear {
            let meta = registry.metadata(for: pluginID)
            version = meta.version ?? "1.0.0"
            descriptionText = meta.summary ?? ""
        }
    }

    private func publish() {
        guard let descriptor = registry.descriptor(for: pluginID),
              let source = try? String(contentsOf: descriptor.sourceURL, encoding: .utf8) else {
            result = .failed(String(localized: "Couldn't read the plugin file."))
            return
        }
        isPublishing = true
        result = nil
        Task {
            result = await account.publish(
                name: pluginID,
                version: version.trimmingCharacters(in: .whitespaces),
                description: descriptionText.trimmingCharacters(in: .whitespacesAndNewlines),
                source: source,
                permissions: Array(registry.declaredPermissions(for: pluginID))
            )
            isPublishing = false
        }
    }
}
