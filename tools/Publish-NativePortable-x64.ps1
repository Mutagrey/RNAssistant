param(
    [string]$Destination = "C:\Temp\RNAssistant-x64"
)

& (Join-Path $PSScriptRoot "Publish-NativePortable.ps1") `
    -Configuration Release `
    -Architecture x64 `
    -Destination $Destination
