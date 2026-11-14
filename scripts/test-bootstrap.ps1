$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\Install-vALERT.ps1')
foreach ($bad in @('http://valert.rebootranch.net','https://user:secret@valert.rebootranch.net','https://valert.rebootranch.net/path','https://valert.rebootranch.net?token=secret','https://valert.rebootranch.net#fragment','relative')) {
    $rejected = $false
    try { Assert-ConsoleOrigin $bad | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe enrollment origin was accepted: $bad" }
}
foreach ($bad in @('http://github.com/installer','https://github.com.attacker.example/installer','https://user:secret@github.com/installer','https://attacker.example/installer')) {
    $rejected = $false
    try { Assert-DownloadUri ([uri]$bad) } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe download redirect was accepted: $bad" }
}
Assert-ConsoleOrigin 'https://valert.rebootranch.net' | Out-Null
Assert-DownloadUri ([uri]'https://release-assets.githubusercontent.com/test?download=1')
# Ensure the Windows PowerShell HTTP stack supports explicit TLS and no redirects.
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.SslProtocols = [System.Security.Authentication.SslProtocols]::Tls12
$handler.Dispose()
Write-Host "Bootstrap HTTPS origin and redirect checks passed on PowerShell $($PSVersionTable.PSVersion)."
