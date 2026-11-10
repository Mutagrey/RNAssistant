# Versioning

Status: active product/build version policy, revised with the maintainer on
2026-11-11. This replaces the release-only product-bump restriction from
[ADR-0007](../decisions/ADR-0007-release-only-versioning.md), preserving its separate
build identity, assembly binding and release gates. The old
[master plan, section 13](../stabilization/STABILIZATION_MASTER_PLAN.md#13-версионирование-новая-обязательная-политика)
is migration history; this document owns the current policy.

## Policy

- Bump the product version once per completed product change, in its completing
  commit. Intermediate commits, documentation, tests and behavior-preserving
  refactoring do not require a bump. A commit or version bump does not create a
  release, Git tag or automatic push.
- Historical baseline is `v16.0.4` (`225a05bb44dd7701892b5f8c98ea2e3b342274a7`); do not create another baseline tag.
- Development continues on `main` by default. The change owner selects the bump
  with the implementation; routine patch/minor bumps need no separate release
  approval. This does not authorize an otherwise unrequested commit.
- Open Windows/Office evidence does not block a development version bump. It remains
  visible and must be resolved for any release whose declared qualification requires
  it. A higher version or prerelease suffix is not proof of working Office behavior.
- Internal refactoring, class moves, parser changes and internal protocol changes do
  not justify `17.0.0`. Major requires an intentional incompatible change to a
  published bridge/API, tool package, durable storage, CLI, automation or integration
  contract without a compatible migration path.
- Protocol versions are independent and change according to their own contracts.

## Choosing the bump

Choose by the delivered behavior, not the commit count, file count or internal
phase number. Use the highest applicable level for one coherent change.

| Completed change | Product change | Example from `16.2.3-dev` |
|---|---|---|
| Bug fix; reliability, performance, diagnostics or usability improvement to existing behavior | Patch | `16.2.4-dev` |
| New user-visible capability, workflow, tool or substantial functional extension | Minor; reset patch | `16.3.0-dev` |
| Intentional incompatible change to a published contract described above | Major; reset minor/patch and document the compatibility/reset decision | `17.0.0-dev` |
| Docs/tests/build-maintenance only, behavior-preserving refactoring, unfinished implementation | No bump | `16.2.3-dev` |

A build/dependency/configuration fix that changes delivered behavior is a patch;
its directory or commit label does not make it maintenance-only. Internal wire or
storage renumbering alone does not determine the product bump.

## Completing a change

1. Define the product result and its owner. Several implementation commits may form
   one change; count it once when its consumers are switched and its scoped checks
   and required review are complete. Keep unavailable Windows evidence explicit.
2. Read the current `Directory.Build.props` immediately before the completing commit.
   Select patch/minor/major and update only `RNAssistantVersionPrefix` and
   `RNAssistantVersionSuffix`. If another change advanced the version, recompute
   from the new value; never overwrite it with a stale planned number.
3. Include a concise behavior note under `CHANGELOG.md` → `[Unreleased]` in the same
   commit, with the assigned development version in the note. Do not create a dated
   release section or empty notes for intermediate/docs-only commits.
4. Run the minimal checks for the behavior and, before commit, `ValidateVersionFormat`
   below. A bump alone does not require a build, full harness, remote access,
   signing certificate or Office validation. Report the new version, or why no bump
   applies, when reporting the completed change.

The build does not choose or increment versions. This is a change-owner/review
rule; do not restore the old mandatory per-commit HEAD comparison or add a commit
hook that guesses product semantics from commit messages.

## Development and release versions

Ordinary completed changes advance the numeric prefix and use suffix `dev`.
Intermediate builds keep that product version and differ by SHA/dirty metadata.
Explicit release preparation selects `alpha.N`, `beta.N`, `rc.N` or no suffix for
the chosen numeric version; candidate revisions follow
[Release process](RELEASE_PROCESS.md#milestones-and-qualification). Development
versions are source-build identifiers, not a published update channel: the lexical
SemVer transition from `dev` to `alpha.N` does not express an upgrade sequence.
Published candidates/releases must advance relative to prior published versions.

At adoption, the tracked value is still `16.1.0-alpha.1`, prepared on 2026-10-26
in `4bd9fa55`, with 250 subsequent commits observed during the review. It is not a
tagged or qualified distribution. The docs-only policy change does not bump it.
At the next completed product change, account for the accumulated functionality
with one minor bump to `16.2.0-dev`, unless a published-contract incompatibility
requires an explicit major decision. Summarize the accumulated delivered scope in
Unreleased; do not manufacture versions or tags for the intervening commits.

The previous freeze was documentary: this policy, `AGENTS.md` and development rules
allowed bumps only at a release milestone. `ValidateVersionFormat` already accepts
other valid numeric versions. The remaining `16.1.0` restriction is in the release
script; see its [implementation boundary](RELEASE_PROCESS.md#current-validation-boundary).
It blocks releasing another version with that script, not development bumps.

## Version fields

| Field | Source / example | Rule |
|---|---|---|
| Product | `RNAssistantVersionPrefix`, `RNAssistantVersionSuffix` in `Directory.Build.props` | Only tracked source of product version; completing change updates prefix/suffix |
| Assembly compatibility | `RNAssistantAssemblyVersion=16.0.4.0` | Preserve baseline binding, independent of product changes |
| File / VSTO Application | `<RNAssistantVersionPrefix>.<RNAssistantBuildNumber>` | Default build number 0; CI/release passes it without a commit |
| Informational | `<Version>+g<12-char-sha>` | Add `.dirty` when tracked/index/untracked changes exist; archives without SHA use `+source-archive`, unknown tree state adds `.unknown` |
| Assembly metadata | ProductVersion, CommitSha, BuildUtc, Branch, Channel, WorkingTreeState, Configuration, RuntimePlatform, BuildEvidenceSignerSha256 | Full SHA; UTC timestamp; exact candidate configuration/platform; signer is `unavailable` outside an evidence-bearing candidate |
| Protocol | Defined by each existing contract | Never inferred from product/assembly version |

Numeric version components must be 0–65534. Build number changes do not change
product version. Git identity is resolved at build time and written into both SDK
and old-style assembly metadata. Existing version readers can read informational
version. The Settings navigation renders a known Git build compactly as
`v<product> · <7-char-sha>` and preserves its `dirty`/`unknown` marker, so
intermediate development builds remain distinguishable without a product bump.

For distributed candidates sharing a numeric prefix, the release owner assigns an
increasing `RNAssistantBuildNumber`: prerelease suffixes do not distinguish numeric
File/ApplicationVersion. An ordinary source bump does not qualify ClickOnce updates.

The master plan recommends `AssemblyVersion=16.0.0.0` after checking VSTO/ClickOnce.
That check has not run here. Retaining the actual baseline `16.0.4.0` avoids an
unverified binding change/downgrade. Windows install/update qualification is required
before changing this compatibility version or distributing Office builds.

## Ordinary validation

```sh
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
```

`ValidateVersionFormat` runs before ordinary builds: SemVer, derived numeric versions
and build identity. It does not compare product version with HEAD, require a clean
tree, inspect tags, contact remotes or modify source files.
`ValidateRNAssistantVersion` was replaced, with no compatibility alias.
For changes to versioning implementation, use the focused checks in the
[Harness guide](../../tests/RNAssistant.Harness/README.md); do not run them merely
for a numeric bump. Docs-only work needs diff/link checks; the lightweight format
target still applies if a commit is actually being made.

Git is required for checkout builds. Ordinary source archive builds without `.git`
are allowed in both Debug and Release configurations. Git/GitHub archives substitute
the exact source commit into the tracked archive-provenance file, so their
InformationalVersion is `<Version>+g<12-char-sha>.unknown`; branch and
post-extraction working-tree state remain unknown and the build is not release
evidence. A plain copied archive without the substituted commit remains
`<Version>+source-archive.unknown`. Git failures in an actual checkout still fail.
For exact archive provenance, supply `RNAssistantCommitSha` (full SHA),
`RNAssistantBranch` and `RNAssistantWorkingTreeState=clean|dirty` through MSBuild
properties or the ignored root `Directory.Build.local.props` (preserve any signing
settings already in that file). Explicit malformed metadata still fails validation.
The Visual Studio Release configuration is not an explicit release milestone:
`RNAssistantReleaseBuild=true`, a release tag or direct release validation still
require known provenance and a Git checkout for live clean-tree checks.
CI may also provide `RNAssistantBuildUtc` in `yyyy-MM-ddTHH:mm:ssZ` format,
`RNAssistantBuildNumber` and `RNAssistantRuntimePlatform`. These values are metadata,
not a product bump. Only a candidate for signed qualification evidence supplies the
lowercase certificate-DER SHA-256 as `RNAssistantBuildEvidenceSignerSha256`; ordinary
builds use `unavailable`.

## Release-only gates

| Target | Checks |
|---|---|
| `ValidateReleaseTagMatchesProductVersion` | Exact `v<Version>`; stable or alpha.N/beta.N/rc.N; rejects dev |
| `ValidateReleaseTreeClean` | No tracked, staged or untracked changes |
| `ValidateReleaseChangelog` | Dated exact version section with at least one note |
| `ValidateReleaseEvidenceSigner` | Candidate pins a lowercase SHA-256 signer fingerprint; `unavailable` is rejected |
| `ValidateTagDoesNotExist` | Tag absent locally and on configured remote; inaccessible remote fails closed |
| `ValidateRNAssistantRelease` | Format/tag match, clean tree, changelog and signer pin |

`RNAssistantReleaseBuild=true` or a nonempty `RNAssistantReleaseTag` enables the
release aggregate before build. A tag build must supply `RNAssistantReleaseTag`.
The absence check is preparation-only: rebuilding an existing immutable release
does not demand a new tag. The default remote is `origin`; an alternate configured
name can be supplied as `RNAssistantReleaseRemote`.

No release gate creates or moves a tag. Only the explicitly invoked
[release script](RELEASE_PROCESS.md) creates the annotated tag only in its explicit
finalization step after compatible [exact-build evidence](BUILD_EVIDENCE.md).
