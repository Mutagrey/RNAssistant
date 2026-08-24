# Agent JSON flow

RNAssistant has two explicit modes.

- `chat`: a normal text completion. The request contains no tool catalog or skill bodies and the response is not parsed as an agent command.
- `agent`: a prompt-driven loop over local Office tools. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate agent stage. It may make bounded request-local format corrections when the model violates the JSON contract.

## Agent context

Every Agent request contains the editable Markdown `SystemPrompt` and one `RUNTIME_CONTEXT` JSON object. Its instruction role is selected independently as `developer` (default), `system`, or `user`:

- current host and document identity;
- every enabled, schema-valid tool that the agent may run in this request;
- the enabled skill catalog with `id`, `name`, and `description` only;
- chat-owned user context and artifact references.

Visible planning is optional data, not a protocol phase. `common.plan_create/read/update/delete` stores a versioned plan artifact for the active chat. The model explicitly supplies every step status; runtime does not infer progress from tool calls. The active plan artifact id appears in the artifact index.

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. An explicit user cancellation is terminal for that run and does not invoke the model again.

When a catalog description matches the task, the model calls `common.skills_read` with the exact id. Its `TOOL_RESULT.data` contains `id`, `host`, `name`, `description`, `version`, `enabled`, `format: "markdown"`, and the complete body once in authoring-compatible `bodyMarkdown`, subject only to the remaining request budget. Several clearly relevant skills may be read as independent calls. The result is normal conversation history; there is no router or activation state.

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

Agent mode always returns the same raw JSON envelope with no Markdown or surrounding prose. `AgentResponseMode` selects its transport constraint:

- `json_object` (default) asks the endpoint for a generic JSON object and relies on the local parser and tool argument validators;
- `json_schema` sends a strict response schema generated from the exact currently runnable tool catalog. The schema fixes the root fields, exact tool names, and each tool's argument contract.

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

The parser accepts one or more calls, requires a non-empty user-facing `message` for every tool turn, requires call ids to be unique within that response, and checks each exact tool name. The executor validates every argument object against its tool schema immediately before execution. Calls execute locally and sequentially in array order. A multi-call response is appropriate only when calls are independent and later arguments do not depend on earlier results.

If a call needs confirmation, execution pauses at that call and later calls from the same response are not retained or executed. The pending id, cumulative iteration/tool-step counters, and execution fingerprint of that tool and its pipeline dependencies are persisted with the chat, so confirmation survives a WebView or Office restart but cannot execute a replaced definition. Cosmetic changes to unrelated tools do not invalidate it. A new request in that chat is blocked until the action is confirmed or cancelled. After confirmation, the model receives that result and chooses the remaining work normally using the remaining original budget. There is no separate batch state. The local parser tolerates additional root fields in `json_object`; strict `json_schema` rejects them at the endpoint.

If parsing fails, the runtime makes up to `MaxAgentFormatRetries` correction requests (default 10, clamped to 1–20). Every attempt starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored. A refusal remains valid user-facing content when returned in `message` with an empty `tool_calls` array. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy response-envelope normalization.

## Tool result

Office tools execute locally. `ToolResultRole` is independent from the instruction role and controls only replay transport:

- `user` (default) or `developer`: the next turn receives a protocol message with that role and the `TOOL_RESULT:` prefix;
- `tool`: runtime stores a matching `assistant.tool_calls` entry followed by a `role=tool` message with the same call id. This is transport history only; the model still chooses tools through the JSON envelope above.

For `user` and `developer`, the result looks like:

```text
TOOL_RESULT:
{"ok":true,"tool_call_id":"call_1","name":"excel.read_range","status":"completed","message":"Range read.","data":{"values":[[1,2]]},"error":null}
```

The `tool` form uses the same JSON as its message content without the text prefix. On failure, `ok` is `false`, `data` may still contain partial details, and `error` contains `code`, `message`, and `retryable`. The model chooses the next step from this JSON; the runtime does not infer one.

`message` and `data` are bounded before they enter model context. Oversized `data` is replaced with `{truncated, original_chars, original_estimated_tokens, preview, hint}` so the model can request a smaller scope. Before every model request, including format repair and continuation after confirmation, the runtime verifies the estimated prompt against the current input budget and stops with a visible diagnostic instead of sending an oversized request.

Chat-local plan/HTML mutations are serialized by the per-chat lease. HTML bindings may replay only adapter tools explicitly marked `CanSourceHtmlData`; they must remain read-only, confirmation-free, enabled, and Agent-runnable. Refresh keeps the last good JSON on source failure. Document and shared-local-state mutations are serialized by effective safety metadata, including nested pipeline safety. Waiting for another mutation is bounded and returns retryable `tool_mutation_busy`. If an unexpected exception occurs after mutation execution may have started, the result is `tool_effect_uncertain`, is not automatically retried, and tells the model/user to inspect state first.

## Local invariants

- Disabled, unavailable, or `AgentCanRun=false` tools are not exposed to Agent mode.
- Confirmation and mutation safety remain local executor rules.
- Agent mode remains available for an archived or closed document. Its request catalog keeps document-independent local tools, including HTML workspace tools, while Office/VBA tools and Office-backed HTML bindings are omitted until that document is open again.
- Every Agent run pins both the stable document key and runtime COM identity. The UI/STA adapter accepts either matching identity so COM proxy changes and document-key migration do not create false switches; when neither matches, it returns non-retryable `active_document_changed` before starting the Office tool.
- Maximum iterations and maximum tool steps bound execution.
- Pipelines call existing tool ids through `OfficeToolExecutor`; nested safety is resolved recursively.
- Excel/Word/PowerPoint replacement tools inspect the current target scope inside the locked mutation. Search remains optional for discovery/preview; model-facing match-count and scope-hash preconditions are not required.
- VBA mutations keep backup/strict-live-hash/stale-state checks inside the implementation. Runtime reads current state and binds a chat/document/module guard while preparing the mutation, persists it through confirmation, revalidates immediately before mutation, and verifies final state by read-back. No preparatory public read or model-supplied hash is required; when a read/search snapshot already exists, runtime consumes it automatically to surface one actionable stale warning before rebinding on an intentional retry. `common.vba_read_module` handles whole-source and bounded line-range reads; `common.vba_write_module` is an idempotent whole-source upsert. Legacy VBA and superseded HTML/Office discovery/read/write ids canonicalize to compact facades, including inside saved pipelines. Export-aware package hashes remain separate from live module hashes.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace a fully included replay prefix with a stored checkpoint, but it does not split a tool exchange, delete the source transcript, partially mark an oversized message as summarized, change the agent protocol, or repeat Office tools.
- A persisted `running` or `cancelling` run without a live cross-process owner is marked interrupted and is never resumed automatically. If it stopped while a tool may have been in flight, it is marked `interrupted_unknown` and that run's protocol remains visible but is excluded from replay. Protocol through a saved tool-result boundary remains replayable.
