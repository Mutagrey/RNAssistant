# Session event stream

## Decision

RNAssistant uses one append-only event stream per chat as its durable source of truth. There is no mutable chat snapshot, summary index, separate HTML-body store, or migration from the previous v1-v3 snapshot formats.

```text
%AppData%/RNAssistant/
  chats/<document-hash>/<session-hash>.events.jsonl
  chat-blobs/<sha-prefix>/<sha256>.blob
  attachments/staging/...
```

`ChatSession` is an in-memory projection rebuilt by replay. Its `Revision` is the last durable event sequence, not an independently stored counter.

## Event contract

Every `SessionEvent` contains `SchemaVersion`, `SessionId`, contiguous `Sequence`, `EventId`, UTC time, type, optional run/turn/step correlation, `PreviousHash`, `Hash`, JSON data, and an optional content-addressed payload reference.

- `session.created` seeds the initial projection.
- `session.forked` seeds an independent projection and records parent session id, source revision, and boundary message id.
- `session.commit` applies typed operations such as user/assistant messages, tool calls/results, tool execution boundaries, run state, metadata, context, active references, and artifact revisions.
- `turn.started` / `turn.ended` delimit one logical user turn. `TurnId` remains stable when a confirmation pause resumes under a new runtime `RunId`.
- `step.started` / `step.ended` delimit one model request. Startup recovery appends a synthetic interrupted end for a request that never reached a terminal event.
- `llm.request` records the exact final JSON body after messages, attachment parts, system instructions, tool schemas, and response schema are materialized. Persistence succeeds before HTTP dispatch.
- `llm.response` records the raw non-stream response body or the normalized complete streaming result.
- `assistant.chunk` records ordered provider SSE data frames in bounded JSON-array batches (up to roughly 64 KiB or one second while frames arrive). The event stores first frame index, count, completion marker, and a CAS payload; batching avoids one durable flush per token.
- `llm.failure` records endpoint/status/failure metadata and any bounded provider error body.
- `agent.response.rejected` keeps malformed Agent output for diagnosis without adding it to model replay or visible chat history.

The hash-chain detects accidental edits, truncation in the middle of the log, and reordered records. It is an integrity check, not authentication: it is not keyed.

## Blobs and artifacts

Large immutable content is stored once by SHA-256 in `chat-blobs`; the event stream keeps hash, byte length, and content type.

- committed attachment bytes and extracted text use CAS references;
- HTML workspace, chart, plan, compaction, and other artifact bodies use the same CAS;
- exact model request/response payloads use the same CAS;
- equal bytes across chats or revisions deduplicate automatically.

Artifact metadata and lineage remain in the session stream. HTML undo/redo is derived from the active artifact and its parent/child chain. Chart UI data is derived from a chart artifact. Context checkpoints are derived from compaction artifacts. These values are not persisted again as competing state.

## Durability and recovery

- A document-scoped cross-process lock and event-tail compare-and-swap prevent stale writers.
- Each append is flushed to stable storage before returning.
- A parse-incomplete final JSONL row is ignored; the next successful append rewrites only the validated prefix first.
- Sequence continuity and the hash-chain are validated on every load. A corrupt stream is not projected or listed.
- Startup recovery marks tool effect as unknown only when the stream contains `tool.execution.started` without the matching `tool.execution.finished` for that run.
- Recovery closes open model steps with `step.ended { Status: "interrupted", Synthetic: true }`, then closes the logical turn through the normal persisted run transition.
- Missing or corrupt CAS content leaves its metadata visible but is never hydrated as trusted content.

## Inspection

Settings → Diagnostics → Trajectory reads the last 500 events from the same stream. It shows run, turn, and step correlation. Event metadata and state operations are inline; model payloads and streaming-frame batches are fetched lazily by event id and shown as a bounded preview. The bridge never includes API keys or authorization headers.

The prioritized follow-up work for trajectory queries, HTML branches, CAS lifecycle, and document-scoped VBA recovery is tracked in [trajectory-roadmap.md](trajectory-roadmap.md).

## Format policy

`ChatSession.CurrentFormatVersion` is 4 and `SessionEvent.CurrentSchemaVersion` is 1. Unsupported event schemas and old snapshot files are refused rather than guessed or migrated. During development, use **Clear Chats/Data** once after upgrading.
