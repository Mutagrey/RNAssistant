# Phase 11T9C5 — native capability runtime

Date: 2026-09-01
Scope: `common.capabilities_search/read`

## Result

- `CapabilityToolCatalog` owns the two exact Agent/Plan descriptors and
  independent local `Read + None` policies. Chat cannot admit either tool.
- `CapabilityToolHandler` executes the immutable run tool catalog and selected
  skill catalog through typed `CapabilityCatalogService*` outcomes. Skill
  core/reference reads were removed from `SkillToolExecutor`; search and read have
  no dispatch/effect boundary.
- Compact metadata, descriptor and skill revisions, complete tool-schema evidence,
  skill core/reference paging and ToolPack admission semantics are preserved.
  Capability ids and native bindings have no case alias.
- `CapabilityDiscoveryExecutor`, `ControllerExecutorKind.CapabilityDiscovery` and
  capability use of `LegacyToolOutcomeAdapter` / `LegacyToolResultAdapter` are
  deleted without alias or dual dispatch.

## Checks

- Discovery/native ownership: 1/1; Skills CRUD/reference/exact catalog: 4/4;
  ToolPack snapshot/admission/replay: 6/6.
- Oversized capability evidence: 1/1; Agent skill context/load: 2/2; Plan mode:
  3/3; disabled pipelines: 3/3; strict controller schemas: 1/1.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows/live-provider WQ-PACK must verify exact model-visible schema/skill reads,
context admission and Prompt Inspector/UI projection. Qualification cannot restore
the removed controller executor or case-insensitive aliases.

## Next

11T9C6 moves `common.prompts_read/save` to exact native handlers and removes
`ControllerExecutorKind.Prompt`.
