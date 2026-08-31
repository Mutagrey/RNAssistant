# Phase 11C2 — inert uploaded HTML import and source preview

Date: 2026-08-31
Scope: host-neutral uploaded-HTML source/view/import boundary only

## Result

- Uploaded HTML remains an immutable attachment original. Selecting it cannot add
  workspace files or execute source.
- `UploadedHtmlResourceService` resolves one exact canonical chat revision and
  validates its message/attachment identity, hash, length and HTML type. Preview is
  a typed `ResourceGatewayService` text read bounded to 32,000 characters.
- The UI renders returned source only through `pre.textContent`, labels truncation
  and discards an unfinished response after a chat switch.
- Import is an explicit confirmed action guarded by the exact active HTML artifact.
  It requires a new `.html`/`.htm` path and complete decoded source no larger than
  300,000 characters, then appends one normal whole-workspace revision.
- The new revision records exact source URI/artifact/hash/path metadata and a source
  relation; descendants retain provenance. The original attachment, CAS reference
  and user-message reference are unchanged.
- No compatibility adapter, automatic conversion, second transport or legacy HTML
  execution path was added.

## Checks

- Harness: HTML import 1/1; typed bridge 1/1; HTML lineage 1/1; Artifact Library,
  commit ordering and production source inclusion regressions 3/3.
- Web: uploaded-HTML actions 5/5; artifact viewer 6/6; Plan 7/7; Artifact Library
  3/3; changed JavaScript syntax pass.
- Pre-commit version format, `git diff --check` and local Markdown links — pass.

## Open gate / next

Windows WebView2/Office interaction was not run. 11C3 separately owns binding,
recovery and export payload preservation; typed text/Markdown and media viewers
remain later slices.
