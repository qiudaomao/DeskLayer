#!/bin/bash
#
# Verifies the conformance goldens in shared/conformance/ still match what
# the app's JS runtime actually produces. Run after any change to shared/
# (prelude, fixtures) or the JS runtime; release.sh runs it before archiving.
#
# To update the goldens after an intentional contract change:
#   TEST_RUNNER_DESKLAYER_REGEN_GOLDENS=1 scripts/check-goldens.sh
# then review and commit the golden diffs.
#
set -euo pipefail
cd "$(dirname "$0")/.."

echo "Running conformance suite against shared/conformance goldens"
xcodebuild test \
    -project mac/DeskLayer/DeskLayer.xcodeproj \
    -scheme DeskLayer \
    -only-testing:DeskLayerTests/ConformanceTests \
    -quiet

# A regeneration run leaves edits behind; surface them so they get committed.
if ! git diff --quiet -- shared/conformance; then
    echo
    echo "shared/conformance goldens changed — review and commit:"
    git --no-pager diff --stat -- shared/conformance
fi
