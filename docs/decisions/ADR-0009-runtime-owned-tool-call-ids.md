# ADR-0009: Runtime owns tool-call IDs

Date: 2026-08-28
Status: Accepted contract for R29; implementation/qualification evidence is recorded separately.

## Context

The v3 wire contract made the model generate an ID unique across the accepted
user run. A repeated administrative ID could reject an otherwise useful payload
and invoke full model regeneration. ID uniqueness is necessary for correlation,
but it is a runtime responsibility. Distinct IDs also do not prove that two calls
describe different actions. This design bug is [R29](../stabilization/RISK_REGISTER.md#r29--runtime-должен-владеть-идентификаторами-вызовов).

## Decision

- Switch the one active wire to [Conversation Response v4](../protocols/CONVERSATION_RESPONSE_V4.md):
  only `message` and `tool_calls`, with each call containing exactly `name` and
  `arguments`. Call-level `id` is rejected, not stripped or renamed. No v3 fallback
  or dual contract remains.
- Keep ID-free `ConversationToolCall` / kernel `ToolCallDraft` proposals separate
  from accepted `ToolCall` records. `AgentKernel` allocates every call's ID after
  validation and before accepted append, confirmation or dispatch. Existing exact
  argument checks, singleton safety and batch limits remain.
- ModelProtocol generates one raw attempt ID per dispatch and returns an immutable
  `SourceModelAttemptId` from the successful attempt. Repair cannot substitute the
  first attempt or a logical step ID as its origin. The separate accepted-ID
  uniqueness context and model-ID generation requirement are removed; local
  batch-safety context remains. Repair still receives accepted history with its
  runtime IDs in native calls/results and `TOOL_RESULT` records.
- Persist runtime `ToolCallId` and immutable
  `AcceptedToolCallOrigin { StepId, ModelAttemptId, CallIndex }` together in the
  existing `session.commit`. The entire accepted batch is saved before execution;
  each record retains its original zero-based call position. Raw response evidence
  is unchanged. No second durable index or diagnostic-only mapping is introduced.
- Runtime allocation failure/collision is an infrastructure failure before
  dispatch, not a model repair request. Required accepted-persistence failure also
  prevents dispatch. Optional protocol accepted markers have no allocated call
  IDs; an empty diagnostic ID list is not execution authority.
- All user/developer/native tool-result history uses the same persisted ID.
  Confirmation and replay restore it, including across compaction and runtime
  `RunId` changes. Full-history v4 preflight rejects incompatible or incomplete
  records before preparation/confirmation. Old history requires an explicit new
  chat or reset/skip, without automatic deletion or conversion.
- Advance prompt schema to `13` with the wire switch. Preserve saved custom text
  and require explicit review/reset; ordinary saves cannot approve an old schema.

## Consequences and limits

Valid message/name/argument payloads no longer need regeneration to satisfy call
identity. IDs correlate accepted calls, pending confirmation, execution, results
and replay. They do not deduplicate actions, authorize tool retry or establish
effect success. [ADR-0008](ADR-0008-unknown-effects-are-not-retried.md) remains in
force; the [ModelProtocol retry budgets](ADR-0002-model-protocol-boundary.md#retry-policy-phase-2b)
are unchanged for actual format/provider failures.

Assigning an ID only to a completed result is too late for pending confirmation
and dispatch. Silently stripping/renaming v3 IDs would hide the contract change.
Using payload hashes as IDs would conflate identity with semantic deduplication.
Raw or optional trace rewriting cannot replace accepted persistence.

This is a bounded Phase 2 protocol correction with coordinated Phase 3 consumers,
not the start of Phase 4 or a persistence/UI redesign. Requirements and actual
checks belong to [the canonical v4 contract](../protocols/CONVERSATION_RESPONSE_V4.md#remaining-cutover-gates)
and [R29 evidence](../stabilization/R29_RUNTIME_CALL_IDS.md). This ADR marks no tests
passed. Windows x64 + Office + VS 2022, controller/WebView/DPAPI and real-provider
qualification remain explicit gates; correct envelope transport does not certify
the syntax or behavior of HTML/VBA payloads.
