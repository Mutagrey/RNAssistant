# CAS health and garbage collection

`chat-blobs` is one immutable SHA-256 CAS shared by chat model payloads, artifact bodies, committed attachments, extracted attachment text, and VBA before/intended/backup source. CAS files are not a second index: durable ownership exists only in chat `*.events.jsonl` streams and document-scoped VBA `mutations.events.jsonl` journals.

## Reachability audit

Settings → Diagnostics → CAS storage runs a repository-wide audit under the cross-window maintenance gate. It:

1. Enumerates every canonical chat stream and VBA journal.
2. Validates schema, sequence, hash/HMAC chain, protection key, decrypted event data, replay projection, and canonical source path.
3. Discovers payload references and the typed SHA-256/byte-length pairs used by artifacts and attachments from every retained event, including shadowed historical revisions.
4. Verifies each referenced blob after decryption by plaintext byte length and SHA-256.
5. Classifies stored canonical blobs with no retained event/journal reference as orphans.

The report distinguishes missing referenced blobs, corrupt/unreadable referenced blobs, harmless orphans, malformed CAS paths, reference conflicts, and invalid sources. Protected history is audited with the current DPAPI-backed API-key/custom-secret protector; secrets are never returned through Diagnostics.

## Fail-closed collection

Collection always performs a fresh audit inside the same maintenance window; it never trusts a previous UI report. The gate prevents new chat/VBA operations from starting and rejects collection while any local or external run remains active.

Deletion is allowed only when reachability is complete. Any unreadable, corrupt, unsupported, misplaced, or projection-invalid source blocks all deletion. Managed roots and discovered source/blob entries that are reparse points, symlinks, or junctions are never traversed and are treated as unreadable/blocking. An incomplete final JSONL row also blocks GC even though normal append recovery can later trim that row. Missing or corrupt referenced content is never an orphan and is never deleted.

Only canonical `<first-two-hex>/<sha256>.blob` paths from the fresh orphan set are deleted, one exact file at a time. Unknown `.blob` layouts are reported and retained. Other managed recursive cleanup removes child reparse points themselves without following them. Source streams, journals, settings, keys, staging files, and directories are not modified by GC.

This collector intentionally reclaims crash leftovers and blobs from removed whole streams, not historical revisions still referenced by append-only events. Retention/pruning, redacted exports, re-keying, and disposable query indexes remain separate future operations.
