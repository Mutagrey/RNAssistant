# ADR-0007: Release-only product versioning

Date: 2026-08-28
Status: Accepted (Phase 0)

## Context

Per-commit product bumps and tags confuse internal progress with releases and cause
assembly binding churn. The stabilization baseline is `v16.0.4`; the target is
`16.1.0`. The mandatory [master plan](../stabilization/STABILIZATION_MASTER_PLAN.md)
supersedes the former contributor policy.

## Decision

- Set `16.1.0-dev` once. Ordinary commits keep that product version and create no tags.
- Separate `ValidateVersionFormat` from explicit release/tag checks. Remove the old
  validation target and per-commit HEAD comparison policy; retain no alias.
- Identify checkout builds through product version, full Git SHA, UTC, branch/channel
  and clean/dirty metadata in SDK and old-style assemblies. Ordinary source archives
  may record missing provenance as `unknown`, with a warning and `source-archive`
  informational version when the SHA is unknown; they are not release evidence.
- Separate numeric file/application build number from product version.
- Preserve baseline AssemblyVersion `16.0.4.0` until Windows/VSTO/ClickOnce qualification.
  Do not downgrade it to the suggested `16.0.0.0` without that evidence.
- Create immutable annotated tags only at qualified, explicitly approved release
  milestones. The release script checks local/remote absence and never pushes by default.
- Keep protocol versions independent; internal refactoring alone does not warrant a major.

## Consequences

Ordinary development works without a version bump or remote access. Release preparation
adds clean-tree, exact tag/product, changelog and uniqueness gates; unavailable remote
state blocks release. Ordinary archives no longer require manually supplied provenance
to build; missing provenance is explicit, never a fabricated SHA or clean-tree claim.
Release validation remains strict and requires a Git checkout for live tree checks.
Dirty local builds are marked and are not release evidence.

Runtime, model/tool protocols, Resource Fabric, VBA, UI and persistence are unchanged.
PowerShell release execution and Windows/Office packaging remain separately qualified.

Canonical procedures: [VERSIONING.md](../operations/VERSIONING.md) and
[RELEASE_PROCESS.md](../operations/RELEASE_PROCESS.md).
