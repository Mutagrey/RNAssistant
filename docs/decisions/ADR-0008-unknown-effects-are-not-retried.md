# ADR-0008: Unknown effects are not retried

Date: 2026-08-28
Status: Accepted; Phase 3B1 pure-kernel coverage. Production adapter/replay qualification remains open.

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
The adapter must map it to the existing event stream and lease/CAS mechanism;
no second durable index or mutable snapshot authority is permitted.

Cancellation cannot discard a mandatory append. The kernel observes it again
before dispatch and writes the final evidence with a non-cancelled token.
Append failure or a non-advancing cursor stops execution. `RunStoreException`
exposes a new, explicitly unpersisted failure summary for the caller; it does not
claim the failed append was absent from disk or invent a persisted terminal.
The caller must reload/validate the authoritative stream before further action.
There is no automatic append retry or recovery execution.

## Consequences and limits

The in-memory continuation is immutable and is not durable authority. Its recovery
from validated existing events, crash interruption, pending cancellation cleanup,
and real controller leases/fingerprint checks must be wired and verified in 3B2.
Fake-port CAS/fault tests cannot close R11 or certify the existing storage recovery.
Domain read-back/journals continue to own actual effect evidence; this change
does not alter VBA, CAS, event schemas or tool-result wire serialization.

See [ADR-0001](ADR-0001-model-does-not-own-completion.md) for lifecycle/health and
[the migration map](../stabilization/MIGRATION_MAP.md) for active consumers and
removal gates. Windows x64 + Office + VS 2022 qualification remains required for
production controller and host execution.
