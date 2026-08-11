// A persistent SSH connection, reused across ssh() calls — the Windows twin
// of the mac SSHSession.
//
// Every ssh() used to spawn its own ssh.exe, paying TCP, key exchange and
// authentication on each one, and RemoteMonitor polls every 2s per
// destination. A session keeps one ssh process alive with a remote `sh`
// reading commands from its stdin, so the handshake happens once and a
// command costs a round trip: ~210-378ms fell to ~7-27ms against a LAN host.
//
// OpenSSH's ControlMaster would do this on Unix, but Windows' OpenSSH has no
// multiplexing — passing ControlPath fails the connection outright with
// "getsockname failed: Not a socket" — so driving one process over its stdin
// is the only way to reuse a connection here. It is also what the mac does,
// so both platforms behave the same.
//
// The remote shell is shared between calls, so a plugin's command must not be
// able to disturb it. Each command runs in a subshell with its stdin from
// /dev/null: `exit` ends that subshell rather than the session, `cd` cannot
// leak into the next call, and a command that reads stdin cannot swallow the
// command stream. Replies are framed by a per-session random sentinel, which
// a plugin never sees and cannot practically guess.

using System.Diagnostics;
using System.Text;

namespace DeskLayer.Core.Js;

public sealed class SshSession : IDisposable
{
    public readonly record struct Output(string Stdout, string Stderr, int Status);

    public enum Outcome
    {
        Ok,
        /// The connection is gone. The caller may retry with a one-shot ssh,
        /// which also produces the real diagnostic if the host is down.
        Dead,
        /// The command outlived its watchdog. A shared channel has no way to
        /// cancel one command, so the session was killed; retrying would just
        /// hang again, so this is reported to the plugin as a failure.
        TimedOut,
    }

    public readonly record struct Result(Outcome Outcome, Output Value);

    /// Closes a session left idle this long. A plugin that polls keeps its
    /// connection; one that called ssh() once at load lets it go.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly Process process;
    private readonly StreamWriter input;
    private readonly string outEnd;
    private readonly string errEnd;
    private readonly string errFile;
    /// One command at a time: the channel is a single stream of framed
    /// replies, so concurrent callers would read each other's output.
    private readonly object gate = new();
    private readonly Timer idleTimer;
    /// Written by the watchdog thread, read under the gate.
    private volatile bool isDead;
    private DateTime lastUsed = DateTime.UtcNow;

    private SshSession(Process process, string nonce)
    {
        this.process = process;
        input = process.StandardInput;
        outEnd = $"__DL_O_{nonce}__";
        errEnd = $"__DL_E_{nonce}__";
        errFile = $"/tmp/dl-ssh-{nonce}.err";
        // Checks the clock rather than closing outright: the timer may
        // already have fired and be waiting on the gate while a command runs.
        idleTimer = new Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// Opens a session and proves it can carry framed commands. Returns null
    /// when the remote end can't host one — a login shell that isn't POSIX (a
    /// Windows OpenSSH server defaulting to cmd), or no writable /tmp. The
    /// caller then falls back to one ssh per call for that destination.
    public static SshSession? Open(string exe, IEnumerable<string> arguments)
    {
        var nonce = Guid.NewGuid().ToString("N")[..16];
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without this the writer emits a UTF-8 BOM, and the remote shell
            // reads it as part of the first command ("syntax error near
            // unexpected token").
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // -T explicitly: a user's ssh_config may say RequestTTY, and a pty
        // would echo commands back and corrupt the framing.
        psi.ArgumentList.Add("-T");
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add("sh");

        Process? started;
        try { started = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        if (started == null) return null;

        var session = new SshSession(started, nonce);
        // Drain stderr so a chatty banner can't fill the pipe and block ssh.
        _ = started.StandardError.ReadToEndAsync();

        var probe = session.Run("printf dl-ok", TimeSpan.FromSeconds(20));
        if (probe.Outcome != Outcome.Ok || probe.Value.Status != 0 || probe.Value.Stdout != "dl-ok")
        {
            session.Close();
            return null;
        }
        return session;
    }

    /// Runs one command on the shared shell and returns its output verbatim.
    public Result Run(string command, TimeSpan timeout)
    {
        lock (gate)
        {
            if (isDead || process.HasExited)
            {
                isDead = true;
                return new Result(Outcome.Dead, default);
            }
            idleTimer.Change(Timeout.Infinite, Timeout.Infinite);

            // The newline before each sentinel guarantees the marker starts a
            // line even when the command's output has no final newline;
            // joining the collected lines with "\n" puts the bytes back
            // exactly as the command wrote them.
            var script = $"( {command} ) </dev/null 2>{errFile}; __dl_status=$?; "
                       + $"printf '\\n%s %s\\n' {outEnd} $__dl_status; "
                       + $"cat {errFile} 2>/dev/null; printf '\\n%s\\n' {errEnd}";
            try
            {
                input.WriteLine(script);
                input.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                MarkDead();
                return new Result(Outcome.Dead, default);
            }

            // Killing the process is the only way to interrupt a hung
            // command; it also unblocks the read below with end-of-stream.
            var timedOut = false;
            using var watchdog = new Timer(_ => { timedOut = true; MarkDead(); }, null,
                                           (int)timeout.TotalMilliseconds, Timeout.Infinite);

            var (outLines, errLines) = (new List<string>(), new List<string>());
            int? status = null;
            var budget = 1 << 20;   // same 1MB cap the one-shot path applies
            while (true)
            {
                string? line;
                try { line = process.StandardOutput.ReadLine(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { line = null; }
                if (line == null)
                {
                    MarkDead();
                    return new Result(timedOut ? Outcome.TimedOut : Outcome.Dead, default);
                }
                if (status == null)
                {
                    if (line.StartsWith(outEnd, StringComparison.Ordinal))
                    {
                        status = int.TryParse(line[outEnd.Length..].Trim(), out var parsed) ? parsed : -1;
                        continue;
                    }
                    if (budget > 0) { outLines.Add(line); budget -= line.Length + 1; }
                }
                else
                {
                    if (line.StartsWith(errEnd, StringComparison.Ordinal)) break;
                    if (budget > 0) { errLines.Add(line); budget -= line.Length + 1; }
                }
            }

            lastUsed = DateTime.UtcNow;
            if (!isDead) idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            return new Result(Outcome.Ok, new Output(
                string.Join("\n", outLines), string.Join("\n", errLines), status ?? -1));
        }
    }

    public void Close()
    {
        lock (gate)
        {
            if (isDead) return;
            isDead = true;
            try { idleTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try
            {
                if (!process.HasExited)
                {
                    // Tidy the remote scratch file, then let `sh` see EOF.
                    input.WriteLine($"rm -f {errFile}");
                    input.WriteLine("exit");
                    input.Flush();
                    input.Close();
                    // Don't wait on a wedged connection; ssh dies with the pipe.
                    if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
                }
            }
            catch { /* the process is going away either way */ }
        }
    }

    private void CloseIfIdle()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - lastUsed >= IdleTimeout) Close();
        }
    }

    private void MarkDead()
    {
        isDead = true;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        Close();
        idleTimer.Dispose();
        process.Dispose();
    }
}
