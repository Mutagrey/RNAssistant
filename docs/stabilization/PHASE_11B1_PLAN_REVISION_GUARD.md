# Phase 11B1 — Plan exact revision guard

Date: 2026-08-31  
Scope: host-neutral Plan create/update lineage only

## Result

- `Office.Services.PlanDocumentService` is the single create/update domain owner.
- Create and update validate non-empty Markdown but preserve the complete input
  string, including leading/trailing whitespace and Markdown hard-break spaces.
- Update requires the supplied artifact id to equal the active Plan revision.
- Revision numbers must be contiguous and unique, parents must form one linear
  chain, and the active revision must be its head. Drift fails before append.
- `PlanDocumentToolExecutor` now adapts schemas/arguments/results only; its replaced
  create/update lineage implementation was removed.
- The Plan UI validates with a trimmed probe but sends the unmodified editor value.

Delete was moved mechanically under the same service without changing its current
physical-removal behavior. Append-only restore/tombstone semantics are deliberately
the next isolated 11B2 invariant.

## Checks

- Harness `plan document:` — 1/1 pass.
- Harness `plan mode:` — 2/2 pass.
- Harness production source inclusion — 1/1 pass.
- `node tests/web/plan-document.test.js` — 2/2 pass.
- Reused affected `node tests/web/artifact-library-projection.test.js` — 3/3 pass.
- `node --check web/js/app-html-workspace-artifacts.js` — pass.
- Pre-commit version format and `git diff --check` — pass.
- 307 local Markdown link targets in eight changed docs — pass.

## Open gate / next

Windows WebView2 editor/reload/clipboard behavior was not run. Next is 11B2:
restore-as-new-head and guarded append-only tombstone removal while historical exact
message references remain intact.
