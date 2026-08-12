#!/bin/bash
# Sign appcast-win.xml itself.
#
# NetSparkle in SecurityMode.Strict does not just verify each enclosure — it
# fetches "<appcast-url>.signature" and verifies the FEED before reading a
# single item. Without that file every check fails with "Signature check of
# appcast failed", which the UI reports as "either you aren't connected to
# the internet, or our server is having a problem". Mac Sparkle has no such
# requirement, so this step exists only on the Windows side.
#
# Re-run this after ANY edit to appcast-win.xml: the signature covers the
# exact bytes served, so a one-character change invalidates it.
#
#   scripts/win/sign-appcast.sh <ed25519-private-key.pem>
#
set -euo pipefail
cd "$(dirname "$0")/../.."

key="${1:-$HOME/.desklayer/desklayer-win-ed25519.pem}"
feed="appcast-win.xml"
out="$feed.signature"

[ -f "$key" ] || { echo "private key not found: $key" >&2; exit 1; }

# No trailing newline: the file's whole content is the signature.
openssl pkeyutl -sign -inkey "$key" -rawin -in "$feed" | base64 | tr -d '\n' > "$out"

echo "signed $feed -> $out"
echo "  $(cat "$out")"
