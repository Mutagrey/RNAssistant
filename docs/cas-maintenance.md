# CAS health and garbage collection

`chat-blobs` is one immutable SHA-256 CAS shared by chat model payloads, artifact bodies, committed attachments, extracted attachment text, file snapshots, web manifests/verification records and VBA before/intended/backup source. Durable roots include chat `*.events.jsonl`, VBA journals, resource authority revisions/views and resource mutation journals. CAS files are not a second index.

## New-generation journal closure and backup — 2026-11-10

The collector independently inventories `conversation-streams/<scope-hash>` and
`resource-stream`, including streams absent from SQLite. Segment gaps, unknown
files, links, unknown fact kinds, incomplete tails and mismatched reducer cursors
block collection. Only the known append lock and unpublished create-only header
staging names are noncanonical. Maintenance does not truncate/adopt tails.

The same domain reducers rebuild a private temporary SQLite projection; cursor
completeness is checked against the scanned canonical ends. The production index
is neither trusted nor changed by audit. Typed journal owners enumerate CAS refs,
not observed-content checksums. Run checkpoint bodies must agree with their frozen
metadata and explicit nested prepared-state closure. Missing/corrupt new-generation
bodies block deletion. Recent new-generation orphans have a conservative one-hour
grace; age never replaces the exclusive publication/run boundary.

Native context-state (2026-11-14): the conversation owner follows typed
DocumentContext and local compaction bodies during canonical maintenance, once per
distinct CAS container. Instruction payloads and nested claim/note evidence remain
roots even after selection or visible history changes. This closes their backup /
restore and GC reachability without copying a closure into every chat header or
parsing arbitrary model text. Catalog attribution of nested context bytes remains P3.

`ForScope` run leases join the existing `locks/run_*.lck`/`run-registry.lck` namespace.
`RuntimeStorageActivity` remains held from CAS publication to acceptance or confirmed
abort. A second-process publication and a live scope run both block collection.
Index repair has its separate short admission and need not drain live runs.

[Workspace connection metadata](stabilization/BACKLOG.md#native-workspace-connection-backup--2026-11-14)
is not yet included. Restored journal/CAS evidence does not qualify complete
application/project restore or preserve Browser connections by itself.

Internal `BackupJournals`/`RestoreJournals` are exercised only on isolated new roots
until P7. Backup refuses mixed legacy roots; it copies every verified canonical
segment and complete CAS closure, plus existing settings, salt and DPAPI-protected
key files. It does not include SQLite, orphans, locks or optional diagnostics.
Known empty resource scope-lock files are allowed in a new root. Unexpected names,
nonempty locks, directories and reparse points do not bypass mixed-root rejection.
A checksum/HMAC manifest authenticates the file set. Restore creates a private root,
verifies all files/semantic closure, rebuilds the index and only then publishes a
previously absent destination. Failure cannot overwrite an existing destination.
Original source history/keys are never deleted or imported.

Plaintext and AES/HMAC restore, unknown effects, cross-owner receipts, nested refs,
unindexed streams and corrupt/missing backup bodies are checked on macOS/net8.
DPAPI key files are copied unchanged, not decrypted/rekeyed for transport. Actual
DPAPI restore requires its original supported Windows identity; that qualification
and filesystem power-loss/Office checks remain open. No Settings UI backup command
or production format switch is claimed by these internal APIs.

## Reachability audit

Native catalog usage is a disposable projection of retained conversation facts:
chat-local unique hashes/logical counters, plus advisory physical sizes from the
existing ChatBlobStore snapshot. Shared blobs/frames can be attributed to several
chats; catalog totals are not exclusive disk ownership. Neither chat_blob nor a
missing-file warning participates in closure/GC authority. Rebuild and backup/restore
still validate canonical journals and payloads independently.
See [catalog usage](session-events.md#journal-catalog-storage-usage--2026-11-13).

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

Chat trace publication holds the persistence maintenance boundary from CAS storage
through the flushed JSONL reference. A reachability snapshot cannot run between
those steps and classify that new body as an orphan. Failed/uncommitted publications
can still leave ordinary collectable orphans.

Raw HTTP/SSE capture is optional and RAM-only since P6a (2026-11-10), with byte,
entry and time limits in `ModelDiagnosticBuffer`. Compact mandatory manifests have
wire hashes/lengths, not raw CAS references. Existing historical raw payload roots
remain retained; the new policy does not authorize deleting old semantic streams
or reachable bodies. Inspector distinguishes unavailable capture from a manifest.

`StoreFile` hashes and encodes the same open source handle with cancellable,
bounded-buffer passes and stage progress. Temporary compressed/encoded writes
are removed on failure/cancellation. `OpenVerifiedRead` authenticates, decodes and
hashes the complete retained body before returning its read-only stream. Small
reads can pin the verified in-memory entry; larger reads keep the CAS handle until
disposal. Raw bodies are seekable; compressed/encrypted large bodies remain sequential
and never spill plaintext to a scratch file. Reads also
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

P4a (2026-11-11) replaces the per-instance encoding fingerprint with one shared
storage-owned entry, keyed by canonical CAS path, plaintext hash/length and the
exact `StorageProtector` instance. Small bodies (up to 1 MiB plaintext and 1 MiB +
64 KiB encoded) reuse verified immutable bytes. UTF-8 text and line offsets are
lazy and bounded by that body. These are defensive caps, not measured optimal
settings. Only one entry is cached; active read leases may independently pin it
until disposal. No writable array is exposed. Larger bodies keep the prior bounded
encoding fingerprint where useful and use streaming verification otherwise.

Every hit still opens and hashes **all current encoded bytes**. Missing/corrupt
content or a different key fails closed; file size/time alone never establishes
integrity. A miss verifies/decrypts/decompresses one bounded encoded snapshot before
publishing its immutable entry. Concurrent same-key misses share that decode.
Small `ResourceTextWindow` reads use the retained exact line/character metadata;
large sources and explicitly seekable consumers keep existing streaming/scratch
paths. No offsets seek into compressed bytes. Explicit live source reads still
capture their addressed live file; cache presence grants no model delivery or
mutation authority. Restart discards the cache; GC verifies current bytes.

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
