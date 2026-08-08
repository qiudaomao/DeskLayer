//
//  PowerStateController.swift
//  DeskLayer
//
//  Merges system power signals into one RenderPolicy consumed by every
//  FrameScheduler. Per-window occlusion is handled separately in
//  DesktopWindowController (it is per-screen, not global).
//

import AppKit
import Combine
import Foundation
import os

enum RenderPolicy: Equatable {
    case run
    case throttled(maxFps: Double)
    case paused
}

@MainActor
final class PowerStateController: ObservableObject {
    @Published private(set) var policy: RenderPolicy = .run

    private var isAsleep = false
    private var isLocked = false
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "power")

    func start() {
        let workspace = NSWorkspace.shared.notificationCenter
        workspace.addObserver(self, selector: #selector(pauseSignal), name: NSWorkspace.screensDidSleepNotification, object: nil)
        workspace.addObserver(self, selector: #selector(resumeSignal), name: NSWorkspace.screensDidWakeNotification, object: nil)
        workspace.addObserver(self, selector: #selector(pauseSignal), name: NSWorkspace.willSleepNotification, object: nil)
        workspace.addObserver(self, selector: #selector(resumeSignal), name: NSWorkspace.didWakeNotification, object: nil)

        let distributed = DistributedNotificationCenter.default()
        distributed.addObserver(self, selector: #selector(lockSignal), name: Notification.Name("com.apple.screenIsLocked"), object: nil)
        distributed.addObserver(self, selector: #selector(unlockSignal), name: Notification.Name("com.apple.screenIsUnlocked"), object: nil)

        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(recompute), name: .NSProcessInfoPowerStateDidChange, object: nil)
        center.addObserver(self, selector: #selector(recompute), name: ProcessInfo.thermalStateDidChangeNotification, object: nil)

        recompute()
    }

    @objc private func pauseSignal() { isAsleep = true; recompute() }
    @objc private func resumeSignal() { isAsleep = false; recompute() }
    @objc private func lockSignal() { isLocked = true; recompute() }
    @objc private func unlockSignal() { isLocked = false; recompute() }

    @objc private func recompute() {
        // Notifications can arrive on arbitrary threads; state lives on main.
        DispatchQueue.main.async { [self] in
            let new: RenderPolicy
            if isAsleep || isLocked || ProcessInfo.processInfo.thermalState == .critical {
                new = .paused
            } else if ProcessInfo.processInfo.thermalState == .serious {
                new = .throttled(maxFps: 15)
            } else if ProcessInfo.processInfo.isLowPowerModeEnabled {
                new = .throttled(maxFps: 10)
            } else {
                new = .run
            }
            if new != policy {
                policy = new
                log.info("render policy → \(String(describing: new), privacy: .public)")
            }
        }
    }
}
