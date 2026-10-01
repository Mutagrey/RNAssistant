# Release process

Only for an explicitly approved release milestone. Ordinary commits and the working
baseline decision do not invoke this workflow or create tags. Development uses `main`
by default; alpha, beta and RC may be prepared from the selected clean `main` commit.
An existing integration branch may still be selected deliberately during transition.

## Milestones and qualification

The allowed main sequence is `v16.1.0-alpha.1` (testable alpha),
`v16.1.0-beta.1` (VBA/Excel slices working on Windows),
`v16.1.0-rc.1` (all release gates passed), then `v16.1.0`.
Stages may be skipped. Additional beta/rc numbers require a substantial fix and
a new build for testers, not merely completion of an internal phase.

Before release, record the applicable scenario matrix and open gates from the
[current Windows runbook](../stabilization/WINDOWS_QUALIFICATION_RUNBOOK.md),
[PROGRESS](../stabilization/PROGRESS.md) and
[RISK_REGISTER](../stabilization/RISK_REGISTER.md). The
[master plan sections 17–18](../stabilization/STABILIZATION_MASTER_PLAN.md)
retain migration rationale and historical acceptance evidence.
Run Windows x64 + Office x64 + VS 2022 qualification, including VSTO/ClickOnce
installation/update, assembly binding, target switching, VBA and write-fault cases.
Record evidence and remaining risks in `PROGRESS.md` / `RISK_REGISTER.md`.
A successful host-neutral harness is not Office validation.

## Exact-build two-stage release

Prerequisites: PowerShell 5.1+ or PowerShell 7, Git, the .NET SDK used by the harness,
clean tree/index (including untracked files), configured/reachable `origin`, an RSA
certificate in `Cert:\CurrentUser\My`, and substantive user-visible notes under
`CHANGELOG.md` → `[Unreleased]`. Record the lowercase SHA-256 of the certificate DER.
Choose the current `main` or existing `stabilization/16.1` branch explicitly;
scripts never switch branches. The branch name does not confer a quality level.

Example for a future qualified milestone; this is not a working-baseline step:

```powershell
./tools/Prepare-Release.ps1 `
  -Version 16.1.0-rc.1 `
  -Branch main `
  -BuildNumber 1 `
  -BuildEvidenceSignerSha256 <lowercase-certificate-der-sha256>
```

This preparation invocation:

1. checks clean tree, exact selected branch, version format and local/remote tag absence;
2. updates only the product prefix/suffix and moves Unreleased notes into a dated
   release section, retaining an empty Unreleased section.
3. checks version/changelog and runs the full host-neutral harness;
4. refuses concurrent HEAD/branch changes or unexpected changed files;
5. stages only `Directory.Build.props` and `CHANGELOG.md`, then creates one release commit;
6. rechecks release gates and rebuilds metadata for that commit.

Preparation **does not create a tag or push**. Build one unchanged Release/x64 Office
candidate from the resulting commit with the same build number and signer fingerprint.
Exercise the applicable scenarios directly on Windows x64 + Office x64 and record
results for the exact candidate. The release contour constructs the payload described
in [Exact-build release evidence](BUILD_EVIDENCE.md), hashes the evidence bundle and
distributable files, then signs it without overwriting an earlier envelope:

```powershell
./tools/Sign-BuildEvidence.ps1 `
  -PayloadPath .\BuildEvidence.payload.v1.json `
  -OutputPath .\RNAssistant.BuildEvidence.v1.json `
  -CertificateThumbprint <thumbprint> `
  -ExpectedSignerSha256 <lowercase-certificate-der-sha256>
```

Review the recorded Windows results and unchanged candidate artifacts, then finalize:

```powershell
./tools/Prepare-Release.ps1 `
  -Version 16.1.0-rc.1 `
  -Branch main `
  -BuildNumber 1 `
  -BuildEvidenceSignerSha256 <lowercase-certificate-der-sha256> `
  -Finalize `
  -BuildEvidenceManifest .\RNAssistant.BuildEvidence.v1.json `
  -WindowsOfficeValidated
```

Finalization verifies the tracked product version, clean exact commit, tag absence,
signer/signature and payload identity. It then creates one annotated tag containing
the manifest hash. It pushes the branch and exact tag atomically only with explicit
`-Push`. `-WindowsOfficeValidated` acknowledges recorded evidence; it does not run
Office. The script verifies the envelope and commit/version identity, while the
release owner reviews the Windows matrix and artifact hashes in the signed payload.

The scripts do not build/package Office add-ins or execute Office scenarios. Do not distribute
pre-commit, dirty or post-evidence rebuilt artifacts.

## Build an existing release

Check out the intended immutable annotated tag, verify it resolves to the selected
commit, and pass `RNAssistantReleaseBuild=true`,
`RNAssistantReleaseTag=v<Version>`, the assigned `RNAssistantBuildNumber`,
`RNAssistantRuntimePlatform=x64` and the signer fingerprint recorded for that release.
Release checks enforce format/tag-product match, clean tree and exact changelog.
They do not create another tag. The caller must verify the checked-out tag/commit;
a supplied tag string alone is not evidence that a checkout is tagged.

## Failure handling

Failure stops the workflow. Before commit, inspect and correct the prepared files.
After the preparation commit, correct failures with a new candidate/evidence set;
never attach old evidence to rebuilt files. After tag but before push, verify that
same tag, commit and manifest hash; never recreate, move or force it.
No automatic reset, rollback, tag deletion or write retry occurs.
Remote inspection failures fail closed, including when credentials/network are unavailable.

## Current validation boundary

The PowerShell release workflow has not been executed for the current working baseline.
This Mac has no `pwsh`; execution on the release workstation and Office/VSTO
qualification remain unverified. Targeted harness tests exercise the MSBuild gates
with disposable local repositories/remotes, without contacting or tagging this repository's origin.
