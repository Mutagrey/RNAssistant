# Phase 11T1 — typed Excel find/replace

Date: 2026-08-31
Scope: existing `excel.find_cells` and `excel.replace_cells` behavior

## Result

- Both public ids keep their existing schemas and are registered directly in
  `ToolRuntime` through typed requests, `ExcelFindReplaceService` and typed outcomes.
- `ExcelFindReplaceInteropBackend` receives only the workbook retained by
  `ExcelDocumentSession`. Workbook, sheet, range and selection scopes are resolved
  against that object; there is no generic `ToolCommand`, `ExecuteTool` or
  `ActiveWorkbook` fallback.
- Find preserves literal/regex, case, whole-word, values/formulas/both, bounds,
  previews and scope hashes. Replace preserves current scope defaults,
  `replaceAll`, formula/value selection and replacement limits.
- Replace validates each exact value/formula snapshot immediately before assignment,
  marks the dispatch boundary before the first write and performs exact read-back.
  It reports verified no-change/change, definite pre-dispatch error or non-retryable
  post-dispatch unknown.
- Production `ExcelAdapter` find/replace branches, pattern/range helpers and their
  legacy result mapping were physically removed. No alias or dual execution path
  remains. Sheet lifecycle and later Excel families stay separate 11T slices.

## Checks

- `excel find replace:` 4/4.
- Existing Excel read 4/4, Excel write 4/4 and HostRuntime 10/10 regressions pass.
- Built-in catalog/safety/snapshot/authority/schema regressions pass; architecture
  boundaries 4/4 and production source inclusion 1/1 pass.
- MockDemo compiles with no errors; changed host/composition sources parse as C# 7.3.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-EXCEL against real
workbooks, including values/formulas, literal/regex, every scope, large/protected
ranges, selection change, exact pre-dispatch drift, partial COM failure and divergent
read-back. Failure fixes the typed backend or bound-session contract; removed legacy
dispatch must not return.
