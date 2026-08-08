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

    init(coordinator: RuntimeCoordinator, showManager: @escaping () -> Void) {
        self.coordinator = coordinator
        self.showManager = showManager
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        pauseMenuItem = NSMenuItem(title: "Pause Rendering", action: #selector(togglePause), keyEquivalent: "")
        loginMenuItem = NSMenuItem(title: "Launch at Login", action: #selector(toggleLaunchAtLogin), keyEquivalent: "")
        super.init()

        statusItem.button?.image = NSImage(
            systemSymbolName: "square.3.layers.3d.down.left",
            accessibilityDescription: "DeskLayer"
        )

        let menu = NSMenu()
        menu.delegate = self
        let show = NSMenuItem(title: "Show Manager", action: #selector(showManagerAction), keyEquivalent: "m")
        show.target = self
        menu.addItem(show)
        pauseMenuItem.target = self
        menu.addItem(pauseMenuItem)
        menu.addItem(.separator())
        let openFolder = NSMenuItem(title: "Open Plugins Folder", action: #selector(openPluginsFolder), keyEquivalent: "")
        openFolder.target = self
        menu.addItem(openFolder)
        loginMenuItem.target = self
        menu.addItem(loginMenuItem)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit DeskLayer", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        pauseMenuItem.title = coordinator.isUserPaused ? "Resume Rendering" : "Pause Rendering"
        loginMenuItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
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
