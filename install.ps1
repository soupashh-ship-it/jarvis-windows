# Jarvis for Windows: one-time setup. From the repo folder, run:
#   powershell -ExecutionPolicy Bypass -File install.ps1
# Safe to run again (it updates packages and re-registers the logon task).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 1. Python 3.12 virtual environment and packages
if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw "The py launcher wasn't found. Install Python 3.12 from python.org first (keep 'py launcher' ticked)."
}
if (-not (Test-Path .venv)) { py -3.12 -m venv .venv; if ($LASTEXITCODE) { throw "Couldn't create the venv. Is Python 3.12 installed?" } }
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
if ($LASTEXITCODE) { throw "pip install failed, see above." }

# 2. Kokoro voice, "Hey Jarvis" wake word and voice detection models (whisper downloads itself on first run)
New-Item -ItemType Directory -Force models, logs | Out-Null
$kokoro = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0"
foreach ($f in "kokoro-v1.0.onnx", "voices-v1.0.bin") {
    if (-not (Test-Path "models\$f")) { Write-Host "Downloading $f..."; Invoke-WebRequest "$kokoro/$f" -OutFile "models\$f" }
}
.\.venv\Scripts\python.exe -c "import openwakeword.utils as u; u.download_models(['hey_jarvis'])"
if ($LASTEXITCODE) { throw "Wake word model download failed." }

# 3. Start Jarvis and its widget at every logon: a Task Scheduler task for this user (no admin needed)
$py = Join-Path $PSScriptRoot ".venv\Scripts\pythonw.exe"
$me = "$env:USERDOMAIN\$env:USERNAME"
$actions = @(
    (New-ScheduledTaskAction -Execute $py -Argument "`"$PSScriptRoot\jarvis.py`"" -WorkingDirectory $PSScriptRoot),
    (New-ScheduledTaskAction -Execute $py -Argument "`"$PSScriptRoot\widget.py`"" -WorkingDirectory $PSScriptRoot))
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName "Jarvis" -Action $actions -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $me) `
    -Settings $settings -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Force | Out-Null
Stop-ScheduledTask -TaskName "Jarvis" -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName "Jarvis"

Write-Host ""
Write-Host "Jarvis is installed and starting (the first start downloads whisper, give it a minute)."
Write-Host "Dashboard: http://127.0.0.1:8765    Log: $PSScriptRoot\logs\jarvis.log"
Write-Host "Make sure your model is set in config_local.py (see README) or Ollama is running."
