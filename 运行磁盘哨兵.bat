@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& .\ткпп╢еелиз╠Ь.ps1; exit $LASTEXITCODE"
echo.
echo мкЁЖбКё╨%errorlevel%
pause
