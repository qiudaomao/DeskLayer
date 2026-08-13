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
    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @EnvironmentObject private var store: LayoutStore

    @State private var version = ""
    @State private var descriptionText = ""
    @State private var isPublishing = false
    @State private var result: PublishResult?
    /// Showcase screenshot, captured from a running instance of the plugin.
    @State private var previewPng: Data?

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

                if let previewPng, let image = NSImage(data: previewPng) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Showcase screenshot").font(.caption).foregroundStyle(.secondary)
                        Image(nsImage: image)
                            .resizable().scaledToFit()
                            .frame(maxHeight: 110)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.quaternary))
                        HStack(spacing: 12) {
                            Button("Recapture") { capturePreview() }
                            Button("Remove") { self.previewPng = nil }
                        }
                        .buttonStyle(.borderless).font(.caption)
                    }
                } else {
                    // A listing with a screenshot reads far better; nudge,
                    // don't block.
                    Text("No screenshot: add \(pluginID) to the desktop and it will be captured from the running plugin.")
                        .font(.caption2).foregroundStyle(.tertiary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else if account.isLoggingIn {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Waiting for the browser sign-in… Finish signing in on the forum page that just opened. First time? Registering and confirming your email can take a few minutes — this will wait.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("Cancel") { account.cancelSignIn() }
                        .buttonStyle(.borderless).font(.caption)
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
                // Close leaves an in-flight sign-in polling: a first-time
                // user may still be registering in the browser, and the
                // token should land whenever they finish.
                Button("Close", action: onClose)
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
            capturePreview()
        }
    }

    /// Stamps the signed-in forum username into the source's declared
    /// author, so the downloaded/inspected copy agrees with the store's
    /// attribution instead of showing a template's "DeskLayer". Exactly one
    /// existing author literal is replaced; zero or several means the shape
    /// is unclear, and guessing would corrupt someone's source — no-op.
    /// (Same rule as the Windows port's StampAuthor.)
    static func stampAuthor(in source: String, username: String) -> String {
        let trimmed = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return source }
        let pattern = #"(author\s*:\s*)("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return source }
        let range = NSRange(source.startIndex..., in: source)
        let matches = regex.matches(in: source, range: range)
        guard matches.count == 1, let match = matches.first,
              let literal = Range(match.range(at: 2), in: source) else { return source }
        let escaped = trimmed
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return source.replacingCharacters(in: literal, with: "\"\(escaped)\"")
    }

    /// The gallery-grid image: the preview downscaled to ≤480px wide,
    /// within the store's 256KB thumbnail cap. Grids never load the full
    /// preview, so a publish without this renders as a placeholder tile.
    ///
    /// Drawn into a CGContext of exact pixel dimensions — NSImage.lockFocus
    /// renders at the screen's backing scale, which on Retina silently
    /// doubled the pixels, blew the size cap, and dropped the thumbnail.
    static func thumbnail(from previewPng: Data) -> Data? {
        guard let source = CGImageSourceCreateWithData(previewPng as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
              image.width > 0, image.height > 0 else { return nil }
        // 480 wide normally; one narrower retry if a busy image still
        // overflows the cap as PNG.
        for maxWidth in [480.0, 320.0] {
            let scale = min(maxWidth / Double(image.width), 1)
            let width = Int((Double(image.width) * scale).rounded(.down))
            let height = Int((Double(image.height) * scale).rounded(.down))
            guard width > 0, height > 0,
                  let context = CGContext(
                      data: nil, width: width, height: height,
                      bitsPerComponent: 8, bytesPerRow: 0,
                      space: CGColorSpaceCreateDeviceRGB(),
                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                  ) else { return nil }
            context.interpolationQuality = .high
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            guard let scaled = context.makeImage() else { return nil }
            let rep = NSBitmapImageRep(cgImage: scaled)
            if let png = rep.representation(using: .png, properties: [:]),
               png.count <= 256 * 1024 {
                return png
            }
        }
        return nil
    }

    /// Grabs the latest rendered frame of any placed instance of this plugin
    /// — the same throttled thumbnail the manager's canvas shows. PNG, and
    /// only if it fits the store's 2MB cap.
    private func capturePreview() {
        let itemIDs = store.layout.items.filter { $0.pluginID == pluginID }.map(\.id)
        guard let image = itemIDs.compactMap({ coordinator.thumbnails[$0] })
            .max(by: { $0.width * $0.height < $1.width * $1.height }) else { return }
        let rep = NSBitmapImageRep(cgImage: image)
        guard let png = rep.representation(using: .png, properties: [:]),
              png.count <= 2 * 1024 * 1024 else { return }
        previewPng = png
    }

    private func publish() {
        guard let descriptor = registry.descriptor(for: pluginID),
              var source = try? String(contentsOf: descriptor.sourceURL, encoding: .utf8) else {
            result = .failed(String(localized: "Couldn't read the plugin file."))
            return
        }
        // The published copy carries the publisher's name; the local file
        // stays as written.
        if let username = account.user?.username {
            source = Self.stampAuthor(in: source, username: username)
        }
        isPublishing = true
        result = nil
        Task {
            result = await account.publish(
                name: pluginID,
                version: version.trimmingCharacters(in: .whitespaces),
                description: descriptionText.trimmingCharacters(in: .whitespacesAndNewlines),
                source: source,
                permissions: Array(registry.declaredPermissions(for: pluginID)),
                previewPng: previewPng,
                thumbnailPng: previewPng.flatMap(Self.thumbnail(from:))
            )
            isPublishing = false
        }
    }
}
