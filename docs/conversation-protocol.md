# Conversation JSON flow

RNAssistant has two explicit modes and one `ConversationRunService` transport/transcript loop.

- `chat`: the editable `ChatSystemPrompt`, a dynamic `RUNTIME_CONTEXT`, and exactly the safe read-only `common.resources_list/resolve/search/read` catalog. Skills, Office tools, local mutations, and confirmation are unavailable by runtime policy.
- `agent`: the same structured loop with progressive tool discovery and enabled skill metadata. The complete mode/session-filtered catalog remains local execution authority; the model receives only the current callable schema working set. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate stage.

Both modes return the same raw `message + tool_calls[]` JSON envelope and use the same bounded request-local format repair. The tool catalog, not model wording, is the authority: a Chat response naming any other tool is rejected before execution.

## Conversation context

Every request contains one editable instruction followed by one dynamic `RUNTIME_CONTEXT` JSON object. Agent composes general (`SystemPrompt`), tool-use (`AgentToolsPrompt`), and skill-use (`AgentSkillsPrompt`) Markdown; Chat uses `ChatSystemPrompt`. The instruction role is selected independently as `developer` (default), `system`, or `user`:

- current host and document identity;
- exact bootstrap and currently loaded callable tool descriptors plus `tool_discovery` catalog revision, bounds, and compact namespace counts;
- the enabled skill catalog with `id`, `name`, `description`, package `revision`, `bodyChars`, and `referenceCount` in Agent, or an empty catalog in Chat;
- chat-owned user context and artifact references.

The Agent sections and Chat prompt use one explicit settings schema version. Settings without the current marker are hard-reset to the current defaults; RNAssistant does not merge an older no-tools Chat contract into the structured loop. Once the current marker is saved, current custom values are preserved normally.

Agent bootstrap schemas are `common.resources_list/resolve/search/read`, `common.tools_list/search/read`, and `common.skills_read`. `common.tools_list` pages metadata inside an exact namespace, while `common.tools_search` performs bounded literal metadata search; neither returns parameters or makes a result tool callable. `common.tools_read` accepts one exact runnable id and returns `kind:"tool-schema"`, the descriptor revision, complete native-like descriptor, and explicit `loaded:true`, `complete:true`, `truncated:false`. The local parser and strict response schema admit a non-bootstrap tool only after that exact evidence matches the current descriptor.

Dynamic callable schemas use an evidence-derived LRU with at most eight entries and a context-derived 8,192–20,000 token budget. Successful exact calls update recency. Replay processes schema-read and tool-call evidence in order, producing the same eviction without a mutable index or hidden activation state. `TOOL_WORKING_SET` reports the current ids and any request-local eviction. Generic result truncation removes loaded evidence; compaction, descriptor revision drift, or eviction requires another exact read. A descriptor over 24,000 compact JSON characters is omitted from the runnable catalog rather than being partially advertised. The model may read several independent schemas in one response, but it cannot combine a first schema read with a dependent newly loaded call in that same response.

Visible planning is optional data, not a protocol phase. `common.plan_create/update/delete` stores a versioned plan artifact for the active chat, and `common.resources_read` reads its exact active `rna://` revision. The model explicitly supplies every step status; runtime does not infer progress from tool calls. The active plan URI appears in `RUNTIME_CONTEXT`.

The resource index is a bounded working-set manifest, not a body store. `common.resources_list` pages metadata, `common.resources_resolve` validates one exact reference, `common.resources_search` returns bounded literal matches, and `common.resources_read` reads one exact `metadata`, `text`, `structure`, `source`, or `media` representation by canonical revision-pinned `rna://` URI. `read` accepts the exact optional `revision` returned with a reference. Immutable text/source/structure uses an offset internally because the URI is already pinned; live continuations bind that position to the observed content hash, and list continuations bind it to a collection fingerprint. A changed live value or collection returns retryable `resource_revision_changed` instead of combining pages. Model-facing read results expose only opaque `nextCursor`, never a raw continuation offset; it must be copied unchanged into the next call's `cursor`. Search `matchOffset`/`snippetOffset` values are informational and are not read arguments. Media is attached only to the immediately following model step, with the resource URI kept as provenance and no base64 in tool JSON. A capable main model reads it directly; missing Vision/Audio capability uses the isolated attachment helper. Query-specific helper output is not advertised as a reusable resource representation.

Paste, drop, and paperclip use one chat-scoped staging action. `sendChat` accepts only the resulting `resourceDraftIds`; before any model request, runtime promotes their bytes into CAS, creates immutable artifact revisions, links them to the user message, and persists that state. Existing resources are never eagerly injected through a separate selection field: their canonical URIs remain in the bounded working set and the model reads the needed representation through `common.resources_*`.

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. Chat tools never require confirmation. An explicit user cancellation is terminal for that run and does not invoke the model again.

The skill catalog is metadata only: a listed name/description does not load or replace the skill Markdown. When the user names a skill or a catalog description clearly matches the task, the model calls `common.skills_read` with the exact id before skill-governed work. Its core `TOOL_RESULT.data` contains `kind:"skill"`, `id`, metadata, the human-authored `version`, package `revision`, `format:"markdown"`, the complete `bodyMarkdown`, and explicit `loaded:true`, `complete:true`, `truncated:false`. A revision is loaded only while that exact top-level evidence remains in active model context. Generic bounding replaces oversized data with top-level `truncated:true` and therefore cannot preserve a false loaded marker. Compaction or a revision mismatch requires another core read; an unchanged truncated core read is not retried.

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

Both modes always return the same raw JSON envelope with no Markdown or surrounding prose. `AgentResponseMode` selects its transport constraint for the shared loop:

- `json_object` (default) asks the endpoint for a generic JSON object and relies on the local parser and tool argument validators;
- `json_schema` sends a strict response schema generated from the exact current callable working set. The schema fixes the root fields, loaded tool names, and each loaded argument contract; the full internal catalog is never copied into it.

With SSE enabled, transport chunks still contain that raw JSON envelope. The live UI projection incrementally decodes only the root `message` string, resets it for every model request or format-repair attempt, and never exposes `tool_calls`/other raw JSON. Provider reasoning and one leading `<think>` block use the separate reasoning projection; its terminal update is emitted before visible message content starts or when the stream ends.

Strict response schemas require every object property to appear. Properties that are optional in the executable tool contract are therefore represented as nullable in the response schema. A model may return `null` for an irrelevant optional argument; immediately before normal validation, runtime removes those optional nulls and applies the declared defaults. Required arguments remain non-null unless their original tool schema explicitly allows null.

When `FallbackToJsonObject` is enabled and the endpoint explicitly rejects `json_schema`, that run is retried once with `json_object`; the saved selection is unchanged. This is not model routing or general error retry.

Tool call:

```json
{
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

Final answer or clarification:

```json
{
  "message": "Готово.",
  "tool_calls": []
}
```

An empty `tool_calls` array ends the run. It must not accompany an unfinished action promise. While runnable tools exist, the parser conservatively rejects short Russian or English progress-only messages such as “создаю…” or “checking…” with no call and sends them through the ordinary bounded format-repair path. The check also evaluates a trailing promise after a completed-prefix sentence, such as “Анализ завершен. Подготавливаю отчет.” A concrete answer, clarification, refusal, completion, or inability remains a valid terminal response.

The parser accepts at most 32 calls, requires a non-empty user-facing `message` for every tool turn, unique call ids, and each call to contain exactly `id`, `name`, and an object `arguments`. Duplicate JSON properties and argument names that differ only by case are rejected. Structured arguments remain native JSON objects/arrays through parsing; escaped JSON strings are not coerced. The executor checks each exact tool name and validates arguments against its tool schema immediately before execution. Calls execute locally and sequentially in array order. A multi-call response is appropriate only when calls are independent and later arguments do not depend on earlier results.

If a call needs confirmation, execution pauses at that call and later calls from the same response are not retained or executed. The pending id, cumulative iteration/tool-step counters, and execution fingerprint of that tool and its pipeline dependencies are persisted with the chat, so confirmation survives a WebView or Office restart but cannot execute a replaced definition. Cosmetic changes to unrelated tools do not invalidate it. A new request in that chat is blocked until the action is confirmed or cancelled. After confirmation, the model receives that result and chooses the remaining work normally using the remaining original budget. There is no separate batch state. The local parser tolerates additional root fields in `json_object`; strict `json_schema` rejects them at the endpoint.

If parsing fails, the runtime makes up to `MaxAgentFormatRetries` correction requests (default 10, clamped to 1–20). Every attempt starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored. A refusal remains valid user-facing content when returned in `message` with an empty `tool_calls` array. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy response-envelope normalization.

The Prompts UI and confirmed `common.prompts_save` edit the three Agent sections plus `ChatSystemPrompt`, `ContextCompactionPrompt`, `ChatTitlePrompt`, and `AttachmentAnalysisPrompt`. Endpoint compatibility probes and JSON repair text are fixed protocol safeguards rather than agent-authored prompts.

## Tool result

Office tools execute locally. `ToolResultRole` is independent from the instruction role and controls only replay transport:

- `user` (default) or `developer`: the next turn receives a protocol message with that role and the `TOOL_RESULT:` prefix;
- `tool`: runtime stores a matching `assistant.tool_calls` entry followed by a `role=tool` message with the same call id. This is transport history only; the model still chooses tools through the JSON envelope above.

For `user` and `developer`, the result looks like:

```text
TOOL_RESULT:
{"ok":true,"tool_call_id":"call_1","name":"excel.read_range","status":"completed","message":"Range read.","data":{"values":[[1,2]]},"error":null,"resources":[{"uri":"rna://chat/s1/artifact/a1/revision/1","revision":"1","relation":"result"}]}
```

The `tool` form uses the same JSON as its message content without the text prefix. `resources` is optional and contains exact references produced by the tool or used to externalize its full result; the latter is marked `relation:"result"` so it is not confused with another produced/cited resource. On failure, `ok` is `false`, `data` may still contain partial details, and `error` contains `code`, `message`, and `retryable`. The model chooses the next step from this JSON; the runtime does not infer one. A completed run's collapsed UI status reflects its terminal final answer; recovered intermediate failures remain visible only in the expanded action trace and do not relabel that run as failed.

`message` and `data` are bounded before they enter model context. Eligible oversized generic `data` up to 2,000,000 characters is stored as a CAS-backed `tool_result` artifact before the next model dispatch; the envelope contains its exact reference and replaces inline data with `{truncated, original_chars, original_estimated_tokens, preview, hint}`. The model can page the full value through `common.resources_read` or request a smaller scope. Resource/tool/skill discovery evidence is not copied into an untrusted artifact. A specialized chart payload is materialized once at the result boundary, exposes its exact URI to the next model step, and is reused by storage/UI projection. Before every model request, including format repair and continuation after confirmation, the runtime verifies the estimated prompt against the current input budget and stops with a visible diagnostic instead of sending an oversized request.

Chat-local plan/HTML mutations are serialized by the per-chat lease. Manual library checks and VBA-editor reads use an isolated session snapshot, so they do not advance observations visible only to the running model. Effective safety metadata allows read-only library tools to run while that chat is active; document/local-state mutations return `manual_tool_chat_busy` until the chat stops. HTML bindings may replay only adapter tools explicitly marked `CanSourceHtmlData`; they must remain read-only, confirmation-free, enabled, and Agent-runnable. Bind and refresh revalidate the exact schema and enter the same reentrant document gate as live providers; refresh keeps the last good JSON on source failure. Document and shared-local-state mutations are serialized by effective safety metadata, including nested pipeline safety. Live `document`/`vba` provider calls use the shared gate so reads and journal reconciliation cannot cross an in-flight mutation; chat/CAS resource reads do not acquire it. Waiting for another mutation is bounded and returns retryable `tool_mutation_busy`. If an unexpected exception occurs after mutation execution may have started, the result is `tool_effect_uncertain`, is not automatically retried, and tells the model/user to inspect state first.

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
- VBA discovery, source search/read, and backup metadata use provider `vba` through `common.resources_list/resolve/search/read`; source reads are bounded character chunks with explicit continuation and content-hash evidence. The four public `common.vba_*` operations are mutations only: write/rename, exact patch, delete, and restore. They keep backup/strict-live-hash/stale-state checks inside the implementation. Runtime reads current state and binds a chat/document/module guard while preparing the mutation, persists it through confirmation, revalidates immediately before mutation, and verifies final state by read-back. An exact patch skips already-satisfied hunks; if the ordered result equals current source, it succeeds without a write, backup, or journal entry because no mutation occurs. No preparatory public read or model-supplied hash is required; when a resource source read/search snapshot already exists, runtime consumes it automatically to surface one actionable stale warning before rebinding on an intentional retry. Rename guards/journals old and new names and uses a hidden identity-preserving backend rather than write+delete. Removed built-in ids are rejected directly and saved pipelines are never compatibility-rewritten. Export-aware package hashes remain separate from live module hashes.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace a fully included replay prefix with a stored checkpoint and a bounded deterministic union of its exact resource references, but it does not split a tool exchange, delete the source transcript, partially mark an oversized message as summarized, change the agent protocol, or repeat Office tools.
- A persisted `running` or `cancelling` run without a live cross-process owner is marked interrupted and is never resumed automatically. If it stopped while a tool may have been in flight, it is marked `interrupted_unknown` and that run's protocol remains visible but is excluded from replay. Protocol through a saved tool-result boundary remains replayable.
