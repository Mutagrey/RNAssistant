# Phase 9D5 — immutable run view projection

Date: 2026-08-30
Baseline: `5e043f05dffdbe4b1c914d725d02752d0c9b0d2a`

## Scope

This slice introduces one immutable `RunViewState` in Core and atomically switches
the application result, bridge, chat catalog and static UI to it. The projection is
derived from authoritative `KernelState` plus source-owned `ToolExecutionEvidence`;
model prose and retained `ResponseStatus` never determine lifecycle or effects.

The event stream, CAS, schemas, `IRunStore`, `IEventStore` and
`IConversationStore` are unchanged. No second read model, writable snapshot,
history migration, vendor or network path is added.

## Contract

`RunViewState` carries:

- exact run/turn identity, narrative and current action;
- runtime lifecycle and execution health;
- successful reads, verified changes, verified no-change writes, unverified writes,
  failed calls and unknown effects;
- complete pending confirmation identity only while lifecycle is
  `awaiting_confirmation`.

Successful legacy mutation results without source-owned verification are projected
as `UnverifiedWrites` plus `UnknownEffects`; they cannot render as verified or clean.
Inconsistent effect evidence is capped by kernel write counts and remains visibly
unknown. Evidence follows the stable logical `TurnId` across a confirmation
continuation even when runtime `RunId` changes. A resumed confirmation may retain
its kernel correlation record while the lifecycle is already running; that retained
record is not exposed as a new user confirmation.

The projection is stamped on visible run messages and replayed into chat headers.
If a crash boundary lacks a stamped message, the header reducer derives a
conservative state from the same kernel summary and never invents verified effects.

## Ordering and UI

Every full chat-state response now carries the canonical session revision. The UI
keeps a monotonic revision per chat, rejects late detail responses and preserves
the current catalog membership/order plus every newer chat summary when an older
catalog arrives. The existing stream
revision CAS remains the cross-window write authority; the UI revision is only an
ordering guard for already produced projections.
The new module loads before every consumer, and all switched JS/CSS assets use one
cutover cache key so WebView cannot combine the new bridge with cached flat-status
readers.

Agent summaries, message badges and confirmation controls consume only normalized
`RunViewState`. Verified change and verified no-change remain distinct. Missing or
malformed typed state fails closed with an explicit unknown/reset indication; it
does not inherit an older clean state or parse model text.

## Ownership and cleanup

- `Core/Models/RunViewState.cs` owns the immutable DTO.
- `Core/Services/RunViewStateProjector.cs` is the only runtime-to-UI projector.
- Office materializes the projection but retains model context, tool execution and
  effect evidence ownership in their existing layers.
- `web/js/app-run-view-state.js` validates bridge shapes and owns per-chat UI
  ordering; it performs no network or persistence operation.
- `RunExecutionSummary`, its bridge fields, application result fields, message/run
  properties, getter projection and JS readers are removed. Obsolete JSON fields in
  retained raw events are ignored; they cannot seed a current projection or
  confirmation. Incompatible runs require the existing explicit new-chat/reset
  path.
- The replaced live-status catalog overlay and model-status UI branches are removed.

This closes R48 host-neutral and completes Phase 9 host-neutral. R10/R11 still need
the real Windows/WebView/restart/multi-window acceptance in Milestone WQ.

## Verification

- immutable/effect projection, pending confirmation and replay equality — 3/3;
- actual kernel replay and recovery, including stale confirmation and append faults
  — 12/12;
- completion guard and chat-session/recovery projections — 19/19;
- connected Agent, bridge and result-materialization regressions — 61/61;
- projection architecture and production-source inclusion — 4/4;
- total — 99 distinct targeted harness cases pass;
- all 13 static web test files — 70/70 cases pass, including stale transcript/outcome
  rejection and the existing diagnostics/viewer/vendor gates;
- MockDemo actual-controller compile — 0 errors, 3 existing CA1416 PDF warnings;
- `ValidateVersionFormat`, `git diff --check` and 249 local links in 11 changed
  Markdown files — pass.

Windows x64 + Office + VS 2022 controller/WebView/reload/confirmation/live-append,
clipboard and multi-window qualification was not performed. Product version remains
`16.1.0-dev`; release script, tag and push were not used.
