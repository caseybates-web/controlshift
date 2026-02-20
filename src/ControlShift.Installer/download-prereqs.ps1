# download-prereqs.ps1
# Downloads ViGEmBus and HidHide installers from their GitHub Releases.
# Pinned to specific versions for reproducible builds.

$ErrorActionPreference = "Stop"

$prereqDir = Join-Path $PSScriptRoot "prereqs"
New-Item -ItemType Directory -Force -Path $prereqDir | Out-Null

# ViGEmBus 1.22.0
$vigemUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"
$vigemPath = Join-Path $prereqDir "ViGEmBus_Setup.exe"
if (-not (Test-Path $vigemPath)) {
    Write-Host "Downloading ViGEmBus 1.22.0..."
    Invoke-WebRequest -Uri $vigemUrl -OutFile $vigemPath -UseBasicParsing
    Write-Host "  -> $vigemPath"
} else {
    Write-Host "ViGEmBus already downloaded: $vigemPath"
}

# HidHide 1.4.192
$hidhideUrl = "https://github.com/nefarius/HidHide/releases/download/v1.4.192.0/HidHide_1.4.192_x64.exe"
$hidhidePath = Join-Path $prereqDir "HidHide_Setup.exe"
if (-not (Test-Path $hidhidePath)) {
    Write-Host "Downloading HidHide 1.4.192..."
    Invoke-WebRequest -Uri $hidhideUrl -OutFile $hidhidePath -UseBasicParsing
    Write-Host "  -> $hidhidePath"
} else {
    Write-Host "HidHide already downloaded: $hidhidePath"
}

Write-Host "All prerequisites downloaded."
