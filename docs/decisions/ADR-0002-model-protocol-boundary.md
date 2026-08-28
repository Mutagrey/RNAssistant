# ADR-0002: ModelProtocol owns raw model attempts

Date: 2026-08-28
Status: Accepted (Phase 2A boundary + Phase 2B retry policy; v3 work remains)

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
  consume a protocol response slot or become tool errors. Phase 2B adds the
  bounded provider policy below. The explicit, enabled strict-schema fallback is
  bounded to one extra request, using the same options object as its trace sink.

## Retry policy (Phase 2B)

`ModelProtocolRetryBudget` is created once per `GetResponseAsync`, not for each
repair or raw request. A protocol attempt counts a received completion submitted
to the v2 parser (or accepted native refusal), including the first response.

| Budget | Limit | On exhaustion |
|---|---|---|
| Protocol responses | `MaxAgentFormatRetries`, default 10, normalized 1–20 total | `ProtocolExhausted`, no accepted completion |
| Transient provider retries | Two for the entire step; cancellable delays of 1s, then 2s | `Provider` with the original failure kind/status/cause |
| Explicit schema fallback | One, enabled by `FallbackToJsonObject`; also during repair | No second fallback |

Only typed `Timeout`, `Network` and `TransientServer` failures retry. Other HTTP
errors (including authorization failures), rate limiting, size limits and invalid
provider envelopes remain terminal provider failures. No body-text heuristics or
endpoint failover are introduced. This policy acts on the existing LLM adapter's
typed classification; it does not change HTTP parsing/classification.

Provider retries/fallback reuse the exact current prompt, including any single
repair instruction. They do not advance the protocol counter. The provider budget
does not reset after a rejected completion; it resets at the next logical step.
The json_object choice remains run-local and never changes saved settings. With
limit N the raw request ceiling is N+3, at most 23; a healthy transport with twenty
invalid responses makes exactly twenty requests. Every raw request gets its own
modelAttemptId. Existing rejected trace `Attempt` remains a zero-based index;
repair instruction `attempt`/`max_attempts` and diagnostics use total responses.

Cancellation is checked before dispatch, during backoff, after a completion and
after rejection. A late completion cannot be accepted once cancellation is
observed. Tools, resources, summaries and accepted-history appends stay outside
the retry loops.

## Transitional contracts

Phase 2A preserved initial + configured retries; Phase 2B removes that extra
attempt (R20). The existing `MaxAgentFormatRetries` settings/bridge key and stored
numeric values remain; the value now means total protocol responses. The caption
and tooltip explain this change. There is no second key, alias or settings rewrite.
V2 parsing/status/history remain unchanged.

`ModelProtocolFailure.Cause` is a nonserialized exception adapter. Owner: Runtime /
Application. Consumer: the loop rethrows it with its original stack into the
existing controller cancellation/failure handling. Removal: Phase 3 AgentKernel
integration. Accepted `LlmCompletionResult` and the existing context-usage
projection are metadata for current transcript consumers, not a second protocol
or durable result store.

The v3 parser/schema, explicit v2 adapter and v3 canonical document remain in Phase 2.
This ADR does not declare that phase complete or authorize Phase 3 in this commit.

## Consequences and verification

The old loop completion/parse/repair/fallback/trace methods and
`AgentJsonProtocol.CreateFormatRepairMessage` are removed, without aliases or dual
execution. Tool orchestration, completion guard and native refusal behavior stay
unchanged. Media may now be sent again on a repair; memory/traffic cost is tracked
as R24. Provider retries may repeat billable generation or extend latency after a
lost response (R25); they cannot replay Office tool execution. Real provider,
Windows/Office/controller/WebView qualification remains open.

Evidence and exact commands: [Phase 2A](../stabilization/PHASE_2A_MODEL_PROTOCOL.md),
[Phase 2B](../stabilization/PHASE_2B_RETRY_POLICY.md).
