# Phase 11B3 — Plan history, removal UX and handoff

Date: 2026-08-31
Scope: host-neutral Plan UI actions only

## Result

- Each non-head Plan history row exposes `Restore`; it passes the server-projected
  exact current and exact source artifact ids to `common.plan_doc_restore`.
- Restore explicitly confirms creation of a new version and preserves the historical
  revision; concurrent Plan mutations are serialized client-side and still rely on
  the domain stale guard.
- Delete first runs the same exact-guarded command as `dryRun`, then displays the
  returned revision count and every referencing message id before confirmation.
  The final call repeats the same exact guard; the warning explains that pinned
  history becomes stable removal placeholders.
- Ready handoff rechecks the raw artifact against the active id, `ready` status and
  byte-exact `rna://` URI before switching to Agent. The submitted composer request
  contains only that pinned URI and `common.resources_read` guidance, never internal
  artifact ids.
- Plan action mutations moved out of the detail renderer into the existing
  `RNAssistantHtmlWorkspaceActions` owner. The editor passes only narrow callbacks.

## Checks

- `node tests/web/plan-document.test.js` — 7/7 pass.
- `node tests/web/artifact-library-projection.test.js` — 3/3 pass.
- Syntax checks for the four changed JavaScript modules — pass.
- Pre-commit version format, `git diff --check` and changed local Markdown links — pass.

## Open gate / next

Windows WebView2 confirmation, focus, reload, clipboard and real form submission were
not run. Next begins the isolated HTML workspace contour with exact whole-workspace
revision ownership; import/viewer work remains separate.
