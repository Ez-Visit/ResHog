#Requires -RunAsAdministrator
<#
.SYNOPSIS
    ResHog uninstallation script
.DESCRIPTION
    Uninstalls the ResHog service and UI client.
.PARAMETER KeepData
    Keep the data directory (database, logs, reports).
.EXAMPLE
    .\uninstall.ps1
.EXAMPLE
    .\uninstall.ps1 -KeepData
#>

param(
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$ServiceName = "ResHog"
$InstallDir = "C:\Program Files\ResHog"
$DataDir = "C:\ProgramData\ResHog"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  ResHog Uninstaller" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Stop service
Write-Host "[1/4] Stopping service..." -ForegroundColor Green
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    Write-Host "    -> Service stopped"
} else {
    Write-Host "    -> Service not found, skipping" -ForegroundColor Yellow
}

# 2. Delete service
Write-Host "[2/4] Deleting Windows service..." -ForegroundColor Green
if ($service) {
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "    -> Service deleted"
}

# 3. Delete shortcuts
Write-Host "[3/4] Deleting shortcuts..." -ForegroundColor Green
$shortcuts = @(
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ResHog.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\ResHog.lnk")
)
foreach ($path in $shortcuts) {
    if (Test-Path $path) {
        Remove-Item $path -Force
    }
}
Write-Host "    -> Shortcuts deleted"

# 4. Delete files
Write-Host "[4/4] Deleting program files..." -ForegroundColor Green

# Delete install directory
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Write-Host "    -> $InstallDir deleted"
}

# Delete data directory
if (-not $KeepData) {
    if (Test-Path $DataDir) {
        Write-Host ""
        Write-Host "[!] About to delete data directory: $DataDir" -ForegroundColor Yellow
        Write-Host "    Contains: database, logs, reports (irreversible)" -ForegroundColor Yellow
        $confirm = Read-Host "    Confirm deletion? (y/N)"
        if ($confirm -eq "y" -or $confirm -eq "Y") {
            Remove-Item -Recurse -Force $DataDir
            Write-Host "    -> $DataDir deleted"
        } else {
            Write-Host "    -> Data directory kept" -ForegroundColor Yellow
            $KeepData = $true
        }
    }
} else {
    Write-Host "    -> Data directory kept (-KeepData)" -ForegroundColor Yellow
}

# Done
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Uninstallation complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
if ($KeepData -and (Test-Path $DataDir)) {
    Write-Host "Data kept at: $DataDir"
}
Write-Host ""
