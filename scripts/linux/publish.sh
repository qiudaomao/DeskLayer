#!/bin/bash
#
# Builds the Linux distribution: a self-contained linux-x64 publish with the
# Wayland shim, packed as a portable tar.gz — and, when appimagetool is on
# PATH, an AppImage from linux/installer/AppDir. Mirrors scripts/win/publish.sh.
#
# The shim is native code: built here when a Linux toolchain is available
# (i.e. running ON Linux), otherwise reuses linux/native/desklayer-wl/prebuilt/.
#
# Usage: scripts/linux/publish.sh [version]
#
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION="${1:-0.0.0}"
OUT=build/linux
STAGE="$OUT/stage"

echo "==> publishing DeskLayer.LinuxApp $VERSION"
rm -rf "$OUT"
mkdir -p "$STAGE"
dotnet publish linux/src/DeskLayer.LinuxApp -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -p:Version="$VERSION" -o "$STAGE" | tail -1
rm -f "$STAGE"/*.pdb

echo "==> wayland shim"
if command -v wayland-scanner >/dev/null 2>&1; then
    make -C linux/native/desklayer-wl >/dev/null
    cp linux/native/desklayer-wl/libdesklayer-wl.so "$STAGE/"
elif [ -f linux/native/desklayer-wl/prebuilt/libdesklayer-wl.so ]; then
    cp linux/native/desklayer-wl/prebuilt/libdesklayer-wl.so "$STAGE/"
else
    echo "    no toolchain and no prebuilt shim — layer-shell backend will be unavailable" >&2
fi

echo "==> tarball"
TAR="$OUT/DeskLayer-$VERSION-linux-x64.tar.gz"
tar czf "$TAR" -C "$STAGE" .
echo "    $TAR"

if command -v appimagetool >/dev/null 2>&1; then
    echo "==> AppImage"
    APPDIR="$OUT/AppDir"
    cp -R linux/installer/AppDir "$APPDIR"
    mkdir -p "$APPDIR/usr/bin"
    cp "$STAGE"/* "$APPDIR/usr/bin/"
    appimagetool "$APPDIR" "$OUT/DeskLayer-$VERSION-x86_64.AppImage"
else
    echo "==> appimagetool not found — skipping AppImage (tarball is complete)"
fi

echo "==> done"
