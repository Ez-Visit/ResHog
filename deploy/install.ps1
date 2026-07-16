#Requires -RunAsAdministrator
<#
.SYNOPSIS
    ResHog installation script
.DESCRIPTION
    Installs the ResHog service and UI client. Copies binaries to the install
    directory, registers the Windows service, configures auto-restart on failure,
    and optionally creates Start Menu shortcuts.
.PARAMETER InstallDir
    Program install directory. Default: C:\Program Files\ResHog
.PARAMETER DataDir
    Data directory (database, logs, reports). Default: C:\ProgramData\ResHog
.PARAMETER NoAutoStart
    Skip configuring the UI client for auto-start at login.
.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -InstallDir "D:\ResHog" -DataDir "D:\ResHogData"
#>

param(
    [string]$InstallDir = "C:\Program Files\ResHog",
    [string]$DataDir = "C:\ProgramData\ResHog",
    [switch]$NoAutoStart
)

$ErrorActionPreference = "Stop"
$ServiceName = "ResHog"
$ServiceExe = Join-Path $InstallDir "ResHog.Service.exe"

# 步骤日志辅助函数
$stepCount = 0
function Write-Step([string]$title) {
    $script:stepCount++
    Write-Host ""
    Write-Host "[$script:stepCount/$script:totalSteps] $title" -ForegroundColor Green
    Write-Host "    (Step $script:stepCount)" -ForegroundColor DarkGray
}

# 统计总步骤数（用于日志标记）
$totalSteps = 8

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  ResHog Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Remove existing service if present
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "[!] Existing ResHog service found, removing old version..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# 2. Create directories
Write-Step "Creating directories"
$dirs = @($InstallDir, $DataDir, "$DataDir\logs", "$DataDir\reports", "$InstallDir\UI")
foreach ($d in $dirs) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
Write-Host "    -> $InstallDir"
Write-Host "    -> $DataDir"

# 3. Copy files
Write-Step "Copying program files"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Copy service (supports both layouts: service/ subdirectory or flat structure)
if (Test-Path "$scriptDir\service") {
    Copy-Item "$scriptDir\service\*" $InstallDir -Recurse -Force
} elseif (Test-Path "$scriptDir\ResHog.Service.exe") {
    Copy-Item "$scriptDir\ResHog.Service.exe" $InstallDir -Force
    Copy-Item "$scriptDir\*.dll" $InstallDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$scriptDir\*.json" $InstallDir -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "[!] Service binary not found (expected service\ dir or ResHog.Service.exe)" -ForegroundColor Red
    exit 1
}

# Copy UI client (supports both layouts: ui/ subdirectory or flat structure)
if (Test-Path "$scriptDir\ui") {
    Copy-Item "$scriptDir\ui\*" "$InstallDir\UI" -Recurse -Force
} elseif (Test-Path "$scriptDir\ResHog.UI.exe") {
    Copy-Item "$scriptDir\ResHog.UI.exe" "$InstallDir\UI" -Force
} else {
    Write-Host "[!] UI client not found (expected ui\ dir or ResHog.UI.exe)" -ForegroundColor Yellow
    Write-Host "    Skipping UI client installation" -ForegroundColor Yellow
}

# 4. Generate config file from template
Write-Step "Configuring application"
$configPath = Join-Path $InstallDir "appsettings.json"
$templatePath = Join-Path $scriptDir "appsettings.template.json"
if (-not (Test-Path $templatePath)) {
    $templatePath = Join-Path $InstallDir "appsettings.template.json"
}

if (Test-Path $configPath) {
    Write-Host "    -> appsettings.json already exists, keeping current config"
} elseif (Test-Path $templatePath) {
    $config = Get-Content $templatePath -Raw
    # Replace data directory placeholder ({{DATA_DIR}} -> actual path, backslashes need escaping)
    $config = $config -replace '\{\{DATA_DIR\}\}', ($DataDir -replace '\\', '\\')
    $config | Set-Content $configPath -Encoding UTF8 -NoNewline
    Write-Host "    -> $configPath (from template)"
} else {
    Write-Host "    -> [!] Config template not found, service will use defaults" -ForegroundColor Yellow
}

# 5. Register Windows service
Write-Step "Registering Windows service"
$binPath = "`"$ServiceExe`""
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ResHog Resource Monitor" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE (service may already exist)" }

sc.exe description $ServiceName "Monitors CPU, memory, and disk I/O usage of all processes for optimization analysis." 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE" }

# 6. Configure service failure recovery (auto-restart on crash)
Write-Step "Configuring failure recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }
Write-Host "    -> Auto-restart on crash (30s/60s/120s)"

# 7. Start service
Write-Step "Starting service"
sc.exe start $ServiceName 2>&1 | Out-Null
$startExitCode = $LASTEXITCODE
if ($startExitCode -ne 0 -and $startExitCode -ne 1056) {
    # 1056 = service already running, which is fine
    Write-Host "    -> sc start returned exit code $startExitCode (service may still be starting)" -ForegroundColor Yellow
}

# Poll for up to 30s. Service init (DB load + Kestrel bind) can exceed a
# fixed 3s wait on large databases, leaving the status StartPending. Only
# treat Stopped as a real failure; tolerate StartPending until Running.
$started = $false
$pollSeconds = 30
for ($i = 0; $i -lt $pollSeconds; $i++) {
    Start-Sleep -Seconds 1
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) { continue }
    if ($svc.Status -eq "Running") { $started = $true; break }
    if ($svc.Status -eq "Stopped") { break }
}

$status = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
if ($status -eq "Running") {
    Write-Host "    -> Service started successfully" -ForegroundColor Green
} else {
    Write-Host "    -> [!] Service failed to start, status: $status" -ForegroundColor Red
    Write-Host "    -> Check logs at: $DataDir\logs\" -ForegroundColor Yellow
    Write-Host "    -> Debug mode: run '$ServiceExe --console' in a terminal" -ForegroundColor Yellow
}

# 8. Create shortcuts
Write-Step "Creating shortcuts"
$uiExe = Join-Path $InstallDir "UI\ResHog.UI.exe"
if (Test-Path $uiExe) {
    $startMenuPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ResHog.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenuPath)
    $shortcut.TargetPath = $uiExe
    $shortcut.IconLocation = $uiExe
    $shortcut.Description = "ResHog - Windows Resource Monitor"
    $shortcut.Save()
    Write-Host "    -> Start Menu shortcut created"

    # Auto-start at login
    if (-not $NoAutoStart) {
        $startupPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\ResHog.lnk"
        $shortcut = $shell.CreateShortcut($startupPath)
        $shortcut.TargetPath = $uiExe
        $shortcut.WindowStyle = 1
        $shortcut.Description = "ResHog Client (Auto Start)"
        $shortcut.Save()
        Write-Host "    -> Auto-start at login configured"
    }
} else {
    Write-Host "    -> [!] UI client not found, skipping shortcuts" -ForegroundColor Yellow
}

# Done
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Service status:  $status"
Write-Host "Install dir:     $InstallDir"
Write-Host "Data dir:        $DataDir"
Write-Host "API endpoint:    http://localhost:5180"
Write-Host "Log path:        $DataDir\logs\"
Write-Host ""
if (Test-Path $uiExe) {
    Write-Host "Launch UI:       Search 'ResHog' in Start Menu or run $uiExe"
}
Write-Host "Debug service:   Run '$ServiceExe --console' in a terminal"
Write-Host ""
