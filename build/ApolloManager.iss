; ==============================================================================
; Apollo Manager — Inno Setup 6 Installer Script (Windows x64)
; ==============================================================================
; Compile directly via ISCC.exe or automatically via codebase\build\package-win-x64.ps1:
;   ISCC.exe /DMyAppVersion=0.1.0-beta.1 /DStageDir=..\dist\stage-win-x64 /DOutDir=..\dist ApolloManager.iss
; ==============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-beta.1"
#endif

#ifndef StageDir
  #define StageDir "..\dist\stage-win-x64"
#endif

#ifndef OutDir
  #define OutDir "..\dist"
#endif

#define MyAppName "Apollo Manager"
#define MyAppPublisher "Apollo Manager Contributors"
#define MyAppExeName "Apollo Manager.exe"

[Setup]
AppId={{8E74A8D2-4F5A-4F62-9B18-4D9E61D5A701}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir={#OutDir}
OutputBaseFilename=ApolloManager-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\L3.Presentation\Walkman.L3.AppShell\Assets\apollo-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
