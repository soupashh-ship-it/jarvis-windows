# Jarvis for Windows: one-time setup. From the repo folder, run:
#   powershell -ExecutionPolicy Bypass -File install.ps1
# Safe to run again (it updates packages and re-registers the logon task).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 1. Install the official Antigravity CLI if needed. Google stores its sign-in in the user's credential store.
$agyDir = Join-Path $env:LOCALAPPDATA "agy\bin"
$agyPath = Join-Path $agyDir "agy.exe"
if (-not (Test-Path $agyPath)) {
    $agyCommand = Get-Command agy.exe -ErrorAction SilentlyContinue
    if ($agyCommand) { $agyPath = $agyCommand.Source }
}
if (-not (Test-Path $agyPath)) {
    Write-Host "Installing the official Google Antigravity CLI..."
    Invoke-Expression (Invoke-RestMethod "https://antigravity.google/cli/install.ps1")
    $agyPath = Join-Path $env:LOCALAPPDATA "agy\bin\agy.exe"
}
if (-not (Test-Path $agyPath)) { throw "Antigravity CLI wasn't installed. See https://antigravity.google/docs/cli/install/." }

# 2. Python 3.12 virtual environment and packages
if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw "The py launcher wasn't found. Install Python 3.12 from python.org first (keep 'py launcher' ticked)."
}
if (-not (Test-Path .venv)) { py -3.12 -m venv .venv; if ($LASTEXITCODE) { throw "Couldn't create the venv. Is Python 3.12 installed?" } }
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
if ($LASTEXITCODE) { throw "pip install failed, see above." }

# 3. Kokoro voice, "Hey Jarvis" wake word and voice detection models (whisper downloads itself on first run)
New-Item -ItemType Directory -Force models, logs | Out-Null
$kokoro = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0"
foreach ($f in "kokoro-v1.0.onnx", "voices-v1.0.bin") {
    # a file of the wrong size (an earlier download cut short) is fetched again; a new one lands as .part first
    $size = 0
    try { $size = [long]"$((Invoke-WebRequest "$kokoro/$f" -Method Head -UseBasicParsing).Headers['Content-Length'])" } catch { }
    if (-not (Test-Path "models\$f") -or ($size -gt 0 -and (Get-Item "models\$f").Length -ne $size)) {
        Write-Host "Downloading $f..."
        Invoke-WebRequest "$kokoro/$f" -OutFile "models\$f.part"
        Move-Item -Force "models\$f.part" "models\$f"
    }
}
.\.venv\Scripts\python.exe -c "import openwakeword.utils as u; u.download_models(['hey_jarvis'])"
if ($LASTEXITCODE) { throw "Wake word model download failed." }

# 3. Sign in through Google's CLI using the account with Antigravity/Gemini access.
Write-Host ""
Write-Host "Antigravity sign-in: complete the Google account flow in the CLI, then type /exit or press Ctrl+D."
Write-Host "The account session stays in your Windows credential store; Jarvis does not need an API key."
Push-Location $env:TEMP
try { & $agyPath } finally { Pop-Location }

# 4. Start Jarvis and its widget at every logon: a Task Scheduler task each for this user (no admin needed).
#    Two tasks, not one task with two actions: a task runs its actions one after another, and Jarvis never ends.
$py = Join-Path $PSScriptRoot ".venv\Scripts\pythonw.exe"
$me = "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
$tasks = [ordered]@{ "Jarvis" = "jarvis.py"; "Jarvis Widget" = "widget.py" }
foreach ($name in $tasks.Keys) {
    $action = New-ScheduledTaskAction -Execute $py -Argument "`"$PSScriptRoot\$($tasks[$name])`"" -WorkingDirectory $PSScriptRoot
    Register-ScheduledTask -TaskName $name -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $me) `
        -Settings $settings -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Force | Out-Null
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $name
}

Write-Host ""
Write-Host "Jarvis is installed and starting (the first start downloads whisper, give it a minute)."
Write-Host "Dashboard: http://127.0.0.1:8765 (or .venv\Scripts\python jarvisctl.py dashboard)    Log: $PSScriptRoot\logs\jarvis.log"
Write-Host "Default model: Gemini 3.8 Flash Medium through your signed-in Antigravity account."
Write-Host "Make sure your model is set in config_local.py (see README) or Ollama is running."
