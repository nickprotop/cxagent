# cxagent Installer for Windows
# Usage: irm https://raw.githubusercontent.com/nickprotop/cxagent/master/install.ps1 | iex
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

$ErrorActionPreference = "Stop"

$repo = "nickprotop/cxagent"
$installDir = "$env:LOCALAPPDATA\cxagent"

Write-Host "Installing cxagent..." -ForegroundColor Cyan

# Detect architecture
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
    "X64"   { $binary = "cxagent-win-x64.exe" }
    "Arm64" { $binary = "cxagent-win-arm64.exe" }
    default {
        Write-Host "Error: Unsupported architecture: $arch" -ForegroundColor Red
        exit 1
    }
}

# Get latest release
Write-Host "Fetching latest release..."
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest"
$version = $release.tag_name -replace '^v', ''
$asset = $release.assets | Where-Object { $_.name -eq $binary }

if (-not $asset) {
    Write-Host "Error: Binary '$binary' not found in release $($release.tag_name)" -ForegroundColor Red
    exit 1
}

Write-Host "Latest version: $version"

# Create directories
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Download binary
Write-Host "Downloading $binary..."
$outputPath = Join-Path $installDir "cxagent.exe"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $outputPath

# Download uninstaller
$uninstallUrl = "https://raw.githubusercontent.com/$repo/master/uninstall.ps1"
$uninstallPath = Join-Path $installDir "cxagent-uninstall.ps1"
Invoke-WebRequest -Uri $uninstallUrl -OutFile $uninstallPath

# Add to PATH if not already there
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$installDir;$userPath", "User")
    Write-Host "Added $installDir to user PATH" -ForegroundColor Green
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  cxagent v$version installed!" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Binary:  $outputPath"
Write-Host ""
Write-Host "  Run:     cxagent"
Write-Host "  Remove:  cxagent-uninstall.ps1"
Write-Host ""
Write-Host "  Note: Restart your terminal for PATH changes to take effect." -ForegroundColor Yellow
