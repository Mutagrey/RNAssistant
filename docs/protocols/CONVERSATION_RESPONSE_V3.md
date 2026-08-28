# Conversation Response v3

Status: **introduced, not active in the conversation runtime** (Phase 2C1).
Canonical requirements: [master plan §7.1](../stabilization/STABILIZATION_MASTER_PLAN.md#71-conversation-response-v3).
The active wire/history version remains v2 until the coordinated Phase 2C2
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
  case-insensitive. A different run has a fresh ID scope.
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

`ConversationResponseParser.Parse` requires five explicit inputs:

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
that updated set. A read adapter is not such an acceptance operation.

Batching is opt-in. A missing ID in the batch-safe set forces singleton even if
all legacy tool flags are false. `MutatesDocument`, `MutatesLocalState` or
`RequiresConfirmation` also force singleton despite a supplied batch-safe ID.
Core does not infer safety from tool-name suffixes or duplicate Office pipeline
analysis. Building this effective safety projection and seeding run IDs are
**cutover gates**, not runtime behavior delivered by 2C1. See R26.

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

## Explicit v2 read adapter

`ConversationResponseV2Adapter.Read` reads an **explicitly identified historical
v2 JSON envelope**, requiring a known string `status` as a version discriminator.
It then discards status; there is no mapping from model status to runtime truth:

- v2 `completed` with empty calls means only the model ended its loop;
- continuation is determined only by a nonempty call list, even if status disagrees;
- `blocked`, `refused`, `planned` or `awaiting_user` strings do not produce runtime outcomes.

The adapter checks envelope structure/bounds but deliberately does not require
today's callable catalog or revalidate historical arguments against today's
schema. It preserves exact old names without aliases and grants no permission to
execute them. Reusing converted content as a new response requires normal v3
validation. New v3 parsing never falls back to this adapter.

Owner: **ModelProtocol**. Current consumers: the focused harness only. Intended
consumer: accepted-history projection during Phase 2C2. Removal: **Phase 10** after
legacy consumers are removed under an explicit history compatibility decision.
The adapter does not migrate event streams, rewrite files, handle native-tool
replay records/plain final messages, or introduce a second durable store.

## Cutover gates — not completed in 2C1

Per [change budget §14.3](../stabilization/STABILIZATION_MASTER_PLAN.md#143-change-budget),
introduce/read-adapt is separate from the coordinated switch/delete:

1. Switch ModelProtocol result/parser/repair, mode instructions and compatibility
   probes together. Resolve saved custom v2 prompts explicitly; never silently
   accept a v2 response on a v3 request. Preserve provider-native refusal metadata
   separately from model-authored status.
2. Supply the accepted-run ID scope, including confirmation continuation and
   compaction, and trusted effective batch safety. No new tool policies, automatic
   tool retries, planner or hidden runtime phase selection.
3. Adapt all accepted-history forms (JSON envelope, native tool role and plain
   final text) explicitly. Do not read only the currently visible prompt to seed IDs.
4. Switch request schema, canonical accepted writes and protocol version marker
   together. **After cutover all new accepted events are v3; no dual-write.**
   Historical event sources remain untouched; compatibility is a read projection.
5. Remove superseded live v2 parser/schema/DTO consumers at the switch; keep only
   the mapped temporary read adapter. Run integration tests for repair, history,
   confirmation, streaming, tools and completion guard. Phase 3 remains separate.

Current implementation/evidence: [Phase 2C1](../stabilization/PHASE_2C1_V3_CONTRACT.md).
Decision: [ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md).
