@echo off
setlocal EnableExtensions

set "REPO_ROOT=%~dp0"
set "BUILD_PROJECT=%REPO_ROOT%build\RNAssistant.LocalBuild.proj"
set "BUILD_TARGET=NativeBoth"

if "%~1"=="" goto resolve_msbuild
if /i "%~1"=="both" goto reject_extra
if /i "%~1"=="x64" set "BUILD_TARGET=NativeX64"
if /i "%~1"=="x64" goto reject_extra
if /i "%~1"=="x86" set "BUILD_TARGET=NativeX86"
if /i "%~1"=="x86" goto reject_extra
if /i "%~1"=="native" goto native_command
if /i "%~1"=="desktop" set "BUILD_TARGET=Desktop"
if /i "%~1"=="desktop" goto reject_extra
if /i "%~1"=="all" set "BUILD_TARGET=All"
if /i "%~1"=="all" goto reject_extra
if /i "%~1"=="doctor" set "BUILD_TARGET=Doctor"
if /i "%~1"=="doctor" goto reject_extra
goto usage

:native_command
if "%~2"=="" goto resolve_msbuild
if /i "%~2"=="both" set "BUILD_TARGET=NativeBoth"
if /i "%~2"=="x64" set "BUILD_TARGET=NativeX64"
if /i "%~2"=="x86" set "BUILD_TARGET=NativeX86"
if /i not "%~2"=="both" if /i not "%~2"=="x64" if /i not "%~2"=="x86" goto usage
if not "%~3"=="" goto usage
goto resolve_msbuild

:reject_extra
if not "%~2"=="" goto usage

:resolve_msbuild
set "MSBUILD_EXE="
if defined MSBUILD_EXE_PATH if exist "%MSBUILD_EXE_PATH%" set "MSBUILD_EXE=%MSBUILD_EXE_PATH%"
if defined MSBUILD_EXE goto msbuild_found

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto msbuild_missing
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD_EXE=%%I"
if not defined MSBUILD_EXE goto msbuild_missing

:msbuild_found
if not exist "%BUILD_PROJECT%" (
  echo Build project not found: "%BUILD_PROJECT%"
  exit /b 2
)

if not exist "%REPO_ROOT%artifacts" mkdir "%REPO_ROOT%artifacts"
if not exist "%REPO_ROOT%artifacts" (
  echo Cannot create artifacts directory: "%REPO_ROOT%artifacts"
  exit /b 4
)

echo MSBuild: %MSBUILD_EXE%
echo Target:  %BUILD_TARGET% ^(Release^)
"%MSBUILD_EXE%" "%BUILD_PROJECT%" /t:%BUILD_TARGET% /p:Configuration=Release /nologo /v:minimal /fl /flp:"logfile=%REPO_ROOT%artifacts\build-local.log;verbosity=normal"
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" (
  echo Build failed. Log: "%REPO_ROOT%artifacts\build-local.log"
  exit /b %BUILD_EXIT%
)

echo Build completed. Log: "%REPO_ROOT%artifacts\build-local.log"
exit /b 0

:msbuild_missing
echo MSBuild.exe was not found.
echo Install the required Visual Studio 2022 components or set MSBUILD_EXE_PATH.
exit /b 3

:usage
echo Usage:
echo   build-local.cmd                 Native Release x64 + x86
echo   build-local.cmd x64            Native Release x64
echo   build-local.cmd x86            Native Release x86
echo   build-local.cmd native both    Native Release x64 + x86
echo   build-local.cmd desktop        Desktop Release x64
echo   build-local.cmd all            Native both + Desktop x64
echo   build-local.cmd doctor         Check local inputs
exit /b 2
