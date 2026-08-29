# VBA mutation journal

The Office document remains authoritative for current live VBA. RNAssistant keeps one separate, document-scoped append-only journal for recovery evidence and rollback snapshots; chat replay, fork, edit, undo, and deletion never replay external VBA effects.

## Durable layout

- `%AppData%/RNAssistant/vba-journals/<document-hash>/mutations.events.jsonl` is the canonical journal.
- VBA source bodies are immutable `text/x-vba` blobs in the shared SHA-256 `chat-blobs` CAS. Journal events contain only hash, byte length, content type, encryption metadata, and key id.
- The backup list is a projection of `backup.created` and retained `before` sides of module/package preparations. There are no mutable backup JSON files or inline source copies.
- Each event has a contiguous sequence, previous hash, and SHA-256 or optional HMAC-SHA256 integrity chain. A partial final JSONL row is removed before the next append; a valid unterminated row remains readable and is normalized before appending. Corruption elsewhere fails closed.
- When the same live Office document receives a new stable key after first save or Save As, the journal moves to the new canonical path and appends `document.identity.changed`. Earlier events and hashes are not rewritten; an interrupted preparation remains recoverable under the live identity.

History protection applies to the VBA journal and its CAS bodies exactly as it does to chat history. HMAC and authenticated encryption are independent and disabled by default. The key comes from the DPAPI-protected API key or a separate DPAPI-protected custom secret; no secret is written to settings, events, or blobs.

## Text representations

Pure rules live in `Core.Tools.VbaTextCanonicalizer`; `VbaPatchEngine` performs one
text replacement and returns typed status/text/match information. JSON validation,
resource guidance, guards, ordered operations and journal orchestration remain in
Office. Phase 6A changes ownership only, not stored hashes or source bytes.

Phase 6B places internal VBA list/module command construction, deterministic name
fallback and typed project/module payload validation in `Office.Vba.VbaReader`.
Callers retain the HostRuntime gate and mutation/journal ownership. A malformed
successful read is rejected and never converted into live or durable evidence;
this extraction does not change CAS bytes, journal events, reconciliation or COM.

Phase 6C moves the complete `common.vba_apply_patch` workflow and shared module
prepare/dispatch/terminal orchestration to `Office.Vba.VbaMutationService`.
`Office.Vba.VbaVerifier` owns module write/delete read-back and before/intended
assessment. `VbaToolExecutor` remains the argument/result adapter and retains
other mutation entrypoints, the reconciliation loop and package/rename journal
until their ordered switches. The journal format, CAS bytes, correlation, COM
dispatch and public wire are unchanged. The current `ToolCommand`/`ToolResult`
service seam and message-based rollback detection are explicit compatibility
debt for Phase 6D, not permanent domain contracts.

| Representation | Purpose / existing transformation |
|---|---|
| Transport / raw CAS bytes | Exact stored source bytes; CAS SHA-256 is not a normalized text hash |
| Live canonical text | `NormalizeLiveCode` / `LiveCodeSha256`: normalize real CRLF/CR to LF, remove one terminal newline; preserve other whitespace, blank lines and ownership comments |
| Package canonical text | `NormalizePackageCode` / `PackageCodeSha256`: additionally strip recognized export headers and RNAssistant ownership markers, trim outer whitespace |
| VBE-comparable fingerprint | `NormalizeVbeComparableCode` / `VbeComparableCodeSha256`: existing token-based comparison; quoted strings/bracketed names and apostrophe comment text remain significant; not replacement source |
| Package-comparable fingerprint | `PackageComparableCodeSha256`: package normalization followed by VBE-comparable normalization |

Patch inputs match actual newline characters to the current source style. Literal
backslash sequences are never decoded again. Comparison representations are never
written over the original CAS body. Every starting offset counts toward uniqueness,
including overlaps (`aaaa` / `aaa` has two matches). A replacement requires exactly
one match even when its text equals the find block. Ambiguity returns
`vba_patch_ambiguous` with the full `matchCount` and leaves source unchanged.

Ordered operations work on candidate text only. If any operation is ambiguous,
the entire patch is rejected before confirmation, backend write or creation of a
backup/prepared journal record for that patch; earlier candidate edits are not
partially dispatched. This R33 correction does not change existing recovery or
the journal protocol; Windows/VBE qualification remains open.

## Transaction protocol

After guard validation and confirmation, but before COM dispatch, every public `write`, `patch`, `delete`, and `restore` persists `mutation.prepared` with:

- stable and runtime document identity, module/type, and existence;
- live-text and VBE-comparable before/intended hashes plus exact-byte CAS references;
- rollback backup id when a before state exists;
- chat/session, run, turn, step, and tool-call correlation.

An exact patch whose ordered replacements produce the current source is already satisfied, not a mutation: its execution returns success and writes neither a backup nor journal events.

After the Office operation and read-back, one `mutation.terminal` records:

- `committed` — verified intended state;
- `not_applied` — verified before state;
- `rolled_back` — a structured backend disposition explicitly reports rollback and live state matches before; message text is never classification evidence;
- `failed` — reserved for a definite terminal failure without an uncertain external effect;
- `unknown` — live state is unreadable or matches neither side.

The typed domain outcome is only `ok`, `error`, or `unknown`. Verified intended state maps to `ok`; verified before/not-applied maps to a definite `error`; unreadable or divergent state maps to non-retryable `unknown`. Existing live components must match the recorded component type as well as the applicable source hash: a create race that leaves identical source under another type is `unknown`, not committed. Source read-back verifies the requested text/type state, not VBA compilation or runtime behavior.

Common tool results expose `mutationId`, `rollbackBackupId`, and bounded actual-effect evidence, but never the internal journal status. If terminal persistence fails after inspection, the result is non-retryable `unknown` with `terminalRecorded=false`; the prepared record stays open for later read-only reconciliation and the mutation is not replayed merely to write a terminal. Restore is not a special side channel: it validates the current guard, journals the current source as the new before/rollback state, writes the selected CAS backup, verifies it, and appends its own terminal event.

Package install/remove writes one `package.mutation.prepared` before COM dispatch. It contains package identity, session/persistent scope and every component's before/intended existence, type, normalized and VBE-comparable package source hashes, and CAS reference. The comparable hash also excludes import headers and RNAssistant ownership markers. Persistent operations retain component backup ids; temporary session injection keeps recovery references without exposing long-lived rollback backups. One `package.mutation.terminal` records the overall status plus every component's actual existence/type/hashes and whether it matches before and/or intended state. Mixed or unreadable component state is `unknown`, never partial success. Current package/rename orchestration remains executor-owned until later Phase 6 slices, but its common result already omits `packageJournalStatus`/`journalStatus` and no longer infers rollback from exception/result prose.

Phase 1B observes the existing journalled module/rename/package wrappers through
metadata-only `domain.effect.prepared/dispatched/verified` events in the chat stream.
They carry the real mutation id, call/step, observed runtime document id and
`JournalRunId` (which may precede the confirmation execution run). `verified` records
the existing assessment, including `unknown`, before terminal journal persistence;
it is not a success assertion. Optional trace failures never alter journal or tool
outcomes. Read-back, guards, recovery and the journal format are unchanged. See
[causal trace semantics](stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Recovery

On the next safe VBA access for the active document, runtime finds module and package preparations without a terminal record and compares live state with recorded before/intended hashes and types. It appends `committed`, `not_applied`, or `unknown`. Package reconciliation assesses the complete set and retains mixed per-component evidence. Recovery never retries a write, creates/deletes a component, or restores a backup automatically.

This differs deliberately from HTML navigation. HTML undo/redo only changes the active id among immutable chat artifacts. VBA undo is an explicit, confirmed restore that creates a new external mutation; there is no automatic VBA redo stack.

## Remaining work

- CAS health/reachability and fail-closed garbage collection now include every VBA journal; invalid or incomplete journals block all deletion.
- Diagnostics now rebuilds one paged module/package history from the validated journal. Its cursor pins the journal sequence snapshot, every row retains its prepared/terminal event ids and sequences, and search never scans CAS bodies.
- Per-component before/intended-after source is read and verified from CAS only when the operator opens a diff. Terminal actual existence/type/hash and before/intended match assessments remain metadata; live Office source is not silently substituted for durable evidence.
- Restore is available only when a retained before backup exists. The UI requires an explicit confirmation and then uses the normal guarded restore executor, which records a new prepared/terminal mutation.
