# Phase 9D1 — persistence, replay and projection audit

Date: 2026-08-30
Baseline: `496e5e43c9816550b1f9817eecd455a17aa1363f`

## Scope

This docs-only prerequisite maps the remaining Phase 9 work after the completed
9A–9C diagnostics UI slices. It inspects the active store ports, writers, replay,
recovery and bridge projections. Runtime, event schema, UI and tests are unchanged.

## Existing authority

| Boundary | Current owner | Audit result |
|---|---|---|
| Chat truth | One hash-linked `*.events.jsonl` stream plus referenced `chat-blobs` CAS | Keep. `ChatSession`, headers, trajectory and UI are projections; no second store is needed. |
| Run decisions | `AgentKernel` → minimal `IRunStore` implemented by `ConversationKernelAdapter.Store` | Keep the port. Accepted batch and tool start are durable before dispatch; completed effect evidence is durable before the next model step. |
| Global concurrency | `ChatStore.Save` revision CAS plus the kernel adapter's invocation cursor | Keep both scopes. Stale confirmation and concurrent writes fail before a second dispatch. |
| Recovery | `ChatSessionService.ReconcileInterruptedRuns` reloads the stream and never replays a tool | Restart recovery is correct for covered cases: an open possible write becomes unknown, while persisted terminal evidence remains known. |
| Model/stream trace | `ModelTracePersistenceService` and `SessionTraceWriteQueue` append into the same stream | Queued chunks are bounded and drained before terminal response/failure. These observations cannot replace run/tool authority. |
| ToolPack admission | `ToolPackAdmissionJournal` appends accepted/rejected typed payloads through the generic trace API | Accepted admission is mandatory authority; rejected admission is diagnostic. The generic string API does not express that distinction. |
| Diagnostics | `ITrajectoryQuery` derives `run-causal` and other views from validated events | Keep read-only. Synthetic gaps are labelled non-proof and never affect replay. |
| UI outcome | `KernelState` is authority; `RunExecutionSummary`, `ChatRunRecord` flat fields and bridge DTOs are projections | Partial. There is no single typed `RunViewState`; lifecycle/health/pending data is assembled across DTOs and JS consumers. |

## Reused coverage

The existing host-neutral tests already cover more than the old Phase 3 label
suggested:

- normal, error, unknown, pending and cancelled `RunSummary` replay;
- accepted-call identity/origin and stale confirmation CAS;
- mandatory append failures before model/network and before tool dispatch;
- result append failure after a write, with no automatic retry;
- restart recovery of an open dispatch and preservation of already durable terminal
  evidence;
- CAS blob-before-event orphan handling and fail-closed GC;
- ordered queued stream chunks, terminal drain and queue failure propagation.

These cases are reused evidence only. They do not close current-process recovery,
typed store boundaries, stale/multi-window UI projection or Windows qualification.

## Missing invariants and ordered slices

### 9D2 — fail-stop reload and reconciliation

`RunStoreException` correctly bypasses the controller's ordinary error writer and the
run lease is released in `finally`. However, reconciliation is invoked only during
controller startup. After an append failure following possible dispatch, the same
process can reload the durable open boundary without immediately projecting it as
unknown; the user may see a stale running state until restart.

Add one single-chat recovery entry point owned by `ChatSessionService`. Start and
confirmation controllers must release run ownership, discard the mutated in-memory
projection, reload canonical state and invoke that entry point. It must not append
the `RunStoreException.UnpersistedSummary`, fabricate a terminal result, retry a
store append or execute a tool. Cover failure before dispatch and after possible
dispatch for both start and confirmation paths, including idempotent repeated load.

### 9D3 — typed event classification and event port

Introduce a closed typed descriptor for existing chat-stream event kinds, separating
authority from diagnostic observation and mandatory from best-effort durability.
Expose it through one narrow `IEventStore` adapter over `ChatStore`; switch all active
Office event writers/readers in the same slice and remove their arbitrary string
append dependency. `tool_pack.extension.accepted` stays mandatory authority;
rejected model/ToolPack attempts and causal observations stay diagnostics. This is a
classification of the existing stream, not a new file, schema, journal or dual-write.

### 9D4 — conversation projection port

Define the smallest `IConversationStore` required by current session/controller
consumers and implement it over the same `ChatStore`. Move load/save/revision-CAS
consumers atomically, leaving CAS maintenance and storage-internal projection code at
the concrete owner. Do not create a marker interface or copy `ChatSession` into a
second writable snapshot.

### 9D5 — typed runtime-to-UI projection

Create one immutable `RunViewState` from `KernelState` and source-owned effect
evidence, then switch bridge and JS consumers together. It must carry narrative,
lifecycle, execution health, verified/no-change writes, failed calls, unknown
effects and pending confirmation without parsing model prose or flat legacy status.
Cover replay equality, stale projection and multi-window update ordering before
removing the remaining flat projection adapter.

## Explicit exclusions

- event/CAS format rewrite, retention, migration or a second durable index;
- changes to VBA journals or their recovery authority;
- ToolPack, ToolRuntime, provider retry, streaming and HTML behavior;
- Phase 10 moves and broad file splitting;
- production COM/WebView qualification.

## Verification

The audit used targeted source/call-site searches and existing test registration.
No runtime source changed, so no harness or build was run. `git diff --check`, all
167 local links in the six changed Markdown files and `ValidateVersionFormat` pass.
Product version remains `16.1.0-dev`; release script, tag and push were not used.
