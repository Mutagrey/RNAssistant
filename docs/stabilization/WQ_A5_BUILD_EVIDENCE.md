# WQ-A5 — exact-build evidence and release admission

Date: 2026-08-31

Baseline: `7e43209`

Scope: host-neutral release evidence contract/admission only

## Changed invariants

- Qualification results are a detached RS256-signed sidecar, so recording evidence
  never changes the binary that was qualified.
- Candidate assembly metadata pins the certificate DER SHA-256 before the build.
- `release.candidate` is unavailable unless signature, exact build/catalog/files,
  host-neutral harness and the full 19-run matrix are complete and compatible.
- Run events and UI provenance pin the exact evidence-envelope SHA-256.
- Release preparation creates the version commit without a tag; finalization requires
  Windows/Office acknowledgement, a passed in-app release pack and signed evidence for
  that exact tracked version/commit.

## Implementation

- Strict bounded payload/envelope parsers and RSA verifier:
  `BuildEvidenceContracts.cs`, `BuildEvidenceRuntime.cs`.
- Embedded `release.candidate` pack, coverage owner and deterministic catalog
  fingerprint.
- Typed application/bridge/UI projection for evidence status and run provenance.
- Candidate signer/configuration/platform assembly metadata and release-only signer
  gate.
- Immutable signer helper and two-stage `Prepare-Release.ps1`.
- Canonical operation contract: [Exact-build qualification evidence](../operations/BUILD_EVIDENCE.md).

## Verification

- `qualification:` — 14/14 pass, including complete/incompatible/invalid evidence,
  exact pack hashes, traversal refusal and real RSA tamper rejection.
- Targeted versioning — 6/6; production source inclusion — 1/1; Qualification
  Center Node — 5/5.
- MockDemo Release compile — 0 errors / 3 existing CA1416 warnings.
- `ValidateVersionFormat`, 322 local links in 16 changed Markdown files and
  `git diff --check` — pass.

## Open gates

PowerShell 5.1/7 execution, production Office/VSTO build, real certificate store,
Windows x64 + Office x64 pack runs and actual signed manifest were not exercised on
this Mac. No release tag was created. Product version remains `16.1.0-dev`.
