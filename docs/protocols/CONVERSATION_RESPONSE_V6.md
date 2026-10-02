# Conversation Response v6

Status: **active**. Response protocol is `6`; built-in prompt schema is `38`.
The [v5 contract](CONVERSATION_RESPONSE_V5.md) is historical. Existing v5 chats
require explicit new chat/reset; no automatic conversion or data deletion occurs.

## Envelope

One raw JSON object contains exactly `message` (string), `action` (string), and
`tool_calls` (array). Calls contain only exact `name` and object `arguments`;
runtime assigns call IDs. No Markdown, extra fields, duplicate keys or JSON
extensions are accepted. At most 32 calls are allowed.

| `action` | `tool_calls` | Meaning |
|---|---|---|
| `tool` | One or more | Dispatch validated calls. Multiple calls are allowed only for independent local reads. |
| `continue` | Empty | Record a visible finding/decision/next step, then request another model response. |
| `done` | Empty | Model assesses the requested answer or work as complete and ends the turn. |
| `blocked` | Empty | End the turn with unfinished work and a concrete blocker. |
| `needs_input` | Empty | End the turn with a material question for the user. |

`continue`, `blocked` and `needs_input` require a nonblank message. Three consecutive no-call `continue`
responses fail the run with `no_tool_progress`; a tool call or new user input
resets this counter. `done`, `blocked` and `needs_input` are distinct accepted
model decisions. They do not prove effects or semantic completion. Runtime
`RunLifecycle.Completed` means the model turn ended; its reason preserves the
model decision. `RunViewState` and the result card show actual read/write
evidence separately.

The `message` on `tool` concisely states the relevant finding from a prior
result, when available, the decision it supports and why the selected call is
next. An initial read states the question it will answer. A `continue` message
records a concrete checkpoint, not merely an announcement. `done` reports the
delivered outcome and available checks; `blocked` and `needs_input` name the
unfinished outcome and obstacle or question. Message prose does not expose
private reasoning or grant execution authority. The selected calls, tool
results and read-back remain the only evidence for operations. Unknown effects
are never retried automatically.

## Parsing, history and recovery

`ModelProtocolWire` owns parser, strict schema, canonical writer and format
repair for both `json_schema` and `json_object`. The kernel validates action/call
shape again before acceptance. An accepted no-call `continue` is persisted as
protocol history and a visible run step before the next model request. Call
acceptance retains runtime IDs and exact tool policy as before.

Accepted assistant history is marked `ResponseProtocolVersion=6`. Full-history
preflight rejects incompatible or malformed records before dispatch. Existing
v5 data stays intact and requires explicit reset/new chat. Windows/Office,
WebView2 and target-model qualification remain open.
