# ADR-0008: Unknown effects are not retried

Date: 2026-08-28
Status: Accepted; Phase 3B2 production adapter and existing-event replay covered host-neutral. Windows/Office qualification remains open.

## Decision

A missing terminal result after entering a possible write/external executor cannot
certify its effect. The kernel records `unknown`, stops the invocation and never
replays/retries that tool automatically. Entering a read runtime without a reliable
result records a read error. An executor can return known terminal evidence even
when cancellation has arrived; cancellation stops the next step without erasing
that evidence. A known error/unknown returned normally may be followed by a new
model step, but later success or optimistic wording cannot clear earlier counts.

`ToolExecutionRecord.MayHaveDispatched` is conservative: true includes ambiguous
entry, not proof of a domain mutation. False certifies non-dispatch. Pending
confirmation and cancellation before entry are not successful effects. Accepted
calls skipped by cancellation or a limit receive explicit non-dispatch results,
so accepted batches are closed before the terminal summary.

`IRunStore.AppendAsync` is a mandatory ordered boundary before model/tool entry
and after accepted responses/results. Appends use a compare-and-swap cursor;
confirmation claims the saved cursor before execution, preventing the same
continuation from dispatching twice. The cursor must advance only after durability.
The Office adapter maps it to the existing event stream and lease/CAS mechanism;
no second durable index or mutable snapshot authority is permitted.

Cancellation cannot discard a mandatory append. The kernel observes it again
before dispatch and writes the final evidence with a non-cancelled token.
Append failure or a non-advancing cursor stops execution. `RunStoreException`
exposes a new, explicitly unpersisted failure summary for the caller; it does not
claim the failed append was absent from disk or invent a persisted terminal.
The caller must reload/validate the authoritative stream before further action.
There is no automatic append retry or recovery execution.

## Consequences and limits

The in-memory continuation is immutable, rebuilt from validated existing events
and full accepted history. The controller's lease and preflight stay in place;
policy/fingerprint checks precede actual confirmed execution in the kernel path.
Replay tests cover pending/cancelled confirmation and stale cursor rejection.

Known terminal counts are saved before optional materialization. If preparation
fails, the run fails without changing those counts. If execution crossed a
persisted in-flight boundary but no terminal evidence is durable, recovery records
unknown for a possible write (error for a read), once, without dispatch. If a
known terminal was saved but its result projection was interrupted, recovery keeps
the counts and excludes the incomplete exchange from model replay.

These existing-store tests contain the minimal Phase 3 R11 gate; full persistence,
UI and host recovery qualification remain Phase 9/12. The legacy executor adapter
still lacks the full typed effect classification planned for Phase 4. VBA journals,
CAS algorithms and tool-result wire schemas are unchanged.

See [ADR-0001](ADR-0001-model-does-not-own-completion.md) for lifecycle/health and
[the migration map](../stabilization/MIGRATION_MAP.md) for active consumers and
removal gates. Windows x64 + Office + VS 2022 qualification remains required for
production controller and host execution.
