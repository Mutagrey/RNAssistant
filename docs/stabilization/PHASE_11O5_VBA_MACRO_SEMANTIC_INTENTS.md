# Phase 11O5 — VBA/macro semantic intents

Date: 2026-09-03
Scope: public VBA mutation and Office macro model contracts

## Result

- The public family now has six exact ids: whole-source write, identity-preserving
  rename, exact patch, delete, restore and arbitrary Office macro execution.
- `common.vba_write_module` no longer contains rename. The separate
  `common.vba_rename_module` accepts only source and destination component names and
  keeps the existing two-identity guard, journal, typed backend and read-back owner.
- Patch hunks accept only `find` and `text`; runtime supplies the fixed replace
  operation. Restore accepts either an exact readable Resource Fabric backup target
  or `moduleName` for its latest available backup. Runtime resolves and pins the raw
  backup id before confirmation. Missing and ambiguous targets fail closed.
- Exact backup/mutation ids, hashes, revisions, cursors, guards and journal state
  remain in durable/manual evidence and are removed from model result data/messages. Old
  `op`, `backupId`, combined write/rename, retired ids and synthetic `rna_*` names
  cannot enter accepted history; an incompatible chat requires explicit reset.
- The internal VBA editor restore adapter translates its existing typed backup id
  to the same readable target before entering the public schema. No second executor,
  alias, dual schema or automatic mutation retry was introduced.
- Prompt schema is 22. The reviewed inventory contains 69 unique built-in ids and
  72 effective host variants.

## Checks

- Full host-neutral `vba:` slice: 94/94.
- Focused Agent, model projection/history, resource, bridge, settings, catalog,
  removed-id and inventory checks: 15/15.
- Architecture boundary checks: 4/4.
- Version-format and final diff checks pass. Existing CA1416 platform warnings are
  unchanged.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 remains mandatory for Excel, Word and
PowerPoint VBE mutation/confirmation, semantic backup selection, arbitrary macro
execution and WebView2 result/history qualification. This host-neutral result does
not make the build stable, beta or RC.

## Next

11O6 recomputes the minimal mode/host core pack from explicit eval evidence while
keeping optional exact schemas available through capability admission. UI-only
built-in documentation, typed Library Test/layout and final post-cutover Windows/
live-provider WQ remain after it; Phase 12 stays blocked.
