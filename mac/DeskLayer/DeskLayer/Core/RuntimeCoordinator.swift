//
//  RuntimeCoordinator.swift
//  DeskLayer
//
//  Turns the LayoutStore's model into running plugin instances placed on
//  desktop windows. M1 strategy: full teardown + rebuild on any change
//  (fine-grained diffing arrives with the manager UI in M2 — live property
//  edits already go through PluginInstance.applyOverride without a rebuild).
//

import AppKit
import Combine
import os

@MainActor
final class RuntimeCoordinator: ObservableObject {
    private let store: LayoutStore
    private let screens: ScreenManager
    private let plugins: PluginRegistry
    private let power = PowerStateController()

    /// Menu-bar "Pause Rendering" toggle; overrides the power policy.
    @Published var isUserPaused = false {
        didSet { pushPolicies() }
    }

    /// Latest frame per item (throttled), for the manager's virtual desktop.
    @Published private(set) var thumbnails: [UUID: CGImage] = [:]

    private enum ItemRuntime {
        case canvas(renderer: ItemRenderer, layer: CALayer)
        case declarative(host: DeclarativeItemHost)
    }

    private struct RunningItem {
        let layoutID: UUID
        let instance: PluginInstance
        let runtime: ItemRuntime
        let displayUUID: String
        var panel: FloatingPanelController?
    }

    private var running: [UUID: RunningItem] = [:]
    private var suppressRebuild = false
    private var cancellables: Set<AnyCancellable> = []
    private let widgetPublisher = WidgetPublisher()
    private let hookServer = HookServer()
    /// Default loopback port for the hook receiver ($server plugins).
    static let hookPort: UInt16 = 8787
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "coordinator")

    init(store: LayoutStore, screens: ScreenManager, plugins: PluginRegistry) {
        self.store = store
        self.screens = screens
        self.plugins = plugins
    }

    func start() {
        power.start()
        screens.start()
        plugins.bootstrap()
        hookServer.start(port: Self.hookPort)

        store.onChange
            .sink { [weak self] in
                guard let self, !self.suppressRebuild else { return }
                self.rebuild()
            }
            .store(in: &cancellables)
        screens.onScreensChanged
            .sink { [weak self] in self?.rebuild() }
            .store(in: &cancellables)
        power.$policy
            .sink { [weak self] _ in self?.pushPolicies() }
            .store(in: &cancellables)

        store.load()

        // Debug hook: DESKLAYER_SNAPSHOT=1 dumps every item's frame as PNG
        // (base64 on stderr) every 8 seconds, so a terminal session can watch
        // live edits land on the wallpaper.
        if ProcessInfo.processInfo.environment["DESKLAYER_SNAPSHOT"] == "1" {
            Timer.scheduledTimer(withTimeInterval: 8, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    let dir = URL(fileURLWithPath: NSTemporaryDirectory())
                    for item in self.running.values {
                        let url = dir.appendingPathComponent("desklayer-\(item.instance.pluginID)-\(item.layoutID).png")
                        switch item.runtime {
                        case .canvas(let renderer, _): renderer.writeDebugSnapshot(to: url)
                        case .declarative(let host): host.writeDebugSnapshot(to: url)
                        }
                    }
                }
            }
        }
    }

    private func pushPolicies() {
        let effective: RenderPolicy = isUserPaused ? .paused : power.policy
        for controller in screens.controllers.values {
            controller.scheduler.policy = effective
        }
        for item in running.values {
            if case .declarative(let host) = item.runtime {
                host.isPaused = effective == .paused
            }
        }
    }

    func rebuild() {
        // Teardown current runtime. Clear hook handlers synchronously first
        // so a stale instance's async teardown can't drop a re-registration.
        hookServer.removeAllHandlers()
        for controller in screens.controllers.values {
            controller.scheduler.removeAll()
        }
        for item in running.values {
            item.instance.invalidate()
            switch item.runtime {
            case .canvas(_, let layer): layer.removeFromSuperlayer()
            case .declarative(let host): host.stop()
            }
            item.panel?.tearDown()
        }
        running.removeAll()

        // Spawn model items whose display is connected.
        for layoutItem in store.layout.items where layoutItem.isEnabled {
            guard let controller = screens.controller(forDisplayUUID: layoutItem.displayUUID) else {
                log.info("item \(layoutItem.pluginID, privacy: .public) offline (display absent)")
                debugPrint("item \(layoutItem.pluginID) offline: display \(layoutItem.displayUUID) not in \(Array(screens.controllers.keys))")
                continue
            }
            spawn(layoutItem, on: controller)
        }
        log.info("rebuilt runtime: \(self.running.count) items running")
        debugPrint("rebuilt: \(running.count)/\(store.layout.items.count) items running; plugins: \(plugins.plugins.map(\.id))")
        thumbnails = thumbnails.filter { running.keys.contains($0.key) }
        widgetPublisher.prune(currentItemIDs: Array(running.keys))
    }

    /// stderr diagnostics for terminal-launched debug runs (os_log is
    /// unreadable from a sandboxed-away terminal).
    private func debugPrint(_ message: String) {
        guard ProcessInfo.processInfo.environment["DESKLAYER_SNAPSHOT"] == "1" else { return }
        FileHandle.standardError.write(Data("[coordinator] \(message)\n".utf8))
    }

    private func spawn(_ layoutItem: LayoutItem, on controller: DesktopWindowController) {
        guard let source = plugins.source(for: layoutItem.pluginID) else {
            log.error("plugin source missing: \(layoutItem.pluginID, privacy: .public)")
            debugPrint("plugin source missing: \(layoutItem.pluginID)")
            return
        }
        guard let instance = PluginInstance(
            pluginID: layoutItem.pluginID,
            source: source,
            overrides: layoutItem.propertyOverrides
        ) else {
            log.error("plugin failed to boot: \(layoutItem.pluginID, privacy: .public)")
            return
        }

        let screenFrame = controller.screen.frame
        // Frame within the desktop window's content (origin-relative).
        let localFrame = CGRect(
            x: layoutItem.normalizedFrame.origin.x * screenFrame.width,
            y: layoutItem.normalizedFrame.origin.y * screenFrame.height,
            width: layoutItem.normalizedFrame.width * screenFrame.width,
            height: layoutItem.normalizedFrame.height * screenFrame.height
        )
        let scale = controller.screen.backingScaleFactor
        let itemID = layoutItem.id
        let isFloating = layoutItem.target == .floatingWindow

        // Wire $server.on(...) to the shared app-level hook receiver.
        if instance.permissions.contains("server") {
            let server = hookServer
            instance.connectHooks(
                register: { method, handler in
                    server.addHandler(HookServer.Handler(itemID: itemID, method: method, deliver: handler))
                },
                unregister: { server.removeHandlers(itemID: itemID) }
            )
        }

        // Floating items live in their own panel (absolute screen coords);
        // the item content then fills the panel's content view.
        var panel: FloatingPanelController?
        let contentFrame: CGRect
        if isFloating {
            let panelFrame = localFrame.offsetBy(dx: screenFrame.minX, dy: screenFrame.minY)
            let floatingPanel = FloatingPanelController(frame: panelFrame, screen: controller.screen)
            floatingPanel.isClickThrough = layoutItem.clickThrough
            floatingPanel.onMoved = { [weak self] normalized in
                guard let self, var item = self.store.layout.items.first(where: { $0.id == itemID }) else { return }
                item.normalizedFrame = normalized
                self.suppressRebuild = true
                self.store.update(item)
                self.suppressRebuild = false
            }
            panel = floatingPanel
            contentFrame = CGRect(origin: .zero, size: localFrame.size)
        } else {
            contentFrame = localFrame
        }
        let hostView: NSView? = isFloating ? panel?.panel.contentView : controller.window.contentView

        switch instance.renderMode {
        case .canvas:
            let assetsURL = plugins.descriptor(for: layoutItem.pluginID)?.assetsURL
            guard let renderer = ItemRenderer(
                instance: instance, size: contentFrame.size, scale: scale, assetsURL: assetsURL
            ) else {
                log.error("renderer failed for \(layoutItem.pluginID, privacy: .public) (zero size?)")
                instance.invalidate()
                return
            }
            let layer = CALayer()
            layer.frame = contentFrame
            layer.contentsScale = scale
            layer.contentsGravity = .resize
            layer.zPosition = CGFloat(layoutItem.zOrder)
            hostView?.layer?.addSublayer(layer)

            let scheduled = ScheduledItem(id: itemID, renderer: renderer, layer: layer)
            // A floating panel stays visible when the desktop is covered.
            scheduled.pausesWhenDesktopOccluded = !isFloating
            let pluginID = layoutItem.pluginID
            scheduled.onThumbnail = { [weak self] image in
                self?.thumbnails[itemID] = image
                self?.widgetPublisher.publishCanvas(itemID: itemID, pluginID: pluginID, image: image)
            }
            controller.scheduler.add(scheduled)
            running[itemID] = RunningItem(
                layoutID: itemID,
                instance: instance,
                runtime: .canvas(renderer: renderer, layer: layer),
                displayUUID: layoutItem.displayUUID,
                panel: panel
            )

        case .declarative:
            let host = DeclarativeItemHost(instance: instance, frame: contentFrame)
            host.hostingView.layer?.zPosition = CGFloat(layoutItem.zOrder)
            host.hostingView.autoresizingMask = isFloating ? [.width, .height] : []
            host.onThumbnail = { [weak self] image in
                self?.thumbnails[itemID] = image
            }
            let pluginID = layoutItem.pluginID
            host.onTreeJSON = { [weak self] json in
                self?.widgetPublisher.publishDeclarative(itemID: itemID, pluginID: pluginID, treeJSON: json)
            }
            hostView?.addSubview(host.hostingView)
            host.isPaused = isUserPaused
            host.start()
            running[itemID] = RunningItem(
                layoutID: itemID,
                instance: instance,
                runtime: .declarative(host: host),
                displayUUID: layoutItem.displayUUID,
                panel: panel
            )
        }
        panel?.show()
    }

    /// Live property edit path from the inspector: pushes into the running
    /// JS context without a runtime rebuild. fps is consumed by the Swift
    /// scheduler at spawn, so an fps edit rebuilds instead.
    func applyOverride(itemID: UUID, name: String, value: PropertyValue) {
        running[itemID]?.instance.applyOverride(name: name, value: value)
        if var item = store.layout.items.first(where: { $0.id == itemID }) {
            item.propertyOverrides[name] = value
            suppressRebuild = true
            store.update(item)
            suppressRebuild = false
        }
        if name == "fps" || name == "interval" {
            rebuild()
        } else if case .declarative(let host) = running[itemID]?.runtime {
            // Static declarative plugins re-render only on edits; ticking
            // ones get instant feedback instead of waiting for the timer.
            host.renderOnce()
        }
    }

    /// Live frame updates from canvas drags. Moves reposition the running
    /// CALayer directly (no re-render, no rebuild). A size change needs new
    /// pixel buffers, so it rebuilds — but only when `commit` (drag end);
    /// during a live resize the layer just stretches (contentsGravity).
    func setFrame(itemID: UUID, normalizedFrame: CGRect, commit: Bool) {
        guard var item = store.layout.items.first(where: { $0.id == itemID }) else { return }
        let sizeChanged = item.normalizedFrame.size != normalizedFrame.size
        item.normalizedFrame = normalizedFrame
        suppressRebuild = true
        store.update(item)
        suppressRebuild = false

        if let runningItem = running[itemID],
           let controller = screens.controller(forDisplayUUID: item.displayUUID) {
            let screenFrame = controller.screen.frame
            let frame = CGRect(
                x: normalizedFrame.origin.x * screenFrame.width,
                y: normalizedFrame.origin.y * screenFrame.height,
                width: normalizedFrame.width * screenFrame.width,
                height: normalizedFrame.height * screenFrame.height
            )
            if let panel = runningItem.panel {
                panel.setFrame(frame.offsetBy(dx: screenFrame.minX, dy: screenFrame.minY))
                // Canvas content in a resized panel needs new pixel buffers.
                if case .canvas = runningItem.runtime, commit, sizeChanged { rebuild() }
                return
            }
            switch runningItem.runtime {
            case .canvas(_, let layer):
                CATransaction.begin()
                CATransaction.setDisableActions(true)
                layer.frame = frame
                CATransaction.commit()
                // New pixel buffers needed at the new size.
                if commit && sizeChanged { rebuild() }
            case .declarative(let host):
                // SwiftUI relayouts; no buffer reallocation ever needed.
                host.hostingView.frame = frame
            }
        } else if commit && sizeChanged {
            rebuild()
        }
    }

    /// console.log output of a running item, for the inspector's log panel.
    func logs(for itemID: UUID) -> [PluginLogEntry] {
        running[itemID]?.instance.recentLogs() ?? []
    }

    func clearLogs(for itemID: UUID) {
        running[itemID]?.instance.clearLogs()
    }

    /// Inspector status line: nil when the item runs fine.
    func errorMessage(for itemID: UUID) -> String? {
        guard let item = store.layout.items.first(where: { $0.id == itemID }) else { return nil }
        guard let runningItem = running[itemID] else {
            if screens.controller(forDisplayUUID: item.displayUUID) == nil {
                return "offline — display not connected"
            }
            if plugins.descriptor(for: item.pluginID) == nil {
                return "plugin \"\(item.pluginID)\" not found"
            }
            return item.isEnabled ? "failed to start (see log)" : nil
        }
        if runningItem.instance.isErrored {
            return runningItem.instance.errorMessage ?? "plugin threw an exception"
        }
        return nil
    }
}
