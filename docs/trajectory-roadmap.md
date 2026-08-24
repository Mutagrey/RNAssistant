# Trajectory and artifact roadmap

## Current guarantees

- A chat's append-only `*.events.jsonl` stream is its only durable source of truth. `ChatSession`, model history, diagnostics, and UI state are replayed projections.
- Immutable payloads and artifact bodies are stored once in the shared SHA-256 `chat-blobs` CAS. Events keep verified references rather than competing body copies.
- History protection is explicit and disabled by default. HMAC-SHA256 can authenticate the event chain, while authenticated AES-256-CBC + HMAC-SHA256 encryption protects event data and committed CAS using either the API key or a separate DPAPI-protected custom secret.
- Every real HTML workspace change creates an immutable workspace artifact with a parent id. Undo and redo only move `ActiveHtmlArtifactId` to an existing revision, so navigation survives replay without creating duplicate revisions.
- VBA is deliberately not a chat artifact. The Office document is the authority for current live VBA state; a document-scoped append-only journal records prepared/terminal mutations, CAS-backed before/intended source, rollback backups, correlation, and deterministic recovery evidence.
- Chat replay, fork, prune, HTML undo, and HTML redo must never replay or mutate VBA or any other external Office state.

## Known gaps

- Diagnostics now provides a repository-wide CAS health report and fail-closed orphan collector. Retention/pruning, re-keying, and redacted export lifecycles are still missing.
- VBA package install/remove still emits per-component rollback backups rather than one multi-module transaction manifest.
- VBA recovery state is canonical, but Diagnostics does not yet expose paged mutation history or before/after diffs.
- Diagnostics expose a bounded recent tail; there is no reusable paged query API for complete turn, step, tool, artifact, and failure trajectories.

## Next implementation order

### P0 — deterministic recovery and storage health

- [x] Add a document-scoped append-only VBA mutation journal. A prepared record contains host/document identity, module name/type, existence, before hash and CAS reference, intended after hash and CAS reference, backup id, and chat/run/turn/step/tool-call correlation. Terminal records classify `committed`, `not_applied`, `rolled_back`, `failed`, or `unknown`.
- [x] Replace inline VBA backup source with CAS references and derive the backup list from the VBA journal. The journal is document-scoped rather than chat-owned, so chat fork/prune semantics cannot change the live Office project.
- [x] Reconcile interrupted VBA writes on the next safe VBA access: compare the live module with recorded before/after hashes, append `committed`, `not_applied`, or `unknown`, and never auto-retry or auto-restore an external mutation.
- [x] Make VBA restore a journaled transaction: validate the live guard, snapshot current source, persist the prepared record, write, read back, then append the terminal outcome. Preserve explicit confirmation.
- [x] Make HTML redo branch-aware. Redo without an id is valid only with exactly one child; multiple children return an explicit branch-choice result. Expose child revision metadata in the bridge while keeping bodies lazy and CAS-backed.
- [x] Add a CAS health/GC service that scans all validated event streams and document-scoped VBA journals, reports missing/corrupt/orphaned blobs, and removes only proven unreachable blobs under the maintenance gate. Corrupt, unreadable, misplaced, or incomplete sources make deletion fail closed.
- [x] Add harness coverage for interrupted VBA prepare/write reconciliation, journal tail recovery/corruption, CAS-backed projections, correlation, and history protection.
- [x] Add explicit blob-before-event crash injection and verify that fail-closed GC preserves candidates when a chat tail or VBA journal is invalid.
- [ ] Complete HTML branch recovery fixtures. VBA/COM behavior also requires Windows x64 + Office x64 + VS 2022 smoke tests.

### P1 — trajectory queries and operator UX

- [ ] Introduce `ITrajectoryQuery`: cursor pagination, FTS, and filters for sequence range, event type, run, turn, step, tool call, artifact, status, and `current` / `shadowed` / `log-only`. The event stream remains authoritative; any index must be disposable and rebuildable.
- [ ] Add derived views for model replay, tool execution, artifact lineage, confirmation pauses, failure/retry history, and per-turn timing/token/cost usage. Every projection row must retain `sourceEventSeqs` and source event ids.
- [ ] Upgrade Diagnostics from the fixed recent tail to pagination, correlation navigation, artifact lineage, and VBA before/after diff with an explicit restore action.
- [ ] Add an export bundle containing selected event records, a manifest of referenced CAS payloads, and integrity hashes for offline trajectory analysis and regression fixtures.
- [ ] Define persistence seams (`ISessionPersistence`, `IBlobStore`, `ITrajectoryQuery`) before considering an optional SQLite backend. SQLite is useful only for query scale; it must preserve append-only/CAS semantics and must not become a second durable truth beside JSONL.

### P2 — lifecycle, evaluation, and tamper resistance

- [ ] Add retention/pruning policies for chats, model payloads, attachments, artifacts, VBA snapshots, and diagnostic exports on top of the reference-aware collector; add configurable redaction before share/export.
- [ ] Add reproducible replay fixtures and trajectory evaluations for malformed Agent output, confirmation continuation, tool failures, HTML branch navigation, VBA stale guards, and crash recovery.
- [ ] Surface aggregate latency, tokens, cost, model failures, format repairs, tool outcomes, uncertain effects, and restore outcomes from the same canonical journal without mixing telemetry into model replay.
- [x] Add optional HMAC-SHA256 event authentication and optional authenticated encryption-at-rest for event data and committed CAS; keep both disabled by default and expose API/custom-secret key selection in Settings.
- [ ] Add explicit re-key/decrypt-for-export operations so protected history can change keys or become a shareable redacted bundle without clearing canonical data.
- [ ] Journal multi-module VBA package operations as one transaction manifest with per-component before/after state and best-effort rollback outcomes.

## Non-negotiable invariants

- No mutable snapshot, summary, redo stack, or query index may compete with its canonical event/journal stream.
- Derived checkpoints and indexes must be discardable and reproducible from validated records plus CAS.
- External Office mutations are never automatically replayed from chat history.
- An uncertain mutation is inspected or explicitly restored before retrying.
- UserForm Designer/FRX state remains outside the current VBA source protocol until it has a complete export/import and verification design.
