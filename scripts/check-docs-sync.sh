#!/bin/bash
#
# The plugin API contract exists in several places and must agree:
#
#   shared/spec/                          canonical docs, edited by hand
#   shared/runtime/prelude.js             canonical declarative-builder JS
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

echo "Checking plugin API contract is in sync:"
compare "guide   → app bundle" shared/spec/plugin-guide.md "$RES/plugin-guide.md"
compare "d.ts    → app bundle" shared/spec/plugin.d.ts     "$RES/plugin-dts.txt"
compare "prelude → app bundle" shared/runtime/prelude.js   "$RES/prelude.js"
if [ -d "$STORE" ]; then
    compare "guide   → store repo" shared/spec/plugin-guide.md "$STORE/plugin-guide.md"
    compare "d.ts    → store repo" shared/spec/plugin.d.ts     "$STORE/plugin.d.ts"
else
    echo "  skip  store repo not checked out at $STORE"
fi

if [ $status -ne 0 ]; then
    echo
    echo "Copy shared/ over the others:"
    echo "  cp shared/spec/plugin-guide.md $RES/plugin-guide.md"
    echo "  cp shared/spec/plugin.d.ts     $RES/plugin-dts.txt"
    echo "  cp shared/runtime/prelude.js   $RES/prelude.js"
    echo "  cp shared/spec/plugin-guide.md shared/spec/plugin.d.ts $STORE/"
    exit 1
fi
