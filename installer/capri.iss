; Capri on your desktop: her installer (Inno Setup 6). Built by tools\make_release.ps1 -Installer, which passes
;   AppVersion  her version (0.1.0)
;   Stage       the folder with her built app (CapriDesktop.exe, art\, Assets\, README.txt, version.txt)
;   OutDir      where Capri-Desktop-Setup-<version>.exe goes
; She installs for this user only (no administrator), into %LocalAppData%\Programs\CapriDesktop: the folder her Voxta
; module looks in, so Voxta's Play starts the copy installed here.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{5C7A1E2B-9D41-4F3C-8B6E-CA9121DE5A01}
AppName=Capri on your desktop
AppVersion={#AppVersion}
AppPublisher=Reign
AppPublisherURL=https://github.com/ReignMocap/capri-desktop
DefaultDirName={localappdata}\Programs\CapriDesktop
DisableDirPage=yes
DefaultGroupName=Capri
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=Capri-Desktop-Setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\CapriDesktop\Assets\capri.ico
UninstallDisplayIcon={app}\CapriDesktop.exe
UninstallDisplayName=Capri on your desktop
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put Capri on the desktop (a shortcut)"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Capri"; Filename: "{app}\CapriDesktop.exe"
Name: "{userdesktop}\Capri"; Filename: "{app}\CapriDesktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CapriDesktop.exe"; Description: "Start Capri now (start Voxta first)"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\version.txt"
