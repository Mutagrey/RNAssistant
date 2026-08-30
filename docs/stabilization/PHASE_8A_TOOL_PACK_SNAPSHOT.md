# Phase 8A — immutable ToolPack run authority

Date: 2026-08-30
Baseline: `83789254ee8238cc22b37d913b8cfcb4843a7782`

## Scope

This host-neutral slice introduces the immutable execution snapshot required before
changing callable-schema lifecycle. It does not disable the current LRU, choose the
final Excel/VBA core pack, extend snapshots, change compaction, add events, or move
the remaining resource handlers.

## Runtime boundary

- `Core.Tools.ToolPackSnapshot` copies and validates exact typed registrations. One
  canonical SHA-256 registration revision covers descriptor/schema, policy,
  handler/entry point/scope/host, and package fingerprint; the pack revision also covers
  mode, host, and ordered membership.
- `ToolPackSnapshotFactory` is the only current `ToolDefinition` conversion boundary.
  It selects exact native bindings where handlers have migrated and one explicit
  legacy binding for remaining consumers.
- `ConversationKernelAdapter` captures one snapshot after run filtering and reader
  schema binding. Native runtime registers those captured registrations directly.
  Legacy `Describe` reads the same snapshot and a pre-dispatch recheck rejects drift
  with `tool_registration_changed` before any effect.
- Confirmation continuation rebuilds current authority; the existing kernel policy
  comparison rejects descriptor, policy, binding, scope, host, or package replacement under
  the same id.
- The old ad-hoc `ConversationRunService.ToolExecutionFingerprint` implementation and
  alias were removed. Model context, `AgentKernel`, Resource Fabric, result wire, and
  domain handlers are unchanged.

## Regression cleanup

- R22 catalog expectations now match the audited public sets: Excel 15, Word 9,
  PowerPoint 9, Outlook 5. Removed ids remain explicitly rejected.
- Two stale fixtures were corrected to the already-active contracts: `excel.read_range`
  uses `address`, and the edited Chat completion uses status-free response v4. No
  production behavior was changed for either finding.

## Verification

The final source snapshot is covered by 90 distinct host-neutral cases:

| Filter | Cases |
|---|---:|
| `tool pack:` | 2 |
| `agent:` | 36 |
| `chat:` | 13 |
| `plan` | 2 |
| `conversation v4:` | 13 |
| `tool runtime:` | 14 |
| `excel read:` / `excel write:` | 8 |
| `tools: compact catalog rejects removed aliases` | 1 |
| `harness: production projects include all source files` | 1 |

MockDemo compile (0 errors / 3 existing CA1416 warnings), version-format validation,
194 local links in 10 changed Markdown files, and diff checks passed. Windows x64 + Office x64 +
VS 2022 WQ-PACK remains mandatory.

## Remaining Phase 8 work

Phase 8B has since delivered deterministic core membership, full-budget atomic
admission, monotonic explicit loading, and removal of LRU eviction while preserving
this execution snapshot; see [8B evidence](PHASE_8B_CALLABLE_TOOL_PACK.md).
Confirmation/compaction/crash rematerialization, durable extension events, the
remaining resource native handlers, Resource-data ADR, and Windows WQ-PACK stay
open until their ordered slices.
