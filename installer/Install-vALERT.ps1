# Customer-specific bootstrap. Extract beside enrollment.json and run as Administrator.
$ErrorActionPreference = 'Stop'
function Assert-ConsoleOrigin([string]$Address) {
    $uri = [uri]$Address
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
        throw 'Enrollment requires an HTTPS console origin without credentials, a path, query, or fragment.'
    }
    return $uri
}
function Assert-DownloadUri([uri]$Uri) {
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -ne 'https' -or $Uri.UserInfo -or $Uri.Host -notin @('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com')) {
        throw 'Installer redirects must remain on an approved GitHub HTTPS download host.'
    }
}
# Permit validation tests to load the guards without enrollment, downloads, or installation.
if ($MyInvocation.InvocationName -eq '.') { return }
Add-Type -AssemblyName System.Net.Http
$e = Get-Content (Join-Path $PSScriptRoot 'enrollment.json') -Raw | ConvertFrom-Json
if ([DateTimeOffset]::Parse($e.expiresAt) -lt [DateTimeOffset]::UtcNow) { throw 'Enrollment expired. Generate a new package in vALERT.' }
$portal = Assert-ConsoleOrigin $e.portalUrl
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
# Windows PowerShell 5.1 supports TLS 1.2. The installed .NET agent allows TLS 1.2/1.3.
$handler.SslProtocols = [System.Security.Authentication.SslProtocols]::Tls12
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(10)
$file = Join-Path $env:TEMP ('vALERT-Agent-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $body = [System.Net.Http.StringContent]::new((@{token=$e.token} | ConvertTo-Json), [Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $client.PostAsync([uri]::new($portal, 'api/enrollment/installer'), $body).GetAwaiter().GetResult()
        try {
            # 3xx is a failure: the enrollment token is never forwarded to a redirect destination.
            $response.EnsureSuccessStatusCode() | Out-Null
            $asset = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        } finally { $response.Dispose() }
    } finally { $body.Dispose() }
    $download = [uri]$asset.url
    if ($asset.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $download.AbsoluteUri -notmatch '^https://github\.com/CheeseHeadChris96/vALERT/releases/download/v[0-9A-Za-z.-]+/vALERT-Agent-Setup\.exe$') {
        throw 'The console returned an invalid installer URL or checksum.'
    }
    for ($redirects = 0; ; $redirects++) {
        Assert-DownloadUri $download
        $response = $client.GetAsync($download, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                if ($redirects -ge 5 -or -not $response.Headers.Location) { throw 'Installer redirect limit exceeded.' }
                $download = [uri]::new($download, $response.Headers.Location)
                continue
            }
            $response.EnsureSuccessStatusCode() | Out-Null
            $output = [IO.File]::Create($file)
            try { $response.Content.CopyToAsync($output).GetAwaiter().GetResult() } finally { $output.Dispose() }
            break
        } finally { $response.Dispose() }
    }
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $asset.sha256) { throw 'Installer checksum mismatch.' }
    $process = Start-Process -FilePath $file -ArgumentList @('/ENROLLMENT="' + (Join-Path $PSScriptRoot 'enrollment.json') + '"') -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode)." }
    Write-Host 'Installation complete. Check the customer page for enrollment and collection status.'
} finally {
    $client.Dispose()
    if (Test-Path $file) { Remove-Item $file -Force }
}
