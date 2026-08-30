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
assessment. Phase 6D replaces the temporary command/result seam with typed
document/read/backend/journal ports and `Ok/Error/Unknown`; rollback is never
inferred from prose. Phases 6E–6G move whole-module write, delete and restore
guards, dispatch and verification into that same owner. Restore uses a dedicated
guard that binds the exact backup id/module/type/loaded-source hash together with
the current target existence/source hash before confirmation; changing either
side blocks the action before preparation/dispatch. Phase 6J moves rename guard,
two-identity preparation, typed backend action, read-back and recovery into the same
`Office.Vba.VbaMutationService`; `VbaToolExecutor` remains only the argument/result
adapter plus serialized reconciliation caller. Package journal/read-back/reconciliation
belongs to `Office.Vba.VbaPackageService`; CAS bytes, COM dispatch and public result
wire remain unchanged.

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

The typed domain outcome is only `ok`, `error`, or `unknown`. Verified intended state maps to `ok`; verified before/not-applied maps to a definite `error`; unreadable or divergent state maps to non-retryable `unknown`. Existing live components must match the recorded component type as well as the applicable source hash: a create race that leaves identical source under another type is `unknown`, not committed. Delete `ok` requires verified absence after the compare-and-swap backend action; backend success while the component remains is not success. Source read-back verifies the requested text/type state, not VBA compilation or runtime behavior.

Common tool results expose `mutationId`, `rollbackBackupId`, and bounded actual-effect evidence, but never the internal journal status. If terminal persistence fails after inspection, the result is non-retryable `unknown` with `terminalRecorded=false`; the prepared record stays open for later read-only reconciliation and the mutation is not replayed merely to write a terminal. Restore is not a special side channel: its typed service reloads and validates the exact guard-bound CAS backup, rechecks current target state/type, journals current source as the new before/rollback state, performs one create-or-replace action, verifies source/type, and appends its own terminal event. Backup substitution, missing guard evidence, stale target, and incompatible existing component type fail before journal/dispatch.

Package install/remove writes one `package.mutation.prepared` before COM dispatch. It contains package identity, session/persistent scope, exact required ownership marker and every component's before/intended existence, type, normalized and VBE-comparable package source hashes, explicit before-marker presence/evidence, and CAS reference. Session records additionally carry one `LifecycleId` shared by install and cleanup. The comparable hash excludes import headers and RNAssistant ownership markers, while verification separately requires the recorded marker state. Install passes the prepared existence/type/comparable-source/marker guard to the shared backend and rejects post-prepare drift before its first component mutation. Persistent operations retain component backup ids; temporary session injection keeps recovery references without exposing long-lived rollback backups. One `package.mutation.terminal` records the overall status plus every component's actual existence/type/hashes and whether it matches before and/or intended state. Mixed, marker-divergent or unreadable component state is `unknown`, never partial success. Package orchestration belongs to the typed package service. Rename deliberately keeps this existing two-component durable wire through a narrow `IVbaRenameJournal` adapter over the same store, while its domain API and owner are rename-specific; no second journal, generic transaction layer or dual-write exists. Common results omit internal journal status and never infer rollback from exception/result prose.

Rename guard preparation resolves and binds the exact source/destination names,
source live hash, source component type and code-only UserForm state before
confirmation. Execution re-reads both identities, persists the two-name preparation,
and passes source hash/type CAS evidence to the host backend before one rename
dispatch. `ok` requires old-name absence plus new-name source/type match and a durable
terminal. Complete before state is a definite error; both names present, both absent,
divergent type/source or unreadable state is non-retryable `unknown`. Terminal loss
leaves the preparation open; the next safe access only classifies complete-before,
complete-intended or mixed state and never repeats rename.

R41 is fixed host-neutral in 6I. A completed session install and later cleanup are
correlated by durable lifecycle id, and probe combines live ownership/source/type
with all lifecycle records. A missing or unknown cleanup remains visible even after
the marker is stripped: macro execution and persistent overwrite are blocked.
Read-only reconciliation remains the only automatic recovery action. Exact cleanup
requires a new policy-authorized operation and fresh prepared record; no recovery
replay/remove/overwrite or macro run is synthesized.

Phase 1B observes the existing journalled module/rename/package wrappers through
metadata-only `domain.effect.prepared/dispatched/verified` events in the chat stream.
They carry the real mutation id, call/step, observed runtime document id and
`JournalRunId` (which may precede the confirmation execution run). `verified` records
the existing assessment, including `unknown`, before terminal journal persistence;
it is not a success assertion. Optional trace failures never alter journal or tool
outcomes. Read-back, guards, recovery and the journal format are unchanged. See
[causal trace semantics](stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Recovery

On the next safe VBA access for the active document, runtime finds module and package preparations without a terminal record and compares live state with recorded before/intended hashes, types and required package ownership marker. It appends `committed`, `not_applied`, or `unknown`. Package reconciliation assesses the complete set and retains mixed per-component evidence. Recovery never retries a write, creates/deletes a component, runs a macro, or restores a backup automatically. A completed session install without a committed correlated cleanup remains `session_cleanup_required`/`recovery_required` until exact explicit cleanup succeeds.

This differs deliberately from HTML navigation. HTML undo/redo only changes the active id among immutable chat artifacts. VBA undo is an explicit, confirmed restore that creates a new external mutation; there is no automatic VBA redo stack.

## Remaining work

- CAS health/reachability and fail-closed garbage collection now include every VBA journal; invalid or incomplete journals block all deletion.
- Diagnostics now rebuilds one paged module/package history from the validated journal. Its cursor pins the journal sequence snapshot, every row retains its prepared/terminal event ids and sequences, and search never scans CAS bodies.
- Per-component before/intended-after source is read and verified from CAS only when the operator opens a diff. Terminal actual existence/type/hash and before/intended match assessments remain metadata; live Office source is not silently substituted for durable evidence.
- Restore is available only when a retained before backup exists. The UI requires an explicit confirmation and then uses the normal typed guarded restore workflow, which binds the exact backup and records a new prepared/terminal mutation.
