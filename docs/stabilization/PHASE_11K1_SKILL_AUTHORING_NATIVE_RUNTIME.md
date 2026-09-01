# Phase 11K1 — native Skill authoring

Date: 2026-09-01
Scope: existing `common.skills_upsert/delete` core/reference mutations

## Result

- `SkillAuthoringCatalog` owns two exact Agent-only confirmed-write descriptors and
  native bindings. No case alias or second dispatch path remains.
- `SkillPackageSource` contract v1 captures the complete current package. Its
  deterministic revision covers stable metadata, normalized core Markdown and
  ordered reference revisions; the human `version` remains a separate label.
- One typed `SkillAuthoringService` prepares a bounded accepted-arguments and
  complete-current-package guard. Confirmation rejects stale state before dispatch,
  preserves omitted core fields, marks the storage boundary and verifies the exact
  resulting package revision or absence. Exact no-change avoids dispatch.
- Core and one reference remain separate mutations. Native result v1 reports the
  operation, reference path, previous/current revision and changed flag with typed
  dispatch/effect evidence.
- `SkillToolExecutor`, the final `ControllerExecutorKind`/controller execution
  branch and Skill use of legacy command/result conversion are deleted.

## Checks

- Skills: 4/4; Tools: 36/36; ToolPack: 6/6.
- Confirmation/context/replay/recovery focused cases pass, including stale cursor,
  cancellation, later model-preparation failure and pre/post-dispatch store faults.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors with existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

11K1 does not claim append-only Skill package history, restore/tombstone or artifact
import/export (R54). The existing Skills UI still uses its current bridge/store
shapes and is mandatory 11K2 scope. Real Windows x64 with Office and VS 2022 must
qualify confirmation, storage, Library editor/reference actions and WebView state.
Failures fix the native service/handler; the removed controller path cannot return.
The broad `kernel replay:` filter still has three retired Outlook result-queue
expectations against the already-native production handler; that test-only
compatibility seam is explicitly removed in 11T10 and is not runtime authority.

## Next

11K2 moves the existing Skills editor/reference bridge to versioned typed package
and result DTOs, routes its mutations through the same guarded owner and removes
direct UI-to-`SkillStore` mutation. Then 11T10 removes final generic catalog,
dispatch and legacy definition/result/UI adapters.
