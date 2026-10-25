<#
.SYNOPSIS
    Installs SmartWiFi for the current user (no administrator rights needed).

.DESCRIPTION
    Copies SmartWiFi.exe to %LOCALAPPDATA%\Programs\SmartWiFi, adds a Start menu shortcut,
    and starts it. SmartWiFi then registers itself to start when you sign in.

    By default the latest release is downloaded from GitHub. Pass -Source to install an
    .exe you already have (for example one you built yourself).

.EXAMPLE
    irm https://raw.githubusercontent.com/sehrawaty/SmartWiFi/main/scripts/install.ps1 | iex

.EXAMPLE
    .\install.ps1 -Source .\SmartWiFi.exe
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $Version = "latest"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # makes Invoke-WebRequest much faster

$repo      = "sehrawaty/SmartWiFi"
$installTo = Join-Path $env:LOCALAPPDATA "Programs\SmartWiFi"
$exe       = Join-Path $installTo "SmartWiFi.exe"

Write-Host ""
Write-Host "  SmartWiFi installer" -ForegroundColor Cyan
Write-Host ""

# 1. Get the exe.
if (-not $Source -and $PSScriptRoot) {
    # Run from a folder that already has the exe next to it (e.g. an extracted release).
    $local = Join-Path $PSScriptRoot "SmartWiFi.exe"
    if (Test-Path $local) { $Source = $local }
}

$downloaded = $null
if (-not $Source) {
    $url = if ($Version -eq "latest") {
        "https://github.com/$repo/releases/latest/download/SmartWiFi.exe"
    } else {
        "https://github.com/$repo/releases/download/$Version/SmartWiFi.exe"
    }
    $downloaded = Join-Path $env:TEMP "SmartWiFi-$([guid]::NewGuid().ToString('N')).exe"
    Write-Host "  Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $downloaded -UseBasicParsing
    $Source = $downloaded
}

if (-not (Test-Path $Source)) { throw "Can't find $Source" }

# 2. Stop a running copy so the file can be replaced.
Get-Process -Name "SmartWiFi" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  Stopping the running SmartWiFi"
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_.Kill() }
}

# 3. Copy it into place.
New-Item -ItemType Directory -Force -Path $installTo | Out-Null
Copy-Item -Path $Source -Destination $exe -Force
Unblock-File -Path $exe
if ($downloaded) { Remove-Item $downloaded -Force -ErrorAction SilentlyContinue }
Write-Host "  Installed to $exe"

# 4. Start menu shortcut.
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$lnk = Join-Path $startMenu "SmartWiFi.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installTo
$shortcut.Description = "Keeps this laptop online: hotspot first, hostel Wi-Fi as fallback"
$shortcut.Save()
Write-Host "  Added a Start menu shortcut"

# 5. Run it.
Start-Process -FilePath $exe
Write-Host ""
Write-Host "  Done. SmartWiFi is in the tray, next to the clock (click ^ if you don't see it)." -ForegroundColor Green
Write-Host "  The first time, its Settings window opens so you can add your networks."
Write-Host ""
