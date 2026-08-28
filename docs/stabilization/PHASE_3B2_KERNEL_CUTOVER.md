# Phase 3B2 — Kernel production cutover

Date: 2026-08-28. Baseline: `c1628ce`. Branch: `stabilization/16.1`.
Scope: Phase 3 only; Phase 4 is not started.

## Scope and boundaries

The switch coordinates 23 production files, including project includes and the
deleted builder. This exceeds the ordinary §14.3 file budget because normal and
confirmed execution, immutable contracts, the existing event store and its
projections must switch together. The earlier 3A/3B1 steps already isolated model
context and introduced the pure kernel; retaining another loop for this switch
would leave two outcome authorities.

- `ConversationRunService` calls `AgentKernel.RunAsync/ResumeAsync`; the controller
  no longer executes a confirmed tool or seeds/aggregates its result itself.
- `ConversationKernelAdapter` separates model, tool and store ports into partials.
  Prompt/compaction, working set, media and provider metadata remain outside Core.
  Existing executor, VBA and Resource Fabric algorithms are unchanged.
- Existing typed `run.updated` operations carry immutable `KernelState`. No new
  store, event envelope, ID index, historical backfill or fallback path is added.
- Whole-response acceptance precedes tool dispatch. Result projection preserves
  native `call → result` pairs in the current request and after event replay.
- Actual counts are durable before optional result/model-input preparation.
  Known effects survive preparation failure; unresolved in-flight effects remain
  unknown on recovery. Recovery does not execute tools.

Removed: old Office `RunLoopAsync`, `ContinueAfterToolAsync`, `RunSummaryBuilder`,
its controller seed/observe path, `Failure.Cause`/rethrow, mutable Office accepted-ID
bookkeeping, and controller terminal fallback mapping. Retained adapters and
removal gates are in [MIGRATION_MAP](MIGRATION_MAP.md).

## Verification

The final harness build uses C# 7.3 and the real neutral source files:

```sh
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"
```

| Filter | Pass | Evidence |
|---|---:|---|
| `agent:` | 34 | Existing modes/protocol/resources/LRU/limits; confirmation executes through kernel; native refusal is locally failed; new native read-batch live/replay pairing; no duplicate usage on runtime diagnostic |
| `kernel replay:` | 9 | Actual ChatStore normal/error/unknown, pending and cancelled confirmation, stale cursor, preparation failure, interrupted write and known-but-unprojected result recovery |
| `protocol context:` | 6 | Full accepted IDs across compaction and all three result roles; incomplete/duplicate evidence rejected |
| `completion guard:` | 5 | Legacy single-result classification; cumulative errors/unknown through real confirmation; cancelled summary reload |
| `conversation:` | 4 | Streaming/projection regressions |
| `plan mode:` | 2 | Existing mode policy and local interaction |
| `chat: uses only read-only resource loop` | 1 | Chat catalog/mode isolation |
| `causal trace:` | 6 | Existing request/tool correlations and optional trace behavior |
| `kernel:` | 41 | Reused within this change: pure kernel ports/outcomes/IDs/limits/cancellation/append tests; later changes affect Office projection and recovery, not this loop |
| `model protocol:` | 15 | Reused: materialized client/parser/repair implementation unchanged by final projection cleanup |
| `preflight` | 3 | Reused: unchanged guards; one case also belongs to `model protocol:` |
| `storage: turn lifecycle` | 1 | Reused existing typed lifecycle/flat legacy projection coverage |
| `storage: interrupted step` | 1 | Reused existing step closure |
| `chat sessions: interrupted` | 1 | Reused old-record recovery |
| `chat sessions: saved run boundary` | 1 | Reused old-record boundary preservation |
| `harness: production projects` | 1 | Reused: all added source includes present; later edits add no source files |

Total: **130 distinct passing cases**, not the sum of overlapping filters. Reused
results are from earlier builds in this same isolated change, not from Phase 3B1.
Full harness and JS were not run; no UI JS changes. Known baseline R22 was not
retested or fixed.

Replay tests use disposable AppData roots, the actual typed event store and
existing executors with a fake Office adapter. They check saved state before
model/tool entry, real local skill persistence, independent reload/clones/headers,
and that stale/CAS failures do not retry a mutation. The compaction continuation
fixture now uses a real CAS artifact; an in-memory-only checkpoint cannot survive
mandatory store projection. Pending arguments retain the original accepted input
despite executor-added schema defaults.

```sh
dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
```

MockDemo: **0 errors, 3 existing CA1416 warnings** in PDF rendering. This compiles
the actual controller and typed constructors; the harness otherwise uses its
controller stub. Controller source review separately confirms lease/reload,
preflight before pending consumption, kernel confirmation, unchanged document
guard, live registry projection, and no save retry for `RunStoreException`.
Version format, staged diff and changed document links are checked before commit.

## Remaining gates

Windows x64 + Office x64 + VS 2022/VSTO, real controller/WebView delivery, DPAPI,
live providers and COM interruption are **not validated here**. R11 is contained
only for this minimal summary/replay matrix; complete persistence/UI qualification
remains Phase 9/12. Legacy effect classification (R23) and the positive local-read
registry remain until Phase 4. Old pending runs without kernel evidence can be
inspected/cancelled/reset, but cannot continue through a legacy executor path.

Parallel docs-only reports R28 (streaming) and R29 (model-owned call IDs) remain
open in the [risk register](RISK_REGISTER.md). This cutover moves accepted-ID
bookkeeping to the kernel, not ID generation out of the model. It does not fix
ID-triggered payload regeneration or establish the cause of the live streaming
complaint; the current v3 wire contract is unchanged.

Product target remains `16.1.0-dev`; no repeated version bump, Git tag, release
script or push is part of this change.
