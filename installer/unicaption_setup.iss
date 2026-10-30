; Inno Setup script for the native (WinUI 3) Unicaption build.
; Normally invoked by installer\build_installer.ps1, which passes the version and publish folder:
;   ISCC.exe /DMyAppVersion=0.2.0 /DPublishDir=..\artifacts\publish\win-x64 installer\unicaption_setup.iss
;
; Migration decision (section 17 / "Migratsiya"): the native app REPLACES the Flutter app.
; It keeps the same AppId and install folder, so the old 0.1.x auto-updater can deliver it
; and Windows shows a single "Unicaption" entry. Old Flutter runtime files are removed.

#define MyAppName "Unicaption"
#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef MyAppOutputBaseFilename
  #define MyAppOutputBaseFilename "UnicaptionSetup-" + MyAppVersion
#endif
#define MyAppPublisher "Unicaption"
#define MyAppURL "https://unicaption.com"
#define MyAppExeName "Unicaption.exe"

[Setup]
; Same AppId as the Flutter installer (do not change: it links upgrades and uninstall).
AppId={{B7E2B1A4-6C3D-4F2E-9A1B-UNICAPTION01}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename={#MyAppOutputBaseFilename}
SetupIconFile=..\src\Unicaption.App\Assets\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Brand panel on the Welcome / Finish pages and the header logo, one file per DPI step
; (100-250 %) so they stay sharp; regenerate with branding\make_branding.ps1.
WizardImageFile=branding\WizardImage-100.bmp,branding\WizardImage-125.bmp,branding\WizardImage-150.bmp,branding\WizardImage-175.bmp,branding\WizardImage-200.bmp,branding\WizardImage-225.bmp,branding\WizardImage-250.bmp
WizardSmallImageFile=branding\WizardSmallImage-100.bmp,branding\WizardSmallImage-125.bmp,branding\WizardSmallImage-150.bmp,branding\WizardSmallImage-175.bmp,branding\WizardSmallImage-200.bmp,branding\WizardSmallImage-225.bmp,branding\WizardSmallImage-250.bmp
WizardImageStretch=no
; Inno 6 hides the Welcome page by default; the brand flow starts with it.
DisableWelcomePage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
english.WelcomeLabel1=Welcome to Unicaption Setup
english.WelcomeLabel2=Unicaption shows live captions and translation for your microphone and computer audio.%n%nThis will install [name/ver] on your computer. Close other applications before continuing.
english.InstallingLabel=Installing Unicaption...
english.FinishedHeadingLabel=Unicaption has been installed
english.FinishedLabel=Unicaption is ready. Sign in with Google when it opens to start captioning.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Leftovers of the Flutter build in the same folder.
Type: files; Name: "{app}\unicaption_desktop.exe"
Type: files; Name: "{app}\flutter_windows.dll"
Type: files; Name: "{app}\*_plugin.dll"
Type: filesandordirs; Name: "{app}\data"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: optional "Launch Unicaption" checkbox.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Auto-update (silent, started by the app with /RELAUNCH=1): reopen the app as the original user.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall runasoriginaluser; Check: RelaunchRequested

[UninstallDelete]
; Per-user data (settings, encrypted session, diagnostics) is under %LOCALAPPDATA%\Unicaption
; and is intentionally kept for reinstall; users can delete it manually.

[Code]
function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent() and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
