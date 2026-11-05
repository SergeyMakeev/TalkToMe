@echo off
if exist "%~dp0TalkToMe.exe" (
  start "" "%~dp0TalkToMe.exe" "%~dp0."
  exit /b
)
if not exist "%~dp0build\app\TalkToMe.exe" (
  echo Extract the complete Windows release, or follow the source build instructions in README.md.
  pause
  exit /b 1
)
start "" "%~dp0build\app\TalkToMe.exe" "%~dp0."
