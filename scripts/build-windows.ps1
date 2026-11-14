param([string]$Version='0.1.0-preview.1')
$ErrorActionPreference='Stop'
foreach($project in @('Portal','Agent','Updater')){
    $output=Join-Path $PSScriptRoot "..\artifacts\$($project.ToLowerInvariant())"
    dotnet publish (Join-Path $PSScriptRoot "..\src\Valert.$project") --configuration Release --runtime win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $output
    if($LASTEXITCODE-ne 0){throw "Publishing $project failed."}
}
$compiler='C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if(-not(Test-Path $compiler)){$compiler=(Get-Command ISCC.exe -ErrorAction Stop).Source}
foreach($installer in @('Console','Agent')){
    & $compiler "/DAppVersion=$Version" (Join-Path $PSScriptRoot "..\installer\$installer.iss")
    if($LASTEXITCODE-ne 0){throw "$installer installer compilation failed."}
}
$release=Join-Path $PSScriptRoot '..\artifacts\release';New-Item -ItemType Directory -Force $release|Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\artifacts\installers\*.exe') $release
Compress-Archive -Path (Join-Path $PSScriptRoot '..\artifacts\agent\*') -DestinationPath (Join-Path $release 'vALERT-Agent-win-x64.zip') -Force
Compress-Archive -Path (Join-Path $PSScriptRoot '..\artifacts\portal\*') -DestinationPath (Join-Path $release 'vALERT-Portal-win-x64.zip') -Force
Copy-Item (Join-Path $PSScriptRoot '..\docs\WINDOWS-INSTALL.md') $release
$manifest=@{}
Get-ChildItem $release -File | Where-Object {$_.Name-ne'release-manifest.json'} | ForEach-Object {$manifest[$_.Name]=@{sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();size=$_.Length}}
$manifest|ConvertTo-Json -Depth 4|Set-Content (Join-Path $release 'release-manifest.json') -Encoding utf8NoBOM
