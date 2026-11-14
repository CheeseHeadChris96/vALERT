param([Parameter(Mandatory=$true)][string]$Archive,[string]$InstallRoot=(Join-Path $env:ProgramFiles 'vALERT Console'))
$ErrorActionPreference='Stop'
Stop-Service 'vALERT.Portal' -Force -ErrorAction SilentlyContinue
$data=Join-Path $env:ProgramData 'vALERT\Portal'
if(Test-Path $data){$backup=$data+'.before-restore-'+(Get-Date -Format yyyyMMddHHmmss);Move-Item $data $backup;Write-Host "Existing destination data preserved at $backup"}
New-Item -ItemType Directory -Path $data | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M'|Out-Null
$secure=Read-Host 'Migration passphrase' -AsSecureString
$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try{
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=Join-Path $InstallRoot 'portal\Valert.Portal.exe'
    $start.Arguments='--restore "'+(Resolve-Path $Archive).Path+'"';$start.UseShellExecute=$false;$start.RedirectStandardInput=$true
    $process=[Diagnostics.Process]::Start($start)
    $process.StandardInput.WriteLine([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer));$process.StandardInput.Close();$process.WaitForExit()
    if($process.ExitCode-ne 0){throw 'Restore failed. The source archive and pre-restore destination directory are unchanged.'}
}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)}
Write-Host 'Restore complete. Configure the HTTPS certificate on this server with Configure-Portal.ps1, update DNS, then verify reporting. Keep the old portal stopped.'
