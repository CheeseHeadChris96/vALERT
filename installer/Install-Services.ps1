param([Parameter(Mandatory=$true)][ValidateSet('Portal','Agent')][string]$Component,[Parameter(Mandatory=$true)][string]$InstallRoot,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$data=Join-Path $env:ProgramData "vALERT\$Component"
New-Item -ItemType Directory -Force -Path $data | Out-Null
# The service can modify its data, but cannot modify executables, updater trust, or updater recovery state.
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if($LASTEXITCODE-ne 0){throw 'Could not secure the service data directory.'}
$service="vALERT.$Component"
$exe=if($Component-eq'Portal'){Join-Path $InstallRoot 'portal\Valert.Portal.exe'}else{Join-Path $InstallRoot "versions\$Version\Valert.Agent.exe"}
if(Get-Service $service -ErrorAction SilentlyContinue){Stop-Service $service -Force -ErrorAction SilentlyContinue;& sc.exe config $service binPath= "`"$exe`"" start= auto | Out-Null}
else{& sc.exe create $service binPath= "`"$exe`"" start= auto obj= 'NT AUTHORITY\LocalService' DisplayName= "vALERT $Component" | Out-Null}
if($LASTEXITCODE-ne 0){throw 'Could not configure the Windows service.'}
& sc.exe failure $service reset= 86400 actions= 'restart/60000/restart/60000/restart/60000' | Out-Null
if($Component-eq'Agent'){
    $action=New-ScheduledTaskAction -Execute (Join-Path $InstallRoot 'updater\Valert.Updater.exe') -Argument ('--root "'+$InstallRoot+'"')
    $trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 15)
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'vALERT Agent Updater' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
}
if($Component-eq'Portal'){
    # These administrator-owned files let the limited portal request an update without choosing an executable or URL.
    $metadata=@{installRoot=$InstallRoot;dataDirectory=$data}|ConvertTo-Json
    $metadata|Set-Content (Join-Path $InstallRoot 'tools\console-install.json') -Encoding UTF8
    $metadata|Set-Content (Join-Path $InstallRoot 'portal\console-install.json') -Encoding UTF8
    $action=New-ScheduledTaskAction -Execute (Join-Path $InstallRoot 'updater\Valert.Updater.exe') -Argument ('--console --root "'+$InstallRoot+'"')
    $trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'vALERT Console Updater' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
}
if($Component-eq'Portal'-or(Test-Path(Join-Path $data 'agent-settings.json'))){Start-Service $service}
