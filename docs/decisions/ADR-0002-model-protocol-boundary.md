# ADR-0002: ModelProtocol owns raw model attempts

Date: 2026-08-28
Status: Accepted (2A boundary, 2B retry policy, 2C1 v3 contract, 2C2 context adaptation; runtime cutover remains)

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

## V3 introduction and explicit legacy read (Phase 2C1, historical)

Introduce `ConversationResponse`, parser/schema builder and a canonical v3 writer
in Core/ModelProtocol. V3 contains only `message` and `tool_calls`; no Status member
or universal runtime status is added. Validate exact callable names, original
argument schemas, accepted-run ID uniqueness and singleton safety before acceptance.
The caller supplies accepted IDs and an explicit batch-safe read-only set; missing
classification forces singleton. Parsing does not reserve IDs or execute tools.

`ConversationResponseV2Adapter.Read` is a separate historical-envelope entrypoint,
not an automatic live-parser fallback. A known v2 status identifies the old format
and is discarded; continuation follows the call list, never a success assertion.
Historical names/arguments need not match a current catalog and grant no execution
authority. Owner: ModelProtocol; current consumers: focused harness; intended
consumer: history projection at cutover; original removal plan: Phase 10.
This historical compatibility plan is superseded by the 2026-08-28 amendment below.

The complete switch exceeds the ten-production-file budget: it includes current
DTO/parser/schema, ModelProtocol contracts/client, AppSettings prompts, transcript,
AgentJsonProtocol, loop, compatibility probes and project includes. Apply §14.3:
2C1 introduced the tested contract; the original plan assigned adapt/switch/delete
to 2C2. The result below records the completed adaptation and remaining 2C3 switch. There is
no feature flag, dual execution or dual-write. Active requests/history still use
v2; the current `AgentResponseProtocol.CurrentVersion` remains 2. Native refusals,
retry counts, runtime health and Office dispatch are unchanged.

[The v3 canonical contract](../protocols/CONVERSATION_RESPONSE_V3.md) defines the
remaining gates: saved prompts, complete run-ID/effective-safety context (R26),
all current-run history forms, v3-only new accepted writes and removal of superseded
live v2 paths. Phase 2 is not complete and Phase 3 is not authorized by this commit.

## Amendment 2026-08-28 — local cleanup and no historical compatibility requirement

Preserving old chats/formats is not a stabilization requirement. At the v3 cutover,
incompatible old chats require an explicit skip/reset boundary without automatic
deletion, stream rewriting or silent history truncation. Current v3 run history,
replay, confirmation and complete accepted-run ID scope remain required.

The v2 read adapter must not be wired solely to preserve old chats. Recheck actual
consumers at cutover and remove it with obsolete tests when no necessary runtime
consumer remains. Temporary retention requires an owner, concrete consumers,
reason and nearest removal gate. Master plan §15.1 makes verified local cleanup
mandatory in each substep; Phase 10 is a final structural audit. This amendment
changes the migration plan, not the recorded 2C1 implementation or validation.

## Consequences and verification

### Context adaptation and cleanup (Phase 2C2)

The next v3 switch needs complete accepted-run IDs without importing Office
session/compaction logic into Core. `ConversationProtocolContext` now owns that
transient bookkeeping in the existing loop and passes detached
`ModelProtocolCallContext` snapshots to each logical model step. It records the
entire accepted response before dispatch; rejected attempts cannot reserve IDs.
Confirmation seeds the full latest user turn, including compacted/suppressed
records, rather than the prompt window or confirmation's new `RunId`. An
incomplete seed stays explicit; the v3 context overload rejects it. The active v2
client does not enforce this new context yet.

Core's current-v3 history reader handles canonical envelopes, identified single
native calls and literal final text without mutating sources or granting tool
authority. The 2C1 v2 read adapter had no production consumers: it, its legacy JSON
branch, project include and obsolete tests are removed now. Only a typed-ID
helper for the current v2 transcript remains in the Office context builder; delete
it at the coordinated v3 writer switch, not in Phase 10. Old chats need explicit
skip/reset; no compatibility parser, silent truncation or historical rewrite.

Legacy ToolDefinition lacks external-effect metadata. The context therefore uses
an explicit audited local-read set intersected with built-in binding and existing
effective safety; unknown/external tools and all pipelines remain singleton.
This is a conservative projection for the future parser, not a new executor
policy. Ownership/removal: bookkeeping to AgentKernel in Phase 3; replace the
positive registry with typed ToolPolicy and nested/external tests in Phase 4.

Nine production files (including project includes and the deleted adapter) change
within the ModelProtocol adaptation. Real host-neutral loop/executor tests verify
accepted-only IDs and confirmation after compaction, while focused Core tests
verify history/context failures. The next Phase 2C3 change can consume this
contract without rediscovering session boundaries; it must still switch client,
prompts/schema/writes and remove live v2 consumers together. No Phase 3 extraction
or Office tool changes are part of 2C2.

The old loop completion/parse/repair/fallback/trace methods and
`AgentJsonProtocol.CreateFormatRepairMessage` are removed, without aliases or dual
execution. Tool orchestration, completion guard and native refusal behavior stay
unchanged. Media may now be sent again on a repair; memory/traffic cost is tracked
as R24. Provider retries may repeat billable generation or extend latency after a
lost response (R25); they cannot replay Office tool execution. Real provider,
Windows/Office/controller/WebView qualification remains open.

Evidence and exact commands: [Phase 2A](../stabilization/PHASE_2A_MODEL_PROTOCOL.md),
[Phase 2B](../stabilization/PHASE_2B_RETRY_POLICY.md),
[Phase 2C1](../stabilization/PHASE_2C1_V3_CONTRACT.md),
[Phase 2C2](../stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md).
