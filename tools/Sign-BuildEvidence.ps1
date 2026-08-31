[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedSignerSha256
)

$ErrorActionPreference = 'Stop'
$payloadFullPath = [System.IO.Path]::GetFullPath($PayloadPath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not [System.IO.File]::Exists($payloadFullPath)) { throw 'Build evidence payload was not found.' }
if ([System.IO.File]::Exists($outputFullPath)) { throw 'Build evidence output already exists; immutable evidence is never overwritten.' }

$payload = [System.IO.File]::ReadAllBytes($payloadFullPath)
if ($payload.Length -lt 1 -or $payload.Length -gt 1048576) { throw 'Build evidence payload must be between 1 byte and 1 MiB.' }
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$payloadText = $utf8.GetString($payload)
if ($payloadText.Length -gt 0 -and $payloadText[0] -eq [char]0xfeff) { throw 'Build evidence payload must use UTF-8 without BOM.' }
$payloadJson = $payloadText | ConvertFrom-Json
if ($payloadJson.schemaVersion -ne 1 -or $payloadJson.status -ne 'complete') { throw 'Build evidence payload must be a complete schema v1 document.' }

$thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
if ($null -eq $certificate -or -not $certificate.HasPrivateKey) { throw 'The selected CurrentUser certificate with a private key was not found.' }
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $signerSha256 = ([System.BitConverter]::ToString($sha256.ComputeHash($certificate.RawData))).Replace('-', '').ToLowerInvariant()
} finally {
    $sha256.Dispose()
}
if ($signerSha256 -ne $ExpectedSignerSha256) { throw 'The selected certificate does not match the signer pinned into the candidate build.' }

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if ($null -eq $rsa) { throw 'The selected certificate does not expose an RSA private key.' }
try {
    $signature = $rsa.SignData(
        $payload,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
} finally {
    $rsa.Dispose()
}

$envelope = [ordered]@{
    schemaVersion = 1
    algorithm = 'RS256'
    certificateDer = [Convert]::ToBase64String($certificate.RawData)
    payloadBase64 = [Convert]::ToBase64String($payload)
    signatureBase64 = [Convert]::ToBase64String($signature)
} | ConvertTo-Json -Compress
$parent = [System.IO.Path]::GetDirectoryName($outputFullPath)
if (-not [System.IO.Directory]::Exists($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
[System.IO.File]::WriteAllText($outputFullPath, $envelope, (New-Object System.Text.UTF8Encoding($false)))
Write-Output $outputFullPath
