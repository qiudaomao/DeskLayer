//
//  PluginMetadata.swift
//  DeskLayer
//
//  Declarative metadata a plugin exposes on plugin.export:
//    version, author, description, updateURL.
//  Extracted in a throwaway JSContext with inert stubs, so top-level plugin
//  code runs without side effects (no timers/network/rendering) and we can
//  read the export fields — used both for installed plugins and for checking
//  a remote update's version before installing it.
//

import Foundation
import JavaScriptCore

nonisolated struct PluginMetadata: Sendable, Equatable {
    var version: String?
    var author: String?
    var summary: String? // plugin.export.description
    var updateURL: String?
    /// Preferred size in points, if the plugin declares width/height. Used to
    /// size a freshly added item so its rect matches the content's aspect.
    var preferredSize: CGSize?
    /// Whether the item may be resized on the canvas (default true).
    var resizable: Bool = true

    var isEmpty: Bool { version == nil && author == nil && summary == nil && updateURL == nil }

    /// Evaluates `source` in an isolated context with inert globals and reads
    /// plugin.export.{version,author,description,updateURL}. Returns empty
    /// metadata if the script doesn't parse or declares none.
    static func extract(from source: String) -> PluginMetadata {
        guard let context = JSContext() else { return PluginMetadata() }
        context.exceptionHandler = { _, _ in }

        // Inert stubs so top-level code runs but does nothing observable.
        let noop: @convention(block) () -> Void = {}
        let noopTimer: @convention(block) () -> Int = { 0 }
        for name in ["setTimeout", "setInterval", "clearTimeout", "clearInterval"] {
            context.setObject(noopTimer, forKeyedSubscript: name as NSString)
        }
        context.setObject(noop, forKeyedSubscript: "__dl_log" as NSString)
        context.evaluateScript("""
        var plugin = { export: null };
        var console = { log: function(){}, error: function(){}, warn: function(){} };
        var $system = { stats: function(){ return {}; } };
        var $server = { on: function(){}, listen: function(){} };
        function fetch(){ return { then: function(){ return this; }, catch: function(){ return this; } }; }
        function shell(){ return Promise.resolve({}); }
        function applescript(){ return Promise.resolve(''); }
        function ssh(){ return Promise.resolve({}); }
        function WebSocket(){ this.send = function(){}; this.close = function(){}; }
        // View builders return chainable inert nodes (declarative plugins call
        // these at top level in some styles).
        function __node(){ return new Proxy(function(){ return __node(); }, { get: function(){ return function(){ return __node(); }; } }); }
        var view = __node, VStack = __node, HStack = __node, ZStack = __node;
        var Text = __node, Image = __node, Spacer = __node, Section = __node, Paragraph = __node;
        """)

        context.evaluateScript(source)

        guard let export = context.objectForKeyedSubscript("plugin")?.objectForKeyedSubscript("export"),
              !export.isUndefined, !export.isNull else {
            return PluginMetadata()
        }
        func string(_ key: String) -> String? {
            guard let value = export.objectForKeyedSubscript(key), value.isString else { return nil }
            let s = value.toString()
            return (s?.isEmpty ?? true) ? nil : s
        }
        func number(_ key: String) -> Double? {
            guard let value = export.objectForKeyedSubscript(key), value.isNumber else { return nil }
            return value.toDouble()
        }
        var size: CGSize?
        if let w = number("width"), let h = number("height"), w > 0, h > 0 {
            size = CGSize(width: w, height: h)
        }
        let resizableValue = export.objectForKeyedSubscript("resizable")
        let resizable = (resizableValue?.isBoolean == true) ? resizableValue!.toBool() : true

        return PluginMetadata(
            version: string("version"),
            author: string("author"),
            summary: string("description"),
            updateURL: string("updateURL") ?? string("updateUrl"),
            preferredSize: size,
            resizable: resizable
        )
    }
}

/// Dotted numeric version compare ("1.2.10" > "1.2.9"). Non-numeric parts fall
/// back to a string compare; missing/blank versions sort lowest.
nonisolated func compareVersions(_ a: String, _ b: String) -> ComparisonResult {
    let lhs = a.split(separator: ".").map { String($0) }
    let rhs = b.split(separator: ".").map { String($0) }
    for i in 0..<max(lhs.count, rhs.count) {
        let l = i < lhs.count ? lhs[i] : "0"
        let r = i < rhs.count ? rhs[i] : "0"
        if let ln = Int(l), let rn = Int(r) {
            if ln != rn { return ln < rn ? .orderedAscending : .orderedDescending }
        } else if l != r {
            return l < r ? .orderedAscending : .orderedDescending
        }
    }
    return .orderedSame
}
