[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\Jarvis"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "JARVIS.lnk"

Get-Process Jarvis -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $shortcutPath) { Remove-Item $shortcutPath -Force; Write-Host "Removed shortcut." }
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "JARVIS" -ErrorAction SilentlyContinue
if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed program files." }

if ($PurgeData) {
    $data = Join-Path $env:LOCALAPPDATA "Jarvis"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force; Write-Host "Removed settings, notes and logs." }
}
else {
    Write-Host "Settings, notes and logs kept in $(Join-Path $env:LOCALAPPDATA 'Jarvis'). Use -PurgeData to remove them." -ForegroundColor DarkGray
}
