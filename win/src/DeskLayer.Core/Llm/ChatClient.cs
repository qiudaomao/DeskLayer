// One request per turn against an OpenAI-compatible /chat/completions
// endpoint, with tool calling — the Windows twin of the mac ChatClient. That
// wire format is what OpenAI, DeepSeek, Moonshot, Qwen, OpenRouter, Ollama,
// LM Studio and vLLM all speak, so the base URL is the only thing that
// changes between them.
//
// Providers disagree on the details, so decoding is deliberately forgiving:
// a field that is missing, or arguments sent as a string instead of an
// object, must not fail the run. Errors come back as a value, never a throw
// at the UI — the same rule PluginUpdater.UpdateResult follows.

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskLayer.Core.Llm;

/// A message in the conversation, in the wire's own shape.
public sealed class ChatMessage
{
    public string Role = "user";        // system | user | assistant | tool
    public string? Content;
    public List<ToolCall>? ToolCalls;   // assistant asking for tools
    public string? ToolCallId;          // tool result answering one

    public static ChatMessage System(string text) => new() { Role = "system", Content = text };
    public static ChatMessage User(string text) => new() { Role = "user", Content = text };
    public static ChatMessage ToolResult(string text, string callId) =>
        new() { Role = "tool", Content = text, ToolCallId = callId };

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["role"] = Role };
        // "content" is required for tool results even when empty; assistant
        // messages carrying tool_calls may have null content.
        if (Content != null || ToolCalls == null) o["content"] = Content ?? "";
        if (ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var call in ToolCalls)
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = call.Type,
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments,
                    },
                });
            o["tool_calls"] = calls;
        }
        if (ToolCallId != null) o["tool_call_id"] = ToolCallId;
        return o;
    }
}

public sealed class ToolCall
{
    public string Id = "";
    public string Type = "function";
    public string Name = "";
    /// JSON, as a string — that is how the wire carries it.
    public string Arguments = "{}";

    /// Reads one string field out of the arguments JSON.
    public string? StringArgument(string key)
    {
        try
        {
            var root = JsonNode.Parse(Arguments) as JsonObject;
            var value = root?[key];
            if (value is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<double>(out var n)) return n.ToString("R");
            }
        }
        catch (JsonException) { }
        return null;
    }
}

/// A tool the model may call, described as JSON Schema (already-built JSON).
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone(),
        },
    };
}

/// What one turn produced. Exactly one of the fields is set.
public sealed class ChatTurn
{
    public string? Text;
    public (List<ToolCall> Calls, ChatMessage Assistant)? Tools;
    public string? Error;

    public static ChatTurn FromText(string text) => new() { Text = text };
    public static ChatTurn Failed(string message) => new() { Error = message };
}

public sealed class ChatClient
{
    // Generation is slow; the short timeouts used elsewhere in the app would
    // cut off a large plugin mid-answer.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public async Task<ChatTurn> Send(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        LlmSettings settings,
        string apiKey,
        CancellationToken cancel = default)
    {
        var url = settings.CompletionsUrl;
        if (url == null) return ChatTurn.Failed("That base URL isn't valid.");

        var body = new JsonObject { ["model"] = settings.Model };
        var wireMessages = new JsonArray();
        foreach (var message in messages) wireMessages.Add(message.ToJson());
        body["messages"] = wireMessages;
        if (tools.Count > 0)
        {
            var wireTools = new JsonArray();
            foreach (var tool in tools) wireTools.Add(tool.ToJson());
            body["tools"] = wireTools;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        string data;
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            data = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
            {
                // Providers put the useful part in the body, not the status.
                return ChatTurn.Failed(ErrorMessage(data) ?? $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return ChatTurn.Failed("Cancelled.");
        }
        catch (Exception ex)
        {
            return ChatTurn.Failed(ex.Message);
        }

        var message2 = DecodeFirstMessage(data);
        if (message2 == null) return ChatTurn.Failed("The endpoint returned no reply.");
        if (message2.ToolCalls is { Count: > 0 })
            return new ChatTurn { Tools = (message2.ToolCalls, message2) };
        return ChatTurn.FromText(message2.Content ?? "");
    }

    /// The models the endpoint offers. Sorted, because providers return them
    /// in creation order, which is not useful in a picker. Errors are text.
    public async Task<(List<string>? Models, string? Error)> ListModels(
        LlmSettings settings, string apiKey, CancellationToken cancel = default)
    {
        var url = settings.ModelsUrl;
        if (url == null) return (null, "That base URL isn't valid.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            var data = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
                return (null, ErrorMessage(data) ?? $"HTTP {(int)response.StatusCode}");

            var ids = new List<string>();
            if (JsonNode.Parse(data) is JsonObject root && root["data"] is JsonArray entries)
                foreach (var entry in entries)
                {
                    // Some gateways use "name" instead of "id".
                    var id = AsString(entry?["id"]) ?? AsString(entry?["name"]);
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            if (ids.Count == 0) return (null, "The endpoint listed no models.");
            ids.Sort(StringComparer.Ordinal);
            return (ids, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// choices[0].message, decoded forgivingly.
    private static ChatMessage? DecodeFirstMessage(string data)
    {
        JsonObject? message;
        try
        {
            var root = JsonNode.Parse(data) as JsonObject;
            message = (root?["choices"] as JsonArray)?.FirstOrDefault()?["message"] as JsonObject;
        }
        catch (JsonException) { return null; }
        if (message == null) return null;

        string? content = null;
        if (message["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var contentText))
            content = contentText;
        var decoded = new ChatMessage
        {
            Role = AsString(message["role"]) ?? "assistant",
            Content = content,
        };
        if (message["tool_calls"] is JsonArray calls)
        {
            decoded.ToolCalls = new List<ToolCall>();
            foreach (var node in calls)
            {
                if (node is not JsonObject call) continue;
                var function = call["function"] as JsonObject;
                // Some providers send `arguments` as an object rather than
                // the string the spec calls for. Accept both. An id is
                // required to answer a call; synthesise one rather than fail.
                var argumentsNode = function?["arguments"];
                var arguments = argumentsNode switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject or JsonArray => argumentsNode.ToJsonString(),
                    _ => "{}",
                };
                decoded.ToolCalls.Add(new ToolCall
                {
                    Id = AsString(call["id"]) ?? Guid.NewGuid().ToString(),
                    Type = AsString(call["type"]) ?? "function",
                    Name = AsString(function?["name"]) ?? "",
                    Arguments = arguments,
                });
            }
            if (decoded.ToolCalls.Count == 0) decoded.ToolCalls = null;
        }
        return decoded;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// `{"error": {"message": "..."}}` or `{"error": "..."}`, both seen.
    private static string? ErrorMessage(string data)
    {
        try
        {
            if (JsonNode.Parse(data) is not JsonObject root) return null;
            if (root["error"] is JsonObject error && error["message"] is JsonValue m &&
                m.TryGetValue<string>(out var inner)) return inner;
            if (root["error"] is JsonValue e && e.TryGetValue<string>(out var flat)) return flat;
            if (root["message"] is JsonValue msg && msg.TryGetValue<string>(out var top)) return top;
        }
        catch (JsonException) { }
        return null;
    }
}
