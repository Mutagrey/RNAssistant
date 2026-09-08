# Artifact Library and Viewers

Status: Phase 11 target contract. 11A1 and 11A2 implement the host-neutral commit-time
boundary, explicit draft/preparing/committed labels and exact Library head/history
projection. 11B1–11B3 complete the host-neutral Plan domain owner, exact whole-Markdown
lineage, restore/removal UX and ready handoff by pinned URI. 11C1–11C3 complete the
host-neutral HTML lineage, inert uploaded-source import, binding evidence, recovery
and guarded exact export contour. 11D1 implements host-neutral bounded text/source
and complete-only sanitized Markdown viewers. 11D2 adds exact local image preview,
bounded thumbnails, collection/chat galleries and the shared preview-first/Details
presentation for Plan, Task List, Markdown, image and existing domain viewers. 11D3
adds bounded PDF pages, paged extracted-text / scan state, the shared sequence shell
and matching x64/x86 native packaging; audio remains a later independent slice.
The existing Resource Fabric ingestion, CAS,
`ResourceRef`, provider and model-context semantics remain authoritative. This
document defines the user-visible lifecycle, viewers and mutation rules; it does not
introduce another artifact transport or store.

## Authorized document ownership cutover — 2026-09-07

Status: slice **1a, sent originals, implemented host-neutral**. Newly sent files
belong to the document and are discoverable/readable from its other chats.
Slice **1b, Plan publication, is implemented host-neutral (2026-09-08)**: new Plans
also belong to the document. Shared authored HTML workspaces and their authored
JSON resources are implemented host-neutral (2026-09-08), using the same retained
artifact-record path, revision journal and CAS. Independent Markdown creation,
revision and restore are implemented host-neutral in Agent mode (2026-09-08).
Originals/Plan/HTML/Markdown working-set links and chat-local unlink are implemented. This section owns
the artifact-specific decision, not a second resource architecture.

`DocumentArtifactStore` publishes immutable original metadata as a retained view
in the existing authority journal/CAS. Raw and extracted payloads are retained
parts, so deletion of the origin chat and CAS collection preserve the original.
Chat events retain exact links and input observations; their `Artifacts`
projection does not duplicate document-owned records. The first publication
records origin-attempt provenance, even if saving its chat link subsequently fails.
Retrying that link reuses the exact original; conflicting metadata is refused.
Independent publications retry only the authority generation check.

Gateway discovery (`scope=document`), text/raw reads and exact viewers use this
owner without requiring the origin message. Fork preserves the original ref.
Missing metadata/extraction fails explicitly, with no origin-chat/body fallback;
foreign document references are refused. Existing chat-local records are not
silently migrated. The remaining ownership seam and removal gate are tracked in
[MIGRATION_MAP](stabilization/MIGRATION_MAP.md#document-artifact-ownership--active-slices).
Exact reads load one metadata record. Model discovery pages current roots from the
existing ordered authority projection before reading metadata (see below). Picker/
history enumeration remain separate slice-3 work; authored and uploaded text views
are implemented below.

### Implemented Plan publication slice

`PlanDocumentService` applies domain rules to a disposable selected projection.
`ResourceMutationAuthorityObserver` prepares the logical Plan under the existing
document mutation lease, compares the selected exact snapshot with the document
head, and hydrates committed lineage before dispatch. Only current/restore-source
bodies are loaded. Contending Plan writers wait within the existing bounded storage
lock policy; no lease spans a model/user wait. Stale, missing or ambiguous selection
is a typed pre-dispatch rejection, not a successful write or a replay instruction.

`DocumentArtifactStore.RetainPlan` retains metadata, exact Markdown and origin
chat/run/attempt provenance in the existing revision journal/CAS. The mutation
observer publishes the immutable snapshot, per-Plan logical head and stable
operation receipt together in one document authority commit. The receipt checks
repeated runtime operations without scanning the library or loading bodies.
Only authority-generation conflicts retry that
publication; the mutation is not rerun. Restore records both snapshot and logical
restore origins. The subsequent chat save persists links/selection, not another
copy of the Plan metadata/body/head. A failed chat save does not remove the
published resource; repeating the same runtime operation is refused with
the retained-publication recovery route.

Gateway lists/searches current document Plans and reads exact historical snapshots
from any chat of that document. Reads of a current snapshot carry its logical-head
dependency. Chat deletion and GC preserve history; chat fork retains the selected
snapshot without copying its identity. A document tombstone survives removal of
its origin message or a dialogue rewind. Discovery omits the removed Plan; retained
exact bytes remain available for historical reads.

A new chat can create a separate Plan, select an existing document Plan through the
working-set selector, or inherit a selected Plan through fork. Refresh is an
explicit selection of the displayed current snapshot; no implicit current-head
replacement or old-chat migration. Independent MD authoring remains open.

### Implemented shared HTML publication — 2026-09-08

`HtmlWorkspacePublication` supplies domain preparation and aggregate read-back to
`ResourceMutationAuthorityObserver`. The existing document mutation lease serializes
writers; the selected exact snapshot must match the logical workspace head before
dispatch. Preparation reconstructs the current aggregate from its published bytes.
A new unselected chat creates a separate logical workspace. Multiple workspaces
have independent identities and lineages in the same document.

The common `DocumentArtifactStore.RetainAuthoredSnapshot` path retains workspace
records and newly authored JSON files in the existing revision store/CAS. There is
no HTML-specific metadata/data store. Snapshot, logical head and operation receipt
cross one authority publication barrier; saving chat links follows publication.
A failed link save leaves a discoverable resource, and replaying the same operation
is refused. Incompatible chat-owned HTML is explicitly rejected for mutation.

Gateway and HTML member URIs preserve the document owner. Exact root/member refs
reconstruct one snapshot in chat projection; member refs do not create duplicate
artifact records. Current reads carry the shared logical-head dependency. Exact
bindings retain exact dependency evidence; supported `head` bindings remain dynamic
identities resolved through the existing Gateway/data plane at read/export time.
Uploaded HTML remains an immutable original; authored JSON is an ordinary file
artifact. Export requires a saved document snapshot and does not create a revision.

**«Ресурсы» → «Из документа…» → «Выбрать HTML»** loads the displayed current snapshot
in another chat. Selection validates the complete body before changing membership.
A stale writer is rejected before dispatch and must explicitly select the new head.
The picker and response guards preserve newer drafts/navigation. Unlink clears
only this chat's selected workspace, including when its body is unavailable.

Undo/recovery/redo publish new causal snapshots with parent and exact restore-source
provenance, including the corresponding logical revision. Navigation/redo metadata
survives restart. Rewriting dialogue and fork preserve selection and historical
message refs without publishing rollback or copying shared HTML/JSON identities.
Origin-chat deletion and CAS collection retain historical snapshots and bindings.

`shared HTML:`, HTML runtime/replay and resource chat-lifecycle checks cover this
host-neutral slice. Windows/Office/WebView2 and Playwright layout qualification
remain open; indexed discovery is a separate slice.

### Implemented independent Markdown — 2026-09-08

Agent mode exposes `common.markdown_save` and `common.markdown_restore`. Save
requires a title, a grounded description of purpose/contents, and the complete
Markdown (up to 200,000 characters). Omitting `target` creates an independent
logical document. Supplying the exact semantic target returned by common resource
discovery replaces that document's current revision; titles alone are not targets.
An uploaded `.md` remains immutable. Ordinary Markdown replies remain messages.
Chat mode stays read-only; this slice does not change Plan mode's workflow.

`MarkdownDocumentService` prepares a typed runtime intent with exact base and
optional restore source. `ToolRuntime` supplies the same prepared state to the
handler and mutation observer through preparation and read-back, including automatic
execution. The kernel execution identity and persisted event format are unchanged.
The existing document lease rechecks the base before dispatch. The common
`DocumentArtifactStore` authored-record path then retains complete text and
metadata; snapshot, logical head, operation receipt and effect publish atomically.
Receipts include the runtime call identity so two creations in one model step
remain distinct. A failed chat-link save does not lose bytes or authorize replay.
Restore creates a new causal revision and preserves exact source provenance.

There is no global active Markdown slot. The existing picker attaches or refreshes
an exact Markdown link (**«Подключить MD»**); each logical document has independent
history and membership. The same shared Gateway provides find/read and the existing
sanitized Markdown/source viewer. Discovery and the bounded prompt index expose the
authored description as untrusted metadata. Full text is read on demand, through
bounded exact pages; reopening a chat reconstructs history metadata without loading
all Markdown bodies. Fork and dialogue changes preserve shared identities/history.
Unlink is chat-local and still works with missing body bytes; origin-chat deletion
and CAS collection retain published revisions.

`shared Markdown:` covers cross-chat read/write, concurrent and prepared stale
writers, duplicate titles, same-step independent calls, restore, immutable uploads,
failed link publication, foreign documents, metadata-only restart, fork, unlink,
GC and the complete agent execution/event-replay cycle. Partial discovery and
Markdown/Plan text views are implemented below. Richer compiler context, explicit
independent-copy UX and Windows/Office/WebView2/layout qualification remain open.

### Implemented working-set links — 2026-09-08

The original working-set slice covered document-owned originals and Plans.
The shared HTML slice extends the same contract to HTML and authored JSON files;
Markdown now extends it too; remaining discovery/recovery work in slices 3–4 stays open.

`ChatSession.ArtifactLinks` is append-only-event-backed chat membership: one
logical resource identity, exact attached snapshot and detached flag per decision.
It owns no title/body/head. In the absence of an explicit decision, existing exact
message refs establish membership. Explicit detach overrides all message refs for
that logical resource. Attached refs are reachability roots independent of messages.

**«Убрать» / «Убрать из этого чата»** removes a working-set link and, for the selected
Plan or HTML, clears its selection. It excludes that resource from library heads, the next
bounded prompt manifest and new compaction reference collection. It preserves
historical messages/checkpoints, exact resource reads, other chats and document
CAS. It is neither a document tombstone nor an access revocation. History rewrite
and fork preserve explicit decisions; clearing the entire chat clears membership.

The **«Ресурсы» → «Из документа…»** picker remains available in an empty chat. It
lists metadata for current Plans/HTML/Markdown and ordinary document files, supports title search and returns
50 items per page with a cursor bound to the chat revision, document, query and
ordered collection. Duplicate names remain separate exact resources; continuation
cannot silently skip a changed catalog. A click attaches an original or selects the
exact displayed Plan/HTML or refreshes a Markdown link. Detach is also available
beside eligible working-set rows. Run/system resources are outside this slice.

`ArtifactWorkingSetService` owns validation and document-lease coordination; the
typed `listDocumentArtifacts`/`changeArtifactLink` bridge carries an explicit chat,
exact snapshot URI and expected chat revision for writes. The controller reserves
and reloads that addressed chat, verifies the bound document, then saves membership
(including an initially empty chat). A short document mutation lease spans Plan
currentness validation through chat persistence. Stale chat saves fail optimistic
concurrency; stale Plan selections fail without choosing latest. No resource head
is published by link changes. UI requests capture chat/navigation, suppress double
clicks, ignore late responses and never retry mutations automatically.

Chat reconstruction reads Plan metadata before optional active-body hydration.
A missing body therefore does not block unlink; an explicit body read still fails.
Per-resource metadata recovery is implemented below; bounded indexed enumeration
remains tracked in the stabilization backlog. Model-facing partial discovery is
implemented in the following correction.
Enumeration still scans document revision metadata; a 50-item response does not
claim bounded source allocation.

Evidence: `artifact working set:` covers original/Plan attach, native cross-chat
editing, restart, fork, history, missing body, clear, stale snapshot/session,
competing document lease, foreign scope, duplicate-title paging and stale cursors.
`artifact-working-set.test.js` covers empty-chat access, request ordering, captured
writes, duplicate clicks and navigation races. Windows/Office/WebView2 and real
layout qualification remain open.

### Unavailable metadata recovery — 2026-09-08

After working-set commit `5bf9aba1`, a separate dependency-safe correction closes
chat reconstruction and picker failures caused by an individual missing/corrupt
metadata record. That recovery mechanism also serves the implemented HTML owner move.

`DocumentArtifactStore.InspectMetadata` produces an explicit reference-only
`AvailabilityIssue=metadata_unavailable` projection. The runtime validates owner,
snapshot ID and revision; Plan logical identity is derived from its canonical
runtime-generated snapshot ID. It does not invent a title, body, MIME, provenance,
creation time or current head. This issue is transient (`JsonIgnore`), document
artifact projections remain excluded from chat events, and strict resource reads
still require their exact retained record/body. The previous scan-based selected
Plan lookup is replaced by exact runtime snapshot addressing.

Chats retain their selected unavailable snapshot instead of hiding the chat,
creating another resource or silently choosing latest. Originals/Plan picker
inspection isolates individual failures, labels unavailable entries and keeps
healthy entries. For an unknown logical Plan head it displays a retained snapshot
as `head_unavailable`, with selection disabled; it does not advertise that snapshot
as current. Snapshot availability participates in the continuation fingerprint.
Unlink checks that the logical resource actually belongs to the chat working set,
then persists only the link decision under the existing revision/lease guards.

Typed chat/library/picker DTOs carry availability. Cards with missing metadata
cannot open a viewer, while the picker and eligible resource rows expose unlink.
The next model manifest reports unavailable counts and a recovery action without
creating guessed semantic targets. Fork preserves the issue and exact message
refs; attachment linking never reconstructs unavailable owner metadata from a
message. Restoring the exact metadata removes the issue on a fresh load but never
reattaches a previously detached link.

This does not repair lost authority journals, invent missing references, revoke
historical bytes. The later partial-discovery correction below isolates failures
for model enumeration; exact reads remain strict and indexed discovery remains open. No second store, user-data deletion or resource-head publication
is introduced. `artifact recovery:` verifies deleted Plan metadata, corrupted
original metadata, healthy picker entries, forbidden selection/read, durable unlink,
metadata return, unknown head and fork. Real WebView qualification remains open.

### Implemented partial current discovery — 2026-09-08

`DocumentArtifactStore.InspectCurrentMetadata` builds a disposable source page at one
captured authority generation. The provider reads only the exact current Plan,
HTML and authored Markdown metadata selected by logical-head dependencies, plus
originals and other retained artifact roots. Historical metadata is loaded by
explicit history/exact consumers. A missing or unknown head never chooses the
largest retained version; missing current metadata never falls back to history.

Collection list/search isolates per-resource metadata, aggregate and text-body
availability failures. Healthy matches remain visible. Typed `unavailableResources`
counts survive pagination and feed the existing model `partial`, `complete`,
`empty` and `unavailableScopes` fields. An unavailable resource cannot become a
complete negative or establish a unique semantic target, even if one healthy
candidate remains. `availabilityHint` gives a runtime-owned recovery route;
existing specific provider errors (including a changed live document) are preserved.
Exact retained reads remain independent of unrelated collection damage.

### Implemented bounded document discovery pages — 2026-09-08

`ResourceAuthorityStore.ReadHeads` reads bounded identity ranges from the existing
ordered `Heads` projection under its usual lock. It does not copy a full authority
snapshot/commit list per page. `DocumentArtifactStore` selects logical Plan/HTML/MD
heads and original/authored-file roots; history and operation-receipt ranges are
skipped before metadata IO. Currentness still comes from exact head dependencies.
No second durable index, resource store, or publication protocol is introduced.

The chat provider hydrates at most 50 source slots per list page. Filtering,
unavailable metadata and removed entries consume their slots without shifting
continuation. `totalIsExact=false` distinguishes the unfiltered source-slot count
from an exact filtered result count. Empty filtered pages may have continuation;
Gateway consumes at most 20 pages per plan, even with zero matches. Artifact search
also examines at most 20 source pages while preserving existing character/result
budgets. Any unexamined remainder stays incomplete and cannot prove absence or
uniqueness. Search reaches later pages without loading all metadata in advance.

Root continuation binds chat, document authority generation and local projection;
its offset addresses source slots, not healthy-result positions. Generation drift
invalidates the cursor. Metadata recovery at the same generation does not shift
those slots: previously omitted entries are recovered by a fresh discovery, and
Gateway preserves any earlier page's unavailable status through the current scan.
This supersedes the prior all-collection availability fingerprint, which required
rehydrating the library before every continuation. HTML-member discovery reads only
the exact selected current workspace; missing/stale selection stays explicit.
Direct immutable snapshot identity resolution reads its own head and one metadata
record, removing the final all-history load from the provider identity path.

`Heads` remains a replayed in-memory projection of the append-only journal. Cold
replay, ordered insertion and explicit full-authority/history consumers still scale
with journal size; this slice does not claim bounded startup or write allocation.
The working-set picker/history scans, section/content index, richer compiler context,
authority-journal recovery and Windows/layout qualification remain open.
Host-neutral coverage includes a 73-resource library with 1000 unrelated receipts,
per-page metadata IO, no full capture on the provider path, point identity reads,
complete traversal/search, source-page ceilings, unavailable metadata and writer drift.

### Implemented exact text discovery views — 2026-09-08

Document Markdown and Plan content search now uses `artifact-text-index-v1`, a
deterministic `ResourceRevisionView` over the exact published body. Its manifest
and 32,000-character text parts live in the existing CAS/revision journal, using
the same retention/GC edges. It publishes no heads, observation or read guard and
introduces no library database or second currentness projection.

First materialization reads one exact source bounded to 2 million characters;
subsequent searches read retained parts under the existing 1-million-character
query budget. Authored Markdown/Plans no longer stop at the old per-artifact
128,000-character prefix. This is a section/chunk view, not an inverted full-text
index: an uncached build reads the bounded source and negative queries scan parts.
One occurrence per resource suffices for discovery; a budget-limited negative
remains incomplete. Matches spanning parts preserve exact UTF-16 source offsets,
including CRLF and surrogate pairs.

`sectionTitle` supplies the nearest preceding recognized ATX Markdown heading,
excluding fenced code, with up to 200 characters plus an omission marker. This
bounded outline recognizes at most 4096 headings; it supplies no title after an
omitted heading. It is not a complete Markdown AST. Exact section selection uses
the complete-source scan described below, never the clipped discovery label alone.
Search-only Gateway candidates now retain the authored description too. Both
fields are untrusted discovery context; snippets are source excerpts, not proof of
whole-source inspection or mutation eligibility.

A missing/corrupt derived manifest or part is recreated once from the same exact
body; immutable view registration rejects conflicting derivations. Missing source
bytes stay unavailable. Current search selects current heads, while an explicitly
requested historical snapshot keeps its own index. Generation is checked after
the last source page as well as between pages, so a write during materialization
cannot produce an apparently current mixed-generation result.

Uploaded text and HTML members now use the extensions below. Semantic section
reads are implemented below; picker/history pagination, richer
compiler decision context and cold allocation/Windows/layout qualification remain
open. No model-generated synopsis or embedding publication is added.

### Implemented uploaded text discovery views — 2026-09-08

`DocumentArtifactStore.SearchText` also indexes the original's retained extraction
through `artifact-extracted-text-index-v1`. The exact resource still identifies the
immutable upload; the view hash binds its **extracted text**, independently of the
original binary payload hash. The existing manifest/parts/CAS mechanism, source
bounds, source-page generation guards and repair path are reused. There is no
second extractor, source-chat fallback or new store. Original search no longer
uses the per-resource 128k prefix path; local resources keep their existing path.

Materialization validates the retained extraction character count. Uploaded
Markdown receives ATX heading context; PDF/plain-text hash marks do not become
Markdown headings. Snippet offsets address the retained text representation and
round-trip through the existing exact resource read. No read-evidence contract
changes. If text extraction bytes are missing, text search stays unavailable even
with a warm index; the original's metadata remains discoverable. Missing derived
bytes are rebuilt from that exact extraction, and normal CAS GC retains the view.

`TextTruncated`, or PDF page metadata showing unextracted pages, keeps search
incomplete even on a hit or a query longer than the retained text. These views
record character-range coverage instead of whole coverage. A complete text scan
refers to the retained text representation; it does not inspect images or prove
full visual coverage of a PDF. HTML member indexing is implemented below;
semantic section reads remain subsequent work.

### Implemented HTML member discovery views — 2026-09-08

`ChatHtmlResourceCatalog` resolves/serializes members from the exact selected HTML
aggregate. `DocumentArtifactStore.SearchMemberText` reuses the existing text-view
engine and CAS retention, under the exact published parent revision with view key
`artifact-member-text-index-v1:member/{type}/{key}`. Member character coverage is
scoped by that path; it never claims whole-parent coverage or publishes new heads.
The common text materialization/repair path replaces the document-member 128k
prefix scan. Legacy chat-local member search retains its existing bounded path.

File content is source code; data-member content is its serialized binding, not
the resource behind that binding. Search does not execute HTML, fetch bound data,
or interpret source as Markdown. Returned references and offsets address the exact
member representation, and model scope remains `html` despite document ownership.
CAS protection metadata is preserved for retained member payloads. Missing/corrupt
derived views are regenerated from the same aggregate; a missing parent stays
unavailable. Retained parts survive ordinary GC and owner restart.

Search validates selected artifact and authority generation after member scanning,
including member-only requests. Combined search also checks its initial document
generation and preserves member failures in `unavailableResources`. A changed
selection/current head requires refresh; exact historical reads still work.

The catalog still loads/parses the complete bounded aggregate for member discovery,
including warm-index searches. This slice does not establish metadata-only HTML
discovery or bounded cold-start allocation. Markdown section reads are implemented
below; picker/history
pagination and Windows/Office/WebView2/layout qualification remain open.

### Implemented Markdown section reads — 2026-09-08

`common.resources_read` accepts `section` together with `representation=text` for
document-owned authored Markdown, Plans and complete uploaded Markdown text.
The selector is a unique ATX heading title without its leading `#` markers, up to
200 characters, compared case-insensitively after trimming outer whitespace.
Remaining title markup is literal. The selected text includes its heading and
nested subsections until the next heading of equal or lesser depth. Fenced code
headings are ignored; Setext/HTML headings are not supported by this grammar.

One `MarkdownHeadingScanner` drives discovery and section selection; the existing
v1 index manifest/label bytes are preserved. Section resolution reads the exact CAS
text under the existing 2-million-character source bound and scans at most 4096
headings. It does not infer uniqueness from a clipped index or incomplete extraction.
Missing/repeated headings, truncated extraction, unsupported resources, oversized
sections and generation drift fail explicitly with recovery guidance. A selected
section is bounded to 32000 characters; there is no silent truncation or whole-read
fallback. Cursors and table/record selectors cannot be combined with `section`.

The result follows normal Gateway/provider/authority/evidence flow. `complete=true`
means the named section is complete; its coverage is always the exact source
character range, including when it spans a small file. Evidence retains only the
delivered bytes and cannot satisfy a whole-resource refresh requirement. A later
whole read remains independent. Discovery `usage` explains section versus whole
reading. Native v5 `message` guidance now connects a known finding, the purpose of
actual upcoming calls and what the result will clarify, without parsing reasoning
or changing runtime lifecycle ownership. Real target-model response quality remains
an open qualification gate.

### Ownership and user behavior

The durable owner is the existing `DocumentAuthorityId`, independent of a chat,
window, current path or open runtime session. The library is a projection of that
owner's committed resources. It does not become another writable inventory.

The operation actor remains the exact addressed `chatId`; shared ownership never
switches that actor to the mutable active chat. Background HTML/Markdown/Plan
controllers must require an explicit chat id and use `LoadAddressedSession`, not
the activating `LoadSession`. An addressed load must preserve the stored document
authority and locator. Office mutations additionally validate the current bound
document before dispatch; resource sharing does not grant access to another live
Office document. The native Plan handler/observer use their captured session.

| Object | Owner / behavior |
|---|---|
| Authored HTML workspace, Markdown documentation, report, specification, long-lived Plan | Document; independently named logical artifacts, each with immutable revisions |
| Sent original files, PDF/image/audio and imported source | Document; exact immutable originals, with extraction/provenance relations |
| Charts, CSV/JSON results, exports, OCR and converted representations | Document; explicit snapshot/derived-resource provenance and completeness |
| Task List for one run, diagnostic/tool results, compaction checkpoint | Chat/run; do not advertise them as reusable document deliverables |
| Unsent upload | Chat draft until Send commits it |
| Tool/Skill packages | Existing catalog owner, independent of document/chat deletion |

A request to create an MD document creates an authored Markdown resource. A normal
Markdown-formatted reply remains a message. Multiple MD documents and multiple
HTML workspaces must coexist; do not implement documentation by overwriting the
single active Plan or adding MD text to the single active HTML workspace.

A chat keeps its selected working-set links, not copies or private resource heads.
`Attach from document library` and `Continue in new chat` retain the logical
identity. `Create independent copy` explicitly creates a new logical identity and
source relation. Forking/editing dialogue must neither clone the shared workspace
nor rewind its head. A message always retains the exact revision it referenced.
Sharing across *different* Office documents is outside this cutover: an explicit
copy/import with dependencies is required, without granting access to another live
Office target. Existing document identity rules own Save/Save As/reopen semantics.

### Publication, races and retention

Reuse the existing resource authority/revision journals and CAS. Add the missing
domain publication semantics there; do not scan all chats into an authoritative
library, keep a mutable library JSON file, or dual-write artifact bodies/heads to
both the conversation and document owner. Chat events retain refs, origin and
accepted operations; UI and working-set projections are disposable.

Each logical artifact has its own current head and immutable parent lineage.
Runtime binds a selected semantic target to the exact logical identity and observed
revision. Under the document mutation gate, recheck that revision immediately
before dispatch and publish the complete verified revision and head atomically.
Do not hold the gate during model/user waits. An intervening write returns a
conflict with the read/compare recovery route, never a silent overwrite or replay.
Unrelated artifact edits should not conflict merely because the document generation
advanced; per-artifact guards still publish against a coherent authority snapshot.

HTML members and binding refs form one revision. Historical bindings stay exact;
a new source head does not rewrite them. An unavailable source or changed binding
must remain explicit. Switching a chat/resource cannot replace a dirty editor's
base guard. Late reads/uploads must be rejected by the existing ownership leases.

A resource commit can survive a failed subsequent chat-link save. It remains in
the document library with the source attempt identity, and recovery can reconcile
the reference without rerunning a mutation. Do not report the entire operation as
completed before the required publication/link barriers are durable. Deterministic
attempt identity prevents duplicate publication after an uncertain acknowledgement.

Deleting a chat removes its links/history according to the chat operation, never
its document's resources. Removing a resource is a separate explicit tombstone
operation with incoming-reference checks. Authority revisions, original binary
payloads, source dependencies, pinned message refs and unresolved attempts remain
CAS roots. The current attachment-to-message lookup and chat-local GC roots must
be replaced before deleting an originating chat can be declared safe.

Existing incompatible streams remain preserved and explicitly reset/skip; no
hidden rebase, fallback or migration. No old body is deleted by format rejection.
Any explicit import is a separate operation with checked source provenance.

### Discovery, descriptions and model context

Use `common.resources_find/read` and existing Gateway/evidence/compiler owners.
Discovery needs document artifacts, selected chat resources and explicitly queried
history; it must not silently search another document. Search current artifacts by
default. Historical versions/changes and origin messages are separate requested
views so old versions do not crowd out the current deliverable.

Three levels keep context useful without loading the library wholesale:

1. A bounded current working set: complete semantic target, type, purpose and
   revision/currentness metadata projected by runtime. Never truncate a target.
2. Search over names, descriptions, section headings and indexed content, returning
   relevant snippets and honest coverage/unavailability. Zero matches in a partial
   search does not mean no resource exists. Index generation must be matched to
   the frozen authority generation or explicitly marked incomplete.
3. Exact reads of requested sections, history and changes. Search snippets and
   descriptions never grant a whole-read mutation guard or claim full source
   coverage. Rename/deletion/ambiguity returns an explicit rediscovery route.

Implemented working-set context (2026-09-08): `ChatResourcePromptIndex` advertises
document/conversation ownership and a next-read representation for every admitted
row. It explicitly identifies these entries as potentially historical snapshots;
selection roles and authored descriptions do not prove currentness. Current/shared
discovery uses `common.resources_find`, followed by `common.resources_read` with
the returned target. The prompt index does not derive currentness from timestamps
or create a second authority projection.

Authored Markdown purpose is optional context: its projection is bounded to 240
characters with an omission marker, sanitized and JSON-quoted. An over-budget
description is dropped before its complete target; malformed optional description
metadata yields an explicit unavailable label instead of aborting the prompt.
Durable metadata is unchanged. Bodies and query snippets stay in their existing
read/search paths. When the compiler excludes a stale/unavailable read, it retains
only the semantic target for recovery and an explicit rediscover-then-read action;
old source text and section titles do not become current evidence. Real model
behavior remains unqualified; richer cross-chat decision claims remain separate.

A resource's authored description records its purpose and scope; it is not a
truncated body. A generated synopsis is a derived observation bound to the source
revision, coverage and producer. Keep the complete submitted description durably;
only its display/context projection is bounded and marks omissions. A stale or
failed synopsis must not hide a valid artifact or masquerade as current content.
Use small relevant descriptions and deterministic headings first; embeddings or
an additional model call on every save are not prerequisites.

`message` remains visible prose in conversation-response v5. For a tool turn it
should briefly connect an observed finding, the purpose of the actual upcoming
calls, and the question their result will resolve. Empty parts are omitted. It is
not private reasoning, a fixed multi-section essay, proof of effect, or a source
for parsing runtime lifecycle/guards. Runtime already owns accepted calls,
dispatch/read-back evidence and exact source associations.

Compaction retains supported findings, decisions, unresolved questions, constraints
and next actions with source provenance. A valid `sourceIds` link checks provenance,
not semantic entailment: model claims remain interpretations. Do not relabel a
promise as completed work or promote resource instructions to user requirements.
Typed findings/decisions belong to the existing claim/compiler contract, with
source-role validation and stale-evidence exclusion; never extract them by parsing
ad-hoc headings from `message`. Shared decision memory should be a
versioned resource with source citations, not an untraceable global summary.

Implemented prerequisite (2026-09-08): `context-claims-v4` compaction carries
`kind` plus runtime-derived `SourceRoles`. Kinds are `constraint`, `decision`,
`observation`, `interpretation`, `question` and `next_action`. Constraints and
decisions require only genuine user-message sources or prior claims of the same
kind. Observations require successful Tool Results with canonical resource evidence
or prior observations. Tool Result envelope roles do not change their source role.
Recompaction cannot promote a prior interpretation into a decision/observation.
The compiler retains these distinctions and filters changed source evidence using
the existing frozen authority/reducer; unrelated claims survive. These checks
establish source eligibility, not semantic entailment or proof of user approval.
Invalid extraction preserves the prior checkpoint. Older/untyped checkpoints are
retained but skipped, leaving original messages available for fresh compaction.
This is used by current chat compaction; document-owned publication, discovery and
explicit cross-chat consumption of decision memory are not implemented yet.

### Resource Fabric boundary audit — 2026-09-08

The HTML owner cutover must reuse the Resource MASTER's three canonical contracts.
`DocumentArtifactStore` is an artifact-domain facade over the existing
`IResourceAuthorityStore`, `IResourceRevisionStore` and CAS. A partial source file
is not a new physical store, but duplicating registration/read rules by artifact
kind is still unnecessary. Its common `RetainRecord` / `ReadRecordSnapshot` path
serves Plan and authored HTML/JSON metadata and bodies; the former Plan-only record
implementation is removed. Existing Plan record view, provenance fields and
publication barriers are retained. HTML publication uses this common path.

The discarded, unconnected `DocumentArtifactStore.Html.cs` draft must not return
as an independent HTML metadata/read/receipt subsystem. The HTML slice follows
these boundaries:

- Keep aggregate assembly and binding semantics in the HTML domain owner. Retain
  its immutable artifact record through the common facade, with `PayloadRef` and
  typed `ResourceDependency` provenance in the existing revision store.
- Keep guards, operation receipts, unknown outcomes and atomic effect/head/
  generation publication in the existing mutation observer/authority journal.
  Do not build an HTML-specific authority or freshness registry.
- Keep reads, schema/mapping resolution, derived resources and leases on the
  existing Gateway/providers/data plane. Document ownership alone does not justify
  an HTML-specific JSON store, copying every dependency, or banning supported
  `head` bindings. Historical/exact views and live bindings follow Fabric semantics.
- Publish restore through the existing mutation protocol as a new logical revision
  with parent/restored-from provenance. Changing a chat selection or editing/forking
  dialogue must not publish a rollback of a shared resource head.

The unregistered draft is removed. Shared HTML now uses the common owner, tools
and consumers above. Markdown uses that path too; indexed discovery remains open.

### Required implementation slices and acceptance

HTML/Markdown cutover audit (2026-09-08): the shared HTML slice switches these assumptions
together with the domain owner, not ahead of it:

- `ChatArtifactResourceProvider.ResolveIdentity` must preserve the exact resource's
  owner when constructing a member URI; its current `session.Id` construction is
  was valid only for chat-owned HTML; HTML now preserves document ownership. Check member round-trip from a
  second chat of the same document.
- Extend `DocumentArtifactStore.Owns` to HTML/independent Markdown only when their
  document publication/read owners are active. Fork/history rebase consumes this
  predicate; verify that shared references survive without copying or rebasing.
- HTML controller loads now address an explicit chat without activating it.
  The control-action correction below pins selection/version before dispatch;
  document ownership must retain these guards and replace the chat-local head
  with the new domain's exact head. Verify real document switches on Windows.

HTML control-action correction (2026-09-08), before the ownership move:
`HtmlWorkspaceActionPayload` carries `chatId`, `expectedActiveHtmlArtifactId` and
`expectedSessionRevision` for delete file/data, entry selection, import, export,
restore and redo. Missing guards fail explicitly; an empty snapshot string is
accepted only for an actually empty workspace. The reserved addressed session
must belong to the currently bound document. `HtmlWorkspaceActionGuard` checks
both snapshot and chat revision after reload and again inside the existing
mutation gate before dispatch. This detects returning to the same snapshot after
an intervening chat change. Refusal abandons preparation without marking an effect
unknown or publishing a new revision. Intent payloads retain both guards.

The UI captures these controls before prompts/confirmation and checks navigation,
local edit version and the accepted chat projection revision before send/apply.
Duplicate controls and competing editor uploads are suppressed while pending.
A late response never clears selection or new drafts; export capabilities are
closed using their original chat/checkpoint even when its projection is discarded.
No mutation is automatically retried. Existing binary-upload saves retain their
exact snapshot/authority guards. Unversioned bridge/controller action signatures
are removed. `html actions:` harness checks and
`tests/web/html-workspace-actions.test.js` cover refusal before dispatch, typed
transport, duplicate clicks, navigation ABA, local edits and stale export cleanup.
Production controller/document switching and WebView layout still need Windows
qualification. This guard prerequisite preceded the implemented HTML ownership move.

| Order | Owner and replacement | Required evidence |
|---|---|---|
| 1 | Core resource authority/revision storage + artifact domain owner: move durable artifact identity, original metadata and head publication out of `ChatSession` | Two chats and fresh store instances read one resource; concurrent stale write rejected; commit/link crash reconciliation; restart and CAS retention |
| 2 | HTML/Plan/authored Markdown owners + tools: explicit selected artifact, multiple documents/workspaces; remove chat-only lineage, fork copy/rebase and active-head authority | A creates, B edits, A conflicts; old message remains exact; two MD/HTML artifacts stay independent; unrelated resource writes can both succeed |
| 3 | Gateway/provider + discovery + frozen compiler: document library access, descriptions, working set, exact history and source search | Search result round-trips; duplicate names, rename, >page/scan bounds, missing synopsis/blob, source mutation between find/read/write, compaction and stale decision coverage |
| 4 | Library/bridge/viewer/editor + chat lifecycle + GC: attach/continue/copy, revisioned notifications and protected drafts; remove old chat-owned consumers | Chat switch/upload/save race; lost notification recovery; deletion/fork/edit of origin chat preserves shared originals and dependencies; cross-document access refused |

Each slice must switch its active consumers and remove the replaced path; no
unused future contracts. Windows/Office/WebView2 delivery and target-model traces
remain required. All four slices are required before claiming that MD/HTML can be
continued across chats. None of the discovery corrections below closes that gate.

Design rationale: lightweight discovery followed by selective reads, concise
operational context, and provenance-aware compaction follow the primary guidance
in [Anthropic context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)
and [tool design](https://www.anthropic.com/engineering/writing-tools-for-agents).
The document owner, publication barriers and guards are RNAssistant-specific
choices derived from the existing resource architecture, not claims from those
articles. No remote runtime or cross-document memory service is introduced.

## Principles

- A staged file is a chat-scoped draft, not a durable artifact and not model
  context.
- Send promotes selected drafts to immutable CAS-backed revisions and binds their
  exact `ResourceRef` values to the user turn before any model dispatch.
- The append-only chat event stream remains the durable source of truth. The
  Artifact Library is a revision-guarded projection, not a writable index.
- File extension does not grant mutation rights. An uploaded Markdown or HTML
  file is an immutable original; an authored Plan, Markdown document or HTML
  workspace is a versioned domain object.
- Messages always retain the exact revision they cited. The library may show the
  current head, but must never silently redirect an old message to that head.
- UI viewers use bounded representations from the existing resource gateway via
  typed bridge DTOs. They do not read CAS paths, invent a second URI or grant
  execution authority.

## Attachment lifecycle

| State | Durable | User-visible behavior | Model visibility | Removal |
|---|---|---|---|---|
| Draft | No | Composer chip; an Artifact Library implementation may repeat it only in a separate `Drafts — not sent` group | None | Immediate discard of staging bytes and metadata |
| Preparing | Not yet | Pending user turn; draft remains recoverable until the durable save succeeds | None | Failure returns the same draft to retry state |
| Committed | Yes | Message card and Artifact Library entry appear from one revisioned post-commit projection | Exact reference plus bounded current-turn materialization | Explicit message/resource operation only |
| Run failed or cancelled after commit | Yes | The committed user turn and resources remain visible; model failure never rolls them back | Available to later turns through resources | Same as any committed resource |
| Removed | Append-only tombstone/projection change | Placeholder remains where history cited the resource; it is absent from new library heads | No new working-set admission; document Plan exact snapshots remain readable historically, other domains retain their removal contract | CAS GC only after verified reachability proves no live reference |

Paste, drag-and-drop and the paperclip use the same staging action. Pasting
ordinary text remains composer text; only clipboard file/media items create
resource drafts.

If Send is requested while picker, drop or paste staging is still in flight, the
composer waits for that chat's serialized staging queue before it snapshots the
draft IDs. A staging failure keeps the text and successful drafts available for
retry and starts no partial text-only model request.

The current limits remain 10 files per message, 20 MiB per file and 50 MiB total
unless a later bounded contract changes them explicitly. Supported uploads are
signature-validated images, PDF, MP3/WAV and safely decoded text-based files;
arbitrary binary files are not accepted merely because of their extension.

### Commit-time UI boundary

After CAS storage, artifact/message linking and mandatory chat save succeed, the
controller must synchronously queue one full chat projection carrying the new
`sessionRevision`, message refs and artifact heads before starting the first model
transport call. A connected active UI applies it through the existing monotonic
per-chat revision guard. Model execution does not wait for a WebView acknowledgement;
a missed best-effort delivery is recovered by selecting/reloading the chat and never
affects durability.

The same boundary applies to artifacts created while a conversation run is active.
After each durable `tool_result` checkpoint, including a continuation after explicit
confirmation, the controller queues the complete revisioned chat projection before
reporting further progress or starting the next model step. Thus chart/tool-result,
Plan, Task List and HTML artifacts do not wait for the terminal bridge response.
This reuses the same full projection and revision guard; progress messages are not a
second artifact transport.

Progress text, the local pending card, a generated chat title and model output are
not evidence that the resource was committed. Conversely, a model/provider error
after this boundary does not return the resource to draft state.

## Resource classes

| Class | Examples | Viewer | Mutation and versions |
|---|---|---|---|
| Immutable original | uploaded TXT/MD/source, image, PDF, audio, uploaded HTML | MIME/kind-specific read-only viewer | Display `Original`, not `v1`; editing creates an explicit derived editable copy |
| Immutable snapshot | chart, generated image, tool result, compaction checkpoint | domain viewer or safe source/metadata fallback | No editable head and no meaningless version badge |
| Versioned document | Plan, explicitly authored Markdown/text document | rendered preview plus exact source | Every save creates a new immutable revision under one logical document identity |
| Versioned aggregate | HTML workspace with HTML/CSS/JS/JSON members and bindings | sandboxed preview, source tree and data views | One revision captures the whole workspace; files are not independent artifact histories |
| Derived resource | OCR, transcription, PDF text, conversion/import output | viewer for the derived kind | Separate child resource with source ref, producer, parameters, time and content hash; never a disguised source revision |

An upload is not converted automatically. In particular:

- uploaded Markdown/TXT is readable source, not the active Plan;
- uploaded HTML is inert source and is never executed by opening the resource;
- `Import into HTML workspace` creates a separate versioned workspace with
  explicit source provenance;
- `Create editable copy` of uploaded text creates a separate authored document;
- uploaded `SKILL.md` or a skill archive remains an untrusted immutable artifact;
  explicit confirmed `Install as skill` creates a separate global/host-scoped
  Library package revision with source provenance;
- reusable OCR/transcription is a derived resource, while query-specific helper
  analysis remains evidence for that model step only.

Installed skills are intentionally excluded from the Artifact Library. They are
trusted capability packages shared across chats and belong to `Library → Skills`;
chat deletion must not delete them and skill deletion must not rewrite chat history.
Agent skill mutations may render a UI-only link to the Library item, not an artifact
card or second model transport. See [Skill Library](skills.md).

## Preview refresh cost

HTML preview retains its iframe and open resource leases across repeated metadata
renders and tab revisits when the exact source/binding inputs and chat/workspace
owner are unchanged. Source or binding changes, owner changes, unavailable source
and edit/detail transitions invalidate reuse; replacement closes the previous
leases before installing the new document. The reuse key is transient UI state,
not resource authority. Real Office/WebView2 responsiveness remains unqualified.

## Library and revision display

The September 8 UI correction labels ownership from the typed
`ChatArtifactDto.documentScoped` projection: `Общий для чатов документа` for
published document resources and `Только этот чат` for chat-owned artifacts.
This reflects implemented ownership (originals and Plans); it does not migrate HTML
or old chat-local records. The tree uses one active selection and puts metadata
below the title. Markdown has one source/document toggle inside the existing
Preview/Details shell.

A run's resource cards collapse revisions only by the Library's logical identity,
showing the highest referenced revision in that run. Independent same-name files
remain separate. Exact message refs and Library history remain unchanged; a card
never silently advances to a newer revision from a later run.


The Artifact Library tree shows one row per immutable resource or logical document
head, grouped as authored documents, files/media, generated snapshots and system
evidence. A group title is also a selectable collection: selecting it opens the
current filtered/sorted resources as a responsive grid, while its expander continues
to control the tree. Drafts, when shown, are always separated and labelled
non-durable. The grid is another view over `artifactLibrary.heads[]`; it is not a
second projection or durable store.

Each row exposes title, type, size where meaningful, source turn, created/updated
time and exact-reference copy. Versioned documents additionally expose current
`vN`, status and a history action. Immutable resources use `Original`; derived
resources show `Derived from …` rather than a version number.

History for a versioned document shows every immutable revision, its exact URI,
parent, time, source run/user action and restore relation. Plan revision numbers are
strictly monotonic and linear. HTML may branch through undo/redo, but revision
numbers remain unique and monotonic for the logical workspace and the active branch
is explicit.

Restoring never modifies an old revision. Plan restore creates a new head revision
whose body equals the selected revision and records `restoredFrom`. HTML may move
the active pointer through an explicit undo/redo branch operation; the next save
creates a new child and the UI keeps alternative branches visible. No revision is
silently overwritten or renumbered.

The current bridge projection is
`artifactLibrary { sessionRevision, heads[], removedResourceUris[] }`.
`ArtifactLibraryProjectionService` derives it from the replayed `ChatSession`; it is
never persisted separately. Each head carries the server-owned class, group,
normalized display kind, exact head URI and history entries with exact parent/
restore relations. HTML selects `ActiveHtmlArtifactId`, including an older undo or
branch target, instead of guessing the largest revision. The raw `artifacts[]`
projection remains available only for exact message cards and existing viewers;
the client no longer computes library lineage from it. Direct HTML editor responses
carry the same revisioned library projection so save/undo cannot leave the library
stale until reload.

Message cards resolve their pinned revision even when a newer head exists. If the
resource was explicitly removed, the message shows a stable `Resource removed`
placeholder rather than falling forward to another revision.

### Collections, thumbnails and sequences

Collection cards load media lazily near the viewport. Image thumbnails use a
separate typed exact-revision read: the source attachment URI/hash/length is checked
as for the full viewer, then a local Skia provider returns JPEG at no more than
320 px and 512 KiB. The UI admits the returned source hash, thumbnail hash, byte
length and dimensions, runs at most four reads concurrently and retains at most 48
ephemeral results per chat. Thumbnail state is cleared on chat switch and is never
an artifact, revision, message event or model-facing resource.

Messages render exact committed image refs as a compact mosaic of at most four
cells; additional images use a `+N` overlay. Opening a cell carries every exact image
ref from that message into an ephemeral gallery context. Opening an image from a
Library collection carries the collection's current filtered/sorted image refs.
Previous/next navigation and the thumbnail filmstrip therefore do not guess latest
revisions or require repeated tree clicks. A corresponding generic attachment tile
is suppressed only when its exact image artifact identifies the same attachment.

`RNAssistantSequenceViewer` is the reusable UI-only sequence shell. It owns bounded
virtualized vertical/horizontal rails, current position, direct selection, optional
numeric jump and keyboard-compatible navigation, but owns no bytes, bridge calls or
format semantics. The format provider supplies item count, labels, thumbnails and
selection callbacks. PDF pages use this shell vertically; image galleries use it
horizontally. One PDF or future presentation remains one artifact: its pages/slides
are preview frames, never child artifacts or independently durable revisions.

## Viewer contracts

- Text/source: fixed 32,000-character pages within a 512,000-character viewer
  document bound, line numbers, page search/copy and exact full copy/download only
  after a contiguous stable-URI/hash/total read. A truncated extracted source or an
  over-limit document remains explicitly partial.
- Markdown: rendered/sanitized view plus exact Source. Rendering is disabled until
  the full exact source is available. Plan uses this viewer and is never labelled
  JSON; a dirty Plan preview is explicitly a non-durable draft.
- Image: the exact revision-pinned JPEG/PNG/GIF/WebP bytes are read from attachment
  CAS through a typed bridge under the existing 20 MiB attachment bound. The
  locally vendored Viewer.js 1.12.0 receives one admitted Blob image and provides
  proportional fit with upscaling, 100%/button/wheel/pinch zoom, pan, rotation,
  Fit/100% double-click toggle and keyboard shortcuts. RNAssistant retains natural
  dimensions, download and teardown. The stage occupies the remaining preview
  height without cropping or distortion. Collection/message thumbnails and the
  horizontal gallery filmstrip use the separately bounded thumbnail contract above;
  selecting an item loads only that exact full image into Viewer.js. Main-UI CSP
  admits only local `data:`/`blob:` image sources and denies vendor network/worker
  access.
- PDF: exact revision info exposes the PdfPig page count plus hash/count/completeness
  evidence and an explicit truncation/scan warning; it does not return the whole
  extracted body. Text uses the same exact viewer paging as other sources: 32,000
  characters per read within the 512,000-character viewer ceiling. Storage ingestion
  may retain up to 1,000,000 extracted characters, so a larger or incomplete
  extraction remains explicitly partial in the viewer. Page navigation renders one
  requested page at a time to JPEG through the separately admitted local
  PDFtoImage/PDFium/Skia path, bounded to 2,048 px and 10 MiB per page. The same
  Viewer.js instance type displays that verified JPEG. A separate typed thumbnail
  read renders at most 320 px / 1 MiB; the shared vertical sequence rail exposes
  direct page selection and a numeric jump without constructing 10,000 DOM rows.
  PDF thumbnail rendering is capped at four concurrent reads and 24 ephemeral
  cached results.
  The viewer defaults to pages, keeps extracted text on its own tab and also uses
  centered hover/focus arrows, a persistent page position and Left/Right navigation.
  Matching exact-package PE32+ x64 and PE32 x86 native libraries are vendored and
  selected by process architecture. A native x64 DLL is not admitted into an x86
  Office process; managed caller bitness does not remove that native loader boundary.
  At most two media viewer states remain in the per-chat cache; main-page and
  thumbnail Blob URLs are revoked on tab, selection, chat and window teardown.
  Repository wiring is not execution evidence: real Windows x64/x86 Office/WebView
  import, preview, thumbnail rail, scanned-page and model-send qualification remains
  open.
- Audio: local bounded player and optional transcript relation; no autoplay.
- JSON/chart/tool result: existing lossless bounded JSON/domain viewers remain
  owners of their formats. `application/vnd.rnassistant.chart+json` artifacts
  render the ECharts chart viewer in Preview and keep the exact JSON payload in
  Details; if the domain viewer is unavailable, the safe JSON fallback remains.
  Message-backed chart controls capture the source chat/message identity when the
  viewer is created. A delayed save or Excel refresh persists only to that chat and
  never rewrites whichever chat becomes active while the operation is in flight.
  The refresh tool call carries that same exact chat through the typed bridge;
  manual tool execution without a chat owner is rejected instead of falling back
  to the currently selected chat.
  Agent run resource cards do not count a supporting HTML workspace as a second
  visible resource when the same run also exposes a chart artifact; HTML-only runs
  still expose the workspace.
- Uploaded HTML: escaped source only, loaded on demand through the shared exact
  text viewer/data plane. The original remains inert; rendered preview requires
  explicit import into the HTML workspace. Untrusted upload source is never
  inserted into the main DOM or granted network access.
- HTML workspace: sandboxed rendered preview, exact HTML/CSS/JS editors,
  ResourceRef bindings, revision/branch history and export. Hosted network access
  retains its explicit allowlist. The unified [Resource Fabric](resource-fabric.md)
  replaces eager dataset JSON, table aliases and independent last-good state.
  Page code opens explicit names through `RN.resources`, then reads bounded exact
  batches/streams. Refresh reconciles source authority and reopens head bindings;
  it does not manufacture a workspace revision or replace an exact binding. A
  workspace whose
  HTML/JavaScript references `echarts` receives the exact local ECharts 5.6.0 bundle
  as classic JavaScript before workspace scripts in its sandbox and standalone
  export. The tree projects that runtime as the read-only
  `Dependencies/echarts.min.js` item; it is not a user-editable workspace member or
  a second durable artifact. Ordinary workspaces do not carry it, and Chart.js/CDN
  loading is unsupported. Full-document assembly inserts workspace scripts against
  the original document's last closing body/html tag before adding the vendor head
  block, so tag-shaped strings inside the bundled source cannot capture the
  insertion. Standalone export pulls exact resources through the same data plane
  into bounded inert snapshot parts, served lazily by the same `RN.resources` API.
  It has no live Office/network fallback; incomplete, oversized or mixed-revision
  capture prevents download. Missing/tampered parts and an unavailable pinned
  ECharts runtime fail explicitly. Actual downloaded-file/WebView2 qualification
  remains open.

ViewerRegistry remains UI-only dispatch. Fetching bounded text/media and checking
the exact revision belong to the Artifact Library owner and the shared resource
gateway. Viewers receive already authorized data plus completeness metadata and
cannot call tools, bridge, CAS or network themselves.

The extension boundary is provider-based, not a universal viewer dependency. A
future PowerPoint artifact provider may expose exact presentation info, one bounded
rendered slide, bounded slide thumbnails and separately evidenced extracted text to
the existing sequence shell. It must select and qualify its local conversion SDK,
font/layout behavior, input/output/memory bounds and Windows x64/x86 support as its
own slice; no PowerPoint bridge contract or vendor is predeclared here. A live
browser remains the separately permissioned 11L session/package and never reuses an
artifact or HTML-preview WebView as browser authority. Only an explicit browser
snapshot/download may enter the Library as an immutable artifact with provenance.

For 11D1/11D2, `ArtifactViewerService` accepts only a canonical revision-pinned URI from
the active chat and returns a typed page projection over `ResourceGatewayService`.
Attachment text pages carry the extracted-text hash, never the source binary hash.
The screen owner validates contiguous offset, representation hash, total and viewer
kind before granting full-source actions. Page state is ephemeral, bounded to eight
selected resources and cleared on chat switch; it is not an event, artifact revision
or persisted index. Image reads additionally require one exact source-message /
attachment identity, matching kind/MIME/hash/length and a recomputed binary SHA-256
before the payload reaches WebView. Uploaded HTML originals use the same inert text
viewer with exact retained attachment evidence, including when a title resembles
Markdown. Executable HTML workspaces and JSON remain with their specialized owners.

The uploaded-HTML source bridge/DTO and its separate eight-entry preview cache are
removed. Source reads return metadata/leases only; bounded pages, exact extracted
text hashes, continuation checks and lease cleanup share `ArtifactViewerService`
and the existing viewer cache. Text reads and imports require an explicit chat,
without selecting another chat or falling back to the active one.
`UploadedHtmlResourceService` imports through exact Gateway text pages, not a direct
attachment callback. Retained text evidence and the existing 300,000-character
import limit are checked before hydration; contiguous pages, total length and the
complete extracted-text SHA-256 are verified before any workspace revision is
created. Missing, corrupted or truncated text cannot be imported as empty/partial
content. Original binary identity, source provenance, target-path collision and
active-workspace guards remain unchanged. Real Windows/WebView source/import
qualification remains open.

Artifact detail is preview-first. Plan/Markdown renders as a document, Task List as
goal/progress/steps and image as media; domain JSON remains a domain viewer. Generic
metadata, raw Task List/JSON payloads and revision history live under `Details` and
do not precede or replace the primary preview.

For 11D3, PDF info, full page and thumbnail are separate typed bridge calls. All bind
the same canonical artifact URI, original binary SHA-256 and page count. Extracted
text stays on the existing typed viewer-page call and is bound by its separate
extracted-text SHA-256, total length, cursor and contiguous offsets; the UI
cross-checks info/text/full-page responses before admitting the preview and checks
each thumbnail against the admitted source hash/count. Render calls accept only a
zero-based index inside the PdfPig count and return JPEGs whose signature, size and
dimensions are checked before WebView. Native load or machine-type failure is an
explicit renderer-unavailable error and is never retried automatically.
`ArtifactPdfViewerService` owns PDF admission and both PDFtoImage render sizes; the
generic `ArtifactViewerService` only delegates that format and keeps shared exact
text paging. Viewer.js is UI-only and receives no PDF bytes, bridge handle or URI.

PDF.js is intentionally not part of this 11D3 implementation. Its full viewer is a
separate browser application, while the admitted WebView contract currently receives
one hash-checked, bounded JPEG page plus separately bounded extracted text rather than
the original PDF bytes. Adopting PDF.js requires its exact local viewer/worker assets,
raw-PDF bridge admission and memory bounds, CSP/worker policy, cache teardown and
Windows WebView qualification as one separate cutover. It must not be presented as a
cosmetic drop-in or silently run beside PDFtoImage. Official references:
[PDF.js project](https://github.com/mozilla/pdf.js/blob/master/README.md),
[viewer options](https://github.com/mozilla/pdf.js/wiki/Viewer-options) and
[binary-data opening](https://github.com/mozilla/pdf.js/wiki/Frequently-Asked-Questions).

## HTML editor source downloads

Init, chat-state, mutation and export responses use body-free
`HtmlWorkspaceFileDto` entries: id/path/kind, exact existing HTML member `ResourceRef`,
byte/character length and SHA-256. `HtmlWorkspaceDto.revisionArtifactId` identifies
the displayed workspace, independently of a newer published active pointer.
`HtmlWorkspaceEditorResourceService.Metadata` is the single projection owner;
no response clones current file content or reintroduces an inline read fallback.

The selected editor file is pulled on demand. Preview and export require the
complete bounded workspace source set before assembly; missing files are errors,
never empty substitutes. The same `html-editor` download consumer uses the existing
Gateway/Chat HTML member provider and complete immutable CAS views, then the shared
sequential byte downloader. Each background read retains only a copy of its exact
parent artifact, not the active run's mutable artifact list. Source transport is
inert UTF-8; it neither executes HTML nor adds model evidence. Historical exact
reads do not switch the active workspace, and opened leases retain their snapshot.

Per-file bounds remain 300,000 characters / 1,200,000 bytes; the current workspace
cache is limited by the existing 100-file and 1,500,000-source-character bounds.
There is one source producer, coalesced demand/cancellation and no automatic retry
after read/integrity failure. Late leases close in the original owner. Chat/page
cleanup, exact resource/hash/length checks and strict decoding precede hydration.
Unloaded or not-yet-rendered source cannot be synchronized from an old editor
placeholder into a draft, edited or saved as empty.
Same-chat metadata pushes preserve dirty source and its original revision guard;
they cannot silently rebase it. `Исходники ↻` explicitly reloads/retries, confirms
discarding dirty edits, and preserves edits made while the reload was in flight.
Real Windows/WebView2 render, reload/cancel, export and multi-window qualification
remain open.

## HTML editor uploads

HTML/CSS/JS Save/create and JSON Save/create use the same shared bounded upload
route. Bridge controls carry an explicit chat, target path/kind or data name,
the displayed `expectedActiveHtmlArtifactId`, upload lease and complete SHA-256;
inline `content`/`json` controls are rejected. An empty expected id is valid only
for an absent workspace. `HtmlWorkspaceEditorResourceService` consumes a single-use
`html-editor` capability, verifies complete strict UTF-8, and delegates to the
existing `HtmlWorkspaceToolService` through `OfficeToolExecutor.MutateLocalResources`.
The durable prepared intent uses the existing CAS and records the expected logical
revision plus editor guard. Under the conversation mutation lease, the current
Known publication must still match its exact immutable workspace snapshot; stale,
Unknown, invalid-path/JSON and capacity failures cannot dispatch. The existing
read-back/persistence/authority barrier remains the only publication owner.

Reservation is at most 1,200,000 UTF-8 bytes; existing per-source 300,000-character
and aggregate workspace limits remain. Empty files are complete replacements;
empty/invalid JSON is refused. The browser has one in-flight writer, sequential
acknowledged chunks, chat/page cancellation and late-lease cleanup. Changed drafts
prevent dispatch; edits made after dispatch are not replaced or silently rebased
by Save's acknowledgement. Lost/late responses require explicit refresh/review,
never automatic retry. Creation cannot discard an existing dirty draft. Upload
alone creates neither an artifact nor model evidence. Outgoing source, preview and
export hydration use the shared [source reader](#html-editor-source-downloads).

## Edit and delete semantics

Only domain-owned mutable resources expose Save/Delete:

- Plan Save writes the complete Markdown payload as a new revision; runtime resolves
  and enforces the exact-current guard. Restore selects a readable version and runtime
  copies its exact historical revision into a new guarded head. Delete appends a
  tombstone for the logical Plan only after an explicit warning; it does not erase
  prior revisions or message references.
- HTML Save/delete/bind operates on exact workspace members and publishes a
  complete new workspace revision. Save uses the guarded upload contract above.
  Refresh reconciles resource authority without manufacturing a workspace revision;
  a failed refresh is explicit, not an independent last-good JSON authority.
- Immutable uploads/snapshots have no in-place editor. `Create editable copy` or
  `Import` creates a related resource and leaves the original unchanged.

`Office.Services.PlanDocumentService` owns Plan domain rules; the document
authority owns durable lineage as specified in the implemented slice above.
`common.plan_doc_save` validates non-empty title/Markdown/status without normalizing
the Markdown: leading/trailing whitespace and hard-break spaces are stored exactly.
The service creates a plan when absent; otherwise it resolves the active exact
artifact and appends `vN+1` as its linear child. Committed duplicate, skipped or
branched lineage fails closed; disposable chat metadata is rebuilt from that owner.
`common.plan_doc_restore` accepts only a user-visible version; the
service binds the same exact-current guard, resolves one exact non-tombstone revision
and appends it as `vN+1` with `restoredFromArtifactId` provenance. Argument-free
`common.plan_doc_delete` resolves the exact current head and appends a `removed:true`
child revision while clearing the active pointer. Runtime-only ids/guards remain in
durable evidence and are removed from the model projection. Historical `ResourceRef`
values are never rewritten. Discovery and the new working set omit the removed
Plan; document-owned historical exact snapshots remain readable. Its tombstone
is document-owned, independent of origin-message removal and dialogue forks.
The older chat-local format is not migrated and cannot be mutated by the new
native Plan binding.

Draft discard deletes only staging data. `Hide from library` is a UI preference and
does not alter history or model references. Destructive removal of a committed
resource first displays every referencing message/document revision and either
refuses the operation or explicitly includes those references in the same append-
only mutation. It appends removal/tombstone facts; it never rewrites the JSONL
stream. Physical CAS deletion is deferred to the existing fail-closed reachability
GC. Clear Chat/Data remains a separate explicit operation.

## Model context

- Drafts never enter prompt inspection, resource indexes or model requests.
- The committing turn receives bounded extracted text, supported media and readable
  semantic targets according to model routing; exact durable refs remain stored on
  the user message but do not enter model context.
- Later turns receive only the bounded working-set manifest. Bodies are loaded on
  demand through `common.resources_find/read`.
- The active Plan and HTML workspace are advertised by readable semantic targets;
  their exact refs and bodies are not injected on every step.
- Compaction preserves a deterministic bounded union of semantic targets and may
  discard hydrated bodies/read results. Runtime reconstructs a later read from
  durable exact evidence.
- Existing resources require no `В запрос` dual transport. A future `@resource`
  affordance may insert an exact ref for disambiguation only.

## Delivery and acceptance

Phase 11 is implemented as separate changes:

1. Artifact lifecycle/library foundation — done host-neutral in 11A1/11A2: draft/
   committed UI states, commit-time revisioned projection, exact head/history
   presentation and current kind/label cleanup. Windows WebView qualification stays
   open.
2. Plan, separate changes:
   - 11B1 — done host-neutral: Markdown preview/source uses an exact payload, one
     domain service owns linear whole-content revisions, and stale or broken heads
     fail before append.
   - 11B2 — done host-neutral: append-only restore-as-new-head and guarded tombstone
     removal preserve exact historical message refs and project `resource_removed`.
     [Evidence](stabilization/PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md).
   - 11B3 + R61/11O1–11O2 — done host-neutral: historical revisions expose semantic
     version restore while runtime binds the exact head/source; removal preflight
     lists every referencing message before confirmation; ready handoff revalidates
     internally and submits only a readable semantic target, never a URI or artifact
     id. [Baseline evidence](stabilization/PHASE_11B3_PLAN_HISTORY_HANDOFF.md).
3. HTML, separate changes:
   - 11C1 — done host-neutral: every whole-workspace save uses the next revision
     number across all branches, retains the exact active parent, and refuses
     duplicate/invalid lineage before mutation. The explicit active pointer remains
     authoritative after undo. [Evidence](stabilization/PHASE_11C1_HTML_LINEAGE.md).
   - 11C2 — done host-neutral: an uploaded HTML original remains immutable and
     inert; the UI obtains only an exact 32,000-character bounded source projection
     and inserts it with `textContent`. Explicit import requires the current HTML
     head, a new `.html`/`.htm` path and a complete decoded payload within the
     300,000-character workspace-file bound, then creates a separate workspace
     revision with exact source URI/hash/relation provenance.
     [Evidence](stabilization/PHASE_11C2_HTML_IMPORT_PREVIEW.md).
   - 11C3 — done host-neutral: storage cannot synthesize workspace revisions;
     export checkpoints the guarded workspace through the sole lineage owner.
     Its former inline binding-JSON transport is replaced by the unified Resource
     cutover: typed exact leases, bounded pulls and inert standalone snapshot parts.
     [Evidence](stabilization/PHASE_11C3_HTML_BINDING_EXPORT.md).
   - R61/11O3 — done host-neutral: model authoring uses separate semantic file/data
     writes, exact patch/delete and identity-free bind/refresh/freeze. Inspection and
     active selection are internal; bind now resolves the canonical resource target while
     URI/revision/hash/source arguments remain durable/runtime-only.
     [Evidence](stabilization/PHASE_11O3_HTML_SEMANTIC_INTENTS.md).
4. Typed viewers:
   - 11D1 — done host-neutral: exact bounded text/source paging, complete-only full
     copy/download and complete-only sanitized Markdown with exact Source.
     [Evidence](stabilization/PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md).
   - 11D2 — done host-neutral: exact allowlisted image bytes, dimensions,
     fit/zoom/download, bounded cache/object-URL lifetime and shared preview-first /
     Details layout for Plan, Task List, Markdown, image and existing domain content.
     [Evidence](stabilization/PHASE_11D2_IMAGE_PREVIEW.md).
   - 11D3 — done host-neutral: exact PDF info/extracted text, single-page bounded
     JPEG rendering/navigation, scan/truncation state and matching exact-package
     x64/x86 native vendor/publisher wiring.
     [Evidence](stabilization/PHASE_11D3_PDF_PREVIEW_X86.md).
   - Audio remains a separate measured slice with its own security and Windows gate.
5. The Artifact milestone closes only after one Windows WebView pass covers the
   Library, Plan and HTML together: reload, exact history navigation, stale
   revisions, viewer cleanup and bounded large payloads. Product-wide Problems and
   causal evidence links then belong to the Phase 11
   [Issue Center](qualification.md#11-phase-11-issue-center), not to artifact
   metadata or a new artifact class.

Minimum tests prove: a draft is absent from durable projection/context; commit and
UI projection precede the first fake model transport call; provider failure after
commit preserves the resource; message refs remain revision-pinned; stale UI state
cannot replace a newer projection; immutable uploads cannot be mutated; restore and
branch lineage replay exactly; removed resources do not silently resolve; viewers
respect bounds, MIME allowlists, clipboard/download failure and zero-network rules.
Real WebView2 image/PDF/clipboard/lifecycle behavior remains a Windows qualification
gate; host-neutral image behavior is implemented but does not close that gate.

## Run change presentation

Completed chat runs can compare retained authored text revisions and individual
HTML-workspace files. This uses existing lineage/CAS and does not alter Library
heads or resource authority. Scope, source fidelity and limits are defined in
[Conversation protocol — Run text changes](conversation-protocol.md#run-text-changes).
