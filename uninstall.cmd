@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" -Action uninstall
set "result=%errorlevel%"
pause
exit /b %result%
