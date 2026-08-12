// Plugin store — port of the mac PluginStore.swift. A store is a JSON catalog
// at a URL listing installable plugins; each added store is a library
// category. Catalogs are cached 24h and tried across mirror URLs (GitHub is
// unreachable from some networks). Persisted to %APPDATA%\DeskLayer\stores.json.
//
// Catalog format:
//   { "name": "Acme", "website": "...", "mirrors": [...],
//     "plugins": [ { "name": "Clock", "description": "...", "preview": "...",
//                    "url": "...Clock.js", "mirrors": [...],
//                    "version": "1.2.0", "author": "Acme" } ] }

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLayer.Core.Model;

public sealed record StorePlugin
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Preview { get; init; }
    public string Url { get; init; } = "";
    public IReadOnlyList<string>? Mirrors { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }

    // Community-store extras. Absent from ordinary catalogs (the decode is
    // lossy either way): forum likes on the plugin's showcase topic, its
    // reply count, the staff-applied verified tag, and a deep link for a
    // "Discuss" button. Synced from the forum roughly every 30 minutes.
    public int? Cheers { get; init; }
    public int? Comments { get; init; }
    public bool? Verified { get; init; }
    public string? TopicUrl { get; init; }

    /// Every download address to try, primary first.
    public IEnumerable<string> CandidateUrls => new[] { Url }.Concat(Mirrors ?? Array.Empty<string>());
}

public sealed record StoreCatalog
{
    public string Name { get; init; } = "";
    public string? Website { get; init; }
    public IReadOnlyList<string>? Mirrors { get; init; }
    public IReadOnlyList<StorePlugin> Plugins { get; init; } = Array.Empty<StorePlugin>();
}

public sealed class StoreEntry
{
    public string Url { get; init; } = "";
    public StoreCatalog? Catalog { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public List<string> Mirrors { get; set; } = new();
    public string? LastGoodUrl { get; set; }

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    /// Every catalog address to try, best-known first, no repeats.
    public IEnumerable<string> CandidateUrls()
    {
        var seen = new HashSet<string>();
        foreach (var u in new[] { LastGoodUrl, Url }.Where(u => u != null)
                     .Concat(Mirrors).Concat(Catalog?.Mirrors ?? Array.Empty<string>()))
            if (u != null && seen.Add(u)) yield return u;
    }

    public string DisplayName => Catalog?.Name
        ?? (Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url);

    public bool IsFresh(DateTimeOffset now)
    {
        if (Catalog == null || FetchedAt == null) return false;
        var age = now - FetchedAt.Value;
        return age >= TimeSpan.Zero && age < CacheLifetime;
    }
}

/// Stores suggested in the Add menu (one click instead of pasting a URL).
public sealed record PresetStore(string Name, string Url, IReadOnlyList<string> Mirrors)
{
    private const string Raw = "https://raw.githubusercontent.com/qiudaomao/DeskLayerPluginStore/main";
    private const string Cdn = "https://cdn.jsdelivr.net/gh/qiudaomao/DeskLayerPluginStore@main";

    public static readonly IReadOnlyList<PresetStore> All = new[]
    {
        new PresetStore(L.T("Official Store"), $"{Raw}/official/catalog.json", new[] { $"{Cdn}/official/catalog.json" }),
        new PresetStore(L.T("Sample Store"), $"{Raw}/samples/catalog.json", new[] { $"{Cdn}/samples/catalog.json" }),
        // User-published plugins with forum comments and cheers behind them
        // (bbs.byteplayer.app accounts; publishing lives in the inspector).
        new PresetStore(L.T("Community Store"), Community.CommunityClient.CatalogUrl, Array.Empty<string>()),
    };
}

public sealed class PluginStoreRegistry
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string StoresFile => Path.Combine(LayoutStore.DataDirectory, "stores.json");
    private static string OriginsFile => Path.Combine(LayoutStore.DataDirectory, "store-origins.json");

    private readonly Action<string> log;
    public List<StoreEntry> Stores { get; } = new();
    public event Action? DidChange;

    // Cache-busting: raw.githubusercontent.com sends max-age=300, so an edited
    // catalog would look stale for five minutes without this.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PluginStoreRegistry(Action<string> log)
    {
        this.log = log;
        Load();
    }

    // ---- persistence ----

    private void Load()
    {
        try
        {
            if (!File.Exists(StoresFile)) return;
            var entries = JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(StoresFile), Json);
            if (entries == null) return;
            foreach (var e in entries)
                Stores.Add(new StoreEntry
                {
                    Url = e.Url,
                    Catalog = e.Catalog,
                    FetchedAt = e.FetchedAt,
                    Mirrors = e.Mirrors ?? new(),
                    LastGoodUrl = e.LastGoodUrl,
                });
        }
        catch (Exception ex) { log($"store load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(LayoutStore.DataDirectory);
            var entries = Stores.Select(s => new StoredEntry
            {
                Url = s.Url,
                Catalog = s.Catalog,
                FetchedAt = s.FetchedAt,
                Mirrors = s.Mirrors,
                LastGoodUrl = s.LastGoodUrl,
            }).ToList();
            File.WriteAllText(StoresFile, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception ex) { log($"store save failed: {ex.Message}"); }
        DidChange?.Invoke();
    }

    private sealed class StoredEntry
    {
        public string Url { get; set; } = "";
        public StoreCatalog? Catalog { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
        public List<string>? Mirrors { get; set; }
        public string? LastGoodUrl { get; set; }
    }

    // ---- store management ----

    public async Task<bool> AddStore(string urlString, IReadOnlyList<string>? mirrors = null)
    {
        var trimmed = urlString.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return false;
        if (Stores.Any(s => s.Url == trimmed)) return true;
        var entry = new StoreEntry { Url = trimmed, Mirrors = mirrors?.ToList() ?? new() };
        await Fetch(entry);
        if (entry.Catalog == null) return false;
        Stores.Add(entry);
        Save();
        return true;
    }

    public void RemoveStore(string url)
    {
        Stores.RemoveAll(s => s.Url == url);
        Save();
    }

    public async Task RefreshAll(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in Stores)
            if (force || !entry.IsFresh(now))
                await Fetch(entry);
        Save();
    }

    private async Task Fetch(StoreEntry entry)
    {
        var failures = new List<string>();
        foreach (var candidate in entry.CandidateUrls())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.CacheControl = new() { NoCache = true };
                var response = await Http.SendAsync(req);
                if (!response.IsSuccessStatusCode) { failures.Add($"{candidate}: HTTP {(int)response.StatusCode}"); continue; }
                var catalog = JsonSerializer.Deserialize<StoreCatalog>(await response.Content.ReadAsStringAsync(), Json);
                if (catalog == null) { failures.Add($"{candidate}: empty"); continue; }
                entry.Catalog = catalog;
                if (catalog.Mirrors is { Count: > 0 }) entry.Mirrors = catalog.Mirrors.ToList();
                entry.LastGoodUrl = candidate;
                entry.LastError = null;
                entry.FetchedAt = DateTimeOffset.UtcNow;
                return;
            }
            catch (Exception ex) { failures.Add($"{candidate}: {ex.Message}"); }
        }
        entry.LastError = failures.Count > 0
            ? $"Couldn't reach the store (tried {failures.Count} address{(failures.Count == 1 ? "" : "es")})."
            : "No usable catalog URL.";
        log($"store {entry.Url}: {string.Join(" | ", failures)}");
    }

    // ---- install ----

    /// Downloads a store plugin into the plugins folder. Returns null on
    /// success, else an error message.
    public async Task<string?> Install(StorePlugin plugin, string storeName, string pluginsDirectory)
    {
        var lastError = "invalid plugin URL";
        foreach (var candidate in plugin.CandidateUrls)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out _)) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.CacheControl = new() { NoCache = true };
                var response = await Http.SendAsync(req);
                if (!response.IsSuccessStatusCode) { lastError = $"HTTP {(int)response.StatusCode}"; continue; }
                var source = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(source)) { lastError = "plugin body was not text"; continue; }
                if (!source.Contains("plugin.export")) { lastError = "that file doesn't define plugin.export"; continue; }

                var safeName = plugin.Name.Replace('/', '-');
                Directory.CreateDirectory(pluginsDirectory);
                File.WriteAllText(Path.Combine(pluginsDirectory, $"{safeName}.js"), source);
                RecordOrigin(safeName, storeName);
                log($"installed {safeName} from {storeName}");
                return null;
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        return lastError;
    }

    // ---- origins (pluginID → store name) ----

    public void RecordOrigin(string pluginId, string storeName)
    {
        var map = LoadOrigins();
        map[pluginId] = storeName;
        try { File.WriteAllText(OriginsFile, JsonSerializer.Serialize(map, Json)); } catch { }
    }

    public string? OriginOf(string pluginId) => LoadOrigins().GetValueOrDefault(pluginId);

    private Dictionary<string, string> LoadOrigins()
    {
        try
        {
            return File.Exists(OriginsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(OriginsFile), Json) ?? new()
                : new();
        }
        catch { return new(); }
    }
}
