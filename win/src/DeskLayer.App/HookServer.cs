// One app-level, loopback-only HTTP listener — the Windows twin of the mac
// HookServer. Local tools POST to 127.0.0.1:8787; each request fans out to
// every running plugin that registered a matching method via $server.on().
// A raw TcpListener (not HttpListener) avoids URL-ACL/admin prompts and
// never binds to an external interface.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DeskLayer.App;

public sealed class HookServer : IDisposable
{
    public sealed record Handler(Guid ItemId, string Method, Action<string, string> Deliver);

    private readonly object gate = new();
    private readonly List<Handler> handlers = new();
    private readonly Action<string> log;
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    public int Port { get; private set; }

    public HookServer(Action<string> log) => this.log = log;

    /// (Re)bind to a loopback port. Safe to call repeatedly; a no-op if the
    /// port is unchanged and already listening.
    public void Start(int port)
    {
        if (Port == port && listener != null) return;
        Stop();
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port); // loopback only
            listener.Start();
            Port = port;
            cts = new CancellationTokenSource();
            _ = AcceptLoop(listener, cts.Token);
            log($"hook server listening on 127.0.0.1:{port}");
        }
        catch (SocketException ex)
        {
            log($"hook server failed to bind 127.0.0.1:{port}: {ex.Message}");
            listener = null;
            Port = 0;
        }
    }

    public void Stop()
    {
        cts?.Cancel();
        try { listener?.Stop(); } catch { }
        listener = null;
        cts = null;
        Port = 0;
    }

    // ---- registration (called from plugin instances via the coordinator) ----

    public void AddHandler(Handler handler)
    {
        lock (gate) handlers.Add(handler);
    }

    public void RemoveHandlers(Guid itemId)
    {
        lock (gate) handlers.RemoveAll(h => h.ItemId == itemId);
    }

    /// Cleared synchronously at the start of a runtime rebuild so a stale
    /// instance's teardown can't drop a freshly re-registered handler.
    public void RemoveAllHandlers()
    {
        lock (gate) handlers.Clear();
    }

    private List<Handler> Matching(string method)
    {
        lock (gate) return handlers.Where(h => h.Method == method).ToList();
    }

    // ---- connection handling ----

    private async Task AcceptLoop(TcpListener active, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await active.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = HandleConnection(client);
        }
    }

    private async Task HandleConnection(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var request = await ReadRequest(stream);
                if (request == null) return;

                var targets = Matching(request.Method);
                var eventJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["method"] = request.Method,
                    ["path"] = request.Path,
                    ["headers"] = request.Headers,
                });
                foreach (var handler in targets) handler.Deliver(eventJson, request.Body);

                var payload = JsonSerializer.Serialize(new { ok = true, delivered = targets.Count });
                var body = Encoding.UTF8.GetBytes(payload);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
                await stream.WriteAsync(body);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Client hung up; nothing to do.
            }
        }
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private static async Task<HttpRequest?> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[64 * 1024];
        var received = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return null;
            received.Write(buffer, 0, read);
            if (received.Length > 1 << 20) return null; // 1 MB cap
            headerEnd = IndexOfDoubleCrlf(received.GetBuffer(), (int)received.Length);
        }

        var all = received.GetBuffer();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestParts = lines[0].Split(' ');
        if (requestParts.Length < 2) return null;

        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        var bodyStart = headerEnd + 4;
        while (received.Length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            received.Write(buffer, 0, read);
        }
        all = received.GetBuffer();
        var bodyLen = Math.Min(contentLength, (int)received.Length - bodyStart);
        var body = bodyLen > 0 ? Encoding.UTF8.GetString(all, bodyStart, bodyLen) : "";

        return new HttpRequest(requestParts[0].ToUpperInvariant(), requestParts[1], headers, body);
    }

    private static int IndexOfDoubleCrlf(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }

    public void Dispose() => Stop();
}
