# Phase 11T6 — bound typed Word vertical

Date: 2026-08-31
Scope: all existing public Word reads and mutations

## Result

- The exact public ids `word.read_text`, `word.find_text`, `word.inspect`,
  `word.write_text`, `word.replace_text`, `word.format_text`, `word.add_table`,
  `word.insert_page_break` and `word.add_comment` keep their schemas and now use
  direct `ToolRuntime` registrations, typed requests/outcomes and `WordService`.
- Desktop composition resolves one exact open document by durable identity or
  window and creates `WordDocumentSession`. Word VSTO owns one runtime per window;
  each runtime retains its exact `Word.Document`. Closed or mismatched sessions fail
  instead of rebinding to `ActiveDocument`.
- `WordInteropBackend` receives only that retained document, runs under
  `HostRuntime` on the owner STA and never receives `ToolCommand`, calls generic
  `ExecuteTool` or resolves an active document as an execution fallback.
- Reads preserve current document/selection/range, story search and inspection
  shapes. HTML binding uses the same typed Word read adapter. Table creation is
  capped at 10,000 cells.
- Mutations capture and recheck exact target state before the first COM assignment.
  Write, formatting, table, page-break and comment operations perform operation-
  specific read-back; replacement plans exact story edits and verifies the complete
  resulting scope. No-change/change/error/unknown evidence is explicit, and any
  failure after possible effect is non-retryable `unknown`.
- The nine public branches, methods and replaced helpers were physically removed
  from `WordAdapter`. The fake generic Word route is fail-closed, preventing dual
  dispatch. Only the separately gated VBA/macro host branches remain for 11T9.

## Checks

- `word tools:` 4/4.
- Full host-neutral harness: 565/565.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors, existing platform warnings only.
- All 386 production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-WORD against real
documents and multi-window VSTO panes. Required cases include saved/unsaved and
Save As identity, multiple documents/windows, close during access, selection
ownership, character ranges, all Word stories, literal/regex and case/whole-word
replacement, replacement length changes, localized styles/fonts, mixed formatting,
tables at each location, comments, page breaks, protected/read-only documents,
target drift, COM failure before/after dispatch, partial effects and divergent
read-back. Failure fixes the typed backend or bound-session contract; the removed
generic Word path must not return.
