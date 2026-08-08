//
//  LayoutStore.swift
//  DeskLayer
//
//  Single source of truth for the layout. Loads/saves hand-editable JSON at
//  <container>/Library/Application Support/DeskLayer/layout.json with a
//  debounced atomic write. The RuntimeCoordinator observes `onChange`.
//

import AppKit
import Combine
import Foundation
import os

@MainActor
final class LayoutStore: ObservableObject {
    @Published private(set) var layout = Layout()

    /// Fires after any mutation (and initial load).
    let onChange = PassthroughSubject<Void, Never>()

    private var saveWork: DispatchWorkItem?
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "store")

    static let directoryURL: URL = {
        // Dev/test override: point the data directory anywhere.
        if let override = ProcessInfo.processInfo.environment["DESKLAYER_DATA_DIR"] {
            return URL(fileURLWithPath: override, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("DeskLayer", isDirectory: true)
    }()

    static let fileURL = directoryURL.appendingPathComponent("layout.json")

    // MARK: - Load / save

    func load() {
        try? FileManager.default.createDirectory(at: Self.directoryURL, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: Self.fileURL) {
            do {
                layout = try JSONDecoder().decode(Layout.self, from: data)
                log.info("loaded layout with \(self.layout.items.count) items")
            } catch {
                // Never clobber a hand-edited file that fails to parse.
                log.error("layout.json failed to decode, keeping file untouched: \(error.localizedDescription, privacy: .public)")
                return
            }
        } else {
            layout = Self.defaultLayout()
            log.info("no layout.json, created default with \(self.layout.items.count) items")
            scheduleSave()
        }
        onChange.send()
    }

    func replace(_ new: Layout) {
        layout = new
        scheduleSave()
        onChange.send()
    }

    func update(_ item: LayoutItem) {
        guard let index = layout.items.firstIndex(where: { $0.id == item.id }) else { return }
        layout.items[index] = item
        scheduleSave()
        onChange.send()
    }

    func add(_ item: LayoutItem) {
        layout.items.append(item)
        scheduleSave()
        onChange.send()
    }

    func remove(id: UUID) {
        layout.items.removeAll { $0.id == id }
        scheduleSave()
        onChange.send()
    }

    private func scheduleSave() {
        saveWork?.cancel()
        let snapshot = layout
        let work = DispatchWorkItem { [log] in
            do {
                let encoder = JSONEncoder()
                encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                let data = try encoder.encode(snapshot)
                try data.write(to: Self.fileURL, options: .atomic)
            } catch {
                log.error("layout save failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        saveWork = work
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    // MARK: - First run

    private static func defaultLayout() -> Layout {
        let display = NSScreen.main.flatMap(ScreenManager.displayUUID(for:)) ?? ""
        return Layout(items: [
            LayoutItem(
                pluginID: "AnalogClock",
                displayUUID: display,
                normalizedFrame: CGRect(x: 0.06, y: 0.55, width: 0.16, height: 0.25)
            ),
            LayoutItem(
                pluginID: "Particles",
                displayUUID: display,
                normalizedFrame: CGRect(x: 0.30, y: 0.40, width: 0.32, height: 0.32)
            ),
        ])
    }
}
