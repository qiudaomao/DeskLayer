; DeskLayer for Windows — Inno Setup installer.
; Build:  iscc DeskLayer.iss  (after publishing the app to ..\..\desklayer-dist)
; Signing: pass /Ssigntool="signtool sign /fd sha256 /a $f" to iscc, or sign
;          the app exe and the installer output separately with a real cert.
;          The Sparkle appcast Ed25519 signature is independent of and
;          complementary to this Authenticode signature.

#define AppName "DeskLayer"
#define AppVersion "1.2.2"
#define AppPublisher "DeskLayer"
#define AppExeName "DeskLayer.App.exe"
#define DistDir "..\..\desklayer-dist"

[Setup]
AppId={{B7E5B0A2-DE5C-4E77-9C2A-DE5C0A2B7E5B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user install: no admin needed, matches the HKCU Run-key login item.
PrivilegesRequired=lowest
OutputBaseFilename=DeskLayer-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; An update runs this while DeskLayer is still running, and Windows will not
; replace a running exe: Setup used to abort with exit code 5 and leave the
; old version installed, so the updater offered the same version forever.
; Restart Manager alone can't close a tray app with no visible window, hence
; the explicit stop in PrepareToInstall below.
CloseApplications=force
RestartApplications=no

[Files]
; The self-contained single-file publish (app exe + any extracted natives).
Source: "{#DistDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"

[Registry]
; The login item; the app's tray toggle manages the same value at runtime.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "DeskLayer"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop a running instance before removing files.
Filename: "{cmd}"; Parameters: "/c taskkill /im {#AppExeName} /f"; Flags: runhidden; RunOnceId: "StopDeskLayer"

; User plugins and layout live in %APPDATA%\DeskLayer and are intentionally
; left in place on uninstall.

[Code]
// Stop a running DeskLayer before any file is replaced. Asked politely
// first: a forced kill skips the wallpaper restore, which would leave the
// desktop showing our last frame until the new copy starts drawing.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // WM_CLOSE first, so the app can put the real wallpaper back.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im DeskLayer.App.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2500);
  // Anything still alive is holding the file we are about to overwrite.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im DeskLayer.App.exe /f',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;
