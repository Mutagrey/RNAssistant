param(
    [string]$Subject = "CN=RNAssistant ClickOnce Development",
    [int]$Years = 5,
    [switch]$SkipTrust,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$localPropsPath = Join-Path $repoRoot "Directory.Build.local.props"
$certBackupDir = Join-Path $repoRoot "certs\local"
$validAfter = (Get-Date).AddDays(30)

if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
    throw "New-SelfSignedCertificate is not available. Run this script in Windows PowerShell 5+ on Windows 10/11."
}

New-Item -ItemType Directory -Force -Path $certBackupDir | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt $validAfter } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing) {
    $cert = $existing
} else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyExportPolicy Exportable `
        -KeySpec Signature `
        -NotAfter (Get-Date).AddYears($Years)
}

$cerPath = Join-Path $certBackupDir "RNAssistantClickOnce.cer"
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

function Import-RNAssistantCertificate {
    param(
        [string]$StorePath,
        [string]$CertificatePath,
        [string]$Thumbprint
    )

    $trusted = Get-ChildItem $StorePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1

    if (-not $trusted) {
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation $StorePath | Out-Null
    }
}

if (-not $SkipTrust) {
    Import-RNAssistantCertificate -StorePath "Cert:\CurrentUser\Root" -CertificatePath $cerPath -Thumbprint $cert.Thumbprint
    Import-RNAssistantCertificate -StorePath "Cert:\CurrentUser\TrustedPublisher" -CertificatePath $cerPath -Thumbprint $cert.Thumbprint
}

$thumbprint = $cert.Thumbprint
$localProps = @"
<?xml version="1.0" encoding="utf-8"?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition="'`$(UseVSTO)' == 'true'">
    <SignManifests>true</SignManifests>
    <GenerateManifests>true</GenerateManifests>
    <ManifestCertificateThumbprint>$thumbprint</ManifestCertificateThumbprint>
  </PropertyGroup>
</Project>
"@

Set-Content -Path $localPropsPath -Value $localProps -Encoding UTF8

if (-not $Quiet) {
    Write-Host "RNAssistant ClickOnce certificate is ready."
    Write-Host "Subject: $($cert.Subject)"
    Write-Host "Thumbprint: $thumbprint"
    Write-Host "Local MSBuild props: $localPropsPath"
    Write-Host "Public certificate backup: $cerPath"
    if (-not $SkipTrust) {
        Write-Host "Trusted for current user: Root, TrustedPublisher"
    }
    Write-Host ""
    Write-Host "Close and reopen Visual Studio, then build the add-in projects."
}
