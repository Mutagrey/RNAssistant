# Phase 10D — final architecture audit

Date: 2026-08-31
Baseline: `40402b3a5e3d4dbb1f51a9f4d96d2a7bea7aa6ec`

## Scope

This docs-and-verification slice closes the mandatory host-neutral Phase 10. It
checks canonical architecture instructions, migration ownership/removal gates,
physical source paths, old-style project inclusion and the existing dependency
suite. It changes no runtime, protocol, tool, COM, VSTO or WebView behavior.

## Physical and project audit

- Final production inventory: 107 Core, 173 Office and 15 OfficeHosts C# files.
- `DocumentIdentity.cs` and both `VbaProjectSupport` partials exist only under
  `RNAssistant.OfficeHosts`; `AssistantRuntime.cs` exists only at the root Office
  application-façade path.
- The old paths, linked duplicates and removed `AgentResponseParser.cs` are absent.
- Production source inclusion passes, including the updated Office/OfficeHosts
  old-style projects and intentional harness source links.
- Forty-nine literal `src/`, `web/` and `tests/` references in canonical
  architecture instructions resolve to current files.

## Authority and adapter audit

`LegacyToolDefinitionAdapter` has three current production call sites:

- `ToolPackSnapshotFactory` uses `Adapt` and `BindingFor` for remaining legacy
  `ToolDefinition` catalogs/execution;
- `ConversationProtocolContext` uses `PolicyFor` for the conservative legacy
  read-batch projection.

No resource catalog/data-plane file uses that adapter, and `ProjectRead` is absent
from production and tests. These remaining calls are one-way compatibility seams,
not a second ToolPack or resource authority.

The audit found stale removal text for the active VBA mutation/package adapters:
it still named completed Phase 8 as a future direct-handler switch. The canonical
migration map now assigns document binding to WQ0/5B2 and the optional post-stable
direct-handler/typed-host cleanup to a separately admitted Phase 11 slice. Existing
Phase 6 VBA execution/lifecycle remains stable-core behavior and does not block
Phase 12. Permanent journal-store ports remain documented as architectural ports,
not compatibility stores.

## Verification

- `architecture:`: 4/4 pass.
- `harness: production projects include all source files`: 1/1 pass.
- Replaced-path and resource-adapter scans: pass.
- Canonical literal source references: 49 checked, 0 missing.
- `ValidateVersionFormat`: pass; product remains `16.1.0-dev`.
- `git diff --check` and changed Markdown local-link validation: pass.

No full candidate harness, Office/VSTO build or Windows/WebView scenario was run in
this docs-only slice. Those checks belong to the immutable candidate and accumulated
Milestone WQ runbook; no Windows gate is closed here.

## Result and next gate

Phases 0–10 are complete host-neutral. At the first available Windows x64 + Office
x64 + VS 2022 window, run WQ0 identity probe before choosing production 5B2
identity/factory semantics. Then complete 7D and the accumulated qualification
runbook. Phase 12 remains blocked on that evidence; optional Phase 11 is not a
prerequisite.
