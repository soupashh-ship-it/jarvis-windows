[CmdletBinding()]
param(
    [switch]$StartWithWindows,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = [System.IO.Path]::GetFullPath((Join-Path $root "dist\Jarvis"))
$programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs"))
$target = [System.IO.Path]::GetFullPath((Join-Path $programsRoot "Jarvis"))
$exe = Join-Path $target "Jarvis.exe"
$programsPrefix = $programsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($programsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Install path must be a child of '$programsRoot'."
}

if (-not (Test-Path -LiteralPath (Join-Path $dist "Jarvis.exe") -PathType Leaf)) {
    Write-Host "No published build found; building first." -ForegroundColor Yellow
    & (Join-Path $root "build.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Build failed; the existing installation was not changed." }
}

$running = Get-Process Jarvis -ErrorAction SilentlyContinue
if ($running) {
    throw "JARVIS is running. Quit it from the tray before installing an update."
}

if (Test-Path -LiteralPath $target) {
    $targetItem = Get-Item -LiteralPath $target -Force
    if (($targetItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Install target is a link or mount point; refusing to replace '$target'."
    }
}

$installId = [guid]::NewGuid().ToString("N")
$stage = "$target.install-$installId"
$backup = "$target.backup-$installId"
$shortcutTemp = $null
foreach ($path in @($stage, $backup)) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($programsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Transaction path escaped the programs directory: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) { throw "Transaction path already exists: $fullPath" }
}

New-Item -ItemType Directory -Path $programsRoot -Force | Out-Null
Write-Host "Staging JARVIS under $programsRoot" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $stage | Out-Null
$oldMoved = $false
$newActivated = $false
try {
    Copy-Item -Path (Join-Path $dist "*") -Destination $stage -Recurse -Force
    $stagedExe = Join-Path $stage "Jarvis.exe"
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "The staged package does not contain Jarvis.exe."
    }
    if ((Get-Item -LiteralPath $stagedExe).Length -le 0) {
        throw "The staged Jarvis.exe is empty."
    }

    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $backup
        $oldMoved = $true
    }
    try {
        Move-Item -LiteralPath $stage -Destination $target
        $newActivated = $true
    }
    catch {
        if ($oldMoved -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $backup -Destination $target
            $oldMoved = $false
        }
        throw
    }

    $startMenu = [Environment]::GetFolderPath("Programs")
    $shortcutPath = Join-Path $startMenu "JARVIS.lnk"
    $shortcutTemp = Join-Path $startMenu "JARVIS.$installId.tmp.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutTemp)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "JARVIS voice assistant"
    $shortcut.Save()
    Move-Item -LiteralPath $shortcutTemp -Destination $shortcutPath -Force
    Write-Host "Start-menu shortcut: $shortcutPath" -ForegroundColor DarkGray

    if ($StartWithWindows) {
        $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
        Set-ItemProperty -Path $runKey -Name "JARVIS" -Value "`"$exe`""
        Write-Host "Autostart enabled." -ForegroundColor DarkGray
    }

    Write-Host "Installed: $exe" -ForegroundColor Green
    if (-not $NoLaunch) { Start-Process -FilePath $exe }
}
catch {
    if ($newActivated -and (Test-Path -LiteralPath $target)) {
        # Keep a failed activation for diagnosis, then restore the prior working install if present.
        $failed = "$target.failed-$installId"
        Move-Item -LiteralPath $target -Destination $failed
    }
    if ($oldMoved -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
        Move-Item -LiteralPath $backup -Destination $target
    }
    throw
}
finally {
    if ($shortcutTemp -and (Test-Path -LiteralPath $shortcutTemp)) {
        Remove-Item -LiteralPath $shortcutTemp -Force
    }
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

if ($oldMoved -and (Test-Path -LiteralPath $backup)) {
    try { Remove-Item -LiteralPath $backup -Recurse -Force }
    catch { Write-Warning "JARVIS was installed, but the previous version remains at '$backup'." }
}
