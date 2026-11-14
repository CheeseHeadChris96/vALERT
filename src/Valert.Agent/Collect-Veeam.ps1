param([Parameter(Mandatory=$true)][string]$Since)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
Import-Module Veeam.Backup.PowerShell -ErrorAction Stop
Connect-VBRServer -Server localhost -ErrorAction Stop | Out-Null
$issues = [Collections.Generic.List[string]]::new()
$sinceDate = [DateTimeOffset]::Parse($Since).UtcDateTime
function Get-Value($Object, [string[]]$Names, $Default = $null) {
    foreach ($name in $Names) { if ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$name] -and $null -ne $Object.$name) { return $Object.$name } }
    return $Default
}
function Convert-Date($Value) {
    if ($null -eq $Value) { return $null }
    try { $date = [DateTimeOffset]$Value; if ($date.Year -lt 2000) { return $null }; return $date.ToUniversalTime().ToString('o') } catch { return $null }
}
function Get-Jobs([string]$Command, [string]$Category) {
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) { return @() }
    try { return @(& $Command -ErrorAction Stop | ForEach-Object { @{ Job = $_; Category = $Category } }) }
    catch { $issues.Add("$Category discovery failed: $($_.Exception.Message)"); return @() }
}
try {
    $discovered = @(Get-Jobs 'Get-VBRJob' 'VM Backups') + @(Get-Jobs 'Get-VBRBackupCopyJob' 'BackupCopy') + @(Get-Jobs 'Get-VBRComputerBackupJob' 'AgentBackup') + @(Get-Jobs 'Get-VBRComputerBackupCopyJob' 'AgentBackupCopy') + @(Get-Jobs 'Get-VBRTapeJob' 'Tape') + @(Get-Jobs 'Get-VBRUnstructuredBackupJob' 'FileBackup') + @(Get-Jobs 'Get-VBRSureBackupJob' 'SureBackup')
    $allSessions = [Collections.Generic.List[object]]::new()
    foreach ($command in @('Get-VBRBackupSession','Get-VBRComputerBackupJobSession','Get-VBRCopyBackupSession','Get-VBRTapeBackupSession','Get-VBRUnstructuredBackupSession','Get-VBRSureBackupSession')) {
        if (Get-Command $command -ErrorAction SilentlyContinue) {
            try { foreach ($session in @(& $command -ErrorAction Stop)) { $allSessions.Add($session) } }
            catch { $issues.Add("$command history unavailable: $($_.Exception.Message)") }
        }
    }
    $runs = [Collections.Generic.List[object]]::new()
    $uniqueRuns = @{}
    foreach ($session in $allSessions) {
        $id = [string](Get-Value $session @('Id','Uid'))
        $jobId = [string](Get-Value $session @('JobId'))
        $start = Get-Value $session @('CreationTimeUTC','CreationTime','StartTime')
        if (-not $id -or -not $jobId -or -not $start -or $uniqueRuns.ContainsKey($id)) { continue }
        if (([DateTime]$start).ToUniversalTime() -lt $sinceDate -and [string](Get-Value $session @('State','Status')) -notin @('Working','Running','Starting')) { continue }
        $uniqueRuns[$id] = $true
        $tasks = @()
        try {
            $tasks = @(Get-VBRTaskSession -Session $session -ErrorAction Stop | ForEach-Object {
                $progress = Get-Value $_ @('Progress')
                @{
                    name = [string](Get-Value $_ @('Name') 'Unknown')
                    result = [string](Get-Value $_ @('Status','Result') 'Unknown')
                    startedAt = Convert-Date (Get-Value $progress @('StartTimeLocal','StartTime'))
                    endedAt = Convert-Date (Get-Value $progress @('StopTimeLocal','StopTime'))
                    message = [string](Get-Value $_ @('Reason','Error','Description'))
                }
            })
        } catch { $issues.Add("Task details unavailable for session ${id}: $($_.Exception.Message)") }
        $runs.Add(@{ id=$id;jobId=$jobId;startedAt=(Convert-Date $start);endedAt=(Convert-Date (Get-Value $session @('EndTimeUTC','EndTime')));result=[string](Get-Value $session @('Result') 'Unknown');message=[string](Get-Value $session @('Reason','Description'));vms=$tasks })
    }
    $jobs = [Collections.Generic.List[object]]::new(); $seen = @{}
    foreach ($entry in $discovered) {
        $job = $entry.Job; $id = [string](Get-Value $job @('Id','Uid')); if (-not $id -or $seen.ContainsKey($id)) { continue }; $seen[$id]=$true
        $schedule = Get-Value $job @('ScheduleOptions','Schedule')
        if (Get-Command Get-VBRJobScheduleOptions -ErrorAction SilentlyContinue) { try { $schedule=Get-VBRJobScheduleOptions -Job $job -ErrorAction Stop } catch { } }
        $auto = [bool](Get-Value $job @('IsScheduleEnabled') (Get-Value $schedule @('RunAutomatically','Enabled') $false))
        $disabled = [bool](Get-Value $job @('IsDisabled') $false)
        if ($null -ne $job.PSObject.Properties['IsEnabled']) { $disabled = -not [bool]$job.IsEnabled }
        $sessions = @($allSessions | Where-Object { [string](Get-Value $_ @('JobId')) -eq $id } | Sort-Object -Property CreationTime -Descending)
        $last = $sessions | Select-Object -First 1
        $next = Convert-Date (Get-Value $schedule @('NextRun','NextRunTime') (Get-Value $job @('NextRun','NextRunTime')))
        # Exclude mutable NextRun from the fingerprint: otherwise a missed expectation could be erased.
        $scheduleText = if ($null -eq $schedule) { '' } else { ($schedule | Select-Object * -ExcludeProperty NextRun,NextRunTime | ConvertTo-Json -Depth 10 -Compress) }
        $hash = [Security.Cryptography.SHA256]::Create(); $fingerprint = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($scheduleText))).Replace('-',''); $hash.Dispose()
        $scheduleIssue = $null
        if ($auto -and -not $next) { $scheduleIssue='PowerShell did not return the next scheduled run. Overdue detection requires a valid schedule baseline.' }
        $jobs.Add(@{id=$id;name=[string](Get-Value $job @('Name') 'Unnamed');type=[string](Get-Value $job @('JobType','Type') $entry.Category);enabled=(-not $disabled);scheduled=$auto;scheduleFingerprint=$fingerprint;scheduleDescription=$(if($auto){'Scheduled'}else{'Manual / schedule disabled'});scheduleIssue=$scheduleIssue;nextRun=$next;lastStart=(Convert-Date (Get-Value $last @('CreationTimeUTC','CreationTime')));running=([string](Get-Value $last @('State','Status')) -in @('Working','Running','Starting'));objectCount=0})
    }
    $repositories = @()
    # Use supported REST for repository capacity unless the installed PowerShell repository object exposes it.
    try {
        $repositories = @(Get-VBRBackupRepository -ErrorAction Stop | ForEach-Object {
            $container = Get-Value $_ @('Info','Container')
            $capacity = Get-Value $_ @('Capacity') (Get-Value $container @('CachedTotalSpace','TotalSpace'))
            $free = Get-Value $_ @('FreeSpace') (Get-Value $container @('CachedFreeSpace','FreeSpace'))
            if ($null -ne $capacity -and $null -ne $capacity.PSObject.Properties['InBytes']) { $capacity=$capacity.InBytes }
            if ($null -ne $free -and $null -ne $free.PSObject.Properties['InBytes']) { $free=$free.InBytes }
            @{id=[string]$_.Id;name=[string]$_.Name;type=[string]$_.Type;capacityBytes=$capacity;freeBytes=$free}
        })
        if (@($repositories | Where-Object { $null -eq $_.capacityBytes }).Count -gt 0) { $issues.Add('Some repository capacity is unavailable through this PowerShell object model. Use REST collection for capacity where supported.') }
    } catch { $issues.Add("Repository discovery failed: $($_.Exception.Message)") }
    $service = Join-Path $env:ProgramFiles 'Veeam\Backup and Replication\Backup\Veeam.Backup.Service.exe'
    $version = if (Test-Path $service) { (Get-Item $service).VersionInfo.FileVersion } else { $null }
    @{reportId=[guid]::NewGuid().ToString('N');collectedAt=[DateTimeOffset]::UtcNow.ToString('o');serverName=$env:COMPUTERNAME;veeamVersion=$version;inventoryComplete=($issues.Count -eq 0);collectionSucceeded=$true;jobs=@($jobs.ToArray());runs=@($runs.ToArray());repositories=@($repositories);issues=@($issues.ToArray())} | ConvertTo-Json -Depth 30 -Compress
} finally { Disconnect-VBRServer -ErrorAction SilentlyContinue | Out-Null }
