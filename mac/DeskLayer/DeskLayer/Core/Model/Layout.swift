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
/// here — it lives in the Keychain, keyed by the item id.
nonisolated struct SSHConfig: Codable, Hashable {
    var host: String = ""
    var port: Int = 22
    var user: String = ""
    var auth: SSHAuth = .none
    var keyPath: String = ""

    var isConfigured: Bool { !host.isEmpty && !user.isEmpty && auth != .none }
}

nonisolated struct LayoutItem: Codable, Identifiable, Hashable {
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
    /// Remote destination for the ssh() binding (empty until configured).
    var ssh: SSHConfig

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
        ssh: SSHConfig = SSHConfig()
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
        self.ssh = ssh
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
        ssh = try container.decodeIfPresent(SSHConfig.self, forKey: .ssh) ?? SSHConfig()
    }
}

nonisolated struct Layout: Codable {
    var version: Int = 1
    var items: [LayoutItem] = []
}
