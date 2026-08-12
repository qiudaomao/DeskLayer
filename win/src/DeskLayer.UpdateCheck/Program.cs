// Headless verification of the NetSparkle updater wiring: parses the Windows
// appcast with NetSparkle's own XML parser (proving format compatibility with
// the mac Sparkle feed), compares the latest version against a baseline, and
// verifies the Ed25519 signature over the release artifact (proving the
// signing chain). Not shipped; runs in CI.
//
// Pass --live <feed-url> as well to run the check a real client runs, over
// the network. That half matters: NetSparkle in Strict mode verifies the FEED
// against "<feed-url>.signature" before reading any item, and a missing one
// shipped unnoticed through a release because the file-based checks below
// never fetch anything.
//
// Usage: DeskLayer.UpdateCheck <appcast-file> <pubkey-b64> <artifact-path> <baseline-version>
//                              [--live <feed-url>]

using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

if (args.Length < 4)
{
    Console.WriteLine("usage: DeskLayer.UpdateCheck <appcast-file> <pubkey-b64> <artifact-path> <baseline-version>");
    return 2;
}
var (appcastFile, pubKey, artifactPath, baseline) = (args[0], args[1], args[2], args[3]);

var checker = new Ed25519Checker(SecurityMode.Strict, pubKey);

// 1. Parse the mac-format Sparkle appcast with NetSparkle's own parser.
var generator = new XMLAppCastGenerator();
var appcast = generator.DeserializeAppCastFromFile(appcastFile);
var latest = appcast.Items.OrderByDescending(i => new Version(i.ShortVersion ?? i.Version ?? "0")).FirstOrDefault();
var latestVersion = latest?.ShortVersion ?? latest?.Version;
Console.WriteLine($"parsed {appcast.Items.Count} appcast item(s); latest = {latestVersion ?? "(none)"}");

// 2. Version compare against the running baseline.
var newer = latestVersion != null && new Version(latestVersion) > new Version(baseline);
Console.WriteLine($"version-compare ({latestVersion} > {baseline}): {(newer ? "PASS (update available)" : "FAIL")}");

// 3. Ed25519 signature over the artifact (Strict mode → a bad sig fails).
var sig = latest?.DownloadSignature;
var artifactBytes = File.Exists(artifactPath) ? File.ReadAllBytes(artifactPath) : Array.Empty<byte>();
var goodSig = sig != null && artifactBytes.Length > 0
    && checker.VerifySignature(sig, artifactBytes) == ValidationResult.Valid;
Console.WriteLine($"signature over artifact: {(goodSig ? "PASS (valid)" : "FAIL")}");

// 4. Tamper check: flip a byte → signature must be rejected.
var tampered = (byte[])artifactBytes.Clone();
if (tampered.Length > 0) tampered[0] ^= 0xFF;
var rejectsTamper = sig != null && checker.VerifySignature(sig, tampered) != ValidationResult.Valid;
Console.WriteLine($"tampered artifact rejected: {(rejectsTamper ? "PASS" : "FAIL")}");

// 5. The live path: fetch the feed the way a client does. Strict mode
//    verifies the feed's own signature first, so this catches a missing or
//    stale <feed>.signature — which no local check can see.
var livePass = true;
var liveIndex = Array.IndexOf(args, "--live");
if (liveIndex >= 0 && liveIndex + 1 < args.Length)
{
    var feedUrl = args[liveIndex + 1];
    Console.WriteLine($"\nlive check against {feedUrl}");
    var sparkle = new SparkleUpdater(feedUrl, new Ed25519Checker(SecurityMode.Strict, pubKey));
    try
    {
        var info = await sparkle.CheckForUpdatesQuietly();
        var offered = info.Updates?.FirstOrDefault();
        livePass = info.Status == UpdateStatus.UpdateAvailable && offered != null;
        Console.WriteLine($"  status: {info.Status}" + (offered != null ? $" -> {offered.ShortVersion ?? offered.Version}" : ""));
        if (!livePass)
            Console.WriteLine("  (a valid feed signature is required in Strict mode: scripts/win/sign-appcast.sh)");
    }
    catch (Exception ex)
    {
        livePass = false;
        Console.WriteLine($"  threw: {ex.GetType().Name}: {ex.Message}");
    }
    Console.WriteLine($"live feed accepted and update offered: {(livePass ? "PASS" : "FAIL")}");
}

var allPass = newer && goodSig && rejectsTamper && livePass;
Console.WriteLine(allPass ? "\nUPDATER CHAIN OK" : "\nUPDATER CHAIN FAILED");
return allPass ? 0 : 1;
