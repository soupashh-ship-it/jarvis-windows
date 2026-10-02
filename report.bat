@echo off
rem Double-click: zips the Jarvis logs (API keys removed) to your Desktop for a bug report. Nothing is uploaded.
cd /d "%~dp0"
".venv\Scripts\python.exe" jarvisctl.py report
pause
