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

## P3a — bounded excluded-context receipts (2026-11-11)

1. F10/F11, receipt portion: `ModelContextCompiler.DescribeMessages` keeps every
   delivered entry and at most 16 excluded examples; remaining unique source IDs
   contribute reason counts. Both hosts consume the same compiler result.
2. `LastContextReceipt` and the mandatory dispatch manifest preserve these counts.
   Excluded examples retain no delivery claim; selected message order/coverage is
   unchanged. Sink-side defensive trimming adds counts without mutating the receipt.
3. Removed the full excluded presentation list, source GroupBy/intermediate array
   and per-exclusion atom search. Only sampled exclusions resolve final body
   locations, with each retained body checked once. Core delta: +42/-11, net +31.
4. T02/T05 (receipt subset): the new scale fixture fails on the baseline and passes
   after the change; four relevant manifest/presentation/retained-body regressions
   pass. It also checks unchanged model wire, all 40 selected entries, duplicate
   source identity and zero excluded-body hydration. macOS/net8/C#7.3, isolated
   e8d719b6 + owned changes as above; no Office/VSTO build.
5. Fixed one-message active set; input construction excluded from measurements.
   Compile + receipt access/serialization, single local samples:

   | Excluded H | Receipt bytes before → after | Allocated before → after | Wall ms before → after |
   |---:|---:|---:|---:|
   | 100 | 29,464 → 5,473 | 589,352 → 266,960 | 4.34 → 3.34 |
   | 1,000 | 286,864 → 5,474 | 3,820,104 → 232,624 | 13.10 → 0.44 |
   | 10,000 | 2,860,864 → 5,475 | 37,834,304 → 1,289,568 | 140.50 → 1.32 |

   Detail entries: H+1 → 17. No payload hydration in either case. CPU and physical
   I/O were not measured; these timings are not Windows/production latency claims.
6. Sources still arrive as a full collection and are indexed once for attribution;
   allocations therefore remain H-dependent. Working-set restart, bounded metadata
   selection and snapshot JSON roundtrips remain P3/F10/F11 work. No complete P3 claim.
7. Conversation protocol, Session events, MIGRATION_MAP, PROGRESS and Harness guide
   are updated; no adapter or second store. Product `16.2.0-dev` follows the initial
   transition in the version policy, not a storage cutover or qualified release.

## P3b — typed frozen skill catalog (2026-11-11)

1. F11, catalog portion: `SkillCatalogSnapshot` stores typed detached definitions
   and Arsenal metadata instead of JSON strings. Both host consumers use this owner.
2. Getters copy mutable records/lists; immutable Markdown/resource/payload values
   are shared. Constructor inputs and returned nested dependencies/references cannot
   change later requests, revisions or publication generation.
3. Removed catalog serialization and both getter deserializations. Core delta:
   +36/-13, net +23 lines. No cache, alternate catalog or compatibility path.
4. T02/T05 subset: frozen-copy fixture and existing selection/compaction/scope,
   dependency/revocation and exact-dispatch checks pass on macOS/net8/C#7.3.
   Source-linked validation follows the same isolated baseline as P3a. The old
   selected-skill composition test fails its tool/user role expectation on both
   candidate and pre-change `35c5bc11` snapshot; it is in RISK_REGISTER.
5. Same 16 definitions × 12,000 characters, 24 Skills+Metadata accesses: 4,608,000
   instruction characters read in both cases; calling-thread allocations
   15,836,840 → 190,952 bytes; single-sample wall 12.96 → 0.66 ms. Construction/hash
   and warm-up excluded. No I/O or CPU measurement; not a Windows latency claim.
6. Copy work still depends on metadata count. ModelContextSnapshot JSON getters,
   full-session runtime, working-set recovery and bounded preparation remain open
   P3 gates. No Office qualification or overall optimization completion.
7. Skills, MIGRATION_MAP, PROGRESS, risk register and Harness guide updated;
   product patch `16.2.1-dev`, no temporary adapter or storage cutover.

## P3c — typed model snapshot and context copies (2026-11-11)

1. F11, snapshot portion: `ModelContextSnapshot` has its own model file, registered
   in the old-style Core project. Typed copy methods belong to the metadata types;
   compiler and wire provenance reuse them instead of JSON cloning.
2. Constructor/getter/repair copies detach mutable messages, receipt parts, Arsenal,
   agent addresses, claims, attachments, pending patch diagnostics/previews and
   recursive resource targets. Immutable text/refs/evidence are reused without I/O.
   Actual delivery and live guards keep their previous owners and semantics.
3. Removed snapshot JSON strings and all three deserializing getters, compiler
   message JSON clones, four wire-provenance roundtrips and the second repair copy.
   Production C# delta: +216/-38, net +178; explicit typed copying replaces generic
   serialization of the mutable graph. No new store, generic clone framework or cache.
4. T02/T05/T06 subset: new snapshot isolation fixture plus eight targeted regressions
   pass, including actual Office/Workspace adapters with fake/scripted sources,
   exact read delivery, partial/whole guard behavior and request repair/budget.
   macOS/net8/C#7.3, same isolated source-linked e8d719b6 + owned changes; not a full
   current-HEAD or Windows/Office qualification. An independent Office HTTP fixture
   in the shared working tree is excluded from this slice.
5. Same six 18,000-character messages, 24 accesses (2,592,000 characters), snapshot
   construction plus getter copies: allocations 18,810,768 → 204,704 bytes; one local
   wall sample 15.04 → 0.56 ms. Input construction and serialization comparisons
   are excluded. Neither path does payload I/O; CPU/physical I/O are not measured.
6. Normal compiler selection still receives full-session inputs; runtime history,
   progress/checkpoint delegates, working-set restore and repeated body-key work
   remain P3/F10/F11 tasks. No overall bounded-context or P3 completion claim.
7. Conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide updated; retained
   source wording now matches the already-migrated request/live-guard behavior.
   No temporary adapter; the new journal generation remains isolated until P7.

## P3d — bounded live run metadata (2026-11-11)

1. F11 live-runtime portion, P3: Application `ConversationRunOwner` owns scalar run
   identity/status and handles only. Office progress/checkpoint use its existing
   `UpdateIdentity`; document ownership also consumes bounded active metadata.
2. Catalog reads committed headers; explicit detail reads canonical sessions while
   the live lease still suppresses interrupted-run recovery. Executor callbacks
   retain their current session/streaming path. Office/Workspace Stop signals
   cancellation before addressing the existing exact-scope Inbox, without replay.
3. Removed retained `ChatSession`, snapshot delegate, full-history update/get copies,
   `Sessions()` and the catalog overlay that rebuilt headers from these snapshots.
   Production C# delta: +49/-122 (net -73). No additional store or snapshot adapter.
4. T02 and targeted lifecycle/recovery subset: three new checks plus nine regressions
   pass on macOS/net8/C#7.3, isolated source-linked e8d719b6 + owned changes. Coverage:
   metadata isolation/stale run, canonical detail/catalog, exact Inbox scope/Run,
   pause persistence/failure, cross-owner leases, confirmation, invocation identity,
   Save As, Workspace Stop and recovery. Not full current-HEAD/Office qualification.
5. Same fixed 256-character message bodies, H=100/1000/10000, 12 progress updates +
   12 registry reads. Fixture creation and initial run lease acquisition excluded:

   | H | Historical messages copied before → after | Allocated bytes before → after | Local wall ms before → after |
   |---:|---:|---:|---:|
   | 100 | 2,400 → 0 | 21,908,776 → 1,576 | 8.21 → <0.01 |
   | 1000 | 24,000 → 0 | 217,542,592 → 1,576 | 79.00 → <0.01 |
   | 10000 | 240,000 → 0 | 2,172,487,528 → 1,576 | 771.63 → <0.01 |

   The old snapshot adapter counted copied messages. The replacement reads only
   scalar fields; allocation is independent of H. Neither measured operation does
   storage I/O; explicit detail loads and actual durable checkpoints are outside
   this measurement. CPU/physical I/O and Windows latency were not measured.
6. F04 aggregate persistence, full explicit detail reads, kernel history/working-set
   restart and other compiler scans remain open. Required gates: remaining P3/P7
   cost curves and Windows x64/Office x64 controller delivery/Stop evidence. This
   slice does not make the complete progress or persistence pipeline bounded.
7. Conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide updated.
   No temporary adapter. The journal generation/root switch remains isolated/P7.

## P3e — reuse exact read-body metadata within compilation (2026-11-11)

1. F11, repeated body parsing/key portion: Core `ContextReadBody` owns the derived
   metadata for one compiler atom; registered in the old-style Core project.
2. Key lookup, presence checks, receipt presentation and delivered observations
   reuse exact input metadata. Header validity is rechecked on a hit; changed
   content/call/tool binding is parsed again, payload/evidence identity is respected.
   Hashing is lazy. Parsed trees and separate text bodies are not retained, and
   omission/replacement drops derived state. Actual delivery/live guards are unchanged.
3. Removed repeated `VisibleReadBody`/`IsResourceRead` parsing and per-lookup body
   hashing/key serialization from the compiler. Shared header validation stays in
   `ToolResultHistoryReader`. Production C# delta +132/-65 (net +67); no cross-request
   cache, new store or recovery adapter.
4. T02/T05/T06 subset: two new checks and eight focused regressions pass on
   macOS/net8/C#7.3, isolated e8d719b6 + owned changes. They cover changed bindings,
   invalid protocol/receipt/omission, payload replacement, duplicate/partial/table/
   empty bodies, coverage/scope/resource differences, final retained pointers,
   working-set recovery, budget folding and scripted Office/Workspace live guards.
   Concurrent Browser/window work is excluded; this is not current-HEAD or Office qualification.
5. Same six inline reads (108,006 text characters, three duplicate pairs), 12 actual
   compiler preparations: allocated bytes 247,310,616 → 142,690,200 (−42.3%). One
   local wall sample 102.33 → 62.27 ms. Warm-up/input creation/final wire comparison
   excluded. Final wire SHA-256 matches before/after:
   `CA1927CBD1E9FA1D864DA0112FC2ED79CFBB69A58CA3EA6C67CE5CD50E71F42D`.
   Both fixtures hydrate zero CAS bodies; CPU/physical I/O and Windows latency are unmeasured.
6. Working-set restore still scans history to retain unknown effects and unresolved
   recovery; safely removing that scan requires indexed membership/checkpoint work.
   Across-request reconstruction, aggregate persistence and complete P3/P7 gates
   remain open. This result is not a bounded-restart claim.
7. Conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide updated.
   No temporary adapter; new journal/root integration is still separate.


## P3f — indexed context and addressed unresolved operations (2026-11-11)

1. F10 (indexed-selection subset) and P3.10: Core `ConversationJournal.Context` owns working
   candidates and unresolved requirements in the existing disposable index. Shared
   compiler owns the bounded notice; `AgentProgressTracker` still owns repeat policy.
2. Ready-index restart selects recent metadata, source refs and optional receipts;
   unknown/recovery presence produces a fixed-size notice. There is no refusal based
   on the number of unresolved records. Every unknown remains addressable through
   keyset pages and exact operation lookup. An indexed canonical argument signature
   finds the original unknown call across runs for the existing action-level guard,
   without collecting all unknowns into RAM/prompt. Actual mandatory token overflow
   still refuses preparation, without changing history. Production archive restore
   now also retains opaque runtime `Unknown` behind wire `ok` without E/effect metadata.
3. Removed the isolated recent-only `OpenContext` entry point and switched both
   consumers. The same append/suffix/rebuild reducer derives source membership and
   unresolved recovery; no membership journal, separate registry or body copy.
   Resolved dependency rows cascade/clear; externalized call signatures are prepared
   outside append/SQL locks. Production `Restore(session.Messages)` remains until P7.
4. Eleven focused checks pass on macOS/net8/C#7.3: three indexed-context cases and
   eight regressions (retained results, shared-index/process repair, late outcome,
   run checkpoints, backup/restore, archive working set, two opaque-action policies).
   Fixtures cover 141 unknowns without chat refusal, all 142 after an index fault,
   canonical signature/property order/new call ID, externalized call rebuild, scope
   isolation, real token overflow, and 42 recovery combinations against typed policy,
   including earlier/failed reads and whole/partial evidence. Retained whole/partial
   bodies preserve actual delivery and match the archive-built request. Validation
   uses isolated e8d719b6 + owned changes through P3f; concurrent Browser/window work
   is excluded. This is not a current-HEAD or Windows/Office qualification.
5. Same two retained source observations and one recent call/result, growing only
   irrelevant successful history; fixture writes and final compiler checks excluded:

   | History pairs H | Archive visits | Archive allocated bytes | Indexed records / bytes read | Indexed allocated bytes | Archive / indexed wall ms | Indexed CPU ms |
   |---:|---:|---:|---:|---:|---:|---:|
   | 100 | 204 | 732,728 | 7 / 14,765 | 1,266,304 | 1.57 / 6.48 | 7.06 |
   | 1,000 | 2,004 | 6,944,344 | 7 / 14,784 | 1,167,144 | 6.72 / 3.30 | 3.33 |
   | 10,000 | 20,004 | 69,191,472 | 7 / 14,802 | 1,167,264 | 64.92 / 4.21 | 4.71 |

   Indexed selection visits zero history messages and hydrates no CAS body. The
   archive comparator starts with history already in RAM; its storage-load cost is
   excluded. Logical record bytes vary slightly with identifier lengths. Timing is
   one local sample, not a latency guarantee; old-path CPU, physical I/O and Windows
   performance are unmeasured. Summary/guard/detail queries remain bounded even when
   the unknown backlog exceeds the former proposed 128-record threshold.
6. P3/P7 are not complete: production aggregate consumers, shared semantic detail
   reads, indexed action-guard wiring, full kernel recovery and late-proof resolution
   remain integration gates. Switching only prompt selection is unsafe; the summary
   cannot replace runtime guards. Existing exact-signature policy does not authorize
   other mutations or expand live preconditions/delivery evidence. Windows/Office,
   DPAPI and production backup/GC qualification remain open independently.
7. Updated Conversation protocol, Session events, plan P3.10 (user clarification),
   MIGRATION_MAP, PROGRESS and Harness guide. No adapter or dual-write added; isolated
   owners remain until the coordinated P7 consumer switch deletes old archive paths.

## P1 — runtime write boundary without caller history (2026-11-11)

1. F04, contract-boundary subset: existing Core `ConversationRunCommit` owns exact
   scope/run/revision, scalar message count, current non-history state and named
   writes. Office/Workspace and saved-wait cancellation consume the same boundary.
   This addresses the observed Office full-ID capture and aggregate store argument;
   it does not complete P1 or remove the backend's full-history dependency.
2. A synchronous single submission requires a confirmed position and applies only
   cursor/derived metadata to the caller. Its messages are never replaced. Own
   recovered appends preserve the cursor while the exception still escapes; stale
   CAS cannot adopt another writer's tail. Checkpoint/active view and declared
   activity/result/media order survive the internal canonical-prefix reconstruction.
3. Removed `CommitRun(session, command)` and Office per-fact whole-ID sets/diffs;
   consumers select the new tail by list position. Generic `Commit` rejects runtime
   commands. The existing backend still reads one complete projection, with no
   extra load or second store. Internal history-free state conversion is temporary;
   its owner/removal condition is recorded in MIGRATION_MAP. Production C# delta:
   +179/−38 (net +141); no additional owner or store is introduced.
4. Fourteen focused checks pass on macOS/net8/C#7.3: six `storage: runtime` checks
   plus host parity/retained guards, durable aliases, historical restart/fork,
   indexed selection, resource revisions, lifecycle operations, saved wait and
   Workspace cold cancellation. Coverage includes omitted writes, exact scope/CAS,
   missing receipt, single submission, before-write/after-flush faults and delta
   order. The existing final-response fixture now supplies mandatory v8 `outcome`;
   its previous input failed protocol validation before reaching storage.
   Validation uses isolated e8d719b6 + owned changes through this slice, excluding
   concurrent Browser/window work; not full current-HEAD or Office qualification.
5. Same one-message delta, 24 preparations, growing only the caller's message
   history. Fixture setup and storage excluded; the new probe includes state capture:

   | H | Allocated bytes before → after | Local wall ms before → after |
   |---:|---:|---:|
   | 100 | 186,728 → 79,680 | 0.2414 → 0.0479 |
   | 1,000 | 1,765,288 → 79,680 | 1.2599 → 0.0440 |
   | 10,000 | 16,162,792 → 79,680 | 18.7827 → 0.0529 |

   Separate state-only backend probe compares ordinary aggregate commit with the
   new runtime command on the same fixtures. Both materialize 100/1,000/10,000
   messages. At H=10,000 allocated bytes are 181,514,536 → 182,497,456 (+0.54%);
   local wall time 702.06 → 599.68 ms is one noisy sample, not a storage speedup
   claim. Non-history metadata/artifacts may still grow. CPU, logical/physical I/O
   and Windows latency were not measured for this slice.
6. F04 remains open for ordinary commit/read DTOs and backend aggregate replay.
   Shared kernel-fact translation, semantic resource integration and coordinated
   P2/P3/P5/P7 cutover remain separate work. No new operation-id/idempotency or
   resource transaction guarantee is claimed. Windows/Office delivery remains open.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. The temporary
   state carrier is removed with the aggregate runtime backend at the journal
   consumer switch; no compatibility migration, fallback or dual-write is added.

## P1 — common accepted facts and chronological results (2026-11-11)

1. P1.2/F01 host-drift subset: existing `ConversationRunProjection` moved from
   Office to Core Services. Office/Workspace and saved-wait cancellation use its
   run-state projection; both live model consumers use its accepted response factory.
2. Exact origin, per-position call identity, first-call reasoning/usage and large
   argument CAS now have one implementation. Literal strings remain exact. Actual
   request-delivery evidence stays with the Office model session; terminal results
   retain `ToolResultPersistence`, and host tool/media presentation remains separate.
   The real Office batch exposed a regression in `5b0df48c`: result insertion beside
   an earlier call escaped tail capture and failed the mandatory append. Both hosts
   now append in occurrence order; existing request compilation pairs call/results.
3. Removed Workspace's separate accepted/run constructors, Office's call factory,
   duplicate reasoning projection and `AppendPairedResult`. Production C# +181/−215,
   net −34; one Core compile item added. No new owner/store/registry/facade or adapter.
4. Twelve targeted checks pass on macOS/net8/C#7.3: actual Office batch/restart and
   reasoning consumers, Workspace turn/restart, retained read/mutation parity,
   Workspace confirmation/unknown guards, saved wait, five exact replay/fault
   cases and one shared accepted-batch CAS/restart check. The latter covers large
   literal arguments, ISO-looking strings, reasoning bounds and missing origin.
   A local current working-tree batch run also passed; the selected set uses isolated
   e8d719b6 + owned changes through this slice, excluding concurrent Browser/window
   work. This is not full current-HEAD or Windows/Office qualification.
5. Structural counters: accepted/run projection owners 2 → 1; actual two-call batch
   still persists two exact accepted records and executes two reads once, including
   restart. Historical insertion search/shift is removed from result recording.
   No new timing, allocation or I/O benchmark was run; no end-to-end speedup is
   claimed, and the legacy backend still materializes full history.
6. Eight selected failures reproduce before and after the shared projection with
   the chronological correction held constant; exact failures and owners are in
   [Backlog](../stabilization/BACKLOG.md#host-neutral-harness-baseline-failures--2026-10-13).
   Their assertions/budgets were not weakened. Store/read DTOs, resource owners,
   coordinated consumer cutover and Windows evidence remain open. No P0/index/
   restore mechanism was redesigned or requalified in this slice.
7. Session events, MIGRATION_MAP, PROGRESS, Harness guide and §8.4 updated. Remaining
   P4 is deferred per user scope. Before cutover: verified history/CAS/key backup,
   stopped old writers and short Windows/Office photo evidence; the new generation
   starts new history, retaining the old data separately. The prior internal legacy
   state carrier still ends with the aggregate-backend switch; no new adapter.

## P1 — typed read metadata and transfer scopes (2026-11-11)

1. F04, read-contract subset / P1.4: existing conversation/event ports now use
   immutable `ConversationMetadata` for identity, scope and observed revision.
   The DTO lives in the existing contract file; no new owner or storage is added.
2. Office/Workspace diagnostics and transfer/draft consumers no longer receive
   a partial writable `ChatSession`. Event reads/readers/payload access accept the
   same six scalars. Diagnostic lease liveness uses metadata instead of loading
   full addressed history. Live editor authority and publication remain unchanged.
3. Removed `ReadDiagnosticsSession`, partial-session construction and the aggregate
   signatures of `IEventStore.Read/OpenReader/ReadPayload`. All production consumers
   and existing fixtures switched directly; no compatibility overload or facade.
   Production C# +129/−100, net +29; no new compile item required.
4. Fourteen targeted checks pass on macOS/net8/C#7.3, source-linked working tree
   based on `7555ac0d`: Office diagnostic scope, Workspace scope, exact leased
   export, two payload cases, bounded upload/cancelled completion, actual Office
   batch/restart and Workspace turn/restart, Inbox recovery and four event-feed
   cases. No new test framework or P0/index/restore requalification. Controller
   callsites were reviewed separately; this is not Windows/Office qualification.
5. Read-port aggregate parameters 3 → 0; diagnostic result contains six scalars
   and no messages/artifacts/run continuation. The existing scope fixture still
   forbids full loads/catalog reads and preserves exact history bytes and selection;
   the Workspace fixture retains foreign-scope refusal. No new allocation, CPU or
   I/O timing benchmark was needed, and no end-to-end speedup is claimed.
6. `ConversationMessageWindow.Session`, ordinary commit/load, remaining run/detail
   DTOs, resource integration and journal cutover remain open. Full raw-event scans
   and cold index work retain their existing cost. Actual controller transfer/
   lease delivery still needs the Windows/Office photo gate before qualification.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. No adapter,
   store, import, dual-write or fallback added. P4 remains deferred and the existing
   backup/stop-writers/Windows gates still precede the new-history switch.

## P1 — typed message windows (2026-11-11)

1. F04 / P1 read-contract subset. `ConversationMessageWindow` now carries metadata,
   page messages and artifact presentation data through the existing Store port.
2. Office/Workspace consumers no longer receive a partial writable `ChatSession`.
   Existing owners resolve detached membership, tombstones and selected heads;
   off-page source existence becomes applicable tombstone IDs, not synthetic messages.
   URI/card/library projection shares the same helpers for live and paged inputs.
3. Removed the window's `Session` property and every consumer's dependency on it.
   No new owner, store, facade, compile item or fallback. Production C# +167/−61,
   net +106 across ten files; external UI/storage behavior and product version unchanged.
4. Eleven targeted checks pass on macOS/net8/C#7.3 in an isolated source archive of
   `b61515b2` plus this slice: two page fixtures, real Workspace history consumer,
   five artifact-library checks, corrupt-row repair, damaged-metadata repair and
   catalog/page locking. Office page presentation uses the production services;
   the controller callsite is reviewed separately, not Office-qualified.
5. The existing H=190/page=80 fixture still materializes exactly 80 messages; no
   off-page bodies are hydrated and canonical JSONL bytes remain unchanged. Resource
   cards match full-session presentation, including an explicitly selected older
   head and a later branch. No CPU/allocation/timing benchmark or read speedup claimed.
6. `ChatStore` still constructs a temporary session internally for its existing
   resource projection. Runtime checkpoint hydration, artifact enumeration, ordinary
   commit/load and new-journal integration remain open. The window no longer retains
   or exposes that aggregate. Actual Windows/Office paging needs photo evidence.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. Existing
   internal aggregate owner: Core Storage/resource projection; remove it when scoped
   resource materialization joins the journal consumers. No new adapter, import or
   dual-write. P4 remains deferred; backup/stop-writers/Windows gates still precede
   the new-history cutover.

## P1 — history pages without execution hydration (2026-11-11)

1. F04 / P1 read boundary; existing `ChatStore.ReadMessageWindowAtPath` excludes
   `LastRun` before copying the page projection root.
2. Office/Workspace can read older messages despite an absent/corrupt opaque body
   of an unfinished tool operation. Full run loading still fails explicitly; no
   recovery, replay, state reset or canonical rewrite occurs during the page read.
3. Removed the page's checkpoint-copy/execution-CAS dependency. No new API, store,
   adapter or fallback; production C# +4/−1, net +3. Patch version `16.5.1-dev`.
4. Two existing fixtures extended through production Office presentation and the
   real Workspace consumer fail with `RUNTIME_PAYLOAD_UNAVAILABLE` on `ae730868`.
   Both pass with this change on macOS/net8/C#7.3. Three selected regressions also
   pass: bounded message rows, exact resource/tombstone presentation and corrupt-row
   index repair. Five checks total; no new-journal/restore requalification.
5. Baseline/candidate page success in the two regression fixtures: 0/2 → 2/2.
   The Office fixture also checks corrupt bodies. Execution bodies required by these
   page reads: one → zero; the existing H=190/page=80 check still hydrates 80 messages.
   Canonical bytes stay identical and full run loading still rejects unavailable
   evidence after paging. No CPU/allocation/latency benchmark or total speedup claim.
6. The temporary internal session and artifact/context projection remain; ordinary
   Store operations and new-journal consumers are still open. Native Office delivery
   and DPAPI remain separate Windows evidence; no COM/VSTO validation was run.
7. Session events, MIGRATION_MAP, PROGRESS, Harness guide and Unreleased updated.
   The existing resource projection dependency keeps its prior owner/removal gate;
   no new adapter. P4 stays deferred and cutover retains the backup/writer-stop gates.

## P1 — scoped resource reads (2026-11-11)

1. F04 / P1 read-owner subset: six generic `DocumentArtifactStore` read APIs now
   accept existing immutable `ConversationMetadata`; private Plan/record readers
   use it too. No conversation archive or execution state crosses this boundary.
2. Office/Workspace, resource providers and storage projections capture scalar scope
   explicitly. Materialization reuses captured scope; identity helpers share scalar validation
   without per-artifact metadata copies. Publication/body/owner/removal guards remain.
3. Removed aggregate signatures of `Read`, `List`, `InspectMetadata`,
   `InspectMetadataList`, `SnapshotHistory` and `IsRemoved`; no compatibility read
   overload, store, adapter or new production file. Production C# +203/−106, net +97
   across 30 files, mostly caller migration. Behavior-preserving: no version bump.
4. Seven checks pass on macOS/net8/C#7.3, isolated `478edb71` plus owned changes:
   real Office Plan publication/competing writers, new Workspace Plan historical
   read with pre-publication and foreign-scope refusals, originals restart/GC,
   partial discovery, shared HTML metadata, removal/restart and tombstone pages.
   No new-journal mechanisms or platform qualification were rerun.
5. Generic resource read parameters carrying the aggregate: 6 → 0. They carry six
   scalars through the existing scope DTO; no new CPU/allocation/I/O benchmark or
   speedup claim. Workspace fixture preserves canonical chat bytes after reading.
6. The old shared Markdown lifecycle check fails on removed `markdown.save` in both
   candidate and saved pre-slice build (`ae730868` + `478edb71` page fix); tracked in
   Backlog without weakening assertions. Temporary page projection, specialized
   Plan/text APIs, ordinary Store writes and journal cutover remain open. Actual
   controller/Office delivery still needs Windows evidence.
7. Resource Fabric, Session events, MIGRATION_MAP, PROGRESS, Harness guide and
   Backlog updated. Existing resource-projection owner/removal gate retained;
   no new adapter/import/dual-write/fallback. P4 remains deferred.

## P1 — direct message-page projection (2026-11-11)

1. F04 / P1 read projection: Core `ChatStore` now materializes the existing message
   window directly. Resource metadata/removal, working-set/tombstone guards and
   chart/context presentation remain with existing owners, shared with full loads.
2. Office/Workspace page consumers retain exact messages/cards, selected versions,
   detached membership and off-page tombstone evidence. Pages no longer reconstruct
   the editor's HTML workspace. Execution CAS remains outside the history read.
3. Removed the page → `ProjectSqliteSession` aggregate path, metadata-root clone and
   synthetic `ChatMessage` objects. No new store, facade, adapter or production file.
   Production C# +200/−124, net +76 across eight files.
4. Nine focused checks pass on macOS/net8/C#7.3, isolated `d95c6206` plus this slice:
   enriched Office message/card parity; Workspace paging; indexed bounds; unavailable
   runtime CAS; corrupt row and typed scope repair; document removal/restart; full
   HTML navigation; compaction reconstruction. One host-neutral compilation, no
   Office/VSTO validation. The damaged-root fixture now corrupts consumed `Host`.
5. Same 190-message/80-row fixture: materialized messages 80 → 80; temporary aggregate
   1 → 0; copied metadata roots 1 → 0; HTML workspace body hydration 1 → 0 (64 KB
   HTML input). Chart/compaction content and wire cards equal the full projection;
   canonical bytes unchanged. Artifact enumeration/source-ID lookups are unchanged.
   CPU/allocation bytes and timings were not benchmarked; no whole-read speedup claim.
6. Artifact/history enumeration, specialized Plan/text operations, ordinary Store
   writes and new-journal consumers remain open. Cutover still requires verified
   backup, stopped old writers and Windows/Office photo evidence. P4 remains deferred.
7. Session events, Resource Fabric, MIGRATION_MAP, PROGRESS and Harness guide updated.
   The internal page-aggregate removal gate is closed; no new transitional path.
   Performance improvement: `16.6.3-dev`, with its Unreleased note.

## P1 — indexed runtime write basis (2026-11-11)

1. F04 / P1: existing `ChatStore` now selects only addressed message rows and related
   calls/evidence for `CommitRun`. Finish locator decoding is shared with canonical
   finish admission; the existing normalizer accepts storage-verified checkpoint
   source membership without loading the source body.
2. Real Office/Workspace consumers keep exact revision/count guards, TC/E allocation,
   context selection, chronological activity/result/media ordering and own-append
   recovery. A malformed related-call locator rebuilds before the terminal append.
3. Removed runtime full-history loading/merging, archive-wide new-tail reorder and
   full projection-cache insertion. The selected internal basis is never advertised
   or cached as a complete session. No new store, registry, facade or production file.
   Production C# +140/−42, net +98 across four files; tests +19 lines.
4. Eight final checks pass on macOS/net8/C#7.3, isolated `7d87163e` plus owned changes:
   five runtime-boundary checks, actual Office native read batch, Workspace accepted
   history across turns, and durable TC/E/finish admission. The existing checkpoint
   regression caught incomplete-history normalization and passes with indexed source
   membership. Damaged-locator rebuild, restart, stale writes and before-write/
   after-flush failures preserve history and acknowledgement semantics.
5. Same state-only fixture, aggregate commit versus runtime command:

   | History H | Materialized messages | Allocated bytes | Elapsed ms |
   |---|---|---|---|
   | 100 | 100 → 0 | 2,083,104 → 1,314,312 | 13.85 → 17.05 |
   | 1,000 | 1,000 → 0 | 18,291,288 → 9,805,808 | 73.12 → 32.20 |
   | 10,000 | 10,000 → 0 | 180,841,680 → 95,116,736 | 745.66 → 269.07 |

   One addressed edit materializes one row; terminal append after index repair reads
   one related accepted-call row. Logical body materialization is measured; physical
   I/O and CPU time are not. Timings are single observations during concurrent checks,
   not a gate. Metadata/header work still scales with H; no constant-cost append claim.
6. Ordinary aggregate commits/full loads, metadata/header/artifact costs and new-journal
   consumers remain open. Backup, stopped writers and Windows/Office photo evidence
   still gate cutover; no platform qualification or P4 expansion was performed.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. The existing
   internal carrier keeps its P2/P7 removal condition; no additional adapter.
   Performance improvement: `16.6.5-dev`, recorded in Unreleased.
