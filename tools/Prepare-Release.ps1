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

    [switch]$WindowsOfficeValidated,
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

if (-not $WindowsOfficeValidated) {
    throw "First record the milestone qualification from the master plan, including Windows x64 / Office x64 / VS 2022. -WindowsOfficeValidated acknowledges that evidence; it does not run Office validation."
}

Push-Location $repository
try {
    Invoke-ReleaseCheck -Target "ValidateReleaseTreeClean"
    $actualBranch = (Invoke-Checked -Command "git" -Arguments @("branch", "--show-current") | Out-String).Trim()
    if ($actualBranch -ne $Branch) {
        throw "Expected branch '$Branch', found '$actualBranch'. No branch switch is performed."
    }
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
        "run", "--project", $harness, "--configuration", "Release", "-p:RNAssistantBuildNumber=$BuildNumber"
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
    Invoke-Checked -Command "git" -Arguments @("tag", "-a", $releaseTag, "-m", "RNAssistant $Version")
    if ($Push) {
        Invoke-Checked -Command "git" -Arguments @(
            "push", "--atomic", "--no-follow-tags", "origin", "HEAD:refs/heads/$Branch",
            ("refs/tags/{0}:refs/tags/{0}" -f $releaseTag)
        )
    }
    Write-Host "Prepared $releaseTag. Push requested: $Push. Package Office artifacts separately on the qualified Windows workstation."
}
catch {
    Write-Warning "Release stopped. Inspect the working tree, commits and tags; no automatic reset, tag deletion or retry is performed."
    throw
}
finally {
    Pop-Location
}
