// Auto-update via NetSparkleUpdater — the Windows twin of the mac Sparkle
// wrapper. NetSparkle consumes the SAME appcast XML format and Ed25519
// signatures the mac release already uses (scripts/ + appcast.xml), so one
// release pipeline drives both platforms; Windows items live in a sibling
// appcast-win.xml with Windows installer enclosures.
//
// Checks quietly on launch; the tray "Check for updates…" forces a
// user-facing check. The Ed25519 public key gates every download — an
// unsigned or tampered installer is refused.

using System.Reflection;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

namespace DeskLayer.App;

public sealed class UpdateController : IDisposable
{
    // Base64 Ed25519 public key (SUPublicEDKey) for the Windows release
    // feed. Its private half signs every enclosure in appcast-win.xml and
    // lives outside this repo — see scripts/win/sign-artifact.sh. Changing
    // this key orphans every installed copy, which can no longer verify a
    // download: treat it as permanent.
    public const string PublicKey = "YE7CYYM3/8sQVQ4C9U4+nGyShyeGzkGWW/6AChLHcF4=";

    public const string DefaultFeedUrl =
        "https://raw.githubusercontent.com/qiudaomao/DeskLayer/main/appcast-win.xml";

    private readonly SparkleUpdater sparkle;

    public UpdateController(string? feedUrl = null, string? publicKey = null)
    {
        var checker = new Ed25519Checker(SecurityMode.Strict, publicKey ?? PublicKey);
        sparkle = new SparkleUpdater(feedUrl ?? DefaultFeedUrl, checker)
        {
            UIFactory = new NetSparkleUpdater.UI.WinForms.UIFactory(),
            RelaunchAfterUpdate = true,
        };
    }

    /// App version NetSparkle compares the feed against (assembly version).
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// Silent launch check: shows UI only if an update is available.
    public void StartQuietCheck() => sparkle.StartLoop(true, true);

    /// User-triggered check (tray menu): always shows a result.
    public async Task CheckAtUserRequest() => await sparkle.CheckForUpdatesAtUserRequest();

    /// Headless check used by tests/CI: parses the feed, verifies signatures,
    /// compares versions, and returns the result without any UI.
    public async Task<(UpdateStatus status, string? version)> CheckQuietly()
    {
        var info = await sparkle.CheckForUpdatesQuietly();
        return (info.Status, info.Updates.FirstOrDefault()?.Version?.ToString());
    }

    public void Dispose() => sparkle.Dispose();
}
