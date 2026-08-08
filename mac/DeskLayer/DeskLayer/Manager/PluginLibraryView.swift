//
//  PluginLibraryView.swift
//  DeskLayer
//
//  Left pane: available plugins. Rows drag onto the virtual desktop
//  (payload = pluginID string; drags never leave the app in v1).
//

import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct PluginLibraryView: View {
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var selection: ManagerSelection

    var body: some View {
        List {
            Section("Plugins") {
                ForEach(registry.plugins) { plugin in
                    PluginRow(plugin: plugin) {
                        addToDesktop(plugin.id)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("DeskLayer")
        .safeAreaInset(edge: .bottom, spacing: 0) {
            // Bottom action bar, Notes/Mail-style.
            VStack(spacing: 0) {
                Divider()
                HStack(spacing: 2) {
                    Button {
                        importPlugin()
                    } label: {
                        Image(systemName: "plus")
                            .frame(width: 22, height: 22)
                    }
                    .help("Import plugin…")
                    Button {
                        NSWorkspace.shared.open(PluginRegistry.directoryURL)
                    } label: {
                        Image(systemName: "folder")
                            .frame(width: 22, height: 22)
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

    private struct PluginRow: View {
        let plugin: PluginDescriptor
        let onAdd: () -> Void
        @State private var isHovering = false

        var body: some View {
            HStack {
                Label(plugin.id, systemImage: "puzzlepiece.extension")
                    .lineLimit(1)
                Spacer()
                // Always present (visible + accessible); emphasized on hover.
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
            .help("Drag onto the desktop canvas, or click + to add")
        }
    }

    private func addToDesktop(_ pluginID: String) {
        guard let displayUUID = selection.displayUUID else { return }
        let size = CGSize(width: 0.2, height: 0.2)
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
