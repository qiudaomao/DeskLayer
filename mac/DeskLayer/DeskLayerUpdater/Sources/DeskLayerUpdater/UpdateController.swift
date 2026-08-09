//
//  UpdateController.swift
//  DeskLayerUpdater
//
//  App self-updates via Sparkle. Plugins update themselves from their store
//  (PluginUpdater); this is the app bundle, which a store can't replace.
//
//  The feed and the public EdDSA key come from Info.plist (SUFeedURL,
//  SUPublicEDKey). An update installs only if it is signed by the matching
//  private key, so a compromised feed host still cannot ship code.
//

import Sparkle
import AppKit

@MainActor
public final class UpdateController {
    /// Sparkle's standard controller owns the updater, its UI, and the
    /// scheduled background checks.
    private let controller: SPUStandardUpdaterController

    public init() {
        // startingUpdater: true begins the scheduled check cycle now; Sparkle
        // asks the user about automatic checks on first launch.
        controller = SPUStandardUpdaterController(
            startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil
        )
    }

    /// Menu action: shows Sparkle's UI, including the "you're up to date"
    /// case, which the silent scheduled check deliberately doesn't.
    public func checkForUpdates() {
        controller.updater.checkForUpdates()
    }

    public var canCheckForUpdates: Bool { controller.updater.canCheckForUpdates }

    public var automaticallyChecksForUpdates: Bool {
        get { controller.updater.automaticallyChecksForUpdates }
        set { controller.updater.automaticallyChecksForUpdates = newValue }
    }

    public var lastUpdateCheckDate: Date? { controller.updater.lastUpdateCheckDate }
}
