# Phase 6G — VBA restore ownership

Date: 2026-08-30

Baseline: `f8c267488a8105d12db9043a91ccfdab318af420`

Status: done host-neutral; package/rename ownership and Windows/COM/VBE
qualification remain open.

## Scope and ownership

`common.vba_restore_backup` now has one typed domain workflow:

```text
VbaToolExecutor argument/guard/result adapter
→ exact backup lookup through the canonical journal port
→ VbaRestoreRequest + restore-specific typed guard
→ VbaMutationService.RestoreBackup
→ current-state/type recheck + prepared journal
→ typed create-or-replace backend action
→ source/type read-back + terminal
→ VbaMutationOutcome (Ok | Error | Unknown)
→ current Tools result adapter
```

`VbaMutationService` owns exact backup selection, confirmation guard preparation
and validation, dry-run, current target inspection, component-type policy, journal
preparation, backend action and verified terminal outcome. The removed executor
path has no alias or dual execution fallback. `IVbaMutationJournal` reads backup
metadata/body through the same `VbaJournalStore` and exposes only a narrow immutable
backup snapshot, not the storage DTO or CAS reference. There is no second store or
durable snapshot. The current Tools adapters remain one-way seams for host commands
and legacy `ToolResult` mapping.

The Phase 6G audit recorded R40: the previous generic guard pinned current module
state but not the exact backup identity/live source. Runtime normally pinned the selected
id in command arguments, yet the guard itself could not reject a substituted id.
The dedicated restore guard closes that gap host-neutrally; Windows confirmation
qualification remains open.

## Safety properties

- preparation resolves latest-by-module selection to one exact `backupId` before
  confirmation;
- the guard binds document/chat/module, exact backup id/type/canonical live-source hash,
  and current target existence/source hash without embedding backup source;
- backup id/live-source substitution, missing/malformed guard, stale target and
  incompatible existing component type stop before journal preparation or backend
  dispatch;
- restore journals current live source as rollback evidence before dispatch;
- replace receives the live target SHA-256 as compare-and-swap evidence; missing
  targets use typed create with the backup component type;
- backend success is insufficient: `ok` requires verified source and component
  type plus a durable terminal record;
- accepted call/run/turn/step correlation comes from the prepared guard and is
  retained in the new mutation record.

The COM implementation, HostRuntime gate, document binding, journal/CAS event
format, public schema, confirmation policy and Tool Result v1 wire did not change.

## Cleanup and remaining boundaries

Removed from Tools:

- executor-owned backup lookup, restore journal/backend/read-back orchestration;
- generic restore-only prepare/validate/bind guard helpers;
- direct restore write helper and executor alias to the shared verifier.

Still intentionally executor-owned:

- rename and package operations;
- recovery/reconciliation outer loop;
- legacy argument/result mapping.

The next separate 6H step must first audit consumers and decide the stable-core
scope/order for package lifecycle and remaining rename ownership. Production
document identity and typed host binding still depend on Phase 5B2 and Windows
qualification.

## Verification

- `vba: restore service owns workflow` — 1/1 pass.
- Full `vba:` filter — 70/70 pass.
- `agent: characterization` — 7/7 pass.
- `causal trace:` — 6/6 pass.
- `harness: production projects include all source files` — 1/1 pass.
- Total — 84 distinct targeted harness cases.
- Harness compile — 0 errors, 4 existing platform warnings.
- MockDemo actual-controller compile — 0 errors, 3 existing platform warnings.
- `ValidateVersionFormat`, changed Markdown links and diff checks — pass.

No Windows x64 + Office x64 + VS 2022, VSTO, real COM/VBE, controller delivery or
live-provider validation was run. Product version and tags remain unchanged; this
commit is not a release.
