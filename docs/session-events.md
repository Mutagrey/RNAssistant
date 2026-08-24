# Session event stream

## Decision

RNAssistant uses one append-only event stream per chat as its durable source of truth. There is no mutable chat snapshot, summary index, separate HTML-body store, or migration from the previous v1-v3 snapshot formats.

```text
%AppData%/RNAssistant/
  chats/<document-hash>/<session-hash>.events.jsonl
  chat-blobs/<sha-prefix>/<sha256>.blob
  attachments/staging/...
  history-protection.salt
  history-secret.bin
```

`ChatSession` is an in-memory projection rebuilt by replay. Its `Revision` is the last durable event sequence, not an independently stored counter.

## Event contract

Every `SessionEvent` contains `SchemaVersion`, `SessionId`, contiguous `Sequence`, `EventId`, UTC time, type, optional run/turn/step correlation, `PreviousHash`, hash algorithm/key metadata, `Hash`, JSON data or encrypted data, and an optional content-addressed payload reference.

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

The default SHA-256 hash-chain detects accidental edits, truncation in the middle of the log, and reordered records. Optional HMAC-SHA256 prevents recomputing valid edited records without the selected secret. Neither mode prevents deletion of an unanchored final suffix.

## Blobs and artifacts

Large immutable content is stored once by SHA-256 in `chat-blobs`; the event stream keeps hash, byte length, and content type.

- committed attachment bytes and extracted text use CAS references;
- HTML workspace, chart, plan, compaction, and other artifact bodies use the same CAS;
- exact model request/response payloads use the same CAS;
- equal bytes across chats or revisions deduplicate automatically.

When history encryption is enabled, committed CAS files contain authenticated ciphertext while their references retain the plaintext SHA-256 and byte length for deterministic identity and post-decryption verification.

Artifact metadata and lineage remain in the session stream. HTML undo follows the active artifact's parent. Redo is derived only from its direct children: one child is deterministic, while multiple children require an explicit artifact id. The bridge exposes child revision/count metadata without loading their CAS bodies, and no mutable redo stack is stored. Chart UI data is derived from a chart artifact. Context checkpoints are derived from compaction artifacts. These values are not persisted again as competing state.

## Optional history protection

Settings → Diagnostics → History protection controls two independent features. Both are off by default: events use the ordinary SHA-256 chain and history remains plaintext.

- HMAC-SHA256 authenticates the complete canonical event envelope, including data/ciphertext and CAS references. HMAC does not encrypt content.
- Authenticated encryption uses AES-256-CBC with HMAC-SHA256. It encrypts every chat and VBA journal event `Data` value and every committed `chat-blobs` payload; event type, sequence, time, correlation ids, hashes, key id, CAS plaintext hash/length, and content equality remain visible.
- The selected key source is the API key by default or a separate custom secret. Both secrets are stored with DPAPI CurrentUser and never enter settings, events, diagnostics, or exports.
- Keys are derived with PBKDF2-SHA256 and a portable installation salt, then domain-separated for encryption, ciphertext authentication, and event-chain HMAC.
- Changing the enabled modes, key source, or effective key is rejected while event streams or CAS blobs exist. Clear Chats/Data first. In particular, rotating an API key used for protection requires clearing or a future explicit re-key operation.
- For sharing protected history, use a custom secret and transfer the event/CAS data plus `history-protection.salt`; communicate the secret separately. Never share an API key for this purpose.

Current history encryption does not cover transient attachment staging, settings, runtime logs, or WebView data. Committed attachments and VBA snapshots are protected after they enter the shared CAS; document-scoped VBA journal data is protected without making it chat-owned.

## Durability and recovery

- A document-scoped cross-process lock and event-tail compare-and-swap prevent stale writers.
- Each append is flushed to stable storage before returning.
- A parse-incomplete final JSONL row is ignored; the next successful append rewrites only the validated prefix first.
- Sequence continuity and the hash-chain are validated on every load. A corrupt stream is not projected or listed.
- Startup recovery marks tool effect as unknown only when the stream contains `tool.execution.started` without the matching `tool.execution.finished` for that run.
- Recovery closes open model steps with `step.ended { Status: "interrupted", Synthetic: true }`, then closes the logical turn through the normal persisted run transition.
- Missing or corrupt CAS content leaves its metadata visible but is never hydrated as trusted content.
- CAS health scans every validated chat stream and VBA journal, then verifies referenced bodies and reports missing, corrupt, and orphaned blobs. Garbage collection rebuilds this reachability under the maintenance gate and deletes only exact canonical orphan files. Any invalid, unreadable, misplaced, or incomplete source blocks deletion; see [cas-maintenance.md](cas-maintenance.md).
- VBA preparations left without a terminal record are compared with live module state on the next safe VBA access and closed as `committed`, `not_applied`, or `unknown`; recovery never replays an Office mutation.

## Inspection

Settings → Diagnostics → Trajectory queries the same stream through disposable `ITrajectoryQuery`. Raw results use exclusive sequence cursors, newest-first pages, tokenized text search and filters for sequence, event type, run/turn/step, tool call, artifact, status and reconstructed `current`/`shadowed`/`log-only` visibility. Snapshot-paged derived views correlate model replay, tools, artifact lineage, confirmation pauses, failures/retries and per-turn timing/usage; every row carries its complete source event sequences and ids. Event metadata and state operations are inline; model payloads and streaming-frame batches are fetched lazily by event id and shown as a bounded preview. CAS storage audits all retained chat/VBA references and exposes an explicitly confirmed orphan cleanup. The bridge never includes API keys, history secrets or authorization headers. See [trajectory-query.md](trajectory-query.md).

The prioritized follow-up work for trajectory queries, HTML branches, CAS lifecycle, and document-scoped VBA recovery is tracked in [trajectory-roadmap.md](trajectory-roadmap.md).

## Format policy

`ChatSession.CurrentFormatVersion` is 5 and `SessionEvent.CurrentSchemaVersion` is 2. Unsupported event schemas and old snapshot files are refused rather than guessed or migrated. During development, use **Clear Chats/Data** once after upgrading.
