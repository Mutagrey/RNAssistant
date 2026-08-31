# Phase 11T2 — typed Excel sheet lifecycle

Date: 2026-08-31
Scope: existing `excel.add_sheet` and `excel.rename_sheet` behavior

## Result

- Both public ids keep their exact schemas and now use direct `ToolRuntime`
  registrations, typed requests/outcomes and `ExcelSheetService`.
- `ExcelSheetInteropBackend` receives only the workbook retained by
  `ExcelDocumentSession`; it reads and mutates that workbook on its owner STA without
  `ToolCommand`, generic `ExecuteTool` or `ActiveWorkbook` fallback.
- Worksheet name/default/active-sheet behavior is preserved. The domain owner checks
  exact pre-state and collisions; the backend rechecks the ordered sheet collection
  immediately before dispatch. Add and rename then use exact read-back and report
  verified no-change/change, definite pre-dispatch error or non-retryable unknown.
- `ExcelAdapter` add/rename branches, methods and the replaced worksheet-name helper
  were removed. Test consumers that intentionally characterize legacy result
  conversion now name a still-legacy family instead of treating `add_sheet` as a
  generic-dispatch marker.
- No delete-sheet capability, schema expansion, alias or dual route was introduced.
  Range operations remain the separate 11T3 slice.

## Checks

- `excel sheet:` 4/4; all previously affected kernel replay, completion guard,
  causal trace, Agent, desktop dispatch, resource, pipeline and tool-shadow filters
  pass.
- Full host-neutral harness: 549/549.
- Architecture boundaries 4/4 and production source inclusion 1/1 pass.
- MockDemo compiles with no errors; changed host/composition sources parse as C# 7.3.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-EXCEL for add and
rename on real workbooks, including active-sheet fallback, invalid/colliding and
case-only names, protected workbook structure, collection drift, COM failure after
creation/rename, rollback attempt and divergent read-back. Failure fixes the typed
backend or bound-session contract; removed legacy dispatch must not return.
