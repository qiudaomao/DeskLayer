//
//  PluginLibraryView.swift
//  DeskLayer
//
//  Left pane: available plugins as a two-level tree grouped by origin
//  (Built-in / Examples / User Installed), each group collapsible. Rows
//  drag onto the virtual desktop, and selecting one shows its details in
//  the inspector.
//

import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct PluginLibraryView: View {
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var selection: ManagerSelection
    @EnvironmentObject private var screens: ScreenManager

    /// Collapsed groups are remembered for the session.
    @State private var collapsed: Set<PluginOrigin> = []

    var body: some View {
        // Native list selection: proper highlight, arrow-key navigation, and
        // it works with VoiceOver/automation (a custom tap gesture doesn't).
        List(selection: pluginSelection) {
            ForEach(PluginOrigin.allCases, id: \.self) { origin in
                let plugins = registry.plugins.filter { $0.origin == origin }
                if !plugins.isEmpty {
                    DisclosureGroup(isExpanded: expansion(for: origin)) {
                        ForEach(plugins) { plugin in
                            PluginRow(plugin: plugin) { addToDesktop(plugin.id) }
                                .tag(plugin.id)
                        }
                    } label: {
                        HStack(spacing: 6) {
                            Text(origin.rawValue)
                            Text("\(plugins.count)")
                                .foregroundStyle(.tertiary)
                                .font(.caption)
                        }
                        .font(.caption.bold())
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("DeskLayer")
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(spacing: 0) {
                Divider()
                HStack(spacing: 2) {
                    Button {
                        importPlugin()
                    } label: {
                        Image(systemName: "plus").frame(width: 22, height: 22)
                    }
                    .help("Import plugin…")
                    Button {
                        NSWorkspace.shared.open(PluginRegistry.directoryURL)
                    } label: {
                        Image(systemName: "folder").frame(width: 22, height: 22)
                    }
                    .help("Open plugins folder")
                    Spacer()
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
            }
        }
    }

    /// List selection ⇄ the shared inspector selection.
    private var pluginSelection: Binding<String?> {
        Binding(
            get: { selection.pluginID },
            set: { newValue in if let newValue { selection.pluginID = newValue } }
        )
    }

    private func expansion(for origin: PluginOrigin) -> Binding<Bool> {
        Binding(
            get: { !collapsed.contains(origin) },
            set: { isExpanded in
                if isExpanded { collapsed.remove(origin) } else { collapsed.insert(origin) }
            }
        )
    }

    private struct PluginRow: View {
        let plugin: PluginDescriptor
        let onAdd: () -> Void
        @State private var isHovering = false

        var body: some View {
            HStack {
                Label(plugin.id, systemImage: "puzzlepiece.extension")
                    .lineLimit(1)
                Spacer()
                Button(action: onAdd) {
                    Image(systemName: "plus.circle.fill")
                        .foregroundStyle(isHovering ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.quaternary))
                }
                .buttonStyle(.borderless)
                .help("Add \(plugin.id) to the desktop")
                .accessibilityLabel("Add \(plugin.id)")
            }
            .contentShape(Rectangle())
            .onHover { isHovering = $0 }
            .draggable(plugin.id)
            .help("Select for details, drag onto the canvas, or click + to add")
        }
    }

    private func addToDesktop(_ pluginID: String) {
        guard let displayUUID = selection.displayUUID else { return }
        let screenSize = screens.controller(forDisplayUUID: displayUUID)?.screen.frame.size
            ?? NSScreen.main?.frame.size
        let size = PluginLibraryView.defaultNormalizedSize(
            preferred: registry.metadata(for: pluginID).preferredSize, screen: screenSize
        )
        let item = LayoutItem(
            pluginID: pluginID,
            displayUUID: displayUUID,
            normalizedFrame: CGRect(x: 0.5 - size.width / 2, y: 0.5 - size.height / 2,
                                    width: size.width, height: size.height),
            zOrder: (store.layout.items.map(\.zOrder).max() ?? 0) + 1
        )
        store.add(item)
        selection.itemID = item.id
    }

    /// A newly added item adopts the plugin's declared point size (converted
    /// to a screen fraction) so its rect matches the content; falls back to
    /// 20% of the screen when the plugin declares no size.
    static func defaultNormalizedSize(preferred: CGSize?, screen: CGSize?) -> CGSize {
        guard let preferred, let screen, screen.width > 0, screen.height > 0 else {
            return CGSize(width: 0.2, height: 0.2)
        }
        return CGSize(
            width: min(preferred.width / screen.width, 1),
            height: min(preferred.height / screen.height, 1)
        )
    }

    private func importPlugin() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "js") ?? .javaScript]
        panel.allowsMultipleSelection = true
        panel.message = "Choose plugin .js files to import"
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            let destination = PluginRegistry.directoryURL.appendingPathComponent(url.lastPathComponent)
            try? FileManager.default.copyItem(at: url, to: destination)
        }
        registry.rescan()
    }
}
