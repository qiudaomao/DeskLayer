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
        List(registry.plugins) { plugin in
            HStack(spacing: 8) {
                Image(systemName: "puzzlepiece.extension")
                    .foregroundStyle(.secondary)
                Text(plugin.id)
                    .lineLimit(1)
                Spacer()
                Button {
                    addToDesktop(plugin.id)
                } label: {
                    Image(systemName: "plus.circle")
                }
                .buttonStyle(.borderless)
                .help("Add \(plugin.id) to the desktop")
                .accessibilityLabel("Add \(plugin.id)")
            }
            .padding(.vertical, 2)
            .contentShape(Rectangle())
            .draggable(plugin.id)
            .help("Drag onto the desktop canvas to add")
        }
        .listStyle(.sidebar)
        .navigationTitle("Plugins")
        .safeAreaInset(edge: .bottom) {
            HStack {
                Button {
                    importPlugin()
                } label: {
                    Label("Import…", systemImage: "plus")
                }
                Button {
                    NSWorkspace.shared.open(PluginRegistry.directoryURL)
                } label: {
                    Label("Open Folder", systemImage: "folder")
                }
                Spacer()
            }
            .buttonStyle(.borderless)
            .padding(8)
            .background(.bar)
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
