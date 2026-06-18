@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-LocalAddIns.ps1" %*
exit /b %ERRORLEVEL%
