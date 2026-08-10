// Headless verification of the NetSparkle updater wiring, no network: parses
// the Windows appcast from a file with NetSparkle's own XML parser (proving
// format compatibility with the mac Sparkle feed), compares the latest
// version against a baseline, and verifies the Ed25519 signature over the
// release artifact (proving the signing chain). Not shipped; runs in CI.
//
// Usage: DeskLayer.UpdateCheck <appcast-file> <pubkey-b64> <artifact-path> <baseline-version>

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

var allPass = newer && goodSig && rejectsTamper;
Console.WriteLine(allPass ? "\nUPDATER CHAIN OK" : "\nUPDATER CHAIN FAILED");
return allPass ? 0 : 1;
