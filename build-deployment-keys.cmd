@echo off
setlocal EnableExtensions

if not "%~1"=="" goto usage

set "REPO_ROOT=%~dp0"
set "KEYS_PROJECT=%REPO_ROOT%tools\RNAssistant.DeploymentKeys.Windows\RNAssistant.DeploymentKeys.Windows.csproj"
set "KEYS_EXE=%REPO_ROOT%tools\RNAssistant.DeploymentKeys.Windows\bin\Release\RNAssistant.DeploymentKeys.Windows.exe"
set "MSBUILD_EXE="

if defined MSBUILD_EXE_PATH if exist "%MSBUILD_EXE_PATH%" set "MSBUILD_EXE=%MSBUILD_EXE_PATH%"
if defined MSBUILD_EXE goto msbuild_found

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto msbuild_missing
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD_EXE=%%I"
if not defined MSBUILD_EXE goto msbuild_missing

:msbuild_found
if not exist "%KEYS_PROJECT%" (
  echo Deployment keys project not found: "%KEYS_PROJECT%"
  exit /b 2
)

echo Building deployment keys utility ^(Release^)...
"%MSBUILD_EXE%" "%KEYS_PROJECT%" /t:Build /p:Configuration=Release /p:Platform=AnyCPU /nologo /v:minimal
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" exit /b %BUILD_EXIT%

echo Built: "%KEYS_EXE%"
echo Run the utility to create the issuer. This build did not create keys.
exit /b 0

:msbuild_missing
echo MSBuild.exe was not found.
echo Install Visual Studio 2022 with .NET Framework 4.8 targeting pack or set MSBUILD_EXE_PATH.
exit /b 3

:usage
echo Usage: build-deployment-keys.cmd
exit /b 2
