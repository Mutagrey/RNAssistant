# Phase 11T9A — bound typed VBA host backend

Date: 2026-09-01
Scope: production VBA reads, module mutations, packages and macro host execution

## Result

- `IVbaHostBackend` is the single narrow production host port for project/module
  snapshots, guarded module writes, package install/remove and macro invocation.
  Its request and action contracts contain no `ToolCommand`, `ToolDefinition` or
  legacy `ToolResult`.
- Excel, Word and PowerPoint expose one `VbaInteropBackend` over the same exact
  retained `DocumentSession` already used by their typed host vertical. Reads and
  mutations resolve only `BoundDocumentObject`; closed sessions fail instead of
  rebinding through `ActiveWorkbook`, `ActiveDocument` or `ActivePresentation`.
  The retained application object is used only for `Application.Run`.
- `VbaReader`, `VbaMutationService` and `VbaPackageService` now receive typed
  snapshots/actions through narrow domain adapters. Package guards remain typed
  through the COM boundary; serialized component/hash command payloads are gone.
- The Excel/Word/PowerPoint VBA and macro command switches, host result mapping,
  line-read helper and the replaced mutation/package compatibility backend
  adapters were physically removed. Retired host ids have no production alias or
  dual execution path.
- Append-only VBA journals, CAS bodies, guards, reconciliation and recovery
  classification are unchanged and remain the only durable authority.
- One explicit boundary remains for the next atomic slice: public VBA/macro calls
  still enter through the controller catalog and `VbaLegacyResultProjection`.
  11T9B replaces that boundary with native ToolRuntime handlers. The harness fake
  temporarily maps typed backend calls to its established scripted fault queue;
  it is test-only, listed in `MIGRATION_MAP.md`, and must be removed by 11T10.

## Checks

- VBA targeted harness: 91/91.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- Harness and MockDemo builds: 0 errors; existing platform warnings only.
- All production C# sources parse with C# 7.3: 406 files, 0 syntax errors.
- Production source audit finds no retired VBA/macro host ids, old backend adapter
  names, `ToolCommand`, `ToolResult` or `ExecuteTool` under `OfficeHosts/Vba`.
- Version format and `git diff --check` pass before commit.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-VBA for Excel, Word
and PowerPoint against real VBE projects. Required cases include Trust Access off,
macro-free documents, multiple open documents, Save As/close during access, exact
session/runtime identity, project and bounded module reads, stale guards, module
create/write/rename/delete/restore, code-only UserForms, package install/run/cleanup,
rollback/read-back divergence, interruption reconciliation and macro failure after
possible external effect. Failure fixes the typed backend/session contract; removed
host commands and active-document fallback must not return.
