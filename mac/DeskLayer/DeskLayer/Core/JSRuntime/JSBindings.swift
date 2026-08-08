//
//  JSBindings.swift
//  DeskLayer
//
//  Timers, fetch and WebSocket for plugin JS. JSC has none of these; they
//  are bound to DispatchSourceTimer / URLSession. Every JS callback is
//  invoked on the instance's serial queue (JSValues are not thread-safe;
//  JSContext runs promise reactions automatically when a call unwinds).
//
//  Ownership: PluginInstance → JSBindings → callback JSValues → JSContext.
//  The native blocks installed as JS globals capture the binding *weakly*,
//  breaking the context → block → binding → JSValue → context cycle.
//  invalidate() cancels everything; late completions no-op.
//

import Foundation
import JavaScriptCore
import os

nonisolated final class JSBindings: NSObject, @unchecked Sendable {
    private let queue: DispatchQueue
    private let pluginName: String
    /// Called after every JS callback to surface exceptions (set by PluginInstance).
    var afterCallback: (@Sendable () -> Void)?

    private var nextID = 1
    private var timers: [Int: DispatchSourceTimer] = [:]
    private var sockets: [Int: URLSessionWebSocketTask] = [:]
    private var socketCallbacks: [Int: (onOpen: JSValue, onMessage: JSValue, onClose: JSValue, onError: JSValue)] = [:]
    private var isInvalidated = false

    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        return URLSession(configuration: config, delegate: socketDelegate, delegateQueue: nil)
    }()
    private let socketDelegate = SocketDelegate()

    init(queue: DispatchQueue, pluginName: String) {
        self.queue = queue
        self.pluginName = pluginName
        super.init()
        socketDelegate.owner = self
    }

    /// Tear down all async work. Call on the instance queue.
    func invalidate() {
        isInvalidated = true
        for timer in timers.values { timer.cancel() }
        timers.removeAll()
        for socket in sockets.values { socket.cancel(with: .goingAway, reason: nil) }
        sockets.removeAll()
        socketCallbacks.removeAll()
        session.invalidateAndCancel()
        afterCallback = nil
    }

    /// Run `body` on the instance queue unless torn down; fire afterCallback.
    private func onQueue(_ body: @escaping @Sendable () -> Void) {
        queue.async { [weak self] in
            guard let self, !self.isInvalidated else { return }
            body()
            self.afterCallback?()
        }
    }

    // MARK: - Install

    func install(into context: JSContext) {
        // Timers
        let setTimeout: @convention(block) (JSValue, Double) -> Int = { [weak self] fn, ms in
            self?.addTimer(fn: fn, ms: ms, repeats: false) ?? 0
        }
        let setInterval: @convention(block) (JSValue, Double) -> Int = { [weak self] fn, ms in
            self?.addTimer(fn: fn, ms: ms, repeats: true) ?? 0
        }
        let clearTimer: @convention(block) (Int) -> Void = { [weak self] id in
            self?.removeTimer(id: id)
        }
        context.setObject(setTimeout, forKeyedSubscript: "setTimeout" as NSString)
        context.setObject(setInterval, forKeyedSubscript: "setInterval" as NSString)
        context.setObject(clearTimer, forKeyedSubscript: "clearTimeout" as NSString)
        context.setObject(clearTimer, forKeyedSubscript: "clearInterval" as NSString)

        // Fetch
        let fetch: @convention(block) (String, JSValue, JSValue, JSValue) -> Void = { [weak self] url, options, resolve, reject in
            self?.startFetch(urlString: url, options: options, resolve: resolve, reject: reject)
        }
        context.setObject(fetch, forKeyedSubscript: "__dl_fetch" as NSString)

        // WebSocket
        let wsOpen: @convention(block) (String, JSValue, JSValue, JSValue, JSValue) -> Int = { [weak self] url, onOpen, onMessage, onClose, onError in
            self?.openSocket(urlString: url, onOpen: onOpen, onMessage: onMessage, onClose: onClose, onError: onError) ?? 0
        }
        let wsSend: @convention(block) (Int, String) -> Void = { [weak self] handle, text in
            self?.sendSocket(handle: handle, text: text)
        }
        let wsClose: @convention(block) (Int) -> Void = { [weak self] handle in
            self?.closeSocket(handle: handle)
        }
        context.setObject(wsOpen, forKeyedSubscript: "__dl_ws_open" as NSString)
        context.setObject(wsSend, forKeyedSubscript: "__dl_ws_send" as NSString)
        context.setObject(wsClose, forKeyedSubscript: "__dl_ws_close" as NSString)

        context.evaluateScript(Self.prelude)
    }

    // MARK: - Timers

    private func addTimer(fn: JSValue, ms: Double, repeats: Bool) -> Int {
        guard !isInvalidated else { return 0 }
        let id = nextID
        nextID += 1
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = max(ms, 0) / 1000.0
        if repeats {
            timer.schedule(deadline: .now() + interval, repeating: interval)
        } else {
            timer.schedule(deadline: .now() + interval)
        }
        timer.setEventHandler { [weak self] in
            guard let self, !self.isInvalidated else { return }
            if !repeats { self.timers.removeValue(forKey: id)?.cancel() }
            fn.call(withArguments: [])
            self.afterCallback?()
        }
        timers[id] = timer
        timer.resume()
        return id
    }

    private func removeTimer(id: Int) {
        timers.removeValue(forKey: id)?.cancel()
    }

    // MARK: - Fetch

    private func startFetch(urlString: String, options: JSValue, resolve: JSValue, reject: JSValue) {
        guard !isInvalidated else { return }
        guard let url = URL(string: urlString), url.scheme != nil else {
            onQueue { reject.call(withArguments: ["invalid URL: \(urlString)"]) }
            return
        }
        var request = URLRequest(url: url)
        if let method = options.objectForKeyedSubscript("method"), method.isString {
            request.httpMethod = method.toString()
        }
        if let headers = options.objectForKeyedSubscript("headers"), headers.isObject,
           let dict = headers.toDictionary() as? [String: Any] {
            for (key, value) in dict {
                request.setValue("\(value)", forHTTPHeaderField: key)
            }
        }
        if let body = options.objectForKeyedSubscript("body"), body.isString {
            request.httpBody = body.toString().data(using: .utf8)
        }
        if let timeout = options.objectForKeyedSubscript("timeout"), timeout.isNumber {
            request.timeoutInterval = timeout.toDouble() / 1000.0
        }

        session.dataTask(with: request) { [weak self] data, response, error in
            guard let self else { return }
            self.onQueue {
                if let error {
                    reject.call(withArguments: [error.localizedDescription])
                    return
                }
                let http = response as? HTTPURLResponse
                var headers: [String: String] = [:]
                for (key, value) in http?.allHeaderFields ?? [:] {
                    headers[String(describing: key).lowercased()] = String(describing: value)
                }
                let body = data.map { String(decoding: $0, as: UTF8.self) } ?? ""
                resolve.call(withArguments: [[
                    "status": http?.statusCode ?? 0,
                    "headers": headers,
                    "body": body,
                ] as [String: Any]])
            }
        }.resume()
    }

    // MARK: - WebSocket

    private func openSocket(urlString: String, onOpen: JSValue, onMessage: JSValue, onClose: JSValue, onError: JSValue) -> Int {
        guard !isInvalidated, let url = URL(string: urlString) else {
            onQueue { onError.call(withArguments: ["invalid URL: \(urlString)"]) }
            return 0
        }
        let id = nextID
        nextID += 1
        let task = session.webSocketTask(with: url)
        sockets[id] = task
        socketCallbacks[id] = (onOpen, onMessage, onClose, onError)
        socketDelegate.register(task: task, handle: id)
        receiveLoop(handle: id, task: task)
        task.resume()
        return id
    }

    private func receiveLoop(handle: Int, task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            guard let self else { return }
            self.onQueue {
                guard let callbacks = self.socketCallbacks[handle] else { return }
                switch result {
                case .success(let message):
                    switch message {
                    case .string(let text):
                        callbacks.onMessage.call(withArguments: [text])
                    case .data(let data):
                        callbacks.onMessage.call(withArguments: [data.base64EncodedString()])
                    @unknown default:
                        break
                    }
                    self.receiveLoop(handle: handle, task: task)
                case .failure(let error):
                    // A cancelled/closed socket surfaces here too; onClose fires
                    // via the delegate. Only report while still tracked.
                    if self.sockets[handle] != nil {
                        callbacks.onError.call(withArguments: [error.localizedDescription])
                    }
                }
            }
        }
    }

    private func sendSocket(handle: Int, text: String) {
        guard let task = sockets[handle] else { return }
        task.send(.string(text)) { [weak self] error in
            guard let self, let error else { return }
            self.onQueue {
                self.socketCallbacks[handle]?.onError.call(withArguments: [error.localizedDescription])
            }
        }
    }

    private func closeSocket(handle: Int) {
        sockets.removeValue(forKey: handle)?.cancel(with: .normalClosure, reason: nil)
        socketCallbacks.removeValue(forKey: handle)
    }

    fileprivate func socketDidOpen(taskID: Int) {
        onQueue { self.socketCallbacks[taskID]?.onOpen.call(withArguments: []) }
    }

    fileprivate func socketDidClose(taskID: Int, code: Int) {
        onQueue {
            self.socketCallbacks[taskID]?.onClose.call(withArguments: [code])
            self.sockets.removeValue(forKey: taskID)
            self.socketCallbacks.removeValue(forKey: taskID)
        }
    }

    // MARK: - URLSession delegate shim

    private final class SocketDelegate: NSObject, URLSessionWebSocketDelegate, @unchecked Sendable {
        weak var owner: JSBindings?
        private let lock = NSLock()
        private var handles: [ObjectIdentifier: Int] = [:]

        func register(task: URLSessionWebSocketTask, handle: Int) {
            lock.lock()
            handles[ObjectIdentifier(task)] = handle
            lock.unlock()
        }

        private func handle(for task: URLSessionWebSocketTask) -> Int? {
            lock.lock()
            defer { lock.unlock() }
            return handles[ObjectIdentifier(task)]
        }

        func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
            if let id = handle(for: webSocketTask) { owner?.socketDidOpen(taskID: id) }
        }

        func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
            if let id = handle(for: webSocketTask) { owner?.socketDidClose(taskID: id, code: closeCode.rawValue) }
        }
    }

    // MARK: - JS prelude (fetch Response wrapper + WebSocket class)

    static let prelude = """
    function fetch(url, options) {
        return new Promise(function (resolve, reject) {
            __dl_fetch(String(url), options || {}, function (r) {
                resolve({
                    status: r.status,
                    ok: r.status >= 200 && r.status < 300,
                    headers: { get: function (k) { var v = r.headers[String(k).toLowerCase()]; return v === undefined ? null : v; } },
                    text: function () { return Promise.resolve(r.body); },
                    json: function () {
                        try { return Promise.resolve(JSON.parse(r.body)); }
                        catch (e) { return Promise.reject(e); }
                    }
                });
            }, function (err) {
                reject(new Error(err));
            });
        });
    }

    function WebSocket(url) {
        var self = this;
        this.url = String(url);
        this.readyState = 0; // CONNECTING
        this.onopen = null; this.onmessage = null; this.onclose = null; this.onerror = null;
        this.__handle = __dl_ws_open(this.url,
            function () { self.readyState = 1; if (self.onopen) self.onopen({}); },
            function (msg) { if (self.onmessage) self.onmessage({ data: msg }); },
            function (code) { self.readyState = 3; if (self.onclose) self.onclose({ code: code }); },
            function (err) { if (self.onerror) self.onerror(new Error(err)); });
    }
    WebSocket.prototype.send = function (s) { __dl_ws_send(this.__handle, String(s)); };
    WebSocket.prototype.close = function () { this.readyState = 2; __dl_ws_close(this.__handle); };
    WebSocket.CONNECTING = 0; WebSocket.OPEN = 1; WebSocket.CLOSING = 2; WebSocket.CLOSED = 3;
    """
}
