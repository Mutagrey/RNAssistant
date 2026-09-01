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
  `common.capabilities_*` for model discovery/schema reads;
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
flat package store is not immutable revision history. Model authoring uses the
separate `common.tools_definition_read`, `common.tools_validate`,
`common.tools_upsert` and `common.tools_delete` operations. Since 11J1 these four
exact Agent-only operations use native typed handlers; confirmed upsert/delete bind
accepted arguments to the current effective definition, reject stale state, mark the
storage boundary and require read-back verification. They cannot alter the immutable
ToolPack of the accepted run. Since 11J2, execution and Library install/remove/status
capture one complete `ToolPackageSource` v1. Its deterministic content revision is
separate from the manifest package version and is pinned with the native handler in
the accepted run. Library actions return typed result v1 with status, source revision,
dispatch and effect evidence; PascalCase/legacy result fallbacks are unsupported.
Since 11T10 the existing Tools editor also uses lowercase
`rnassistant.toolLibrary` v1 and explicit revision-guarded create/update/rename/
delete mutation DTOs through the same `ToolAuthoringService` as model authoring.
Controller-owned catalog reconciliation, `StoragePath` identity, generic execution
and unversioned response fallback are absent. This does not make the flat store
immutable history.

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

## Phase 11 slices and gates

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
