# RNAssistant — Resource Authority, Consistency & Mutation Commit: FINAL direct-cutover implementation contract

**Status:** canonical companion implementation specification.

**Prerequisite 1:** `RNAssistant_UNIVERSAL_RESOURCE_FABRIC_FINAL_DIRECT_CUTOVER.md` is authoritative for resource identity, exact revisions, heads, views, coverage, providers, effects, dependencies, payloads and leases.

**Prerequisite 2:** `RNAssistant_RESOURCE_LINEAGE_EVIDENCE_CONTEXT_COMPILER_FINAL_DIRECT_CUTOVER.md` is authoritative for evidence lifecycle, authority freezing for model requests and compiled model context.

**This document owns:** authority scope, atomic head/effect publication, generation semantics, cross-chat/cross-consumer freshness, mutation commit/recovery, external-drift reconciliation, logical document identity and publication barriers for active catalogs.

**Strategy:** direct cutover only. Do not preserve hash-as-revision, chat-local head truth, duplicate freshness managers, or compatibility mutation paths in touched contours.

---

# 0. Coding-agent instruction

Implement the missing consistency layer **inside/alongside the canonical URF authority implementation**, not as a second resource system.

The final architecture must have one answer to each question:

```text
What is the current state of this logical resource?
    -> ResourceAuthorityStore / frozen ResourceAuthoritySnapshot

What exact logical state was observed or restored?
    -> canonical URF ResourceRevisionRef

What mutation/drift fact changed or confirmed that state?
    -> ResourceEffect

When did the new state become visible to all consumers?
    -> one atomic ResourceAuthorityCommit / generation advance

What did this particular model request freeze?
    -> ModelAuthoritySnapshot consuming authority generation stamps
```

Do not build:

```text
ConversationResourceAuthority
HtmlFreshnessStore
PythonFreshnessStore
ResourceGenerationManager separate from URF
second resource-head registry
second CAS/blob store
global background polling of all Office resources
new distributed transaction framework
new graph database
```

Reuse existing repository primitives where they already satisfy the semantics:

```text
ResourceGateway / ResourceStore / CAS
append-only durable writer/checkpoint utilities
HostRuntime / document serialization gate
VBA mutation journal or equivalent prepared-write machinery
ToolPack admission/publication machinery
existing document identity/registry services
```

Refactor or replace them only where the old semantics conflict with this contract.

---

# 1. Why this document exists

The two prerequisite specifications deliberately separate:

```text
URF
    resource identity / revisions / heads / effects / providers

Model Context layer
    evidence reduction / context compilation
```

A consistency contract is required between them.

Without it, an implementation can still accidentally do this:

```text
compiler starts
  reads head A from time T1
mutation occurs
  reads head B from time T2
skill catalog changes
  reads generation from time T3
compiler emits one request containing mixed authority
```

or:

```text
Office mutation succeeds
process crashes before ResourceEffect/head update is persisted
restart
old head is still presented as current
```

or:

```text
Chat A reads R10
Chat B mutates same workbook -> R11
Chat A continues to treat R10 as current because freshness was chat-local
```

This document closes those gaps.

---

# 2. Canonical ownership boundaries

The ownership model after cutover is:

```text
PHYSICAL / DOMAIN STATE
Excel / VBA / Word / PowerPoint / files / packages / artifacts
        |
        v
DOMAIN OWNER / RESOURCE PROVIDER
read / mutate / verify / detect drift
        |
        v
UNIVERSAL RESOURCE FABRIC
identity / revisions / payloads / coverage / effects
        |
        v
RESOURCE AUTHORITY
shared current heads / known-unknown state / generation / publication
        |
        +---------------------+---------------------+
        |                     |                     |
        v                     v                     v
Evidence reducer         HTML/viewers          future Python
        |
        v
ModelContextCompiler
```

Strict responsibility rules:

```text
Provider/domain owner
    may perform live I/O and verification

ResourceAuthorityStore
    may persist/publish resource authority metadata
    must NOT perform Office COM/provider I/O

EvidenceStateReducer
    consumes frozen authority facts
    must NOT mutate authority

ModelContextCompiler
    consumes frozen authority snapshots
    must NOT read live providers

HTML/viewer/Python consumer
    may request resource reads via Gateway
    must NOT maintain independent current-head truth
```

---

# 3. Authority is resource/document scoped, never conversation scoped

A conversation does not own a resource head.

Example:

```text
Chat A
    read Sheet1 -> R10

Chat B
    write Sheet1 -> R11

Chat A next request
    authority says Sheet1 -> R11
    evidence on R10 is historical/superseded according to reducer
```

Therefore:

```text
conversation truth = per conversation/event stream
resource truth     = shared by authority scope
```

A chat may persist a reference to an `EffectId`, `EvidenceId` or exact revision. It must not persist a private “current revision” that overrides shared authority.

---

# 4. `ResourceAuthorityScopeId`

Introduce/reuse one logical authority scope abstraction.

Conceptually:

```csharp
ResourceAuthorityScopeId
{
    Kind;   // document, package-root, workspace/resource-root, immutable/global where needed
    Id;     // stable opaque logical id
}
```

For Office resources the normal scope is the logical document:

```text
DocumentAuthorityId
    -> workbook/document/presentation/message/container logical lifetime
```

Do **not** use:

```text
chat id
window handle
COM pointer
filesystem path alone
content hash
WebView workspace id
```

as the authority identity.

A scope exists to provide a coherent mutable-head publication boundary. It is not a security token and not a model-visible identifier by default.

---

# 5. `DocumentAuthorityId` and physical locator must be separate

This is mandatory for Office Save/Save As/reopen behavior.

Separate:

```text
DocumentAuthorityId
    logical resource lineage root

DocumentLocator
    current physical/runtime location
    path / host document handle / provider locator metadata
```

Canonical rules:

## 5.1 Unsaved document

On first RNAssistant attachment/capture:

```text
unsaved Workbook1
    -> create DocumentAuthorityId D17
    -> bind D17 to current live host document session
```

Do not derive `D17` from the temporary display name `Book1`.

## 5.2 First Save

```text
unsaved D17
Save as C:\Work\A.xlsx
    -> D17 remains D17
    -> locator changes
```

Saving does not create a new logical resource universe.

## 5.3 Save As on the same open document

```text
D17 @ C:\Work\A.xlsx
Save As C:\Work\B.xlsx
    -> active open document remains D17
    -> locator moves to B.xlsx
```

The old path is no longer allowed to alias D17 merely because it once did.

## 5.4 Save Copy As / copied file

A copied physical file is not automatically the same authority lineage.

If the copy is later opened as an independent document:

```text
new DocumentAuthorityId
```

unless the application has an explicit, deliberate lineage-fork/import operation.

Do not infer same identity from equal content hash.

## 5.5 Close and reopen

Use the existing persistent document registry/identity mapping where available to recover the logical identity for the same managed document.

Do not create heuristic identity by “same bytes means same document”.

If the system cannot prove continuity after an external move/copy, prefer a new logical document identity over an unsafe collision.

## 5.6 Locator changes are not content revisions

Changing a path/host locator does not by itself create a new content revision.

It may update authority metadata/provider routing, but `RevisionId` must represent resource logical state, not filesystem path churn.

---

# 6. `ResourceHeadState` must represent knowledge, not just a revision string

Do not model current authority as:

```text
Dictionary<ResourceIdentity, RevisionId>
```

because after `UnknownAfterDispatch` or unresolved external drift, there may be no safe known current revision.

Use/reuse a semantic state equivalent to:

```csharp
ResourceHeadState
{
    ResourceIdentity Identity;
    HeadKnowledge Knowledge;    // Known | Unknown | Unavailable if needed
    ResourceRevisionRef? Revision;
    EffectId? Cause;
    long AuthorityGeneration;
}
```

Required semantics:

```text
Known
    current exact revision is known

Unknown
    old revision is no longer safe to claim as current,
    but replacement revision is not yet known

Unavailable
    optional state for a resource that cannot currently be resolved/accessed;
    do not conflate with historical snapshot retention failure
```

Critical invariant:

```text
Unknown != “keep old revision and attach a stale boolean”
```

When authority becomes unknown, the old exact revision remains a valid historical revision if retained, but it is no longer the current head.

---

# 7. `ResourceAuthorityStore` — one shared current-state authority

Introduce/reuse one narrow authority owner.

Conceptual API:

```csharp
interface IResourceAuthorityStore
{
    ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope);
    ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes);

    ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity);

    AuthorityCommitResult Publish(ResourceAuthorityCommit commit);

    // recovery/checkpoint APIs may be internal
}
```

Responsibilities:

```text
maintain/reconstruct current head projection
maintain monotonic generation per authority scope
persist atomic authority commits
capture immutable/frozen snapshots
expose known/unknown head state
publish lightweight in-process change notifications after durable commit
recover projection after restart
```

Must not:

```text
call Excel COM
read files to determine freshness
execute mutations
summarize model history
own conversation state
own HTML workspace state
```

If the existing canonical `ResourceStore` already owns persisted heads and can satisfy this contract atomically, extend/refactor it rather than adding a duplicate store.

---

# 8. Generation semantics

Use a monotonic generation **per authority scope**, not one global process-wide lock for every document.

Conceptually:

```text
Document D17
    generation 120

Document D18
    generation 42
```

A successful authority commit for D17 becomes:

```text
D17 generation 121
```

D18 remains 42.

Generation means:

> “the version of the shared authority projection for this scope after a particular atomic publication.”

It does **not** mean:

```text
resource revision id
content hash
conversation sequence
CAS generation
UI refresh counter
```

Each commit advances the scope generation exactly once, even when it updates multiple heads.

---

# 9. `ResourceAuthorityCommit` — the atomic publication unit

Use one atomic publication record for authority state.

Conceptual model:

```csharp
ResourceAuthorityCommit
{
    AuthorityCommitId CommitId;
    ResourceAuthorityScopeId ScopeId;
    long PreviousGeneration;
    long NewGeneration;

    ResourceEffect? Effect;
    IReadOnlyList<ResourceHeadChange> HeadChanges;

    AuthorityCommitReason Reason;
    MutationAttemptId? MutationAttemptId;
    DateTimeOffset RecordedAt;
}
```

Possible reasons may include:

```text
InitialObservation
MutationEffect
Restore
ExternalDrift
Reconciliation
DerivedPublication
CatalogResourcePublication
MetadataTransition when authority-visible and actually required
```

Do not proliferate reasons if existing resource-store semantics already encode them.

The critical property is atomicity:

```text
Effect + all head changes + generation advance
    become visible together
```

Forbidden partial publication:

```text
persist effect
crash
head remains old
```

or:

```text
head becomes new
crash
no effect/provenance exists
```

---

# 10. `ResourceHeadChange`

A commit may affect zero, one or many heads.

Conceptual model:

```csharp
ResourceHeadChange
{
    ResourceIdentity Identity;
    ResourceHeadState Before;
    ResourceHeadState After;
}
```

Examples:

```text
VerifiedChanged
    Known R10 -> Known R11

VerifiedNoChange
    no head change required

UnknownAfterDispatch
    Known R10 -> Unknown(cause=E9)

ExternalDriftObserved with captured state
    Known R10 -> Known R12

ExternalDriftObserved without captured replacement
    Known R10 -> Unknown(cause=E11)

Restore
    Known R12 -> Known R13
    R13.RestoredFrom = R4
```

A no-op effect may still produce an authority commit/generation advance because it is a durable authoritative fact, even when `HeadChanges` is empty.

---

# 11. Durable publication implementation

Do not require a new database.

Preferred implementation order:

1. If the existing canonical resource metadata store can atomically persist equivalent `effect + head changes + generation`, use it.
2. Otherwise add a **narrow append-only authority commit journal using existing durable JSONL/event writer primitives**, not a new general event-sourcing framework.
3. Reconstruct an in-memory head projection from commits and optional compact checkpoints.

For an append-only implementation:

```text
ResourceAuthorityCommit append
        = publication barrier

in-memory projection update
        = cache/update after durable append

restart
        = replay commits/checkpoint -> same projection
```

If the process crashes after durable append but before in-memory notification, replay must recover the new authority correctly.

Do not persist large payload bodies in the authority journal. Store payloads in existing CAS and reference them through canonical URF revision metadata/`PayloadRef`.

---

# 12. `ResourceAuthoritySnapshot` — immutable frozen authority

Capture must return a frozen view, never a live service facade.

Conceptual structure:

```csharp
ResourceAuthoritySnapshot
{
    ResourceAuthorityScopeId ScopeId;
    long Generation;
    AuthorityCommitId? HighWaterCommitId;
    long EffectHighWaterMark; // if separate/meaningful in implementation
    IReadOnlyDictionary<ResourceIdentity, ResourceHeadState> Heads;
}
```

For large scopes, the implementation may use a persistent immutable map/shared snapshot structure rather than copying every head on every model request.

Requirements:

```text
snapshot lookup never performs provider I/O
snapshot does not change after capture
all head states belong to the declared generation
```

A snapshot is authority metadata, not a full resource-body snapshot.

---

# 13. `ResourceAuthoritySnapshotSet` for multi-resource/multi-document context

A model request or consumer may reference more than one mutable authority scope.

Do not solve this with a global generation.

Use a frozen set/tuple:

```text
AuthoritySnapshotSet
    D17 @ generation 121
    D18 @ generation 42
    package-root @ generation 9
```

The exact type may differ, but the `ModelAuthoritySnapshot` must retain these stamps.

Cross-scope atomic transactions are **not** implied.

If an operation mutates multiple independent documents, publish scoped commits independently and report partial/unknown outcomes explicitly if necessary.

---

# 14. Model compile capture order

The model-context prerequisite already allows an ordered frozen generation tuple rather than impossible global cross-subsystem atomicity.

Use a deterministic capture order.

Recommended:

```text
1. freeze conversation/event high-water mark
2. identify authority scopes referenced/active for this compile
3. capture ResourceAuthoritySnapshotSet
4. capture active ToolPackSnapshot
5. capture active SkillCatalogSnapshot
6. capture active SchemaRegistrySnapshot if relevant
7. build ModelAuthoritySnapshot from this fixed tuple
8. compile only against these frozen objects
```

Why event high-water first:

```text
own tool mutation
    authority commit must already be durable
    then corresponding conversation tool-result/effect reference is appended

next compile
    captures conversation through that result
    then captures authority at same-or-newer shared state
```

Do not capture authority first and then include a later local tool result that references a resource revision absent from the frozen authority snapshot.

Concurrent mutations from another chat between steps 1 and 3 may legitimately appear in authority without a local dialogue event. That is correct: shared resource truth is newer than Chat A's private conversation stream.

Mutations after step 3 belong to the next model request.

---

# 15. Tool execution ordering relative to conversation events

For a resource-changing tool call:

```text
accepted tool call
    -> prepare/dispatch/verify mutation
    -> publish authority commit
    -> persist final tool result + EffectId/revision semantic projection in conversation stream
    -> only then allow next model request
```

Do not append a successful changed tool result before the corresponding authority state is durably published.

If execution fails before dispatch:

```text
FailedNoEffect
```

may be persisted as the tool result without changing a resource head.

If dispatch may have occurred but final state cannot be established:

```text
UnknownAfterDispatch
```

must be published to authority so the old head cannot remain falsely current.

---

# 16. Expected-revision guards

Mutations against mutable resources should use canonical URF revision guards where semantics require optimistic concurrency.

Conceptually:

```text
ExpectedRevision = R10
Current authority = Known R10
    -> may prepare dispatch

ExpectedRevision = R10
Current authority = Known R11
    -> fail before dispatch: RESOURCE_REVISION_CHANGED

Current authority = Unknown
    -> do not blindly dispatch guarded mutation
    -> reconcile/re-read according to provider/domain semantics first
```

Do not use `ContentHash` as the logical revision guard.

A content hash may be used to verify bytes, but:

```text
RevisionId != ContentHash
```

remains mandatory.

---

# 17. Mutation commit protocol — canonical state machine

All mutable resource domains must map their operations to the same semantic protocol, while provider-specific execution remains domain-owned.

Canonical phases:

```text
RESOLVE/GUARD
    -> PREPARE
    -> MARK DISPATCH MAY OCCUR
    -> DISPATCH
    -> VERIFY
    -> MATERIALIZE AFTER REVISION if changed/known
    -> PUBLISH AUTHORITY COMMIT
    -> FINALIZE ATTEMPT
    -> EMIT/PERSIST TOOL RESULT
```

Do not collapse dispatch and publication into one optimistic call.

---

# 18. `MutationAttempt` — narrow crash-recovery fact, not resource truth

For external side effects where the process can crash between dispatch and publication, reuse the existing mutation journal machinery or normalize it to a narrow attempt record.

Conceptual model:

```csharp
MutationAttempt
{
    MutationAttemptId AttemptId;
    ResourceAuthorityScopeId ScopeId;
    Operation;
    Target;
    ExpectedRevision?;
    PayloadRef?;
    IntendedSemanticHash?; // only when useful for verification, never revision identity

    State;
    PreparedAt;
    LinkedAuthorityCommitId?;
}
```

Minimal states:

```text
Prepared
DispatchMayHaveOccurred
Resolved
AbandonedBeforeDispatch
```

Do not create dozens of generic transaction states.

`MutationAttempt` exists only to answer after a crash:

> “Could the external side effect have happened without an authority commit?”

It is not model-facing resource truth.

---

# 19. Why `DispatchMayHaveOccurred` must be marked before the external call

This ordering is mandatory for crash safety.

Unsafe:

```text
call Excel/VBA mutation
mutation succeeds
process crashes
mark never written
restart concludes “not dispatched”
```

Safe conservative ordering:

```text
persist Prepared
persist DispatchMayHaveOccurred
call provider/domain mutation
```

If a crash happens between the marker and the actual external call, recovery may temporarily treat the attempt conservatively as uncertain. That is acceptable and resolvable by read-back/probe.

False certainty is not acceptable.

---

# 20. Detailed mutation flow

Implement or normalize mutable operations to this sequence.

## 20.1 Resolve authority

```text
resolve ResourceIdentity
resolve authority scope
capture/get current head state
validate ownership/capability/security outside this authority contract as already required by URF
```

## 20.2 Validate guard

If mutation carries `ExpectedRevision`, compare against authority.

Fail before dispatch on mismatch.

If head is `Unknown`, do not pretend the expected revision is still current.

## 20.3 Prepare payload

Large mutation bodies must already follow URF reference-first rules:

```text
large VBA source / HTML / Excel matrix / JSON body
    -> existing CAS
    -> PayloadRef
```

Persist any recovery-required semantic verification metadata before dispatch.

## 20.4 Persist mutation attempt

```text
Prepared
```

## 20.5 Mark possible dispatch

```text
DispatchMayHaveOccurred
```

before invoking the external mutable domain.

## 20.6 Dispatch through domain owner

Examples:

```text
Excel -> approved Office STA path -> Range.Value2/write API
VBA   -> existing mutation owner/journal-safe VBE operation
Word  -> document range mutation owner
Plan/tool/skill/schema publication -> their own canonical publish owner
```

The authority store itself never calls COM/provider mutation APIs.

## 20.7 Verify

Provider/domain owner returns one of the semantic outcomes required by URF:

```text
VerifiedChanged
VerifiedNoChange
FailedNoEffect
UnknownAfterDispatch
Restored
```

External drift detection uses the separate drift semantics below.

## 20.8 Materialize exact after-state when known

For changed/restored state:

```text
create/capture new canonical ResourceRevisionRef
RevisionId is new
ContentHash may equal an older revision
parent/provenance restored-from metadata preserved
large body -> existing CAS PayloadRef
```

Do not “reuse” an old revision merely because bytes are equal.

## 20.9 Build one `ResourceEffect`

Include generic `ResourceImpact` records and provider/domain-specific coverage relation through the canonical URF contract.

## 20.10 Publish one `ResourceAuthorityCommit`

Atomically publish:

```text
Effect
HeadChanges
NewGeneration
```

## 20.11 Finalize attempt

Link attempt to the durable authority commit and mark `Resolved`.

This step may occur after publication because publication itself is already the authority barrier.

If the process crashes here, recovery finds the authority commit by `MutationAttemptId` and resolves idempotently.

## 20.12 Persist semantic tool result

Only after authority publication, persist the final conversation/tool result needed by the agent loop.

---

# 21. Outcome-specific rules

## 21.1 `VerifiedChanged`

```text
before Known R10
verification confirms changed state
new exact revision R11 created
publish:
    Effect VerifiedChanged
    R10 -> R11
    generation + 1
```

## 21.2 `VerifiedNoChange`

```text
before Known R10
verification confirms target still semantically/currently R10
publish:
    Effect VerifiedNoChange
    no head change
    generation + 1
```

Do not create R11 merely to represent a proven no-op unless the underlying domain has a real published logical revision event that must be represented.

## 21.3 `FailedNoEffect`

If failure is definitely before any external effect:

```text
old head remains safe
```

Persist the failure result. Recording an authority effect/commit is optional if existing ResourceEffect durability requires it, but do not change the head.

Do not classify an acknowledgement loss after dispatch as `FailedNoEffect`.

## 21.4 `UnknownAfterDispatch`

```text
before Known R10
external call may have executed
final state not established
publish:
    Effect UnknownAfterDispatch
    R10 -> Unknown(cause=EffectId)
    generation + 1
```

This is mandatory.

## 21.5 `Restored`

```text
head R12
restore historical R4
provider performs ordinary guarded mutation/publish
verification succeeds
new revision R13
R13.RestoredFrom = R4
R13.Parent = R12 where lineage model supports parent
publish head R13
```

Never set head directly back to R4.

---

# 22. Repeated equal bytes after uncertainty still require new lineage when continuity was lost

Important edge case:

```text
Known head R10 / hash A
unknown external drift occurs
head -> Unknown
later provider read returns hash A again
```

Do **not** automatically revive `R10` as current simply because the bytes equal hash A.

The logical state may have changed away and back while authority was uncertain.

Safe semantics:

```text
capture new revision R11
R11.ContentHash = hash A
R11.RevisionId != R10.RevisionId
publish R11 as reconciled current head
```

Only preserve the same revision when the provider/domain can actually prove continuity rather than merely equal content.

---

# 23. Startup crash recovery

On runtime startup/document attach, recover authority before allowing mutation-dependent model execution.

Order:

```text
1. load/replay authority commits/checkpoint
2. rebuild current head projection/generations
3. load unresolved MutationAttempts
4. for each attempt:
       if linked authority commit already exists -> mark Resolved idempotently
       else reconcile according to attempt state
5. only after recovery expose mutable authority as ready
```

Do not replay old mutation commands automatically.

Recovery is reconciliation, not blind retry.

---

# 24. Recovery rules by attempt state

## `Prepared`

If `DispatchMayHaveOccurred` was never durably recorded:

```text
dispatch was not permitted to begin under the canonical protocol
-> mark AbandonedBeforeDispatch
-> no authority head change
```

## `DispatchMayHaveOccurred` with no authority commit

Provider/domain reconciliation must determine current state using bounded read-back/probe where possible.

Possible results:

```text
intended changed state proven
    -> materialize after revision
    -> publish VerifiedChanged/reconciliation effect

proven no change / old state still current
    -> publish VerifiedNoChange or safe equivalent

conflicting different state observed
    -> publish ExternalDriftObserved with captured new revision

cannot establish state
    -> publish UnknownAfterDispatch
    -> head Unknown
```

Do not silently assume success because the payload exists in CAS.

Do not silently assume failure because the original process died.

---

# 25. Idempotent recovery linking

Every authority commit produced from a mutation attempt should carry/link `MutationAttemptId` where practical.

Recovery must support:

```text
commit exists, attempt not finalized
    -> do not publish second effect/head revision
    -> mark same attempt resolved
```

This avoids duplicate logical revisions after a crash immediately after publication.

---

# 26. External drift contract

URF already defines drift detection sources. This document defines how drift affects shared authority.

Detected drift must be converted to a shared authority commit, not a chat-local stale flag.

## 26.1 Drift with captured replacement state

```text
known head R20
provider/event/probe proves current source differs
capture exact new revision R21
publish:
    ExternalDriftObserved
    R20 -> R21
    generation + 1
```

## 26.2 Drift detected but replacement revision unknown

```text
known head R20
change event/guard proves R20 is no longer safe
new bytes/state not captured
publish:
    ExternalDriftObserved
    R20 -> Unknown
    generation + 1
```

## 26.3 No global polling

Do not add a timer scanning all workbooks/modules/files.

Use existing/natural detection points:

```text
Office event already emitted
provider read/probe
mutation optimistic guard
explicit user refresh
file/package watcher already justified by that domain
```

---

# 27. Reconciliation of `Unknown` head

When a subsequent bounded provider observation can establish current state:

```text
Unknown
    -> capture exact revision Rnew
    -> publish reconciliation authority commit
    -> Known Rnew
```

The provider may reuse an existing exact revision **only if it can prove identity/continuity**, not merely matching bytes.

The old evidence reducer then has enough shared facts to transition prior observations appropriately.

---

# 28. Reads and authority publication

A normal resource read creates `ResourceEvidence` in the conversation layer, but it does not necessarily advance shared authority.

Cases:

## Known current head, no drift

```text
read R10
head already Known R10
-> append evidence only
-> no authority generation change required
```

## Resource first observed / no current head exists

If the provider establishes a canonical current revision:

```text
publish InitialObservation head commit
-> Known R1
-> generation + 1
```

## Read discovers changed live state

```text
known R10
read/probe establishes R11
-> publish ExternalDriftObserved/reconciliation commit
-> evidence references R11
```

Do not let a read return “current R11” to one chat while shared authority remains at R10.

---

# 29. Cross-chat correctness

All conversations attached to the same authority scope consume the same shared projection.

Example:

```text
Generation 40
Sheet1 -> R7

Chat A evidence: R7
Chat B evidence: R7

Chat B writes -> R8
Authority commit -> generation 41

Chat A next compile captures generation 41
EvidenceStateReducer:
    R7 evidence -> Superseded/intersection-dependent state

Chat B next compile captures generation 41
same shared truth
```

No direct Chat B -> Chat A message is required for correctness.

The authority store is the synchronization point.

---

# 30. Cross-consumer change notification

After a durable authority commit, publish one lightweight in-process notification for responsive UI/cache behavior.

Conceptual event:

```csharp
ResourceAuthorityChanged
{
    ResourceAuthorityScopeId ScopeId;
    long Generation;
    AuthorityCommitId CommitId;
    IReadOnlyList<ResourceIdentity> AffectedResources;
}
```

Optional semantic per-resource detail may include new known revision where safe.

Consumers:

```text
HTML workspace bindings
artifact/resource viewers
derived-resource cache
Evidence/context checkpoint cache
future Python resource adapter
```

Critical rule:

```text
notification != authority
```

Notifications may be coalesced/dropped during UI churn. Consumers can always compare/capture current generation from `ResourceAuthorityStore`.

Do not make correctness depend on every subscriber receiving every event.

---

# 31. HTML binding semantics under shared authority

URF already defines head vs exact binding.

Apply authority rules:

```text
head-bound HTML resource
    sees generation change
    marks view cache dirty
    next bounded read resolves new current head

exact-bound HTML resource @ R10
    remains R10 historical snapshot
    head advancing to R11 does not mutate its exact snapshot
```

Do not copy a new full dataset into workspace state merely because the head changed.

Use metadata notification plus pull-based bounded refresh.

---

# 32. Viewer/cache semantics

Resource/artifact viewers must cache by exact revision/view/coverage, not only logical identity.

Safe cache key concept:

```text
ExactResourceRevision + View + Coverage + transformation/schema revision if relevant
```

For a head-bound viewer:

```text
authority generation/head change
    -> resolve new head
    -> old exact cache remains historical/reusable if retained
```

Do not delete historical cache merely because current head advanced.

---

# 33. ResourceLease and head changes

A lease must already be bound to exact revision/snapshot semantics under URF.

Authority change rules:

```text
head R10 -> R11

lease explicitly bound to exact R10 snapshot
    -> may remain valid until its own expiry/retention

live/head capability without stable snapshot
    -> next operation resolves according to current authority and provider semantics
```

Do not mutate an exact lease to point at R11.

Do not use lease expiry as resource-history invalidation.

---

# 34. Continuation consistency and authority generation

A continuation is bound to the exact revision/snapshot/lease captured when it started.

Store enough metadata to reject silent mixing:

```text
continuation C5
    scope D17
    exact revision R10
    lease/snapshot L4
    authority generation at open = 40 (diagnostic/guard metadata if useful)
```

If current head advances to R11, continuation C5 may continue only against its exact R10 snapshot/lease.

If that snapshot is unavailable:

```text
RESOURCE_SNAPSHOT_UNAVAILABLE
```

Never “helpfully” continue from current R11.

---

# 35. Derived resources and dependency authority

A derived revision retains exact dependencies as URF provenance.

Example:

```text
D9 depends on:
    Excel source R7
    Schema S3
    Mapping M2
```

Do not eagerly destroy D9 when a source head changes.

Instead:

```text
D9 remains valid historical derived revision
current-derived validity is computed from dependency authority
```

A small derived-state cache may subscribe to authority generations, but correctness must be reproducible from:

```text
frozen heads + exact dependency refs
```

No full dependency graph database is required for this cutover.

---

# 36. Schema Registry publication barrier

URF defines schema revisions and states conceptually. Activation/publication must use immutable generation snapshots.

Recommended active states:

```text
Draft
Validated
Published
Deprecated
```

Only published/active revisions enter `SchemaRegistrySnapshot` used as authority by agent/compiler workflows.

Publication flow:

```text
build/validate complete new schema revision
persist immutable body/metadata
build complete immutable next registry snapshot
atomically publish snapshot pointer + generation
notify subscribers after publication
```

Never mutate the currently published snapshot in place.

A model-generated draft does not become authoritative merely because it exists as a resource.

---

# 37. Skill catalog publication barrier

Skills require the same immutable publication semantics as ToolPack.

Target:

```text
SkillCatalogSnapshot
{
    Generation;
    immutable active entries/revisions;
}
```

Publication:

```text
persist new skill resource/revision
validate/admit according to existing skill rules
construct complete new SkillCatalogSnapshot
atomic swap/publish generation N+1
```

Forbidden:

```text
reader enumerates catalog while writer edits same mutable collection
new descriptor visible with old body/binding
historical skill body becomes active merely because resource exists
```

If current `SkillCatalogService` loads mutable store state on each request, refactor it to snapshot publication rather than keeping this behavior beside the new compiler.

---

# 38. ToolPack publication barrier

Preserve and reuse existing ToolPack admission/pinning machinery where it already provides immutable snapshot semantics.

Required invariant:

```text
ToolPackSnapshot generation G
    contains one internally coherent set of callable tool descriptors,
    schemas, handlers/bindings and package revisions
```

A tool update is visible to new model requests only after the complete new snapshot is admitted/published.

Do not publish:

```text
new schema + old handler
new package body + old descriptor
half-updated tool set
```

The URF may expose historical tool/package resources, but resource existence is not activation authority.

---

# 39. Cross-catalog atomicity

Do not build one global lock spanning ResourceAuthority, ToolPack, SkillCatalog and SchemaRegistry.

Each subsystem publishes an internally immutable snapshot with its own generation.

`ModelAuthoritySnapshot` captures a fixed tuple:

```text
ResourceAuthorityScope D17 generation 121
ResourceAuthorityScope D18 generation 42
ToolPack generation 18
SkillCatalog generation 7
SchemaRegistry generation 12
Conversation high-water 904
```

The compiler uses exactly that tuple for the whole request.

If a new publication occurs during compilation, it belongs to the next request.

---

# 40. Publication visibility rule

For every authority/catalog subsystem:

```text
persist complete new state first
atomic publication barrier second
subscriber notification third
```

Never:

```text
notify -> then persist
expose mutable object -> then finish populating it
increment generation -> then write missing pieces
```

A generation must never identify a partially constructed state.

---

# 41. Interaction with `EvidenceStateReducer`

The reducer receives frozen facts only.

It may compare:

```text
Evidence exact revision
    vs ResourceAuthoritySnapshot head state

Evidence coverage
    vs ResourceEffect/ResourceImpact

Derived dependency revision
    vs frozen source/schema/mapping authority
```

The reducer does not subscribe to live change events during one reduction.

Cross-chat invalidation therefore requires no chat-local stale mutation:

```text
next reducer run + newer authority snapshot = correct state
```

This is the intended architecture.

---

# 42. Interaction with `ModelContextCompiler`

The compiler must receive already frozen authority snapshots/generation stamps.

Forbidden:

```text
compiler asks ResourceGateway “what is current now?” per atom
compiler reads Excel/VBA to refresh stale content
compiler checks mutable SkillCatalogService repeatedly
compiler uses latest ToolPack halfway through serialization
```

Correct:

```text
freeze tuple
    -> reduce evidence against tuple
    -> build atoms
    -> correctness filter
    -> relevance
    -> selective hydration of exact selected payloads
    -> compaction/budget
    -> serialize
```

Hydration must use exact resource/payload references chosen from the frozen context. If exact payload is unavailable, emit the model-context error semantics already defined by the compiler specification; do not fall forward to a newer head.

---

# 43. Interaction with conversation checkpoints

Compiler/evidence checkpoints may cache reduced metadata up to:

```text
conversation event high-water
resource authority generation tuple
catalog generation tuple
```

A checkpoint is reusable only when its dependency stamps remain compatible.

Do not store a mutable pointer to “current resource state” inside the checkpoint.

Do not embed full resource bodies in checkpoint state.

---

# 44. Multi-write agent steps

Do **not** reintroduce a policy that only one write operation may occur per agent step merely to simplify authority.

Multiple resource mutations are allowed.

Each mutation must have clear commit semantics:

```text
write A -> authority commit GA+1
write B -> authority commit GB+1
write C -> authority commit GC+1
```

For a provider-supported atomic batch within one authority scope, one `ResourceAuthorityCommit` may contain multiple verified `HeadChanges`/impacts.

If a batch can partially apply, the result must not pretend atomicity. Represent per-target changed/unknown outcomes using the provider/domain mutation contract or split into independently committed operations.

---

# 45. Do not over-generalize mutation execution

The common layer owns:

```text
guard semantics
attempt/recovery boundary
ResourceEffect envelope
atomic authority publication
known/unknown head semantics
```

Domain owner still owns:

```text
how Excel range write occurs
how VBA mutation/journal/read-back works
how Word/PowerPoint writes are verified
how a tool/skill/package is admitted
how schema validation works
```

Do not create a universal mutation DSL or giant switch over every domain.

---

# 46. Provider verification contract

Normalize provider/domain mutation response sufficiently for authority publication.

Conceptual response:

```csharp
MutationVerification
{
    Outcome;
    BeforeRevision?;
    AfterRevision?;
    Impacts[];
    VerificationKind;
    DiagnosticCode?;
}
```

Verification kind may be domain-specific metadata such as:

```text
read-back exact
host acknowledgement + guard
published immutable package pointer
structural comparison
```

Do not expose low-level COM handles or sensitive runtime details to the model.

---

# 47. Coverage-aware effects remain domain-owned

Authority publication stores the canonical effect/impact metadata; it does not implement Excel/VBA intersection logic.

Example:

```text
Excel write B4:B20
    effect impact coverage = B4:B20

Evidence A1:F500
    Excel matcher evaluates intersection
```

For head state, provider/resource model decides which logical resource head actually advances.

Do not invent subsection revision guarantees the provider does not support.

If the provider only certifies a whole worksheet/resource revision, use conservative whole-resource head semantics while retaining `ResourceCoverage` for evidence relevance/intersection.

---

# 48. Initial observation and repeated reads

Do not create a new logical revision on every read merely to have a timestamp.

Rules:

```text
known head R10
provider proves same continuing state
    -> keep R10

unknown/no registered head
provider captures current state
    -> create/publish first or reconciled revision

known R10
provider detects actual change
    -> new revision R11
```

`ObservedAt` belongs to `ResourceEvidence`, not necessarily to a new resource revision.

---

# 49. Revision lineage after restore or equal-content writes

Preserve causal lineage even with content deduplication.

```text
R1 hash A
R2 hash B
R3 hash C
restore R1
R4 hash A
```

CAS may deduplicate payload bytes:

```text
R1.PayloadRef == R4.PayloadRef
```

but:

```text
R1.RevisionId != R4.RevisionId
```

The same applies when a verified mutation produces bytes identical to an older historical state after intervening revisions.

---

# 50. Authority and CAS retention

Authority metadata and payload retention are separate.

Never assume:

```text
head is historical -> delete payload immediately
```

CAS GC must respect reachability from retained:

```text
current and historical resource revisions
restore lineage/provenance
derived dependencies
artifacts
ResourceEvidence where historical replay requires body
structured compacted claims when exact payload dependency is retained
pending MutationAttempts required for recovery
```

Transient objects such as:

```text
leases
shared buffers
stream cursors
hydrated model payload copies
```

may use short retention independently.

If historical revision metadata remains but body was legitimately expired under retention policy, exact body access returns the canonical unavailable error; it must never silently read current head instead.

---

# 51. Pending mutation payload retention

A mutation attempt that has reached `DispatchMayHaveOccurred` must retain enough information for reconciliation until resolved.

For large bodies:

```text
MutationAttempt -> PayloadRef -> CAS
```

Do not GC that payload while the attempt is unresolved if verification depends on it.

After resolution, normal retention rules may apply.

---

# 52. Authority readiness and startup behavior

Expose a small readiness state per mutable authority scope if necessary:

```text
Recovering
Ready
Unavailable
```

Do not let the model perform guarded mutations against a scope whose unresolved mutation journal has not yet been reconciled.

Read-only historical exact resources may remain accessible when safe, but do not present an unrecovered mutable head as definitely current.

Avoid blocking the entire application globally because one unrelated document scope is recovering.

---

# 53. Office threading and `HostRuntime`

Keep existing Office STA/document serialization rules.

This authority contract does not replace `HostRuntime`/document gate.

Correct separation:

```text
HostRuntime/document gate
    prevents unsafe concurrent Office COM operations for the same document

ResourceAuthorityStore
    publishes the resulting logical state for all consumers/chats
```

Do not use the authority store lock as a COM threading mechanism.

Do not hold a global authority lock while performing slow Office COM/provider I/O.

---

# 54. Locking rule

Never hold the atomic publication lock across external mutation dispatch or provider reads.

Use optimistic guard + publish compare:

```text
capture expected authority generation/head
perform prepared guarded domain mutation under domain serialization
verify
publish commit only if authority preconditions still match the domain operation semantics
```

For Office, the existing per-document execution gate should normally prevent competing in-process writes while the external operation is underway.

If an external drift is detected before publication, reconcile and fail/mark unknown rather than overwriting newer authority.

---

# 55. Authority commit compare-and-publish

`Publish` must validate at least:

```text
ScopeId matches
PreviousGeneration == current generation
head-before expectations required by HeadChanges still match
commit id is not duplicated
MutationAttemptId has not already produced another commit
```

On conflict:

```text
do not force-write the projection
reconcile/recompute effect according to provider/domain state
```

This protects against race-induced lost updates even if the domain gate is bypassed by an external edit.

---

# 56. In-process notification coalescing for performance

Do not emit expensive UI work per low-level change event.

The durable authority commit remains fine-grained enough for correctness; notification delivery may coalesce by scope/generation.

Example:

```text
10 rapid cell changes
    -> authority commits as required by actual captured semantics
    -> UI receives “D17 advanced to generation 130” once/coalesced
    -> visible viewer pulls what it needs
```

Do not rebuild all artifact trees, HTML datasets or prompt state on every notification.

---

# 57. Pull-based UI invariant

Authority changes should invalidate metadata/cache, not push bulk resource bodies.

```text
authority changed
    -> metadata notification
    -> visible consumer requests bounded current data if needed
```

Forbidden:

```text
authority changed
    -> serialize entire workbook/table/PDF into WebView message
```

This preserves the URF performance goal and prevents authority from becoming a new UI load source.

---

# 58. Telemetry

Keep diagnostics compact.

Useful coarse records:

```text
AuthorityCommitId
scope generation before/after
Effect outcome
number of head changes
mutation attempt recovery result
publication conflicts/errors
```

Do not log:

```text
every cell value
full VBA source
full payload bodies
per-subscriber refresh chatter
high-frequency UI button events
```

`ContextReceipt` remains the model-context explainability record; do not duplicate it here.

---

# 59. Canonical error semantics

Reuse URF errors and add only narrow authority/commit errors where necessary.

Recommended semantic set:

```text
RESOURCE_AUTHORITY_NOT_READY
RESOURCE_AUTHORITY_CONFLICT
RESOURCE_REVISION_CHANGED
RESOURCE_HEAD_UNKNOWN
RESOURCE_MUTATION_RECOVERY_REQUIRED
RESOURCE_MUTATION_VERIFY_FAILED
RESOURCE_SNAPSHOT_UNAVAILABLE
```

Do not expose internal file paths, COM ids, journal filenames or capability tokens in model-visible errors.

Map errors to compact semantic tool results.

---

# 60. Current project contours to inspect first

Before coding, inspect the current implementation around these responsibilities; use actual repository names where they have changed.

```text
ResourceGateway / resource store / resource revision/head implementation
ResourceEffect / mutation verification paths
Excel read/write owner and Office STA dispatcher
VBA mutation journal / prepared-write / read-back path
HostRuntime / per-document gate
current document identity and Save/Save As handling
conversation/event persistence and shared CAS
ConversationModelSession / ModelContextCompiler integration point
EvidenceStateReducer if already introduced by the companion cutover
HTML workspace resource bindings / resourceChanged path
Artifact/resource viewers and caches
ToolPackSnapshot / admission journal / publication
SkillCatalogService / skill store
Schema registry/mapping publication if already present
```

Do not spend a separate exploration phase documenting every file. Locate the owners above, implement the contract, and remove conflicting touched legacy paths.

---

# 60.1 Current contour -> target ownership map

Use this map to prevent the cutover from creating another layer beside the current services. Exact class names may have moved; follow responsibilities, not filenames.

| Current contour/responsibility | Target after cutover |
|---|---|
| `ResourceGateway` | stays the narrow generic execution boundary; resolves through canonical authority/provider semantics but does not become a freshness manager |
| resource store/head metadata | becomes or backs the canonical `ResourceAuthorityStore`; no second head registry |
| `HostRuntime` / per-document gate | stays responsible for Office STA/document serialization; does not own resource truth |
| VBA mutation journal/prepared write | reuse/normalize as the domain implementation of `MutationAttempt` + recovery; do not add a parallel generic VBA journal |
| Excel write/read owner | produces verification/revisions/effects through URF and publishes them through authority |
| conversation event store | remains chat-local durable semantic history; stores refs to effects/evidence, not private current heads |
| shared CAS / `PayloadRef` | unchanged canonical large-body store; authority only references it |
| `ConversationModelSession` | must not cache/own current resource authority; consumes compiled `ModelContextSnapshot` |
| `ConversationPromptComposer` / runtime-context assembly | must not inject independently captured mutable resource truth that bypasses evidence/authority |
| `ModelToolResultProjection` | semantic projection only; no independent current/stale decision |
| `ToolResultResourceService` / large result handling | externalizes/reference-backs payloads; does not decide currentness |
| `EvidenceStateReducer` | sole model-evidence current/superseded/unknown projection from frozen authority facts |
| HTML workspace bindings | head/exact binding metadata only; no independent “last good current truth” |
| artifact/resource viewer cache | exact revision/view/coverage cache; current head always comes from authority |
| `ToolPackSnapshot`/admission | preserve immutable publication barrier |
| `SkillCatalogService` | refactor from live mutable enumeration/loading to immutable generation snapshot publication if still necessary |
| Schema registry | immutable published snapshot/generation; drafts remain resources but not active authority |
| mutable Office selection/document excerpts | represent as URF-backed invocation/resource evidence; do not bypass freshness through generic `DocumentContext` text |
| durable user notes/preferences | may remain ordinary non-resource context; do not force them into URF merely for uniformity |

The desired deletion pattern is:

```text
old service owned A + B + C
    -> keep A where it belongs
    -> move B to canonical authority
    -> move C to evidence/compiler
    -> delete old duplicate decision logic
```

Do not leave the old service making the same decision “for compatibility”.

---

# 60.2 Mutable `DocumentContext` must not bypass resource authority

This is a required alignment point with the Model Context Compiler cutover.

Separate captured context into:

```text
InstructionContext
    durable user-authored notes/preferences/static metadata

ResourceContext
    selected Excel cells
    selected Word text
    active slide contents
    VBA source excerpts
    other mutable document excerpts
```

`ResourceContext` must become either:

```text
ResourceEvidence referencing an exact URF revision/view/coverage
```

or an invocation-scoped exact resource observation that participates in the same frozen authority/evidence rules.

Forbidden after cutover:

```text
OfficeContextCaptureService reads mutable cells/text
    -> raw RUNTIME_CONTEXT string
    -> model

while ResourceEvidence says a different head is current
```

The model must never receive two competing freshness systems for the same mutable Office content.

---

# 60.3 Required `RN.resources.stream` consistency/backpressure amendment

The URF document owns the JS/data-plane API. During this cutover, ensure its stream implementation also satisfies these consistency requirements.

A stream/continuation must be pull-bounded and cancellable:

```text
open -> exact revision/snapshot lease
next -> one bounded batch
next -> one bounded batch
cancel/close -> release transient resources
```

Required controls:

```text
max in-flight bytes/batches
no unbounded producer queue
consumer-driven `next`/pull or equivalent backpressure
cancellation
workspace/session close cleanup
lease expiry handling
request/stream correlation
slow-consumer handling
```

A head change may notify the consumer, but an already opened exact stream must not switch revisions.

Do not solve this with a second HTML data cache or by preloading the entire dataset into WebView memory.

---

# 61. Direct cutover phase A — Authority core + logical document identity

Implement first:

1. normalize `DocumentAuthorityId` / `ResourceAuthorityScopeId` separate from locator/path;
2. normalize `ResourceHeadState` with `Known`/`Unknown` semantics;
3. implement/reuse `ResourceAuthorityStore` as the single shared current-head authority;
4. implement monotonic per-scope generation;
5. implement immutable `ResourceAuthoritySnapshot`/snapshot set;
6. implement atomic `ResourceAuthorityCommit` with effect/head changes/generation;
7. ensure `RevisionId != ContentHash` everywhere touched;
8. remove chat-scoped/private head authority in touched paths;
9. wire Save/Save As/reopen identity behavior to logical document authority.

**Acceptance gate A:**

```text
one document has one shared authority across chats;
Save As does not reset logical lineage;
head can become Unknown without pretending old revision is current;
one commit publishes all head changes/effect/generation atomically;
frozen snapshot cannot change after capture.
```

---

# 62. Direct cutover phase B — Mutation commit + crash recovery + drift

Implement:

1. normalize/reuse narrow `MutationAttempt` journal semantics;
2. mark `DispatchMayHaveOccurred` before external dispatch;
3. route Excel and VBA mutable operations through prepare -> dispatch -> verify -> authority publish;
4. publish `UnknownAfterDispatch` as head `Unknown`;
5. link mutation attempts to authority commits idempotently;
6. add startup reconciliation for unresolved attempts;
7. convert detected external drift to shared authority commits;
8. ensure read-discovered new current state updates authority before evidence/result claims it is current;
9. implement compare-and-publish generation/head guards;
10. retain pending payload refs until reconciliation completes.

**Acceptance gate B:**

```text
crash before dispatch cannot become a false success;
crash after possible dispatch cannot leave old head falsely current;
restart never blindly replays a write;
verified changed/no-op/unknown outcomes publish correct authority;
external drift in one consumer becomes shared truth.
```

---

# 63. Direct cutover phase C — Cross-consumer + model authority + catalog barriers

Implement:

1. lightweight `ResourceAuthorityChanged` notification after durable publication;
2. HTML/viewer cache invalidation by generation/head metadata only;
3. exact binding remains historical; head binding re-resolves current head;
4. `ModelAuthoritySnapshot` captures frozen resource scope generations;
5. enforce model compile capture order from section 14;
6. ensure successful mutation authority commit precedes final conversation tool result;
7. make `SkillCatalogSnapshot` immutable/generation-published;
8. preserve/reuse coherent `ToolPackSnapshot` publication barrier;
9. make SchemaRegistry active publication immutable/generation-published;
10. ensure compiler consumes frozen snapshots only.

**Acceptance gate C:**

```text
Chat B mutation invalidates Chat A evidence on Chat A's next compile without chat-local stale mutation;
HTML/viewer sees change through metadata and bounded pull;
model request carries fixed authority/catalog generation tuple;
no catalog can be observed half-published.
```

---

# 64. Direct cutover phase D — Cleanup + retention + documentation alignment

Implement:

1. delete hash-as-revision assumptions in touched code/docs;
2. delete duplicate chat-local/resource-local stale-head stores;
3. delete mutation paths that bypass authority publication;
4. delete direct UI freshness truth that is not derived from authority;
5. align CAS GC reachability with historical revisions/provenance/pending attempts;
6. ensure checkpoints/cache entries include authority generation dependencies rather than mutable pointers;
7. update architecture/resource/context docs to reference this consistency contract;
8. remove obsolete heavy diagnostics tied to removed freshness/mutation paths;
9. final repository search for duplicate `currentRevision`, stale booleans, path-based document identity and direct head mutation.

**Acceptance gate D:**

```text
one resource authority path remains reachable;
one mutation publication path remains reachable for mutable resource domains touched;
no UI/model subsystem can override current head truth;
retention cannot delete payload required by unresolved mutation recovery;
no legacy compatibility branch preserves the old semantics.
```

---

# 65. What to remove definitively

Search and remove/replace touched patterns equivalent to:

```text
Revision = ContentHash for live mutable resources
current head stored per chat/conversation
IsStale booleans used as primary freshness authority
HTML-specific “last good current revision” truth independent of URF
viewer-specific current revision registries
successful tool result persisted before authority publication
mutation dispatch with no prepared/recovery marker where crash uncertainty exists
post-dispatch failure classified as FailedNoEffect without proof
head silently left at old revision after UnknownAfterDispatch
Save As creating unrelated logical resource identity for the same active document
filesystem path used as sole document identity
SkillCatalog loaded/mutated live during model request assembly
catalog generation advanced before complete snapshot is published
continuation silently falling forward to current head
```

Do not delete canonical durable history, exact historical revisions, CAS, existing domain verification logic or ToolPack admission machinery that already satisfies the new contract.

---

# 66. What NOT to build

Do not add:

```text
new database solely for authority
new message bus framework
resource polling daemon
cross-document distributed transaction manager
universal Office mutation DSL
graph database for resource dependencies
second document registry
second revision type
second CAS
per-consumer freshness managers
global process-wide authority lock
full audit payload logging
background model-based reconciliation
```

The architecture should become smaller in responsibility count, not larger.

---

# 67. Minimal focused checks only

Do not create a broad test/benchmark phase. Build affected projects and cover the critical semantics directly.

Required focused scenarios:

```text
1. R1 hash A -> R2 hash B -> restore R1 -> R3 hash A, R3 != R1
2. same document opened in Chat A and Chat B shares one authority scope
3. Chat B mutation advances authority; Chat A next frozen snapshot sees new generation
4. VerifiedNoChange preserves known head
5. UnknownAfterDispatch changes Known head to Unknown
6. later reconciliation of Unknown creates/chooses a safe exact current revision
7. crash after DispatchMayHaveOccurred but before authority commit is recovered without blind retry
8. crash after authority commit but before attempt finalization does not duplicate revision/effect
9. Save As keeps DocumentAuthorityId and updates locator
10. independent copied/opened file does not collide with the original authority id
11. continuation opened on R10 never returns R11 after head advance
12. HTML head binding refreshes by pull; exact R10 binding remains R10
13. ToolPack/SkillCatalog/Schema snapshot generation is fixed for one model compile
14. authority commit conflict cannot overwrite a newer generation
15. unresolved mutation payload cannot be GC'd before reconciliation
```

User will perform real-machine performance/behavior validation after implementation. Do not add a long synthetic qualification stage.

---

# 68. Definition of Done

- [ ] one canonical shared `ResourceAuthorityStore` or equivalent owns current head projection;
- [ ] resource authority is document/resource scoped, never conversation scoped;
- [ ] `DocumentAuthorityId` is separate from path/COM/runtime locator;
- [ ] Save/Save As/reopen semantics preserve or fork lineage deliberately rather than by hash/path accident;
- [ ] live mutable `RevisionId` is never the content hash;
- [ ] `ResourceHeadState` can represent `Unknown` without retaining the old revision as current;
- [ ] authority generation is monotonic per scope;
- [ ] one immutable `ResourceAuthoritySnapshot` represents one exact generation;
- [ ] one atomic `ResourceAuthorityCommit` publishes effect + head changes + generation together;
- [ ] no external provider/COM I/O occurs inside authority publication/capture;
- [ ] guarded mutations check canonical revision/head authority before dispatch;
- [ ] crash-sensitive mutations persist `DispatchMayHaveOccurred` before external dispatch;
- [ ] verified mutation outcome is published before final successful tool result enters conversation history;
- [ ] `UnknownAfterDispatch` cannot leave old head falsely current;
- [ ] startup recovery reconciles unresolved attempts and never blindly replays writes;
- [ ] mutation recovery is idempotently linked to authority commits;
- [ ] detected external drift becomes shared authority, not chat-local stale state;
- [ ] read-discovered new current state is published before being represented as current evidence;
- [ ] all chats attached to one authority scope observe the same current state;
- [ ] UI/HTML/viewer notifications are advisory metadata; correctness comes from authority snapshots;
- [ ] exact leases/continuations never fall forward to newer heads;
- [ ] derived resource currentness can be evaluated from frozen dependency authority without deleting historical revisions;
- [ ] ToolPack, SkillCatalog and SchemaRegistry publish immutable complete snapshots with generations;
- [ ] one model request uses one fixed authority/catalog generation tuple;
- [ ] current compiler/evidence layer performs no live authority/provider refresh mid-compile;
- [ ] multiple writes per agent step remain allowed; each mutation has explicit commit semantics;
- [ ] existing CAS is reused and pending recovery payloads are retained until safe;
- [ ] no parallel legacy head/freshness/mutation-publication path remains reachable in touched contours.

---

# 69. Required architecture-document alignment

After implementation, update the canonical architecture docs so the ownership chain is explicit:

```text
Universal Resource Fabric
    owns resource identity/revisions/effects/provider semantics

Resource Authority & Consistency
    owns shared current-head knowledge/generation/atomic publication/recovery

Resource Evidence & Model Context Compiler
    owns observation currentness projection and LLM context compilation
```

Add a short normative link from the URF document near `ResourceEffect`, current-head updates and external drift:

> Atomic publication of effects/head changes/generation and mutation recovery follow `RNAssistant_RESOURCE_AUTHORITY_CONSISTENCY_AND_MUTATION_COMMIT_FINAL_DIRECT_CUTOVER.md`.

Add a short normative link from the Model Context Compiler document near `ModelAuthoritySnapshot`:

> Resource authority generations and frozen head/effect snapshots are captured from the canonical Resource Authority contract; the compiler must not reconstruct them independently.

Do not duplicate this entire contract into the other documents.

---

# 70. Final target architecture

```text
                         DOMAIN OWNERS
           Excel / VBA / Word / files / packages
                              |
                    mutate / read / verify
                              |
                              v
                 UNIVERSAL RESOURCE FABRIC
          identity / revisions / views / coverage
          payloads / effects / dependencies / leases
                              |
                              v
                 RESOURCE AUTHORITY STORE
             shared by document/resource scope
         heads: Known/Unknown + generation + commits
                              |
              +---------------+---------------+
              |               |               |
              v               v               v
      EvidenceStateReducer   HTML/viewers   future Python
              |
              v
       ModelAuthoritySnapshot
              |
              v
       ModelContextCompiler
              |
              v
             LLM

Durability side:

MutationAttempt journal
      |
      +-> DispatchMayHaveOccurred
      |
      +-> provider/domain dispatch + verify
      |
      +-> ResourceAuthorityCommit  <--- atomic publication barrier
                |
                +-> ResourceEffect
                +-> HeadChanges
                +-> Generation N+1
                +-> mutation-attempt link
```

---

# 71. Final implementation principle

The implementation is correct only when the following statement is true everywhere in RNAssistant:

> **A resource becomes current for the system only through one shared authority publication. A conversation, UI surface, provider result, cache or successful tool message cannot independently declare a resource current.**

And for uncertain writes:

> **If RNAssistant cannot prove whether a dispatched mutation changed the physical source, it must lose certainty about the old head rather than preserve a convenient but false current state.**

These two invariants are more important than preserving any legacy class or compatibility path.

---

# 72. Final report format for coding agent

```text
Implemented
- shared ResourceAuthorityStore / scope generations
- ResourceAuthoritySnapshot + atomic commits
- logical DocumentAuthorityId / Save As handling
- mutation attempt + crash recovery integration
- Excel/VBA authority publication
- external drift reconciliation
- cross-chat/cross-consumer notification
- immutable ToolPack/SkillCatalog/Schema publication barriers
- ModelAuthoritySnapshot handoff

Removed/replaced
- hash-as-revision assumptions
- chat-local current-head/freshness state
- mutation paths bypassing authority publication
- live mutable catalog reads in model request construction
- duplicate current-revision caches in touched consumers

Key invariants confirmed
- RevisionId != ContentHash
- effect/head/generation publish atomically
- UnknownAfterDispatch -> Unknown head
- Save As preserves logical document lineage
- exact continuations do not fall forward
- one model compile uses one frozen generation tuple

Build/checks
- affected projects build
- focused scenarios from section 67 covered

Remaining notes
- only genuine out-of-scope issues; do not propose restoring legacy compatibility paths
```
