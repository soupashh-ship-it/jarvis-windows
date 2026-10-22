[CmdletBinding()]
param(
    [string]$ReleasePackage = (Join-Path $PSScriptRoot "..\dist\Jarvis")
)

$ErrorActionPreference = "Stop"

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceInstaller = Join-Path $repository "install.ps1"
$tempRoot = [IO.Path]::GetFullPath($env:TEMP)
$testRoot = Join-Path $tempRoot ("JarvisInstallerAudit-" + [guid]::NewGuid().ToString("N"))
$localAppData = Join-Path $testRoot "LocalAppData"
$projectRoot = Join-Path $testRoot "Project"
$distribution = Join-Path $projectRoot "dist\Jarvis"
$releasePackage = [IO.Path]::GetFullPath($ReleasePackage)
$testStartMenu = Join-Path $localAppData "TestStartMenu"
$testInstaller = Join-Path $projectRoot "install.ps1"

New-Item -ItemType Directory -Path $distribution -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $releasePackage "Jarvis.exe") -PathType Leaf)) {
    throw "Build the release package before running this test."
}
Copy-Item -Path (Join-Path $releasePackage "*") -Destination $distribution -Recurse -Force
$releaseExeHash = (Get-FileHash -LiteralPath (Join-Path $releasePackage "Jarvis.exe") -Algorithm SHA256).Hash
$releaseFileCount = (Get-ChildItem -LiteralPath $releasePackage -File -Recurse -Force | Measure-Object).Count

$installerText = [IO.File]::ReadAllText($sourceInstaller)
$startMenuLine = '$startMenu = [Environment]::GetFolderPath("Programs")'
$runningProcessLine = '$running = Get-Process Jarvis -ErrorAction SilentlyContinue'
if (-not $installerText.Contains($startMenuLine) -or -not $installerText.Contains($runningProcessLine)) {
    throw "install.ps1 changed; review and update this isolated test before running it."
}

# The test copy redirects writes to an isolated LocalAppData and shortcut folder. It also avoids
# treating the user's running Jarvis instance as a blocker for this isolated transaction test.
$testInstallerText = $installerText.
    Replace($startMenuLine, '$startMenu = Join-Path $env:LOCALAPPDATA "TestStartMenu"').
    Replace($runningProcessLine, '$running = Get-Process JarvisInstallerAudit -ErrorAction SilentlyContinue')
[IO.File]::WriteAllText($testInstaller, $testInstallerText, [Text.UTF8Encoding]::new($false))

$originalLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $localAppData
    New-Item -ItemType Directory -Path $testStartMenu -Force | Out-Null
    & $testInstaller -NoLaunch

    $programs = Join-Path $localAppData "Programs"
    $target = Join-Path $programs "Jarvis"
    $installedExe = Join-Path $target "Jarvis.exe"
    $shortcut = Join-Path $testStartMenu "JARVIS.lnk"
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw "Fresh install did not activate." }
    if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne $releaseExeHash) {
        throw "Fresh install executable differs from the release package."
    }
    if ((Get-ChildItem -LiteralPath $target -File -Recurse -Force | Measure-Object).Count -ne $releaseFileCount) {
        throw "Fresh install did not copy the complete release package."
    }
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw "Fresh install did not create the shortcut." }

    [IO.File]::WriteAllText((Join-Path $distribution "Jarvis.exe"), "second-release")
    $testRootFull = [IO.Path]::GetFullPath($testRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
                    [IO.Path]::DirectorySeparatorChar
    $shortcutFolderFull = [IO.Path]::GetFullPath($testStartMenu)
    if (-not $shortcutFolderFull.StartsWith($testRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the isolated test root: $shortcutFolderFull"
    }
    [IO.Directory]::Delete($shortcutFolderFull, $true)

    $upgradeFailure = $null
    try { & $testInstaller -NoLaunch }
    catch { $upgradeFailure = $_.Exception.Message }
    if (-not $upgradeFailure) { throw "Expected shortcut failure did not trigger the upgrade rollback path." }
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw "Rollback did not restore the previous install." }
    if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne $releaseExeHash) {
        throw "Rollback restored the wrong executable."
    }

    $failedPackages = @(Get-ChildItem -LiteralPath $programs -Directory -Filter "Jarvis.failed-*")
    $stages = @(Get-ChildItem -LiteralPath $programs -Directory -Filter "Jarvis.install-*")
    $backups = @(Get-ChildItem -LiteralPath $programs -Directory -Filter "Jarvis.backup-*")
    if ($failedPackages.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $failedPackages[0].FullName "Jarvis.exe"))) {
        throw "The failed promoted package was not retained for diagnosis."
    }
    if ($stages.Count -ne 0 -or $backups.Count -ne 0) { throw "Rollback left a stage or backup directory behind." }

    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) +
                  [IO.Path]::DirectorySeparatorChar
    if (-not $fullTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $fullTestRoot) -notmatch "^JarvisInstallerAudit-[0-9a-f]{32}$") {
        throw "Refusing cleanup because the test directory did not pass validation: $fullTestRoot"
    }
    [IO.Directory]::Delete($fullTestRoot, $true)

    [PSCustomObject]@{
        FreshInstall = "passed"
        CompletePackageCopied = $releaseFileCount
        UpgradeRollback = "passed"
        PreviousVersionRestored = $releaseExeHash
        FailedPackageRetained = $failedPackages[0].Name
        InjectedFailure = $upgradeFailure
        TemporaryTestData = "removed after verification"
    } | Format-List
}
catch {
    Write-Warning "Installer test evidence preserved at $testRoot"
    throw
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
}
