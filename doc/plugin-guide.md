# Writing DeskLayer Plugins

A DeskLayer plugin is a single JavaScript file that draws something onto your
desktop — the wallpaper layer, a floating window, or a macOS widget. This
guide covers everything you can do.

- [Quick start](#quick-start)
- [Where plugins live](#where-plugins-live)
- [The plugin shape](#the-plugin-shape)
- [Properties](#properties)
- [Render cadence (fps / interval)](#render-cadence)
- [Canvas mode: `render(ctx)`](#canvas-mode)
- [Declarative mode: `render()` returns a view tree](#declarative-mode)
- [Timers & networking](#timers--networking)
- [Host APIs & permissions](#host-apis--permissions)
  - [`$system` — machine stats](#system--machine-stats)
  - [`shell()` — run a local command](#shell--run-a-local-command)
  - [`applescript()`](#applescript)
  - [`ssh()` — run on a remote machine](#ssh--run-on-a-remote-machine)
  - [`$server` — receive local HTTP hooks](#server--receive-local-http-hooks)
- [Per-item settings (inspector)](#per-item-settings)
- [Debugging](#debugging)
- [Reference](#reference)

---

## Quick start

Create `~/Library/Application Support/DeskLayer/Plugins/Hello.js`:

```js
let properties = [
    { name: "fps", valueType: "number", value: "30" }
];

function render(ctx) {
    ctx.clearRect(0, 0, ctx.width, ctx.height);
    ctx.fillStyle = "#4CD964";
    ctx.font = "bold 24px Helvetica";
    ctx.fillText("Hello, desktop!", 20, 60);
}

plugin.export = { properties, render };
```

Open DeskLayer, and `Hello` appears in the plugin library — drag it onto the
virtual desktop (or click the **+**). The folder is watched, so saving edits
re-scans automatically; re-add the item (or restart) to pick up code changes.

---

## Where plugins live

```
~/Library/Application Support/DeskLayer/Plugins/
    MyPlugin.js                 ← a bare script
    Fancy.deskplugin/
        main.js                 ← folder form (enables bundled image assets)
        logo.png
```

Use **Open Plugins Folder** / **Import…** at the bottom of the plugin library.
The `pluginID` is the file or folder name (without extension).

---

## The plugin shape

Every plugin assigns `plugin.export`:

```js
plugin.export = {
    properties,        // array of {name, valueType, value} — optional
    render,            // function — required
    mode,              // "canvas" | "declarative" — optional (auto-detected)
    permissions,       // ["shell", "applescript", "ssh", "server"] — optional
};
```

`render` is the only required field. Everything else is optional.

---

## Properties

Declare configurable values; users edit them live in the inspector. Each is
`{ name, valueType, value }`. `valueType` is one of `string`, `number`,
`boolean`, `color`. **Values may be written as strings** — DeskLayer coerces
them by `valueType`:

```js
let properties = [
    { name: "fps",    valueType: "number",  value: "30" },
    { name: "label",  valueType: "string",  value: "CPU" },
    { name: "tint",   valueType: "color",   value: "#4CD964FF" },
    { name: "shadow", valueType: "boolean", value: "true" }
];
```

Read them at render time. In **canvas** mode use `ctx.getProp(name)`; in either
mode you can read the live `properties` array directly:

```js
const tint = ctx.getProp("tint");                 // canvas
const label = properties.find(p => p.name === "label").value;  // any mode
```

When the user edits a property in the inspector, the change is pushed into your
running plugin immediately (no reload) — the next frame reflects it.

Two names are special and read by DeskLayer itself, not your code: **`fps`**
and **`interval`** (see below).

---

## Render cadence

`render` is called on a schedule you choose:

| Declaration | Meaning |
|---|---|
| `{ name: "fps", value: "60" }` | 60 frames/second |
| `{ name: "fps", value: "0.2" }` | fractional fps → every 5 seconds |
| `{ name: "fps", value: "0" }` | render **once** |
| `{ name: "interval", value: "5" }` | every 5 **seconds** |
| `{ name: "interval", value: "3600" }` | every hour |
| *(neither declared)* | canvas: 30fps · declarative: static (renders only when a property changes) |

`interval` (seconds) wins over `fps` if both are present. Slow items (≥1s)
don't hold a display link open — they're woken by a low-power timer, so an
hourly plugin costs almost nothing. Rendering automatically pauses when the
display sleeps, the screen locks, or a fullscreen app covers the desktop.

---

## Canvas mode

If `render` takes a `ctx` argument, it's a Canvas2D-style drawing surface.
Coordinates are in points, origin **top-left**, like the web `<canvas>`.

```js
function render(ctx) {
    const w = ctx.width, h = ctx.height;
    ctx.clearRect(0, 0, w, h);

    ctx.fillStyle = "rgba(20,22,30,0.85)";
    ctx.fillRect(0, 0, w, h);

    ctx.strokeStyle = "#5ac8fa";
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(w / 2, h / 2, 40, 0, Math.PI * 2, false);
    ctx.stroke();

    ctx.fillStyle = "white";
    ctx.font = "16px Menlo";
    const m = ctx.measureText("hi");
    ctx.fillText("hi", (w - m.width) / 2, h / 2);
}
```

Canvas content **persists between frames** (like a real canvas) — call
`clearRect` yourself, or draw a translucent full-canvas rectangle each frame
for a motion-trail effect.

### Supported `ctx` API

State: `save()`, `restore()`
Transform: `translate(x,y)`, `rotate(rad)`, `scale(x,y)`
Rects: `clearRect`, `fillRect`, `strokeRect` (each `x,y,w,h`)
Paths: `beginPath()`, `closePath()`, `moveTo(x,y)`, `lineTo(x,y)`,
`arc(x,y,r,start,end,anticlockwise)`, `fill()`, `stroke()`
Style: `fillStyle`, `strokeStyle` (CSS colors: `#rgb`, `#rrggbb`,
`#rrggbbaa`, `rgb()`, `rgba()`, named), `globalAlpha`, `lineWidth`,
`lineCap`, `lineJoin`
Text: `font` (e.g. `"bold 16px Helvetica"`), `fillText(s,x,y)`,
`measureText(s)` → `{width}`
Images (folder plugins): `drawImage(name, x, y, w, h)` — `name` is a file in
the `.deskplugin` folder
Info: `ctx.width`, `ctx.height`, `ctx.getProp(name)`

---

## Declarative mode

If `render` takes **no** argument and returns a view tree, DeskLayer renders it
as **native SwiftUI**. Great for text, layouts, and cards.

```js
let properties = [
    { name: "title", valueType: "string", value: "Hello, World!" }
];

render = () => view([
    VStack([
        Text(properties[0].value).textColor("green").fontSize(28).bold(),
        HStack([ Image("sun.max.fill"), Text("72°F") ]).spacing(8)
    ])
    .spacing(6)
    .padding(16)
    .background("#101420CC")
    .cornerRadius(14)
]);

plugin.export = { properties, render };
```

### Elements

`view([...])` (root), `VStack`, `HStack`, `ZStack`, `Text("...")`,
`Image("sf.symbol.name")`, `Spacer()`.
Aliases: `Section` = `VStack`, `Paragraph` = `Text`.
`Image` first tries an SF Symbol name, then a file in the plugin folder.

### Modifiers (chainable)

`.textColor(css)` / `.foregroundColor(css)`, `.fontSize(pt)`, `.bold()`,
`.padding(pt)`, `.background(css)`, `.cornerRadius(pt)`, `.frame(w, h)`,
`.opacity(0–1)`, `.spacing(pt)` (on stacks).

Unchanged trees are skipped automatically, so returning the same structure is
cheap. Unknown elements/modifiers render a small warning badge instead of
crashing.

> Tip: force declarative mode explicitly with `plugin.export.mode =
> "declarative"` if your `render` happens to accept an unused argument.

---

## Timers & networking

Standard-ish globals are available in every plugin (no permission needed):

```js
setTimeout(fn, ms);  setInterval(fn, ms);  clearTimeout(id);  clearInterval(id);

fetch("https://api.example.com/data")     // https only
    .then(r => r.json())
    .then(data => { /* ... */ })
    .catch(e => console.log(e.message));

const ws = new WebSocket("wss://example.com/feed");
ws.onmessage = (e) => console.log(e.data);
ws.send("hello");
```

`fetch` returns a Promise with `status`, `ok`, `headers.get(name)`, `text()`,
`json()`. All callbacks run on your plugin's own thread; a broken plugin can
only stall itself.

---

## Host APIs & permissions

Some APIs reach the machine and require opting in via `permissions`:

```js
plugin.export = { permissions: ["ssh", "server"], properties, render };
```

Call host APIs **after load** — from `render`, a timer, or a handler — not at
the top level of your file (permissions are resolved just after the script
loads). A common pattern is `setTimeout(setup, 0)`.

### `$system` — machine stats

No permission required. Read-only CPU / memory / disk / network, straight from
the OS (no shelling out):

```js
const s = $system.stats();
// s.cpu        0–1, overall usage since the last call
// s.cores      logical core count
// s.memory     { total, used, free }  (bytes)
// s.disk       { total, free }        (bytes, home volume)
// s.network    { rxBytes, txBytes }   (cumulative — diff two samples for a rate)
// s.uptime, s.thermalState
```

### `shell()` — run a local command

Permission: `"shell"`. Pass an **argv array** (no shell string → no injection):

```js
const r = await shell(["git", "-C", "/repo", "rev-parse", "HEAD"]);
// r.status (exit code), r.stdout, r.stderr
```

Destructive commands (`rm`, `dd`, `sudo`, `kill`, `mv`, …) are blocked.
Requires the non-sandboxed build (see [note](#a-note-on-the-sandbox)).

### `applescript()`

Permission: `"applescript"`.

```js
const front = await applescript('tell application "System Events" to ' +
                                'get name of first process whose frontmost is true');
```

### `ssh()` — run on a remote machine

Permission: `"ssh"`. Configure the destination in the inspector's **SSH**
section (host, port, user, and either a password or an identity key). With no
destination configured, `ssh()` rejects with a clear error.

```js
const r = await ssh(["cat", "/proc/loadavg"]);   // argv form
const r2 = await ssh("uptime");                   // string → runs in remote shell
// r.status, r.stdout, r.stderr
```

Passwords are stored in your macOS Keychain, never in `layout.json`. Key auth
uses the identity file you pick (`-i`). Requires the non-sandboxed build.

### `$server` — receive local HTTP hooks

Permission: `"server"`. DeskLayer runs **one** loopback-only listener on
`127.0.0.1:8787` and delivers each request to every plugin that registered a
handler. Perfect for hooking local tools (Claude Code, Codex, scripts):

```js
$server.on("POST", (event, body) => {
    // event.method, event.path, event.headers ; body is the raw string
    let tool = body;
    try { tool = JSON.parse(body).tool; } catch (e) {}
    console.log("hook: " + tool);
});
```

Send it something from a shell or a tool hook:

```sh
curl -X POST -d '{"tool":"Bash"}' http://127.0.0.1:8787
```

The listener is bound to loopback only — never reachable from the network.

---

## Per-item settings

Independent of your code, each placed item has inspector controls: enable,
wallpaper vs. floating window, click-through (floating), display, z-order,
frame, a **background color** (transparent by default — set opacity to 0 for a
see-through tint), and the SSH destination (when the plugin declares the `ssh`
permission).

---

## Debugging

- **Log panel** — `console.log` / `console.error` / `console.warn` show in the
  inspector's *Log* section (timestamped, live, with Clear).
- **Safari Web Inspector** — every plugin's JS context is inspectable. In
  Safari: Develop menu → your Mac → **DeskLayer:<PluginName>** for breakpoints,
  a console, and profiling.
- A plugin that throws is unscheduled and flagged in the inspector; one stuck
  in an infinite loop is caught by a watchdog after ~2s. Neither affects other
  plugins or the app.

### A note on the sandbox

`$system`, `fetch`, `WebSocket`, timers, and `$server` work in every build.
`shell()`, `applescript()`, and `ssh()` spawn processes, which macOS App
Sandbox forbids — DeskLayer ships **unsandboxed** (outside the App Store) so
these work. In a sandboxed build they return a clear error.

---

## Reference

### `plugin.export`

| Field | Type | Notes |
|---|---|---|
| `render` | function | Required. `render(ctx)` = canvas; `render()` returning a tree = declarative. |
| `properties` | array | `{ name, valueType, value }`; `valueType` ∈ string/number/boolean/color. |
| `mode` | string | Optional `"canvas"` / `"declarative"` override. |
| `permissions` | string[] | Subset of `shell`, `applescript`, `ssh`, `server`. |

### Special property names

| Name | Effect |
|---|---|
| `fps` | Frames/second (fractional; `0` = render once). |
| `interval` | Seconds between renders (wins over `fps`). |

### Globals

Always: `console`, `setTimeout`/`setInterval`/`clearTimeout`/`clearInterval`,
`fetch`, `WebSocket`, `$system`, `plugin`.
Declarative builders: `view`, `VStack`, `HStack`, `ZStack`, `Text`, `Image`,
`Spacer`, `Section`, `Paragraph`.
Permission-gated: `shell`, `applescript`, `ssh`, `$server`.

See the bundled sample plugins (installed to your Plugins folder on first run)
for complete working examples: `AnalogClock`, `Particles`, `HelloCard`,
`WeatherCard`, `FetchDemo`, `WebSocketDemo`, `SystemMonitor`, `HookBoard`,
`RemoteMonitor`.
