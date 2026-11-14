$ErrorActionPreference='Stop'
$setup=Resolve-Path (Join-Path $PSScriptRoot '..\artifacts\release\vALERT-Console-Setup.exe')
$log=Join-Path $env:RUNNER_TEMP 'valert-installer.log'
$process=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$log+'"')) -Wait -PassThru
if($process.ExitCode-ne 0){Get-Content $log -Tail 100;throw "Installer failed: $($process.ExitCode)"}
$ready=$false
for($i=0;$i-lt 45;$i++){try{$response=Invoke-RestMethod 'http://localhost:5080/health';if($response.status-eq'ready'){$ready=$true;break}}catch{Start-Sleep -Seconds 2}}
if(-not $ready){Get-Service 'vALERT.Portal'|Format-List;Get-Content $log -Tail 100;throw 'Installed portal failed readiness check.'}
$page=Invoke-WebRequest 'http://localhost:5080/' -UseBasicParsing
if($page.Content-notmatch'vALERT'){throw 'Installed portal did not serve the dashboard.'}
$data=Join-Path $env:ProgramData 'vALERT\Portal'
if(-not(Test-Path(Join-Path $data 'initial-setup.txt'))){throw 'Initial setup token was not generated.'}
Write-Host "Windows installer smoke test passed. Service version: $($response.version)"
Stop-Service 'vALERT.Portal'
