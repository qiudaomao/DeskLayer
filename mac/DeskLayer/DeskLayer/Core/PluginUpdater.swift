//
//  PluginUpdater.swift
//  DeskLayer
//
//  Checks a plugin's updateURL for a newer version and installs it by
//  overwriting the plugin file. Per-plugin auto-update is a user preference;
//  when on, DeskLayer checks at launch (and on demand).
//

import Foundation
import os

nonisolated enum UpdateResult: Sendable, Equatable {
    case upToDate(version: String)
    case updated(from: String, to: String)
    case noUpdateURL
    case failed(String)

    var message: String {
        switch self {
        case .upToDate(let v): return "Up to date (\(v))"
        case .updated(let from, let to): return "Updated \(from) → \(to)"
        case .noUpdateURL: return "No update URL declared"
        case .failed(let why): return "Update failed: \(why)"
        }
    }
}

@MainActor
final class PluginUpdater {
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "updater")
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        return URLSession(configuration: config)
    }()

    // Per-plugin "auto-update" preference, persisted in UserDefaults.
    private static let autoKey = "DeskLayer.autoUpdatePlugins"

    func isAutoUpdate(_ pluginID: String) -> Bool {
        autoSet().contains(pluginID)
    }

    func setAutoUpdate(_ on: Bool, for pluginID: String) {
        var set = autoSet()
        if on { set.insert(pluginID) } else { set.remove(pluginID) }
        UserDefaults.standard.set(Array(set), forKey: Self.autoKey)
    }

    private func autoSet() -> Set<String> {
        Set(UserDefaults.standard.stringArray(forKey: Self.autoKey) ?? [])
    }

    /// Fetch updateURL, compare versions, install if newer. `installedSource`
    /// is the current file contents (to read its version); `destination` is
    /// where a newer version is written.
    func check(
        pluginID: String,
        installedSource: String,
        destination: URL
    ) async -> UpdateResult {
        let localMeta = PluginMetadata.extract(from: installedSource)
        guard let urlString = localMeta.updateURL, let url = URL(string: urlString), url.scheme != nil else {
            return .noUpdateURL
        }
        let localVersion = localMeta.version ?? "0"

        do {
            let (data, response) = try await session.data(from: url)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                return .failed("HTTP \(http.statusCode)")
            }
            guard let remoteSource = String(data: data, encoding: .utf8) else {
                return .failed("response was not text")
            }
            let remoteMeta = PluginMetadata.extract(from: remoteSource)
            guard remoteMeta.version != nil || !remoteSource.isEmpty else {
                return .failed("remote has no plugin.export")
            }
            let remoteVersion = remoteMeta.version ?? "0"

            guard compareVersions(remoteVersion, localVersion) == .orderedDescending else {
                return .upToDate(version: localVersion)
            }
            // Install: overwrite atomically. The folder watcher rescans; the
            // coordinator re-spawns affected items.
            try data.write(to: destination, options: .atomic)
            log.info("updated \(pluginID, privacy: .public) \(localVersion, privacy: .public) → \(remoteVersion, privacy: .public)")
            return .updated(from: localVersion, to: remoteVersion)
        } catch {
            return .failed(error.localizedDescription)
        }
    }
}
