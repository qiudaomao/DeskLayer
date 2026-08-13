//
//  CommunityGalleryView.swift
//  DeskLayer
//
//  The community gallery: a paged thumbnail grid of everything published to
//  the community store, sortable by cheers, downloads, or recency. Selected
//  from the sidebar; replaces the desktop canvas while open.
//
//  The sort control is hand-rolled on purpose — a platform-view Picker in
//  this pane is what crashed multi-display Macs at launch (see
//  DesktopCanvasView's display switcher).
//

import AppKit
import SwiftUI

struct CommunityGalleryView: View {
    @StateObject private var gallery = CommunityGallery()
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var stores: PluginStoreRegistry

    private let columns = [GridItem(.adaptive(minimum: 210, maximum: 280), spacing: 14)]

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            footer
        }
        .navigationTitle("Community")
        .onAppear { if gallery.plugins.isEmpty { gallery.load() } }
    }

    private var header: some View {
        HStack {
            HStack(spacing: 2) {
                ForEach(GallerySort.allCases) { sort in
                    let isSelected = gallery.sort == sort
                    Button {
                        gallery.sort = sort
                    } label: {
                        Text(sort.title)
                            .font(.subheadline)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 3)
                            .background(
                                RoundedRectangle(cornerRadius: 5)
                                    .fill(isSelected ? AnyShapeStyle(.background) : AnyShapeStyle(.clear))
                                    .shadow(color: .black.opacity(isSelected ? 0.15 : 0), radius: 1, y: 0.5)
                            )
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(2)
            .background(RoundedRectangle(cornerRadius: 7).fill(.quaternary.opacity(0.5)))

            // A chip, not a Toggle: same platform-view caution as the sort
            // control above.
            Button {
                gallery.verifiedOnly.toggle()
            } label: {
                Label("Verified", systemImage: gallery.verifiedOnly ? "checkmark.seal.fill" : "checkmark.seal")
                    .font(.subheadline)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 3)
                    .background(
                        RoundedRectangle(cornerRadius: 7)
                            .fill(gallery.verifiedOnly ? AnyShapeStyle(.blue.opacity(0.2)) : AnyShapeStyle(.quaternary.opacity(0.5)))
                    )
            }
            .buttonStyle(.plain)
            .help("Only staff-verified plugins")

            TextField("Search", text: $gallery.query)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 200)
                .onSubmit { gallery.load(page: 1) }

            Spacer()
            if gallery.isLoading {
                ProgressView().controlSize(.small)
            } else {
                Text("\(gallery.total) plugins")
                    .font(.caption).foregroundStyle(.tertiary)
            }
        }
        .padding(10)
    }

    @ViewBuilder
    private var content: some View {
        if let error = gallery.error {
            VStack(spacing: 8) {
                Label(error, systemImage: "wifi.exclamationmark")
                    .foregroundStyle(.secondary)
                Button("Retry") { gallery.load(page: gallery.page) }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if gallery.plugins.isEmpty && !gallery.isLoading {
            Text("Nothing published yet — be the first.")
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVGrid(columns: columns, spacing: 14) {
                    ForEach(gallery.plugins) { plugin in
                        GalleryTile(
                            plugin: plugin,
                            isInstalled: registry.plugins.contains { $0.id == plugin.name }
                        )
                    }
                }
                .padding(14)
            }
        }
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button {
                gallery.load(page: gallery.page - 1)
            } label: {
                Image(systemName: "chevron.left")
            }
            .buttonStyle(.borderless)
            .disabled(gallery.page <= 1 || gallery.isLoading)
            Text("\(gallery.page) / \(gallery.pages)")
                .font(.caption).foregroundStyle(.secondary)
                .monospacedDigit()
            Button {
                gallery.load(page: gallery.page + 1)
            } label: {
                Image(systemName: "chevron.right")
            }
            .buttonStyle(.borderless)
            .disabled(gallery.page >= gallery.pages || gallery.isLoading)
            Spacer()
        }
        .padding(6)
    }
}

private struct GalleryTile: View {
    let plugin: GalleryPlugin
    let isInstalled: Bool
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var stores: PluginStoreRegistry
    @State private var isInstalling = false
    @State private var installError: String?
    @State private var showsDetail = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            preview
                .frame(height: 110)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary))
                .contentShape(Rectangle())
                .onTapGesture { showsDetail = true }

            HStack(spacing: 4) {
                Text(plugin.name).font(.headline).lineLimit(1)
                if plugin.verified == true {
                    Image(systemName: "checkmark.seal.fill")
                        .font(.caption).foregroundStyle(.blue)
                        .help("Verified by store staff")
                }
                Spacer()
            }

            if let author = plugin.author {
                Text(author).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }

            HStack(spacing: 10) {
                Label("\(plugin.cheers ?? 0)", systemImage: "hands.clap")
                Label("\(plugin.downloads ?? 0)", systemImage: "arrow.down.circle")
                if let date = plugin.publishedDate {
                    Text(date, format: .relative(presentation: .named))
                        .lineLimit(1)
                }
                Spacer()
            }
            .font(.caption2).foregroundStyle(.tertiary)

            HStack {
                if isInstalling {
                    ProgressView().controlSize(.small)
                } else if isInstalled {
                    Label("Installed", systemImage: "checkmark.circle.fill")
                        .font(.caption).foregroundStyle(.green)
                } else {
                    Button("Install") { install() }
                        .controlSize(.small)
                }
                Spacer()
                Button {
                    showsDetail = true
                } label: {
                    Image(systemName: "bubble.left.and.bubble.right")
                }
                .buttonStyle(.borderless)
                .help("Cheer and discuss")
            }
            if let installError {
                Text(installError).font(.caption2).foregroundStyle(.orange)
                    .lineLimit(2)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(.quaternary.opacity(0.35)))
        .help(plugin.description ?? plugin.name)
        .sheet(isPresented: $showsDetail) {
            GalleryDetailSheet(plugin: plugin, isInstalled: isInstalled) { showsDetail = false }
        }
    }

    @ViewBuilder
    private var preview: some View {
        // Grid tiles load the small thumbnail only — `preview` can be 2MB.
        if let thumbnail = plugin.thumbnail, let url = URL(string: thumbnail) {
            AsyncImage(url: url) { phase in
                switch phase {
                case .success(let image):
                    image.resizable().scaledToFill()
                default:
                    placeholder
                }
            }
        } else {
            placeholder
        }
    }

    private var placeholder: some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.16, green: 0.2, blue: 0.32),
                                    Color(red: 0.08, green: 0.09, blue: 0.16)],
                           startPoint: .top, endPoint: .bottom)
            Image(systemName: "puzzlepiece.extension")
                .font(.largeTitle).foregroundStyle(.white.opacity(0.35))
        }
    }

    private func install() {
        isInstalling = true
        installError = nil
        Task {
            // Same path as installing from an added store, so the plugin is
            // recorded as store-owned (updates find it; rewrites become
            // copies). The origin name matches the community catalog's.
            let error = await stores.install(plugin.asStorePlugin, from: "DeskLayer Community",
                                             into: PluginRegistry.directoryURL)
            installError = error
            registry.rescan()
            isInstalling = false
        }
    }
}

/// Full-size preview, live cheer toggle, and the forum comments — read and
/// write — without leaving the app. The backend relays to the forum as the
/// signed-in user.
private struct GalleryDetailSheet: View {
    let plugin: GalleryPlugin
    let isInstalled: Bool
    let onClose: () -> Void
    @EnvironmentObject private var account: CommunityAccount
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var stores: PluginStoreRegistry

    @State private var cheers: Int?
    @State private var cheered = false
    @State private var isCheering = false
    @State private var comments: [CommunityAccount.PluginComment] = []
    @State private var commentsLoaded = false
    @State private var draft = ""
    @State private var isSending = false
    @State private var socialError: String?
    @State private var isInstalling = false

    /// Discourse forbids liking your own post; hide the ability up front.
    private var isOwnPlugin: Bool {
        account.user.map { $0.username == plugin.author } ?? false
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Text(plugin.name).font(.headline)
                if plugin.verified == true {
                    Image(systemName: "checkmark.seal.fill").foregroundStyle(.blue)
                        .help("Verified by store staff")
                }
                if let version = plugin.version {
                    Text(version).font(.caption).foregroundStyle(.tertiary)
                }
                Spacer()
                if let author = plugin.author {
                    Text(author).font(.caption).foregroundStyle(.secondary)
                }
            }

            if let preview = plugin.preview, let url = URL(string: preview) {
                AsyncImage(url: url) { phase in
                    if case .success(let image) = phase {
                        image.resizable().scaledToFit()
                    }
                }
                .frame(maxHeight: 180)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            }

            if let description = plugin.description {
                Text(description).font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .lineLimit(4)
            }

            HStack(spacing: 12) {
                // Cheer = a forum like on the showcase post, toggled in-app.
                Button {
                    cheer()
                } label: {
                    Label("\(cheers ?? plugin.cheers ?? 0)",
                          systemImage: cheered ? "hands.clap.fill" : "hands.clap")
                }
                .disabled(isCheering || isOwnPlugin || !account.isSignedIn)
                .help(isOwnPlugin ? String(localized: "You can't cheer your own plugin.")
                                  : String(localized: "Cheer this plugin"))
                Label("\(plugin.downloads ?? 0)", systemImage: "arrow.down.circle")
                    .foregroundStyle(.secondary)
                Spacer()
                if !isInstalled {
                    Button(isInstalling ? String(localized: "Installing…") : String(localized: "Install")) {
                        install()
                    }
                    .disabled(isInstalling)
                }
                if let topic = plugin.topicUrl, let url = URL(string: topic) {
                    Button {
                        NSWorkspace.shared.open(url)
                    } label: {
                        Image(systemName: "safari")
                    }
                    .buttonStyle(.borderless)
                    .help("Open the forum topic in the browser")
                }
            }

            Divider()

            // Comments, chronological, straight from the forum topic.
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    if comments.isEmpty {
                        Text(commentsLoaded ? String(localized: "No comments yet.")
                                            : String(localized: "Loading comments…"))
                            .font(.caption).foregroundStyle(.tertiary)
                    }
                    ForEach(comments) { comment in
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) {
                                Text(comment.author).font(.caption.bold())
                                if let date = comment.createdDate {
                                    Text(date, format: .relative(presentation: .named))
                                        .font(.caption2).foregroundStyle(.tertiary)
                                }
                                if let likes = comment.likes, likes > 0 {
                                    Label("\(likes)", systemImage: "heart")
                                        .font(.caption2).foregroundStyle(.tertiary)
                                }
                            }
                            // Markdown source; render best-effort.
                            Text((try? AttributedString(markdown: comment.text)) ?? AttributedString(comment.text))
                                .font(.caption)
                                .fixedSize(horizontal: false, vertical: true)
                                .textSelection(.enabled)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(minHeight: 80, maxHeight: 180)

            if let socialError {
                // Discourse's own message (rate limits, trust levels) —
                // already human-readable, shown verbatim.
                Label(socialError, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if account.isSignedIn {
                HStack {
                    TextField("Add a comment…", text: $draft)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { send() }
                    Button(isSending ? String(localized: "Sending…") : String(localized: "Send")) { send() }
                        .disabled(isSending || draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            } else if account.isLoggingIn {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Waiting for the browser sign-in…")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Cancel") { account.cancelSignIn() }
                        .buttonStyle(.borderless).font(.caption)
                }
            } else {
                Button {
                    account.signIn()
                } label: {
                    Label("Sign In to Cheer and Comment…", systemImage: "person.crop.circle.badge.checkmark")
                }
                .buttonStyle(.borderless)
            }

            HStack {
                Spacer()
                Button("Close", action: onClose)
            }
        }
        .padding(16)
        .frame(width: 460)
        .task { await loadSocial() }
        .onChange(of: account.isSignedIn) { _, signedIn in
            if signedIn { Task { await loadSocial() } }
        }
    }

    private func loadSocial() async {
        if let detail = await account.liveDetail(slug: plugin.slug) {
            cheers = detail.cheers
            cheered = detail.cheered ?? false
        }
        switch await account.comments(slug: plugin.slug) {
        case .success(let page): comments = page.comments
        case .failure(let error): socialError = error.message
        }
        commentsLoaded = true
    }

    private func cheer() {
        isCheering = true
        socialError = nil
        Task {
            switch await account.cheer(slug: plugin.slug) {
            case .success(let state):
                cheered = state.cheered
                cheers = state.cheers
            case .failure(let error):
                socialError = error.message
            }
            isCheering = false
        }
    }

    private func send() {
        let body = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty, !isSending else { return }
        isSending = true
        socialError = nil
        Task {
            switch await account.postComment(slug: plugin.slug, body: body) {
            case .success(let comment):
                comments.append(comment)
                draft = ""
            case .failure(let error):
                socialError = error.message
            }
            isSending = false
        }
    }

    private func install() {
        isInstalling = true
        Task {
            let error = await stores.install(plugin.asStorePlugin, from: "DeskLayer Community",
                                             into: PluginRegistry.directoryURL)
            if let error { socialError = error }
            registry.rescan()
            isInstalling = false
        }
    }
}
