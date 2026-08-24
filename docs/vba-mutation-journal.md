# VBA mutation journal

The Office document remains authoritative for current live VBA. RNAssistant keeps one separate, document-scoped append-only journal for recovery evidence and rollback snapshots; chat replay, fork, edit, undo, and deletion never replay external VBA effects.

## Durable layout

- `%AppData%/RNAssistant/vba-journals/<document-hash>/mutations.events.jsonl` is the canonical journal.
- VBA source bodies are immutable `text/x-vba` blobs in the shared SHA-256 `chat-blobs` CAS. Journal events contain only hash, byte length, content type, encryption metadata, and key id.
- The backup list is a projection of `backup.created` and retained `before` sides of module/package preparations. There are no mutable backup JSON files or inline source copies.
- Each event has a contiguous sequence, previous hash, and SHA-256 or optional HMAC-SHA256 integrity chain. A partial final JSONL row is removed before the next append; corruption elsewhere fails closed.

History protection applies to the VBA journal and its CAS bodies exactly as it does to chat history. HMAC and authenticated encryption are independent and disabled by default. The key comes from the DPAPI-protected API key or a separate DPAPI-protected custom secret; no secret is written to settings, events, or blobs.

## Transaction protocol

After guard validation and confirmation, but before COM dispatch, every public `write`, `patch`, `delete`, and `restore` persists `mutation.prepared` with:

- stable and runtime document identity, module/type, and existence;
- raw and VBE-comparable before/intended hashes plus CAS references;
- rollback backup id when a before state exists;
- chat/session, run, turn, step, and tool-call correlation.

After the Office operation and read-back, one `mutation.terminal` records:

- `committed` — verified intended state;
- `not_applied` — verified before state;
- `rolled_back` — runtime reported rollback and live state matches before;
- `failed` — reserved for a definite terminal failure without an uncertain external effect;
- `unknown` — live state is unreadable or matches neither side.

Tool results expose `mutationId`, `rollbackBackupId`, and `journalStatus`. Restore is not a special side channel: it validates the current guard, journals the current source as the new before/rollback state, writes the selected CAS backup, verifies it, and appends its own terminal event.

Package install/remove writes one `package.mutation.prepared` before COM dispatch. It contains package identity, session/persistent scope and every component's before/intended existence, type, normalized source hash and CAS reference. Persistent operations retain component backup ids; temporary session injection keeps recovery references without exposing long-lived rollback backups. One `package.mutation.terminal` records the overall status plus every component's actual existence/type/hash and whether it matches before and/or intended state. Mixed or unreadable component state is `unknown`, never partial success.

## Recovery

On the next safe VBA access for the active document, runtime finds module and package preparations without a terminal record and compares live state with recorded before/intended hashes and types. It appends `committed`, `not_applied`, or `unknown`. Package reconciliation assesses the complete set and retains mixed per-component evidence. Recovery never retries a write, creates/deletes a component, or restores a backup automatically.

This differs deliberately from HTML navigation. HTML undo/redo only changes the active id among immutable chat artifacts. VBA undo is an explicit, confirmed restore that creates a new external mutation; there is no automatic VBA redo stack.

## Remaining work

- CAS health/reachability and fail-closed garbage collection now include every VBA journal; invalid or incomplete journals block all deletion.
- Diagnostics still needs paged VBA mutation queries and before/after diff views.
- Diagnostics still needs package-level navigation and per-component before/intended/actual diff views.
