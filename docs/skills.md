# Skill Library and package revisions

## Status and boundary

The progressive skill read contract is implemented. Immutable custom package
history, import/export provenance and restore/delete UX are a deferred Phase 11
Skills authoring contour. They do not expand WQ-A, WQ or Phase 12.

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
up to 64 direct UTF-8 `references/*.md` files. The core front matter contains `id`,
`host`, `name`, `description`, human-authored `version` and `enabled`.

The visible catalog is built-in first, then custom, filtered to `Common` plus the
current adapter host. A custom package cannot shadow a built-in id. Tool and skill
ids share one namespace and a collision fails request construction.

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
UI. A mutation becomes available through a freshly built catalog on the next run
boundary; it does not rewrite the immutable catalog of an already accepted model
step.

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

Capacity is reserved before catalog hydration. The existing limits remain 500,000
characters and 2,100,000 bytes; the editor enables only a complete verified UTF-8
snapshot. Source-file revision/byte length (which may include a file BOM) remain
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
edits remain visible and conflicts block further writes. Core/reference mutation
body transport (including the reference mutation echo) remains an open consumer
in the same cutover.

## Built-in guidance contract

Built-in skill metadata is selection guidance; the complete Markdown body is loaded
before skill-governed work. A built-in body owns its domain prerequisites, ordered
workflow, quality constraints, failure/recovery behavior and evidence-based
definition of done. It does not copy JSON schemas or gain execution authority.

The system prompt owns only the universal Understand → Prepare → Inspect → Execute
→ Verify → Finish lifecycle. The tool prompt owns catalog discovery/admission and
call-envelope rules. The current loaded tool description and strict schema remain
authoritative for the exact operation, root arguments and returned evidence. If a
skill and schema disagree, the model follows the higher-priority runtime prompt and
schema and reports the inconsistency rather than inventing compatibility fields.

Every exact `common.*`, `excel.*`, `word.*`, `powerpoint.*` or `outlook.*`
capability named by built-in guidance must resolve to the current tool/skill
catalog. Harness checks that invariant across all four hosts, rejects duplicate or
empty built-ins and prevents retired `TOOL_RESULT ok=true` guidance from returning.

## Model context

- Chat has an empty capability catalog and cannot load skills.
- Plan and Agent receive enabled skill metadata only: exact id, `kind:"skill"`,
  summary, package revision, body size and reference count.
- When the user names a skill or its summary clearly matches the task, the model
  reads the exact id through `common.capabilities_read`.
- A core read returns complete revision-matched Markdown and reference metadata. A
  needed reference is read separately in bounded chunks through the same tool.
- Reading a skill does not activate a router, load tool schemas, add callable tools
  or weaken confirmation/safety policy. Compaction, lost complete evidence or a
  catalog revision mismatch requires another exact read.
- Under Host Fabric, the capability catalog comes from the selected execution
  endpoint. A window hosted by Excel but targeting Word receives `Common + Word`
  skills, never skills chosen from the window-owner host.

The current `Discuss skill` UI convenience copies a Markdown definition into custom
chat context. The target path instead records a compact exact skill id/revision hint
and lets Agent/Plan perform the normal `common.capabilities_read`; it must not create
a second durable body transport. Chat mode cannot use this shortcut because skills
are unavailable there.

## Target Skill Library UX

Skills remain in Library, grouped by built-in/custom and host. They are not rows in
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

The Phase 11 store must be append-only for package facts and content-addressed for
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

## Phase 11 slices and gates

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
