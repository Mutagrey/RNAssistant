# Phase 1C — Transitional completion guard

Baseline: `5df587b`, clean `stabilization/16.1`. Scope: one runtime completion
invariant and its minimal bridge/UI projection. Phase 2 is not started.

## Changed invariant

Model `completed` means the loop ended, not that mutations succeeded.
`RunSummaryBuilder` owns independent `clean/errors/unknown` health from actual
`ToolResult` and effective safety metadata. Unknown write/local effects dominate
errors; any read/write error dominates clean. No model text, descriptions, tool
name suffixes or model-supplied summary fields are inspected for safety decisions.

`RunExecutionSummary` carries `ReadOk`, `ReadError`, `WriteOk`, `WriteError`,
`WriteUnknown` counts. These count top-level invocations, not nested pipeline steps,
changed cells, actual non-no-op mutations or independently verified document diffs.
The guard preserves the existing executor's evidence; it adds no COM verifier.

Pending confirmation has no final effect. Confirmation carries the previous
logical turn's summary into the new run, records the actual confirmed result before
attachment/model preparation, and does not count it twice. A new user turn resets
the builder. Published snapshots do not change when later tools execute.

Unknown survives later errors/success, cancellation and post-result delivery
failures. An escaping exception or missing result after entering a possible
mutation cannot certify its effect. Rejected model attempts add no tool errors;
protocol exhaustion still fails the existing lifecycle. v2 status/text are retained
without being treated as effect evidence; retry limits are unchanged (R20).

## Red → green evidence

Before any production edits, the existing four write ok/error/unknown/no-write
characterization cases received runtime-summary assertions. Running
`dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization`
returned **3 pass / 4 fail**: each new assertion failed because runtime evidence
was absent. The three protocol-repair cases remained green.

After the guard, the same filter returns **7/7 pass**. The write-error case also
supplies a forged `executionSummary` extra root field in the model's v2 JSON:
runtime still reports the real error and zero successful writes. The unknown case
uses the real local VBA journal and a fake host whose read-back differs from both
before and intended source; it is not a fabricated model-status fixture.

## Verification

Commands run from the repository root. After the successful harness build,
unchanged binaries were reused with `--no-build` for focused filters.

| Command | Result |
|---|---|
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` | red 3/7 → green 7/7 |
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"` | 5/5 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41, includes characterization |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: turn lifecycle"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1; new source is in old-style csproj |
| `node tests/web/completion-guard.test.js` | 8/8 |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

There are 61 distinct passing targeted harness cases, plus 8 Node projection
cases. The full harness was not rerun. Its last recorded result is **320/321** in
Phase 1B, with the independently reproduced baseline catalog mismatch R22; this
change neither fixes nor claims a green full suite.

The guard tests cover effective nested/local safety, precedence, no prose/name
heuristics, reference-identity invocation deduplication, uncertainty mapping,
missing historical evidence, actual executor failure followed by cancellation,
confirmation preserving earlier errors/unknown, and fresh-turn reset. The existing
lifecycle test now covers canonical event replay, independent message/run clones,
typed DTO serialization and absence of runtime summary fields in model transport.

Node loads the real `app-utils.js`, `app-agent-model.js`, `app-agent.js` with a
minimal DOM; unrelated trace/media helpers are stubbed. It checks warning position
outside collapsed details, unchanged model text, no-write response, legacy/corrupt
summary rejection, cancellation and missing evidence at a recovery boundary. It
does **not** validate layout, a browser engine or WebView delivery.

## Projection and persistence boundaries

Visible tool/final/diagnostic messages and `LastRun` carry scalar summary snapshots.
Existing typed session operations persist them; there is no new store, index,
envelope schema, history migration or replay decision algorithm. Send/confirmation
responses expose a typed optional `executionSummary`. History UI reads the same
evidence on message DTOs, including ordinary chat-state loads.

Unknown/error warnings appear before the final answer and outside collapsed trace.
A no-write response says there are no confirmed changes. Missing summary on the
terminal/recovered boundary is unverified; it cannot inherit an earlier clean
snapshot. Model text is preserved as model text, including false claims.

Phase 1B causal markers remain intact. `run.summary.created.Status` is still the
legacy lifecycle marker, not health. Health is in the correlated canonical
message/run operations. No second logging or execution state machine was added.

## Legacy adapter and limits

| Adapter | Owner | Consumers | Removal/switch phase |
|---|---|---|---|
| `ToolResult` + effective `ToolSafetyPolicy` → `RunSummaryBuilder` | Runtime / ToolRuntime | ConversationRunService, confirmed tool continuation | Builder moves with AgentKernel in Phase 3; legacy result mapping replaced by typed evidence in Phase 4 |
| Optional `RunExecutionSummary`, absent old history/continuation evidence | Application / Persistence / UI | ChatMessage, LastRun, clones, send/confirmation DTOs, static UI | Full RunSummary/projection switch in Phases 3/9; remove obsolete adapter paths in Phase 10 |

Mutation `partial_failure`, `unknown`, `interrupted_unknown`,
`tool_effect_uncertain`, missing result and unclassified policy are treated
conservatively. This can overreport unknown where later typed domain evidence
would prove a definite outcome (R23). An old pending turn with no summary keeps
unknown health without inventing historical write counts. Existing `Success`
semantics, possible no-ops, domain verification gaps and process-crash recovery
remain unchanged; Phase 4/6/7/9 must qualify those paths.

Production controller wiring was inspected, **not compiled or executed here**:
the harness substitutes `AssistantControllerBridgeStub`. Actual controller
confirmation, cancellation, save/reopen, real WebView, Office COM, VSTO/ClickOnce
and Windows x64 + Office x64 + VS 2022 validation remain not performed (R21/R16).
This is host-neutral containment, not release qualification.

## Versioning and scope

Product remains `16.1.0-dev`; `Directory.Build.props` and the mandatory master plan
are unchanged. No Git tag is created or moved, no push or release script is run.
The change touches 10 production files including the project entry; domain tools,
VBA/COM, Resource Fabric, model parser/protocol and persistence algorithms are not
changed. There are no aliases, dual-write paths or new feature flags.

## Changed files

25 files: 10 production, 6 test/test-documentation, 9 documentation.

| Paths | Purpose |
|---|---|
| `src/RNAssistant.Core/Models/ChatModels.cs` | Optional scalar runtime summary on run/messages |
| `src/RNAssistant.Office/Services/RunSummaryBuilder.cs` | Centralized outcome aggregation and legacy adapter |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Observe actual results; publish independent health |
| `src/RNAssistant.Office/Services/ChatCloneService.cs` | Independent projection copies |
| `src/RNAssistant.Office/Controller/AssistantController.ChatExecution.cs` | Terminal/failure summary and send DTO |
| `src/RNAssistant.Office/Controller/AssistantController.Agent.cs` | Confirmation evidence continuity |
| `src/RNAssistant.Office/Contracts/BridgeDtos.cs` | Typed optional bridge field |
| `src/RNAssistant.Office/RNAssistant.Office.csproj` | Explicit source inclusion |
| `web/js/app-agent-model.js`, `web/js/app-agent.js` | Evidence projection and visible warnings |
| `tests/RNAssistant.Harness/Program.SimpleAgentTests.cs` | Red→green characterization and confirmation regression |
| `tests/RNAssistant.Harness/Program.AgentSafetyTests.cs` | Aggregation, metadata, uncertainty, cancellation |
| `tests/RNAssistant.Harness/Program.SessionEventStoreTests.cs` | Replay, clones, DTO/model isolation |
| `tests/RNAssistant.Harness/Program.cs`, `tests/RNAssistant.Harness/README.md` | Register focused tests and document commands |
| `tests/web/completion-guard.test.js` | Actual JS projection/render regression |
| `README.md`, `CHANGELOG.md`, `docs/conversation-protocol.md`, `docs/session-events.md` | Current runtime contract and user-visible fix |
| `docs/stabilization/PROGRESS.md`, `docs/stabilization/BACKLOG.md`, `docs/stabilization/RISK_REGISTER.md`, `docs/stabilization/MIGRATION_MAP.md` | Phase status, R23, adapter owners/consumers/removal gates |
| `docs/stabilization/PHASE_1C_COMPLETION_GUARD.md` | This evidence and validation limits |
