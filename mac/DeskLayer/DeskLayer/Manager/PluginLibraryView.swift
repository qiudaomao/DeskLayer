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
    @EnvironmentObject private var stores: PluginStoreRegistry

    /// Collapsed groups are remembered for the session.
    @State private var collapsed: Set<PluginOrigin> = []
    @State private var isAddingStore = false
    @State private var isCreatingPlugin = false
    @State private var newStoreURL = ""
    @State private var addStoreError: String?

    var body: some View {
        // Native list selection: proper highlight, arrow-key navigation, and
        // it works with VoiceOver/automation (a custom tap gesture doesn't).
        List(selection: pluginSelection) {
            // A fresh install has no plugins and no stores. Real content
            // here, not an empty list: it tells a new user where to start —
            // and an empty sidebar List has been seen driving AppKit's
            // update-constraints loop guard to a crash at first launch.
            if registry.plugins.isEmpty && stores.stores.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text("No plugins yet")
                        .font(.headline)
                    Text("Add the Official Store from the ＋ menu below, or drop a .js plugin into the plugins folder.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.vertical, 8)
            }
            // Everything on disk, whichever store it came from — this is the
            // list of plugins you can actually place. Store categories below
            // list what each store offers.
            if !registry.plugins.isEmpty {
                DisclosureGroup(isExpanded: expansion(for: .user)) {
                    ForEach(registry.plugins) { plugin in
                        PluginRow(plugin: plugin) { addToDesktop(plugin.id) }
                            .tag(plugin.id)
                    }
                } label: {
                    groupLabel(PluginOrigin.user.title, count: registry.plugins.count)
                }
            }
            ForEach(stores.stores) { entry in
                StoreSection(
                    entry: entry,
                    installed: registry.plugins,
                    isExpanded: expansion(for: .store(entry.displayName)),
                    onAddToDesktop: { addToDesktop($0) }
                )
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("DeskLayer")
        .sheet(isPresented: $isCreatingPlugin) {
            CreatePluginSheet { isCreatingPlugin = false }
        }
        .sheet(isPresented: $isAddingStore) {
            AddStoreSheet(url: $newStoreURL, error: $addStoreError) {
                let ok = await stores.addStore(urlString: newStoreURL)
                if ok {
                    isAddingStore = false
                    newStoreURL = ""
                    addStoreError = nil
                } else {
                    addStoreError = "Couldn't read a plugin catalog from that URL."
                }
            } onCancel: {
                isAddingStore = false
                addStoreError = nil
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(spacing: 0) {
                Divider()
                HStack(spacing: 2) {
                    Menu {
                        Button("Add Plugin…") { importPlugin() }
                        Button("Create Plugin…") { isCreatingPlugin = true }
                        Divider()
                        // The app ships no plugins, so the first thing a new
                        // user needs is a store — offer ours by name.
                        ForEach(PresetStore.all) { preset in
                            let added = stores.stores.contains { $0.url == preset.url }
                            Button(added ? "\(preset.name) (added)" : "Add \(preset.name)") {
                                Task { await stores.addStore(urlString: preset.url, mirrors: preset.mirrors) }
                            }
                            .disabled(added)
                        }
                        Divider()
                        Button("Add Plugin Store…") { isAddingStore = true }
                    } label: {
                        Image(systemName: "plus").frame(width: 22, height: 22)
                    }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .frame(width: 26)
                    .help("Add a plugin or a plugin store")
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

    private func groupLabel(_ title: String, count: Int) -> some View {
        HStack(spacing: 6) {
            Text(title)
            Text("\(count)").foregroundStyle(.tertiary).font(.caption)
        }
        .font(.caption.bold())
    }

    /// A store category: its plugins, with installed ones marked. Selecting
    /// the header shows the store's details; selecting a listed plugin shows
    /// its details with an Install button.
    private struct StoreSection: View {
        let entry: PluginStoreEntry
        let installed: [PluginDescriptor]
        let isExpanded: Binding<Bool>
        let onAddToDesktop: (String) -> Void
        @EnvironmentObject private var selection: ManagerSelection
        @EnvironmentObject private var stores: PluginStoreRegistry

        var body: some View {
            let catalog = entry.catalog
            DisclosureGroup(isExpanded: isExpanded) {
                if let plugins = catalog?.plugins, !plugins.isEmpty {
                    ForEach(plugins) { plugin in
                        StorePluginRow(
                            plugin: plugin,
                            storeName: entry.displayName,
                            isInstalled: installed.contains { $0.id == plugin.name },
                            onAddToDesktop: { onAddToDesktop(plugin.name) }
                        )
                        .tag("store:\(entry.id)|\(plugin.name)")
                    }
                } else if let error = entry.lastError {
                    Label(error, systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange).lineLimit(2)
                } else {
                    Text("Loading…").font(.caption).foregroundStyle(.tertiary)
                }
            } label: {
                HStack(spacing: 6) {
                    Text(entry.displayName)
                    Text("\(catalog?.plugins.count ?? 0)")
                        .foregroundStyle(.tertiary).font(.caption)
                    Spacer()
                    Button {
                        Task { await stores.refreshAll() }
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Refresh \(entry.displayName)")
                }
                .font(.caption.bold())
                .contentShape(Rectangle())
            }
            .tag("storehdr:\(entry.id)")
        }
    }

    private struct StorePluginRow: View {
        let plugin: StorePlugin
        let storeName: String
        let isInstalled: Bool
        let onAddToDesktop: () -> Void
        @EnvironmentObject private var stores: PluginStoreRegistry
        @EnvironmentObject private var registry: PluginRegistry
        @State private var isHovering = false
        @State private var isInstalling = false

        var body: some View {
            HStack {
                Label(plugin.name, systemImage: isInstalled ? "puzzlepiece.extension.fill" : "arrow.down.circle")
                    .lineLimit(1)
                    .foregroundStyle(isInstalled ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                if plugin.verified == true {
                    Image(systemName: "checkmark.seal.fill")
                        .font(.caption2).foregroundStyle(.blue)
                        .help("Verified by store staff")
                }
                if let cheers = plugin.cheers, cheers > 0 {
                    Label("\(cheers)", systemImage: "hands.clap")
                        .font(.caption2).foregroundStyle(.tertiary)
                        .labelStyle(.titleAndIcon)
                }
                Spacer()
                if isInstalling {
                    ProgressView().controlSize(.small)
                } else if isInstalled {
                    Button(action: onAddToDesktop) {
                        Image(systemName: "plus.circle.fill")
                            .foregroundStyle(isHovering ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                    }
                    .buttonStyle(.borderless)
                    .help("Add \(plugin.name) to the desktop")
                } else {
                    // Installing straight from the row: the detail pane is one
                    // click further away and says nothing extra for a plugin
                    // the user already decided to install.
                    Button {
                        install()
                    } label: {
                        Image(systemName: "arrow.down.circle.fill")
                            .foregroundStyle(isHovering ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                    }
                    .buttonStyle(.borderless)
                    .help("Install \(plugin.name)")
                }
            }
            .contentShape(Rectangle())
            .onHover { isHovering = $0 }
            .help(isInstalled ? "Installed — select for details"
                  : "Select for details, or click to install")
        }

        private func install() {
            isInstalling = true
            Task {
                _ = await stores.install(plugin, from: storeName, into: PluginRegistry.directoryURL)
                registry.rescan()
                isInstalling = false
            }
        }
    }

    /// One selection binding drives three kinds of sidebar row; tags encode
    /// which kind was picked.
    private var pluginSelection: Binding<String?> {
        Binding(
            get: {
                if let ref = selection.storePlugin { return "store:\(ref.storeID)|\(ref.name)" }
                if let storeID = selection.storeID { return "storehdr:\(storeID)" }
                return selection.pluginID
            },
            set: { newValue in
                guard let newValue else { return }
                if newValue.hasPrefix("storehdr:") {
                    selection.storeID = String(newValue.dropFirst("storehdr:".count))
                } else if newValue.hasPrefix("store:") {
                    let body = newValue.dropFirst("store:".count)
                    if let separator = body.firstIndex(of: "|") {
                        selection.storePlugin = StorePluginRef(
                            storeID: String(body[body.startIndex..<separator]),
                            name: String(body[body.index(after: separator)...])
                        )
                    }
                } else {
                    selection.pluginID = newValue
                }
            }
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
                    // Semantic styles only: a selected row is painted in the
                    // accent colour, so an accent-tinted button on it is
                    // invisible. primary/secondary get inverted by the list.
                    Image(systemName: "plus.circle.fill")
                        .foregroundStyle(isHovering ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
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
        PluginLibraryView.addToDesktop(
            pluginID, store: store, registry: registry, screens: screens, selection: selection
        )
    }

    /// Places a new item centred on the selected display. Shared with the
    /// inspector's "Install & Add to Desktop".
    static func addToDesktop(
        _ pluginID: String,
        store: LayoutStore,
        registry: PluginRegistry,
        screens: ScreenManager,
        selection: ManagerSelection
    ) {
        guard let displayUUID = selection.displayUUID else { return }
        let screenSize = screens.controller(forDisplayUUID: displayUUID)?.screen.frame.size
            ?? NSScreen.main?.frame.size
        let size = defaultNormalizedSize(
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

    private struct AddStoreSheet: View {
        @Binding var url: String
        @Binding var error: String?
        let onAdd: () async -> Void
        let onCancel: () -> Void
        @State private var isWorking = false

        var body: some View {
            VStack(alignment: .leading, spacing: 12) {
                Text("Add Plugin Store").font(.headline)
                Text("A store is a JSON catalog listing plugins you can install. It becomes its own category in the library.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                TextField("https://example.com/plugins.json", text: $url)
                    .textFieldStyle(.roundedBorder)
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Spacer()
                    Button("Cancel", action: onCancel)
                    Button("Add") {
                        isWorking = true
                        Task { await onAdd(); isWorking = false }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(url.isEmpty || isWorking)
                }
            }
            .padding(20)
            .frame(width: 420)
        }
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
