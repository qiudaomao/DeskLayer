//
//  ChatClient.swift
//  DeskLayer
//
//  One request per turn against an OpenAI-compatible /chat/completions
//  endpoint, with tool calling. That wire format is what OpenAI, DeepSeek,
//  Moonshot, Qwen, OpenRouter, Ollama, LM Studio and vLLM all speak, so the
//  base URL is the only thing that changes between them.
//
//  Providers disagree on the details, so decoding is deliberately forgiving:
//  a field that is missing, or arguments sent as a string instead of an
//  object, must not fail the run. Errors come back as a value, never a throw
//  at the UI — the same rule PluginUpdater.UpdateResult follows.
//

import Foundation
import os

/// A message in the conversation, in the wire's own shape.
nonisolated struct ChatMessage: Codable, Equatable {
    var role: String            // system | user | assistant | tool
    var content: String?
    var toolCalls: [ToolCall]?  // assistant asking for tools
    var toolCallID: String?     // tool result answering one

    enum CodingKeys: String, CodingKey {
        case role, content
        case toolCalls = "tool_calls"
        case toolCallID = "tool_call_id"
    }

    static func system(_ text: String) -> ChatMessage { ChatMessage(role: "system", content: text) }
    static func user(_ text: String) -> ChatMessage { ChatMessage(role: "user", content: text) }
    static func toolResult(_ text: String, callID: String) -> ChatMessage {
        ChatMessage(role: "tool", content: text, toolCallID: callID)
    }
}

nonisolated struct ToolCall: Codable, Equatable {
    var id: String
    var type: String = "function"
    var function: Function

    struct Function: Codable, Equatable {
        var name: String
        /// JSON, as a string — that is how the wire carries it.
        var arguments: String

        init(name: String, arguments: String) {
            self.name = name
            self.arguments = arguments
        }

        /// Some providers send `arguments` as an object rather than the
        /// string the spec calls for. Accept both.
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
            if let text = try? c.decode(String.self, forKey: .arguments) {
                arguments = text
            } else if let object = try? c.decode(JSONValue.self, forKey: .arguments),
                      let data = try? JSONEncoder().encode(object),
                      let text = String(data: data, encoding: .utf8) {
                arguments = text
            } else {
                arguments = "{}"
            }
        }
    }

    init(id: String, type: String = "function", function: Function) {
        self.id = id
        self.type = type
        self.function = function
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        // An id is required to answer a call; synthesise one rather than fail.
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? UUID().uuidString
        type = try c.decodeIfPresent(String.self, forKey: .type) ?? "function"
        function = try c.decode(Function.self, forKey: .function)
    }
}

/// A tool the model may call, described as JSON Schema.
nonisolated struct ToolSpec: Encodable {
    var type = "function"
    var function: Function

    struct Function: Encodable {
        var name: String
        var description: String
        var parameters: JSONValue
    }
}

/// What a model listing produced. An enum, not Result — errors here are text
/// for the user, not Swift `Error`s, the same as `ChatTurn`.
nonisolated enum ModelList {
    case models([String])
    case failed(String)
}

/// What one turn produced.
nonisolated enum ChatTurn {
    case text(String)
    case toolCalls([ToolCall], assistant: ChatMessage)
    case failed(String)
}

nonisolated final class ChatClient {
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "llm")
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        // Generation is slow; the 20s used elsewhere in the app would cut off
        // a large plugin mid-answer.
        config.timeoutIntervalForRequest = 180
        config.timeoutIntervalForResource = 600
        config.urlCache = nil
        return URLSession(configuration: config)
    }()

    func send(
        messages: [ChatMessage],
        tools: [ToolSpec],
        settings: LLMSettings,
        apiKey: String
    ) async -> ChatTurn {
        guard let url = settings.completionsURL else {
            return .failed(String(localized: "That base URL isn't valid."))
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }

        let body = Request(model: settings.model, messages: messages,
                           tools: tools.isEmpty ? nil : tools)
        do {
            request.httpBody = try JSONEncoder().encode(body)
        } catch {
            return .failed(error.localizedDescription)
        }

        do {
            let (data, response) = try await session.data(for: request)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                // Providers put the useful part in the body, not the status.
                let detail = Self.errorMessage(in: data) ?? "HTTP \(http.statusCode)"
                log.error("chat failed: \(detail, privacy: .public)")
                return .failed(detail)
            }
            let decoded = try JSONDecoder().decode(Response.self, from: data)
            guard let message = decoded.choices.first?.message else {
                return .failed(String(localized: "The endpoint returned no reply."))
            }
            if let calls = message.toolCalls, !calls.isEmpty {
                return .toolCalls(calls, assistant: message)
            }
            return .text(message.content ?? "")
        } catch is CancellationError {
            return .failed(String(localized: "Cancelled."))
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// The models the endpoint offers. Sorted, because providers return
    /// them in creation order, which is not useful in a picker.
    func listModels(settings: LLMSettings, apiKey: String) async -> ModelList {
        guard let url = settings.modelsURL else {
            return .failed(String(localized: "That base URL isn't valid."))
        }
        var request = URLRequest(url: url)
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        do {
            let (data, response) = try await session.data(for: request)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                return .failed(Self.errorMessage(in: data) ?? "HTTP \(http.statusCode)")
            }
            let decoded = try JSONDecoder().decode(ModelListResponse.self, from: data)
            let ids = decoded.data.map(\.id).filter { !$0.isEmpty }
            guard !ids.isEmpty else {
                return .failed(String(localized: "The endpoint listed no models."))
            }
            return .models(ids.sorted())
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// Not private: the tests decode provider samples directly.
    struct ModelListResponse: Decodable {
        var data: [Entry]
        struct Entry: Decodable {
            var id: String
            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                // Some gateways use "name" instead of "id".
                id = (try? c.decodeIfPresent(String.self, forKey: .id))
                    ?? (try? c.decodeIfPresent(String.self, forKey: .name)) ?? ""
            }
            enum CodingKeys: String, CodingKey { case id, name }
        }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            data = try c.decodeIfPresent([Entry].self, forKey: .data) ?? []
        }
        enum CodingKeys: String, CodingKey { case data }
    }

    /// `{"error": {"message": "..."}}` or `{"error": "..."}`, both seen.
    private static func errorMessage(in data: Data) -> String? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let error = root["error"] as? [String: Any], let message = error["message"] as? String {
            return message
        }
        if let error = root["error"] as? String { return error }
        if let message = root["message"] as? String { return message }
        return nil
    }

    // MARK: - Wire types

    private struct Request: Encodable {
        var model: String
        var messages: [ChatMessage]
        var tools: [ToolSpec]?
    }

    private struct Response: Decodable {
        var choices: [Choice]

        struct Choice: Decodable {
            var message: ChatMessage?

            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                message = try c.decodeIfPresent(ChatMessage.self, forKey: .message)
            }
            enum CodingKeys: String, CodingKey { case message }
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            choices = try c.decodeIfPresent([Choice].self, forKey: .choices) ?? []
        }
        enum CodingKeys: String, CodingKey { case choices }
    }
}

/// Enough JSON to describe tool parameters and to re-encode arguments that
/// arrived as an object.
nonisolated enum JSONValue: Codable, Equatable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case object([String: JSONValue])
    case array([JSONValue])
    case null

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Double.self) { self = .number(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode([JSONValue].self) { self = .array(v) }
        else if let v = try? c.decode([String: JSONValue].self) { self = .object(v) }
        else { self = .null }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .string(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .null: try c.encodeNil()
        }
    }

    /// Reads one string field out of a tool call's arguments.
    static func string(_ key: String, in json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        if let s = root[key] as? String { return s }
        if let n = root[key] as? NSNumber { return n.stringValue }
        return nil
    }
}
