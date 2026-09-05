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
An exact revision with only prepared metadata cannot borrow another revision's head
or cached index to become readable; copied context becomes readable after the
owning atomic fork publication.
`ResourceGatewayService.Binary` captures CAS image/thumbnail/PDF-page views using
the existing renderer owners. Providers retain interpretation/materialization.

VBA component source capture now retains the complete bounded source already read
under the document gate in the existing CAS. A returned page still has partial
coverage; the retained whole view enables later exact reads without another COM
read, even after external drift. New unpinned reads still capture live source and
publish its current logical revision. Truncated captures never acquire whole-view
coverage, and corrupt retained bytes never fall back to current Office content.
`VbaEditorResourceService` uses this same Gateway capture and shared download data
plane: `getVbaModule` carries typed metadata and an exact resource reference, not
source code inside `ToolRunResult.DataJson`. The editor only enables complete,
integrity-checked code and keeps the normalized VBA write guard separate from the
raw CAS/transport SHA-256. See [VBA journal](vba-mutation-journal.md#editor-source-reads).

Model discovery/read uses `common.resources_find/read` with runtime-resolved
semantic targets and exact internal references/continuations. A mutable semantic
target captures its current head on the first read, then pins all internal pages
to that exact revision; it cannot get stuck on discovery's previous observation.
The `catalogs` scope discovers definitions without execution admission.
`CatalogResourceProvider` serves committed metadata, exact skill bodies and reference
Markdown, including historical publications. Its text continuations bind publication
revision plus URI/view through the shared exact cursor rules, not payload hashes;
equal-byte publications/restores remain distinct. Capability reference `action=next`
reconstructs the same logical cursor from durable evidence. Catalog children have
no synthetic heads: the common reducer checks their immutable identity and exact
root-publication dependency. Open leases remain pinned after publication changes.
`CatalogPublicationService` proves visibility from canonical authority commits,
never prepared metadata alone; missing/corrupt CAS fails with
`RESOURCE_SNAPSHOT_UNAVAILABLE`. Historical reads neither activate a generation nor
heal Unknown authority. Each member read hydrates its root once; public root
projection remains in the catalog owner. Remaining domain-specific read consumers
are tracked below.

`SkillEditorResourceService` uses that published catalog and exact Gateway/CAS
source for Library core/reference editing, including read-only built-ins.
Catalog DTOs contain body metadata only; opening a source pulls its text through
`readSkillSource`. This one read action carries metadata and
a shared bounded download lease, not Markdown or a whole package. It reserves
capacity before hydration and requires the displayed package revision; no authoring
file fallback or model observation is created. Complete-source/read-only, cache
conflict and existing save-guard rules belong to
[Skill Library](skills.md#editor-source-reads). Metadata-only edits explicitly
preserve the guarded body without fetching it. Skill mutation body transport
still requires consumer cutover.

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
Fork preparation and retained reads use the same authority-owned publication proof:
copy provenance is eligible only after the verified fork commit, including historical
copies that were not selected as heads. Preparation caches only its bounded graph's
publication order, not an independently rebuilt publication history.
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
Structured/virtual continuations use shared exact-reference guards before source
capture, index/definition CAS hydration or implicit artifact identity resolution.
Their binding pins URI, view and the requested projection: JSON path and ordered
field names remain case-sensitive; omitted/empty fields both mean all fields.
The first head read rebinds outgoing cursors to its resolved exact address.
Collection-style guards and lowercased projection bindings are removed without
compatibility tokens. Excel snapshot bounds remain explicit.

`ResourceDefinitionToolHandler` owns draft/validated publication. Only exact
published schema heads enter `SchemaRegistrySnapshot`. Mappings pin source/schema
revisions. `ResourceDerivedViewService` preserves transitive dependencies for
virtual/materialized views; drafts have no authority before publication. Virtual
tables expose only the root record projection and reject unsupported paths.
`ResourceAuthorityService.RequirePublished` guards retained text, structural indexes,
virtual definitions and catalog roots against prepared-revision exposure. Committed
history remains readable after head changes without publishing or healing authority.
The shared retained reader also owns typed CAS read failures for those definitions,
indexes, parts and catalog references. Domain owners keep their size/shape bounds;
missing/corrupt payloads return `RESOURCE_SNAPSHOT_UNAVAILABLE`, never current-source
replacement or an invented empty body.

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

Attachment ingestion uses the same router at `/v1/upload/<opaque-lease>`.
`beginChatResourceUpload`, `completeChatResourceUpload` and cancellation carry only
typed metadata/capabilities. Binary POST chunks have an exact acknowledged byte
offset and count, at most 256 KiB each; CORS preflight admits only POST/Content-Type
from the opaque local origin. There is one in-flight operation per upload, at most
four uploads within the shared 50 MiB transfer-buffer and 64-lease budgets.
The existing 20 MiB/file and 50 MiB/message limits remain. Ten-minute expiry is
checked on access and swept periodically; close/dispose releases idle buffers.
A cancelled busy lease retains its reservation until the operation actually exits.

Incomplete, malformed or cancelled uploads cannot create a resource publication.
Completion consumes the capability and stages managed bytes through the existing
`ChatResourceIngestionService`/`AttachmentStore`; CAS promotion and message resource
linking still happen only at send. Known late drafts are discarded, uncertain
chunks/completion are not replayed. The UI awaits staging in the addressed chat,
even if another chat becomes active. Extraction runs off the UI thread; the bounded
WebView request stream is consumed on its STA. No base64 staging route/adapter or
second durable upload store remains. Real Windows POST/preflight/close-during-PDF
qualification is still open.

VBA editor save/create now uses the same upload route, byte reservations and shared
sequential browser uploader. Its capability is scoped to the addressed chat and
`vba-editor` consumer, distinct from attachment staging. `VbaEditorResourceService`
consumes verified complete UTF-8 source once; only the existing guarded mutation
owner can then write/publish live VBA. Control DTOs contain upload identity and raw
source hash, never source code; save also requires its normalized editor read guard.
There is no attachment/publication side effect from uploading alone. Bounds and
cancel/late-response semantics are in [VBA journal](vba-mutation-journal.md#editor-source-uploads).

Trajectory ZIP export uses the same owner-scoped data plane at
`/v1/download/<opaque-lease>`, with metadata-only bridge setup, sequential 256 KiB
GET chunks and full-payload SHA-256 verification before download. At most two
downloads share capture/lease limits and the 50 MiB transfer budget with uploads;
reservation precedes source validation and production. The ZIP remains a transient
projection of one validated event snapshot, not a synthetic published resource or
second store. Redaction, bounds and lifetime are owned by
[Trajectory export](trajectory-export.md). General binary/raw resource-view
negotiation and bounded cold replay remain separate open gates.

Diagnostic event payload previews also use these download slots and the same
sequential reader. `TrajectoryPayloadService` resolves the exact event in a complete
validated journal, and the existing CAS codec verifies the whole bounded source
while retaining only a prefix. Source and preview hashes remain separate; bridge
setup contains no text. Both raw diagnostics and Run Journal use one cancellable
UI consumer, with no new publication/store. Bounds, UTF-8 handling and remaining
qualification gates are owned by [Trajectory query](trajectory-query.md#payload-preview-delivery).

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
viewer text/base64 bridge transport; attachment base64 staging bridge and decoder;
trajectory ZIP base64 bridge body and decoder;
diagnostic event payload inline bridge text and whole-body-then-clip preview reads;
direct VBA editor module reads, controller JSON parsing and inline source bridge
body (now the same Gateway/CAS snapshot and bounded download owner);
inline VBA save/create code DTOs and the attachment-only browser chunk loop
(now shared bounded upload transport with separate consumer capabilities);
uploaded-HTML source bridge/DTO and independent preview cache, plus direct
attachment-text import reads (now exact Gateway pages through the same owners);
mutable-disk skill reference activation;
direct Skill Library reference reads and their inline body/fake mutation-result
bridge response (now published catalog → Gateway/CAS → shared bounded download);
inline SkillPackageDto core text in catalog/mutation projections and the separate
reference-only bridge action (now one pull-based skill source reader);
separate state/Office retained text readers, externally hash-bound Office/state/catalog
continuations, and read-side exposure of prepared state/context/catalog identities.
No compatibility alias, dual-write or feature flag restores these paths.

Still open within this same cutover: remaining definition/domain read consumers, finer
Excel coverage/named resources, complete binary/raw view negotiation,
remaining bulk upload/export surfaces, bounded history/retention
optimization and final documentation cleanup. These are not permanent adapters.

## Delivery order

MASTER waves are one dependency-ordered implementation: shared foundation →
mutation/evidence → frozen compiler → reference-first HTML/viewers → schema/derived/
catalog/retention cleanup. Host-neutral checks do not close real Windows x64 +
Office x64 + VS 2022/WebView2 qualification, Phase 12 or any release gate.
