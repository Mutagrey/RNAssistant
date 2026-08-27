# Release process

Only for an explicitly approved release milestone. Ordinary commits and Phase 0
must not invoke this workflow or create tags.

## Milestones and qualification

The allowed main sequence is `v16.1.0-alpha.1` (testable alpha),
`v16.1.0-beta.1` (VBA/Excel slices working on Windows),
`v16.1.0-rc.1` (all release gates passed), then `v16.1.0`.
Stages may be skipped. Additional beta/rc numbers require a substantial fix and
a new build for testers, not merely completion of an internal phase.

Before release, record the applicable acceptance matrix and gates from
[master plan sections 17–18](../stabilization/STABILIZATION_MASTER_PLAN.md).
Run Windows x64 + Office x64 + VS 2022 qualification, including VSTO/ClickOnce
installation/update, assembly binding, target switching, VBA and write-fault cases.
Record evidence and remaining risks in `PROGRESS.md` / `RISK_REGISTER.md`.
A successful host-neutral harness is not Office validation.

## Explicit preparation

Prerequisites: PowerShell 5.1+ or PowerShell 7, Git, the .NET SDK used by the harness,
clean tree/index (including untracked files), configured/reachable `origin`,
and substantive user-visible notes under `CHANGELOG.md` → `[Unreleased]`.
Choose `stabilization/16.1` or `main` explicitly; the script never switches branches.

Example for a future qualified milestone; do not run during Phase 0:

```powershell
./tools/Prepare-Release.ps1 -Version 16.1.0-rc.1 -Branch stabilization/16.1 -BuildNumber 1 -WindowsOfficeValidated
```

`-WindowsOfficeValidated` is the release owner's acknowledgement of recorded
qualification, not an automated check or permission to skip it.

The script:

1. Checks clean tree, exact selected branch, version format and local/remote tag absence.
2. Updates only the product prefix/suffix and moves Unreleased notes into a dated
   release section, retaining an empty Unreleased section.
3. Checks version/changelog and runs the full host-neutral harness.
4. Refuses concurrent HEAD/branch changes or unexpected changed files.
5. Stages only `Directory.Build.props` and `CHANGELOG.md`, then creates one release commit.
6. Rechecks release gates and rebuilds harness metadata for the release commit SHA.
7. Rechecks tag absence and creates an annotated tag without force.
8. Pushes that branch and exact tag atomically only if `-Push` was explicitly supplied.

The script does not build/package Office add-ins. Build distributable Office artifacts
from the resulting release commit on the qualified Windows workstation, using the
same build number and recording their identity. Those builds still require release
checks; do not distribute the pre-commit dirty test artifacts.

## Build an existing release

Check out the intended immutable annotated tag, verify it resolves to the selected
commit, and pass `RNAssistantReleaseBuild=true`,
`RNAssistantReleaseTag=v<Version>` and the assigned `RNAssistantBuildNumber`.
Release checks enforce format/tag-product match, clean tree and exact changelog.
They do not create another tag. The caller must verify the checked-out tag/commit;
a supplied tag string alone is not evidence that a checkout is tagged.

## Failure handling

Failure stops the workflow. Before commit, inspect and correct the prepared files.
After commit but before tag, inspect the committed state and rerun read-only gates
before an explicitly approved continuation. After tag but before push, verify that
same tag and commit; never recreate, move or force it.
No automatic reset, rollback, tag deletion or write retry occurs.
Remote inspection failures fail closed, including when credentials/network are unavailable.

## Phase 0 validation boundary

The PowerShell release workflow is added but not executed in Phase 0.
This Mac has no `pwsh`; execution on the release workstation and Office/VSTO
qualification remain unverified. Targeted harness tests exercise the MSBuild gates
with disposable local repositories/remotes, without contacting or tagging this repository's origin.
