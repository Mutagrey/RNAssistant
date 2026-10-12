# Conversation Response v6

Status: **historical**. The active contract is [v7](CONVERSATION_RESPONSE_V7.md).
This document records response protocol `6` and prompt schema `39`.
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

After an accepted `continue`, the kernel marks the next request for transient
`RUNTIME_FEEDBACK`: the comment was recorded and no tools were called in that
step. The shared context compiler places this feedback after history using the
effective instruction role, includes it in budget/inspector accounting and
preserves it in the frozen format-repair request. It is not a tool result or a
durable chat message. A tool step or new user input clears the signal; checkpoint
continuation reconstructs it from the kernel's accepted facts, independently of
the compacted model window. All five actions and the three-step limit remain
unchanged; feedback neither forces a call nor certifies completion.

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

Accepted assistant history is marked `ResponseProtocolVersion=6`. Full-history
preflight rejects incompatible or malformed records before dispatch. Existing
v5 data stays intact and requires explicit reset/new chat. Windows/Office,
WebView2 and target-model qualification remain open.
