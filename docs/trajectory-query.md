# Trajectory query projection

`ITrajectoryQuery` is a read-only, disposable projection over a fully validated session event stream. The implementation receives canonical `SessionEvent` records from `ChatStore`, builds query metadata in memory, returns one page, and discards it. It never writes an index or another history file.

Phase 9A exposes a host-neutral `run-causal` projection over these source events.
Phase 9B supplies the shared JSON viewer and Phase 9C now renders the projection as
an expandable host-neutral run journal:
[R32 — run journal and shared JSON viewer](stabilization/R32_DIAGNOSTICS_JSON_VIEWER.md).
Existing query/export authority and raw pagination remain intact; the journal is not
a second durable log.

## Query contract

Raw results and existing aggregate views are ordered newest first. `run-causal` is
ordered chronologically. `pageSize` defaults to 100 and is capped at 200. Raw
`nextCursor` is an exclusive sequence cursor (`seq:<n>`), so later appends do not shift older pages.

Filters compose with AND:

- inclusive `minSequence` / `maxSequence`;
- exact event types;
- run, turn and step correlation;
- tool-call id, artifact id, exact canonical `resourceUri`, and status extracted from typed event data, including conversation-response v2 `ResponseStatus` on assistant-message operations;
- `current`, `shadowed` or `log-only` visibility;
- case-insensitive tokenized full-text search over event metadata and materialized event data.

CAS payload bodies are intentionally excluded from full-text search and remain lazy. Their hash, content type and size metadata remain searchable/visible, and Diagnostics loads a bounded body preview only after explicit row expansion by exact event id.

Every returned raw-event row retains `sourceEventSeqs`, `sourceEventIds`, and deduplicated revision evidence in `resourceRefs`.

## Payload preview delivery

`TrajectoryPayloadService` resolves one exact event in the explicitly addressed
chat through `IEventStore.Read(RequireComplete)`. The shared resource data plane
reserves a download slot before event/CAS capture. `getChatEventPayload` returns
only typed source metadata, `returnedCharacters`, `textTruncated` and the existing
download capability; there is no inline body or alternate legacy path.

`ChatBlobStore.ReadPrefix` streams through the existing CAS codec: authentication,
decompression and whole-source length/SHA-256 verification must finish before any
preview is exposed. This diagnostic operation rejects sources above 32 MiB and
retains at most `4 * (512 * 1024 + 1)` source bytes. The preview contains at most
`512 * 1024` UTF-16 code units without splitting a surrogate pair. It does not
allocate the whole source body. Empty exact UTF-8 is valid; missing, corrupt,
inaccessible, ambiguous or oversized evidence fails explicitly.

The original `sha256`/`byteLength` identify the whole CAS source; `data.payload`
identifies the returned preview bytes. Transport MIME is inert
`text/plain; charset=utf-8`; original MIME is only a renderer hint. Raw diagnostics
and Run Journal share `RNAssistantTrajectoryPayload` and the sequential bounded
`RNAssistantResourceDownload` reader, which verifies preview SHA-256 before strict
UTF-8 decoding. BOM, duplicate JSON keys and numeric lexemes remain unchanged.
At most two browser captures are pending, with no unbounded queue. Selection/chat
change, row collapse/unmount and page close cancel capture/read; success, failure
and late metadata revoke the exact lease. A late response cannot rebind or render.

The preview is disposable, not a published resource/head or a new CAS root/store.
Complete journal replay is still required; bounded cold replay/checkpoints and
real Windows/WebView2 capture/read/cancel qualification remain open gates.

## Derived views

The same `ITrajectoryQuery` rebuilds seven correlated, read-only projections:

- `run-causal`: one chronological row stream for persisted user/run boundaries,
  exact model request/response/rejection/acceptance, runtime-owned accepted call
  origin, tool activity/dispatch, domain effect, artifact, summary and UI projection
  evidence. It exposes `ModelAttemptId`, `ToolCallId`, `MutationId`, `JournalRunId`,
  exact source events and revision-pinned `ResourceRef`; `ui.projected` remains only
  projected, never delivered;
- `model-replay`: model step boundaries, request/response/chunk payload references, format repairs, failures, attempts and usage;
- `tool-execution`: a tool call from protocol record through running/waiting/terminal states;
- `artifact-lineage`: immutable artifact revision metadata and `parentArtifactId` links;
- `confirmation-pauses`: waiting interval and its pending/resolved/failed/cancelled outcome;
- `failure-retries`: model, tool and terminal-turn failures with retry counts; model-declared `blocked` and `refused` turns are included, while `awaiting_user` is terminal but not a failure;
- `turn-usage`: lifecycle timing, model/tool/confirmation/failure counts, actual and estimated tokens, and provider-reported USD cost.

Every row retains the complete contributing `sourceEventSeqs` and `sourceEventIds`. Derived pagination uses `view:<view>:<snapshotSequence>:<offset>`: all pages use the same upper event-stream boundary even if new events are appended. A changed view or filter starts a fresh cursor.

`run-causal` adds a synthetic `diagnostic.evidence.missing` row only after a typed
terminal turn when an accepted call has no dispatch/terminal evidence, or a completed
model transport response has no parser verdict. The row links the observed source
and terminal events and explicitly states that absence proves neither success nor
failure. Waiting/confirmation/user pauses do not create gaps. No effect is inferred
from tool name, response prose or timestamps.

Accepted call/result classification at write time is owned by runtime
`AcceptedCallOrigin`, not by the configured provider-facing tool-result role or
presence of native `ToolCalls`. Phase 9A corrected current event writes accordingly.
Trajectory readers now trust only the exact persisted current operation type: an
earlier `tool.result.recorded` carrying `AcceptedCallOrigin` is exposed in
`run-causal` as `incompatible` with `requiresReset=true` and is excluded from
`tool-execution`. It is never reinterpreted, rewritten or granted replay/execution
authority.

Diagnostics turns row correlations into navigation rather than another index: run/turn/step/tool-call filters reopen the relevant chat projection, artifact and parent ids open lineage, and a source-event action opens the bounded raw sequence range. Summary metrics are scoped to the loaded selection: if a correlation/search/page filter excludes the run terminal, the journal says it was not found in that selection and never claims the canonical run has no terminal. Document-scoped VBA mutation rows use their recorded `SessionId` to navigate back to the originating chat without treating VBA as a chat artifact.

The Phase 9C UI defaults Diagnostics to the latest known run, requests at most 200
chronological rows per page and passes already loaded DTOs to `RNAssistantRunJournal`.
It keeps filters, expansion and scroll in UI memory only. Expanded rows show accepted
arguments, executor result/data and typed effect evidence. Exact API request/response
rows have a visible body badge and a dedicated filter; expanding one row automatically
loads its bounded persisted CAS body preview through the existing Diagnostics callback,
while a manual refresh remains available. The journal itself still reads no bridge, network, CAS or
storage. Projection correlation IDs are collapsed under a technical section, while the
source-range action still returns to raw JSONL rows. Missing evidence and `ui.projected`
retain their non-proof wording.

New `llm.response` events keep compact actual token usage inline beside the immutable CAS response reference. Older streams fall back to token usage in replayable assistant-message operations. Cost is shown only when the provider persisted it in `usage`; RNAssistant does not recalculate historical cost from mutable current price tables.

## Visibility projection

- `current`: the session seed, or a `session.commit` that still supplies at least one final projection target.
- `shadowed`: a `session.commit` whose message, artifact, order, metadata, context, run or active-reference targets were all superseded by later commits.
- `log-only`: lifecycle/model trace/rejection events that are intentionally excluded from `ChatSession` replay.

A commit containing multiple operations is `current` while any one of its targets remains current. Visibility is reconstructed from the complete stream on each query; it is not persisted into JSONL.

## Export

Diagnostics can export the current chat trajectory selection as a bounded disposable ZIP. The service rereads a complete validated event stream, applies the same raw/derived filters, resolves full `sourceEventSeqs`, and records referenced CAS metadata. It never writes an index or export copy into RNAssistant storage.

The default `metadata` mode removes event/row data, content-derived row titles, the search phrase, and all CAS bodies. `secrets` recursively replaces known credential-named fields but may still contain prompts or document text. `none` preserves decrypted event data and is the only mode that can include CAS bodies; each included body is decrypted and verified through `ChatBlobStore` before packaging.

`manifest.json` records selection, source-stream head evidence, reference metadata, and file hashes. `checksums.sha256` covers all preceding bundle files including the manifest, while the bridge reports the ZIP SHA-256. Because redaction changes the records, exported source hashes are evidence linked to the canonical stream rather than a self-contained replacement hash chain. See [trajectory-export.md](trajectory-export.md).
