//
//  WidgetPublisher.swift
//  DeskLayer
//
//  Publishes running items into the App Group container for the widget
//  extension: declarative trees as JSON, canvas frames as PNG. Writes are
//  throttled per item, and WidgetCenter reloads are rate-limited globally
//  (widget refreshes are budget-limited by the system).
//

import CoreGraphics
import DeskLayerKit
import Foundation
import ImageIO
import WidgetKit

@MainActor
final class WidgetPublisher {
    private var lastWrite: [UUID: Date] = [:]
    private var lastReload = Date.distantPast
    private let writeInterval: TimeInterval = 10
    private let reloadInterval: TimeInterval = 60

    func publishDeclarative(itemID: UUID, pluginID: String, treeJSON: String) {
        guard shouldWrite(itemID) else { return }
        WidgetPayloadStore.write(WidgetPayload(
            itemID: itemID.uuidString,
            pluginID: pluginID,
            kind: .declarative,
            treeJSON: treeJSON
        ))
        requestReload()
    }

    func publishCanvas(itemID: UUID, pluginID: String, image: CGImage) {
        guard shouldWrite(itemID) else { return }
        guard let url = WidgetPayloadStore.imageURL(for: itemID.uuidString),
              let directory = WidgetPayloadStore.directory else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else { return }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { return }
        WidgetPayloadStore.write(WidgetPayload(
            itemID: itemID.uuidString,
            pluginID: pluginID,
            kind: .canvas
        ))
        requestReload()
    }

    /// Drop payloads for items that no longer run (called on rebuild).
    func prune(currentItemIDs: [UUID]) {
        lastWrite = lastWrite.filter { currentItemIDs.contains($0.key) }
        WidgetPayloadStore.keepOnly(itemIDs: Set(currentItemIDs.map(\.uuidString)))
    }

    private func shouldWrite(_ itemID: UUID) -> Bool {
        let now = Date()
        if let last = lastWrite[itemID], now.timeIntervalSince(last) < writeInterval {
            return false
        }
        lastWrite[itemID] = now
        if ProcessInfo.processInfo.environment["DESKLAYER_SNAPSHOT"] == "1" {
            let where_ = WidgetPayloadStore.directory?.path ?? "NO GROUP CONTAINER"
            FileHandle.standardError.write(Data("[widget] publishing \(itemID) → \(where_)\n".utf8))
        }
        return true
    }

    private func requestReload() {
        let now = Date()
        guard now.timeIntervalSince(lastReload) > reloadInterval else { return }
        lastReload = now
        WidgetCenter.shared.reloadTimelines(ofKind: "DeskLayerItemWidget")
    }
}
