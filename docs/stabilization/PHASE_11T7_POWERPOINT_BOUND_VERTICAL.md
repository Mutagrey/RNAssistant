# Phase 11T7 — bound typed PowerPoint vertical

Date: 2026-08-31
Scope: all existing public PowerPoint reads and mutations

## Result

- The exact public ids `powerpoint.read_slides`, `powerpoint.search_text`,
  `powerpoint.replace_text`, `powerpoint.add_slide`, `powerpoint.set_text`,
  `powerpoint.add_object`, `powerpoint.duplicate_slide`, `powerpoint.move_slide`
  and `powerpoint.list_objects` keep their schemas and now use direct
  `ToolRuntime` registrations, typed requests/outcomes and `PowerPointService`.
- Desktop composition resolves one exact open presentation and its window and
  creates `PowerPointDocumentSession`. PowerPoint VSTO owns one runtime per window;
  each runtime retains its exact `PowerPoint.Presentation` and `DocumentWindow`.
  Closed or mismatched sessions fail instead of rebinding to
  `ActivePresentation`.
- `PowerPointInteropBackend` receives only that retained presentation/window, runs
  under `HostRuntime` on the owner STA and never receives `ToolCommand`, calls
  generic `ExecuteTool` or resolves an active presentation as an execution fallback.
- Reads preserve the current slide/selection/full-deck scopes and bounded slide,
  shape and text projections. HTML binding uses the same typed PowerPoint read
  adapter. Table dimensions, returned collections and picture payloads are bounded.
- Mutations capture and recheck operation-specific slide/shape state before the
  first COM assignment. Replace, add/set-object, slide creation/duplication and move
  perform exact read-back. No-change/change/error/unknown evidence is explicit, and
  any failure after possible effect is non-retryable `unknown`.
- The nine public branches, methods and replaced helpers were physically removed
  from `PowerPointAdapter`. The fake generic PowerPoint route is fail-closed,
  preventing dual dispatch. Only the separately gated VBA/macro host branches remain
  for 11T9.

## Checks

- `powerpoint tools:` 4/4.
- Full host-neutral harness: 571/571.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors, existing platform warnings only.
- All 394 production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-POWERPOINT against
real presentations and multi-window VSTO panes. Required cases include
saved/unsaved and Save As identity, multiple presentations/windows, close during
access, slide/shape selection ownership, title/body/notes placeholders, grouped and
unsupported shapes, stable shape/slide ids, literal/regex replacement, table and
picture insertion limits, duplicate/move ordering, protected/read-only files,
target drift, COM failure before/after dispatch, partial effects and divergent
read-back. Failure fixes the typed backend or bound-session contract; the removed
generic PowerPoint path must not return.
