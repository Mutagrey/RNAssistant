# ADR-0015: Independent source, publication and data authority for plugins

Status: accepted and implemented host-neutral, 2026-10-19; native qualification open.
Owner: application plugin services. Canonical contract: [Plugins](../plugins.md).

## Decision

Plugins are static HTML/CSS/JS workspaces that can be edited by an agent and used
without rebuilding RNAssistant. Keep source workspace identity, immutable published
release identity and selected user-data authority independent. Neither manifest,
vendor resolution nor source provenance grants data access. Publication captures
code/dependencies in existing CAS and atomically switches one registry head.
Accepted operations retain their publication and data grant; preview receives a
separate test workspace, and disable revokes new/current execution without deleting
user projects or source chats.

Core owns contracts and immutable storage. Application owns admission, publication,
grants and command lifecycle. WebView2 binds typed frame channels to individual
origins and hosts managed Workers independently of the visible screen. Plugin code
never enters the privileged root DOM or receives the main Office bridge.

Reuse WorkspaceConversationService, ConversationRunService/AgentKernel, ToolRuntime
and WorkspaceFileService. Shared workspace application code is imported into both
Framework 4.8 and CLI targets; Chromium verification is an optional platform port.
There is no second model loop or .NET 8 requirement for the Office product.
Runtime retains candidate/project/release tokens through prepared confirmation;
model arguments/results use semantic intent and domain facts.

One vendor resolver serves supplied and local exact versions/hashes. Execution
admission belongs to the consuming context: HTML sandbox, plugin capabilities and
portable export are separate policies. Scanning/importing a vendor never executes it;
an observed version cannot be replaced with different contents. New contents require
a new version. No external dependency download is implied.

Data Templates is the first ordinary plugin. Its source distribution authority is
now PluginManifestV1; exporter, package inventory, vendor checks, export tests and
docs switch together, then the old application manifest and root mounting adapters
are removed. The data-project format and shared browser engine remain unchanged.
The existing compiled Tabular pin stays only for its explicit legacy native/CLI
consumers until native admission/isolation/revocation/drain evidence closes their
[removal gate](../stabilization/MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16).
It is not an execution fallback for plugin failure.

## Consequences

Editing code cannot silently alter active commands or widen their data permissions.
Failed preparation leaves the old publication; an ambiguous publication/write is
reported unknown and is never automatically retried. Source code, private staging
and user results have separate lifetimes and identifiers.

Only files and explicitly selected data are in v1. Memory, conversation events,
direct Office mutations, external APIs and other tabs need their own capability
contracts and acceptance evidence. Actual Windows/WebView2 behavior remains an
explicit release gate; host-neutral and Chromium tests do not close it.

This decision supersedes only the distribution manifest and root-mounting portions
of [ADR-0014](ADR-0014-data-templates.md). Its independent application/domain/data
ownership and standalone export requirements remain in force.
