@echo off
if not exist "%~dp0build\app\TalkToMe.exe" (
  echo Run setup.ps1 first. See README.md for setup options.
  pause
  exit /b 1
)
start "" "%~dp0build\app\TalkToMe.exe" "%~dp0."
