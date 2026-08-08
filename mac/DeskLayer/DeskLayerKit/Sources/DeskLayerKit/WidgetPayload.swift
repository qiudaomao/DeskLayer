//
//  WidgetPayload.swift
//  DeskLayerKit
//
//  The handoff format between the app and the widget extension, exchanged
//  through the App Group container. JS never runs in the widget process:
//  the app evaluates plugins and publishes either a serialized view tree
//  (declarative) or a rendered PNG snapshot (canvas); the widget just
//  displays it.
//

import Foundation

public struct WidgetPayload: Codable, Sendable {
    public enum Kind: String, Codable, Sendable {
        case declarative
        case canvas
    }

    public var itemID: String
    public var pluginID: String
    public var kind: Kind
    /// ViewNode JSON for declarative plugins.
    public var treeJSON: String?
    public var updatedAt: Date

    public init(itemID: String, pluginID: String, kind: Kind, treeJSON: String? = nil, updatedAt: Date = Date()) {
        self.itemID = itemID
        self.pluginID = pluginID
        self.kind = kind
        self.treeJSON = treeJSON
        self.updatedAt = updatedAt
    }
}

public enum WidgetPayloadStore {
    public static let appGroupID = "group.com.qiudaomao.DeskLayer"

    public static var directory: URL? {
        FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroupID)?
            .appendingPathComponent("WidgetPayloads", isDirectory: true)
    }

    public static func payloadURL(for itemID: String) -> URL? {
        directory?.appendingPathComponent("\(itemID).json")
    }

    /// Canvas snapshot PNG next to the payload.
    public static func imageURL(for itemID: String) -> URL? {
        directory?.appendingPathComponent("\(itemID).png")
    }

    public static func write(_ payload: WidgetPayload) {
        guard let directory, let url = payloadURL(for: payload.itemID) else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(payload) else { return }
        try? data.write(to: url, options: .atomic)
    }

    public static func readAll() -> [WidgetPayload] {
        guard let directory else { return [] }
        let files = (try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: nil
        )) ?? []
        let decoder = JSONDecoder()
        return files
            .filter { $0.pathExtension == "json" }
            .compactMap { url in
                (try? Data(contentsOf: url)).flatMap { try? decoder.decode(WidgetPayload.self, from: $0) }
            }
            .sorted { $0.pluginID < $1.pluginID }
    }

    /// Prunes payloads for items that no longer exist.
    public static func keepOnly(itemIDs: Set<String>) {
        guard let directory else { return }
        let files = (try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: nil
        )) ?? []
        for url in files {
            let stem = url.deletingPathExtension().lastPathComponent
            if !itemIDs.contains(stem) {
                try? FileManager.default.removeItem(at: url)
            }
        }
    }
}
