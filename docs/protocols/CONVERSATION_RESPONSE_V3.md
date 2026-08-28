# Conversation Response v3

> **Superseded by [Conversation Response v4](CONVERSATION_RESPONSE_V4.md).** This
> document is retained only as the historical v3 specification. It defines no
> runtime compatibility parser, fallback or migration path; v3 history requires
> the explicit new-chat/reset boundary described in v4.

Historical status: Phase 2C3C wire/history v3 and prompt schema 12, before the R29
correction. The sections and evidence below describe that implementation at its
recorded phase; they are not current v4 validation claims. Product and protocol
versions remain independent.

**Superseded R29 design:** v3 required the model to generate unique call IDs.
[ADR-0009](../decisions/ADR-0009-runtime-owned-tool-call-ids.md) replaces that
responsibility with runtime allocation before accepted persistence and execution,
preserving correlation through results, confirmation and replay. The switch is
explicit; silently rewriting duplicate model IDs is not an alternative.

## Envelope

```json
{
  "message": "Прочитаю диапазон.",
  "tool_calls": [
    {
      "id": "call_17",
      "name": "excel.read_range",
      "arguments": { "address": "A1:D20" }
    }
  ]
}
```

```json
{
  "message": "Обработка завершена.",
  "tool_calls": []
}
```

- Root contains exactly `message` (string) and `tool_calls` (array).
  An empty string is still a string; wording does not establish lifecycle or effects.
- No model-owned `status`, `phase`, `completed`, `retry`, `verified`, or other root
  fields. Fields and exact tool names are case-sensitive.
- Each call has only nonblank string `id`, nonblank string `name`, and object
  `arguments`. JSON properties cannot repeat; argument names differing only by
  case are rejected before the existing case-insensitive argument normalization.
- At most 32 calls per response, retaining the existing bound. Call IDs cannot
  repeat within the response or the accepted run; comparison remains conservatively
  case-insensitive. A new user turn starts a fresh scope; confirmation continues
  the same logical scope even when its runtime RunId changes.
- Write, external, confirmation-required and unclassified calls must be singleton. Multiple
  independent local read-only calls may be returned in order and executed sequentially.
  Parsing does not execute or schedule calls and does not prove independence.
- Empty calls mean only that the model ended its loop. Neither that fact nor
  `message` proves a successful write, a verified result, or a clean execution.

The reader accepts one JSON object, without fences, prose, comments, single-quoted
strings, unquoted properties, trailing commas, non-JSON literals or non-finite
numbers. Duplicate properties and nesting beyond 64 levels fail parsing.
Date-shaped strings remain strings. Unsupported top-level argument numbers that
cannot enter the existing `Int64` argument representation return a parse failure.
No repair of the model's content is performed by the parser.

## Core boundary

`Core/ModelProtocol/ConversationResponse` contains a message and the ordered
existing `AgentToolCall` records, with no Status member. `ToJson()` is the explicit
canonical v3 envelope writer; do not serialize a runtime DTO as the wire contract.
The legacy `AgentResponse` DTO and live v2 parser/schema are removed.
`ModelProtocolResult` carries either a `ConversationResponse`, separate native
`ProviderRefusal` metadata with its completion, or a typed failure. A provider
refusal takes precedence even when the same completion also contains JSON. It
does not become a model-authored status or a synthetic response envelope.

`ConversationResponseParser.Parse` requires these explicit inputs; its context
overload takes the last two as a complete `ModelProtocolCallContext` snapshot:

| Input | Authority / use |
|---|---|
| Content | One untrusted v3 response; never auto-detected as v2 |
| Callable tools | Exact loaded schemas for this request |
| Runnable catalog | Distinguishes known-but-unloaded tools from unknown IDs; does not make them callable |
| Accepted tool call IDs | All accepted IDs in the logical run, including confirmation continuations, not just the compacted prompt |
| Batch-safe read-only tool IDs | Trusted local execution authority; excludes external, mutation, confirmation and unresolved effects, including nested effects |

Null authority/ID inputs fail closed; empty sets are valid. Parsing copies the
sets, never reserves IDs and never mutates the caller's catalog or history.
A rejected response returns no partial calls. Only the caller's acceptance of
the entire response may advance accepted-run IDs. The next parse must receive
that updated set. Reading accepted history is not a new acceptance operation.

Batching is opt-in. A missing ID in the batch-safe set forces singleton even if
all legacy tool flags are false. `MutatesDocument`, `MutatesLocalState` or
`RequiresConfirmation` also force singleton despite a supplied batch-safe ID.
Core does not infer safety from tool-name suffixes or duplicate Office execution
analysis. Phase 2C2 introduced this context; Phase 2C3C requires completeness before
raw dispatch and checks run-wide IDs and singleton safety on every response.

For callable tools the parser reuses `ToolSchemaSupport` to validate original
argument contracts before acceptance. Optional structured-output nulls are
removed; execution defaults are not applied by ModelProtocol. Required values,
types, unknown arguments and declared constraints remain checked. The executor
will still validate its own arguments/policies and apply execution defaults.

## Structured-output schema

`ConversationResponseSchemaBuilder` emits `rnassistant_conversation_response_v3`
from only valid, unique callable tool definitions, using the existing strict
schema conversion for nullable optional arguments. Root/call properties are
closed; no callable tools means `tool_calls.maxItems = 0`.

The schema expresses shape, names, argument contracts and the 32-call bound.
Run ID uniqueness and effective singleton safety are checked locally, not encoded
as provider-specific cross-field schema constructs. Both `json_object` and
`json_schema` use the same local parser. The existing bounded retry/fallback
policy is unchanged by the cutover.

## Accepted context and current v3 history (Phase 2C2)

`ConversationProtocolContext` owns transient ID bookkeeping in the current Office
loop. It snapshots the entire accepted response before any call can pause/fail;
rejected raw attempts never enter the set. Every request gets detached read-only
lists. A fresh user run starts empty; confirmation reconstructs IDs from full
`session.Messages` after the latest real user boundary, including compacted-away
and suppressed pending call records. `LastRun.TurnId` checks that boundary when
present; confirmation's new `RunId` must not reset the logical turn's ID scope.
Missing/ambiguous records produce an incomplete context, never a valid empty set.

`ConversationResponseHistoryReader` reads **only explicitly marked v3** assistant
records: canonical JSON envelopes, one native call with canonical `ToolName` and
matching call metadata, or plain final text. It does not reverse provider-safe
names, interpret final text as JSON, grant tool authority, or rewrite history.
Ambiguous native batches/metadata and unknown versions fail; full canonical JSON
batches retain all IDs. Diagnostics and tool-result records are not responses.

The temporary `ConversationProtocolContext.ReadCurrentV2CallIds` consumer is
**removed in 2C3C** with the writer switch. The unused `ConversationResponseV2Adapter`,
its legacy structural branch, project include and obsolete tests were already
removed in 2C2. Incompatible old chats require explicit skip/reset, without deletion,
migration or silent history truncation. Integration tests use actual v3 writers,
including confirmation in all three tool-result roles, without fixture conversion.

Until ToolPolicy has external-effect metadata, the Office projection permits
only audited built-in local reads: `common.resources_list/resolve/search/read`,
`common.capabilities_search/read`, `excel.inspect/read_range/find_cells`. Enabled,
built-in binding and effective `ToolSafetyPolicy` must also permit Agent execution
without document/local mutation or confirmation. All other IDs, including
unclassified/external calls, conservatively stay singleton. Pipelines are disabled
and excluded from the callable catalog. No tool
definition/executor policy changes here. Rebuild the projection for each new run
or confirmation; do not infer safety from false legacy flags alone.

Owners/consumers/removal gates: [migration map](../stabilization/MIGRATION_MAP.md).
ID bookkeeping moves to AgentKernel in Phase 3; the positive safety registry is
replaced by typed ToolPolicy in Phase 4, after equivalent nested/external tests.

## Active wire owner (Phase 2C3A)

`Core/ModelProtocol/ModelProtocolWire` owns active schema selection, JSON validation
and envelope writing. ModelProtocolClient, ConversationRunService, AgentJsonProtocol
and ModelCompatibilityService use it; duplicate Office schema/writer/parser paths
are removed. It is a permanent contract owner, not another loop, version selector
or historical fallback. Since 2C3C it selects only the v3 schema, parser and writer;
there is no v2 fallback or dual-write.

Probes derive fixed sentinels from the active writer and compare validated responses;
each still makes one raw attempt, without repair/fallback. Native refusal fails a
probe even if its content matches the sentinel. Their native call history uses
the actual transcript writer. Prompt-authoring guidance reads current defaults
instead of repeating a separate version-specific envelope.

## History and context preflight (Phase 2C3C)

`ConversationProtocolContext.EnsureCurrentHistory` checks the entire session
projection against the active `AgentResponseProtocol.CurrentVersion`, including
suppressed and compacted-away assistant records. Null history/records, a nonzero
incompatible LastRun marker, or an unmarked/different-version assistant response
block use of that chat. Every current assistant record also passes the v3 history
reader, so a current marker cannot bless malformed content/metadata. Diagnostic
activities and tool-result messages are not
assistant responses. A LastRun without a response marker is allowed for a fresh
turn when the history itself is compatible (for example, after an interrupted
request that accepted no response).

`EnsureCanContinue` additionally requires a current LastRun marker, a command and
complete accepted IDs for its logical user turn. It runs before the controller
consumes pending state or executes the confirmed tool. The full seed is checked
again with the current safety catalog before neutral-loop materialization.

Send/edit/retry entry calls preflight before `prepareTurn`, attachment analysis or
compaction; manual **«Сжать контекст»** has the same history gate. Both neutral
service entries are guarded before history/summary mutation. Failure asks for an
explicit new chat or history reset; cancellation of a pending action remains
available. The guard does not migrate, truncate, delete or relabel any history.

`ModelProtocolClient` rejects a missing/incomplete CallContext as an `Infrastructure`
failure with the existing exception cause, before any raw attempt, progress or
attempt trace. It cannot be repaired by FORMAT_REPAIR. This is a caller precondition;
the accepted response then passes the same v3 parser on every attempt. Production
controller/Office/WebView execution is still a Windows qualification gate;
host-neutral preflight and integration tests pass.

The controller's old LastRun-only `EnsureCurrentResponseProtocol` helper is removed;
all relevant entry points use the existing context owner. The coordinated switch
adds no compatibility adapter or automatic settings/history migration.

## Remaining cutover gates

The shared wire, client/result/repair, mode defaults, canonical accepted writes,
version marker and all active consumers switched together in Phase 2C3C. The live
v2 DTO/parser/schema, their project includes and typed-ID helper are deleted.
Fifteen production files form one contract change; [§14.3](../stabilization/STABILIZATION_MASTER_PLAN.md#143-change-budget)
permits this bounded coordinated switch without another count-driven substep.
History/context preflight is part of the same change. Phase 3 is separate.

Host-neutral tests cover strict parsing, real loop/repair/streaming, run IDs,
read-only batches, singleton writes, refusal, confirmation, prompt review/reset,
versioned history/replay and the independent completion guard. Existing runtime
`AgentResponseStatuses` labels remain projection/lifecycle metadata until Phase 3;
`completed` on empty calls means only model-loop end, not proof of effect.
`RunExecutionSummary` still reports actual errors/unknown and verified write counts.

Remaining qualification:

- Windows x64 + Office + VS 2022: actual controller send/edit/retry, attachment
  preparation, manual compaction and confirmation ordering; WebView and DPAPI.
- Real provider strict-schema support, explicit fallback and native refusal.
  Fake endpoints verify local behavior, not provider conformance.

No release or Windows gate is marked passed by the host-neutral cutover. R26/R27
track those limits; the next architecture change is the separate Phase 3 boundary.

## Saved-prompt review (Phase 2C3B)

R27's automatic reset path is removed. AppSettings normalization preserves all five
authored instructions and a missing/old/future marker. SettingsService stages a
save on a clone and advances a stored unreviewed marker only for the explicit,
request-local `reviewAgentPrompts` command. Failed saves do not mutate the draft's
marker; no approval flag becomes a durable setting. Blank fields still select
defaults, but normalizing them does not itself imply review.

The existing settings bridge carries that typed flag from the Library → Prompts
**«Подтвердить проверку»** action after user confirmation. Users can retain edited
text or explicitly clear prompts with the existing reset action before review.
Ordinary form/tool/diagnostic saves do not approve prompts. Plan instructions and
stored text without a loaded editor are preserved in the form payload.

The shared AppSettings guard runs before controller preparation/auxiliary model
calls, before pending confirmation is consumed, and at neutral loop entry. It is
a configuration precondition, not format repair. Controller wiring is code-reviewed
only here; Windows x64 + Office + VS 2022 and DPAPI remain qualification gates.
Phase 2C3B verified host-neutral persistence/loop and JS actions without changing
response v2 or prompt schema 11. Phase 2C3C advances the prompt schema to 12 with
v3 defaults. Tests now cover old marker 11 as well as missing marker 0: ordinary
and failed saves preserve authored text; explicit review retains it; explicit
reset selects the actual v3 defaults. The unchanged JS review action is not a
substitute for Windows controller/DPAPI qualification.

Current evidence: [Phase 2C3C](../stabilization/PHASE_2C3C_V3_CUTOVER.md).
Prompt review: [Phase 2C3B](../stabilization/PHASE_2C3B_PROMPT_REVIEW.md).
Wire ownership: [Phase 2C3A](../stabilization/PHASE_2C3A_WIRE_OWNER.md).
Context adaptation: [Phase 2C2](../stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md).
Historical contract introduction: [Phase 2C1](../stabilization/PHASE_2C1_V3_CONTRACT.md).
Decision: [ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md).
