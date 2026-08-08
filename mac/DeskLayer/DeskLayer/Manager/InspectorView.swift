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

                let permissions = registry.declaredPermissions(for: item.pluginID)
                if permissions.contains("ssh") {
                    Section("SSH Destination") {
                        SSHEditor(item: item) { updated in store.update(updated) }
                            .id(item.id)
                    }
                }

                Section("Frame (% of screen)") {
                    FrameEditor(item: item) { newFrame in
                        coordinator.setFrame(itemID: item.id, normalizedFrame: newFrame, commit: true)
                    }
                    .id(item.id)
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

// MARK: - Background color (transparent by default)

private struct BackgroundColorEditor: View {
    let hex: String?
    let onChange: (String?) -> Void
    @State private var isCustom = false
    @State private var color = Color.black.opacity(0.6)

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Toggle("Background", isOn: $isCustom)
                .onChange(of: isCustom) { _, on in
                    onChange(on ? (color.hexString() ?? "#000000FF") : nil)
                }
            if isCustom {
                ColorPicker("Color", selection: $color, supportsOpacity: true)
                    .onChange(of: color) { _, newValue in
                        if isCustom { onChange(newValue.hexString()) }
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
        }
    }
}

// MARK: - SSH destination

private struct SSHEditor: View {
    let item: LayoutItem
    let commit: (LayoutItem) -> Void

    @State private var host = ""
    @State private var port = "22"
    @State private var user = ""
    @State private var auth = SSHAuth.none
    @State private var keyPath = ""
    @State private var password = ""

    var body: some View {
        Group {
            TextField("Host", text: $host).onChange(of: host) { _, _ in push() }
            TextField("Port", text: $port).onChange(of: port) { _, _ in push() }
            TextField("User", text: $user).onChange(of: user) { _, _ in push() }
            Picker("Auth", selection: $auth) {
                Text("None").tag(SSHAuth.none)
                Text("Password").tag(SSHAuth.password)
                Text("Identity Key").tag(SSHAuth.key)
            }
            .onChange(of: auth) { _, _ in push() }

            switch auth {
            case .password:
                SecureField("Password", text: $password)
                    .onChange(of: password) { _, newValue in
                        KeychainStore.setPassword(newValue, forItem: item.id)
                        commit(item) // re-spawn so ssh() picks up the new secret
                    }
                Text("Stored in your login Keychain, never in layout.json.")
                    .font(.caption2).foregroundStyle(.tertiary)
            case .key:
                HStack {
                    Text(keyPath.isEmpty ? "No key selected" : (keyPath as NSString).lastPathComponent)
                        .font(.caption).foregroundStyle(keyPath.isEmpty ? .tertiary : .secondary)
                        .lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Choose…") { chooseKey() }
                }
            case .none:
                Text("Select an auth method to enable ssh().")
                    .font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .onAppear {
            host = item.ssh.host
            port = String(item.ssh.port)
            user = item.ssh.user
            auth = item.ssh.auth
            keyPath = item.ssh.keyPath
            password = KeychainStore.password(forItem: item.id) ?? ""
        }
    }

    private func push() {
        var updated = item
        updated.ssh.host = host
        updated.ssh.port = Int(port) ?? 22
        updated.ssh.user = user
        updated.ssh.auth = auth
        updated.ssh.keyPath = keyPath
        commit(updated)
    }

    private func chooseKey() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Choose an SSH identity (private key) file"
        // ~/.ssh is hidden; let the user reveal it.
        panel.showsHiddenFiles = true
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        keyPath = url.path
        push()
    }
}

// MARK: - Frame editor (percent units; origin = bottom-left, AppKit-style)

private struct FrameEditor: View {
    let item: LayoutItem
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
            percentField("Height", value: $height)
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

private struct StringEditor: View {
    let name: String
    let value: String
    let commit: (PropertyValue) -> Void
    @State private var draft = ""

    var body: some View {
        TextField(name, text: $draft)
            .onAppear { draft = value }
            .onChange(of: draft) { _, newValue in commit(.string(newValue)) }
    }
}

private struct NumberEditor: View {
    let name: String
    let value: Double
    let commit: (PropertyValue) -> Void
    @State private var draft = 0.0

    var body: some View {
        HStack {
            TextField(name, value: $draft, format: .number)
                .onSubmit { commit(.number(draft)) }
            Stepper("", value: $draft, step: 1)
                .labelsHidden()
                .onChange(of: draft) { _, newValue in commit(.number(newValue)) }
        }
        .onAppear { draft = value }
    }
}

private struct ColorEditor: View {
    let name: String
    let hex: String
    let commit: (PropertyValue) -> Void
    @State private var draft = Color.white

    var body: some View {
        ColorPicker(name, selection: $draft, supportsOpacity: true)
            .onAppear { draft = Color(hexString: hex) ?? .white }
            .onChange(of: draft) { _, newValue in
                if let hexString = newValue.hexString() {
                    commit(.color(hexString))
                }
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
