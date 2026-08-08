//
//  ViewNode.swift
//  DeskLayerKit
//
//  The serialized view tree a declarative plugin returns from render():
//  plain data produced by the pure-JS builders in the app's JSUIPrelude
//  (zero bridge calls while building; one JSON.stringify per render).
//  Equatable so unchanged trees skip the SwiftUI update entirely.
//  Shared with the widget extension, which renders the same trees.
//

import Foundation

public struct ViewNode: Codable, Equatable, Sendable {
    public var type: String
    public var text: String?
    public var modifiers: [NodeModifier]?
    public var children: [ViewNode]?

    public static func decode(fromJSON json: String) -> ViewNode? {
        guard let data = json.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(ViewNode.self, from: data)
    }
}

public struct NodeModifier: Codable, Equatable, Sendable {
    public var name: String
    public var args: [JSONValue]

    public var firstDouble: Double? { args.first?.doubleValue }
    public var firstString: String? { args.first?.stringValue }
}

/// Heterogeneous modifier argument (string | number | bool | null).
public enum JSONValue: Codable, Equatable, Sendable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case null

    public var doubleValue: Double? {
        switch self {
        case .number(let n): return n
        case .string(let s): return Double(s)
        case .bool(let b): return b ? 1 : 0
        case .null: return nil
        }
    }

    public var stringValue: String? {
        switch self {
        case .string(let s): return s
        case .number(let n): return String(n)
        case .bool(let b): return String(b)
        case .null: return nil
        }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let b = try? container.decode(Bool.self) {
            self = .bool(b)
        } else if let n = try? container.decode(Double.self) {
            self = .number(n)
        } else {
            self = .string(try container.decode(String.self))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let s): try container.encode(s)
        case .number(let n): try container.encode(n)
        case .bool(let b): try container.encode(b)
        case .null: try container.encodeNil()
        }
    }
}
