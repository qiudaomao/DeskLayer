//
//  AppDelegate.swift
//  DeskLayer
//

import Cocoa

@main
class AppDelegate: NSObject, NSApplicationDelegate {
    // Without a storyboard, NSApplicationMain never instantiates the
    // delegate — wire it ourselves. NSApplication.delegate is not retained,
    // so keep a strong reference.
    private static var delegateRef: AppDelegate?

    static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        let delegate = AppDelegate()
        delegateRef = delegate
        app.delegate = delegate
        app.run()
    }

    private let layoutStore = LayoutStore()
    private let screenManager = ScreenManager()
    private let pluginRegistry = PluginRegistry()
    private var coordinator: RuntimeCoordinator?
    private var managerWindow: ManagerWindowController?
    private var statusItem: StatusItemController?

    func applicationDidFinishLaunching(_ aNotification: Notification) {
        let coordinator = RuntimeCoordinator(
            store: layoutStore,
            screens: screenManager,
            plugins: pluginRegistry
        )
        self.coordinator = coordinator
        coordinator.start()

        let manager = ManagerWindowController(
            store: layoutStore,
            registry: pluginRegistry,
            screens: screenManager,
            coordinator: coordinator
        )
        managerWindow = manager
        statusItem = StatusItemController(coordinator: coordinator) { [weak manager] in
            manager?.show()
        }
        manager.show()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        managerWindow?.show()
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        true
    }
}
