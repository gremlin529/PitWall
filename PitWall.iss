#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{6F0D5C1E-3B7A-4C52-9A41-5A9E7B2D8C10}
AppName=PitWall
AppVersion={#AppVersion}
AppPublisher=PitWall
DefaultDirName={localappdata}\Programs\PitWall
DefaultGroupName=PitWall
PrivilegesRequired=lowest
OutputDir=artifacts
OutputBaseFilename=PitWall-Setup
SetupIconFile=Assets\Pitwall.ico
UninstallDisplayIcon={app}\PitWall.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PitWall"; Filename: "{app}\PitWall.exe"
Name: "{userdesktop}\PitWall"; Filename: "{app}\PitWall.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PitWall.exe"; Description: "Launch PitWall"; Flags: nowait postinstall skipifsilent
