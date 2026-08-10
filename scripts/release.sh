#!/bin/bash
#
# Builds a signed, notarized, stapled DeskLayer.dmg.
#
# Notarization is what stops macOS from quarantining the download — without
# it users see "DeskLayer is damaged and can't be opened" and have to run
# `xattr -d com.apple.quarantine`. Signing alone is not enough.
#
# One-time setup (stores an app-specific password in the keychain):
#
#   xcrun notarytool store-credentials DeskLayer \
#       --apple-id you@example.com --team-id SGZE33W2XX --password abcd-efgh-ijkl-mnop
#
# App-specific passwords come from appleid.apple.com → Sign-In and Security.
#
# Usage: scripts/release.sh [version]
#
set -euo pipefail

cd "$(dirname "$0")/.."

SCHEME="DeskLayer"
PROJECT="mac/DeskLayer/DeskLayer.xcodeproj"
NOTARY_PROFILE="${NOTARY_PROFILE:-DeskLayer}"
OUT="build/release"
APP="$OUT/DeskLayer.app"
VERSION="${1:-$(date +%Y.%m.%d)}"
DMG="$OUT/DeskLayer-$VERSION.dmg"

say() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

say "Checking the plugin API docs are in sync"
# The model that writes plugins is taught from the bundled copy; if it drifts
# from shared/spec/ the app ships instructions for an API it no longer has.
scripts/check-docs-sync.sh

say "Checking the conformance goldens match the runtime"
scripts/check-goldens.sh

say "Archiving"
rm -rf "$OUT"
mkdir -p "$OUT"
xcodebuild -project "$PROJECT" -scheme "$SCHEME" -configuration Release \
    -archivePath "$OUT/DeskLayer.xcarchive" \
    CODE_SIGN_STYLE=Automatic DEVELOPMENT_TEAM=SGZE33W2XX \
    archive | tail -3

say "Exporting with Developer ID"
# -allowProvisioningUpdates: App Groups oblige even a Developer ID build to
# carry a provisioning profile, and Xcode has to fetch/create it.
xcodebuild -exportArchive \
    -archivePath "$OUT/DeskLayer.xcarchive" \
    -exportOptionsPlist scripts/ExportOptions.plist \
    -exportPath "$OUT" -allowProvisioningUpdates | tail -3

say "Verifying the signature"
# --deep checks the embedded widget extension too.
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -dv --verbose=4 "$APP" 2>&1 | grep -E "Authority|TeamIdentifier|flags"

say "Submitting the app to Apple for notarization (a few minutes)"
ditto -c -k --keepParent "$APP" "$OUT/DeskLayer.zip"
xcrun notarytool submit "$OUT/DeskLayer.zip" \
    --keychain-profile "$NOTARY_PROFILE" --wait
rm -f "$OUT/DeskLayer.zip"

say "Stapling the ticket to the app"
# Stapling embeds the ticket so the first launch works offline.
xcrun stapler staple "$APP"

say "Building the disk image"
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "DeskLayer" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

say "Notarizing the disk image too"
# A ticket covers the exact artifact that was submitted, so the DMG needs
# its own pass — stapling one notarized only via the app's zip fails with
# "Could not find base64 encoded ticket".
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$DMG"

say "Checking Gatekeeper accepts it"
spctl -a -vvv -t install "$APP"
xcrun stapler validate "$DMG"

say "Updating the Sparkle appcast"
# generate_appcast signs each build with the private EdDSA key from the
# keychain and writes appcast.xml describing the whole directory, so keep
# older DMGs in releases/ — deleting one removes it from the feed.
GENERATE_APPCAST="$(find "$OUT/.." -name generate_appcast -path '*sparkle*' 2>/dev/null | head -1)"
if [ -z "$GENERATE_APPCAST" ]; then
    GENERATE_APPCAST="$(find ~/Library/Developer/Xcode/DerivedData -name generate_appcast \
        -path '*sparkle*' 2>/dev/null | head -1)"
fi
mkdir -p releases
cp "$DMG" releases/
if [ -n "$GENERATE_APPCAST" ]; then
    "$GENERATE_APPCAST" releases \
        --download-url-prefix "https://github.com/qiudaomao/DeskLayer/releases/download/$VERSION/"
    # One --download-url-prefix is applied to every item, so older entries
    # would be rewritten into this release's folder. Repoint each at its own.
    python3 scripts/fix_appcast_urls.py releases/appcast.xml
    cp releases/appcast.xml appcast.xml
    echo "appcast.xml updated — commit it, then upload to the $VERSION release:"
    ls releases/*"$VERSION"*.dmg releases/*.delta 2>/dev/null | sed 's/^/    /'
else
    echo "generate_appcast not found; build once so SPM checks Sparkle out, then rerun"
fi

say "Done: $DMG"
