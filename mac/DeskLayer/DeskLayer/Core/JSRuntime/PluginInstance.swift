//
//  PluginInstance.swift
//  DeskLayer
//
//  One running plugin: its own JSVirtualMachine + JSContext + serial queue,
//  so a wedged or broken plugin only ever stalls itself. Owns the async
//  bindings (timers/fetch/WebSocket) and the declared properties.
//
//  All JS access happens on `queue`. The pixel side lives in ItemRenderer.
//

import Foundation
import JavaScriptCore
import os

nonisolated enum PluginRenderMode: String {
    /// render(ctx) draws into the Canvas2D bridge at fps.
    case canvas
    /// render() returns a view tree, rendered as native SwiftUI.
    case declarative
}

nonisolated struct PluginLogEntry: Identifiable, Sendable {
    let id: UUID
    let date: Date
    let message: String
}

nonisolated final class PluginInstance: @unchecked Sendable {
    let pluginID: String
    let queue: DispatchQueue
    /// Declared properties merged with overrides; cadence is read by the scheduler.
    private(set) var properties: [PluginProperty]
    /// Seconds between renders. Derived from the plugin's `interval` property
    /// (seconds — e.g. 5, 300, 3600) or `fps` (frames/second, fractions
    /// allowed: 0.2 = every 5s). `fps: 0` and `.infinity` mean render once.
    /// Neither declared → 30fps for canvas; declarative hosts treat it as
    /// static (re-render on property edits only).
    let renderInterval: Double
    /// True when the plugin declared fps or interval itself.
    let hasDeclaredCadence: Bool
    let renderMode: PluginRenderMode

    /// Display-link rate hint (clamped to something CADisplayLink accepts).
    var fps: Double {
        guard renderInterval.isFinite, renderInterval > 0 else { return 1 }
        return min(max(1.0 / renderInterval, 1), 120)
    }

    /// Ring buffer of console.log output for the manager's inspector.
    /// A standalone object so the JS block can capture it during init
    /// (before self is fully initialized) — boot-time logs are kept too.
    private final class LogSink: @unchecked Sendable {
        private let lock = NSLock()
        private var entries: [PluginLogEntry] = []
        private static let capacity = 200

        func append(_ message: String) {
            lock.lock()
            entries.append(PluginLogEntry(id: UUID(), date: Date(), message: message))
            if entries.count > Self.capacity {
                entries.removeFirst(entries.count - Self.capacity)
            }
            lock.unlock()
        }

        func recent() -> [PluginLogEntry] {
            lock.lock()
            defer { lock.unlock() }
            return entries
        }

        func clear() {
            lock.lock()
            entries.removeAll()
            lock.unlock()
        }
    }

    private let logSink = LogSink()

    private let vm: JSVirtualMachine
    let context: JSContext
    private let renderFunction: JSValue
    private let exportValue: JSValue
    private let bindings: JSBindings
    private(set) var isErrored = false
    private(set) var errorMessage: String?

    /// Boots the plugin source. Returns nil when the source doesn't produce
    /// a usable plugin.export (already logged).
    init?(pluginID: String, source: String, overrides: [String: PropertyValue]) {
        self.pluginID = pluginID
        let queue = DispatchQueue(label: "desklayer.item.\(pluginID)", qos: .userInteractive)
        self.queue = queue

        vm = JSVirtualMachine()
        guard let jsContext = JSContext(virtualMachine: vm) else { return nil }
        context = jsContext
        context.name = "DeskLayer:\(pluginID)"
        // Attachable from Safari's Develop menu (▸ this Mac ▸ DeskLayer:<id>)
        // for breakpoints, the console, and profiling inside plugin JS.
        context.isInspectable = true

        context.exceptionHandler = { context, exception in
            let message = exception?.toString() ?? "unknown"
            renderLog.error("[\(pluginID, privacy: .public)] JS exception: \(message, privacy: .public)")
            // A custom handler replaces JSC's default of storing the exception;
            // re-store it so checkException() after a render call sees it.
            context?.exception = exception
        }

        let sink = logSink
        let log: @convention(block) (String) -> Void = { message in
            renderLog.info("[\(pluginID, privacy: .public)] \(message, privacy: .public)")
            sink.append(message)
        }
        context.setObject(log, forKeyedSubscript: "__dl_log" as NSString)
        context.evaluateScript("var plugin = { export: null }; var console = { log: __dl_log, error: __dl_log, warn: __dl_log };")

        bindings = JSBindings(queue: queue, pluginName: pluginID)
        bindings.install(into: context)
        context.evaluateScript(JSUIPrelude.source)

        context.evaluateScript(source, withSourceURL: URL(fileURLWithPath: "/plugins/\(pluginID).js"))

        guard
            let export = context.objectForKeyedSubscript("plugin")?.objectForKeyedSubscript("export"),
            !export.isUndefined, !export.isNull,
            let render = export.objectForKeyedSubscript("render"),
            !render.isUndefined, !render.isNull
        else {
            renderLog.error("[\(pluginID, privacy: .public)] plugin.export.render missing")
            return nil
        }
        exportValue = export
        renderFunction = render

        // Parse declared properties, coercing by declared valueType.
        var declared: [PluginProperty] = []
        if let raw = export.objectForKeyedSubscript("properties")?.toArray() as? [[String: Any]] {
            for entry in raw {
                guard let name = entry["name"] as? String else { continue }
                let valueType = entry["valueType"] as? String ?? "string"
                guard let value = PropertyValue.coerce(entry["value"], valueType: valueType) else { continue }
                declared.append(PluginProperty(name: name, valueType: valueType, value: value))
            }
        }
        // Apply persisted overrides on top of declared defaults.
        for (index, property) in declared.enumerated() {
            if let override = overrides[property.name] {
                declared[index].value = override
            }
        }
        properties = declared
        let declaredFps = declared.first { $0.name == "fps" }?.value.doubleValue
        let declaredInterval = declared.first { $0.name == "interval" }?.value.doubleValue
        if let interval = declaredInterval, interval > 0 {
            // Seconds between renders, up to a day.
            renderInterval = min(max(interval, 1.0 / 120.0), 86_400)
            hasDeclaredCadence = true
        } else if let fpsValue = declaredFps {
            // Fractions allowed (0.2 = every 5s); 0 or negative = render once.
            renderInterval = fpsValue <= 0 ? .infinity : 1.0 / min(max(fpsValue, 1.0 / 86_400.0), 120)
            hasDeclaredCadence = true
        } else {
            renderInterval = 1.0 / 30.0
            hasDeclaredCadence = false
        }

        // Mode: explicit plugin.export.mode wins; otherwise render's arity —
        // render(ctx) is canvas, render() is declarative.
        if let mode = export.objectForKeyedSubscript("mode")?.toString(),
           let parsed = PluginRenderMode(rawValue: mode) {
            renderMode = parsed
        } else {
            let arity = renderFunction.objectForKeyedSubscript("length")?.toInt32() ?? 1
            renderMode = arity >= 1 ? .canvas : .declarative
        }

        bindings.afterCallback = { [weak self] in self?.checkException() }
        pushPropertiesToJS()
    }

    // MARK: - Rendering (call on `queue`)

    /// Invokes render(ctx). Returns false when the plugin threw.
    func callRender(with argument: JSValue?) -> Bool {
        guard !isErrored else { return false }
        if let argument {
            renderFunction.call(withArguments: [argument])
        } else {
            renderFunction.call(withArguments: [])
        }
        checkException()
        return !isErrored
    }

    /// Declarative mode: invokes render() and returns the tree as JSON.
    /// Call only on `queue`.
    func callRenderTree() -> String? {
        guard !isErrored else { return nil }
        let result = renderFunction.call(withArguments: [])
        checkException()
        guard !isErrored, let result, !result.isUndefined, !result.isNull else { return nil }
        let json = context.objectForKeyedSubscript("JSON")?
            .invokeMethod("stringify", withArguments: [result])
        checkException()
        return json?.isString == true ? json?.toString() : nil
    }

    func checkException() {
        guard let exception = context.exception, !exception.isUndefined, !exception.isNull else { return }
        isErrored = true
        errorMessage = exception.toString()
        context.exception = nil
        renderLog.error("[\(self.pluginID, privacy: .public)] errored, item unscheduled: \(self.errorMessage ?? "?", privacy: .public)")
    }

    /// Watchdog path: render() has been running too long (runaway loop?).
    /// The item is unscheduled; its queue thread is abandoned until (if
    /// ever) the loop exits — documented v1 trade-off.
    func flagWedged(after seconds: Double) {
        guard !isErrored else { return }
        isErrored = true
        errorMessage = String(format: "watchdog: render() still running after %.1fs (runaway loop?)", seconds)
        renderLog.error("[\(self.pluginID, privacy: .public)] \(self.errorMessage!, privacy: .public)")
    }

    /// Typed property lookup for ctx.getProp / the scheduler.
    func property(named name: String) -> PropertyValue? {
        properties.first { $0.name == name }?.value
    }

    // MARK: - console.log capture (inspector log panel)

    func recentLogs() -> [PluginLogEntry] {
        logSink.recent()
    }

    func clearLogs() {
        logSink.clear()
    }

    /// Applies an override live (from the inspector or a layout edit):
    /// updates the Swift copy and mutates the plugin's exported properties
    /// array in place, matching the plugin author's mental model.
    func applyOverride(name: String, value: PropertyValue) {
        queue.async { [self] in
            if let index = properties.firstIndex(where: { $0.name == name }) {
                properties[index].value = value
            }
            pushPropertiesToJS()
        }
    }

    private func pushPropertiesToJS() {
        guard let array = exportValue.objectForKeyedSubscript("properties"), array.isObject else { return }
        let length = Int(array.objectForKeyedSubscript("length")?.toInt32() ?? 0)
        for i in 0..<length {
            guard let entry = array.atIndex(i), entry.isObject,
                  let name = entry.objectForKeyedSubscript("name")?.toString(),
                  let property = properties.first(where: { $0.name == name })
            else { continue }
            entry.setObject(property.value.jsValue, forKeyedSubscript: "value" as NSString)
        }
    }

    /// Cancels all async work. Call once when the item is removed.
    func invalidate() {
        queue.async { [bindings] in
            bindings.invalidate()
        }
    }
}
