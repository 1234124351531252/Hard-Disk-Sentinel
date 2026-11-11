@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0????.ps1"
if errorlevel 1 (
  echo.
  echo Publish failed. See the PowerShell error above.
  pause
  exit /b 1
)
echo.
echo The app is ready in ???\????.
pause
