# Conversation Response v7

Status: **historical**. The active contract on `main` is [v8](CONVERSATION_RESPONSE_V8.md).
Response protocol is `7`; the v7-era built-in prompt schema was `44`.
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
| `done` | Empty | Model assesses the requested answer or work as complete; accepted completion ends the turn. |
| `blocked` | Empty | End the turn with unfinished work and a concrete blocker. |
| `needs_input` | Empty | End the turn with a material question for the user. |

`blocked` and `needs_input` require a nonblank message. `done`, `blocked` and
`needs_input` are distinct accepted model decisions. They do not prove effects or semantic completion. Runtime
`RunLifecycle.Completed` means the model turn ended; its reason preserves the
model decision. `RunViewState` and the result card show actual read/write
evidence separately.

An accepted response without calls ends the model turn. The runtime requests the
next model step after tool results, new user input, or failure of explicit workspace
acceptance criteria, never merely to elaborate on a progress message. For requested
file/count/browser criteria, a valid `done` can be rejected before acceptance;
the same kernel continues with concrete feedback under its original limits.
This does not inspect prose or task-list status; see the
[workspace acceptance contract](../conversation-protocol.md). `needs_input` preserves the question and waits for the user's
next message; `common.questions_ask` remains available for structured questions
and pauses through its typed `AwaitingUser` result. Neither path implies `done`.
The question tool's accepted call has a terminal result before this pause, so an
answer/restart retains a closed call/result exchange, including native tool history.
`needs_input` remains a text decision: its nonblank message must contain the actual
question. It does not manufacture structured question cards from a promise to ask.

Choose the action from remaining work before composing the message. Feasible
authorized work continues through the next needed call. If the requested answer
or code needs no tool, deliver it in the current `done` message. A promise to
prepare it later is not completion and cannot schedule another request.
Unfinished work alone is not a blocker: `blocked` requires a concrete obstacle,
and `needs_input` requires an indispensable user answer. These decision rules are
shared by defaults, schema, format repair and the workspace prompt.

The `message` on `tool` may be empty. Its frequency is independent of tool calls
and Task List note updates. The first tool response of a substantial task briefly
states the approach and includes the next concrete call in that same response;
direct answers need no progress preamble. Later updates report a new relevant finding, decision, contradiction,
blocker or meaningful stage change, with its basis/uncertainty and next action
when useful. Routine skill/schema loading or continuation of an explained action
does not require another message. Rephrasing prior progress adds no information.
`ModelProtocolWire.MessageGuidance` is shared by defaults, schema and format repair.
There is no extra model call for narration, timer-driven narration or prose
classifier. Tool activities retain their identities and remain available in
the transcript; UI disclosures may fold completed silent work.
`done` answers the user
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
Repair instructions preserve the unfinished task: a missing call must be supplied
or the complete answer delivered, rather than merely relabeling a promise `done`.
These are model instructions, not a semantic validator. A syntactically valid
premature `done` still ends the turn unless explicit workspace postconditions
reject it. Its wording never triggers an automatic retry; the wire parser and
bounded format repair remain independent of that application-owned check.

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
