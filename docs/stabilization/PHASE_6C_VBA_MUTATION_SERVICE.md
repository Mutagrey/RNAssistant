# Phase 6C — VBA apply-patch mutation service

Date: 2026-08-29
Baseline: `fba247b`
Status: done host-neutral; Windows/VBE qualification and full Phase 6 DoD remain open.

## Scope and ownership

`common.vba_apply_patch` now has one Office-domain workflow:

```text
VbaToolExecutor argument adapter
→ VbaMutationService guard / ordered patch / prepared journal / dispatch
→ VbaVerifier read-back and module assessment
→ VbaMutationService terminal journal + legacy result adapter
```

`VbaMutationService` also owns the common module journal pipeline used by existing
write/delete/restore callers. `VbaVerifier` owns module write/delete verification
and recovery assessment. The old `VbaToolExecutor.Patching.cs` and duplicate
module journal/verifier helpers were removed.

`HostRuntime` still owns the bound document gate and operation lifetime.
`IOfficeApplicationAdapter` remains the host/COM authority. `VbaJournalStore`
remains the only durable journal owner; CAS bodies, event schema, hashes,
correlation and public Tool Result v1 wire did not change.

## Preserved boundaries

This slice does not move whole-module tool entrypoints, package/rename journal,
the reconciliation outer loop, COM implementations, factories, protocol, UI or
Phase 7. It adds no second read, store, alias or fallback.

The service temporarily accepts `ToolCommand` and returns `ToolResult` so current
consumers can switch without changing semantics. Rollback detection also still
examines legacy result/exception messages. Phase 6D must replace this seam with a
typed domain request/outcome, leave mapping in the executor, remove string-based
classification and cover terminal persistence/fault ordering. These items are
open and 6C does not claim the full VBA vertical slice.

## Regression coverage

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -c Release -- "vba:"` — 59/59 pass.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -c Release -- "harness: production projects include all source files"` — 1/1 pass.
- `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — 0 errors; 3 existing CA1416 warnings.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Changed Markdown links and `git diff --check` — pass.

The VBA filter covers the direct service boundary, stale confirmation, queued
guards, exact/ambiguous patching, backend compare-and-swap, write/delete read-back
drift, restore, journal corruption/reconciliation, package/rename regression and
fake COM paths. No Windows x64 + Office + VS 2022 or VSTO validation was run.
