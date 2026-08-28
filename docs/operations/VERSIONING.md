# Versioning

Status: accepted for Phase 0. Decision: [ADR-0007](../decisions/ADR-0007-release-only-versioning.md).
Requirements: [master plan, section 13](../stabilization/STABILIZATION_MASTER_PLAN.md#13-версионирование-новая-обязательная-политика).

## Policy

- Commit is not release. No version bump, Git tag or automatic push per ordinary commit.
- Historical baseline is `v16.0.4` (`225a05bb44dd7701892b5f8c98ea2e3b342274a7`); do not create another baseline tag.
- The development target is set once to `16.1.0-dev` on `stabilization/16.1`.
- Product version changes only at an explicitly approved, qualified release milestone.
- Internal refactoring, class moves, parser changes and internal protocol changes do
  not justify `17.0.0`. Major requires an intentional incompatible change to a
  published bridge/API, tool package, durable storage, CLI, automation or integration
  contract without a compatible migration path.
- Protocol versions are independent. Phase 0 does not introduce conversation v3,
  tool-result v1 or change any storage/resource protocol.

## Version fields

| Field | Source / example | Rule |
|---|---|---|
| Product | `RNAssistantVersionPrefix=16.1.0`, `RNAssistantVersionSuffix=dev` | `Version=16.1.0-dev`; only tracked source of product version |
| Assembly compatibility | `RNAssistantAssemblyVersion=16.0.4.0` | Preserve baseline binding, independent of product changes |
| File / VSTO Application | `16.1.0.<RNAssistantBuildNumber>` | Default build number 0; CI/release passes it without a commit |
| Informational | `16.1.0-dev+g<12-char-sha>` | Add `.dirty` when tracked/index/untracked changes exist; archives without SHA use `+source-archive`, unknown tree state adds `.unknown` |
| Assembly metadata | ProductVersion, CommitSha, BuildUtc, Branch, Channel, WorkingTreeState | Full SHA; UTC timestamp; branch or `HEAD` for detached checkout |
| Protocol | Defined by each existing contract | Never inferred from product/assembly version |

Numeric version components must be 0–65534. Build number changes do not change
product version. Git identity is resolved at build time and written into both SDK
and old-style assembly metadata. Existing version readers can read informational
version; no diagnostics UI or runtime code is changed in Phase 0.

The master plan recommends `AssemblyVersion=16.0.0.0` after checking VSTO/ClickOnce.
That check has not run here. Retaining the actual baseline `16.0.4.0` avoids an
unverified binding change/downgrade. Windows install/update qualification is required
before changing this compatibility version or distributing Office builds.

## Ordinary validation

```sh
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"
```

`ValidateVersionFormat` runs before ordinary builds: SemVer, derived numeric versions
and build identity. It does not compare product version with HEAD, require a clean
tree, inspect tags, contact remotes or modify source files.
`ValidateRNAssistantVersion` was replaced, with no compatibility alias.

Git is required for checkout builds. Ordinary source archive builds without `.git`
are allowed in both Debug and Release configurations. Missing commit, branch and
working-tree metadata is recorded as `unknown`, with a build warning; no SHA or clean
state is invented. With no metadata, InformationalVersion is
`16.1.0-dev+source-archive.unknown`. Git failures in an actual checkout still fail.
For exact archive provenance, supply `RNAssistantCommitSha` (full SHA),
`RNAssistantBranch` and `RNAssistantWorkingTreeState=clean|dirty` through MSBuild
properties or the ignored root `Directory.Build.local.props` (preserve any signing
settings already in that file). Explicit malformed metadata still fails validation.
The Visual Studio Release configuration is not an explicit release milestone:
`RNAssistantReleaseBuild=true`, a release tag or direct release validation still
require known provenance and a Git checkout for live clean-tree checks.
CI may also provide `RNAssistantBuildUtc` in `yyyy-MM-ddTHH:mm:ssZ` format and
`RNAssistantBuildNumber`. These values are metadata, not a product bump.

## Release-only gates

| Target | Checks |
|---|---|
| `ValidateReleaseTagMatchesProductVersion` | Exact `v<Version>`; stable or alpha.N/beta.N/rc.N; rejects dev |
| `ValidateReleaseTreeClean` | No tracked, staged or untracked changes |
| `ValidateReleaseChangelog` | Dated exact version section with at least one note |
| `ValidateTagDoesNotExist` | Tag absent locally and on configured remote; inaccessible remote fails closed |
| `ValidateRNAssistantRelease` | Format/tag match, clean tree, changelog |

`RNAssistantReleaseBuild=true` or a nonempty `RNAssistantReleaseTag` enables the
release aggregate before build. A tag build must supply `RNAssistantReleaseTag`.
The absence check is preparation-only: rebuilding an existing immutable release
does not demand a new tag. The default remote is `origin`; an alternate configured
name can be supplied as `RNAssistantReleaseRemote`.

No release gate creates or moves a tag. Only the explicitly invoked
[release script](RELEASE_PROCESS.md) creates the annotated tag after qualification.
