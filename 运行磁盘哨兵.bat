@echo off
setlocal
set "APP=%~dp0???\????\????.exe"
if not exist "%APP%" (
  echo Published app not found. Run ????.bat first.
  pause
  exit /b 1
)
start "" "%APP%"
exit /b 0
