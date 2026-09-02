# Phase 11D3 — PDF preview and matching x86 native runtime

Date: 2026-09-02
Scope: host-neutral PDF viewer/runtime packaging only

## Result

- PDF info and page rendering use separate typed bridge operations bound to one
  canonical revision-pinned attachment URI. Exact source message/attachment identity,
  PDF MIME, original SHA-256, byte length and PdfPig page count must agree. Dedicated
  `ArtifactPdfViewerService` owns PDF admission/native rendering; the generic artifact
  viewer only delegates it and retains the shared exact text-page boundary.
- The info response verifies the stored extracted-text SHA-256/character count and
  returns only that evidence, page text lengths, explicit truncation and scan/little-
  text warning. Extracted text stays on the existing exact viewer-page operation:
  32,000 characters per read within its 512,000-character viewer ceiling. It cannot
  bypass that bound even though ingestion may store up to 1,000,000 characters.
- One requested zero-based page is rendered locally to JPEG. Input remains under the
  20 MiB attachment bound; page count is capped at 10,000; output is capped at
  2,048 px and 10 MiB with JPEG signature/hash/length evidence. A separate typed
  thumbnail operation uses the same exact URI/hash/count admission and a 320 px /
  1 MiB output bound. Main-page navigation replaces one render; the virtualized rail
  permits at most four concurrent thumbnail reads and retains at most 24 ephemeral
  ready/error results rather than accumulating the document.
- The UI cross-checks the original PDF hash/page count and the separate extracted-
  text hash/length before it defaults to `Страницы`. A local Viewer.js 1.12.0
  instance handles both admitted images and rendered PDF JPEGs: proportional fit
  with upscaling, 100%/button/wheel/pinch zoom, pan, rotation and rendered-page
  download. Independently paged extracted text remains under `Текст`.
  PDF pages additionally have a virtualized left thumbnail rail and bounded numeric
  jump. Centered arrows appear on hover/focus (always visible for coarse pointers);
  Left/Right changes pages, Fit/100% toggles on double-click, and position remains
  visible. Main and thumbnail Blob URLs are revoked on tab, page, selection, chat or
  window teardown. Metadata remains under outer `Детали`.
- Exact unchanged files from bblanchon.PDFium.Win32 147.0.7690 and
  SkiaSharp.NativeAssets.Win32 3.119.2 are added under `win-x86`. Both report PE32
  Intel 80386; the existing x64 pair remains PE32+ x86-64. Office output and the
  loader use matching `x86/` or `x64/` subdirectories; the portable publisher copies
  only its requested pair to the package root fallback. They are never cross-loaded.
  Hashes are recorded in `vendor/pdf-rendering/README.md`.
- Native DLL/load failures are explicit and not automatically retried. Viewer.js is
  pinned with exact dist/license hashes and has no bridge/network/worker access. No
  PDF.js, raw-PDF browser path, durable viewer store or fallback identity was added.

## PDF.js decision

PDF.js was evaluated but is not connected in this slice. Viewer.js is only the
interaction layer over an already verified JPEG and never receives the PDF. The
official PDF.js full viewer does provide page navigation, zoom, search and
presentation controls, but it consumes
the PDF URL or decoded binary. RNAssistant currently exposes only one bounded,
hash-checked JPEG page to WebView and pages extracted text through a separate exact
representation. A PDF.js cutover therefore requires exact vendored viewer/worker
assets, an admitted raw-PDF byte contract and bounds, CSP/worker policy, cache cleanup
and Windows WebView evidence together. It is not a styling layer over the existing
page-image contract. References: [project/readme](https://github.com/mozilla/pdf.js/blob/master/README.md),
[viewer options](https://github.com/mozilla/pdf.js/wiki/Viewer-options),
[binary-data opening](https://github.com/mozilla/pdf.js/wiki/Frequently-Asked-Questions).

The current native renderer remains process-architecture-bound. AnyCPU/managed code
does not make a PE32+ x64 PDFium/Skia DLL loadable inside an x86 Office process, so
the matching reviewed PE32 x86 vendor pair remains required until a separately
qualified renderer cutover removes that dependency.

## Checks

- Harness: artifact viewers 3/3 (text, image, PDF) and typed artifact bridge 1/1.
- Media-viewer follow-up: focused viewer/action 8/8, vendor admission 5/5 and
  affected Artifact/Plan/HTML checks 24/24 — 37/37 web checks total; syntax passes
  for all five changed JavaScript files. A local 1280×720 Chromium smoke confirms
  the proportional full-area page fit, centered overlays and a 148 px scrolling
  rail; 60 pages produce only eight visible thumbnail DOM rows.
- MockDemo actual-controller build succeeds with 0 errors; five platform analyzer
  warnings cover the existing and new guarded PDFtoImage calls.
- `file` identifies both new native DLLs as PE32 Intel 80386. Exact SHA-256 matches
  the downloaded package entries; project/publisher XML, version format and
  `git diff --check` pass.

## Open gate / next

This ARM/macOS host cannot execute Windows x86 DLLs or Office/WebView2. Real Windows
x86 must still prove text-readable import/extraction, page preview/thumbnail rail,
scanned-page rendering, loader error behavior and image-capable model sending. The
same Windows viewer lifecycle pass remains open for x64. 11D4 audio remains a later
viewer slice; the active tools route continues with 11O4. No WQ or Phase 12 gate is
closed.
