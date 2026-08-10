//
//  ConformanceTests.swift
//  DeskLayerTests
//
//  Runs the cross-platform conformance fixtures in shared/conformance/
//  against this app's JS runtime and compares the canonical output with the
//  checked-in goldens. The goldens are the plugin-API contract every port
//  must match; the runner rules are documented in
//  shared/conformance/runner-notes.md — keep runner, notes, and any port
//  in lockstep.
//
//  Regenerate goldens with:
//    DESKLAYER_REGEN_GOLDENS=1 xcodebuild test ...
//

import Foundation
import JavaScriptCore
import Testing
@testable import DeskLayer

// MARK: - Canonical JSON

/// Deterministic JSON: object keys sorted, integral doubles printed as
/// integers, other doubles in Swift's shortest round-trip form, compact
/// (no whitespace). Mirrored exactly by every port's conformance runner.
enum CanonicalJSON {
    static func serialize(_ value: Any) -> String {
        var out = ""
        write(value, into: &out)
        return out
    }

    private static func write(_ value: Any, into out: inout String) {
        switch value {
        case let s as String:
            writeString(s, into: &out)
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() {
                out += n.boolValue ? "true" : "false"
            } else {
                writeNumber(n.doubleValue, into: &out)
            }
        case is NSNull:
            out += "null"
        case let array as [Any]:
            out += "["
            for (i, element) in array.enumerated() {
                if i > 0 { out += "," }
                write(element, into: &out)
            }
            out += "]"
        case let object as [String: Any]:
            out += "{"
            for (i, key) in object.keys.sorted().enumerated() {
                if i > 0 { out += "," }
                writeString(key, into: &out)
                out += ":"
                write(object[key]!, into: &out)
            }
            out += "}"
        default:
            Issue.record("unserializable value in conformance output: \(type(of: value))")
        }
    }

    private static func writeNumber(_ d: Double, into out: inout String) {
        if d.isFinite, d == d.rounded(), abs(d) < 1e15 {
            out += String(Int64(d))
        } else {
            out += "\(d)"
        }
    }

    private static func writeString(_ s: String, into out: inout String) {
        out += "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case let c where c.value < 0x20:
                out += String(format: "\\u%04x", c.value)
            default:
                out.unicodeScalars.append(scalar)
            }
        }
        out += "\""
    }
}

// MARK: - Runner

struct ConformanceTests {
    /// shared/conformance/, located from this source file's checkout path —
    /// goldens live in the repo, not the test bundle, so regeneration can
    /// write them back.
    private static let conformanceRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent() // DeskLayerTests/
        .deletingLastPathComponent() // DeskLayer/
        .deletingLastPathComponent() // mac/
        .deletingLastPathComponent() // repo root
        .appendingPathComponent("shared/conformance")

    private static var regenerate: Bool {
        ProcessInfo.processInfo.environment["DESKLAYER_REGEN_GOLDENS"] == "1"
    }

    private struct Fixture {
        let name: String
        let source: String
        let overrides: [String: PropertyValue]
        let goldenURL: URL
    }

    private static func fixtures(in suite: String) throws -> [Fixture] {
        let dir = conformanceRoot.appendingPathComponent(suite)
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "js" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        return try files.map { url in
            let name = url.deletingPathExtension().lastPathComponent
            // Optional <name>.overrides.json: persisted property overrides to
            // apply at boot, in the declared-properties shape.
            var overrides: [String: PropertyValue] = [:]
            let overridesURL = dir.appendingPathComponent("\(name).overrides.json")
            if let data = try? Data(contentsOf: overridesURL),
               let raw = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] {
                for entry in raw {
                    guard let entryName = entry["name"] as? String,
                          let valueType = entry["valueType"] as? String,
                          let value = PropertyValue.coerce(entry["value"], valueType: valueType)
                    else { continue }
                    overrides[entryName] = value
                }
            }
            return Fixture(
                name: name,
                source: try String(contentsOf: url, encoding: .utf8),
                overrides: overrides,
                goldenURL: dir.appendingPathComponent("golden/\(name).json")
            )
        }
    }

    private static func compareOrRegenerate(_ output: String, for fixture: Fixture) throws {
        let text = output + "\n"
        if regenerate {
            try text.write(to: fixture.goldenURL, atomically: true, encoding: .utf8)
            return
        }
        guard let golden = try? String(contentsOf: fixture.goldenURL, encoding: .utf8) else {
            Issue.record("\(fixture.name): golden missing — run with DESKLAYER_REGEN_GOLDENS=1")
            return
        }
        #expect(text == golden, "\(fixture.name): output drifted from golden")
    }

    // MARK: Canvas

    @Test func canvasFixturesMatchGoldens() throws {
        let all = try Self.fixtures(in: "canvas")
        #expect(all.count >= 25, "canvas suite shrank below the M-0.5 floor")
        for fixture in all {
            guard let instance = PluginInstance(pluginID: fixture.name, source: fixture.source, overrides: fixture.overrides) else {
                Issue.record("\(fixture.name): failed to boot")
                continue
            }
            defer { instance.invalidate() }
            #expect(instance.renderMode == .canvas, "\(fixture.name): not a canvas plugin")

            let output: String? = instance.queue.sync {
                let export = instance.context.objectForKeyedSubscript("plugin")?.objectForKeyedSubscript("export")
                let width = export?.objectForKeyedSubscript("width")?.toDouble() ?? 0
                let height = export?.objectForKeyedSubscript("height")?.toDouble() ?? 0
                let recorder = RecordingCanvas(width: width > 0 ? width : 200, height: height > 0 ? height : 100)
                recorder.propertyProvider = { [weak instance] name in
                    instance?.property(named: name)?.jsValue
                }
                guard let ctxValue = JSValue(object: recorder, in: instance.context) else { return nil }
                for frame in 0..<2 {
                    recorder.mark(frame: frame)
                    guard instance.callRender(with: ctxValue) else {
                        Issue.record("\(fixture.name): render threw on frame \(frame): \(instance.errorMessage ?? "?")")
                        return nil
                    }
                }
                return CanonicalJSON.serialize(recorder.ops)
            }
            if let output {
                try Self.compareOrRegenerate(output, for: fixture)
            }
        }
    }

    // MARK: Declarative

    @Test func declarativeFixturesMatchGoldens() throws {
        let all = try Self.fixtures(in: "declarative")
        #expect(all.count >= 20, "declarative suite shrank below the M-0.5 floor")
        for fixture in all {
            guard let instance = PluginInstance(pluginID: fixture.name, source: fixture.source, overrides: fixture.overrides) else {
                Issue.record("\(fixture.name): failed to boot")
                continue
            }
            defer { instance.invalidate() }
            #expect(instance.renderMode == .declarative, "\(fixture.name): not a declarative plugin")

            // Two renders: catches state leaks and proves action ids reset.
            var frames: [Any] = []
            var failed = false
            for frame in 0..<2 {
                let json = instance.queue.sync { instance.callRenderTree() }
                guard let json, let data = json.data(using: .utf8),
                      let tree = try? JSONSerialization.jsonObject(with: data) else {
                    Issue.record("\(fixture.name): render produced no tree on frame \(frame): \(instance.errorMessage ?? "?")")
                    failed = true
                    break
                }
                frames.append(tree)
            }
            guard !failed else { continue }
            try Self.compareOrRegenerate(CanonicalJSON.serialize(["frames": frames]), for: fixture)
        }
    }
}
