# Phase 8C — durable ToolPack reconstruction

Date: 2026-08-30
Baseline: `2b298ab2f0548bbc4758952d0ba70a8bcaf4d898`

## Scope

This host-neutral slice makes Phase 8B optional callable admission durable across
confirmation continuation, compaction, and process restart. It does not change
`AgentKernel`, the immutable execution `ToolPackSnapshot`, tool execution/outcomes,
the compaction algorithm, ResourceRef/CAS transport, resource handlers, document
identity/factories, or COM/WebView wiring.

## Durable publication barrier

`CallableToolPack.PreparePending` computes the complete candidate and full-request
budget decision without changing live membership. `ToolPackAdmissionJournal` then
appends one typed v1 event to the existing chat `*.events.jsonl` stream:

- `tool_pack.extension.accepted` pins mode, host, profile, catalog diagnostics,
  before/after snapshot revisions and requested exact schema revisions;
- `tool_pack.extension.rejected` records the bounded exact request and rejection code
  for diagnostics but carries no callable authority.

Only after the append succeeds does `CallableToolPack.Publish` change membership and
add request-local `TOOL_PACK_STATE`. An append failure leaves the pack unchanged and
the model port returns an infrastructure failure before sending the next request.
The event is a normal authenticated/encrypted stream record when those existing
history settings are enabled; there is no side store, snapshot, or second writer.

## Reconstruction

At model-session creation the journal reads the ordered accepted events with the
current logical `TurnId`. Runtime `RunId` is deliberately not the scope because
confirmation may resume the same turn with a new invocation id. Each exact requested
delta is validated before mutation against the current filtered catalog, per-schema
revision and prior snapshot revision; its resulting snapshot revision is recomputed.
Each extension is restored atomically. A broken/drifted chain resets to core until a
later accepted event explicitly rebases from the current core revision. This avoids
quadratic full-snapshot duplication in the append-only log.

The following never grants authority:

- a raw `common.capabilities_read` result in message history;
- a rejected event;
- an accepted event from another logical turn;
- an ID whose current descriptor revision differs from the pinned ref.

Profile/descriptor/snapshot drift keeps only deterministic core and injects a bounded
`TOOL_PACK_RESTORE_STATE` warning. This lets a confirmed terminal
`pending_tool_catalog_changed` result reach the model instead of hiding it behind a
preparation failure; a new exact read can establish a fresh accepted snapshot.
Automatic compaction rebuilds the same durable set before recomputing the request and
still fails visibly if the complete rematerialized request cannot fit.

## Prompt and ownership cleanup

Prompt schema 16 replaces the temporary instruction to re-read every optional schema
after reconstruction. Saved schema 15/custom text is preserved until explicit
review/reset. Stale LRU/touch wording was removed from canonical architecture,
resource-fabric, review-roadmap, and harness docs.

`ConversationModelSession` remains the model-context and admission-boundary owner;
`ToolPackAdmissionJournal` owns only typed stream append/lookup;
`CallableToolPack` owns membership validation/publication. `ChatStore` remains the
single persistence authority and `AgentKernel` is unchanged.

## Verification

The final host-neutral source snapshot passes 50 distinct targeted cases: Agent 34,
ToolPack 6, settings 5, canonical event log/HMAC/encrypted history/shared trace stream
4, and production source inclusion 1. Harness compilation has 0 errors and 4
existing CA1416 warnings from the Windows-only Excel identity probe. The actual-controller
MockDemo compiles with 0 errors and 3 existing CA1416 PDF warnings.

`ValidateVersionFormat`, `git diff --check`, and all 230 local links in the 15 changed
Markdown files pass. Product version remains `16.1.0-dev`; no release script, tag, or
push was performed. The full harness was not run.

The focused regression covers durable-before-publication failure, accepted and
rejected event shapes, restart and runtime-ID change under the same turn, new-turn
isolation despite retained raw evidence, atomic descriptor-drift fallback, optional
schema restoration through real confirmation, confirmed registration-change
continuation, and compaction without replay-tail schema evidence.

Office/VSTO execution was not run. Windows x64 + Office x64 + VS 2022 WQ-PACK must
exercise live-provider admission, confirmation, compaction, process restart, event
append failure, protected history, and controller delivery.

## Remaining Phase 8 work

Phase 8D+ owns the remaining Resource Fabric capability lifecycle/handlers, R30
closure (`Resource = data`, exact ResourceRef plus bounded readers, no second CAS
transport), and missing ADR-0004. Windows WQ-PACK remains open.
