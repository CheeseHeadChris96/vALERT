$ErrorActionPreference = 'Stop'
$setup = Resolve-Path (Join-Path $PSScriptRoot '..\artifacts\release\vALERT-Agent-Setup.exe')
$root = Join-Path $env:ProgramFiles 'vALERT Agent'
$data = Join-Path $env:ProgramData 'vALERT\Agent'
function Install-Agent {
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Agent installer failed: $($process.ExitCode)" }
}
Install-Agent
$service = Get-CimInstance Win32_Service -Filter "Name='vALERT.Agent'"
if ($service.StartName -ne 'NT AUTHORITY\LocalService') { throw 'Agent did not install with the expected limited service identity.' }
$exe = $service.PathName.Trim('"')
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Installed agent failed its runtime check.' }
& (Join-Path $root 'updater\Valert.Updater.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Installed updater failed its runtime check.' }
if (-not (Get-ScheduledTask -TaskName 'vALERT Agent Updater')) { throw 'Automatic updater task is missing.' }
# Start without enrollment to verify the Windows service host. It makes no outbound requests.
Start-Service 'vALERT.Agent'
$ready = Join-Path $data 'ready.json'
for ($i=0; $i -lt 20 -and -not (Test-Path $ready); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $ready)) { throw 'Agent Windows service did not become ready.' }
Stop-Service 'vALERT.Agent'
$queue = Join-Path $data 'queue'
New-Item -ItemType Directory -Force -Path $queue | Out-Null
$sentinel = Join-Path $queue 'installer-preservation-test.json'
'{"synthetic":"queued report must survive installer updates"}' | Set-Content $sentinel
$hash = (Get-FileHash $sentinel).Hash
Install-Agent
if ((Get-FileHash $sentinel).Hash -ne $hash) { throw 'Reinstall changed queued data.' }
Write-Host 'Windows agent installer, service host, updater task, and queue preservation checks passed.'
