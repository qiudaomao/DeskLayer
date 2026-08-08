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
        if let item = selectedItem {
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

                Section("Frame (% of screen)") {
                    let resizable = registry.metadata(for: item.pluginID).resizable
                    FrameEditor(item: item, resizable: resizable) { newFrame in
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

// MARK: - Plugin info & updates

private struct PluginAboutView: View {
    let pluginID: String
    @EnvironmentObject private var registry: PluginRegistry
    @State private var isChecking = false

    var body: some View {
        let meta = registry.metadata(for: pluginID)
        VStack(alignment: .leading, spacing: 6) {
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
                if config.id != hosts.last?.id { Divider() }
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
        VStack(alignment: .leading, spacing: 4) {
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

            // Alias from ~/.ssh/config — supplies user, port, and identity.
            if !aliases.isEmpty {
                Picker("Alias", selection: aliasBinding) {
                    Text("Custom…").tag("")
                    ForEach(aliases, id: \.self) { Text($0).tag($0) }
                }
            }

            TextField("Host or ~/.ssh/config alias", text: $config.host)
                .onChange(of: config.host) { _, _ in onChange() }

            DisclosureGroup("Overrides") {
                TextField("User (blank = from ssh config)", text: $config.user)
                    .onChange(of: config.user) { _, _ in onChange() }
                TextField("Port", value: $config.port, format: .number)
                    .onChange(of: config.port) { _, _ in onChange() }
                Picker("Auth", selection: $config.auth) {
                    Text("ssh config / agent").tag(SSHAuth.none)
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
                    HStack {
                        Text(config.keyPath.isEmpty ? "No key selected"
                             : (config.keyPath as NSString).lastPathComponent)
                            .font(.caption)
                            .foregroundStyle(config.keyPath.isEmpty ? .tertiary : .secondary)
                            .lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button("Choose…") { chooseKey() }
                    }
                case .none:
                    Text("Uses ~/.ssh/config and your agent — nothing else to set.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            }
            .font(.caption)
        }
        .onAppear {
            password = KeychainStore.password(forItem: itemID, host: config.id) ?? ""
            isSeeded = true
        }
    }

    /// Picking an alias fills the host field; editing host by hand clears it.
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

private struct FrameEditor: View {
    let item: LayoutItem
    var resizable: Bool = true
    let commit: (CGRect) -> Void

    @State private var x = 0.0
    @State private var y = 0.0
    @State private var width = 0.0
    @State private var height = 0.0

    var body: some View {
        Group {
            percentField("X", value: $x)
            percentField("Y (from bottom)", value: $y)
            percentField("Width", value: $width)
                .disabled(!resizable)
            percentField("Height", value: $height)
                .disabled(!resizable)
        }
        .onAppear { load() }
        .onChange(of: item.normalizedFrame) { _, _ in load() }
    }

    private func load() {
        x = item.normalizedFrame.minX * 100
        y = item.normalizedFrame.minY * 100
        width = item.normalizedFrame.width * 100
        height = item.normalizedFrame.height * 100
    }

    private func percentField(_ label: String, value: Binding<Double>) -> some View {
        TextField(label, value: value, format: .number.precision(.fractionLength(0...1)))
            .onSubmit {
                let frame = CGRect(
                    x: min(max(x / 100, 0), 1),
                    y: min(max(y / 100, 0), 1),
                    width: min(max(width / 100, 0.02), 1),
                    height: min(max(height / 100, 0.02), 1)
                )
                commit(frame)
            }
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
