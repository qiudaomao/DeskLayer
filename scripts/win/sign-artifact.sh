#!/bin/bash
# Ed25519-sign a Windows release artifact for appcast-win.xml, matching the
# Sparkle signature format the mac release already uses. Prints the base64
# signature and byte length to paste into the <enclosure> element.
#
#   scripts/win/sign-artifact.sh <ed25519-private-key.pem> <artifact>
#
# Generate the keypair once (keep the private key OUT of the repo):
#   openssl genpkey -algorithm ed25519 -out desklayer-win-ed25519.pem
#   openssl pkey -in desklayer-win-ed25519.pem -pubout -outform DER | tail -c 32 | base64
#     → the SUPublicEDKey (UpdateController.PublicKey)
set -euo pipefail
key="$1"; artifact="$2"
sig=$(openssl pkeyutl -sign -inkey "$key" -rawin -in "$artifact" | base64 | tr -d '\n')
size=$(wc -c < "$artifact" | tr -d ' ')
echo "length=$size"
echo "sparkle:edSignature=$sig"
