; ===================================================================
; MM Launcher installer (Inno Setup 6)
; Purpose: installs MMLauncher.exe for the current Windows user, with a Start menu
; shortcut, an optional desktop shortcut and a normal uninstaller (Settings > Apps).
; Built by the GitHub release workflow:  ISCC /DAppVersion=0.9.0 /DSourceExe=<path> MMLauncher.iss
;
; Choices made here:
;   PrivilegesRequired=lowest   installs into the user's own folder: no admin prompt, and
;                               the launcher can keep updating itself in place
;   AppId                       never change it: it's how Windows knows later installs
;                               and uninstalls belong to the same app
;   Settings                    live in %APPDATA%\MMLauncher (shared with the portable
;                               exe) and are kept on uninstall unless the user says otherwise
; ===================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\out\launcher\MMLauncher.exe"
#endif

[Setup]
AppId={{2DEB2730-F75A-4BE1-9EA7-9C5B68326BAE}
AppName=MM Launcher
AppVersion={#AppVersion}
AppVerName=MM Launcher {#AppVersion}
AppPublisher=Misfit Mercenaries
AppPublisherURL=https://github.com/meccmax/mm-launcher
AppSupportURL=https://github.com/meccmax/mm-launcher/issues
AppUpdatesURL=https://github.com/meccmax/mm-launcher/releases
DefaultDirName={localappdata}\Programs\MM Launcher
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=MMLauncher-Setup
SetupIconFile=..\src\MMLauncher.App\Assets\mm.ico
UninstallDisplayIcon={app}\MMLauncher.exe
UninstallDisplayName=MM Launcher
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "MMLauncher.exe"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\MM Launcher"; Filename: "{app}\MMLauncher.exe"; Comment: "Misfit Mercenaries DayZ launcher"
Name: "{userdesktop}\MM Launcher"; Filename: "{app}\MMLauncher.exe"; Comment: "Misfit Mercenaries DayZ launcher"; Tasks: desktopicon

[Run]
Filename: "{app}\MMLauncher.exe"; Description: "Start MM Launcher"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leftovers from self-updates (the previous exe is kept as .old until the next start).
Type: files; Name: "{app}\MMLauncher.exe.old"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Settings := ExpandConstant('{userappdata}\MMLauncher');
    if DirExists(Settings) and not UninstallSilent then
      if MsgBox('Also remove your MM Launcher settings, favorites and editor presets?' + #13#10 + #13#10 +
                'Choose No to keep them (for the portable version, or if you reinstall later).',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Settings, True, True, True);
  end;
end;
