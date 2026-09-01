# Phase 11T9C3 — native Task List runtime

Date: 2026-09-01
Scope: `common.task_list_create/update/close`

## Result

- `TaskListToolCatalog` owns the three exact descriptors, schemas and source-owned
  Agent/Plan `Write + ToolVerification` policies.
- `TaskListService` owns typed checklist validation, current revision selection,
  immutable append and active/closed pointer updates. It marks dispatch immediately
  before the session mutation.
- `TaskListToolHandler` is the single model/direct/manual execution path and reports
  `VerifiedChange` only after checking the exact appended artifact and active or
  cleared pointer. Validation/current-list failures remain known pre-dispatch errors.
- `ControllerExecutorKind.TaskList`, its executor field/branch and
  `TaskListToolExecutor.cs` are deleted without alias or dual dispatch.

## Checks

- Task Lists: 3/3, including all create/update/close native bindings,
  dispatch/effect evidence, pre-dispatch rejection, exact active/closed state and
  non-mutating dry-run.
- Plan mode: 3/3; ToolPack: 6/6; architecture boundary: 4/4; production source
  inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows WebView must verify live checklist projection, updates, closure, history
rewind and durable session save. Qualification cannot restore the removed executor.

## Next

11T9C4 moves the HTML workspace family to exact native handlers and removes
`ControllerExecutorKind.HtmlArtifact`.
