# Phase 11A2 — exact Artifact Library projection

Date: 2026-08-31  
Status: done host-neutral; Windows WebView qualification open

## Scope

This slice adds one server-owned read-only projection for Artifact Library heads,
resource classes and exact revision history. It does not add a store, resource
transport, generic editor, mutation API or AgentKernel behavior.

## Implementation

- `ArtifactLibraryProjectionService` derives `artifactLibrary { sessionRevision,
  heads[] }` from the replayed `ChatSession`.
- Each head identifies immutable original/snapshot, versioned document/aggregate or
  derived resource; it carries a canonical UI group, normalized display kind, exact
  head URI and body-free history records with parent/restore relations.
- Plan and Task List use their persisted logical ids. HTML uses the exact
  `ActiveHtmlArtifactId`, including undo/branch selection, rather than a client-side
  maximum revision. Immutable snapshots remain separate rows even when provenance
  includes a parent.
- Init, full chat, send result and direct HTML editor responses carry the same
  projection. Existing session revision guards reject stale library state.
- Raw `artifacts[]` remains the exact-revision source for message cards and existing
  viewers. Explicit message/run collections are deduplicated only by exact artifact
  id and never redirected to a library head.

## Cleanup

- Removed client lineage-root/head selection and the stale
  `activePlanArtifactId` alias.
- Removed `Revision > 1`/extension-driven version labels. Uploads show `Original`,
  derived resources show their relation, versioned documents show `vN`, and
  immutable snapshots have no version badge.
- Normalized `plan_document` to Plan presentation and replaced the false Plan JSON
  label/action with Markdown/Source.
- Artifact navigation now uses the four canonical groups: authored documents,
  files/media, generated snapshots and system evidence.

## Verification

- Harness `artifact library:`: 3/3 pass — immutable classes/labels, exact Plan/HTML
  heads and branch history, derived provenance.
- Harness `bridge: typed sendChat`: 1/1 pass.
- Harness `html workspace`: 4/4 pass.
- Harness production source inclusion: 1/1 pass.
- Web Artifact Library projection: 3/3 pass; commit projection: 3/3 pass; tree
  adapter/consumer: 4/4 pass. Changed JavaScript files pass `node --check`.
- MockDemo Release full `--self-test`: all four model profiles and failed-turn
  persistence pass; only the existing three CA1416 PDF warnings are emitted.
- `ValidateVersionFormat`, `git diff --check` and 304 local Markdown link targets in
  nine changed documents: pass.

Full harness was not run. Windows x64 + Office x64 + real WebView2 reload,
multi-window ordering, history disclosure and clipboard behavior were not run.
Plan append-only restore/delete semantics, HTML revision uniqueness/import and typed
text/media viewers remain later Phase 11 slices. Product remains `16.1.0-dev`; no
release or tag is created.

## Result

11A2 is done host-neutral. The next independent change is the Plan slice: exact
Markdown payload/current-revision guards, restore-as-new-head and append-only delete
semantics, followed separately by HTML and viewer work.
