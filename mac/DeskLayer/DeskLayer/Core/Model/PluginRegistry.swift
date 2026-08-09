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

/// Where a plugin came from — drives grouping in the library and whether it
/// can be uninstalled.
nonisolated enum PluginOrigin: Hashable {
    /// Anything in the plugins folder that no store claims.
    case user
    /// Installed from a plugin store, grouped under its name.
    case store(String)

    var title: String {
        switch self {
        // Localized here: the title crosses a String parameter on its way to
        // the sidebar, so a bare literal would render untranslated. A store's
        // own name is whatever its catalog says and stays verbatim.
        case .user: return String(localized: "Installed")
        case .store(let name): return name
        }
    }

    /// Nothing ships with the app any more, so every plugin can be removed.
    var isRemovable: Bool { true }

    /// The local categories, in sidebar order (stores are appended after).
    static let localCases: [PluginOrigin] = [.user]
}

nonisolated struct PluginDescriptor: Identifiable, Hashable {
    let id: String
    let sourceURL: URL
    /// Folder holding the plugin's assets (.deskplugin form); nil for bare .js.
    var assetsURL: URL?
    var origin: PluginOrigin = .user
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

    /// A plugin belongs to the store it was installed from, if any.
    private static func origin(of name: String) -> PluginOrigin {
        if let store = PluginStoreRegistry.storeName(forPlugin: name) { return .store(store) }
        return .user
    }

    func bootstrap() {
        try? FileManager.default.createDirectory(at: Self.directoryURL, withIntermediateDirectories: true)
        rescan()
        watch()
        Task { await self.autoUpdateAll() }
    }

    /// Deletes a plugin's file (or .deskplugin folder). Plugins all come from
    /// stores or the user now, so nothing refuses to be removed.
    @discardableResult
    func uninstall(_ id: String) -> Bool {
        guard let descriptor = descriptor(for: id), descriptor.origin.isRemovable else { return false }
        let target = descriptor.assetsURL ?? descriptor.sourceURL
        do {
            try FileManager.default.trashItem(at: target, resultingItemURL: nil)
        } catch {
            log.error("uninstall \(id, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            return false
        }
        rescan()
        return true
    }

    /// What a rename did, or why it didn't. A value rather than a throw, the
    /// same shape `UpdateResult` uses.
    nonisolated enum RenameOutcome: Equatable {
        case renamed(String)
        /// The new name is the old one — nothing to do, and not an error.
        case unchanged
        case notFound
        /// Store plugins keep their catalog name: an update looks the plugin
        /// up by name, and a renamed copy would be installed alongside it.
        case fromStore(String)
        case invalidName
        case nameTaken
        case failed(String)

        var isOK: Bool {
            switch self {
            case .renamed, .unchanged: return true
            default: return false
            }
        }

        var message: String? {
            switch self {
            case .renamed, .unchanged: return nil
            case .notFound: return String(localized: "That plugin is no longer installed.")
            case .fromStore(let store):
                return String(localized: "Plugins from \(store) keep their name so updates can find them.")
            case .invalidName:
                return String(localized: "Use a name without “/” or “:”.")
            case .nameTaken: return String(localized: "Another plugin already has that name.")
            case .failed(let detail): return detail
            }
        }
    }

    /// A plugin id is a file name: keep it one path component, and let the
    /// user type "Name" or "Name.js" indifferently. nil when the result would
    /// not be a usable file name.
    nonisolated static func normalizedName(_ proposed: String) -> String? {
        var name = proposed.trimmingCharacters(in: .whitespacesAndNewlines)
        if name.lowercased().hasSuffix(".js") {
            name = String(name.dropLast(3)).trimmingCharacters(in: .whitespaces)
        }
        guard !name.isEmpty, !name.hasPrefix("."),
              !name.contains("/"), !name.contains(":")
        else { return nil }
        return name
    }

    /// Can this plugin be renamed? False for anything a store installed.
    func canRename(_ id: String) -> Bool {
        guard let descriptor = descriptor(for: id) else { return false }
        if case .store = descriptor.origin { return false }
        return true
    }

    /// The checks and the file move, separated from the instance so the
    /// tests can run it against a temporary folder. `existingIDs` is every
    /// installed plugin id, used for the collision check.
    nonisolated static func performRename(
        of descriptor: PluginDescriptor, to proposed: String, existingIDs: [String]
    ) -> RenameOutcome {
        if case .store(let store) = descriptor.origin { return .fromStore(store) }
        guard let name = normalizedName(proposed) else { return .invalidName }
        guard name != descriptor.id else { return .unchanged }
        // A collision with a different plugin; the file system is
        // case-insensitive by default, so compare that way.
        if existingIDs.contains(where: {
            $0 != descriptor.id && $0.caseInsensitiveCompare(name) == .orderedSame
        }) {
            return .nameTaken
        }

        let source = descriptor.assetsURL ?? descriptor.sourceURL
        let suffix = descriptor.assetsURL == nil ? "js" : "deskplugin"
        let destination = source.deletingLastPathComponent()
            .appendingPathComponent("\(name).\(suffix)")
        do {
            try FileManager.default.moveItem(at: source, to: destination)
        } catch {
            return .failed(error.localizedDescription)
        }
        return .renamed(name)
    }

    /// Renames a plugin's file, and with it the plugin's id. Placed items
    /// point at the id, so the caller repoints the layout — see
    /// `LayoutStore.repoint(pluginID:to:)`.
    @discardableResult
    func rename(_ id: String, to proposed: String) -> RenameOutcome {
        guard let descriptor = descriptor(for: id) else { return .notFound }
        let outcome = Self.performRename(of: descriptor, to: proposed,
                                         existingIDs: plugins.map(\.id))
        guard case .renamed(let name) = outcome else {
            if case .failed(let detail) = outcome {
                log.error("rename \(id, privacy: .public) failed: \(detail, privacy: .public)")
            }
            return outcome
        }

        // Preferences keyed by id travel with the plugin, or the rename would
        // silently turn auto-update off.
        if updater.isAutoUpdate(id) {
            updater.setAutoUpdate(false, for: id)
            updater.setAutoUpdate(true, for: name)
        }
        updateStatus[name] = updateStatus.removeValue(forKey: id)
        rescan()
        log.info("renamed plugin \(id, privacy: .public) to \(name, privacy: .public)")
        return outcome
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
                let id = url.deletingPathExtension().lastPathComponent
                found.append(PluginDescriptor(
                    id: id, sourceURL: url, origin: Self.origin(of: id)
                ))
            } else if url.pathExtension == "deskplugin" {
                let main = url.appendingPathComponent("main.js")
                if FileManager.default.fileExists(atPath: main.path) {
                    let id = url.deletingPathExtension().lastPathComponent
                    found.append(PluginDescriptor(
                        id: id,
                        sourceURL: main,
                        assetsURL: url,
                        origin: Self.origin(of: id)
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
