# Phase 11D2 — exact image and preview-first artifact viewers

Date: 2026-09-02
Scope: host-neutral image viewer and artifact detail presentation only

## Result

- `ArtifactViewerService` accepts only a canonical revision-pinned image artifact
  from the active chat. It resolves one exact source message/attachment, requires
  matching image kind, JPEG/PNG/GIF/WebP MIME, hash and byte length, then recomputes
  SHA-256 over local CAS bytes before returning the typed payload.
- The existing 20 MiB attachment limit is also the viewer limit. The client
  validates URI/kind/MIME/hash/base64 length before caching; at most two image
  payloads remain in the per-chat viewer cache.
- The UI-only image adapter creates a local Blob URL and provides fit, 100%, zoom,
  natural dimensions and download. It revokes the URL when the selection/chat is
  replaced and on window teardown. It has no bridge, CAS, tool or network access.
- Artifact detail now defaults to `Просмотр`. Plan/Markdown, Task List and image
  have full domain previews. Metadata, raw Task List/JSON payload and revision
  history are under `Детали`; metadata no longer replaces or precedes the preview.
  Existing JSON and uploaded-HTML owners remain unchanged.
- No durable event, artifact revision, fallback identity, second store or PDF/audio
  behavior was introduced.

## Checks

- Harness: artifact viewers 2/2 (existing exact text plus new exact image); typed
  artifact bridge 1/1.
- Web: image/text viewers 7/7; artifact JSON/task/detail routing 7/7; Plan 8/8;
  HTML export 6/6; uploaded HTML 5/5; Artifact Library 3/3 — 36/36 affected checks.
- Changed JavaScript syntax and `git diff --check` — pass.

## Open gate / next

Real Windows WebView2 image dimensions, download failure behavior, Blob lifetime
across reload/multi-window and large payload interaction were not run. 11D3 remains
the next separate slice for PDF pages, extracted text and scan/truncation state with
a separately admitted local renderer. Audio remains 11D4. No WQ or Phase 12 gate is
closed.
