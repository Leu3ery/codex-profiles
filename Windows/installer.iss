#ifndef AppVersion
  #define AppVersion "2.0.1"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif
[Setup]
AppId={{2A59B566-9EE6-441A-A71E-B1EF510D2B94}
AppName=Codex Profiles
AppVersion={#AppVersion}
AppPublisher=Leu3ery / qwersyk
AppPublisherURL=https://github.com/Leu3ery/codex-profiles
DefaultDirName={localappdata}\Programs\Codex Profiles
DefaultGroupName=Codex Profiles
PrivilegesRequired=lowest
OutputDir=..\dist\windows-{#AppArch}
OutputBaseFilename=Codex-Profiles-{#AppVersion}-windows-{#AppArch}-Setup
SetupIconFile=CodexProfiles.Windows\AppIcon.ico
UninstallDisplayIcon={app}\Codex-Profiles.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if AppArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes
[Files]
Source: "..\dist\windows-{#AppArch}\Codex-Profiles-windows-{#AppArch}.exe"; DestDir: "{app}"; DestName: "Codex-Profiles.exe"; Flags: ignoreversion
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Icons]
Name: "{group}\Codex Profiles"; Filename: "{app}\Codex-Profiles.exe"
Name: "{autodesktop}\Codex Profiles"; Filename: "{app}\Codex-Profiles.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Codex-Profiles.exe"; Description: "Open Codex Profiles"; Flags: nowait postinstall skipifsilent
