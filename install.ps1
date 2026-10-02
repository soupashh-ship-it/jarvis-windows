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

# 3. Start Jarvis and its widget at every logon: a Task Scheduler task each for this user (no admin needed).
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
Write-Host "Dashboard: .venv\Scripts\python jarvisctl.py dashboard    Log: $PSScriptRoot\logs\jarvis.log"
Write-Host "Make sure your model is set in config_local.py (see README) or Ollama is running."
