//
//  HostBindings.swift
//  DeskLayer
//
//  Host-power APIs for plugin JS, in three safety tiers:
//
//  - $system.stats(): always available. Curated read-only metrics
//    (CPU/memory/disk/network) straight from mach/sysctl — the right way
//    to build a system monitor; no process spawning, sandbox-friendly.
//  - shell(cmd), applescript(src): arbitrary execution, so a plugin must
//    opt in by declaring plugin.export.permissions = ["shell"] /
//    ["applescript"]. Undeclared calls reject with a clear error.
//  - $server: permission "server". A loopback-ONLY HTTP listener so local
//    tools (Claude/Codex hooks, scripts) can push data into a plugin:
//        $server.on('POST', (event, body) => 'ok');
//        $server.listen(8787);
//    Never bound to external interfaces.
//
//  All JS callbacks run on the instance's serial queue.
//

import Darwin
import Foundation
@preconcurrency import JavaScriptCore
import Network
import os

nonisolated final class HostBindings: NSObject, @unchecked Sendable {
    private let queue: DispatchQueue
    private let pluginName: String
    /// Resolved after the plugin's export is read (see PluginInstance), so
    /// bindings can be installed before evaluation while enforcement waits
    /// for the declared permission set. Host APIs are meant to be used from
    /// render()/handlers/timers, which all run after load.
    var permissions: Set<String> = []
    /// Resolved SSH destination (with password from Keychain), set by the
    /// coordinator at spawn. nil until the user configures one — ssh() then
    /// rejects with a clear error.
    struct ResolvedSSH: Sendable {
        var name: String = "default"
        var host: String
        var port: Int
        var user: String
        var usesKey: Bool
        var keyPath: String
        var password: String?
    }
    /// Configured destinations in inspector order; ssh(argv) targets the
    /// first, ssh(argv, "name") targets one by name.
    var sshHosts: [ResolvedSSH] = [] {
        didSet { publishHostNames() }
    }
    /// Set by install(): lets us refresh $ssh.hosts when destinations change.
    private weak var jsContext: JSContext?

    private func publishHostNames() {
        guard let jsContext else { return }
        let names = sshHosts.map(\.name)
        jsContext.setObject(names, forKeyedSubscript: "__dl_ssh_hosts" as NSString)
        jsContext.evaluateScript("if (typeof $ssh === 'object') { $ssh.hosts = __dl_ssh_hosts; }")
    }
    private let stats = SystemStats()
    private var isInvalidated = false
    var afterCallback: (@Sendable () -> Void)?

    /// App-level hook wiring, set by the coordinator after init. Plugins
    /// typically call $server.on(...) at load — before this is wired — so
    /// such calls are buffered and flushed once the registrar arrives.
    typealias HookRegistrar = (_ method: String, _ handler: @escaping @Sendable ([String: Any], String) -> Void) -> Void
    private var registrar: HookRegistrar?
    var unregisterHooks: (() -> Void)?
    private var pendingHooks: [(String, @Sendable ([String: Any], String) -> Void)] = []

    /// Wires the shared HookServer; flushes any hooks registered pre-wire.
    func setHookRegistrar(_ registrar: @escaping HookRegistrar, unregister: @escaping () -> Void) {
        self.registrar = registrar
        self.unregisterHooks = unregister
        for (method, handler) in pendingHooks {
            registrar(method, handler)
        }
        pendingHooks.removeAll()
    }

    init(queue: DispatchQueue, pluginName: String) {
        self.queue = queue
        self.pluginName = pluginName
        super.init()
    }

    func invalidate() {
        isInvalidated = true
        unregisterHooks?()
        registrar = nil
        unregisterHooks = nil
        pendingHooks.removeAll()
        afterCallback = nil
    }

    /// Executables a plugin may never invoke, even with the shell permission.
    private static let blockedCommands: Set<String> = [
        "rm", "rmdir", "unlink", "srm", "shred", "dd", "mkfs", "newfs",
        "diskutil", "fdisk", "gpt", "shutdown", "reboot", "halt", "kill",
        "killall", "pkill", "sudo", "su", "launchctl", "chown", "chmod",
        "chflags", "mv", "format", "tmutil", "csrutil", "nvram", "purge",
    ]

    // MARK: - Install

    /// Installed once, before evaluation. The API surface is always present;
    /// each privileged call checks `permissions` (resolved from the plugin's
    /// export just after load) at call time and rejects if not granted.
    func install(into context: JSContext) {
        let systemStats: @convention(block) () -> [String: Any] = { [stats] in
            stats.snapshot()
        }
        context.setObject(systemStats, forKeyedSubscript: "__dl_system_stats" as NSString)

        // argv array: shell(['git', 'status']) — no shell interpretation,
        // so no injection. First element is the executable.
        let shell: @convention(block) (JSValue, JSValue, JSValue) -> Void = { [weak self] argv, resolve, reject in
            guard let self else { return }
            guard self.permissions.contains("shell") else {
                reject.call(withArguments: ["permission 'shell' not granted (add it to plugin.export.permissions)"])
                return
            }
            let args = (argv.toArray() ?? []).map { String(describing: $0) }
            self.runShell(argv: args, resolve: resolve, reject: reject)
        }
        context.setObject(shell, forKeyedSubscript: "__dl_shell" as NSString)

        let applescript: @convention(block) (String, JSValue, JSValue) -> Void = { [weak self] source, resolve, reject in
            guard let self else { return }
            guard self.permissions.contains("applescript") else {
                reject.call(withArguments: ["permission 'applescript' not granted (add it to plugin.export.permissions)"])
                return
            }
            self.runAppleScript(source: source, resolve: resolve, reject: reject)
        }
        context.setObject(applescript, forKeyedSubscript: "__dl_applescript" as NSString)

        // ssh(argv, hostName?) — run a command on a configured destination.
        jsContext = context
        let ssh: @convention(block) (JSValue, JSValue, JSValue, JSValue, JSValue) -> Void = { [weak self] argv, hostName, raw, resolve, reject in
            guard let self else { return }
            guard self.permissions.contains("ssh") else {
                reject.call(withArguments: ["permission 'ssh' not granted (add it to plugin.export.permissions)"])
                return
            }
            let args = (argv.toArray() ?? []).map { String(describing: $0) }
            let name = hostName.isString ? hostName.toString() : nil
            self.runSSH(argv: args, hostName: name, isRawCommand: raw.toBool(),
                        resolve: resolve, reject: reject)
        }
        context.setObject(ssh, forKeyedSubscript: "__dl_ssh" as NSString)

        // Register a handler with the shared app-level HookServer. The
        // handler fires on this plugin's queue; its return value is ignored
        // (the server acks all registered plugins at once).
        let on: @convention(block) (String, JSValue) -> Void = { [weak self] method, handler in
            // No permission check here: $server.on runs at plugin load,
            // before permissions are resolved. Enforcement is upstream — the
            // coordinator only supplies a registrar when "server" is granted,
            // so an ungranted plugin's buffered hooks are simply never wired.
            guard let self else { return }
            let queue = self.queue
            // Wrap the JS handler so it always fires on this plugin's queue.
            let wrapped: @Sendable ([String: Any], String) -> Void = { [weak self] event, body in
                queue.async {
                    guard let self, !self.isInvalidated else { return }
                    handler.call(withArguments: [event, body])
                    self.afterCallback?()
                }
            }
            if let registrar = self.registrar {
                registrar(method.uppercased(), wrapped)
            } else {
                // Registered at load, before the coordinator wired us up.
                self.pendingHooks.append((method.uppercased(), wrapped))
            }
        }
        context.setObject(on, forKeyedSubscript: "__dl_server_on" as NSString)

        context.evaluateScript(Self.prelude)
    }

    private static let prelude = """
    var $system = { stats: function () { return __dl_system_stats(); } };
    function shell(argv) {
        if (!Array.isArray(argv)) {
            return Promise.reject(new Error("shell(argv) takes an array: shell(['git', 'status'])"));
        }
        return new Promise(function (resolve, reject) {
            __dl_shell(argv, resolve, function (e) { reject(new Error(e)); });
        });
    }
    function applescript(source) {
        return new Promise(function (resolve, reject) {
            __dl_applescript(String(source), resolve, function (e) { reject(new Error(e)); });
        });
    }
    // ssh('uptime') or ssh(['cat', '/proc/cpuinfo']) — runs on the item's
    // first configured destination. Pass a name to target another:
    // ssh(['uptime'], 'nas'). $ssh.hosts lists configured names.
    var $ssh = { hosts: [] };
    function ssh(cmd, host) {
        var isRaw = !Array.isArray(cmd);           // string → remote shell
        var argv = isRaw ? [String(cmd)] : cmd;    // array → exec-like argv
        return new Promise(function (resolve, reject) {
            __dl_ssh(argv, host === undefined ? null : String(host), isRaw, resolve,
                     function (e) { reject(new Error(e)); });
        });
    }
    // The port is owned by the app; plugins only register handlers.
    var $server = {
        on: function (method, handler) { __dl_server_on(String(method), handler); return $server; }
    };
    """

    // MARK: - shell

    private func runShell(argv: [String], resolve: JSValue, reject: JSValue) {
        guard !isInvalidated else { return }
        guard let executable = argv.first, !executable.isEmpty else {
            onQueue { reject.call(withArguments: ["shell(argv) needs at least the command"]) }
            return
        }
        let base = (executable as NSString).lastPathComponent.lowercased()
        guard !Self.blockedCommands.contains(base) else {
            onQueue { reject.call(withArguments: ["command '\(base)' is blocked by DeskLayer for safety"]) }
            return
        }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let process = Process()
            // env resolves the command against PATH; argv passed verbatim
            // (no shell parsing → no injection).
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = argv
            let out = Pipe(), err = Pipe()
            process.standardOutput = out
            process.standardError = err
            do {
                try process.run()
            } catch {
                self?.onQueue { reject.call(withArguments: [error.localizedDescription]) }
                return
            }
            // Watchdog: kill anything still running after 30s.
            let timeout = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + 30, execute: timeout)
            let stdout = String(decoding: out.fileHandleForReading.readDataToEndOfFile().prefix(1 << 20), as: UTF8.self)
            let stderr = String(decoding: err.fileHandleForReading.readDataToEndOfFile().prefix(1 << 20), as: UTF8.self)
            process.waitUntilExit()
            timeout.cancel()
            let status = Int(process.terminationStatus)
            self?.onQueue {
                resolve.call(withArguments: [["status": status, "stdout": stdout, "stderr": stderr] as [String: Any]])
            }
        }
    }

    // MARK: - ssh

    /// POSIX single-quoting so an argument survives the remote shell.
    private static func shellQuoted(_ s: String) -> String {
        if s.isEmpty { return "''" }
        let safe = Set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./=:@%+,")
        if s.allSatisfy({ safe.contains($0) }) { return s }
        return "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private func runSSH(argv: [String], hostName: String?, isRawCommand: Bool, resolve: JSValue, reject: JSValue) {
        guard !isInvalidated else { return }
        let match = hostName.flatMap { name in sshHosts.first { $0.name == name } } ?? sshHosts.first
        guard let config = match, !config.host.isEmpty else {
            let detail = hostName.map { "no SSH destination named '\($0)'" }
                ?? "no SSH destination configured for this item (set it in the inspector)"
            onQueue { reject.call(withArguments: [detail]) }
            return
        }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            // Password auth needs an interactive prompt (fed via SSH_ASKPASS);
            // everything else runs non-interactively.
            let usesPassword = !config.usesKey && (config.password?.isEmpty == false)
            var sshArgs = [
                "-o", "BatchMode=" + (usesPassword ? "no" : "yes"),
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "ConnectTimeout=10",
            ]
            if config.port != 22 { sshArgs += ["-p", String(config.port)] }
            if config.usesKey, !config.keyPath.isEmpty {
                sshArgs += ["-i", (config.keyPath as NSString).expandingTildeInPath, "-o", "IdentitiesOnly=yes"]
            }
            // A bare host name resolves through ~/.ssh/config (alias, user,
            // key, port); user@host is used only when a user is given.
            sshArgs.append(config.user.isEmpty ? config.host : "\(config.user)@\(config.host)")
            // ssh joins the remaining words with spaces and the REMOTE shell
            // re-parses them, so an argv array must be shell-quoted to reach
            // the far side intact (ssh(['sh','-c',script]) then behaves like
            // exec). A raw string is passed through for shell interpretation.
            sshArgs += isRawCommand ? argv : argv.map(Self.shellQuoted)

            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
            process.arguments = sshArgs
            var environment = ProcessInfo.processInfo.environment

            // Password auth: feed it via a throwaway SSH_ASKPASS helper so
            // it never appears on a command line or in the process table.
            var askpassURL: URL?
            if !config.usesKey, let password = config.password, !password.isEmpty {
                let helper = FileManager.default.temporaryDirectory
                    .appendingPathComponent("dl-askpass-\(UUID().uuidString).sh")
                let script = "#!/bin/sh\ncat \"\(helper.path).pw\"\n"
                try? script.write(to: helper, atomically: true, encoding: .utf8)
                try? password.write(toFile: helper.path + ".pw", atomically: true, encoding: .utf8)
                try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
                try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: helper.path + ".pw")
                environment["SSH_ASKPASS"] = helper.path
                environment["SSH_ASKPASS_REQUIRE"] = "force"
                environment["DISPLAY"] = environment["DISPLAY"] ?? ":0"
                askpassURL = helper
            }
            process.environment = environment

            let out = Pipe(), err = Pipe()
            process.standardOutput = out
            process.standardError = err
            process.standardInput = FileHandle.nullDevice
            do {
                try process.run()
            } catch {
                self?.cleanupAskpass(askpassURL)
                self?.onQueue { reject.call(withArguments: [error.localizedDescription]) }
                return
            }
            let timeout = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + 60, execute: timeout)
            let stdout = String(decoding: out.fileHandleForReading.readDataToEndOfFile().prefix(1 << 20), as: UTF8.self)
            let stderr = String(decoding: err.fileHandleForReading.readDataToEndOfFile().prefix(1 << 20), as: UTF8.self)
            process.waitUntilExit()
            timeout.cancel()
            self?.cleanupAskpass(askpassURL)
            let status = Int(process.terminationStatus)
            let detail = Self.annotated(stderr: stderr, status: status)
            self?.onQueue {
                resolve.call(withArguments: [["status": status, "stdout": stdout, "stderr": detail] as [String: Any]])
            }
        }
    }

    /// macOS gates connections to hosts on the same link behind the Local
    /// Network privacy permission. Without it ssh can't even open the socket,
    /// which surfaces as a bare "exit 255" — hosts reached through a gateway
    /// keep working, so the failure looks arbitrary. Say what it actually is.
    private static func annotated(stderr: String, status: Int) -> String {
        guard status == 255,
              stderr.contains("connect to host"),
              stderr.contains("Operation timed out") || stderr.contains("No route to host")
                || stderr.contains("Network is unreachable") || stderr.contains("Host is down")
        else { return stderr }
        return stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            + "\nIf the host is on your local network, allow DeskLayer in"
            + " System Settings > Privacy & Security > Local Network."
    }

    private func cleanupAskpass(_ url: URL?) {
        guard let url else { return }
        try? FileManager.default.removeItem(at: url)
        try? FileManager.default.removeItem(atPath: url.path + ".pw")
    }

    // MARK: - applescript

    private func runAppleScript(source: String, resolve: JSValue, reject: JSValue) {
        guard !isInvalidated else { return }
        // NSAppleScript wants the main thread; keep scripts short.
        DispatchQueue.main.async { [weak self] in
            var errorInfo: NSDictionary?
            let script = NSAppleScript(source: source)
            let descriptor = script?.executeAndReturnError(&errorInfo)
            let value = descriptor?.stringValue ?? ""
            let message = errorInfo?[NSAppleScript.errorMessage] as? String
            self?.onQueue {
                if let message {
                    reject.call(withArguments: [message])
                } else {
                    resolve.call(withArguments: [value])
                }
            }
        }
    }

    // MARK: - Queue hop

    private func onQueue(_ body: @escaping @Sendable () -> Void) {
        queue.async { [weak self] in
            guard let self, !self.isInvalidated else { return }
            body()
            self.afterCallback?()
        }
    }
}

// MARK: - System metrics (mach/sysctl; safe and cheap)

private final class SystemStats: @unchecked Sendable {
    private let lock = NSLock()
    private var lastTicks: (user: UInt64, system: UInt64, idle: UInt64, nice: UInt64)?

    func snapshot() -> [String: Any] {
        lock.lock()
        defer { lock.unlock() }
        return [
            "time": Date().timeIntervalSince1970,
            "cpu": cpuUsage(),
            "cores": ProcessInfo.processInfo.activeProcessorCount,
            "memory": memory(),
            "disk": disk(),
            "network": network(),
            "uptime": ProcessInfo.processInfo.systemUptime,
            "thermalState": ProcessInfo.processInfo.thermalState.rawValue,
        ]
    }

    /// Overall CPU usage 0…1 since the previous stats() call (0 on first).
    private func cpuUsage() -> Double {
        var size = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.size / MemoryLayout<integer_t>.size)
        var info = host_cpu_load_info_data_t()
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(size)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &size)
            }
        }
        guard result == KERN_SUCCESS else { return 0 }
        let ticks = (
            user: UInt64(info.cpu_ticks.0),
            system: UInt64(info.cpu_ticks.1),
            idle: UInt64(info.cpu_ticks.2),
            nice: UInt64(info.cpu_ticks.3)
        )
        defer { lastTicks = ticks }
        guard let last = lastTicks else { return 0 }
        let busy = (ticks.user - last.user) + (ticks.system - last.system) + (ticks.nice - last.nice)
        let total = busy + (ticks.idle - last.idle)
        return total > 0 ? Double(busy) / Double(total) : 0
    }

    private func memory() -> [String: Any] {
        let total = ProcessInfo.processInfo.physicalMemory
        var size = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size)
        var info = vm_statistics64_data_t()
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(size)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &size)
            }
        }
        guard result == KERN_SUCCESS else { return ["total": total, "used": 0, "free": total] }
        let pageSize = UInt64(vm_kernel_page_size)
        let used = (UInt64(info.active_count) + UInt64(info.wire_count) + UInt64(info.compressor_page_count)) * pageSize
        return ["total": total, "used": used, "free": total > used ? total - used : 0]
    }

    private func disk() -> [String: Any] {
        let home = NSHomeDirectory()
        guard let attributes = try? FileManager.default.attributesOfFileSystem(forPath: home),
              let total = attributes[.systemSize] as? UInt64,
              let free = attributes[.systemFreeSize] as? UInt64
        else { return ["total": 0, "free": 0] }
        return ["total": total, "free": free]
    }

    /// Cumulative bytes over en* interfaces since boot; plugins diff samples
    /// to get rates.
    private func network() -> [String: Any] {
        var rx: UInt64 = 0, tx: UInt64 = 0
        var interfaces: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaces) == 0 else { return ["rxBytes": 0, "txBytes": 0] }
        defer { freeifaddrs(interfaces) }
        var cursor = interfaces
        while let current = cursor {
            let interface = current.pointee
            if interface.ifa_addr?.pointee.sa_family == UInt8(AF_LINK),
               String(cString: interface.ifa_name).hasPrefix("en"),
               let data = interface.ifa_data?.assumingMemoryBound(to: if_data.self) {
                rx &+= UInt64(data.pointee.ifi_ibytes)
                tx &+= UInt64(data.pointee.ifi_obytes)
            }
            cursor = interface.ifa_next
        }
        return ["rxBytes": rx, "txBytes": tx]
    }
}
