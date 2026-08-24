# Trajectory and artifact roadmap

## Current guarantees

- A chat's append-only `*.events.jsonl` stream is its only durable source of truth. `ChatSession`, model history, diagnostics, and UI state are replayed projections.
- Immutable payloads and artifact bodies are stored once in the shared SHA-256 `chat-blobs` CAS. Events keep verified references rather than competing body copies.
- Every real HTML workspace change creates an immutable workspace artifact with a parent id. Undo and redo only move `ActiveHtmlArtifactId` to an existing revision, so navigation survives replay without creating duplicate revisions.
- VBA is deliberately not a chat artifact. The Office document is the authority for current live VBA state; RNAssistant adds a runtime-bound stale-state guard, confirmation, a pre-mutation rollback backup, and post-write read-back verification.
- Chat replay, fork, prune, HTML undo, and HTML redo must never replay or mutate VBA or any other external Office state.

## Known gaps

- HTML history is a graph after undo followed by a new edit. When one revision has multiple children, redo without an explicit artifact id currently selects the newest child; the UI does not expose branch choice.
- Missing or corrupt CAS content fails closed, but there is no repository-wide health report or reachability garbage collector. A crash after storing a blob but before appending its reference can leave a harmless orphan.
- VBA backups are separate document-scoped JSON files with inline source. They are not linked to a durable VBA mutation record or deduplicated in CAS.
- A process/COM crash between `tool.execution.started` and `tool.execution.finished` is correctly classified as an unknown effect, but runtime cannot yet compare live VBA with the intended before/after states to distinguish applied, not applied, and divergent outcomes.
- Diagnostics expose a bounded recent tail; there is no reusable paged query API for complete turn, step, tool, artifact, and failure trajectories.

## Next implementation order

### P0 — deterministic recovery and storage health

- [ ] Add a document-scoped append-only VBA mutation journal. A prepared record must contain host/document identity, module name/type, existence, before hash and CAS reference, intended after hash and CAS reference, backup id, and chat/run/turn/step/tool-call correlation. Terminal records must classify `committed`, `not_applied`, `rolled_back`, `failed`, or `unknown`.
- [ ] Replace inline VBA backup source with CAS references and derive the backup list from the VBA journal. Keep this journal document-scoped rather than chat-owned so chat fork/prune semantics cannot change the live Office project.
- [ ] Reconcile interrupted VBA writes on the next safe document attach: compare the live module with the recorded before/after hashes. Report `committed` or `not_applied` when exact, otherwise `unknown`; never auto-retry or auto-restore an external mutation.
- [ ] Make VBA restore a journaled transaction: validate the live guard, snapshot current source, persist the prepared record, write, read back, then append the terminal outcome. Preserve explicit confirmation.
- [ ] Make HTML redo branch-aware. Redo without an id is valid only with exactly one child; multiple children return an explicit branch-choice result. Expose child revision metadata in the bridge while keeping bodies lazy and CAS-backed.
- [ ] Add a CAS health/GC service that scans all validated event streams and document-scoped VBA journals, reports missing/corrupt/orphaned blobs, and removes only proven unreachable blobs under the maintenance gate. Corrupt or unreadable journals must make deletion fail closed.
- [ ] Add harness crash-injection coverage for blob-before-event, HTML branching, interrupted VBA prepare/write/verify, and deterministic reconciliation. VBA/COM behavior also requires Windows x64 + Office x64 + VS 2022 smoke tests.

### P1 — trajectory queries and operator UX

- [ ] Introduce a read-only paged trajectory query boundary with filters for sequence range, event type, run, turn, step, tool call, artifact, status, and accepted versus log-only records. The event stream remains authoritative; any index must be disposable and rebuildable.
- [ ] Add derived views for model replay, tool execution, artifact lineage, confirmation pauses, failure/retry history, and per-turn timing/token usage. Every row must retain its source event sequence/id.
- [ ] Upgrade Diagnostics from the fixed recent tail to pagination, correlation navigation, artifact lineage, and VBA before/after diff with an explicit restore action.
- [ ] Add an export bundle containing selected event records, a manifest of referenced CAS payloads, and integrity hashes for offline trajectory analysis and regression fixtures.
- [ ] Define persistence seams (`ISessionEventStore`, `IBlobStore`, `ITrajectoryQuery`) before considering SQLite. A SQLite backend is useful only for query scale; it must preserve append-only/CAS semantics and must not become a second durable truth beside JSONL.

### P2 — lifecycle, evaluation, and tamper resistance

- [ ] Add reference-aware retention policies for chats, model payloads, attachments, artifacts, VBA snapshots, and diagnostic exports.
- [ ] Add reproducible trajectory evaluations for malformed Agent output, confirmation continuation, tool failures, HTML branch navigation, VBA stale guards, and crash recovery.
- [ ] Surface aggregate latency, tokens, model failures, format repairs, tool outcomes, uncertain effects, and restore outcomes without mixing telemetry into model replay.
- [ ] Add optional authenticated integrity (for example, keyed hashes) and encryption-at-rest for sensitive local payloads. The current SHA-256 chain detects accidental corruption, not malicious rewriting.
- [ ] Journal multi-module VBA package operations as one transaction manifest with per-component before/after state and best-effort rollback outcomes.

## Non-negotiable invariants

- No mutable snapshot, summary, redo stack, or query index may compete with its canonical event/journal stream.
- Derived checkpoints and indexes must be discardable and reproducible from validated records plus CAS.
- External Office mutations are never automatically replayed from chat history.
- An uncertain mutation is inspected or explicitly restored before retrying.
- UserForm Designer/FRX state remains outside the current VBA source protocol until it has a complete export/import and verification design.
