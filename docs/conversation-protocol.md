# Conversation JSON flow

RNAssistant has three explicit modes and one `ConversationRunService` transport/transcript loop.

- `chat`: the editable `ChatSystemPrompt`, a dynamic `RUNTIME_CONTEXT`, and exactly the safe read-only `common.resources_list/resolve/search/read` catalog. Skills, Office tools, local mutations, and confirmation are unavailable by runtime policy.
- `plan`: the editable `PlanSystemPrompt`, read-only discovery, enabled skills, typed `common.questions_ask`, one revisioned Markdown plan through `common.plan_doc_*`, and optional `common.task_list_*`. Office/shared mutations and confirmation are unavailable by runtime policy.
- `agent`: the same structured loop with progressive tool discovery and enabled skill metadata. The complete mode/session-filtered catalog remains local execution authority; the model receives only the current callable schema working set. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate stage.

All modes return the same conversation-response v2 JSON envelope: `status + message + tool_calls[]`. They use the same bounded request-local format repair. Explicit structure and the tool catalog, never model wording, are the authority: a Chat response naming any other tool is rejected before execution.

The [status-free v3 contract](protocols/CONVERSATION_RESPONSE_V3.md) is introduced;
Phase 2C2 adapts full-turn ID/safety snapshots to the boundary and prepares a
v3-only history reader, **without switching this runtime**. The unused v2 read
adapter is removed. Active prompts, retry, schema selection and accepted history
remain v2; coordinated switch/delete and explicit old-chat skip/reset are Phase
2C3B gates. Phase 2C3A gives runtime and probes one active ModelProtocolWire owner;
no new v3 events, historical migration or dual-write exist yet.

## Conversation context

Every request contains one editable instruction followed by one dynamic `RUNTIME_CONTEXT` JSON object. Agent composes general (`SystemPrompt`), tool-use (`AgentToolsPrompt`), and skill-use (`AgentSkillsPrompt`) Markdown; Plan uses `PlanSystemPrompt` with the same progressive capability policy; Chat uses `ChatSystemPrompt`. The instruction role is selected independently as `developer` (default), `system`, or `user`:

- current host and document identity;
- exact bootstrap and currently loaded callable tool descriptors;
- one compact `capabilities` catalog with exact `id`, explicit `kind` (`tool` or `skill`), summary, revision, and kind-specific safety/body metadata in Agent, or an empty catalog in Chat;
- chat-owned user context and artifact references.

Explicitly addressed background work (runs, context operations, diagnostics, resource staging) loads its target session without changing the user's selected chat. Only navigation actions such as select/create/activate change the active session.

The Agent sections and Plan/Chat prompts use one explicit settings schema version. Settings without the current marker are hard-reset to the current defaults; RNAssistant does not merge an older no-tools Chat contract into the structured loop. Once the current marker is saved, current custom values are preserved normally.

Agent bootstrap schemas are `common.resources_list/resolve/search/read` and `common.capabilities_search/read`. `RUNTIME_CONTEXT.capabilities.items` immediately exposes the complete compact schema-free index of exact runnable tool and enabled skill ids for the run; it is never paged or silently truncated. `common.capabilities_search` is only an optional metadata filter over that same index. `common.capabilities_read` accepts one exact catalog id. For `kind:"tool"` it returns `kind:"tool-schema"`, the descriptor revision, complete native-like descriptor, and explicit `loaded:true`, `complete:true`, `truncated:false`; for `kind:"skill"` it returns the complete Markdown skill evidence described below. The local parser and strict response schema admit a non-bootstrap tool only after matching tool-schema evidence. Tool and skill ids share one namespace, and a collision aborts request construction instead of choosing one implicitly. Catalog sources are rebuilt at every user-run and confirmation-continuation boundary (including fresh document-local VBA discovery), then revision-pinned for that run so schemas and execution authority cannot drift mid-run.

When JSON names an exact runnable-catalog tool whose schema is not in the current callable set, the parser reports `Tool schema is not loaded` and the format-repair instruction requires a separate `common.capabilities_read` call for that exact id. It reports `Unknown tool` only for an id absent from the runnable catalog. This distinction prevents a known unloaded tool from entering a repeated unknown-id repair loop without silently auto-loading or retrying it.

Dynamic callable schemas use an evidence-derived LRU with at most eight entries and a context-derived 8,192–20,000 token budget. Successful exact calls update recency. Replay processes schema-read and tool-call evidence in order, producing the same eviction without a mutable index or hidden activation state. `TOOL_WORKING_SET` reports the current ids and any request-local eviction. Generic result truncation removes loaded evidence; compaction, descriptor revision drift, or eviction requires another exact read. A descriptor over 24,000 compact JSON characters is omitted from the runnable catalog rather than being partially advertised. The model may read several independent schemas in one response, but it cannot combine a first schema read with a dependent newly loaded call in that same response.

Planning and execution tracking are separate. `common.plan_doc_create/update/delete` stores the single broad free-form Markdown plan as immutable revisions; its exact active `rna://` URI and metadata appear in `RUNTIME_CONTEXT.active_plan`, while the body is read only through `common.resources_read`. An explicit planning request in Plan mode loads the plan schema at the first opportunity and creates the active draft once enough facts exist; message prose or HTML is not a substitute for that artifact. `common.task_list_create/update/close` stores a temporary ordered execution checklist for work with at least three meaningful stages. Runtime never maps tool calls to steps or changes statuses automatically. A ready-plan handoff switches to Agent and cites the exact revision URI; internal artifact ids are not transport.

The resource index is a bounded working-set manifest, not a body store. `common.resources_list` pages metadata, `common.resources_resolve` validates one exact reference, `common.resources_search` returns bounded literal matches, and `common.resources_read` reads one exact `metadata`, `text`, `structure`, `source`, or `media` representation by canonical revision-pinned `rna://` URI. `read` accepts the exact optional `revision` returned with a reference. Immutable text/source/structure uses an offset internally because the URI is already pinned; live continuations bind that position to the observed content hash, and list continuations bind it to a collection fingerprint. A changed live value or collection returns retryable `resource_revision_changed` instead of combining pages. Model-facing read results expose only opaque `nextCursor`, never a raw continuation offset; it must be copied unchanged into the next call's `cursor`. Search `matchOffset`/`snippetOffset` values are informational and are not read arguments. Media is attached only to the immediately following model step, with the resource URI kept as provenance and no base64 in tool JSON. A capable main model reads it directly; missing Vision/Audio capability uses the isolated attachment helper. Query-specific helper output is not advertised as a reusable resource representation.

Paste, drop, and paperclip use one chat-scoped staging action. `sendChat` accepts only the resulting `resourceDraftIds`; before any model request, runtime promotes their bytes into CAS, creates immutable artifact revisions, links them to the user message, and persists that state. Existing resources are never eagerly injected through a separate selection field: their canonical URIs remain in the bounded working set and the model reads the needed representation through `common.resources_*`.

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. Chat tools never require confirmation. An explicit user cancellation is terminal for that run and does not invoke the model again.

The skill entries in the unified capability catalog are metadata only: a listed name/summary does not load or replace the skill Markdown. When the user names a skill or a catalog summary clearly matches the task, the model calls `common.capabilities_read` with that exact id before skill-governed work. Its core `TOOL_RESULT.data` contains `kind:"skill"`, `id`, metadata, the human-authored `version`, package `revision`, `format:"markdown"`, the complete `bodyMarkdown`, explicit `loaded:true`, `complete:true`, `truncated:false`, and adjacent `capabilityUse` evidence stating that tool schemas named by the Markdown were not loaded by the skill read. Each such tool still requires its own exact schema read unless already callable. A revision is loaded only while that exact top-level evidence remains in active model context. If complete tool-schema or skill-core evidence does not fit the remaining context, transport changes the result to `ok:false` with `capability_evidence_context_too_large`, `loaded:false`, and `truncated:true`; it never reports a successful load whose evidence was removed. Compaction or a revision mismatch requires another core read; an unchanged oversized read is not retried.

A custom skill package may contain up to 64 direct UTF-8 `references/*.md` files. Their paths, byte sizes, and content revisions are listed by the core read without bodies and are included in the package revision. The model reads only a needed reference through the same tool using exact `referencePath`; optional zero-based `offset` and `maxChars` produce bounded chunks with `nextOffset`. A reference chunk is ordinary context evidence but never loads the core skill. `common.skills_upsert` writes one reference when both `referencePath` and `referenceMarkdown` are supplied; `common.skills_delete` removes one when `referencePath` is supplied. Core and reference mutations are separate confirmed calls, and each reference mutation changes the package revision. Several clearly relevant skills may be read independently. There is no router or activation state.

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

Strict response schemas require every object property to appear. Properties that are optional in the executable tool contract are therefore represented as nullable in the response schema. A model may return `null` for an irrelevant optional argument; immediately before normal validation, runtime removes those optional nulls and applies the declared defaults. Required arguments remain non-null unless their original tool schema explicitly allows null.

When `FallbackToJsonObject` is enabled and the endpoint explicitly rejects `json_schema`, ModelProtocol retries once with `json_object`, including during format repair, and keeps that choice for the rest of the run. The exact current prompt is reused and the saved selection is unchanged. This compatibility fallback has its own limit and is not model routing.

Tool call:

```json
{
  "status": "in_progress",
  "message": "Читаю диапазон.",
  "tool_calls": [
    {
      "id": "call_1",
      "name": "excel.read_range",
      "arguments": { "sheet": "Data", "address": "A1:D20" }
    }
  ]
}
```

Final answer:

```json
{
  "status": "completed",
  "message": "Готово.",
  "tool_calls": []
}
```

Conversation-response v2 requires a root `status`. The strict response schema enforces its presence and exposes only statuses callable for the current request; the local parser additionally enforces its relationship with `tool_calls`. The schema puts `status` after `tool_calls` so constrained decoders choose the action list first. This avoids unsupported cross-field constructs in provider structured-output schemas while keeping the same invariant in both `json_schema` and `json_object` modes:

| `status` | Meaning | `tool_calls` | UI/run projection |
| --- | --- | --- | --- |
| `in_progress` | The model is requesting executable work now. | At least one call. | Run continues. |
| `completed` | The model declares its answer complete. | Empty. | Loop ended; runtime execution health independently describes tool outcomes. |
| `awaiting_user` | A user decision or missing information is required. | Empty. | Current run ends and visibly waits for the user. |
| `blocked` | Work cannot proceed because of a concrete dependency or inability. | Empty. | Final blocked outcome. |
| `refused` | The request is explicitly refused. | Empty. | Final refusal. |
| `planned` | Reserved and unavailable in current modes. | Empty. | Rejected by the parser. |

The runtime enforces these rules:

- `status` is explicit and required; it is never derived from `message`, punctuation, historical tool failures, or plan text.
- `in_progress` with no calls and any terminal status with calls are structural format errors handled by the bounded format-repair path.
- `awaiting_user` is the structured form of a model question. Plan mode normally uses `common.questions_ask` for typed questions and publishes its ready artifact before returning `completed`.
- Provider-native refusal metadata maps directly to `refused`; ordinary response text is never classified as a refusal.
- `failed`, `cancelled`, `interrupted`, and `interrupted_unknown` remain runtime-owned states and are not model-selectable.
- Tool failures do not rewrite the accepted model status, but do prevent `clean` execution health. The UI shows the runtime warning outside the collapsed trace even if the model later says `completed`.
- Accepted status and response protocol version `2` are persisted in the append-only session stream; replay never reconstructs them from message wording.

The parser accepts at most 32 calls, requires a non-empty user-facing `message` for every tool turn, unique call ids, and each call to contain exactly `id`, `name`, and an object `arguments`. Duplicate JSON properties and argument names that differ only by case are rejected. Structured arguments remain native JSON objects/arrays through parsing; escaped JSON strings are not coerced. The executor checks each exact tool name and validates arguments against its tool schema immediately before execution. Calls execute locally and sequentially in array order. A multi-call response is appropriate only when calls are independent and later arguments do not depend on earlier results.

If a call needs confirmation, execution pauses at that call and later calls from the same response are not retained or executed. The pending id, cumulative iteration/tool-step counters, and execution fingerprint of that tool and its pipeline dependencies are persisted with the chat, so confirmation survives a WebView or Office restart but cannot execute a replaced definition. Cosmetic changes to unrelated tools do not invalidate it. A new request in that chat is blocked until the action is confirmed or cancelled. After confirmation, the model receives that result and chooses the remaining work normally using the remaining original budget. There is no separate batch state. The local parser tolerates additional root fields in `json_object`; strict `json_schema` rejects them at the endpoint.

`ModelProtocolClient` permits `MaxAgentFormatRetries` total protocol responses per logical step (default 10, normalized 1–20), **including the first response**. Limit 1 means no format repair; limit 20 accepts a valid twentieth response and stops after twenty invalid responses. Every repair starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored in accepted history. Internal repair attempts are not shown as user-facing activity, while the rejected payload and exact parser error remain available in trajectory diagnostics. A refusal is valid user-facing content only as `status:"refused"` with an empty `tool_calls` array. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy response-envelope normalization.

The Prompts UI and confirmed `common.prompts_save` edit the three Agent sections plus `ChatSystemPrompt`, `PlanSystemPrompt`, `ContextCompactionPrompt`, `ChatTitlePrompt`, and `AttachmentAnalysisPrompt`. Endpoint compatibility probes and JSON repair text are fixed protocol safeguards rather than agent-authored prompts.

## ModelProtocol boundary (Phase 2)

One `IModelProtocol` instance serves a conversation run. `GetResponseAsync` receives
the accepted materialized messages, current callable schemas, runnable catalog and
request-local transport options. It returns an accepted `AgentResponse` and only
that completion's metadata, or a typed `ModelProtocolFailure`. Provider failures,
cancellation, prompt-budget rejection and protocol exhaustion are distinct. The
separate bounded provider retry policy is defined below.

`ModelProtocolWire` owns active response schema options, envelope writing and local
JSON validation. The loop adds only its reasoning/cache/trace fields to fresh
options; AgentJsonProtocol retains native-role mapping and local history metadata.
Compatibility probes reuse that same contract and transcript writer, but retain
one raw attempt per check: no format repair, provider retry or fallback may turn a
failed qualification probe into a pass. Fixed sentinel values remain independent
of saved prompts. Prompt-authoring guidance refers to current defaults rather than
copying another protocol version's field/status rules.

The loop now also supplies an immutable `ModelProtocolCallContext`: all accepted
IDs in the logical turn (not just the compacted prompt) and a conservative local
batch-safe projection. Confirmation restores IDs across `RunId` changes from full
accepted history. This is preparation for v3 validation; the live v2 client does
not enforce the snapshot or its incomplete-history error. See the canonical
[context contract and remaining gates](protocols/CONVERSATION_RESPONSE_V3.md#accepted-context-and-current-v3-history-phase-2c2).

The loop owns step ids, tool execution, summaries and transcript append. Core owns
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

The nonserialized `Failure.Cause` adapter rethrows provider/cancellation and
infrastructure exceptions into the existing controller handling until the Phase 3
AgentKernel switch. V2 parsing/history and response status remain current. V3 and
its compatibility adapter are not introduced by Phases 2A/2B.
See [ADR-0002](decisions/ADR-0002-model-protocol-boundary.md) and the
[validation evidence](stabilization/PHASE_2A_MODEL_PROTOCOL.md).

## Retry policy (Phase 2B)

| Outcome | Action | Budget |
|---|---|---|
| Received completion fails the v2 contract | Retry from accepted prompt + one fresh repair instruction | Total 1–20 responses, including first |
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

Office tools execute locally. `ToolResultRole` is independent from the instruction role and controls only replay transport:

- `user` (default) or `developer`: the next turn receives a protocol message with that role and the `TOOL_RESULT:` prefix;
- `tool`: runtime stores a matching `assistant.tool_calls` entry followed by a `role=tool` message with the same call id. This is transport history only; the model still chooses tools through the JSON envelope above.

For `user` and `developer`, the result looks like:

```text
TOOL_RESULT:
{"ok":true,"tool_call_id":"call_1","name":"excel.read_range","status":"completed","message":"Range read.","data":{"values":[[1,2]]},"error":null,"resources":[{"uri":"rna://chat/s1/artifact/a1/revision/1","revision":"1","relation":"result"}]}
```

The `tool` form uses the same JSON as its message content without the text prefix. `resources` is optional and contains exact references produced by the tool or used to externalize its full result; the latter is marked `relation:"result"` so it is not confused with another produced/cited resource. On failure, `ok` is `false`, `data` may still contain partial details, and `error` contains `code`, `message`, and `retryable`. The model chooses the next step from this JSON; the runtime does not infer one. A later successful action cannot erase an earlier error or unknown effect from the runtime summary.

`message` and `data` are bounded before they enter model context. Eligible oversized generic `data` up to 2,000,000 characters is stored as a CAS-backed `tool_result` artifact before the next model dispatch; the envelope contains its exact reference and replaces inline data with `{truncated, original_chars, original_estimated_tokens, preview, hint}`. The model can page the full value through `common.resources_read` or request a smaller scope. Resource/tool/skill discovery evidence is not copied into an untrusted artifact. A specialized chart payload is materialized once at the result boundary, exposes its exact URI to the next model step, and is reused by storage/UI projection. Before every conversation model request, including format repair and continuation after confirmation, ModelProtocol verifies the estimated prompt against the current input budget and stops with a visible diagnostic instead of sending an oversized request.

Chat-local plan/HTML mutations are serialized by the per-chat lease. Manual library checks and VBA-editor reads use an isolated session snapshot, so they do not advance observations visible only to the running model. Effective safety metadata allows read-only library tools to run while that chat is active; document/local-state mutations return `manual_tool_chat_busy` until the chat stops. HTML bindings may replay only adapter tools explicitly marked `CanSourceHtmlData`; they must remain read-only, confirmation-free, enabled, and Agent-runnable. Bind and refresh revalidate the exact schema and enter the same reentrant document gate as live providers; refresh keeps the last good JSON on source failure. Document and shared-local-state mutations are serialized by effective safety metadata, including nested pipeline safety. Live `document`/`vba` provider calls use the shared gate so reads and journal reconciliation cannot cross an in-flight mutation; chat/CAS resource reads do not acquire it. Waiting for another mutation is bounded and returns retryable `tool_mutation_busy`. If an unexpected exception occurs after mutation execution may have started, the result is `tool_effect_uncertain`, is not automatically retried, and tells the model/user to inspect state first.

## Stabilization causal trace

Phase 1B gives each conversation iteration a logical step id before its first model
request, and each completion call a model attempt id. Repair/schema fallback retain
the step and receive a new attempt; tool commands retain that same logical step.
Accepted/rejected parser diagnostics link the transport request, attempt and step;
accepted diagnostics also carry the exact tool-call ids. They never enter replay.
Phase 1B left the v2 response, retry limits and outcome behavior unchanged. See the
[causal trace contract and validation limits](stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Transitional completion guard (Phase 1C)

`RunSummaryBuilder` aggregates actual executor results using effective
`ToolSafetyPolicy` metadata, including nested pipelines and local-state mutations.
Model text, descriptions and model-supplied extra JSON fields are not evidence.
Existing v2 statuses and lifecycle names remain unchanged; no AgentKernel or v3
contract is introduced in this phase.

`RunExecutionSummary` contains `ExecutionHealth` (`clean`, `errors`, `unknown`) and
`ReadOk`, `ReadError`, `WriteOk`, `WriteError`, `WriteUnknown` invocation counts.
Any uncertain write wins over errors; otherwise any read/write error wins over
clean. Pending confirmation is not an outcome. Rejected model attempts do not add
tool errors; protocol exhaustion still fails the lifecycle.

The legacy adapter conservatively maps mutation `partial_failure`, `unknown`,
`interrupted_unknown`, `tool_effect_uncertain` and missing results to unknown.
An exception escaping a possible mutation dispatch cannot certify its effect.
Other unsuccessful results are errors. Missing/invalid policy cannot certify a
successful read or write. Counts describe top-level tool invocations, including
local mutations and possible no-ops, not changed cells or verified document diffs.
ToolRuntime must replace this adapter with typed evidence in Phase 4.

A new user turn starts a fresh summary. Confirmation retains the logical turn's
earlier summary and counts the confirmed call once, including when the controller
observed it before attachment/model preparation. Continuation of an old pending
run without summary evidence stays unknown; it does not invent historical calls.

Runtime snapshots accompany visible tool/final/diagnostic messages and `LastRun`
through existing canonical event operations. Send/confirmation DTOs also expose
typed `executionSummary`; history UI uses the message snapshots. Unknown/errors
receive an independent visible warning before the unchanged model answer. A
clean no-write answer says there are no confirmed changes. A terminal/recovered
boundary without a summary is unverified, never inherited from an older clean
message. This is a minimal projection, not the Phase 9 persistence/UI migration.

See [red→green evidence and remaining limits](stabilization/PHASE_1C_COMPLETION_GUARD.md).

## Local invariants

- Disabled, unavailable, or `AgentCanRun=false` tools are not exposed to Agent mode.
- Chat exposes only the four exact `common.resources_*` read tools after schema and safety validation; it never receives skills, confirmation, document mutations, or local-state mutations.
- Confirmation and mutation safety remain local executor rules.
- HTML workspace is an ordinary Agent capability, not a separate chat mode or preference flag; the model discovers and chooses its tools from the request and current metadata/schema evidence.
- Agent mode remains available for an archived or closed document. Its local discovery catalog keeps document-independent capabilities, including HTML workspace tools, while Office/VBA tools and Office-backed HTML bindings are omitted until that document is open again.
- Every Agent run pins both the stable document key and runtime COM identity. The UI/STA adapter accepts either matching identity so COM proxy changes and document-key migration do not create false switches; when neither matches, it returns non-retryable `active_document_changed` before starting the Office tool.
- Maximum iterations and maximum tool steps bound execution.
- Tool schemas use the locally enforced closed dialect documented in the README; unsupported assertion keywords and duplicate/case-colliding property names are rejected before catalog publication.
- Pipelines call existing exact tool ids through `OfficeToolExecutor`; nested safety is resolved recursively, and unresolved `args`/`steps` placeholders fail before the affected call.
- Excel/Word/PowerPoint replacement tools inspect the current target scope inside the locked mutation. Search remains optional for discovery/preview; model-facing match-count and scope-hash preconditions are not required.
- Excel, Word, and PowerPoint publish one progressive host-neutral `common.office_run_macro` high-risk mutation tool. It accepts any exact `Application.Run` macro name and up to 30 positional scalar arguments without a manifest or allowlist, always requires confirmation unless auto-confirm is explicitly enabled, and returns the actual execution result to the loop. Host-prefixed `*.run_macro` ids remain hidden adapter backends; Outlook does not expose this unsupported runtime.
- VBA discovery, source search/read, and backup metadata use provider `vba` through `common.resources_list/resolve/search/read`; source reads are bounded character chunks with explicit continuation and content-hash evidence. The four public `common.vba_*` operations are mutations only: write/rename, exact patch, delete, and restore. They keep backup/strict-live-hash/stale-state checks inside the implementation. Runtime reads current state and binds a chat/document/module guard while preparing the mutation, persists it through confirmation, revalidates immediately before mutation, and verifies final state by read-back. An exact patch skips already-satisfied hunks; if the ordered result equals current source, it succeeds without a write, backup, or journal entry because no mutation occurs. No preparatory public read or model-supplied hash is required; when a resource source read/search snapshot already exists, runtime consumes it automatically to surface one actionable stale warning before rebinding on an intentional retry. Rename guards/journals old and new names and uses a hidden identity-preserving backend rather than write+delete. Removed built-in ids are rejected directly and saved pipelines are never compatibility-rewritten. Export-aware package hashes remain separate from live module hashes.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace a fully included replay prefix with a stored checkpoint and a bounded deterministic union of its exact resource references, but it does not split a tool exchange, delete the source transcript, partially mark an oversized message as summarized, change the agent protocol, or repeat Office tools.
- A persisted `running` or `cancelling` run without a live cross-process owner is marked interrupted and is never resumed automatically. If it stopped while a tool may have been in flight, it is marked `interrupted_unknown` and that run's protocol remains visible but is excluded from replay. Protocol through a saved tool-result boundary remains replayable.
