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
- **Spike 2 (2026-08-23, Arch + Hyprland 0.56.2): PASS.** The shim's layer
  `bottom` surface composites half-transparently above the compositor
  wallpaper and below all UI (bar, notifications); 455 frames / 0 skipped
  at ~38fps under sleep pacing; teardown restores the desktop untouched.
  Build note that made it work: layer-shell's `get_popup` references
  `xdg_popup_interface`, so the xdg-shell protocol glue must be generated
  and linked too (Makefile does this now).
- **Spike 1/3 on Hyprland (2026-08-23): X11-DESKTOP fallback is a no-go on
  wlroots compositors.** A `_NET_WM_WINDOW_TYPE_DESKTOP` XWayland window is
  managed as a normal tiled window (focus border, above the wallpaper, in
  the tiling layout). Consequence: on Wayland the backend selector must
  treat layer-shell as the only wallpaper path for the wlroots family; the
  XWayland-DESKTOP fallback is exclusively a GNOME/mutter bet and still
  needs a GNOME VM to measure. Plain-X11 sessions (spike 1 proper) also
  still need their matrix run (XFCE/KDE/GNOME Xorg).
- **Conformance also ALL GREEN on Arch** (second distro data point,
  omarchy box).
- Spike 4 and the GNOME/X11 DE matrix: pending (GNOME VM on minipve).
- **M1 walking skeleton (2026-08-23): PASS on Hyprland.** The mac's real
  `layout.json` + plugin files, copied verbatim, decode and render: two
  canvas plugins (The Clock, CPU History) on the wallpaper at their declared
  cadences, transparent over the compositor background, positioned by the
  normalized frames; declarative items correctly deferred to M2;
  DESKLAYER_DUMP_ITEM PNGs verified; clean exit restores the desktop.
  Stats read zero until the M4 $system binding lands.
- **M2 wallpaper declarative + live stats (2026-08-23): PASS.** ViewNode
  trees render through the toolkit-free Skia NodeRenderer (SystemMonitor,
  RemoteMonitor incl. its ssh error cards — the ssh binding works end to
  end); $system.stats() reads real /proc//sys data. X11 backend runs
  cleanly under XWayland (visual validation on a real X11 DE pending).
  Installed as a systemd user service on the omarchy box: 4 mac plugins
  as the live wallpaper, crash-restart verified.

## Install (current state)

`scripts/linux/publish.sh <version>` builds a self-contained tarball (and an
AppImage when appimagetool is present). On the target:

```sh
mkdir -p ~/.local/opt/desklayer && tar xzf DeskLayer-*.tar.gz -C ~/.local/opt/desklayer
# layer-shell shim (until prebuilt ships): make -C linux/native/desklayer-wl && cp the .so alongside
cp linux/installer/desklayer.service ~/.config/systemd/user/
systemctl --user enable --now desklayer
# launcher entry ("DeskLayer" in walker/rofi/GNOME opens the Manager):
printf '#!/bin/sh\nexec "%s/.local/opt/desklayer/DeskLayer.LinuxApp" --manager "$@"\n' "$HOME" \
  > ~/.local/bin/desklayer-manager && chmod +x ~/.local/bin/desklayer-manager
cp linux/installer/desklayer-manager.desktop ~/.local/share/applications/
cp linux/installer/AppDir/desklayer.png ~/.local/share/icons/hicolor/256x256/apps/
```

The service restarts the engine on failure — the Linux analog of the win
port's Explorer-restart watchdog (verified: kill -9 → back in seconds).

## Manager

`DeskLayer.LinuxApp --manager` opens the Avalonia manager — the same
three-pane structure as win/mac (win ManagerWindow is the reference):

- **Sidebar** — the Community entry, the Installed library, and one
  category per store (inline add-to-desktop / install buttons, "+" menu
  with preset stores / add-by-URL / import .js, plugins-folder button).
- **Centre** — the desktop overview: item rects over a screen-shaped
  canvas, drag to move, corner grip to resize (declared resize policy
  honored: aspect, limits, auto-size axes snap back). Selecting the
  Community entry swaps in the gallery: thumbnail tiles, sort chips,
  verified filter, debounced search, pager, device-code **sign-in**, and
  a detail dialog (full preview, live cheer toggle, comments + compose).
- **Inspector** — follows the selection kind, exactly like win: placed
  item (origin, enabled, show-as, z-order, background, frame in points
  with top-left Y, typed property editors, SSH destinations, update
  controls, remove) · installed plugin (about, capabilities, read-only
  properties, updates, add/reveal/uninstall) · store (counts, catalog
  URL, refresh, remove) · store-listed plugin (details, install,
  install-&-add).

Sign-in stores the forum token via `CommunityClient.Token`, which now
branches off-Windows to a 0600 plain file (Secret Service is the tracked
follow-up). Floating-window target still renders nowhere on Linux; the
inspector says so.

The Manager owns a StatusNotifier **tray icon** (Open Manager · Pause/
Resume Wallpaper · Restart Engine · Quit); closing the window hides it.
Pause drops a `.paused` sentinel in the data dir which the engine polls —
the wallpaper freezes on its last frame, resume is instant. Debug hooks:
`DESKLAYER_MANAGER_TAB=community`, `DESKLAYER_MANAGER_SELECT=<n>`.

Avalonia note for future UI work: assigning `null` to `Foreground`/
`Background` blanks the theme brush (invisible text) — use `ClearValue`.

It is a separate process from the engine service: both edit the
wire-format `layout.json`, which the engine watches and rebuilds from —
no IPC beyond that file and the sentinel. v1 rebuild resets JS state on
edit; in-place reconcile is the tracked follow-up.

## Backlog toward parity (next cycles)

- floating-window target (Avalonia panels), webview mode
- community sign-in (cheer/comment/publish) + LLM authoring UIs — blocked
  on the ISecretStore seam (CommunityClient.Token is DPAPI/Windows-only)
- power controller (logind), D-Bus single instance, Secret Service ISecretStore
- AppImage + NetSparkle updater on appcast-linux.xml
- in-place reconcile, multi-output, MIT-SHM & fractional-scale presentation
- GNOME Wayland / X11 DE matrix runs (spikes ready; needs VMs)
- Linux symbol/font tables (`-linux` siblings) + bundled fonts

## Platform notes

- Plugins and layout live in `~/.config/DeskLayer` (override with
  `DESKLAYER_DATA_DIR`), wire-compatible with the mac/win `layout.json`.
- Auto-update: `../appcast-linux.xml` (Sparkle format + Ed25519, shared
  pipeline) applied by atomically replacing `$APPIMAGE`.
- Dropped vs mac: `applescript()` (rejects with a clear message), the widget
  extension. Webview render mode is deferred for v1 (rejects with a message).
- Secrets: Secret Service over D-Bus (gnome-keyring / KWallet) behind the
  Core `ISecretStore` seam; plaintext file fallback with a logged warning.
