# Phase 6E — whole-module VBA write ownership

Date: 2026-08-29

Baseline: `26f678c60823aef6231c072b7c07dacc01891e57`

Status: done host-neutral; delete/restore/package ownership and Windows/COM/VBE qualification remain open.

## Scope and ownership

The `upsert`, `createOnly`, and `updateOnly` branches of
`common.vba_write_module` now have one typed domain workflow:

```text
VbaToolExecutor argument/mode adapter
→ VbaWholeModuleWriteRequest + typed guard
→ VbaMutationService.WholeModuleWrite
→ typed read + create/replace backend
→ prepared journal + source/type read-back + terminal
→ VbaMutationOutcome (Ok | Error | Unknown)
→ current Tools result adapter
```

`VbaMutationService` owns deterministic name normalization, target-existence
preparation, observation/confirmation guard binding and recheck, mode refusal,
dry-run, journal preparation, create-versus-replace selection and verification.
The removed executor path has no alias or fallback. `VbaToolExecutor` still owns
legacy argument/result adaptation and routes `mode=rename` to its unchanged
identity/package-journal contour.

`IVbaMutationBackend` gained one typed create action. Its current Tools adapter is
the sole place that constructs the legacy internal create command. The COM
implementation, HostRuntime gate, document binding, journal/CAS bytes, tool schema,
confirmation policy and public result wire did not change.

## Safety correction

Reconciliation after a backend error previously compared module source hashes but
did not require the live component type to match the prepared state. A racing
component with identical source and a different type could therefore look
committed. Module assessment now requires both applicable source representation and
component type whenever the live component exists.

The regression case creates that exact race. It produces durable `unknown` and a
non-retryable tool outcome; dispatch occurs once. `createOnly` against an existing
target and `updateOnly` against a missing target remain definite errors before a
journal preparation or backend action.

## Cleanup and remaining boundaries

Removed from Tools:

- `WriteVbaModule` orchestration;
- `PrepareWriteGuard` and `BindWriteGuard`;
- executor-owned whole-write mode/existence/journal/create/replace/read-back logic.

Still intentionally executor-owned:

- delete and restore workflows;
- rename and package operations;
- recovery/reconciliation outer loop;
- legacy command/result mapping.

The next separate slice is Phase 6F delete ownership. Production document identity
and typed host binding still depend on Phase 5B2 and Windows qualification.

## Verification

- `vba: whole write service owns workflow` — 1/1 pass.
- Full `vba:` filter — 68/68 pass.
- `agent: characterization` — 7/7 pass, including write ok/error/unknown.
- `causal trace:` — 6/6 pass.
- `harness: production projects include all source files` — 1/1 pass.
- Harness compile — 0 errors, 4 existing platform warnings.
- MockDemo actual-controller compile — 0 errors, 3 existing platform warnings.
- `ValidateVersionFormat`, changed Markdown links and diff checks — pass.

No Windows x64 + Office x64 + VS 2022, VSTO, real COM/VBE, controller delivery or
live-provider validation was run. Product version and tags remain unchanged; this
commit is not a release.
