# Phase 11D1 — bounded text/source and Markdown viewers

Date: 2026-08-31
Scope: host-neutral text/source and Markdown viewer boundary only

## Result

- `ArtifactViewerService` accepts only a canonical revision-pinned artifact URI
  from the active chat and reads its text representation through the shared
  `ResourceGatewayService`. The typed bridge returns fixed 32,000-character pages,
  exact offset/length/total evidence and one stable representation SHA-256.
- The viewer admits only allowlisted text/source and Markdown MIME/kind/extension
  combinations. HTML and JSON remain with their existing specialized owners;
  image, PDF and audio are not admitted by this slice.
- A viewer document is bounded to 512,000 characters. Full copy/download becomes
  available only after contiguous pages of the same exact URI, hash, total and kind
  prove a complete source inside that bound. Truncated attachment extraction and
  over-limit source never become full-source authority.
- Attachment reads expose the extracted-text SHA-256 as text-representation evidence
  rather than incorrectly reusing the original binary attachment hash.
- The allowlisted UI-only `text` and `markdown` adapters provide page line numbers,
  bounded search and page copy. Markdown renders through the existing sanitizer only
  after a complete exact read and keeps an exact Source tab; an incomplete source is
  never rendered. Viewer adapters do not call bridge, CAS or network.
- Paging/full-read/download orchestration lives in the thematic
  `app-artifact-viewer-actions.js` screen owner. The old generic artifact `<pre>`
  fallback is removed; JSON and inert uploaded HTML retain their specialized paths.
  Plan source editing stays disabled until the exact Markdown is loaded, and a dirty
  Plan preview is explicitly labelled as a non-durable draft.
- Viewer pages remain an in-memory per-chat cache and are cleared on chat switch.
  No artifact revision, event, compatibility adapter or second resource identity is
  created.

## Checks

- Harness: exact bounded viewer 1/1; typed bridge payload 1/1; shared Resource
  Gateway regression 1/1; production source inclusion 1/1.
- Web: text/Markdown viewer 6/6; Artifact JSON routing 6/6; Plan 7/7; inert HTML
  import 5/5; HTML export 6/6; Artifact Library 3/3; shared JSON viewer 7/7;
  Markdown JSON integration 8/8.
- Changed JavaScript syntax, pre-commit version format, `git diff --check` and local
  Markdown links — pass.

## Open gate / next

Windows WebView2 rendering, clipboard/download failure behavior, reload and large
payload interaction were not run. The next separate slice is 11D2 image bytes,
dimensions, fit/zoom/download and object-URL lifetime. PDF and audio remain later
independent slices; 11D1 does not close R51 or any WQ/Phase 12 gate.
