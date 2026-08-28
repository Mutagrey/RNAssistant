# ADR-0002: ModelProtocol owns raw model attempts

Date: 2026-08-28
Status: Accepted (Phase 2A boundary; remaining Phase 2 work is not complete)

## Context

`ConversationRunService` mixed tool orchestration with endpoint calls, format
repair, native refusals, compatibility fallback and model diagnostics. The
[master plan](../stabilization/STABILIZATION_MASTER_PLAN.md) assigns those model
concerns to Core and requires the loop to consume one typed outcome per step.

## Decision

- Introduce `IModelProtocol` / `ModelProtocolClient` in `RNAssistant.Core/ModelProtocol`.
  One instance serves one run; only the endpoint-format fallback choice survives
  between steps. Confirmation continuation creates a fresh instance, as before.
- Pass the accepted materialized prompt, current callable tools, runnable catalog
  and request-local options. Return either an accepted `AgentResponse` with its
  completion/usage metadata, or a typed `ModelProtocolFailure`. No rejected
  completion is returned. Core neither executes tools nor changes chat history,
  resource revisions or the working set.
- Each repair copies the accepted message sequence and appends one current fixed
  instruction. Rejected bodies and earlier repair instructions are not replayed.
  Hydrated media remains available throughout that logical protocol step; the
  loop releases it in `finally` after acceptance/failure, before tool execution.
- Keep request/response persistence on the existing configured trace sink. Core
  creates distinct raw attempt ids and accepted/rejected markers; the loop owns
  the logical step id. Rejected diagnostic failure stops execution. Failure of
  the optional accepted marker cannot change an accepted outcome.
- Keep provisional content/reasoning presentation in the Office stream projector,
  reset per attempt through callbacks. It is not accepted history. No UI code or
  storage format changes are part of this extraction.
- Transport errors and cancellation return distinct typed failures; they never
  consume another format retry or become tool errors. No general provider retry
  is introduced. The existing explicit, enabled strict-schema fallback remains
  bounded to one extra request, using the same options object as its trace sink.

## Transitional contracts

Phase 2A preserves v2 parsing/status/history and the legacy setting's meaning:
initial request plus `MaxAgentFormatRetries` (default 10, clamp 1–20 retries).
Thus the old maximum is still 21 requests, excluding schema fallback (R20).
Fallback is still handled for the first raw call of a logical step, not for an
explicit endpoint rejection during a later format repair. Its choice remains
local to the run and never changes saved settings.

`ModelProtocolFailure.Cause` is a nonserialized exception adapter. Owner: Runtime /
Application. Consumer: the loop rethrows it with its original stack into the
existing controller cancellation/failure handling. Removal: Phase 3 AgentKernel
integration. Accepted `LlmCompletionResult` and the existing context-usage
projection are metadata for current transcript consumers, not a second protocol
or durable result store.

The total 1–20 attempt policy, complete provider/protocol retry policy, v3
parser/schema, explicit v2 adapter and v3 canonical document remain in Phase 2.
This ADR does not declare that phase complete or authorize Phase 3 in this commit.

## Consequences and verification

The old loop completion/parse/repair/fallback/trace methods and
`AgentJsonProtocol.CreateFormatRepairMessage` are removed, without aliases or dual
execution. Tool orchestration, completion guard and native refusal behavior stay
unchanged. Media may now be sent again on a repair; memory/traffic cost is tracked
as R24. Real provider, Windows/Office/controller/WebView qualification remains open.

Evidence and exact commands: [Phase 2A](../stabilization/PHASE_2A_MODEL_PROTOCOL.md).
