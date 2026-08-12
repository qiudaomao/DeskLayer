// Async host bindings: timers, fetch, WebSocket — port of the mac
// JSBindings.swift. The JS-facing prelude (fetch Response wrapper,
// WebSocket class) is copied verbatim so the contract matches.
//
// Threading: network I/O runs on the thread pool, but every JS callback is
// queued and only ever executed when the OWNING thread calls Pump() — the
// Windows twin of the mac per-plugin serial queue. Timers are also driven
// by Pump(), so resolution is the host's frame tick (plenty for plugin
// cadences).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace DeskLayer.Core.Js;

public sealed class JsBindings : IDisposable
{
    private static readonly HttpClient Http = new();

    private readonly Engine engine;
    private readonly Action<string> log;
    private readonly Action onCallbackError;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Action> completions = new();

    private sealed class TimerEntry
    {
        public required int Id;
        public required JsValue Fn;
        public required double IntervalMs;
        public required bool Repeats;
        public double DueMs;
    }

    private readonly List<TimerEntry> timers = new();
    private readonly Dictionary<int, (ClientWebSocket socket, CancellationTokenSource cts, SemaphoreSlim sendGate)> sockets = new();
    private int nextId = 1;
    private volatile bool invalidated;

    public JsBindings(Engine engine, Action<string> log, Action onCallbackError)
    {
        this.engine = engine;
        this.log = log;
        this.onCallbackError = onCallbackError;
    }

    public void Install()
    {
        engine.SetValue("__dl_setTimeout", (Func<JsValue, double, int>)((fn, ms) => AddTimer(fn, ms, repeats: false)));
        engine.SetValue("__dl_setInterval", (Func<JsValue, double, int>)((fn, ms) => AddTimer(fn, ms, repeats: true)));
        engine.SetValue("__dl_clearTimer", (Action<int>)RemoveTimer);
        engine.SetValue("__dl_fetch", (Action<string, JsValue, JsValue, JsValue>)StartFetch);
        engine.SetValue("__dl_ws_open", (Func<string, JsValue, JsValue, JsValue, JsValue, int>)OpenSocket);
        engine.SetValue("__dl_ws_send", (Action<int, string>)SendSocket);
        engine.SetValue("__dl_ws_close", (Action<int>)CloseSocket);
        engine.Execute(JsPrelude);
    }

    /// Enqueue a callback to run on the owning thread at the next Pump —
    /// shared by HostBindings (shell/ssh/$server completions) so all JS
    /// callbacks funnel through one queue.
    public void Enqueue(Action completion) => completions.Enqueue(completion);

    /// Runs due timers and completed network callbacks. Call from the
    /// thread that owns the engine, once per frame tick.
    public void Pump()
    {
        if (invalidated) return;
        while (completions.TryDequeue(out var completion))
        {
            if (invalidated) return;
            completion();
        }

        var nowMs = clock.Elapsed.TotalMilliseconds;
        // Snapshot: a timer callback may add/clear timers.
        foreach (var timer in timers.Where(t => nowMs >= t.DueMs).ToList())
        {
            if (invalidated) return;
            if (!timer.Repeats) timers.Remove(timer);
            else timer.DueMs = nowMs + timer.IntervalMs;
            InvokeGuarded(timer.Fn);
        }
    }

    private void InvokeGuarded(JsValue fn, params object?[] args)
    {
        try
        {
            engine.Invoke(fn, args);
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException or TimeoutException)
        {
            log($"callback threw: {ex.Message}");
            onCallbackError();
        }
    }

    // ---- timers ----

    private int AddTimer(JsValue fn, double ms, bool repeats)
    {
        if (invalidated) return 0;
        var id = nextId++;
        timers.Add(new TimerEntry
        {
            Id = id,
            Fn = fn,
            IntervalMs = Math.Max(ms, 0),
            Repeats = repeats,
            DueMs = clock.Elapsed.TotalMilliseconds + Math.Max(ms, 0),
        });
        return id;
    }

    private void RemoveTimer(int id) => timers.RemoveAll(t => t.Id == id);

    // ---- fetch ----

    private void StartFetch(string url, JsValue options, JsValue resolve, JsValue reject)
    {
        if (invalidated) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            completions.Enqueue(() => InvokeGuarded(reject, $"invalid URL: {url}"));
            return;
        }

        // Read the options object HERE, on the engine's thread — Jint values
        // must never be touched from the thread pool.
        var method = "GET";
        var headers = new Dictionary<string, string>();
        string? body = null;
        double? timeoutMs = null;
        if (options.IsObject())
        {
            var obj = options.AsObject();
            var m = obj.Get("method");
            if (m.IsString()) method = m.AsString();
            var h = obj.Get("headers");
            if (h.IsObject())
            {
                var headersObj = h.AsObject();
                foreach (var key in headersObj.GetOwnPropertyKeys())
                    headers[key.ToString()] = headersObj.Get(key).ToString();
            }
            var b = obj.Get("body");
            if (b.IsString()) body = b.AsString();
            var t = obj.Get("timeout");
            if (t.IsNumber()) timeoutMs = t.AsNumber();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), uri);
                if (body != null) request.Content = new StringContent(body);
                foreach (var (key, value) in headers)
                    if (!request.Headers.TryAddWithoutValidation(key, value))
                        request.Content?.Headers.TryAddWithoutValidation(key, value);

                using var cts = timeoutMs is { } t ? new CancellationTokenSource(TimeSpan.FromMilliseconds(t)) : null;
                var response = await Http.SendAsync(request, cts?.Token ?? CancellationToken.None);
                var responseBody = await response.Content.ReadAsStringAsync();
                var responseHeaders = new Dictionary<string, object>();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    responseHeaders[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);

                var result = new Dictionary<string, object>
                {
                    ["status"] = (double)(int)response.StatusCode,
                    ["headers"] = responseHeaders,
                    ["body"] = responseBody,
                };
                completions.Enqueue(() => InvokeGuarded(resolve, result));
            }
            catch (Exception ex)
            {
                completions.Enqueue(() => InvokeGuarded(reject, ex.Message));
            }
        });
    }

    // ---- WebSocket (text frames; binary arrives base64, mac parity) ----

    private int OpenSocket(string url, JsValue onOpen, JsValue onMessage, JsValue onClose, JsValue onError)
    {
        if (invalidated) return 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            completions.Enqueue(() => InvokeGuarded(onError, $"invalid URL: {url}"));
            return 0;
        }
        var id = nextId++;
        var socket = new ClientWebSocket();
        var cts = new CancellationTokenSource();
        sockets[id] = (socket, cts, new SemaphoreSlim(1, 1));

        _ = Task.Run(async () =>
        {
            try
            {
                await socket.ConnectAsync(uri, cts.Token);
                completions.Enqueue(() => InvokeGuarded(onOpen));
                var buffer = new byte[64 * 1024];
                var message = new MemoryStream();
                while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var code = (int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure);
                        completions.Enqueue(() => { InvokeGuarded(onClose, (double)code); sockets.Remove(id); });
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var payload = result.MessageType == WebSocketMessageType.Text
                        ? System.Text.Encoding.UTF8.GetString(message.ToArray())
                        : Convert.ToBase64String(message.ToArray());
                    message.SetLength(0);
                    completions.Enqueue(() => InvokeGuarded(onMessage, payload));
                }
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                completions.Enqueue(() =>
                {
                    if (sockets.ContainsKey(id)) InvokeGuarded(onError, ex.Message);
                });
            }
        });
        return id;
    }

    private void SendSocket(int handle, string text)
    {
        if (!sockets.TryGetValue(handle, out var entry)) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _ = Task.Run(async () =>
        {
            await entry.sendGate.WaitAsync();
            try
            {
                await entry.socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, entry.cts.Token);
            }
            catch (Exception ex) when (!entry.cts.IsCancellationRequested)
            {
                log($"ws send failed: {ex.Message}");
            }
            finally
            {
                entry.sendGate.Release();
            }
        });
    }

    private void CloseSocket(int handle)
    {
        if (!sockets.Remove(handle, out var entry)) return;
        entry.cts.Cancel();
        try { entry.socket.Abort(); } catch { }
        entry.socket.Dispose();
    }

    public void Dispose()
    {
        invalidated = true;
        foreach (var id in sockets.Keys.ToList()) CloseSocket(id);
        timers.Clear();
    }

    // The mac JS prelude, verbatim (JSBindings.swift), plus the timer
    // aliases the mac installs as native names directly.
    private const string JsPrelude = """
        var setTimeout = function (fn, ms) { return __dl_setTimeout(fn, Number(ms) || 0); };
        var setInterval = function (fn, ms) { return __dl_setInterval(fn, Number(ms) || 0); };
        var clearTimeout = function (id) { __dl_clearTimer(Number(id) || 0); };
        var clearInterval = function (id) { __dl_clearTimer(Number(id) || 0); };

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
        """;
}
