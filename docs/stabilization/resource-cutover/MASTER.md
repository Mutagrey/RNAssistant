# RNAssistant — Resource Architecture MASTER Implementation Order

**Status:** master execution guide for one integrated direct cutover.

**Purpose:** define the reading order, dependency order, ownership boundaries, implementation sequence, and legacy-removal rules for the three canonical resource/context specifications.

**Important:** this file does **not** redefine the contracts in the three canonical documents. It only tells the coding agent how to implement them as one architecture without duplicating concepts or running three independent migrations.

---

# 0. Coding-agent instruction

Treat the following three documents as **one architectural contract** and implement them in a single coordinated cutover:

1. `RNAssistant_UNIVERSAL_RESOURCE_FABRIC_FINAL_DIRECT_CUTOVER.md`
2. `RNAssistant_RESOURCE_AUTHORITY_CONSISTENCY_AND_MUTATION_COMMIT_FINAL_DIRECT_CUTOVER.md`
3. `RNAssistant_RESOURCE_LINEAGE_EVIDENCE_CONTEXT_COMPILER_FINAL_DIRECT_CUTOVER.md`

Do **not** implement them as three independent feature plans and do not execute each document's local Phase A/B/C/D sequence separately from the others.

The global dependency is:

```text
PHYSICAL / DOMAIN STATE
Excel / VBA / files / JSON / PDF / image / artifact / package
        |
        v
UNIVERSAL RESOURCE FABRIC
identity / exact revisions / views / coverage / payloads / providers / effects
        |
        v
RESOURCE AUTHORITY
current heads / generations / atomic publication / mutation recovery / drift
        |
        v
RESOURCE EVIDENCE
observations / current-superseded-unknown projection
        |
        v
MODEL AUTHORITY SNAPSHOT
frozen resources + effects + ToolPack + Skills + Schemas + event high-water
        |
        v
MODEL CONTEXT COMPILER
correctness -> relevance -> hydration -> compaction -> budget -> request
        |
        v
LLM
```

Consumers attach to the same resource/authority layer:

```text
Agent tools
HTML / RN.resources
Artifact viewers
future Python adapter
```

There must be **one** implementation of each canonical concept. Do not preserve a second legacy path for compatibility in contours being cut over.

---

# 1. Authority of the three documents

Use domain ownership, not file order, to resolve questions.

## Universal Resource Fabric document owns

```text
ResourceIdentity
ResourceRevisionRef / exact ResourceRef
RevisionId != ContentHash
ResourceDescriptor
ResourceCoverage
Resource View / Capability
Provider / ResourceGateway
PayloadRef / CAS usage
ResourceLease / continuation semantics
ResourceDependency / provenance
ResourceEffect / ResourceImpact data contract
semantic schema / mapping / derived-resource primitives
RN.resources data-plane contract
```

## Resource Authority document owns

```text
ResourceAuthorityScopeId
DocumentAuthorityId
ResourceHeadState
ResourceAuthorityStore
ResourceAuthorityGeneration
ResourceAuthoritySnapshot / SnapshotSet
ResourceAuthorityCommit
atomic effect + head + generation publication
expected-revision guards
mutation attempt / dispatch / verification lifecycle
UnknownAfterDispatch
external drift / reconciliation
cross-chat and cross-consumer currentness
publication barriers
Save / Save As / reopen identity semantics
retention required for authority and unresolved mutations
```

## Resource Evidence & Model Context document owns

```text
ResourceEvidence
EvidenceStateReducer
current / superseded / unknown evidence state
ModelAuthoritySnapshot used for one compile
ContextAtom
ToolInteractionFrame
ModelContextCompiler
ModelContextSnapshot
ContextReceipt
correctness-first filtering
selective payload hydration
structured claims / provenance-aware compaction
model-request assembly
```

### Conflict rule

If wording appears to overlap:

```text
URF defines WHAT a resource/revision/effect is.
Authority defines WHEN current state changes and becomes visible.
Context defines WHAT the model may see from that frozen state.
```

Do not create a new abstraction to reconcile wording differences. Use the ownership rule above.

---

# 2. Read order before editing

Read in this exact order:

```text
1. this MASTER file
2. UNIVERSAL_RESOURCE_FABRIC_FINAL_DIRECT_CUTOVER
3. RESOURCE_AUTHORITY_CONSISTENCY_AND_MUTATION_COMMIT_FINAL_DIRECT_CUTOVER
4. RESOURCE_LINEAGE_EVIDENCE_CONTEXT_COMPILER_FINAL_DIRECT_CUTOVER
```

Then inspect only the repository contours directly referenced by the current implementation wave. Do not spend a separate broad research phase rediscovering the entire project.

Open existing canonical primitives before creating replacements:

```text
ResourceRef / resource registry / gateway / providers
CAS / blob / snapshot storage
Excel and VBA read/write owners
conversation/event persistence
ConversationModelSession
ConversationPromptComposer
PromptBudgetComposer
ContextCompactionService
ModelToolResultProjection
ToolResultResourceService
ToolPack / SkillCatalog / schema catalog code
HTML workspace / AssistantWebBridge / WebView transport
artifact viewer/resource paths
DocumentContext / OfficeContextCaptureService
HostRuntime / document gate
```

Prefer adapting existing primitives when their semantics match. Replace or rename them when their current semantics are incompatible. Do not wrap an incorrect legacy contract merely to avoid touching callers.

---

# 3. Non-negotiable global invariants

The final implementation must satisfy all of these:

```text
RevisionId != ContentHash
path/locator != DocumentAuthorityId
conversation != resource authority scope
historical revision != current head
ResourceEvidence != resource authority
ResourceEffect != ResourceRevision
PayloadRef != ResourceRevision
lease != historical identity
conversation history != model context
compaction != source-of-truth mutation
HTML state != independent freshness truth
Tool/Skill/Schema storage != active authority generation
```

And:

```text
one ResourceGateway
one ResourceAuthorityStore
one current-head truth
one EvidenceStateReducer
one ModelContextCompiler
one model-request assembly path
one CAS/blob authority already used by the project
```

No model-context compiler or reducer may perform live Excel/VBA/provider I/O.

No HTML bridge, viewer, future Python adapter, or model-context code may call Excel COM directly around the domain/resource layer.

---

# 4. Global implementation order

The local phases in the three detailed documents are dependencies inside the following **five global waves**. Implement in this order.

---

# WAVE 1 — Canonical Resource + Authority Foundation

**Primary source:** URF Phase A + Authority Phase A.

Implement/normalize first:

1. `ResourceIdentity` and exact `ResourceRevisionRef` semantics;
2. stable logical identity independent of current revision;
3. `RevisionId` separate from `ContentHash` and `PayloadRef`;
4. `ResourceHeadState` including explicit `Known` / `Unknown` knowledge;
5. `ResourceAuthorityScopeId`;
6. `DocumentAuthorityId` independent of path/physical locator;
7. Save / first Save / Save As / copy / reopen identity rules;
8. `ResourceDescriptor`, views, capabilities and `ResourceCoverage`;
9. provider registry + narrow `ResourceGateway`;
10. existing CAS exposed consistently through `PayloadRef`;
11. `ResourceLease` and continuation pinned to exact revision/snapshot;
12. `ResourceAuthorityStore`;
13. monotonic authority generation;
14. immutable `ResourceAuthoritySnapshot` / `ResourceAuthoritySnapshotSet`;
15. `ResourceAuthorityCommit` as the only publication unit for head/effect/generation changes.

### Required semantic result

After this wave, these cases must be representable without hacks:

```text
R1 hash=A
R2 hash=B
restore R1 -> R3 hash=A, Parent=R2, RestoredFrom=R1
```

and:

```text
unsaved workbook -> DocumentAuthorityId X
Save -> same X
Save As on same live document -> same X, new locator
copy/fork -> new authority identity according to the authority contract
```

### Remove while touching this contour

Remove or replace:

```text
Revision = ContentHash semantics for mutable live resources
per-chat current head ownership
duplicate ResourceKey / ResourceRef identity variants
generic currentness derived from path alone
```

Do not build compatibility adapters that preserve these semantics behind new names.

---

# WAVE 2 — Mutation Commit + Effects + Evidence Correctness

**Primary source:** Authority Phase B + Context Phase A + URF effect/impact sections.

Implement mutation truth before rebuilding prompt context.

For every mutable domain owner being cut over, use the canonical lifecycle:

```text
resolve authority
  -> validate expected revision / guard
  -> prepare bounded/reference-backed payload
  -> persist narrow MutationAttempt where required
  -> mark DispatchMayHaveOccurred before external side effect
  -> dispatch through domain owner
  -> verify/read-back according to provider guarantees
  -> produce exact after-state when known
  -> build one ResourceEffect
  -> publish one ResourceAuthorityCommit
  -> finalize mutation attempt
  -> persist semantic tool result / conversation fact
```

Support at minimum:

```text
VerifiedChanged
VerifiedNoChange
FailedNoEffect
UnknownAfterDispatch
Restored
```

Do not use `FailedNoEffect` after dispatch if the system cannot prove no side effect occurred.

Then implement:

1. immutable `ResourceEvidence` from exact reads;
2. one deterministic `EvidenceStateReducer`;
3. VBA impact matcher;
4. Excel range/coverage impact matcher;
5. current / superseded / unknown projection;
6. same-run invalidation after mutation;
7. external drift -> authority effect/commit or `Unknown` head;
8. startup reconciliation of unresolved `DispatchMayHaveOccurred` attempts;
9. cross-chat invalidation through shared resource/document authority;
10. lightweight `ResourceAuthorityChanged` notification to consumers.

### Required semantic result

```text
Chat A reads Sheet1 R10
Chat B writes overlapping range -> R11
next compile in Chat A cannot treat R10 evidence as current
```

and:

```text
read R10
verified no-op write
R10 evidence may remain valid according to coverage/domain guarantees
```

and:

```text
read R10
dispatch occurred
verification inconclusive
head/evidence becomes Unknown rather than falsely Current
```

### Multi-write rule

Do not retain an artificial global rule such as "only one write operation per agent step". Multiple writes are allowed when each operation independently obeys authority guards, domain serialization requirements, verification, and commit semantics.

Do not create a universal mutation language; typed domain owners remain responsible for real execution and verification.

---

# WAVE 3 — One Frozen Model Context Path

**Primary source:** Context Phase B + Authority model-capture/publication sections.

Only after resource/authority/evidence truth exists, replace the model request path.

Implement:

1. frozen `ModelAuthoritySnapshot` containing the required authority generations/high-water marks;
2. capture of coherent resource authority snapshot(s);
3. frozen active ToolPack generation;
4. frozen SkillCatalog generation;
5. frozen SchemaRegistry generation when schemas are active;
6. event high-water mark;
7. `ContextAtom` intermediate representation;
8. `ToolInteractionFrame` preserving call/result causality;
9. one `ModelContextCompiler`;
10. strict compiler order:

```text
freeze authority/state
-> replay/reduce durable facts
-> build raw ContextAtoms
-> correctness filter
-> collapse terminal mutation frames
-> deduplicate repeated observations
-> relevance selection
-> hydrate selected payloads
-> structured compaction
-> token budgeting
-> serialize request
-> freeze ModelContextSnapshot + ContextReceipt
```

11. route every normal LLM request through this compiler;
12. make `PromptBudgetComposer` only a subordinate budgeting policy;
13. make `ContextCompactionService` only a structured compaction helper;
14. reduce `ModelToolResultProjection` to semantic/wire projection, not freshness authority;
15. strip `ConversationModelSession` of durable mutable `_messages` ownership as model truth;
16. keep conversation/event history durable and append-only, but compile model context afresh from frozen authority + reduced facts.

### DocumentContext cutover

Split mutable Office content from durable instruction context.

```text
user-authored durable notes/preferences
    -> instruction/context atoms

selected cells / selected text / active-slide excerpts / mutable document content
    -> exact resource observation/evidence under the same freshness rules
```

Do not allow mutable `DocumentContext` text to bypass resource authority and be injected directly as permanently current prompt state.

### Required semantic result

A model request must never contain a mixed snapshot such as:

```text
Resource A from generation 100
Resource B from generation 101
Skill catalog from generation 38
ToolPack from generation 37
```

One compile uses one coherent frozen authority tuple. Changes published during compile become visible only to a later compile.

---

# WAVE 4 — Reference-First Payloads + HTML/Viewers/Data Plane

**Primary source:** URF Phase B + Context Phase C + Authority consumer consistency sections.

After model correctness is centralized, move bulk consumers to the same fabric.

Implement:

1. large read bodies -> existing CAS `PayloadRef`;
2. large mutation arguments -> `PayloadRef` where appropriate;
3. selective model hydration only after correctness/relevance filtering;
4. remove special rules forcing exact large reads to stay inline in model history;
5. HTML workspace stores resource bindings, not copied authoritative JSON bodies;
6. `RN.resources` reads through the ResourceGateway;
7. bounded read/stream transport for tables and bulk data;
8. PDF/image/artifact bulk delivery through the same resource data-plane;
9. metadata-only `resourceChanged` notifications driven by authority commits;
10. viewer caches keyed/pinned by canonical exact revision and invalidated by shared authority;
11. pull-based UI hydration: list metadata first, body only when opened/read;
12. cancellation, lease expiry, request correlation and backpressure for `RN.resources.stream`;
13. no unbounded producer queue when WebView/consumer is slow.

### Remove definitively in touched contours

```text
HtmlWorkspaceDataSource.Json as authoritative bulk state
window.RNAssistantData
accepted.DataJson bulk binding
HTML-specific last-good freshness truth
duplicate HtmlDataRef / HtmlDataStore resource systems
base64 bulk PDF/image transport through ordinary control messages
hidden large resource-text caches bypassing ResourceEvidence/PayloadRef
```

### Required semantic result

A large Excel table can simultaneously serve:

```text
Agent bounded read
HTML dashboard stream
Artifact/table viewer
future Python adapter
```

through the same logical resource identity/current authority without duplicating the data fabric.

---

# WAVE 5 — Semantic Schemas, Derived Resources, Retention + Final Cleanup

**Primary source:** URF Phase C/D + Authority Phase C/D + Context Phase D.

Implement the semantic/derived layer only after base resource and freshness semantics are stable.

Implement:

1. versioned `SemanticSchema` registry;
2. explicit lifecycle such as Draft -> Validated -> Published -> Deprecated;
3. only Published schemas enter active `SchemaRegistrySnapshot`;
4. versioned `ResourceSchemaMapping`;
5. mapping/derived provenance to exact source + schema + mapping revisions;
6. virtual vs materialized derived resource semantics;
7. derived-currentness computed from exact dependencies, without deleting historical derived revisions;
8. publication barrier for SchemaRegistry;
9. immutable-generation publication for SkillCatalog;
10. preserve existing ToolPack pinned/admission semantics and align generation capture;
11. no partial visibility of new catalog generation;
12. structured compacted claims with provenance when source atoms are removed from current prompt residency;
13. rollback as new resource revision/effect, never history rewrite;
14. CAS/resource retention roots for historical revisions, evidence, artifacts, derived provenance and unresolved mutations;
15. typed `RESOURCE_SNAPSHOT_UNAVAILABLE`-style behavior when an intentionally expired payload cannot be recovered;
16. final documentation alignment and repository search for duplicate concepts.

### Final cleanup target

After this wave, remove reachable legacy paths equivalent to:

```text
parallel stale booleans outside EvidenceStateReducer
multiple independent conversation->messages builders
manual mutable resource text injected directly into prompts
per-chat resource head/currentness truth
old hash-as-revision assumptions
HTML-specific freshness/cache authority
legacy context compaction performed before correctness filtering
provider/live COM reads from ModelContextCompiler
duplicate resource identity/store abstractions
old tool/skill/schema bodies treated as active merely because stored
```

Do **not** delete:

```text
append-only durable conversation history
historical resource revisions
existing CAS/blob store
HostRuntime/document serialization gate
ToolPack/SkillCatalog authority concepts
artifact history/projection concepts that already match the new contracts
```

---

# 5. Publication and locking rules

Keep concurrency simple.

```text
domain owner / HostRuntime
    owns external Office serialization requirements

ResourceAuthorityStore
    owns short compare-and-publish authority commit critical section

ModelContextCompiler
    only reads frozen snapshots; it does not hold Office/authority locks during compile
```

Do not hold a global resource lock while calling Excel/VBA/Word/PowerPoint/Outlook APIs.

Use optimistic expected-head validation and compare-and-publish at authority commit time where required by the detailed authority contract.

Do not add a general distributed transaction framework.

---

# 6. Cross-system ownership after cutover

Use this target map to prevent duplicated responsibility:

| Concern | Final owner |
|---|---|
| Physical Excel/VBA mutation/read semantics | typed domain owner/provider |
| Logical resource identity / exact revision / coverage / view | URF |
| Current head and generation | `ResourceAuthorityStore` |
| Mutation uncertainty / publication | Authority commit protocol |
| Cross-chat currentness | shared Authority + reducer |
| Historical observation | `ResourceEvidence` |
| Current/superseded/unknown evidence state | `EvidenceStateReducer` |
| Model request content | `ModelContextCompiler` |
| Token budgeting | compiler subordinate budget policy |
| Compaction | compiler subordinate structured compaction policy |
| Large immutable body | existing CAS via `PayloadRef` |
| HTML bulk access | `RN.resources` -> `ResourceGateway` |
| Viewer bulk access | `ResourceGateway` |
| Resource freshness notification | authority generation/head change |
| Tool activation | ToolPack snapshot/publication |
| Skill activation | SkillCatalog snapshot/publication |
| Semantic schema activation | SchemaRegistry snapshot/publication |
| Durable chat truth | existing append-only event store |
| Office threading | existing `HostRuntime` / document gate |

If two touched classes both claim one row of this table, consolidate them instead of keeping both.

---

# 7. Performance rules to preserve during implementation

This cutover must reduce, not increase, runtime/UI overhead.

Keep these invariants:

```text
no global polling of all resources
no full resource body in list/tree metadata
no repeated bulk JSON copies between C# and WebView
no full tool/read body kept inline merely for history
no context rebuild that performs live provider I/O
no eager hydration before correctness/relevance filtering
no heavy event telemetry for every UI click/runtime callback
no unbounded stream queue
```

Prefer:

```text
metadata/reference first
bounded read
exact revision pinning
pull-based hydration
notification coalescing
short authority commits
CAS deduplication
incremental reducer/checkpoint state
```

---

# 8. Minimal verification policy

Do not create a separate long testing/benchmark phase unless required to fix a discovered failure. Build affected projects and use focused checks at the end of each logical wave.

The integrated critical scenarios are:

```text
exact historical revision remains distinct after head changes
restore creates a new revision even when bytes equal an older revision
Save As does not accidentally destroy logical document identity
continuation cannot silently cross revisions
guarded write cannot publish over an unexpected head
verified mutation supersedes intersecting old evidence
verified no-op does not falsely supersede valid evidence
UnknownAfterDispatch produces Unknown, never false Current
cross-chat mutation invalidates affected evidence in another chat
one frozen authority tuple is used per model compile
all normal model requests use one ModelContextCompiler
large payloads remain reference-first and bounded
HTML/viewer reads use ResourceGateway rather than copied current JSON
slow stream consumer cannot create unbounded buffering
schema/mapping change invalidates current derived semantic result by dependency
rollback creates new lineage rather than rewriting history
```

Real-machine UI responsiveness/performance may be validated by the user after implementation. Do not add broad benchmark infrastructure merely for this cutover.

---

# 9. Implementation discipline

Use one coding agent / one coordinated workstream for Waves 1-3. These waves change shared contracts and must not be independently invented in parallel branches.

After Waves 1-3 establish canonical contracts, independent work may be parallelized only by consumer contour, for example:

```text
workstream A: HTML / RN.resources / WebView data plane / viewers
workstream B: schemas / mappings / derived resources / catalog publication
```

Both must consume already-established URF/Authority/Evidence/Compiler contracts unchanged.

Do not create new architecture names merely because an existing name is inconvenient. Prefer canonical names from the three specifications or refactor existing names toward them.

When legacy code conflicts with the target contract, remove or replace it in the touched contour rather than introducing `Legacy`, `V2`, `New`, `Compat`, `AdapterForOld`, or dual-write paths.

---

# 10. Required final repository state

The architecture should reduce to this ownership chain:

```text
Domain owners / Providers
        |
        v
Universal Resource Fabric
        |
        v
ResourceAuthorityStore
        |
        +------------------------------+
        |                              |
        v                              v
ResourceEvidence / Reducer       HTML / viewers / future Python
        |
        v
ModelAuthoritySnapshot
        |
        v
ModelContextCompiler
        |
        v
ModelContextSnapshot
        |
        v
LLM
```

Durability remains orthogonal:

```text
append-only EventStore
existing CAS / PayloadRef
historical ResourceRevisions
ResourceEffects / authority commits
structured checkpoints / ContextReceipts
```

The system must not require conversation history to determine current resource truth, and must not require current resource bodies to be permanently retained in model history.

---

# 11. Definition of Done for the integrated cutover

The work is complete only when all three canonical specifications are implemented as one architecture and the following are true:

- [ ] one canonical resource identity/revision model is used;
- [ ] `RevisionId`, `ContentHash` and `PayloadRef` are separate concepts;
- [ ] logical document authority survives locator changes according to the authority contract;
- [ ] one shared `ResourceAuthorityStore` owns current head truth;
- [ ] head/effect/generation changes publish atomically through `ResourceAuthorityCommit`;
- [ ] mutation uncertainty survives crashes as explicit state;
- [ ] cross-chat and cross-consumer freshness derives from shared authority;
- [ ] one deterministic `EvidenceStateReducer` determines evidence currentness;
- [ ] one frozen authority tuple feeds each model compile;
- [ ] one `ModelContextCompiler` assembles every normal model request;
- [ ] correctness filtering precedes relevance, hydration, compaction and budgeting;
- [ ] mutable `DocumentContext` content cannot bypass evidence/currentness rules;
- [ ] large read/write bodies are reference-first via existing CAS/PayloadRef;
- [ ] HTML/viewers use the same ResourceGateway and authority truth;
- [ ] `RN.resources.stream` has cancellation/backpressure/lease semantics;
- [ ] ToolPack, SkillCatalog and SchemaRegistry use immutable published generations;
- [ ] derived resources carry exact dependency provenance;
- [ ] rollback/restore creates new lineage rather than rewriting history;
- [ ] old parallel stale/context/resource/bulk-binding paths are no longer reachable;
- [ ] no second CAS, resource registry, stale manager, generic mutation engine or context assembler was introduced;
- [ ] affected projects build and focused critical checks pass.

---

# 12. Final report required from the coding agent

At completion, return one concise integrated report:

```text
Implemented
- canonical URF core
- authority store/generation/commit
- mutation recovery/drift
- evidence reducer
- model authority snapshot/compiler
- payload externalization
- HTML/viewer resource data plane
- schema/mapping/derived integration

Removed
- legacy resource/currentness paths
- old prompt/request assembly paths
- obsolete HTML bulk-binding paths
- duplicate stale/freshness logic

Key ownership changes
- list old class/responsibility -> new canonical owner

Important compatibility removals
- list intentionally deleted legacy contracts/fields/paths

Focused checks
- list only critical scenarios actually checked

Remaining issues
- only genuine blockers or follow-up work outside these three specifications
```

Do not report a phase as completed if an old reachable path still bypasses the new canonical owner.

---

# 13. One-sentence execution rule

```text
Build resource identity first, make current-state publication authoritative second, make evidence/model context consume that truth third, move every bulk consumer onto the same fabric fourth, then add semantic/derived capabilities and delete all superseded paths.
```
