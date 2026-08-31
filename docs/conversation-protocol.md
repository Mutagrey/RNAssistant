# Conversation JSON flow

RNAssistant has three explicit modes and one `Core/Agent/AgentKernel` loop, invoked by `ConversationRunService`.

- `chat`: the editable `ChatSystemPrompt`, a dynamic `RUNTIME_CONTEXT`, and exactly the safe read-only `common.resources_list/resolve/search/read` catalog. Skills, Office tools, local mutations, and confirmation are unavailable by runtime policy.
- `plan`: the editable `PlanSystemPrompt`, read-only discovery, enabled skills, typed `common.questions_ask`, one revisioned Markdown plan through `common.plan_doc_*`, and optional `common.task_list_*`. Office/shared mutations and confirmation are unavailable by runtime policy.
- `agent`: the same structured loop with progressive tool discovery and enabled skill metadata. The complete mode/session-filtered catalog remains local execution authority; the model receives only the current callable schema working set. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate stage.

All modes return conversation-response v4: only `message` (string) and `tool_calls` (array); calls contain `name` and `arguments`, never a model-owned ID. The shared ModelProtocol boundary owns strict parsing/schema, bounded repair and provider compatibility; the kernel receives one validated draft, separate provider-native refusal, or typed failure. Model wording is never execution evidence.

R29 switches client, prompts, schema, probes and accepted history together from v3 to v4. The model-ID parser/context path is removed; only the kernel creates accepted IDs. Full-history/context preflight rejects incompatible chats before preparation or confirmation; no historical migration or dual-write is performed. See the [canonical v4 contract](protocols/CONVERSATION_RESPONSE_V4.md).

## Conversation context

`ConversationModelSession` is the Office owner for one start/confirmation invocation's
accepted model messages, request options/cache, callable evidence and temporary
media. It composes through `ConversationPromptComposer`, rematerializes the latest
valid durable callable snapshot after restart or automatic compaction, appends bounded
accepted tool results and emits the updated callable state after a response. The loop
supplies step IDs and complete accepted-call context; it does not own prompt, media or
ToolPack state. Confirmation uses the same kernel accounting and preserves replay/result
ordering. The whole accepted read batch is persisted before dispatch; bounded result
projection then keeps each native call/result pair adjacent in live and replayed requests.

Every request contains one editable instruction followed by one dynamic `RUNTIME_CONTEXT` JSON object. Agent composes general (`SystemPrompt`), tool-use (`AgentToolsPrompt`), and skill-use (`AgentSkillsPrompt`) Markdown; Plan uses `PlanSystemPrompt` with the same progressive capability policy; Chat uses `ChatSystemPrompt`. The instruction role is selected independently as `developer` (default), `system`, or `user`:

- current host and document identity;
- exact bootstrap and currently loaded callable tool descriptors;
- one compact `capabilities` catalog with exact `id`, explicit `kind` (`tool` or `skill`), summary, revision, and kind-specific safety/body metadata in Agent, or an empty catalog in Chat;
- chat-owned user context and artifact references.

Explicitly addressed background work (runs, context operations, diagnostics, resource staging) loads its target session without changing the user's selected chat. Only navigation actions such as select/create/activate change the active session.

The Agent sections and Plan/Chat prompts use one explicit settings schema version.
Missing, older or unknown markers require review: normalization preserves authored
text and the marker, filling only blank fields with defaults. It never merges or
silently approves an older contract. Ordinary saves can change unrelated settings
but cannot approve stored unreviewed prompts, including when a caller supplies a
fresh current marker. `SettingsService.Save` stages normalization/review on a clone.

In Library → Prompts → actions, **«Подтвердить проверку»** explicitly saves the
current form and approves the five conversation instructions after confirmation.
Existing **«Сбросить все промпты»** clears drafts; save/review then selects defaults.
The typed `saveSettings.reviewAgentPrompts` flag defaults to false, is request-local,
and is never persisted as a setting. Normal saves, diagnostics and
`common.prompts_save` do not opt in. The form preserves PlanSystemPrompt and retains
stored text if the prompt editor is unavailable.

`EnsureAgentPromptsReviewed` runs before controller turn preparation, attachment
analysis/compaction, and before confirmation consumes pending state. The neutral
loop also guards direct entry/continuation before materialization. A mismatch is
an actionable configuration error, not a model response to repair. Fixed endpoint
probes remain available. This does not validate the user's instruction semantics;
the active strict response parser remains authoritative. See [prompt review](protocols/CONVERSATION_RESPONSE_V4.md#saved-prompt-review).

Agent bootstrap schemas are `common.resources_list/resolve/search/read` and `common.capabilities_search/read`. Agent on Excel adds the exact 15 built-in `excel.*` schemas and five public `common.vba_*`/`common.office_run_macro` schemas; Word and PowerPoint add the same five VBA schemas when present. Chat keeps its four read-only resource schemas, while Plan keeps only bootstrap discovery/read schemas in core. These finite exact-ID profiles are intersected with the already filtered run catalog, so a closed document or unsupported host cannot regain a tool through core selection. `RUNTIME_CONTEXT.capabilities.items` immediately exposes the complete compact schema-free index of exact runnable tool and enabled skill ids for the run; it is never paged or silently truncated.

`common.capabilities_search` is only an optional metadata filter over that same index. `common.capabilities_read` accepts one exact catalog id. For `kind:"tool"` it returns `kind:"tool-schema"`, the descriptor revision, complete native-like descriptor, `loaded:true`, `complete:true`, `truncated:false`, and `admission:"already_callable_or_next_model_step"`; `loaded` describes complete evidence, not callable publication. Core membership is already identified by `schemaLoaded:true` in the compact catalog. Several independent optional reads from one response are staged as one extension. Runtime checks the complete next request, including history/media, response schema, output/safety allocation and bounded format-repair overhead, before publishing all requested schemas under a new snapshot revision. Failure publishes none, retains every earlier schema, and reports `TOOL_PACK_STATE.admitted=false`; success reports the new revision and the optional members. An optional tool cannot be called in the same response as its read. For `kind:"skill"` the reader returns the complete Markdown evidence described below and never changes callable membership.

No callable schema is touched by execution or removed by LRU. Before publication, runtime appends `tool_pack.extension.accepted` or `tool_pack.extension.rejected` to the canonical chat stream; an append failure leaves the live pack unchanged and stops the next model request. Each accepted event carries the exact requested ID/revision delta and before/after snapshot revisions. The ordered accepted chain for the same logical `TurnId` is the only reconstruction authority across confirmation continuation, compaction, and crash/replay, including when the runtime `RunId` changes. A rejected event and raw `capabilities_read` result prove no callable authority. Every delta is rematerialized atomically against the current filtered catalog; descriptor/profile drift or a broken chain leaves only finite core and emits `TOOL_PACK_RESTORE_STATE` until a later accepted event explicitly rebases from the current core revision. The finite global catalog remains local execution authority, and registry changes become visible on the next run. Another run's raw evidence cannot stage an extension. Tool and skill ids share one namespace, and a collision aborts request construction instead of choosing one implicitly. Catalog sources are rebuilt at every user-run and confirmation-continuation boundary (including fresh document-local VBA discovery). Phase 8A captures their complete execution authority in one immutable run `ToolPackSnapshot`: descriptor/schema, typed policy, binding/scope/host and package fingerprint cannot be replaced under the same id in an accepted call or confirmation. Native handlers consume the captured registration; the remaining legacy adapter rechecks it before dispatch. This execution snapshot is outside `AgentKernel` and is distinct from model-visible callable membership.

When JSON names an exact runnable-catalog tool whose schema is not in the current callable set, the parser reports `Tool schema is not loaded` and the format-repair instruction requires a separate `common.capabilities_read` call for that exact id. It reports `Unknown tool` only for an id absent from the runnable catalog. This distinction prevents a known unloaded tool from entering a repeated unknown-id repair loop without silently auto-loading or retrying it.

A descriptor over 24,000 compact JSON characters is omitted from the runnable catalog rather than being partially advertised. Capability results are bounded against the already materialized request options and repair reserve; truncated or otherwise incomplete schema evidence fails closed and cannot enter an extension. Prompt Inspector includes that same repair reserve in its totals and exposes it as a separate section, so diagnostics and admission use one budget boundary. Prompt schema 16 adds durable turn-scoped reconstruction guidance. Stored custom prompts with schema 15 or another marker remain unchanged and require the existing explicit review/reset before Agent/Plan execution.

Planning and execution tracking are separate. `common.plan_doc_create/update/restore/delete` stores the single broad free-form Markdown plan as immutable linear revisions. Update, restore and delete require the exact active artifact id; restore appends an exact historical body as a new head, while delete appends a tombstone without rewriting pinned message refs. Removed exact refs return `resource_removed`. The active `rna://` URI and metadata appear in `RUNTIME_CONTEXT.active_plan`, while the body is read only through `common.resources_read`. An explicit planning request in Plan mode loads the plan schema at the first opportunity and creates the active draft once enough facts exist; message prose or HTML is not a substitute for that artifact. `common.task_list_create/update/close` stores a temporary ordered execution checklist for work with at least three meaningful stages. Runtime never maps tool calls to steps or changes statuses automatically. A ready-plan handoff switches to Agent and cites the exact revision URI; internal artifact ids are not transport.

The resource index is a bounded working-set manifest, not a body store. `common.resources_list` pages metadata, `common.resources_resolve` validates one exact reference, `common.resources_search` returns bounded literal matches, and `common.resources_read` reads one exact `metadata`, `text`, `structure`, `source`, or `media` representation by canonical revision-pinned `rna://` URI. `read` accepts the exact optional `revision` returned with a reference. Immutable text/source/structure uses an offset internally because the URI is already pinned; live continuations bind that position to the observed content hash, and list continuations bind it to a collection fingerprint. A changed live value or collection returns retryable `resource_revision_changed` instead of combining pages. Model-facing read results expose only opaque `nextCursor`, never a raw continuation offset; it must be copied unchanged into the next call's `cursor`. Search `matchOffset`/`snippetOffset` values are informational and are not read arguments. Media is attached only to the immediately following model step, with the resource URI kept as provenance and no base64 in tool JSON. A capable main model reads it directly; missing Vision/Audio capability uses the isolated attachment helper. Query-specific helper output is not advertised as a reusable resource representation.

Paste, drop, and paperclip use one chat-scoped staging action. `sendChat` accepts only the resulting `resourceDraftIds`; before any model request, runtime promotes their bytes into CAS, creates immutable artifact revisions, links them to the user message, and persists that state. Existing resources are never eagerly injected through a separate selection field: their canonical URIs remain in the bounded working set and the model reads the needed representation through `common.resources_*`.

A resource draft is not durable history or model context. After the mandatory
pre-dispatch save, application must queue the committed message and artifact heads
under the new `sessionRevision` before the first model transport call. UI applies
this through the existing per-chat monotonic revision guard, while model execution
does not wait for a WebView acknowledgement. Local pending messages, progress text
and generated titles are not commit evidence. Delivery failure is recovered by chat
reload and cannot undo the durable turn. Format-specific viewing,
immutable/versioned classification and removal rules are defined in
[Artifact Library and Viewers](artifact-library.md).

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. Chat tools never require confirmation. An explicit user cancellation is terminal for that run and does not invoke the model again.

The skill entries in the unified capability catalog are metadata only: a listed name/summary does not load or replace the skill Markdown. When the user names a skill or a catalog summary clearly matches the task, the model calls `common.capabilities_read` with that exact id before skill-governed work. Its core `TOOL_RESULT.data` contains `kind:"skill"`, `id`, metadata, the human-authored `version`, package `revision`, `format:"markdown"`, the complete `bodyMarkdown`, explicit `loaded:true`, `complete:true`, `truncated:false`, and adjacent `capabilityUse` evidence stating that tool schemas named by the Markdown were not loaded by the skill read. Each such tool still requires its own exact schema read unless already callable. A revision is loaded only while that exact top-level evidence remains in active model context. If complete tool-schema or skill-core evidence does not fit the remaining context, transport changes the result to `status:error` with `data.code:capability_evidence_context_too_large`, `loaded:false`, and `truncated:true`; it never reports a successful load whose evidence was removed. Compaction or a revision mismatch requires another core read; an unchanged oversized read is not retried.

A custom skill package may contain up to 64 direct UTF-8 `references/*.md` files. Their paths, byte sizes, and content revisions are listed by the core read without bodies and are included in the package revision. The model reads only a needed reference through the same tool using exact `referencePath`; optional zero-based `offset` and `maxChars` produce bounded chunks with `nextOffset`. A reference chunk is ordinary context evidence but never loads the core skill. `common.skills_upsert` writes one reference when both `referencePath` and `referenceMarkdown` are supplied; `common.skills_delete` removes one when `referencePath` is supplied. Core and reference mutations are separate confirmed calls, and each reference mutation changes the package revision. Several clearly relevant skills may be read independently. There is no router or activation state.

Installed skills are capability-library entities rather than chat artifacts. An
uploaded Markdown/package remains untrusted resource content and cannot appear in
the capability catalog until an explicit validated install. Agent authoring changes
the catalog only at a later run boundary; the accepted step keeps its immutable
catalog. Phase 11 package history, tombstone, restore/import and Library UX are
defined in [Skill Library](skills.md) without changing the exact
`common.capabilities_read` model transport.

Tools use a native-like description:

```json
{
  "type": "function",
  "function": {
    "name": "excel.read_range",
    "description": "Read a range from a worksheet.",
    "parameters": {
      "type": "object",
      "properties": {
        "sheet": { "type": "string", "description": "Worksheet name; omit to use the active sheet." },
        "address": { "type": "string", "description": "A1 range to read; defaults to A1." }
      },
      "required": [],
      "additionalProperties": false
    }
  },
  "safety": {
    "mutates_document": false,
    "mutates_local_state": false,
    "requires_confirmation": false,
    "risk_level": 0
  }
}
```

Custom tools must have a strict object JSON Schema with explicit `properties`, `required`, and `additionalProperties:false`. Every argument requires a type and useful description; real defaults, enums, limits, and array items belong in that same schema. Safety metadata is resolved locally and cannot be overridden by a prompt or skill.

## Model response

All modes always return the same raw JSON envelope with no Markdown or surrounding prose. `AgentResponseMode` selects its transport constraint for the shared loop:

- `json_object` (default) asks the endpoint for a generic JSON object and relies on the local parser and tool argument validators;
- `json_schema` sends a strict response schema generated from the exact current callable working set. The schema fixes the root fields, loaded tool names, and each loaded argument contract; the full internal catalog is never copied into it.

With SSE enabled, transport chunks still contain that raw JSON envelope. The live UI projection incrementally decodes only the root `message` string and never exposes `tool_calls` or other raw JSON. A new model attempt marks the previous provisional projection for replacement, but UI applies that reset only with the first new content/reasoning delta so a format repair cannot create an empty blink. Provider reasoning and one leading `<think>` block use the separate reasoning projection; its terminal update is emitted before visible message content starts or when the stream ends.

Strict response schemas require every object property to appear. Properties that are optional in the executable tool contract are therefore represented as nullable in the response schema. A model may return `null` for an irrelevant optional argument; ModelProtocol removes those optional nulls before schema validation; the executor later applies the declared defaults. Required arguments remain non-null unless their original tool schema explicitly allows null.

When `FallbackToJsonObject` is enabled and the endpoint explicitly rejects `json_schema`, ModelProtocol retries once with `json_object`, including during format repair, and keeps that choice for the rest of the run. The exact current prompt is reused and the saved selection is unchanged. This compatibility fallback has its own limit and is not model routing.

Tool call:

```json
{
  "message": "Читаю диапазон.",
  "tool_calls": [
    {
      "name": "excel.read_range",
      "arguments": { "sheet": "Data", "address": "A1:D20" }
    }
  ]
}
```

Final answer:

```json
{
  "message": "Готово.",
  "tool_calls": []
}
```

The v4 parser rejects every extra root/call field in both response modes. Each of at most 32 calls contains only an exact callable `name` and object `arguments`; `id` is forbidden. Duplicate JSON/argument names and unsupported JSON extensions are rejected. Rejected attempts execute nothing. The string `message` may be empty; text and punctuation never classify lifecycle or effects.

After whole-response validation, `AgentKernel` converts ID-less `ToolCallDraft` records to accepted `ToolCall` records. It allocates IDs once, before accepted persistence, confirmation and dispatch; IDs remain unique across the accepted user run. An allocator exception, invalid ID or collision fails before acceptance without asking the model to regenerate content. Identical calls still represent separate accepted positions; IDs do not authorize automatic retries or deduplicate effects.

Each accepted message persists `ToolCallId` and immutable `AcceptedCallOrigin { StepId, ModelAttemptId, CallIndex }` in the same `session.commit` before tool entry. The entire batch is saved before its first call. The raw model response is never rewritten to inject IDs; `SourceModelAttemptId` identifies the actual accepted attempt after any repair. Optional protocol verdicts do not allocate IDs or replace this durable mapping. Results, native history and continuation reuse these IDs; replay does not generate them. Argument strings, including HTML, literal backslashes and date-shaped values, remain intact through the ID boundary.

Write, external, confirmation-required and unclassified calls are singleton. Independent local reads may be batched and execute sequentially. Effective safety comes from local authority, not tool-name guesses or model claims. The executor still validates policy/arguments and applies execution defaults.

Empty calls mean only that the model ended its loop. Since Phase 3B2 the kernel's `RunSummary` owns lifecycle and execution counts; Phase 9D5 projects the UI through immutable `RunViewState` plus source-owned effect evidence. The kernel ends an empty-call response as `completed`, independently of errors/unknown effects. Provider-native refusal is a separate ModelProtocol result classified as `failed / provider_refused`; retained accepted-history metadata may say `refused`, but the UI lifecycle comes from `RunViewState`. Model-authored refusal or question text remains ordinary `message` text. `common.questions_ask`, confirmation and technical failures retain typed runtime control signals; text never sets those outcomes.

Accepted history is marked protocol `4`: ID-less v4 JSON call envelopes plus mandatory runtime metadata, native history with matching runtime IDs/canonical names, or plain final text. A dedicated history reader reconstructs accepted calls from metadata; the wire reader never reads IDs. Both service entries and controller preparation check full history, not a truncated prompt window. Unmarked/v2/v3, incomplete v4 or ambiguous mappings block dispatch and require an explicit new chat/reset. Confirmation validates the complete accepted-turn seed before consuming pending state or executing the tool; old pending actions can still be cancelled. No stream is converted, truncated, relabeled or deleted automatically.

A confirmation pause persists its pending id, cumulative iteration/tool-step counters and execution fingerprint. After the singleton call is confirmed, its result returns to the same logical user run. A new request stays blocked until confirmation or cancellation; replaced definitions cannot execute. There is no persistent batch state.

`ModelProtocolClient` permits `MaxAgentFormatRetries` total protocol responses per logical step (default 10, normalized 1–20), **including the first response**. Limit 1 means no format repair; limit 20 accepts a valid twentieth response and stops after twenty invalid responses. Every repair starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored in accepted history. Internal repair attempts are not shown as user-facing activity, while the rejected payload and exact parser error remain available in trajectory diagnostics. Native provider refusal is a separate accepted metadata outcome, including when accompanied by JSON content; it cannot dispatch calls. A model-authored refusal sentence is ordinary `message` text and does not set runtime status. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy response-envelope normalization.

The Prompts UI and confirmed `common.prompts_save` edit the three Agent sections plus `ChatSystemPrompt`, `PlanSystemPrompt`, `ContextCompactionPrompt`, `ChatTitlePrompt`, and `AttachmentAnalysisPrompt`. Endpoint compatibility probes and JSON repair text are fixed protocol safeguards rather than agent-authored prompts.

## ModelProtocol boundary (Phase 2)

One `IMaterializedModelProtocol` instance serves a conversation run. `GetResponseAsync` receives
the accepted materialized messages, current callable schemas, runnable catalog and
request-local transport options. It returns an accepted `ConversationResponse` and only
that completion's metadata, a separate `ProviderRefusal`, or a typed `ModelProtocolFailure`. Provider failures,
cancellation, prompt-budget rejection and protocol exhaustion are distinct. The
separate bounded provider retry policy is defined below.

`ModelProtocolWire` owns active response schema options, envelope writing and local
JSON validation. `ConversationModelSession` adds reasoning/cache/trace fields to fresh
options; AgentJsonProtocol retains native-role mapping and local history metadata.
Compatibility probes reuse that same contract and transcript writer, but retain
one raw attempt per check: no format repair, provider retry or fallback may turn a
failed qualification probe into a pass. Fixed sentinel values remain independent
of saved prompts. Prompt-authoring guidance refers to current defaults rather than
copying another protocol version's field/status rules.

The loop supplies an immutable `ModelProtocolCallContext` containing the conservative
local batch-safe projection. Runtime IDs stay in kernel continuation, not the parser
context. Confirmation restores them across `RunId` changes from full accepted
history. A missing/incomplete snapshot fails with typed
`Infrastructure` before any raw request or format repair. Full-session version
checks run before send/edit/retry preparation and manual compaction; confirmation
also validates the accepted-turn seed before consuming pending state or executing
the tool. Incompatible/unmarked history requires an explicit new chat or reset,
without automatic truncation, conversion or deletion. The v4 parser enforces ID-less shape and singleton rules on every attempt; the kernel owns ID allocation. See the canonical
[preflight and remaining gates](protocols/CONVERSATION_RESPONSE_V4.md#history-and-context-preflight).

The loop owns step ids, tool execution, summaries and presentation timing.
`ConversationModelSession` appends accepted model messages; `AgentTranscript`
constructs visible activity, resource/chart provenance and HTML checkpoints. Core owns
raw attempt ids, parsing, fixed repair instructions, format fallback and the
accepted/rejected diagnostics sent through the existing configured trace sink.
Rejected diagnostic append failure stops the step; optional accepted marker
failure preserves acceptance. Transient streaming still uses the Office projector
and is not accepted history. No new store or model self-repair events are introduced.

Hydrated media stays in the unchanged accepted prompt throughout all attempts of
one logical step. It is released in `finally` after acceptance, failure or
cancellation, before any following tool execution or model step. This may repeat
media traffic during repair (R24); it does not reread resources, change revisions
or load/evict tool schemas between attempts.

`ConversationKernelAdapter.Model` maps boundary failures to typed kernel failures;
`Failure.Cause` and exception rethrow are removed. Provider metadata/context usage
remain outside the kernel. A runtime diagnostic does not duplicate the usage of
a prior accepted response. R29 changes the wire shape; retry budgets remain unchanged.
See [ADR-0002](decisions/ADR-0002-model-protocol-boundary.md).

## Retry policy (Phase 2B)

| Outcome | Action | Budget |
|---|---|---|
| Received completion fails the v4 contract | Retry from accepted prompt + one fresh repair instruction | Total 1–20 responses, including first |
| Runtime ID allocation/restore failure | Fail before acceptance/dispatch; never regenerate model payload for an ID | No model repair |
| Typed timeout, network failure or HTTP 5xx/server failure | Retry the exact current prompt after a cancellable delay | Two extra requests for the whole step, delays 1s then 2s |
| Explicit `json_schema` rejection with fallback enabled | Switch to `json_object`, including during repair | One extra request, independent of other budgets |
| Authorization/other HTTP errors, 429, size limits, invalid provider envelope | Typed provider failure | No automatic retry |
| Cancellation | Typed cancelled failure | No further dispatch or acceptance |

The provider budget does not reset between format attempts; the next logical
step gets a new budget. With protocol limit N, no more than N+3 raw completion
requests can be made (maximum 23). Provider failures do not create format-repair
messages or consume protocol response slots. This wrapper does not change the
LLM adapter's HTTP classification, configure endpoint failover or retry tools.

The `MaxAgentFormatRetries` settings/bridge key and its stored number are kept;
the number now means total responses, not additional corrections. The form label
and tooltip state that distinction. Default 10 and normalization to 1–20 remain.
No second setting key or settings migration is introduced.

Every raw attempt retains step correlation and gets a distinct modelAttemptId.
Existing rejected trace `Attempt` stays zero-based; repair instructions and the
exhaustion diagnostic use one-based total response counts. Cancellation during
backoff, a final rejection or a late completion cannot turn into acceptance.
Provider retries can repeat billable generation after a lost response (R25).
See [Phase 2B evidence](stabilization/PHASE_2B_RETRY_POLICY.md).

## Tool result

Tool Result v1 is the only active model-result envelope. Core owns the immutable
terminal value and `ModelProtocol.ToolResultWire`; Office owns budgeting, media and
resource materialization. The only statuses are `ok`, `error`, and `unknown`.
There is no root `ok`/`Success`, duplicated `error`, `retryable`, journal state or
model-facing pause status. Error details use `data.code`; other domain details
remain opaque data. `ok` certifies the invocation outcome, not a verified change.
Dispatch/effect evidence and the cumulative runtime summary remain independent.
See [ADR-0003](decisions/ADR-0003-tool-result-three-states.md#phase-4b-wire-gate).

`ToolResultRole` is independent from the instruction role and controls transport:

- `user` (default) / `developer`: result JSON follows the `TOOL_RESULT:` prefix;
- `tool`: the same raw JSON follows a matching `assistant.tool_calls` entry;
  provider-safe names and `tool_call_id` retain the persisted runtime identity.

```text
TOOL_RESULT:
{"tool_call_id":"call_1","name":"excel.read_range","status":"ok","message":"Range read.","data":{"values":[[1,2]]},"resources":[{"uri":"rna://chat/s1/artifact/a1/revision/1","revision":"1","relation":"result"}]}
```

All five root fields shown before `resources` are required. `data` may be any JSON
value, including null. The optional `resources` array contains exact `rna://`
URI/revision references; at most one has `relation:"result"` for full externalized
data. Neither a resource `kind` nor CAS hash/internal artifact ID is a second
transport. The strict reader rejects aliases, extra fields, duplicate keys,
comments, trailing content and unsupported statuses; ISO and literal strings are
not date-converted. Writer, probes and all replay roles use the same contract.

Accepted call and result records carry local `ToolResultProtocolVersion=1`
metadata; it is not an extra JSON root field. Full-history preflight validates
markers, roles, runtime ID/name pairing and one present result per accepted call
within its user run, including suppressed/compacted history. Old result envelopes
and old pending calls require an explicit new chat/reset before preparation or
confirmation; no conversion, repair, fallback or automatic deletion is performed.
Plain current-v4 history without tools can continue. Fork rebasing covers all three
roles without changing runtime IDs or resource revision; it rewrites the resource
URI into the new chat scope. Missing terminal results
alone do not invent a failure: in-flight calls and typed confirmation/user-input
pauses remain controlled by the kernel. Cancelling old pending work remains possible.

Native `resources_list` passes its typed result directly to materialization.
`LegacyToolResultAdapter` converts active domain results once using the recorded
runtime outcome; it never reads old history. `ToolResultUiProjection` serves only
existing activity/manual-command consumers and is never fed back to the model
writer. Pending/awaiting-user and proven non-dispatch are runtime controls/evidence,
not inferred from prose or `data.code`. Known outcome/evidence is saved before
optional projection; projection failure cannot erase a known effect or authorize retry.

Current prompt schema is 14. Existing custom text and older markers are preserved
until explicit review/reset. Built-in prompt authoring requires only model call
name/arguments and assigns IDs to runtime (R31); matching `status=ok` alone does not
prove that a document changed.

`message` and `data` are bounded before they enter model context. Eligible oversized generic `data` up to 2,000,000 characters is stored as a CAS-backed `tool_result` artifact before the next model dispatch; the envelope contains its exact reference and replaces inline data with `{truncated, original_chars, original_estimated_tokens, preview, hint}`. The model can page the full value through `common.resources_read` or request a smaller scope. Resource/tool/skill discovery evidence is not copied into an untrusted artifact. A specialized chart payload is materialized once at the result boundary, exposes its exact URI to the next model step, and is reused by storage/UI projection. Before every conversation model request, including format repair and continuation after confirmation, ModelProtocol verifies the estimated prompt against the current input budget and stops with a visible diagnostic instead of sending an oversized request.

Chat-local plan/HTML mutations are serialized by the per-chat lease. Manual library checks and VBA-editor reads use an isolated session snapshot, so they do not advance observations visible only to the running model. Effective safety metadata allows read-only library tools to run while that chat is active; document/local-state mutations return `manual_tool_chat_busy` until the chat stops. HTML bindings may replay only adapter tools explicitly marked `CanSourceHtmlData`; they must remain read-only, confirmation-free, enabled, and Agent-runnable. Bind and refresh revalidate the exact schema and enter the same reentrant document gate as live providers; refresh keeps the last good JSON on source failure. Document and shared-local-state mutations are serialized by effective safety metadata. Live `document`/`vba` provider calls use the shared gate so reads and journal reconciliation cannot cross an in-flight mutation; chat/CAS resource reads do not acquire it. Waiting for another mutation is bounded and returns retryable `tool_mutation_busy`. If an unexpected exception occurs after mutation execution may have started, the result is `tool_effect_uncertain`, is not automatically retried, and tells the model/user to inspect state first.

## Stabilization causal trace

Phase 1B gives each conversation iteration a logical step id before its first model
request, and each completion call a model attempt id. Repair/schema fallback retain
the step and receive a new attempt; tool commands retain that same logical step.
Accepted/rejected parser diagnostics link the transport request, attempt and step;
accepted diagnostics also carry the exact tool-call ids. They never enter replay.
Phase 1B left the v2 response, retry limits and outcome behavior unchanged. See the
[causal trace contract and validation limits](stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Kernel state model (Phase 3B2 production switch)

`Core/Agent/AgentKernel` accepts generic messages through `IModelProtocol.SendAsync`.
It does not own prompt composition, compaction, callable ToolPack/capability lifecycle, media or provider
metadata. The materialized boundary above remains the current endpoint owner;
its rename does not change the active v4 wire or retry behavior.

`RunSummary` has independent lifecycle and execution health. Empty calls end the
loop (`completed`), without certifying effects. Health comes only from immutable
execution records: unknown write/external effect dominates errors, then clean.
Narrative is preserved but cannot set either axis. Typed model failures end the
invocation without fabricated tool errors; native provider refusal is locally
classified as `failed / provider_refused`.

Start/resume share accounting, limits and accepted-turn IDs. Pending approval
consumes a tool-budget reservation, not an outcome; confirmation uses that
reservation once and rechecks captured policy/revision. Mandatory run facts are
appended before dispatch and after results; a failed append stops execution.
Synthetic result messages retain typed evidence for the external serializer.
No automatic tool replay/retry or new result-wire format is introduced.

Production start and confirmation use `ConversationKernelAdapter`. The controller
retains the per-chat lease, document guard and prompt/history preflight; it does
not execute the confirmed tool before calling the kernel. Current exact policy
and executable fingerprint are rechecked by the shared execution path. Pending
arguments preserve the accepted input; defaults are recomputed by the executor
under that fingerprint, not treated as a new model call.

The existing event stream stores `KernelState` (immutable summary/limits and an
optional in-flight boundary) through `run.updated`. Continuation is reconstructed
from that state and complete accepted turn history, including compacted records.
Missing/duplicate results, altered pending arguments or missing evidence fail
closed. A pending run without kernel evidence cannot resume: cancel it or start
a new chat. No backfill or fallback loop exists.

## Effect mapping and UI projection

`ToolRuntime` classifies each native invocation from a captured typed policy and
handler-supplied dispatch/effect facts. `LegacyToolOutcomeAdapter` remains only for
unmigrated results; their absent effect evidence is `Unreported`, never fabricated
verification. Only the kernel aggregates records. `ChatActivity.ExecutionEvidence`
preserves compact native facts through existing event operations and clone; a
present incomplete evidence/policy object fails deserialization.

`RunViewState` is the only application/bridge/UI outcome projection. It carries
the kernel lifecycle, narrative, successful reads and failed calls, while
`VerifiedWrites`/`NoChangeWrites` come only from source-owned effect evidence.
Successful legacy mutations without that evidence are `UnverifiedWrites` and add
an `UnknownEffect`; they cannot render as verified or clean. Any unknown wins over
errors; otherwise any failed call wins over clean. Pending confirmation is exposed
only while lifecycle is `awaiting_confirmation`. Rejected model attempts do not
add tool failures; protocol exhaustion still fails the lifecycle.

The legacy adapter conservatively maps mutation `partial_failure`, `unknown`,
`interrupted_unknown`, `tool_effect_uncertain` and missing results to unknown.
An exception escaping a possible mutation dispatch cannot certify its effect.
Other unsuccessful results are errors. Missing/invalid policy cannot certify a
successful read or write. Counts describe top-level tool invocations, including
local mutations and possible no-ops, not changed cells or verified document diffs.
Migrated handlers remove this mapping at their switch; 4B removes the legacy
model-result writer/readers, not the still-needed VBA/domain preparation paths.
`VerifiedNoChange` and `VerifiedChange` are independent facts, not inferred from
`WriteOk`, policy verification requirements or model wording.

A new user turn starts fresh counts. Confirmation retains the logical turn's
summary and counts its execution once; effect projection follows that stable
`TurnId` when the continuation receives a new runtime `RunId`. Kernel execution records are saved before
optional result/media/context preparation. Preparation failure can stop the run,
but cannot rewrite or repeat that invocation; effect verification remains whatever
the source evidence proves.

Visible run messages and replayed headers retain the same immutable `RunViewState`.
Full bridge responses carry the canonical session revision; static UI rejects a
late per-chat projection instead of replacing newer history/outcome. The old
`RunExecutionSummary` type, fields, getter and JS readers are removed. Unknown old
JSON fields are ignored and grant no authority; a run without current
`KernelState` requires explicit new-chat/reset. UI never computes effects from
narrative or retained `ResponseStatus`. Unknown/errors retain an independent
warning, and a clean no-write answer does not certify applied changes.

See [event durability/recovery](session-events.md),
[ADR-0001](decisions/ADR-0001-model-does-not-own-completion.md),
[ADR-0008](decisions/ADR-0008-unknown-effects-are-not-retried.md) and
[Phase 3B2 evidence and remaining gates](stabilization/PHASE_3B2_KERNEL_CUTOVER.md).

## Local invariants

- Disabled, unavailable, or `AgentCanRun=false` tools are not exposed to Agent mode.
- Chat exposes only the four exact `common.resources_*` read tools after schema and safety validation; it never receives skills, confirmation, document mutations, or local-state mutations.
- Confirmation and mutation safety remain local executor rules.
- HTML workspace is an ordinary Agent capability, not a separate chat mode or preference flag; the model discovers and chooses its tools from the request and current metadata/schema evidence.
- Agent mode remains available for an archived or closed document. Its local discovery catalog keeps document-independent capabilities, including HTML workspace tools, while Office/VBA tools and Office-backed HTML bindings are omitted until that document is open again.
- Every Agent run pins both the stable document key and runtime COM identity. The UI/STA adapter accepts either matching identity so COM proxy changes and document-key migration do not create false switches; when neither matches, it returns non-retryable `active_document_changed` before starting the Office tool.
- Maximum iterations and maximum tool steps bound execution.
- Tool schemas use the locally enforced closed dialect documented in the README; unsupported assertion keywords and duplicate/case-colliding property names are rejected before catalog publication.
- Pipelines are disabled during stabilization. Stored definitions are skipped, injected pipeline calls fail before confirmation/execution (`pipeline_disabled`), and authoring schemas expose only VBA. No old pipeline replay, migration or compatibility path is supported. Direct tools and VBA safety remain unchanged.
- Excel/Word/PowerPoint replacement tools inspect the current target scope inside the locked mutation. Search remains optional for discovery/preview; model-facing match-count and scope-hash preconditions are not required.
- Excel, Word, and PowerPoint publish one progressive host-neutral `common.office_run_macro` high-risk mutation tool. It accepts any exact `Application.Run` macro name and up to 30 positional scalar arguments without a manifest or allowlist, always requires confirmation unless auto-confirm is explicitly enabled, and returns the actual execution result to the loop. Host-prefixed `*.run_macro` ids remain hidden adapter backends; Outlook does not expose this unsupported runtime.
- VBA discovery, source search/read, and backup metadata use provider `vba` through `common.resources_list/resolve/search/read`; source reads are bounded character chunks with explicit continuation and content-hash evidence. The four public `common.vba_*` operations are mutations only: write/rename, exact patch, delete, and restore. They keep backup/strict-live-hash/stale-state checks inside the implementation. Runtime reads current state and binds a chat/document/module guard while preparing the mutation, persists it through confirmation, revalidates immediately before mutation, and verifies final state by read-back. An exact patch skips already-satisfied hunks; if the ordered result equals current source, it succeeds without a write, backup, or journal entry because no mutation occurs. No preparatory public read or model-supplied hash is required; when a resource source read/search snapshot already exists, runtime consumes it automatically to surface one actionable stale warning before rebinding on an intentional retry. Rename guards/journals old and new names and uses a hidden identity-preserving backend rather than write+delete. Removed built-in ids are rejected directly and old pipeline definitions are unsupported. Export-aware package hashes remain separate from live module hashes.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace a fully included replay prefix with a stored checkpoint and a bounded deterministic union of its exact resource references, but it does not split a tool exchange, delete the source transcript, partially mark an oversized message as summarized, change the agent protocol, or repeat Office tools.
- A persisted `running` or `cancelling` run without a live cross-process owner is marked interrupted and is never resumed automatically. If it stopped while a tool may have been in flight, it is marked `interrupted_unknown` and that run's protocol remains visible but is excluded from replay. Protocol through a saved tool-result boundary remains replayable.
