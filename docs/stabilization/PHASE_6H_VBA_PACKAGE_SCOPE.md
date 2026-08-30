# Phase 6H — VBA package/rename scope audit

Date: 2026-08-30
Baseline: `cd0bd6154ff50f1d6c819f8413ffaeb15c76365a`

## Scope

This was a source-and-contract audit only. It did not change runtime behavior,
public schemas, journal events, COM adapters, UI, product version, or tags.

The audit traced every remaining package and rename consumer before admitting the
next Phase 6 implementation slices.

## Active consumers

| Capability | Current entrypoint | Why it matters |
|---|---|---|
| Global/document-local VBA tool execution | `OfficeToolExecutor.ExecuteResolvedCommand` → `VbaToolExecutor.ExecuteCustomTool` | Enabled `executor=vba` definitions are present in the conversation catalog and are runnable by Agent/manual execution. |
| Temporary package lifecycle | `ExecuteCustomTool` → session install → `Application.Run` → remove in `finally` | Required to run an enabled global VBA tool when its components are absent from the active document. |
| Persistent install/uninstall | Tools UI → WebView bridge → `AssistantController.Vba` → `OfficeToolExecutor` | Existing user-visible deployment of an already-authored package into a macro-enabled document. |
| Installation discovery/status | `ToolCatalogService` and `VbaToolExecutor.GetInstallationStatus` | Merges global definitions with live document packages and controls the current Tools UI state. |
| Rename | `common.vba_write_module` with `mode=rename` | Public stable-core VBA mutation; it uses a two-identity `package.mutation.*` record but is not an optional package feature. |
| Recovery/diagnostics | `VbaJournalStore`, reconciliation, mutation query/detail | Package and rename records are durable CAS-backed evidence and project into the existing diagnostics view. |

## Decision

The current package lifecycle stays in the first stable core and is migrated as
one domain contour. Deferring only persistent install/uninstall would leave the
same safety-critical install/remove implementation with two owners. This admits
only existing behavior: it does not admit dynamic tool definition authoring, new
package features, pipelines, or a new model-facing package tool. Dynamic authoring
remains Phase 11; the Phase 8 ToolPack still owns descriptor/policy/binding/package
fingerprint pinning.

Rename also remains mandatory Phase 6 work. Its domain API must be rename-specific
even while the existing durable `package.mutation.*` representation is preserved;
no journal rewrite, alias stream, or generic transaction framework is introduced.

## R41 found during the audit

The current temporary lifecycle records install and cleanup as two separate package
mutations. If install reaches the intended live state but its terminal append or the
subsequent cleanup does not complete, session-owned components can remain in the
document. The current source/type installation probe ignores ownership markers, so
a later run can classify matching session code as ordinary `installed`, execute it,
and skip cleanup. Reconciliation of an open install only records the observed state;
it does not link a completed install to a missing cleanup.

This is not accepted stable behavior. It is tracked as R41. Recovery must remain
fail-closed: no automatic replay, removal, overwrite, or macro execution. A later
policy-authorized operation may clean an exact unchanged session-owned package,
but only through a fresh journalled mutation with visible recovery state.

## Ordered implementation

1. **6I — typed package lifecycle.** Introduce one `Office.Vba` owner for package
   validation/probe, temporary install/run/cleanup, persistent install/remove/status,
   package preparation/read-back/terminal mapping, and package reconciliation. Use
   marker-aware states and one lifecycle correlation for temporary execution; a
   missing/unknown cleanup blocks execution. Keep the current journal/CAS authority
   and bridge/public result shapes behind adapters.
2. **6J — typed rename ownership.** Move rename guard, two-name preparation, backend
   action, source/type/identity verification, terminal outcome, and recovery
   assessment into the VBA domain boundary. Remove the executor-owned rename/package
   journal helpers once their last consumer has switched.
3. Phase 6 is only host-neutral complete after both slices, local cleanup, and the
   package/rename fault matrix. Windows/VBE/COM qualification remains WQ-VBA.

## Required regression matrix

- package/rename journal prepare failure before dispatch;
- backend throw before effect and after an applied effect;
- unreadable or mixed read-back;
- terminal append failure;
- cancellation before and after dispatch;
- restart after preparation, without replay;
- temporary install success followed by run failure and cleanup success/failure;
- install intended state with missing terminal or missing cleanup (R41);
- marker/type/hash drift and partial package collision;
- rename old/new complete-before, complete-intended, mixed, and collision states.

## Boundaries

No Phase 6I/6J slice may change production host identity/factories, COM implementation,
Tool Result v1, ToolPack loading, dynamic authoring, WebView layout, or Office host
scope. Windows x64 + Office x64 + VS 2022 remains required for real VBE normalization,
Trust Access, crash/restart, marker ownership, and cleanup behavior.

## Verification

- Exact source search traced all package/rename calls through executor, controller,
  bridge, catalog, UI, journal store, host adapters, and current harness cases.
- `git diff --check` — pass.
- 13 newly added local Markdown links — targets present.
- `ValidateVersionFormat` — pass; product version remains `16.1.0-dev`.
- No harness/build/COM/WebView test was run because this change is docs-only.
