# Exact-build qualification evidence

Status: WQ-A5 host-neutral contract and admission are implemented. Real evidence is
created only on the Windows x64 + Office x64 release workstation.

## Why the manifest is detached

`RNAssistant.BuildEvidence.v1.json` is a signed sidecar placed beside
`RNAssistant.Office.dll`. Embedding qualification results after the run would change
the binary that was tested. Instead, the candidate is built once with the SHA-256 of
the allowed signing certificate in assembly metadata; the release contour later
signs evidence for those exact unchanged files.

An ordinary build pins `unavailable`, so it cannot expose
`qualification.build-evidence.v1` or pass `release.candidate`. A candidate build must
set both:

```text
RNAssistantBuildEvidenceSignerSha256=<lowercase SHA-256 of certificate DER>
RNAssistantRuntimePlatform=x64
```

The certificate pin is the trust anchor. The private key and certificate store are
not part of the repository or evidence bundle. No CDN, network lookup or certificate
chain download is used by the application.

## Signed payload v1

The envelope is strict UTF-8 JSON without BOM and contains `schemaVersion=1`,
`algorithm=RS256`, certificate DER, exact payload bytes and signature. The strict
payload binds:

- product/informational version, full commit, build UTC, branch, channel, clean-tree
  state, `Release` configuration and `x64` platform;
- qualification catalog fingerprint (coverage plus every embedded pack revision/hash);
- environment and evidence-bundle hashes;
- bounded relative artifact paths with byte lengths and SHA-256 hashes, including
  the current `RNAssistant.Office.dll`;
- passed host-neutral harness evidence;
- the complete release run matrix and each exact pack revision/hash, run/event ID,
  completion time and evidence hash.

The required matrix is four `common.quick` hosts; provider, storage and UI packs;
both Excel WQ0 variants; Excel read/write and complex-task packs; four VBA lifecycle
hosts; and four cross-run hosts. Missing, failed or blocked records never become pass.

## Creation and admission

1. Prepare the release commit without creating a tag as described in
   [Release process](RELEASE_PROCESS.md).
2. Build that exact clean commit as Release/x64 with the pinned signer.
3. Run Milestone WQ and produce the complete payload plus immutable evidence bundle.
4. Sign the payload once on Windows:

```powershell
./tools/Sign-BuildEvidence.ps1 `
  -PayloadPath .\BuildEvidence.payload.v1.json `
  -OutputPath .\RNAssistant.BuildEvidence.v1.json `
  -CertificateThumbprint <thumbprint> `
  -ExpectedSignerSha256 <lowercase-certificate-der-sha256>
```

5. Copy the sidecar beside the unchanged `RNAssistant.Office.dll`, restart the
   application, and run `release.candidate` in Qualification Center.
6. Finalize the release only after that pack passes for the same manifest hash.

The application verifies signature, signer pin, binary/catalog identity, all listed
file hashes and the complete run matrix. Status is `missing`, `invalid`,
`incompatible`, `incomplete` or `complete`; only `complete` publishes the exact
capability. The signer script refuses to overwrite an existing envelope. The release
script checks the signed envelope again and never moves or reuses a tag.

Qualification event contract v2 now pins `buildEvidenceSha256`. Pre-WQ-A5 v1 chats
do not satisfy this contract and must be explicitly reset; they are not
silently upgraded or used as release evidence.
