// Where the "Create Plugin" feature sends its requests — the Windows twin of
// the mac LLMSettings. Any OpenAI-compatible endpoint works — the base URL is
// the only thing that changes between OpenAI, DeepSeek, Moonshot, OpenRouter,
// Ollama and LM Studio.
//
// The API key is deliberately absent from the JSON: it is DPAPI-encrypted in
// its own file (the mac keeps it in the login Keychain). A key in llm.json
// would be a key on disk in the clear.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskLayer.Core.Model;

namespace DeskLayer.Core.Llm;

public sealed class LlmSettings
{
    /// Everything before `/chat/completions`. Trailing slashes are tolerated.
    public string BaseUrl = "https://api.openai.com/v1";
    public string Model = "gpt-4o";
    /// How many times the model may call tools before the run is stopped.
    /// A confused model can otherwise read files forever.
    public int MaxTurns = 12;
    /// Models last fetched from the endpoint. Kept so the picker is populated
    /// on the next launch without asking the server again — the list only
    /// changes when the user fetches it.
    public List<string> CachedModels = new();

    /// `{baseURL}/chat/completions`, however the user typed the base.
    public Uri? CompletionsUrl
    {
        get
        {
            var trimmed = BaseUrl.Trim().Trim('/');
            if (trimmed.Length == 0) return null;
            // A base URL that already names the endpoint is used as given, so
            // pasting the full URL from a provider's docs works too.
            var full = trimmed.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? trimmed : trimmed + "/chat/completions";
            return Uri.TryCreate(full, UriKind.Absolute, out var url) ? url : null;
        }
    }

    /// `{baseURL}/models` — the OpenAI-compatible listing endpoint.
    public Uri? ModelsUrl
    {
        get
        {
            var trimmed = BaseUrl.Trim().Trim('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.Ordinal))
                trimmed = trimmed[..^"/chat/completions".Length].TrimEnd('/');
            if (trimmed.Length == 0) return null;
            return Uri.TryCreate(trimmed + "/models", UriKind.Absolute, out var url) ? url : null;
        }
    }

    public bool IsConfigured => CompletionsUrl != null && Model.Trim().Length > 0;

    // MARK: - Persistence (llm.json in the data directory)

    private static string SettingsPath => Path.Combine(LayoutStore.DataDirectory, "llm.json");
    private static string KeyPath => Path.Combine(LayoutStore.DataDirectory, "llm-key.bin");

    /// Every field optional on the way in, so settings written by an older
    /// build still load after a field is added.
    public static LlmSettings Load()
    {
        var settings = new LlmSettings();
        try
        {
            if (!File.Exists(SettingsPath)) return settings;
            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject root) return settings;
            settings.BaseUrl = root["baseURL"]?.GetValue<string>() ?? settings.BaseUrl;
            settings.Model = root["model"]?.GetValue<string>() ?? settings.Model;
            settings.MaxTurns = root["maxTurns"]?.GetValue<int>() ?? settings.MaxTurns;
            if (root["cachedModels"] is JsonArray models)
                settings.CachedModels = models
                    .Select(m => (m as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                    .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return settings;
    }

    public void Save()
    {
        var root = new JsonObject
        {
            ["baseURL"] = BaseUrl,
            ["model"] = Model,
            ["maxTurns"] = MaxTurns,
            ["cachedModels"] = new JsonArray(CachedModels.Select(m => (JsonNode)m).ToArray()),
        };
        try
        {
            Directory.CreateDirectory(LayoutStore.DataDirectory);
            File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// DPAPI (current user), never plain JSON — the Windows stand-in for the
    /// mac's login Keychain. Null/empty clears the stored key.
    ///
    /// On non-Windows the key is written as-is with owner-only (0600)
    /// permissions — the same treatment as CommunityClient.Token, pending
    /// the Secret Service seam. The Windows format and path are unchanged.
    public static string? ApiKey
    {
        get
        {
            try
            {
                if (!File.Exists(KeyPath)) return null;
                var raw = File.ReadAllBytes(KeyPath);
                var clear = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser)
                    : raw;
                var key = Encoding.UTF8.GetString(clear);
                return key.Length == 0 ? null : key;
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
                    File.Delete(KeyPath);
                    return;
                }
                Directory.CreateDirectory(LayoutStore.DataDirectory);
                if (OperatingSystem.IsWindows())
                {
                    var sealed_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(KeyPath, sealed_);
                }
                else
                {
                    File.WriteAllBytes(KeyPath, Encoding.UTF8.GetBytes(value));
                    File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { }
        }
    }
}
