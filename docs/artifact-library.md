# Artifact Library and Viewers

Status: Phase 11 target contract. 11A1 and 11A2 implement the host-neutral commit-time
boundary, explicit draft/preparing/committed labels and exact Library head/history
projection. 11B1–11B3 complete the host-neutral Plan domain owner, exact whole-Markdown
lineage, restore/removal UX and ready handoff by pinned URI. 11C1–11C3 complete the
host-neutral HTML lineage, inert uploaded-source import, binding evidence, recovery
and guarded exact export contour. 11D1 implements host-neutral bounded text/source
and complete-only sanitized Markdown viewers; image, PDF and audio remain later
independent slices. The existing Resource Fabric ingestion, CAS,
`ResourceRef`, provider and model-context semantics remain authoritative. This
document defines the user-visible lifecycle, viewers and mutation rules; it does not
introduce another artifact transport or store.

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
| Removed | Append-only tombstone/projection change | Placeholder remains where history cited the resource; it is absent from new library heads | No new working-set admission; exact reads report removal | CAS GC only after verified reachability proves no live reference |

Paste, drag-and-drop and the paperclip use the same staging action. Pasting
ordinary text remains composer text; only clipboard file/media items create
resource drafts.

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

## Library and revision display

The default Artifact Library shows one row per immutable resource or logical
document head, grouped as authored documents, files/media, generated snapshots and
system evidence. Drafts, when shown, are always separated and labelled non-durable.

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

## Viewer contracts

- Text/source: fixed 32,000-character pages within a 512,000-character viewer
  document bound, line numbers, page search/copy and exact full copy/download only
  after a contiguous stable-URI/hash/total read. A truncated extracted source or an
  over-limit document remains explicitly partial.
- Markdown: rendered/sanitized view plus exact Source. Rendering is disabled until
  the full exact source is available. Plan uses this viewer and is never labelled
  JSON; a dirty Plan preview is explicitly a non-durable draft.
- Image: local bytes only, fit/zoom, dimensions and download; object URLs are
  revoked on selection/chat/window changes.
- PDF: page navigation plus extracted-text view and scan/extraction warning. Any
  PDF.js/native renderer is a separately admitted local asset/runtime with bounded
  pages, worker lifecycle, manifest/license/CSP and Windows WebView qualification.
- Audio: local bounded player and optional transcript relation; no autoplay.
- JSON/chart/tool result: existing lossless bounded JSON/domain viewers remain
  owners of their formats.
- Uploaded HTML: escaped source only by default. Preview requires explicit import
  into the HTML workspace; untrusted upload source is never inserted into the main
  DOM or granted network access.
- HTML workspace: sandboxed rendered preview, exact HTML/CSS/JS/JSON editors,
  binding status, revision/branch history and export. Network origins retain the
  existing explicit allowlist and last-good binding behavior.

ViewerRegistry remains UI-only dispatch. Fetching bounded text/media and checking
the exact revision belong to the Artifact Library owner and the shared resource
gateway. Viewers receive already authorized data plus completeness metadata and
cannot call tools, bridge, CAS or network themselves.

For 11D1, `ArtifactViewerService` accepts only a canonical revision-pinned URI from
the active chat and returns a typed page projection over `ResourceGatewayService`.
Attachment text pages carry the extracted-text hash, never the source binary hash.
The screen owner validates contiguous offset, representation hash, total and viewer
kind before granting full-source actions. Page state is ephemeral, bounded to eight
selected resources and cleared on chat switch; it is not an event, artifact revision
or persisted index. HTML/JSON are rejected by this generic text bridge and remain
with their specialized viewer owners.

## Edit and delete semantics

Only domain-owned mutable resources expose Save/Delete:

- Plan Save uses an exact-current-revision guard and writes the complete Markdown
  payload as a new revision. Restore copies an exact historical revision into a new
  guarded head. Delete appends a tombstone for the logical Plan only after an explicit
  warning; it does not erase prior revisions or message references.
- HTML Save/delete/bind/refresh operates on exact workspace members and produces a
  complete new workspace revision. A failed refresh keeps the last-good JSON.
- Immutable uploads/snapshots have no in-place editor. `Create editable copy` or
  `Import` creates a related resource and leaves the original unchanged.

`Office.Services.PlanDocumentService` owns the complete Plan lifecycle lineage. Create and
Save validate non-empty Markdown without normalizing it: leading/trailing whitespace
and Markdown hard-break spaces are stored exactly. Update accepts only the active
exact artifact id and appends `vN+1` as its linear child; duplicate, skipped or
branched revision state fails closed. The tool executor only adapts arguments/results.
Restore requires the same exact-current guard, copies one exact non-tombstone revision
and appends it as `vN+1` with `restoredFromArtifactId` provenance. Delete requires the
exact current head and appends a `removed:true` child revision while clearing the
active pointer. Historical `ResourceRef` values are never rewritten; Library and the
new working set omit the removed Plan, while exact resolve/read returns
`resource_removed`. A model-linked tombstone follows its source message during
history editing/forking; a direct UI deletion is session-level.

Draft discard deletes only staging data. `Hide from library` is a UI preference and
does not alter history or model references. Destructive removal of a committed
resource first displays every referencing message/document revision and either
refuses the operation or explicitly includes those references in the same append-
only mutation. It appends removal/tombstone facts; it never rewrites the JSONL
stream. Physical CAS deletion is deferred to the existing fail-closed reachability
GC. Clear Chat/Data remains a separate explicit operation.

## Model context

- Drafts never enter prompt inspection, resource indexes or model requests.
- The committing turn receives bounded extracted text and supported media according
  to model routing, plus exact durable refs already stored on the user message.
- Later turns receive only the bounded working-set manifest. Bodies are loaded on
  demand through `common.resources_list/resolve/search/read`.
- The active Plan and HTML workspace are advertised by exact revision refs; their
  bodies are not injected on every step.
- Compaction preserves a deterministic bounded union of exact refs and may discard
  hydrated bodies/read results. A later exact read reconstructs them.
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
   - 11B3 — done host-neutral: historical revisions expose guarded
     restore-as-new-head; removal preflight lists every referencing message before
     confirmation; ready handoff revalidates and submits only the exact active pinned
     URI. [Evidence](stabilization/PHASE_11B3_PLAN_HISTORY_HANDOFF.md).
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
     binding JSON carries exact hash and explicit completeness; explicit export
     checkpoints the exact guarded workspace through the sole lineage owner and
     returns its pinned URI/CAS hash before standalone assembly. Raw JSON strings
     are not parsed/stringified during export.
     [Evidence](stabilization/PHASE_11C3_HTML_BINDING_EXPORT.md).
4. Typed viewers:
   - 11D1 — done host-neutral: exact bounded text/source paging, complete-only full
     copy/download and complete-only sanitized Markdown with exact Source.
     [Evidence](stabilization/PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md).
   - Image, PDF and audio remain separate measured slices with their own security
     and Windows gates.
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
gate.
