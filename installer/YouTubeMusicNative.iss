; YouTube Music Native installer (Inno Setup 6).
; Build:  ISCC installer\YouTubeMusicNative.iss /DAppVersion=1.2.3 /DSourceDir=<publish folder> /O<output folder>
; Installs for the current user (default, no admin prompt) or, picked on the first page, for all users in Program
; Files (asks for admin rights; the app's own updates then ask for them too). Updates keep the mode used before.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "YouTube Music Native"
#define AppKey "YouTubeMusicNative"
#define AppExe "YouTubeMusicNative.exe"
#define AppUrl "https://github.com/mybugga/youtube-music-native"

[Setup]
AppId={{6E0B8E0B-5B7B-4C1E-9D2A-7C1F3A9E4B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
; {autopf} is %LocalAppData%\Programs for a per-user install (same folder as before) and Program Files for all users.
DefaultDirName={autopf}\{#AppKey}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; Always ask when run by hand (even over an existing install); the app's own updates pass /CURRENTUSER or /ALLUSERS.
UsePreviousPrivileges=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename={#AppKey}-{#AppVersion}-setup
SetupIconFile=..\src\YouTubeMusicNative\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Close a running copy (it holds its own files open) and don't nag about it.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The shortcuts take their icon from this file, not the exe, so a new icon isn't hidden by Windows' icon cache.
Source: "..\src\YouTubeMusicNative\Assets\app.ico"; DestDir: "{app}"; DestName: "YouTubeMusicNative.ico"; Flags: ignoreversion

[InstallDelete]
; Leftovers from an older build layout.
Type: files; Name: "{app}\*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\YouTubeMusicNative.ico"; AppUserModelID: "{#AppKey}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\YouTubeMusicNative.ico"; Tasks: desktopicon

[Run]
; Interactive install: offer to launch. Silent update with /RELAUNCH=1: start the new version straight away.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; yt-dlp updates itself in place and may leave its old copy behind.
Type: filesandordirs; Name: "{app}"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  // Settings, sign-in and the resume snapshot live in %LocalAppData%\YouTubeMusicNative.
  if CurUninstallStep = usPostUninstall then
  begin
    // "Start with Windows" entry written by the app itself.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'YouTubeMusicNative');
    DataDir := ExpandConstant('{localappdata}\YouTubeMusicNative');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also remove your YouTube Music Native settings and sign-in?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
