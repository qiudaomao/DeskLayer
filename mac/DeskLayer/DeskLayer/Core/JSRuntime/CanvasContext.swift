//
//  CanvasContext.swift
//  DeskLayer
//
//  The `ctx` object handed to plugin render(ctx): a Canvas2D subset bridged
//  via JSExport onto a CGContext. The backing CGContext is flipped to a
//  top-left origin at creation so plugin coordinates match Canvas2D.
//
//  Only methods declared in CanvasJSExports are visible to JS.
//  All calls happen on the owning item's serial render queue.
//

import CoreGraphics
import CoreText
import DeskLayerKit
import Foundation
import JavaScriptCore

@objc nonisolated protocol CanvasJSExports: JSExport {
    var fillStyle: String { get set }
    var strokeStyle: String { get set }
    var lineWidth: Double { get set }
    var lineCap: String { get set }
    var lineJoin: String { get set }
    var globalAlpha: Double { get set }
    var font: String { get set }
    var width: Double { get }
    var height: Double { get }

    func save()
    func restore()
    func translate(_ x: Double, _ y: Double)
    func rotate(_ angle: Double)
    func scale(_ x: Double, _ y: Double)

    func clearRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double)
    func fillRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double)
    func strokeRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double)

    func beginPath()
    func closePath()
    func moveTo(_ x: Double, _ y: Double)
    func lineTo(_ x: Double, _ y: Double)
    func arc(_ x: Double, _ y: Double, _ r: Double, _ startAngle: Double, _ endAngle: Double, _ anticlockwise: Bool)
    func fill()
    func stroke()

    func fillText(_ text: String, _ x: Double, _ y: Double)
    func measureText(_ text: String) -> [String: Double]

    func drawImage(_ name: String, _ x: Double, _ y: Double, _ w: Double, _ h: Double)

    func getProp(_ name: String) -> Any?
}

nonisolated final class CanvasContext: NSObject, CanvasJSExports, @unchecked Sendable {
    private var cg: CGContext
    private let widthPts: Double
    private let heightPts: Double

    private var fillColor: CGColor = CGColor(gray: 0, alpha: 1)
    private var strokeColor: CGColor = CGColor(gray: 0, alpha: 1)
    private var fillStyleString = "#000000"
    private var strokeStyleString = "#000000"
    private var fontSpec = "13px Helvetica"
    private var ctFont = CTFontCreateWithName("Helvetica" as CFString, 13, nil)
    private var saveDepth = 0

    /// Set by ItemRenderer: typed property lookup for ctx.getProp(name).
    var propertyProvider: ((String) -> Any?)?
    /// Set by ItemRenderer: named image lookup for ctx.drawImage(name, …).
    var imageProvider: ((String) -> CGImage?)?

    init(cg: CGContext, widthPts: Double, heightPts: Double) {
        self.cg = cg
        self.widthPts = widthPts
        self.heightPts = heightPts
        super.init()
    }

    // MARK: - Frame lifecycle (Swift-only, invisible to JS)

    /// Rebind to the frame's back buffer and pop any save() the plugin leaked.
    /// Does NOT clear: Canvas2D content persists across frames (the caller
    /// carries the previous frame's pixels forward into the new back buffer).
    func beginFrame(on context: CGContext) {
        while saveDepth > 0 {
            cg.restoreGState()
            saveDepth -= 1
        }
        cg = context
        // Canvas2D style state persists across frames; the alternating back
        // buffer doesn't, so re-sync it.
        cg.setLineWidth(CGFloat(lineWidth))
        cg.setAlpha(CGFloat(min(max(globalAlpha, 0), 1)))
        applyLineCap()
        applyLineJoin()
    }

    private func applyLineCap() {
        switch lineCap {
        case "round": cg.setLineCap(.round)
        case "square": cg.setLineCap(.square)
        default: cg.setLineCap(.butt)
        }
    }

    private func applyLineJoin() {
        switch lineJoin {
        case "round": cg.setLineJoin(.round)
        case "bevel": cg.setLineJoin(.bevel)
        default: cg.setLineJoin(.miter)
        }
    }

    // MARK: - Properties

    var fillStyle: String {
        get { fillStyleString }
        set {
            fillStyleString = newValue
            if let c = CSSColor.parse(newValue) { fillColor = c }
        }
    }

    var strokeStyle: String {
        get { strokeStyleString }
        set {
            strokeStyleString = newValue
            if let c = CSSColor.parse(newValue) { strokeColor = c }
        }
    }

    var lineWidth: Double = 1 {
        didSet { cg.setLineWidth(CGFloat(lineWidth)) }
    }

    var lineCap: String = "butt" {
        didSet { applyLineCap() }
    }

    var lineJoin: String = "miter" {
        didSet { applyLineJoin() }
    }

    var globalAlpha: Double = 1 {
        didSet { cg.setAlpha(CGFloat(min(max(globalAlpha, 0), 1))) }
    }

    var font: String {
        get { fontSpec }
        set {
            fontSpec = newValue
            ctFont = Self.parseFont(newValue)
        }
    }

    var width: Double { widthPts }
    var height: Double { heightPts }

    // MARK: - State & transforms

    func save() {
        cg.saveGState()
        saveDepth += 1
    }

    func restore() {
        guard saveDepth > 0 else { return }
        cg.restoreGState()
        saveDepth -= 1
    }

    func translate(_ x: Double, _ y: Double) { cg.translateBy(x: CGFloat(x), y: CGFloat(y)) }
    func rotate(_ angle: Double) { cg.rotate(by: CGFloat(angle)) }
    func scale(_ x: Double, _ y: Double) { cg.scaleBy(x: CGFloat(x), y: CGFloat(y)) }

    // MARK: - Rects

    func clearRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) {
        cg.clear(CGRect(x: x, y: y, width: w, height: h))
    }

    func fillRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) {
        cg.setFillColor(fillColor)
        cg.fill(CGRect(x: x, y: y, width: w, height: h))
    }

    func strokeRect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) {
        cg.setStrokeColor(strokeColor)
        cg.stroke(CGRect(x: x, y: y, width: w, height: h))
    }

    // MARK: - Paths
    // v1 note: fill()/stroke() consume the context path (Canvas2D keeps it).
    // Acceptable for M0; revisit with an explicit CGMutablePath in M1.

    func beginPath() { cg.beginPath() }
    func closePath() { cg.closePath() }
    func moveTo(_ x: Double, _ y: Double) { cg.move(to: CGPoint(x: x, y: y)) }
    func lineTo(_ x: Double, _ y: Double) { cg.addLine(to: CGPoint(x: x, y: y)) }

    func arc(_ x: Double, _ y: Double, _ r: Double, _ startAngle: Double, _ endAngle: Double, _ anticlockwise: Bool) {
        // The context CTM is flipped (top-left origin), which mirrors sweep
        // direction: passing Canvas2D's anticlockwise flag straight through
        // as CG's `clockwise` yields the Canvas2D-visible direction.
        cg.addArc(
            center: CGPoint(x: x, y: y),
            radius: CGFloat(r),
            startAngle: CGFloat(startAngle),
            endAngle: CGFloat(endAngle),
            clockwise: anticlockwise
        )
    }

    func fill() {
        cg.setFillColor(fillColor)
        cg.fillPath()
    }

    func stroke() {
        cg.setStrokeColor(strokeColor)
        cg.strokePath()
    }

    // MARK: - Text

    func fillText(_ text: String, _ x: Double, _ y: Double) {
        let attributes: [CFString: Any] = [
            kCTFontAttributeName: ctFont,
            kCTForegroundColorAttributeName: fillColor,
        ]
        let attributed = CFAttributedStringCreate(nil, text as CFString, attributes as CFDictionary)!
        let line = CTLineCreateWithAttributedString(attributed)
        cg.saveGState()
        cg.textMatrix = .identity
        cg.translateBy(x: CGFloat(x), y: CGFloat(y))
        // Un-flip locally so glyphs render upright in the flipped context.
        cg.scaleBy(x: 1, y: -1)
        cg.textPosition = .zero
        CTLineDraw(line, cg)
        cg.restoreGState()
    }

    func measureText(_ text: String) -> [String: Double] {
        let attributes: [CFString: Any] = [kCTFontAttributeName: ctFont]
        let attributed = CFAttributedStringCreate(nil, text as CFString, attributes as CFDictionary)!
        let line = CTLineCreateWithAttributedString(attributed)
        let width = CTLineGetTypographicBounds(line, nil, nil, nil)
        return ["width": width]
    }

    // MARK: - Images

    func drawImage(_ name: String, _ x: Double, _ y: Double, _ w: Double, _ h: Double) {
        guard let image = imageProvider?(name) else { return }
        // CG draws images y-up; un-flip locally so they render upright in
        // the flipped (top-left origin) context.
        cg.saveGState()
        cg.translateBy(x: CGFloat(x), y: CGFloat(y + h))
        cg.scaleBy(x: 1, y: -1)
        cg.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        cg.restoreGState()
    }

    // MARK: - Properties bridge

    func getProp(_ name: String) -> Any? {
        propertyProvider?(name)
    }

    // MARK: - Font parsing ("13px Menlo", optional leading "bold")

    private static func parseFont(_ spec: String) -> CTFont {
        var size: CGFloat = 13
        var family = "Helvetica"
        var bold = false
        var tokens = spec.split(separator: " ").map(String.init)
        if let first = tokens.first, first == "bold" || first == "italic" {
            bold = first == "bold"
            tokens.removeFirst()
        }
        if let sizeToken = tokens.first(where: { $0.hasSuffix("px") || $0.hasSuffix("pt") }),
           let v = Double(sizeToken.dropLast(2)) {
            size = CGFloat(v)
            tokens.removeAll { $0 == sizeToken }
        }
        if !tokens.isEmpty {
            family = tokens.joined(separator: " ")
        }
        var font = CTFontCreateWithName(family as CFString, size, nil)
        if bold, let bolder = CTFontCreateCopyWithSymbolicTraits(font, size, nil, .traitBold, .traitBold) {
            font = bolder
        }
        return font
    }
}
