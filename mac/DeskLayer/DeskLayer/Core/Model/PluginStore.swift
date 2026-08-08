//
//  PluginStore.swift
//  DeskLayer
//
//  A plugin store is a JSON catalog at a URL listing plugins available to
//  install. Each added store becomes its own category in the library.
//
//  Catalog format:
//    {
//      "name": "Acme Widgets",
//      "plugins": [
//        { "name": "Clock",
//          "description": "A tasteful clock.",
//          "preview": "https://acme.example/clock.png",   // optional
//          "url": "https://acme.example/Clock.js",
//          "version": "1.2.0",                            // optional
//          "author": "Acme" }                             // optional
//      ]
//    }
//

import Combine
import Foundation
import os

nonisolated struct StorePlugin: Codable, Hashable, Identifiable {
    var name: String
    var description: String?
    /// Preview image URL, shown in the plugin's detail pane.
    var preview: String?
    /// Where the plugin's .js lives.
    var url: String
    var version: String?
    var author: String?

    var id: String { name }
}

nonisolated struct StoreCatalog: Codable, Hashable {
    var name: String
    var plugins: [StorePlugin]
}

/// A store the user added: its source URL plus the last catalog we fetched.
nonisolated struct PluginStoreEntry: Codable, Hashable, Identifiable {
    var url: String
    var catalog: StoreCatalog?
    var lastError: String?

    var id: String { url }
    /// Falls back to the host name until the catalog is fetched.
    var displayName: String {
        catalog?.name ?? URL(string: url)?.host ?? url
    }
}

@MainActor
final class PluginStoreRegistry: ObservableObject {
    @Published private(set) var stores: [PluginStoreEntry] = []
    @Published private(set) var isRefreshing = false

    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "stores")
    private static let storesKey = "DeskLayer.pluginStores"
    /// pluginID → store display name, so installed plugins stay grouped
    /// under the store they came from.
    private static let originsKey = "DeskLayer.pluginStoreOrigins"

    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        return URLSession(configuration: config)
    }()

    // MARK: - Persistence

    func load() {
        guard let data = UserDefaults.standard.data(forKey: Self.storesKey),
              let saved = try? JSONDecoder().decode([PluginStoreEntry].self, from: data) else { return }
        stores = saved
        Task { await refreshAll() }
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(stores) else { return }
        UserDefaults.standard.set(data, forKey: Self.storesKey)
    }

    /// Which store a plugin was installed from, if any. Reads UserDefaults
    /// directly so the plugin scan (off the main actor) can call it.
    nonisolated static func storeName(forPlugin id: String) -> String? {
        (UserDefaults.standard.dictionary(forKey: "DeskLayer.pluginStoreOrigins") as? [String: String])?[id]
    }

    private static func recordOrigin(pluginID: String, storeName: String) {
        var map = (UserDefaults.standard.dictionary(forKey: originsKey) as? [String: String]) ?? [:]
        map[pluginID] = storeName
        UserDefaults.standard.set(map, forKey: originsKey)
    }

    // MARK: - Stores

    @discardableResult
    func addStore(urlString: String) async -> Bool {
        let trimmed = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), url.scheme != nil else { return false }
        guard !stores.contains(where: { $0.url == trimmed }) else { return true }
        let entry = await fetched(PluginStoreEntry(url: trimmed))
        // Only keep a store whose catalog actually parsed.
        guard entry.catalog != nil else { return false }
        stores.append(entry)
        save()
        return true
    }

    func removeStore(_ id: String) {
        stores.removeAll { $0.id == id }
        save()
    }

    func refreshAll() async {
        isRefreshing = true
        for index in stores.indices where index < stores.count {
            stores[index] = await fetched(stores[index])
        }
        isRefreshing = false
        save()
    }

    /// Fetches a catalog, returning an updated copy (not inout: the awaits
    /// inside can't capture an inout parameter).
    private func fetched(_ entry: PluginStoreEntry) async -> PluginStoreEntry {
        var entry = entry
        guard let url = URL(string: entry.url) else { return entry }
        do {
            let (data, response) = try await session.data(from: url)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                entry.lastError = "HTTP \(http.statusCode)"
                return entry
            }
            entry.catalog = try JSONDecoder().decode(StoreCatalog.self, from: data)
            entry.lastError = nil
        } catch {
            entry.lastError = error.localizedDescription
            let url = entry.url
            log.error("store \(url, privacy: .public): \(error.localizedDescription, privacy: .public)")
        }
        return entry
    }

    // MARK: - Install

    /// Downloads a store plugin into the plugins folder. The plugin then
    /// appears in the library under that store's category.
    @discardableResult
    func install(_ plugin: StorePlugin, from storeName: String, into directory: URL) async -> String? {
        guard let url = URL(string: plugin.url), url.scheme != nil else { return "invalid plugin URL" }
        do {
            let (data, response) = try await session.data(from: url)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                return "HTTP \(http.statusCode)"
            }
            guard let source = String(data: data, encoding: .utf8), !source.isEmpty else {
                return "plugin body was not text"
            }
            // Reject anything that isn't actually a plugin before writing it.
            guard PluginMetadata.extract(from: source).isEmpty == false
                    || source.contains("plugin.export") else {
                return "that file doesn't define plugin.export"
            }
            let safeName = plugin.name.replacingOccurrences(of: "/", with: "-")
            let destination = directory.appendingPathComponent("\(safeName).js")
            try data.write(to: destination, options: .atomic)
            Self.recordOrigin(pluginID: safeName, storeName: storeName)
            log.info("installed \(safeName, privacy: .public) from \(storeName, privacy: .public)")
            return nil
        } catch {
            return error.localizedDescription
        }
    }
}
