// Client for the DeskLayer community store (store.byteplayer.app) — sign in
// with the forum account and publish a plugin from inside the app.
//
// Accounts live on the Discourse forum (bbs.byteplayer.app); the store
// backend authenticates through DiscourseConnect, so one account covers the
// forum, the store, and in-app publishing. Sign-in is a device-code flow:
// the app opens a browser URL and polls for the token, so no custom URL
// scheme is needed. The bearer token is delivered exactly once and stored
// with DPAPI — the same treatment as the LLM API key.
//
// Contract: DeskLayerBackend/docs/API.md. The catalog side of the backend
// needs none of this — /catalog.json is a plain store any build can add.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLayer.Core.Community;

public sealed record CommunityUser(
    string Username,
    string? Name,
    [property: JsonPropertyName("discourseId")] long DiscourseId,
    bool Admin,
    bool Moderator);

public sealed record DeviceLogin(
    [property: JsonPropertyName("deviceCode")] string DeviceCode,
    [property: JsonPropertyName("loginUrl")] string LoginUrl,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
    [property: JsonPropertyName("pollUrl")] string PollUrl);

/// What one poll of the token endpoint produced. Exactly one field is set.
public sealed record PollResult(string? Token, CommunityUser? User, bool Pending, string? Error);

public sealed record PublishRequest(
    string Name,
    string Version,
    string? Description,
    string Source,
    string? Permissions,
    string? PreviewUrl = null,
    string? SourceRepo = null,
    /// PNG bytes, base64 — the store hosts it per version and points the
    /// catalog's preview at it. Wins over PreviewUrl when both are sent.
    string? PreviewPngBase64 = null);

public sealed record PublishResult(
    string? Slug,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
    [property: JsonPropertyName("topicUrl")] string? TopicUrl,
    string? Error);

public static class CommunityClient
{
    /// Overridable for rehearsals against a test backend, like the updater's
    /// DESKLAYER_FEED_URL.
    public static string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("DESKLAYER_STORE_BASE") is { Length: > 0 } custom
            ? custom.TrimEnd('/')
            : "https://store.byteplayer.app";

    public static string CatalogUrl => $"{BaseUrl}/catalog.json";
    public static string ForumUrl => "https://bbs.byteplayer.app";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string TokenPath => Path.Combine(Model.LayoutStore.DataDirectory, "community-token.bin");

    /// The stored bearer token, DPAPI-sealed for this Windows account (the
    /// same treatment as the LLM API key). Null/empty clears it.
    public static string? Token
    {
        get
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                var clear = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(clear);
                return token.Length == 0 ? null : token;
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    File.Delete(TokenPath);
                    return;
                }
                Directory.CreateDirectory(Model.LayoutStore.DataDirectory);
                var sealed_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenPath, sealed_);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { }
        }
    }

    /// Starts the device-code flow. The caller opens LoginUrl in a browser
    /// and polls PollToken until it stops returning Pending.
    public static async Task<DeviceLogin?> BeginLogin()
    {
        try
        {
            using var response = await Http.PostAsync($"{BaseUrl}/auth/device", content: null);
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<DeviceLogin>(await response.Content.ReadAsStringAsync(), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public static async Task<PollResult> PollToken(DeviceLogin login)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { deviceCode = login.DeviceCode }), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(login.PollUrl, content);
            var body = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode == 202) return new PollResult(null, null, Pending: true, null);
            if (!response.IsSuccessStatusCode)
                return new PollResult(null, null, false, ErrorFrom(body) ?? $"HTTP {(int)response.StatusCode}");
            var root = JsonSerializer.Deserialize<JsonElement>(body, Json);
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            var user = root.TryGetProperty("user", out var u)
                ? JsonSerializer.Deserialize<CommunityUser>(u.GetRawText(), Json) : null;
            if (token is not { Length: > 0 }) return new PollResult(null, null, false, "no token in response");
            return new PollResult(token, user, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PollResult(null, null, false, ex.Message);
        }
    }

    /// The signed-in user, or null when the token is missing or no longer
    /// valid (a 401 clears it, so the UI falls back to "sign in").
    public static async Task<CommunityUser?> Me()
    {
        var token = Token;
        if (token == null) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/me");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Token = null;
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<CommunityUser>(await response.Content.ReadAsStringAsync(), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// Publishes one version. The backend validates plugin.export, stores the
    /// immutable file, and creates (or extends) the forum showcase topic.
    public static async Task<PublishResult> Publish(PublishRequest request)
    {
        var token = Token;
        if (token == null) return new PublishResult(null, null, null, L.T("Sign in first."));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/plugins")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    name = request.Name,
                    version = request.Version,
                    description = request.Description,
                    source = request.Source,
                    permissions = request.Permissions,
                    previewUrl = request.PreviewUrl,
                    previewPng = request.PreviewPngBase64,
                    sourceRepo = request.SourceRepo,
                }, Json), Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Token = null;
                return new PublishResult(null, null, null, L.T("Your session expired — sign in again."));
            }
            if (!response.IsSuccessStatusCode)
                return new PublishResult(null, null, null, ErrorFrom(body) ?? $"HTTP {(int)response.StatusCode}");
            var ok = JsonSerializer.Deserialize<PublishResult>(body, Json);
            return ok ?? new PublishResult(null, null, null, "unreadable response");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PublishResult(null, null, null, ex.Message);
        }
    }

    private static string? ErrorFrom(string body)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            return root.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
