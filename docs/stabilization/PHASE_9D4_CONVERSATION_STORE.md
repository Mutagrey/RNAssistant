# Phase 9D4 — conversation projection boundary

Date: 2026-08-30
Baseline: `e06f4735a7fce73136182ab9a0edd3881cd9de7e`

## Scope

This slice introduces the smallest current `IConversationStore` and switches the
session service, controller conversation paths and kernel persistence adapter
together. One `ChatConversationStoreAdapter` delegates to the same `ChatStore` that
already owns the canonical hash-linked event stream, revision CAS and CAS blobs.

No file format, replay rule, schema, durable index, writable snapshot, second store
or dual-write is added. Immutable `RunViewState`, bridge/UI ordering and flat
projection removal remain Phase 9D5.

## Contract

The port contains only operations required by current conversation aggregate
consumers:

- load, save and persisted-state checks;
- transient creation, active-chat selection and header projections;
- document/session move and deletion;
- one recovery-intent operation that closes the existing storage-owned interrupted
  step boundary and reports retained open-tool evidence.

The recovery operation does not expose raw event reads, lifecycle append APIs or
hash/revision internals. `ChatSessionService` still owns run recovery policy and the
final projection save. `ChatStore` still owns event adjacency, revision CAS and
stream mechanics.

Artifact-body hydration, HTML revision activation, event payload access, trajectory
queries, CAS health/GC and storage-internal projection reducers are deliberately not
members of `IConversationStore`. They retain their existing concrete or typed
owners. `IEventStore` and `IConversationStore` are separate adapters over one
backend; they do not persist competing copies.

## Ownership and cleanup

- `Core/Persistence/IConversationStore.cs` owns the application-facing aggregate
  contract.
- `Core/Storage/ChatConversationStoreAdapter.cs` is the only implementation and
  delegates to the existing `ChatStore` instance.
- `AssistantController` creates that instance once, then gives conversation and
  event consumers their respective narrow adapters.
- `ChatSessionService`, `ConversationRunService` and `ConversationKernelAdapter`
  no longer reference concrete `ChatStore`.
- Controller direct concrete calls are limited to artifact-body and HTML-revision
  operations; CAS maintenance retains the concrete owner.
- Replaced public `ChatStore` conversation methods, including full-session listing
  and raw interruption helpers, are Core-internal. There is no compatibility
  overload, fallback or alternate write path.

This closes R47 host-neutral. The adapter is a permanent layer boundary while
`ChatStore` remains the canonical backend; it is removed only if that backend
directly implements the same narrow port, never by adding another store.

## Verification

- conversation-port source/public-surface boundary — 1/1 pass;
- production source inclusion — 1/1 pass;
- session lifecycle, active selection, migration, deletion and interruption —
  14/14 pass;
- actual event replay, persistence/materialization faults and interruption —
  10/10 pass;
- confirmation failure reload/recovery — 2/2 pass;
- stale revision CAS rejection — 1/1 pass;
- total — 29 distinct targeted cases pass;
- MockDemo actual-controller compile — 0 errors, 3 existing CA1416 PDF warnings.
- `ValidateVersionFormat`, `git diff --check` and 216 local links in 10 changed
  Markdown files — pass.

Windows x64 + Office + VS 2022 controller/restart/multi-window qualification was not
performed. Product version remains `16.1.0-dev`; release script, tag and push were
not used.
