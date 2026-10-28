; Inno Setup script – per-user install, no admin rights needed.
; Build: iscc /DAppVersion=1.2.3 installer\Peel.iss  (expects the published app in ..\publish)
;
; The installer carries only Peel itself. On first start Peel downloads FFmpeg, Pandoc and yt-dlp
; from their official releases into {app}\tools (see src\FileAction.App\Shell\ToolSetup.cs).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{9F62351A-7C66-449E-A8E4-70B505337FCB}
AppName=Peel
AppVersion={#AppVersion}
AppVerName=Peel {#AppVersion}
AppPublisher=GuardaChuva22
AppPublisherURL=https://github.com/GuardaChuva22/peel
AppSupportURL=https://github.com/GuardaChuva22/peel/issues
AppUpdatesURL=https://github.com/GuardaChuva22/peel/releases
DefaultDirName={localappdata}\Programs\Peel
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=Peel-Setup-{#AppVersion}
SetupIconFile=..\src\FileAction.App\Assets\Peel.ico
UninstallDisplayIcon={app}\Peel.exe
UninstallDisplayName=Peel
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\Peel"; Filename: "{app}\Peel.exe"; Parameters: "--home"

[Run]
Filename: "{app}\Peel.exe"; Parameters: "--home"; Description: "{cm:LaunchProgram,Peel}"; Flags: nowait postinstall skipifsilent
; Silent install = in-app update: start the new version again.
Filename: "{app}\Peel.exe"; Parameters: "--updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im Peel.exe /f"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{app}\Peel.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[UninstallDelete]
; Helper engines Peel downloaded itself. Settings and history in %APPDATA%\Peel are kept.
Type: filesandordirs; Name: "{app}\tools"
