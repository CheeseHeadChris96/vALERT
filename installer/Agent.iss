#ifndef AppVersion
  #define AppVersion "0.1.0-preview.1"
#endif
[Setup]
AppId={{C579E9F8-5D1B-45A0-B048-05D16A860F76}
AppName=vALERT Agent
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.0
AppPublisher=vALERT
AppPublisherURL=https://github.com/CheeseHeadChris96/vALERT
DefaultDirName={autopf}\vALERT Agent
DefaultGroupName=vALERT Agent
OutputDir=..\artifacts\installers
OutputBaseFilename=vALERT-Agent-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes
[Files]
Source: "..\artifacts\agent\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\updater\*"; DestDir: "{app}\updater"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Services.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Remove-Services.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\docs\AGENT-SETUP.md"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Configure vALERT Agent"; Filename: "{app}\versions\{#AppVersion}\Valert.Agent.exe"; Parameters: "--configure """" --install-root ""{app}"""
Name: "{group}\Agent setup guide"; Filename: "{app}\AGENT-SETUP.md"
[Run]
Filename: "{app}\versions\{#AppVersion}\Valert.Agent.exe"; Parameters: "--configure ""{param:ENROLLMENT|}"" --install-root ""{app}"""; Flags: waituntilterminated; Check: HasEnrollment
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Install-Services.ps1"" -Component Agent -InstallRoot ""{app}"" -Version ""{#AppVersion}"""; Flags: runhidden waituntilterminated
[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Remove-Services.ps1"" -Component Agent"; Flags: runhidden waituntilterminated
[Code]
function HasEnrollment: Boolean;
begin
  Result := ExpandConstant('{param:ENROLLMENT|}') <> '';
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop vALERT.Agent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Result := '';
end;
