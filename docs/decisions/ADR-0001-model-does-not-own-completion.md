# ADR-0001: Model does not own completion

Date: 2026-08-28
Status: Accepted; Phase 3B1 kernel introduced and tested with fake ports. Production switch and existing-event replay remain Phase 3B2.

## Context

Empty v3 tool calls end model generation; they cannot prove a document effect.
Phase 3A isolated materialization in Office. A full switch still couples model
context, normal/confirmation execution, durable events and visible projections.
Per [§14.3](../stabilization/STABILIZATION_MASTER_PLAN.md#143-change-budget),
3B1 introduces the kernel contract; 3B2 connects those current consumers and
removes their replaced loop/accounting paths.

## Decision

- `Core/Agent/AgentKernel` consumes immutable `AgentMessage`, `AgentResponse`,
  `ToolCall`, execution records and three ports: `IModelProtocol`, `IToolRuntime`,
  `IRunStore`. It has no Office, HTTP, resource lifecycle, compaction or UI calls.
- `IModelProtocol.SendAsync` receives accepted messages and the full current-turn
  ID set. Only accepted responses, typed boundary failures and separate native
  refusals cross it. The materialized endpoint boundary is explicitly named
  `IMaterializedModelProtocol.GetResponseAsync`; its existing client and v3 retry
  behavior are unchanged. The future Office adapter uses `ConversationModelSession`
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

The kernel currently has only harness consumers; it is not selected by production
start/continue, feature flags or fallback. `ConversationRunService`,
`RunSummaryBuilder`, `Failure.Cause` and current LastRun/bridge projections stay
active until the 3B2 switch. 3B2 must connect the executor and existing typed event
store, preserve controller guards, and prove normal/error/unknown/confirmation
summary replay before Phase 3 can close. Full ToolRuntime, Tool Result v1, resource
and persistence/UI redesigns remain in their own phases.

Fake model/tool/store tests cover outcome ordering, IDs, budgets, cancellation,
confirmation and append faults. They do not prove existing-store replay, live
providers or Windows/Office/controller delivery. See
[PROGRESS](../stabilization/PROGRESS.md#phase-3b1--pure-kernel-introduction) and
[MIGRATION_MAP](../stabilization/MIGRATION_MAP.md).
