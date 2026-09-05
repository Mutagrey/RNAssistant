# Resource Fabric

Status: unified direct cutover **in progress**, host-neutral. Execution order and
acceptance gates belong to [Resource MASTER](stabilization/resource-cutover/MASTER.md).
Read its normative documents in order: [URF](stabilization/resource-cutover/UNIVERSAL_RESOURCE_FABRIC.md),
[Authority](stabilization/resource-cutover/RESOURCE_AUTHORITY.md), then
[Evidence/Compiler](stabilization/resource-cutover/EVIDENCE_CONTEXT_COMPILER.md).
This file maps implementation owners, not a fourth normative architecture.
Earlier R61 whole-read/accepted-tool-result binding explanations are superseded.

## Goals

One resource identity, shared current-state authority, immutable historical evidence
and one model-context compiler serve model reads, HTML and viewers. Reading content
never admits a tool schema, activates a stored package or authorizes a mutation.
Domain owners retain typed guard/dispatch/read-back responsibilities.

## Domain model

`ResourceIdentity` is logical identity; `ResourceRef` adds an exact revision.
`DocumentAuthorityId`, runtime binding and physical locator are separate identities.
A revision ID is not a content hash. Restoring equal bytes creates new lineage
with Parent/RestoredFrom while the existing CAS deduplicates bytes.
VBA journal preparations record the exact known preimage resource when proven.
Restore pins that origin before confirmation, or pins the independently captured
backup resource itself. Equal hashes never select a historical origin.
Unknown heads never advertise an old revision as current.

Descriptors separate physical type, capabilities, views, coverage, schema/mapping
refs and dependencies. `ResourceCoverage` qualifies the observation; a transport
batch is not a complete source. Core contracts live in `Resource*Models.cs` and
`ModelContextModels.cs`. `PayloadRef` is durable; `ResourceLease` is transient access.

## Providers

`ResourceGatewayService` and `ResourceProviderRegistry` own generic routing and
bounded reads. Registered owners are chat artifacts/attachments, bound Office
document/VBA, Excel ranges/formulas, conversation state/definitions, typed context
and catalogs. `ContextResourceProvider` discovers attached supplied data in the
conversation scope and Office observations in the exact bound document scope.
Instructions/untyped notes are not resources; display previews never supply bodies.
`ResourceSnapshotReadService` reads state/context and retained Office views from
whole captured views or the canonical revision payload. Partial views cannot substitute for whole bodies;
continuations bind logical revision and URI/view, never an equal content hash.
Exact historical reads retain access after drift/removal without moving heads.
Gateway Office continuations require an exact logical reference and reject
cross-revision, URI/view or old hash-bound tokens before provider dispatch.
`ResourceAuthorityService` translates the logical token to the retained physical
view guard only inside dispatch, validates the provider continuation and returns
a logical token. It owns no second cursor/head store. Available exact Office views
use the common snapshot reader even for the last known head; fresh head reads still
observe the provider. Missing/corrupt retained CAS never falls back to live bytes.
An identity with only prepared metadata cannot be activated by reading it; copied
context becomes readable after the owning atomic fork publication.
`ResourceGatewayService.Binary` captures CAS image/thumbnail/PDF-page views using
the existing renderer owners. Providers retain interpretation/materialization.

Model discovery/read uses `common.resources_find/read` with runtime-resolved
semantic targets and exact internal references/continuations. A mutable semantic
target captures its current head on the first read, then pins all internal pages
to that exact revision; it cannot get stuck on discovery's previous observation.
The `catalogs`
scope discovers definitions without execution admission. `CatalogResourceProvider`
serves committed metadata, exact skill bodies and reference Markdown, including
historical publications. Remaining domain-specific read consumers are tracked below.

## Conversation loop

Controller clear/edit/message-delete/fork use typed `ChatResourceMutationIntent`
through the existing mutation observer/journal. `ChatHistoryEditService` and
`ChatCloneService` own history/resource preparation; conversation state is durable
before workspace/plan/task/membership heads become visible in one commit. Clear
removes active conversation definitions too, retaining exact revisions/CAS. Edit
publishes a new restore revision only for changed logical state. Failed publication
blocks fresh captures; recovery records Unknown and never replays the command.
Fork keeps the live document authority and creates child conversation resources.
Copied artifact bindings are rebased into a new workspace snapshot, not rewritten
inside old immutable bodies. Missing exact checkpoints fail closed.
`ResourceForkService` prepares the bounded dependency graph for schema/mapping/
virtual and materialized derived resources plus supplied context. Required copy
preparation retains exact CAS/revision metadata without publishing heads. One fork
commit publishes all selected definitions and workspace state after persistence.
`ResourceCopyLink` facts in conversation events preserve exact copy provenance and
source publication order, including nested forks and multiple retained revisions;
they never become a current-head store or an implicit cross-chat read alias.
Only deliberately copied artifacts and references are rebound. Materialized data
reuses its CAS body; typed definitions get new bodies when their internal refs change.
Cycles, missing/unpublished dependencies and size/depth bounds fail before publication.

`ConversationRunService` → `AgentKernel` remains the only lifecycle/outcome loop.
`ConversationKernelAdapter` captures published catalogs at request boundaries.
`ConversationModelSession` freezes history/high-water, resource authority, tool pack,
skills and schemas before `ModelContextCompiler`. Normal requests, protocol repair,
compaction and Inspector use that compiler; compilation does not consult COM,
mutable files/catalogs or another chat's transcript.

`EvidenceStateReducer` classifies Current/Superseded/Unknown/Unavailable against
frozen authority. Correctness filtering, terminal-write collapse and deduplication
precede budget selection and CAS hydration. `ContextReceipt` records generations,
exclusions and hydration. Compaction retains structured source-grounded claims;
free summary prose cannot resurrect superseded resource bodies.

`ContextNote.Role` distinguishes explicit user instructions, supplied data and
bound Office observations. Instructions use an exact `InstructionPayload`; data
and observations use `ResourceEvidence.Payload`. The compiler hydrates these only
after correctness filtering, never from mutable `Text`/`Preview`. Those fields are
bounded UI previews. Draft skill/tool/prompt attachments remain data, not activation
authority. Untyped old notes require explicit reattachment; normalization cannot
infer their role. Upsert and fork preserve the typed role and exact payload/evidence.

## Ingestion and derived data

Existing ingestion promotes chat drafts before model dispatch. Immutable bodies
reuse `ChatBlobStore` CAS, without a second resource payload store.
`ResourceStructuredViewService` stores bounded indexes/parts for record/table
views; ambiguous JSON paths/properties and unsafe integer precision fail explicitly.
Excel snapshot bounds remain explicit.

`ResourceDefinitionToolHandler` owns draft/validated publication. Only exact
published schema heads enter `SchemaRegistrySnapshot`. Mappings pin source/schema
revisions. `ResourceDerivedViewService` preserves transitive dependencies for
virtual/materialized views; drafts have no authority before publication.

## Context and storage

`ResourceAuthorityStore` is the shared append-only head/effect/revision/view
authority, scoped to document, conversation or catalog. One scoped commit atomically
publishes all affected heads, one effect and generation. Cross-window catch-up reads
journal tails; the rebuildable in-memory scope cache is bounded. Known heads require
durable exact revision metadata before publication.

`ResourceMutationJournal` records Prepared and possible dispatch before invocation.
`ResourceMutationAuthorityObserver` durably captures verified read-back before
terminal publication and the tool-result event. Uncaptured affected views become
Unknown. Interrupted attempts are never automatically replayed/restored.
HostRuntime/document serialization covers guard through read-back, not model/user waits.

Session events use schema 4; incompatible streams require explicit new/reset without
deleting user history or fallback. Large accepted arguments, activity/results and
pending execution payloads use CAS refs. Registered revisions, views and their parts
remain retention roots even before the first head commit. Cold replay/checkpoints
and bounded retention optimization remain open; current retention is conservative.

## Domain projections and UI

HTML bindings hold ResourceRef, head/exact policy, view/path and optional schema/
mapping refs, never current dataset JSON or executable tool calls. Head policy is
resolved on open; existing handles remain exact. Static JSON is a chat resource.

`RN.resources` opens named capabilities, reads bounded batches/streams and closes
them. `ResourceDataPlaneService`/`ResourceDataRouter` serve the internal
`https://rnassistant.local-resource/v1/<opaque-lease>` WebView route, not a server.
Access is owner-scoped, exact, sequential and cancellable: one read per handle,
four opens, 64 leases and ten-minute expiry bound work.

Text/Markdown/PDF text pages, images, thumbnails and PDF renders travel through
that data plane. Typed bridge DTOs carry metadata/leases, not page text or base64.
Text leases close after each bounded page; media leases close on replacement,
eviction and chat change, including late responses. Full text is an explicit
bounded user action, not an eager workspace cache.

Authority notifications coalesce to bounded scope/generation metadata.
`RN.resources.subscribe` receives authorized binding names only; it does not create
a pushed dataset or another freshness cache. Notifications currently reach
in-process windows; other processes catch up on fresh shared authority capture.

`HtmlWorkspaceExportService` prepares export leases through the same data plane.
After capture, all head bindings must match one frozen authority tuple; explicitly
historical bindings stay exact. Head opens and the final export set use the common
`EvidenceStateReducer` against resource and dependency scopes. Matching the derived
head alone cannot pass when its schema/mapping/source has changed or is unknown.
Exact historical views remain readable. Export never changes binding policy or creates a
second head/body store. The UI pulls the complete export in bounded sequential
batches: at most 32 bindings, 1024 parts, 32 MiB of transport bytes and 8 MiB per
part. Failure, cancellation/chat change or mixed revisions prevent download and
release prepared leases. No bulk body crosses the ordinary control bridge.

The standalone HTML contains inert, integrity-checked snapshot parts plus exact
resource/coverage metadata. The same `RN.resources` handles/streams use a read-only
snapshot transport; parts hydrate only when read, with field/row/character bounds,
expiry and close/backpressure. Text/source, table/records and negotiated binary
views are supported. There is no live host/network fallback or head subscription.
Integrity checks require Web Crypto; missing/tampered parts fail explicitly.
ECharts remains the pinned local dependency. These host-neutral contracts do not
qualify actual Windows WebView2 capture or downloaded-file browser behavior.

## Audit decisions

The three normative contracts supersede conflicting pre-cutover area explanations.
[PROGRESS](stabilization/PROGRESS.md) records current work/Windows gates and
[MIGRATION_MAP](stabilization/MIGRATION_MAP.md) records remaining consumer removal.
Existing Office adapters, mutation journal, event store and CAS remain canonical
owners, not parallel legacy/new paths.

## Removed architecture

Removed in touched contours: per-chat VBA observation/refresh hash dictionaries and
callbacks; `VbaToolExecutor.Observations`; `HtmlAcceptedReadSourceResolver`; HTML
binding tool IDs/arguments/transforms/current JSON; independent model history/repair
assembly; free-summary resource authority; inline large pending payload duplication;
viewer text/base64 bridge transport; mutable-disk skill reference activation;
separate state/Office retained text readers, externally hash-bound Office/state
continuations, and read-side activation of prepared state/context identities.
No compatibility alias, dual-write or feature flag restores these paths.

Still open within this same cutover: catalog text logical-continuation normalization,
remaining definition/domain read consumers, finer
Excel coverage/named resources, complete binary/raw view negotiation,
remaining bulk upload/export surfaces, bounded history/retention
optimization and final documentation cleanup. These are not permanent adapters.

## Delivery order

MASTER waves are one dependency-ordered implementation: shared foundation →
mutation/evidence → frozen compiler → reference-first HTML/viewers → schema/derived/
catalog/retention cleanup. Host-neutral checks do not close real Windows x64 +
Office x64 + VS 2022/WebView2 qualification, Phase 12 or any release gate.
