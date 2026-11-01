; Fav Explorer installer.
;
; Built to the Microsoft Store's rules for EXE installers (policy 10.2.9) even though
; we are not submitting yet, because they are also just good rules:
;
;   - installs silently with /VERYSILENT (only the UAC prompt appears)
;   - downloads nothing; the .NET runtime ships inside the app
;   - registers in Apps & features and uninstalls cleanly
;   - never restarts Explorer on its own
;
; Admin rights are needed for exactly one thing: Windows reads icon overlay handlers
; from HKEY_LOCAL_MACHINE only, so there is no per-user install of an overlay.
;
; Build with:  ISCC.exe installer\favexplorer.iss      (after build.ps1)

#define AppName      "Fav Explorer"
#define AppExe       "FavExplorer.exe"
#define DistDir      "..\dist"
#define AppVersion   GetVersionNumbersString(DistDir + "\" + AppExe)
#define AppShortVer  Copy(AppVersion, 1, RPos(".", AppVersion) - 1)

[Setup]
; Never change AppId: it is how upgrades and uninstall find an existing install.
AppId={{635D1787-01C8-43BF-A8E4-44CA7B5BF40E}
AppName={#AppName}
AppVersion={#AppShortVer}
AppVerName={#AppName} {#AppShortVer}
AppPublisher=MOHAMMED Tashreef
AppPublisherURL=https://github.com/tashreefshareef/fav-explorer
AppSupportURL=https://github.com/tashreefshareef/fav-explorer/issues
AppUpdatesURL=https://github.com/tashreefshareef/fav-explorer/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer

DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=admin

; The overlay DLL loads into 64-bit Explorer, so the app must be 64-bit and install
; into the 64-bit registry view. ARM64 Explorer cannot load an x64 DLL, so refuse
; to install there rather than install something that silently never works.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763

SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern

; We close the tray app ourselves and never want Restart Manager to offer to
; close Explorer, which holds the overlay DLL open.
CloseApplications=no
RestartApplications=no

OutputDir=..\build\installer
OutputBaseFilename=FavExplorerSetup-{#AppShortVer}
Compression=lzma2/ultra64
SolidCompression=yes

[Tasks]
Name: startup; Description: "Start {#AppName} when I sign in"; GroupDescription: "Options:"

[Files]
; The DLL registers itself (DllRegisterServer writes its CLSID and the overlay key)
; and unregisters itself on uninstall. uninsrestartdelete because Explorer will
; still have it loaded at uninstall time.
Source: "{#DistDir}\HeartOverlay.dll"; DestDir: "{app}"; Flags: ignoreversion regserver 64bit uninsrestartdelete
Source: "{#DistDir}\*"; DestDir: "{app}"; Excludes: "HeartOverlay.dll,*.ps1"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\PRIVACY.md"; DestDir: "{app}"; DestName: "PRIVACY.txt"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Same value the app's own Settings toggle reads and writes, so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Launched as the signed-in user, never elevated: a keyboard hook installed from an
; elevated process misbehaves against normal windows.
Filename: "{app}\{#AppExe}"; Flags: nowait postinstall runasoriginaluser; \
  Description: "Start {#AppName} now"

; Explorer only reads the overlay list when it starts. Offered, never forced, and
; never in a silent install.
Filename: "{cmd}"; Parameters: "/c taskkill /f /im explorer.exe & start explorer.exe"; \
  Flags: nowait postinstall unchecked skipifsilent runhidden runasoriginaluser; \
  Description: "Restart File Explorer now so the hearts appear (closes open Explorer windows)"

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopTray"

[UninstallDelete]
Type: files; Name: "{app}\HeartOverlay.*.old"

[Messages]
FinishedLabel=Setup has installed [name].%n%nSelect files in File Explorer and press Ctrl+Shift+F to favourite them.%n%nThe hearts appear the next time File Explorer starts — tick the box below, or just sign out and back in.

[Code]
// This app was called Heartmark before. Left installed, Heartmark's tray app would
// keep hooking the same shortcut and its overlay would compete for the same scarce
// slot, so it is removed first. Its uninstaller leaves the user's data behind, which
// the app migrates on first run.
const
  HeartmarkUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E0C6F4A-8B1D-4E3C-9A57-2F4B7D1C9E31}_is1';

procedure RemoveHeartmark();
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM64, HeartmarkUninstallKey, 'UninstallString', Uninstaller) then Exit;
  Log('Removing Heartmark: ' + Uninstaller);
  Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

// Stop the running tray app so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  RemoveHeartmark();
  Result := '';
end;

// On an upgrade, Explorer still has the old HeartOverlay.dll mapped and the copy
// would fail. Windows lets you rename a file that is in use, so move the old one
// aside: running Explorers keep the renamed file, new ones load the new DLL, and
// nobody's Explorer gets restarted. Leftovers from earlier upgrades are deleted
// here when nothing holds them any more.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dll, Aside: String;
  Rec: TFindRec;
begin
  if CurStep <> ssInstall then Exit;

  if FindFirst(ExpandConstant('{app}\HeartOverlay.*.old'), Rec) then
  try
    repeat
      DeleteFile(ExpandConstant('{app}\') + Rec.Name);
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;

  Dll := ExpandConstant('{app}\HeartOverlay.dll');
  if FileExists(Dll) then
  begin
    Aside := ExpandConstant('{app}\HeartOverlay.') + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.old';
    if not RenameFile(Dll, Aside) then
      Log('Could not move the old overlay DLL aside: ' + Dll);
  end;
end;
