//
//  PropertyValue.swift
//  DeskLayer
//
//  Typed value for plugin properties. Plugins declare properties as
//  {name, valueType, value} where value often arrives as a *string*
//  (e.g. {"valueType": "number", "value": "30"}) — coercion always goes
//  by the declared valueType, never by the JSON type.
//

import Foundation

nonisolated enum PropertyValue: Codable, Hashable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case color(String) // #rrggbb / #rrggbbaa

    // MARK: - Accessors

    var stringValue: String {
        switch self {
        case .string(let s), .color(let s): return s
        case .number(let n): return n == n.rounded() ? String(Int(n)) : String(n)
        case .bool(let b): return b ? "true" : "false"
        }
    }

    var doubleValue: Double? {
        switch self {
        case .number(let n): return n
        case .string(let s): return Double(s)
        case .bool(let b): return b ? 1 : 0
        case .color: return nil
        }
    }

    var boolValue: Bool? {
        switch self {
        case .bool(let b): return b
        case .number(let n): return n != 0
        case .string(let s): return ["true", "1", "yes"].contains(s.lowercased())
        case .color: return nil
        }
    }

    /// The value as a JS-bridgeable object (String / Double / Bool).
    var jsValue: Any {
        switch self {
        case .string(let s), .color(let s): return s
        case .number(let n): return n
        case .bool(let b): return b
        }
    }

    // MARK: - Coercion (by declared valueType, not JSON type)

    static func coerce(_ raw: Any?, valueType: String) -> PropertyValue? {
        switch valueType {
        case "number":
            if let n = raw as? NSNumber { return .number(n.doubleValue) }
            if let s = raw as? String, let v = Double(s) { return .number(v) }
            return nil
        case "boolean", "bool":
            if let n = raw as? NSNumber { return .bool(n.boolValue) }
            if let s = raw as? String { return .bool(["true", "1", "yes"].contains(s.lowercased())) }
            return nil
        case "color":
            if let s = raw as? String { return .color(s) }
            return nil
        default: // "string" and anything unknown
            if let s = raw as? String { return .string(s) }
            if let n = raw as? NSNumber { return .string(n.stringValue) }
            return nil
        }
    }

    // MARK: - Codable (tagged: {"type": "number", "value": 30})

    private enum CodingKeys: String, CodingKey { case type, value }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let type = try container.decode(String.self, forKey: .type)
        switch type {
        case "number": self = .number(try container.decode(Double.self, forKey: .value))
        case "bool": self = .bool(try container.decode(Bool.self, forKey: .value))
        case "color": self = .color(try container.decode(String.self, forKey: .value))
        default: self = .string(try container.decode(String.self, forKey: .value))
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .string(let s):
            try container.encode("string", forKey: .type)
            try container.encode(s, forKey: .value)
        case .number(let n):
            try container.encode("number", forKey: .type)
            try container.encode(n, forKey: .value)
        case .bool(let b):
            try container.encode("bool", forKey: .type)
            try container.encode(b, forKey: .value)
        case .color(let c):
            try container.encode("color", forKey: .type)
            try container.encode(c, forKey: .value)
        }
    }
}

/// A property as declared by a plugin: name + valueType + current value.
nonisolated struct PluginProperty: Hashable {
    let name: String
    let valueType: String
    var value: PropertyValue
}
