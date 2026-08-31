# Phase 11C1 — HTML whole-workspace lineage

Date: 2026-08-31
Scope: host-neutral HTML revision ownership only

## Result

- `HtmlWorkspaceArtifactService` assigns every whole-workspace artifact the next
  revision number across the complete workspace graph, including inactive branches.
- A save after undo records the exact selected artifact as parent; the explicit
  active pointer remains the Library head even when another branch has a greater
  revision number.
- Duplicate/invalid revision ids, revision numbers and non-older existing parents
  fail before HTML mutation or pointer restoration. Incompatible history requires a
  new chat/reset instead of silent renumbering or fallback.
- A missing ancestor remains the existing degraded-readable case: ancestry is not
  guessed, undo stops at the gap and a readable active branch may continue.
- The replaced `active.Revision + 1` allocation path is removed; no adapter or
  dual-write remains.

## Checks

- `html lineage:` — 1/1 pass.
- HTML navigation, redo branch, both recovery and Artifact Library focused
  regressions — 5/5 pass.
- Pre-commit version format, `git diff --check` and local Markdown links — pass.

## Open gate / next

Windows WebView2/Office behavior was not run. 11C2 separately owns inert uploaded
HTML import with explicit provenance and bounded source/preview UX; bindings,
recovery/export changes remain 11C3.
