# DeskLayer for Windows

A C#/.NET 8 port of DeskLayer, rendering the same JavaScript plugins onto the
Windows wallpaper layer and into floating windows. Built against the shared
plugin contract in `../shared/` — the conformance suite there runs on both
platforms and must stay byte-identical.

## Layout

```
win/
  DeskLayer.Win.sln
  src/
    DeskLayer.Core/         plugin runtime + model, platform-neutral
      Model/                PropertyValue, Layout(Store), PluginRegistry, ViewNode
      Js/                   Jint PluginInstance, JsBindings (timers/fetch/ws),
                            HostBindings (shell/ssh/$server/$system/$platform)
      Conformance/          RecordingCanvas + CanonicalJson (shared-golden twin)
      SharedAssets.cs       loads shared/runtime symbol-map + font-aliases
    DeskLayer.App/          the WinForms/WPF + Direct2D shell
      WallpaperEngine.cs    D2D swap chain under WorkerW, per-item reconcile
      D2DCanvas.cs          ctx → Direct2D bridge (canvas mode)
      NodeInterpreter.cs    ViewNode → WPF (declarative mode)
      FloatingPanel.cs      borderless nonactivating widget windows
      WebViewHost.cs        WebView2 (webview mode)
      HookServer.cs         loopback $server listener (127.0.0.1:8787)
      PowerController.cs    lock/suspend/battery/fullscreen → RenderPolicy
      ManagerWindow.cs      library / desktop overview / inspector
      UpdateController.cs   NetSparkle on the shared appcast format
      LoginItem.cs          HKCU Run-key start-with-Windows
    DeskLayer.Conformance/  runs shared/conformance against the Jint runtime
    DeskLayer.UpdateCheck/  headless appcast/signature verification (CI)
  installer/DeskLayer.iss   Inno Setup installer
```

## Build & run

```
dotnet build DeskLayer.Win.sln -c Release
dotnet run  --project src/DeskLayer.App          # runs the wallpaper app
dotnet run  --project src/DeskLayer.Conformance  # must print ALL GREEN
```

Plugins and layout live in `%APPDATA%\DeskLayer` (override with
`DESKLAYER_DATA_DIR`), wire-compatible with the mac app's `layout.json`.

## Package

`scripts/win/publish.sh` produces a self-contained single-file exe and, if
Inno Setup is installed, the installer. Auto-update uses `../appcast-win.xml`
(Sparkle format + Ed25519, shared pipeline with the mac release);
`scripts/win/sign-artifact.sh` signs a release artifact.

## Platform notes

- **Target:** Windows 10 21H2+ / Windows 11, x64. Verified on 22H2.
- **JS engine:** Jint (default; ~18× faster than ClearScript V8 on
  bridge-heavy canvas renders, no native dll). An `IJsEngine`-shaped seam
  keeps V8 available for compute-heavy plugins.
- **Wallpaper attach:** the WorkerW/Progman `SetParent` trick, with a
  strategy chain and Explorer-restart recovery. The post-24H2 attach path is
  untested (dev box is 22H2).
- **Dropped vs mac:** `applescript()` (rejects with a clear message), the
  widget extension, the LLM plugin-authoring assistant (mac-only for now).
