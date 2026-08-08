//
//  HookServer.swift
//  DeskLayer
//
//  One app-level, loopback-ONLY HTTP listener. Local tools (Claude/Codex
//  hooks, scripts) POST to it; the server fans each request out to every
//  running plugin that registered a matching handler via $server.on(...).
//  Never bound to an external interface.
//
//  Owned by RuntimeCoordinator; plugins never open their own port.
//

import Foundation
import Network
import os

nonisolated final class HookServer: @unchecked Sendable {
    struct Handler {
        let itemID: UUID
        let method: String
        /// Delivers (event, body) on the plugin's own queue.
        let deliver: @Sendable ([String: Any], String) -> Void
    }

    private let lock = NSLock()
    private var handlers: [Handler] = []
    private var listener: NWListener?
    private(set) var port: UInt16?
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "hookserver")

    // MARK: - Lifecycle

    /// (Re)bind to a loopback port. Safe to call repeatedly; a no-op if the
    /// port is unchanged and already listening.
    func start(port: UInt16) {
        if self.port == port, listener != nil { return }
        listener?.cancel()
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { return }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: nwPort)
        guard let newListener = try? NWListener(using: parameters) else {
            log.error("failed to bind 127.0.0.1:\(port)")
            return
        }
        newListener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        newListener.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state {
                self?.log.error("listener failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        newListener.start(queue: DispatchQueue.global(qos: .utility))
        listener = newListener
        self.port = port
        log.info("hook server listening on 127.0.0.1:\(port)")
    }

    func stop() {
        listener?.cancel()
        listener = nil
        port = nil
    }

    // MARK: - Registration (called from plugin instances)

    func addHandler(_ handler: Handler) {
        lock.lock()
        handlers.append(handler)
        lock.unlock()
    }

    func removeHandlers(itemID: UUID) {
        lock.lock()
        handlers.removeAll { $0.itemID == itemID }
        lock.unlock()
    }

    /// Cleared synchronously at the start of a runtime rebuild, so a stale
    /// instance's async teardown can't drop a freshly re-registered handler.
    func removeAllHandlers() {
        lock.lock()
        handlers.removeAll()
        lock.unlock()
    }

    private func matching(method: String) -> [Handler] {
        lock.lock()
        defer { lock.unlock() }
        return handlers.filter { $0.method == method }
    }

    // MARK: - Connection handling

    private func accept(_ connection: NWConnection) {
        connection.start(queue: DispatchQueue.global(qos: .utility))
        receive(connection, buffer: Data())
    }

    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self, error == nil else { connection.cancel(); return }
            var buffer = buffer
            if let data { buffer.append(data) }
            if buffer.count > 1 << 20 { // 1 MB cap
                self.respond(connection, status: "413 Payload Too Large", json: ["ok": false, "error": "too large"])
                return
            }
            if let request = HTTPRequest(data: buffer) {
                self.dispatch(request, connection: connection)
            } else if isComplete {
                connection.cancel()
            } else {
                self.receive(connection, buffer: buffer)
            }
        }
    }

    private func dispatch(_ request: HTTPRequest, connection: NWConnection) {
        let targets = matching(method: request.method)
        let event: [String: Any] = [
            "method": request.method,
            "path": request.path,
            "headers": request.headers,
        ]
        for handler in targets {
            handler.deliver(event, request.body)
        }
        respond(connection, status: "200 OK", json: ["ok": true, "delivered": targets.count])
    }

    private func respond(_ connection: NWConnection, status: String, json: [String: Any]) {
        let body = (try? JSONSerialization.data(withJSONObject: json)) ?? Data("{}".utf8)
        let head = "HTTP/1.1 \(status)\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }
}

// MARK: - Minimal HTTP/1.1 request parsing

private struct HTTPRequest {
    let method: String
    let path: String
    let headers: [String: String]
    let body: String

    /// Returns nil while the request is still incomplete.
    init?(data: Data) {
        guard let headerEnd = data.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        let headerText = String(decoding: data[..<headerEnd.lowerBound], as: UTF8.self)
        var lines = headerText.components(separatedBy: "\r\n")
        guard !lines.isEmpty else { return nil }
        let requestParts = lines.removeFirst().components(separatedBy: " ")
        guard requestParts.count >= 2 else { return nil }

        var parsedHeaders: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            parsedHeaders[key] = value
        }
        let contentLength = Int(parsedHeaders["content-length"] ?? "0") ?? 0
        let bodyData = data[headerEnd.upperBound...]
        guard bodyData.count >= contentLength else { return nil }

        method = requestParts[0].uppercased()
        path = requestParts[1]
        headers = parsedHeaders
        body = String(decoding: bodyData.prefix(contentLength), as: UTF8.self)
    }
}
