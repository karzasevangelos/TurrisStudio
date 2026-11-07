; Turris Studio installer (Inno Setup 6).
; Build:  powershell -File installer\build-installer.ps1
; Needs:  publish\release (self-contained app), installer\deps\ffmpeg\{ffmpeg,ffprobe}.exe + LICENSE,
;         installer\deps\PawnIO_setup.exe (sensor driver used by LibreHardwareMonitor).

#define AppName "Turris Studio"
#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

[Setup]
AppId={{8C3F6B2E-5A41-4D0B-9E57-2B1C7A9D4E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Turris Studio
AppComments=Stats, GIFs, videos and images on TRYX Turris 620 and Stage 360 cooler screens
DefaultDirName={autopf}\Turris Studio
DefaultGroupName=Turris Studio
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\publish\installer
OutputBaseFilename=TurrisStudio-Setup-{#AppVersion}
SetupIconFile=..\src\TurrisStudio.App\Assets\app.ico
UninstallDisplayIcon={app}\TurrisStudio.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\release\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "deps\ffmpeg\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\ffmpeg\ffprobe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\ffmpeg\LICENSE"; DestDir: "{app}\licenses"; DestName: "FFmpeg-LICENSE.txt"; Flags: ignoreversion
Source: "..\src\TurrisStudio.Core\Protocol\Proto\LICENSE-Tryx-Linux-GUI.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "tasks.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsPawnIO

[Icons]
Name: "{autoprograms}\Turris Studio"; Filename: "{app}\TurrisStudio.exe"; Comment: "Control your TRYX cooler screen"
Name: "{autodesktop}\Turris Studio"; Filename: "{app}\TurrisStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing the sensor driver (PawnIO)..."; Flags: waituntilterminated; Check: NeedsPawnIO
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tasks.ps1"" -AppPath ""{app}\TurrisStudio.exe"""; StatusMsg: "Setting up automatic start..."; Flags: runhidden waituntilterminated
; Start the background app right away (hidden in the tray, full sensor access), also for silent installs.
Filename: "schtasks.exe"; Parameters: "/run /tn ""Turris Studio"""; StatusMsg: "Starting Turris Studio..."; Flags: runhidden waituntilterminated
Filename: "{app}\TurrisStudio.exe"; Description: "Open Turris Studio now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /im TurrisStudio.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tasks.ps1"" -AppPath ""{app}\TurrisStudio.exe"" -Remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTasks"

[Code]
function NeedsPawnIO: Boolean;
begin
  Result := not RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Upgrades: stop the running copy (it lives in the tray) so its files can be replaced.
  Exec('taskkill.exe', '/f /im TurrisStudio.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
