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

# 安装日志文件（setup.exe 吞掉了 stdout，用文件日志排查安装失败点）
$InstallLog = Join-Path $DataDir "logs\install.log"
function Write-InstallLog([string]$Message) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$ts] $Message"
    try { Add-Content -Path $InstallLog -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch {}
}

# 步骤日志辅助函数
$stepCount = 0
function Write-Step([string]$title) {
    $script:stepCount++
    Write-Host ""
    Write-Host "[$script:stepCount/$script:totalSteps] $title" -ForegroundColor Green
    Write-Host "    (Step $script:stepCount)" -ForegroundColor DarkGray
    Write-InstallLog "STEP $script:stepCount: $title"
}

# 统计总步骤数（用于日志标记）
$totalSteps = 9

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  ResHog Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

Write-InstallLog "=== Install.ps1 started ==="
Write-InstallLog "InstallDir=$InstallDir"
Write-InstallLog "DataDir=$DataDir"
Write-InstallLog "ServiceExe=$ServiceExe"

try {

# 1. Remove existing service if present
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "[!] Existing ResHog service found, removing old version..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    # 等待服务真正停止 + SQLite 释放数据库锁
    # 8GB 老库的 SQLite 进程可能需要 10+ 秒做 checkpoint 才能释放文件锁
    # 不等待会导致 migrate.ps1 遇到 "disk I/O error" 或 "database is locked"
    $waited = 0
    $maxWait = 30
    while ($waited -lt $maxWait) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $svc -or $svc.Status -eq "Stopped") { break }
        Start-Sleep -Seconds 1
        $waited++
    }
    Write-Host "    -> Service stopped (waited ${waited}s)"
    # 额外等待 2s 确保 SQLite 文件句柄完全释放
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
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

# 4. Run database migrations (idempotent, before service starts)
#
# 重要：迁移必须在服务停止后、新服务启动前执行
# - 服务停止后 SQLite 文件锁释放，migrate.ps1 才能安全操作数据库
# - 迁移失败必须中止安装，否则服务会以错误的 schema 启动导致数据不一致
#   （历史教训：migrate.ps1 失败被吞掉，导致 schema_version 报告 v3 但表结构是 v1）
Write-Step "Running database migrations"
$migrateScript = Join-Path $scriptDir "migrations\migrate.ps1"
$dbPath = Join-Path $DataDir "data.db"
Write-InstallLog "migrateScript=$migrateScript (exists=$(Test-Path $migrateScript))"
Write-InstallLog "dbPath=$dbPath (exists=$(Test-Path $dbPath))"
if (Test-Path $migrateScript) {
    # 把 migrations 目录也复制到安装目录（保持与 Payload 结构一致）
    $installMigrationsDir = Join-Path $InstallDir "migrations"
    if (-not (Test-Path $installMigrationsDir)) {
        New-Item -ItemType Directory -Force -Path $installMigrationsDir | Out-Null
    }
    Copy-Item "$scriptDir\migrations\*" $installMigrationsDir -Recurse -Force

    # 执行迁移脚本（独立于服务启动路径，确保一次性 schema 变更不进入服务初始化代码）
    # 重要：必须用 `powershell -File` 在子进程中运行，而不是 `& $migrateScript`
    # 因为 migrate.ps1 末尾有 `exit` 语句，PowerShell 的 `&` 调用会让 exit 终止
    # 整个 PowerShell 会话（包括 install.ps1），导致后续步骤不会执行。
    # 子进程方式下，exit 只退出子进程，install.ps1 继续执行后续步骤。
    Write-InstallLog "Launching migrate.ps1 in subprocess..."
    & powershell -ExecutionPolicy Bypass -File $migrateScript -DbPath $dbPath -MigrationDir $installMigrationsDir
    $migrateExit = $LASTEXITCODE
    Write-InstallLog "migrate.ps1 exited with code=$migrateExit"
    if ($migrateExit -ne 0) {
        Write-Host "    -> [!] Database migration FAILED (exit code $migrateExit)" -ForegroundColor Red
        Write-Host "    -> Aborting installation to prevent schema/version mismatch." -ForegroundColor Red
        Write-Host "    -> Manual recovery: stop service, delete data.db, re-run installer." -ForegroundColor Yellow
        Write-InstallLog "ABORT: migrate failed, exit $migrateExit"
        exit $migrateExit
    }
    Write-Host "    -> Database migrations applied successfully"
    Write-InstallLog "Migrate OK"
} else {
    Write-Host "    -> migrations\migrate.ps1 not found, skipping (fresh install or new database)" -ForegroundColor Yellow
    Write-InstallLog "migrate.ps1 NOT FOUND, skipping"
}

# 5. Generate config file from template
Write-Step "Configuring application"
$configPath = Join-Path $InstallDir "appsettings.json"
$templatePath = Join-Path $scriptDir "appsettings.template.json"
Write-InstallLog "templatePath=$templatePath (exists=$(Test-Path $templatePath))"
if (-not (Test-Path $templatePath)) {
    $templatePath = Join-Path $InstallDir "appsettings.template.json"
    Write-InstallLog "fallback templatePath=$templatePath (exists=$(Test-Path $templatePath))"
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

# 6. Register Windows service
Write-Step "Registering Windows service"
$binPath = "`"$ServiceExe`""
Write-InstallLog "sc.exe create binPath=$binPath"
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ResHog Resource Monitor" 2>&1 | Out-Null
$createExit = $LASTEXITCODE
Write-InstallLog "sc.exe create exit=$createExit"
if ($createExit -ne 0) { throw "sc.exe create failed with exit code $createExit (service may already exist)" }

sc.exe description $ServiceName "Monitors CPU, memory, and disk I/O usage of all processes for optimization analysis." 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE" }

# 7. Configure service failure recovery (auto-restart on crash)
Write-Step "Configuring failure recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }
Write-Host "    -> Auto-restart on crash (30s/60s/120s)"

# 8. Start service
Write-Step "Starting service"
Write-InstallLog "sc.exe start $ServiceName"
sc.exe start $ServiceName 2>&1 | Out-Null
$startExitCode = $LASTEXITCODE
Write-InstallLog "sc.exe start exit=$startExitCode"
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

# 9. Create shortcuts
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
    Write-InstallLog "UI client not found, skipping shortcuts"
}

# Done
Write-InstallLog "=== Install.ps1 completed, status=$status ==="
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

} catch {
    Write-InstallLog "FATAL EXCEPTION: $_"
    Write-InstallLog "Stack: $($_.ScriptStackTrace)"
    Write-Host ""
    Write-Host "[!] INSTALL FAILED: $_" -ForegroundColor Red
    Write-Host "    See install log: $InstallLog" -ForegroundColor Yellow
    exit 1
}

Write-InstallLog "Install.ps1 exiting with code 0"
exit 0
