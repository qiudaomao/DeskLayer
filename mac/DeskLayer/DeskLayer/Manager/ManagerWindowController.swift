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
        author: PluginAuthorSession,
        community: CommunityAccount
    ) {
        let root = ManagerRootView()
            .environmentObject(store)
            .environmentObject(registry)
            .environmentObject(screens)
            .environmentObject(coordinator)
            .environmentObject(stores)
            .environmentObject(author)
            .environmentObject(community)

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

        // ONE authority on the minimum size: the window. The root view used
        // to carry .frame(minWidth:minHeight:) too, and the two disagreed by
        // exactly the titlebar height once a 900x560 *frame* was restored —
        // SwiftUI demanded 560pt of content from a window that provides 532,
        // and the constraint engine oscillated until AppKit's loop guard
        // killed the app (every launch, on a machine that had saved that
        // frame). Min first, so the restore below is clamped against it.
        window.contentMinSize = NSSize(width: 900, height: 560)

        // setFrameAutosaveName restores the saved frame if there is one, so
        // only fall back to centring for a first run.
        let hadSavedFrame = UserDefaults.standard
            .string(forKey: "NSWindow Frame \(Self.frameAutosaveName)") != nil
        window.setFrameAutosaveName(Self.frameAutosaveName)
        if !hadSavedFrame { window.center() }
        // A frame saved by an older (crashing) build can still be under the
        // minimum; grow it back so no launch starts inside the conflict.
        let content = window.contentRect(forFrameRect: window.frame).size
        if content.width < 900 || content.height < 560 {
            window.setContentSize(NSSize(width: max(content.width, 900),
                                         height: max(content.height, 560)))
        }
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
