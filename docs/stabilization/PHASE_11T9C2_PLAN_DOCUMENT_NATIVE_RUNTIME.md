# Phase 11T9C2 — native Plan Document runtime

Date: 2026-09-01
Scope: `common.plan_doc_create/update/restore/delete`

## Result

- `PlanDocumentToolCatalog` owns the four exact descriptors, schemas and Plan-only
  `Write + ToolVerification` policies. Delete retains risk level 1; none of the
  operations requests confirmation because Plan mode cannot confirm tools.
- `PlanDocumentToolHandler` is the single model/direct/manual execution path.
  `PlanDocumentService` validates the exact current revision and linear history,
  then marks dispatch immediately before appending the revision or tombstone.
- A successful result requires the exact returned artifact to exist in the session
  and the active Plan pointer to match it (or be cleared after removal). Only then
  does ToolRuntime receive `VerifiedChange`; semantic rejection remains
  `NotDispatched` with its stable error code.
- `ControllerExecutorKind.PlanDocument`, its executor field/branch and
  `PlanDocumentToolExecutor.cs` are deleted. There is no alias, dual dispatch or
  legacy result projection in this family.

## Checks

- Plan mode: 3/3.
- Plan Document: 3/3, including exact policy/binding, dispatch/effect evidence,
  pre-dispatch rejection, active-revision verification and non-mutating dry-run.
- ToolPack: 6/6; architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows WebView/Office must verify live Plan create/update/restore/removal,
projection persistence, history controls and ready handoff. This qualification
cannot restore the removed controller path.

## Next

11T9C3 moves `common.task_list_create/update/close` to exact native handlers and
removes `ControllerExecutorKind.TaskList`.
