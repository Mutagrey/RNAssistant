# Conversation JSON flow

RNAssistant has three explicit modes and one `Core/Agent/AgentKernel` loop, invoked by `ConversationRunService`.

- `chat`: the editable `ChatSystemPrompt`, a dynamic `RUNTIME_CONTEXT`, and exactly the safe read-only semantic `common.resources_find/read` catalog. Skills, Office tools, local mutations, and confirmation are unavailable by runtime policy.
- `plan`: the editable `PlanSystemPrompt`, read-only discovery, enabled skills, exact native `common.questions_ask`, one revisioned Markdown plan through `common.plan_doc_save/restore/delete`, and an optional checklist through `common.task_list_set`. The question handler returns typed `AwaitingUser`; Plan and Task List mutations carry source-owned verified-write evidence. Message prose cannot pause a run. Office/shared mutations and confirmation are unavailable by runtime policy.
- `agent`: the same structured loop with progressive tool discovery and enabled skill metadata. The complete mode/session-filtered catalog remains local execution authority; the model receives only the current callable schema working set. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate stage.

All modes return conversation-response v5: `message` (string), `final` (boolean)
and `tool_calls` (array); calls contain `name` and `arguments`, never a
model-owned ID. The shared ModelProtocol boundary owns strict parsing/schema,
bounded repair and provider compatibility; the kernel receives one validated
draft, separate provider-native refusal, or typed failure. Model wording and
`final` are never execution evidence.

Agent readiness precedes any domain read or mutation. The model first maps explicit deliverables,
required source/current-artifact inspection, dependency order, applicable catalog skills
and tool schemas, and completion evidence. Three or more meaningful stages, or a real
discovery → construction → verification workflow, require a Task List before the first
domain operation. Source inspection precedes the primary deliverable; binding/testing
precedes any requested reusable Skill/Tool documentation. A terminal response must
reconcile every deliverable and Task List step with result evidence. Validation/tool
errors cannot be converted into success prose or justify silently replacing a richer
artifact with a simplified placeholder.

R29 switched client, prompts, schema, probes and accepted history together from v3
to v4 and removed the model-ID parser/context path; only the kernel creates
accepted IDs. R72 switches the active response intent contract from v4 to v5 by
adding required `final`. Full-history/context preflight rejects incompatible
chats before preparation or confirmation; no historical migration or dual-write is
performed. See the [canonical v5 contract](protocols/CONVERSATION_RESPONSE_V5.md).

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
the active strict response parser remains authoritative. See
[prompt review](protocols/CONVERSATION_RESPONSE_V5.md#history-and-prompts).

Agent bootstrap schemas are `common.resources_find/read` and `common.capabilities_search/read`. The final R61 Excel Agent core adds the exact 15 built-in `excel.*` schemas plus routine VBA editing intents `common.vba_write_module` and `common.vba_apply_patch` (21 schemas total). Word and PowerPoint add the same two VBA editing schemas when present; their host tools remain optional. `common.vba_rename_module`, `common.vba_restore_backup`, `common.vba_delete_module` and `common.office_run_macro` require exact capability admission because they represent explicit identity, rollback, destructive or arbitrary-execution intent. Outlook Agent and other hosts keep only bootstrap unless an optional schema is admitted. Chat keeps only the two read-only resource schemas, while Plan keeps the four bootstrap schemas in core. These finite exact-ID profiles are intersected with the filtered run catalog. `RUNTIME_CONTEXT.capabilities.items` exposes the complete compact schema-free index of exact public runnable tool and enabled skill ids; it carries no catalog/package/descriptor revision. Already callable tools use `schemaLoaded:true`; unloaded tools and skills retain bounded selection metadata.

`common.capabilities_search` is an optional fixed-top-20 metadata filter accepting only `query` and optional `kind`. `common.capabilities_read` accepts one exact public id; a skill reference additionally accepts exact `referencePath` and semantic `action=read|next`. Offsets, sizes, catalog/package/descriptor revisions and admission guards are runtime-owned. Both ids execute through exact native ToolRuntime handlers with Agent/Plan `Read + None` policies; Chat cannot admit them. Durable results retain exact revisions for validation and continuation, while `ModelToolResultProjection` removes them from model results and invalidates stale evidence explicitly. A tool read returns the complete native-like descriptor and may stage an atomic optional extension; a skill read returns complete Markdown without changing callable membership. Runtime admits messages, request options, repair overhead and continuation reserve before publication. Failure publishes none and reports `TOOL_PACK_STATE.admitted=false`; success reports admitted public ids only. An optional tool cannot be called in the same response as its read.

No callable schema is touched by execution or removed by LRU. Before publication, runtime appends `tool_pack.extension.accepted` or `tool_pack.extension.rejected` to the canonical chat stream; an append failure leaves the live pack unchanged and stops the next model request. Each accepted event carries the exact requested ID/revision delta and before/after snapshot revisions. The ordered accepted chain for the same logical `TurnId` is the only reconstruction authority across confirmation continuation, compaction, and crash/replay, including when the runtime `RunId` changes. A rejected event and raw `capabilities_read` result prove no callable authority. Every delta is rematerialized atomically against the current filtered catalog; descriptor/profile drift or a broken chain leaves only finite core and emits `TOOL_PACK_RESTORE_STATE` until a later accepted event explicitly rebases from the current core revision. The finite global catalog remains local execution authority, and registry changes become visible on the next run. Another run's raw evidence cannot stage an extension. Tool and skill ids share one namespace, and a collision aborts request construction instead of choosing one implicitly. Catalog sources are rebuilt at every user-run and confirmation-continuation boundary (including fresh document-local VBA discovery). Phase 8A captures their complete execution authority in one immutable run `ToolPackSnapshot`: descriptor/schema, typed policy, binding/scope/host and package fingerprint cannot be replaced under the same id in an accepted call or confirmation. Native handlers consume the captured registration; the remaining legacy adapter rechecks it before dispatch. This execution snapshot is outside `AgentKernel` and is distinct from model-visible callable membership.

When JSON names an exact runnable-catalog tool whose schema is not in the current callable set, the parser reports `Tool schema is not loaded` and the format-repair instruction requires a separate `common.capabilities_read` call for that exact id. It reports `Unknown tool` only for an id absent from the runnable catalog. This distinction prevents a known unloaded tool from entering a repeated unknown-id repair loop without silently auto-loading or retrying it.

The call's `arguments` value is itself the root object validated against the selected
tool schema. A nested `arguments`, `parameters`, schema or other wrapper is invalid.
Format repair explicitly maps `$ contains unsupported property arguments` to a
removed wrapper, moving declared fields up first only when necessary, and forbids
repeating the rejected object unchanged.

A descriptor over 24,000 compact JSON characters is omitted from the runnable catalog rather than being partially advertised. Successful resource/capability evidence is never replaced by a successful transport preview: the complete resource representation or capability body/chunk must fit together with request options and both reserves. Otherwise the projection returns explicit `resource_evidence_context_too_large` or `capability_evidence_context_too_large`; a later media/materialization failure likewise changes an otherwise successful read projection to `status:error`. Budget exhaustion is `PromptBudgetExceeded`, not infrastructure failure. Incomplete schema evidence cannot enter an extension. Prompt schema 23 introduced readiness-before-domain-work, dependency-ordered Task List/skill/tool loading, root tool arguments and evidence-reconciled completion. Schema 24 made that contract an explicit Understand → Prepare → Inspect → Execute → Verify → Finish workflow and assigned non-overlapping authority: system prompt owns universal lifecycle, skill bodies own domain workflow/quality, and current tool descriptions/schemas own exact calls, arguments and evidence. Schema 25 strengthens the finish gate: an open active Task List is unfinished work unless the final message explicitly reports why it could not be closed, and documents HTML table binding row-label aliases plus visible render evidence before claiming refreshed data reached the page. Schema 26 adds the explicit v5 `final` response intent: only `final=true` with empty `tool_calls` finishes the model loop, while `final=false` with empty calls is a bounded checkpoint. Current schema 27 requires final read-back of the user-visible result, an explicit likely-bug/regression check, and a quality decision that the result is fit to hand off before a successful final. Schema 26 and any other older marker preserve stored text and require explicit review/reset before Agent/Plan execution.

Planning and execution tracking are separate. Exact native `common.plan_doc_save` accepts only the complete title/Markdown/status intent; runtime creates the active plan when absent or binds the exact active head and appends a guarded linear revision. `common.plan_doc_restore` accepts one user-visible version, while runtime resolves its exact source and current guard. `common.plan_doc_delete` has no arguments and retains the explicit-request guard plus removal tombstone semantics. `RUNTIME_CONTEXT.active_plan` exposes only current readable metadata, while the body is found and read through the semantic resource pair. `common.questions_ask` accepts prompt/options without question or option ids; runtime generates UI-only ids, and submitted answers return question text plus selected labels/free text. `common.task_list_set` has small typed `save` and `close` branches; runtime owns active-list and stable step ids while the model supplies the complete goal/ordered step state or terminal outcome. Model Tool Results omit all these internal identities and guards. A ready-plan handoff revalidates the exact selected revision internally, switches to Agent, and submits a semantic instruction to find/read the active plan; no URI enters the model request.

The resource index is a bounded semantic working-set manifest, not a body store.
`common.resources_find` accepts optional literal `query` plus semantic `scope` and
returns at most 20 readable targets. Query results are filtered matches, not a
complete inventory. An unfiltered VBA browse pins the `VBA project` target first;
reading its `structure` returns the complete discovered component inventory.
When the exact bound document supports VBA, `RUNTIME_CONTEXT.document.vba_project_target`
publishes that same readable project target so a project-wide request can read it
without a preceding find. `common.resources_read` accepts this runtime target or
one returned target plus an optional
`metadata|text|structure|source|media` representation. Runtime supplies the exact
reference, reads all internal provider pages under revision guards and publishes
one complete representation. Internal provider list/resolve/search/read still use
revision-pinned `ResourceRef`, scope-bound cursors and live hash/collection guards.
Drift, provider truncation or insufficient request context returns an explicit
error; no partial successful prefix or model continuation action exists. Model
arguments, results, `RUNTIME_CONTEXT`, media projection, compaction input and replay
contain no URI, revision/hash, cursor/offset, provider identity or internal id.

Paste, drop, and paperclip use one chat-scoped staging action. `sendChat` accepts only the resulting `resourceDraftIds`; before any model request, runtime promotes their bytes into CAS, creates immutable artifact revisions, links them to the user message, and persists that state. Existing resources are represented to the model only by bounded semantic targets and read through `common.resources_find/read`.

A resource draft is not durable history or model context. After the mandatory
pre-dispatch save, application must queue the committed message and artifact heads
under the new `sessionRevision` before the first model transport call. UI applies
this through the existing per-chat monotonic revision guard, while model execution
does not wait for a WebView acknowledgement. Local pending messages, progress text
and generated titles are not commit evidence. Delivery failure is recovered by chat
reload and cannot undo the durable turn. Format-specific viewing,
immutable/versioned classification and removal rules are defined in
[Artifact Library and Viewers](artifact-library.md).

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. Chat tools never require confirmation. An explicit user cancellation is terminal for that run and does not invoke the model again. Fresh and confirmed controller invocations share one progress/checkpoint callback, success/failure finalizer and run-lease release path; targeted store recovery releases that ownership before canonical reload. The lease remains per chat. Global coordination and document-access gates are not held across model wait, and `ConversationRunService → AgentKernel` remains the single execution loop.

The skill entries in the unified capability catalog are metadata only. When the user names a skill or a summary clearly matches, the model calls `common.capabilities_read` with that exact public id. Its model result contains `kind:"skill"`, id, readable metadata/version, complete `bodyMarkdown`, and explicit loaded/complete flags, but no package revision. Runtime validates the hidden exact revision before every projection and replaces stale evidence with `capability_evidence_stale`. Each tool named by the skill still needs its own schema read unless already callable. Oversized evidence becomes explicit `capability_evidence_context_too_large`; compaction or stale evidence requires another read.

A custom skill package may contain up to 64 direct UTF-8 `references/*.md` files. The core read lists paths and byte sizes without bodies or model-visible revisions. A needed reference is read with exact public skill id and `referencePath`; fixed runtime chunks continue with `action=next`. Exact offsets and reference/package revisions remain in durable results. A reference chunk never loads the core skill. Core/reference mutations remain separate confirmed calls.

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

Every string in the raw conversation object, including nested tool arguments, uses one JSON escaping layer. A real line break is `\n`; one literal source backslash is `\\`, so source `\n` or regex `\d` is represented as `\\n` or `\\d`. The local parser decodes the envelope once, then argument, runtime, storage and replay paths preserve the resulting text exactly. There is no source auto-unescape or repair because it would corrupt valid JavaScript, CSS, regular expressions and paths.

Inside ModelProtocol each decoded argument object remains one detached `JObject`
through optional-null removal and schema validation; canonical `ArgumentsJson` is
created directly from that tree. For handler dispatch, `ToolRuntime` remains the
single JSON-to-typed-arguments handoff. No intermediate dictionary normalization
or object re-materialization is part of response acceptance.

With SSE enabled, transport chunks still contain that raw JSON envelope. The live UI projection incrementally decodes only the root `message` string and never exposes `tool_calls` or other raw JSON. A new model attempt marks the previous provisional projection for replacement, but UI applies that reset only with the first new content/reasoning delta so a format repair cannot create an empty blink. Provider reasoning and one leading `<think>` block use the separate reasoning projection; its terminal update is emitted before visible message content starts or when the stream ends. `[DONE]` and EOF remain normal transport terminals; a non-empty `choices[0].finish_reason` also starts a one-second bounded drain for an optional final usage chunk, after which an OpenAI-compatible endpoint cannot hold the completed response open until the request timeout.

Strict response schemas require every object property to appear. Properties that are optional in the executable tool contract are therefore represented as nullable in the response schema. A model may return `null` for an irrelevant optional argument; ModelProtocol removes those optional nulls before schema validation; the executor later applies the declared defaults. Required arguments remain non-null unless their original tool schema explicitly allows null.

When `FallbackToJsonObject` is enabled and the endpoint explicitly rejects `json_schema`, ModelProtocol retries once with `json_object`, including during format repair, and keeps that choice for the rest of the run. The exact current prompt is reused and the saved selection is unchanged. This compatibility fallback has its own limit and is not model routing.

Tool call:

```json
{
  "message": "Читаю диапазон.",
  "final": false,
  "tool_calls": [
    {
      "name": "excel.read_range",
      "arguments": { "sheet": "Data", "address": "A1:D20" }
    }
  ]
}
```

No-tool checkpoint:

```json
{
  "message": "Составляю итог.",
  "final": false,
  "tool_calls": []
}
```

Final answer:

```json
{
  "message": "Готово.",
  "final": true,
  "tool_calls": []
}
```

The v5 parser rejects every extra root/call field in every response mode. Each of at most 32 calls contains only an exact callable `name` and object `arguments`; `id` is forbidden. Duplicate JSON/argument names and unsupported JSON extensions are rejected. Rejected attempts execute nothing. The string `message` may be empty; text, punctuation and `final` never classify effects.

After whole-response validation, `AgentKernel` converts ID-less `ToolCallDraft` records to accepted `ToolCall` records. It allocates IDs once, before accepted persistence, confirmation and dispatch; IDs remain unique across the accepted user run. An allocator exception, invalid ID or collision fails before acceptance without asking the model to regenerate content. Identical calls still represent separate accepted positions; IDs do not authorize automatic retries or deduplicate effects.

Each accepted message persists `ToolCallId` and immutable `AcceptedCallOrigin { StepId, ModelAttemptId, CallIndex }` in the same `session.commit` before tool entry. The entire batch is saved before its first call. The raw model response is never rewritten to inject IDs; `SourceModelAttemptId` identifies the actual accepted attempt after any repair. Optional protocol verdicts do not allocate IDs or replace this durable mapping. Results, native history and continuation reuse these IDs; replay does not generate them. Argument strings, including HTML, literal backslashes and date-shaped values, remain intact through the ID boundary.

Write, external, confirmation-required and unclassified calls are singleton. Independent local reads may be batched and execute sequentially. Effective safety comes from local authority, not tool-name guesses or model claims. The executor still validates policy/arguments and applies execution defaults.

Empty calls mean only that the model ended its loop. The fixed response schema tells the model to compare every requested deliverable with the turn's tool results before returning `[]`; one successful intermediate call is not completion. This is guidance rather than a semantic verifier: the generic kernel cannot infer whether an arbitrary Office task is complete from prose or invocation count. Since Phase 3B2 the kernel's `RunSummary` owns lifecycle and execution counts; Phase 9D5 projects the UI through immutable `RunViewState` plus source-owned effect evidence. The kernel ends an empty-call response as `completed`, independently of errors/unknown effects. Provider-native refusal is a separate ModelProtocol result classified as `failed / provider_refused`; retained accepted-history metadata may say `refused`, but the UI lifecycle comes from `RunViewState`. Model-authored refusal or question text remains ordinary `message` text. `common.questions_ask`, confirmation and technical failures retain typed runtime control signals; text never sets those outcomes.

Accepted history is marked protocol `4`: ID-less v4 JSON call envelopes plus mandatory runtime metadata, native history with matching runtime IDs/canonical names, or plain final text. A dedicated history reader reconstructs accepted calls from metadata; the wire reader never reads IDs. Both service entries and controller preparation check full history, not a truncated prompt window. Unmarked/v2/v3, incomplete v4 or ambiguous mappings block dispatch and require an explicit new chat/reset. Confirmation validates the complete accepted-turn seed before consuming pending state or executing the tool; old pending actions can still be cancelled. No stream is converted, truncated, relabeled or deleted automatically.

A confirmation pause persists its pending id, cumulative iteration/tool-step counters and execution fingerprint. A native preparable handler may additionally persist one bounded opaque prepared-state payload and a separate bounded confirmation preview. The state belongs to the exact accepted call/policy: accepted argument JSON is not rewritten, confirmed execution does not re-prepare live state, and missing/oversized/mismatched state fails before dispatch. After the singleton call is confirmed, its result returns to the same logical user run. A new request stays blocked until confirmation or cancellation; replaced definitions cannot execute. There is no persistent batch state.

`ModelProtocolClient` permits `MaxAgentFormatRetries` total protocol responses per logical step (default 10, normalized 1–20), **including the first response**. Limit 1 means no format repair; limit 20 accepts a valid twentieth response and stops after twenty invalid responses. Every repair starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored in accepted history. Internal repair attempts are not shown as user-facing activity, while the rejected payload and exact parser error remain available in trajectory diagnostics. Native provider refusal is a separate accepted metadata outcome, including when accompanied by JSON content; it cannot dispatch calls. A model-authored refusal sentence is ordinary `message` text and does not set runtime status. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy response-envelope normalization.

The Prompts UI and exact Agent-only native `common.prompts_read/save` handlers expose the three Agent sections plus `ChatSystemPrompt`, `PlanSystemPrompt`, `ContextCompactionPrompt`, `ChatTitlePrompt`, and `AttachmentAnalysisPrompt`. Model-facing save accepts exactly one enumerated `promptKey` plus its complete `value` and requires confirmation; role values are validated as `developer`, `system`, or `user`. Preparation binds that one accepted field to its current hash; confirmation rejects a changed pre-state before dispatch, preserves every unrelated setting, marks the storage boundary before save, then verifies the supplied value by read-back. An already matching request returns verified no-change without dispatch. Endpoint compatibility probes and JSON repair text are fixed protocol safeguards rather than agent-authored prompts.

The three exact Agent-only `common.tools_definition_read`, `common.tools_upsert`
and `common.tools_delete` authoring operations execute through native ToolRuntime
handlers. Read requires one exact semantic tool id. Upsert accepts only that id,
existence policy, complete ordered VBA components and human documentation; the VBA
manifest owns callable metadata/schema while runtime assigns conservative authority
and validates the complete effective definition before any write. Separate
model-facing `common.tools_validate`, list mode, executor, storage names and
self-granted safety/capability fields are absent. Upsert/delete preparation binds
the exact accepted arguments, operation and current stored definition hash; confirmation
rejects drift before dispatch. Storage writes are marked before the possible effect
and verified by exact effective-definition/absence read-back. A matching upsert is
verified no-change and does not dispatch. Authoring never changes the immutable
catalog already captured for the accepted run.

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
without automatic truncation, conversion or deletion. The v5 parser enforces
ID-less shape, explicit final intent and singleton rules on every attempt; the
kernel owns ID allocation. See the canonical
[preflight and remaining gates](protocols/CONVERSATION_RESPONSE_V5.md#remaining-gates).

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
  that accepted-history entry carries the exact public tool id and only the
  schema-valid semantic arguments accepted from conversation-response v5. RNAssistant
  does not advertise a second native function catalog. The result message contains
  exactly `role`, `tool_call_id` and `content`, with no message-level `name`; the
  same public id remains inside Tool Result v1 and local replay metadata. Stored
  pre-cutover resource/capability calls that no longer satisfy the current schema
  require an explicit new chat/reset before another model request.

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

For the R61-switched resource, capability, question, Plan, Task List, HTML and Prompt/Tool/Skill authoring families, Tool Result v1 keeps the
same required correlation/status fields but its model projection omits
`resources` and removes opaque identity from nested `data`; the accepted durable
result retains exact references for replay, provenance, hydration and read-back.
The runtime-generated `tool_call_id` remains the sole opaque wire exception because
it correlates the already accepted call and result. It is never present in tool
arguments or generated by the model. Exact public tool/skill ids may remain only as
stable semantic catalog identities; descriptor/package revisions and admission
guards do not enter model context after their R61 family cutover.

Accepted call and result records carry local `ToolResultProtocolVersion=1`
metadata; it is not an extra JSON root field. Full-history preflight validates
markers, roles, runtime ID/name pairing and one present result per accepted call
within its user run, including suppressed/compacted history. Old result envelopes
and old pending calls require an explicit new chat/reset before preparation or
confirmation; no conversion, repair, fallback or automatic deletion is performed.
Plain current-v5 history without tools can continue. Fork rebasing covers all three
roles without changing runtime IDs or resource revision; it rewrites the resource
URI into the new chat scope. Missing terminal results
alone do not invent a failure: in-flight calls and typed confirmation/user-input
pauses remain controlled by the kernel. Cancelling old pending work remains possible.

Native handlers pass typed results directly to materialization. Existing custom VBA
packages do the same since 11J2: their exact registration captures
`ToolPackageSource` contract v1, and arbitrary macro dispatch produces `unknown`
effect evidence even when VBA returns a normal string. Since 11K1, skill authoring
also returns a versioned native result with explicit
dispatch/change evidence after complete-package read-back. Since 11T10 no generic
definition/result adapter remains: model materialization consumes the typed runtime
record directly, while manual/bridge consumers receive strict `ToolRunResult` v1.
Neither path reads or converts old history. Pending/awaiting-user and proven
non-dispatch are runtime controls/evidence,
not inferred from prose or `data.code`. Known outcome/evidence is saved before
optional projection; projection failure cannot erase a known effect or authorize retry.

R61/11O4 splits that family into exact core `common.skills_upsert/delete` and
reference `common.skills_reference_upsert/delete` intents; mixed core/reference
arguments are not replayable. Prompt schema 21 was that authoring boundary; current
prompt schema is 27. Existing custom text and older markers are preserved until
explicit review/reset. Built-in prompt authoring requires only model call
name/arguments and assigns IDs to runtime (R31); matching `status=ok` alone does not
prove that a document changed.

`message` is bounded before it enters model context. Each accepted terminal result is parsed once into a strict detached token; externalization and semantic media selection reuse that raw representation, while sanitization and every request-budget candidate reuse one separate model projection. The immutable durable wire is built once after that projection fits. Eligible oversized generic `data` up to 2,000,000 characters is stored completely as a CAS-backed `tool_result` artifact before the next model dispatch. Durable materialization retains the exact `relation:"result"` reference; until their own family cutover, generic/producing tool results keep that current relation, while subsequent resource calls find and read the semantic target rather than accepting its URI. Resource/capability read evidence is provider-bounded and is neither rewrapped as an untrusted artifact nor silently truncated by transport; its model projection has no exact relation. A specialized chart payload is materialized once at the result boundary and follows the same current unswitched-producing-family rule. After the durable tool-result checkpoint, the controller queues that complete revisioned artifact projection before later progress or the next model step, including confirmation continuation; progress itself carries no artifact authority. Before every conversation model request, including initial dispatch, format repair and continuation after confirmation, ModelProtocol verifies the same messages + options + applicable repair + continuation calculation and stops with a visible diagnostic instead of sending an oversized request.

Chat-local plan/HTML mutations are serialized by the per-chat lease. Manual library checks and VBA-editor reads use an isolated session snapshot, so they do not advance observations visible only to the running model. Effective safety metadata allows read-only library tools to run while that chat is active; document/local-state mutations return `manual_tool_chat_busy` until the chat stops. Since 11O3 the model-facing HTML family is seven exact Agent-only verified-write intents: separate whole-file and JSON-data writes, exact patch, semantic delete, bind, refresh and freeze. Static inspection and active preview selection are internal UI/runtime operations. Bind accepts only a data name plus optional transform/header choices and consumes the latest successful eligible accepted Office read from the same Agent run; its exact public source-tool id, schema-valid arguments and complete result evidence remain runtime-owned. Refresh accepts an optional data name, while preview policy stays internal. Stored bindings revalidate the exact captured source schema and refresh under the same document gate as live providers, call only the typed bound backend, and never fall through to generic host dispatch; source failure keeps the last good JSON. Model Tool Results remove resource references, URI/revision/hash, source identity and internal selection state, while durable evidence and workspace lineage retain them. Document and shared-local-state mutations are serialized by effective safety metadata. Live `document`/`vba` provider calls use the shared gate so reads and journal reconciliation cannot cross an in-flight mutation; chat/CAS resource reads do not acquire it. Waiting for another mutation is bounded and returns retryable `tool_mutation_busy`. If an unexpected exception occurs after mutation execution may have started, the result is `tool_effect_uncertain`, is not automatically retried, and tells the model/user to inspect state first.

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
its rename does not change the active v5 wire or retry behavior.

`RunSummary` has independent lifecycle and execution health. Only `final=true`
with empty calls ends the loop (`completed`), without certifying effects.
`final=false` with empty calls is accepted as a bounded no-tool checkpoint; three
consecutive checkpoints fail as `model_loop_stalled`. Health comes only from
immutable execution records: unknown write/external effect dominates errors, then
clean.
Narrative is preserved but cannot set either axis. Typed model failures end the
invocation without fabricated tool errors; native provider refusal is locally
classified as `failed / provider_refused`.
When a completed model response follows write errors or unknown write effects, the
application appends a runtime-owned warning to the visible and durable assistant
message. It does not reinterpret model prose or change lifecycle, but an unsupported
success claim is no longer shown without the authoritative execution-health caveat.

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
handler-supplied dispatch/effect facts. Every active tool uses that path; absent
historical effect evidence remains `Unreported`, never fabricated verification.
Only the kernel aggregates records. `ChatActivity.ExecutionEvidence`
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
`VerifiedNoChange` attached to a failed write is accounted by that failed call and
does not create an additional synthetic unknown effect in `RunViewState`.

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

If an application/controller exception escapes after the kernel has created current
run evidence, both new-run and confirmation paths interrupt that exact `KernelState`
to `failed` or `cancelled` before persisting the diagnostic projection and rethrowing.
The interrupt preserves recorded tool counts and classifies an open possible effect
conservatively; flat `LastRun.Status` never substitutes for the terminal kernel
lifecycle. Therefore a failed bridge request cannot leave the durable UI projection
in `running` merely because the visible activity was already closed.

See [event durability/recovery](session-events.md),
[ADR-0001](decisions/ADR-0001-model-does-not-own-completion.md),
[ADR-0008](decisions/ADR-0008-unknown-effects-are-not-retried.md) and
[Phase 3B2 evidence and remaining gates](stabilization/PHASE_3B2_KERNEL_CUTOVER.md).

## Local invariants

- Disabled, unavailable, or `AgentCanRun=false` tools are not exposed to Agent mode.
- Chat exposes only the two exact `common.resources_find/read` tools after schema and safety validation; it never receives skills, confirmation, document mutations, or local-state mutations.
- Confirmation and mutation safety remain local executor rules.
- HTML workspace is an ordinary Agent capability, not a separate chat mode or preference flag; the model discovers and chooses its tools from the request and current metadata/schema evidence.
- Agent mode remains available for an archived or closed document. Its local discovery catalog keeps document-independent capabilities, including HTML workspace tools, while Office/VBA tools and Office-backed HTML bindings are omitted until that document is open again.
- Every Agent run pins both the stable document key and runtime COM identity. The UI/STA adapter accepts either matching identity so COM proxy changes and document-key migration do not create false switches; when neither matches, it returns non-retryable `active_document_changed` before starting the Office tool.
- Maximum iterations and maximum tool steps bound execution.
- Tool schemas use the locally enforced closed dialect documented in the README; unsupported assertion keywords and duplicate/case-colliding property names are rejected before catalog publication.
- Pipelines are disabled during stabilization. Stored definitions are skipped, injected pipeline calls fail before confirmation/execution (`pipeline_disabled`), and authoring schemas expose only VBA. No old pipeline replay, migration or compatibility path is supported. Direct tools and VBA safety remain unchanged.
- Excel/Word/PowerPoint replacement tools inspect the current target scope inside the locked mutation. Search remains optional for discovery/preview; model-facing match-count and scope-hash preconditions are not required.
- `excel.inspect` reads structure/metadata rather than cell values. It is not a mandatory write preflight; the model reuses an unchanged selector result until a later workbook change could invalidate it. Chart upsert does not require a prior chart-list call.
- Excel, Word, and PowerPoint publish one progressive host-neutral `common.office_run_macro` high-risk external-effect tool. It accepts an exact module/procedure name and up to 30 positional scalar arguments without a manifest or allowlist; runtime replaces any incoming document qualifier with the exact bound document name before `Application.Run`. It always requires confirmation unless auto-confirm is explicitly enabled. The typed backend return is preserved in Tool Result data, but any call that crossed `Application.Run` has `unknown` effect evidence because generic runtime cannot verify document, file-system or external effects. Retired host-prefixed `*.run_macro` ids have no backend alias; Outlook does not expose this unsupported runtime.
- VBA discovery, source search/read, and backup metadata use the semantic `common.resources_find/read` pair with `vba`/`backups` scopes. Model calls carry readable targets only; bounded continuation and content-hash evidence remain internal and durable. The five public `common.vba_*` mutations are distinct whole-source write, identity-preserving rename, exact patch, delete, and restore intents; `common.office_run_macro` remains the separate external-effect operation. Write no longer contains a rename branch. Patch selects one component by `moduleName`; hunks require `find`/`text` and may carry exact unchanged `contextBefore`/`contextAfter` to disambiguate repeated source inside that component. Restore accepts an exact readable backup target or the explicit latest-for-module choice. Runtime supplies the fixed replace operation, resolves raw backup identity, reads current state, binds the exact chat/document/module guard before confirmation and verifies final state by read-back. All six ids execute through exact native intent bindings; incompatible retained `op`, `backupId`, or write/rename calls require a new chat/reset. Model Tool Results omit backup/mutation ids, hashes, guards and backend identity while durable evidence remains exact. An exact patch skips already-satisfied hunks; if the ordered result equals current source, it succeeds without a write, backup, or journal entry. The first mutation needs no model-supplied hash; after a source mutation, however, internal read-back does not refresh model context and a complete resource source read is required before another same-module mutation. Rename still guards/journals both names and uses a hidden identity-preserving backend rather than write+delete. Removed built-in ids have no aliases; export-aware package hashes remain separate from live module hashes.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace a fully included replay prefix with a stored checkpoint and a bounded deterministic union of its exact resource references, but it does not split a tool exchange, delete the source transcript, partially mark an oversized message as summarized, change the agent protocol, or repeat Office tools.
- A persisted `running` or `cancelling` run without a live cross-process owner is marked interrupted and is never resumed automatically. If it stopped while a tool may have been in flight, it is marked `interrupted_unknown` and that run's protocol remains visible but is excluded from replay. Protocol through a saved tool-result boundary remains replayable.
