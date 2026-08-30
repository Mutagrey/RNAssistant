# Phase 9D2 — same-process run-store failure recovery

Date: 2026-08-30
Baseline: `eb23002f39c54d3790f9fc8721890bc3860a4004`

## Scope

This slice closes R45 for Agent start and confirmation. A mandatory `IRunStore`
append failure still escapes as the original `RunStoreException`, but the current
controller now releases run ownership, discards its mutated projection, reloads the
canonical chat stream and reconciles an unfinished durable boundary immediately.

No event format, kernel decision, tool retry, UI contract or second store is added.

## Runtime contract

1. `AgentKernel` stops at the failed mandatory append and never retries it.
2. The controller disposes the stale causal scope and `ChatRunLease` before recovery.
3. `ChatSessionService` reloads the exact persisted session and acquires the existing
   recovery lease. It never reads `RunStoreException.UnpersistedSummary`; active
   cache replacement uses the canonical projection while that lease is still held.
4. A failure before confirmation dispatch keeps the durable pending call unchanged.
5. A durable open dispatch is interrupted once: possible write effect becomes
   `unknown`, open protocol exchange is excluded, and no tool is replayed.
6. Already persisted terminal evidence stays known. Repeated recovery is a no-op.
7. A recovery append CAS conflict is not retried; the latest canonical projection is
   returned. If local recovery itself fails, the original exception remains visible
   and startup recovery remains available.

## Ownership and cleanup

- `ChatSessionService` remains the only chat-run reconciliation owner; startup scan
  and targeted recovery share one implementation.
- `AssistantController.RunStoreRecovery` owns only release → reload orchestration for
  the two controller entry points. It cannot calculate outcome or save in-memory
  evidence.
- Confirmation invalidates the VBA catalog before recovery because a confirmed
  mutation may already have crossed dispatch.
- The previous catch filters that merely let `RunStoreException` escape were removed.
  There is no parallel recovery path or compatibility adapter.

## Fault matrix

| Path | Failed durable boundary | Result after release/reload |
|---|---|---|
| New run, before write dispatch | accepted/model boundary CAS conflict | failed/interrupted, zero effect, no tool call |
| New run, after write dispatch | terminal append CAS conflict | one actual execution, durable open boundary becomes `WriteUnknown` |
| Confirmation, before claim/dispatch | continuation claim CAS conflict | original pending confirmation remains available, zero effect |
| Confirmation, after confirmed write | terminal append CAS conflict | one actual write, pending is closed by interruption, `WriteUnknown` |

Both recovery paths are blocked while the original run lease is held and are
idempotent after it is released.

## Verification

- `kernel recovery:` — 2/2 pass;
- new-run append failure before/after dispatch — 2/2 pass;
- existing startup recovery unknown/saved-boundary cases — 2/2 pass;
- production project source inclusion — 1/1 pass;
- MockDemo actual-controller compile — 0 errors, 3 existing CA1416 PDF warnings.
- version format and 177 local links in 7 changed Markdown files — pass.

Windows x64 + Office + VS 2022 controller/WebView qualification was not performed.
Product version remains `16.1.0-dev`; release script, tag and push were not used.
