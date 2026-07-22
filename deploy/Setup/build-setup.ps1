# build-setup.ps1 — 一句话重构 setup.exe
# 用法: .\build-setup.ps1
# 自动执行 dotnet publish 生成 Service / UI 单文件产物，然后打包 setup.exe

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root
$root = Split-Path -Parent $root

$release = "$root\artifacts\release\ResHog-0.2.2-win-x64"
$payload = "$root\deploy\SetupUI\Payload"
$zip = "$root\deploy\SetupUI\Payload.zip"

Write-Host "=== Build setup.exe ===" -ForegroundColor Cyan

# 0. Publish Service and UI (self-contained, single-file, win-x64)
# csproj 中已配置 PublishSingleFile=true + SelfContained=true，只需 -r win-x64 触发
Write-Host "[0/5] Publish Service and UI (self-contained single-file)..." -ForegroundColor Yellow

dotnet publish "$root\src\ResHog.Service\ResHog.Service.csproj" -c Release -r win-x64 --self-contained true -o "$release\service"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Service failed with exit code $LASTEXITCODE" }

dotnet publish "$root\src\ResHog.UI\ResHog.UI.csproj" -c Release -r win-x64 --self-contained true -o "$release\ui"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish UI failed with exit code $LASTEXITCODE" }

Write-Host "    -> Service published to $release\service\"
Write-Host "    -> UI published to $release\ui\"

# 1. Copy latest binaries to payload
Write-Host "[1/5] Copy latest binaries to payload..." -ForegroundColor Yellow
Remove-Item "$payload\*" -Recurse -Force -ErrorAction SilentlyContinue
New-Item "$payload\service" -ItemType Directory -Force | Out-Null
New-Item "$payload\ui" -ItemType Directory -Force | Out-Null
New-Item "$payload\migrations" -ItemType Directory -Force | Out-Null

# Service / UI binaries from release dir (dotnet publish output)
# -Force is required: existing files from previous build would silently fail without it
Copy-Item "$release\service\ResHog.Service.exe" "$payload\service" -Force
Copy-Item "$release\ui\ResHog.UI.exe" "$payload\ui" -Force

# Verify critical files were copied successfully
$serviceExe = "$payload\service\ResHog.Service.exe"
$uiExe = "$payload\ui\ResHog.UI.exe"
if (-not (Test-Path $serviceExe)) {
    throw "Failed to copy ResHog.Service.exe to payload"
}
if (-not (Test-Path $uiExe)) {
    throw "Failed to copy ResHog.UI.exe to payload"
}
Write-Host "    -> Service: $([math]::Round((Get-Item $serviceExe).Length/1MB,2))MB"
Write-Host "    -> UI: $([math]::Round((Get-Item $uiExe).Length/1MB,2))MB"

# Deploy scripts and config from deploy source dir (ensure latest version)
Copy-Item "$root\deploy\install.ps1" $payload -Force
Copy-Item "$root\deploy\uninstall.ps1" $payload -Force
Copy-Item "$root\deploy\appsettings.template.json" $payload -Force

# Database migration scripts (from deploy/migrations source dir)
# install.ps1 step 4 calls migrations\migrate.ps1 to apply one-time schema changes
Copy-Item "$root\deploy\migrations\*" "$payload\migrations" -Recurse -Force
Write-Host "    -> Migrations: $((Get-ChildItem "$payload\migrations").Count) files"

# 2. Create Payload.zip
Write-Host "[2/5] Create Payload.zip..." -ForegroundColor Yellow
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "       $([math]::Round((Get-Item $zip).Length/1MB))MB"

# 3. Build and publish Setup
Write-Host "[3/5] Build and publish..." -ForegroundColor Yellow
# 禁用 Avalonia 遥测（避免 TRAE Sandbox 拦截 buildtasks.log 写入）
$env:AVALONIA_BUILD_TELEMETRY_ENABLED = "0"
dotnet publish "$root\deploy\SetupUI\SetupUI.csproj" -c Release -o "$root\artifacts\publish\setup" /p:AvaloniaBuildTelemetryEnabled=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 4. Copy to release
Write-Host "[4/5] Copy to release..." -ForegroundColor Yellow
Copy-Item "$root\artifacts\publish\setup\ResHog_Setup.exe" "$release\setup.exe" -Force

$size = [math]::Round((Get-Item "$release\setup.exe").Length/1MB)
Write-Host ""
Write-Host "=== Done: setup.exe ($size MB) ===" -ForegroundColor Green
Write-Host "    $release\setup.exe"
