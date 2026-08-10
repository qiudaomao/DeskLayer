// Permission-gated host powers — port of the mac HostBindings.swift:
//   - shell(argv): run a program (argv-only, no shell parsing → no
//     injection), permission "shell", with a Windows-tuned blocklist
//   - applescript(src): rejects on Windows (no analogue; use
//     shell(["powershell.exe", ...]) under the shell permission)
//   - ssh(cmd, host?): bundled OpenSSH client, permission "ssh"
//   - $server.on(method, handler): register a loopback HTTP handler
//   - $platform: "windows" so plugins can branch (Windows addition)
//
// The API surface is always present; each privileged call checks the
// resolved permission set at call time. Callbacks queue and run on the
// owning thread via JsBindings.Pump (shared completion queue).

using System.Diagnostics;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace DeskLayer.Core.Js;

public sealed class HostBindings
{
    /// Executables a plugin may never invoke, even with the shell permission
    /// (Windows-tuned; mirrors the mac blocklist intent).
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "del", "erase", "rd", "rmdir", "format", "diskpart", "fdisk",
        "shutdown", "reboot", "taskkill", "tskill", "reg", "regedit",
        "vssadmin", "cipher", "sc", "net", "runas", "bcdedit", "wmic",
        "attrib", "icacls", "takeown", "powercfg",
    };

    private readonly Engine engine;
    private readonly SystemStats stats;
    private readonly Action<string> log;
    private readonly Action<Action> enqueueCompletion;
    private readonly Action onCallbackError;

    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();

    private IReadOnlyList<ResolvedSsh> sshHosts = Array.Empty<ResolvedSsh>();
    /// Resolved SSH destinations for this item (name → connection details).
    /// Setting also refreshes the JS-visible $ssh.hosts name list — plugins
    /// read it to decide whether a destination is configured, so the C# list
    /// alone isn't enough (mac HostBindings does the same refresh). Only set
    /// on the engine's own thread.
    public IReadOnlyList<ResolvedSsh> SshHosts
    {
        get => sshHosts;
        set
        {
            sshHosts = value;
            try
            {
                engine.SetValue("__dl_ssh_hosts", value.Select(h => h.Name).ToArray());
                engine.Execute("if (typeof $ssh === 'object') { $ssh.hosts = Array.from(__dl_ssh_hosts); }");
            }
            catch (Exception ex) when (ex is JavaScriptException or JintException)
            {
                log($"refreshing $ssh.hosts failed: {ex.Message}");
            }
        }
    }

    // $server.on runs at top-level load, before the coordinator wires the
    // registrar (and before permissions resolve). Buffer registrations until
    // ConfigureHookRegistrar is called; a null registrar drops them (the
    // "server" permission wasn't granted).
    private readonly List<(string method, Action<string, string> deliver)> pendingHooks = new();
    private Action<string, Action<string, string>>? hookRegistrar;

    public void ConfigureHookRegistrar(Action<string, Action<string, string>>? registrar)
    {
        hookRegistrar = registrar;
        if (registrar == null) { pendingHooks.Clear(); return; }
        foreach (var (method, deliver) in pendingHooks) registrar(method, deliver);
        pendingHooks.Clear();
    }

    public sealed record ResolvedSsh(string Name, string Host, int Port, string User, string? KeyPath);

    /// Host provides the $system.stats() snapshot source.
    public interface SystemStats { IDictionary<string, object> Snapshot(); }

    public HostBindings(Engine engine, SystemStats stats, Action<string> log,
                        Action<Action> enqueueCompletion, Action onCallbackError)
    {
        this.engine = engine;
        this.stats = stats;
        this.log = log;
        this.enqueueCompletion = enqueueCompletion;
        this.onCallbackError = onCallbackError;
    }

    public void Install()
    {
        engine.SetValue("__dl_system_stats", (Func<object>)(() => stats.Snapshot()));
        engine.SetValue("__dl_shell", (Action<JsValue, JsValue, JsValue>)Shell);
        engine.SetValue("__dl_applescript", (Action<string, JsValue, JsValue>)AppleScript);
        engine.SetValue("__dl_ssh", (Action<JsValue, JsValue, JsValue, JsValue, JsValue>)Ssh);
        engine.SetValue("__dl_server_on", (Action<string, JsValue>)ServerOn);
        engine.SetValue("__dl_platform", "windows");
        engine.Execute(JsPrelude);
    }

    private void Complete(JsValue fn, params object?[] args) => enqueueCompletion(() =>
    {
        try { engine.Invoke(fn, args); }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            log($"host callback threw: {ex.Message}");
            onCallbackError();
        }
    });

    // ---- shell ----

    private void Shell(JsValue argvValue, JsValue resolve, JsValue reject)
    {
        if (!Permissions.Contains("shell"))
        {
            Complete(reject, "permission 'shell' not granted (add it to plugin.export.permissions)");
            return;
        }
        var argv = ToStringArray(argvValue);
        if (argv.Count == 0 || argv[0].Length == 0)
        {
            Complete(reject, "shell(argv) needs at least the command");
            return;
        }
        var baseName = System.IO.Path.GetFileNameWithoutExtension(argv[0]).ToLowerInvariant();
        if (Blocked.Contains(baseName))
        {
            Complete(reject, $"command '{baseName}' is blocked by DeskLayer for safety");
            return;
        }
        RunProcess(argv[0], argv.Skip(1), resolve, reject);
    }

    private void RunProcess(string exe, IEnumerable<string> args, JsValue resolve, JsValue reject)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false, // argv passed verbatim, no cmd parsing
                    CreateNoWindow = true,
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Complete(reject, $"failed to start '{exe}'");
                    return;
                }
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000)) // 30s watchdog (mac parity)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                Complete(resolve, new Dictionary<string, object>
                {
                    ["status"] = (double)process.ExitCode,
                    ["stdout"] = Truncate(stdout.Result),
                    ["stderr"] = Truncate(stderr.Result),
                });
            }
            catch (Exception ex)
            {
                Complete(reject, ex.Message);
            }
        });
    }

    private static string Truncate(string s) => s.Length > (1 << 20) ? s[..(1 << 20)] : s;

    // ---- applescript (no Windows analogue) ----

    private void AppleScript(string source, JsValue resolve, JsValue reject) =>
        Complete(reject, "applescript() is not available on Windows (use shell(['powershell.exe', '-NoProfile', '-Command', ...]) with the 'shell' permission)");

    // ---- ssh ----

    private void Ssh(JsValue argvValue, JsValue hostNameValue, JsValue rawValue, JsValue resolve, JsValue reject)
    {
        if (!Permissions.Contains("ssh"))
        {
            Complete(reject, "permission 'ssh' not granted (add it to plugin.export.permissions)");
            return;
        }
        var hostName = hostNameValue.IsString() ? hostNameValue.AsString() : null;
        var match = hostName != null
            ? SshHosts.FirstOrDefault(h => h.Name == hostName)
            : SshHosts.FirstOrDefault();
        if (match == null || match.Host.Length == 0)
        {
            Complete(reject, hostName != null
                ? $"no SSH destination named '{hostName}'"
                : "no SSH destination configured for this item (set it in the inspector)");
            return;
        }

        var argv = ToStringArray(argvValue);
        var isRaw = rawValue.AsBoolean();
        var remote = isRaw ? argv.FirstOrDefault() ?? "" : string.Join(" ", argv.Select(ShellQuote));

        var sshExe = @"C:\Windows\System32\OpenSSH\ssh.exe";
        if (!System.IO.File.Exists(sshExe)) sshExe = "ssh"; // fall back to PATH
        var sshArgs = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=10",
            "-p", match.Port.ToString(),
        };
        if (match.KeyPath is { Length: > 0 } key) { sshArgs.Add("-i"); sshArgs.Add(key); }
        sshArgs.Add(match.User.Length > 0 ? $"{match.User}@{match.Host}" : match.Host);
        sshArgs.Add(remote);
        RunProcess(sshExe, sshArgs, resolve, reject);
    }

    /// POSIX single-quoting so an argument survives the remote shell (mac parity).
    private static string ShellQuote(string s)
    {
        if (s.Length == 0) return "''";
        const string safe = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./=:@%+,";
        return s.All(c => safe.Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
    }

    // ---- $server ----

    private void ServerOn(string method, JsValue handler)
    {
        // No permission check here: $server.on runs at load, before
        // permissions resolve. Enforcement is upstream — the registrar is
        // only wired when "server" is granted; otherwise these are dropped.
        void Deliver(string eventJson, string body) => Complete(handler, ParseJson(eventJson), body);
        var upper = method.ToUpperInvariant();
        if (hookRegistrar != null) hookRegistrar(upper, Deliver);
        else pendingHooks.Add((upper, Deliver));
    }

    private JsValue ParseJson(string json)
    {
        try { return engine.Evaluate($"({(json.Length == 0 ? "{}" : json)})"); }
        catch { return JsValue.Undefined; }
    }

    // ---- helpers ----

    private static List<string> ToStringArray(JsValue value)
    {
        var result = new List<string>();
        if (value.IsArray())
            foreach (var element in value.AsArray())
                result.Add(element.ToString());
        return result;
    }

    private const string JsPrelude = """
        var $platform = __dl_platform;
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
        var $ssh = { hosts: [] };
        function ssh(cmd, host) {
            var isRaw = !Array.isArray(cmd);
            var argv = isRaw ? [String(cmd)] : cmd;
            return new Promise(function (resolve, reject) {
                __dl_ssh(argv, host === undefined ? null : String(host), isRaw, resolve,
                         function (e) { reject(new Error(e)); });
            });
        }
        var $server = {
            on: function (method, handler) { __dl_server_on(String(method), handler); return $server; }
        };
        """;
}
