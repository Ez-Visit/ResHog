<#
.SYNOPSIS
    ResHog data.db disk usage monitor script
.DESCRIPTION
    Checks data.db and WAL/SHM file sizes, queries service status via API,
    and warns when total disk usage exceeds threshold.
    Can run manually or via Windows Task Scheduler.
.PARAMETER DataDir
    ResHog data directory, default C:\ProgramData\ResHog
.PARAMETER ApiPort
    ResHog API port, default 5180
.PARAMETER WarnGb
    Warning threshold in GB, default 4
.PARAMETER CritGb
    Critical threshold in GB, default 6
.PARAMETER LogFile
    Optional. If specified, appends result to log file
.EXAMPLE
    .\check-db-size.ps1
    Manual check with default thresholds
.EXAMPLE
    .\check-db-size.ps1 -WarnGb 3.5 -LogFile C:\ProgramData\ResHog\logs\db-size-monitor.log
    Custom threshold with logging
.EXAMPLE
    # Windows Task Scheduler (daily at 09:00):
    schtasks /create /tn "ResHog DB Monitor" /tr "powershell -ExecutionPolicy Bypass -File C:\ProgramData\ResHog\scripts\check-db-size.ps1 -LogFile C:\ProgramData\ResHog\logs\db-size-monitor.log" /sc daily /st 09:00 /ru System
#>
param(
    [string]$DataDir = "C:\ProgramData\ResHog",
    [int]$ApiPort = 5180,
    [double]$WarnGb = 4.0,
    [double]$CritGb = 6.0,
    [string]$LogFile = ""
)

$ErrorActionPreference = "SilentlyContinue"

# === Helper functions ===
function Format-Gb {
    param([long]$Bytes)
    return "{0:N2} GB" -f ($Bytes / 1GB)
}

# === Main logic ===
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$output = @()
$output += "============================================================"
$output += "ResHog DB Monitor  |  $timestamp"
$output += "============================================================"

# 1. Check file sizes
$dbPath = Join-Path $DataDir "data.db"
$walPath = Join-Path $DataDir "data.db-wal"
$shmPath = Join-Path $DataDir "data.db-shm"

$totalSize = 0
$fileInfo = @()

if (Test-Path $dbPath) {
    $dbFile = Get-Item $dbPath
    $totalSize += $dbFile.Length
    $fileInfo += [PSCustomObject]@{ Name="data.db"; SizeBytes=$dbFile.Length; LastWrite=$dbFile.LastWriteTime }
} else {
    $output += "[ERROR] data.db not found at $dbPath"
}

if (Test-Path $walPath) {
    $walFile = Get-Item $walPath
    $totalSize += $walFile.Length
    $fileInfo += [PSCustomObject]@{ Name="data.db-wal"; SizeBytes=$walFile.Length; LastWrite=$walFile.LastWriteTime }
}

if (Test-Path $shmPath) {
    $shmFile = Get-Item $shmPath
    $totalSize += $shmFile.Length
    $fileInfo += [PSCustomObject]@{ Name="data.db-shm"; SizeBytes=$shmFile.Length; LastWrite=$shmFile.LastWriteTime }
}

$output += ""
$output += "--- File Sizes ---"
foreach ($f in $fileInfo) {
    $sizeGb = $f.SizeBytes / 1GB
    $output += ("  {0,-15} {1,12:N2} GB   (last write: {2:yyyy-MM-dd HH:mm})" -f $f.Name, $sizeGb, $f.LastWrite)
}
$output += ("  {0,-15} {1,12:N2} GB" -f "TOTAL:", ($totalSize/1GB))

# 2. Query service status via API
$output += ""
$output += "--- Service Status (API :$ApiPort) ---"
try {
    $health = Invoke-RestMethod "http://localhost:$ApiPort/api/health" -TimeoutSec 10
    $output += ("  Status:            {0}" -f $health.status)
    $output += ("  Version:           {0}" -f $health.version)
    $output += ("  Uptime:            {0:N0} seconds ({1:N1} hours)" -f $health.uptimeSeconds, ($health.uptimeSeconds/3600))
    $output += ("  SampleCount:       {0:N0}" -f $health.sampleCount)
    $output += ("  MonitoredProcs:    {0}" -f $health.monitoredProcesses)
} catch {
    $output += "  [WARNING] API not reachable: $($_.Exception.Message)"
}

# 3. Threshold check
$output += ""
$output += "--- Threshold Check ---"
$output += ("  Warn threshold: {0:N1} GB" -f $WarnGb)
$output += ("  Crit threshold: {0:N1} GB" -f $CritGb)
$output += ("  Current total:  {0:N2} GB" -f ($totalSize/1GB))

$alertLevel = "OK"
$alertColor = "Green"
if (($totalSize/1GB) -ge $CritGb) {
    $alertLevel = "CRITICAL"
    $alertColor = "Red"
} elseif (($totalSize/1GB) -ge $WarnGb) {
    $alertLevel = "WARNING"
    $alertColor = "Yellow"
}

$output += ""
$output += "============================================================"
$output += ("  RESULT: [{0}] Total disk usage: {1:N2} GB" -f $alertLevel, ($totalSize/1GB))
$output += "============================================================"

# 4. Output result
foreach ($line in $output) {
    if ($alertLevel -ne "OK") {
        Write-Host $line -ForegroundColor $alertColor
    } else {
        Write-Host $line
    }
}

# 5. Optional log file
if ($LogFile -ne "") {
    $logDir = Split-Path $LogFile -Parent
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    Add-Content -Path $LogFile -Value $output -Encoding UTF8
    Write-Host ""
    Write-Host "Log appended to: $LogFile" -ForegroundColor Cyan
}

# 6. Exit code (for Task Scheduler)
if ($alertLevel -eq "CRITICAL") {
    exit 2
} elseif ($alertLevel -eq "WARNING") {
    exit 1
} else {
    exit 0
}
