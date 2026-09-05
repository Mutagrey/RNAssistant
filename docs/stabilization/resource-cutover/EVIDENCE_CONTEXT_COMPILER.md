# RNAssistant — Resource Evidence Lifecycle & Model Context Compiler: FINAL direct-cutover implementation contract

**Status:** canonical second-stage implementation specification.

**Prerequisite:** `RNAssistant_UNIVERSAL_RESOURCE_FABRIC_FINAL_DIRECT_CUTOVER.md` is the authority for all resource identity/revision/head/view/coverage/effect/payload semantics.

**Supersedes:** earlier `RNAssistant_RESOURCE_LINEAGE_EVIDENCE_CONTEXT_COMPILER_DIRECT_CUTOVER*.md` versions for implementation.

**Strategy:** direct cutover; one deterministic correctness path and one `ModelContextCompiler`; remove old parallel stale/context projection paths in touched contours.

---

# 0. Coding-agent instruction

Do not redefine Resource Fabric concepts in this implementation.

Consume from URF:

```text
ResourceIdentity / canonical ResourceRef
exact revisions and current heads
ResourceDescriptor
Resource View
ResourceCoverage
ResourceEffect / ResourceImpact
ResourceDependency/provenance
PayloadRef/CAS
ToolPackSnapshot / SkillCatalogSnapshot authority generations
```

Build only the layer above those primitives:

```text
runtime resource observations/effects
        |
        v
ResourceEvidence records
        |
        v
EvidenceStateReducer
        |
        v
ModelContextCompiler
        |
        v
bounded current LLM request
```

Durable history remains append-only/reference-first. Prompt context is a compiled projection, not a replay of all raw messages/tool bodies.

Do not perform live Excel/VBA/PDF/provider reads inside context compilation.

---

# 1. Canonical separation of truths

Keep these four levels separate:

```text
Resource Fabric truth
    resource heads, exact revisions, effects, dependencies

Durable conversation/event truth
    append-only user/assistant/tool/runtime facts

Evidence state projection
    which observations are current/superseded/unknown for this compile snapshot

Model context projection
    which valid/relevant atoms enter this specific LLM request
```

Critical invariants:

```text
Durable history != model context
Resource validity != prompt residency
ResourceEvidence != resource truth
Compaction != deletion of durable history
```

---

# 2. `ResourceEvidence` is an observation, never authority

Introduce/reuse one observation record referencing canonical URF identity.

Conceptual fields:

```text
EvidenceId
exact ResourceRef
View
Coverage
Completeness: complete/partial
PayloadRef if externalized
Dependencies/provenance when derived
ObservedAt / source EventId
optional semantic summary metadata
```

Do not create `EvidenceResourceRef`, `ContextResourceKey` or another resource identity model.

Do not permanently mutate an observation into `IsStale=true` as the source of truth. Evidence currentness is computed by reducer from frozen URF/event facts.

---

# 3. Evidence states

The reducer should produce a small deterministic state model such as:

```text
Current
Superseded
Unknown
Unavailable
```

Optional reason metadata may include:

```text
head advanced
coverage intersects mutation
dependency changed
external drift unknown
snapshot unavailable
tool/skill authority generation changed
schema/mapping changed
```

Avoid a dozen overlapping stale booleans.

`Unknown` is important after URF `UnknownAfterDispatch` or drift with unknown new revision.

---

# 4. EvidenceStateReducer — one deterministic reducer

Inputs are frozen facts only:

```text
ResourceEvidence observations
ResourceEffect/ResourceImpact stream or reduced head/effect state
ResourceDependency/provenance
ToolPackSnapshot generation
SkillCatalogSnapshot generation
schema/mapping revisions
```

The reducer must not:

```text
read live providers
call Excel COM
inspect files directly
run model summarization
decide token budget
```

Use provider/domain impact matchers for coverage semantics. No giant `if VBA / if Excel / if PDF` stale switch in the reducer.

---

# 5. Domain impact matchers

Implement small domain adapters where needed.

Examples:

## VBA

```text
read Module1@R15 lines 1-800
patch Module1 -> R16
impact exact/intersects source
old evidence -> Superseded
```

No-op effect keeps prior evidence current if revision/head semantics confirm no change.

`UnknownAfterDispatch` makes affected prior evidence `Unknown` until re-observed/resolved.

## Excel ranges

Evidence:

```text
Sheet1@R22 / table / A1:F500
```

Write:

```text
B4:B20
```

Matcher evaluates coverage intersection. Non-intersecting evidence may remain current if provider/head semantics support sectional validity; if the URF revision model only certifies whole-resource revisions, prefer conservative supersession rather than inventing unsupported certainty.

## PDF/image/immutable file

Exact immutable revision evidence remains current for that exact revision. If a logical head changes, evidence is historical rather than invalid bytes.

## Derived/schema/mapping

Evidence on a derived resource becomes superseded/unknown according to dependency provenance when source/schema/mapping revisions change.

---

# 6. Same-run correctness after mutation

Within one agent run:

```text
read R15
mutate -> verified R16
continue reasoning
```

The next model request must not continue seeing R15 as current simply because the tool read occurred earlier in the same dialogue tail.

Flow:

```text
mutation produces ResourceEffect
-> reducer updates evidence projection
-> next ModelContextCompiler request sees R15 observation as Superseded
-> model can re-read R16 if needed
```

This is the primary correctness requirement of the cutover.

---

# 7. Re-read semantics

Do not automatically re-read every stale resource during context compilation.

Correct behavior:

```text
compiler excludes/demotes superseded evidence
model sees compact stale marker/changed resource fact when useful
model decides whether task requires re-read
resource tool reads current/exact revision through URF
new ResourceEvidence is appended
```

This prevents hidden COM I/O and token/memory churn.

---

# 8. ModelAuthoritySnapshot — freeze one coherent compile input

At the beginning of every LLM request, freeze a coherent authority/state snapshot.

Conceptually include:

```text
resource head/effect generation or immutable reduced snapshot
ToolPackSnapshot generation
SkillCatalogSnapshot generation
SchemaRegistry generation if relevant
conversation/event high-water mark
```

Call this `ModelAuthoritySnapshot` if helpful; it need not be a new type if existing snapshots can be grouped atomically.

The compiler must not mix generations halfway through one request.

If perfect cross-subsystem atomicity is impossible, capture ordered generation IDs/high-water marks and compile against that fixed tuple; document the consistency rule.

---

# 9. `ModelContextCompiler` — the only LLM request assembler

After cutover every model request goes through one compiler.

`ConversationModelSession`, planner/executor loops and repair turns must not each build their own history/resources/tool projections.

The compiler consumes:

```text
frozen authority snapshot
append-only conversation/event facts up to high-water mark
current EvidenceState projection
system/project instructions
active SkillCatalogSnapshot
active ToolPackSnapshot
current user request
budget policy
```

It emits:

```text
ModelContextSnapshot
serialized request messages/tool schemas
ContextReceipt
```

---

# 10. ContextAtom — one intermediate representation

Normalize candidate model-visible content into one internal atom format before budgeting/serialization.

Suggested conceptual fields:

```text
AtomId
Kind
CausalFrameId if any
Priority/Relevance class
Token estimate
Semantic body or PayloadRef
ResourceEvidence refs / provenance
Authority generation dependency
CanCompact
MustKeep
```

Kinds may include:

```text
system invariant
user message
assistant semantic result
tool interaction frame
resource evidence
resource change marker
plan/decision
structured compacted claim set
```

Do not make every original message format a separate budgeting path.

---

# 11. ToolInteractionFrame — preserve causality

Tool call and result must be treated as one causal frame for model projection.

Conceptual frame:

```text
assistant intent/call
runtime tool invocation
model-visible result/error
resource effects/evidence metadata
```

Do not independently drop a tool call while retaining an orphan result, or retain huge arguments after a terminal verified operation.

After a completed mutation, project a compact semantic frame such as:

```text
Updated Module1 from R15 to R16; verified.
```

Large original source body remains behind `PayloadRef` in durable/runtime storage if needed for audit/rollback.

---

# 12. Large tool arguments/results externalization

Use the URF/CAS `PayloadRef` contract.

## Large mutation arguments

Examples:

```text
VBA replacement source
HTML full document
large Excel value matrix
large JSON rewrite
tool/skill implementation body
```

Durable event stores semantic invocation metadata + `PayloadRef`, not repeated megabyte-scale inline bodies.

## Large read results

Resource read observation stores:

```text
EvidenceId
exact ResourceRef
View
Coverage
PayloadRef
semantic metadata
```

For a current model request, compiler hydrates only selected bounded payloads.

Do not blindly hydrate every stored payload referenced by history.

---

# 13. Compiler pipeline — strict order

Use this order. Correctness filtering must happen before compaction/budgeting.

## Step 1 — Freeze authority/state

Capture the fixed `ModelAuthoritySnapshot` tuple/high-water marks.

## Step 2 — Replay/reduce durable facts incrementally

Produce current evidence/resource-effect projection from checkpoints + tail events. Do not replay giant bodies.

## Step 3 — Build raw ContextAtoms

Create atoms/causal frames from durable facts and active instructions/skills/tools.

## Step 4 — Correctness filter FIRST

Remove or transform atoms relying on superseded/unknown authority according to policy.

Examples:

```text
superseded VBA source body -> exclude
historical exact revision explicitly requested -> may include as historical
unknown-after-dispatch -> include compact uncertainty marker, not old body as current
inactive old skill/tool generation -> exclude from current authority
```

## Step 5 — Collapse terminal mutation frames

Replace large completed write arguments/results with compact verified semantic outcome + refs.

## Step 6 — Deduplicate repeated observations

Keep the newest/current equivalent evidence required for reasoning; retain historical facts only when task/relevance requires them.

## Step 7 — Relevance selection

Select atoms relevant to current user task and causal continuity.

## Step 8 — Hydrate selected payloads

Read only selected `PayloadRef`s from CAS/runtime store, with bounds.

## Step 9 — Structured compaction

Compact only already-valid atoms. Never summarize stale content and then keep the stale summary as truth.

## Step 10 — Token budgeting

Apply budget policy after correctness/relevance/compaction.

## Step 11 — Serialize request

Produce final messages/tool schemas/instruction blocks.

## Step 12 — Freeze `ModelContextSnapshot` + `ContextReceipt`

Record what generations/high-water marks and major atom decisions produced the request.

---

# 14. Structured compaction and claims

Do not build a full knowledge graph of every dialogue sentence.

`ContextClaims`/claim provenance is required primarily for **compacted summaries that must survive removal of their source atoms**.

Normal raw dialogue messages do not need per-fact claim graph decomposition.

For a durable compacted summary, store structured claims with provenance such as:

```text
claim text/semantic fields
source EventIds/EvidenceIds
resource exact revisions when relevant
authority generations when relevant
```

If supporting evidence becomes superseded, the reducer/compiler can invalidate or rebuild only affected claims.

Never let a stale fact survive solely because it was copied into an old free-form summary.

---

# 15. Dialogue tail

Keep a bounded recent causal tail for conversational continuity, but apply correctness projection to it too.

A recent message saying “Module1 currently contains old source…” must not override a newer verified resource effect merely because it is recent.

Resource truth wins over textual recency.

---

# 16. Skills and tools authority

Use active authority snapshots from URF-adjacent catalogs:

```text
SkillCatalogSnapshot
ToolPackSnapshot
```

Historical skill/tool bodies may remain resources but are not active model authority.

The compiler injects only active tool schemas/skill instructions required by current request according to existing policy.

If an active catalog generation changes, old context atoms depending on previous active instruction/tool definitions are not automatically current authority.

---

# 17. PromptBudgetComposer responsibility

After cutover, budget logic owns only budget/policy decisions such as:

```text
maximum request tokens
reserved response tokens
priority classes
bounded dialogue tail size
payload hydration budget
```

It must not independently decide stale/current resource truth.

If an existing `PromptBudgetComposer` also performs history correctness or arbitrary serialization, strip those responsibilities and route them through `ModelContextCompiler`.

---

# 18. ContextCompactionService responsibility

Compaction operates on valid selected atoms/claims, not raw unrestricted history.

It may:

```text
summarize old dialogue
merge repeated valid observations
produce structured claims with provenance
```

It must not:

```text
read live providers
resurrect superseded resource bodies
rewrite append-only durable history
become a second context compiler
```

---

# 19. ModelToolResultProjection

If an existing projection service remains useful, make it a thin serializer from typed runtime/model results into `ContextAtom`/model-visible form.

If it duplicates compiler correctness/filtering, remove it.

There must be one correctness authority path.

---

# 20. ConversationModelSession responsibility

After cutover:

```text
receive request/agent state
-> ask ModelContextCompiler for compiled snapshot
-> invoke model
-> append resulting durable events
```

It does not manually concatenate all previous messages/tool results/resources.

Every LLM request path, including repair/retry/subagent where applicable, must use the compiler or an explicitly documented bounded specialized derivative using the same frozen authority/evidence state.

---

# 21. Resource reads during one run

A resource read creates new `ResourceEvidence` immediately in durable/runtime facts.

Subsequent model turns in the same run consume it through the same reducer/compiler pipeline.

No hidden “resource text cache” should bypass evidence/revision semantics.

---

# 22. Rollback workflow

Rollback uses URF restore semantics:

```text
select historical exact revision
-> normal guarded restore mutation
-> verify
-> new revision/head/effect
-> new evidence on re-read if needed
```

Context compiler does not retrieve an old prompt snapshot and pretend runtime state rolled back.

Historical `ModelContextSnapshot`s are diagnostic/replay artifacts, not a mechanism for mutating current runtime truth.

---

# 23. Diff

Treat diff as a bounded resource view/derived resource when useful.

Do not permanently carry full before/after source versions in every prompt merely to support “what changed?”.

Example:

```text
diff(R15,R16, coverage=...) -> bounded derived/view payload
```

---

# 24. Plans, HTML and artifacts

Plan text and generated HTML/artifact bodies follow the same reference-first/history rules:

```text
large body -> PayloadRef/resource revision
conversation -> compact semantic event/ref
current context -> hydrate only if relevant
```

Do not duplicate artifact bodies into model history after they become canonical resources.

---

# 25. ContextReceipt — lightweight diagnostics

Produce one compact receipt per compiled request; avoid heavy per-token/per-event telemetry.

Suggested contents:

```text
request/context snapshot id
authority generation tuple
conversation high-water mark
atom counts by kind
excluded superseded/unknown counts
payloads hydrated count/bytes
token estimate by major class
compaction applied yes/no
```

Do not log user payload bodies.

The receipt exists to answer “why did the model see this?” without replaying verbose diagnostic logs.

---

# 26. Checkpoints and incremental reduction

For performance, allow compact checkpoints of reducer/compiler state.

Checkpoint may include:

```text
processed event high-water mark
resource/evidence reduced metadata
claim provenance index
catalog generation refs
```

It must not become correctness authority independent of durable facts. On invalid checkpoint/version mismatch, rebuild from durable metadata + CAS refs.

No full resource body should be embedded in checkpoints.

---

# 27. Retention

Separate retention classes:

```text
durable semantic events
resource revision/provenance metadata
CAS payload bodies
transient leases/shared buffers
ModelContextSnapshots/receipts
```

Do not delete a historical resource revision merely because it left the prompt. Do not keep every hydrated body forever merely because the resource lineage remains.

Use existing retention mechanisms where possible; this document does not require a new database/event framework.

---

# 28. Stale/change markers shown to model

Use compact semantic markers only when useful for the current task, for example:

```text
Resource Module1 changed from R15 to R16 after the previous read; old source evidence was superseded.
```

or:

```text
A prior Excel read may be stale because a write was dispatched but verification was lost; re-read before relying on affected cells.
```

Do not dump internal `EffectId`, CAS URI, COM identity or capability token unless an existing model-facing protocol explicitly requires a safe semantic identifier.

---

# 29. Error handling

Distinguish resource errors from evidence/compiler errors.

Resource errors come from URF, e.g.:

```text
RESOURCE_SNAPSHOT_UNAVAILABLE
RESOURCE_REVISION_CHANGED
RESOURCE_STALE
```

Compiler/evidence errors may include:

```text
CONTEXT_PAYLOAD_UNAVAILABLE
CONTEXT_AUTHORITY_SNAPSHOT_INCONSISTENT
CONTEXT_COMPACTION_FAILED
```

Prefer fail-safe bounded context over silently injecting uncertain old evidence.

---

# 30. Telemetry cleanup

Do not add high-frequency runtime logs for every button/tool/context atom.

Keep:

```text
ResourceEffect durable facts where required
ContextReceipt per model request
errors/warnings
coarse elapsed/size diagnostics around compiler if already supported
```

Remove/disable touched old telemetry that only existed to debug superseded parallel context/stale paths and causes UI/runtime load.

---

# 31. Direct cutover — four phases only

## PHASE A — Evidence/Effect integration

Open first:

```text
conversation/event persistence
resource read/write result handling
existing stale/invalidation code
VBA/Excel mutation verification paths
URF ResourceEffect/ResourceCoverage contracts
```

Implement:

1. one `ResourceEvidence` observation record/projection;
2. evidence creation from resource reads;
3. one deterministic `EvidenceStateReducer`;
4. provider/domain impact matchers beginning with VBA and Excel;
5. same-run mutation effect integration;
6. current/superseded/unknown semantics;
7. no live provider I/O in reducer.

**Acceptance gate A:** read -> mutate -> next model turn no longer receives old evidence as current; no-op preserves valid evidence; unknown-after-dispatch produces uncertainty rather than false currentness.

## PHASE B — One ModelContextCompiler

Open first:

```text
ConversationModelSession
PromptBudgetComposer
ContextCompactionService
ModelToolResultProjection
all model-request construction paths
```

Implement:

1. fixed `ModelAuthoritySnapshot` capture;
2. `ContextAtom` and causal `ToolInteractionFrame`;
3. compiler pipeline in the exact order from section 13;
4. every model request routed through compiler;
5. budget/compaction become subordinate policies/services;
6. `ModelContextSnapshot` + `ContextReceipt`;
7. remove duplicate old request assemblers in touched paths.

**Acceptance gate B:** one model-request path; changing resource/tool/skill authority during compile cannot silently mix generations; correctness filtering happens before budgeting/compaction.

## PHASE C — Payload externalization + all resource domains

Implement:

1. large read bodies -> existing CAS `PayloadRef`;
2. large mutation arguments -> `PayloadRef`;
3. selective hydration after relevance filtering;
4. Excel range matcher/coverage;
5. derived/schema/mapping dependency invalidation;
6. PDF/image/artifact exact-revision evidence semantics;
7. active skill/tool generation dependencies;
8. remove hidden large resource-text caches that bypass evidence.

**Acceptance gate C:** durable/context history size is metadata/reference-first for large payloads; model can still receive selected bounded content; historical exact immutable evidence remains addressable without being treated as current head.

## PHASE D — Cleanup, structured claims, rollback/docs

Implement:

1. delete old stale managers/projections/request assemblers in touched contours;
2. structured claim provenance only for compacted summaries that outlive source atoms;
3. rollback flow uses URF restore/new revision semantics;
4. context/skills/prompts documentation update;
5. remove obsolete heavy telemetry tied to removed paths;
6. final repository search for duplicate concepts.

**Acceptance gate D:** no parallel correctness/context path remains reachable; summaries cannot preserve stale resource facts without provenance; rollback does not rewrite history; providers/Gateway remain independent of context compiler.

---

# 32. What to remove definitively

Search and remove/route around obsolete touched patterns such as:

```text
parallel “stale resource” booleans outside reducer
manual resource text injection into prompt
multiple independent conversation->messages builders
full tool arguments/results retained inline after terminal operations
context compaction that summarizes raw stale history before correctness filtering
model request code that directly reads live Excel/VBA/provider state
old skill/tool bodies treated as active because they exist in resources
```

Do not remove append-only durable history, resource historical revisions, CAS or ToolPack/SkillCatalog authority snapshots.

---

# 33. What NOT to build

Do not add:

```text
new ResourceRef/ResourceKey
second CAS
knowledge graph for every dialogue fact
live resource polling during context compile
model-generated stale decisions
new event-sourcing framework if current durable events suffice
full Python runtime
full dependency graph database
background summarization service
huge telemetry pipeline
```

---

# 34. Minimal focused checks only

Do not create broad test churn. Build affected projects and cover these critical scenarios:

```text
VBA read -> verified mutation -> old evidence superseded
verified no-op -> evidence remains valid
unknown-after-dispatch -> evidence becomes unknown
Excel non/intersecting coverage behavior according to provider guarantees
resource exact historical revision remains historical/readable
schema/mapping dependency change supersedes derived semantic evidence
one compiler path used by model request
active ToolPack/SkillCatalog generation frozen per request
large payload externalization/hydration bounded
rollback creates new revision/effect
```

Real-machine UI/performance validation can be performed by the user after implementation; do not add a separate long benchmark phase.

---

# 35. Definition of Done

- [ ] `ResourceEvidence` references canonical URF refs/views/coverage and is not a second identity model;
- [ ] observations are immutable facts; currentness is reducer projection;
- [ ] one `EvidenceStateReducer` handles resource effect truth through domain matchers;
- [ ] `UnknownAfterDispatch` cannot leave old evidence falsely current;
- [ ] no live provider/COM I/O occurs during context compile;
- [ ] one frozen authority/state tuple is used per model request;
- [ ] one `ModelContextCompiler` constructs every normal LLM request;
- [ ] correctness filtering precedes relevance, compaction and budgeting;
- [ ] tool calls/results remain causal frames;
- [ ] large read/write bodies are reference-first via existing `PayloadRef`/CAS;
- [ ] only selected bounded payloads are hydrated;
- [ ] active SkillCatalog/ToolPack authority is generation-frozen;
- [ ] compacted summaries that survive source removal carry claim provenance;
- [ ] normal dialogue is not forced into a per-fact knowledge graph;
- [ ] rollback uses URF restore/new revision semantics;
- [ ] `ContextReceipt` provides lightweight explainability without heavy telemetry;
- [ ] old duplicate stale/context projection paths in touched contours are deleted;
- [ ] durable history remains append-only and distinct from prompt residency.

---

# 36. Final report format for coding agent

```text
Implemented
- ResourceEvidence/reducer
- domain matchers
- ModelAuthoritySnapshot/freeze rule
- ModelContextCompiler pipeline
- payload externalization
- claim provenance/rollback integration

Existing components reused
- ...

Removed legacy
- ...

Files changed
- ...

Focused checks
- ...

Architecture checks
- one reducer: yes/no
- one compiler: yes/no
- no live provider I/O during compile: yes/no
- correctness before compaction/budget: yes/no
- large payloads reference-first: yes/no
- ToolPack/SkillCatalog frozen authority: yes/no

Remaining limitations
- only concrete limitations
```
