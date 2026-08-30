# Phase 10C1 — application façade physical move

Date: 2026-08-31
Baseline: `7d589c1452e8083f124b1381a08749738cce979f`

## Scope

`AssistantRuntime.cs` moved with `git mv` from `RNAssistant.Office/Runtime` to the
root `RNAssistant.Office` application-façade path. The production old-style project
include was updated.

The file remains byte-identical. Its `RNAssistant.Office` namespace, controller/pane
lifecycle, disposal order, web-root resolution, constructors and all VSTO/Desktop/
OfficeHosts consumers are unchanged. No forwarding type, linked duplicate or second
lifetime owner was added.

The source-linked harness intentionally does not compile this WinForms/WebView
lifetime façade; it uses its existing controller stub. Therefore there was no harness
source path to rewrite. Production project inclusion and a dedicated architecture
assertion verify the physical owner, while real Office/WebView lifetime remains a
Windows qualification gate.

## Cleanup boundary

Document/tool coordination stays in `Office/Runtime`; the root file now reflects the
existing public application namespace and composition responsibility. The separate
resource catalog projection is untouched.

The next atomic 10C2 change must move the four resource read projections from
`LegacyToolDefinitionAdapter.ProjectRead` to `ControllerToolDefinition`, preserve
their exact descriptor/policy/schema, then delete only `ProjectRead`. It must not
change native handlers, ToolPack authority, dispatch or model wire.

## Verification

- mandatory architecture checks — 4/4 pass;
- production old-style source inclusion — 1/1 pass;
- moved source is byte-identical to baseline;
- old source/project include and duplicate paths are absent;
- `ValidateVersionFormat`, `git diff --check` and changed-document links — pass.

Production Office/VSTO/WebView compilation and runtime validation was not performed
on this machine. Windows x64 + Office + VS 2022 qualification remains required.
Product version stays `16.1.0-dev`; no release/tag/push.
