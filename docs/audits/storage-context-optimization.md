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

## P1/P2 — direct journal text Store integration (2026-11-11)

1. F01/F04/P1/P2: existing `ConversationJournal` now directly implements the existing
   `IConversationStore` port for scoped plain-text state. Append-only facts retain
   address/card, expected revision, exact message delta and CAS references. Shared
   derived index schema 10 adds locators, ordinals/counts, activity and selection.
2. Office `ChatSessionService` creation and bridge page presentation, plus Workspace
   create/load/page/catalog, consume this owner in isolated roots. Plain user/assistant
   text and reasoning, preferences, active selection and exact identities survive
   restart. Metadata updates preserve activity; stale writes and altered retries fail;
   an accepted receipt cannot rewind a newer projection. Missing text CAS fails on the
   addressed read while warm metadata remains readable. History is never rewritten.
3. Removed `WorkspaceStore`'s concrete `ChatStore` parameter dependency across its
   consumers. The new implementation belongs directly to ConversationJournal;
   `ChatStore` is unchanged, with no new store, wrapper, feature flag, import or
   fallback. Production still supplies its existing `ChatConversationStoreAdapter`.
4. Seven checks pass on macOS/net8/C#7.3 in isolated `de343788` plus owned changes:
   three `journal text:` checks and four targeted regressions (Workspace create/
   relocate, catalog without history, atomic agent delivery/wait, run checkpoints).
   Lost acknowledgement, same-command retry, rejected stale receipt, index lag,
   rebuild, CAS closure and restore cover plaintext and synthetic encrypted storage.
   Compilation/testing excludes concurrent protocol/browser edits; no Office/VSTO
   validation or real Windows DPAPI check was run.
5. Same 25-message batch size and fixed 8-message page at every H:

   | H | Metadata + header records / logical bytes / allocated bytes | Append records / logical bytes / allocated bytes |
   |---|---|---|
   | 100 | 4 / 86,084 / 6,095,256 | 3 / 64,563 / 5,846,024 |
   | 1,000 | 4 / 86,096 / 6,095,752 | 3 / 64,572 / 5,846,648 |
   | 10,000 | 4 / 86,108 / 6,094,888 | 3 / 64,581 / 5,845,032 |

   Metadata/header and append hydrate zero historical bodies; the page hydrates
   exactly 8. Reads include bounded canonical frames, so metadata allocations are
   still material, but no longer depend on H in this fixture. These are logical
   journal counters and current-thread allocations, not physical I/O or CPU timings.
   No old/new speedup comparison: the preceding legacy runtime-commit fixture is a
   different operation and must not be compared numerically with this text slice.
6. Plain text Store coverage does not close a complete model/AgentKernel turn.
   Protocol/runtime commits, resources/attachments, rewrite/move/delete/recovery,
   usage reporting, bridge-stable receipts and multi-scope production composition
   remain open. Full load is intentionally explicit and proportional to H. Cutover
   requires those consumers, verified backup, stopped old writers and Windows/Office
   photo evidence; P4 stays deferred. Unknown/effect mechanisms are not redeveloped.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. Existing
   adapter owner/removal gate is recorded in MIGRATION_MAP: switch the whole port
   and remove the old backend at P7. No product bump or Unreleased behavior claim
   for this intermediate, production-disabled implementation.

## P1/P2 — atomic no-tool runtime integration (2026-11-11)

1. F01/F04, P1/P2: existing `ConversationJournal` implements the runtime Store port
   and model event port directly. Its chat frame contains the existing bounded run
   checkpoint and message delta; one transaction projects chat/run/message cursors.
   Kernel body, context receipt and typed model manifest use existing CAS. Schema 11
   adds only a chat-history index to the same disposable database.
2. Actual Office `ConversationRunService` and Workspace runtime ports complete two
   no-tool turns through AgentKernel and a scripted HTTP endpoint: reflection,
   finish, restart and retained-history reuse. Model manifests precede HTTP. Accepted
   answer and checkpoint survive lost acknowledgement atomically; same-command
   retry appends nothing. A model finish without the final kernel transition remains
   Running after restart/restore, with no automatic execution. Index lag and stale
   writers are exercised. Metadata cannot change accepted run state/policy/model;
   presentation updates preserve text/reasoning and protocol origin.
3. Removed concrete ChatStore dependencies from WorkspacePorts, Plan and task-list
   composition; existing artifact ownership is supplied explicitly. Callable-tool
   restore now uses the addressed event-port admission query, not an all-events read.
   No new adapter/store/registry, feature flag, legacy backend optimization or
   dual-write. Production C# +402/−65, net +337; two partials extend the existing owner.
4. Nine targeted checks pass on macOS/net8/C#7.3 in isolated `bf6709f9` plus this
   slice: two runtime checks, three text/page/scale checks, and existing tool-pack
   cross-turn replay, Workspace accepted-call history, scoped Plan reads and run
   checkpoint/ownership checks. No Office/VSTO validation. An additional broad
   journal-filter run encountered `vba: journal records mutation correlation`;
   its identical failure reproduces in the saved pre-slice build and is recorded
   in Backlog, without changing its assertion or VBA policy.
5. Same state-only kernel-start append after H plain messages and one fixed tail
   message; no historical body is hydrated:

   | H | Journal records | Logical journal bytes | Allocated bytes |
   |---|---|---|---|
   | 100 | 5 | 9,800 | 3,088,064 |
   | 1,000 | 5 | 9,820 | 2,066,376 |
   | 10,000 | 5 | 9,835 | 2,065,728 |

   The first runtime sample includes serializer initialization. Metadata + header
   remain 4 reads / about 86 KB; an 8-message page reads 8 bodies. Physical I/O,
   CPU and Windows timings are not measured. The previous new-generation Store
   rejected CommitRun, so no old/new runtime speedup is claimed. Request preparation
   and explicit full load still use full history; P3 consumer integration is open.
6. Mutation/resource owners, Inbox/send/wait/control, interrupted-run recovery,
   history rewrite/move/delete, usage, feed delivery and stable bridge retry receipts
   remain open. Real adapter setup still creates legacy resource journals: mixed-root
   backup correctly refuses them. New run/CAS restore is verified separately in a
   clean root, not represented as full application backup. Production cutover still
   requires migrated consumers, verified backup, stopped old writers and Windows/
   Office photo evidence. P4 stays deferred; unknown/effect mechanisms are unchanged.
7. Session events, MIGRATION_MAP, PROGRESS, Harness guide and baseline Backlog updated.
   Existing production Store/events adapters retain the same P7 removal gate. This
   is an intermediate, production-disabled integration with no product version bump.

## P1/P2 — direct resource ports (2026-11-11)

1. F07 integration: existing `ResourceJournal` directly implements authority,
   revision and mutation ports. Revision/view registration, multi-head commits and
   abandoned attempts extend its existing stream/reducer and shared index (schema
   12). Bodies and view parts use existing CAS. No second durable owner is introduced.
2. Actual Workspace file create/replace/delete/explicit restore/move and Office
   mutation observer use these ports in isolated new roots. Prepared/dispatch remain
   durable before invocation. One authority frame publishes heads/effect and resolves
   the attempt; `Resolve` verifies the exact terminal receipt. Unknown cannot repeat
   after restart, does not stop an independent file operation, and remains Unknown
   after explicit current-state inspection. Office module/project heads publish
   atomically; interrupted Office observation becomes Unknown without execution.
3. Removed concrete resource-backend dependencies from Workspace file owner and
   Office mutation observer/Plan/task publication. Extracted the mutation contract;
   moved the pre-existing advisory observer list, without duplicating it. Mutation
   receipt lookup replaces whole-history enumeration. Snapshot history is lazy and
   pinned to canonical generation/hash; current heads remain a complete scope
   projection. Production C# +643/−137, net +506. No ChatStore changes, new adapter,
   history import, dual-write or hidden fallback; legacy resource roots are rejected.
4. Ten targeted checks pass in isolated `38959ae6` plus this slice, macOS/net8/C#7.3:
   three `journal resources:` cases; native publication/late-outcome and canonical
   closure/backup regressions; existing authority replay, explicit reset, file
   delete/restore; two no-tool runtime checks. Faults cover lost acknowledgement,
   missing CAS, substituted attempt locator, index lag, rebuild and clean resource
   restore. Native late proof remains addressable without rewriting the old Unknown
   or rolling back a newer head. No Office/VSTO validation or Windows qualification.
5. Same one-head fixture, H authority commits: capture current metadata plus exact
   attempt receipt reads **5 records / 5,780 bytes at H=100**, **5 / 5,792 at H=1,000**.
   It does not hydrate old effects or CAS bodies. Immutable registration retries
   append zero bytes. Before this slice the new journal did not implement these
   consumer ports; no old/new latency speedup is claimed. CPU, allocations, physical
   I/O and Windows timings were not measured. Explicit full history still costs H;
   current-head snapshots cost the number of heads, while pages remain bounded.
6. Still open: accepted tool-call/result correlation with these resource-port
   outcomes, remaining semantic resource owners/composition, other Store/Inbox/
   control operations and P3 integration. Whole-application backup/restore must also
   retain workspace identity locators and pending move intents; resource-journal/CAS
   restore alone does not qualify it. Production switch requires verified complete
   backup, stopped old writers and Windows/Office photo evidence. P4 stays deferred.
7. Resource Fabric, Session events, MIGRATION_MAP, PROGRESS and Harness guide updated.
   Default constructors retain the old resource backend until complete resource/
   runtime composition switches at P7, when `ResourceAuthorityStore` and
   `ResourceMutationJournal` are removed together. No new compatibility adapter.
   This intermediate integration has no product version bump or release claim.

## P1/P2 — atomic read-tool runtime (2026-11-11)

1. The next dependency is now connected: `ConversationJournal.CommitRun` feeds
   accepted calls, runtime entry and terminals into its existing call/result/context
   reducers. Each fact shares one frame/index transaction with its message delta and
   kernel checkpoint. Schema 13 indexes lifecycle locators, new R bindings and typed
   ToolPack admissions; these are disposable projections, not new durable owners.
2. Actual Office run service and Workspace ports pass an Excel range/file read,
   invalid E citation and correction without another execution, restart and reuse of
   historical E in the next turn. Office retains its terminal-before-presentation
   order. Terminal acknowledgement loss accepts the own receipt exactly once; Ok
   and Unknown read outcomes, counters and call/start identity survive restart,
   rebuild and restore before model presentation. These Unknown cases are reads,
   not qualification of uncertain external mutation.
3. Extracted row-level reducers for use in the existing atomic transaction. T/E/R
   allocation updates only the current delta; full reference hydration is reserved
   for explicit Load. Standalone native tool writes cannot enter an integrated chat;
   that boundary is checked before append, without scanning all prior calls during
   each metadata/header read. Production C# +577/−84, net +493. No ChatStore changes,
   wrapper, old-history import, dual-write, fallback or new production writer.
4. Nine distinct focused checks pass in isolated `c75218b2` plus this slice,
   macOS/net8/C#7.3: four `journal runtime:` cases, three `journal text:` cases,
   retained results/context and native resource publication/late-outcome regressions.
   Coverage includes typed admission/rebuild, stale writes, unsupported mixed writes,
   clean-root CAS backup/restore and dispatch rejection after a terminal. Version
   format validation passes. No Office/VSTO or Windows qualification was run.
5. Addressed finish selection for one cited read uses 9 canonical record reads,
   about 56 KB in Office and 62 KB in Workspace, with zero historical body hydration,
   both in its original turn and after restart in the next turn. Selection scales
   with current calls/cited evidence, not an event-history enumeration; admission
   bodies are read outside append locks. Existing H=100/1000/10000 probes retain
   4 metadata/header reads, 3 append reads and 5 no-tool kernel-append reads.
   No tool-loop H scaling, CPU, physical-I/O or Windows latency claim is made;
   the previous direct Store did not support tools, so no old/new speedup is claimed.
6. Next: bind resource preparation/outcome to the exact accepted call before dispatch,
   then finish resource composition and remaining Store/rewrite/move/delete/usage,
   confirmation/wait/recovery, Inbox/control/feed and P3 request preparation.
   Remaining metadata/header byte/allocation overhead stays in that queue. The
   direct integration rejects unsupported effects and confirmation before dispatch.
   Production cutover still needs complete history/CAS/key and workspace locator/
   move-intent backup, stopped old writers and Windows/Office photo evidence.
   P4 remains deferred. Existing proven mutation/Unknown mechanisms are reused.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. Existing
   production adapters/backends retain their complete-port P7 removal condition;
   this intermediate integration adds none. Product version is unchanged, with no
   production switch, release, push or Windows qualification claim.

## P1/P2 — bound mutation calls (2026-11-11)

1. Advances the F01/F04/F07 consumer migration: the existing ConversationJournal
   and ResourceJournal now connect accepted kernel calls to single-target mutation
   preparation/outcomes. Real Workspace file and Office VBA owners execute
   `core.write`; catalog publications use those same injected resource ports.
   This does not close the production migration of these findings.
2. `prepared` retains exact accepted-call/runtime-entry positions before dispatch.
   Schema 14 adds a unique origin locator to the existing attempt index. Terminal
   commits link the exact resource authority receipt. Lost authority acknowledgement
   preserves Unknown even when a receipt exists; missing read-back keeps an unresolved
   attempt and required unknown context. Explicit inspection links later proof
   without rewriting the terminal or repeating the mutation. Unconnected owners
   reject before invocation; independent file work remains available.
3. Removed concrete resource-backend dependencies from Office executor/catalog and
   Workspace catalog composition; the old backend explicitly rejects bound origins.
   No ChatStore optimization, new store/registry, import, dual-write or fallback.
   Production C# net +198 lines. New-root backup accepts only known empty resource
   lease files; nonempty files still block backup rather than being silently omitted.
4. Thirteen distinct focused checks pass in isolated `8d957f6b` plus this slice,
   macOS/net8/C#7.3: four `journal mutation:` cases, four `journal runtime:`, three
   `journal resources:`, native publication/late-outcome and canonical closure/backup.
   Faults cover missing read-back, lost authority/terminal acknowledgement, exact
   repeat/foreign-origin rejection, restart/rebuild, late inspection and clean
   journal/CAS restore. Existing post-unknown inspection requirements remain enforced;
   an unverified model finish cannot bypass them. Version format validation passes.
   No Office/VSTO validation or Windows qualification was run.
5. Same existing one-head fixture before/after: H=100/1000 head capture plus addressed
   attempt lookup remains **5 records / 5,780 bytes** and **5 / 5,792 bytes**. Read-tool
   finish selection remains 9 records and zero historical body hydration. New origin
   lookup uses the unique index; proof verification reads two addressed conversation
   frames outside resource/index locks. The previous integrated path rejected these
   mutations, so there is no comparable old/new mutation latency. CPU/allocations,
   physical I/O and Windows timings were not measured; no speedup is claimed.
6. Next: remaining mutation/resource owners and confirmation/wait/recovery, then
   remaining Store/rewrite/move/delete/usage, Inbox/control/feed and P3 request
   preparation. Metadata/header byte/allocation overhead remains in that queue.
   Full restore must also preserve Workspace identity locators and move intents;
   these journal-only restore checks do not close that gate. Cutover needs verified
   history/CAS/key/owner backup, stopped legacy writers and Windows/Office photos.
   P4 remains deferred.
7. Session events, Resource Fabric, CAS maintenance, MIGRATION_MAP, PROGRESS and
   Harness guide updated. Existing production adapters/backends retain their P7
   complete-port removal condition. Temporary owner restrictions are removed as
   each semantic owner joins this composition. No new adapter or product version
   bump; this intermediate slice does not switch production, release or push.

## P1/P2 — journal mutation confirmation (2026-11-11)

1. Advances F01/F04 consumer migration: ConversationJournal now persists and resumes
   the existing kernel approval for single-call file/VBA `core.write` through actual
   Office run service and Workspace ports. No approval service/store is introduced.
2. Pending preparation remains in checkpoint CAS, without terminal/E or a source
   mutation attempt. Schema 15 adds an addressed pending locator to the existing call
   index; compact ID/hash binds it to exact preparation. Resume claims a new invocation
   in the original run before source dispatch, then consumes the pending locator on
   runtime entry. Stale/changed approval fails before execution. Lost claim ack does
   not recreate pending or retry the effect. Workspace denial yields NotDispatched;
   the existing evidence owner rejects using that receipt as successful write proof.
3. Removed the unconditional confirmation rejection from the journal path. Workspace's
   existing history reconstruction now takes its payload owner explicitly and is
   reused by the integration. Production C# net +64 lines; no ChatStore changes,
   parallel history, new registry/adapter, old-history import, dual-write or fallback.
4. Thirteen distinct checks pass on macOS/net8/C#7.3 in isolated `d1e7b4a2` plus this
   slice: four `journal confirmation:`, four `journal mutation:`, four `journal
   runtime:` and `storage optimization: run checkpoints and invocation ownership`.
   Approval tests cover both consumers, exact preparation, stale second resume,
   restart/rebuild and clean journal/CAS restore after approval; denial uses real
   Workspace ports. Existing unknown/lost resource/terminal acknowledgement cases
   also pass. Version format validation passes. No Office/VSTO/Windows validation.
5. Same read-tool fixture before/after: finish admission remains 9 canonical records,
   about 56 KB for Office / 62 KB for Workspace, zero historical body hydration,
   before and after restart. Approval lookup uses one indexed call and exact frames;
   its prepared JSON is not copied into headers. Previously this path rejected
   confirmation, so no old/new confirmation latency comparison exists. CPU,
   allocations, physical I/O and Windows timings were not measured.
6. Still open: interrupted-run reconciliation after a consumed claim or in-flight
   call, agent waits, other mutation/resource owners, remaining Store operations,
   Inbox/control/feed and P3 request preparation, including metadata/header overhead.
   Production switch still requires complete history/CAS/key/Workspace locator and
   move-intent backup, stopped old writers and Windows/Office photo evidence. The
   clean journal restore is not full application restore. P4 remains deferred.
7. Session events, Resource Fabric, MIGRATION_MAP, PROGRESS and Harness guide updated.
   Existing adapters/backends retain their complete-port P7 removal condition;
   temporary semantic-owner restrictions remain until those owners are connected.
   No product bump for this intermediate integration, production switch or push.

## P1/P2 — journal interrupted-run recovery (2026-11-12)

1. Advances F01/F04: existing Office/Workspace recovery now writes the same typed
   kernel commit as runtime execution. Existing lease and exact cursor/run fencing
   remain; no recovery service/store/registry is added.
2. Kernel interruption supplies the synthetic terminal: entered reads become Error,
   possible writes Unknown. Checkpoint, counters, activity and diagnostic share one
   frame. A known terminal is preserved before optional result presentation. An
   abandoned approval claim without runtime entry closes without an invented effect;
   pending approval is not interrupted. Resource proof cannot promote Unknown to Ok.
3. Removed the unsupported journal recovery port and Workspace recovery's direct
   ChatStore write. The old generic kernel-recovery commit is replaced in both consumers;
   inputs preceding any kernel checkpoint retain their non-tool path with a plain
   diagnostic, without inventing an execution activity.
   Production C# net +59 lines. No import, dual-write, retry or new fallback.
   Old backend deletion remains at P7.
4. Fourteen checks pass on macOS/net8/C#7.3 in isolated `d5aa1e12` + this slice:
   three `journal recovery:`, four confirmation, four mutation, shared recovery/cursor,
   known unprojected terminal recovery and interrupted-write replay. Scenarios include
   actual Workspace dispatch, Office/Workspace recovery consumers, lost recovery ack,
   repeated recovery, rebuild and clean journal/CAS restore. Two old `kernel recovery:`
   tests fail at line 486 before recovery: retired `skills.upsert` creates no pending
   confirmation. The fixture gap is in BACKLOG; no claim that the full suite is green.
5. Recovery adds one atomic chat frame and uses the existing indexed run/call/result
   lookups. Tests assert no additional model request or write, and no second frame on
   repeated recovery. Recovery still explicitly loads the chat; no bounded-history,
   CPU/allocation or Windows-latency improvement is claimed.
6. Agent waits, other mutation/resource owners, rewrite/move/delete/usage,
   Inbox/control/feed and P3 (including metadata/header overhead) remain open. P4
   remains deferred. Full backup including keys/Workspace locators/move intents,
   stopped legacy writers and Windows/Office photo evidence still gate P7.
7. Canonical session events, migration/progress and Harness guide updated. Common
   production recovery gets patch `16.12.1-dev`; the unrelated pending `16.13.0-dev`
   working-tree version is preserved. Journal production composition remains off.
   Version-format and diff checks pass; no VSTO/Office build, tag, push or release.

## P1/P2 — journal saved waits and queue control (2026-11-12)

1. Connects unfiltered `agents.wait` through actual Office/Workspace runtime and
   existing Inbox. ToolWaiting retains its exact context/condition/deadline in CAS
   with one locator in the existing call index; no terminal/E or effect is invented.
2. The kernel marks explicit continuation claims. Journal claims a new invocation
   under the chat cursor while retaining Waiting; deadline/budget changes and a
   paused queue fail before continuation. Timeout/cancel resolve the original call
   once. Shutdown, lost claim acknowledgement and stale requests cannot replay it.
3. User pause/resume shares the chat frame and existing queue-control reducer/index.
   Schema 16 adds only a control-page index. Input replay reads those indexed facts,
   not chat bodies. No new store, scheduler, adapter, imported history or dual-write.
   Retiring backends keep the full-port P7 removal condition. Production C# net +69.
4. Thirteen focused checks pass in isolated `ca48ef64` + this slice on macOS/net8/
   C#7.3: five `journal wait:` cases (both consumers), four confirmation cases,
   native run checkpoints/queue controls and two kernel durable/timed wait cases.
   Coverage includes both lost acknowledgements, stale continuation, changed deadline,
   pause fencing, cancellation, exact budgets, rebuild and clean Workspace journal/CAS
   restore. Office mixed-root backup remains refused; no Windows/Office qualification.
5. Wait creates no result row until its terminal; timeout/cancel do not repeat the
   accepted call or recharge tool steps. Controls update cursor and index in one
   canonical frame. Input control replay is indexed per chat and hydrates no message
   bodies. No CPU/allocation/Windows latency measurement or full-Inbox boundedness claim.
6. Incoming input delivery, filtered reply/assignment waits, peer controls/feed,
   other resource/mutation owners, remaining Store operations and P3 metadata/header
   costs remain open. P4 stays deferred. Full history/CAS/key/Workspace-locator backup,
   stopped old writers and Windows/Office photo evidence still gate production cutover.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. This is an
   intermediate journal integration; the new claim marker does not change the
   production backend's behavior. No product bump, production switch, tag or push.
   Version-format/diff/link checks pass; unrelated working-tree changes are excluded.

## P1/P2 — journal user Inbox delivery (2026-11-12)

1. Text user acceptance, pending edits/removal and delivery now use the existing
   ConversationJournal owner. Office queue/steer and Workspace initial intake run
   through existing Inbox and real runtime consumers. Initial Pending/Delivering
   and explicit removal of paused undelivered input retain current semantics.
2. Input/control/chat cursor commit atomically in one frame; text stays in CAS.
   Applied stores an addressed prior message receipt and verifies input ID, role,
   accepted run, provenance and exact body before acceptance and during replay.
   Lost acceptance/message/Applied acknowledgements cannot manufacture success or
   automatically repeat delivery. Read-only receipts respect the supplied cursor.
3. Schema 17 adds request identity to existing input_state and its unique index.
   No store, registry, façade, fallback, dual-write or legacy history import.
   Common input code delegates delivery proof to the existing event port; old
   envelope parsing moved mechanically into the retiring ChatEventStoreAdapter.
   ChatStore is not optimized; its removal still belongs to whole-port P7 cutover.
   Production C# net +175 lines.
4. Twelve targeted checks pass in isolated `0313240f` + this slice on macOS/net8/
   C#7.3: four journal input cases, five journal wait cases, two existing Inbox
   recovery cases and native atomic agent delivery/wait. They cover exact proof,
   stale receipts/revisions, request collisions, missing message, wrong body,
   edit/remove, restart, lost acknowledgements, rebuild and clean Workspace
   journal/CAS restore. No VSTO/Office validation or production qualification.
5. Input replay reads indexed controls, not unrelated chat bodies. Inbox hydrates
   only the latest CAS body per input; acceptance identity remains separate from
   edited text. Applied verification reads the addressed original frame. Full
   input/control replay still scales with transition count; metadata/header costs,
   CPU/allocation and Windows latency are not claimed solved by this slice.
6. Attachments, peer input/control/feed, correlated waits, remaining Store/resource
   operations, Workspace steer transport/default composition and P3 costs remain
   open. P4 remains deferred. Verified history/CAS/keys/Workspace-locator backup,
   stopped old writers and Windows/Office screen-photo evidence still gate cutover.
7. Canonical session events, MIGRATION_MAP, PROGRESS and Harness guide updated.
   Intermediate journal integration leaves production composition unchanged;
   format-validation relocation preserves existing backend behavior. No product
   bump, tag or push. Version-format/diff/link checks pass; unrelated dirty work
   is excluded from the commit.

## P5 — journal event feed and metadata reconnect (2026-11-13)

1. F22's journal consumer now uses the existing chat-write locators and shared
   suffix validation. ConversationJournal implements ISessionEventReader; the
   existing ConversationEventFeed consumes typed notices. Full production F22
   closure still requires P7; Inbox history/eviction and peer delivery remain open.
2. Pages validate exact scope/hash anchors and pin high water across interleaved
   chats and later appends. Model/input/chat notices do not hydrate CAS. Workspace
   ReadEvents/SubscribeEvents now use WorkspaceStore metadata ownership and the
   configured event port, preserving foreign-workspace rejection and cancellation.
   Invalid canonical data returns HistoryUnavailable and preserves corrupt bytes.
3. Removed full-session hydration from Workspace feed entry points and unconditional
   event-adapter construction. Legacy operation parsing moved out of the common feed
   into its existing retiring reader. No new store/index/registry/adapter, durable
   format change, imported history or fallback. Production C# net +125 lines.
4. Nine checks pass on isolated `bf2bcb70` + this slice, macOS/net8/C#7.3: five
   journal feed cases and four existing feed regressions. Real Office/Workspace
   runtime notices, Workspace metadata reconnect with unavailable message CAS,
   scope/hash/paging, external append, lost acknowledgement, backpressure/cancel,
   corrupt complete suffix, rebuild and clean journal/CAS restore are covered.
5. Before: native OpenReader was unsupported; Workspace scope checks loaded the full
   session. At H=100/1000/10000 messages with equal 25-message frames, a two-notice
   native page now reads 5 records (~87 KB), idle reads 2 and reading a new event
   after another writer's append reads 4; no CAS body reads. These are logical-I/O
   counters. No CPU/allocation/Windows timing comparison or cold-rebuild bound.
6. The user clarified priority on 2026-11-13: complete stages by dependency while
   retaining current capabilities; no reduced Windows pilot is required. Next are
   peer delivery/correlated waits and remaining Store/resource consumers, followed
   by complete cutover. P3 metadata/header and input-history costs remain open; P4
   stays deferred. Production writers, Office UI subscription adoption, complete
   backup/restore and Windows/Office photo qualification remain separate gates.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. Patch
   16.15.2-dev records the active Workspace reconnect improvement; journal writers
   remain unchanged. Retiring readers/adapters are removed with whole-port P7
   cutover. Version-format/diff/link checks pass; no push/tag. After validation,
   `59c101f9` accidentally included one unfinished plugin-test registration from
   parallel work. `5cc30c52` removes it from the committed baseline while preserving
   the other worker's working-tree line. The nine checks above describe the isolated
   candidate; the corrected baseline is used for the following peer slice.

## P5 — journal peer Inbox and correlated waits (2026-11-13)

1. P5a integration prerequisite for F19/F22: ConversationJournal now supplies native
   peer input and addressed request references to existing Inbox/wait consumers.
   This does not close application Send, F18/F21 input-history costs or full P5/P7.
2. One acceptance frame saves delivery/membership, recipient input/control and both
   chat heads. Peer text/provenance are immutable; Applied needs its exact prior
   message receipt. Feed cursors expose the same native frame in each affected chat.
   Saved reply/completion waits retain run/call/deadline and consume the reply once.
3. Common wait no longer depends on Prepared-event envelopes: typed request lookup
   lives on the existing event port, with legacy parsing in its retiring adapter.
   Schema 18 extends existing chat/delivery indices; no table/store/registry/facade,
   legacy import, dual-write or fallback is added. Production C# +274/−47 lines.
4. Three new cases and 19 regressions pass on isolated `5cc30c52` + this slice,
   macOS/net8/C#7.3. Office run service, Workspace runtime ports/shared input channel
   and Inbox cover cold pause, correlated reply/assignment completion, body/provenance
   rejection, prefix/foreign lookup, lost acceptance acknowledgement, rebuild and
   clean Workspace restore. Existing input (4), feed (5), wait (5), native delivery
   (1), correlated reply (2), assignment correlation (1) and durable completion (1)
   checks pass. Publication uses native SendAgent after an atomic accepted call;
   this is not application outbound Send/Resolve or live-notification evidence.
   After parallel Browser commit `8c5f3dae`, all slice implementations remain
   unchanged; harness build and the three new cases pass on the combined baseline.
5. Before this slice, managed peer input was unsupported. Acceptance now advances
   both heads in one frame; same-operation retry adds zero bytes. Existing feed
   fixture at H=100/1000/10000 retains page/idle/after-append reads of 5/2/4 records
   and zero CAS hydration. No peer history cost curve, CPU/allocation comparison or
   Windows latency was measured; full Inbox replay remains unbounded by history.
6. Next: application Send/Resolve, spawn, peer controls and live Inbox notification,
   then remaining Store/resource consumers. Metadata/header, ordinary Store
   operations, attachment/Workspace steer delivery and complete backup/restore
   remain open. P4 stays deferred. Verified history/CAS/keys/locator backup, stopped
   old writers and Windows/Office screen-photo evidence still gate full cutover.
7. Session events, MIGRATION_MAP, PROGRESS and Harness guide updated. Existing
   ChatStore/event adapters are removed with whole-port P7; no new adapter. This
   intermediate integration keeps production composition and behavior unchanged,
   so no version bump. Tested source hashes are checked against the isolated
   candidate and staged diff; unrelated work remains unstaged. No push/tag.

## P5 — application Send/Resolve on the journal (2026-11-13)

1. F19's known-address Send consumer now uses scoped journal alias lookup instead
   of catalog enumeration. Existing AgentCommunicationService, Inbox, event port
   and ConversationJournal remain owners. Spawn/control/list and complete F19/P5
   closure remain open; F18/F21 input-history costs are not closed by this slice.
2. Actual Office/Workspace tool execution enters the native atomic acceptance path.
   Exact run/call/step/runtime-entry and both chat revisions guard dispatch; the
   terminal checks its canonical delivery. Live cursors advance after read-back,
   the existing Inbox signals input, and scheduling runs outside locks. User stop
   remains effective. Receipt loss with exact frame/intent/body read-back proves
   queued; unavailable read-back retains Unknown through recovery without replay.
3. Native Send/Resolve no longer use catalog scans, source Prepared/Accepted or a
   separate root reservation. The old event backend explicitly keeps SendEvents
   until P7; this is a declared capability boundary, not fallback or dual-write.
   Existing scope Endpoint now accepts the exact address. No new owner/store/index,
   schema change or imported history. Production C# +259/−15 lines.
4. On isolated `a38e5aba` + this slice, macOS/net8/C#7.3: 3 new Send/Resolve cases
   and 16 regressions pass. They cover both real runtime consumers, exact native
   receipt/terminal, notification/stop, lost acknowledgement, unavailable read-back,
   Unknown recovery, restart, rebuild and clean Workspace restore. Regressions:
   peer (3), native delivery (1), mutation (4), tool evidence (1), interrupted read
   (1), send batches (2), spawn (2), receipt failure (1), assignment correlation (1).
   One additional parser/batch-policy check fails identically on clean `a38e5aba`;
   recorded separately in [backlog](../stabilization/BACKLOG.md#send-batch-parserfixture-mismatch--2026-11-13), not counted as passed.
5. Before: native outbound application delivery was unsupported. The new fixture
   records zero catalog enumerations for Send and Resolve, one acceptance per
   accepted call, and no new acceptance on restart/rebuild/restore. These are
   operation counts; no history cost curve, CPU/allocations or Windows latency
   comparison was made. Inbox replay still grows with retained input history.
6. Next: native spawn, peer controls and assignment/group projections, then other
   Store/resource operations and P3 metadata/header costs. P4 remains deferred.
   Production composition still uses the old backend. Verified history/CAS/keys/
   Workspace locator backup, stopped old writers, full restore and Windows/Office
   screen-photo evidence gate full cutover; controller binding changes are not
   declared Windows qualified by this harness.
7. Session events, conversation protocol, MIGRATION_MAP, PROGRESS, Harness guide
   and the separate backlog item are updated. P7 removes the explicit capability
   branch/SendEvents with the existing retiring adapters. This intermediate slice
   changes no default production behavior, so no product bump. Scoped staging
   preserves parallel window/UI changes. No push/tag.

## P5 — application spawn on the journal (2026-11-13)

1. Native spawn now reaches the same Store/Inbox/runtime consumers as Send/Resolve.
   Owners remain Application AgentCommunicationService/Inbox and Core journal/event
   port. This advances F19/P5; peer controls, assignment/group projections and
   history-sized input/metadata/header work are not declared closed.
2. One `agent.spawned` frame accepts child metadata/context/provenance, membership,
   optional input/control and both heads. Runtime-entry/source-revision guards run
   before dispatch. Frame/intent/CAS read-back precedes live projection and fresh
   scheduling. Idle creation preserves selection and has no run/input; inheritance
   preserves mode/mutation restrictions/effective model without history or approval.
   Lost acknowledgement resolves to the same child; unavailable proof recovers
   Unknown without replay. A detached view cannot erase proven creation.
3. Native spawn omits root reservations, standalone Store commits and a separate
   initial Send. Existing chat metadata writing and runtime-entry validation are
   shared. Schema 19 adds sender/sequence lookup to existing `agent_spawn`; the
   child's metadata uses existing chat indices. Feed union avoids duplicate spawn
   deliveries; backup reachability includes child context. No new store, registry,
   import, dual-write or fallback. Production C# +401/−75 lines plus one csproj entry.
4. On `8e021769` + this slice, macOS/net8/C#7.3: four new cases pass through real
   Office run service and Workspace runtime ports. They cover idle Plan/Deny/model
   inheritance, catalog creator, ordinary Store rename, initial assignment/Resolve,
   notification failure, native feeds, lost ack, Unknown, restart/rebuild and clean
   Workspace restore. Fourteen targeted regressions pass: Send/Resolve (3), peer
   Inbox/waits (3), feed (5), primitive atomic spawn (1), retiring spawn identity and
   initial failure (2). No full suite or VSTO/Office validation was run.
5. Before: application spawn could not create an integrated native Store chat.
   After: zero catalog reads, one shared acceptance frame, and no additional creation
   or automatic scheduling after restart/rebuild/restore in these fixtures. The
   existing feed cost curve still reads 5/2/4 canonical records for page/idle/after
   append at H=100/1000/10000, with zero CAS body hydration. These are logical I/O
   and operation counts; spawn CPU/allocations/history curve and Windows latency
   were not measured. Metadata/header and full Inbox replay costs remain open.
6. Next: native peer controls and assignment/group projections, remaining ordinary
   Store/resource operations and P3 metadata/header costs. P4 remains deferred.
   Production writers are unchanged. Full P7 still needs verified history/CAS/keys/
   locator backup, stopped old writers, full restore and Windows/Office screen-photo
   evidence; host-neutral tests do not qualify controller/Office delivery.
7. Canonical session events/protocol, MIGRATION_MAP, PROGRESS and Harness guide are
   updated. Whole-port P7 removes `SpawnEvents` and the explicit capability branch
   with SendEvents/retiring adapters. This intermediate integration changes no
   default production behavior, so no product bump. One scoped commit; no push/tag.

## P5 — native peer pause and resume (2026-11-13)

1. Native pause/resume now reaches real Office run-service and Workspace runtime
   consumers. Owners remain Application AgentCommunicationService/Inbox and Core
   journal/event port. This advances P5; native cancel and peer projections remain
   separate work. No new store, registry or intermediate facade.
2. One `queue.control` records the exact source call/runtime entry, both heads,
   target run, live owner lifetime and PauseId. Read-back precedes notification;
   lost acknowledgement resolves to the same receipt, unavailable proof retains
   Unknown without replay. Requested does not claim safe pause: the kernel reaches
   `Waiting + PauseId` before acknowledgement. Resume retains run/counts/budget and
   cannot release user Stop, cold recovery or a replacement user pause. Pending wait
   retains its original deadline; incoming peer input preserves pause and FIFO.
3. Input-less local user controls/ack now write direct control facts, removing
   mutable run snapshot copying and chat/context serialization. The actual consumer
   checkpoint commits share the existing Inbox lock through cursor publication;
   model/user waits and UI callbacks stay outside. Schema 20 extends existing
   `control_fact` and source-call/page indices. Native feed/rebuild/restore use the
   same proof. `PauseEvents` explicitly serves only the retiring backend; native
   controls never fall back, import history or dual-write.
4. On `bdad05b3` + this slice, macOS/net8/C#7.3: seven new cases pass through both
   consumers: pause/resume with input, lost ack, Stop, restart/rebuild/clean Workspace
   restore, unavailable read-back/Unknown, pending wait, user takeover/stale release.
   Twenty-three targeted regressions pass: Send/Resolve (3), spawn (4), peer (3),
   saved waits (5), feed (5), primitive durable controls (1), retiring pause authority
   and Workspace wire (2). The takeover case exposed and now guards the live cursor
   race between checkpoint and control. No full suite or Office/VSTO validation.
5. Before: native application peer controls were unsupported; local controls copied
   aggregate run data. After: addressed peer controls enumerate zero catalogs in
   these fixtures and direct control frames need no CAS bodies. Existing wait/user
   discovery keeps its catalog reads. Feed cost checks retain 5/2/4 canonical reads
   for page/idle/after append at H=100/1000/10000, with zero CAS hydration. These are
   logical I/O/operation counts, not a pause history curve or CPU/Windows latency
   measurement. Full Inbox replay and metadata/header costs remain open.
6. Next: native cancel, assignment/group projections, other Store/resource
   operations and P3 metadata/header costs. P4 stays deferred. Default writers are
   unchanged. Whole-port P7 still requires verified history/CAS/keys/locator backup,
   stopped old writers, full restore and Windows/Office screen-photo evidence.
7. Session events, conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide
   are updated. P7 removes PauseEvents and the explicit capability branch with
   SendEvents/SpawnEvents/retiring adapters. This intermediate integration does not
   change default production behavior, so no product bump. One scoped commit;
   no push/tag.

## P5 — native peer cancellation (2026-11-13)

1. Native `agents.cancel` now uses real Office/Workspace runtime consumers and the
   same Application service/Inbox/RunOwner and Core journal control port. Saved
   cancellation remains owned by AgentKernel/ConversationWaitCancellation/CommitRun.
   This advances P5; assignment/group projections remain separate work.
2. A single `queue.control` pins the exact source accepted call/runtime entry, both
   heads, target run/checkpoint and live lifetime or reserved saved wait. Verified
   read-back precedes signalling; Requested does not prove target termination.
   Only terminal read-back permits Cancelled. Live pause cancellation signals under
   the existing Inbox locks before a continuation can observe cleared peer authority.
   Cold wait/pause cancellation retains history, input, run, budgets and unknown
   effects without model/tool dispatch. Competing cold ownership refuses before append.
3. The existing control DTO/port now handles Pause/Resume/Cancel, replacing its
   pause-only signature. Native cancellation avoids legacy CancelPrepared/Recorded
   and catalog enumeration; the explicit CancelEvents branch serves only the
   retiring backend. Cold terminal projection is shared with existing user/legacy
   cancellation. No new store, registry, schema, import, dual-write or fallback.
   Production C# +172/−60 lines; no new source/project entries.
4. On `abacb894` + this slice, macOS/net8/C#7.3: seven new cases pass through both
   actual runtime consumers: live cancellation/queued input, lost request ack,
   unavailable read-back, cold wait/restart/rebuild/clean Workspace restore, lost
   target terminal ack, competing cold lease and live/saved peer pause. Seventeen
   targeted regressions pass: native pause/resume (7), retiring cancellation (4),
   native Send/Resolve (3), primitive controls (1), Office saved-pause cancellation
   (1) and native wait cancellation (1). No full suite or VSTO/Office validation.
5. Before: native application cancellation was unsupported. After: zero catalog
   reads and one native cancel request per accepted call in these fixtures. Lost
   request ack reconciles the same frame; unavailable proof sends no signal, and
   lost terminal ack keeps source Unknown even when target cancellation is durable.
   Restart/rebuild/restore neither repeat cancellation nor resume queued work.
   These are operation/behavior checks; no CPU/allocation/history-cost curve or
   Windows latency measurement. Metadata/header and full Inbox replay costs remain open.
6. Next: assignment/group projections, other Store/resource operations and P3
   metadata/header costs; P4 stays deferred. Default writers are unchanged. Whole-port
   P7 still needs verified history/CAS/keys/locator backup, stopped old writers,
   full restore and Windows/Office screen-photo evidence.
7. Session events, conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide
   are updated. Whole-port P7 removes CancelEvents with the other retiring control/
   delivery paths and capability/adapters. This is intermediate integration without
   a default production behavior change, so no product bump. One scoped commit;
   no push/tag.

## P5 — native assignment pages (2026-11-13)

1. `agents.list(assignments=incoming|outgoing)` now reads native intake through the
   existing event port and Application service/Inbox. The existing page/view/state
   DTO and excerpt helper move to Core; no second assignment store or durable read
   model. Group operations remain the next separate owner/dependency slice.
2. Each page checks current chat scope/revision, canonical request/resolution and
   this chat's own input state. It returns at most 50 rows with a request-ID cursor;
   separate pages do not share a pinned snapshot. Cold listing skips Inbox replay
   and catalog enumeration. Only page request bodies are read for the existing
   bounded/surrogate-safe summary; report bodies remain behind FIFO/policy. Canonical
   Open/ResolutionReceived never changes a source Unknown or proves external effects.
3. Schema 21 adds an assignment flag and directional partial indices to existing
   `agent_delivery`; rebuild derives them from accepted facts. Shared input lookup
   accepts a revision fence. Explicit AssignmentsEvents remains only for the retiring
   backend until whole-port P7. No import, dual-write, fallback or new store/registry.
   Production C# +159/−45 lines; new Core model/partial files are included in the
   old-style csproj.
4. On `37170720` + this slice, macOS/net8/C#7.3: two new cases pass through actual
   Office run-service and Workspace ports/model wire, plus 50+1 paging, local
   direction, summary bounds, stale/scope refusal, rebuild and clean Workspace
   restore. Sixteen focused regressions pass: Send/Resolve (3), native peer (3),
   spawn (4), retiring assignments (5), feed page/rebuild (1). One further retiring
   Workspace assignment-wire case failed once at run persistence and passed a
   targeted trace rerun; its cause remains [open](../stabilization/BACKLOG.md#intermittent-workspace-assignment-wire-failure--2026-11-13).
   This is not a clean 17-case pass or a claimed fix. The Unknown fixture
   recreates its lost Inbox owner after recovery; stale live cursors still fail.
   No full suite or Office/VSTO validation was run.
5. Before: native assignment listing used unsupported legacy event projections.
   After: a cold 50-row incoming page with 61 retained inputs reads 103 canonical
   records and exactly 50 request bodies in both hosts; it enumerates no catalog
   and hydrates no report/unrelated input bodies. This proves bounded page selection,
   not a history cost curve, CPU/allocation reduction or Windows latency. Request
   body size, full Inbox replay and metadata/header costs remain separate concerns.
6. Next: groups through their existing resource owner, other Store/resource
   operations and P3 metadata/header costs. P4 stays deferred. Default writers are
   unchanged; verified history/CAS/keys/locator backup, stopped old writers, full
   restore and Windows/Office screen-photo evidence still gate whole-port P7.
7. Session events, conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide
   are updated. P7 removes AssignmentsEvents with the explicit capability/adapters.
   This intermediate integration does not change default production behavior, so
   no product bump. One scoped commit; no push/tag.

## P5 — native chat groups (2026-11-13)

1. Native create/list/rename/add/remove/delete now use the existing
   ConversationGroupService through actual Office run-service and Workspace ports,
   plus the existing typed UI change contract. Scoped resource heads and immutable
   group bodies remain in ResourceJournal/CAS; no group stream, table or new owner.
2. The existing resource scope lease spans source/member checks, revision/name
   guards, prepared, dispatch, authority commit and exact head read-back. Model
   calls bind resource preparation to their accepted origin; only the kernel writes
   the conversation terminal. Lost acknowledgement preserves source Unknown even
   when the resource commit is durable. Groups do not rewrite participant history,
   schedule runs, alter queues or reserve causal-chain membership. Tombstones cannot
   be resurrected by reusing a creation key; no-op/list produces no resource effect.
3. Native alias resolution uses the existing Inbox/event index and membership uses
   conversation metadata. Reads select only group-prefix heads in 100-row pages;
   verified change results reuse their captured group without rereading the catalog.
   Resource outcome links reuse existing journal facts, with no schema migration.
   ChatStore.Groups itself is unchanged; its explicit service constructor/catalog
   branch remains only until whole-port P7. No import, dual-write or hidden fallback.
   Production C# +250/−47 lines; the new Application partial is in its projitems.
4. On `1e84f83e` + this slice, macOS/net8/C#7.3: three new cases pass across Office
   and Workspace for typed UI guards, model CRUD/list/no-op, scoped aliases,
   lease refusal, tombstones, unchanged participant histories, lost acknowledgement,
   restart/rebuild and clean Workspace restore. Ten focused regressions pass:
   retiring groups (3), native resource ports (3), native mutation (4). Version
   format and diff checks pass. No full suite or VSTO/Office validation was run.
5. Before: native tool admission rejected group mutations and the group service
   depended on ChatStore. After: group operations use the existing resource ports
   and canonical kernel outcomes, without loading member histories or enumerating
   the chat catalog for model IDs (the fixture rejects that access). Group bodies
   in the scope are still hydrated for unique-name checks; authority Publish still
   returns its existing snapshot. No total-history cost curve, allocation reduction
   or Windows latency claim is made for this integration slice.
6. Next: remaining ordinary Store/resource operations, including native Delete/Move,
   and P3 metadata/header/authority-snapshot costs. Native chat deletion must share
   the resource scope lease and pass the add/delete-chat race; it is not qualified
   by these group tests. P4 stays deferred. Default writers remain unchanged;
   verified history/CAS/keys/locator backup, stopped old writers, full restore and
   Windows/Office screen-photo evidence remain whole-port P7 gates.
7. Conversation protocol, session events, MIGRATION_MAP, PROGRESS and Harness guide
   are updated. P7 removes the retiring constructor, catalog branch and
   ChatStore.Groups with its old backend. This intermediate port integration does
   not change default production behavior, so no product bump. One scoped commit;
   no push/tag.

## P1/P2 — native single-chat deletion (2026-11-13)

1. The existing ConversationJournal directly implements single-chat Store deletion
   with `chat.deleted`. Office ChatSessionService and WorkspaceStore consume it;
   no new facade/store/registry or alternate production writer is introduced.
2. The fact pins address/revision/hash. Append admission checks exact scope and
   terminal-run state under the same resource scope lease as group membership.
   The existing card locator becomes the canonical tombstone receipt; alias,
   live metadata and selection disappear atomically. Store/primitive/spawn creation
   cannot reuse the ID. Other chats and resource-owned groups survive unchanged.
3. Canonical history, CAS and resource evidence remain retained. This is logical
   deletion, not storage reclamation. Lost flush acknowledgement/index update uses
   exact read-back without redispatch; unavailable proof throws instead of claiming
   success. Restart/rebuild/restore do not resurrect the chat or upgrade a retained
   source Unknown. No new table/schema, old-history import or fallback. Production
   C# +124/−11; the partial is included in the old-style Core csproj.
4. On `35a1defe` + this slice, macOS/net8/C#7.3: four new cases cover Office/Workspace
   deletion/selection/catalog/alias, foreign scope, rejected resurrection, group
   persistence, active-run refusal and post-recovery deletion, the membership lease
   race, before-append/after-flush/index/read-back faults, rebuild and clean Workspace
   restore. A real model Unknown and its cross-journal resource proof survive source
   deletion and restore. Twelve regressions pass: native groups (3), spawn (4),
   Send/Resolve (3), text Store consumers/recovery (2). Version format/diff checks
   pass. No full suite or Office/VSTO validation was run.
5. Before: the native Store rejected Delete. After: one bounded fact removes the
   live chat without loading historical bodies or rewriting memberships/other chats;
   the fault fixture observes zero ChatBodyReads. Deleted cards are excluded from
   catalog enumeration. This does not establish a Windows latency/allocation curve
   or close broader metadata/header costs; retained history still occupies storage.
6. Next: DeleteDocument, Move/MoveDocument, history rewrites/usage, remaining resource
   consumers and P3 metadata/header/authority snapshot. P4 stays deferred. Verified
   history/CAS/keys/locator backup, stopped old writers, full restore and Windows/
   Office screen-photo evidence still gate whole-port P7.
7. Session events, conversation protocol, migration map, progress and Harness guide
   are updated. Default writers remain unchanged, so this intermediate integration
   has no product version bump. The existing ChatStore/Store adapter is removed
   with whole-port P7. One scoped commit; no push/tag.

## P5 — native group catalog scope fix (2026-11-13)

1. Catalog capture constructed a temporary ChatSession with its default random ID.
   The native scope guard correctly rejected that nonexistent chat, so catalog
   consumers received an error instead of the group's metadata.
2. Existing ConversationGroupService now explicitly uses `Id=null` for scope-only
   catalog capture. The bound document/workspace guard and addressed-chat checks
   remain in the same owner; no additional store, DTO or fallback is introduced.
3. One production line changes. The discovered adjacent defect is fixed in its own
   commit, separately from native chat deletion.
4. On `3c038d8d` + this fix, the existing scoped UI-contract case checks a real native
   catalog for both Office and Workspace headers, plus its existing revision/scope
   guards. It passes; diff/version-format checks pass. No broad rerun or Office build.
5. Before: valid native catalog scopes had a fabricated chat identity and failed.
   After: the catalog returns its existing group. No performance claim is made.
6. Remaining Store/resource integration, metadata/header costs, P7 and Windows
   qualification remain open; P4 remains deferred.
7. Session events, progress and Harness guide are updated. Default production
   composition is unchanged, so no product bump; no push/tag.

## P1/P2 — native document chat deletion (2026-11-13)

1. Existing ConversationJournal now implements DeleteDocument with one
   `document.deleted` fact. Its exact address, prior cursor/hash, target count and
   witness identify the deletion boundary without a growing target array.
2. The existing resource scope lease covers metadata/run guards and publication.
   All matching chats must be idle before append; one active run rejects the whole
   set. One index transaction removes live cards/aliases/selection. Later creation
   and selection, other addresses, groups, CAS and canonical history survive.
3. Single/batch deletion share scope, idle guard, row update and exact read-back
   helpers. Existing card/chat-write locators prove the tombstone and its inclusion
   boundary. Lost flush/index acknowledgement is resolved without another append;
   partial index application rolls back and rebuilds atomically. Unavailable proof
   throws instead of claiming success. No new store/table/schema, import or fallback.
   Production C# +177/−14; the new Core partial is included in the old-style csproj.
4. Four new cases pass on an isolated source archive of `7fae9c36` plus the owned
   storage changes: Office/Workspace readers after bulk deletion, scope/cutoff,
   group retention, before-append/after-flush/index/partial-index/read-back faults,
   late active-run veto, scope lease refusal, independent later writer, rebuild and
   clean Workspace restore. Actual model Unknown and cross-journal resource proof
   survive document chat deletion and restore. Eight regressions pass: single-chat
   deletion (4), native groups (3), text Store consumers (1). Owned source files
   match the validated archive. Version-format/diff checks pass. Shared-worktree
   compilation hit an unfinished parallel Browser `StageBrowserVisualSelection`
   API; this evidence does not qualify that integration. No VSTO/Office/full-suite run.
5. Before: native DeleteDocument was unsupported. After: healthy-index deletion
   hydrates zero history bodies; the two-chat fixture writes a compact frame below
   4 KiB. Metadata validation remains O(live chats in the scope), and retained
   history still occupies storage. No constant-time, allocation or Windows latency
   claim is made. Groups are filtered on read rather than rewritten for each chat.
6. Next: Move/MoveDocument, remaining rewrites/usage and resource consumers, then
   P3 metadata/header/authority-snapshot work. P4 stays deferred. Verified backup,
   stopped old writers, complete restore and Windows/Office screen-photo evidence
   remain whole-port P7 gates. Default writers are unchanged.
7. Session events, conversation protocol, migration map, progress and Harness guide
   are updated. ChatStore and its existing adapter remain only until whole-port P7;
   this intermediate native integration has no product bump. One scoped commit;
   no push/tag. Parallel Browser edits are outside this storage change.

## P1/P2 — native chat relocation (2026-11-13)

1. Existing ConversationJournal now implements the remaining Move/MoveDocument
   Store operations. This closes their unsupported native path; overall persistence
   findings are not declared resolved before the default-writer cutover.
2. Single/batch address changes use one compact canonical fact and one index
   transaction. Stable chat/authority IDs, history, groups and Unknown/resource proof
   survive. Active runs, stale revisions and foreign workspace targets reject before
   append. Selection, transient drafts and later independent writers retain their
   existing semantics through actual Office Save As/alias and Workspace consumers.
3. Removed both Store stubs; reused deletion scope/idle guards and the existing
   lineage owner without constructing a full session for each metadata derivation.
   Schema 22 extends chat_write's key with chat ID; no new store/table/registry,
   compatibility import or fallback. The shared boundary guard recognizes an empty
   successor header already verified by StorageJournal during Move/DeleteDocument
   replay. Lost flush/index acknowledgement uses exact canonical read-back; failed
   read-back throws and does not redispatch. The new partial is in the Core csproj.
4. Five new cases and 12 selected regressions pass on an isolated source archive of
   `3da1992c` plus owned changes, macOS/net8/C#7.3. Cases cover real host-neutral
   consumers, identity/run/lease guards, append/flush/index/partial-index/read-back
   faults, later creation/metadata/selection, event feeds, Unknown/resource linkage,
   rebuild and clean restore. A retained empty successor header is checked through
   single/batch Move and DeleteDocument. Regressions cover deletion (8), text Store,
   feed paging, peer Inbox and existing Save As behavior. No VSTO/full-suite run or
   mixed Browser working-tree qualification is claimed. All 16 owned source/project
   files match the validation archive; version-format and scoped diff checks pass.
5. Before: native relocation was unsupported. After: the fixture reads zero
   message/context bodies and appends a frame below 4 KiB. Batch guards still scan
   live scope metadata; relocated headers follow only address changes since the
   last ordinary metadata commit. Lineage/header costs remain open. No CPU,
   allocations, physical I/O or Windows latency improvement is claimed.
6. Remaining: history rewrites/usage, resource consumers, P3 metadata/header and
   authority-snapshot costs. P4 stays deferred. Default writers remain ChatStore;
   verified backup/history/CAS/keys, writer shutdown, complete restore and Windows/
   Office screen-photo evidence remain cutover/release gates.
7. Session events, conversation protocol, MIGRATION_MAP, PROGRESS and Harness guide
   are updated. ChatStore/adapters retire at whole-port P7; this is an intermediate
   native integration without a product bump. One scoped commit; no push/tag.
   Parallel Browser changes are preserved outside this slice.

## P1/P2 — native catalog storage usage (2026-11-13)

1. Existing ConversationJournal closes unsupported usage catalogs under P1/P2;
   overall metadata/header findings remain open. Office GetChatSummaries and
   Workspace ListSessions now consume native storage fields through their Store port.
2. Accepted facts atomically update retained frame bytes, unique CAS count and
   logical bytes with their projection/cursor. Physical sizes and missing warnings
   use the existing ChatBlobStore metadata snapshot, including compressed files.
   Shared frame/blob attribution is per chat; resource-owned closure is unchanged.
3. Removed the usage rejection and updated its old test. Schema 23 adds counters
   to existing chat_state and one disposable chat_blob index, not another store.
   Native reducers reuse accepted records; primitives address only the new frame.
   Metadata upserts preserve accumulated counters. No import, fallback or dual write.
4. Three new cases and nine regressions pass on `6fbb7b65` plus owned changes,
   macOS/net8/C#7.3: real catalog consumers, deduplication, missing files, append/
   flush/index/partial-index faults, retries, relocation, rebuild/clean restore and
   actual Unknown/resource proof. Regressions cover text Store (2), peer Inbox (1),
   header recovery (1), deletion consumers (1), native spawn (4). Parallel Browser
   edits, including its changing Harness registration, are excluded from validation.
   No VSTO/Office or full-suite run; default-writer composition is unchanged.
5. Before: native usage catalogs threw NotSupportedException. After: the 16-message
   fixture hydrates zero retained bodies and reads at most eight journal records
   for its one healthy-index card. Repeated body references add one blob; unrelated
   chat writes do not grow that card's counters. Physical enumeration still depends
   on retained hashes/prefixes. No CPU/allocation/Windows latency claim is made.
6. History rewrites and remaining resource consumers are still open. Enabling
   edit/delete/clear must preserve indexed unresolved effects and bind the existing
   replay guard to runtime admission; hiding transcript rows cannot authorize a
   retry. P3 metadata/header/transient-summary/authority costs, cutover backup,
   writer shutdown, restore and Windows evidence remain; P4 stays deferred.
7. Session events, CAS maintenance, MIGRATION_MAP, PROGRESS and Harness guide are
   updated; BACKLOG records the separate session-summary/cursor attribution gap.
   Fourteen owned source/project files and the owned Harness registrations match
   the validated archive; version-format/diff checks pass.
   ChatStore/adapters retire at whole-port P7; no additional adapter is
   introduced. This intermediate native integration has no product bump, push or tag.

## P1/P2 — visible history and retained replay guards (2026-11-14)

1. Native text edit/delete/clear closes another existing Store operation. Office
   ChatHistoryEditService uses its mutation observer; Workspace deletion now commits
   through IConversationStore. This is not completion of resource-bearing history/fork.
2. A CAS membership manifest preserves ordered canonical locators and exact removals;
   the bounded frame carries only changed/new user text. Existing chat_message rows
   retain hidden IDs, preventing resurrection. Runtime facts/effect links/CAS roots
   and reference sequences are never removed. Active/pending runs cannot be erased.
3. IEventStore/IRunStore address one retained Unknown by exact proposed signature;
   AgentProgressTracker applies existing repeat/opaque-confirmation policy before
   dispatch. Native lookup failure has no fallback. RetainedReplayGuards explicitly
   separates the retiring adapter's transcript path until P7; no new store/schema.
   Feed invalidation includes removals/cleared run. Removed generic rewrite rejection
   for this scope and the concrete Workspace Commit calls on deletion/edit submission.
4. Four new checks and eight selected regressions pass, macOS/net8/C#7.3, isolated
   `e107add2` plus owned sources, excluding concurrent Browser commits. New checks:
   real Office/Workspace consumers, 160-message rewrite, one-body page, CAS/index
   recovery, append/flush/index/partial-index faults, duplicate/conflicting retry,
   actual Unknown write then deleted exchange, independent read and opaque approval.
   Workspace restore keeps the original Unknown terminal and action guard. Regressions:
   native runtime (4), Office/Workspace mutation (1), confirmation/resume (1), lost
   confirmation claim (1), existing kernel unknown guard (1). No full suite or VSTO.
5. Before: explicit rewrite failed and the indexed replay guard had no runtime
   consumer. After: more than 128 retained messages use one manifest; healthy page
   hydration remains one selected body. Only the uncertain exact operation is denied;
   unrelated read/finish works. Rewrites still cost O(selected history); no allocation,
   CPU, Windows latency or full P3 improvement is claimed.
6. Actual Office binding creates documents.json in the existing authority registry.
   Journal backup correctly refuses that mixed root: complete Office/application
   backup remains open, recorded in BACKLOG. No registry deletion/skip or data import.
   Remaining: fork/resource-bearing history, resource-state/attachment consumers,
   P3 metadata/header/context selection, P6/P7 complete backup/writer shutdown/cutover;
   P4 deferred and Windows/Office evidence separate. Default writer stays ChatStore.
7. Session events, Resource Fabric, MIGRATION_MAP, BACKLOG, PROGRESS and Harness guide
   updated. This is an intermediate native integration, without product bump/tag/push.
   All owned source files and registrations match the tested archive; required
   version-format and diff checks are performed before commit.

## P1/P2 — native attachment consumers (2026-11-14)

1. ConversationJournal now accepts message/queued attachments through the existing
   Office ingestion and Workspace resource provider. This closes one resource
   consumer under P1/P2; default composition and the remaining owners are unchanged.
2. Original/extraction/analysis bytes live in CAS. Headers omit extracted previews;
   analysis has a separate reference. Pending input roots files before delivery.
   Transitions cannot swap files, and Applied requires the same text and originals.
   Immutable upload metadata cannot be rebound by normal updates or text rewrites.
3. Attachment membership/attach-detach decisions use one reference-only CAS manifest.
   Original metadata remains in the accepted Workspace message or document authority.
   Schema 24 adds a disposable locator to existing JournalIndex; rows and cursor
   commit atomically. Existing artifact construction is reused, with no new store,
   registry, adapter, import, dual-write or fallback. Metadata-only text commits do
   not open a resource owner. Relocation preserves the working-set reference.
4. Three new checks and ten selected regressions pass on macOS/net8/C#7.3 in an
   isolated c71598d8 archive plus owned changes. New cases cover real Office ingestion
   and Workspace reads, queued-only files, exact delivery, missing CAS, lost ack,
   index failure, restart/rebuild, detach, relocation, history and Workspace restore.
   Regressions: native input (4), runtime (4), Office history consumer (1), existing
   attachment targets (1). Only the affected runtime/input failures were rerun after
   removing unnecessary resource-owner construction. No full suite or VSTO run.
5. Before: native Store rejected attachments/analysis and queued files. After: a
   later reference-only page resolves its original from a source-header locator or
   document authority, without hydrating the earlier message, 70 KB analysis or file.
   That page reads one selected message plus the working-set manifest; catalog body
   reads remain zero. Working-set metadata size, full-load repetition and authority
   reads remain P3 costs; no CPU/allocation/Windows latency improvement is claimed.
6. Remaining: Plan/Task/HTML/context-state consumers, fork and their history paths;
   P3 metadata/header/context/summary usage; complete per-owner backup/restore, old
   writer shutdown, default cutover and ChatStore/adapter removal. Office identity
   backup remains open; only Workspace restore is qualified here. P4 is deferred;
   Windows/Office photo evidence remains separate. Retry-policy follow-up is recorded
   in BACKLOG (c71598d8), not implemented or added as a mandatory storage phase.
7. Session events, Resource Fabric, MIGRATION_MAP, PROGRESS and Harness guide updated.
   Owned source files match the validation archive; version-format and scoped diff/link
   checks run before commit. Concurrent Browser work is excluded. This intermediate
   native integration has no product bump, tag or push.

## P1/P2 — native Plan consumers (2026-11-14)

1. Native Plan selection/membership closes the next resource consumer under P1/P2.
   Real Office/Workspace kernel publication now reaches the existing Plan owner
   and exact resource-outcome link instead of the unsupported-owner rejection.
2. The attachment manifest becomes the same owner's resource manifest, adding an
   optional exact active Plan reference. Its existing serialized field name stays
   stable; no parallel format, fallback, store, registry or index schema is added.
   Plan metadata/Markdown/lineage remain in DocumentArtifactStore / ResourceJournal.
3. WorkspacePlanService uses authority/revision/mutation ports supplied by
   WorkspacePorts, including accepted-call origin. Office retains its observer.
   Both use existing preparation/publication/recovery. Removed only Plan-specific
   native denials. The shared Store state helper is separated from attachment
   binding to avoid duplicating manifest/load/page logic for another resource owner.
4. Three Plan checks pass: actual kernel creation on both hosts; shared lifecycle,
   metadata pages, off-page tombstone, index failure, lost chat acknowledgement,
   rebuild and Workspace restore; real Workspace Unknown reconciliation with
   independent chat input. Six selected regressions pass: attachments (3), native
   file/VBA mutation (1), confirmation (1), default Workspace scoped Plan read (1).
   Tests use macOS/net8/C#7.3 in an isolated 9955637e archive plus owned sources;
   no full-suite or VSTO/Office validation. The prior unsupported-Plan test is
   replaced by the connected runtime case, not retained as a stale requirement.
5. Before: native Store rejected active Plan state and Workspace used legacy
   resource owners. After: load/pages retain exact selection without Markdown
   hydration; the selected page reads one message and one reference manifest.
   At most one latest retained metadata snapshot per on-page Plan family adds
   removal semantics. Full working-set metadata scans and authority lookups remain
   P3 costs; no CPU/allocation/Windows latency improvement is claimed.
6. Remaining: Task/HTML/context-state consumers, fork and remaining history paths;
   P3 metadata/header/context/summary usage; complete per-owner backup, old writer
   shutdown and default cutover with ChatStore/adapter deletion. Default writer
   stays ChatStore, Office identity backup and Windows evidence remain open, P4
   remains deferred. Plan publication survives a lost chat receipt; Unknown never
   grants a repeat or becomes a verified effect through presentation state.
7. Session events, Resource Fabric, Artifact Library, MIGRATION_MAP, PROGRESS and
   Harness guide updated. Owned sources match the validation archive; scoped
   diff/links and mandatory version-format check run before commit. Intermediate
   native integration: no product bump, tag or push.


## P1/P2 — native Task List consumers (2026-11-14)

1. Task List is the next P1/P2 consumer after Plan. Real Office/Workspace kernel
   save/update/blocked/close now use native conversation snapshots and exact
   resource-outcome proof through the existing TaskListPublication owner.
2. ConversationJournal appends bounded new snapshot-header deltas; bodies live in
   CAS and unchanged revisions are not republished. The existing reference manifest
   gains exact ActiveTask selection. JournalIndex schema 25 adds a rebuildable
   chat_task_list locator in the same index/cursor transaction. CAS closure/usage
   retains historical bodies after clear. No store, registry, adapter, import,
   dual-write or fallback is introduced. Snapshot body/metadata/parent rebinding
   and a selected closed list are rejected before acceptance.
3. WorkspaceTaskListObserver consumes supplied authority/revision/mutation ports
   and accepted-call origin; Office retains its bound observer and persists before
   capture/publication. Both use the same frozen-authority projection and mutation
   precondition. Unknown makes current-list state unavailable and denies another
   save, while exact historical reads and independent input remain usable. A saved
   body alone does not turn interrupted execution into a verified outcome.
4. Three new checks pass on macOS/net8/C#7.3: real kernel parity with schema admission
   and exact proof; snapshot/index/ack/retry/restart/rebuild, cold history rewind and
   Workspace restore; interrupted Task dispatch with both observers' rejection,
   independent input and restored Unknown. Ten selected regressions pass: existing
   Task Lists (5), native Office history edit/clear and Workspace deletion (1),
   native Plan kernel parity (1), attachments (3). The old Task rewind fixture now
   uses the actual history mutation owner instead of bypassing authority. No full
   suite, VSTO/Office validation or Windows evidence is claimed.
5. Before: native Store rejected Task state; Workspace used separate default
   resource constructors; history editing hydrated every reachable Task body.
   After: Load and accepted-session state keep only the active body hot (128 KiB
   bound), message pages read referenced metadata, and history selects by metadata
   and loads one body. The tested one-message page reads text plus one manifest,
   zero Task bodies. Metadata/header/working-set scans and Workspace full read-back
   remain P3 costs; no unmeasured CPU/allocation or Windows latency claim is made.
6. Remaining: HTML/context-state consumers, fork and remaining resource-bearing
   history; P3 metadata/header/context/summary usage; complete owner backup, writer
   shutdown and default cutover with ChatStore/adapters removed. Office identity
   backup and Windows qualification stay open. P4 remains deferred. The native
   composition is still isolated and production storage remains ChatStore.
7. Canonical session/resource/artifact docs, migration map, progress and harness
   guide updated. Product version is 16.27.2-dev: shared Office admission/projection
   now matches Workspace, and history preserves blocked selection while avoiding
   all-history body hydration. This shared behavior change warrants a patch even
   though native storage integration remains intermediate. Scoped diff and mandatory
   ValidateVersionFormat pass before commit; no tag, push or release.


## P1/P2 — native HTML consumers (2026-11-14)

1. Connected the next resource consumer through actual Office skill admission and
   kernel HTML creation/write, plus Workspace saved-project services. Native Store
   now retains exact HTML selection and message checkpoints instead of rejecting
   them. Existing DocumentArtifactStore/ResourceJournal owns every published body,
   revision, logical head and outcome; conversation facts retain references only.
2. The existing resource manifest gains optional ActiveHtml. Pages normalize member
   refs to metadata roots and retain removal/selection semantics. No new store,
   registry, canonical format variant or index schema. Legacy chat-owned HTML stays
   unsupported. Other published document metadata uses the same source-owner read
   and immutable projection validation; context-state publication is not claimed.
3. Moved HTML reconstruction out of ChatStore into existing
   HtmlWorkspaceNavigationService; removed its private duplicate parser/lookup.
   Current active bytes and source lineage reconstruct the disposable view. Missing
   or corrupt bytes produce noneditable recovery without losing selection or
   independent chat input. Workspace/Document version and working-set services use
   injected resource ports; project copy/import retains the injected file owner.
   Added addressed ReadHeader on the existing conversation port for exact run actor
   checks, reusing its current header projection without transcript hydration.
   Reference-only SelectedSkills/tool pins survive native run commits; the existing
   skill owner revalidates exposure. Unsupported mutation owners remain rejected.
4. Two new checks pass: actual Office skill/kernel/publication, page/checkpoint/
   selection faults, corrupt body, Unknown and restart/rebuild; actual Workspace
   version/run-owner guards, metadata membership, offline-root restart/copy and
   journal/CAS restore. Twelve selected regressions pass: Task List (3), Plan kernel
   (1), attachments (3), HTML checkpoints (3), navigation (1), broken-parent recovery
   (1). A separate recovery-blocks fixture fails identically on archived fdfcfe66
   and current sources at unchanged direct UpsertFile/Capture(null); backlog records
   that unsupported test path. Native corrupt-source recovery passes. macOS/net8,
   C#7.3 only; no full suite or Office/VSTO validation.
5. Before: HTML snapshots/checkpoints and selected skills failed native Store
   admission; HTML version services constructed legacy ports. After: normal message
   pages load one selected text and the reference manifest, zero HTML bodies; full
   HTML selection hydrates only the active aggregate. Version actor checks read no
   chat or run bodies. Removed metadata flags are reused without another authority
   read; chats without active HTML do not construct its projection. Metadata scans,
   lineage enumeration, header/selection size and large aggregates remain P3/P4
   costs; no unmeasured allocation/Windows latency claim is made.
6. The real saved-project consumer exposed another P6/P7 dependency: journal/CAS
   restore omits WorkspaceStore connection associations. Snapshot bytes and chat
   refs restore, but complete application restore remains open. Recorded with the
   existing backup owner; no inferred connection or fallback added. Remaining:
   context-state, fork, P3, complete owner backup/quiescence and production cutover
   with retiring ChatStore/adapters removed. Office identity backup, Windows evidence
   and deferred P4 remain open. Default writer stays ChatStore.
7. Canonical session/artifact/CAS docs, migration map, backlog, progress and Harness
   guide updated. Scoped diff/links and ValidateVersionFormat checked before commit.
   This is intermediate native integration plus behavior-preserving owner extraction;
   no product bump (16.27.2-dev), tag, push or release.

## P1/P2 — native context-state consumers (2026-11-14)

1. Continued F04/F08/F10 integration through ConversationJournal and the existing
   ContextCompactionRuntime, DocumentArtifactStore and Workspace compiler. This does
   not close their remaining P3 scans/serialization. Typed notes, local checkpoint
   selection and optional shared publication now survive native persistence.
2. Local snapshots retain immutable CAS bodies and typed boundaries; selection
   loads one checkpoint. Notes preserve exact instructions versus supplied data /
   observations. Fixed the actual Workspace consumer omitting notes and their
   authority scopes. History edits invalidate the summary without deleting canonical
   roots; compaction cannot replace Unknown or its addressed replay guard.
3. Reused the Task snapshot reducer as ConversationJournal.Snapshots and schema 26
   chat_snapshot; removed the Task-only implementation/file. Existing source field
   naming is unchanged. No new store, registry, adapter, import or dual-write.
   CAS maintenance now follows typed context/checkpoint containers and their nested
   payloads instead of omitting instruction bodies from native backup closure.
4. macOS/net8/C#7.3: two new `journal context:` checks pass (T01/T06/T13): forced
   compaction plus actual Office/Workspace kernel requests, two checkpoints, typed
   notes, lost index/ack/retry, rebuild, history edit/delete, nested-payload restore
   and Unknown with independent input. Eight regressions pass without rebuilding:
   Task List (3), HTML (2), shared context (2), legacy compaction projection (1).
   No full suite, VSTO/Office validation or new automatic-threshold qualification.
5. Before: native Store rejected context-state. After: the same fixture retains two
   checkpoint artifacts but loads one checkpoint body; a one-message page reads two
   chat bodies (text and manifest), zero checkpoint bodies. Addressed header reads
   zero chat bodies. CAS-maintenance visits each distinct context container once.
   Full history/metadata work and nested context usage attribution remain P3; no
   CPU/allocation or Windows latency claim is made.
6. Remaining: fork/other unsupported resource consumers, P3 metadata/header/context/
   summary usage, complete owner backup, old-writer shutdown and default cutover.
   Office identity and Workspace connection backup, plus Windows evidence, remain
   open. P4 is deferred; production storage still uses ChatStore.
7. Updated Session events, Conversation protocol, Artifact Library, CAS maintenance,
   migration map, progress and Harness guide. Existing default composition/adapters
   retire at P7. Version 16.27.3-dev includes the shared Workspace notes correction;
   CHANGELOG records it. Scoped diff/links and ValidateVersionFormat checked before
   commit; no tag, push or release.

## P1/P2 — native fork preparation (2026-11-14)

1. Advances F04/resource-consumer migration through existing IConversationStore,
   ConversationJournal, ChatCloneService and ResourceForkService. The scoped result
   is exact native source preparation; atomic native fork acceptance remains open.
2. Office uses the conversation port for artifact bodies; Workspace uses it for
   transient creation and commit. Native reads fence the loaded source revision and
   immutable metadata, retaining document/local snapshot ownership. Preparation
   reads selected Task/document HTML only; missing selected bytes fail explicitly.
   Checkpoint clones now preserve shared-publication provenance and failure state.
3. Removed the affected concrete ChatStore calls and all-reachable Task/document
   HTML hydration. Shared HTML no longer enters conversation-definition copying.
   Existing chat-owned HTML dependencies remain only in the old composition until
   P7 removal. No new store, adapter, schema, import, dual-write or hidden fallback.
4. Seven selected checks pass on macOS/net8/C#7.3: native Office HTML, Office/Workspace
   Task snapshots, shared HTML selection, failed fork preparation, definition graph
   copying, real Workspace fork and checkpoint publication cloning. They include
   corrupt older/selected bodies, exact selection, source concurrency, no publication
   during preparation and explicit native commit rejection without partial writes.
   The old broad clone fixture has invalid batch/version expectations; recorded in
   backlog, unchanged. No full suite or Office/VSTO validation.
5. Before: preparation requested every reachable HTML/Task body. After: one selected
   body per kind; fixture counters show one local Task CAS read, zero chat-body reads
   for document HTML (its selected body uses the resource owner), unchanged journal
   write counters and cold historical metadata. Source history is not reloaded.
   No CPU/allocation or Windows latency measurement is claimed.
6. Next: atomic native prefix membership and inherited call/result/Unknown evidence,
   preserved independently of source rewrites/deletion and visible prefix. A copied
   result must not count as child execution or authorize repetition. Remaining
   consumers, P3 metadata/header/context/summary usage, complete owner backup and
   whole-port cutover remain open. Default writer is ChatStore; P4 is deferred.
7. Updated canonical session/artifact contracts, migration map, progress, backlog
   and Harness guide. Version 16.27.4-dev records the shared fork improvement in
   CHANGELOG; default adapter retires at P7. Scoped diff/links and mandatory version
   validation checked before commit. Windows evidence remains open; no tag, push or release.

## P1/P2 — native atomic text fork (2026-11-14)

1. Advances F01/F04 through existing ConversationJournal, history/CAS/index and
   actual Office publication / Workspace fork owners. Native text forks now commit
   atomically; resource-bearing forks retain their explicit rejection.
2. One bounded frame freezes source address/revision/hash and boundary; the existing
   history manifest retains original message locators. Calls/results keep canonical
   source ownership, without child call/result/effect records. Source must be idle;
   runs/confirmations/assignments are not resumed. Lost acknowledgement retries the
   same command, including after ancestor deletion; altered evidence is rejected.
3. Removed the blanket native-fork rejection for the supported scope and same-chat
   locator assumptions in pages/history. Workspace uses its existing fork method
   with the Store port, extracted without another service. Schema 27 adds chat to
   existing context-obligation/dependency keys; inherited Unknown/repeat guards and
   reserved aliases are applied with membership in the same index transaction.
   No new store, registry, import, dual-write or hidden fallback.
4. Three new scenarios pass: actual Office/Workspace text fork, 140-message prefix,
   child editing and multi-generation source deletion; before-append/after-flush/
   index faults, exact source fencing, forgery rejection and same-command retry;
   actual Workspace kernel repeat rejection plus independent read after inherited
   Unknown outside the visible prefix, rebuild and journal/CAS restore. A prefix
   cut within a tool exchange preserves derived exclusion through child edits.
   Ten regressions pass without another build: history (4), context-state (2),
   Task, HTML, Workspace default fork and document move (1 each). macOS/net8/C#7.3;
   no full suite or Office/VSTO validation.
5. Before: native Store rejected every fork. After: the 140-message fixture appends
   less than 16 KiB to the conversation journal; a one-message page reads one text
   body, zero membership manifests. Existing CAS payloads are reused. Fork creation
   still explicitly loads/clones/verifies the selected history; metadata/ancestry
   work and inherited per-chat usage attribution remain P3. No CPU/allocation or
   Windows latency claim is made.
6. Next: resource-bearing fork publication and ownership (artifacts, attachments,
   checkpoints and supplied-data/definition copies), then remaining consumers/P3,
   complete owner backup/quiescence and P7 removal of ChatStore/adapters. Unknown
   retry-policy refinement remains its separately recorded task. P4 is deferred;
   production composition, Office identity/Workspace backup and Windows gates remain open.
7. Updated session/artifact/CAS contracts, migration map, progress and Harness guide.
   This is intermediate native integration and a behavior-preserving extraction of
   the default consumer: no product bump, version remains 16.27.4-dev. Scoped diff,
   changed documentation links and ValidateVersionFormat checked before commit.
   No tag, push or release.

## P1/P2 — native shared resource fork (2026-11-14)

1. Advances F04 through existing ConversationJournal, ArtifactWorkingSet and actual
   Office/Workspace fork consumers. Scope: published shared Plans, document HTML and
   document-owned originals; conversation-local graphs remain a separate boundary.
2. A fork retains shared metadata, links and exact selection independently of the
   dialogue prefix. Detach, off-prefix tombstones, original attachments/analysis and
   publisher provenance survive. Shared heads never rewind or republish; historical
   refs retain their original revision. Later publication leaves those bytes intact,
   and a stale write fails. Real kernel call/results do not become child executions.
3. Replaced incomplete manual/JSON artifact clones with existing-model CopyMetadata;
   existing working-set owner supplies common fork behavior. Native canonical
   validation now admits shared references while retaining source membership and
   selection. No body copies, new store/registry, schema change, fallback or dual-write.
4. Ten selected checks pass on macOS/net8/C#7.3: one new shared Office/Workspace case,
   two extended actual-kernel Plan/HTML cases, three text-fork regressions and four
   existing graph/HTML/Workspace/Task regressions. Coverage includes stale writes,
   lost acknowledgement retry after source deletion, rebuild and Workspace journal/
   CAS restore. One build reused; no full suite or Office/VSTO validation.
5. Before: native forks rejected shared resources; Workspace prefix filtering and
   JSON cloning lost working-set state/provenance. After: shared metadata stays
   cold and no payload is copied. A one-message page reads two chat bodies in the
   Workspace fixture, three with Office attachment analysis, no Plan/original bytes.
   All-shared metadata scans and initial history cloning remain P3. No CPU/allocation
   or Windows latency claim; selected HTML keeps its existing hydration owner.
6. Next: local Task/checkpoint snapshots, Workspace chat originals and supplied-data
   definition-copy publication. P3 metadata/header/context/summary usage, complete
   owner backup/quiescence and P7 removal of ChatStore/adapters remain open. Default
   writer is still ChatStore; Office identity/Workspace connections and Windows
   evidence are not qualified by journal/CAS restore. P4 stays deferred; Unknown
   retry-policy refinement remains its separately recorded task.
7. Updated session/artifact/CAS contracts, migration map, progress and Harness guide.
   Version 16.27.5-dev records the shared production Workspace correction in
   CHANGELOG; native composition remains isolated until P7. Scoped diff/links and
   mandatory ValidateVersionFormat checked before commit. No tag, push or release.

## P1/P2 — native checkpoint fork (2026-11-14)

1. Advances F04 through ContextCompactionRuntime, ConversationJournal and existing
   Office/Workspace fork consumers. Scope is local compaction snapshots and their
   retained lineage; Task authority and definition copies remain separate.
2. Fork preparation retains exact selected claims only if the source activity and
   boundary remain in the prefix. Missing selected bytes fail; shorter prefixes or
   history-invalidated selection never activate an older summary. Workspace now
   preserves selection. Child compaction can append to the inherited lineage.
3. Replaced Office's selection from in-memory clones and Workspace's omitted
   selection with the existing compaction owner. Native snapshot index rows point
   to original frames, guarded by fork ancestry; only presentation links rebase.
   No copied snapshot delta, new store/schema/adapter, fallback, dual-write or
   invented child tool execution. Claims and Unknown retain original provenance.
4. Eight selected checks pass on macOS/net8/C#7.3: new Office/Workspace checkpoint
   fork, extended actual-kernel Unknown, shared fork, atomic fork faults, two context
   cases, Task snapshots and default Workspace fork. The new case covers parent
   membership, selected/cold corrupt bytes, continuation, lost ack after source
   deletion, two ancestors, edits, rebuild and Workspace journal/CAS restore. Only
   the changed checkpoint case was rebuilt/rechecked after the final local lookup
   correction; no full suite or Office/VSTO validation.
5. Before: native forks rejected local checkpoints and Workspace dropped selection.
   After: one canonical fork frame adds no copied snapshot metadata; existing CAS
   is reused. A one-message page reads two chat bodies (text and membership), zero
   checkpoint bodies; selected preparation leaves an unavailable older body cold.
   Parent-membership checks use a set rather than nested scans. Initial history/
   metadata scans and inherited usage remain P3; no CPU/allocation/timing claim.
6. Next: Task fork selection and child authority publication (recorded in backlog),
   Workspace originals and supplied-data/definition copies. P3 metadata/header/
   context/summary usage, complete owner backup/quiescence and P7 remain open.
   Default writer is ChatStore; P4 deferred. Windows evidence and full Office
   identity/Workspace connection backup are not closed by journal/CAS checks.
7. Updated session/artifact/CAS contracts, migration map, progress, backlog and
   Harness guide. Version 16.27.6-dev records the shared production consumer fix
   in CHANGELOG. Default adapters retire at P7. Scoped diff/links and mandatory
   ValidateVersionFormat checked before commit; no tag, push or release.

## P1/P2 — native Task fork (2026-11-14)

1. Advances F04 through TaskListPublication/TaskListService, ConversationJournal
   and existing Office/Workspace fork consumers. Scope: exact Task prefix selection,
   inherited snapshot lineage and independent child authority.
2. Active/blocked selection hydrates one exact body; closed prefixes retain cold
   history without reactivating an older revision. Workspace prepares publication,
   commits the child and compares durable parent/boundary/snapshot/hash before
   publishing. Lost acknowledgement resolves exact saved state; absent/different
   state remains Unknown without recreating the child or replaying a Task effect.
3. Reuses the existing snapshot index and original canonical/CAS locators, with no
   schema/store/adapter or copied snapshot delta. Typed Task result refs replace
   activity-JSON reconstruction; shared selection replaces Office's duplicate scan.
   Removed arbitrary URI rewriting inside historical call/result/activity payloads.
   Only child presentation refs rebase; no invented child execution or source proof.
4. Nine selected checks pass on macOS/net8/C#7.3: three Task cases, new fork recovery,
   default Workspace fork, shared/checkpoint/Unknown forks and clone contract.
   Real kernel history covers Office/Workspace child edits and unchanged source
   heads; recovery covers both journal acknowledgements, source deletion, rebuild
   and journal/CAS restore. The clone fixture was updated only for valid current
   protocol inputs and the changed preservation contract. No full suite/Office build.
5. Before: native Task forks were rejected; default Workspace lost selection/head.
   After: four closed revisions remain cold; a blocked-prefix fork retains three
   snapshots and hydrates one. A one-message page reads two chat bodies (text and
   membership), zero Task bodies. No new snapshot metadata/body copies are appended.
   These are logical I/O/structure checks, not measured CPU/allocation/time gains.
6. Next: Workspace originals, supplied-data/definition copies and remaining
   composition consumers. Recorded the older Workspace history edit/delete Task
   authority gap separately. P3 metadata/header/context/summary usage, complete
   owner backup/quiescence and P7 removal remain open. Default writer is ChatStore;
   P4 deferred. Windows/Office and full application restore need separate evidence.
7. Updated session/artifact/CAS contracts, migration map, progress, backlog and
   Harness guide. Version 16.27.9-dev records production fork fixes in CHANGELOG;
   pending UI slices remain separate. Default adapters retire at P7. Scoped
   diff/links and mandatory ValidateVersionFormat precede commit; no tag/push/release.
