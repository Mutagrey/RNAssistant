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
Equal normalized guards do not certify equal immutable bytes: a fresh complete
capture with changed line endings receives a new logical revision. Repeated exact
bytes retain that revision; historical LF/CRLF snapshots are never rewritten.

Model discovery/read uses `common.resources_find/read` with runtime-resolved
semantic targets and exact internal references/continuations. Live document/VBA
search providers return typed, non-serialized scan captures independently of matches.
Gateway publishes each captured view through the shared authority before binding
bounded match evidence to that exact logical revision. Zero-match scans therefore
publish observed drift too. Complete captures stay in the existing CAS for historical
reads; bounded VBA prefixes retain only character-range coverage. Backup searches
can retain an already-materialized whole body while honestly reporting a truncated
search prefix. Metadata-only discovery neither reads nor publishes source bodies;
it may advertise an existing authority head, never a provider hash as a revision.
Discovery consumes ordinary list continuations before claiming completeness.
`Truncated=true` on a terminal provider page remains incomplete source coverage,
even when a query leaves only one or zero matches. A later complete provider cannot
erase that flag. `complete=false` and `refineQuery=true` report bounded coverage;
`partial`/`unavailableScopes` retain their separate provider-availability meaning.
`empty=true` requires complete enumeration/search, not just zero observed matches.
The generic semantic-target resolver refuses incomplete enumeration with
`resource_scope_incomplete` rather than assuming the observed name is unique.
Explicit domain-owned target resolution and already pinned exact reads are unchanged.
A mutable semantic
target captures its current head on the first read, then pins all internal pages
to that exact revision; it cannot get stuck on discovery's previous observation.
The `catalogs` scope discovers definitions without execution admission.
`CatalogResourceProvider` serves committed metadata, exact skill bodies/reference
Markdown and tool-source JSON children, including historical publications. Its text continuations bind publication
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

Model source inspection now discovers host-visible `tool source` targets through
`common.resources_find` and reads those same catalog children through
`common.resources_read`. It records publication-dependent resource evidence, not
callable admission. The direct authoring-file reader is removed; exact snapshots
and generic bounds are shared with the Library. See
[Model source reads](tool-library.md#model-source-reads).

Prompt inspection uses the same provider: metadata-only keys lead to exact field
children of `prompts` and the source-owned `prompt-defaults` publication. The latter
is captured once at builtin publication, not regenerated on read. Bounded individual
values carry their exact root dependency; current-settings drift cannot replace
published text and reads cannot activate/reset defaults. The direct model settings
reader is removed. Bounds, fields and save authority belong to
[Published prompt inspection](conversation-protocol.md#published-prompt-inspection).

`PromptEditorResourceService` reuses those exact field snapshots for selected UI
source reads via shared downloads. Initialization/settings DTOs expose only controls
and prompt refs; dirty field batches use the same single-use upload service before
the existing guarded settings writer/catalog commit barrier. There is no inline
settings-body fallback or second publication owner. Bounds, stale-draft preservation,
explicit reset/review and cancellation belong to
[Prompt editor transport](conversation-protocol.md#prompt-editor-transport).

`SkillEditorResourceService` uses that published catalog and exact Gateway/CAS
source for Library core/reference editing, including read-only built-ins.
Catalog DTOs contain body metadata only; opening a source pulls its text through
`readSkillSource`. This one read action carries metadata and
a shared bounded download lease, not Markdown or a whole package. It reserves
capacity before hydration and requires the displayed package revision; no authoring
file fallback or model observation is created. Complete-source/read-only, cache
conflict and existing save-guard rules belong to
[Skill Library](skills.md#editor-source-reads). Metadata-only edits explicitly
preserve the guarded body without fetching it. The same owner consumes core batches
and reference upserts as bounded typed UTF-8 JSON through the existing single-use
upload route. Bridge write controls carry only chat/lease/hash; structural/body
validation precedes the existing sequential guarded authoring/commit path.
No upload publishes a resource or creates model evidence. Limits, cancellation,
partial outcomes and draft rules belong to
[Skill mutation uploads](skills.md#editor-mutation-uploads).

`ToolEditorResourceService` reads metadata-addressed Library source through the
same Gateway/CAS and shared bounded download. Custom and source-owned built-in
tool children depend on existing catalog publications; document-local tools prove
their exact live VBA component refs, with no second document catalog authority.
All outgoing Library DTOs omit source bodies. Hydration, draft conflicts and
selected-source cache rules belong to [Tool source reads](tool-library.md#library-source-reads).
Generated builtin human documentation now shares this owner: publication captures
source-owned policy-derived Markdown into CAS parts, and exact `/documentation`
children are read without Office access. UI controls carry only metadata and a
bounded download; generated bodies never enter callable registrations or compact
catalog projections. Bounds, comparison-only verification, cache and cancellation
rules belong to [Tool documentation](tool-library.md#human-documentation-without-model-context-cost).
The same owner consumes the single-use upload route:
ordinary Save and save-before-VBA-install send chat/lease/hash-only
controls and one bounded typed batch. Existing Tool authoring, catalog commit and
document access owners retain their responsibilities. No upload grants execution
or publication authority. Bounds and verified-prefix/draft rules are owned by
[Tool Library](tool-library.md#library-mutation-uploads).

## Excel range reads

`excel.read_range` is removed from catalog, native bindings and the Excel core pack.
Model/manual callers use `common.resources_read` with an explicit semantic target
such as `Excel range: Data!A1:B4`: `text` returns values, `formulas` formulas,
`structure` the existing domain-owned profile, and `table`/`records` bounded rows.
Discover used ranges through `common.resources_find`; omitted active-sheet/selection
defaults of the removed tool are not silently translated. `excel.inspect` remains
the bounded workbook-object metadata action.

`ExcelResourceProvider` is the only range-read entry over `ExcelReadService` and the
bound backend. The 100000-cell ceiling precedes values/formulas materialization.
The profile keeps existing counts, headers and ten-row sample; it is not a complete
values/formulas representation. Each requested view records exact resource evidence
and complete captured bytes in the existing CAS. Internal text continuations stay
on the first logical revision; retained historical profile reads perform no Office
I/O. Drift observed in a fresh view uses shared authority and evidence reduction.
HTML bindings use the same Gateway/provider, not accepted direct-tool JSON.
Old accepted `excel.read_range` calls fail explicit protocol validation; no alias,
replay translation or public domain-output wrapper remains. Real Windows Excel/STA
and final catalog/model qualification are still open.

### Named Excel tables

`Excel table: Sales` is discovered and read through the same generic tools and
`ExcelResourceProvider`, not a separate table reader. The `excel-table` kind uses
the bound document token plus case-normalized table name as its logical identity;
moving or resizing the table does not turn a binding into a fixed A1 range.
The existing typed `CaptureStructure("tables")` supplies metadata only. Named
resolution requires a complete bounded catalog (up to 200 entries), one matching
name and a local A1 rectangle. Ambiguous/missing names, incomplete catalogs and
invalid extents fail explicitly; the 100000-cell bound precedes range capture.

`text` and `formulas` retain the existing typed range snapshot, including sheet,
address and dimensions. Those coordinates are part of the captured bytes, so a
move with unchanged cell values still supersedes old evidence. `structure` keeps
the existing profile. `table`/`records` use explicit path `$.values`, including the
full table range (headers/totals when present), with shared row coverage and CAS
parts. No second JSON store or fallback to the former address is introduced.
Historical exact reads/projections need no table lookup or Office I/O; missing CAS
does not read the current table. Native bound-STA/closed-workbook behavior is checked
with fakes; real Windows ListObject/model/WebView2 qualification remains open.

### Excel Defined Names

`Excel name: Sales` and `Excel name: Data!Sales` use the existing Excel provider
and generic discovery/read tools. The `excel-defined-name` identity is the bound
document token plus the case-normalized exact name reported by Excel; sheet
qualification is preserved, never guessed or aliased to a workbook-scoped name.
Resolution requires one match in a complete catalog of at most 200 names.

The typed backend classifies `RefersToRange`: only `BoundRange` (one area whose
worksheet belongs to the exact bound workbook runtime) permits cell capture.
`Unresolved`, `ForeignRange` and `MultipleAreas` expose metadata only, even if a
snapshot contains sheet/address fields. Constants, unresolved formulas and external
references are not evaluated by the provider or redirected to a local sheet.
Dynamic definitions use their observed single bound extent when available. Local
A1 validation and the 100000-cell bound precede capture; unsupported/full-column
extents remain discoverable as metadata but fail an unsupported/oversized body read.

`metadata` retains the full definition; discovery carries only a bounded preview.
`text`/`formulas` capture the definition and typed range snapshot together;
`structure` wraps the existing domain profile with the same definition.
`table`/`records` use `$.range.values`. Definition changes or extent changes advance
the revision even when cell values remain equal. Exact retained reads/projections
use shared CAS without resolving the live name, including after its removal;
missing CAS never falls forward. Fresh observations are not continuous monitoring.
Host-neutral tests cover this routing and bound-STA/closed-session refusal. Real
Windows name resolution, external/dynamic/multi-area COM behavior and WebView2
qualification remain open, as does finer Excel impact/coverage qualification.

### Excel search

`excel.find_cells` captures through `ExcelSearchResourceService` → the existing
Excel provider/Gateway/CAS, then invokes pure `ExcelFindReplaceService.Find` over
the exact cell snapshot. The direct adapter search method and public scope/content
hashes are removed. Matches retain sheet/cell/field coordinates and bounded previews,
not complete value/formula copies; full cell data uses the resource readers.
Literal/regex, case/whole-word and values/formulas/both semantics remain unchanged.

`Excel search scope: workbook`, `selection`, `sheet 'Name'` and
`range 'Name'!A1:B10` expose exact text JSON with scope and captured cell fields.
Workbook, selection and named-sheet scopes are body-free discoverable; explicit
range scopes can also be discovered/resolved. Apostrophes in sheet names are doubled.
Omitting a sheet retains the existing bound active-sheet behavior; omitted tool
scope still infers range from address, sheet from sheet name, otherwise workbook.
Named/multi-area range interpretation remains with the bound Excel backend.

Capture admits at most 100,000 cells and one million aggregate field characters;
serialized JSON is independently capped at one million characters. Native range
cell counts are checked before cell materialization. Invalid/duplicate cells and
oversize sources fail explicitly, never produce a prefix-as-complete snapshot.
Invalid regex is refused before capture. Positive, zero-match and blank-cell results
retain complete exact evidence; drift and replacement invalidate previous evidence.
Historical pages do no Excel I/O, and missing CAS never falls forward. Search scopes
share the existing provider's exact paging owner, not a second store or transport.
Replacement preparation/read-back stays with its existing domain owner. Windows COM
selection/named-range/count limits, real model and WebView qualification remain open.

## Word text reads

`word.read_text` is removed. Document and selection targets discovered through
`common.resources_find`, and explicit `Word range: start:end` targets, use
`common.resources_read` (`text`). Range positions are main-story, zero-based,
start-inclusive/end-exclusive; reversed, out-of-document and noncanonical targets
are rejected, never clamped or given an implicit end.

The existing `LiveDocumentResourceProvider` routes all Word source reads through
`WordService.CaptureText` and the bound `IWordBackend`; it does not call the generic
adapter's document/selection text fallback. Complete captures use existing CAS and
authority, so internal pages and historical exact reads do not recapture live Word.
The one-million-character capture ceiling is checked before COM `Range.Text`;
oversized document/selection reads fail explicitly and require a narrower target,
not clipped text presented as complete. Empty exact ranges remain readable.
HTML uses this same provider. Word inspection and mutations keep their specialized
owners. Real Word range/selection/STA and final catalog qualification remain open
on Windows.

`word.find_text` keeps its semantic literal/regex/main/selection/all contract but
executes through `WordSearchResourceService` → Gateway → the existing document
provider → `WordService.CaptureSearch`. Matching is a pure Word domain operation
over the retained snapshot; the direct tool-adapter/backend search path is removed.
`Word search scope: main|selection|all` are discoverable scope-collection resources:
their exact JSON contains scope plus story kind/start/end/text, not runtime story
IDs. They share the existing document authority and CAS; no search store or separate
freshness state is introduced. Historical source pages can be read through the
ordinary resource reader without new Word I/O.

Capture bounds are 256 stories and one million aggregate text/range characters;
the provider also caps serialized JSON at one million characters. The bound backend
checks story count and aggregate ranges before `Range.Text`, then verifies the actual
text extent. Invalid regex is refused before capture; malformed/oversized snapshots
fail explicitly. Matching retains absolute coordinates within each named story,
case/whole-word behavior and output limits. Both positive and zero-match searches
carry complete exact CAS evidence; later observed scope drift or document mutation
invalidates previous evidence through the shared reducer. Search results no longer
expose scope/content hashes as public coordinates. Missing retained payloads never
fall forward. Actual Word story enumeration, COM and WebView qualification remain
open; other host-specific search consumers remain separate cutover work. Generic
`resources_find` scan publication uses the shared provider/Gateway path above.

## PowerPoint slide reads

`powerpoint.read_slides` is removed. Use `common.resources_read` with a discovered
document target or `PowerPoint slide: N` (positive one-based index). The `text`
view contains slide text; `source` is an ordered JSON array of slide id/index,
text and speaker notes, including for a single slide. `structure` remains metadata,
not a slide-content alias. Noncanonical and nonexistent slide targets fail explicitly.

The existing document provider calls `PowerPointService.CaptureSlides` and the bound
`IPowerPointBackend`; document/slide reads no longer call the adapter's clipped
document snapshot. Complete captures use the shared authority, CAS and evidence;
internal continuations and retained historical reads never recapture Office.
The backend enforces 500 slides, 1000 shapes per slide/notes page and a shared
one-million-character capture budget. Shape text length is checked before COM
`TextRange.Text`; over-limit decks/slides fail without a prefix-as-complete result.
Serialized view size is bounded too. HTML binds through the same Gateway.
Search, object inspection and mutations remain specialized. The existing selection
observation is separate and unchanged; it does not expose the slide `source` view.
Real COM shape/notes capture, selection and final catalog/model qualification remain
open on Windows.

### PowerPoint search

`powerpoint.search_text` keeps its literal/regex, case/whole-word, notes and
shape-local coordinate semantics. `PowerPointSearchResourceService` captures through
Gateway/CAS and invokes pure `PowerPointService.Search` over that exact snapshot;
the direct adapter search path and public scope/content hashes are removed.
`PowerPoint search scope: deck` and `deck+notes` are body-free discoverable resources;
`slide:N` and `slide:N+notes` resolve a positive one-based slide explicitly. They
expose `metadata` and exact `text` JSON containing slide index, shape name, kind and
text, not private target IDs. As before, tool `slideIndex=0` searches the deck.

Bound capture enforces 500 slides, 1000 shapes per slide/notes page, 5000 text
targets and one million aggregate text characters; serialized JSON is also limited
to one million characters. Search checks native text length before materialization
and does not swallow COM read errors as empty text. Invalid regex is refused before
capture. Complete evidence accompanies positive, zero-match and empty-scope results;
observed drift or document mutation invalidates prior evidence. Historical pages
use retained CAS without Office reads; missing payloads fail without live fallback.
Mutation preparation/read-back retains its existing domain owner and is not a
second search path. Real COM limits/enumeration and WebView qualification stay open.

## Outlook mail reads

`OutlookService.CaptureMail` is the typed owner of complete mail-source capture
and reply/update preparation. `outlook.read_mail` and its output wrapper are removed.
Model/manual reads and HTML bindings use the existing Gateway/document provider,
shared authority, CAS and evidence. Source continuations retain the first capture;
historical pages do not read Outlook or fall forward to another mail.

`common.resources_find` discovers `Outlook mail` targets from header-only bounded
discovery (up to 500 folder items; truncation remains explicit). Targets use subject,
sender and received time; identical targets fail as ambiguous. Runtime owns EntryID
and resource keys. An Inspector admits only its retained mail, including an unsaved
mail; a folder lookup supplies its StoreID and verifies parent StoreID/EntryID.
Moved/out-of-scope mail cannot be freshly read through a previously discovered URI.
The document/selection aliases mean current selected/open mail, not the whole folder.
Use a discovered mail child for stable mail identity within this bound scope.

The `text` view contains the full body; `source` includes headers, body and attachment
metadata (not attachment bytes); `structure` includes headers/attachment metadata
without reading body. Message reads return the entire body within the ceiling (at most
one million characters) or fail explicitly. They never return a clipped body.
The backend reads body once and derives the preparation token from those same
captured fields; source/attachment getter failures are not replaced by empty values.
Attachment-only capture does not access body or create a mutation token.
`BodyCaptured` distinguishes no body capture from a genuinely empty body.
Missing body/token/attachment metadata, mismatched explicit targets and cancellation
fail before the snapshot reaches a caller or a mutation dispatch.

Outlook OOM exposes `MailItem.Body` as one string: the character ceiling is enforced
after that property read, **not** before COM materialization. Bounded body acquisition
and real large-mail execution remain open. Mutation read-back remains a specialized
existing contour; folder search uses the exact projection described below. Real Inspector/folder/store
membership, unsaved-mail identity, WebView2 and final catalog/model qualification
remain open; this reader switch does not close those gates.

Folder collection uses the same document provider, shared authority and CAS;
`outlook.collect_mail`, its handler/request/output and monthly JSON wrapper are
removed, including accepted-call replay. `common.resources_find` exposes an
`Outlook collection` target. `text` is the complete JSON projection; `records/table`
at `$.messages` and HTML bindings derive from that exact retained text snapshot.
No separate collection store, bulk result tool or compatibility alias remains.

This is a bounded projection of at most 500 newest **folder items** (mail rows only),
not a complete mailbox or full-body capture. The JSON envelope reports
`totalFolderItems` and `collectionTruncated`. Rows expose subject, sender, received,
`month` (`yyyy-MM`), `bodyPreview` (at most 1000 characters) and `bodyTruncated`.
Read the envelope before interpreting record totals; `complete` on a records page
means completion of this captured projection, not the whole folder. Monthly grouping
belongs to the consumer; full bodies use individual mail resources. Folder paths and
EntryIDs are not projected into the body. Empty folders/previews remain valid.

The typed `OutlookService.CaptureCollection` validates row identity/extent and a
750,000-character aggregate budget; serialized JSON has the provider's one-million
character ceiling. The collection backend captures each preview once, with explicit
truncation and no mutation-token/second body read. Header/getter failures cannot
become empty successful captures. OOM still materializes the full body before
trimming, so this does not close the pre-COM allocation gate. Inspector runtimes
cannot read their parent folder. Fresh reads publish observed collection drift;
exact historical text/records and opened continuations never fall forward.

### Outlook search

`outlook.search_mail` retains literal/regex, case/whole-word, field-local coordinates
and subject/sender/recipients/body matching. `OutlookSearchResourceService` obtains
one Gateway/CAS snapshot and invokes pure `OutlookService.SearchMail`. Search output
contains semantic mail targets and bounded matching previews, not EntryIDs, folder
paths or repeated body copies. The obsolete `maxBodyChars` argument and direct tool
adapter search branch are removed without fallback; complete mail bodies use
`common.resources_read`. Duplicate semantic mail targets remain explicitly ambiguous.

The document provider exposes `Outlook search scope: latest:N` (headers only) and
`latest:N+body`, where N is 1–500 newest folder items; the default 100-item scopes are
discoverable without body reads. The exact text JSON includes the capture limit,
body-capture flag, folder extent/truncation and mail header/body-prefix rows. Bodies
are capped at 100,000 characters per mail with explicit `bodyTruncated`; the
aggregate retained header/body bound is 750,000 characters and serialized JSON is
limited to one million. Oversize aggregate captures fail and request a lower N.
Header-only search never reads Body; body search reads it once without a mutation
token, preserves surrogate boundaries and never substitutes empty text after errors.

`sourceTruncated` and the returned overall `truncated` flag include incomplete body
prefixes as well as folder truncation; `matchCount` counts only captured fields.
Complete evidence means the complete bounded projection, not the entire mailbox or
full mail bodies. Positive, zero-match and empty-folder searches retain exact CAS
evidence and publish observed drift; historical reads do no Office I/O and missing
payloads never fall forward. Inspector runtimes cannot search their parent folder.
The old search-body duplicate field and unused folder-path snapshot field are removed.
Real Windows/COM, OOM pre-materialization and WebView/model qualification remain open.

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
remain retention roots even before the first head commit. Current retention is
conservative. New checkpoints/retention optimization are not prerequisites for
cutover unless a concrete correctness or boundedness failure requires them.

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

HTML workspace file and JSON Save/create now consume the same upload route under
the distinct `html-editor` owner. Only typed target/chat/expected-workspace/lease/hash
controls cross the bridge; complete source enters the existing CAS-backed mutation
intent and existing HTML writer/commit barrier. Before dispatch, the editor guard,
Known logical publication and exact immutable source must agree under the mutation
lease. Draft/lease cancellation and bounds are in
[Artifact Library](artifact-library.md#html-editor-uploads). Outgoing workspace
files are now metadata plus exact existing member refs in every init/chat/mutation/
export projection. Selected source, preview and export use the same HTML member
provider's complete CAS views and shared download owner, with one bounded browser
producer and no inline body fallback. Unloaded source cannot become an empty draft;
dirty drafts keep their original workspace guard across newer metadata pushes.
Read/cancel/reload bounds are in [source downloads](artifact-library.md#html-editor-source-downloads).

Trajectory ZIP export uses the same owner-scoped data plane at
`/v1/download/<opaque-lease>`, with metadata-only bridge setup, sequential 256 KiB
GET chunks and full-payload SHA-256 verification before download. At most two
downloads share capture/lease limits and the 50 MiB transfer budget with uploads;
reservation precedes source validation and production. The ZIP remains a transient
projection of one validated event snapshot, not a synthetic published resource or
second store. Redaction, bounds and lifetime are owned by
[Trajectory export](trajectory-export.md). Binary/raw resource views use the shared
route described below; a separate cold-replay optimization is not a blanket gate.

Diagnostic event payload previews also use these download slots and the same
sequential reader. `TrajectoryPayloadService` resolves the exact event in a complete
validated journal, and the existing CAS codec verifies the whole bounded source
while retaining only a prefix. Source and preview hashes remain separate; bridge
setup contains no text. Both raw diagnostics and Run Journal use one cancellable
UI consumer, with no new publication/store. Bounds, UTF-8 handling and remaining
qualification gates are owned by [Trajectory query](trajectory-query.md#payload-preview-delivery).

Context Inspector's opt-in request JSON uses `PromptContextInspectorDownloadService`
and the same download slots/reader. The bridge carries snapshot metadata and a
`rawData` lease, never `rawRequestJson`. Reservation precedes inspection; the existing
512,000-character preview is surrogate-safe and transported as strict UTF-8 inert
text within 2 MiB. The browser verifies the payload before rendering, retains one
bounded raw-text cache and closes the lease on success/failure, panel close or chat
change, including late setup responses. Closing cancels delivery, not an already
running synchronous compiler capture. This is a disposable diagnostic projection,
not a new resource publication/store; compiler and context-budget semantics are
unchanged. Pre-truncation raw serialization allocation and real WebView2 lifecycle
qualification remain open.

The unused `getRuntimeLog`/`clearRuntimeLog` bridge commands, inline response DTO
and exclusive tail/clear helpers are removed, not replaced by a new download.
The existing UI log is WebView-session-local (`app-core.js`/`app-logs.js`), with no
runtime-file reader. Runtime file logging remains unchanged; existing log files
are neither cleared nor migrated by the cutover. Retired commands fail as unknown
bridge messages.

Text/Markdown/PDF text pages, images, thumbnails and PDF renders travel through
that data plane. Typed bridge DTOs carry metadata/leases, not page text or base64.
Text leases close after each bounded page; media leases close on replacement,
eviction and chat change, including late responses. Full text is an explicit
bounded user action, not an eager workspace cache.

Artifact list/resolve descriptors now advertise admitted binary views through the
existing `representations` and typed `viewCapabilities`: image (20 MiB), image
thumbnail (512 KiB), PDF page (10 MiB) and PDF thumbnail (1 MiB). The existing media
owner supplies these metadata-only capabilities only for matching exact attachment
evidence and admitted kind/MIME/extent; an unconfigured attachment reader advertises
no attachment binary views. These are `maxPayloadBytes` object bounds, not renderer availability.
Binary views advertise sequential byte offsets/streaming and separate 256 KiB
`maxBatchBytes`/`maxItemsPerBatch` limits. Gateway rejects unsupported views and row/field
selectors before source hydration. Captured and retained views obey the same
per-view byte limit and MIME contract; retained data cannot bypass negotiation.
The same owner also exposes `raw` for exact attachment-backed image/file/attachment
artifacts, including empty originals. It reads and verifies original byte length
and SHA-256, never extracted text or rendered output. The retained `binary:raw`
view uses the existing CAS and must still match the exact source evidence; missing
CAS never falls back to the original reader. Control setup contains metadata only.
Delivery uses the same binary lease with sequential byte offsets and chunks up to
256 KiB; raw has no row/field/page selectors. The source MIME stays
in the descriptor; the raw payload/HTTP MIME is inert `application/octet-stream`
under the shared no-sniff/CSP route. HTML bindings accept `view: raw`, and the same
`RN.resources` binary consumer and standalone export return original byte chunks.
The same raw owner also admits committed CAS originals for file, Markdown, plan,
task-list and chart artifacts, without requiring an attachment reader. Discovery
checks kind, exact hash/length, the same 20 MiB bound and absence of attachment
provenance, without loading bodies. Reads verify the original CAS bytes directly;
they never reserialize JSON, normalize text, hydrate `InlineText` or reconstruct a
missing original. Broken/ambiguous attachment provenance and malformed metadata
cannot become a stored-body fallback, including on retained reads. Internal
checkpoints and HTML workspace aggregates are not raw files. Exact raw views and
their byte payloads use the same authority retention roots and historical access.
Workspace file members also offer `raw`: UTF-8 bytes of the exact committed file
source, not bytes of the parent workspace JSON. BOM, CRLF and Unicode are preserved;
the byte hash matches the same member's `source` CAS payload. Empty files are valid.
The existing member catalog checks the 20 MiB bound before encoding. Data-binding
members deliberately do not offer `raw`: a binding descriptor is not the bound data;
consumers read that resource's advertised view instead.
`ChatArtifactResourceProvider` owns all raw capture through `IResourceRawSource`;
the former viewer raw-reader is removed. Gateway resolves the exact descriptor,
preserving member identity, verifies length/hash before registering a view and uses
the existing binary retention/chunk route. Workspace edits do not replace historical
file reads, and missing retained CAS is not rebuilt from the parent snapshot.
No new reader tool or store is introduced. The implemented original-byte domains
(attachments including PDF/image/text, stored file/JSON artifacts and workspace
files) now negotiate raw through this path. `UpsertDataSource` already publishes a
JSON file artifact; its binding is not another raw source. This does not require
inventing `raw` for every live Office, catalog or state projection. Real
Windows/WebView2 qualification remains open.

Retained binary metadata is read through the shared snapshot reader, with a 4 KiB
JSON bound, strict UTF-8 and bounded typed parsing. Missing/null/corrupt records,
invalid payloads and extra JSON return `RESOURCE_SNAPSHOT_UNAVAILABLE`, never
recapture or renderer fallback. The embedded payload must match the view hash and
its exact retained part (hash, length, MIME and protection), so readable metadata
cannot bypass the authority's CAS retention roots. Failed reads publish no head
or generation and do not repair the retained record.

Binary opens reserve capture capacity before provider work in the existing 50 MiB
upload/download transfer budget. A lease verifies and retains its bounded CAS body
once on the first chunk, then serves sequential slices without per-chunk CAS
rehydration. Completion frees the buffer/reservation; close, expiry and failure
invalidate the lease, keeping busy-operation capacity until that operation exits.
Empty resources still verify their CAS on a zero-byte read. No second durable
store or producer queue is introduced; full bounded CAS verification remains, not
constant-memory decoding from disk.
Artifact image/PDF/thumbnail consumers use the shared verified chunk accumulator
before creating cache-owned blob URLs; cache eviction/chat close cancels pending
delivery and revokes URLs. No image element points directly at the capability URL.
`RN.resources.read/stream` yields byte chunks with `offset`, `nextOffset`, `done`;
fields and out-of-sequence offsets are rejected. Standalone export manifest v2
stores bounded binary parts with byte offsets and lazy verified offline slicing;
v1 manifests are explicitly refused by the current assembler, with no fallback or
rewriting of previously exported HTML files.

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
inline Skill Library core/reference mutation bodies and reference source echo
(now one bounded upload consumer, retaining the existing guarded mutation owner);
inline Tool Library mutation requests in Save and save-before-install
(now one bounded upload consumer through the same guarded authoring/commit owner);
inline Tool Library schema/code/README/component catalog and response bodies,
plus the UI whole-catalog body serializer/component reconstruction fallback
(now exact selected source through Gateway/CAS and shared download);
inline generated builtin documentation controls and state-wide documentation/body
request caches (now committed documentation CAS parts, exact catalog children and
the same bounded download owner, with one selected cache and no generator fallback);
`common.tools_definition_read` and its direct authoring-file reader/native binding
(now metadata discovery and exact tool-source reads with catalog-dependent evidence);
`common.prompts_read`, its current/defaults bundle and direct-settings native reader
(now exact published prompt/default field resources through the same generic reader);
separate state/Office retained text readers, externally hash-bound Office/state/catalog
continuations, and read-side exposure of prepared state/context/catalog identities.
HTML workspace file/data Save/create inline bridge bodies are also removed; the
four UI callers share one upload writer and the existing guarded domain owner.
Outgoing `HtmlWorkspaceDto.Files` domain-body cloning is removed from all four
projection sites; editor/preview/export now pull exact source through the same
member provider/CAS/download. Preview assembly refuses missing source bodies.
No compatibility alias, dual-write or feature flag restores these paths.

The acceptance reconciliation below replaces the former blanket TODOs for
"remaining consumers", named resources and cold-replay/retention optimization.
Do not treat those labels as authorization for further provider expansion.

## MASTER acceptance reconciliation — 2026-09-07

This is a source/existing-check review of MASTER §8, not a new test run, Windows
qualification or assertion that every repository path was exhaustively proved.
Test method names below are in `tests/RNAssistant.Harness`; implementation owners
are in `src/RNAssistant.Office/Services` unless another layer is stated.

| MASTER scenario | Current implementation / existing check | Remaining acceptance gap |
|---|---|---|
| Historical exact revision; continuation cannot cross revisions | Gateway/ResourceSnapshotReadService; `ResourceLiveContinuationsUseLogicalRevisions`, `ResourceRetainedPayloadsFailClosed` | No new architecture required by this review |
| Restore/rollback create new lineage even with equal bytes | ResourceMutationAuthorityObserver; `LocalResourceRestorePublishesNewLogicalRevision`, `VbaRestorePreservesExactSourceLineage` | Real Office restore remains Windows evidence |
| Save As preserves logical document identity | Document authority binding; `DocumentAuthoritySurvivesSaveAsAndSeparatesCopy` | Real COM/window lifetime remains Windows evidence |
| Guarded write cannot publish over unexpected head | HostRuntime/domain guard, mutation lease, authority compare-and-publish; extended `VbaConfirmedMutationRejectsStaleSnapshot` | Checked host-neutral: another chat observes a replacement revision while confirmation waits; stale confirmation performs no mutation/publication or backup. Guard mismatch may separately publish conservative ExternalDriftObserved/Unknown; the competing historical snapshot remains intact |
| Changed/no-op/unknown effects; cross-chat invalidation | ResourceMutationAuthorityObserver/OfficeResourceMutationDomain + Core EvidenceStateReducer; `ResourceTwoChatMutationsReachCompiler` | Checked through two executors sharing document authority, native Excel read/write, persisted paired read facts and actual ConversationModelSession.CreateRequest. No-op retains current evidence; changed-without-captured-after-state and lost-read-back remove uncertain content. Compiler/historical reads perform no Office I/O or replay |
| One coherent frozen authority tuple | UseInput carries the captured catalog generation through CreateAsync/RebindAuthority; CompileCurrent checks it against the same frozen CaptureMany tuple | Fixed host-neutral: intervening publication fails before compilation/request dispatch; the extended `ResourcePromptPublicationIsFrozen` checks rejection, fresh rebind and retained request/repair |
| Every normal model request uses one compiler | ConversationKernelAdapter.Model → ConversationModelSession.CreateRequest → ModelContextCompiler; repair also uses compiler; `ResourceCompilerFiltersBeforeBudget` | Preserve this route when closing the catalog race; no second builder |
| Large payloads remain reference-first/bounded | Existing CAS/download/upload owners; `ResourceRuntimePayloadStorage`, `ResourceCompletedCallDoesNotHydrateArguments`, binary/raw admission cases | Known source-allocation limits below are not closed by transport bounds |
| HTML/viewers use Gateway, not copied current JSON | ResourceDataPlaneService and resource-backed bindings; `ResourceBoundedTableLeaseUsesOneSnapshot`, `tests/web/resource-data-plane.test.js` | Real WebView2 qualification; HtmlWorkspaceDataSource now contains binding metadata, not the removed Json body |
| Slow consumer cannot create unbounded buffering | Pull-based reads, busy/lease/byte limits; `ResourceBinaryChunkBudget`, sequential stream/download/upload browser cases | Real WebView2 responsiveness qualification |
| Schema/mapping change invalidates derived currentness | ResourceStateProvider/ResourceDerivedViewService + reducer dependencies; `ResourceSchemaMappingDerivedPublication` | No new semantic layer required by this review |
| Wave 5 retention roots and unavailable historical payloads | Core CasMaintenanceService scans chats, VBA journal, authority revisions/views/parts and mutation journal; `ResourceUnpublishedRevisionRetention`, retained missing/corrupt-CAS cases | Conservative retention already protects roots; do not invent a new GC/checkpoint subsystem |

Finite remaining order:

1. **Catalog capture correctness — complete host-neutral.** The generation from
   `UseInput` accompanies the active tools/skills/prompts into the model session.
   `CompileCurrent` compares it with the catalog scope in its single frozen
   `CaptureMany` result, before compilation or receipt publication. An intervening
   publication returns `RESOURCE_CATALOG_CHANGED`; no mixed request reaches the
   provider, and no automatic model/tool replay is introduced. Fresh capture/rebind
   permits the next request. Publication after freeze cannot alter that request;
   repair also closes over the original catalog/settings/budget rather than later
   rebound session fields. The existing frozen-prompt test now deterministically
   covers this interval. Published global-tool projection does not rediscover
   document VBA; bound host/document registrations come from the input owner.
   Local closed-document runs and default publication refresh perform no incidental
   Office discovery. Explicit Library/document discovery keeps its existing guarded
   owner. Owner: existing catalog capture/model-session owners.
2. **Integrated mutation acceptance — complete host-neutral.** The new two-chat
   scenario covers changed/no-op/unknown through native Excel execution, the shared
   journal/authority and the next actual model request. Verified change without a
   captured complete after-state yields an Unknown head, while the durable effect
   remains VerifiedChanged (distinct from UnknownAfterDispatch). No-op preserves
   the head/generation; prior frozen requests and historical CAS remain unchanged.
   The existing stale VBA confirmation case now checks competing authority state,
   no mutation publication/backup, separate conservative guard-drift publication
   and retained replacement bytes. Two focused checks cover all four scenarios;
   no production change was required. Finer sectional validity is not mandatory:
   Evidence/Compiler §5 and Authority §47 permit conservative head semantics.
3. **Resolve existing source-allocation qualification.** Outlook `MailItem.Body`
   allocates before its ceiling is checked (tracked in RISK_REGISTER); Inspector
   request serialization also precedes preview truncation. Distinguish source
   allocation from bounded transport, and record an explicit owner decision or
   correction before claiming bounded-source qualification. Do not turn this into
   a new diagnostics/performance project.
4. Align the final removal records with these results, then run the already-required
   Windows/Office/WebView2 qualification. Only concrete reachable bypasses found
   during this closure justify further consumer changes. No more named resource
   kinds, universal raw expansion, finer Excel coverage or checkpoint optimization
   are scheduled by this reconciliation.

## Delivery order

MASTER waves are one dependency-ordered implementation: shared foundation →
mutation/evidence → frozen compiler → reference-first HTML/viewers → schema/derived/
catalog/retention cleanup. Host-neutral checks do not close real Windows x64 +
Office x64 + VS 2022/WebView2 qualification, Phase 12 or any release gate.
