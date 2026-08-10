#!/bin/bash
# Publish DeskLayer for Windows as a self-contained single-file exe, then
# build the installer with Inno Setup (iscc must be on PATH).
#
#   scripts/win/publish.sh [output-dir]
set -euo pipefail
cd "$(dirname "$0")/../../win"
OUT="${1:-../desklayer-dist}"

dotnet publish src/DeskLayer.App/DeskLayer.App.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT"

echo "published to $OUT"
if command -v iscc >/dev/null 2>&1; then
    iscc installer/DeskLayer.iss
    echo "installer built (see installer output)"
else
    echo "Inno Setup (iscc) not found — install it to build the installer from installer/DeskLayer.iss"
fi
