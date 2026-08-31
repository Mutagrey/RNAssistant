# Phase 11T0 / 7D — bound Excel production cutover

Date: 2026-08-31
Scope: production Excel document binding and existing typed read/write backends

## Result

- Desktop, VSTO and in-process Excel composition bind one exact `Workbook` when the
  pane/target session is created. `ExcelDocumentSession` captures that object, one
  current `RuntimeKey`, owner STA dispatcher and mutation gate for the whole adapter
  lifetime. Its stable document id is read from that bound object on the owner STA,
  so Save As may change the durable key without changing the runtime target or gate.
- `excel.inspect`, `excel.read_range`, `excel.write_range` and HTML Excel data
  binding use one direct `ExcelInteropBackend`. It receives only the bound workbook
  and cannot call generic `ExecuteTool`, resolve a descriptor or select another
  `ActiveWorkbook` during execution.
- Read/write ranges and the current selection/active cell are accepted only when
  their worksheet belongs to the bound workbook. Close rejects access instead of
  rebinding. Save As keeps the same live object/session; a new workbook requires a
  new adapter.
- `ExcelReadCompatibilityBackend`, `ExcelWriteCompatibilityBackend`, four internal
  command ids, `ExcelAdapter.WriteRange.cs` and the repeated descriptor/active-book
  resolver were physically removed. Public ids and typed domain semantics did not
  change.
- Other Excel families and Word/PowerPoint/Outlook still use generic host dispatch;
  their mandatory 11T migrations and final legacy cleanup remain separate changes.

## Checks

- `excel read:` 4/4; `excel write:` 4/4; `host runtime:` 10/10.
- Accepted-call regression 1/1; HTML binding 1/1; identity-probe parser/verifier
  regression 5/5; architecture boundaries 4/4; production source inclusion 1/1.
- MockDemo compile: 0 errors / 3 existing platform warnings. Changed production
  host/composition sources parse with C# 7.3 and no syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

This host-neutral cutover intentionally accepts the current `RuntimeKey` as a
bound-object lifetime assumption. It does not prove COM proxy identity. Windows x64
+ Office x64 + VS 2022 must still run WQ0, WQ-SESSION and WQ-EXCEL for desktop,
VSTO and native composition, including independent proxies, active-workbook switch,
same-name workbooks, Save As and close/reopen. A failure changes the new session
identity/lifetime contract; removed legacy fallback must not return.
