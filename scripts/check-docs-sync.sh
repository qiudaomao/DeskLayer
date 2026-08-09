#!/bin/bash
#
# The plugin API docs exist in three places and must agree:
#
#   doc/                                  canonical, edited by hand
#   mac/DeskLayer/DeskLayer/Resources/    shipped in the app, fed to the LLM
#   ../DeskLayerPluginStore/docs/         published for plugin authors
#
# If they drift, the model is taught an API the app no longer has. The
# declarations are bundled as .txt because Xcode's synchronized groups skip
# .ts files — they never reach Contents/Resources.
#
set -euo pipefail
cd "$(dirname "$0")/.."

RES="mac/DeskLayer/DeskLayer/Resources"
STORE="../DeskLayerPluginStore/docs"
status=0

compare() { # label, a, b
    if [ ! -f "$2" ] || [ ! -f "$3" ]; then
        echo "  missing: $2 or $3"; status=1; return
    fi
    if cmp -s "$2" "$3"; then
        echo "  ok    $1"
    else
        echo "  DRIFT $1 ($2 vs $3)"; status=1
    fi
}

echo "Checking plugin API docs are in sync:"
compare "guide  → app bundle"  doc/plugin-guide.md "$RES/plugin-guide.md"
compare "d.ts   → app bundle"  doc/plugin.d.ts     "$RES/plugin-dts.txt"
if [ -d "$STORE" ]; then
    compare "guide  → store repo" doc/plugin-guide.md "$STORE/plugin-guide.md"
    compare "d.ts   → store repo" doc/plugin.d.ts     "$STORE/plugin.d.ts"
else
    echo "  skip  store repo not checked out at $STORE"
fi

if [ $status -ne 0 ]; then
    echo
    echo "Copy doc/ over the others:"
    echo "  cp doc/plugin-guide.md $RES/plugin-guide.md"
    echo "  cp doc/plugin.d.ts     $RES/plugin-dts.txt"
    echo "  cp doc/plugin-guide.md doc/plugin.d.ts $STORE/"
    exit 1
fi
