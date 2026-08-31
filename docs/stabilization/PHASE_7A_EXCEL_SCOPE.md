# Phase 7A — Excel read/write boundary and consumer audit

Date: 2026-08-30
Baseline: `d362b48c85584365559926f231d0b7a90c3edab0`

## Scope

This is a docs-only prerequisite for the Phase 7 runtime switches. It maps every
active `excel.inspect`, `excel.read_range`, and `excel.write_range` route, fixes the
ownership sequence, and records blockers that must not be hidden by a partial
cutover. Runtime, schemas, factories, COM and tests are unchanged.

## Current owners and consumers

| Path | Current behavior | Required switch |
|---|---|---|
| Agent and confirmed calls | `ConversationKernelAdapter` sends only `common.resources_list` through native `ToolRuntime`; Excel calls use `OfficeToolExecutor` and legacy `_adapter.ExecuteTool` mapping | 7B/7C exact native registrations and typed domain outcomes |
| Manual Tools execution | `OfficeToolExecutor` validates the public schema and calls the same legacy adapter under `HostRuntime` | Reuse the same 7B/7C domain adapter; no manual-only path |
| HTML bind/refresh | `HtmlArtifactToolExecutor.ExecuteDataSource` holds the live-document gate but calls `_adapter.ExecuteTool` directly | Inject the 7B read adapter under the already-held access; never retain a second public Excel implementation |
| Host implementation | `ExcelAdapter` owns public routing, target/sheet resolution, range materialization, profile logic and scalar/formula/table writes | Extract only the admitted read/write COM backend; leave other Excel behavior in place |
| Production target | `ExcelAdapter` has no production `IOfficeDocumentSession`; descriptor/`ActiveWorkbook` compatibility resolution remains pending 5B2 | Accepted-risk atomic 11T0/7D: one `ExcelDocumentSession.BoundDocumentObject`, captured current `RuntimeKey`, no execution fallback; WQ0 remains deferred release evidence |
| Catalog/schema | `OfficeBuiltInToolCatalog` owns the current exact public contracts | Preserve them through Phase 7; Phase 8 owns ToolPack/catalog replacement |

Direct `FakeOfficeAdapter.ExecuteTool` calls used to seed harness state are test
fixtures, not another production consumer. Scripted demo model calls flow through the
normal executor and do not own execution semantics.

## Ordered runtime slices

### 7B — typed reads and one public route

- Add the host-neutral Excel read contracts/service under `RNAssistant.Office`.
  The service cannot reference COM, `ToolCommand`, legacy `ToolResult`, chat state or
  controller/UI types.
- Move the complete public `excel.inspect` selector family atomically: `workbook`,
  `sheets`, `charts`, `tables`, `names`, and `shapes`. `kind=charts` is read-only
  metadata within this tool; it does not admit chart creation/update/delete.
- Move `excel.read_range` values, formulas and profile through the same typed owner.
  Empty data is a successful explicit snapshot; missing/unreadable/malformed data is
  an error and cannot be projected as an empty workbook or range.
- Register the two exact public IDs in native `ToolRuntime` only when their handlers
  are composed. Native ownership, `Describe`, manual routing and registry admission
  must change atomically; a static ID claim without an exact handler is invalid.
  Remove their public cases from `ExcelAdapter` in that switch; no per-selector
  fallback or dual execution is allowed.
- The native model path must enter `HostRuntime` with the chat's exact document
  expectation. Manual execution and HTML refresh reuse their already-held synchronous
  access instead of opening a second independent document-operation root.
- Route HTML bind/refresh through the same read adapter for both switched source IDs;
  it must not call the host adapter directly.
- Preserve the 100,000-cell range ceiling before `Value2`/`Formula` materialization.
  The host backend must receive the ceiling and inspect dimensions first; the domain
  service also rejects inconsistent or oversized returned snapshots.
- Bound collection output for sheets/charts/tables/names/shapes. Defined-name
  inspection returns bounded metadata and must not materialize an arbitrary
  multi-cell `RefersToRange.Value2`.

A single explicit compatibility backend may map typed requests to internal host
commands until 7D. Its owner, consumers and removal gate must be listed in
`MIGRATION_MAP.md`; public tool IDs cannot remain executable inside the host adapter.

### 7C — verified `write_range`

- Add a typed write service and native handler for only scalar value, formula and 2D
  table writes. Other Excel mutations remain legacy and outside this phase.
- Validate and normalize the exact target rectangle before dispatch. Ragged table
  rows retain the current deterministic null-padding rule; size bounds apply before
  allocating or assigning the COM matrix.
- Read the exact values/formulas before dispatch and again after dispatch from the
  same backend/target. A matching intended state is required for `ok`.
- Emit `VerifiedNoChange` when the before state already equals the intended state,
  and `VerifiedChange` only when read-back matches and the state changed. A definite
  pre-dispatch refusal is `error`; possible dispatch with unreadable or divergent
  final state is non-retryable `unknown`.
- Mark the dispatch boundary immediately before the host write. Preserve current
  policy/confirmation authority in `ToolRuntime`; do not infer effect from COM
  return, prose, or legacy `Success`.

### 7D — bound production backend and cleanup

7D is blocked by the WQ0 identity observation and the separate 5B2 production
switch. After that evidence, all desktop/VSTO/native factories must supply the same
live `ExcelDocumentSession`; the extracted interop backend receives only its bound
workbook object. The compatibility resolver/internal command seam and
`ActiveWorkbook`/descriptor execution fallback are then removed together and the
WQ-SESSION/WQ-EXCEL matrix is run on Windows.

## Explicit exclusions

- `excel.find_cells`, `excel.create_chat_chart`, `excel.replace_cells`;
- table/chart creation, update or deletion;
- formatting, sheet management, clear/sort/filter;
- VBA, other Office hosts, AgentKernel, persistence, UI and ToolPack changes;
- production identity/factory changes before WQ0.

## Risks found

- **R43:** current `excel.inspect` enumerations are unbounded, and defined-name
  inspection can request `RefersToRange.Value2` without a cell ceiling.
- **R44:** HTML data bind/refresh directly invokes the host adapter; switching only
  Agent/manual calls would leave second legacy `excel.inspect`/`excel.read_range`
  implementations.

Both remain open until 7B. Existing R03/R23 cover unverified write effects; R04
continues to cover production target identity and cannot be closed by fake sessions.

## Verification

The audit used targeted source/call-site searches and canonical contract review.
No runtime source changed, so no build or harness was run. `git diff --check` and
`ValidateVersionFormat` pass; 7 changed Markdown files contain 130 local links with
0 missing targets.
