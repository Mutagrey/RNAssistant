# Phase 11K2 — typed Skills UI boundary

Date: 2026-09-01
Scope: existing Skills editor, catalog refresh and reference bridge

## Result

- Init, SendChat and Library refresh expose one lowercase
  `rnassistant.skillLibrary` contract v1 with exact package/reference revisions.
  The WebView maps that contract once into editor state and rejects raw arrays,
  PascalCase shapes and unversioned responses.
- Save sends explicit create/update/rename/delete operations instead of reconciling
  the whole custom catalog. Existing items carry their exact loaded revision; a
  stale package fails before dispatch, processing stops at the first failure and
  unrelated externally added skills are never inferred as deletes.
- Reference read/upsert/delete carries the exact package revision and returns one
  versioned typed result with dispatch/effect evidence plus exact package/reference
  metadata. Core and reference changes remain separate verified operations.
- Manual editor mutations use `SkillAuthoringService`, the same domain owner as
  Agent authoring. The controller no longer retains `SkillStore` or writes package
  files directly. Rename moves the current package without an alias and preserves
  its references.
- `StoragePath` is not bridge identity. The old raw `SkillDefinition[]` request and
  unversioned reference result fields/fallbacks are removed without dual-read.

## Checks

- Harness build: 0 errors; 4 existing platform warnings.
- Skills: 5/5, including update, stale rejection without dispatch, guarded
  reference lifecycle and rename without old identity.
- Bridge: 25/25, including typed tools/skills, Init and SendChat catalogs.
- Skills WebView contract: 4/4; adjacent run/artifact projections: 12/12; JavaScript
  syntax checks pass.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; 3 existing platform warnings.
- Version format, affected document links and `git diff --check` pass before commit.

## Deferred evidence

11K2 does not claim immutable package history, restore/tombstone or artifact
import/export (R54). Real Windows x64 with Office and VS 2022 must qualify Init and
catalog refresh, concurrent editor conflicts, core/rename/delete/reference actions,
WebView rendering and storage failure presentation. A failure fixes the typed
service/bridge; removed raw/store paths cannot return.

## Next

11T10 removes the final generic host catalog/dispatch surface,
`LegacyToolDefinitionAdapter`, legacy result adapters/UI projection and retired
test-only compatibility queues, then performs the required active-legacy audit.
