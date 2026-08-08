//
//  PluginRegistry.swift
//  DeskLayer
//
//  Scans the plugins folder for available plugins. A plugin is either a
//  bare `Name.js` file or a folder `Name.deskplugin/main.js` (folder form
//  future-proofs bundled assets). pluginID = file/folder basename.
//

import Combine
import Foundation
import os

nonisolated struct PluginDescriptor: Identifiable, Hashable {
    let id: String
    let sourceURL: URL
    /// Folder holding the plugin's assets (.deskplugin form); nil for bare .js.
    var assetsURL: URL?
}

@MainActor
final class PluginRegistry: ObservableObject {
    @Published private(set) var plugins: [PluginDescriptor] = []
    /// Fires after a rescan (folder edit, import, or applied update) so the
    /// coordinator can re-spawn running items with fresh code.
    let didChange = PassthroughSubject<Void, Never>()

    private var watcher: DispatchSourceFileSystemObject?
    private var propertiesCache: [String: [PluginProperty]] = [:]
    private var permissionsCache: [String: Set<String>] = [:]
    private var metadataCache: [String: PluginMetadata] = [:]
    private let updater = PluginUpdater()
    /// Last update-check result per plugin, surfaced in the inspector.
    @Published private(set) var updateStatus: [String: UpdateResult] = [:]
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "plugins")

    static let directoryURL = LayoutStore.directoryURL.appendingPathComponent("Plugins", isDirectory: true)

    func bootstrap() {
        try? FileManager.default.createDirectory(at: Self.directoryURL, withIntermediateDirectories: true)
        SamplePlugins.installIfMissing(into: Self.directoryURL)
        rescan()
        watch()
        Task { await self.autoUpdateAll() }
    }

    // MARK: - Metadata & updates

    /// version / author / description / updateURL, cached until the next scan.
    func metadata(for id: String) -> PluginMetadata {
        if let cached = metadataCache[id] { return cached }
        let meta = source(for: id).map(PluginMetadata.extract(from:)) ?? PluginMetadata()
        metadataCache[id] = meta
        return meta
    }

    func isAutoUpdate(_ id: String) -> Bool { updater.isAutoUpdate(id) }
    func setAutoUpdate(_ on: Bool, for id: String) { updater.setAutoUpdate(on, for: id) }

    /// Manual "Check for Update". Publishes the result to `updateStatus`.
    @discardableResult
    func checkForUpdate(_ id: String) async -> UpdateResult {
        guard let descriptor = descriptor(for: id), let source = source(for: id) else {
            let result = UpdateResult.failed("plugin not found")
            updateStatus[id] = result
            return result
        }
        // .deskplugin folders update their main.js; bare .js updates in place.
        let destination = descriptor.sourceURL
        let result = await updater.check(pluginID: id, installedSource: source, destination: destination)
        updateStatus[id] = result
        if case .updated = result { rescan() }
        return result
    }

    /// Launch-time pass: check every auto-update plugin that has an updateURL.
    func autoUpdateAll() async {
        for descriptor in plugins where updater.isAutoUpdate(descriptor.id) {
            let meta = metadata(for: descriptor.id)
            guard meta.updateURL != nil else { continue }
            _ = await checkForUpdate(descriptor.id)
        }
    }

    func descriptor(for id: String) -> PluginDescriptor? {
        plugins.first { $0.id == id }
    }

    func source(for id: String) -> String? {
        guard let descriptor = descriptor(for: id) else { return nil }
        return try? String(contentsOf: descriptor.sourceURL, encoding: .utf8)
    }

    /// Declared properties for the inspector, from a throwaway boot of the
    /// plugin (immediately invalidated so its timers/requests die). Cached
    /// until the folder rescans.
    func declaredProperties(for id: String) -> [PluginProperty] {
        if let cached = propertiesCache[id] { return cached }
        guard let source = source(for: id),
              let instance = PluginInstance(pluginID: id, source: source, overrides: [:])
        else { return [] }
        let properties = instance.properties
        instance.invalidate()
        propertiesCache[id] = properties
        return properties
    }

    /// Permissions the plugin declares (plugin.export.permissions), for the
    /// inspector to decide which host-config sections to show.
    func declaredPermissions(for id: String) -> Set<String> {
        if let cached = permissionsCache[id] { return cached }
        guard let source = source(for: id),
              let instance = PluginInstance(pluginID: id, source: source, overrides: [:])
        else { return [] }
        let result = instance.permissions
        instance.invalidate()
        permissionsCache[id] = result
        return result
    }

    func rescan() {
        propertiesCache.removeAll()
        permissionsCache.removeAll()
        metadataCache.removeAll()
        var found: [PluginDescriptor] = []
        let contents = (try? FileManager.default.contentsOfDirectory(
            at: Self.directoryURL, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]
        )) ?? []
        for url in contents {
            if url.pathExtension == "js" {
                found.append(PluginDescriptor(id: url.deletingPathExtension().lastPathComponent, sourceURL: url))
            } else if url.pathExtension == "deskplugin" {
                let main = url.appendingPathComponent("main.js")
                if FileManager.default.fileExists(atPath: main.path) {
                    found.append(PluginDescriptor(
                        id: url.deletingPathExtension().lastPathComponent,
                        sourceURL: main,
                        assetsURL: url
                    ))
                }
            }
        }
        plugins = found.sorted { $0.id < $1.id }
        log.info("plugins folder scan: \(self.plugins.map(\.id).joined(separator: ","), privacy: .public)")
        didChange.send()
    }

    private func watch() {
        let fd = open(Self.directoryURL.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .rename], queue: .main
        )
        source.setEventHandler { [weak self] in self?.rescan() }
        source.setCancelHandler { close(fd) }
        source.resume()
        watcher = source
    }
}
