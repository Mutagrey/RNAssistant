# Phase 11B2 — Plan restore and tombstone removal

Date: 2026-08-31
Scope: host-neutral Plan restore/delete durable semantics only

## Result

- `Office.Services.PlanDocumentService` owns guarded restore and delete alongside
  create/update.
- Restore requires the exact active revision, copies the selected historical
  Markdown/title/status unchanged, and appends a linear child with exact source
  provenance.
- Delete requires the exact active revision and appends one `removed:true` tombstone;
  it clears the active pointer but never deletes revisions or rewrites message refs.
- Durable commits use only `artifact.revision.created`; `artifact.remove` is absent.
- Library/list/search/new prompt and compaction checkpoint projections omit a removed
  Plan. Exact retained URI resolve/read fails with non-retryable `resource_removed`.
- Replay, pruning and forks retain an applicable tombstone and cannot resurrect a
  removed Plan from earlier pinned refs. Model-linked removal follows its source
  message; direct UI removal is session-level.
- `common.plan_doc_restore` is admitted only by the existing Plan-local policy. The
  tool executor remains an argument/schema/result adapter.

The replaced physical-delete path and its message-ref removal behavior were removed;
there is no alias or dual-write compatibility path.

## Checks

- Harness `plan document:` — 2/2 pass, including compaction checkpoint exclusion.
- Harness `plan mode:` — 2/2 pass.
- Harness `artifact library:` — 3/3 pass.
- Targeted Resource Gateway read/list — 1/1 pass.
- Harness production source inclusion — 1/1 pass.
- `node tests/web/plan-document.test.js` — 4/4 pass.
- `node tests/web/artifact-library-projection.test.js` — 3/3 pass.
- Syntax checks for three changed JS modules — pass.
- Pre-commit version format, `git diff --check` and changed local Markdown links — pass.

## Open gate / next

Windows WebView2/reload/history/fork interaction was not run. Next is isolated 11B3:
history restore/removal UX and ready-plan handoff by exact pinned `rna://` URI.
