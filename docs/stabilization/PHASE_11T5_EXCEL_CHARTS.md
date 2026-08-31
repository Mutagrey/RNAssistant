# Phase 11T5 — typed Excel charts

Date: 2026-08-31
Scope: existing `excel.create_chat_chart`, `excel.upsert_chart` and
`excel.delete_chart` behavior

## Result

- The three exact public ids and schemas now use direct `ToolRuntime`
  registrations, typed requests/outcomes and `ExcelChartService`; no generic chart
  action list, batch mutation or schema expansion was added.
- `ExcelChartInteropBackend` receives only the workbook retained by
  `ExcelDocumentSession`, runs on its owner STA and never receives `ToolCommand`,
  calls generic `ExecuteTool` or resolves `ActiveWorkbook`.
- Chat-chart reads preserve the current `ChartArtifact` contract and selection/range
  defaults with a 10000-cell ceiling. The read is source-owned and read-only.
- Worksheet-chart mutations preserve upsert/createOnly/updateOnly, generated-name,
  source/type/title/label/axis/geometry and delete behavior. One operation observes
  at most 200 workbook charts, 100 series per chart and 10000 cells in each requested
  source/label range; ambiguous names across worksheets fail closed.
- Before mutation, the backend rechecks an opaque token covering the full chart
  collection and requested source/label values/formulas. Exact create/update/delete
  state and all untouched charts must survive read-back before verified evidence;
  divergence or failure after possible effect is non-retryable `unknown`.
- `ExcelAdapter`'s three chart branches, methods and replaced chart helpers were
  physically removed. The fake generic host route is fail-closed, so tests detect
  dual dispatch. All current Excel public families are now direct typed/bound.

## Checks

- `excel chart:` 4/4.
- Full host-neutral harness: 561/561.
- MockDemo build: 0 errors, 3 existing platform warnings.
- All 378 production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-EXCEL against real
workbooks. Required cases include selection and explicit chat sources; empty,
formula and error cells; generated and explicit chart names; workbook-wide duplicate
names; createOnly/updateOnly; supported and unsupported/localized chart types; series
formula normalization; category labels; axis-bearing and axis-free types; title
removal; geometry clamps; protected sheets; source/collection drift; chart/series/
cell ceilings; COM failure before and after create/update/delete; rollback attempt;
and divergent read-back. Failure fixes the typed backend or bound-session contract;
the removed generic host path must not return.
