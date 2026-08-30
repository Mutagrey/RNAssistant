# Phase 10B1 — host document identity physical move

Date: 2026-08-31
Baseline: `ab0b723eca9dad6511238d633b5ad1d8d6001eae`

## Scope

`DocumentIdentity.cs` moved with `git mv` from `RNAssistant.Office/Runtime` to
`RNAssistant.OfficeHosts/Identity`. Its namespace, the two old-style project files,
the three host-adapter consumers and the source-linked harness consumer were updated.

The identity algorithm, fallback behavior, property name, COM identity lease,
production factories and WQ0 semantics are unchanged. Phase 5B2/7D and both
`VbaProjectSupport` files remain outside this change.

## Boundary and cleanup

`RNAssistant.Office` no longer contains or compiles the host-specific document
identity helper. `RNAssistant.OfficeHosts.Identity` is its single production owner.
The architecture check now rejects every `DocumentIdentity` consumer in the Office
assembly; only the still-unmoved `VbaProjectSupport` partials retain a temporary
source exclusion until 10B2. No alias, forwarding type or duplicate source path was
left behind.

R49 remains open only for the two `VbaProjectSupport` partials. The next atomic
change is 10B2; it must not alter VBA domain, journal, guard or COM algorithms.

## Verification

- document/catalog and identity harness slice — 4/4 pass;
- mandatory architecture checks — 4/4 pass;
- production old-style source inclusion — 1/1 pass;
- `ValidateVersionFormat` and `git diff --check` — pass.

The harness compiled the moved helper through its production source link. The
OfficeHosts COM project, VSTO add-ins and real Office identity behavior were not
validated on this machine. Windows x64 + Office + VS 2022 qualification, including
WQ0, remains required. Product version remains `16.1.0-dev`; no release/tag/push.
