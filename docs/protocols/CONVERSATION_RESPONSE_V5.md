# Conversation Response v5

Status: **active R72 response-intent contract**. Response protocol is `5`;
prompt schema is `26`. Product version is independent and unchanged by this
switch. The [v4 specification](CONVERSATION_RESPONSE_V4.md) is historical, not a
runtime compatibility path. This document records host-neutral behavior; Windows,
Office and live-provider qualification remain separate gates.

## Envelope

Tool turn:

```json
{
  "message": "Прочитаю диапазон.",
  "final": false,
  "tool_calls": [
    {
      "name": "excel.read_range",
      "arguments": { "address": "A1:D20" }
    }
  ]
}
```

No-tool checkpoint:

```json
{
  "message": "Составляю итоговый отчет.",
  "final": false,
  "tool_calls": []
}
```

Final answer:

```json
{
  "message": "Обработка завершена.",
  "final": true,
  "tool_calls": []
}
```

- Root contains exactly `message` (string), `final` (boolean) and `tool_calls`
  (array). Model-owned `status`, `phase`, `completed`, `retry`, `verified` and
  all other root fields are rejected.
- `final=true` means only that `message` is the final user-facing answer for the
  model loop. It is valid only with an empty `tool_calls` array.
- `final=false` with one or more calls is a normal tool turn.
- `final=false` with empty calls is an accepted no-tool checkpoint. The runtime
  persists it and asks the model for the next response instead of completing.
- Message wording never proves execution success, failure, verification or
  refusal. Runtime lifecycle, execution health and effect evidence remain separate.
- Each call contains exactly a nonblank string `name` and object `arguments`.
  Call-level `id` is forbidden; runtime remains the only call-ID owner.
- Names and envelope fields are case-sensitive. At most 32 calls may be returned,
  preserving array order. One raw JSON object is required, without Markdown,
  surrounding prose, comments, duplicate properties, trailing content or non-JSON
  literals.

## Runtime Behavior

`ModelProtocolWire` owns the one active parser, schema and canonical wire writer.
`ConversationResponse.ToJson()` writes the v5 envelope only. Both `json_schema`
and `json_object` responses pass through the same local parser and safety checks.

`AgentKernel` completes a model loop only after an accepted response with
`final=true` and no calls. An accepted `final=false` empty-call response increments
a bounded no-tool checkpoint counter and continues. Three consecutive no-tool
checkpoints fail closed with `model_loop_stalled`, without dispatching tools or
inventing effects.

The same R29 runtime-ID boundary remains in force: the model supplies only
`name` and `arguments`; the kernel assigns opaque IDs after whole-response
validation and before accepted persistence, confirmation or dispatch. Rejected
responses execute nothing. Runtime ID allocation failures are infrastructure
failures and are never repaired by regenerating model content.

## History And Prompts

Accepted assistant records are explicitly marked `ResponseProtocolVersion=5`.
History is a projection of accepted runtime calls and accepted no-tool/final
responses, not a second model-facing response format. Unmarked, older or malformed
assistant history requires explicit reset/new chat; RNAssistant does not sniff,
convert, dual-write or delete user data automatically.

Agent, Chat and Plan defaults switch together to prompt schema `26`. Missing,
older or future stored markers require explicit review/reset before execution.
Prompt guidance must describe `final` as response intent only; tool results and
read-back evidence remain the authority for effects.

## Remaining Gates

Required host-neutral evidence covers schema/parser/writer, prompt defaults,
accepted history, no-tool checkpoint continuation, bounded stalled-loop failure
and unchanged R29 call-ID/result behavior. Windows Office, WebView2 and live
provider qualification remain open until recorded in stabilization progress.
