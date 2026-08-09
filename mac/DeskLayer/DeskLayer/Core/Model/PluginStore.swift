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
    /// Alternate download addresses, tried in order when `url` fails —
    /// GitHub is unreachable from some networks, so a store should be able
    /// to name a CDN or a mirror host.
    var mirrors: [String]?
    var version: String?
    var author: String?

    var id: String { name }

    /// Every address to try, primary first.
    var candidateURLs: [String] { [url] + (mirrors ?? []) }
}

nonisolated struct StoreCatalog: Codable, Hashable {
    var name: String
    /// The catalog's canonical address, if it wants to state one (a store
    /// reachable through several URLs can name the one to trust).
    var url: String?
    /// Human-facing home page, opened from the store's inspector pane.
    var website: String?
    /// Alternate catalog addresses. Fetched catalogs carry these forward, so
    /// a store only has to be reachable once for its mirrors to be learned.
    var mirrors: [String]?
    var plugins: [StorePlugin]
}

/// A store the user added: its source URL plus the last catalog we fetched.
nonisolated struct PluginStoreEntry: Codable, Hashable, Identifiable {
    var url: String
    var catalog: StoreCatalog?
    var lastError: String?
    /// When the catalog was last fetched; drives the cache window below.
    var fetchedAt: Date?
    /// Fallback catalog addresses: seeded from the preset (or the URL the
    /// user added) and refreshed from each fetched catalog.
    var mirrors: [String] = []
    /// The address that last worked — tried first next time, so a user behind
    /// a network that blocks the primary host stops paying for the timeout.
    var lastGoodURL: String?

    var id: String { url }

    /// Every catalog address to try, best-known first and without repeats.
    var candidateURLs: [String] {
        var seen = Set<String>()
        return ([lastGoodURL, url].compactMap { $0 } + mirrors + (catalog?.mirrors ?? []))
            .filter { seen.insert($0).inserted }
    }
    /// Falls back to the host name until the catalog is fetched.
    var displayName: String {
        catalog?.name ?? URL(string: url)?.host ?? url
    }

    /// Catalogs are cached for a day: launching the app shouldn't hit every
    /// store's server, but a store that changes is picked up without the user
    /// having to think about it. The Refresh button ignores this.
    static let cacheLifetime: TimeInterval = 24 * 60 * 60

    func isFresh(now: Date = Date()) -> Bool {
        guard catalog != nil, let fetchedAt else { return false }
        let age = now.timeIntervalSince(fetchedAt)
        return age >= 0 && age < Self.cacheLifetime
    }
}

/// Stores the app suggests in the Add menu, so the common case is one click
/// instead of pasting a URL.
nonisolated struct PresetStore: Identifiable, Hashable {
    var name: String
    var url: String
    /// Mirrors seeded at add time, so the very first fetch already has a
    /// fallback — a catalog's own `mirrors` are only known once one succeeds.
    var mirrors: [String] = []
    var id: String { url }

    private static let raw =
        "https://raw.githubusercontent.com/qiudaomao/DeskLayerPluginStore/main"
    /// jsDelivr serves the same repository and is reachable from networks that
    /// can't reach raw.githubusercontent.com.
    private static let cdn =
        "https://cdn.jsdelivr.net/gh/qiudaomao/DeskLayerPluginStore@main"

    static let all: [PresetStore] = [
        PresetStore(name: "Official Store",
                    url: "\(raw)/official/catalog.json",
                    mirrors: ["\(cdn)/official/catalog.json"]),
        PresetStore(name: "Sample Store",
                    url: "\(raw)/samples/catalog.json",
                    mirrors: ["\(cdn)/samples/catalog.json"]),
    ]
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
        // Cached catalogs are shown immediately; only stale ones are re-fetched.
        Task { await refreshAll(force: false) }
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
    func addStore(urlString: String, mirrors: [String] = []) async -> Bool {
        let trimmed = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), url.scheme != nil else { return false }
        guard !stores.contains(where: { $0.url == trimmed }) else { return true }
        let entry = await fetched(PluginStoreEntry(url: trimmed, mirrors: mirrors))
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

    /// `force` is the Refresh button; the launch path passes false so a
    /// catalog fetched within the cache window is left alone.
    func refreshAll(force: Bool = true) async {
        isRefreshing = true
        for index in stores.indices where index < stores.count {
            guard force || !stores[index].isFresh() else { continue }
            stores[index] = await fetched(stores[index])
        }
        isRefreshing = false
        save()
    }

    /// Refresh one store, ignoring the cache.
    func refresh(_ id: String) async {
        guard let index = stores.firstIndex(where: { $0.id == id }) else { return }
        isRefreshing = true
        stores[index] = await fetched(stores[index])
        isRefreshing = false
        save()
    }

    /// Fetches a catalog, returning an updated copy (not inout: the awaits
    /// inside can't capture an inout parameter).
    private func fetched(_ entry: PluginStoreEntry) async -> PluginStoreEntry {
        var entry = entry
        var failures: [String] = []
        for candidate in entry.candidateURLs {
            guard let url = URL(string: candidate) else { continue }
            do {
                let (data, response) = try await session.data(from: url)
                if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                    failures.append("\(candidate): HTTP \(http.statusCode)")
                    continue
                }
                let catalog = try JSONDecoder().decode(StoreCatalog.self, from: data)
                entry.catalog = catalog
                // Learn the catalog's own mirrors for next time.
                if let mirrors = catalog.mirrors { entry.mirrors = mirrors }
                entry.lastGoodURL = candidate
                entry.lastError = nil
                entry.fetchedAt = Date()
                return entry
            } catch {
                failures.append("\(candidate): \(error.localizedDescription)")
            }
        }
        // Every address failed. Keep the cached catalog — a store being
        // unreachable shouldn't empty its category.
        entry.lastError = failures.last.map { _ in
            "Couldn't reach the store (tried \(failures.count) address\(failures.count == 1 ? "" : "es"))."
        } ?? "No usable catalog URL."
        log.error("store \(entry.url, privacy: .public): \(failures.joined(separator: " | "), privacy: .public)")
        return entry
    }

    // MARK: - Install

    /// Downloads a store plugin into the plugins folder. The plugin then
    /// appears in the library under that store's category.
    @discardableResult
    func install(_ plugin: StorePlugin, from storeName: String, into directory: URL) async -> String? {
        var lastError = "invalid plugin URL"
        // Mirrors again: the catalog may come from a CDN while the plugin
        // itself still points at the origin, or vice versa.
        for candidate in plugin.candidateURLs {
            guard let url = URL(string: candidate), url.scheme != nil else { continue }
            do {
                let (data, response) = try await session.data(from: url)
                if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                    lastError = "HTTP \(http.statusCode)"
                    continue
                }
                guard let source = String(data: data, encoding: .utf8), !source.isEmpty else {
                    lastError = "plugin body was not text"
                    continue
                }
                // Reject anything that isn't actually a plugin before writing it.
                guard PluginMetadata.extract(from: source).isEmpty == false
                        || source.contains("plugin.export") else {
                    lastError = "that file doesn't define plugin.export"
                    continue
                }
                let safeName = plugin.name.replacingOccurrences(of: "/", with: "-")
                let destination = directory.appendingPathComponent("\(safeName).js")
                try data.write(to: destination, options: .atomic)
                Self.recordOrigin(pluginID: safeName, storeName: storeName)
                log.info("installed \(safeName, privacy: .public) from \(storeName, privacy: .public)")
                return nil
            } catch {
                lastError = error.localizedDescription
            }
        }
        return lastError
    }
}
