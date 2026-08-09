//
//  ManagerWindowController.swift
//  DeskLayer
//
//  Hosts the SwiftUI 3-pane manager in a plain NSWindow (the AppKit shell
//  keeps window lifecycle out of SwiftUI App-lifecycle territory).
//

import AppKit
import SwiftUI

@MainActor
final class ManagerWindowController: NSWindowController {
    private static let frameAutosaveName = "ManagerWindow"

    convenience init(
        store: LayoutStore,
        registry: PluginRegistry,
        screens: ScreenManager,
        coordinator: RuntimeCoordinator,
        stores: PluginStoreRegistry,
        author: PluginAuthorSession
    ) {
        let root = ManagerRootView()
            .environmentObject(store)
            .environmentObject(registry)
            .environmentObject(screens)
            .environmentObject(coordinator)
            .environmentObject(stores)
            .environmentObject(author)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1080, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "DeskLayer"
        // Full-height sidebar, Finder/Notes-style: content extends under a
        // transparent titlebar; NavigationSplitView manages the safe areas.
        // Title stays visible — it anchors the toolbar's leading/trailing
        // sections so primaryAction items sit at the right edge.
        window.titlebarAppearsTransparent = true
        window.toolbarStyle = .unified
        window.titlebarSeparatorStyle = .automatic

        // Content first, and with sizing options cleared: an NSHostingController
        // otherwise pushes SwiftUI's ideal size onto the window, which would
        // overwrite whatever frame the autosave restores below.
        let hosting = NSHostingController(rootView: root)
        hosting.sizingOptions = []
        window.contentViewController = hosting

        // setFrameAutosaveName restores the saved frame if there is one, so
        // only fall back to centring for a first run.
        let hadSavedFrame = UserDefaults.standard
            .string(forKey: "NSWindow Frame \(Self.frameAutosaveName)") != nil
        window.setFrameAutosaveName(Self.frameAutosaveName)
        if !hadSavedFrame { window.center() }
        window.isReleasedWhenClosed = false
        self.init(window: window)
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
