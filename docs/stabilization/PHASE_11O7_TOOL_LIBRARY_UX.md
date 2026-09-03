# Phase 11O7 — Tool Library documentation and typed Test UX

Date: 2026-09-03
Status: done host-neutral; Windows WebView2/Office qualification remains open

## Result

- Built-in documentation is generated on demand for one exact built-in id and
  catalog revision through `rnassistant.toolLibraryDocumentation*` v1. It is not
  stored in `ToolCatalogEntry.Readme`, the compact Library list, model
  descriptions, capability context/results or ToolPack revision input.
- Every effective built-in receives purpose, target, schema arguments and
  constraints, policy/confirmation/effect, result/error guidance, limitations and
  a safe Library recipe from the same captured catalog contract.
- Test now renders boolean, numeric, enum, long-string and bounded JSON controls;
  shows required/conditional/optional state, defaults/constraints, omit/null and
  inline validation; advanced JSON must be explicitly applied and pass schema.
- `common.capabilities_read` reference paging exposes semantic `Далее` only after
  `hasMore=true`. One bounded in-memory cloned session retains the prior strict
  result; no cursor or continuation token crosses the WebView boundary or enters
  the active chat.
- Implementation/Test pages now own horizontal overflow, wrap actions and full
  descriptions, keep children at `min-width:0`, and refresh CodeMirror after the
  visible-tab layout frame.

The removed flat Test path no longer converts every field through one text input or
uses schema descriptions as truncated placeholders. No second executor, catalog,
store or authority was added; Test still invokes the production ToolRuntime.

## Verification

- Harness: `tools: built-in documentation is UI-only`, `tools: Library
  continuation keeps runtime state internal`, and `bridge: typed tools and skills`
  pass 3/3.
- Focused web Tool Library/editor/package/vendor tests pass 16/16; JavaScript syntax
  checks and `git diff --check` pass.
- Production source inclusion is checked by the existing old-style project gate.

## Open qualification

Real Windows WebView2 must still verify narrow-pane Implementation/Test layout,
keyboard/focus/theme, Markdown rendering, confirmation, read continuation and live
Excel/Word/PowerPoint/Outlook target binding. Final live-provider/WQ-PACK evidence
must use this post-11O7 catalog; Phase 12 has not started.
