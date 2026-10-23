# CAS health and garbage collection

`chat-blobs` is one immutable SHA-256 CAS shared by chat model payloads, artifact bodies, committed attachments, extracted attachment text, file snapshots, web manifests/verification records and VBA before/intended/backup source. Durable roots include chat `*.events.jsonl`, VBA journals, resource authority revisions/views and resource mutation journals. CAS files are not a second index.

## Reachability audit

Settings → Diagnostics → CAS storage runs a repository-wide audit under the cross-window maintenance gate. It:

1. Enumerates canonical chat streams, VBA/resource mutation journals, resource authority records and managed Outlook archive-cache references.
2. Validates each source's schema, replay and canonical-path rules; chat/VBA event validation also checks sequence, hash/HMAC chain, protection key and decrypted event data.
3. Discovers payload references and the typed SHA-256/byte-length pairs used by artifacts and attachments from every retained event, including shadowed historical revisions.
4. Verifies each referenced blob by plaintext byte length and SHA-256. A previously authenticated encoding may use fresh stored-byte verification as described below.
5. Classifies stored canonical blobs with no retained event/journal reference as orphans.

Web snapshot manifests use authority view `Parts` for each retained file payload;
verification views retain the manifest payload. Both pending and terminal revisions
remain roots. No recursive JSON body scan or second snapshot-specific GC index is
required. Focused workspace snapshot tests verify collection preserves these roots.

The document text index v3 is also a derived authority view: its manifest retains
every 32,000-character CAS part and the exact source/extraction payload. A failed
build can leave unreferenced derived parts, which the ordinary orphan audit may
collect. Indexing consumes protected CAS bytes in memory and publishes the view
only after the source hash is verified; it writes no plaintext scratch file.
Failures in the derived-view consumer propagate as consumer failures; they are not
reinterpreted as malformed compressed CAS bytes or retried as legacy raw blobs.

The report distinguishes missing referenced blobs, corrupt/unreadable referenced blobs, harmless orphans, malformed CAS paths, reference conflicts, and invalid sources. Protected history is audited with the current DPAPI-backed API-key/custom-secret protector; secrets are never returned through Diagnostics.

## Publication and reuse

`ChatBlobStore.StoreText` uses the same verified write path as `StoreBytes`.
Supplying exact source bytes can repair a damaged blob at its existing content
address; an earlier reference, matching file size or valid envelope cannot bypass
verification. No CAS format or identity changes are involved.

`StoreFile` hashes and encodes the same open source handle with cancellable,
bounded-buffer passes and stage progress. Temporary compressed/encoded writes
are removed on failure/cancellation. `OpenVerifiedRead` authenticates, decodes and
hashes the complete retained body before returning its read-only stream; it keeps
that CAS handle open until disposal. Raw bodies are seekable; compressed/encrypted
bodies remain sequential and never spill plaintext to a scratch file. Reads also
observe cancellation. A caller supplies its source-byte budget independently of
preview/text-response limits. Existing CAS format, protection and reachability
rules apply unchanged to large workspace source revisions.

Reference-only original/shared-context publication, model claim evidence, HTML
member search and workspace move/delete/restore use `HasVerifiedReference`.
Damaged dependencies are rejected before publication or mutation preparation;
shared-context validation also precedes retaining its new snapshot. A reference
alone cannot repair unknown bytes. These checks read current stored bytes through
the existing verifier; they do not introduce a persistent validity cache.

The internal `MayHaveStoredReference` is only an availability hint for skipping
externalization of an unchanged, already trusted in-memory chat artifact. It does
not establish integrity or authorize publication. Metadata-only chat saves retain
this optimization; reopening a body and maintenance audits still verify it.

## Repeated source verification

`ChatBlobStore` retains at most one in-memory encoding fingerprint, bound to the
plaintext hash/length and the exact immutable `StorageProtector` instance. A cold
check captures at most 1 MiB of encoded bytes, verifies that immutable snapshot
through the normal authentication/decompression/plaintext-hash path, then records
its SHA-256. This shortcut applies only when the encoding is less than half the
plaintext size. Larger or weakly compressed encodings keep streaming verification.

Every later check opens the current file and hashes **all** stored bytes within
its captured length. Only an exact fingerprint match under the same protector can
avoid repeating decryption, decompression and plaintext hashing. Size and timestamp
never prove integrity. A changed encoding is fully verified before replacing the
fingerprint; missing/corrupt bytes or another protection key fail closed. No source
body, read authority, persistent cache or new CAS format is retained. Restart drops
the fingerprint; collection still checks current bytes, not a prior audit result.
The raw and compressed paths share one bounded hash loop, including prefix reads
and provisional index visitors.

## Fail-closed collection

Background workspace source preparation joins the same protocol through
`RuntimeStorageActivity`: it acquires `run-registry.lck` before opening a unique
`run_storage_*.lck`, releases coordination and holds its activity lock until work
stops. Existing external-run detection therefore blocks maintenance without a
synthetic chat/run or another index. A separate cancellable `source-reader.lck`
serializes heavy source reads without holding maintenance coordination while waiting.

Collection always performs a fresh audit inside the same maintenance window; it never trusts a previous UI report. The gate prevents new chat/VBA operations from starting and rejects collection while any local or external run remains active.

Deletion is allowed only when reachability is complete. Any unreadable, corrupt, unsupported, misplaced, or projection-invalid source blocks all deletion. Managed roots and discovered source/blob entries that are reparse points, symlinks, or junctions are never traversed and are treated as unreadable/blocking. An incomplete final JSONL row also blocks GC even though normal append recovery can later trim that row. Missing or corrupt referenced content is never an orphan and is never deleted.

Resource journal discovery inventories all files, independently of extension.
Authority, revision and view journals must remain directly under
`resource-authority`, named `<64 lowercase hex>.authority.jsonl`,
`.revisions.jsonl` or `.views.jsonl`. Renamed, case-changed or nested journals block
collection, including unpublished revision/view roots. Only known sibling
document metadata, lock files, the mutation journal and
`outlook-archive-cache/<64-hex-document>/<64-hex-id>.ref.json` pointers and their
`.ref.json.lock` siblings are admitted; the existing
mutation/cache owners scan their references. Unsupported names and unreadable or
linked paths block authority replay before it can open companion journals. Audit
and collection do not move or repair these sources.

Only canonical `<first-two-hex>/<sha256>.blob` paths from the fresh orphan set are deleted, one exact file at a time. Unknown `.blob` layouts are reported and retained. Other managed recursive cleanup removes child reparse points themselves without following them. Source streams, journals, settings, keys, staging files, and directories are not modified by GC.

This collector intentionally reclaims crash leftovers and blobs from removed whole streams, not historical revisions still referenced by append-only events. Retention/pruning, redacted exports, re-keying, and disposable query indexes remain separate future operations.
