# Storage/context optimization — implementation evidence

Owner: Core persistence/context, Application host consumers. Task order and F/T
identities: [user plan](../proposals/RNAssistant_Architecture_and_Implementation_Plan.md).
Decision: [ADR-0019](../decisions/ADR-0019-bounded-jsonl-storage.md).
Baseline inspected at `eed95756`, after `ecb7612d`; unrelated unfinished chat-overview
and Browser edits are preserved. This file records evidence, not a second contract.

## P0 baseline — 2026-11-10

Confirmed current paths: Workspace `AppendAsync/WireResult` stores complete result
JSON, Office externalizes the envelope above 8192 chars. `SaveInternalLocked`
materializes the stored session and compares historical messages. Context restores
working sets from archive before limiting selection. `StoreBytes` publishes a temp
written by `File.WriteAllBytes`, without its own durable flush. These findings
corroborate F01/F04/F10/F23; the newer overview changes do not replace these paths.

macOS, SDK 10.0.203, source-linked net8.0 Harness/C# 7.3; temporary synthetic roots,
warm valid index, no LLM/COM. One new 14-character input, fixed page size 20:

| H | Historical messages materialized by append | Appended logical bytes | Allocated bytes | Append ms | Page ms |
|---:|---:|---:|---:|---:|---:|
| 100 | 100 | 1287 | 6,312,832 | 23.48 | 3.69 |
| 1000 | 1000 | 1286 | 53,976,608 | 113.44 | 10.05 |
| 10000 | 10000 | 1287 | 529,212,152 | 1308.08 | 101.46 |

Single samples diagnose algorithmic cost, not latency percentiles or physical I/O.
Fixture creation/full index build is excluded; each root had one initial index
build and zero suffix repairs during these appends. Allocation count covers the
calling thread. Full repair and context/source counters are separate pending gates.

Installed provider probe: Microsoft.Data.Sqlite 10.0.10.0, native sqlite 3.54.0.
Two actual processes opened the same temporary database; short transactions and
composite `(scope,chat,fact)` keys preserve two different E1s and idempotent inserts.
This verifies basic provider loading/query/transaction feasibility, not the new
journal's commit/recovery protocol or Windows shipping dependencies.

Reproduce after a Harness build:
`RNA_STORAGE_OPT_BASELINE=1 dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage optimization: baseline and index provider"`.

## Producer / consumer / recovery inventory

| Producer/facts | Consumer and recovery reason | Body owner / classification |
|---|---|---|
| ChatStore session.create/fork/commit | Conversation state, history, exact accepted calls/results, restart | CAS refs + compact semantic facts; mandatory |
| ChatStore turn/step boundaries | Run recovery, interrupted model attempt, trajectory | Compact facts; remove only where kernel transition fully supplies recovery |
| Inbox input.changed | Pending delivery, dedup, consumed receipt and wait correlation | Input body CAS; mandatory identity/state |
| AgentCommunication changed | Send/spawn/control receipts, chain membership and unknown dispatch | Body CAS; mandatory accepted policy/operation; local handshake replaced by one frame |
| ToolPack extension.accepted | Exact capability pins before next request, restart admission | Compact mandatory fact; rejection detail is diagnostic |
| ModelTracePersistence request | Actual delivery/admission + Inspector | Mandatory compact manifest; full wire body becomes opt-in diagnostic |
| ModelTrace response/chunk/failure/rejection | Inspector/provider investigation; accepted model verdict is in commit | Optional bounded trace after manifest migration; no effect authority |
| Run/tool/UI/domain causal observations | Diagnostics/trajectory | Optional; actual mutation prepared/outcome remains mandatory in its domain journal |
| ResourceAuthority commits/revisions/views | Exact head, retained source, currentness, read-back and groups | Immutable CAS + mandatory resource facts; views are rebuildable only when deterministic |
| Mutation/VBA journals | Prepared preimages, possible dispatch, recovery/unknown, read-back | Mandatory attempts/outcomes and CAS preimages; never replay effects during index repair |
| DocumentAuthorityRegistry | Stable document binding, conflict/isolation and relocation | Mandatory identity facts; no second owner in conversation index |
| Derived session/header/locators/checkpoints | Bounded reads and restart acceleration | Rebuildable index only, no history/body copies |

## P0 protocol prototype and result

`storage optimization:` passes 2 focused checks in an isolated source archive of
`d0ab9d09` plus this storage diff. The shared working tree was concurrently changing
Browser/agent APIs; no changes to those owners were copied into the validation tree.
No Office/VSTO validation or release build was run.

The prototype covers unprotected and protected encoded frame limits, create-only
headers, rotation/immutable closed segments, lost ack after flush, suffix catch-up,
valid no-LF adoption, torn suffix removal, rejection of a complete bad hash/schema,
and append→index→receipt/rebuild with two independent E1s. Rebuild preserves all
accepted journal bytes. It is not yet the production conversation store.

The repeated baseline on that isolated tree (H=100/1000/10000, K=20) materialized
100/1000/10000 messages on append and visited 101/1001/10001 in working-set recovery.
Append allocations: 6,312,896 / 53,976,568 / 529,212,280 bytes. Append logical bytes:
1287 each. Time samples varied (24/99/1941 ms); counters, not a timing threshold,
are the acceptance basis. The older `context perf: remaining hotspots` fixture
fails its pre-existing protocol assumptions (128 required recoveries → 0); it is
not used as evidence for this work. The new baseline uses a current typed checkpoint.

Existing scripted multiagent fixture passes at 1/2/4/8 recipients. Exactly 2/4/8/16
model calls, peak concurrency 1/2/3/4, request characters 130742/261746/524540/1053272.
Median durable intake ms: 42.49/21.98/25.05/24.60; idle context ms:
290.72/277.62/291.94/472.22. These are local fake-provider samples, not Office latency.

P0 completed: decision, producer inventory, baseline, provider feasibility and
fault prototype are recorded. Full two-process repair admission, production CAS
closure/GC, protected shared-index rows and Windows filesystem/provider qualification
remain gates of P2/P6/P7, not claims of this prototype. Next: §8.3, typed result/body
parity and bounded result queries on the new isolated path.

## F23 — small CAS publication

Owner: `ChatBlobStore.StoreBytes` / `StorageFileSystem`. Temporary CAS bytes now get
an explicit `FileStream.Flush(true)` before atomic name publication. Removed
`File.WriteAllBytes` from this publication path. Existing compression/encryption
checks cover byte fidelity; real power-loss/rename durability remains a Windows
gate. This does not change CAS content identity or authorize deletion of old data.

## §8.3 / F01, F09, F12 — shared retained read path

1. Owners: Core `ToolResultPersistence`, result materialization and context compiler;
   Office `ConversationModelSession`; Application Workspace handlers. Typed source
   reads retain raw text in CAS, with a versioned metadata header and separate
   occurrence. Large opaque JSON is retained without stripping arbitrary fields.
   Selected consumers reconstruct the envelope; a body hash is not delivery or
   mutation authority. Office assigns the exact RunId at this common boundary.
2. Removed Office full-envelope externalization, Workspace `WireResult`, the
   Workspace per-run observation dictionary and the historical live-file refresh
   loop before each request. Guards use actual delivered observations with exact
   scope/URI/revision/coverage and preserve whole/partial views. Explicit live reads,
   mutation preparation/dispatch/read-back and final workflow acceptance still
   validate addressed current sources.
3. T01/T05/T06: real Office/Workspace result adapters, fake sources, loopback scripted
   provider; one raw source body for repeated occurrences; actual next-run requests
   hydrate retained source in both hosts. Workspace sends it while the live file is
   locked, uses whole delivery despite a later partial read, and refuses a write
   after an external change. Opaque data round-trips unchanged. No COM/live model.
4. The isolated journal records accepted calls before terminal results. Bounded
   `OpenContext` restores original accepted arguments, not manufactured calls.
   Scope/run/call/step, runtime T/E allocation, ineligible terminals, duplicate
   operations/terminals and missing CAS references have explicit guards.
5. Canonical docs: Session events, Conversation protocol, Resource fabric;
   adapter/gates: MIGRATION_MAP. Production storage/context still uses ChatSession;
   F04/F06/F10/F20 are not closed by this isolated slice.

## P2a–P2c — isolated append/query/repair

Owners: `StorageJournal`, `ConversationJournal`, `JournalIndexAdmission`. Flush
precedes index transaction; index failure returns a committed receipt with
unavailable projection. Retry/catch-up preserve the accepted operation. One reducer
handles append, suffix and streaming rebuild. Keyset queries have no full root,
header, deep OFFSET or history count. Runtime T/E counters are stream/chat scoped.

Repair drains only short metadata sections and closes their connections; live run
leases and CAS activities survive. Canonical directory/header inventory rebuilds all
conversation streams in a temporary index, replacing only that derived file.
Corruption codes 11/26 trigger coordinated repair; SQL error 1 surfaces without
reset. Normal readers catch up only their stream. No-LF adoption verifies metadata
transition and body references; complete invalid records fail closed.

T03/T11/T16: two real processes append during repair with a live model lease/CAS
pin; both retain all 24 receipts. E1 remains distinct across chats. Corrupt-index
recovery discovers another scope and preserves exact receipts/aliases and accepted
JSONL bytes. Rotation/tail cases also cover AES/HMAC. Resource stream is covered by
the following P2d slice; full runtime integration, maintenance and Windows remain open.

## Comparable append curve

Same 12,000-character retained read, H completed call/result pairs plus one accepted
call, K=20. Setup excluded. Both stores use the common body format; the candidate
also allocates its terminal E receipt. macOS source-linked net8.0/C# 7.3 Harness:

| H pairs | Aggregate messages materialized | New records read | Aggregate allocated bytes | New allocated bytes | New restart records |
|---:|---:|---:|---:|---:|---:|
| 100 | 201 | 3 | 21,964,632 | 915,792 | 21 |
| 1000 | 2001 | 3 | 206,874,616 | 915,784 | 21 |
| 10000 | 20001 | 3 | 3,320,973,456 | 916,592 | 21 |

Logical append bytes aggregate/new: 2725/2638, 2729/2646, 2735/2653. New metadata
reads: 4779/4800/4821 bytes (boundary and exact call, no archived bodies). Restart:
one boundary + 10 call/result pairs. CPU ms aggregate/new: 52.03/2.13, 430.55/1.90,
12577.43/3.44. Wall ms: 56.40/6.36, 383.65/6.45, 12315.06/8.21. Single samples,
not latency percentiles or physical I/O. CAS verification stays outside append
locks; repeated hashing/decoding F14 remains open.

Reproduce with `RNA_STORAGE_OPT_COST=1` and filter
`storage optimization: comparable append cost curve`. Default focused checks skip
the two expensive measurement fixtures. Windows timing/power loss, resource
multi-target linkage, GC closure, bounded production consumers and aggregate-path removal remain
open; no production storage cutover is claimed.

## Existing unrelated failures

These reproduce on clean `d0ab9d09` without this diff: oversized source continuity
expects the retired developer role; archived read presentation disagrees with the
raw media hydration summary; public Office routing creates HTML without an explicit
workspace. They are not passing gates here. The changed Workspace confirmation/
unknown-effect and oversized capability checks pass with explicit delivered
after-state/CAS ownership.

## P2d — one resource publication and exact conversation linkage

1. F07 (isolated path): Core `ResourceJournal` owns one physical resource stream,
   logical scopes, prepared/possible-dispatch/outcome facts and bounded head/revision/
   view lookups. `JournalIndex` now owns the shared disposable schema and repair;
   conversation-local copies of schema/cursor SQL were removed.
2. Exact attempt binding is persisted with the accepted call. Resource JSONL flush
   precedes index and conversation attachment. Missing attachment/ack is resolved
   from immutable receipts without dispatch. Unknown remains ineligible; delayed
   verified proof is an append-only linkage, never a rewrite/resume of the old run.
   A newer head survives a delayed older read-back.
3. No production resource path was replaced in this isolated slice. The former
   per-scope production authority journals remain until semantic-owner, multi-target,
   closure and consumer gates; no adapter or dual-write connects the two generations.
4. T04 plus T03/T11/T16 regressions: plaintext/AES-HMAC, resource index failure with
   current conversation cursor, crash before terminal, lost terminal ack, late proof
   after terminal/cancel, foreign-call rejection and combined streaming rebuild.
   Seven focused storage checks pass in source-linked macOS net8.0/C#7.3 Harness;
   two optional expensive curves skip in this run. No Office/VSTO build.
5. Same fixture retains exactly two simulated file writes before/after retries and
   rebuild. Duplicate terminal/link adds zero JSONL bytes. Exact revision/view query
   reads at most two records (boundary + locator), no body hydration or history walk.
   CPU/allocations/timing comparison for resource publication was not measured.
6. Open: multi-target/removal publications, one physical source across workspaces
   through the existing semantic owner, real Windows COM/power loss, production
   integration and GC/backup. T04 integration and T13/T14 close those gates.
7. ADR-0019, PROGRESS and MIGRATION_MAP updated. Next dependency is P5a send/wait;
   existing canonical production contracts remain active until their consumer cutover.

## P5a — isolated local send and wait correlation

1. F19 (new-generation contract): `ConversationJournal.Delivery` indexes aliases
   inside one exact scope and accepts input/receipt/membership in one bounded frame.
   Bodies stay in CAS; accepted-call, policy, root/chain and participant guards run
   under one append lock. Pending input never starts a run implicitly.
2. Exact own message ID resolves expected peer/reply or explicit assignment outcome.
   Applied input keeps correlation after pending eviction. `input.changed` checks
   revision and exact accepting run; retry of an older transition returns current
   state without rolling it back. Applied peer text/provenance reconstructs from CAS.
3. No new local Prepared/Accepted handshake exists. Production Application send/
   inbox still uses the previous store until P5b/P7; no dual-write or fallback added.
4. T10/T11: plaintext/AES-HMAC; before-frame and lost-ack faults; index failure;
   changed payload, foreign scope/peer/root, duplicate resolution and stale run
   rejection; early applied reply before wait; restart and combined index rebuild.
   Two actual processes contest the last slot (one accepted) and retry one operation
   (one message ID). Focused checks pass on source-linked e8d719b6 + changes,
   macOS/net8.0/C#7.3. P2d regression passes against the shared schema.
5. One accepted send advances stream sequence by one; retry adds zero bytes.
   Wait for one request reads at most three metadata records, with zero CAS body
   hydration. No CPU/allocation/latency comparison claimed for this slice.
6. Open: spawn, durable control/wait checkpoint, bounded live Inbox integration,
   production notify recovery, actual Windows and cutover/GC. P5b/T10–T12 and P7
   close these gates. Existing application policies and kernel remain authoritative.
7. Session events and MIGRATION_MAP updated. New owner remains isolated; next P5b.

## P5b — atomic spawn, run and control storage contracts

1. F19/F21 foundation: same `ConversationJournal` now accepts child metadata,
   membership and optional initial input in one frame. Child preserves captured
   model/policy and receives no copied history. Run state keeps kernel summary,
   limits, exact invocation, CAS body and explicit nested references, without
   historical messages or accepted-ID arrays. It is a semantic kernel checkpoint,
   not a second history projection. `ConversationLeaseService.ForScope` supplies
   one future scope/chat lease namespace for both hosts.
2. Control version/hash, run, invocation and pause token distinguish requested pause,
   acknowledged safe boundary and release. User/recovery pause is protected; pending
   inputs stay pending. Release does not change kernel lifecycle or start a run.
   Counts/budget/policy cannot reset in a continuation; unknown terminal facts stay.
3. A concrete production owner bug was removed: stale `ConversationRunHandle`
   cancellation/disposal matched only chat/run and could affect a later invocation
   of the same Run. Handles now pin the owner's invocation ID. Existing explicit
   application commands retain their guards. Old production journal/Inbox replay
   and lease namespaces are not switched yet; no dual-write.
4. T10–T12 on source-linked e8d719b6 + changes, macOS/net8.0/C#7.3: atomic/empty
   spawn, before-write/lost-ack, two-process last-slot race, model/policy inheritance,
   saved absolute wait restored by the existing kernel continuation, exact host
   lease, stale invocation handles, stale control version, protected Stop/recovery,
   missing nested CAS ref, late release, and combined rebuild. All 12 focused storage
   checks pass (two optional measurement fixtures skip). No COM/VSTO qualification.
5. Spawn adds exactly one frame; retry adds zero bytes. Current run lookup reads at
   most two records; checkpoint construction never walks ChatSession history.
   CPU/allocations/latency comparison for P5b was not measured.
6. P5b is not fully closed: live Inbox page loading/idle eviction, notify recovery,
   actual adapter admission against these contracts and production cutover remain.
   Existing live-owner guards are still required; durable control alone grants no
   dispatch/resume authority. Windows evidence remains open.
7. Session events, Conversation protocol and MIGRATION_MAP record these boundaries.
   P6a can proceed independently under §8.4; remaining P5b consumers join P7.

## P6a — mandatory manifest and optional bounded diagnostics

1. F02/F03/F05 (raw diagnostics): Core `ModelTracePersistenceService` and
   `LlmClient` now retain a mandatory compact request/delivery manifest before HTTP.
   Office and Workspace use the same opt-in capture setting. Inspector distinguishes
   exact temporary bytes, normalized stream JSON and unavailable/manifest-only state.
2. Manifest freezes wire hash/length, compiler module identity, final instruction
   hashes, response schema/generations, preparation timing, settings and provenance.
   Only 16 excluded candidates remain alongside ordered selection and reason counts.
   This does not promise reconstruction of old wire from current renderer/source.
3. Removed `SessionTraceWriteQueue`, terminal queue drain, ordinary durable SSE and
   duplicated raw debug-file writes. Exact capture uses one RAM buffer, no raw CAS
   roots: 64 MiB total / 8 MiB body / 256 entries / 10 minutes. Limits are defensive
   defaults; optimal latency/throughput values were not measured. Core/Inspector
   trace owners: +233/-320 = -87 production lines (shared callers/csproj excluded).
4. T15 and existing Inspector/golden-attachment/cost/schema-size/causal/SSE checks
   pass on e8d719b6 + changes, macOS/net8.0/C#7.3. All 13 focused storage checks
   pass (two opt-in cost fixtures skip); nine targeted regressions pass. Actual
   Workspace pause wire passes. Steering's tool-entry failure also reproduces on
   clean d0ab9d09 and is already recorded in RISK_REGISTER; it remains open.
5. Fixture: request + response + 100 chunks create zero raw CAS payloads and zero
   chunk events with diagnostics off; buffer retains zero bytes. At 1000 excluded
   candidates, 16 are retained and 984 counted. A 256-byte injected buffer remains
   within its cap after 10 captures, drops oversized captures, expires to zero and
   never changes mandatory acknowledgement. Old raw/chunk behavior is established
   by the removed producer, not a new before/after latency measurement.
6. Mandatory append failure still stops dispatch; optional capture/response failure
   does not. Compact outcome metadata still writes synchronously. Old retained CAS
   traces are not deleted. P3 archive selection/full ContextReceipt, P6b closure,
   production cutover and Windows/Office/visual qualification remain open.
7. Session events, CAS maintenance, Screen Map, PROGRESS and MIGRATION_MAP updated.
   No raw-storage adapter/dual-write remains; isolated journal consumers remain the
   explicit P7 dependency recorded above.

## P6b — independent canonical closure and isolated backup/restore

1. F03/F05/F23 maintenance boundary: `StorageJournal.Inventory`, existing
   `JournalIndex` reducers and `CasMaintenanceService` inventory every canonical
   stream independently of the production index. Typed owners enumerate actual
   CAS refs; observed checksums do not become false roots. Run bodies prove nested
   prepared-state closure against their frozen fact.
2. GC builds/checks a temporary projection without changing canonical bytes or
   the production index. Unknown/misplaced/linked/incomplete sources fail closed;
   no tail recovery runs during audit. Fresh new-generation orphans get a one-hour
   defensive grace. Existing maintenance locks exclude all scoped runs/activities.
3. Removed duplicate repair inventory in favor of the same strict scanner. The old
   assumption that only legacy streams own CAS is removed. Backup/restore uses no
   alternate replay store or index copy. New maintenance files add 346 lines; the
   existing collector changes +16/-2 (= +360 lines for those owners). Small shared
   journal/reducer/lease edits are excluded from this subtotal.
4. T13: 15 focused storage checks pass (two optional curves skip), plus existing
   fail-closed/reparse/maintenance-gate checks; final restore fault additions pass.
   Plaintext and AES/HMAC include a stream without a cursor, nested checkpoint,
   accepted peer input, cross-owner unknown outcome, unchanged JSONL and rebuild.
   Two real processes prove CAS-to-commit activity excludes deletion. Validation
   remains source-linked e8d719b6 + owned changes, macOS/net8/C#7.3; later independent
   HTML commits in the shared workspace are excluded from this validation copy.
5. Fixture deletes one old orphan, retains one young orphan and every canonical
   body without a production index; backup omits index/orphans and restores the
   same canonical bytes. Unknown entry/torn tail blocks all GC. Corrupt/missing
   backup and wrong key publish no restored destination. CPU/allocation/latency
   comparison is not measured; linear maintenance inventory is intentional.
6. Internal backup requires a pure new-generation root and copied settings/salt/key
   profile. DPAPI fixtures prove byte preservation only, not Windows decryption or
   cross-user portability. Production cutover/backup UI, full semantic resource
   integration, P3/P4 and remaining P5 consumers remain open; no overall completion.
7. CAS maintenance, session events, PROGRESS, MIGRATION_MAP and Harness README are
   updated. No GC fallback/dual-write was added. Independent P4 text/source work
   proceeds while the new root remains isolated until P7 integration gates.

## P4a — one verified small-body cache and exact text windows (2026-11-11)

1. F11/F13/F14 (small-source slice): common `ChatBlobStore` now owns one shared
   bounded verified entry and lazy UTF-8/line metadata. `ResourceTextWindow` uses
   indexed coordinates, so both host consumers receive the same selection behavior.
2. Every hit still opens/hashes current encoded CAS bytes under the exact key.
   Same-key misses share one decode. Returned bytes are copies; read leases are
   immutable and release their pin on Dispose. Cache existence grants no evidence
   or mutation authority. Explicit live reads still recapture the addressed file.
3. Removed the separate per-instance fingerprint and repeated small-body decode/
   full-text window scan. One-MiB plaintext / one-MiB-plus-64-KiB encoding admission
   caps are defensive. Larger/explicit-seek reads keep existing stream/scratch paths.
4. T07: plaintext, compressed, encrypted and combined encodings; concurrent reads,
   shared consumers, eviction/dispose, same-size/same-time corruption, missing CAS,
   cancellation, CR/LF/Unicode/surrogate bounds and explicit live drift. All 16
   storage checks pass (two expensive opt-ins skip), plus existing exact-window,
   protected seek and streamed CAS regressions. macOS/net8/C#7.3, source-linked
   e8d719b6 + owned changes; no Office/VSTO build or real Windows qualification.
5. Same 72,000-character body / 24 windows: 1,728,000 inspected characters before,
   72,144 after (one index + selected spans); one decode/index. Each hit still reads
   the complete stored encoding. Calling-thread allocations and single-run wall
   samples below include cold population; CPU/physical I/O were not measured.

   | Encoding | Allocated before → after | Wall ms before → after |
   |---|---:|---:|
   | Raw | 9,883,040 → 2,892,928 | 30.76 → 2.81 |
   | Compressed | 11,871,688 → 2,880,104 | 27.94 → 1.42 |
   | Encrypted | 13,940,168 → 2,976,728 | 29.03 → 1.51 |
   | Both | 15,893,224 → 2,963,336 | 27.72 → 0.74 |

   These are local samples, not a production/Windows latency benchmark.
6. Large-source indexing, remaining duplicate live-capture verification, VBA/table
   pushdown, bounded search and P3 aggregate/history paths remain open. This cache
   does not close the complete P4 or P7 gate. Active leases may pin evicted entries;
   their existing owner/concurrency lifetime remains necessary.
7. CAS maintenance and Resource Fabric describe the shared owner and limits.
   No new public registry, cache family, SDK, disk format or compatibility adapter.
