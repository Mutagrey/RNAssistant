# Agent JSON flow

RNAssistant has two explicit modes.

- `chat`: a normal text completion. The request contains no tool catalog or skill bodies and the response is not parsed as an agent command.
- `agent`: a prompt-driven loop over local Office tools. The runtime does not route the request, select a phase, activate skills, repair model output, retry tools, or verify mutations as a separate agent stage.

## Agent context

Every Agent request contains the editable `SystemPrompt` and one `RUNTIME_CONTEXT` JSON object:

- current host and document identity;
- every enabled tool that the agent may run;
- every enabled skill with its full Markdown instructions;
- chat-owned user context and artifact references.

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
        "sheet": { "type": "string" },
        "address": { "type": "string" }
      },
      "required": ["address"],
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

Custom tools must have a formal object JSON Schema. Safety metadata is resolved locally and cannot be overridden by a prompt or skill.

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

Only one tool call is accepted per model turn. The parser checks the JSON shape and exact tool name; the executor validates arguments against the tool schema immediately before execution. Additional root fields are allowed so the prompt can evolve without a protocol migration.

An invalid model response ends the run with a visible diagnostic. There is no repair loop or legacy normalization.

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
