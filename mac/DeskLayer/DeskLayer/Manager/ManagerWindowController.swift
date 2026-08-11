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
        // Match ManagerRootView's .frame(minWidth: 900, minHeight: 560): if
        // AppKit can propose a smaller size, the split view's constraints
        // push back and the two can oscillate inside one layout pass —
        // AppKit's loop guard then throws (seen on a fresh install, where
        // no saved frame exists).
        window.contentMinSize = NSSize(width: 900, height: 560)
        window.isReleasedWhenClosed = false
        self.init(window: window)

        // A menu bar app while the manager is closed: no Dock icon, no
        // Cmd-Tab entry — the status item is how you come back. The Dock
        // icon returns whenever the window is open, so the app behaves like
        // a normal one while you're actually working in it.
        NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: window, queue: .main
        ) { _ in
            MainActor.assumeIsolated {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }

    func show() {
        // Back to a regular app while the window is up. Policy first: an
        // accessory app can't become active, so activate would be a no-op.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
