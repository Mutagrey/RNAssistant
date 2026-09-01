# Phase 11T9C1 — native Plan questions

Date: 2026-09-01
Scope: `common.questions_ask`

## Result

- The exact tool now has a source-owned Plan-only read/control policy with
  `IndependentLocalRead=false`, so it remains singleton without being counted as a
  write or requesting confirmation.
- `UserQuestionToolHandler` validates one to three typed questions, returns the
  existing bounded `rnassistant.questions` payload and sets ToolRuntime's typed
  `AwaitingUser` control. The kernel stops the invocation without another model
  request; message prose does not choose the lifecycle.
- `UserQuestionToolCatalog` owns the exact descriptor/schema/policy projection.
  `ControllerExecutorKind.UserQuestion`, `ExecuteControllerTool` dispatch and the
  old `UserQuestionToolExecutor` class/path are removed without an alias.
- Direct/manual and model paths use the same handler. Existing UI projection is
  presentation-only and remains scheduled for final 11T10 cleanup.

## Checks

- Plan mode: 3/3, including capability admission → native question → kernel pause.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- Harness and MockDemo C# 7.3 builds: 0 errors; existing platform warnings only.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows WebView must verify the typed question panel, single/multiple selection,
free-text answer and continuation in the same Plan chat. This is UI evidence only;
the removed controller execution branch must not return.

## Next

11T9C2 moves the four `common.plan_doc_*` operations to exact native handlers and
removes `ControllerExecutorKind.PlanDocument`.
