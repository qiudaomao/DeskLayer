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
    convenience init(
        store: LayoutStore,
        registry: PluginRegistry,
        screens: ScreenManager,
        coordinator: RuntimeCoordinator
    ) {
        let root = ManagerRootView()
            .environmentObject(store)
            .environmentObject(registry)
            .environmentObject(screens)
            .environmentObject(coordinator)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1080, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "DeskLayer"
        // Full-height sidebar, Finder/Notes-style: content extends under a
        // transparent titlebar; NavigationSplitView manages the safe areas.
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.toolbarStyle = .unified
        window.titlebarSeparatorStyle = .automatic
        window.center()
        window.setFrameAutosaveName("ManagerWindow")
        window.contentViewController = NSHostingController(rootView: root)
        window.isReleasedWhenClosed = false
        self.init(window: window)
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
