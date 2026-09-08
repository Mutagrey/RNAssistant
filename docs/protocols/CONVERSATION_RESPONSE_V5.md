# Conversation Response v5

Status: **active R72 response-intent contract**. Response protocol is `5`;
current prompt schema is `29` (`AppSettings.CurrentAgentPromptSchemaVersion`).
Product version is independent and unchanged by this switch. The
[v4 specification](CONVERSATION_RESPONSE_V4.md) is historical, not a
runtime compatibility path. This document records host-neutral behavior; Windows,
Office and live-provider qualification remain separate gates.

## Envelope

Tool turn:

The example target is copied from current runtime context or resource discovery.

```json
{
  "message": "Прочитаю диапазон.",
  "final": false,
  "tool_calls": [
    {
      "name": "common.resources_read",
      "arguments": { "target": "Excel range: Data!A1:B4", "representation": "text" }
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
  persists it and asks the model for the next response instead of completing. That
  next request includes a transient runtime continuation telling the model to emit
  `final=true` when the checkpoint already contains the complete answer or asks for
  user input, or to emit the next tool calls; the continuation is not chat history.
- Message wording never proves execution success, failure, verification or
  refusal. Runtime lifecycle, execution health and effect evidence remain separate.
- On tool turns, the native schema asks `message` to briefly connect a relevant
  observed finding, the purpose of the actual upcoming calls, and what their results
  will clarify. Unknown parts are omitted, without invented findings or private
  reasoning narration. This is communication guidance, not a runtime-parsed format.
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
a bounded no-tool checkpoint counter and continues with the transient instruction
above. Three consecutive no-tool
checkpoints fail closed with `model_loop_stalled`, without dispatching tools or
inventing effects.

The same R29 runtime-ID boundary remains in force: the model supplies only
`name` and `arguments`; the kernel assigns opaque IDs after whole-response
validation and before accepted persistence, confirmation or dispatch. Rejected
responses execute nothing. Runtime ID allocation failures are infrastructure
failures and are never repaired by regenerating model content.

More than one call is accepted only when every member belongs to the current
runtime-owned sequential-batch set: independent local reads or managed mutations
whose confirmation is already satisfied. Calls execute in array order and every
mutation has its own fresh guard, dispatch, verification and commit; the batch has
no atomicity promise. An unknown mutation effect remains cumulative run evidence,
but does not close the remaining accepted members by itself. Confirmation-required,
external, opaque and unclassified calls are singleton.

## History And Prompts

Accepted assistant records are explicitly marked `ResponseProtocolVersion=5`.
History is a projection of accepted runtime calls and accepted no-tool/final
responses, not a second model-facing response format. Unmarked, older or malformed
assistant history requires explicit reset/new chat; RNAssistant does not sniff,
convert, dual-write or delete user data automatically.

Agent, Chat and Plan defaults use the same current prompt schema `29`. Missing,
older or future stored markers require explicit review/reset before execution.
Prompt guidance must describe `final` as response intent only; tool results and
read-back evidence remain the authority for effects.

## Remaining Gates

Required host-neutral evidence covers schema/parser/writer, prompt defaults,
accepted history, no-tool checkpoint continuation, bounded stalled-loop failure
and unchanged R29 call-ID/result behavior. Windows Office, WebView2 and live
provider qualification remain open until recorded in stabilization progress.
