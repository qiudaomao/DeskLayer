// Headless end-to-end check for the Create Plugin (LLM) loop: a canned
// OpenAI-compatible endpoint on a loopback TcpListener (no URL ACL needed)
// answers two turns — first a write_plugin tool call carrying a small valid
// plugin, then a closing sentence — and the real PluginAuthorSession must
// drive the whole loop and install the result.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DeskLayer.Core.Llm;
using DeskLayer.Core.Model;

const string pluginName = "LlmCheckCard";
var pluginSource = """
    render = () => view([
        VStack([ Text("llm check").fontSize(14).textColor("white") ])
            .padding(10).background("#101418E6").cornerRadius(10)
    ]);
    plugin.export = {
        version: "1.0.0", author: "check", description: "LLM check plugin.",
        width: 160, height: 60, render
    };
    """;

// ---- canned endpoint ----
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var requestCount = 0;

_ = Task.Run(async () =>
{
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using var _client = client;
            var stream = client.GetStream();
            var buffer = new byte[1 << 20];
            var read = 0;
            // Read headers, then the declared body length.
            while (true)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n == 0) break;
                read += n;
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0) continue;
                var lengthLine = text.Split("\r\n").FirstOrDefault(
                    l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var declared = lengthLine == null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                var bodyBytes = read - Encoding.UTF8.GetByteCount(text[..(headerEnd + 4)]);
                if (bodyBytes >= declared) break;
            }

            var turn = Interlocked.Increment(ref requestCount);
            JsonObject message = turn == 1
                ? new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "call_1",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "write_plugin",
                            // Arguments as an OBJECT, not a string — the
                            // lenient decode path providers actually hit.
                            ["arguments"] = new JsonObject
                            {
                                ["name"] = pluginName,
                                ["source"] = pluginSource,
                            },
                        },
                    }),
                }
                : new JsonObject { ["role"] = "assistant", ["content"] = "A small check card." };
            var body = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = message }),
            }.ToJsonString();
            var payload = Encoding.UTF8.GetBytes(body);
            var head = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(payload);
        });
    }
});

// ---- drive the real session ----
// The session persists its settings; keep the user's llm.json out of it.
var settingsPath = Path.Combine(LayoutStore.DataDirectory, "llm.json");
var savedSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
var installedPath = Path.Combine(PluginRegistry.PluginsDirectory, pluginName + ".js");
File.Delete(installedPath);

using var registry = new PluginRegistry(watch: false);
var stores = new PluginStoreRegistry(Console.WriteLine);
var session = new PluginAuthorSession(registry, stores, Console.WriteLine);
session.Settings.BaseUrl = $"http://127.0.0.1:{port}/v1";
session.Settings.Model = "canned-model";
session.Changed += () =>
{
    var last = session.Steps.Count > 0 ? session.Steps[^1] : null;
    if (last != null) Console.WriteLine($"  step: {last.Text}{(last.Detail is { } d ? " — " + d : "")}");
};

Console.WriteLine($"endpoint on port {port}; docs bundled: {PluginDocs.IsAvailable} " +
                  $"(dts {PluginDocs.Declarations.Length} chars, guide {PluginDocs.Guide.Length} chars)");
session.Start("A small card that says llm check", PluginAuthorSession.Subject.New);

var deadline = DateTime.UtcNow.AddSeconds(30);
while (session.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(100);

var ok = session.InstalledPluginId == pluginName && File.Exists(installedPath);
Console.WriteLine($"error: {session.Error ?? "none"}");
Console.WriteLine($"installed: {session.InstalledPluginId ?? "none"}, file exists: {File.Exists(installedPath)}");
Console.WriteLine(ok ? "LLM CHECK PASSED" : "LLM CHECK FAILED");
File.Delete(installedPath);
if (savedSettings != null) File.WriteAllText(settingsPath, savedSettings);
else File.Delete(settingsPath);
return ok ? 0 : 1;
