# Agent JSON flow

RNAssistant has two explicit modes.

- `chat`: a normal text completion. The request contains no tool catalog or skill bodies and the response is not parsed as an agent command.
- `agent`: a prompt-driven loop over local Office tools. The runtime does not route the request, select a phase, activate skills, retry tools, or verify mutations as a separate agent stage. It may make one request-local format correction when the model violates the JSON contract.

## Agent context

Every Agent request contains the editable `SystemPrompt` and one `RUNTIME_CONTEXT` JSON object:

- current host and document identity;
- every enabled tool that the agent may run;
- the enabled skill catalog with `id`, `name`, and `description` only;
- chat-owned user context and artifact references.

Visible planning is optional data, not a protocol phase. `common.plan_create/read/update/delete` stores a versioned plan artifact for the active chat. The model explicitly supplies every step status; runtime does not infer progress from tool calls. The active plan artifact id appears in the artifact index.

A confirmed tool result always returns to the Agent loop, including `ok:false`, so the model can explain the failure, correct arguments, or choose another tool. An explicit user cancellation is terminal for that run and does not invoke the model again.

When a catalog description matches the task, the model calls `common.skills_read` with the exact id. Its `TOOL_RESULT.data` contains `id`, `host`, `name`, `description`, `version`, `enabled`, `format: "markdown"`, and the complete body in both authoring-compatible `bodyMarkdown` and model-facing `instructions`. Several clearly relevant skills may be read as independent calls. The result is normal conversation history; there is no router or activation state.

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

Agent mode always requests `response_format.type=json_object`. The model returns one raw JSON object with no Markdown or surrounding prose.

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

The parser accepts one or more calls, requires unique call ids, and checks each exact tool name. The executor validates every argument object against its tool schema immediately before execution. Calls execute locally and sequentially in array order. A multi-call response is appropriate only when calls are independent and later arguments do not depend on earlier results.

If a call needs confirmation, execution pauses at that call and later calls from the same response are not retained or executed. After confirmation, the model receives that result and chooses the remaining work normally. There is no separate batch state. Additional root fields are allowed so the prompt can evolve without a protocol migration.

If parsing fails, the runtime makes up to `MaxAgentFormatRetries` correction requests (default 2, clamped to 1–5). Every attempt starts from the same accepted conversation plus one current `FORMAT_REPAIR` instruction; rejected output and prior repair instructions are never copied forward or stored. A refusal remains valid user-facing content when returned in `message` with an empty `tool_calls` array. Exhausting the limit ends the run with a visible diagnostic excluded from model replay. There is no separate repair state machine or legacy normalization.

## Tool result

Office tools execute locally. The accepted assistant JSON and the result are stored as hidden protocol messages. The next model turn receives the result as a string user message:

```text
TOOL_RESULT:
{"ok":true,"tool_call_id":"call_1","name":"excel.read_range","status":"completed","message":"Range read.","data":{"values":[[1,2]]},"error":null}
```

On failure, `ok` is `false`, `data` may still contain partial details, and `error` contains `code`, `message`, and `retryable`. The model chooses the next step from this JSON; the runtime does not infer one.

## Local invariants

- Disabled, unavailable, or `AgentCanRun=false` tools are not exposed to Agent mode.
- Confirmation and mutation safety remain local executor rules.
- `AutoRunToolCalls`, maximum iterations, and maximum tool steps bound execution.
- Pipelines call existing tool ids through `OfficeToolExecutor`; nested safety is resolved recursively.
- VBA mutations keep their backup/hash/stale-state checks inside the VBA tool implementation and may require confirmation.
- Provider reasoning is transport metadata, not part of the agent JSON or replay history.
- Context compaction may replace the replay prefix with a stored checkpoint, but it does not change the agent protocol or repeat Office tools.
