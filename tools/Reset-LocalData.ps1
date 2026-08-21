param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$officeProcesses = @(Get-Process -Name "EXCEL", "WINWORD", "POWERPNT", "OUTLOOK", "RNAssistant.Desktop" -ErrorAction SilentlyContinue)
if ($officeProcesses.Count -gt 0) {
    $names = ($officeProcesses | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
    throw "Close Office and RNAssistant before resetting local data. Running: $names"
}

$roamingAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
if ([string]::IsNullOrWhiteSpace($roamingAppData)) {
    throw "Windows roaming AppData path is unavailable."
}

$target = [IO.Path]::GetFullPath((Join-Path $roamingAppData "RNAssistant"))
$expectedParent = [IO.Path]::GetFullPath($roamingAppData).TrimEnd([IO.Path]::DirectorySeparatorChar)
$actualParent = [IO.Path]::GetDirectoryName($target).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not [string]::Equals([IO.Path]::GetFileName($target), "RNAssistant", [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($actualParent, $expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to reset unexpected path: $target"
}

if (-not (Test-Path -LiteralPath $target -PathType Container)) {
    Write-Host "RNAssistant local data does not exist: $target"
    exit 0
}

if (-not $Force) {
    Write-Host "This permanently deletes settings, API key, chats, attachments, tools, skills, VBA backups, WebView data, and runtime logs:"
    Write-Host $target
    $confirmation = Read-Host "Type DELETE to continue"
    if (-not [string]::Equals($confirmation, "DELETE", [StringComparison]::Ordinal)) {
        Write-Host "Reset cancelled."
        exit 1
    }
}

Remove-Item -LiteralPath $target -Recurse -Force
Write-Host "RNAssistant local data deleted: $target"
Write-Host "Document-local VBA modules and RNAssistant document properties were not changed."
