// A persistent SSH connection, reused across ssh() calls.
//
// Every ssh() used to spawn its own /usr/bin/ssh, paying TCP, key exchange
// and authentication on each one — ~700ms per call to a host across the
// internet, and RemoteMonitor polls every 2s per destination. A session keeps
// one ssh process alive with a remote `sh` reading commands from its stdin,
// so the handshake happens once and a command costs a round trip: ~43ms
// measured against the same host.
//
// OpenSSH's own ControlMaster would do this for us on the Mac, but Windows'
// OpenSSH has no multiplexing at all — passing ControlPath there fails the
// connection outright — and this is a design both platforms implement.
//
// The remote shell is shared between calls, so a plugin's command must not be
// able to disturb it. Each command runs in a subshell with its stdin from
// /dev/null: `exit` ends that subshell rather than the session, `cd` cannot
// leak into the next call, and a command that reads stdin cannot swallow the
// command stream. Replies are framed by a per-session random sentinel, which
// a plugin never sees and cannot practically guess.

import Foundation

/// Reads newline-delimited output from a pipe, blocking until each line
/// arrives. Returns nil at EOF, which is how a dropped session surfaces.
private final class LineReader {
    private let handle: FileHandle
    private var buffer = Data()

    init(_ handle: FileHandle) { self.handle = handle }

    func line() -> String? {
        while true {
            if let index = buffer.firstIndex(of: 0x0A) {
                let text = String(decoding: buffer[buffer.startIndex..<index], as: UTF8.self)
                buffer.removeSubrange(buffer.startIndex...index)
                return text
            }
            let chunk = handle.availableData
            if chunk.isEmpty {
                guard !buffer.isEmpty else { return nil }
                let rest = String(decoding: buffer, as: UTF8.self)
                buffer.removeAll()
                return rest
            }
            buffer.append(chunk)
        }
    }
}

final class SSHSession {
    struct Output {
        let stdout: String
        let stderr: String
        let status: Int
    }

    enum Result {
        case ok(Output)
        /// The connection is gone. The caller may retry with a one-shot ssh,
        /// which also produces the real diagnostic if the host is down.
        case dead
        /// The command outlived its watchdog. A shared channel has no way to
        /// cancel one command, so the session was killed; retrying would just
        /// hang again, so this is reported to the plugin as a failure.
        case timedOut
    }

    /// Writing to a pipe whose reader has exited raises SIGPIPE, which would
    /// take the whole app down. Foundation's networking already disables it;
    /// do the same before the first session writes.
    private static let ignoreSigpipe: Void = { signal(SIGPIPE, SIG_IGN) }()

    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let errors = Pipe()
    private let reader: LineReader
    private let outEnd: String
    private let errEnd: String
    private let errFile: String
    /// One command at a time: the channel is a single stream of framed
    /// replies, so concurrent callers would read each other's output.
    private let lock = NSLock()
    private var isDead = false
    private var idleClose: DispatchWorkItem?

    /// Closes a session left idle this long. A plugin that polls keeps its
    /// connection; one that called ssh() once at load lets it go.
    private static let idleTimeout: TimeInterval = 300

    private init(arguments: [String], environment: [String: String]) {
        _ = Self.ignoreSigpipe
        let nonce = UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(16)
        outEnd = "__DL_O_\(nonce)__"
        errEnd = "__DL_E_\(nonce)__"
        errFile = "/tmp/dl-ssh-\(nonce).err"
        reader = LineReader(output.fileHandleForReading)
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        process.arguments = arguments
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
    }

    /// Opens a session and proves it can carry framed commands. Returns nil
    /// when the remote end can't host one — a login shell that isn't POSIX
    /// (a Windows OpenSSH server defaulting to cmd), or no writable /tmp.
    /// The caller then falls back to one ssh per call for that destination.
    static func open(arguments: [String], environment: [String: String]) -> SSHSession? {
        // -T explicitly: a user's ssh_config may say RequestTTY, and a pty
        // would echo commands back and corrupt the framing.
        let session = SSHSession(arguments: ["-T"] + arguments + ["sh"], environment: environment)
        do {
            try session.process.run()
        } catch {
            return nil
        }
        // Drain stderr so a chatty banner can't fill the pipe and block ssh.
        session.errors.fileHandleForReading.readabilityHandler = { handle in
            _ = handle.availableData
        }
        guard case .ok(let probe) = session.run("printf dl-ok", timeout: 20),
              probe.status == 0, probe.stdout == "dl-ok" else {
            session.close()
            return nil
        }
        return session
    }

    /// Runs one command on the shared shell and returns its output verbatim.
    func run(_ command: String, timeout: TimeInterval) -> Result {
        lock.lock()
        defer { lock.unlock() }
        guard !isDead, process.isRunning else {
            isDead = true
            return .dead
        }
        idleClose?.cancel()

        // The trailing newline before each sentinel guarantees the marker
        // starts a line even when the command's output has no final newline;
        // joining the collected lines with "\n" puts the bytes back exactly.
        let script = "( \(command) ) </dev/null 2>\(errFile); __dl_status=$?; "
            + "printf '\\n%s %s\\n' \(outEnd) $__dl_status; "
            + "cat \(errFile) 2>/dev/null; printf '\\n%s\\n' \(errEnd)\n"
        guard write(script) else {
            markDead()
            return .dead
        }

        // Killing the process is the only way to interrupt a hung command;
        // it also unblocks the read below with EOF.
        var timedOut = false
        let watchdog = DispatchWorkItem { [weak self] in
            timedOut = true
            self?.markDead()
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: watchdog)
        defer {
            watchdog.cancel()
            if !isDead { scheduleIdleClose() }
        }

        var outLines: [String] = []
        var errLines: [String] = []
        var status: Int?
        var budget = 1 << 20   // same 1MB cap the one-shot path applies
        while true {
            guard let line = reader.line() else {
                markDead()
                return timedOut ? .timedOut : .dead
            }
            if status == nil {
                if line.hasPrefix(outEnd) {
                    status = Int(line.dropFirst(outEnd.count).trimmingCharacters(in: .whitespaces)) ?? -1
                    continue
                }
                if budget > 0 { outLines.append(line); budget -= line.utf8.count + 1 }
            } else {
                if line.hasPrefix(errEnd) { break }
                if budget > 0 { errLines.append(line); budget -= line.utf8.count + 1 }
            }
        }
        return .ok(Output(stdout: outLines.joined(separator: "\n"),
                          stderr: errLines.joined(separator: "\n"),
                          status: status ?? -1))
    }

    func close() {
        lock.lock()
        defer { lock.unlock() }
        idleClose?.cancel()
        idleClose = nil
        guard !isDead else { return }
        isDead = true
        if process.isRunning {
            // Tidy the remote scratch file, then let `sh` see end-of-input.
            _ = write("rm -f \(errFile)\nexit\n")
            input.fileHandleForWriting.closeFile()
            // Don't wait on a wedged connection; ssh dies with the pipe.
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { [process] in
                if process.isRunning { process.terminate() }
            }
        }
        errors.fileHandleForReading.readabilityHandler = nil
    }

    private func write(_ text: String) -> Bool {
        guard process.isRunning else { return false }
        do {
            try input.fileHandleForWriting.write(contentsOf: Data(text.utf8))
            return true
        } catch {
            return false
        }
    }

    private func markDead() {
        isDead = true
        if process.isRunning { process.terminate() }
    }

    private func scheduleIdleClose() {
        idleClose?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.close() }
        idleClose = work
        DispatchQueue.global().asyncAfter(deadline: .now() + Self.idleTimeout, execute: work)
    }

    deinit { close() }
}
