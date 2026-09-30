# Exact-build release evidence

The signed `RNAssistant.BuildEvidence.v1.json` envelope belongs to the formal release
workflow. It is not read by RNAssistant at runtime. The working baseline needs no
manifest or separate qualification process.

A release owner records the Windows x64 + Office x64 results for one unchanged
Release/x64 candidate and creates a UTF-8 JSON payload. The payload must identify
the product version, exact commit, build time, branch, clean tree, configuration,
platform, tested host environment, scenario outcomes, remaining gaps and SHA-256
hashes of the evidence bundle and distributable files. The signer certificate DER
SHA-256 is pinned in release build metadata through
`RNAssistantBuildEvidenceSignerSha256`; the key stays outside the repository.

The release owner signs the payload with `tools/Sign-BuildEvidence.ps1` on the release
machine and supplies the envelope to `tools/Prepare-Release.ps1 -Finalize`. The
script checks the RS256 signature, pinned signer, `status=complete` and exact
commit/version/Release/x64/clean-tree identity before tagging. The release owner
must review scenario coverage and artifact hashes: this script does not execute
Office scenarios or independently verify the payload's full matrix and files.

The former in-app `release.candidate` pack and automatic build-evidence admission
were removed with Qualification Center. Their prior design and results are
historical WQ evidence, not a current release gate. See
[Release process](RELEASE_PROCESS.md) and [Windows/Office verification](../qualification.md).
