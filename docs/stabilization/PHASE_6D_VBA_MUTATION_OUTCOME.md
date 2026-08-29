# Phase 6D — typed VBA mutation outcome

Date: 2026-08-29

Baseline: `7a588259e72b76e5622ab42eb2fc1664e4390eac`

Status: done host-neutral; whole-module/package ownership and Windows/COM/VBE qualification remain open.

## Scope and ownership

The module-mutation boundary is now typed end to end inside `Office.Vba`:

```text
VbaToolExecutor argument adapter
→ typed request + narrow document/read/backend/journal ports
→ VbaMutationService / VbaVerifier
→ VbaMutationOutcome (Ok | Error | Unknown)
→ VbaMutationToolResultMapper at the current legacy handler boundary
```

`VbaMutationService` and `VbaVerifier` no longer accept or consume `ToolCommand`,
`ChatSession`, legacy `ToolResult`, or the wide `IOfficeApplicationAdapter`.
`IVbaMutationDocumentContext` exposes identity only; `IVbaMutationReader` exposes
typed module snapshots/errors only; `IVbaMutationBackend` exposes the required write action only;
`IVbaMutationJournal` wraps the existing `VbaJournalStore`. The store, CAS and event
schema are unchanged, and there is no second persistence or execution path.

`VbaToolExecutor` still owns complete whole-module write/delete/restore workflows,
the reconciliation outer loop, and package/rename orchestration. Their calls into
the shared module journal/verifier pipeline now use typed action/outcome values, but
moving each full workflow is a later Phase 6 slice.

## Outcome rules

| Inspected evidence | Domain/tool outcome |
|---|---|
| Live state matches intended state and terminal append succeeds | `ok`, including a backend error after an effect that read-back proves |
| Live state matches before state | definite `error` / not applied |
| Live state is unreadable or matches neither before nor intended | non-retryable `unknown` |
| Terminal append fails after inspection | non-retryable `unknown`, `terminalRecorded=false`, preparation remains open |

The common tool result keeps bounded effect/correlation fields such as `mutationId`,
`rollbackBackupId`, `actualExists`, and hashes. It does not expose
`journalStatus`/`packageJournalStatus` or internal terminal classification in its
status/message. The boundary removes those reserved keys, terminal durability,
backend-error and compile-validation claims from backend-provided data before adding
service-owned evidence. Durable classification remains in the VBA journal and diagnostics.
Source/type read-back does not claim VBA compilation or runtime validation.

Rollback is not inferred from words such as `restored`, `removed`, or `rolled back`.
A `rolled_back` journal terminal requires both an explicit typed backend disposition
and verified live-before state. The current legacy host adapter does not synthesize
that disposition from prose. The same prose classifier and public status fields were
removed mechanically from the still-legacy package/rename path; its full typed
outcome semantics remain a later slice.

## Fault matrix

| Case | Host-neutral evidence |
|---|---|
| Journal prepare failure | `vba: mutation prepare failure blocks dispatch` |
| Prepared, no dispatch / restart | `vba: mutation cancellation boundaries`; reused `vba: journal reconciles interrupted mutations` |
| Backend throws before effect | `vba: mutation backend throw before effect` |
| Backend applies intended state then throws | `vba: mutation committed after backend throw` |
| Read-back unavailable | `vba: mutation unavailable read-back is unknown` |
| Read-back mismatch/divergence | `vba: mutation read-back divergence is unknown`; reused write/delete drift cases |
| Terminal append failure | `vba: mutation terminal failure is unknown` |
| Cancellation before/after dispatch | `vba: mutation cancellation boundaries` |
| VBE-style normalization | reused `vba: VBE normalization is accepted` |
| Duplicate/colliding target | reused create/rename race and COM rename collision cases |
| Target not found | reused named-module patch case |
| Prose resembling rollback | `vba: mutation rollback prose is not evidence` |

The injected backend boundary models effect ordering; it is not real COM evidence.
Unknown never triggers a second dispatch. A failed terminal append does not fabricate
a terminal event; next safe access performs the existing read-only reconciliation.

## Verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba:"` — 67/67 pass.
- `agent: characterization completed after write unknown` — 1/1 pass.
- `causal trace:` — 6/6 pass.
- `harness: production projects include all source files` — 1/1 pass.
- MockDemo actual-controller compile — 0 errors; existing platform warnings only.
- `ValidateVersionFormat`, changed Markdown links and `git diff --check` — pass.

No Windows x64 + Office x64 + VS 2022, VSTO, real COM/VBE, controller delivery, or
live-provider validation was run. Those remain mandatory in WQ-VBA/WQ-CROSS before
Phase 12. Product version and tags are unchanged; this commit is not a release.
