# RNAssistant — Universal Resource Fabric: FINAL direct-cutover implementation contract

**Status:** canonical implementation specification.

**Supersedes:** `RNAssistant_UNIVERSAL_RESOURCE_FABRIC_DIRECT_CUTOVER.md`, `RNAssistant_URF_CONTEXT_ALIGNMENT_ADDENDUM.md`, and the earlier HTML Data Plane plans.

**Strategy:** direct cutover only. Do not keep parallel legacy binding/resource paths for compatibility.

**Primary consumers:** Agent resource tools, HTML/JS/WebView2, artifact viewers; future Python must attach to the same fabric without a second resource system.

---

# 0. Coding-agent instruction

Implement one Universal Resource Fabric and make it the only generic resource/data boundary in RNAssistant.

Do **not** implement a separate `HtmlDataRef`, `HtmlDataStore`, `EvidenceResourceRef`, Python resource registry, or another freshness manager. Reuse the repository's existing canonical resource/snapshot/CAS primitives where they already satisfy this contract; rename/refactor only where semantics are ambiguous.

The final architecture must satisfy:

```text
physical source
  -> provider
  -> ResourceGateway
  -> canonical resource identity/revision
  -> capabilities/views
  -> bounded read/stream or immutable payload
  -> consumers: Agent | HTML/JS | future Python
```

URF owns resource identity, current-head/revision state, provider dispatch, view/capability semantics, snapshots/materialization, dependencies/provenance and resource effects. URF does **not** own model prompt residency, token budgeting, dialogue compaction or summarization.

Do not preserve old `HtmlWorkspaceDataSource.Json`, `window.RNAssistantData`, `accepted.DataJson` bulk-binding, base64 binary payloads in WebView messages, or duplicate resource abstractions merely for compatibility.

---

# 1. Canonical boundaries

```text
PHYSICAL SOURCES
Excel / VBA / JSON / CSV / text / PDF / image / file / artifact / package
        |
        v
RESOURCE PROVIDERS
        |
        v
UNIVERSAL RESOURCE FABRIC
Resource identity / exact revisions / heads / descriptors / capabilities / views
coverage / dependencies / effects / snapshots / CAS payloads / leases
        |
        +------------------------+------------------------+
        |                        |                        |
        v                        v                        v
Agent resource tools        HTML / RN.resources      future Python
```

Forbidden dependencies:

```text
ResourceGateway -> ConversationModelSession       FORBIDDEN
Provider        -> ModelContextCompiler            FORBIDDEN
HTML bridge     -> Excel COM directly              FORBIDDEN
Python adapter  -> Excel COM directly              FORBIDDEN
Context compiler-> live provider I/O               FORBIDDEN
```

---

# 2. Four identity domains — never merge them

The implementation must distinguish these concepts even if existing types use different names.

| Domain | Meaning |
|---|---|
| `ResourceIdentity` | logical resource independent of current revision |
| `ResourceRevisionRef` / canonical exact `ResourceRef` | exact immutable/logically fixed resource state |
| `PayloadRef` | immutable stored body/CAS object |
| `EffectId` / later `EvidenceId` | runtime mutation/observation facts, not resource identity |

Canonical invariant:

```text
Resource identity answers: WHAT resource?
RevisionId answers: WHICH logical state?
PayloadRef/ContentHash answers: WHICH immutable bytes?
Capability token answers: WHO may access WHAT?
```

`ResourceRef` is identity, never authorization. A capability/lease/token is authorization/lifetime, never identity.

If the existing project already has one `ResourceRef` type, keep it canonical. Do not create a second key just to match this document. Normalize its semantics so exact revision and logical identity are unambiguous.

---

# 3. RevisionId != ContentHash

This is mandatory.

```text
R1 -> R2 -> R3
restore R1 -> R4

R4.ContentHash may equal R1.ContentHash
R4.RevisionId must not equal R1.RevisionId
R4.RestoredFrom = R1
```

`RevisionId` represents causal/logical lineage. `ContentHash` identifies immutable bytes/body in CAS.

Content-derived identity is acceptable for an immutable blob object itself, but mutable/live/logical resource history must preserve separate revision lineage.

This distinction is required for:

- restore/rollback;
- verified no-op vs repeated write;
- external drift;
- package generations;
- derived resources;
- audit/provenance.

---

# 4. ResourceIdentity, ResourceRevisionRef and ResourceHead

Conceptually support three roles:

```text
ResourceIdentity
    excel://book/sheet1

Exact revision
    excel://book/sheet1 @ R27

Current head
    excel://book/sheet1 -> R27
```

The actual C# model may combine roles, but APIs must distinguish:

```text
HEAD read      = resolve current revision, then read it according to provider semantics
EXACT read     = read the requested exact revision/snapshot only
```

An exact read must never silently fall forward to current head.

Historical revisions remain valid snapshots while retention allows them. A head change means the old revision is historical, not corrupted.

---

# 5. ResourceDescriptor

Normalize one descriptor returned through the Gateway. It should express only resource-level facts, not consumer-specific UI state.

Recommended information:

```text
identity
exact revision if resolved
resource type
structural kind
size/shape metadata when known
semantic schema ref when attached
capabilities
supported views
provider freshness/tracking semantics
provenance summary for derived resources
```

Do not put filesystem paths, COM identities, ownership secrets or capability tokens in model-visible projections.

---

# 6. Resource Type, Capability, View, Semantic Schema, Mapping

Do not mix these concepts.

## Resource Type — how it is physically read

Examples:

```text
excel.workbook
excel.range
vba.project
vba.module
json
csv
text
pdf
image
binary
artifact
```

## Capability — what operations the resource/provider supports

Examples:

```text
describe
read
stream
table
records
text
pages
render-page
raw
schema
```

## View — representation requested by consumer

Examples:

```text
raw
text
table
records
tree
pages
image
metadata
schema
source
```

## Semantic Schema — what the data means

Examples:

```text
production.daily.v1
production.monthly.v1
budget.cashflow.v1
```

## ResourceSchemaMapping — how a particular physical source maps to a semantic schema

Example:

```text
source = workbook@R7
schema = production.daily.v1@S3
mapping:
  sheet = Production
  headerRow = 3
  well_id -> A
  date    -> B
  oil     -> F
```

A schema must not contain Excel-specific coordinates. Mapping owns physical coordinates.

---

# 7. Capability negotiation

Descriptors should allow consumers to discover supported operations and bounds rather than guess them.

Conceptual example:

```json
{
  "view": "table",
  "supports": {
    "offset": true,
    "fields": true,
    "stream": true
  },
  "limits": {
    "maxRowsPerBatch": 10000,
    "maxBatchBytes": 8388608
  }
}
```

Do not build a giant universal argument object. View/provider-specific request DTOs may exist behind a shared envelope.

---

# 8. ResourceCoverage is first-class

Every partial observation/read that may later participate in freshness or evidence must be describable by coverage.

Minimum forms:

```text
whole
line-range
cell-range
page-range
time-range
json-path
record-range
field-set
```

Examples:

```text
VBA Module1 lines 200-400
Excel Sheet1!A1:F500
PDF pages 10-15
JSON $.production[0:1000]
Table rows 0-5000, fields well_id,date,oil
```

Use a common envelope plus provider/domain-specific matching logic. Do not put all intersection rules in `ResourceGateway`.

---

# 9. Providers

Providers understand physical sources. Transport adapters do not.

Recommended conceptual interface:

```text
IResourceProvider
  CanHandle(type/identity)
  Describe
  Read
  Stream when supported
  Capture/lease snapshot when supported
```

Provider-specific logic stays provider-specific.

## Excel provider

The Excel provider may use Office COM/STA to perform bounded reads such as `Range.Value2`. The WebView endpoint must never call Excel COM directly.

```text
JS -> WebView resource router -> ResourceGateway -> ExcelResourceProvider
   -> approved Office STA dispatcher -> bounded Range read
```

## JSON provider

Reads JSON from memory/file/CAS/resource source and exposes appropriate `raw/tree/records/table` views.

## PDF provider

Exposes `raw`, `metadata`, `pages`, `text`, `render-page`, and later table extraction only if implementation exists.

## Image provider

Exposes `raw`, `image`, `metadata`; transformations are separate capabilities if implemented.

## File/blob/CAS provider

Provides byte streams and immutable payload access without inventing semantic structure.

Providers must not know about HTML, ModelContextCompiler or future Python.

---

# 10. ResourceGateway — deliberately narrow

`ResourceGateway` is the only generic resource execution boundary.

Keep its responsibilities narrow:

```text
describe
resolve identity/head/exact revision
read bounded view
stream bounded view
route to provider
validate ownership/capability at runtime boundary
```

Do **not** turn it into:

```text
SQL engine
aggregation DSL
schema inference engine
HTML bridge
model context compiler
all-format parser switch
```

Derived resources and semantic operations belong to dedicated services built on top of the Gateway.

---

# 11. Three materialization modes

Do not interpret reference-first as “copy every source to CAS before reading it.” Support three modes.

## 11.1 Live bounded read

Use when provider can safely serve a small bounded request directly.

Example:

```text
HTML requests Excel rows 0-5000
-> provider reads only required cells
-> returns bounded batch
```

No mandatory full workbook/range copy.

## 11.2 Snapshot-backed read

Use when one logical continuation/stream must remain revision-consistent across multiple batches/pages.

Provider must either:

- capture/materialize an immutable snapshot; or
- provide a snapshot/lease mechanism with stable revision semantics.

Never mix pages/batches from different live states under one continuation.

## 11.3 Materialized derived resource

Use for expensive/valuable derived results that should become immutable/reusable. Body may live in existing CAS/blob storage and be referenced by `PayloadRef`.

---

# 12. Continuation semantics

A continuation cursor belongs to a specific exact revision/snapshot/lease.

```text
first read -> exact revision R12 / snapshot lease L4
next batch -> R12/L4
next batch -> R12/L4
```

Forbidden:

```text
cursor -> read current live source again -> silently return R13 data
```

If snapshot retention expires, return an explicit stale/snapshot-unavailable error.

---

# 13. ResourceStore, CAS and PayloadRef

Reuse existing CAS/blob infrastructure. Do not create a second blob store.

Store large immutable bodies reference-first:

```text
Resource revision/provenance metadata
    -> PayloadRef
        -> CAS immutable bytes
```

`PayloadRef` is runtime/storage identity, not a model-visible authorization token.

Do not repeatedly hash the same body on every UI refresh. Hash at materialization/capture when required by CAS/content identity.

---

# 14. ResourceLease — transient access, not historical identity

Make transient lifetimes explicit. A shared buffer, stream handle, capability token, provider snapshot or temporary materialization must not be conflated with resource history.

Conceptual `ResourceLease` can own:

```text
lease id
resource exact revision
allowed views/bounds
owner session/workspace/runtime
expiry/cleanup
provider snapshot handle if any
```

Closing an HTML workspace may release leases/buffers. It must not delete historical resource lineage merely because the consumer closed.

---

# 15. ResourceEffect and ResourceImpact

Use one typed effect contract as canonical resource-change truth for mutations, restore and detected external drift.

Conceptual envelope:

```csharp
ResourceEffect
{
    EffectId;
    Operation;
    Outcome;
    Impacts[];
    Verification;
}
```

Required outcomes:

```text
VerifiedChanged
VerifiedNoChange
FailedNoEffect
UnknownAfterDispatch
ExternalDriftObserved
Restored
```

`UnknownAfterDispatch` is mandatory: if dispatch may have applied a mutation but acknowledgement was lost, old current evidence cannot safely remain authoritative.

`ResourceImpact` should contain generic facts:

```text
target identity/ref
relation
coverage
before revision
after revision when known
change kind
```

Recommended relations:

```text
exact
intersects
subtree
container-membership
depends-on
catalog-generation
```

Provider/domain matcher owns detailed intersection semantics. No giant stale `switch` in the Gateway.

---

# 16. External drift

URF must not claim to know live state changes it has not observed.

Detection may come from:

```text
provider probe/read
optimistic mutation guard
Office change event
file/package watcher
explicit refresh
```

When detected:

```text
ExternalDriftObserved
before = previously known revision
after = captured new revision OR unknown
```

If `after` is unknown, previously current evidence is not safe to treat as current. Do not add global background polling of every resource.

Optional provider tracking class may describe:

```text
immutable
strongly-tracked
guarded-on-write
lease-based
probe-on-read
```

This is metadata, not another stale architecture.

---

# 17. Restore semantics

Restore never rewrites history or silently rewinds an old revision pointer.

Canonical flow:

```text
R1 -> R2 -> R3
restore R1
-> guarded mutation/publish
-> verify
-> R4
-> RestoredFrom = R1
-> head = R4
```

For mutable resources, use the ordinary mutation path plus verification. For immutable/logical artifact/package publication, publish a new head with provenance.

---

# 18. Derived resources and provenance

Every derived revision must record dependencies sufficient to understand what produced it.

Conceptual dependency:

```text
source exact ResourceRef
view
coverage
kind
```

Example:

```text
D4 mapped production table depends on:
  source Excel range@R7
  schema production.daily.v1@S3
  mapping@M2

D9 monthly summary depends on:
  D4
```

URF need not automatically recompute every dependent resource in this cutover. It must preserve dependency/provenance metadata so higher layers can determine stale state correctly.

---

# 19. Virtual vs materialized derived resources

Support the distinction explicitly.

## Virtual derived resource

Stores definition/provenance and computes bounded output on demand.

## Materialized derived resource

Stores immutable output body in CAS and references it through `PayloadRef`.

Both have their own revision/provenance. Choose materialization based on cost/reuse, not consumer type.

---

# 20. Semantic Schema Registry

Schema is a versioned semantic contract, not a parser trick.

Recommended workflow:

```text
resource.inspect bounded sample/structure
-> model/tool proposes DraftSchema
-> validate against source/sample
-> publish SchemaRevision
-> create ResourceSchemaMapping revision
-> create mapped/derived resource
```

An inferred draft does not become authoritative merely because a model produced it.

Avoid creating a permanent semantic schema for every one-off file. Use a generic structural `table/tree/document/...` view plus inferred field metadata when no reusable business semantics exist.

Schemas and mappings are themselves versioned resources/provenance nodes where practical.

---

# 21. Model-facing resource tools

Expose a small canonical tool surface around the Gateway; reuse existing names where appropriate.

Conceptual operations:

```text
resource.describe / resources_find
resource.read
resource.stream/continue
resource.inspect
schema.create/publish
mapping.create/publish
derived.create
```

Do not expose provider internals or file paths.

Model-facing read result should be bounded and carry semantic metadata such as:

```text
exact resource revision
view
coverage
complete/partial
shape/schema metadata
continuation if any
```

Large body may be externalized in CAS/runtime storage while the current request hydrates only the selected bounded projection.

---

# 22. Large mutation payloads are resource-backed too

Apply the same reference-first principle to large writes:

```text
VBA source replacement
HTML document upsert
large Excel write matrix
tool/skill implementation body
large JSON rewrite
```

Runtime invocation may use:

```text
operation
target exact/head identity
expectedRevision
PayloadRef
semantic model projection
```

Do not permanently accumulate megabyte-scale mutation bodies in assistant/tool-call history.

---

# 23. HTML Workspace after cutover

HTML Workspace stores bindings to canonical resources, not dataset bodies.

Target conceptual binding:

```text
name
ResourceIdentity/exact or head binding policy
requested structural/semantic view
optional schema/mapping ref
presentation metadata only
```

Remove:

```text
HtmlWorkspaceDataSource.Json as bulk body
window.RNAssistantData
accepted.DataJson dependency for large binding
workspace-sized embedded datasets
```

If HTML needs current live data, binding resolves through canonical head semantics. If it needs an exact snapshot, bind an exact revision.

---

# 24. `RN.resources` JS API

Expose one consumer API independent of transport/provider.

Example:

```js
const resource = await RN.resources.open(bindingName);
const descriptor = resource.descriptor;

const batch = await resource.read({
  view: "table",
  offset: 0,
  limit: 5000,
  fields: ["well_id", "date", "oil"]
});
```

Streaming:

```js
for await (const batch of resource.stream({
  view: "table",
  batchRows: 10000,
  fields: ["well_id", "oil"]
})) {
  // incremental processing
}
```

PDF:

```js
const page = await pdf.read({ view: "render-page", page: 5 });
```

JSON:

```js
const records = await json.read({
  view: "records",
  path: "$.production",
  offset: 0,
  limit: 1000
});
```

Application HTML does not know whether transport is a resource response stream, SharedBuffer or future adapter.

---

# 25. WebView2 transport

The WebView bridge is a transport adapter only.

## Control plane

Use small JSON messages for:

```text
open/close
resource refs/descriptors
revision/head change metadata
errors
batch request metadata
capability/lease setup
```

## Data plane

Use an internal WebView2 resource response/stream for large bodies/batches. It is not an external HTTP server.

Conceptual route:

```text
https://rnassistant.local-resource/...opaque...
```

Router:

```text
request
 -> validate capability/lease/session/workspace
 -> resolve canonical ResourceRef
 -> ResourceGateway
 -> provider/store
 -> bounded stream response
```

The router must not contain Excel/PDF/JSON-specific business logic.

SharedBuffer may be an optimization behind the same API after measurement; it is not a separate architecture.

---

# 26. HTML security

Preserve:

```csharp
AreHostObjectsAllowed = false;
```

Do not expose generic .NET/COM objects to HTML.

ResourceRef alone grants no access. HTML receives a scoped capability/lease permitting only specific resources/views/bounds within the current workspace/session.

Internal resource router must reject:

```text
unknown refs
other-workspace refs
path traversal
filesystem paths
arbitrary URLs
unsupported methods
oversized batches
expired capability/lease
```

Do not weaken CSP globally (`connect-src *` is forbidden). Allow only the narrow internal transport path or parent-frame mediated transport.

---

# 27. One freshness truth for all consumers

Do not create an HTML freshness manager.

One canonical resource transition:

```text
R7 -> R8 / ResourceEffect
```

may project as:

```text
HTML: resourceChanged(name, R8)
future LLM evidence: observations on R7 superseded/unknown according to reducer
future Python: cache/lease invalidation
```

Consumer notification formats may differ; resource truth does not.

---

# 28. Skills/tools/packages: storage != authority

URF may store/version skill/tool/prompt bodies as resources, but discoverability is not activation authority.

Keep separate authority snapshots:

```text
SkillCatalogSnapshot
ToolPackSnapshot
```

A historical tool body found through resources does not become callable. A historical skill resource does not become active.

Publishing a new version advances the corresponding catalog/toolpack generation and may affect future model-context evidence, but ResourceGateway itself does not decide activation.

---

# 29. Runtime/model envelope separation

Where protocol permits, maintain a typed distinction between model-visible semantic result and runtime facts.

Conceptual result:

```json
{
  "model": {
    "message": "Resource updated and verified"
  },
  "runtime": {
    "effects": [],
    "resourceRefs": [],
    "payloadRefs": [],
    "guards": {}
  }
}
```

If current protocol cannot adopt this exact DTO without broad rewrite, preserve equivalent typed separation. Do not depend on ad hoc `JObject.Remove(...)` filtering as the only safety boundary.

---

# 30. Python-ready extension point

Do not implement Python runtime in this cutover.

Make it possible for a future Python adapter to consume the same:

```text
ResourceRef / descriptor
capabilities/views
ResourceGateway
PayloadRef / streams
leases/capability checks
head/revision semantics
```

Future Python must not introduce its own file registry/resource identity system.

---

# 31. Canonical error semantics

Normalize meaningful errors, for example:

```text
RESOURCE_NOT_FOUND
RESOURCE_ACCESS_DENIED
RESOURCE_VIEW_UNSUPPORTED
RESOURCE_BATCH_TOO_LARGE
RESOURCE_REVISION_CHANGED
RESOURCE_STALE
RESOURCE_DEPENDENCY_STALE
RESOURCE_SNAPSHOT_UNAVAILABLE
RESOURCE_EFFECT_UNKNOWN
RESOURCE_SOURCE_UNAVAILABLE
RESOURCE_FORMAT_UNSUPPORTED
```

Distinguish:

```text
RESOURCE_REVISION_CHANGED = expected/current revision mismatch
RESOURCE_STALE = logical/current evidence no longer authoritative
RESOURCE_SNAPSHOT_UNAVAILABLE = exact historical snapshot cannot be served
```

Do not use one generic stale/failure code for all cases.

---

# 32. Performance invariants

Architecture must produce these properties by construction:

```text
no full dataset embedded in workspace JSON
no JSON-inside-JSON bulk transport
no base64 binary artifact transport through ordinary WebView messages
no full-body serialization clone for workspace hot paths
no full body hashing on each UI refresh
no live COM probes during context compile
no duplicate CAS body serialization
no background polling of every resource
bounded provider reads
metadata-only resourceChanged
```

Reference-first does not mean “always materialize everything.” Use the three materialization modes from section 11.

---

# 33. Direct cutover implementation — four phases only

Do not introduce separate legacy migration phases.

## PHASE A — Canonical Resource Core

Open first:

```text
existing ResourceRef/resource registry/gateway/provider code
existing CAS/blob/snapshot code
Office/VBA/Excel read/write abstractions
artifact resource code
```

Implement/normalize in one pass:

1. one canonical `ResourceRef`/identity model;
2. explicit logical identity vs exact revision/head semantics;
3. `RevisionId != ContentHash`;
4. descriptor + capability/view negotiation;
5. `ResourceCoverage`;
6. `ResourceLease`/snapshot continuation semantics;
7. narrow `ResourceGateway` + provider registry;
8. reuse existing CAS via `PayloadRef`;
9. `ResourceEffect`/`ResourceImpact` and current-head updates;
10. dependency/provenance metadata for derived resources;
11. remove duplicate resource identity/store abstractions discovered in touched contours.

**Acceptance gate A:** project builds; exact historical revision remains distinct after head change; restore can create a new revision; a partial read returns exact revision+view+coverage; a continuation cannot silently mix revisions; effect can represent changed/no-op/unknown-after-dispatch.

## PHASE B — HTML/artifact/bulk transport cutover

Open first:

```text
src/RNAssistant.Office/Tools/HtmlWorkspaceToolService*.cs
src/RNAssistant.Office/Tools/HtmlAcceptedReadSourceResolver.cs
src/RNAssistant.Office/WebView/AssistantWebBridge.cs
src/RNAssistant.Office/WebView/AssistantPaneControl.cs
web/js/app-html-workspace-preview.js
artifact viewer/storage paths
```

Implement in one pass:

1. HTML workspace stores resource bindings only;
2. `html_data_bind` binds canonical resource identity/revision/schema mapping;
3. remove `accepted.DataJson` as bulk dependency;
4. add internal WebView resource router -> Gateway;
5. add `RN.resources` API;
6. use bounded batches/streams;
7. move image/PDF/artifact large binary delivery to the same data-plane transport;
8. keep `AreHostObjectsAllowed=false` and scoped capability/lease security;
9. drive `resourceChanged` from canonical head/effect state;
10. delete old JSON/base64 bulk paths in touched contours.

**Acceptance gate B:** a large Excel table can power HTML without entering workspace JSON; PDF/image bytes use resource transport; resource change requires metadata notification only; old `window.RNAssistantData`/bulk `Json` path is absent.

## PHASE C — Schema/mapping/derived workflow

Implement:

1. versioned semantic schema registry;
2. draft -> validate -> publish schema flow;
3. versioned `ResourceSchemaMapping`;
4. bounded inspect path;
5. mapped/derived resource creation with dependencies on exact source+schema+mapping revisions;
6. virtual vs materialized derived resources;
7. HTML uses semantic fields through the same `RN.resources` API;
8. update tool descriptors/skills for the new workflow.

**Acceptance gate C:** a new Excel template can be inspected, mapped to a published semantic schema and consumed by HTML without hard-coded sheet/column knowledge in the dashboard; changing schema/mapping creates new provenance/revision rather than silently reusing stale semantic output.

## PHASE D — Cleanup + context handoff

Perform one repository search and remove obsolete concepts in the affected architecture:

```text
HtmlDataRef / HtmlDataStore if introduced earlier
HtmlWorkspaceDataSource.Json bulk use
window.RNAssistantData
accepted.DataJson bulk binding
base64 artifact-to-WebView body transport
duplicate ResourceKey/ResourceRef variants
duplicate stale/freshness manager in HTML
```

Then:

1. update system prompt with only short resource invariants;
2. update HTML/resource/schema skills with canonical examples;
3. update tool schemas/descriptions;
4. document the exact handoff contract consumed by the Evidence/ModelContext document;
5. leave only a Python adapter extension point, no Python engine implementation.

**Acceptance gate D:** no touched legacy resource/binding path remains reachable; providers/gateway do not depend on model context; SkillCatalog/ToolPack remain separate authority; the handoff contract below is fully available.

---

# 34. Required handoff contract to Model Context layer

After this document is implemented, the next architecture may assume these primitives exist and must not redefine them:

```text
1. canonical ResourceIdentity/ResourceRef semantics;
2. exact immutable revision/snapshot semantics;
3. current-head semantics for mutable/logical resources;
4. RevisionId distinct from PayloadRef/ContentHash;
5. ResourceDescriptor with type/capabilities/views/schema metadata;
6. ResourceGateway as generic read boundary;
7. ResourceCoverage;
8. ResourceLease/snapshot continuation semantics;
9. PayloadRef/CAS for large immutable bodies;
10. ResourceEffect + ResourceImpact;
11. ResourceDependency/provenance;
12. versioned semantic schemas and mappings;
13. exact read runtime metadata: revision + view + coverage;
14. restore provenance without history rewrite;
15. separate ToolPack/SkillCatalog authority generations.
```

URF does not implement `EvidenceStateReducer`, `ModelContextCompiler`, prompt budget or dialogue compaction.

---

# 35. Minimal checks only

Do not create a large new test project. Add/adjust focused checks around changed contracts and compile/build affected projects.

Mandatory focused scenarios:

```text
exact vs head read
restore creates new revision
unknown-after-dispatch effect representable
continuation revision consistency
large HTML table via bounded resource transport
PDF/image resource delivery
foreign/expired HTML capability rejected
schema+mapping provenance
cleanup of temporary leases
```

Do not spend a separate phase on broad performance benchmark infrastructure. Existing/manual real-machine validation may follow after cutover.

---

# 36. Definition of Done

URF cutover is complete only when all are true:

- [ ] there is one canonical resource identity/ref model;
- [ ] head and exact revision semantics are unambiguous;
- [ ] `RevisionId` is not conflated with content hash;
- [ ] historical revisions do not mutate after head changes;
- [ ] restore creates a new revision/effect;
- [ ] partial reads carry `view + coverage + exact revision`;
- [ ] exact continuations cannot mix live revisions;
- [ ] providers are format-specific; Gateway stays format-agnostic;
- [ ] live bounded, snapshot-backed and materialized modes are supported conceptually/where needed;
- [ ] large bodies can live behind `PayloadRef` in existing CAS;
- [ ] transient access uses explicit lease/capability lifetime;
- [ ] one typed resource effect state drives consumer freshness projections;
- [ ] derived resources record exact dependencies/provenance;
- [ ] schemas and mappings are versioned and separate;
- [ ] HTML workspace contains bindings, not bulk datasets;
- [ ] HTML uses `RN.resources`, not `window.RNAssistantData`;
- [ ] WebView large transport routes through Gateway and not directly to Excel COM;
- [ ] generic host objects remain disabled;
- [ ] PDF/image/artifact bulk bytes use the same resource data plane;
- [ ] ResourceRef is never treated as authorization;
- [ ] ToolPack/SkillCatalog remain separate authority snapshots;
- [ ] providers/Gateway do not know about model-context residency;
- [ ] old affected bulk/resource legacy paths are deleted, not preserved;
- [ ] the handoff contract in section 34 is ready for the next document.

---

# 37. Final report format for coding agent

Return only a concise implementation report:

```text
Implemented
- canonical resource identity/head/revision changes
- providers/gateway/views/coverage
- payload/CAS/lease behavior
- effects/provenance
- HTML/resource transport
- schema/mapping/derived workflow

Removed legacy
- ...

Existing components reused
- ...

Files changed
- ...

Focused checks
- ...

Architecture sanity
- one ResourceRef: yes/no
- RevisionId != ContentHash: yes/no
- one freshness truth: yes/no
- HTML->Gateway->provider: yes/no
- no bulk workspace JSON: yes/no
- context handoff ready: yes/no

Remaining limitations
- only real limitations, no speculative backlog
```
