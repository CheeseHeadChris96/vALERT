param([ValidateSet('Portal','Agent')][string]$Component)
$ErrorActionPreference='Continue'
$name="vALERT.$Component"
if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -Force -ErrorAction SilentlyContinue;& sc.exe delete $name | Out-Null}
if($Component-eq'Agent'){Unregister-ScheduledTask -TaskName 'vALERT Agent Updater' -Confirm:$false -ErrorAction SilentlyContinue}
if($Component-eq'Portal'){Unregister-ScheduledTask -TaskName 'vALERT Console Updater' -Confirm:$false -ErrorAction SilentlyContinue}
# Monitoring history, credentials, and queued reports are deliberately retained in ProgramData.
