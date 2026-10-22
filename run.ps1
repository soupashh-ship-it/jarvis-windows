[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "Starting JARVIS ($Configuration)..." -ForegroundColor Cyan
dotnet run --project (Join-Path $root "src\Jarvis.App\Jarvis.App.csproj") -c $Configuration
