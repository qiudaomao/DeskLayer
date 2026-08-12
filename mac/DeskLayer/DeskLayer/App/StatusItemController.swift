//
//  StatusItemController.swift
//  DeskLayer
//
//  Menu-bar presence: the app keeps rendering with the manager closed,
//  so the status item is the always-available control surface.
//

import AppKit
import ServiceManagement

@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    private let statusItem: NSStatusItem
    private let coordinator: RuntimeCoordinator
    private let showManager: () -> Void
    private let pauseMenuItem: NSMenuItem
    private let loginMenuItem: NSMenuItem

    init(
        coordinator: RuntimeCoordinator,
        updateTarget: AnyObject? = nil,
        showManager: @escaping () -> Void
    ) {
        self.coordinator = coordinator
        self.showManager = showManager
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        pauseMenuItem = NSMenuItem(title: String(localized: "Pause Rendering"), action: #selector(togglePause), keyEquivalent: "")
        loginMenuItem = NSMenuItem(title: String(localized: "Launch at Login"), action: #selector(toggleLaunchAtLogin), keyEquivalent: "")
        super.init()

        statusItem.button?.image = NSImage(
            systemSymbolName: "square.3.layers.3d.down.left",
            accessibilityDescription: "DeskLayer"
        )

        let menu = NSMenu()
        menu.delegate = self
        let show = NSMenuItem(title: String(localized: "Show Manager"), action: #selector(showManagerAction), keyEquivalent: "m")
        show.target = self
        menu.addItem(show)
        pauseMenuItem.target = self
        menu.addItem(pauseMenuItem)
        menu.addItem(.separator())
        let openFolder = NSMenuItem(title: String(localized: "Open Plugins Folder"), action: #selector(openPluginsFolder), keyEquivalent: "")
        openFolder.target = self
        menu.addItem(openFolder)
        loginMenuItem.target = self
        menu.addItem(loginMenuItem)
        if let updateTarget {
            let update = NSMenuItem(title: String(localized: "Check for Updates…"),
                                    action: Selector(("checkForUpdatesAction:")), keyEquivalent: "")
            update.target = updateTarget
            menu.addItem(update)
        }
        menu.addItem(.separator())
        let about = NSMenuItem(title: String(localized: "About DeskLayer"), action: #selector(showAbout), keyEquivalent: "")
        about.target = self
        menu.addItem(about)
        menu.addItem(NSMenuItem(title: String(localized: "Quit DeskLayer"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        pauseMenuItem.title = coordinator.isUserPaused
            ? String(localized: "Resume Rendering")
            : String(localized: "Pause Rendering")
        loginMenuItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }

    /// The standard panel, which reads name, version and copyright straight
    /// from the bundle — nothing to keep in sync with the release. A status
    /// item app has no app menu, so this is the only place to find it.
    @objc private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)
        NSApp.orderFrontStandardAboutPanel(nil)
    }

    @objc private func toggleLaunchAtLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            NSApp.presentError(error)
        }
    }

    @objc private func showManagerAction() {
        showManager()
    }

    @objc private func togglePause() {
        coordinator.isUserPaused.toggle()
    }

    @objc private func openPluginsFolder() {
        NSWorkspace.shared.open(PluginRegistry.directoryURL)
    }
}
