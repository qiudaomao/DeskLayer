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
    /// Posted by every launching copy; older copies quit when they hear it.
    private static let instanceNote =
        Notification.Name("com.qiudaomao.DeskLayer.instance-launched")

    func applicationDidFinishLaunching(_ aNotification: Notification) {
        // Two copies of the app (an old version still running from login, a
        // dev build beside the installed one) share UserDefaults, and the
        // last one to save wins — an instance holding an empty store list
        // will happily write it over the other's. One instance only: each
        // copy announces its launch, and any copy that hears another quits
        // itself. Self-termination needs no permissions, unlike asking the
        // other process to quit (Apple-event quits are TCC-gated and get
        // silently dropped). Skipped under tests, where this process hosts
        // the test runner and must not react to the user's real copy.
        if NSClassFromString("XCTestCase") == nil {
            let pid = String(ProcessInfo.processInfo.processIdentifier)
            let center = DistributedNotificationCenter.default()
            center.addObserver(forName: Self.instanceNote, object: nil, queue: .main) { note in
                guard (note.object as? String) != pid else { return }
                // A newer copy just launched; it wins.
                NSApp.terminate(nil)
            }
            center.postNotificationName(Self.instanceNote, object: pid, userInfo: nil,
                                        deliverImmediately: true)
        }

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
        // Next turn, not inside the launch transaction: the window's first
        // constraint pass otherwise runs while AppKit is mid-launch, which
        // is where a fresh install crashed in the update-constraints loop.
        DispatchQueue.main.async { [weak manager] in
            manager?.show()
        }
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
