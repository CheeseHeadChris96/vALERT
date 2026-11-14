param([string]$PublicUrl,[string]$CertificateThumbprint,[int]$HttpsPort=443)
$ErrorActionPreference='Stop'
$data=Join-Path $env:ProgramData 'vALERT\Portal';$path=Join-Path $data 'portal-settings.json'
if(-not $PublicUrl){$PublicUrl=Read-Host 'Public HTTPS address [https://valert.rebootranch.net]';if(-not $PublicUrl){$PublicUrl='https://valert.rebootranch.net'}}
$uri=[uri]$PublicUrl;if(-not $uri.IsAbsoluteUri-or$uri.Scheme-ne'https'-or$uri.UserInfo-or$uri.AbsolutePath-ne'/'-or$uri.Query-or$uri.Fragment){throw 'Enter an HTTPS origin without credentials, a path, query, or fragment.'}
if(-not $CertificateThumbprint){Get-ChildItem Cert:\LocalMachine\My | Select-Object Subject,Thumbprint,NotAfter | Format-Table;$CertificateThumbprint=Read-Host 'TLS certificate thumbprint from LocalMachine\My'}
$cert=Get-Item ('Cert:\LocalMachine\My\'+$CertificateThumbprint.Replace(' ',''));if(-not $cert.HasPrivateKey){throw 'Certificate has no private key.'}
$key=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if($key-is[Security.Cryptography.RSACng]){$keyPath=Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\'+$key.Key.UniqueName)}
elseif($key-is[Security.Cryptography.RSACryptoServiceProvider]){$keyPath=Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\'+$key.CspKeyContainerInfo.UniqueKeyContainerName)}
else{throw 'Grant Local Service read access to this certificate private key manually, then configure portal-settings.json. This helper currently supports RSA certificates.'}
& icacls.exe $keyPath /grant '*S-1-5-19:R' | Out-Null;if($LASTEXITCODE-ne 0){throw 'Cannot grant certificate access.'}
$settings=if(Test-Path $path){Get-Content $path -Raw|ConvertFrom-Json}else{[pscustomobject]@{publicUrl='';certificateThumbprint='';httpsPort=443;localPort=5080;includePreviewUpdates=$true;migrationPaused=$false}}
$settings.publicUrl=$uri.GetLeftPart([UriPartial]::Authority);$settings.certificateThumbprint=$CertificateThumbprint.Replace(' ','');$settings.httpsPort=$HttpsPort
$settings|ConvertTo-Json|Set-Content $path -Encoding UTF8
if(-not(Get-NetFirewallRule -DisplayName 'vALERT HTTPS' -ErrorAction SilentlyContinue)){New-NetFirewallRule -DisplayName 'vALERT HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort|Out-Null}
Restart-Service 'vALERT.Portal'
Write-Host "HTTPS configured: $($settings.publicUrl). Forward your router's HTTPS port to this server."
