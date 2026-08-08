//
//  FrameScheduler.swift
//  DeskLayer
//
//  One per screen. Drives all of that screen's items from one CADisplayLink
//  (NSWindow.displayLink, macOS 14+), honoring each item's fps: a 30fps item
//  fires every other tick of a 60Hz link. The link pauses entirely when the
//  screen has no items, is occluded, or policy says pause — a stopped link
//  is the real power win.
//

import AppKit
import QuartzCore
import os

@MainActor
final class ScheduledItem {
    let id: UUID
    let renderer: ItemRenderer
    let layer: CALayer
    let interval: CFTimeInterval
    var nextDue: CFTimeInterval = 0
    var isRenderInFlight = false
    /// Set by the coordinator to feed the manager's virtual desktop; called
    /// on main with a throttled copy of the latest frame.
    var onThumbnail: ((CGImage) -> Void)?
    var lastThumbnailTime: CFTimeInterval = 0
    /// Wallpaper items pause when the desktop is covered; floating panels
    /// stay visible (fullScreenAuxiliary) so they keep rendering.
    var pausesWhenDesktopOccluded = true
    /// Watchdog bookkeeping: when the in-flight render started.
    var renderStartedAt: CFTimeInterval = 0

    init(id: UUID, renderer: ItemRenderer, layer: CALayer) {
        self.id = id
        self.renderer = renderer
        self.layer = layer
        self.interval = 1.0 / renderer.fps
    }
}

@MainActor
final class FrameScheduler: NSObject {
    private var displayLink: CADisplayLink?
    private var items: [ScheduledItem] = []
    private weak var window: NSWindow?

    /// Occlusion pause (per screen) — set by DesktopWindowController.
    var isOccluded = false { didSet { updateLinkState() } }
    /// Global power policy — pushed by the coordinator.
    var policy: RenderPolicy = .run { didSet { updateLinkState() } }

    private var lastFireTimes: [UUID: CFTimeInterval] = [:]

    func attach(to window: NSWindow) {
        self.window = window
        rebuildLink()
    }

    func add(_ item: ScheduledItem) {
        items.append(item)
        rebuildLink()
    }

    func remove(id: UUID) {
        items.removeAll { $0.id == id }
        lastFireTimes.removeValue(forKey: id)
        rebuildLink()
    }

    func removeAll() {
        items.removeAll()
        lastFireTimes.removeAll()
        rebuildLink()
    }

    /// Recreate the link (after wake, display changes) or retune fps range.
    func rebuildLink() {
        displayLink?.invalidate()
        displayLink = nil
        guard let window, !items.isEmpty else { return }
        let link = window.displayLink(target: self, selector: #selector(tick(_:)))
        let maxFps = Float(items.map(\.renderer.fps).max() ?? 60)
        link.preferredFrameRateRange = CAFrameRateRange(minimum: 15, maximum: maxFps, preferred: maxFps)
        link.add(to: .main, forMode: .common)
        displayLink = link
        updateLinkState()
    }

    private func updateLinkState() {
        let allPauseWithDesktop = items.allSatisfy(\.pausesWhenDesktopOccluded)
        displayLink?.isPaused = policy == .paused || items.isEmpty || (isOccluded && allPauseWithDesktop)
    }

    /// A render stuck longer than this is declared wedged: the item is
    /// marked errored and unscheduled (its queue thread is abandoned —
    /// public JSC has no way to interrupt running JS).
    static let watchdogTimeout: CFTimeInterval = 2.0

    @objc private func tick(_ link: CADisplayLink) {
        let now = link.targetTimestamp
        let fpsCap: Double? = {
            if case .throttled(let maxFps) = policy { return maxFps }
            return nil
        }()

        // Watchdog: flag runaway plugins so isErrored filters them out below.
        for item in items where item.isRenderInFlight {
            if now - item.renderStartedAt > Self.watchdogTimeout, item.renderStartedAt > 0 {
                item.renderer.instance.flagWedged(after: now - item.renderStartedAt)
            }
        }

        for item in items where !item.isRenderInFlight && !item.renderer.isErrored {
            if isOccluded && item.pausesWhenDesktopOccluded { continue }
            if item.nextDue == 0 { item.nextDue = now }
            guard now >= item.nextDue else { continue }
            if let fpsCap, let last = lastFireTimes[item.id], now - last < 1.0 / fpsCap {
                continue
            }
            // Advance past missed frames instead of queueing catch-up work.
            item.nextDue += item.interval
            if item.nextDue < now { item.nextDue = now + item.interval }
            lastFireTimes[item.id] = now
            item.isRenderInFlight = true
            item.renderStartedAt = now

            let wantsThumbnail = item.onThumbnail != nil && now - item.lastThumbnailTime > 0.5
            if wantsThumbnail { item.lastThumbnailTime = now }

            let renderer = item.renderer
            renderer.queue.async {
                let surface = renderer.renderFrame()
                let thumbnail = wantsThumbnail ? renderer.makeThumbnailImage() : nil
                DispatchQueue.main.async {
                    CATransaction.begin()
                    CATransaction.setDisableActions(true)
                    if let surface {
                        item.layer.contents = surface
                    }
                    CATransaction.commit()
                    if let thumbnail {
                        item.onThumbnail?(thumbnail)
                    }
                    item.isRenderInFlight = false
                }
            }
        }
    }
}
