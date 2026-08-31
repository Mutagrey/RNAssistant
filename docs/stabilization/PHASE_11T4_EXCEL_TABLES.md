# Phase 11T4 — typed Excel table creation

Date: 2026-08-31
Scope: existing `excel.add_table` behavior

## Result

- The exact public id and schema now use a direct `ToolRuntime` registration,
  typed request/outcome and `ExcelTableService`; no table upsert, generic action
  list or batch-write contract was added.
- `ExcelTableInteropBackend` receives only the workbook retained by
  `ExcelDocumentSession`, runs on its owner STA and never receives `ToolCommand`,
  calls generic `ExecuteTool` or resolves `ActiveWorkbook`.
- One contiguous source is bounded to 100000 cells and the exact workbook
  collection to 1000 tables. The backend captures source values/formulas plus the
  full table collection, rechecks the opaque state token and dimensions immediately
  before `ListObjects.Add`, then reads the exact collection back.
- Explicit names are checked case-insensitively across the workbook. Exact new-table
  identity, source range, dimensions, header request and optional style must survive
  read-back before `VerifiedChange`; drift before dispatch is a definite error and
  failure/divergence after possible effect is non-retryable `unknown`.
- `ExcelAdapter`'s `excel.add_table` branch and method were physically removed.
  The fake generic host path is fail-closed, so tests detect dual dispatch.
- Public defaults remain `A1:B2`, generated Excel name, headers enabled and no
  explicit style. Charts remain the separate 11T5 slice.

## Checks

- `excel table:` 4/4.
- Full host-neutral harness: 557/557.
- Excel read 4/4, kernel replay 10/10, production source inclusion 1/1.
- MockDemo build: 0 errors, 3 existing platform warnings.
- All 372 production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-EXCEL against real
workbooks. Required cases include generated and explicit names, workbook-wide name
collisions, `hasHeaders=true/false` source interpretation, built-in/localized and
invalid styles, formulas and empty/error cells, overlapping/existing tables,
protected sheets, source/collection drift, table-count and cell ceilings, COM failure
before/after creation, rollback attempt and divergent read-back. In particular,
Excel's live range/header behavior for `xlNo` must be observed rather than inferred.
Failure fixes the typed backend or bound-session contract; the removed generic host
path must not return.
