@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0tools\Install-LocalAddIns.ps1" %*
exit /b %ERRORLEVEL%
