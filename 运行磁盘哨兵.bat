@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0??????.ps1"
if errorlevel 1 (
  echo.
  pause
  exit /b 1
)
