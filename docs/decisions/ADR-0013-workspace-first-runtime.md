# ADR-0013: Workspace-first runtime

Status: accepted for staged implementation, 2026-10-04. Baseline: `ae05b205519365c9a2b02bce8a218afa84302158`.

## Decision

One conversation-response v6 kernel, model protocol, tool runtime, event stream,
CAS and resource authority serve workspace and Office sessions. A workspace root
contains ordinary editable files. Its manifest is configuration, never a permission
grant or a second content head. Office remains an optional resource module.

| Previous boundary | Workspace-first boundary |
|---|---|
| Authored files and chats require `DocumentAuthorityId` | New chats carry `WorkspaceId`; Office authority is present only for a bound document |
| Current authored HTML aggregate is CAS-owned | Current source is a file; CAS retains exact history and recovery payloads |
| Office adapter and pane are required by composition | CLI has no Office or UI dependency; Office composition is migrated by later stages |
| Model tools use only resource selectors without paths | Checked relative paths inside the opened workspace are semantic targets; runtime owns absolute locator, identity and revision |
| Host endpoint is a production premise | External COM with exact binding is the intended Office route; M8/Windows verification remains open |
| Every file mutation needs a separate worker | Development profile may use the guarded file owner without shell/process execution; enterprise permission policy is not inferred from the manifest |

The existing v6 actions (`tool`, `continue`, `done`, `blocked`, `needs_input`),
single-mutation step policy, runtime call IDs, frozen evidence, prepared/dispatch/
read-back distinction and unknown-effect non-replay remain in force. A file's
content hash is not its revision ID. A historical read never falls back to current
disk bytes. Existing document chats are not rewritten or deleted.

## Delivery boundary

The initial CLI uses the shared `AgentKernel`, `ModelProtocolClient`,
`ToolRuntime`, `ChatStore`, `ResourceAuthorityStore`, `ResourceMutationJournal`
and CAS. Its current file adapter and prompt assembly are temporary until the
filesystem provider enters the existing `ResourceGateway` and frozen
`ModelContextCompiler` path. The active Office HTML writer remains for old
document flows until M6; it must not write the CLI's file-backed results.

See [migration map](../stabilization/MIGRATION_MAP.md) and
[current progress](../stabilization/PROGRESS.md) for removal gates and evidence.
