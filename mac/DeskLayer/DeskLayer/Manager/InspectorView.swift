//
//  InspectorView.swift
//  DeskLayer
//
//  Right pane: properties of the selected item. Editors are generated from
//  the plugin's declared valueTypes; every commit pushes straight into the
//  running JS context via the coordinator (no rebuild except fps).
//

import DeskLayerKit
import SwiftUI

struct InspectorView: View {
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var screens: ScreenManager
    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @EnvironmentObject private var selection: ManagerSelection

    var body: some View {
        if let ref = selection.storePlugin {
            StorePluginDetailView(ref: ref)
        } else if let storeID = selection.storeID {
            StoreDetailView(storeID: storeID)
        } else if let pluginID = selection.pluginID {
            PluginDetailView(pluginID: pluginID)
        } else if let item = selectedItem {
            Form {
                Section(item.pluginID) {
                    if let error = coordinator.errorMessage(for: item.id) {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.yellow)
                            .font(.caption)
                    }
                    Toggle("Enabled", isOn: binding(for: item) { $0.isEnabled } set: { $0.isEnabled = $1 })
                    Picker("Show as", selection: binding(for: item) { $0.target } set: { $0.target = $1 }) {
                        Text("Wallpaper").tag(RenderTarget.wallpaper)
                        Text("Floating Window").tag(RenderTarget.floatingWindow)
                    }
                    .pickerStyle(.segmented)
                    if item.target == .floatingWindow {
                        Toggle(
                            "Click-through",
                            isOn: binding(for: item) { $0.clickThrough } set: { $0.clickThrough = $1 }
                        )
                        .help("On: clicks pass through to windows beneath. Off: the window accepts mouse events and can be dragged.")
                    }
                    Picker("Display", selection: binding(for: item) { $0.displayUUID } set: { $0.displayUUID = $1 }) {
                        ForEach(Array(screens.controllers.values), id: \.displayUUID) { controller in
                            Text(controller.screen.localizedName).tag(controller.displayUUID)
                        }
                        if screens.controller(forDisplayUUID: item.displayUUID) == nil {
                            Text("Offline display").tag(item.displayUUID)
                        }
                    }
                    Stepper(
                        "Z-order: \(item.zOrder)",
                        value: binding(for: item) { $0.zOrder } set: { $0.zOrder = $1 }
                    )
                    BackgroundColorEditor(
                        hex: item.backgroundColor,
                        onChange: { newValue in
                            var updated = item
                            updated.backgroundColor = newValue
                            store.update(updated)
                        }
                    )
                    .id(item.id)
                }

                Section("About & Updates") {
                    PluginAboutView(pluginID: item.pluginID).id(item.pluginID)
                }

                let permissions = registry.declaredPermissions(for: item.pluginID)
                if permissions.contains("ssh") {
                    Section("SSH Destinations") {
                        // Route through the coordinator's no-rebuild path so
                        // typing doesn't flash every on-screen widget.
                        SSHEditor(item: item) { updated in coordinator.updateSSH(updated) }
                            .id(item.id)
                    }
                }

                Section("Frame (points)") {
                    let metadata = registry.metadata(for: item.pluginID)
                    let resizable = metadata.resizable
                    FrameEditor(
                        item: item,
                        metadata: metadata,
                        screenSize: screenSize(for: item)
                    ) { newFrame in
                        coordinator.setFrame(itemID: item.id, normalizedFrame: newFrame, commit: true)
                    }
                    .id(item.id)
                    if !resizable {
                        Text("This plugin declares a fixed size (resizable: false).")
                            .font(.caption2).foregroundStyle(.tertiary)
                    }
                }

                Section("Properties") {
                    let declared = registry.declaredProperties(for: item.pluginID)
                    if declared.isEmpty {
                        Text("No properties declared").foregroundStyle(.secondary)
                    }
                    ForEach(declared, id: \.name) { property in
                        PropertyEditorRow(
                            property: property,
                            current: item.propertyOverrides[property.name] ?? property.value
                        ) { newValue in
                            coordinator.applyOverride(itemID: item.id, name: property.name, value: newValue)
                        }
                        // Editors keep drafts in @State; force fresh identity
                        // per item so a selection change can't show (or commit)
                        // the previous item's draft.
                        .id("\(item.id)-\(property.name)")
                    }
                }

                Section("Log") {
                    PluginLogPanel(itemID: item.id)
                }

                Section {
                    Button(role: .destructive) {
                        selection.itemID = nil
                        store.remove(id: item.id)
                    } label: {
                        Label("Remove from Desktop", systemImage: "trash")
                    }
                    .accessibilityLabel("Remove from Desktop")
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Inspector")
        } else {
            ContentUnavailableView(
                "No Selection",
                systemImage: "square.dashed",
                description: Text("Select an item on the desktop canvas")
            )
        }
    }

    private var selectedItem: LayoutItem? {
        guard let id = selection.itemID else { return nil }
        return store.layout.items.first { $0.id == id }
    }

    /// The display an item's normalized frame is measured against — the same
    /// `screen.frame` the coordinator uses to place it. An offline display
    /// falls back to the main screen so the fields still show something sane.
    private func screenSize(for item: LayoutItem) -> CGSize {
        screens.controller(forDisplayUUID: item.displayUUID)?.screen.frame.size
            ?? NSScreen.main?.frame.size
            ?? CGSize(width: 1920, height: 1080)
    }

    /// Read-modify-write binding into the store (structural edits rebuild).
    private func binding<T>(
        for item: LayoutItem,
        get: @escaping (LayoutItem) -> T,
        set: @escaping (inout LayoutItem, T) -> Void
    ) -> Binding<T> {
        Binding(
            get: {
                let current = store.layout.items.first { $0.id == item.id } ?? item
                return get(current)
            },
            set: { newValue in
                guard var current = store.layout.items.first(where: { $0.id == item.id }) else { return }
                set(&current, newValue)
                store.update(current)
            }
        )
    }
}

// MARK: - Log panel (console.log of the running item, refreshed every second)

private struct PluginLogPanel: View {
    let itemID: UUID
    @EnvironmentObject private var coordinator: RuntimeCoordinator

    private static let timeFormat = Date.FormatStyle.dateTime.hour().minute().second()

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { _ in
            let logs = coordinator.logs(for: itemID)
            VStack(alignment: .leading, spacing: 4) {
                if logs.isEmpty {
                    Text("No console.log output")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .frame(maxWidth: .infinity, minHeight: 40, alignment: .center)
                } else {
                    ScrollViewReader { proxy in
                        ScrollView {
                            VStack(alignment: .leading, spacing: 2) {
                                ForEach(logs.suffix(100)) { entry in
                                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                                        Text(entry.date, format: Self.timeFormat)
                                            .foregroundStyle(.tertiary)
                                        Text(entry.message)
                                            .foregroundStyle(.secondary)
                                            .textSelection(.enabled)
                                    }
                                    .font(.caption.monospaced())
                                    .id(entry.id)
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(height: 140)
                        .onChange(of: logs.last?.id) { _, newValue in
                            if let newValue { proxy.scrollTo(newValue, anchor: .bottom) }
                        }
                        .onAppear {
                            if let last = logs.last?.id { proxy.scrollTo(last, anchor: .bottom) }
                        }
                    }
                    HStack {
                        Spacer()
                        Button("Clear") {
                            coordinator.clearLogs(for: itemID)
                        }
                        .buttonStyle(.borderless)
                        .font(.caption)
                    }
                }
            }
        }
    }
}

// MARK: - Store details (category selection)

private struct StoreDetailView: View {
    let storeID: String
    @EnvironmentObject private var stores: PluginStoreRegistry
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var selection: ManagerSelection
    @State private var confirmRemove = false

    var body: some View {
        let entry = stores.stores.first { $0.id == storeID }
        Form {
            Section(entry?.displayName ?? "Store") {
                LabeledContent("Kind", value: "Plugin Store")
                LabeledContent("Plugins", value: "\(entry?.catalog?.plugins.count ?? 0)")
                let installed = entry?.catalog?.plugins.filter { plugin in
                    registry.plugins.contains { $0.id == plugin.name }
                }.count ?? 0
                LabeledContent("Installed", value: "\(installed)")
                if let error = entry?.lastError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if let website = entry?.catalog?.website, let url = URL(string: website) {
                Section("Website") {
                    Link(destination: url) {
                        Label(website, systemImage: "safari")
                            .font(.caption)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    .help("Open \(website) in your browser")
                }
            }

            Section("Catalog URL") {
                // A catalog may name its canonical address; otherwise this is
                // simply where the user added it from.
                Text(entry?.catalog?.url ?? entry?.url ?? "—")
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                if let fetched = entry?.fetchedAt {
                    LabeledContent("Updated", value: fetched.formatted(date: .abbreviated, time: .shortened))
                        .font(.caption)
                }
                Button {
                    Task { await stores.refresh(storeID) }
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .disabled(stores.isRefreshing)
            }

            Section {
                Button(role: .destructive) {
                    confirmRemove = true
                } label: {
                    Label("Remove Store", systemImage: "trash")
                }
                .buttonStyle(.borderless)
                Text("Removing a store only drops its listing. Plugins you already installed from it stay on disk.")
                    .font(.caption2).foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Store")
        .confirmationDialog(
            "Remove \(entry?.displayName ?? "this store")?",
            isPresented: $confirmRemove,
            titleVisibility: .visible
        ) {
            Button("Remove Store", role: .destructive) {
                stores.removeStore(storeID)
                selection.storeID = nil
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Its catalog disappears from the library. Installed plugins are untouched.")
        }
    }
}

// MARK: - Store plugin details (not-yet-installed listing)

private struct StorePluginDetailView: View {
    let ref: StorePluginRef
    @EnvironmentObject private var stores: PluginStoreRegistry
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var selection: ManagerSelection
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var screens: ScreenManager
    @State private var isInstalling = false
    @State private var installError: String?

    var body: some View {
        let entry = stores.stores.first { $0.id == ref.storeID }
        let plugin = entry?.catalog?.plugins.first { $0.name == ref.name }
        let isInstalled = registry.plugins.contains { $0.id == ref.name }

        Form {
            Section(plugin?.name ?? ref.name) {
                if let preview = plugin?.preview, let url = URL(string: preview) {
                    AsyncImage(url: url) { phase in
                        switch phase {
                        case .success(let image):
                            image.resizable().scaledToFit()
                                .clipShape(RoundedRectangle(cornerRadius: 8))
                        case .failure:
                            Label("Preview unavailable", systemImage: "photo")
                                .font(.caption).foregroundStyle(.tertiary)
                        case .empty:
                            ProgressView().frame(maxWidth: .infinity)
                        @unknown default: Color.clear
                        }
                    }
                    .frame(maxHeight: 160)
                }
                if let description = plugin?.description {
                    Text(description)
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                LabeledContent("From", value: entry?.displayName ?? "—")
                if let version = plugin?.version { LabeledContent("Version", value: version) }
                if let author = plugin?.author { LabeledContent("Author", value: author) }
            }

            Section {
                if isInstalled {
                    Label("Installed", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    Button {
                        selection.pluginID = ref.name   // jump to the local details
                    } label: {
                        Label("Show Installed Plugin", systemImage: "arrow.right.circle")
                    }
                    .buttonStyle(.borderless)
                } else {
                    Button {
                        install(plugin)
                    } label: {
                        if isInstalling {
                            ProgressView().controlSize(.small)
                        } else {
                            Label("Install", systemImage: "arrow.down.circle")
                        }
                    }
                    .buttonStyle(.borderless)
                    .disabled(isInstalling || plugin == nil)
                    // The usual reason to install is to use it, so offer the
                    // whole gesture in one step.
                    Button {
                        install(plugin, thenPlace: true)
                    } label: {
                        Label("Install & Add to Desktop", systemImage: "plus.rectangle.on.rectangle")
                    }
                    .buttonStyle(.borderless)
                    .disabled(isInstalling || plugin == nil || selection.displayUUID == nil)
                }
                if let installError {
                    Label(installError, systemImage: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Store Plugin")
    }

    private func install(_ plugin: StorePlugin?, thenPlace: Bool = false) {
        guard let plugin, let storeName = stores.stores.first(where: { $0.id == ref.storeID })?.displayName
        else { return }
        isInstalling = true
        installError = nil
        Task {
            let error = await stores.install(plugin, from: storeName, into: PluginRegistry.directoryURL)
            installError = error
            registry.rescan()
            isInstalling = false
            // Placing selects the new item, which swaps this pane for the
            // item's editor — so only do it once the install actually landed.
            if thenPlace, error == nil {
                PluginLibraryView.addToDesktop(
                    plugin.name, store: store, registry: registry,
                    screens: screens, selection: selection
                )
            }
        }
    }
}

// MARK: - Plugin details (library selection) — read-only, plus uninstall

extension PluginAboutView {
    /// Downloads the catalog's copy over the installed one. Same path as a
    /// first install, so mirrors and the plugin.export check still apply.
    fileprivate func reinstall(_ plugin: StorePlugin, from store: String) {
        isChecking = true
        storeStatus = nil
        Task {
            let error = await stores.install(plugin, from: store, into: PluginRegistry.directoryURL)
            registry.rescan()
            storeStatus = error ?? String(localized: "Installed \(plugin.version ?? "")")
            isChecking = false
        }
    }
}

private struct PluginDetailView: View {
    let pluginID: String
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var selection: ManagerSelection
    @State private var confirmUninstall = false
    @State private var isRenaming = false
    @State private var newName = ""
    @State private var renameError: String?

    var body: some View {
        let meta = registry.metadata(for: pluginID)
        let descriptor = registry.descriptor(for: pluginID)
        let origin = descriptor?.origin ?? .user
        let usageCount = store.layout.items.filter { $0.pluginID == pluginID }.count

        Form {
            Section(pluginID) {
                LabeledContent("Kind", value: origin.title)
                LabeledContent("Version", value: meta.version ?? "—")
                if let author = meta.author { LabeledContent("Author", value: author) }
                if let summary = meta.summary {
                    Text(summary)
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                LabeledContent("On desktop", value: usageCount == 0 ? "not placed"
                               : "\(usageCount) item\(usageCount == 1 ? "" : "s")")
            }

            // Selecting a plugin in the library is where you go to update it,
            // whether or not it is placed on the desktop.
            Section("Updates") {
                PluginAboutView(pluginID: pluginID, showsHeader: false).id(pluginID)
            }

            Section("Capabilities") {
                let permissions = registry.declaredPermissions(for: pluginID)
                LabeledContent("Permissions",
                               value: permissions.isEmpty ? "none" : permissions.sorted().joined(separator: ", "))
                if let size = meta.preferredSize {
                    LabeledContent("Default size", value: "\(Int(size.width)) × \(Int(size.height))")
                }
                LabeledContent("Resize", value: resizeSummary(meta))
                if let limits = limitsSummary(meta) {
                    LabeledContent("Limits", value: limits)
                }
            }

            Section("Properties") {
                let declared = registry.declaredProperties(for: pluginID)
                if declared.isEmpty {
                    Text("No properties declared").foregroundStyle(.secondary)
                }
                // Read-only here: values are edited per placed item.
                ForEach(declared, id: \.name) { property in
                    LabeledContent(property.name, value: property.value.stringValue)
                        .font(.caption)
                }
            }

            Section("Source") {
                if let url = descriptor?.sourceURL {
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([url])
                    } label: {
                        Label("Reveal in Finder", systemImage: "folder")
                    }
                    .buttonStyle(.borderless)
                }
                let canRename = registry.canRename(pluginID)
                Button {
                    newName = pluginID
                    renameError = nil
                    isRenaming = true
                } label: {
                    Label("Rename…", systemImage: "pencil")
                }
                .buttonStyle(.borderless)
                .disabled(!canRename)
                if case .store(let store) = origin {
                    // Renaming would break the match by name that updates and
                    // "Reinstall from Store" rely on.
                    Text("Plugins from \(store) keep their name so updates can find them.")
                        .font(.caption2).foregroundStyle(.tertiary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Button(role: .destructive) {
                    confirmUninstall = true
                } label: {
                    Label("Uninstall", systemImage: "trash")
                }
                .buttonStyle(.borderless)
                .help("Move this plugin to the Trash")
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Plugin")
        .confirmationDialog(
            "Uninstall \(pluginID)?",
            isPresented: $confirmUninstall,
            titleVisibility: .visible
        ) {
            Button("Move to Trash", role: .destructive) {
                registry.uninstall(pluginID)
                selection.pluginID = nil
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(usageCount > 0
                 ? "\(usageCount) item\(usageCount == 1 ? "" : "s") on your desktop use it and will stop rendering."
                 : "The plugin file moves to the Trash.")
        }
        .alert("Rename Plugin", isPresented: $isRenaming) {
            TextField("Name", text: $newName)
            Button("Rename") { rename() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(renameError
                 ?? String(localized: "The file is renamed too. Items on your desktop follow it."))
        }
    }

    /// Renames the file, then repoints placed items so they keep rendering.
    private func rename() {
        let outcome = registry.rename(pluginID, to: newName)
        switch outcome {
        case .renamed(let name):
            store.repoint(pluginID: pluginID, to: name)
            selection.pluginID = name
            renameError = nil
        case .unchanged:
            renameError = nil
        default:
            // Back into the same alert, with the reason above the field. The
            // re-presentation waits a tick: this one is still dismissing.
            renameError = outcome.message
            DispatchQueue.main.async { isRenaming = true }
        }
    }

    private func resizeSummary(_ meta: PluginMetadata) -> String {
        guard meta.resizable else { return "fixed size" }
        var parts = [meta.keepsAspect ? "keeps aspect" : "free"]
        if meta.autoSizeWidth && meta.autoSizeHeight { parts.append("auto-sizes") }
        else if meta.autoSizeHeight { parts.append("auto height") }
        else if meta.autoSizeWidth { parts.append("auto width") }
        return parts.joined(separator: ", ")
    }

    private func limitsSummary(_ meta: PluginMetadata) -> String? {
        func range(_ min: Double?, _ max: Double?) -> String? {
            switch (min, max) {
            case let (min?, max?): return "\(Int(min))–\(Int(max))"
            case let (min?, nil): return "≥ \(Int(min))"
            case let (nil, max?): return "≤ \(Int(max))"
            default: return nil
            }
        }
        let w = range(meta.minWidth, meta.maxWidth).map { "W \($0)" }
        let h = range(meta.minHeight, meta.maxHeight).map { "H \($0)" }
        let parts = [w, h].compactMap { $0 }
        return parts.isEmpty ? nil : parts.joined(separator: "  ")
    }
}

// MARK: - Plugin info & updates

private struct PluginAboutView: View {
    let pluginID: String
    /// The library's detail pane already lists version, author and summary.
    var showsHeader = true
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var stores: PluginStoreRegistry
    @State private var isChecking = false
    @State private var storeStatus: String?

    /// A store that offers this plugin. The store it was installed from wins
    /// when that is known, but any added store listing the same name will do:
    /// a plugin copied into the folder by hand, or installed before its store
    /// was added, has no recorded origin and would otherwise be left with no
    /// way to update at all.
    private var storeSource: (store: String, plugin: StorePlugin)? {
        let origin = PluginStoreRegistry.storeName(forPlugin: pluginID)
        let matches = stores.stores.compactMap { entry -> (String, StorePlugin)? in
            guard let listed = entry.catalog?.plugins.first(where: { $0.name == pluginID })
            else { return nil }
            return (entry.displayName, listed)
        }
        return matches.first { $0.0 == origin } ?? matches.first
    }

    var body: some View {
        let meta = registry.metadata(for: pluginID)
        VStack(alignment: .leading, spacing: 6) {
            if showsHeader {
                LabeledContent("Version", value: meta.version ?? "—")
                if let author = meta.author {
                    LabeledContent("Author", value: author)
                }
                if let summary = meta.summary {
                    Text(summary)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if meta.updateURL != nil {
                Divider()
                Toggle("Auto-update", isOn: Binding(
                    get: { registry.isAutoUpdate(pluginID) },
                    set: { registry.setAutoUpdate($0, for: pluginID) }
                ))
                HStack {
                    Button {
                        isChecking = true
                        Task {
                            await registry.checkForUpdate(pluginID)
                            isChecking = false
                        }
                    } label: {
                        if isChecking {
                            ProgressView().controlSize(.small)
                        } else {
                            Text("Check for Update")
                        }
                    }
                    .disabled(isChecking)
                    Spacer()
                }
                if let status = registry.updateStatus[pluginID] {
                    Text(status.message)
                        .font(.caption)
                        .foregroundStyle(statusColor(status))
                }
            } else if let source = storeSource {
                // A store-installed plugin has no updateURL of its own — the
                // catalog is its source of truth, so update straight from it
                // rather than leaving the user with no button at all.
                Divider()
                LabeledContent("Store", value: source.store)
                let listed = source.plugin.version
                let installed = meta.version
                let newer = listed.map { l in
                    installed.map { compareVersions(l, $0) == .orderedDescending } ?? true
                } ?? false
                HStack {
                    Button {
                        reinstall(source.plugin, from: source.store)
                    } label: {
                        if isChecking {
                            ProgressView().controlSize(.small)
                        } else if newer, let listed {
                            Text("Update to \(listed)")
                        } else {
                            Text("Reinstall from Store")
                        }
                    }
                    .disabled(isChecking)
                    Spacer()
                }
                if newer, let listed {
                    Text("The store lists \(listed).")
                        .font(.caption).foregroundStyle(.orange)
                } else if let status = storeStatus {
                    Text(status).font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("Refresh the store category to look for a newer version.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            } else {
                Text("No update URL declared")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }
        }
    }

    private func statusColor(_ result: UpdateResult) -> Color {
        switch result {
        case .updated: return .green
        case .failed: return .orange
        default: return .secondary
        }
    }
}

// MARK: - Background color (transparent by default)

private struct BackgroundColorEditor: View {
    let hex: String?
    let onChange: (String?) -> Void
    @State private var isCustom = false
    @State private var color = Color.black.opacity(0.6)
    @State private var isSeeded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Toggle("Background", isOn: $isCustom)
                .onChange(of: isCustom) { _, on in
                    guard isSeeded else { return }
                    onChange(on ? (color.hexString() ?? "#000000FF") : nil)
                }
            if isCustom {
                ColorPicker("Color", selection: $color, supportsOpacity: true)
                    .onChange(of: color) { _, newValue in
                        guard isSeeded, isCustom else { return }
                        onChange(newValue.hexString())
                    }
                Text("Set opacity to 0 for a see-through tint.")
                    .font(.caption2).foregroundStyle(.tertiary)
            } else {
                Text("Transparent").font(.caption).foregroundStyle(.tertiary)
            }
        }
        .onAppear {
            isCustom = hex != nil
            if let hex, let parsed = Color(hexString: hex) { color = parsed }
            isSeeded = true
        }
    }
}

// MARK: - SSH destinations (one or more remote hosts per item)

private struct SSHEditor: View {
    let item: LayoutItem
    let commit: (LayoutItem) -> Void

    @State private var hosts: [SSHConfig] = []
    @State private var isSeeded = false
    private var aliases: [String] { SSHConfigFile.aliases() }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach($hosts) { $config in
                SSHHostRow(
                    config: $config,
                    itemID: item.id,
                    aliases: aliases,
                    onChange: { push() },
                    onRemove: {
                        hosts.removeAll { $0.id == config.id }
                        push()
                    },
                    canRemove: hosts.count > 1
                )
                if config.id != hosts.last?.id { Divider().padding(.vertical, 4) }
            }
            Button {
                hosts.append(SSHConfig(name: suggestedName()))
                push()
            } label: {
                Label("Add Server", systemImage: "plus")
            }
            .buttonStyle(.borderless)
            .font(.caption)
            if hosts.count > 1 {
                Text("Plugins target a server by name: ssh(cmd, \"\(hosts[1].name)\").")
                    .font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .onAppear {
            hosts = item.sshHosts.isEmpty ? [SSHConfig()] : item.sshHosts
            isSeeded = true
        }
    }

    private func suggestedName() -> String {
        var n = hosts.count + 1
        while hosts.contains(where: { $0.name == "server\(n)" }) { n += 1 }
        return "server\(n)"
    }

    private func push() {
        guard isSeeded else { return } // ignore the initial onAppear seed
        var updated = item
        updated.sshHosts = hosts
        commit(updated)
    }
}

private struct SSHHostRow: View {
    @Binding var config: SSHConfig
    let itemID: UUID
    let aliases: [String]
    let onChange: () -> Void
    let onRemove: () -> Void
    let canRemove: Bool

    @State private var password = ""
    @State private var isSeeded = false

    var body: some View {
        // Matches the breathing room of the form rows above; tighter than
        // this and the settings read as one dense block.
        VStack(alignment: .leading, spacing: 13) {
            HStack {
                TextField("name", text: $config.name)
                    .font(.caption.bold())
                    .onChange(of: config.name) { _, _ in onChange() }
                Spacer()
                if canRemove {
                    Button(role: .destructive) { onRemove() } label: {
                        Image(systemName: "minus.circle")
                    }
                    .buttonStyle(.borderless)
                }
            }

            // Alias mode is the common case and needs one control, so the
            // manual fields stay out of the way until it's switched off.
            Toggle("Use ~/.ssh/config alias", isOn: $config.usesAlias)
                .controlSize(.small)
                .onChange(of: config.usesAlias) { _, _ in onChange() }

            if config.usesAlias {
                if aliases.isEmpty {
                    TextField("Alias", text: $config.host)
                        .onChange(of: config.host) { _, _ in onChange() }
                    Text("No hosts found in ~/.ssh/config.")
                        .font(.caption2).foregroundStyle(.tertiary)
                } else {
                    Picker("Alias", selection: aliasBinding) {
                        Text("Choose…").tag("")
                        ForEach(aliases, id: \.self) { Text($0).tag($0) }
                    }
                    Text("ssh resolves the host name, user, port, and key.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            } else {
                // One field per row: in a form a TextField's placeholder is
                // the row label, so pairing two on a line splits the label
                // column and wraps.
                TextField("Host", text: $config.host)
                    .onChange(of: config.host) { _, _ in onChange() }
                TextField("Port", value: $config.port, format: .number)
                    .onChange(of: config.port) { _, _ in onChange() }
                TextField("User", text: $config.user)
                    .onChange(of: config.user) { _, _ in onChange() }

                Picker("Auth", selection: $config.auth) {
                    Text("Agent").tag(SSHAuth.none)
                    Text("Password").tag(SSHAuth.password)
                    Text("Identity Key").tag(SSHAuth.key)
                }
                .onChange(of: config.auth) { _, _ in onChange() }

                switch config.auth {
                case .password:
                    SecureField("Password", text: $password)
                        .onChange(of: password) { _, newValue in
                            guard isSeeded else { return }
                            KeychainStore.setPassword(newValue, forItem: itemID, host: config.id)
                            onChange()
                        }
                    Text("Stored in your login Keychain, never in layout.json.")
                        .font(.caption2).foregroundStyle(.tertiary)
                case .key:
                    LabeledContent("Key") {
                        HStack(spacing: 6) {
                            Text(config.keyPath.isEmpty ? "None"
                                 : (config.keyPath as NSString).lastPathComponent)
                                .foregroundStyle(config.keyPath.isEmpty ? .tertiary : .secondary)
                                .lineLimit(1).truncationMode(.middle)
                            Button("Choose…") { chooseKey() }
                        }
                    }
                case .none:
                    Text("Uses your ssh agent.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            }
        }
        .textFieldStyle(.roundedBorder)
        .onAppear {
            password = KeychainStore.password(forItem: itemID, host: config.id) ?? ""
            isSeeded = true
        }
    }

    /// In alias mode `host` holds the ~/.ssh/config entry; ssh resolves its
    /// hostname, user, port, and identity from there.
    private var aliasBinding: Binding<String> {
        Binding(
            get: { aliases.contains(config.host) ? config.host : "" },
            set: { newValue in
                guard !newValue.isEmpty else { return }
                config.host = newValue
                if config.name.isEmpty || config.name.hasPrefix("server") || config.name == "default" {
                    config.name = newValue
                }
                onChange()
            }
        )
    }

    private func chooseKey() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Choose an SSH identity (private key) file"
        panel.showsHiddenFiles = true
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        config.keyPath = url.path
        onChange()
    }
}

// MARK: - Frame editor (percent units; origin = bottom-left, AppKit-style)

/// Frames are stored normalized (0…1 of the screen) so items survive a
/// resolution change, but points are what an author actually thinks in — so
/// the editor converts both ways against the item's display.
private struct FrameEditor: View {
    private enum Axis { case width, height }

    let item: LayoutItem
    /// Declared size limits and resize policy for this plugin.
    let metadata: PluginMetadata
    /// Size in points of the display this item lives on.
    let screenSize: CGSize
    let commit: (CGRect) -> Void

    @State private var x = 0.0
    @State private var y = 0.0
    @State private var width = 0.0
    @State private var height = 0.0

    private var resizable: Bool { metadata.resizable }

    var body: some View {
        Group {
            // String(localized:) at the call site: the label crosses a
            // String parameter, so a bare literal would never be localized.
            pointField(String(localized: "X"), value: $x)
            pointField(String(localized: "Y (from top)"), value: $y)
            pointField(String(localized: "Width"), value: $width, axis: .width)
                .disabled(!resizable)
            pointField(String(localized: "Height"), value: $height, axis: .height)
                .disabled(!resizable)
            if let limits = limitsDescription {
                Text(limits).font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .onAppear { load() }
        .onChange(of: item.normalizedFrame) { _, _ in load() }
        .onChange(of: screenSize) { _, _ in load() }
    }

    private var limitsDescription: String? {
        func range(_ min: Double?, _ max: Double?) -> String? {
            switch (min, max) {
            case let (min?, max?): "\(Int(min))–\(Int(max))"
            case let (min?, nil): "≥ \(Int(min))"
            case let (nil, max?): "≤ \(Int(max))"
            default: nil
            }
        }
        let parts = [
            range(metadata.minWidth, metadata.maxWidth).map { "W \($0)" },
            range(metadata.minHeight, metadata.maxHeight).map { "H \($0)" },
        ].compactMap { $0 }
        guard !parts.isEmpty else { return nil }
        let ranges = parts.joined(separator: "  ")
        return String(localized: "Limits: \(ranges) pt")
    }

    private func load() {
        let frame = item.normalizedFrame
        x = (frame.minX * screenSize.width).rounded()
        // Stored bottom-left, edited top-left: an item is placed by its
        // top-left corner, so that is the number the user should see.
        y = ((1 - frame.minY - frame.height) * screenSize.height).rounded()
        width = (frame.width * screenSize.width).rounded()
        height = (frame.height * screenSize.height).rounded()
    }

    private func pointField(_ label: String, value: Binding<Double>, axis: Axis? = nil) -> some View {
        TextField(label, value: value, format: .number.precision(.fractionLength(0)))
            .onSubmit { submit(edited: axis) }
    }

    /// Out-of-range sizes snap back to the declared limit rather than being
    /// silently kept: the coordinator would clamp the frame anyway, and when
    /// the clamp lands on the value already stored no model change comes back
    /// to correct the field.
    private func submit(edited axis: Axis?) {
        guard screenSize.width > 0, screenSize.height > 0 else { return }
        let size = metadata.resolvedSize(
            entered: CGSize(width: width, height: height),
            edited: axis.map { $0 == .width ? .width : .height }
        )

        width = size.width.rounded()
        height = size.height.rounded()
        x = min(max(x, 0), screenSize.width)
        y = min(max(y, 0), screenSize.height)

        // Back to bottom-left for storage. Height changes grow downward,
        // because y is the top edge and stays put.
        let bottom = max(screenSize.height - y - size.height, 0)
        commit(CGRect(
            x: min(x / screenSize.width, 1),
            y: min(bottom / screenSize.height, 1),
            width: min(size.width / screenSize.width, 1),
            height: min(size.height / screenSize.height, 1)
        ))
    }
}

// MARK: - Property editors

private struct PropertyEditorRow: View {
    let property: PluginProperty
    let current: PropertyValue
    let commit: (PropertyValue) -> Void

    var body: some View {
        switch property.valueType {
        case "number":
            NumberEditor(name: property.name, value: current.doubleValue ?? 0, commit: commit)
        case "boolean", "bool":
            Toggle(property.name, isOn: Binding(
                get: { current.boolValue ?? false },
                set: { commit(.bool($0)) }
            ))
        case "color":
            ColorEditor(name: property.name, hex: current.stringValue, commit: commit)
        default:
            StringEditor(name: property.name, value: current.stringValue, commit: commit)
        }
    }
}

// Each editor seeds its draft from the model in onAppear. That seed must NOT
// commit: selecting an item would otherwise fire onChange for every property,
// and an fps/interval "edit" rebuilds the whole runtime — flashing every
// widget on screen. `isSeeded` gates commits until the user actually types.

private struct StringEditor: View {
    let name: String
    let value: String
    let commit: (PropertyValue) -> Void
    @State private var draft = ""
    @State private var isSeeded = false

    var body: some View {
        TextField(name, text: $draft)
            .onAppear { draft = value; isSeeded = true }
            .onChange(of: draft) { _, newValue in
                guard isSeeded, newValue != value else { return }
                commit(.string(newValue))
            }
    }
}

private struct NumberEditor: View {
    let name: String
    let value: Double
    let commit: (PropertyValue) -> Void
    @State private var draft = 0.0
    @State private var isSeeded = false

    var body: some View {
        HStack {
            TextField(name, value: $draft, format: .number)
                .onSubmit {
                    guard draft != value else { return }
                    commit(.number(draft))
                }
            Stepper("", value: $draft, step: 1)
                .labelsHidden()
                .onChange(of: draft) { _, newValue in
                    guard isSeeded, newValue != value else { return }
                    commit(.number(newValue))
                }
        }
        .onAppear { draft = value; isSeeded = true }
    }
}

private struct ColorEditor: View {
    let name: String
    let hex: String
    let commit: (PropertyValue) -> Void
    @State private var draft = Color.white
    @State private var isSeeded = false

    var body: some View {
        ColorPicker(name, selection: $draft, supportsOpacity: true)
            .onAppear {
                draft = Color(hexString: hex) ?? .white
                isSeeded = true
            }
            .onChange(of: draft) { _, newValue in
                guard isSeeded, let hexString = newValue.hexString(), hexString != hex else { return }
                commit(.color(hexString))
            }
    }
}

// MARK: - Color ↔ hex

extension Color {
    init?(hexString: String) {
        guard let cgColor = CSSColor.parse(hexString) else { return nil }
        self.init(cgColor: cgColor)
    }

    func hexString() -> String? {
        guard let converted = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        let r = Int(round(converted.redComponent * 255))
        let g = Int(round(converted.greenComponent * 255))
        let b = Int(round(converted.blueComponent * 255))
        let a = Int(round(converted.alphaComponent * 255))
        return String(format: "#%02X%02X%02X%02X", r, g, b, a)
    }
}
