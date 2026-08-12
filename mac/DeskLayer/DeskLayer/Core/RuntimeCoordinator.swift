//
//  RuntimeCoordinator.swift
//  DeskLayer
//
//  Turns the LayoutStore's model into running plugin instances placed on
//  desktop windows. Model changes are reconciled per item: only an item
//  whose SpawnIdentity changed is restarted, so editing one plugin leaves
//  its neighbours — and their JS state — alone. Full rebuild() is kept for
//  the cases that genuinely need it: display topology and plugin reloads.
//

import AppKit
import Combine
import DeskLayerKit
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
        case webview(host: WebViewHost)
    }

    private struct RunningItem {
        let layoutID: UUID
        let instance: PluginInstance
        let runtime: ItemRuntime
        let displayUUID: String
        var panel: FloatingPanelController?
        /// What this item was spawned from. Anything outside it — frame,
        /// z-order, background, most property edits — is applied to the live
        /// item instead of respawning it.
        let identity: SpawnIdentity
    }

    /// The parts of a layout item that decide *how* it runs. A change here
    /// means the item has to be torn down and started again; a change
    /// anywhere else must not disturb it, and must never disturb its
    /// neighbours.
    private struct SpawnIdentity: Equatable {
        let pluginID: String
        let displayUUID: String
        let target: RenderTarget
        let clickThrough: Bool
        /// mtime+size of the plugin's source. Part of the identity so an
        /// edited (or updated) plugin respawns just the items running it,
        /// while a change elsewhere in the folder — a new file arriving —
        /// leaves every identity untouched, and nothing restarts.
        let sourceStamp: String

        init(_ item: LayoutItem, sourceStamp: String) {
            pluginID = item.pluginID
            displayUUID = item.displayUUID
            target = item.target
            clickThrough = item.clickThrough
            self.sourceStamp = sourceStamp
        }
    }

    /// mtime+size of a plugin's source, or "" when it isn't installed.
    ///
    /// Read through FileManager, not URL.resourceValues: a URL caches the
    /// values it has already fetched, and the descriptor hands back the same
    /// URL every time — so an edited plugin would keep reporting the size it
    /// had at launch, and never reload.
    private func sourceStamp(for pluginID: String) -> String {
        guard let path = plugins.descriptor(for: pluginID)?.sourceURL.path,
              let attributes = try? FileManager.default.attributesOfItem(atPath: path),
              let modified = attributes[.modificationDate] as? Date,
              let size = attributes[.size] as? Int
        else { return "" }
        return "\(modified.timeIntervalSince1970):\(size)"
    }

    private var running: [UUID: RunningItem] = [:]
    private var suppressRebuild = false
    private var cancellables: Set<AnyCancellable> = []
    private let widgetPublisher = WidgetPublisher()
    /// The hook receiver ($server plugins). Binds lazily — only while a
    /// running plugin has a handler registered — on HookServer.resolvedPort()
    /// (DESKLAYER_HOOK_PORT env var > DeskLayer.hookPort default > 8787).
    private let hookServer = HookServer()
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

        store.onChange
            .sink { [weak self] in
                guard let self, !self.suppressRebuild else { return }
                self.reconcile()
            }
            .store(in: &cancellables)
        screens.onScreensChanged
            // Reconcile, not rebuild: a resize re-places every item (their
            // frames are normalized), and only items whose display came or
            // went are (re)spawned. JS state survives a resolution change —
            // Screen Sharing renegotiates the display often enough that a
            // full restart on each one is very visible.
            .sink { [weak self] in self?.reconcile() }
            .store(in: &cancellables)
        // Hot-reload running items when a plugin file changes (edit, import,
        // or an applied update). Reconcile, not rebuild: the source stamp in
        // SpawnIdentity restarts exactly the items whose own plugin changed,
        // so copying a new plugin into the folder no longer restarts every
        // widget on the desktop and throws away its JS state.
        plugins.didChange
            .debounce(for: .milliseconds(300), scheduler: RunLoop.main)
            .sink { [weak self] in self?.reconcile() }
            .store(in: &cancellables)
        power.$policy
            .sink { [weak self] _ in self?.pushPolicies() }
            .store(in: &cancellables)
        power.didWake
            .sink { [weak self] in self?.resumeAfterWake() }
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
                        case .webview: break // no snapshot path for web content
                        }
                    }
                }
            }
        }
    }

    /// Waking is not just "unpause". The display link is gone once its
    /// display has slept, and occlusion is remembered from a notification
    /// that may never arrive again — leaving every item computing as paused
    /// until something else disturbs it. Re-assert both, then repaint, so
    /// the desktop comes back on its own rather than after a manual
    /// pause/resume.
    private func resumeAfterWake() {
        for controller in screens.controllers.values {
            controller.refreshAfterWake()
        }
        pushPolicies()
        for item in running.values {
            switch item.runtime {
            case .declarative(let host): host.renderOnce()
            case .canvas, .webview: break
            }
        }
        log.info("resumed after wake: \(self.running.count) items")
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

    /// Brings the runtime in line with the model, disturbing as little as
    /// possible: an edit to one item must not restart its neighbours, which
    /// would drop their JS state (counters, fetched data, open sockets) and
    /// flash every widget on screen. Only items whose SpawnIdentity changed
    /// are restarted; everything else is adjusted in place.
    func reconcile() {
        let placeable = store.layout.items.filter { item in
            item.isEnabled && screens.controller(forDisplayUUID: item.displayUUID) != nil
        }
        let wanted = Dictionary(placeable.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })

        for (id, item) in running where wanted[id].map({ SpawnIdentity($0, sourceStamp: self.sourceStamp(for: $0.pluginID)) }) != item.identity {
            teardown(id)
        }
        for item in placeable {
            if running[item.id] == nil {
                guard let controller = screens.controller(forDisplayUUID: item.displayUUID) else { continue }
                spawn(item, on: controller)
            } else {
                applyLiveEdits(item)
            }
        }
        log.info("reconciled runtime: \(self.running.count) items running")
        thumbnails = thumbnails.filter { running.keys.contains($0.key) }
        widgetPublisher.prune(currentItemIDs: Array(running.keys))
    }

    /// Restarts one item — for edits it can't absorb, like a new fps (the
    /// scheduler holds the interval) or a webview's url. Its neighbours keep
    /// running, and keep their state.
    private func respawn(_ itemID: UUID) {
        teardown(itemID)
        guard let item = store.layout.items.first(where: { $0.id == itemID }), item.isEnabled,
              let controller = screens.controller(forDisplayUUID: item.displayUUID) else { return }
        spawn(item, on: controller)
    }

    /// Stops one item and forgets it, leaving every other item running.
    private func teardown(_ itemID: UUID) {
        guard let item = running.removeValue(forKey: itemID) else { return }
        hookServer.removeHandlers(itemID: itemID)
        item.instance.invalidate()
        switch item.runtime {
        case .canvas(_, let layer):
            layer.removeFromSuperlayer()
            screens.controller(forDisplayUUID: item.displayUUID)?.scheduler.remove(id: itemID)
        case .declarative(let host): host.stop()
        case .webview(let host): host.stop()
        }
        item.panel?.tearDown()
    }

    /// Model changes a running item can absorb without restarting.
    private func applyLiveEdits(_ item: LayoutItem) {
        setFrame(itemID: item.id, normalizedFrame: item.normalizedFrame, commit: false)
        let background = item.backgroundColor.flatMap { CSSColor.parse($0) }
        switch running[item.id]?.runtime {
        case .canvas(_, let layer):
            layer.zPosition = CGFloat(item.zOrder)
            layer.backgroundColor = background
        case .declarative(let host):
            host.hostingView.layer?.zPosition = CGFloat(item.zOrder)
            host.hostingView.layer?.backgroundColor = background
        case .webview(let host):
            host.webView.layer?.zPosition = CGFloat(item.zOrder)
        case nil:
            break
        }
    }

    func rebuild() {
        log.info("full rebuild: tearing down \(self.running.count) items")
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
            case .webview(let host): host.stop()
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

        // Supply the ssh() destinations (passwords read from the Keychain).
        if instance.permissions.contains("ssh") {
            instance.configureSSH(Self.resolveSSH(for: layoutItem))
        }

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
        // nil / unparseable → clear (fully transparent).
        let backgroundCGColor = layoutItem.backgroundColor.flatMap { CSSColor.parse($0) }

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
            // Backdrop shows through the transparent parts of the plugin's
            // frame; nil = fully transparent.
            layer.backgroundColor = backgroundCGColor
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
                panel: panel,
                identity: SpawnIdentity(layoutItem, sourceStamp: sourceStamp(for: layoutItem.pluginID))
            )

        case .declarative:
            let host = DeclarativeItemHost(instance: instance, frame: contentFrame)
            host.hostingView.layer?.zPosition = CGFloat(layoutItem.zOrder)
            host.hostingView.layer?.backgroundColor = backgroundCGColor
            host.hostingView.autoresizingMask = isFloating ? [.width, .height] : []
            host.onThumbnail = { [weak self] image in
                self?.thumbnails[itemID] = image
            }
            let pluginID = layoutItem.pluginID
            host.onTreeJSON = { [weak self] json in
                self?.widgetPublisher.publishDeclarative(itemID: itemID, pluginID: pluginID, treeJSON: json)
            }
            // Declarative content lays out at its natural size; adopt it so
            // the desktop and the manager's virtual desktop agree.
            host.onContentSize = { [weak self] size in
                self?.adoptContentSize(itemID: itemID, size: size)
            }
            hostView?.addSubview(host.hostingView)
            host.isPaused = isUserPaused
            host.start()
            running[itemID] = RunningItem(
                layoutID: itemID,
                instance: instance,
                runtime: .declarative(host: host),
                displayUUID: layoutItem.displayUUID,
                panel: panel,
                identity: SpawnIdentity(layoutItem, sourceStamp: sourceStamp(for: layoutItem.pluginID))
            )

        case .webview:
            let host = WebViewHost(
                pluginID: layoutItem.pluginID,
                config: instance.webviewConfig ?? WebViewConfig(url: "", userAgent: nil, headers: [:], cookies: [], offsetX: 0, offsetY: 0, zoom: 1),
                frame: contentFrame
            )
            host.webView.frame = contentFrame
            host.webView.layer?.zPosition = CGFloat(layoutItem.zOrder)
            host.webView.layer?.backgroundColor = backgroundCGColor
            host.webView.autoresizingMask = isFloating ? [.width, .height] : []
            host.onThumbnail = { [weak self] image in self?.thumbnails[itemID] = image }
            hostView?.addSubview(host.webView)
            host.start()
            running[itemID] = RunningItem(
                layoutID: itemID,
                instance: instance,
                runtime: .webview(host: host),
                displayUUID: layoutItem.displayUUID,
                panel: panel,
                identity: SpawnIdentity(layoutItem, sourceStamp: sourceStamp(for: layoutItem.pluginID))
            )
        }
        panel?.show()
    }

    /// Live property edit path from the inspector: pushes into the running
    /// JS context without a runtime rebuild. fps is consumed by the Swift
    /// scheduler at spawn, so an fps edit rebuilds instead.
    func applyOverride(itemID: UUID, name: String, value: PropertyValue) {
        // A no-op edit must never rebuild: selecting an item re-seeds the
        // inspector's editors, and an fps/interval "change" to the same value
        // would otherwise respawn every plugin (visible flash).
        if let current = effectiveProperty(itemID: itemID, name: name), current == value {
            return
        }
        running[itemID]?.instance.applyOverride(name: name, value: value)
        if var item = store.layout.items.first(where: { $0.id == itemID }) {
            item.propertyOverrides[name] = value
            suppressRebuild = true
            store.update(item)
            suppressRebuild = false
        }
        if name == "fps" || name == "interval" {
            respawn(itemID)
        } else if case .webview = running[itemID]?.runtime {
            // url / offset / zoom are baked into the webview config at spawn.
            respawn(itemID)
        } else if case .declarative(let host) = running[itemID]?.runtime {
            // Static declarative plugins re-render only on edits; ticking
            // ones get instant feedback instead of waiting for the timer.
            host.renderOnce()
        }
    }

    /// SSH destination edits from the inspector: persist and re-resolve the
    /// running instance's ssh() config live, WITHOUT a runtime rebuild — so
    /// typing in the SSH fields doesn't flash every widget on screen.
    func updateSSH(_ updated: LayoutItem) {
        suppressRebuild = true
        store.update(updated)
        suppressRebuild = false
        guard let runningItem = running[updated.id],
              runningItem.instance.permissions.contains("ssh") else { return }
        runningItem.instance.configureSSH(Self.resolveSSH(for: updated))
    }

    /// Layout SSH configs → runtime destinations, pulling each password from
    /// the Keychain (never stored in layout.json).
    static func resolveSSH(for item: LayoutItem) -> [HostBindings.ResolvedSSH] {
        item.sshHosts.filter(\.isConfigured).map { cfg in
            // In alias mode ssh reads everything from ~/.ssh/config, so any
            // manual values left over from before the switch are dropped
            // rather than silently overriding the alias.
            HostBindings.ResolvedSSH(
                name: cfg.name,
                host: cfg.host,
                port: cfg.usesAlias ? 22 : cfg.port,
                user: cfg.usesAlias ? "" : cfg.user,
                usesKey: !cfg.usesAlias && cfg.auth == .key,
                keyPath: cfg.usesAlias ? "" : cfg.keyPath,
                password: !cfg.usesAlias && cfg.auth == .password
                    ? KeychainStore.password(forItem: item.id, host: cfg.id) : nil
            )
        }
    }

    /// Live frame updates from canvas drags. Moves reposition the running
    /// CALayer directly (no re-render, no rebuild). A size change needs new
    /// pixel buffers, so it rebuilds — but only when `commit` (drag end);
    /// during a live resize the layer just stretches (contentsGravity).
    func setFrame(itemID: UUID, normalizedFrame: CGRect, commit: Bool) {
        guard var item = store.layout.items.first(where: { $0.id == itemID }) else { return }
        let normalizedFrame = clampedToPluginLimits(normalizedFrame, item: item)
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
            case .webview(let host):
                host.webView.frame = frame
            }
        } else if commit && sizeChanged {
            rebuild()
        }
    }

    /// Applies the plugin's declared min/max size (points) to a normalized
    /// frame. Central, so drags, inspector edits, and content auto-sizing
    /// all obey the same limits.
    func clampedToPluginLimits(_ frame: CGRect, item: LayoutItem) -> CGRect {
        let meta = plugins.metadata(for: item.pluginID)
        guard meta.minWidth != nil || meta.maxWidth != nil
                || meta.minHeight != nil || meta.maxHeight != nil else { return frame }
        guard let screen = screens.controller(forDisplayUUID: item.displayUUID)?.screen.frame.size,
              screen.width > 0, screen.height > 0 else { return frame }

        let points = CGSize(width: frame.width * screen.width, height: frame.height * screen.height)
        let clamped = meta.clamp(points)
        guard clamped != points else { return frame }
        return CGRect(
            x: frame.minX, y: frame.minY,
            width: min(clamped.width / screen.width, 1),
            height: min(clamped.height / screen.height, 1)
        )
    }

    /// Resizes an item's frame to the content's natural size (keeping its
    /// top-left anchored, which is how the user perceives placement). The
    /// live view is resized directly and the model updated without a
    /// rebuild, so the manager's preview matches what's on the desktop.
    private func adoptContentSize(itemID: UUID, size: CGSize) {
        guard var item = store.layout.items.first(where: { $0.id == itemID }),
              let controller = screens.controller(forDisplayUUID: item.displayUUID) else { return }
        let screenFrame = controller.screen.frame
        guard screenFrame.width > 0, screenFrame.height > 0 else { return }

        let meta = plugins.metadata(for: item.pluginID)
        // Only axes the plugin declares as content-driven follow the content;
        // otherwise the user's size is kept (else a resize would snap back).
        guard meta.autoSizeWidth || meta.autoSizeHeight else { return }
        let limited = meta.clamp(size)
        let current = item.normalizedFrame
        let normalized = CGSize(
            width: meta.autoSizeWidth ? min(limited.width / screenFrame.width, 1) : current.width,
            height: meta.autoSizeHeight ? min(limited.height / screenFrame.height, 1) : current.height
        )
        guard abs(normalized.width - current.width) > 0.001
                || abs(normalized.height - current.height) > 0.001 else { return }

        // Frames are stored bottom-left (CoreGraphics), but an item is placed
        // by its top-left corner: a plugin whose height follows its content
        // must grow downward, or the header the user aligned drifts up the
        // screen every time the content changes.
        let top = current.minY + current.height
        item.normalizedFrame = CGRect(
            x: current.minX,
            y: max(top - normalized.height, 0),
            width: normalized.width,
            height: normalized.height
        )
        suppressRebuild = true
        store.update(item)
        suppressRebuild = false

        // The live view uses the clamped size, not the raw content size.
        let size = CGSize(
            width: item.normalizedFrame.width * screenFrame.width,
            height: item.normalizedFrame.height * screenFrame.height
        )
        guard let runningItem = running[itemID] else { return }
        if let panel = runningItem.panel {
            // Floating: the content view fills the panel, so resize the panel
            // (screen coordinates) and leave the hosting view autoresizing.
            panel.setFrame(CGRect(
                x: item.normalizedFrame.minX * screenFrame.width + screenFrame.minX,
                y: item.normalizedFrame.minY * screenFrame.height + screenFrame.minY,
                width: size.width,
                height: size.height
            ))
        } else if case .declarative(let host) = runningItem.runtime {
            host.hostingView.frame = CGRect(
                x: item.normalizedFrame.minX * screenFrame.width,
                y: item.normalizedFrame.minY * screenFrame.height,
                width: size.width,
                height: size.height
            )
        }
    }

    /// The value a property currently has for an item: its saved override,
    /// else the running instance's (declared) value.
    func effectiveProperty(itemID: UUID, name: String) -> PropertyValue? {
        if let override = store.layout.items.first(where: { $0.id == itemID })?.propertyOverrides[name] {
            return override
        }
        return running[itemID]?.instance.property(named: name)
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
                return String(localized: "offline — display not connected")
            }
            if plugins.descriptor(for: item.pluginID) == nil {
                return String(localized: "plugin \"\(item.pluginID)\" not found")
            }
            return item.isEnabled ? String(localized: "failed to start (see log)") : nil
        }
        if runningItem.instance.isErrored {
            // A JS exception message is the engine's own text, left as-is.
            return runningItem.instance.errorMessage ?? String(localized: "plugin threw an exception")
        }
        return nil
    }
}
