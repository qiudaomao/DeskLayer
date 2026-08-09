//
//  AppDelegate.swift
//  DeskLayer
//

import Cocoa
import DeskLayerUpdater

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
        // Built after the delegate exists: "Check for Updates…" targets it.
        app.mainMenu = MainMenu.build(updateTarget: delegate)
        app.run()
    }

    private let layoutStore = LayoutStore()
    private let screenManager = ScreenManager()
    private let pluginRegistry = PluginRegistry()
    private let storeRegistry = PluginStoreRegistry()
    private lazy var pluginAuthor = PluginAuthorSession(registry: pluginRegistry)
    private var coordinator: RuntimeCoordinator?
    private var managerWindow: ManagerWindowController?
    private var statusItem: StatusItemController?
    private let updates = UpdateController()

    func applicationDidFinishLaunching(_ aNotification: Notification) {
        let coordinator = RuntimeCoordinator(
            store: layoutStore,
            screens: screenManager,
            plugins: pluginRegistry
        )
        self.coordinator = coordinator
        coordinator.start()

        storeRegistry.load()
        let manager = ManagerWindowController(
            store: layoutStore,
            registry: pluginRegistry,
            screens: screenManager,
            coordinator: coordinator,
            stores: storeRegistry,
            author: pluginAuthor
        )
        managerWindow = manager
        statusItem = StatusItemController(coordinator: coordinator, updateTarget: self) { [weak manager] in
            manager?.show()
        }
        manager.show()
    }

    /// Menu action for "Check for Updates…" in both the app menu and the
    /// status item. Sparkle shows its own UI from here, including the
    /// "you're up to date" case.
    @objc func checkForUpdatesAction(_ sender: Any?) {
        updates.checkForUpdates()
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
