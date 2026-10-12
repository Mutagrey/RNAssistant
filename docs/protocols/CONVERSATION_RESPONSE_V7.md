# Conversation Response v7

Status: **active**. Response protocol is `7`; built-in prompt schema is `40`.
The [v6 contract](CONVERSATION_RESPONSE_V6.md) is historical. Existing v6 chats
require explicit new chat/reset; no automatic conversion or data deletion occurs.

## Envelope

One raw JSON object contains exactly `message` (string), `action` (string), and
`tool_calls` (array). Calls contain only exact `name` and object `arguments`;
runtime assigns call IDs. No Markdown, extra fields, duplicate keys or JSON
extensions are accepted. At most 32 calls are allowed.

| `action` | `tool_calls` | Meaning |
|---|---|---|
| `tool` | One or more | Dispatch validated calls. Multiple calls are allowed only for independent local reads. |
| `done` | Empty | Model assesses the requested answer or work as complete and ends the turn. |
| `blocked` | Empty | End the turn with unfinished work and a concrete blocker. |
| `needs_input` | Empty | End the turn with a material question for the user. |

`blocked` and `needs_input` require a nonblank message. `done`, `blocked` and
`needs_input` are distinct accepted model decisions. They do not prove effects or semantic completion. Runtime
`RunLifecycle.Completed` means the model turn ended; its reason preserves the
model decision. `RunViewState` and the result card show actual read/write
evidence separately.

A response without calls ends the model turn. The runtime requests the next
model step after tool results or new user input, never merely to elaborate on a
progress message. `needs_input` preserves the question and waits for the user's
next message; `common.questions_ask` remains available for structured questions
and pauses through its typed `AwaitingUser` result. Neither path implies `done`.

The `message` on `tool` concisely states the relevant finding from a prior
result, when available, the decision it supports and why the selected call is
next. An initial read states the question it will answer. `done` answers the user
or reports the delivered outcome and available checks; `blocked` and `needs_input` name the
unfinished outcome and obstacle or question. Message prose does not expose
private reasoning or grant execution authority. The selected calls, tool
results and read-back remain the only evidence for operations. Unknown effects
are never retried automatically.

## Parsing, history and recovery

`ModelProtocolWire` owns parser, strict schema, canonical writer and format
repair for both `json_schema` and `json_object`. The kernel validates action/call
shape again before acceptance. `continue` is invalid, including with a nonblank
message. It receives bounded format repair and cannot become accepted history, visible progress or an implicit
`done`. Call acceptance retains runtime IDs and exact tool policy as before.

The strict response schema and parser use the same explicit local
`ModelProtocolCallContext` for batch membership. `tool_calls` allows either up to
32 authorized independent reads or one other call; mixed read/mutation batches
and multiple mutations are excluded by the schema before generation. Every
argument contract occurs once, including when both array alternatives are needed.
Missing/incomplete authority cannot produce a schema. CLI and Office request,
budget and inspector paths supply the captured catalog authority. `json_object`
still relies on the same parser checks; action/call correlation remains validated
by parser/kernel in both modes. Provider enforcement of the schema requires its
own evidence and never replaces local validation.

Accepted assistant history is marked `ResponseProtocolVersion=7`. Full-history
preflight rejects incompatible or malformed records before dispatch. Existing
v6 data stays intact and requires explicit reset/new chat. Windows/Office,
WebView2 and target-model qualification remain open.
