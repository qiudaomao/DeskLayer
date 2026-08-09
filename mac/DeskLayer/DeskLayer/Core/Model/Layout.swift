//
//  Layout.swift
//  DeskLayer
//
//  Persisted layout model: which plugin instances exist, where, with what
//  property overrides. Saved as hand-editable JSON (see LayoutStore).
//

import Foundation

nonisolated enum RenderTarget: String, Codable {
    case wallpaper
    case floatingWindow
}

nonisolated enum SSHAuth: String, Codable {
    case none
    case password
    case key
}

/// Remote destination for the ssh() host binding. The password is NOT stored
/// here — it lives in the Keychain, keyed by the item id + host name.
nonisolated struct SSHConfig: Codable, Hashable, Identifiable {
    /// Stable id so the inspector can edit a list of hosts.
    var id: UUID = UUID()
    /// The name plugins use to target this host: ssh(argv, "nas").
    var name: String = "default"
    var host: String = ""
    var port: Int = 22
    var user: String = ""
    var auth: SSHAuth = .none
    var keyPath: String = ""
    /// Alias mode: `host` is a ~/.ssh/config entry and ssh supplies the rest,
    /// so the inspector shows nothing but the alias. Off, the user fills in
    /// host, port, user, and credentials by hand.
    var usesAlias: Bool = true

    /// A host name alone is enough: it may be a ~/.ssh/config alias, which
    /// supplies user, port, and identity. User/auth are optional overrides.
    var isConfigured: Bool { !host.isEmpty }

    private enum CodingKeys: String, CodingKey {
        case id, name, host, port, user, auth, keyPath, usesAlias
    }

    init(id: UUID = UUID(), name: String = "default", host: String = "", port: Int = 22,
         user: String = "", auth: SSHAuth = .none, keyPath: String = "",
         usesAlias: Bool = true) {
        self.id = id; self.name = name; self.host = host
        self.port = port; self.user = user; self.auth = auth; self.keyPath = keyPath
        self.usesAlias = usesAlias
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "default"
        host = try c.decodeIfPresent(String.self, forKey: .host) ?? ""
        port = try c.decodeIfPresent(Int.self, forKey: .port) ?? 22
        user = try c.decodeIfPresent(String.self, forKey: .user) ?? ""
        auth = try c.decodeIfPresent(SSHAuth.self, forKey: .auth) ?? .none
        keyPath = try c.decodeIfPresent(String.self, forKey: .keyPath) ?? ""
        // Layouts written before the alias toggle: treat a bare host with no
        // manual settings as an alias, anything hand-tuned as manual.
        usesAlias = try c.decodeIfPresent(Bool.self, forKey: .usesAlias)
            ?? (user.isEmpty && port == 22 && auth == .none)
    }
}

nonisolated struct LayoutItem: Codable, Identifiable, Hashable {
    // `ssh` is a computed accessor for sshHosts.first, but the key stays so
    // layouts written before multi-host support still decode.
    private enum CodingKeys: String, CodingKey {
        case id, pluginID, displayUUID, normalizedFrame, target, propertyOverrides
        case isEnabled, zOrder, clickThrough, backgroundColor, sshHosts, ssh
    }

    var id: UUID
    var pluginID: String
    /// Stable across reboots/reconnects (CGDisplayCreateUUIDFromDisplayID),
    /// unlike CGDirectDisplayID.
    var displayUUID: String
    /// 0…1 within the screen frame, bottom-left origin (AppKit convention).
    var normalizedFrame: CGRect
    var target: RenderTarget
    var propertyOverrides: [String: PropertyValue]
    var isEnabled: Bool
    var zOrder: Int
    /// Floating windows only: true = mouse events pass through to whatever
    /// is beneath; false = the panel accepts events (drag to move).
    /// Wallpaper items are inherently click-through.
    var clickThrough: Bool
    /// Backdrop behind the plugin's own drawing. nil = fully transparent
    /// (default); otherwise a CSS color string (#rrggbbaa supported).
    var backgroundColor: String?
    /// Remote destinations for the ssh() binding. A plugin can target one by
    /// name — ssh(argv, "nas") — or iterate $ssh.hosts to render several.
    var sshHosts: [SSHConfig]

    /// The first host, for single-destination plugins.
    var ssh: SSHConfig {
        get { sshHosts.first ?? SSHConfig() }
        set {
            if sshHosts.isEmpty { sshHosts = [newValue] } else { sshHosts[0] = newValue }
        }
    }

    init(
        id: UUID = UUID(),
        pluginID: String,
        displayUUID: String,
        normalizedFrame: CGRect,
        target: RenderTarget = .wallpaper,
        propertyOverrides: [String: PropertyValue] = [:],
        isEnabled: Bool = true,
        zOrder: Int = 0,
        clickThrough: Bool = false,
        backgroundColor: String? = nil,
        sshHosts: [SSHConfig] = []
    ) {
        self.id = id
        self.pluginID = pluginID
        self.displayUUID = displayUUID
        self.normalizedFrame = normalizedFrame
        self.target = target
        self.propertyOverrides = propertyOverrides
        self.isEnabled = isEnabled
        self.zOrder = zOrder
        self.clickThrough = clickThrough
        self.backgroundColor = backgroundColor
        self.sshHosts = sshHosts
    }

    // Custom decoding so layouts written before a field existed still load
    // (a hand-editable file must never be invalidated by an app update).
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        pluginID = try container.decode(String.self, forKey: .pluginID)
        displayUUID = try container.decode(String.self, forKey: .displayUUID)
        normalizedFrame = try container.decode(CGRect.self, forKey: .normalizedFrame)
        target = try container.decodeIfPresent(RenderTarget.self, forKey: .target) ?? .wallpaper
        propertyOverrides = try container.decodeIfPresent([String: PropertyValue].self, forKey: .propertyOverrides) ?? [:]
        isEnabled = try container.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        zOrder = try container.decodeIfPresent(Int.self, forKey: .zOrder) ?? 0
        clickThrough = try container.decodeIfPresent(Bool.self, forKey: .clickThrough) ?? false
        backgroundColor = try container.decodeIfPresent(String.self, forKey: .backgroundColor)
        // Layouts written before multi-host support carry a single `ssh`.
        if let hosts = try container.decodeIfPresent([SSHConfig].self, forKey: .sshHosts) {
            sshHosts = hosts
        } else if let legacy = try container.decodeIfPresent(SSHConfig.self, forKey: .ssh) {
            sshHosts = [legacy]
        } else {
            sshHosts = []
        }
    }

    // Explicit: `ssh` is a computed accessor (decode-only, for old layouts),
    // so the synthesized encoder can't be used.
    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(pluginID, forKey: .pluginID)
        try c.encode(displayUUID, forKey: .displayUUID)
        try c.encode(normalizedFrame, forKey: .normalizedFrame)
        try c.encode(target, forKey: .target)
        try c.encode(propertyOverrides, forKey: .propertyOverrides)
        try c.encode(isEnabled, forKey: .isEnabled)
        try c.encode(zOrder, forKey: .zOrder)
        try c.encode(clickThrough, forKey: .clickThrough)
        try c.encodeIfPresent(backgroundColor, forKey: .backgroundColor)
        try c.encode(sshHosts, forKey: .sshHosts)
    }
}

nonisolated struct Layout: Codable {
    var version: Int = 1
    var items: [LayoutItem] = []

    /// Points every item at a renamed plugin. Returns whether anything moved,
    /// so the store can skip a save when nothing did.
    @discardableResult
    mutating func repoint(pluginID old: String, to new: String) -> Bool {
        var touched = false
        for index in items.indices where items[index].pluginID == old {
            items[index].pluginID = new
            touched = true
        }
        return touched
    }
}
