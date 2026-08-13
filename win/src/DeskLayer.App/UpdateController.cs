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

    public UpdateController(string? feedUrl = null, string? publicKey = null, Action<string>? log = null)
    {
        var checker = new Ed25519Checker(SecurityMode.Strict, publicKey ?? PublicKey);
        sparkle = new SparkleUpdater(feedUrl ?? DefaultFeedUrl, checker)
        {
            UIFactory = new NetSparkleUpdater.UI.WinForms.UIFactory(),
            RelaunchAfterUpdate = true,
            // Without arguments NetSparkle runs the installer bare, which
            // means Inno Setup's wizard — appearing after the app has already
            // quit, so an update looked like a crash and never applied unless
            // the user found the window and clicked through it.
            CustomInstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            // Name the download from the enclosure URL, not by asking the
            // server: GitHub's release redirect doesn't answer the filename
            // probe, so NetSparkle saved the installer as an extensionless
            // GUID — and cmd cannot execute a file with no extension, so the
            // install step of the update batch silently did nothing and the
            // old version relaunched. The URL always ends in the real name.
            CheckServerFileName = false,
        };
        // NetSparkle explains itself in detail — which is the only way to see
        // why an update did nothing, since every failure reaches the user as
        // the same "you aren't connected to the internet" dialog.
        if (log != null) sparkle.LogWriter = new AppLogWriter(log);
    }

    /// Routes NetSparkle's diagnostics into the app log.
    private sealed class AppLogWriter : NetSparkleUpdater.Interfaces.ILogger
    {
        private readonly Action<string> sink;
        public AppLogWriter(Action<string> sink) => this.sink = sink;

        public void PrintMessage(string message, params object[]? arguments)
        {
            string text;
            try { text = arguments is { Length: > 0 } ? string.Format(message, arguments) : message; }
            catch (FormatException) { text = message; }
            sink($"updater: {text}");
        }
    }

    /// App version NetSparkle compares the feed against (assembly version).
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// The same version for people rather than for comparison: "1.1.6"
    /// instead of "1.1.6.0". Shown by the tray's About item and logged at
    /// startup — an update that silently failed to install was impossible to
    /// spot without the running version written down somewhere.
    public static string DisplayVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

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
