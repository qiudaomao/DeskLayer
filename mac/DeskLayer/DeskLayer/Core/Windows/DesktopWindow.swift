//
//  DesktopWindow.swift
//  DeskLayer
//
//  Borderless window pinned at the desktop level: above the wallpaper image,
//  below desktop icons. Stationary across Spaces and Mission Control.
//

import AppKit

final class DesktopWindow: NSWindow {
    init(screen: NSScreen) {
        super.init(
            contentRect: screen.frame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false
        )
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenNone, .ignoresCycle]
        ignoresMouseEvents = true
        hasShadow = false
        isOpaque = false
        backgroundColor = .clear
        isReleasedWhenClosed = false
        isExcludedFromWindowsMenu = true
        animationBehavior = .none
        displaysWhenScreenProfileChanges = true

        let host = NSView(frame: screen.frame)
        host.wantsLayer = true
        contentView = host
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}
