# Conversation Response v4

Status: **R29 coordinated wire/history contract**. Response protocol is `4`;
prompt schema is `13`. Product version is independent and unchanged by this switch.
The [v3 specification](CONVERSATION_RESPONSE_V3.md) is historical, not a runtime
compatibility path. Decision: [ADR-0009](../decisions/ADR-0009-runtime-owned-tool-call-ids.md).
Implementation and verification status belong to the
[R29 evidence](../stabilization/R29_RUNTIME_CALL_IDS.md); this specification does
not mark any test, release or Windows gate passed.

## Envelope

```json
{
  "message": "Прочитаю диапазон.",
  "tool_calls": [
    {
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

- Root contains exactly `message` (string) and `tool_calls` (array). An empty
  message is valid. Model-owned `status`, `phase`, `completed`, `retry`, `verified`
  and all other root fields are rejected.
- Each call contains exactly a nonblank string `name` and object `arguments`.
  Call-level `id` is forbidden, including null or empty values; it is never
  stripped, renamed or accepted through a v3 fallback. An argument named `id`
  remains legal when declared by that tool's argument schema.
- Names and envelope fields are case-sensitive. At most 32 calls may be returned,
  preserving array order. Empty calls end the model loop; neither this fact nor
  message wording establishes execution success or verification.
- One raw JSON object is required, without Markdown, surrounding prose, comments,
  single quotes, unquoted properties, trailing commas or non-JSON literals.
  Duplicate properties, non-finite numbers and nesting beyond 64 levels fail.
  Argument names differing only by case are rejected before normalization.
  Date-shaped strings remain strings; unsupported argument numbers fail parsing.

## Model and runtime boundaries

`ModelProtocolWire` owns the one active parser, schema and canonical wire writer.
`ConversationResponse.ToJson()` writes only the envelope above. Runtime DTO
serialization is not a substitute for this writer.

| Contract | Contents and authority |
|---|---|
| `ConversationResponse` / `ConversationToolCall` | Validated message and ordered `Name`/`Arguments` proposals; no call ID |
| `AgentResponseDraft` / `ToolCallDraft` | Kernel input with message, name and `ArgumentsJson`; still no call ID |
| `AgentResponse` / `ToolCall` | Accepted runtime calls with `Id`, `Name` and `ArgumentsJson` |
| `ModelProtocolResult.SourceModelAttemptId` | Immutable origin of the successful raw completion, not a call ID |

`ConversationResponseParser.Parse` receives content, the exact callable tools,
the runnable catalog and an explicit `ModelProtocolCallContext`. That context
contains only the local batch-safe read-only tool IDs and completeness/error
state. It does not contain accepted-run call IDs. A missing/incomplete context
fails with typed `Infrastructure` before raw dispatch, progress or attempt trace;
it is not a model format error. Empty explicit safety sets are valid.

The runnable catalog distinguishes known tools whose schema is not loaded from
unknown names; it cannot grant callability. The parser validates original tool
argument contracts and removes only permitted optional nulls used by structured
output. It does not apply execution defaults, mutate the caller's catalog or
execute calls. A rejected response returns no partial accepted calls.

For an accepted conversation envelope, `ModelProtocolResult` carries the matching
completion/usage and a required nonblank `SourceModelAttemptId`. The client
generates `TraceModelAttemptId` for every raw dispatch, even without optional
tracing, and copies the successful value before optional accepted diagnostics.
Later requests, option reuse or trace callbacks cannot alter that snapshot. After
repair it identifies the successful attempt, not the first attempt or just the
logical kernel step. Provider-native refusal remains a separate outcome, takes
precedence over accompanying JSON and cannot dispatch calls.

## Runtime identity and durable acceptance

`AgentKernel` is the sole call-ID allocator. After the entire draft passes
validation, it assigns an opaque, nonblank unique ID to every call before accepted
persistence, confirmation or dispatch. IDs must not collide within the logical
accepted user run, including its confirmation continuations. A collision or
invalid allocation is an infrastructure failure before dispatch, never a reason
to request regenerated model content. No part of a rejected batch executes.

The existing accepted-message append stores each call's runtime `ToolCallId`
together with an immutable `AcceptedToolCallOrigin` in the same `session.commit`:

| Origin field | Meaning |
|---|---|
| `StepId` | Kernel step that received the proposal |
| `ModelAttemptId` | Exact successful raw attempt from `SourceModelAttemptId` |
| `CallIndex` | Zero-based position in that attempt's ordered `tool_calls` |

The whole accepted batch is persisted before the first member can execute or
pause. Each per-call history record carries its own ID and origin; batch members
share the source step/attempt and retain distinct positions. This mapping is
accepted execution evidence, not a second mutable index or separate trace store.
Required accepted persistence failure prevents dispatch.

Raw model request/response evidence remains unchanged. Runtime IDs are not added
to the raw response, and raw arguments are not regenerated to obtain an ID.
Decoded argument payloads, including long HTML strings, retain their content
through draft acceptance and dispatch; normal JSON decoding/declared optional-null
normalization does not authorize payload truncation or rewriting.

Optional protocol `accepted` diagnostics precede kernel ID allocation and carry
an empty `ToolCallIds` list. An empty list, a missing optional marker or other
diagnostic data is not acceptance authority and cannot reconstruct IDs. Replay
uses the persisted accepted mapping and never allocates replacement IDs or repeats
effects. IDs correlate records; they do not deduplicate semantically identical
actions and do not authorize automatic tool retry.

## Safety, schema and retries

Write, external, confirmation-required and unclassified calls remain singleton.
Only independent local read-only calls may be batched and executed sequentially in
array order. Local `MutatesDocument`, `MutatesLocalState` and
`RequiresConfirmation` flags override a supplied batch-safe classification.
Unclassified effects do not become safe because legacy flags are false. The
existing conservative local-read projection remains until the separate Phase 4
ToolPolicy switch; R29 introduces no new executor policy or parallel execution.

`ConversationResponseSchemaBuilder` emits
`rnassistant_conversation_response_v4` from valid, unique current callable schemas
only. Root/call properties are closed; calls require only `name` and `arguments`.
Optional tool arguments use the existing nullable structured-output conversion;
an empty callable set gives `tool_calls.maxItems = 0`. Both `json_object` and
`json_schema` pass through the same local parser and safety checks.

The [existing bounded protocol/provider/fallback policy](../decisions/ADR-0002-model-protocol-boundary.md#retry-policy-phase-2b)
is unchanged. An invalid envelope may receive format repair from the accepted
prompt plus one current instruction; rejected attempts never enter model replay.
Repair/default prompts request v4 calls without IDs. Runtime ID failures are
outside model repair. No tool retry, payload repair phase or semantic deduplication
is added. Compatibility probes derive fixed ID-free sentinels from the same wire
writer and retain one raw attempt per probe, without retry or fallback.

## Accepted history and tool results

Accepted assistant records are explicitly marked protocol `4`. History is a
projection of accepted runtime calls, not a second model-facing response format.

| Tool-result role | Accepted call and matching result |
|---|---|
| `user` / `developer` | Assistant content is the canonical v4 envelope without IDs. Separate `ToolCallId`, canonical `ToolName` and `AcceptedCallOrigin` metadata retain the runtime mapping. The following `TOOL_RESULT` carries the same `tool_call_id`. |
| `tool` | Native `assistant.tool_calls` carries the persisted runtime ID and provider-safe name. Canonical `ToolName`, matching `ToolCallId` and origin remain local metadata; the following native tool result uses that same ID. |

`ConversationResponseHistoryReader` reads only identified current-v4 records. It
returns accepted `Core.Agent.AgentResponse` / `ToolCall` values, including
`ArgumentsJson`, without creating IDs or granting execution authority. Per-call
records require complete consistent metadata and origin. Native calls require a
matching runtime ID and canonical tool name; provider-safe names are not reversed
to infer authority. A plain final assistant message has no call metadata and is
not sniffed for embedded JSON instructions. Diagnostics and result records are
not accepted assistant responses.

## History and context preflight

`ConversationProtocolContext.EnsureCurrentHistory` checks the entire session,
including suppressed records and records before the compaction checkpoint.
Unmarked, v3/older or unknown-version assistant history, an incompatible nonzero
LastRun marker, malformed current content, missing origin or ambiguous origin
mapping blocks dispatch. A current version marker cannot bless incomplete
metadata. A LastRun without a response marker is allowed only when the existing
history is itself compatible. A source `(StepId, ModelAttemptId, CallIndex)` must
not identify multiple accepted records.

Send/edit/retry preparation, attachment/compaction preparation and manual
compaction use this gate. Both neutral service entries guard before changing
history or summary. Confirmation additionally restores the complete logical-turn
seed, validates pending ID/name/arguments and policy evidence, and rejects orphan
or duplicate results and incomplete accepted calls before consuming pending state
or executing the tool. A changed runtime `RunId` or compacted prompt does not
change the accepted IDs. All three result roles use the same restoration rules.

Incompatible history requires an explicit new chat or reset/skip action. Pending
actions may still be cancelled. No fallback, format sniffing, migration, silent
truncation, relabeling, dual-write or automatic deletion of user data is allowed.

## Saved-prompt review

Agent, Chat and Plan defaults switch together to v4 with prompt schema `13`.
Missing, old (including `12`) or future stored markers require explicit review.
Normalization preserves authored text and its marker, filling only blank fields
with defaults. Ordinary or failed saves cannot approve an unreviewed contract,
including when a caller supplies a current marker.

Library → Prompts → **«Подтвердить проверку»** uses the explicit request-local
`reviewAgentPrompts` action. Review may retain the user's edited text; the existing
**«Сбросить все промпты»** action followed by save/review selects current defaults.
SettingsService stages normalization/review on a clone; the approval flag is not
a persisted setting. Plan text and stored text without a loaded editor are
preserved. Ordinary form, tool and diagnostic saves do not opt in.

`EnsureAgentPromptsReviewed` guards before model preparation/auxiliary requests,
neutral-loop entry and pending confirmation consumption. A mismatch is a
configuration precondition, not format repair. Review acknowledges the selected
instructions; the strict parser still enforces the wire contract.

## Remaining cutover gates

Record actual commands and results in [R29 evidence](../stabilization/R29_RUNTIME_CALL_IDS.md).
Required targeted evidence includes ID-free schema/parser/probes, argument/safety
guards, distinct runtime IDs for sequential calls and batches, exact successful
attempt/position mapping after repair, persistence failure/collision before
dispatch, all result roles, confirmation/compaction/replay identity preservation,
old-history rejection, prompt review/reset and unchanged long payload delivery.
Rejected attempts must execute nothing; replay must not repeat effects.

Production controller/WebView ordering, Office COM and Windows DPAPI require
Windows x64 + Office + VS 2022 validation. Real-provider strict schema, fallback,
native refusal and streaming qualification remain separate from fake-endpoint
coverage. R29 does not close R28 or certify HTML/VBA payload correctness. This
bounded Phase 2 protocol correction and Phase 3 consumer switch do not start
Phase 4 or the later persistence/UI redesign.
