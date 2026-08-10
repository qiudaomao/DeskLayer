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

## Editor support

[`plugin.d.ts`](plugin.d.ts) declares every API in this guide as TypeScript.
Drop it next to your plugin and add one line at the top of the file:

```js
/// <reference path="./plugin.d.ts" />
```

VS Code then completes `ctx`, the view builders, and `plugin.export`, and
flags the browser and Node habits this runtime doesn't have — no `document`,
no `require`, no `fetch` options it doesn't implement. Check one plugin at a
time; TypeScript shares a single global scope across files, while each plugin
really runs in its own.

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

Use **＋ → Add Plugin…** or **Open Plugins Folder** at the bottom of the plugin
library. The `pluginID` is the file or folder name (without extension).

DeskLayer ships with no plugins. The library groups what you have into
**Installed** plus one category per plugin store you add — the ＋ menu offers
the **Official Store** and **Sample Store** by name, and **Add Plugin Store…**
takes any catalog URL.

### Publishing a plugin store

A store is a JSON catalog at a URL. Add it with **＋ → Add Plugin Store…** and it
becomes its own category; selecting a listed plugin shows its description and
preview with an **Install** button.

```json
{
  "name": "Acme Widgets",
  "url": "https://acme.example/catalog.json",
  "website": "https://acme.example",
  "mirrors": ["https://cdn.example/acme/catalog.json"],
  "plugins": [
    {
      "name": "Clock",
      "description": "A tasteful clock.",
      "preview": "https://acme.example/clock.png",
      "url": "https://acme.example/Clock.js",
      "mirrors": ["https://cdn.example/acme/Clock.js"],
      "version": "1.2.0",
      "author": "Acme"
    }
  ]
}
```

Only `name` and each plugin's `name`/`url` are required.

| Field | Where | Meaning |
| --- | --- | --- |
| `url` | catalog | The catalog's canonical address, shown in the inspector. |
| `website` | catalog | Home page; the inspector turns it into a clickable link. |
| `mirrors` | catalog, plugin | Fallback addresses, tried in order when the primary fails. |
| `preview` | plugin | Image shown in the detail pane — keep it a reasonable size. |

**Mirrors.** `raw.githubusercontent.com` is unreachable on some networks, so
DeskLayer tries the primary URL, then each mirror, and remembers whichever
answered so the next fetch starts there. A catalog's mirrors are learned once
it has been fetched successfully; plugins carry their own, since the catalog
and the scripts may live on different hosts.

**Caching.** Catalogs are cached for 24 hours. Launching shows the cached
listing immediately and only re-fetches stale stores; the **Refresh** button on
a store category always fetches now.

Selecting the store category shows its website, catalog URL, when it was last
updated, how many of its plugins are installed, **Refresh**, and **Remove
Store** (which only drops the listing — installed plugins stay). Install a
plugin straight from its row in the sidebar, or from the detail pane with
**Install** or **Install & Add to Desktop**.

---

## The plugin shape

Every plugin assigns `plugin.export`:

```js
plugin.export = {
    properties,        // array of {name, valueType, value} — optional
    render,            // function — required
    mode,              // "canvas" | "declarative" — optional (auto-detected)
    permissions,       // ["shell", "applescript", "ssh", "server"] — optional
    version,           // "1.0.0" — optional, shown in the inspector
    author,            // "You" — optional
    description,       // "What it does" — optional
    updateURL,         // "https://.../MyPlugin.js" — optional, enables updates
};
```

`render` is the only required field. Everything else is optional.

### Versioning & updates

Declare `version`, `author`, and `description` — they appear in the inspector's
**About & Updates** section. Add an `updateURL` pointing at the raw plugin `.js`
to enable updating:

```js
plugin.export = {
    version: "1.2.0",
    author: "Ada Lovelace",
    description: "A tasteful clock.",
    updateURL: "https://example.com/plugins/Clock.js",
    properties, render
};
```

- **Check for Update** (inspector button) fetches a small **manifest** — a
  JSON file next to your plugin with the same name (`Clock.js` → `Clock.json`,
  `{ "version": "1.2.0", "url": "https://…/Clock.js" }`) — compares versions
  (dotted numeric, so `1.2.10 > 1.2.9`), and downloads the `.js` body only when
  it's newer. No manifest? It falls back to fetching the `.js` directly.
- **Auto-update** (inspector toggle, remembered per plugin) checks at launch.
- Applying an update hot-reloads any running items using that plugin — no
  restart. Editing a plugin file in the folder hot-reloads it the same way.

Version your own plugins by bumping `version` and publishing the new file at the
same `updateURL`. For a `.deskplugin` folder, the `updateURL` replaces `main.js`.

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

`render` must **return** the tree. An arrow function with a concise body
(`render = () => view([...])`) does that for you; one with a block body needs
the `return` spelled out:

```js
render = () => {
    const rows = data.map(d => Text(d.name));
    return view([VStack(rows)]);   // without `return`, nothing is drawn
};
```

A missing `return` renders an empty item with no error — the tree simply
never arrives.

### Elements

Every element is a plain JS object; nothing here is real SwiftUI syntax, so
only what this table lists exists. Anything else renders a warning badge
rather than crashing.

| Builder | Renders as | Notes |
| --- | --- | --- |
| `view([...])` | root `ZStack` | The tree `render()` returns. |
| `VStack([...])` | `VStack` | Vertical. `.spacing(pt)`. Alias: `Section`. |
| `HStack([...])` | `HStack` | Horizontal. `.spacing(pt)`. |
| `ZStack([...])` | `ZStack` | Overlay, later children on top. |
| `Text("…")` | `Text` | Alias: `Paragraph`. |
| `Image(name)` | `Image` / `AsyncImage` | SF Symbol name, or an `http(s)`/`file` URL loaded asynchronously (spinner while loading, a placeholder symbol on failure). |
| `Spacer()` | `Spacer` | Pushes siblings apart. |
| `Button(label, fn?)` | `Button` (borderless) | Or `Button(label).onTap(fn)`. |
| `TextField(place, fn?)` | text field | `.value(str)` seeds the text; `fn` gets `{ text }`. |
| `Rect()` | `Rectangle` | Colour it with `.background(css)`, size with `.frame(w, h)`. |
| `Ring(to)` / `Ring(from, to)` | trimmed `Circle` | Donut gauge, 0…1, clockwise from 12 o'clock. |
| `Spinner()` | `ProgressView` | Indeterminate, small. |
| `ProgressBar(value)` | `ProgressView(value:)` | Determinate, 0…1. |
| `Video(url)` | `AVPlayer` view | `.loop(true)`, `.muted(false)`. |

A single child may be passed without an array: `VStack(Text("hi"))`.

`Rect()` is the primitive for anything you want exact colours on — bars,
rules, dividers. Pass `null` for a dimension to let it flex:
`Rect().frame(null, 1).background("#FFFFFF22")` is a full-width hairline.

Stacking `Ring(from, to)` arcs in a `ZStack` builds a **segmented** ring
entirely in JS — the `RemoteMonitor` memory ring draws used / cached / free
that way.

### Modifiers (chainable)

Modifiers apply in the order you chain them, exactly like SwiftUI: the
result of `.padding(8).background("black")` differs from
`.background("black").padding(8)`.

| Modifier | Applies to | Effect |
| --- | --- | --- |
| `.textColor(css)` | any | Foreground colour. Alias: `.foregroundColor(css)`. |
| `.fontSize(pt)` | any | System font at that size. Alias: `.font(pt)`. |
| `.bold()` | any | Bold weight. |
| `.padding(pt)` | any | Inset on all edges; `.padding()` uses the system default. |
| `.background(css)` | any | Fills behind the element. |
| `.cornerRadius(pt)` | any | Clips to a rounded rectangle. |
| `.frame(w, h)` | any | Fixed size. `null` for either axis leaves it flexible. |
| `.frame(w, h, align)` | any | `align` is `"leading"`, `"center"` (default) or `"trailing"`. |
| `.opacity(0–1)` | any | Transparency. |
| `.lineLimit(n)` | text | Maximum lines before truncation. |
| `.spacing(pt)` | stacks | Gap between children. |
| `.onTapGesture(fn)` | any | `fn` receives `{ x, y }` in local points. |
| `.onTap(fn)` | `Button` | Click handler. |
| `.onChange(fn)` | `TextField` | `fn` receives `{ text }`. |
| `.value(str)` | `TextField` | Initial / controlled text. |
| `.lineWidth(pt)` | `Ring` | Stroke thickness (default 8). |
| `.ringColor(css)` | `Ring` | Arc colour (default green). |
| `.trackColor(css)` | `Ring` | Unfilled remainder; omit for none. |
| `.loop(bool)` | `Video` | Repeat on finish. |
| `.muted(bool)` | `Video` | Muted by default; pass `false` for sound. |

Any `css` value is a CSS colour string: `"#RGB"`, `"#RRGGBB"`,
`"#RRGGBBAA"`, `"rgb(…)"`, `"rgba(…)"`, or a named colour like `"white"`.

Fixed-width frames with an alignment are how you build aligned columns —
give each row's label, value, and bar the same widths and the rows line up.

**What isn't here.** There is no `List`, `Grid`, `ScrollView`, `Toggle`,
`Slider`, `Picker`, `Divider`, `Shape`, `.overlay`, `.shadow`, `.border`,
`.rotationEffect`, or animation modifier — build what you need out of
stacks, `Rect()`, and `.frame()`. If you want something genuinely
unavailable here, canvas mode gives you arbitrary drawing.

### Sizing and placement

An item is placed by its **top-left corner**. Content is laid out top-leading
inside the item's frame, so a tree that grows taller extends downward and the
corner you positioned stays put.

By default the item keeps the size you gave it in the inspector. Declare
`autoSize` to let the content drive it instead:

```js
plugin.export = { width: 200, height: 60, autoSize: "height", properties, render };
```

`"height"` suits stacking content (a list of servers), `"width"` the
opposite, `"both"` for fully content-driven items. Only the axes you name
follow the content — the others keep the user's size, so a manual resize is
never undone on the next render. `minWidth` / `maxWidth` / `minHeight` /
`maxHeight` still bound the result.

### Interactivity (floating windows only)

Buttons, taps, and text input work **only for floating-window items** — the
wallpaper layer ignores mouse events, so interactive plugins should be shown
as a Floating Window (inspector → Show as), with click-through **off**.

```js
let count = 0;
let name = "friend";

render = () => view([
    VStack([
        Text("Hi, " + name).bold(),
        TextField("your name", (e) => { name = e.text; }).value(name),
        HStack([
            Button("−", () => { count = Math.max(0, count - 1); }),
            Text(String(count)),
            Button("+", () => { count += 1; })
        ]),
        Text("tap me").onTapGesture((e) => { count += 1; })   // e.x, e.y in points
    ])
]);
```

- `Button(label, handler)` — or `Button(label).onTap(handler)`.
- `.onTapGesture((e) => …)` on any element; `e.x` / `e.y` are local points.
- `TextField(placeholder, (e) => …)` — `e.text` is the current string;
  `.value(str)` sets the initial/controlled text.

Handlers mutate your plugin's variables; DeskLayer re-renders right after, so
the UI reflects the new state. (Your plugin holds the state and returns a fresh
tree — like React.)

Unchanged trees are skipped automatically, so returning the same structure is
cheap. Unknown elements/modifiers render a small warning badge instead of
crashing.

> Tip: force declarative mode explicitly with `plugin.export.mode =
> "declarative"` if your `render` happens to accept an unused argument.

---

## Webview mode

Set `mode: "webview"` to render a live web page — no `render` function needed.
`url`, `offsetX`, `offsetY`, and `zoom` are editable in the inspector; user
agent, headers, and cookies come from a static `webview` config.

```js
let properties = [
    { name: "url",     valueType: "string", value: "https://example.com" },
    { name: "offsetX", valueType: "number", value: "0" },
    { name: "offsetY", valueType: "number", value: "0" },   // scroll down to
    { name: "zoom",    valueType: "number", value: "1" }     // show a region
];

plugin.export = {
    mode: "webview",
    version: "1.0.0",
    properties,
    webview: {
        userAgent: "Mozilla/5.0 …",                 // optional
        headers: { "Authorization": "Bearer …" },    // optional
        cookies: [                                    // optional, seeded pre-load
            { name: "session", value: "abc", domain: "example.com", path: "/" }
        ]
    }
};
```

`offsetX`/`offsetY` scroll the page after load, and the item frame clips the
rest — so you can frame just the part of a page you want (a dashboard panel, a
live chart). Editing url/offset/zoom in the inspector reloads the view. The
background color setting shows through transparent pages.

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

Permission: `"ssh"`. Configure destinations in the inspector's **SSH
Destinations** section. The quickest setup is a **`~/.ssh/config` alias** — pick
it from the menu and ssh resolves the real host, user, port, and identity for
you. Otherwise fill in host/user/port and choose a password or identity key.

```js
const r  = await ssh(["cat", "/proc/loadavg"]);   // argv form (exec-like)
const r2 = await ssh("uptime | head -1");          // string → remote shell
// r.status, r.stdout, r.stderr
```

The argv form is shell-quoted for you, so `ssh(["sh", "-c", script])` delivers
`script` intact no matter what it contains. A string is passed through for the
remote shell to interpret.

**Multiple servers.** An item can hold several destinations, each with a name.
`$ssh.hosts` lists them, and a second argument picks one — so one plugin can
render a stack of machines:

```js
$ssh.hosts.forEach(name => {
    ssh(["uptime"], name).then(r => { results[name] = r.stdout; });
});
```

With no destination configured, `ssh()` rejects with a clear error. Passwords
live in your macOS Keychain, never in `layout.json`. Requires the non-sandboxed
build. See the `RemoteMonitor` sample: it probes Linux **and** macOS hosts and
renders one block per server.

### `$server` — receive local HTTP hooks

Permission: `"server"`. DeskLayer runs **one** loopback-only listener on
`127.0.0.1:8787` and delivers each request to every plugin that registered a
handler. Perfect for hooking local tools (Claude Code, Codex, scripts). The
listener only exists while a plugin with the `server` permission is running,
and the port is configurable if 8787 conflicts with something else: set the
`DESKLAYER_HOOK_PORT` environment variable, or on macOS
`defaults write com.qiudaomao.DeskLayer DeskLayer.hookPort <port>`, or on
Windows `"hookPort": <port>` in `%APPDATA%\DeskLayer\settings.json`
(restart the app after changing it):

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

**Frames are stored as a percentage of the screen**, not absolute points, so an
item keeps its place and proportion when the resolution changes or it moves
between displays of different sizes. Resizing on the canvas **preserves the
item's aspect ratio** by default; hold **Shift** while dragging the corner to
resize width and height independently.

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
| `version` | string | Optional, e.g. `"1.2.0"`; shown in inspector, drives updates. |
| `author` | string | Optional; shown in inspector. |
| `description` | string | Optional; shown in inspector. |
| `updateURL` | string | Optional; URL of the latest `.js`, enables update checks. |
| `width`, `height` | number | Optional preferred size in points. A newly added item adopts this aspect so its rect matches the content. |
| `resizable` | bool | Optional (default true). `false` hides the resize handle. |
| `scaleMode` | string | `"ratio"` keeps the aspect while resizing, `"free"` lets width and height move independently. Default: ratio when `width`/`height` are declared, free otherwise. Shift inverts it while dragging. |
| `minWidth`, `maxWidth`, `minHeight`, `maxHeight` | number | Optional size limits in points, enforced for drags, inspector edits, and auto-sizing alike. |
| `autoSize` | string | `"height"`, `"width"`, or `"both"` — those axes follow the rendered content instead of the frame you set. Default none, so a manual resize is never undone. Use `"height"` for stacking content (see `RemoteMonitor`, whose height follows the number of servers). |

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
