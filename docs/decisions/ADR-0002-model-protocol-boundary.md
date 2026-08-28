# ADR-0002: ModelProtocol owns raw model attempts

Date: 2026-08-28
Status: Accepted (raw-attempt boundary and retry policy; R29 switches the active wire/history contract to v4; qualification recorded separately)

Current contract: [Conversation Response v4](../protocols/CONVERSATION_RESPONSE_V4.md)
and [ADR-0009: runtime-owned call IDs](ADR-0009-runtime-owned-tool-call-ids.md).
The Phase 2C sections below retain the history of the v2→v3 transition; their
implementation/evidence claims do not qualify the later R29 switch.

## Context

`ConversationRunService` mixed tool orchestration with endpoint calls, format
repair, native refusals, compatibility fallback and model diagnostics. The
[master plan](../stabilization/STABILIZATION_MASTER_PLAN.md) assigns those model
concerns to Core and requires the loop to consume one typed outcome per step.

## Decision

- Introduce the materialized endpoint port (named `IModelProtocol` in 2A,
  `IMaterializedModelProtocol` since 3B1) / `ModelProtocolClient` in `RNAssistant.Core/ModelProtocol`.
  One instance serves one run; only the endpoint-format fallback choice survives
  between steps. Confirmation continuation creates a fresh instance, as before.
- Pass the accepted materialized prompt, current callable tools, runnable catalog
  and request-local options, including explicit local batch-safety context. Return
  either an ID-free `ConversationResponse` with completion/usage metadata and an
  immutable `SourceModelAttemptId` from its successful raw dispatch,
  separate native `ProviderRefusal` with its completion, or a typed
  `ModelProtocolFailure`. No rejected
  completion is returned. ModelProtocol neither executes tools nor changes chat history,
  resource revisions or the working set.
- Each repair copies the accepted message sequence and appends one current fixed
  instruction. Rejected bodies and earlier repair instructions are not replayed.
  Hydrated media remains available throughout that logical protocol step; the
  loop releases it in `finally` after acceptance/failure, before tool execution.
- Keep request/response persistence on the existing configured trace sink. Core
  creates distinct raw attempt IDs even without optional diagnostics; AgentKernel
  owns the logical step ID and runtime call-ID allocation. Rejected diagnostic
  failure stops execution. Optional accepted markers carry no allocated call IDs
  and cannot change an accepted outcome or supply execution authority.
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
to the active parser (now v4) or accepted native refusal, including the first response.

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

Phase 3B1 gives the kernel a generic `IModelProtocol.SendAsync` port without
settings, prompt materialization, runnable catalog or provider metadata. The
existing `GetResponseAsync` port and all its current typed callers are renamed
to `IMaterializedModelProtocol`, without an old-signature alias or a second wire
implementation. Phase 3B2 supplies the generic Office port through
`ConversationKernelAdapter.Model`, using `ConversationModelSession` and this
existing endpoint client. See [ADR-0001](ADR-0001-model-does-not-own-completion.md).

Phase 2A preserved initial + configured retries; Phase 2B removes that extra
attempt (R20). The existing `MaxAgentFormatRetries` settings/bridge key and stored
numeric values remain; the value now means total protocol responses. The caption
and tooltip explain this change. There is no second key, alias or settings rewrite.
V2 parsing/status/history were preserved by 2B; 2C3C replaces that live path below.

`ModelProtocolFailure.Cause` and the rethrow adapter were removed at the Phase 3B2
kernel switch. Typed failures now drive local kernel lifecycle. Accepted
`LlmCompletionResult` and context usage remain metadata for Office transcript
consumers, not a second protocol or durable result store.

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

[The historical v3 contract](../protocols/CONVERSATION_RESPONSE_V3.md) recorded the
remaining gates at this phase: saved prompts, complete run-ID/effective-safety context (R26),
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
effective safety; unknown/external tools remain singleton. Pipelines are disabled
and excluded from the callable catalog by the separate stabilization scope decision.
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

### Shared active wire owner (Phase 2C3A)

The remaining cutover still spans more than ten production files because probes
and transcript/request builders repeat the protocol. Per §§14.3/15.2, first give
active schema selection, JSON validation and envelope writing one permanent Core
owner, `ModelProtocolWire`. Switch ModelProtocolClient, ConversationRunService,
AgentJsonProtocol and ModelCompatibilityService to it and delete their replaced
builders now. No Office/session state crosses this boundary; the Office caller
adds reasoning/cache/trace options and retains native-role/history mapping.

The seven-production-file preparation preserves v2. Probes derive fixed sentinels
from the active writer, compare validated DTOs locally (not as a wire serialization),
and keep their single raw attempt without retries/fallback. Their native-call
history uses the actual transcript writer. Prompt-authoring guidance points to the
active defaults instead of copying v2 status rules. After the 2C3B prompt-review
prerequisite below, 2C3C can switch the shared owner and remove remaining v2 implementations without rediscovering
probe internals. No second runtime, conditional protocol mode or historical adapter.

Verification extends the two existing compatibility tests across both formats,
all three result roles, wrong sentinels/status/casing and unchanged request counts.
R27 records the existing prompt normalizer's automatic reset on version mismatch;
the existing characterization test confirms it. Settings/prompt versions are not
changed here; explicit custom-prompt handling must precede the v3 cutover.

### Explicit saved-prompt review (Phase 2C3B)

The remaining switch requires a prompt-schema bump, but the existing normalizer
silently replaced custom instructions on any mismatch. Resolve R27 first as a
bounded settings/protocol prerequisite, not as a partial v3 switch. Ten production
files change; no Office tool, resource, VBA or event-storage contract changes.

Normalization preserves authored text and the version marker. SettingsService owns
explicit review, stages changes on a clone and keeps stored mismatched markers on
ordinary saves. The existing typed saveSettings bridge accepts a request-local
reviewAgentPrompts flag; a confirmed Library action opts in. The form preserves all
five conversation prompts, including the formerly omitted PlanSystemPrompt.
The old reset branch, duplicate Chat/Plan defaulting and obsolete reset test go away.

Readiness is checked before controller turn preparation/attachment analysis/
compaction and before pending confirmation is consumed, as well as at neutral-loop
entry. It is a configuration error, never a model repair. No compatibility adapter,
automatic settings migration or protocol-version selector is introduced.
The raw model path remains v2 and the prompt schema remains 11.

Persistence tests now source-link the real SettingsService with a test-only DPAPI
boundary that throws on secret-file reads/writes. It does not simulate encryption.
Host-neutral loop/settings and JS actions are verified; production controllers,
WebView and Windows DPAPI are not. Phase 2C3C still needs the coordinated v3 switch,
full-context/old-chat guards before any model call, and its own integration tests.

### Coordinated v3 switch/delete (Phase 2C3C)

Switch `ModelProtocolWire` to the strict v3 parser/schema/canonical writer and
`ModelProtocolResult` to `ConversationResponse`. Root fields are only `message`
and `tool_calls`. Native provider refusal remains separate metadata and takes
precedence over accompanying JSON; compatibility probes reject it without retry.
An empty call list ends the model loop but does not determine effects. Existing
runtime projection labels remain until Phase 3; completion health stays local.

Advance accepted-history version to 3 and prompt schema to 12 together with all
mode defaults. Saved instructions from schema 11 are preserved until explicit
review or reset; tests exercise both paths with actual v3 defaults. No live-v2
fallback, dual-write, status coercion or automatic history/settings migration.

Use the existing context owner for full-history preflight before controller
send/edit/retry preparation, manual compaction and pending confirmation. Require
a complete detached call context before the first raw attempt; an incomplete
context is an infrastructure precondition failure, never a repair request.
Read actual v3 history for all three result roles, keep IDs for the full logical
user turn, and validate run-wide uniqueness and conservative singleton safety
on every response. Rejected batches cannot reserve IDs or execute partial calls.

Delete the live v2 parser/schema/DTO and their includes, the temporary typed-ID
reader and the old controller LastRun-only helper. The coordinated contract has
15 production-file changes; the amended §14.3 permits this bounded switch, so
preflight stays in 2C3C rather than creating another preparation step. No AgentKernel,
tool execution, Resource URI, VBA journal or persistence refactor is part of it.
Controller/Office/WebView/DPAPI and real-provider qualification remain open.

The old loop completion/parse/repair/fallback/trace methods and
`AgentJsonProtocol.CreateFormatRepairMessage` are removed, without aliases or dual
execution. Tool orchestration, completion guard and native refusal behavior stay
unchanged. Media may now be sent again on a repair; memory/traffic cost is tracked
as R24. Provider retries may repeat billable generation or extend latency after a
lost response (R25); they cannot replay Office tool execution. Real provider,
Windows/Office/controller/WebView qualification remains open.

### Runtime-owned IDs (R29)

The coordinated v4 switch replaces model-owned IDs with ID-free wire/kernel
drafts and kernel-allocated accepted calls. `ModelProtocolCallContext` now contains
only local batch-safety authority. `SourceModelAttemptId` binds acceptance to the
exact successful raw attempt after any repair; raw content is never rewritten.
Accepted history stores each runtime ID and immutable step/attempt/position origin
together in the existing `session.commit`, before confirmation or dispatch.
Runtime collisions are infrastructure faults, not format repair. Full-history v4
preflight and prompt schema 13 explicit review/reset switch with the consumers;
there is no historical compatibility path or automatic data conversion.

The detailed decision is [ADR-0009](ADR-0009-runtime-owned-tool-call-ids.md).
Current [contract and open gates](../protocols/CONVERSATION_RESPONSE_V4.md#remaining-cutover-gates)
and [R29 evidence](../stabilization/R29_RUNTIME_CALL_IDS.md) supersede the historical
v3 cutover status without changing the retry budgets above.

Historical evidence and exact commands: [Phase 2A](../stabilization/PHASE_2A_MODEL_PROTOCOL.md),
[Phase 2B](../stabilization/PHASE_2B_RETRY_POLICY.md),
[Phase 2C1](../stabilization/PHASE_2C1_V3_CONTRACT.md),
[Phase 2C2](../stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md),
[Phase 2C3A](../stabilization/PHASE_2C3A_WIRE_OWNER.md),
[Phase 2C3B](../stabilization/PHASE_2C3B_PROMPT_REVIEW.md),
[Phase 2C3C](../stabilization/PHASE_2C3C_V3_CUTOVER.md).
