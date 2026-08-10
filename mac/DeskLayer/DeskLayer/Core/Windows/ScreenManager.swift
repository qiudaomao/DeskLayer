//
//  ScreenManager.swift
//  DeskLayer
//
//  Maps stable display UUIDs ↔ NSScreens and keeps one DesktopWindowController
//  per connected screen, reconciled idempotently on every screen-parameter
//  change. Items on absent displays stay in the model untouched (offline).
//

import AppKit
import Combine
import os

@MainActor
final class DesktopWindowController: NSObject {
    let displayUUID: String
    let window: DesktopWindow
    let scheduler = FrameScheduler()
    private(set) var screen: NSScreen

    init(screen: NSScreen, displayUUID: String) {
        self.screen = screen
        self.displayUUID = displayUUID
        self.window = DesktopWindow(screen: screen)
        super.init()
        scheduler.attach(to: window)
        window.orderFrontRegardless()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(occlusionChanged),
            name: NSWindow.didChangeOcclusionStateNotification,
            object: window
        )
        occlusionChanged()
    }

    func update(screen: NSScreen) {
        self.screen = screen
        window.setFrame(screen.frame, display: true)
        scheduler.rebuildLink()
    }

    func tearDown() {
        scheduler.removeAll()
        window.orderOut(nil)
        NotificationCenter.default.removeObserver(self)
    }

    @objc private func occlusionChanged() {
        scheduler.isOccluded = !window.occlusionState.contains(.visible)
    }

    /// Re-reads state that a missed notification would have left stale, and
    /// rebuilds the display link, which does not survive display sleep.
    func refreshAfterWake() {
        occlusionChanged()
        scheduler.rebuildLink()
    }
}

@MainActor
final class ScreenManager: ObservableObject {
    @Published private(set) var controllers: [String: DesktopWindowController] = [:]

    /// Fires after reconcile so the coordinator can re-place items.
    let onScreensChanged = PassthroughSubject<Void, Never>()

    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "screens")

    static func displayUUID(for screen: NSScreen) -> String? {
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            return nil
        }
        guard let uuidRef = CGDisplayCreateUUIDFromDisplayID(number.uint32Value)?.takeRetainedValue() else {
            return nil
        }
        return CFUUIDCreateString(nil, uuidRef) as String
    }

    func start() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(screensChanged),
            name: NSApplication.didChangeScreenParametersNotification,
            object: nil
        )
        reconcile()
        geometry = Self.currentGeometry()
    }

    func controller(forDisplayUUID uuid: String) -> DesktopWindowController? {
        controllers[uuid]
    }

    /// What the runtime actually cares about: which displays exist, where,
    /// at what scale. The notification fires for far more — a ProMotion
    /// refresh switch, a Screen Sharing session renegotiating, another app
    /// rebuilding the menu bar.
    private var geometry: [String: String] = [:]

    private static func currentGeometry() -> [String: String] {
        var out: [String: String] = [:]
        for screen in NSScreen.screens {
            guard let uuid = displayUUID(for: screen) else { continue }
            out[uuid] = "\(screen.frame)|\(screen.backingScaleFactor)"
        }
        return out
    }

    @objc private func screensChanged() {
        let new = Self.currentGeometry()
        guard new != geometry else {
            // Same displays, same geometry: only the mode can have changed,
            // and the display link dies with it. Rebuild the links, leave
            // the windows — and every running plugin — alone.
            log.info("screen parameters changed without geometry; rebuilding links only")
            for controller in controllers.values { controller.scheduler.rebuildLink() }
            return
        }
        log.info("screen geometry changed")
        geometry = new
        reconcile()
        onScreensChanged.send()
    }

    func reconcile() {
        var seen = Set<String>()
        for screen in NSScreen.screens {
            guard let uuid = Self.displayUUID(for: screen) else { continue }
            seen.insert(uuid)
            if let existing = controllers[uuid] {
                existing.update(screen: screen)
            } else {
                controllers[uuid] = DesktopWindowController(screen: screen, displayUUID: uuid)
                log.info("screen attached: \(screen.localizedName, privacy: .public) \(uuid, privacy: .public)")
            }
        }
        for (uuid, controller) in controllers where !seen.contains(uuid) {
            controller.tearDown()
            controllers.removeValue(forKey: uuid)
            log.info("screen detached: \(uuid, privacy: .public)")
        }
    }
}
