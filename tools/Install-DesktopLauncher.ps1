param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot "src\RNAssistant.Desktop\RNAssistant.Desktop.csproj"

function Find-MSBuild {
    if ($env:MSBUILD_EXE_PATH -and (Test-Path $env:MSBUILD_EXE_PATH)) {
        return $env:MSBUILD_EXE_PATH
    }

    $programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86) {
        $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    }

    if ($vswhere -and (Test-Path $vswhere)) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($found -and (Test-Path $found)) {
            return $found
        }
    }

    $candidates = @()
    if ($programFiles) {
        $candidates += Join-Path $programFiles "Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        $candidates += Join-Path $programFiles "Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        $candidates += Join-Path $programFiles "Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    }
    if ($programFilesX86) {
        $candidates += Join-Path $programFilesX86 "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "MSBuild.exe not found. Install Visual Studio 2022 or set MSBUILD_EXE_PATH."
}

if (-not $NoBuild) {
    $msbuild = Find-MSBuild
    & $msbuild $projectPath "/t:Build" "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/v:minimal"
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for RNAssistant.Desktop."
    }
}

$exePath = Join-Path $repoRoot ("src\RNAssistant.Desktop\bin\{0}\{1}\RNAssistant.Desktop.exe" -f $Platform, $Configuration)
if (-not (Test-Path $exePath)) {
    throw "Desktop exe not found: $exePath"
}

[Environment]::SetEnvironmentVariable("RNASSISTANT_DESKTOP_EXE", $exePath, "User")
Write-Host "RNASSISTANT_DESKTOP_EXE=$exePath"
Write-Host "Import VBA modules from wrappers\native into Office-native add-in containers."
