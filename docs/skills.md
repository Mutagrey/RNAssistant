# Skill Library and package revisions

## Status and boundary

Task-scoped skill loading and dependency-bound tools are implemented without a new Code Mode.
Core `SkillTaskService` owns resolution and request exposure; `SkillLoadToolHandler`
owns selection, the existing compiler owns complete instructions, and Office/CLI
use the same `ToolRuntime` dispatch guard. Source inspection remains a resource read. Immutable custom package
history, import/export provenance and restore/delete UX remain deferred product
work. They require a concrete scope and owner; they are not prerequisites for
ordinary bug fixes or proof of existing release qualification.

An installed skill is a trusted instruction capability, not a `ChatArtifact` and
not an executable tool. It is global or host-scoped, can affect many document chats
and is selected through the capability catalog. The chat-owned Artifact Library
must not duplicate it or inherit its edit/delete semantics.

An uploaded `SKILL.md`, Markdown file or skill archive is different: it remains an
immutable, untrusted chat artifact until the user explicitly installs or imports it
as a skill. File name, extension and Markdown front matter never grant instruction
authority.

## Current implementation

Built-in skills are supplied by the application and host adapter. Custom skills are
stored under `%AppData%/RNAssistant/skills/<host>/<skill>/` as one `SKILL.md` plus
up to 64 direct UTF-8 supporting files under `references/`, `scripts/` and `assets/`.
Allowed extensions are `.md`, `.txt`, `.json`, `.csv`, `.js`, `.py`, `.cs`, `.html`,
`.css`, `.svg`; nested paths, symlinks, traversal and duplicate basenames are rejected.
These files are data; loading a package executes none of them. Binary assets and
archive installation are deferred. The core front matter contains `id`,
`host`, `name`, `description`, human-authored `version` and `enabled`.

`ExcelBuiltInSkillProvider` in Office owns the production Excel report definition;
both the real adapter and fake host use it. The host-neutral report evaluation
therefore exercises the complete shipped instructions and package dependencies.
Its scripted and live cases are documented in the [Harness guide](../tests/RNAssistant.Harness/README.md).

The visible catalog is built-in first, then custom, filtered to `Common` plus the
current host (`Workspace` for the independent CLI). A custom package cannot shadow a built-in id. Tool and skill
ids share one namespace and a collision fails request construction.
Outlook does not publish the built-in VBA editing, CodeOnly UserForm, or VBA tool
authoring procedures: that host has no supported VBA mutation/package route.
Host-neutral package checks select every advertised built-in against each host's
actual published tool catalog, including default-off JavaScript.

### Manifest and task selection

`SKILL.md` is the sole dependency source. A portable file with scalar `name` and
`description` and Markdown body works without a manifest; omitted `id` uses `name`.
The existing bounded front-matter parser is not a general YAML implementation.
RNAssistant extends it with a single-line JSON `rnassistant` field:

```markdown
---
name: report-check
description: Check a report against its source data.
rnassistant: {"formatVersion":1,"packageId":"reports","requiredTools":[{"id":"common.resources_read","contractVersion":"1"}],"optionalTools":[{"id":"web.verify","contractVersion":"1","fallback":"Inspect the generated source and state that browser verification is unavailable."}]}
---
Read the source, compare the report, and verify the final result.
```

Each dependency names a public tool id, contract version and optionally an exact
`revision`. Missing required dependencies, incompatible contracts and conflicts
refuse the entire selection. Optional dependencies require explicit fallback text.
The resolver captures exact descriptor/policy/binding revisions, deduplicates shared
tools, and never silently chooses a different conflicting version. The manifest
participates in the package fingerprint; editor saves and clones preserve it.

The model calls `common.skills_load` with `{ "id": "report-check" }`; optional
`action:"remove"` clears that selection for the next request. The loader is a
singleton call. Success changes `ChatRunRecord.SelectedSkills`, not the already
accepted request. On the next step `SkillTaskService.Compile` combines direct core,
existing direct admissions and selected dependencies. The compiler injects complete
`SELECTED_SKILL` instructions alongside exactly those schemas. Words in Markdown,
a source read, attached files and historical results cannot select a skill.

Default prompt schema 46 asks multi-deliverable/staged tasks to select
`common.task_tracking` and maintain its Task List. This is model guidance, not an
automatic intent classifier or a completion gate. The compact Office catalog's
`loadSkill` lists the packages that can expose a specialized tool; it never admits
that tool by itself. The dynamically configured `common.tools_upsert` descriptor
retains its skill-only exposure. The five built-in VBA editing tools
also require a selected package; `common.vba_code_editing` exposes them, while
`common.vba_userform_authoring` can expose code-only form writes. A direct
`capabilities_read` returns bounded `loadSkill` guidance before or after selection
but cannot admit a skill-only tool. The selected request owns its exact schema;
discovery does not duplicate it into history or create a cached `skill_required`
failure that would block another inspection after selection.
`common.office_run_macro`
remains an optional direct tool for requests to execute an existing macro.

Selection continues across user turns in the same chat. Each new run copies only
the selected package/publication/tool pins from the previous run; existing
`run.updated` events preserve them through cancellation, compaction, confirmation
and restart. Every request revalidates those pins against current publications and
policy. Prior confirmations, prepared state and resource write guards are not
copied into a new turn. There is no loaded-skill store, dual write or replay of an
unfinished business action. The model removes irrelevant selections explicitly
when changing tasks; a message alone does not imply a task boundary. A new chat,
history rewrite/reset or explicit Office mode/permission reset clears selection.
The Office controller preserves selections when confirmation starts a new invocation.
Dispatch validates that invocation against the exact durable pending call's original
request, turn, step, arguments, policy and prepared state; it still rejects changed
bindings. It does not relabel a previous request as a new one.
Changed/revoked publications suspend incompatible selections until explicit reload.
Catalog drift after request compilation revalidates the offered tool's exact
descriptor/policy/binding and its supplying selected skills/dependencies. An unrelated
publication does not revoke an unchanged call. A used contract change or revocation
refuses dispatch with replan feedback. The progress tracker persists a fingerprint
of that operation's admission state: real rebind/selection can release a prior
replan refusal; new call/request IDs or unrelated publications cannot. Unknown
effects keep their replay guard regardless of later admission changes.
Office refreshes complete
catalog authority at the next model-request boundary after an in-run publication;
the already accepted request stays pinned. Instructions and schemas must fit the
budget: the Core half-input check is an initial bound, then Office and workspace
stage a candidate surface and the projected load result through their existing
complete request compiler, including schemas and reserves. Only an admitted
candidate replaces the selection in the accepted run record. Refusal returns
bounded feedback through the previous usable surface. Preview results never enter
history or count as execution facts; preflight performs no LLM call or compaction.
Actual dispatch still recompiles against its own frozen authority.
Office budget-refusal guidance names `common.skills_load` with `action=remove`
for a completed or currently unneeded selection, followed by a separate new load.
It does not remove a skill automatically. Unloaded-tool protocol repair requires
resolving any prior admission refusal before retrying; failed loading grants no
callable tools.

The [32K admission defect](stabilization/RISK_REGISTER.md#architecture-and-regression-review--2026-10-15)
is contained for new selections. With schema 51 guidance, the default 32K Excel
case selects the VBA skill and executes its patch. A separate 32K case with extra
required input refuses the load and permits the next request and restored user
turn. Product context defaults are unchanged. No automatic deselection, larger-budget
fallback or truncation of active instructions exists. Previously persisted
oversized selections are not silently rewritten; explicit reset/profile correction
remains necessary for that old state.

The Library reader validates the published supporting-file basename for all three
allowed directories (`references`, `scripts`, `assets`); it does not strip a fixed
`references/` prefix from every path.

### Agent abilities in the chat inspector

The chat header has one right-panel toggle. Its **Возможности агента** view shows
selected skills, callable tools and explicit availability/restriction information;
editing remains in the Library. The former standalone Arsenal popover and its
header button have been removed.

In Office, `getAgentArsenal` returns typed `rnassistant.agentArsenal` v1 metadata from the run,
`ContextReceipt.Arsenal` and published metadata. The inspector requests details
only when its abilities view opens; the read never executes or selects anything.
Late responses from another chat or revision are discarded. The right panel
shares the prototype's width, view switcher and responsive presentation.

Project and plugin chats use the same compact skill rows and tool groups directly
from the selected session snapshot's `contextReceipt.Arsenal`. This is saved
request metadata, not a live catalog or a promise that a tool can execute now.
An earlier RunId is labelled as the previous run; missing or incomplete metadata
is shown as unavailable, never as zero tools. Before the first compiled request
there is no saved composition. Skills outside `Selected` are not included in the
count. Unavailable skill reasons remain visible in the disclosure even when a
description is present. This view makes no Office query and creates no second
store; dispatch still revalidates exact bindings and policy.

### Independent workspace runtime

Office and CLI share Core `SkillPublicationService` and `SkillCatalogService`.
The former owns skill catalog publication, exact package/reference CAS reads and
publication dependencies; the latter selects host metadata. Office's catalog
provider delegates skill reads to that owner and retains its bounded continuation
and host/runtime guards. Tools and prompts keep their existing publication owners.
There is no second catalog store; selection is part of the existing run record.

The CLI supplies only enabled skill metadata to the model. It exposes bounded
`common.resources_find(type="skill", query?)` (up to 50 entries, explicit
incompleteness) and `common.resources_read(type="skill", target=<skill id>,
referencePath?)`. Reference paths come from the core read's metadata. Whole skill
cores/references above 16000 characters fail explicitly; partial instructions are
not marked loaded and CLI reference pagination is not implemented. File reads
remain the default `type="file"`. A skill read never grants a file mutation
observation or counts toward file-read acceptance, even when the names match.

CLI captures the committed skill generation and resource authority at the model
request boundary. The existing frozen `ModelContextCompiler` owns hydration,
freshness, omission, budgeting and repair. Body reads carry immutable CAS evidence
with a dependency on their catalog publication. No live filesystem skill reader
is used by the compiler. Deleting or editing authoring files cannot change an
already published package; an explicit publication through the existing authoring
owner is required. CLI skill authoring/import commands remain outside this slice.
A workspace-local `SKILL.md` is an ordinary file, not an installed instruction.

The workspace built-ins are `workspace.files` and
`workspace.web_verify_repair`. They cover exact source/edit/final-read workflow
and local browser verification/repair. Both require available file mutation
tools; the web skill also requires `web.verify`. They remain discoverable with an explicit unavailable status when required tools
are missing, the workspace is read-only or the browser provider is absent.
Skill selection stays with the single existing agent loop; the runtime does not
force a skill call or retry `done` to satisfy an acceptance check. Actual LLM
selection and task completion are separate evidence from scripted contract tests.

### Publication and authoring

Custom authoring uses atomic current-file replacement; published catalog generations
retain immutable package bodies in the existing CAS/authority, without an editor
history/restore UI. `version` is a manual label. Runtime separately computes `revision` as a
versioned SHA-256 package fingerprint over the stable id, host, complete normalized
front matter/body and ordered reference paths/revisions. Editing package metadata,
the core or any reference changes the package revision. Delete removes the custom
authoring directory, not previously committed catalog snapshots. External file
drift cannot silently change an active publication.

The existing Library UI already owns skills under `Library → Instructions → Skills`.
It supports Markdown edit/preview, references, enable/disable, clone and custom
delete; built-ins are read-only. It does not currently expose useful revision
history, restore, provenance or a complete version display.
The editor labels built-ins as read-only and offers an editable copy after the
core text has loaded. The copy has a distinct id; the original keeps its
published id.

Agent-side authoring uses four exact Agent-only native intents: core
`common.skills_upsert/delete` and reference
`common.skills_reference_upsert/delete`. Their schemas cannot mix core and
reference fields and use a versioned result contract. Preparation
binds the accepted arguments and complete current package revision; confirmation
rejects stale state before dispatch, and read-back distinguishes verified change,
verified no-change and unknown effect. Direct UI Save/Delete is an explicit manual
operation guarded against an active run. The editor now receives only the versioned
`rnassistant.skillLibrary` package DTO, sends explicit revision-guarded mutations
instead of reconciling a raw catalog, and consumes versioned mutation/reference
results. Core, rename, delete and reference changes pass through the same
`SkillAuthoringService`; the controller no longer owns or mutates `SkillStore`.
There is no unversioned/PascalCase response fallback or storage-path identity in the
UI. A committed mutation becomes available at the next model-request boundary in
an active Agent run, or at the next run boundary when starting from the Library.
It does not rewrite an already accepted model step. A same-run regression creates
and reads a new skill and tool, then selects the skill and admits the tool schema
on later requests.
A failed authoring-directory removal cannot be reported as a verified delete, even
if catalog read-back no longer sees the skill.
Cleanup of a replaced package directory also fails the save instead of silently
leaving the old package behind.

### Editor source reads

`SkillPackageDto` carries metadata, including the core text's raw SHA-256, UTF-8
byte length and character count, never `bodyMarkdown`. Catalog hydration does not
mark a draft dirty or fetch all package bodies.

`SkillEditorResourceService` resolves the exact core or reference from the
host-filtered published catalog, then reads through `CatalogResourceProvider`,
Gateway and the existing CAS. `readSkillSource` accepts
`rnassistant.skillSourceRequest` v1: explicit chat, expected package revision and
path (`""` for the core, otherwise a canonical reference path). It returns only
`rnassistant.skillSourceRead` v1 metadata, an exact `ResourceRef` and a shared
download lease. Built-ins use the same reader and remain read-only. The previous
reference-only reader/bridge action is removed, not kept as an alias;
there is no direct authoring-file reader, inline read body or fake mutation result.
Reading does not publish a catalog or add model observations.

A transfer slot is reserved before catalog hydration. The former source character/byte
ceilings are removed; the editor enables only a complete verified UTF-8 snapshot. Source-file revision/byte length (which may include a file BOM) remain
distinct from the published text's transport hash/length and the logical resource
revision. The existing `SkillAuthoringService` still checks live package drift
before Save/Delete; an old published read cannot authorize overwriting changed files.

Cancel, chat/package/source or Library section changes and bridge/page close invalidate pending
reads and close late leases. Failed reads stay read-only. Catalog refresh discards
changed clean sources, but retains dirty user text and blocks saving its conflict
instead of silently rebasing it. Only the selected skill's core and current
reference retain clean text; other clean sources are evicted. User drafts are not
evicted. Clone/context-copy requires a loaded core and cannot copy a missing body
as empty text.

Metadata-only upserts explicitly send `preserveBody: true` without a replacement
body. The existing mutation owner checks the complete package revision before
preserving its live body. Creation, delete, or mixing preservation with replacement
is rejected; the typed preservation intent is recorded at the same commit barrier.
Replacing the core requires a loaded/user-created draft. A successful Save clears
only its unchanged submitted draft and reloads exact published text; later user
edits remain visible and conflicts block further writes.

### Editor mutation uploads

Core batches and reference upserts use the shared data plane, owned by the same
`SkillEditorResourceService`. `beginSkillMutationUpload` reserves a chat-bound
`skill-editor` capability before receiving bytes. `saveSkills` and
`saveSkillReference` accept only `chatId`, `uploadLeaseId` and the SHA-256 of the
complete uploaded UTF-8 JSON. The typed mutation body (including revision guards)
travels through the existing chunk upload route, never the bridge control message.
It is transient input, not an attachment, CAS publication or model observation.

Uploads stage through the shared chunk transport without the former 16 MiB
source ceiling. A core batch still contains at most 256 mutations; replacement
source length is constrained by its actual representation and model context when applicable. Empty reference text is valid. Complete bytes,
hash, typed shape and all batch body bounds are checked before the first domain
dispatch. The capability is consumed once, including on validation failure.
The existing authoring owner then applies revision-guarded mutations sequentially
through its prepare/read-back/commit barrier, stopping at the first non-OK result.
This preserves a verified committed prefix; it is not an atomic multi-package save.
Metadata-only core mutations still explicitly preserve the guarded body.

The UI permits one pending writer and freezes the submitted core/reference plan.
Later edits are not silently added to that save or cleared by its response.
Cancellation, chat/library replacement and bridge/page close abort transport and
close late leases; possible dispatch or a lost response never triggers replay.
An unconfirmed outcome asks for Library refresh before another attempt. Successful
reference saves return metadata only and reread the exact published source; the
inline source echo is removed. Reference delete remains a body-free guarded action.

## Built-in guidance contract

Built-in skill metadata is selection guidance; the complete Markdown body is loaded
before skill-governed work. A built-in body owns its domain prerequisites, ordered
workflow, quality constraints, failure/recovery behavior and evidence-based
definition of done. It does not copy JSON schemas or gain execution authority.

The system prompt owns the universal Understand → Prepare → Inspect → Execute
→ Verify → Finish lifecycle, including when to select Task Tracking and the final
completion assessment. `common.task_tracking` owns list maintenance and durable
step notes. The tool prompt owns catalog discovery/admission; the shared response
contract owns the call envelope, batching and outcome interpretation. The current loaded tool description and strict
schema remain authoritative for the exact operation, root arguments and returned evidence. If a
skill and schema disagree, the model follows the higher-priority runtime prompt and
schema and reports the inconsistency rather than inventing compatibility fields.

The VBA skill owns operation choice, source inspection and recovery. Patch-field
matching, positions, insertion/deletion and no-op behavior are described once in
the supplied tool contract, rather than repeated in the skill body.

Schema 51 removes repeated universal rules from domain skills and tool descriptions.
Model guidance keeps decisions the model must make: selecting capabilities, choosing
source coverage, constructing edits and interpreting uncertain results. Journal,
guard, storage and restoration implementation details remain runtime documentation.
Selected skills are instructions under the shared policy in both Agent and Plan;
merely inspected skill source remains data. Current delivered reads or verified
after-source can support a later edit without a ritual reread; missing or insufficient
source still requires inspection. Selected bodies remain complete, never truncated.

VBA guidance distinguishes component names, procedure names and semantic resource
targets. Complete current source may be reused after mutation or compaction when
its body is included; a missing/oversized body is not an Office access failure.
Focused patches can use exact line excerpts, while whole-module replacement keeps
its complete-source guard. Tool authoring uses manifest-owned VBA metadata and a
complete component replacement, never an implicit package merge. Prompt authoring
follows automatic archived prompt migration rather than a retired manual reset gate.

Every exact `common.*`, `excel.*`, `word.*`, `powerpoint.*` or `outlook.*`
capability named by built-in guidance must resolve to the current tool/skill
catalog. Harness checks that invariant across all four hosts, rejects duplicate or
empty built-ins and prevents retired `TOOL_RESULT ok=true` guidance from returning.

## Model context

- Agent and Plan receive enabled skill metadata, without instruction bodies or
  package revisions. The retired Chat mode is history only.
- `common.skills_load` is the canonical selection boundary; resource reads only
  inspect source. Selected complete bodies are compiler invariants restored from
  the frozen published catalog on every request, independent of compaction.
- Core/source inspection and supporting reads retain their ordinary history/CAS
  evidence and bounded continuation. A reference read never selects a procedure.
- Default prompt schema 44 switches Agent/Plan, discovery and compaction guidance
  together. Already customized prompts remain governed by existing migration rules.
- Schema 47 consolidates Agent lifecycle/completion guidance and moves Task List
  procedure details into its selected skill. It uses the same automatic migration
  with an archived recovery copy; runtime completion policy is unchanged.
- Schema 48 retains selection across user turns and asks the model to remove
  irrelevant procedures explicitly. Exact revision, policy and budget checks remain
  mandatory; retained selection never grants mutation permission.
- Schema 51 reduces duplicated guidance across Agent/Plan, workspace, skills and
  tool descriptions, using the same archived default migration. The compaction
  helper preserves claims; current skill bodies and schemas arrive separately.
- The current `Discuss skill` UI copies Markdown into custom chat context; that copy
  does not create a selection or grant any tool access.
- Host filtering uses the selected execution endpoint, not the containing window.


Workspace built-ins include `workspace.plugin_authoring`: it guides the source
chat to create a path-only SVG icon from a text brief, update `plugin.json`,
check the package and preview behavior before publication. It uses existing
file tools; an image-generation model is not required. Plugin check/apply remain
runtime tools with their own authority and confirmation.

## Target Skill Library UX

Skills remain in Library, grouped first by type and then by the stable ID namespace
or common feature family (`excel.*`, `common.resources_*`, `common.html_*`, etc.).
Displayed group names may change without renaming callable IDs or package identities.
They are not rows in
the Artifact Library. Each skill row/editor shows:

- stable id, name, description and applicability (`Common` or selected host);
- built-in/custom and enabled state;
- human `version` separately from immutable package `revision`;
- reference count, modified time and source/provenance;
- current-head status and History for custom packages.

Agent mutation results may render a UI-only `Skill created/updated/deleted` card
that opens the exact Library item. It is not an artifact card, model transport or
durable skill URI. Deleting a chat never deletes an installed skill; deleting a
skill never rewrites chat history.

Built-ins remain immutable application content. They may be inspected and cloned
to a new custom id, but not edited, deleted or assigned fake user history.

## Custom package revision contract

Every successful custom core/reference mutation creates one immutable package
revision and moves the logical skill head. A package revision includes:

- stable skill id and host scope;
- complete core Markdown/front matter;
- ordered exact reference paths and bodies;
- content fingerprint, parent revision, actor/source, time and optional originating
  chat/run/tool-call or imported `ResourceRef` provenance.

History is package-level: references do not have independent heads. Core and one
reference remain separate confirmed model calls, so each produces its own package
revision. UI Save may commit all dirty editor members as one explicit atomic manual
package revision after validation.

Restore never modifies an old revision; it creates a new head with `restoredFrom`.
Human `version` is not auto-incremented on every edit and is never used as runtime
identity. Rename is an explicit create-new-id plus tombstone-old operation without
aliases. Delete appends a package tombstone and removes the skill from future
catalogs; physical bodies are retained until fail-closed reachability permits GC.

Any future package store must be append-only for package facts and content-addressed for
immutable bodies. It is separate from document chat streams because skill ownership
is global/host-scoped, but it cannot become a second chat store. Any shared CAS GC
must include every validated skill package journal as a reachability source; an
unreadable/incomplete journal blocks deletion. The current flat `SkillStore` path is
removed at cutover rather than retained as a dual-write history.

## Import, export and derived artifacts

`Install as skill` is an explicit conversion from an immutable artifact to a new
custom package revision. It validates UTF-8, bounded paths/files, package limits,
reserved/colliding ids and host scope, then shows create/update impact and requires
confirmation. Imported instructions cannot silently run during preview or install.

Export materializes a bounded immutable package file with exact source revision and
may be downloaded or attached as a normal artifact. Editing that exported artifact
does not mutate the installed package; re-import is explicit and guarded. A skill
created directly in Library has no artificial source artifact.

## Deferred package scope (historical Phase 11 slices)

1. Contract/store: immutable package journal/bodies, replayed head, tombstone and
   no flat-store dual write.
2. Read projection: built-in/custom metadata, exact head/revision and Host Fabric
   target scoping without changing `common.capabilities_read` semantics.
3. Library history UI: version/revision display, diff, restore-as-new-head and
   deletion impact.
4. Manual editor: atomic dirty-package save, conflict guard and external-change
   handling.
5. Agent authoring: current confirmed upsert/delete switched to the same revision
   owner; UI-only result links and catalog refresh at a later run boundary.
6. Explicit artifact import/export with provenance and no automatic activation.

Tests cover built-in reservation, tool/skill collisions, host/mode filtering,
core/reference fingerprint drift, compaction reload, stale editor conflict, atomic
multi-file manual save, restore/delete replay, import of malicious paths/front
matter, chat deletion independence and fail-closed GC. Real editor/clipboard/file
dialog behavior remains a Windows WebView2 qualification gate.

### Continuity after compaction — 2026-10-01

Core bodies use Gateway whole-read assembly, including bodies larger than one
provider page. The bounded context working set restores complete current skill
results and needed reference coverage from saved events/CAS. Compaction alone is
not a reason to load a skill or admit an already callable schema again. A new user
turn retains selections and may reuse current source evidence. Complete selected
instructions and specialized schemas are reconstructed from the pinned publication
without a repeated `common.skills_load`; changed or unavailable dependencies require
explicit resolution. Unchanged direct optional schemas may also carry across turns
when their exact admission remains valid and fits the request budget. Body
omission/staleness is distinct from the historical successful load. Prompt schema
33 originally required manual review; schema 35 replaces that flow with automatic
default migration and a recovery copy before the next settings save.
Schema 41 uses the same migration for sparse user updates, current Task List notes
and evidence-grounded compaction. Skill/schema loading alone requires no visible
comment; independent known missing capability reads may be batched. The runtime
retention contract is owned by [Conversation protocol](conversation-protocol.md#conversation-context).
