# Trajectory query projection

`ITrajectoryQuery` is a read-only, disposable projection over a fully validated session event stream. The implementation receives canonical `SessionEvent` records from `ChatStore`, builds query metadata in memory, returns one page, and discards it. It never writes an index or another history file.

## Query contract

Results are ordered newest first. `pageSize` defaults to 100 and is capped at 200. `nextCursor` is an exclusive sequence cursor (`seq:<n>`), so later appends do not shift older pages.

Filters compose with AND:

- inclusive `minSequence` / `maxSequence`;
- exact event types;
- run, turn and step correlation;
- tool-call id, artifact id and status extracted from typed event data;
- `current`, `shadowed` or `log-only` visibility;
- case-insensitive tokenized full-text search over event metadata and materialized event data.

CAS payload bodies are intentionally excluded from full-text search and remain lazy. Their hash, content type and size metadata remain searchable/visible, and Diagnostics loads a bounded body preview only by explicit event id.

Every returned raw-event row retains `sourceEventSeqs` and `sourceEventIds`.

## Derived views

The same `ITrajectoryQuery` rebuilds six correlated, read-only projections:

- `model-replay`: model step boundaries, request/response/chunk payload references, format repairs, failures, attempts and usage;
- `tool-execution`: a tool call from protocol record through running/waiting/terminal states;
- `artifact-lineage`: immutable artifact revision metadata and `parentArtifactId` links;
- `confirmation-pauses`: waiting interval and its pending/resolved/failed/cancelled outcome;
- `failure-retries`: model, tool and terminal-turn failures with retry counts;
- `turn-usage`: lifecycle timing, model/tool/confirmation/failure counts, actual and estimated tokens, and provider-reported USD cost.

Every row retains the complete contributing `sourceEventSeqs` and `sourceEventIds`. Derived pagination uses `view:<view>:<snapshotSequence>:<offset>`: all pages use the same upper event-stream boundary even if new events are appended. A changed view or filter starts a fresh cursor.

Diagnostics turns row correlations into navigation rather than another index: run/turn/step/tool-call filters reopen the relevant chat projection, artifact and parent ids open lineage, and a source-event action opens the bounded raw sequence range. Document-scoped VBA mutation rows use their recorded `SessionId` to navigate back to the originating chat without treating VBA as a chat artifact.

New `llm.response` events keep compact actual token usage inline beside the immutable CAS response reference. Older streams fall back to token usage in replayable assistant-message operations. Cost is shown only when the provider persisted it in `usage`; RNAssistant does not recalculate historical cost from mutable current price tables.

## Visibility projection

- `current`: the session seed, or a `session.commit` that still supplies at least one final projection target.
- `shadowed`: a `session.commit` whose message, artifact, order, metadata, context, run or active-reference targets were all superseded by later commits.
- `log-only`: lifecycle/model trace/rejection events that are intentionally excluded from `ChatSession` replay.

A commit containing multiple operations is `current` while any one of its targets remains current. Visibility is reconstructed from the complete stream on each query; it is not persisted into JSONL.
