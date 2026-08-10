// Type declarations for the DeskLayer plugin runtime.
//
// This describes exactly what the app injects into a plugin's JavaScript
// context — nothing more. The runtime is JavaScriptCore, not a browser and
// not Node: there is no DOM, no `window`, no `document`, no `require`, no
// `process`, and no file system access beyond the host APIs below.
//
// Usage (plain JS with editor checking):
//
//     /// <reference path="./plugin.d.ts" />
//
// or in a tsconfig: "include": ["plugin.d.ts", "MyPlugin.js"]
//
// Check one plugin at a time. Each plugin runs in its own JSContext, but
// TypeScript puts every file in one global scope, so two plugins that both
// declare `properties` look like a redeclaration to it.
//
// Writing `function render(ctx) {…}` instead of `render = (ctx) => {…}`
// collides with the `render` declared at the bottom of this file. Either
// style runs; only the function form reports a duplicate here.
//
// Guide: https://github.com/qiudaomao/DeskLayerPluginStore/blob/main/docs/plugin-guide.md

declare global {
  // ───────────────────────────────────────────────────────────── properties

  /**
   * The types the inspector knows how to edit. `(string & {})` keeps the
   * list as suggestions rather than a restriction: a plugin's properties are
   * usually a standalone array, which widens these to plain strings, and the
   * app treats anything it doesn't recognise as "string" regardless.
   */
  type PropertyValueType =
    | "string"
    | "number"
    | "boolean"
    | "bool"
    | "color"
    | (string & {});

  /** A value a plugin exposes to the inspector. */
  interface PluginProperty {
    name: string;
    /**
     * Decides how `value` is coerced — always by this, never by the JSON
     * type, so `{ valueType: "number", value: "30" }` is a number.
     */
    valueType: PropertyValueType;
    /** Declared as a string or a native value; the app coerces it. */
    value: string | number | boolean;
  }

  // ─────────────────────────────────────────────────────────── canvas mode

  /**
   * Canvas 2D subset, backed by CoreGraphics. Origin is top-left, y grows
   * downward. Only the members here exist — no gradients, no patterns, no
   * shadows, no bezier curves, no clipping, no image data.
   */
  interface CanvasContext {
    /** CSS colour: "#RGB", "#RRGGBB", "#RRGGBBAA", "rgb()", "rgba()", or a name. */
    fillStyle: string;
    strokeStyle: string;
    lineWidth: number;
    lineCap: "butt" | "round" | "square";
    lineJoin: "miter" | "round" | "bevel";
    /** 0…1, multiplies everything drawn after it. */
    globalAlpha: number;
    /** "<size>px <PostScript font name>", e.g. "13px HelveticaNeue-Medium". */
    font: string;

    /** Item size in points. Read-only; the frame comes from the inspector. */
    readonly width: number;
    readonly height: number;

    save(): void;
    restore(): void;
    translate(x: number, y: number): void;
    /** Radians. */
    rotate(angle: number): void;
    scale(x: number, y: number): void;

    clearRect(x: number, y: number, w: number, h: number): void;
    fillRect(x: number, y: number, w: number, h: number): void;
    strokeRect(x: number, y: number, w: number, h: number): void;

    beginPath(): void;
    closePath(): void;
    moveTo(x: number, y: number): void;
    lineTo(x: number, y: number): void;
    /** Angles in radians; `anticlockwise` is required, unlike the browser. */
    arc(
      x: number,
      y: number,
      radius: number,
      startAngle: number,
      endAngle: number,
      anticlockwise: boolean
    ): void;
    fill(): void;
    stroke(): void;

    fillText(text: string, x: number, y: number): void;
    /** Width only — no ascent/descent metrics. */
    measureText(text: string): { width: number };

    /** Image file sitting next to the plugin (.deskplugin folder form). */
    drawImage(name: string, x: number, y: number, w: number, h: number): void;

    /** Current value of a declared property, after inspector overrides. */
    getProp(name: string): string | number | boolean | null;
  }

  // ────────────────────────────────────────────────────── declarative mode

  /**
   * A node in the view tree. Modifiers return the same node, so they chain.
   * Every modifier is accepted on every node; ones that don't apply to a
   * given element are ignored.
   */
  interface ViewNode {
    /** Alias: foregroundColor. CSS colour string. */
    textColor(css: string): ViewNode;
    foregroundColor(css: string): ViewNode;
    /** Alias: font. Points. */
    fontSize(points: number): ViewNode;
    font(points: number): ViewNode;
    bold(): ViewNode;
    /** All edges. Omit the argument for the system default inset. */
    padding(points?: number): ViewNode;
    background(css: string): ViewNode;
    cornerRadius(points: number): ViewNode;
    /** `null` leaves that axis flexible. */
    frame(
      width: number | null,
      height: number | null,
      alignment?: "leading" | "center" | "trailing"
    ): ViewNode;
    opacity(value: number): ViewNode;
    /** Text only. */
    lineLimit(lines: number): ViewNode;
    /** Stacks only: gap between children. */
    spacing(points: number): ViewNode;

    /** Ring only. */
    lineWidth(points: number): ViewNode;
    ringColor(css: string): ViewNode;
    trackColor(css: string): ViewNode;

    /** TextField only: initial / controlled text. */
    value(text: string): ViewNode;
    /** Video only. */
    loop(enabled: boolean): ViewNode;
    muted(enabled: boolean): ViewNode;

    /**
     * Interaction reaches floating-window items only — the wallpaper layer
     * ignores mouse events.
     */
    onTapGesture(handler: (event: { x: number; y: number }) => void): ViewNode;
    /** Button only. */
    onTap(handler: () => void): ViewNode;
    /** TextField only. */
    onChange(handler: (event: { text: string }) => void): ViewNode;
  }

  type ViewChildren = ViewNode | ViewNode[];

  /** Root of the tree. `render()` must **return** this. */
  function view(children: ViewChildren): ViewNode;

  function VStack(children: ViewChildren): ViewNode;
  function HStack(children: ViewChildren): ViewNode;
  function ZStack(children: ViewChildren): ViewNode;
  function Text(text: string | number): ViewNode;
  /** SF Symbol name, or an http(s)/file URL loaded asynchronously. */
  function Image(nameOrURL: string): ViewNode;
  function Spacer(): ViewNode;
  function Button(label: string, handler?: () => void): ViewNode;
  /** Plain rectangle: size with .frame(), colour with .background(). */
  function Rect(): ViewNode;
  /** Ring(to) draws 0…to; Ring(from, to) draws one arc segment. */
  function Ring(to: number): ViewNode;
  function Ring(from: number, to: number): ViewNode;
  function Spinner(): ViewNode;
  /** value 0…1. */
  function ProgressBar(value: number): ViewNode;
  function TextField(
    placeholder: string,
    onChange?: (event: { text: string }) => void
  ): ViewNode;
  function Video(url: string): ViewNode;

  /** javascript-ui style aliases. */
  const Section: typeof VStack;
  const Paragraph: typeof Text;

  // ───────────────────────────────────────────────────────────── host APIs

  interface SystemStats {
    /** Unix seconds. */
    time: number;
    /** 0…1 since the previous stats() call; 0 on the first. */
    cpu: number;
    cores: number;
    /** Bytes. */
    memory: { total: number; used: number; free: number };
    /** Bytes, for the volume holding the home directory. */
    disk: { total: number; free: number };
    /** Cumulative counters for en* interfaces — diff them yourself. */
    network: { rxBytes: number; txBytes: number };
    /** Seconds since boot. */
    uptime: number;
    /** ProcessInfo.ThermalState: 0 nominal, 1 fair, 2 serious, 3 critical. */
    thermalState: 0 | 1 | 2 | 3;
  }

  /** Always available; needs no permission. */
  const $system: { stats(): SystemStats };

  /** Result of a command, local or remote. Exit status, never a throw. */
  interface CommandResult {
    status: number;
    stdout: string;
    stderr: string;
  }

  /**
   * Runs a local command. Requires permission "shell".
   *
   * argv form only — `shell("rm -rf /")` is rejected. No shell is involved,
   * so pipes, globs and redirection are literal; use `sh -c` deliberately if
   * you want them. Destructive commands (rm, dd, sudo, kill, …) are blocked.
   */
  function shell(argv: string[]): Promise<CommandResult>;

  /** Runs an AppleScript. Requires permission "applescript". */
  function applescript(source: string): Promise<string>;

  /**
   * Runs a command on a destination configured in the inspector. Requires
   * permission "ssh"; rejects when no destination is set.
   *
   * A string is re-parsed by the remote shell; an array is passed through
   * quoted, like exec. `host` picks a destination by name — default is the
   * first one.
   */
  function ssh(command: string | string[], host?: string): Promise<CommandResult>;

  /** Names of the destinations configured for this item. */
  const $ssh: { hosts: string[] };

  interface HookEvent {
    method: string;
    path: string;
    headers: Record<string, string>;
  }

  /**
   * Receives requests from the app's loopback HTTP server (127.0.0.1:8787
   * by default; the app's DESKLAYER_HOOK_PORT / hookPort setting can move it,
   * and it only listens while a "server"-permitted plugin is running).
   * Requires permission "server". The port belongs to the app, which fans
   * every request out to all registered plugins — so several plugins can
   * listen to the same hook.
   */
  const $server: {
    on(method: string, handler: (event: HookEvent, body: string) => void): typeof $server;
  };

  // ────────────────────────────────────────────────── timers & networking

  function setTimeout(handler: () => void, ms?: number): number;
  function setInterval(handler: () => void, ms?: number): number;
  function clearTimeout(id: number): void;
  function clearInterval(id: number): void;

  interface FetchResponse {
    status: number;
    ok: boolean;
    headers: { get(name: string): string | null };
    text(): Promise<string>;
    json(): Promise<any>;
  }

  /** https only by default (ATS). Body is delivered once. */
  function fetch(
    url: string,
    options?: {
      method?: string;
      headers?: Record<string, string>;
      body?: string;
    }
  ): Promise<FetchResponse>;

  /** Minimal client: no protocols, extensions, or binary frames. */
  class WebSocket {
    constructor(url: string);
    /** 0 connecting, 1 open, 2 closing, 3 closed. */
    readonly readyState: 0 | 1 | 2 | 3;
    onopen: ((event: {}) => void) | null;
    onmessage: ((event: { data: string }) => void) | null;
    onclose: ((event: { code: number }) => void) | null;
    onerror: ((error: Error) => void) | null;
    send(text: string): void;
    close(): void;
  }

  /** Goes to the item's log panel in the inspector, and to the system log. */
  const console: {
    log(message?: any, ...rest: any[]): void;
    error(message?: any, ...rest: any[]): void;
    warn(message?: any, ...rest: any[]): void;
  };

  // ─────────────────────────────────────────────────────────────── exports

  interface WebviewConfig {
    /**
     * Optional here: a declared property named "url" wins, so the address
     * stays editable in the inspector. Same for offsetX / offsetY / zoom —
     * this object is the fallback, the properties are the live values.
     */
    url?: string;
    userAgent?: string;
    headers?: Record<string, string>;
    /** Each entry needs at least name, value, domain, path. */
    cookies?: Array<Record<string, string>>;
    /** Scroll the page so the item shows a chosen region. */
    offsetX?: number;
    offsetY?: number;
    /** 1 = 100%. */
    zoom?: number;
  }

  interface PluginExport {
    /** Semantic version, compared for updates. */
    version?: string;
    author?: string;
    description?: string;
    /**
     * Where to look for a newer copy. A sibling `<Name>.json` manifest
     * ({ version, url }) is checked first; the .js itself is the fallback.
     */
    updateURL?: string;

    /** Natural size in points. Also implies a locked aspect ratio. */
    width?: number;
    height?: number;
    /** false = fixed size; the canvas resize handles are disabled. */
    resizable?: boolean;
    /** "ratio" keeps the aspect on resize, "free" lets the axes move apart. */
    scaleMode?: "ratio" | "free";
    minWidth?: number;
    maxWidth?: number;
    minHeight?: number;
    maxHeight?: number;
    /**
     * Which axes follow the rendered content instead of the user's frame.
     * The item keeps its top-left corner as it grows.
     */
    autoSize?: "width" | "height" | "both" | "none";

    /** Host powers this plugin opts into; anything else is denied. */
    permissions?: Array<"shell" | "applescript" | "ssh" | "server">;

    properties?: PluginProperty[];

    /**
     * Canvas mode when it takes `ctx`; declarative mode when it takes
     * nothing and returns a tree. Omit entirely for a webview plugin.
     */
    render?: ((ctx: CanvasContext) => void) | (() => ViewNode);

    /** Present for webview plugins; `render` is then unused. */
    webview?: WebviewConfig;

    /** Set explicitly if detection by arity isn't what you want. */
    mode?: "canvas" | "declarative" | "webview";
  }

  /** The object the app reads after evaluating your file. */
  const plugin: { export: PluginExport | null };

  /**
   * Conventional global for the render function. Assigning it is a habit,
   * not a requirement — what matters is `plugin.export.render`.
   */
  var render: ((ctx: CanvasContext) => void) | (() => ViewNode);
}

export {};
