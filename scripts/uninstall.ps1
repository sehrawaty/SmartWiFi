<#
.SYNOPSIS
    Removes SmartWiFi for the current user.

.DESCRIPTION
    Stops SmartWiFi, removes it from startup and the Start menu, and deletes the program.
    Settings, logs and data usage are kept unless you pass -RemoveSettings.
    Wi-Fi profiles SmartWiFi created stay in Windows (Settings → Network → Wi-Fi → Manage known networks).
#>
[CmdletBinding()]
param([switch] $RemoveSettings)

$ErrorActionPreference = "Stop"

Get-Process -Name "SmartWiFi" -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_.Kill() }
}

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SmartWiFi" -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\SmartWiFi.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA "Programs\SmartWiFi") -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveSettings) {
    Remove-Item (Join-Path $env:APPDATA "SmartWiFi") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA "SmartWiFi") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "SmartWiFi and its settings were removed."
} else {
    Write-Host "SmartWiFi was removed. Settings are kept in %APPDATA%\SmartWiFi (run with -RemoveSettings to delete them)."
}
