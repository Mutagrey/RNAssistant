# Phase 10A — physical and dependency boundary audit

Date: 2026-08-31
Baseline: `9bbf088149625d150aaa3962caf2c02355d8daf7`

## Scope

This host-neutral slice inventories current production files, namespaces, project
includes, live compatibility consumers and canonical architecture instructions.
It adds regression checks for already switched boundaries. It does not move
production files, change namespaces, alter runtime behavior or start optional,
5B2, 7D, COM, VSTO or WebView work.

## Findings

The production inventory contains 107 Core, 176 Office and 12 OfficeHosts C# files.
Core has no folder/namespace mismatches. Office has 27 and OfficeHosts has five,
but these are not 32 defects: root `RNAssistant.Office` is the intentional public
façade/host-port namespace for controller partials and several runtime contracts,
while folders express ownership. A mechanical rename would create broad API churn
without improving dependency direction.

Three files are real physical ownership debt (R49):

- `Office/Runtime/DocumentIdentity.cs` is used only by Excel/Word/PowerPoint host
  adapters and neutral identity tests;
- `Office/Vba/VbaProjectSupport.cs` and its package-guard partial are used only by
  those host adapters and fake-VBE harness cases;
- no Office service, tool, controller or domain consumer depends on these files.

`Office/Runtime/AssistantRuntime.cs` is an application/UI lifetime façade rather
than document/tool runtime. It can move to the root façade path without namespace
or behavior changes. `LegacyToolDefinitionAdapter.ProjectRead` is not dead code:
the four native resource handlers still need a `ToolDefinition` projection for the
current mixed controller/callable catalog. It must be moved to the existing
controller-definition owner before removal; deleting it now would break schemas.

The only superseded canonical source path found was the removed
`Core/Tools/AgentResponseParser.cs`; `docs/architecture.md` now points to the actual
v4 `Core/ModelProtocol/ConversationResponseParser.cs`. No other switched mandatory
legacy branch was proven unused. Explicit 5B2/7D adapters, R37 historical read
adapter, mixed ToolDefinition/domain projections and Phase 11 consumers retain
their documented gates.

## Ordered cleanup groups

1. **10B1 host identity:** `git mv` `DocumentIdentity.cs` to
   `OfficeHosts/Identity`, update its namespace, both old-style projects, harness
   link and exact consumers. Identity algorithms and WQ0 semantics stay unchanged.
2. **10B2 host VBA:** separately `git mv` both `VbaProjectSupport` partials to
   `OfficeHosts/Vba`, update namespace/projects/harness/host consumers. Mutation
   services, journals, guards and dynamic COM algorithms stay byte-equivalent.
3. **10C application façade:** move `AssistantRuntime.cs` out of the document/tool
   Runtime folder with namespace/lifecycle unchanged.
4. **10C resource projection:** separately replace the resource-only `ProjectRead`
   call with the existing controller definition owner, then delete that method.
5. **10D final:** re-run source inclusion/dependency checks and reconcile
   architecture, AGENTS, migration status and remaining Phase 11 gates.

Each numbered invariant is a separate commit/change; no mass namespace rename.

## Dependency checks

`architecture: mandatory dependency direction` scans current production sources
and fails on these forbidden directions:

- Core.Agent to Office/UI;
- ModelProtocol to tool execution;
- VBA domain to UI;
- resource owners to AgentKernel/application loop;
- OfficeHosts directly to WebView types;
- UI/bridge to tool/domain executors.

The prior typed event, conversation and run-view checks remain in the same filter.
Production source inclusion remains independent so source-linked harness globs
cannot hide a broken old-style project.

## Verification

- `architecture:` — 4/4 pass;
- `harness: production projects include all source files` — 1/1 pass;
- canonical architecture source paths — 0 missing;
- `ValidateVersionFormat`, diff check and 232 local links in 10 changed Markdown
  files — pass.

The harness build reports four existing CA1416 identity-probe warnings. No Office,
VSTO or real WebView validation was run. R49 and every accumulated Windows gate
remain open.
