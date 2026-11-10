# ADR-0019: bounded JSONL storage and context

Status: accepted target, implementation in progress, 2026-11-10.
Source: [user implementation plan](../proposals/RNAssistant_Architecture_and_Implementation_Plan.md), §§0–2, 8.3–8.4.
Current production contracts remain in [Session events](../session-events.md) until each tested cutover.

## Decision

- One segmented conversation JSONL per stable document authority/workspace identity;
  never the generic `office` registration key. One segmented resource JSONL retains
  logical `ResourceAuthorityScopeId` values. Groups stay with resource authority.
- Immutable CAS owns source/view/domain bodies. An occurrence owns call/run IDs,
  timestamp, outcome, eligibility and evidence correlation. Repeated reads reuse the
  body without reusing the occurrence or granting delivery authority.
- One disposable SQLite index per new root, using the existing native provider.
  Composite keys include stream/scope/chat. No full session/header JSON columns,
  second query backend, import, dual-write or silent dependency fallback.
- CAS durable flush/publication precedes JSONL append+durable flush. One bounded
  UTF-8 object plus LF is one acceptance boundary; sequence and previous hash form
  an authenticated chain. Index transaction follows acceptance. Index/notify failure
  cannot turn a committed operation into a safe-to-retry external effect.
- Metadata frame maximum: 256 KiB of encoded bytes, including protection overhead;
  bodies use CAS. Initial rotation threshold: 16 MiB or 4096 frames. A single frame
  may cross the byte threshold; the following append rotates. These are defensive
  bounds, not throughput claims; fixtures must test the exact encoding boundary.
- First/successor headers use create-only publication after durable temp flush.
  A successor binds predecessor segment/length/sequence/hash and closes it. No
  separate authoritative manifest/seal or replacement of a published header.
- A complete valid no-LF tail is validated and durably terminated under the append
  lock; only demonstrably incomplete unaccepted suffix bytes may be discarded.
  Full bad-schema/hash records and damaged accepted prefixes fail closed.
- Lock order: resource gate if needed → shared metadata admission → one stream
  append lock → short index transaction. No model/COM/hash/decode under append/SQL;
  never hold two append locks. Exclusive repair drains only metadata sections,
  not run leases or CAS publication activities. Recheck operation receipt afterward.
- Normal reads catch up only required streams. One typed reducer serves append,
  suffix and streaming repair. Operation IDs/fingerprints and subject-specific
  guards replace a global session revision guard. Resource attempt receipts attach
  idempotently to conversations; unknown dispatch never automatically replays.
- An accepted mutating call retains a deterministic scope/chat/run/call attempt
  binding. Resource publication commits first; its immutable locator is verified
  outside the conversation append section. A later linkage retains historical
  proof without rewriting the original terminal or advancing a newer resource head.
  The isolated P2d implementation currently supports one target per publication.
- New storage lives in a separate generation. Old data/keys are preserved, with
  explicit incompatible-format handling. Production switch waits for durability,
  maintenance/reachability and consumer integration gates. No ClearRuntimeData.

## First change and acceptance

F01/F04/F23 → Core result persistence / ChatStore / ChatBlobStore → one typed
result/body contract and compact delta → remove host-specific wire persistence
and whole-session save from the migrated path → T01/T02/T03/T05/T06.
Normal append must visit zero historical message bodies, repeated exact reads must
not create per-call copies of the source, and restart/query must work without live
source reads. P0 counters and open gates are in the [audit](../audits/storage-context-optimization.md).

Windows net48/winsqlite3, actual Office/WebView2 and filesystem power-loss guarantees
remain separate evidence. Host-neutral checks cannot close them.
