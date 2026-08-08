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

    private var watcher: DispatchSourceFileSystemObject?
    private var propertiesCache: [String: [PluginProperty]] = [:]
    private var permissionsCache: [String: Set<String>] = [:]
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "plugins")

    static let directoryURL = LayoutStore.directoryURL.appendingPathComponent("Plugins", isDirectory: true)

    func bootstrap() {
        try? FileManager.default.createDirectory(at: Self.directoryURL, withIntermediateDirectories: true)
        SamplePlugins.installIfMissing(into: Self.directoryURL)
        rescan()
        watch()
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
