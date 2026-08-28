# Conversation Response v3

Status: **contract/context prepared; shared wire owner and explicit saved-prompt review; live protocol still v2** (Phase 2C3B).
Canonical requirements: [master plan §7.1](../stabilization/STABILIZATION_MASTER_PLAN.md#71-conversation-response-v3).
The active wire/history version remains v2 until the coordinated Phase 2C3C
cutover. Product version remains `16.1.0-dev`; protocol version is independent.

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
- Write, external and confirmation-required calls must be singleton. Multiple
  independent read-only calls may be returned in order and executed sequentially.
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
The legacy `AgentResponse` DTO remains in the active v2 path until switching.

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
Core does not infer safety from tool-name suffixes or duplicate Office pipeline
analysis. Phase 2C2 supplies this context to the boundary, but the live v2 client
does not enforce it. V3 enforcement remains a cutover gate (R26).

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
`json_schema` must use the same local parser after the cutover. The existing
bounded retry/fallback policy is unchanged by this introduction.

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

The still-active v2 transcript has a temporary typed-metadata consumer in
`ConversationProtocolContext.ReadCurrentV2CallIds`. It reads only current call IDs,
not v2 JSON/status, and is removed with the v3 writer switch. It is not old-chat
compatibility. The unused `ConversationResponseV2Adapter`, its legacy structural
branch, project include and obsolete tests were **removed in 2C2**. Incompatible
old chats require explicit skip/reset, without deletion, migration or silent
history truncation; that guard is still a cutover prerequisite.

Until ToolPolicy has external-effect metadata, the Office projection permits
only audited built-in local reads: `common.resources_list/resolve/search/read`,
`common.capabilities_search/read`, `excel.inspect/read_range/find_cells`. Enabled,
built-in binding and effective `ToolSafetyPolicy` must also permit Agent execution
without document/local mutation or confirmation. All other IDs, including pure
pipelines and unclassified/external calls, conservatively stay singleton. No tool
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
or historical fallback. It currently uses v2 and does not enforce v3 CallContext.

Probes derive fixed sentinels from the active writer and compare validated responses;
each still makes one raw attempt, without repair/fallback. Their native call history
uses the actual transcript writer. Prompt-authoring guidance reads current defaults
instead of repeating a separate version-specific envelope. Thus the next switch
can update runtime and qualification coherently without editing probe internals.

## Remaining cutover gates

Per [change budget §14.3](../stabilization/STABILIZATION_MASTER_PLAN.md#143-change-budget),
2C1 introduced the contract, 2C2 adapted context, 2C3A removed duplicate wire
ownership and 2C3B handles saved-prompt review. Phase 2C3C must coordinate
switch/delete, rechecking the change budget:

1. Switch ModelProtocol result/parser/repair, mode instructions and compatibility
   probes together through ModelProtocolWire. Resolve saved custom v2 prompts explicitly; never silently
   accept a v2 response on a v3 request. Preserve provider-native refusal metadata
   separately from model-authored status. Advance the prompt schema marker with
   that switch so saved v2 instructions require the explicit review implemented
   in 2C3B. Recheck preservation/reset with actual v3 defaults; never erase saved
   custom prompts as a hidden part of the protocol switch.
2. Require complete `CallContext` before v3 dispatch and pass it to the local
   parser on every attempt. Incomplete history is a runtime boundary failure,
   not something model repair can fix. Controller attachment analysis/compaction
   can precede the neutral loop, so guard the full request before those calls too.
   Verify run-wide duplicates and singleton
   enforcement through the live v3 client. No tool retries/planner/policy changes.
3. Handle all accepted-history forms of the current v3 run (JSON envelope, native
   tool role and plain final text) with the prepared reader, including actual
   replay/confirmation writers. Incompatible old
   chats require an explicit skip/reset boundary; never silently truncate their
   history and continue the same run. Keep the existing controller protocol-version
   confirmation guard; historical v2 projection is not required.
4. Switch request schema, canonical accepted writes and protocol version marker
   together. **After cutover all new accepted events are v3; no dual-write.**
   Historical event sources remain untouched; no automatic deletion or migration.
5. Remove superseded live v2 parser/schema/DTO consumers and the temporary typed-ID helper
   at the switch per master plan §15.1. Run integration tests for repair, history,
   confirmation, streaming, tools and completion guard. Phase 3 remains separate.

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
Host-neutral persistence/loop and JS action tests pass. Active response version 2,
prompt schema 11 and product `16.1.0-dev` do not change in 2C3B.

Current evidence: [Phase 2C3B](../stabilization/PHASE_2C3B_PROMPT_REVIEW.md).
Wire ownership: [Phase 2C3A](../stabilization/PHASE_2C3A_WIRE_OWNER.md).
Context adaptation: [Phase 2C2](../stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md).
Historical contract introduction: [Phase 2C1](../stabilization/PHASE_2C1_V3_CONTRACT.md).
Decision: [ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md).
