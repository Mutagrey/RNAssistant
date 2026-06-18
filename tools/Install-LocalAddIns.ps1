param(
    [ValidateSet("All", "Excel", "Word", "PowerPoint", "Outlook")]
    [string[]]$Apps = @("All"),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$NoBuild,
    [switch]$NoCert,
    [switch]$SkipTrust,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

$addIns = @(
    @{
        Name = "Excel"
        Project = "src\RNAssistant.ExcelAddIn\RNAssistant.ExcelAddIn.csproj"
        Assembly = "RNAssistant.ExcelAddIn"
        OfficeApp = "Excel"
        FriendlyName = "RN Assistant for Excel"
        Description = "RNAssistant VSTO add-in for Excel."
    },
    @{
        Name = "Word"
        Project = "src\RNAssistant.WordAddIn\RNAssistant.WordAddIn.csproj"
        Assembly = "RNAssistant.WordAddIn"
        OfficeApp = "Word"
        FriendlyName = "RN Assistant for Word"
        Description = "RNAssistant VSTO add-in for Word."
    },
    @{
        Name = "PowerPoint"
        Project = "src\RNAssistant.PowerPointAddIn\RNAssistant.PowerPointAddIn.csproj"
        Assembly = "RNAssistant.PowerPointAddIn"
        OfficeApp = "PowerPoint"
        FriendlyName = "RN Assistant for PowerPoint"
        Description = "RNAssistant VSTO add-in for PowerPoint."
    },
    @{
        Name = "Outlook"
        Project = "src\RNAssistant.OutlookAddIn\RNAssistant.OutlookAddIn.csproj"
        Assembly = "RNAssistant.OutlookAddIn"
        OfficeApp = "Outlook"
        FriendlyName = "RN Assistant for Outlook"
        Description = "RNAssistant VSTO add-in for Outlook."
    }
)

function Get-SelectedAddIns {
    if ($Apps -contains "All") {
        return $addIns
    }

    return $addIns | Where-Object { $Apps -contains $_["Name"] }
}

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

function Get-VstoPath {
    param([hashtable]$AddIn)

    $projectDir = Split-Path -Parent (Join-Path $repoRoot $AddIn["Project"])
    return Join-Path $projectDir ("bin\{0}\{1}\{2}.vsto" -f $Platform, $Configuration, $AddIn["Assembly"])
}

function Set-AddInRegistry {
    param([hashtable]$AddIn)

    $vstoPath = Get-VstoPath -AddIn $AddIn
    if (-not (Test-Path $vstoPath)) {
        throw "VSTO manifest not found: $vstoPath. Build failed or Visual Studio Tools for Office is missing."
    }

    $manifestUri = ([System.Uri]$vstoPath).AbsoluteUri + "|vstolocal"
    $registryPath = "HKCU:\Software\Microsoft\Office\$($AddIn['OfficeApp'])\Addins\$($AddIn['Assembly'])"

    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "Description" -Value $AddIn["Description"] -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "FriendlyName" -Value $AddIn["FriendlyName"] -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "LoadBehavior" -Value 3 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "Manifest" -Value $manifestUri -PropertyType String -Force | Out-Null

    Write-Host "Registered $($AddIn['Name']): $manifestUri"
}

function Remove-AddInRegistry {
    param([hashtable]$AddIn)

    $registryPath = "HKCU:\Software\Microsoft\Office\$($AddIn['OfficeApp'])\Addins\$($AddIn['Assembly'])"
    if (Test-Path $registryPath) {
        Remove-Item -Path $registryPath -Recurse -Force
        Write-Host "Unregistered $($AddIn['Name'])"
    } else {
        Write-Host "Not registered: $($AddIn['Name'])"
    }
}

$selectedAddIns = @(Get-SelectedAddIns)

if ($Unregister) {
    foreach ($addIn in $selectedAddIns) {
        Remove-AddInRegistry -AddIn $addIn
    }
    exit 0
}

if (-not $NoCert) {
    $certArgs = @("-Quiet")
    if ($SkipTrust) {
        $certArgs += "-SkipTrust"
    }
    & (Join-Path $scriptDir "New-LocalClickOnceCertificate.ps1") @certArgs
}

if (-not $NoBuild) {
    $msbuild = Find-MSBuild
    foreach ($addIn in $selectedAddIns) {
        $projectPath = Join-Path $repoRoot $addIn["Project"]
        Write-Host "Building $($addIn['Name'])..."
        & $msbuild $projectPath "/t:Build" "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:VisualStudioVersion=17.0" "/v:minimal"
        if ($LASTEXITCODE -ne 0) {
            throw "MSBuild failed for $($addIn['Name'])."
        }
    }
}

foreach ($addIn in $selectedAddIns) {
    Set-AddInRegistry -AddIn $addIn
}

Write-Host ""
Write-Host "Done. Restart Office apps to load RNAssistant."
