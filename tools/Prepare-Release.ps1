[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^16\.1\.0(-(alpha|beta|rc)\.[1-9][0-9]*)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet("stabilization/16.1", "main")]
    [string]$Branch,

    [ValidateRange(0, 65534)]
    [int]$BuildNumber = 0,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$BuildEvidenceSignerSha256,

    [switch]$Finalize,
    [string]$BuildEvidenceManifest,
    [switch]$WindowsOfficeValidated,
    [switch]$ReleasePackPassed,
    [switch]$Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$harness = "tests/RNAssistant.Harness/RNAssistant.Harness.csproj"
$releaseTag = "v$Version"
$versionParts = $Version.Split(@("-"), 2, [StringSplitOptions]::None)
$versionPrefix = $versionParts[0]
$versionSuffix = if ($versionParts.Length -eq 2) { $versionParts[1] } else { "" }
$releaseProperties = @(
    "-p:RNAssistantVersionPrefix=$versionPrefix",
    "-p:RNAssistantVersionSuffix=$versionSuffix",
    "-p:RNAssistantBuildNumber=$BuildNumber",
    "-p:RNAssistantBuildEvidenceSignerSha256=$BuildEvidenceSignerSha256",
    "-p:RNAssistantRuntimePlatform=x64",
    "-p:RNAssistantReleaseTag=$releaseTag"
)

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE. Release stopped."
    }
}

function Invoke-ReleaseCheck {
    param([string]$Target)
    Invoke-Checked -Command "dotnet" -Arguments (
        @("msbuild", $harness, "-nologo", "-v:minimal", "-t:$Target") + $releaseProperties
    )
}

function Assert-TrackedReleaseVersion {
    param([string]$ExpectedPrefix, [string]$ExpectedSuffix)
    $propsPath = Join-Path $repository "Directory.Build.props"
    $propsText = [System.IO.File]::ReadAllText($propsPath)
    foreach ($entry in @(
        @{ Name = "RNAssistantVersionPrefix"; Value = $ExpectedPrefix },
        @{ Name = "RNAssistantVersionSuffix"; Value = $ExpectedSuffix }
    )) {
        $matches = [regex]::Matches($propsText, "<$($entry.Name)>([^<]*)</$($entry.Name)>")
        if ($matches.Count -ne 1 -or $matches[0].Groups[1].Value -ne $entry.Value) {
            throw "Tracked $($entry.Name) does not match the requested release. MSBuild overrides cannot qualify a different source version."
        }
    }
}

function Assert-SignedBuildEvidence {
    param([string]$Path, [string]$ExpectedCommit)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "-BuildEvidenceManifest is required for -Finalize." }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($fullPath)) { throw "Signed BuildEvidenceManifest was not found." }
    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -lt 1 -or $bytes.Length -gt 2097152) { throw "Signed BuildEvidenceManifest is empty or exceeds 2 MiB." }
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $envelope = $utf8.GetString($bytes) | ConvertFrom-Json
    if ($envelope.schemaVersion -ne 1 -or $envelope.algorithm -ne 'RS256') { throw "Signed BuildEvidenceManifest envelope is unsupported." }
    $certificateBytes = [Convert]::FromBase64String($envelope.certificateDer)
    $payloadBytes = [Convert]::FromBase64String($envelope.payloadBase64)
    $signatureBytes = [Convert]::FromBase64String($envelope.signatureBase64)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $signer = ([BitConverter]::ToString($sha256.ComputeHash($certificateBytes))).Replace('-', '').ToLowerInvariant()
        $manifestSha256 = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha256.Dispose() }
    if ($signer -ne $BuildEvidenceSignerSha256) { throw "Build evidence signer differs from the candidate's pinned signer." }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$certificateBytes)
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
    if ($null -eq $rsa) { throw "Build evidence signer certificate has no RSA public key." }
    try {
        $valid = $rsa.VerifyData($payloadBytes, $signatureBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } finally { $rsa.Dispose(); $certificate.Dispose() }
    if (-not $valid) { throw "Build evidence signature is invalid." }
    $payload = $utf8.GetString($payloadBytes) | ConvertFrom-Json
    if ($payload.schemaVersion -ne 1 -or $payload.status -ne 'complete' -or
        $payload.commitSha -ne $ExpectedCommit -or $payload.productVersion -ne $Version -or
        $payload.configuration -ne 'Release' -or $payload.platform -ne 'x64' -or
        $payload.workingTreeState -ne 'clean') {
        throw "Build evidence payload does not describe this exact clean Release x64 commit/version."
    }
    return $manifestSha256
}

Push-Location $repository
try {
    Invoke-ReleaseCheck -Target "ValidateReleaseTreeClean"
    $actualBranch = (Invoke-Checked -Command "git" -Arguments @("branch", "--show-current") | Out-String).Trim()
    if ($actualBranch -ne $Branch) {
        throw "Expected branch '$Branch', found '$actualBranch'. No branch switch is performed."
    }
    if ($Finalize) {
        if (-not $WindowsOfficeValidated -or -not $ReleasePackPassed) {
            throw "Finalize requires recorded Windows/Office qualification and a passed in-app release.candidate pack."
        }
        Assert-TrackedReleaseVersion -ExpectedPrefix $versionPrefix -ExpectedSuffix $versionSuffix
        $releaseCommit = (Invoke-Checked -Command "git" -Arguments @("rev-parse", "HEAD") | Out-String).Trim()
        Invoke-ReleaseCheck -Target "ValidateRNAssistantRelease"
        Invoke-ReleaseCheck -Target "ValidateTagDoesNotExist"
        $manifestSha256 = Assert-SignedBuildEvidence -Path $BuildEvidenceManifest -ExpectedCommit $releaseCommit
        Invoke-Checked -Command "git" -Arguments @("tag", "-a", $releaseTag, "-m", "RNAssistant $Version; build evidence $manifestSha256")
        if ($Push) {
            Invoke-Checked -Command "git" -Arguments @(
                "push", "--atomic", "--no-follow-tags", "origin", "HEAD:refs/heads/$Branch",
                ("refs/tags/{0}:refs/tags/{0}" -f $releaseTag)
            )
        }
        Write-Host "Finalized $releaseTag for exact build evidence $manifestSha256. Push requested: $Push."
        return
    }
    if ($Push) { throw "-Push is accepted only together with -Finalize." }
    if ($Branch -eq "main" -and $versionSuffix -ne "" -and -not $versionSuffix.StartsWith("rc.")) {
        throw "main accepts only stable or release-candidate code; prepare alpha/beta on stabilization/16.1."
    }
    $startingCommit = (Invoke-Checked -Command "git" -Arguments @("rev-parse", "HEAD") | Out-String).Trim()
    Invoke-ReleaseCheck -Target "ValidateVersionFormat"
    Invoke-ReleaseCheck -Target "ValidateTagDoesNotExist"

    $propsPath = Join-Path $repository "Directory.Build.props"
    $changelogPath = Join-Path $repository "CHANGELOG.md"
    $propsText = [IO.File]::ReadAllText($propsPath)
    $changelogText = [IO.File]::ReadAllText($changelogPath)
    $unreleased = [regex]::Match($changelogText, '(?ms)^## \[Unreleased\]\r?\n(?<body>.*?)(?=^## \[|\z)')
    if (-not $unreleased.Success -or -not [regex]::IsMatch($unreleased.Groups["body"].Value, '(?m)^[-*] \S')) {
        throw "CHANGELOG.md [Unreleased] must contain user-visible release notes."
    }
    if ([regex]::IsMatch($changelogText, '(?m)^## \[' + [regex]::Escape($Version) + '\]')) {
        throw "CHANGELOG.md already contains [$Version]. Inspect the previous release attempt; no automatic retry is performed."
    }
    foreach ($propertyName in @("RNAssistantVersionPrefix", "RNAssistantVersionSuffix")) {
        if ([regex]::Matches($propsText, "<$propertyName>[^<]*</$propertyName>").Count -ne 1) {
            throw "Expected exactly one $propertyName in Directory.Build.props."
        }
    }
    $propsText = [regex]::Replace($propsText, '<RNAssistantVersionPrefix>[^<]*</RNAssistantVersionPrefix>',
        "<RNAssistantVersionPrefix>$versionPrefix</RNAssistantVersionPrefix>")
    $propsText = [regex]::Replace($propsText, '<RNAssistantVersionSuffix>[^<]*</RNAssistantVersionSuffix>',
        "<RNAssistantVersionSuffix>$versionSuffix</RNAssistantVersionSuffix>")
    $newline = [Environment]::NewLine
    $emptyUnreleased = (@("## [Unreleased]", "", "### Added", "", "### Changed", "", "### Fixed", "", "### Removed", "", "### Security", "", "") -join $newline)
    $releaseHeading = "## [$Version] - " + [DateTime]::UtcNow.ToString("yyyy-MM-dd") + $newline
    $replacement = $emptyUnreleased + $releaseHeading + $unreleased.Groups["body"].Value
    $changelogText = $changelogText.Substring(0, $unreleased.Index) + $replacement +
        $changelogText.Substring($unreleased.Index + $unreleased.Length)
    $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [IO.File]::WriteAllText($propsPath, $propsText, $utf8)
    [IO.File]::WriteAllText($changelogPath, $changelogText, $utf8)

    Invoke-ReleaseCheck -Target "ValidateVersionFormat"
    Invoke-ReleaseCheck -Target "ValidateReleaseChangelog"
    # Full host-neutral qualification is intentional for a release, not for ordinary commits.
    Invoke-Checked -Command "dotnet" -Arguments @(
        "run", "--project", $harness, "--configuration", "Release",
        "-p:RNAssistantBuildNumber=$BuildNumber",
        "-p:RNAssistantBuildEvidenceSignerSha256=$BuildEvidenceSignerSha256",
        "-p:RNAssistantRuntimePlatform=x64"
    )
    $currentCommit = (Invoke-Checked -Command "git" -Arguments @("rev-parse", "HEAD") | Out-String).Trim()
    $currentBranch = (Invoke-Checked -Command "git" -Arguments @("branch", "--show-current") | Out-String).Trim()
    if ($currentCommit -ne $startingCommit -or $currentBranch -ne $Branch) {
        throw "HEAD/branch changed during qualification. Inspect the release preparation before continuing."
    }
    $changedPaths = @(
        Invoke-Checked -Command "git" -Arguments @("diff", "--name-only")
        Invoke-Checked -Command "git" -Arguments @("diff", "--cached", "--name-only")
        Invoke-Checked -Command "git" -Arguments @("ls-files", "--others", "--exclude-standard")
    )
    if (@($changedPaths | Where-Object { $_ -notin @("Directory.Build.props", "CHANGELOG.md") }).Count -ne 0) {
        throw "Unexpected source changes appeared during qualification. Nothing else will be staged."
    }
    Invoke-Checked -Command "git" -Arguments @("add", "--", "Directory.Build.props", "CHANGELOG.md")
    Invoke-Checked -Command "git" -Arguments @("commit", "-m", "chore(release): prepare $Version")
    Invoke-ReleaseCheck -Target "ValidateRNAssistantRelease"
    # Rebuild metadata for the release commit SHA, after the tested source has been committed.
    Invoke-Checked -Command "dotnet" -Arguments (
        @("build", $harness, "--configuration", "Release", "--no-restore", "-v:minimal", "-p:RNAssistantReleaseBuild=true") +
        $releaseProperties
    )
    Invoke-ReleaseCheck -Target "ValidateTagDoesNotExist"
    $preparedCommit = (Invoke-Checked -Command "git" -Arguments @("rev-parse", "HEAD") | Out-String).Trim()
    Write-Host "Prepared release commit $preparedCommit without a tag. Build this exact commit with the pinned signer, run Milestone WQ, sign RNAssistant.BuildEvidence.v1.json, then rerun with -Finalize."
}
catch {
    Write-Warning "Release stopped. Inspect the working tree, commits and tags; no automatic reset, tag deletion or retry is performed."
    throw
}
finally {
    Pop-Location
}
