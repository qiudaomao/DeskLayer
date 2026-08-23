# DeskLayer for Linux

Third port, after macOS (`mac/`, Swift) and Windows (`win/`, C#/.NET). Built
against the shared plugin contract in `../shared/` — the conformance suite
there runs on every platform and must stay byte-identical.

Stack: **Avalonia + .NET 8**, reusing `win/src/DeskLayer.Core` (model, Jint JS
runtime, store/community/LLM clients) via a cross-directory project reference.
The wallpaper layer is deliberately **not** Avalonia: raw X11 / Wayland
surfaces are fed software-rendered Skia pixels, because Wayland layer-shell
surfaces can't be Avalonia windows. Avalonia hosts the Manager, dialogs, and
floating panels only.

Full plan: see the approved port plan (milestones M0–M6). Current state: M0.

## Wallpaper backends

| Session | Backend | Mechanism |
| --- | --- | --- |
| Wayland with `zwlr_layer_shell_v1` (KDE, wlroots) | `layer-shell` | layer `bottom`, transparent above the compositor wallpaper, below windows; `wl_shm` double buffer via the C shim in `native/desklayer-wl/` |
| GNOME Wayland (no layer-shell for apps) | `x11` via XWayland | `_NET_WM_WINDOW_TYPE_DESKTOP` window; stacking behavior is what spike 3 measures |
| Plain X11 | `x11` | same DESKTOP-type window, presented with `XShmPutImage` (`XPutImage` fallback) |

Override with `DESKLAYER_WALLPAPER_BACKEND=x11|layer-shell|auto`.

## M0 spikes (`spike/`)

Throwaway probes proving the risky primitives, in the tradition of
`win/spike/`. Run each on the VM matrix (GNOME Wayland / KDE Wayland /
XFCE X11; sway and GNOME Xorg extended) and record findings here.

1. `X11DesktopWindow` — DESKTOP-type window stacking per DE (also run under
   XWayland inside GNOME Wayland for spike 3).
2. `LayerShellShim` — the C shim presenting an animated gradient on layer
   `bottom` at 60fps.
3. (spike 1 binary, GNOME Wayland session) — mutter's treatment of a
   DESKTOP-type XWayland window. Go/no-go for GNOME Wayland wallpaper mode;
   the fallback is floating-only with a Manager banner.
4. `AvaloniaHintProbe` — can an Avalonia X11 window be retyped to DESKTOP
   after show (informative only; the raw-surface path doesn't depend on it).
5. `ConformanceOnLinux` — the shared golden suite through the real Jint
   runtime on Linux.

### Findings

- **Spike 5 (2026-08-23): ALL GREEN — 50/50 fixtures byte-identical** on
  Linux x86_64 (Debian LXC, kernel 6.8, .NET 8 self-contained publish of
  `win/src/DeskLayer.Conformance`, zero code changes). The JS contract
  (Jint + RecordingCanvas + CanonicalJson) holds on Linux as-is.
  Gotcha for anyone repeating it from a Mac: `COPYFILE_DISABLE=1` when
  tar-ing fixtures, or AppleDouble `._*.js` files show up as bogus failures.
- **Shim build (2026-08-23):** `native/desklayer-wl` compiles clean on Linux
  (gcc -Wall -Wextra, wayland-scanner from the fetched protocol XML). Runtime
  behavior still needs a Wayland session (spike 2).
- Spikes 1–4: code ready and compiling; runs pending the desktop VM matrix
  on minipve.

## Platform notes

- Plugins and layout live in `~/.config/DeskLayer` (override with
  `DESKLAYER_DATA_DIR`), wire-compatible with the mac/win `layout.json`.
- Auto-update: `../appcast-linux.xml` (Sparkle format + Ed25519, shared
  pipeline) applied by atomically replacing `$APPIMAGE`.
- Dropped vs mac: `applescript()` (rejects with a clear message), the widget
  extension. Webview render mode is deferred for v1 (rejects with a message).
- Secrets: Secret Service over D-Bus (gnome-keyring / KWallet) behind the
  Core `ISecretStore` seam; plaintext file fallback with a logged warning.
