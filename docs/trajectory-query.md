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

Every returned raw-event row retains `sourceEventSeqs` and `sourceEventIds`. Later derived views must preserve the complete contributing set rather than inventing correlations.

## Visibility projection

- `current`: the session seed, or a `session.commit` that still supplies at least one final projection target.
- `shadowed`: a `session.commit` whose message, artifact, order, metadata, context, run or active-reference targets were all superseded by later commits.
- `log-only`: lifecycle/model trace/rejection events that are intentionally excluded from `ChatSession` replay.

A commit containing multiple operations is `current` while any one of its targets remains current. Visibility is reconstructed from the complete stream on each query; it is not persisted into JSONL.
