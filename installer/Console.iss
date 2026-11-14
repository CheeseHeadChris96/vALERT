#ifndef AppVersion
  #define AppVersion "0.1.0-preview.1"
#endif
[Setup]
AppId={{E7553276-60A7-41D0-9BCB-FA201B59D4D5}
AppName=vALERT Console
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.0
AppPublisher=vALERT
AppPublisherURL=https://github.com/CheeseHeadChris96/vALERT
DefaultDirName={autopf}\vALERT Console
DefaultGroupName=vALERT Console
OutputDir=..\artifacts\installers
OutputBaseFilename=vALERT-Console-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\portal\Valert.Portal.exe
CloseApplications=no
SetupLogging=yes
[Files]
Source: "..\artifacts\portal\*"; DestDir: "{app}\portal"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Services.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Remove-Services.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Configure-Portal.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Restore-Portal.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\docs\WINDOWS-INSTALL.md"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Open vALERT Console"; Filename: "http://localhost:5080"
Name: "{group}\Configure HTTPS"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Configure-Portal.ps1"""; Flags: runminimized
Name: "{group}\Installation guide"; Filename: "{app}\WINDOWS-INSTALL.md"
[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Install-Services.ps1"" -Component Portal -InstallRoot ""{app}"" -Version ""{#AppVersion}"""; Flags: runhidden waituntilterminated
Filename: "http://localhost:5080"; Description: "Open vALERT Console"; Flags: shellexec postinstall skipifsilent
[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Remove-Services.ps1"" -Component Portal"; Flags: runhidden waituntilterminated
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop vALERT.Portal', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Result := '';
end;
