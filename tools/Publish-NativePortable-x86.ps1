param(
    [string]$Destination = "C:\Temp\RNAssistant-x86"
)

& (Join-Path $PSScriptRoot "Publish-NativePortable.ps1") `
    -Configuration Release `
    -Architecture x86 `
    -Destination $Destination
