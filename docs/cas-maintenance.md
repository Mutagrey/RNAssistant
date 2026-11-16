# CAS health and garbage collection

Publication serializes the final existence check and move with the existing
directory write lock; encoding stays outside that lock. This also closes the
[Unix File.Move check/rename race](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs).

`chat-blobs` is one immutable SHA-256 CAS shared by chat model payloads, artifact bodies, committed attachments, extracted attachment text, file snapshots, web manifests/verification records and VBA before/intended/backup source. Durable roots include chat `*.events.jsonl`, VBA journals, resource authority revisions/views and resource mutation journals. CAS files are not a second index.

## New-generation journal closure and backup — 2026-11-10

The collector independently inventories `storage-v2/conversation-streams/<scope-hash>`
and `storage-v2/resource-authority/resource-stream`, including streams absent from
SQLite. `storage-v2/generation.json` is create-only generation authority; legacy
`chats/` and legacy resource JSONL remain separate and are never inferred as a new
stream. Segment gaps, unknown
files, links, unknown fact kinds, incomplete tails and mismatched reducer cursors
block collection. Only the known append/publication locks and unpublished create-only header
staging names are noncanonical. Maintenance does not truncate/adopt tails.

Native resource-owner siblings (2026-11-16): inventory validates the entire
`storage-v2/resource-authority` directory, not only `resource-stream`. The only
other accepted entries are regular, empty `mutation-<64 lowercase hex>.lck` files.
Unknown files, nested directories, links or nonempty lock files block canonical
inventory and GC. Internal backup invokes the same owner check before staging;
unexpected bytes cannot disappear from a successful backup by resembling a lock.
This corrects the former old-directory check and does not extend the supported
backup to a complete mixed-generation application profile.

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

Native forks retain a CAS membership manifest pointing to original journal
frames; ancestor deletion never deletes those canonical frames or their CAS roots.
Shared resource forks retain exact refs and existing resource-owner roots without
copying payloads or publishing heads. Existing replay rebuilds inherited guards.
Multi-generation deletion, rebuild and Workspace journal/CAS restore are checked,
including a selected shared Plan after later publication and source deletion.
Local Task/checkpoint forks inherit original snapshot locators as well; canonical frames
retain their nested CAS roots. Task fork authority survives source deletion and
Workspace journal/CAS restore. Two deleted ancestors, continued child compaction and
Workspace journal/CAS restore are checked without a copied metadata/body stream.
Workspace original forks inherit accepted attachment-message locators and reuse the
same original/extraction/analysis CAS closure. Two ancestor deletions, index rebuild
and Workspace journal/CAS restore retain both visible attachments and metadata whose
source occurrence is hidden. No copied body stream or extra root owner is introduced.
Native definition forks root their immutable copy-link CAS body through the existing
conversation reference enumerator. Resource journals retain copied revision payloads
and dependencies; supplied-note payloads remain in the typed context closure.
Nested copies survive deleted ancestors and index rebuild. Isolated journal/CAS
restore preserves copy provenance without manufacturing authority publication;
missing copy CAS blocks load/rebuild. The internal backup now includes the existing
Office identity registry (see the identity extension below). Inherited per-chat
usage attribution and complete application backup remain open; this is not
Office/application restore qualification.

`ForScope` run leases join the existing `locks/run_*.lck`/`run-registry.lck` namespace.
`RuntimeStorageActivity` remains held from CAS publication to acceptance or confirmed
abort. A second-process publication and a live scope run both block collection.
Index repair has its separate short admission and need not drain live runs.

`BackupJournals`/`RestoreJournals` operate on the new canonical root while the active
runtime uses storage-v2. Backup still refuses mixed legacy roots because a complete
application backup of both generations is not yet defined; it copies every verified canonical
segment and complete CAS closure, plus existing settings, salt and DPAPI-protected
key files and the identity owners below. It does not include SQLite, orphans,
locks or optional diagnostics.
Known empty resource scope-lock files are allowed in a new root. Unexpected names,
nonempty locks, directories and reparse points do not bypass mixed-root rejection.
A SHA-256 manifest checks integrity; protected backups use HMAC authentication of
the file set. Restore creates a private root,
verifies all files/semantic closure, rebuilds the index and only then publishes a
previously absent destination. Failure cannot overwrite an existing destination.
Original source history/keys are never deleted or imported.

### Identity owners in internal backup v2 — 2026-11-15

The existing manifest allowlist now includes these exact owner files, copied
without reserializing, rebasing paths or resolving identities:

| Owner | Retained canonical data |
|---|---|
| `WorkspaceStore` | `workspaces/ws_<id>.json` and `workspaces/path-<path hash>.json` |
| `WorkspaceFileService` | `workspaces/file-locators/<path hash>.json` and `workspaces/file-moves/<attempt id>.json` |
| `DocumentAuthorityRegistry` | `resource-authority/documents.json` |

Backup holds the existing maintenance gate plus Workspace catalog/locator locks
and the document registry's own monitor/file lock through validation and copying.
The owners validate original JSON, exact names/IDs, current Workspace path pairs
and ambiguous bindings. Both source and copied owner data must pass. Historical
Workspace path associations and hidden-state differences remain valid; a moved
project's current ID record stays authoritative. Canonical conversation/resource
scopes require the corresponding Workspace/file identities, including projects
with saved resource versions but no chat. Move metadata is checked against the
latest verified canonical attempt: missing intent is allowed before dispatch,
required after possible dispatch, and never used to replay an operation.

Restore repeats owner and canonical closure validation in its private root before
publishing. A saved HTML version is resolved by the restored
`WorkspaceStore.OpenConnectedHistory` / `WorkspaceHtmlVersionService`, including
when the live project is unavailable. Reconnecting a moved live project remains
an explicit existing `ConnectProject` operation with its manifest and collision
guards; neither backup nor restore searches for a replacement path.

The internal envelope is `rnassistant-journal-backup-v2`. Earlier v1 backups are
rejected because they did not preserve these owners. No default writer, journal
format, application storage selector or production composition is switched.
The manifest remains the sole backup file inventory; no second identity registry
or fallback inference is introduced.

External project files, `.rnassistant/workspace.json` and managed trash, live Office
documents, memory-only `RuntimeBindings`, uncommitted editor state and unrelated
application stores are not included. A new unsaved Office document without a
persisted binding cannot regain its former runtime identity by locator alone;
existing journal-owned identities are retained without inventing a locator.
Historical document IDs need not all occur in the current registry: Save As and
locator collisions retain their existing owner semantics. Full application restore,
production writer quiescence/cutover and Windows qualification remain P7 gates.

Plaintext and AES/HMAC restore, unknown effects, cross-owner receipts, nested refs,
unindexed streams and corrupt/missing backup bodies are checked on macOS/net8.
DPAPI key files are copied unchanged, not decrypted/rekeyed for transport. Actual
DPAPI restore requires its original supported Windows identity; that qualification
and filesystem power-loss/Office checks remain open. No Settings UI backup command
or production format switch is claimed by these internal APIs.

## Full application backup and clean restore — In Review 2026-11-16

`rnassistant-application-backup-v3` is the application-level envelope owned by the
existing `CasMaintenanceService`. Creation holds the shared maintenance gate, plugin
exclusive lease, Workspace catalog/locator locks and document identity lock while it
inventories both storage generations. It requires complete, validated legacy and
native closure, then copies raw legacy JSONL/VBA/resource journals, canonical
`storage-v2` streams, every verified CAS object, settings, DPAPI-protected key files,
chat drafts, prompt backups, attachments, tools, skills, local assets, plugin icons,
vendor bytes, managed plugin/HTML project copies and Workspace/Office identity owners.
The index, logs, WebView profile, locks, temporary files, plugin staging/preview and
plugin result staging are excluded.

The manifest records every file's byte length and SHA-256, the protection profile and
distinct external Workspace root paths. Plaintext profiles use SHA-256 manifest
integrity; HMAC/AES profiles authenticate the manifest with the existing history key.
Restore verifies the manifest and every copied byte, validates legacy/native owners
and CAS reachability in a private staging root, rebuilds the disposable native index,
then publishes only to a previously absent destination. Existing destinations and
corrupt, incomplete or cross-profile backups are refused. The destination must be
outside the live application root; the current runtime is not switched in place.

External Workspace files, their `.rnassistant` manifests/trash, live Office documents,
memory-only bindings and unsaved editor state remain outside this envelope. Their
recorded roots let restore report missing projects and preserve existing identities
without guessing or replaying live operations. A complete machine-level recovery also
back up those external directories with the file system while no process is writing.
The same Windows user identity is required for copied DPAPI files. Old-binary
quiescence, power-loss durability and Windows/Office/WebView2 qualification remain
open release gates.

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
Both byte and streamed-file publication are create-only. An existing object or
concurrent winner must verify against the expected hash, length, format and
protector; otherwise publication fails and preserves its bytes. Supplying the
original plaintext does not authorize repair/overwrite. The existing
`StorageFileSystem.PublishNewFile` owns atomic publication; no replacement path
or new CAS format is introduced. Maintenance/recovery remains explicit.

Default ChatStore/ChatBlobStore owners use the root's SettingsService protection
provider, matching Office composition. Workspace explicitly shares that provider
with its chat/payload/file/web-snapshot owners. Missing keys or unavailable providers
fail closed, with no plaintext fallback, migration or re-encryption.

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
