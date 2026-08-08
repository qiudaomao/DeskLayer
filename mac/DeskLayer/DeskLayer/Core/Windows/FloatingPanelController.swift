//
//  FloatingPanelController.swift
//  DeskLayer
//
//  The floating render target: same item content as the wallpaper, hosted
//  in a borderless non-activating panel above other windows. Dragging the
//  panel writes the item's normalized frame back to the store.
//

import AppKit

@MainActor
final class FloatingPanelController: NSObject, NSWindowDelegate {
    let panel: NSPanel
    private let screenProvider: () -> NSScreen?
    /// Called after a user drag with the new normalized frame (bottom-left origin).
    var onMoved: ((CGRect) -> Void)?
    private var suppressMoveCallback = false

    init(frame: CGRect, screen: NSScreen) {
        panel = NSPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        screenProvider = { [weak panel] in panel?.screen }
        super.init()

        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.isExcludedFromWindowsMenu = true
        panel.animationBehavior = .none
        panel.delegate = self

        let host = NSView(frame: CGRect(origin: .zero, size: frame.size))
        host.wantsLayer = true
        panel.contentView = host
    }

    /// Click-through: mouse events pass to whatever is beneath the panel
    /// (it can then only be moved from the manager's canvas/inspector).
    var isClickThrough: Bool {
        get { panel.ignoresMouseEvents }
        set {
            panel.ignoresMouseEvents = newValue
            panel.isMovableByWindowBackground = !newValue
        }
    }

    func show() {
        panel.orderFrontRegardless()
    }

    func tearDown() {
        panel.delegate = nil
        panel.orderOut(nil)
    }

    /// Programmatic frame updates (inspector edits) — no writeback echo.
    func setFrame(_ frame: CGRect) {
        suppressMoveCallback = true
        panel.setFrame(frame, display: true)
        suppressMoveCallback = false
    }

    func windowDidMove(_ notification: Notification) {
        guard !suppressMoveCallback, let onMoved else { return }
        guard let screen = screenProvider() ?? NSScreen.main else { return }
        let screenFrame = screen.frame
        guard screenFrame.width > 0, screenFrame.height > 0 else { return }
        let f = panel.frame
        onMoved(CGRect(
            x: (f.minX - screenFrame.minX) / screenFrame.width,
            y: (f.minY - screenFrame.minY) / screenFrame.height,
            width: f.width / screenFrame.width,
            height: f.height / screenFrame.height
        ))
    }
}
