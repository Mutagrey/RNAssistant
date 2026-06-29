param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x64", "x86")]
    [string]$Architecture = "x64",

    [string]$Destination = "C:\Temp\RNAssistant"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$nativePlatform = if ($Architecture -eq "x86") { "Win32" } else { "x64" }
$nativeOutput = Join-Path $repo "artifacts\$Configuration\$nativePlatform"
$coreOutput = Join-Path $repo "src\RNAssistant.Core\bin\$Configuration"
$officeOutput = Join-Path $repo "src\RNAssistant.Office\bin\$Configuration"
$hostsOutput = Join-Path $repo "src\RNAssistant.OfficeHosts\bin\$Configuration"
$webViewPackage = Join-Path $repo "packages\Microsoft.Web.WebView2.1.0.2903.40"

function Copy-RequiredFile([string]$Source, [string]$TargetDirectory) {
    if (-not (Test-Path $Source)) {
        throw "Required build artifact not found: $Source"
    }

    New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
    Copy-Item -Force $Source $TargetDirectory
}

Write-Host "Close EXCEL.EXE, WINWORD.EXE, POWERPNT.EXE and OUTLOOK.EXE before publishing."
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

Copy-RequiredFile (Join-Path $nativeOutput "RNAssistant.NativeHostCli.dll") $Destination
Copy-RequiredFile (Join-Path $coreOutput "RNAssistant.Core.dll") $Destination
Copy-RequiredFile (Join-Path $officeOutput "RNAssistant.Office.dll") $Destination
Copy-RequiredFile (Join-Path $hostsOutput "RNAssistant.OfficeHosts.dll") $Destination
Copy-RequiredFile (Join-Path $officeOutput "Newtonsoft.Json.dll") $Destination
Copy-RequiredFile (Join-Path $officeOutput "Microsoft.Web.WebView2.Core.dll") $Destination
Copy-RequiredFile (Join-Path $officeOutput "Microsoft.Web.WebView2.WinForms.dll") $Destination
Copy-RequiredFile (Join-Path $webViewPackage "build\native\$Architecture\WebView2Loader.dll") $Destination

Get-ChildItem -Path $hostsOutput -Filter "Microsoft.Office.Interop.*.dll" -ErrorAction SilentlyContinue |
    Copy-Item -Force -Destination $Destination
Get-ChildItem -Path $hostsOutput -Filter "Office.dll" -ErrorAction SilentlyContinue |
    Copy-Item -Force -Destination $Destination

$webDestination = Join-Path $Destination "web"
if (Test-Path $webDestination) {
    Remove-Item -Recurse -Force $webDestination
}
Copy-Item -Recurse -Force (Join-Path $repo "web") $webDestination

$sourcesDestination = Join-Path $Destination "addins\sources"
New-Item -ItemType Directory -Force -Path $sourcesDestination | Out-Null
Copy-Item -Recurse -Force (Join-Path $repo "wrappers\native\excel") $sourcesDestination
Copy-Item -Recurse -Force (Join-Path $repo "wrappers\native\word") $sourcesDestination
Copy-Item -Recurse -Force (Join-Path $repo "wrappers\native\powerpoint") $sourcesDestination
Copy-Item -Recurse -Force (Join-Path $repo "wrappers\native\outlook") $sourcesDestination
Copy-Item -Recurse -Force (Join-Path $repo "wrappers\native\ribbon") $sourcesDestination

$docsDestination = Join-Path $Destination "docs"
New-Item -ItemType Directory -Force -Path $docsDestination | Out-Null
Copy-Item -Force (Join-Path $repo "wrappers\native\README.md") $docsDestination
Copy-Item -Force (Join-Path $repo "wrappers\native\Outlook2013_Setup.md") $docsDestination

$ownerModeFile = Join-Path $Destination "panel-owner-mode.txt"
if (-not (Test-Path $ownerModeFile)) {
    Set-Content -Encoding ASCII -Path $ownerModeFile -Value "OwnerWindow"
}

New-Item -ItemType Directory -Force -Path (Join-Path $Destination "logs") | Out-Null
Write-Host "Portable RNAssistant published to $Destination ($Architecture)."
