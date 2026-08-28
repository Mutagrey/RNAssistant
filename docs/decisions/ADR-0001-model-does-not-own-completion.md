# ADR-0001: Model does not own completion

Date: 2026-08-28
Status: Accepted; Phase 3B2 production wiring and existing-event replay verified host-neutral. Windows/Office delivery qualification remains open.

## Context

Empty v3 tool calls end model generation; they cannot prove a document effect.
Phase 3A isolated materialization in Office. The switch coordinates model context, normal/confirmation execution, durable
events and visible projections.
Per [§14.3](../stabilization/STABILIZATION_MASTER_PLAN.md#143-change-budget),
3B1 introduced the kernel contract; 3B2 connects those consumers and removes
their replaced loop/accounting paths in one coordinated change.

## Decision

- `Core/Agent/AgentKernel` consumes immutable `AgentMessage`, `AgentResponse`,
  `ToolCall`, execution records and three ports: `IModelProtocol`, `IToolRuntime`,
  `IRunStore`. It has no Office, HTTP, resource lifecycle, compaction or UI calls.
- `IModelProtocol.SendAsync` receives accepted messages and the full current-turn
  ID set. Only accepted responses, typed boundary failures and separate native
  refusals cross it. The materialized endpoint boundary is explicitly named
  `IMaterializedModelProtocol.GetResponseAsync`; its existing client and v3 retry
  behavior are unchanged. The Office adapter uses `ConversationModelSession`
  to materialize requests/results; provider metadata stays outside the kernel.
- New execution-result messages retain their typed `ToolExecutionRecord`, including
  synthetic errors/unknowns/non-dispatch. Result bodies are opaque here; the
  external serializer must not infer outcomes from narrative. Already materialized
  prior-turn history cannot seed current counts or authorize execution.
- `RunSummary` separates lifecycle (`running`, `completed`,
  `awaiting_confirmation`, `cancelled`, `failed`) from health
  (`clean`, `errors`, `unknown`). Empty calls mean `completed`; narrative
  words such as “done”, “blocked” or “refused” are not lifecycle instructions.
  A native provider refusal is locally classified as `failed / provider_refused`.
- Actual execution records exclusively determine counts and health.
  Any unknown write/external effect wins over errors; otherwise any tool error
  wins over clean. A read without a reliable result is a read error.
  Pending and definitely non-dispatched calls add no outcome counts.
  Counts represent invocations, not changed cells or independent read-back proof.
- Normal and confirmation execution use the same kernel accounting method.
  A continuation preserves the logical turn, limits, accepted IDs and counts.
  Policy/revision is checked again before execution; the adapter still owns
  argument validation, authorization and live confirmation/fingerprint gates.
  Only independent local reads may be batched, sequentially.
- Iteration/tool limits, cancellation, protocol failures and confirmation are
  runtime decisions. No automatic tool retry is added. A local interaction can
  end the invocation as `completed / awaiting_user` without another lifecycle.

## Scope and remaining gate

`ConversationRunService` invokes the kernel for start and confirmation through
`ConversationKernelAdapter` model/tool/store ports. The controller retains its
lease, document and preflight gates; it no longer executes or aggregates the
confirmed result. `RunSummaryBuilder`, the Office loop, transient ID accumulator
and `Failure.Cause` are removed. No feature flag, alias or second loop remains.

The existing typed event stream carries immutable `KernelState`; flat run DTOs
are derived from it, while old records remain inspectable. Complete accepted
history reconstructs a pending continuation without an ID index or historical
backfill. Legacy result mapping/local-read classification remain until Phase 4;
working-set/resource implementation remains outside Core until Phase 8; complete
persistence/UI normalization remains Phase 9.

Pure tests and real ChatStore replay cover outcomes, confirmation, cancellation,
CAS and interruption. MockDemo compiles the actual controller, but this is not
Windows/Office execution or delivery validation. See
[cutover evidence](../stabilization/PHASE_3B2_KERNEL_CUTOVER.md) and
[MIGRATION_MAP](../stabilization/MIGRATION_MAP.md).
