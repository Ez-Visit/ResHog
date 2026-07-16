# build-setup.ps1 — 一句话重构 setup.exe
# 用法: .\build-setup.ps1
# 前提: 已执行 dotnet publish 生成 Service 和 UI 产物

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root
$root = Split-Path -Parent $root

$release = "$root\artifacts\release\ResHog-0.2.2-win-x64"
$payload = "$root\deploy\SetupUI\Payload"
$zip = "$root\deploy\SetupUI\Payload.zip"

Write-Host "=== Build setup.exe ===" -ForegroundColor Cyan

# 1. 复制最新产物到 Payload
Write-Host "[1/4] Copy latest binaries to payload..." -ForegroundColor Yellow
Remove-Item "$payload\*" -Recurse -Force -ErrorAction SilentlyContinue
New-Item "$payload\service" -ItemType Directory -Force | Out-Null
New-Item "$payload\ui" -ItemType Directory -Force | Out-Null
Copy-Item "$release\install.ps1" $payload
Copy-Item "$release\uninstall.ps1" $payload
Copy-Item "$release\appsettings.template.json" $payload
Copy-Item "$release\service\ResHog.Service.exe" "$payload\service"
Copy-Item "$release\ui\ResHog.UI.exe" "$payload\ui"

# 2. 打包 Payload.zip
Write-Host "[2/4] Create Payload.zip..." -ForegroundColor Yellow
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "       $([math]::Round((Get-Item $zip).Length/1MB))MB"

# 3. 编译 + 发布 Setup
Write-Host "[3/4] Build and publish..." -ForegroundColor Yellow
dotnet publish "$root\deploy\SetupUI\SetupUI.csproj" -c Release -o "$root\artifacts\publish\setup"

# 4. 复制到 release
Write-Host "[4/4] Copy to release..." -ForegroundColor Yellow
Copy-Item "$root\artifacts\publish\setup\ResHog_Setup.exe" "$release\setup.exe" -Force

$size = [math]::Round((Get-Item "$release\setup.exe").Length/1MB)
Write-Host ""
Write-Host "=== Done: setup.exe ($size MB) ===" -ForegroundColor Green
Write-Host "    $release\setup.exe"
