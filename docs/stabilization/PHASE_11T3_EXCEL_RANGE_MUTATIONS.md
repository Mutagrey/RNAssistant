# Phase 11T3 — typed Excel range mutations

Date: 2026-08-31
Scope: existing `excel.clear_range`, `excel.sort_range`, `excel.filter_range` and
`excel.format_range` behavior

## Result

- All four exact public ids and schemas now use direct `ToolRuntime` registrations,
  typed requests/outcomes and `ExcelRangeMutationService`; no generic action list or
  batch-write contract was introduced.
- `ExcelRangeMutationInteropBackend` receives only the workbook retained by
  `ExcelDocumentSession`, runs on its owner STA and never receives `ToolCommand`,
  calls generic `ExecuteTool` or resolves `ActiveWorkbook`.
- One contiguous target is bounded to 100000 cells; autofit read-back is additionally
  bounded to 10000 row or column dimensions per requested axis. The backend returns an opaque
  operation-specific state token, rechecks that token and exact dimensions
  immediately before the first COM call, then exposes separate content/order/filter/
  format read-back. Autofit also pins the exact observed row/column dimensions after
  dispatch.
- The domain preserves existing defaults and distinguishes verified no-change,
  verified change, definite pre-dispatch error and non-retryable unknown after a
  possible effect. Sort/filter column selectors are rejected before dispatch.
- `ExcelAdapter` clear/sort/filter/format switch cases, methods and the replaced
  color/alignment helpers were removed. Legacy-result characterization tests now use
  a still-legacy table family instead of treating `format_range` as generic dispatch.
- No schema expansion, alias, compatibility backend or dual execution route was
  added. Tables and charts remain the separate 11T4–11T5 slices.

## Checks

- `excel range mutation:` 4/4.
- Full host-neutral harness: 553/553.
- Architecture boundaries 4/4 and production source inclusion 1/1.
- MockDemo build: 0 errors, 3 existing platform warnings.
- All 366 production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-EXCEL against real
workbooks. Required cases include values/formats/all clear, protected and mixed/
conditional-format ranges, ascending/descending sort with and without headers under
the installed Office locale, blank/error/formula keys, AutoFilter empty/text/operator/
wildcard criteria and normalized read-back, number formats/colors/alignment,
row/column/both autofit, Normal-style localization, target/pre-state drift and COM
failure before/after possible dispatch. Divergence stays `unknown` without automatic
retry; failures fix the typed backend or bound-session contract and never restore the
removed generic host path.
