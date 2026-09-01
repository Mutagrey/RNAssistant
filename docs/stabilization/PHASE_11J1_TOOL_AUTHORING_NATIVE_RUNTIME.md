# Phase 11J1 — native custom Tool authoring

Date: 2026-09-01
Scope: `common.tools_definition_read`, `common.tools_validate`,
`common.tools_upsert`, `common.tools_delete`

## Result

- `ToolAuthoringCatalog` owns four exact Agent-only descriptors and policies.
  Read/validate are independent local `Read + None`; upsert/delete are confirmed
  `Write + ToolVerification` mutations.
- Native handlers call one typed `ToolAuthoringService`; no authoring domain method
  accepts `ToolCommand` or returns the legacy result DTO.
- Upsert/delete preparation stores only bounded exact-argument, operation and
  effective pre-state hashes. Confirmation rejects drift before dispatch, preserves
  omitted update fields and verifies the effective stored definition or deletion by
  read-back. Exact no-change avoids dispatch.
- Manual mutations use the same prepare/consume path. Exact ids and bindings have no
  case alias. `ToolAuthoringExecutor`, its old files and
  `ControllerExecutorKind.ToolAuthoring` are deleted without dual dispatch.

## Checks

- Focused native Tool CRUD/guards/effect evidence: 1/1; Tools: 36/36;
  disabled pipelines: 3/3; ToolPack: 6/6; Plan mode: 3/3; strict controller schema:
  1/1.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Existing custom package execution and Tools UI install/remove/status still use the
temporary package definition/result projections and are mandatory 11J2 scope.
Windows Tool Library/storage/VBE behavior remains qualification evidence. Failures
must fix the native authoring service/handler path; the removed controller executor
or a case-insensitive alias cannot return.

## Next

Mandatory 11J2 switches existing custom package execution and Tools UI package
actions to the versioned typed package source/result boundary, then removes
`VbaPackageToolAdapter` and `VbaLegacyResultProjection` from that contour.
