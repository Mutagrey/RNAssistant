# Phase 11J2 — native VBA package runtime

Date: 2026-09-01
Scope: existing global/document-local custom VBA package execution and Tools UI
install/remove/status

## Result

- `ToolPackageSource` contract v1 captures the complete immutable package source at
  the UI or accepted ToolPack boundary. Its deterministic content revision is
  separate from the human package version and stays pinned with the exact custom
  registration.
- Exact custom ids bind to `vba.custom.package.execute.v1` and run through native
  `ToolRuntime` under the retained `HostRuntime` document gate. Confirmation stops
  before package dispatch; the service marks dispatch immediately before the
  install/run/remove backend boundary.
- `VbaPackageResult` contract v1 carries typed status, error, dispatch and effect
  evidence. Arbitrary macro execution is conservatively `unknown` after dispatch;
  persistent install/remove claim change or no-change only from journal/read-back.
- Tools UI install/remove/status receives the same typed source and accepts only the
  lowercase result-v1 shape. `VbaPackageToolAdapter`,
  `VbaLegacyResultProjection`, the legacy custom-command branch and PascalCase UI
  fallback are deleted without aliases or dual dispatch.

## Checks

- Focused package lifecycle/native routing: 23/23; package session execution: 1/1;
  typed VBA bridge: 1/1; web package actions: 1/1.
- Full VBA: 93/93; Tools: 36/36; ToolPack: 6/6; architecture boundary: 4/4;
  production source inclusion: 1/1.
- MockDemo build has 0 errors with the existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

These are host-neutral fake-backend and web normalization checks. Real Windows x64
with Office and VS 2022 must still qualify VBE/Trust Access, retained workbook
identity, confirmation, install/remove/read-back, Library UI and COM cleanup.
Append-only package history and Host Fabric remain future architecture; neither is
claimed or emulated by a compatibility fallback.

## Next

Mandatory 11K moves existing Skill UI/model authoring to versioned typed contracts
and removes the final controller/legacy-result consumer. 11T10 then removes the
remaining generic catalog/dispatch and definition/result/UI adapters.
