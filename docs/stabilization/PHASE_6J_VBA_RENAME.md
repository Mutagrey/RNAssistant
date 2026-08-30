# Phase 6J — typed VBA rename ownership

Date: 2026-08-30
Baseline: `6f9ddcc275d4d20addc08917c533a54ef8082c58`

## Scope

This host-neutral slice moves the complete existing `common.vba_write_module`
`mode=rename` workflow into `Office.Vba.VbaMutationService`:

- source and destination resolution plus confirmation guard;
- two-identity durable preparation before dispatch;
- typed host backend action with source hash/type compare-and-swap evidence;
- old/new identity, source, type and code-only UserForm read-back;
- typed `ok/error/unknown` outcome and terminal persistence;
- read-only recovery of interrupted rename records.

Package lifecycle, public schemas, Tool Result v1, HostRuntime/document binding,
dynamic tool authoring, UI and other domains are unchanged. The three host adapters
only forward the added typed source-type guard to the existing shared COM helper;
real COM behavior is not claimed by host-neutral checks.

## Ownership and cleanup

`VbaToolExecutor` now parses/maps rename arguments and typed outcomes, and calls the
serialized reconciliation owners. Its guard validation, backend command creation,
two-component preparation, assessment, terminal mapping and recovery helpers were
deleted. The executor journal partial dropped from 373 lines to the common
reconciliation caller; no compatibility alias or second dispatch path remains.

`IVbaRenameJournal` is a narrow port over the existing `VbaJournalStore`. Rename
deliberately retains the established `package.mutation.prepared/terminal` two-name
wire and CAS references so trajectory and diagnostics remain unchanged. The port
filters rename records and does not create a new store, snapshot, index, generic
transaction framework or dual-write.

## R42 correction and effect rules

The previous confirmation guard bound source code hash and both names, but not the
source component type. A same-source external replacement with another renameable
type could therefore cross the confirmed boundary. The executor workflow also did
not carry the cancellation token through its durable prepare-to-dispatch interval.

The typed guard now binds source hash, component type and code-only UserForm state,
both resolved/requested names, document identity and accepted-call correlation. The
backend receives source hash/type and checks both plus destination absence before
renaming. Read-back classifies the two identities as a single logical mutation:

- old absent and new source/type match: `committed` → `ok`;
- old source/type match and new absent: `not_applied` → definite `error`;
- both names present, both absent, collision/divergence or unreadable state:
  `unknown` → non-retryable `unknown`.

Verified intended state wins over a backend error or cancellation after dispatch.
Cancellation after preparation but before dispatch records inspected before state
and rethrows. Terminal append failure returns `unknown` with
`terminalRecorded=false`; later reconciliation observes and appends a terminal but
never invokes the backend again.

## Host-neutral verification

- `vba: rename`: 5/5 pass for confirmation race plus the new typed owner, effect
  faults, recovery states and cancellation boundaries.
- Public strict schema/result/journal projection and shared fake-COM component
  identity/type CAS regressions pass in the full slice.
- Full `vba:` regression: 91/91 pass, including unchanged module/package/VBE and
  serialized read/reconciliation cases.
- Production source-project inclusion: 1/1 pass.
- MockDemo actual-controller compile: pass, 0 errors / 3 existing CA1416 warnings.
- `ValidateVersionFormat` and `git diff --check`: pass; 10 changed Markdown
  files contain 161 local links with 0 missing targets.

No full harness or Office/VSTO execution is claimed.

## Open gates

- Windows x64 + Office x64 + VS 2022: Excel/Word/PowerPoint VBIDE rename, Trust
  Access denial, source type/identity race, destination collision after prepare,
  cancellation before/after dispatch, read-back/terminal loss and restart recovery;
- Phase 5B2 production document identity/factory switch and R04;
- Phase 7 Excel read/write and the remaining release gates.

This commit remains `16.1.0-dev`; it is not a release or Windows-qualified
candidate.
