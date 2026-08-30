# Local Automation Agent

## Status and boundary

This is a deferred post-stable optional program, split into independently admitted
Phase 11 features. It is not part of WQ-A, WQ or the first stable core.

The existing Agent mode remains the conversation loop. Local automation adds a new
target scope and typed tool packages; it does not create a second planner or let
prompts grant authority. Because current chats/context are document-owned, the first
implementation change requires an ADR for an explicit `WorkspaceSession` (or another
non-document owner) before any local tool is published.

Full local automation cannot safely execute inside an Office process. Office-hosted
windows may use browser and read-only capabilities admitted for that profile, but
file mutations and process execution require a signed, IT-approved isolated worker
or broker. If corporate policy forbids that worker, unrestricted files/commands are
not an available feature; Office is not used as a policy bypass.

## Authority model

- A user/admin policy grants exact workspace roots, browser profiles/domains,
  executable identities and capability classes. Defaults are deny.
- The immutable run `ToolPackSnapshot` captures those grants and exact handlers.
  `AgentKernel`, `ToolRuntime`, confirmation, events/CAS and effect evidence remain
  the only execution path.
- Resource reads use revision-pinned `rna://` references through
  `common.resources_*`. Paths, process ids, URLs and prose are not authority.
- The worker never receives Office COM objects. Office mutations remain routed to a
  selected Host Fabric endpoint and its `DocumentSession`.
- Every external/mutating operation records preparation, dispatch boundary and
  bounded result/effect evidence. `ok` alone does not prove a filesystem/process
  effect.

## Separate delivery stages

### LA0 — Workspace session and policy

Define workspace-owned chats/context, local target identity, grants, revocation,
audit/export and worker protocol. Add the required ADR and threat model first. No
file, browser or process tool is callable in this slice.

### LA1 — Read-only files and folders

Add bounded list/stat/search/read for user-approved roots. Resolve canonical paths
before authorization and block traversal through symlinks, junctions/reparse points,
device paths, alternate data streams and unapproved UNC locations. Binary/large
content is exposed as typed resources and viewers, not injected wholesale into model
context.

### LA2 — Guarded file mutations

Create/update/move with expected hash/revision, atomic replacement where supported,
conflict detection and recovery evidence. Delete goes to Recycle Bin by default;
permanent delete is a separate destructive operation with explicit confirmation.
Protected roots and policy files remain denied. External changes create a new
resource revision or a visible conflict, never a silent overwrite.

### LA3 — Browser

Browser is its own package and session. Start with read/navigation in an isolated
profile, then click/type/upload/download and existing-profile attachment as separate
slices. Each has explicit domain, authentication-session, popup, permission,
download and network policy. HTML artifact preview WebView is never reused as a
general browser or browser authority.

### LA4 — Non-interactive process execution

Publish a typed `process.run` that accepts an exact approved executable, argument
array, working directory and bounded environment. Do not invoke a shell or perform
string expansion by default. Enforce timeout, output limits, child-process Job
Object, cancellation, exit/effect evidence and per-run confirmation. Network and
elevation remain separate policy grants.

### LA5 — Shell and interactive terminal

Raw `cmd`/PowerShell and PTY are a distinct high-risk capability, disabled by
default and unavailable in Office-hosted execution. Admission requires explicit
enterprise policy, per-call confirmation, isolated worker identity, bounded
filesystem/network grants and a typed process/terminal artifact. Diagnostic logs
must not masquerade as terminal output.

### LA6 — Desktop/computer control

Opening files/applications and GUI automation are a later package with foreground
target pinning, screenshot/redaction policy and action evidence. It cannot be
implicitly granted by Browser, file or shell access.

## Context behavior

Directory listings, file bodies, browser pages, downloads and process output are
resources. The model receives compact descriptors first and resolves exact bounded
content on demand. User attachments keep the existing draft/commit contract. Tool
results may return exact `ResourceRef` values; they do not create a second path-based
reader. Compaction/replay restores references and accepted capability revisions, not
ambient access to whatever currently exists at the old path.

## Minimum qualification

Each stage needs host-neutral policy/contract/fault tests and Windows qualification
for its real worker. Gates include path races and reparse escapes, file replacement
between preparation and dispatch, locked files, Recycle Bin failure, huge/binary
content, browser auth/profile separation, malicious downloads, command quoting,
timeouts, child trees, cancellation after dispatch, elevation attempts and worker
crash/restart. Red-team cases must prove that prompt text, files and web pages cannot
expand the granted capability set.
