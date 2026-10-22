[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [string]$Output = "dist\Jarvis",
    [switch]$RequireSignature,
    [string]$SigningCertificateThumbprint = $env:JARVIS_SIGN_CERT_THUMBPRINT,
    [string]$TimestampServer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$releaseCertificate = $null
if ($RequireSignature -and [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    throw "A trusted code-signing certificate thumbprint is required when -RequireSignature is used."
}
if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    $thumbprint = $SigningCertificateThumbprint -replace '\s', ''
    if ($thumbprint -notmatch '^(?i:[0-9a-f]{40})$') {
        throw "Signing certificate thumbprint must be a 40-character hexadecimal value."
    }
    $timestampUri = $null
    if ([string]::IsNullOrWhiteSpace($TimestampServer) -or
        -not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) {
        throw "Provide an HTTP or HTTPS timestamp server when signing the release."
    }

    $certificatePath = "Cert:\CurrentUser\My\$thumbprint"
    $releaseCertificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop
    if (-not $releaseCertificate.HasPrivateKey) {
        throw "The selected code-signing certificate has no private key."
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $ekuExtension = $releaseCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if (-not $ekuExtension) { throw "The selected certificate has no code-signing usage extension." }
    $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
        $ekuExtension, $ekuExtension.Critical)
    if (-not ($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq $codeSigningOid })) {
        throw "The selected certificate is not valid for code signing."
    }
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    if (-not $chain.Build($releaseCertificate)) {
        $chainStatus = ($chain.ChainStatus | ForEach-Object { $_.Status.ToString() } | Sort-Object -Unique) -join ', '
        throw "The selected certificate chain is not trusted for release signing: $chainStatus"
    }
    Write-Host "Release signing enabled with a trusted certificate." -ForegroundColor DarkGray
}

Push-Location $root
try {
    Write-Host "`n=== JARVIS build ===" -ForegroundColor Cyan
    Write-Host "Restoring..." -ForegroundColor DarkGray
    dotnet restore .\Jarvis.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }

    Write-Host "Building (Release)..." -ForegroundColor DarkGray
    dotnet build .\Jarvis.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    if (-not $SkipTests) {
        Write-Host "Testing..." -ForegroundColor DarkGray
        dotnet test .\tests\Jarvis.Tests\Jarvis.Tests.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    }

    $distRoot = [System.IO.Path]::GetFullPath((Join-Path $root "dist"))
    $outPath = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
    $distPrefix = $distRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $outPath.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output must be a dedicated directory below '$distRoot'."
    }

    $publishId = [guid]::NewGuid().ToString("N")
    $publishPath = "$outPath.publish-$publishId"
    $backupPath = "$outPath.backup-$publishId"
    New-Item -ItemType Directory -Path (Split-Path -Parent $outPath) -Force | Out-Null

    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "Publishing (win-x64, self-contained=$selfContained)..." -ForegroundColor DarkGray
    dotnet publish .\src\Jarvis.App\Jarvis.App.csproj -c Release -r win-x64 `
        --self-contained $selfContained -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$root=/_/Jarvis" -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    if (-not (Test-Path (Join-Path $publishPath "Jarvis.exe"))) { throw "publish did not produce Jarvis.exe" }
    $wakeWordAssembly = Join-Path $publishPath "NanoWakeWord.dll"
    $wakeModelExtractor = Join-Path $root "tools\WakeWordModelExtractor\bin\Release\net10.0\WakeWordModelExtractor.dll"
    if (-not (Test-Path -LiteralPath $wakeWordAssembly -PathType Leaf)) {
        throw "publish did not include NanoWakeWord.dll"
    }
    if (-not (Test-Path -LiteralPath $wakeModelExtractor -PathType Leaf)) {
        throw "Release build did not produce the wake-model extractor"
    }
    dotnet $wakeModelExtractor $wakeWordAssembly (Join-Path $publishPath "models")
    if ($LASTEXITCODE -ne 0) { throw "wake-word model extraction failed" }
    $requiredWakeModels = @(
        "alexa_v0.1.onnx", "embedding_model.onnx", "hey_jarvis_v0.1.onnx",
        "hey_marvin_v0.1.onnx", "hey_mycroft_v0.1.onnx", "melspectrogram.onnx"
    )
    foreach ($model in $requiredWakeModels) {
        $modelPath = Join-Path (Join-Path $publishPath "models") $model
        if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -or
            (Get-Item -LiteralPath $modelPath).Length -le 0) {
            throw "publish is missing a valid wake-word asset: $model"
        }
    }

    if ($releaseCertificate) {
        foreach ($name in @('Jarvis.exe', 'Jarvis.dll', 'Jarvis.Core.dll')) {
            $path = Join-Path $publishPath $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Signing input is missing from the package: $name"
            }
            $signature = Set-AuthenticodeSignature -FilePath $path -Certificate $releaseCertificate `
                -TimestampServer $TimestampServer -HashAlgorithm SHA256
            if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
                throw "Signing failed for ${name}: $($signature.StatusMessage)"
            }
        }
    }
    $hadPrevious = Test-Path $outPath
    if ($hadPrevious) { Move-Item -LiteralPath $outPath -Destination $backupPath }
    try {
        Move-Item -LiteralPath $publishPath -Destination $outPath
    }
    catch {
        if ($hadPrevious -and (Test-Path $backupPath) -and -not (Test-Path $outPath)) {
            Move-Item -LiteralPath $backupPath -Destination $outPath
        }
        throw
    }
    if (Test-Path $backupPath) { Remove-Item -LiteralPath $backupPath -Recurse -Force }

    $exe = Join-Path $outPath "Jarvis.exe"
    Write-Host "`nReady: $exe" -ForegroundColor Green
    Write-Host "Run it, or use .\install.ps1 to add a Start-menu shortcut." -ForegroundColor DarkGray
}
finally {
    if ($publishPath -and (Test-Path $publishPath)) { Remove-Item -LiteralPath $publishPath -Recurse -Force }
    Pop-Location
}

