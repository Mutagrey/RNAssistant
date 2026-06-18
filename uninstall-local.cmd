@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-LocalAddIns.ps1" -Unregister %*
exit /b %ERRORLEVEL%
