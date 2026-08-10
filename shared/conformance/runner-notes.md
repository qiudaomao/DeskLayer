# Conformance runner rules

These fixtures are the executable plugin-API contract. Every DeskLayer host
(macOS today, Windows later) runs them through its real JS runtime and must
produce byte-identical goldens. The macOS runner is
`mac/DeskLayer/DeskLayerTests/ConformanceTests.swift` +
`RecordingCanvas.swift`; a port reimplements these rules exactly.

## Boot

For each `<name>.js` in a suite directory (sorted by filename):

1. If `<name>.overrides.json` exists, parse it — an array of
   `{name, valueType, value}` entries — and coerce each value by its declared
   `valueType` (the same coercion the properties system uses). These are the
   persisted per-item overrides applied at boot.
2. Boot a fresh plugin instance from the fixture source with those overrides,
   exactly as production does: prelude injected first, then the fixture
   evaluated, properties parsed and overrides applied, coerced values pushed
   back into `plugin.export.properties`.

Fixtures are deterministic by construction: they must not read the clock,
`Math.random()`, the network, or anything else that varies between runs or
platforms. `Math.PI` and arithmetic are fine — IEEE 754 doubles are identical
everywhere.

## Canvas suite (`canvas/`)

The runner hands `render(ctx)` a **recording** ctx implementing the full
`CanvasJSExports` surface. It renders **two frames** with the *same* recorder
object (Canvas2D state persists across frames), appending
`{"op":"frame","index":N}` before each call. Recording rules:

- Property sets → `{"op":"set","name":<prop>,"value":<value>}`, recorded on
  every assignment even if the value is unchanged. Reads of `width`/`height`
  are **not** recorded.
- Method calls → `{"op":<method>,"args":[...]}` with arguments in call order.
- `getProp(name)` → recorded as an op; returns the property's bridged value
  (string/number/bool) or undefined when the property doesn't exist.
- `measureText(text)` → recorded as an op; returns
  `{width: 7 × <UTF-16 code-unit count of text>}` — a deterministic stub so
  goldens never depend on a platform text stack.
- The recorder performs no clamping, parsing, or validation: it records
  arguments and assigned values exactly as the JS passed them.

Golden = the canonical JSON (below) of the flat op array, plus a trailing
newline.

## Declarative suite (`declarative/`)

The runner calls the tree render path **twice** (each call resets the action
table first, exactly as production does before `render()`), parses each
returned JSON tree, and writes the canonical JSON of
`{"frames":[<tree0>,<tree1>]}` plus a trailing newline. Two frames prove
action ids restart at 1 every render and that the tree is stable.

## Canonical JSON

- Objects: keys sorted ascending by Unicode code point; no whitespace.
- Numbers: finite doubles whose value is integral and |v| < 10^15 print as
  base-10 integers (no decimal point, `-0` prints as `0`); everything else
  prints in shortest round-trip decimal form. Fixtures must avoid values that
  would print in exponent notation.
- Booleans/null: `true` / `false` / `null`.
- Strings: minimal JSON escaping — `\"`, `\\`, `\n`, `\r`, `\t`, and
  `\u00XX` (lowercase hex) for other control characters below U+0020;
  everything else verbatim UTF-8.
- File ends with a single `\n`.

## Regenerating goldens

Only the macOS runner regenerates; ports only verify.

```sh
cd mac/DeskLayer
TEST_RUNNER_DESKLAYER_REGEN_GOLDENS=1 xcodebuild test -project DeskLayer.xcodeproj \
  -scheme DeskLayer -only-testing:DeskLayerTests/ConformanceTests
```

(The `TEST_RUNNER_` prefix is how xcodebuild forwards an env var into the
test process.)

`scripts/check-goldens.sh` (run in CI and by release.sh) fails when fixtures,
prelude, or runner change without freshly regenerated goldens.
