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

Current custom persistence is atomic current-file replacement, not revision
history. `version` is a manual label. Runtime separately computes `revision` as a
SHA-256 package fingerprint over the normalized core body and ordered reference
revisions. Editing the core or any reference changes the package revision, but old
package bodies are not retained and delete removes the custom package directory.

The existing Library UI already owns skills under `Library → Instructions → Skills`.
It supports Markdown edit/preview, references, enable/disable, clone and custom
delete; built-ins are read-only. It does not currently expose useful revision
history, restore, provenance or a complete version display.

Agent-side core/reference upsert and delete use `common.skills_upsert/delete` and
confirmation policy. Direct UI Save/Delete is an explicit manual operation guarded
against an active run. A mutation becomes available through a freshly built catalog
on the next run boundary; it does not rewrite the immutable catalog of an already
accepted model step.

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
