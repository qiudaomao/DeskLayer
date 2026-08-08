//
//  CSSColor.swift
//  DeskLayerKit
//
//  Parses CSS color strings (#rgb/#rgba/#rrggbb/#rrggbbaa, rgb()/rgba(),
//  a subset of named colors) into CGColor, with a cache. Shared with the
//  widget extension for color modifiers in declarative trees.
//
import CoreGraphics
import Foundation

public enum CSSColor {
    private static let cache = NSCache<NSString, CGColor>()
    private static let space = CGColorSpace(name: CGColorSpace.sRGB)!

    private static let named: [String: (Double, Double, Double, Double)] = [
        "black": (0, 0, 0, 1), "white": (1, 1, 1, 1),
        "red": (1, 0, 0, 1), "green": (0, 0.5, 0, 1), "blue": (0, 0, 1, 1),
        "yellow": (1, 1, 0, 1), "orange": (1, 0.647, 0, 1),
        "purple": (0.5, 0, 0.5, 1), "cyan": (0, 1, 1, 1), "magenta": (1, 0, 1, 1),
        "lime": (0, 1, 0, 1), "pink": (1, 0.753, 0.796, 1),
        "gray": (0.5, 0.5, 0.5, 1), "grey": (0.5, 0.5, 0.5, 1),
        "silver": (0.753, 0.753, 0.753, 1), "gold": (1, 0.843, 0, 1),
        "transparent": (0, 0, 0, 0),
    ]

    public static func parse(_ string: String) -> CGColor? {
        let key = string as NSString
        if let hit = cache.object(forKey: key) { return hit }
        guard let color = parseUncached(string.trimmingCharacters(in: .whitespaces).lowercased()) else {
            return nil
        }
        cache.setObject(color, forKey: key)
        return color
    }

    private static func parseUncached(_ s: String) -> CGColor? {
        if let rgba = named[s] {
            return make(rgba.0, rgba.1, rgba.2, rgba.3)
        }
        if s.hasPrefix("#") {
            return parseHex(String(s.dropFirst()))
        }
        if s.hasPrefix("rgb") {
            return parseRGBFunc(s)
        }
        return nil
    }

    private static func parseHex(_ hex: String) -> CGColor? {
        func value(_ sub: Substring) -> Double? {
            UInt8(sub, radix: 16).map { Double($0) / 255.0 }
        }
        func nibble(_ ch: Character) -> Double? {
            UInt8(String(ch), radix: 16).map { Double($0) * 17.0 / 255.0 }
        }
        let chars = Array(hex)
        switch chars.count {
        case 3, 4:
            guard let r = nibble(chars[0]), let g = nibble(chars[1]), let b = nibble(chars[2])
            else { return nil }
            let a = chars.count == 4 ? (nibble(chars[3]) ?? 1) : 1
            return make(r, g, b, a)
        case 6, 8:
            let h = hex
            guard
                let r = value(h.prefix(2)),
                let g = value(h.dropFirst(2).prefix(2)),
                let b = value(h.dropFirst(4).prefix(2))
            else { return nil }
            let a = chars.count == 8 ? (value(h.dropFirst(6).prefix(2)) ?? 1) : 1
            return make(r, g, b, a)
        default:
            return nil
        }
    }

    private static func parseRGBFunc(_ s: String) -> CGColor? {
        guard let open = s.firstIndex(of: "("), let close = s.lastIndex(of: ")") else { return nil }
        let parts = s[s.index(after: open)..<close]
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
        guard parts.count == 3 || parts.count == 4 else { return nil }
        func channel(_ p: String) -> Double? {
            if p.hasSuffix("%") { return Double(p.dropLast()).map { $0 / 100.0 } }
            return Double(p).map { $0 / 255.0 }
        }
        guard
            let r = channel(parts[0]), let g = channel(parts[1]), let b = channel(parts[2])
        else { return nil }
        let a = parts.count == 4 ? (Double(parts[3]) ?? 1) : 1
        return make(r, g, b, a)
    }

    private static func make(_ r: Double, _ g: Double, _ b: Double, _ a: Double) -> CGColor? {
        CGColor(colorSpace: space, components: [
            CGFloat(min(max(r, 0), 1)), CGFloat(min(max(g, 0), 1)),
            CGFloat(min(max(b, 0), 1)), CGFloat(min(max(a, 0), 1)),
        ])
    }
}
