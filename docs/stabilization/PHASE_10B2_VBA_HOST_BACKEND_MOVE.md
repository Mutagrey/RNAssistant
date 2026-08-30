# Phase 10B2 — VBA host backend physical move

Date: 2026-08-31
Baseline: `97dfe62e439298abbd2fe957b431ecec3681a56f`

## Scope

Both `VbaProjectSupport` partials moved with `git mv` from
`RNAssistant.Office/Vba` to `RNAssistant.OfficeHosts/Vba`. Their namespace, both
production old-style project files, harness source links, three host adapters and
two harness code consumers were updated.

The COM/VBE algorithms, package guard comparisons, mutation services, journals,
typed contracts, factories and model-facing tools are unchanged. No alias, linked
duplicate or second backend remains in the Office assembly.

## Assembly boundary correction

The move exposed one dependency hidden by the source-linked harness: the package
guard consumes `VbaPackageOwnershipMarker`, which was `internal` to the Office
assembly. A production OfficeHosts build could not access it even though the harness
compiled both sources into one test assembly.

The pure marker parser remains owned by `Office.Vba` and is now an explicit public,
read-only contract with a private constructor. Parser code and results are unchanged.
No duplicate parser or broad `InternalsVisibleTo` friendship was introduced. The
architecture case requires this public boundary and rejects every
`VbaProjectSupport`/`DocumentIdentity` consumer in the Office assembly.

This closes R49 host-neutral. Production OfficeHosts/VSTO compilation and real VBE
behavior remain part of the accumulated Windows qualification gate.

## Verification

- connected `COM` harness slice — 47/47 pass, including the five direct helper
  write/rename/package cases;
- exact UserForm create/edit helper case — 1/1 pass;
- exact package COM install-guard case after the assembly-boundary correction —
  1/1 pass;
- mandatory architecture checks — 4/4 pass;
- production old-style source inclusion — 1/1 pass;
- moved sources differ from baseline only by namespace;
- `ValidateVersionFormat`, `git diff --check` and changed-document links — pass.

OfficeHosts COM, VSTO and real Office/VBE validation was not run on this machine.
Windows x64 + Office + VS 2022 qualification remains required. Product version stays
`16.1.0-dev`; no release/tag/push.
