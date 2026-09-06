# Tool Library and capability truth

## Status and boundary

This is the Phase 11 target contract for tool visibility and authoring. It does not
change the current `ToolRuntime`, `ToolPackSnapshot`, confirmation or execution
policy, and it does not add dynamic authoring to the `16.1.0` release scope.

Migration of built-in Excel/Word/PowerPoint/Outlook execution is the separate 11T
contour. Its parity-first semantic-family switches and 11T10 cleanup are complete
host-neutral: generic host dispatch/catalog and definition/result projections are
deleted. Windows `DocumentSession` qualification remains mandatory; the Library
projects the exact current endpoint catalog and cannot hide or satisfy that gate.

A tool is an executable capability, not a chat artifact. Built-in tools belong to
the application build, custom tools are global or host-scoped packages, and
document-local VBA tools belong to the exact live document. An uploaded manifest,
source file or package remains an untrusted immutable artifact until an explicit
validated and confirmed import creates a tool package revision.

The UI may place Artifacts, Tools and Skills under one Library shell, but they keep
separate owners, stores, version rules and model transports:

- artifacts use revision-pinned `ResourceRef` values and `common.resources_*`;
- tools use the selected endpoint's exact capability catalog and
  `common.capabilities_*` for model discovery/schema admission; source inspection
  uses `common.resources_find/read` without admission;
- skills use the same capability reader for instruction bodies, but never become
  executable tools;
- authoring remains separate from discovery and cannot grant authority to an
  already accepted run.

## Current implementation

Source-owned host and controller catalogs are captured in one immutable execution
snapshot. Mutable `ToolCatalogEntry` values are catalog/package projections only;
their exact policy/binding is required at capture and missing authority fails closed.
Agent receives a finite core plus a compact exact-id catalog; optional
schemas are admitted atomically at model-step boundaries. Chat cannot execute tools,
Plan is restricted to its own read-only/Plan-local set, and Agent execution remains
subject to source-owned policy and confirmation.

Existing custom VBA packages are stored under `%AppData%/RNAssistant/tools` and the
current Library editor can validate, save, clone, test and delete them. The current
flat package store is not immutable revision history. Model authoring uses separate
`common.tools_upsert` and `common.tools_delete` operations; source inspection uses
the published resources described below. These Agent-only writes use native typed
handlers; R61/11O4 removes separate model validation and limits upsert to semantic
package source/documentation. The manifest
owns callable metadata; runtime validates the complete effective definition and
assigns conservative authority before write. Confirmed upsert/delete bind
accepted arguments to the current effective definition, reject stale state, mark the
storage boundary and require read-back verification. They cannot alter the immutable
ToolPack of the accepted run. Since 11J2, execution and Library install/remove/status
capture one complete `ToolPackageSource` v1. Its deterministic content revision is
separate from the manifest package version and is pinned with the native handler in
the accepted run. Library actions return typed result v1 with status, source revision,
dispatch and effect evidence; PascalCase/legacy result fallbacks are unsupported.
Model and Library upsert paths both carry `components` as one native `JArray` of
component `JObject` values into `ToolAuthoringService`; they never serialize and
reparse that argument. A quoted/stringified array is an `invalid_arguments` failure
before preparation or storage.

Tool, Skill and Prompt mutation guards use `ToolArgumentReader` as the single owner
for recursive ordinal JSON-object canonicalization and SHA256. Each domain still
builds and validates its own accepted argument/target payload, stale-state contract,
write boundary and read-back; this is representation reuse, not a shared authoring
service or validation policy.
Since 11T10 the existing Tools editor also uses lowercase
`rnassistant.toolLibrary` v1 and explicit revision-guarded create/update/rename/
delete mutation DTOs through the same `ToolAuthoringService` as model authoring.
Controller-owned catalog reconciliation, `StoragePath` identity, generic execution
and unversioned response fallback are absent. This does not make the flat store
immutable history.

### Model source reads

`common.resources_find` with `scope=catalogs` and a tool id discovers a semantic
`tool source: <id>` target for the current host's custom and source-owned builtin
tools, including Common definitions. Discovery carries metadata only. Passing the
returned target to `common.resources_read` reads the existing exact catalog
`/source` child, also used by the Library editor. Its JSON text contains
`argumentSchemaJson`, `code`, `readme` and `components`; generic whole-read bounds
and exact internal continuation rules apply. Generated human docs are not listed
or injected into these source bodies. Document-local VBA remains in its existing
VBA resource provider, not a second catalog authority.

The read records CAS-backed resource evidence with an exact catalog-root dependency.
A later publication supersedes that evidence through the common reducer; the
historical snapshot stays readable. Unpublished authoring-file drift cannot replace
the committed body, and missing/corrupt snapshots fail closed. Reading a schema or
implementation never admits a callable tool or grants mutation authority.

`common.tools_definition_read`, its direct file reader/native binding and catalog
entry are removed, without alias or fallback. Stored calls to that old contract
are explicitly rejected by model-history validation; user history is not deleted.
Upsert/delete preparation and confirmed read-back still belong to the existing
authoring owner. Other domain reads, including prompt inspection, remain open.

### Library source reads

Init, chat updates, catalog refresh and mutation/install/remove responses carry
only source SHA-256/byte extent in `ToolLibraryItemDto.source`, never schema, code,
README or component bodies. The selected editor calls typed `readToolSource` with
explicit chat, tool id and expected Library revision. `ToolEditorResourceService`
reserves a shared download slot before catalog hydration/live reads; its response
contains exact source refs and a bounded download capability. One inert UTF-8
`ToolSourceBodyDto` (schema, code, README, components) is delivered through the
existing data plane, at most 16 MiB, with complete-byte integrity verification.

Custom source comes from the committed `rna://catalog/tools` publication; built-in
source registrations are published once through the same catalog owner under
`builtin-tools-<host>`. Both expose exact `/source` children with root-publication
dependencies, not separate child heads or another activation store. Document-local
source instead proves each cached manifest/component against an exact live VBA
Gateway snapshot under the existing document access owner. Missing, partial or
changed components fail closed and invalidate document discovery for refresh.
Its JSON download is a disposable editor projection of those refs, not a new
document catalog authority. Reads create no model observations or execution rights.

No input becomes editable until the complete source is verified. Failed reads
cannot become empty drafts; unchanged unloaded items are not serialized as writes.
Clean source cache is selection-bounded; hydration does not mark the package dirty.
Local source edits survive refresh and late save responses. A changed published
source with a dirty draft blocks saving rather than silently rebasing it. Chat,
selection, Library section and bridge/page closure cancel reads and close late
leases. Existing revision-guarded mutation uploads remain the only write path.

The separate generated built-in human-documentation view uses the same Gateway/
download owner as described [below](#human-documentation-without-model-context-cost).
Remaining model/domain definition-read consumers are still open cutover work.
The pre-existing VBA
canonical-hash/raw-line-ending collision remains an explicit
[authority gap](stabilization/BACKLOG.md#existing-defects-outside-the-active-slice).
Real Office/WebView2 qualification remains open.

### Library mutation uploads

Both Library Save and the save-before-VBA-install step use one bounded upload
consumer, `ToolEditorResourceService`, over the existing resource data plane.
`beginToolMutationUpload` reserves a chat-bound `tool-editor` capability; the
`saveTools` control contains only `chatId`, `uploadLeaseId` and the complete
upload's SHA-256. Code, README, schema and native typed component values travel in
one UTF-8 `ToolLibraryMutationBatch` body, not an inline bridge request. The batch
is limited to 16 MiB and 256 mutations and shares existing upload capacity/expiry.
Complete bytes, hash, typed shape and batch identities/guards are checked before
dispatch; the capability is consumed even on validation failure. Upload alone is
transient input, not a package, catalog publication or model observation.

The same `ToolAuthoringService` still owns per-package source bounds, validation,
revision recheck, read-back and the existing catalog commit barrier. Mutations
remain sequential and stop at the first non-OK member; a verified committed prefix
is retained, not rolled back or replayed as an atomic multi-package transaction.
Document VBA catalog refresh still uses its existing document access owner.

The UI has one pending save/install/remove operation and retains that slot until
cancelled work exits. Chat/library replacement, bridge/page close and late
responses cannot apply stale state or continue into installation. An unconfirmed
save never retries or triggers install. Successful members acknowledge only their
unchanged submitted drafts; failed members and later text edits remain visible.
If the selected definition changes while saving, installation requires another
explicit save. Installation-status refresh also preserves unsaved authoring text.
Direct package install/remove authorization and execution remain unchanged.

The inline save request and separate pre-install save transport are removed;
catalog and outgoing library projections now use the source metadata above.
The pre-existing README leading-`U+FEFF` sidecar/read-back issue is tracked in
[backlog](stabilization/BACKLOG.md#existing-defects-outside-the-active-slice).

## Mandatory all-tool contract audit (R61)

The post-cutover catalog is complete host-neutral but is not yet Windows-qualified
as the final candidate. R61/11O1
corrected Resources + Capabilities host-neutral, 11O2 corrected planning, and 11O3
corrected HTML authoring/data binding. HTML inspection and active selection are now
internal; file/data writes are separate, patch/delete are semantic, and bind reuses
accepted read evidence without model-owned source arguments. Model context/results/
history omit opaque state while durable records retain it. The later whole-read
correction removes public resource continuation; the bound VBA project target is
available directly in runtime context and unfiltered find keeps it first. 11O4
corrected Prompt/Tool/Skill authoring: one-key prompt save, three exact tool-package
operations, separate skill-core/reference mutations, internal validation and
runtime-owned conservative authority. 11O5 corrected VBA/macro: write and rename
are separate, patch operation and raw backup identity are internal, and restore
selects a readable target or latest-for-module. 11O6 minimized the mode/host core,
and 11O7 completed UI-only built-in documentation, schema-aware Library Test and
responsive Implementation/Test containment. Final live-provider, WQ-PACK and real
Windows WebView2/Office evidence remains open.

The concrete failure attribution, state-ownership decisions, merge/split rules
and required model evals are recorded in
[R61 tool contract audit](stabilization/R61_TOOL_CONTRACT_AUDIT.md). That audit is
the per-family acceptance record; later-family IDs are not pre-approved by the
completed Resources + Capabilities slice.

The 11O0 source baseline in that audit enumerated all 35 conditional built-in
`common.*` tool IDs and all nine then-existing built-in Common skill IDs; after
11O5 the current source contains 31 Common tool IDs and eight Common skills.
Progressive capability loading is
acknowledged, but does not
exempt optional tools from merge/split/internalization review or justify
plumbing-heavy schemas after admission. Skill bodies are contract consumers and
must switch atomically with the IDs/arguments they teach. R61 must also compare
current core membership with a smaller relevant pack; registry count alone is not
an optimization target.

11O0 freezes the source-built-in property baseline in the machine-checked
[R61 inventory](stabilization/R61_TOOL_PROPERTY_INVENTORY.tsv): after 11O5, 69 unique built-in
IDs and 72 effective host variants record exact descriptor revision, allowed modes,
direct binding and recursive property paths. Plumbing-shaped additions require an
explicit semantic or runtime-owned decision. Dynamic installed custom-package
schemas remain package-revision-owned. 11O4 rejects plumbing-shaped custom
arguments unless their description contains an explicit `Domain identity rationale:`;
field names alone never trigger unsafe automatic stripping.

Every published tool must be reviewed individually, including Office, VBA/macro,
resources, capabilities, questions, Plan, Task List, HTML, prompts, Tool/Skill
authoring and custom-package execution. The review classifies every input as either
semantic intent or runtime-owned state:

- Model-visible arguments contain only choices or content the model must actually
  decide. Sheet names, A1 ranges, slide numbers, component names and requested text
  may remain when they identify a real domain target.
- Call/run/chat/document/endpoint IDs, UUIDs, internal artifact IDs, catalog or
  package revisions, optimistic-concurrency hashes, prepared guards, cursors,
  offsets and page tokens are runtime-owned. After a family cutover they are absent
  from model-facing arguments, ordinary Tool Result data, `RUNTIME_CONTEXT` and
  replayed model history; they are not typed, copied or even selected by the model
  or by a person testing a tool.
- A canonical revision-pinned `ResourceRef` remains the durable identity for
  replay, provenance and result evidence, but is present only in the accepted typed
  event/execution projection. The runtime maps a bounded semantic selection or a
  previously accepted semantic candidate to that exact reference. The model-facing
  result contains a readable target description and domain data, not `ResourceRef`,
  `rna://` URI, revision, hash or cursor. Ambiguity fails closed and asks for a
  meaningful target choice; it never falls back silently to the latest or active
  resource and never substitutes another opaque `candidateId`.
- Continuation belongs to the read implementation. It may expose a user-facing
  `Next` action or perform a bounded safe continuation, but a cursor/revision pair
  is copied and validated by code. Revision drift restarts only a read under its
  declared policy and never causes an automatic mutation retry.
- Defaults derivable from the bound session, selected endpoint, current immutable
  run snapshot or prior accepted result are injected after model validation. They
  do not appear as nullable ceremony in the model schema.
- Exact public tool/skill ids may remain only when they are stable semantic
  identities the model must choose from the current catalog. The runtime-generated
  `tool_call_id` may remain in the Tool Result wire solely to correlate an accepted
  call with its result; it is never an argument or model-generated state. Descriptor
  revisions, admission guards and snapshot identities remain internal.

The target therefore has two explicit views of one tool contract: a minimal strict
intent schema for the model and Library test form, plus an internal execution
context containing resolved identity, continuation and guard state. The internal
view is never serialized into a model request or model-visible result. Both views
reach the same `ToolRuntime` registration, policy, confirmation and handler; this is
not a second executor, store or permissive adapter. A system-owned field may remain
public only with a per-tool written rationale proving that the caller must choose it
and that no bounded semantic selector can preserve the same safety.

The cutover is atomic per tool family. Resources + Capabilities are switched; until
another slice switches, its existing contract remains authoritative. No alias,
dual schema, guessed value or compatibility fallback is added.

## Human documentation without model-context cost

Every built-in/system tool must have non-empty human documentation in Library.
Documentation is separate from the compact model selection summary and executable
argument schema. It covers purpose, target selection, semantic arguments, defaults,
types/enums/bounds, confirmation/effect semantics, result and common error examples,
limitations and a safe Library test recipe.

Built-in README is absent from the compact Library projection and remains empty
in the source-editor body. Contract tests
prove that generated documentation is absent from
`ConversationPromptComposer.BuildDescription`, model tool descriptors,
`RUNTIME_CONTEXT`, capability search/read, Tool Result and request-token accounting;
it cannot change executable ToolPack registration or model capability-catalog
revision. Full Markdown is generated from source-owned registrations/policies at
the existing builtin catalog publication boundary. Typed publication entries retain
the definition and a documentation CAS `PayloadRef`; the root's committed view
retains these parts. Neither compact/public catalog projections nor execution
registrations acquire generated Markdown or reconstructed runtime policy.

Only opening the selected builtin's Docs tab requests `getToolDocumentation` with
explicit chat, id and expected Library revision. The controller delegates to
`ToolEditorResourceService`; it reserves shared download capacity before reading
`rna://catalog/builtin-tools-<host>/<id>/documentation` through the Gateway.
The child has an exact root-publication dependency, not its own synthetic head.
The service verifies the published definition/body against this runtime's original
registration/policy; re-generation is comparison only, never a missing-CAS fallback.
This path does not discover live VBA or call Office. Historical reads use retained
snapshots and do not activate a catalog or grant model observations.

The typed `rnassistant.toolLibraryDocumentation*` v1 response carries chat/id/revision,
an exact `ResourceRef` and the shared download lease, not inline `markdown`.
Complete UTF-8/SHA-256 verification and the 2 MiB limit precede rendering. One
selected-tool/chat/revision cache replaces the state-wide body/request dictionaries;
selection/catalog/chat/Library-section and bridge/page closure cancel requests,
discard stale cached bodies and close late leases. Cancelled openings keep their
bounded pending slots until settled. Errors remain explicit, without automatic
retry or an inline reader. Custom README/provenance remains package-owned.
Windows WebView2/Office qualification remains open.

Model-facing descriptions stay short and operational. `UseWhen`, `DoNotUseWhen`
and limitations are audited for selection value rather than copied from the human
manual. Custom package README is not automatically injected into model context.

## Library test form and layout

Library testing uses the effective minimal intent schema and the normal production
runtime. The form must provide:

- checkbox/switch for boolean, numeric controls with integer/number step and
  min/max, select/radio for enum, multiline input for long strings, and bounded
  structured editors for arrays/objects;
- explicit required/optional state, omit/null controls where the contract permits
  them, visible defaults and inline validation before run;
- a wrapping description below each argument instead of a truncated placeholder,
  with consistent spacing between the argument name, description and control;
- runtime-owned values resolved from the selected host/document, current test
  fixture and prior read result. UUIDs, URIs, revisions, hashes and cursors are not
  editable fields. An advanced diagnostic may show the resolved execution context
  read-only to the human after preparation, but it is never copied into test
  arguments, model context or model-visible result;
- `Next`/continuation UX for paged reads and typed result/effect evidence, without
  teaching the user to copy an opaque cursor;
- the same confirmation, document binding, safety and disposable-document rules as
  a normal invocation. Test mode cannot expand authority.

The reported layout defects are explicit acceptance cases: opening
`Implementation` must keep the editor, tabs and actions inside the right pane at
all supported widths; no horizontal displacement or hidden controls are allowed.
The `Test` page must keep labels and controls aligned, wrap full descriptions and
remain usable in the narrow Office task pane. CodeMirror refresh after tab changes,
`min-width:0`, overflow ownership and responsive grids are verified in real
WebView2, not inferred from a desktop browser screenshot.

## R61 delivery and gates

1. **Done host-neutral (11O0, refreshed through 11O6):** freeze every effective built-in tool ID/schema by mode and host. For each
   property record semantic owner, source/default, validation, internal resolver,
   result dependency, test fixture and keep/remove decision.
2. **Done for Resources + Capabilities, planning, HTML, authoring and VBA/macro:** contract checks fail on unreviewed or unexplained plumbing-shaped
   arguments (`*Id`, UUID, URI, revision/hash/etag, cursor/offset/page token). Names
   are a review trigger, not an unsafe automatic stripping rule.
3. **Semantic family switches and final core-pack selection through 11O6 are done
   host-neutral:** deleted argument paths have no aliases or dual schemas. Excel
   Agent keeps four bootstrap, 15 Excel and two routine VBA editing schemas;
   rename/restore/delete/macro remain exact optional schemas available through
   admission.
4. Run deterministic model scenarios proving that calls complete without invented
   opaque values and that cursor/revision confusion is structurally impossible,
   not merely discouraged by descriptions.
5. Inspect the fully materialized request, Tool Results and replayed model history:
   after each family cutover they contain no `ResourceRef`, `rna://`, revision/hash,
   cursor or internal id outside the two explicit public-id/correlation exceptions.
   Harness fixtures create and retain exact state behind runtime; scenarios cannot
   inject it through scripted model arguments.
6. **Done host-neutral (11O7):** populate and verify UI-only documentation for every
   built-in tool, switch the typed test form and fix both reported layouts with
   focused browser tests. Exact Markdown is fetched only for the selected id/revision;
   semantic `Next` retains its opaque state in a bounded isolated runtime session.
7. Qualify the final exact catalog with live providers and Windows WebView2/Office.
   Earlier evidence for a changed schema/catalog cannot close WQ-PACK or release.

R61 is a stabilization correction and a Phase 12 prerequisite explicitly requested
on 2026-09-02. It follows the currently reported Windows rebuild, but final
Milestone WQ evidence must be collected against the post-cutover catalog.

## Read-only Tool Inspector first

Before authoring is expanded, Library → Tools must expose capability truth for the
current execution endpoint. Before Host Fabric this is the local owner endpoint;
after Host Fabric the same DTO follows the explicitly selected target. Each row
shows:

- exact id, name, host and kind;
- origin: built-in, custom package or document-local;
- selected `HostInstanceId`/document scope and catalog snapshot revision;
- `callable`, `discoverable`, `disabled`, `blocked` or `unavailable`, with an exact
  reason rather than an inferred status;
- effect class, risk, confirmation requirement and batch policy;
- descriptor/schema revision and package fingerprint where applicable;
- qualification coverage/readiness, without turning N/A into pass;
- links to the exact recent run/tool call, result and causal evidence.

Changing the UI-selected target refreshes the inspector from the new owner endpoint.
It never retargets an accepted run. A disconnected or stale endpoint keeps its last
descriptor only as visibly stale diagnostic information and grants no callable
authority. The inspector is a projection, not another catalog or settings store.

Built-ins display the application/catalog revision and are immutable. A custom
package displays its human package version separately from its immutable content
revision. A document-local tool displays `Live` plus its exact fingerprint; it does
not receive fabricated RNAssistant history.

## Custom package history and authoring

Dynamic authoring follows Tool Inspector and Host Fabric target pinning. Its target
contract is:

- one append-only custom-package journal and immutable CAS-backed bodies;
- one logical head per exact tool id and host scope;
- optimistic concurrency against the exact current package revision;
- every save creates a new complete package revision; restore creates a new head
  with `restoredFrom`; delete appends a tombstone;
- built-in tool ids and every tool/skill id in the shared namespace cannot be
  shadowed;
- import/export carries exact provenance and never treats an uploaded file as
  trusted before explicit validation and confirmation;
- VBA/custom-tool authoring defines only user/domain parameters consumed by the
  implementation. Bound host/document, `ResourceRef`, revisions, guards, package
  fingerprints and execution IDs are supplied by runtime outside the authored JSON
  Schema and never become hidden macro parameters. Plumbing-shaped authored fields
  fail validation unless the package records an explicit domain-identity rationale;
- authoring preview and Library test use generated runtime fixtures behind the same
  preparation boundary. The author/model fills only the effective semantic form;
  exact prepared state may appear only in a human read-only diagnostic;
- a catalog change becomes available only at the next run boundary and cannot
  mutate the snapshot of an accepted run;
- editor test executes through normal `ToolRuntime`, policy, confirmation,
  `DocumentSession` and result/effect evidence, using a disposable document or an
  explicitly approved copy for mutations;
- Outlook does not gain VBA-package execution merely from a host label; every
  host/executor combination must have an admitted runtime and qualification pack.

The flat `ToolStore` is removed at cutover rather than retained as a dual-write
history. Tool result payloads may still be materialized as ordinary artifacts by
the existing bounded result boundary; that does not make the tool definition an
artifact.

## R56 Tool Library slices and gates

1. Read-only selected-endpoint Tool Inspector and capability/availability DTO.
2. Exact run/result/evidence links and host capability matrix in the Issue Center.
3. Append-only custom package revisions, restore/tombstone and import/export.
4. Guarded Library editor switch, conflicts and disposable-document test flow.
5. Model authoring switch and later-run catalog refresh.
6. Existing VBA package definition/result adapter removal (completed host-neutral in
   11J2); Windows VBE/Library qualification remains required.
7. Final existing-editor typed boundary and generic catalog/result removal
   (completed host-neutral in 11T10); Windows WebView qualification remains required.

Host-neutral tests cover catalog projection, stale endpoint behavior, scope and
revision conflicts, no-shadow rules and run-boundary refresh. Windows x64 + Office
x64 tests cover actual custom-package discovery/install/run/cleanup in Excel, Word
and PowerPoint, target changes during an editor session and endpoint loss. UI status
alone never proves an Office effect; install/remove require package journal/read-back,
and arbitrary VBA macro execution remains unknown after dispatch.
