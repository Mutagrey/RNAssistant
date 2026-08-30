# Phase 9D3 — typed chat event boundary

Date: 2026-08-30
Baseline: `acb67bf7c9d3eda87c38c2c94aa00ea41162606a`

## Scope

This slice classifies every current top-level chat-stream event through one closed
descriptor catalog and introduces a narrow `IEventStore` over the existing
`ChatStore`. All active Office model-trace, ToolPack, causal-trace and diagnostics
event consumers switch together. The JSONL/CAS format, event type strings and
storage lifecycle remain byte-compatible.

No second store, schema migration, dual-write or conversation projection port is
introduced. `IConversationStore` remains Phase 9D4; `RunViewState` remains 9D5.

## Contract

Each `SessionEventKind` resolves to one canonical descriptor with four independent
properties:

| Property | Meaning |
|---|---|
| Lane | Agent protocol/runtime evidence or domain diagnostic observation |
| Authority | May drive replay/publication, or diagnostics only |
| Durability | Failure must stop the owning boundary, or the observation is best effort |
| Write scope | Appendable through `IEventStore`, or owned internally by `ChatStore` |

The important classifications are:

- materialized model request and accepted ToolPack extension are mandatory Agent
  authority;
- model response/failure/chunks and rejected model/ToolPack attempts are mandatory
  Agent diagnostics under the current lifecycle contract;
- `model.response.accepted` is only a best-effort diagnostic marker; the accepted
  response/calls themselves remain authoritative inside `session.commit`;
- run/tool/domain/UI causal observations are best-effort Domain Diagnostics;
- `session.*`, `turn.*` and `step.*` lifecycle events are mandatory storage-internal
  authority and cannot be appended through the Office-facing port.

Classification is selected by source-owned enum kind. The port rejects `Unknown`,
non-canonical descriptors and storage-internal writes. Existing unknown historical
rows remain readable after normal stream validation; the closed rule applies to new
port writes and does not rewrite retained history. Source adapters fail closed on an
unknown model-trace type, and the causal writer accepts only best-effort Domain
Diagnostic descriptors.

## Ownership and cleanup

- `Core/Persistence/IEventStore.cs` owns the closed vocabulary and write/read DTOs.
- `Core/Storage/ChatEventStoreAdapter.cs` is the only adapter and delegates to the
  same `ChatStore` stream/CAS operations.
- `ChatStore` alone still emits adjacent session/turn/step lifecycle rows and owns
  hashing, CAS, revision CAS, recovery and projections.
- `ModelTracePersistenceService`, `ToolPackAdmissionJournal`, `RunCausalTrace` and
  controller diagnostics now depend on the narrow port.
- External CAS payload reads are bound to the selected chat `SessionId`; an event
  envelope from another chat is rejected before hydration.
- Causal producers pass `SessionEventKind`; writable arbitrary `Stage` and all
  direct Office `AppendTrace*`/broad event-read calls were removed.
- The replaced broad `ChatStore` event append/read members are now storage-internal;
  external production code cannot bypass the typed port.
- `ITrajectoryQuery` remains the typed read-only derived-query owner; it is not
  replaced by this append/read boundary.

## Verification

- typed descriptor/wire/storage-scope contract — 1/1 pass;
- Office source boundary — 1/1 pass;
- ToolPack admission/reconstruction — 6/6 pass;
- causal trace — 6/6 pass;
- streaming queue — 2/2 pass;
- trajectory projection/export — 4/4 pass;
- refusal/trace policy, typed trajectory bridge, canonical event log and production
  source inclusion — 4/4 pass;
- total — 24 distinct targeted cases pass;
- MockDemo actual-controller compile — 0 errors, 3 existing CA1416 PDF warnings.
- `ValidateVersionFormat`, `git diff --check` and 208 local links in 9 changed
  Markdown files — pass.

Windows x64 + Office + VS 2022 controller/WebView persistence qualification was not
performed. Product version remains `16.1.0-dev`; release script, tag and push were
not used.
