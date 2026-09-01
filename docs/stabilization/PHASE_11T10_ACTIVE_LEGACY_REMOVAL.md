# Phase 11T10 — final active-legacy removal

Date: 2026-09-01
Scope: final existing-tool catalog, execution, result, manual UI and test seams

## Result

- `IOfficeApplicationAdapter` no longer exposes generic built-in catalog or command
  execution. Its dispatched/UI-thread wrappers and every production host tool-id
  switch are gone; all Office/VBA/controller/custom tools enter exact native
  `ToolRuntime` handlers and direct bound backends.
- `OfficeBuiltInToolCatalog` is replaced by source-owned `OfficeToolCatalog` plus
  exact direct registrations. `ToolPackSnapshotFactory` consumes the catalog entry's
  captured `ToolPolicy` and `ToolBinding` directly and rejects missing authority;
  runtime dispatch rechecks the pinned binding/revision without rebuilding it from
  an id.
- The old Core definition/command/result DTO file is deleted. Mutable catalog and
  package data is `ToolCatalogEntry`, accepted local calls are `ToolInvocation`,
  execution authority remains immutable descriptor/policy/binding, model output
  remains Tool Result v1 and manual/UI output is strict `ToolRunResult` v1.
- `LegacyToolDefinitionAdapter`, `LegacyToolResultAdapter`,
  `ToolResultUiProjection` and their model/manual/activity conversion paths are
  deleted without aliases or dual-read. `OfficeToolExecutor` is now only the typed
  composition/manual façade over one captured runtime.
- The Tools editor uses lowercase `rnassistant.toolLibrary` v1 and explicit
  revision-guarded create/update/rename/delete requests/results. It shares
  `ToolAuthoringService` with model authoring; controller-owned store reconciliation,
  storage-path identity and unversioned/PascalCase response fallback are removed.
- The fake VBA backend now exposes direct typed state, request and fault hooks. Its
  retired command/result ids and queue are deleted while preserving the full fault
  matrix.

## Active-legacy audit

Production/test source contains no `ToolDefinition`, `ToolCommand`, generic
`GetBuiltInTools`/`ExecuteTool`, `OfficeBuiltInToolCatalog` or deleted legacy
adapter/UI-projection path. The remaining `ActiveWorkbook`, `ActiveDocument`,
`ActivePresentation` and Outlook active-window reads occur only in `ThisAddIn` pane
lifecycle discovery; no execution adapter/backend can use them as a target fallback.
Narrow VBA journal ports, exact incompatible-history diagnostics and
`ModelCompatibilityService` share current authorities and are not active legacy.

## Checks

- Harness build/C# 7.3: 0 errors; 4 existing platform warnings.
- Full host-neutral harness: 589/589.
- VBA: 93/93; Tools: 37/37; ToolRuntime: 15/15; ToolPack: 6/6.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- Tool Library WebView: 6/6 across editor, strict contract and typed package result.
- MockDemo build: 0 errors with existing platform warnings.
- Version format, affected-document links and `git diff --check` pass before commit.

## Deferred evidence

No Office/VSTO validation was run on this machine. Windows x64 + Office + VS 2022
must still qualify production project build, COM marshal/cleanup, exact retained
DocumentSession lifetime, pane/window cleanup, WQ0/WQ-SESSION and all host/VBA/
ToolPack/WebView packs. A failure is fixed in the typed direct path; none of the
removed compatibility paths may return.

## Next

Run the mandatory Milestone WQ matrix against the exact candidate and admit Phase 12
only after the accumulated Windows/live-provider gates are satisfied.
