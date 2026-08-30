@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0tools\Install-DesktopLauncher.ps1" %*
exit /b %ERRORLEVEL%
