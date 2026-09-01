# Phase 11T9B — native public VBA runtime

Date: 2026-09-01
Scope: five public VBA/macro tools, preparation and confirmation continuation

## Result

- `common.vba_write_module`, `common.vba_apply_patch`,
  `common.vba_delete_module`, `common.vba_restore_backup` and
  `common.office_run_macro` have source-owned typed policy, exact native binding
  and one `VbaToolHandler` over the bound `IVbaHostBackend` from 11T9A.
- `IPreparableToolHandler` adds a bounded opaque preparation state to ToolRuntime.
  It is stored with pending kernel state, survives append-only chat replay and is
  supplied only to the confirmed execution of the same accepted call/policy.
  Missing, oversized, malformed or argument-mismatched state fails before dispatch.
- Public VBA preparation no longer rewrites accepted `ToolCommand.Arguments` or
  stores a guard in `RuntimeGuardJson`. Exact original argument JSON is hashed into
  the prepared state; resolved module/backup identity and live guard stay opaque.
- Agent continuation consumes the persisted guard without re-preparing. Explicit
  manual VBA actions run the same prepare/execute path under their existing UI
  authorization. Preparation cannot cross an effect boundary; if it does, runtime
  records `unknown`.
- Mutation backends mark the exact first possible dispatch. Typed read-back maps
  committed/no-op/not-applied/ambiguous results to explicit effect evidence.
  Arbitrary `Application.Run` remains `unknown` after dispatch even when it returns,
  because external effects cannot be verified generically.
- `ControllerExecutorKind.Vba`, `ExecuteControllerTool`,
  `PrepareControllerTool`, `PreviewPreparedControllerTool`, public guard readers
  and public use of `VbaLegacyResultProjection` are removed without an alias or
  dual dispatch. The remaining projection serves only custom package/manual and
  reconciliation UI boundaries until 11J.

## Checks

- VBA targeted harness: 92/92.
- ToolRuntime preparation/confirmation: 15/15.
- Production Agent VBA confirmation replay: 1/1; arbitrary macro loop: 1/1.
- ToolPack: 6/6; architecture: 4/4; production source inclusion: 1/1.
- Harness and MockDemo builds: 0 errors; existing platform warnings only.
- Production C# sources parse with C# 7.3: 0 syntax errors.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-VBA for Excel, Word
and PowerPoint. Required cases include durable confirmation across restart, external
module/backup drift after confirmation pause, manual editor create/write/delete/
restore, macro return/throw after possible external effect, Trust Access off,
closed/rebound documents, journal terminal loss and real VBE read-back. Failure
fixes the typed handler/backend/session contract; the removed controller route,
mutable command guard and host compatibility commands must not return.

## Next

11T9C switches the remaining controller-owned existing execution families to exact
native typed handlers and deletes their controller branches. Typed custom authoring
and final generic catalog/result/UI cleanup remain separate mandatory slices.
