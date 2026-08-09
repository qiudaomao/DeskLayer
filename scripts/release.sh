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

say "Submitting to Apple for notarization (a few minutes)"
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
xcrun stapler staple "$DMG"

say "Checking Gatekeeper accepts it"
spctl -a -vvv -t install "$APP"
xcrun stapler validate "$DMG"

say "Done: $DMG"
