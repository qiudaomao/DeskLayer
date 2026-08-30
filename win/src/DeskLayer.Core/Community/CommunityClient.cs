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
    string? PreviewPngBase64 = null,
    /// A downscaled (~480px) PNG for the gallery grid, base64. Kept separate
    /// so the grid isn't loading full-size previews.
    string? ThumbnailPngBase64 = null);

public sealed record PublishResult(
    string? Slug,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
    [property: JsonPropertyName("topicUrl")] string? TopicUrl,
    string? Error);

/// One card in the community gallery — the paged endpoint's richer entry.
/// Superset of a catalog StorePlugin: adds slug, downloads, publishedAt, and
/// a thumbnail-ready preview URL (absent = show a placeholder).
public sealed record GalleryPlugin(
    string Name,
    string? Slug,
    string? Description,
    string Url,
    string? Version,
    string? Author,
    string? Preview,
    /// Small (~480px) image for the grid; null → show a placeholder, never
    /// fall back to the full-size Preview.
    string? Thumbnail,
    int Cheers,
    int Comments,
    int Downloads,
    bool Verified,
    [property: JsonPropertyName("topicUrl")] string? TopicUrl,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt);

/// One page of the gallery. Total/Pages let the pane show "N of M" and cap
/// paging; a past-the-end page comes back with an empty Plugins list.
public sealed record GalleryPage(
    IReadOnlyList<GalleryPlugin> Plugins,
    int Page,
    int Pages,
    int Total);

/// How the gallery is ordered. The endpoint 400s on anything else.
public enum GallerySort { Cheers, Downloads, Latest }

/// A plugin's live detail — the forum is read per request, so cheers/comments
/// reflect the moment, and Cheered is set when the caller is signed in (null
/// otherwise, or when the forum was unreachable and cached counts were used).
public sealed record PluginDetail(
    string Name,
    string? Slug,
    string? Description,
    string Url,
    string? Version,
    string? Author,
    string? Preview,
    int Cheers,
    int Comments,
    int Downloads,
    bool Verified,
    bool? Cheered,
    [property: JsonPropertyName("topicUrl")] string? TopicUrl);

public sealed record CommunityComment(
    long Id,
    string Author,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    int Likes,
    /// Raw markdown source — render a safe subset or show as plain text.
    string Text);

public sealed record CommentPage(
    IReadOnlyList<CommunityComment> Comments,
    int Page,
    int Pages,
    int Total,
    [property: JsonPropertyName("topicUrl")] string? TopicUrl);

public sealed record CheerResult(bool Cheered, int Cheers);

/// DELETE /api/plugins/<slug> — the slug the backend unlisted.
public sealed record UnpublishResult(string Slug, bool Unlisted);

/// A write that either succeeded (Value set) or carries a message to show the
/// user verbatim — the backend passes Discourse's own localized error text
/// through, so it is already in the user's forum language.
public sealed record CommunityResult<T>(T? Value, string? Error) where T : class
{
    public bool Ok => Error == null;
}

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
    ///
    /// On non-Windows the token is written as-is with owner-only (0600)
    /// permissions — the ~/.ssh treatment — until the Secret Service seam
    /// lands. The Windows format and path are unchanged.
    public static string? Token
    {
        get
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                var raw = File.ReadAllBytes(TokenPath);
                var clear = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser)
                    : raw;
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
                if (OperatingSystem.IsWindows())
                {
                    var sealed_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(TokenPath, sealed_);
                }
                else
                {
                    File.WriteAllBytes(TokenPath, Encoding.UTF8.GetBytes(value));
                    File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
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

    /// One page of the community gallery, ordered by `sort`. No auth needed.
    /// Null on a network or decode failure (the pane shows a retry).
    public static async Task<GalleryPage?> Gallery(GallerySort sort, int page = 1, int limit = 24,
                                                   string? query = null, bool verifiedOnly = false)
    {
        var key = sort switch
        {
            GallerySort.Downloads => "downloads",
            GallerySort.Latest => "latest",
            _ => "cheers",
        };
        try
        {
            var url = $"{BaseUrl}/api/store/plugins?sort={key}&page={page}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(query)) url += $"&q={Uri.EscapeDataString(query.Trim())}";
            if (verifiedOnly) url += "&verified=true";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<GalleryPage>(await response.Content.ReadAsStringAsync(), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// Live detail for one plugin (forum read per request). Sends the token
    /// when present so the response carries `cheered`. Null on failure.
    public static async Task<PluginDetail?> Detail(string slug)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/store/plugins/{Uri.EscapeDataString(slug)}");
            if (Token is { } token) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<PluginDetail>(await response.Content.ReadAsStringAsync(), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// One page of a plugin's forum comments (chronological, showcase post
    /// excluded). No auth needed.
    public static async Task<CommentPage?> Comments(string slug, int page = 1, int limit = 50)
    {
        try
        {
            var url = $"{BaseUrl}/api/store/plugins/{Uri.EscapeDataString(slug)}/comments?page={page}&limit={limit}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<CommentPage>(await response.Content.ReadAsStringAsync(), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// Toggles the signed-in user's like on the plugin's showcase topic.
    /// The backend passes Discourse's own (localized) error through — an
    /// author liking their own plugin gets a 403 to show verbatim.
    public static async Task<CommunityResult<CheerResult>> Cheer(string slug)
    {
        var token = Token;
        if (token == null) return new(null, L.T("Sign in first."));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/store/plugins/{Uri.EscapeDataString(slug)}/cheer");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { Token = null; return new(null, L.T("Your session expired — sign in again.")); }
            if (!response.IsSuccessStatusCode) return new(null, ErrorFrom(body) ?? UnreachableOr(response.StatusCode));
            return new(JsonSerializer.Deserialize<CheerResult>(body, Json), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, ex.Message);
        }
    }

    /// Posts a reply on the plugin's showcase topic as the signed-in user.
    public static async Task<CommunityResult<CommunityComment>> PostComment(string slug, string body)
    {
        var token = Token;
        if (token == null) return new(null, L.T("Sign in first."));
        var text = body.Trim();
        if (text.Length == 0) return new(null, L.T("Write something first."));
        if (text.Length > 4000) return new(null, L.T("Comments are limited to 4000 characters."));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/store/plugins/{Uri.EscapeDataString(slug)}/comments")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { body = text }), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { Token = null; return new(null, L.T("Your session expired — sign in again.")); }
            if (!response.IsSuccessStatusCode) return new(null, ErrorFrom(payload) ?? UnreachableOr(response.StatusCode));
            return new(JsonSerializer.Deserialize<CommunityComment>(payload, Json), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, ex.Message);
        }
    }

    /// A friendlier message for the codes the backend defines but doesn't put
    /// a body on: 502 means the forum itself is down.
    private static string UnreachableOr(System.Net.HttpStatusCode code) => code switch
    {
        System.Net.HttpStatusCode.BadGateway => L.T("The forum is unreachable right now — try again shortly."),
        System.Net.HttpStatusCode.Conflict => L.T("This plugin has no discussion topic yet."),
        _ => $"HTTP {(int)code}",
    };

    /// Unlists the plugin from the catalog — owner or staff (the backend
    /// decides; a 403 says "not your plugin" verbatim). Files and the forum
    /// topic stay; the gallery drops it on its next load.
    public static async Task<CommunityResult<UnpublishResult>> Unpublish(string slug)
    {
        var token = Token;
        if (token == null) return new(null, L.T("Sign in first."));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/plugins/{Uri.EscapeDataString(slug)}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { Token = null; return new(null, L.T("Your session expired — sign in again.")); }
            if (!response.IsSuccessStatusCode) return new(null, ErrorFrom(body) ?? UnreachableOr(response.StatusCode));
            return new(JsonSerializer.Deserialize<UnpublishResult>(body, Json), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, ex.Message);
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
                    thumbnailPng = request.ThumbnailPngBase64,
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
