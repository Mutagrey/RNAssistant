@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Reset-LocalData.ps1" %*
exit /b %ERRORLEVEL%
