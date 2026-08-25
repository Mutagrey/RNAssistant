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

Cold projection reads validate and replay the complete stream. Within one running process, up to 16 recently used canonical projection roots are cached in memory (maximum about 4 million characters each and 16 million in total). A same-head read verifies the exact final record at its byte offset. Append-only growth verifies the cached boundary and hash chain, then reads and applies only the new suffix. Shrink, replacement, malformed/incomplete tail, protection mismatch, or changed metadata invalidates the entry and falls back to a complete replay. This cache is disposable, is never written as another index/snapshot, and is not used by trajectory export, CAS reachability, or other operations that require a fresh complete stream scan.

Chat-list/header reads use a separate streaming reducer: a cold read still validates every event and hash, but retains only header metadata, message ids/protocol flags, the last run, and minimal active-HTML artifact references/counts. It does not build `ChatSession`, message/tool bodies, context, or the general artifact projection. Normal HTML counts come from artifact metadata without CAS hydration; only missing/invalid legacy metadata triggers a read of the active workspace body. Up to 64 reducer states are cached in memory (maximum about 512 thousand characters each and 4 million in total); a verified byte-offset boundary lets append-only growth replay only the new suffix. The cache is disposable and never becomes a durable header index.

## Event contract

Every `SessionEvent` contains `SchemaVersion`, `SessionId`, contiguous `Sequence`, `EventId`, UTC time, type, optional run/turn/step correlation, `PreviousHash`, hash algorithm/key metadata, `Hash`, JSON data or encrypted data, and an optional content-addressed payload reference.

- `session.created` seeds the initial projection.
- `session.forked` seeds an independent projection and records parent session id, source revision, and boundary message id.
- `session.commit` applies typed operations such as user/assistant messages, tool calls/results, tool execution boundaries, run state, metadata, context, active references, and artifact revisions.
- `turn.started` / `turn.ended` delimit one logical user turn. `TurnId` remains stable when a confirmation pause resumes under a new runtime `RunId`.
- `step.started` / `step.ended` delimit one model request. Startup recovery appends a synthetic interrupted end for a request that never reached a terminal event.
- `llm.request` records the exact final JSON body after messages, attachment parts, system instructions, tool schemas, and response schema are materialized. Persistence succeeds before HTTP dispatch.
- `llm.response` records the raw non-stream response body or the normalized complete streaming result.
- `assistant.chunk` records ordered provider SSE data frames in bounded JSON-array batches (up to roughly 64 KiB or one second while frames arrive). The event stores first frame index, count, completion marker, and a CAS payload. Batches enter one bounded ordered queue per session (up to 16 pending writes), so the SSE reader normally does not wait for `Flush(true)`; saturation applies backpressure instead of allowing unbounded memory growth.
- `llm.failure` records endpoint/status/failure metadata and any bounded provider error body.
- `agent.response.rejected` keeps malformed Agent output for diagnosis without adding it to model replay or visible chat history.

The default SHA-256 hash-chain detects accidental edits, truncation in the middle of the log, and reordered records. Optional HMAC-SHA256 prevents recomputing valid edited records without the selected secret. Neither mode prevents deletion of an unanchored final suffix.

## Blobs and artifacts

Large immutable content is stored once by SHA-256 in `chat-blobs`; the event stream keeps hash, byte length, and content type.

An artifact body that was just stored or successfully hydrated keeps a transient trusted `(text, SHA-256, byte length)` tuple. Later metadata-only saves reuse the existing canonical blob without encoding, hashing, decrypting, or rereading it. A copied body with only a known reference still compares one UTF-8 hash and can avoid reading the existing blob. A missing or obviously truncated reference falls back to normal verified `StoreText`; explicit reads and CAS health scans continue to authenticate the complete content.

- committed attachment bytes and extracted text use CAS references;
- HTML workspace, chart, plan, compaction, and other artifact bodies use the same CAS;
- exact model request/response payloads use the same CAS;
- equal bytes across chats or revisions deduplicate automatically.

When history encryption is enabled, committed CAS files contain authenticated ciphertext while their references retain the plaintext SHA-256 and byte length for deterministic identity and post-decryption verification.

Artifact metadata and lineage remain in the session stream. HTML undo follows the active artifact's parent. Redo is derived only from its direct children: one child is deterministic, while multiple children require an explicit artifact id. The bridge exposes child revision/count metadata without loading their CAS bodies, and no mutable redo stack is stored. Chart UI data is derived from a chart artifact. Context checkpoints are derived from compaction artifacts. These values are not persisted again as competing state.

HTML recovery is derived from the same validated artifact graph and CAS on every replay. If the active artifact metadata is missing, or its body is unavailable or invalid, the editable workspace projection is empty and all HTML mutations fail closed. Ordinary chat commits remain allowed, retain the active artifact id, and cannot create an empty replacement revision. Recovery candidates contain metadata only; selecting one verifies and parses that exact CAS body before moving `ActiveHtmlArtifactId`. If only an ancestor is broken, the readable active revision remains editable and undo history stops at the damaged edge with a degraded warning.

## Optional history protection

Settings → Diagnostics → History protection controls two independent features. Both are off by default: events use the ordinary SHA-256 chain and history remains plaintext.

- HMAC-SHA256 authenticates the complete canonical event envelope, including data/ciphertext and CAS references. HMAC does not encrypt content.
- Authenticated encryption uses AES-256-CBC with HMAC-SHA256. It encrypts every chat and VBA journal event `Data` value and every committed `chat-blobs` payload; event type, sequence, time, correlation ids, hashes, key id, CAS plaintext hash/length, and content equality remain visible.
- The selected key source is the API key by default or a separate custom secret. Both secrets are stored with DPAPI CurrentUser and never enter settings, events, diagnostics, or exports.
- Keys are derived with PBKDF2-SHA256 and a portable installation salt, then domain-separated for encryption, ciphertext authentication, and event-chain HMAC.
- Changing the enabled modes, key source, or effective key is rejected while event streams or CAS blobs exist. Clear Chats/Data first. In particular, rotating an API key used for protection requires clearing or a future explicit re-key operation.
- For ordinary sharing, use the disposable trajectory export and keep its default metadata redaction. Canonical protected history can still be transferred with a custom secret plus `history-protection.salt`, communicating the secret separately; never share an API key.

Current history encryption does not cover transient attachment staging, settings, runtime logs, or WebView data. Committed attachments and VBA snapshots are protected after they enter the shared CAS; document-scoped VBA journal data is protected without making it chat-owned.

## Durability and recovery

- A document-scoped cross-process lock and event-tail compare-and-swap prevent stale writers.
- Each append is flushed to stable storage before returning.
- The final materialized model request remains a synchronous durability barrier before network dispatch. Model response/failure is another barrier: every earlier queued `assistant.chunk` batch for that session is durable before the terminal event is appended.
- Adjacent lifecycle and trace/commit records remain separate hash-linked lines but share one locked durable append batch.
- A parse-incomplete final JSONL row is ignored; a valid but unterminated final row remains readable. The next successful append normalizes either tail before adding another record.
- A cold load validates sequence continuity and the complete hash-chain. A warm cached load reuses only its previously validated prefix, revalidates the byte boundary/head, and validates every appended event; a non-append change forces complete validation. A corrupt stream is not projected or listed.
- Startup recovery marks tool effect as unknown only when the stream contains `tool.execution.started` without the matching `tool.execution.finished` for that run.
- Recovery closes open model steps with `step.ended { Status: "interrupted", Synthetic: true }`, then closes the logical turn through the normal persisted run transition.
- Missing or corrupt CAS content leaves its metadata visible but is never hydrated as trusted content.
- HTML branch recovery never guesses or auto-replays content: an unreadable active revision requires explicit selection of a verified revision; an unreadable ancestor only truncates derived navigation.
- CAS health scans every validated chat stream and VBA journal, then verifies referenced bodies and reports missing, corrupt, and orphaned blobs. Garbage collection rebuilds this reachability under the maintenance gate and deletes only exact canonical orphan files. Any invalid, unreadable, misplaced, or incomplete source blocks deletion; see [cas-maintenance.md](cas-maintenance.md).
- VBA preparations left without a terminal record are compared with live module state on the next safe VBA access and closed as `committed`, `not_applied`, or `unknown`; recovery never replays an Office mutation.

## Inspection

Settings → Diagnostics → Trajectory queries the same stream through disposable `ITrajectoryQuery`. Raw results use exclusive sequence cursors, newest-first pages, tokenized text search and filters for sequence, event type, run/turn/step, tool call, artifact, status and reconstructed `current`/`shadowed`/`log-only` visibility. Snapshot-paged derived views correlate model replay, tools, artifact lineage, confirmation pauses, failures/retries and per-turn timing/usage; every row carries its complete source event sequences and ids. Event metadata and state operations are inline; model payloads and streaming-frame batches are fetched lazily by event id and shown as a bounded preview. Selected chat rows can be exported as a bounded ZIP with metadata-only default, optional credential-field redaction, or explicit full decrypted data/CAS; protection keys never enter it. CAS storage audits all retained chat/VBA references and exposes an explicitly confirmed orphan cleanup. The bridge never includes API keys, history secrets or authorization headers. See [trajectory-query.md](trajectory-query.md) and [trajectory-export.md](trajectory-export.md).

The prioritized follow-up work for trajectory queries, HTML branches, CAS lifecycle, and document-scoped VBA recovery is tracked in [trajectory-roadmap.md](trajectory-roadmap.md).

## Format policy

`ChatSession.CurrentFormatVersion` is 5 and `SessionEvent.CurrentSchemaVersion` is 2. Unsupported event schemas and old snapshot files are refused rather than guessed or migrated. During development, use **Clear Chats/Data** once after upgrading.
