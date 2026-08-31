# Phase 11C3 — exact HTML bindings, recovery and export

Date: 2026-08-31
Scope: host-neutral HTML binding/checkpoint/export boundary only

## Result

- `HtmlWorkspaceArtifactService` remains the sole whole-workspace revision owner.
  `ChatStore.Save` no longer synthesizes HTML artifacts or branch-local revision
  numbers; the replaced fallback and its comparison/serialization helpers are gone.
- A successful binding records the exact transformed JSON SHA-256 and explicit
  payload completeness (`complete`, `bounded` or `truncated`). Recovery normalizes
  that evidence and marks a hash mismatch as an error instead of silently trusting it.
- Automatic binding refresh remains ephemeral until the next ordinary chat
  checkpoint. Unrelated storage saves cannot turn it into a hidden revision or
  overwrite the last durable recovery state.
- Export requires the exact non-empty active HTML artifact id and creates an ordinary
  checkpoint through the same domain owner only when workspace state changed. The
  typed bridge returns that exact artifact id, canonical revision-pinned `rna://`
  URI, CAS SHA-256 and complete checkpoint workspace before download.
- Dirty editor state, stale heads and incomplete revision evidence fail before the
  browser download. Standalone assembly preserves each raw JSON string without a
  parse/stringify round trip, exposes raw access plus completeness/hash metadata,
  and escapes script terminators and JavaScript line separators.
- No compatibility adapter, duplicate store, mutable export snapshot or background
  binding-revision path remains.

## Checks

- Harness: exact export/recovery 1/1; typed export bridge 1/1; HTML binding 1/1;
  artifact CAS 1/1; HTML lineage 1/1; recovery 2/2; workspace persistence 1/1.
- Web: exact export 6/6; uploaded-HTML import 5/5; Plan 7/7; Artifact Library 3/3;
  changed JavaScript syntax pass.
- Production source inclusion, pre-commit version format, `git diff --check` and
  local Markdown links — pass.

## Open gate / next

Windows WebView2/Office binding, recovery, clipboard/download and standalone-file
interaction were not run. The next separate slice is the bounded text/source and
Markdown viewer; image, PDF and audio remain independent measured slices.
