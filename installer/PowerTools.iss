#ifndef AppVersion
  #define AppVersion "0.9.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\desktop-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "PowerTools"
#define AppPublisher "PowerTools"
#define AppUrl "https://github.com/muxiaoqi007/powertools"
#define AppExeName "PowerTools.Desktop.exe"

[Setup]
AppId={{5E490FAA-2FD3-4A33-85CB-39E2D13FC972}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\PowerTools
DefaultGroupName=PowerTools
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=PowerTools-Setup-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile={#SourceDir}\PowerTools.ico
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Power BI model and report management tools
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PowerTools"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\PowerTools"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\PowerTools.Desktop.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\PowerTools.Desktop.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch PowerTools"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function JsonEscape(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function ExternalToolManifestPath(): String;
var
  CommonProgramFilesX86: String;
begin
  CommonProgramFilesX86 := GetEnv('CommonProgramFiles(x86)');
  if CommonProgramFilesX86 = '' then
    CommonProgramFilesX86 := ExpandConstant('{commonpf32}');
  Result := AddBackslash(CommonProgramFilesX86) + 'Microsoft Shared\Power BI Desktop\External Tools\PowerTools.pbitool.json';
end;

procedure RegisterPowerBiExternalTool();
var
  TargetFolder: String;
  Manifest: String;
  IconBase64: AnsiString;
begin
  TargetFolder := ExtractFileDir(ExternalToolManifestPath());
  if not ForceDirectories(TargetFolder) then
    RaiseException('Unable to create the Power BI External Tools directory: ' + TargetFolder);
  if not LoadStringFromFile(ExpandConstant('{app}\PowerTools-64.base64'), IconBase64) then
    RaiseException('Unable to load the PowerTools icon data.');

  Manifest :=
    '{' + #13#10 +
    '  "version": "1.0.0",' + #13#10 +
    '  "name": "PowerTools",' + #13#10 +
    '  "description": "Read-only Power BI model, report quality and optimization analysis",' + #13#10 +
    '  "path": "' + JsonEscape(ExpandConstant('{app}\{#AppExeName}')) + '",' + #13#10 +
    '  "arguments": "--server \"%server%\" --database \"%database%\"",' + #13#10 +
    '  "iconData": "image/png;base64,' + Trim(IconBase64) + '"' + #13#10 +
    '}' + #13#10;

  if not SaveStringToFile(ExternalToolManifestPath(), Manifest, False) then
    RaiseException('Unable to register PowerTools as a Power BI external tool.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterPowerBiExternalTool();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteFile(ExternalToolManifestPath());
end;
