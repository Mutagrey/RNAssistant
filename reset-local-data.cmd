@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0tools\Reset-LocalData.ps1" %*
exit /b %ERRORLEVEL%
