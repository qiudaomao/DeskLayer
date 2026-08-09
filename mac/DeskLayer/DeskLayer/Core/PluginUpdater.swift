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
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: config)
    }()

    /// Never served from cache: raw.githubusercontent.com sends
    /// Cache-Control: max-age=300, so a user who edits a plugin and checks for
    /// an update would keep seeing the old one for five minutes.
    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        return request
    }


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

    /// Update check. `installedSource` is the current file (to read its
    /// version and updateURL); `destination` is where a newer version writes.
    ///
    /// Efficient path: fetch a small sibling manifest — same path/name with a
    /// `.json` extension (Clock.js → Clock.json) — holding {version, url}, and
    /// download the plugin body only when it's actually newer. If no manifest
    /// exists, fall back to fetching the `.js` at updateURL directly.
    func check(
        pluginID: String,
        installedSource: String,
        destination: URL
    ) async -> UpdateResult {
        let localMeta = PluginMetadata.extract(from: installedSource)
        guard let urlString = localMeta.updateURL, let updateURL = URL(string: urlString), updateURL.scheme != nil else {
            return .noUpdateURL
        }
        let localVersion = localMeta.version ?? "0"

        // 1) Try the manifest (small JSON) first.
        if let manifest = await fetchManifest(for: updateURL) {
            guard compareVersions(manifest.version, localVersion) == .orderedDescending else {
                return .upToDate(version: localVersion)
            }
            let bodyURL = manifest.url.flatMap { URL(string: $0) } ?? bodyURL(for: updateURL)
            return await download(bodyURL, into: destination, from: localVersion, to: manifest.version, pluginID: pluginID)
        }

        // 2) Fallback: fetch the .js directly and read its declared version.
        do {
            let (data, response) = try await session.data(for: request(updateURL))
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                return .failed("HTTP \(http.statusCode)")
            }
            guard let remoteSource = String(data: data, encoding: .utf8), !remoteSource.isEmpty else {
                return .failed("response was not text")
            }
            let remoteVersion = PluginMetadata.extract(from: remoteSource).version ?? "0"
            guard compareVersions(remoteVersion, localVersion) == .orderedDescending else {
                return .upToDate(version: localVersion)
            }
            try data.write(to: destination, options: .atomic)
            log.info("updated \(pluginID, privacy: .public) \(localVersion, privacy: .public) → \(remoteVersion, privacy: .public)")
            return .updated(from: localVersion, to: remoteVersion)
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    private struct Manifest: Decodable {
        let version: String
        let url: String?
    }

    /// The manifest URL is the updateURL with its extension swapped to .json
    /// (or updateURL itself when it already points at a .json).
    private func manifestURL(for updateURL: URL) -> URL {
        if updateURL.pathExtension.lowercased() == "json" { return updateURL }
        return updateURL.deletingPathExtension().appendingPathExtension("json")
    }

    /// Where to download the plugin body when a manifest omits an explicit url:
    /// the updateURL itself, unless it's the .json manifest, in which case .js.
    private func bodyURL(for updateURL: URL) -> URL {
        if updateURL.pathExtension.lowercased() == "json" {
            return updateURL.deletingPathExtension().appendingPathExtension("js")
        }
        return updateURL
    }

    private func fetchManifest(for updateURL: URL) async -> Manifest? {
        let url = manifestURL(for: updateURL)
        guard let (data, response) = try? await session.data(for: request(url)) else { return nil }
        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) { return nil }
        return try? JSONDecoder().decode(Manifest.self, from: data)
    }

    private func download(
        _ url: URL, into destination: URL, from localVersion: String, to remoteVersion: String, pluginID: String
    ) async -> UpdateResult {
        do {
            let (data, response) = try await session.data(for: request(url))
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                return .failed("HTTP \(http.statusCode) fetching plugin body")
            }
            guard let source = String(data: data, encoding: .utf8), !source.isEmpty else {
                return .failed("plugin body was not text")
            }
            try data.write(to: destination, options: .atomic)
            log.info("updated \(pluginID, privacy: .public) \(localVersion, privacy: .public) → \(remoteVersion, privacy: .public) (manifest)")
            return .updated(from: localVersion, to: remoteVersion)
        } catch {
            return .failed(error.localizedDescription)
        }
    }
}
