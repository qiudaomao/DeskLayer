//
//  RecordingCanvas.swift
//  DeskLayerTests
//
//  A CanvasJSExports implementation that records every ctx call as data
//  instead of drawing. The op log, serialized canonically, is the artifact
//  under shared/conformance/canvas/golden: any port's ctx bridge must
//  produce the same log for the same fixture. The exact recording rules
//  live in shared/conformance/runner-notes.md — change them there first.
//
//  measureText returns width = 7 × UTF-16 length, a deterministic stub so
//  goldens don't depend on any platform's text stack.
//

import Foundation
import JavaScriptCore
@testable import DeskLayer

final class RecordingCanvas: NSObject, CanvasJSExports, @unchecked Sendable {
    private(set) var ops: [Any] = []
    private let widthPts: Double
    private let heightPts: Double

    /// Same bridge production uses: PropertyValue.jsValue or nil.
    var propertyProvider: ((String) -> Any?)?

    init(width: Double, height: Double) {
        widthPts = width
        heightPts = height
        super.init()
    }

    /// Called by the conformance runner before each render() call.
    func mark(frame index: Int) {
        ops.append(["op": "frame", "index": Double(index)])
    }

    private func record(_ op: String, _ args: [Any]) {
        ops.append(["op": op, "args": args])
    }

    private func recordSet(_ name: String, _ value: Any) {
        ops.append(["op": "set", "name": name, "value": value])
    }

    // MARK: - Properties

    var fillStyle: String = "#000000" { didSet { recordSet("fillStyle", fillStyle) } }
    var strokeStyle: String = "#000000" { didSet { recordSet("strokeStyle", strokeStyle) } }
    var lineWidth: Double = 1 { didSet { recordSet("lineWidth", lineWidth) } }
    var lineCap: String = "butt" { didSet { recordSet("lineCap", lineCap) } }
    var lineJoin: String = "miter" { didSet { recordSet("lineJoin", lineJoin) } }
    var globalAlpha: Double = 1 { didSet { recordSet("globalAlpha", globalAlpha) } }
    var font: String = "13px Helvetica" { didSet { recordSet("font", font) } }
    var width: Double { widthPts }
    var height: Double { heightPts }

    // MARK: - State & transforms

    func save() { record("save", []) }
    func restore() { record("restore", []) }
    func translate(_ x: Double, _ y: Double) { record("translate", [x, y]) }
    func rotate(_ angle: Double) { record("rotate", [angle]) }
    func scale(_ x: Double, _ y: Double) { record("scale", [x, y]) }

    // MARK: - Rects

    func clearRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) { record("clearRect", [x, y, w, h]) }
    func fillRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) { record("fillRect", [x, y, w, h]) }
    func strokeRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) { record("strokeRect", [x, y, w, h]) }

    // MARK: - Paths

    func beginPath() { record("beginPath", []) }
    func closePath() { record("closePath", []) }
    func moveTo(_ x: Double, _ y: Double) { record("moveTo", [x, y]) }
    func lineTo(_ x: Double, _ y: Double) { record("lineTo", [x, y]) }

    func arc(_ x: Double, _ y: Double, _ r: Double, _ startAngle: Double, _ endAngle: Double, _ anticlockwise: Bool) {
        record("arc", [x, y, r, startAngle, endAngle, anticlockwise])
    }

    func fill() { record("fill", []) }
    func stroke() { record("stroke", []) }

    // MARK: - Text

    func fillText(_ text: String, _ x: Double, _ y: Double) { record("fillText", [text, x, y]) }

    func measureText(_ text: String) -> [String: Double] {
        record("measureText", [text])
        return ["width": Double(text.utf16.count) * 7]
    }

    // MARK: - Images

    func drawImage(_ name: String, _ x: Double, _ y: Double, _ w: Double, _ h: Double) {
        record("drawImage", [name, x, y, w, h])
    }

    // MARK: - Properties bridge

    func getProp(_ name: String) -> Any? {
        record("getProp", [name])
        return propertyProvider?(name)
    }
}
