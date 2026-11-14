param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'vALERT Console'
if(-not(Get-ScheduledTask -TaskName 'vALERT Console Updater')){throw 'The console updater task is missing.'}
# Keep the scheduler from racing this isolated test of the same update engine.
Disable-ScheduledTask -TaskName 'vALERT Console Updater' | Out-Null
$output=Join-Path $PSScriptRoot '..\artifacts\update-smoke'
dotnet publish (Join-Path $PSScriptRoot '..\tests\Valert.UpdateSmoke') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $output
if($LASTEXITCODE-ne0){throw 'Console update test harness did not build.'}
& (Join-Path $output 'Valert.UpdateSmoke.exe') $root (Join-Path $PSScriptRoot '..\artifacts\release\vALERT-Portal-win-x64.zip') $Version
if($LASTEXITCODE-ne0){throw 'Windows console update verification failed.'}
