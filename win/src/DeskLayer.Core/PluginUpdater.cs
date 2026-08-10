// Per-plugin updater — port of the mac PluginUpdater.swift. Reads a plugin's
// declared updateURL + version, fetches a small sibling manifest (Clock.js →
// Clock.json holding {version, url}), and overwrites the .js only when the
// remote version is newer. Falls back to fetching the .js directly. Per-plugin
// auto-update is a preference (auto-update.json in the data dir).

using System.Text.Json;
using DeskLayer.Core.Model;

namespace DeskLayer.Core;

public enum UpdateOutcome { UpToDate, Updated, NoUpdateUrl, Failed }

public readonly record struct UpdateResult(UpdateOutcome Outcome, string Message);

public sealed class PluginUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string AutoFile => Path.Combine(LayoutStore.DataDirectory, "auto-update.json");

    private readonly Action<string> log;
    public PluginUpdater(Action<string> log) => this.log = log;

    // ---- auto-update preference ----

    public bool IsAutoUpdate(string pluginId) => LoadAuto().Contains(pluginId);

    public void SetAutoUpdate(string pluginId, bool on)
    {
        var set = LoadAuto();
        if (on) set.Add(pluginId); else set.Remove(pluginId);
        try { File.WriteAllText(AutoFile, JsonSerializer.Serialize(set)); } catch { }
    }

    private HashSet<string> LoadAuto()
    {
        try
        {
            return File.Exists(AutoFile)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(AutoFile)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    // ---- update check ----

    /// `installedSource` is read for version + updateURL; `destination` is
    /// where a newer body writes.
    public async Task<UpdateResult> Check(string pluginId, string installedSource, string destination)
    {
        var (localVersion, updateUrlString) = PluginMetadata.Extract(installedSource);
        if (string.IsNullOrEmpty(updateUrlString) || !Uri.TryCreate(updateUrlString, UriKind.Absolute, out var updateUrl))
            return new(UpdateOutcome.NoUpdateUrl, "No update URL declared");
        localVersion ??= "0";

        // 1) Manifest first (small JSON).
        var manifest = await FetchManifest(updateUrl);
        if (manifest != null)
        {
            if (CompareVersions(manifest.Version, localVersion) <= 0)
                return new(UpdateOutcome.UpToDate, $"Up to date ({localVersion})");
            var bodyUrl = !string.IsNullOrEmpty(manifest.Url) && Uri.TryCreate(manifest.Url, UriKind.Absolute, out var u)
                ? u : BodyUrl(updateUrl);
            return await Download(bodyUrl, destination, localVersion, manifest.Version, pluginId);
        }

        // 2) Fallback: fetch the .js and read its declared version.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, updateUrl);
            req.Headers.CacheControl = new() { NoCache = true };
            var response = await Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return new(UpdateOutcome.Failed, $"Update failed: HTTP {(int)response.StatusCode}");
            var remoteSource = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(remoteSource)) return new(UpdateOutcome.Failed, "Update failed: response was not text");
            var (remoteVersion, _) = PluginMetadata.Extract(remoteSource);
            remoteVersion ??= "0";
            if (CompareVersions(remoteVersion, localVersion) <= 0)
                return new(UpdateOutcome.UpToDate, $"Up to date ({localVersion})");
            File.WriteAllText(destination, remoteSource);
            log($"updated {pluginId} {localVersion} → {remoteVersion}");
            return new(UpdateOutcome.Updated, $"Updated {localVersion} → {remoteVersion}");
        }
        catch (Exception ex) { return new(UpdateOutcome.Failed, $"Update failed: {ex.Message}"); }
    }

    private sealed class Manifest { public string Version { get; set; } = "0"; public string? Url { get; set; } }

    private static Uri ManifestUrl(Uri updateUrl) =>
        updateUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? updateUrl
            : new Uri(updateUrl.AbsoluteUri[..^Path.GetExtension(updateUrl.AbsolutePath).Length] + ".json");

    private static Uri BodyUrl(Uri updateUrl) =>
        updateUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? new Uri(updateUrl.AbsoluteUri[..^5] + ".js")
            : updateUrl;

    private async Task<Manifest?> FetchManifest(Uri updateUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl(updateUrl));
            req.Headers.CacheControl = new() { NoCache = true };
            var response = await Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<Manifest>(await response.Content.ReadAsStringAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private async Task<UpdateResult> Download(Uri url, string destination, string from, string to, string pluginId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.CacheControl = new() { NoCache = true };
            var response = await Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return new(UpdateOutcome.Failed, $"Update failed: HTTP {(int)response.StatusCode}");
            var source = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(source)) return new(UpdateOutcome.Failed, "Update failed: plugin body was not text");
            File.WriteAllText(destination, source);
            log($"updated {pluginId} {from} → {to} (manifest)");
            return new(UpdateOutcome.Updated, $"Updated {from} → {to}");
        }
        catch (Exception ex) { return new(UpdateOutcome.Failed, $"Update failed: {ex.Message}"); }
    }

    /// Dotted-numeric compare (1.2.10 > 1.2.9). Missing components are 0.
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var na = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var nb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }
}
