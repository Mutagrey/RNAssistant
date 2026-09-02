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
  2,048 px and 10 MiB with JPEG signature/hash/length evidence. Page navigation
  replaces the single cached render rather than accumulating the document.
- The UI cross-checks the original PDF hash/page count and the separate extracted-
  text hash/length before it defaults to `Страницы`. It provides previous/next,
  fit/100%/zoom and rendered-page download, and keeps independently paged extracted
  text under `Текст`. Blob URLs are revoked on tab, page, selection, chat or window
  teardown. Metadata remains under outer `Детали`.
- Exact unchanged files from bblanchon.PDFium.Win32 147.0.7690 and
  SkiaSharp.NativeAssets.Win32 3.119.2 are added under `win-x86`. Both report PE32
  Intel 80386; the existing x64 pair remains PE32+ x86-64. Office output and the
  loader use matching `x86/` or `x64/` subdirectories; the portable publisher copies
  only its requested pair to the package root fallback. They are never cross-loaded.
  Hashes are recorded in `vendor/pdf-rendering/README.md`.
- Native DLL/load failures are explicit and not automatically retried. No PDF.js,
  worker, network asset, durable viewer store or fallback identity was added.

## Checks

- Harness: artifact viewers 3/3 (text, image, PDF) and typed artifact bridge 1/1.
- Web: text/image/PDF viewer and action owner 8/8; affected Artifact/Plan/HTML checks
  29/29; changed JavaScript syntax passes.
- MockDemo actual-controller build succeeds with 0 errors; five platform analyzer
  warnings cover the existing and new guarded PDFtoImage calls.
- `file` identifies both new native DLLs as PE32 Intel 80386. Exact SHA-256 matches
  the downloaded package entries; project/publisher XML, version format and
  `git diff --check` pass.

## Open gate / next

This ARM/macOS host cannot execute Windows x86 DLLs or Office/WebView2. Real Windows
x86 must still prove text-readable import/extraction, page preview, scanned-page
rendering, loader error behavior and image-capable model sending. The same Windows
viewer lifecycle pass remains open for x64. 11D4 audio is the next viewer slice; no
WQ or Phase 12 gate is closed.
