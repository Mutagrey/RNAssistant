# Phase 2A — ModelProtocol boundary

Baseline: `40282c01ceeb8333ed86fc0a2ca25e266cb75924`, clean `stabilization/16.1`.
One extraction within the model/conversation contour: six production files,
including the Core project file. Phase 2 is in progress; Phase 3 is not started.

## Changed invariant

`ConversationRunService` makes one `IModelProtocol.GetResponseAsync` call per
logical step. It does not call the endpoint, parse responses, count format retries
or select fallback. Core returns one accepted response with accepted completion
metadata, or a typed protocol/budget/provider/cancellation/infrastructure failure.
The original exception is rethrown only by the temporary controller adapter.

Model attempts use an unchanged accepted message sequence, plus at most one
current repair instruction. Invalid bodies/reasoning stay out of replay and the
result contract. Existing diagnostics retain rejected evidence. Progress remains
provisional until acceptance; its Office projector resets for each raw attempt.

Media lifetime is the one intentional behavioral correction: the old integration
test expected media to disappear after the initial raw response. It failed after
extraction (`expected 0, got 1`). The updated assertion checks identical accepted
prompt/resource evidence during repair, then release/exclusion after the logical
step. This is a requirement change, not a claim of a new test failing on baseline.
No resource URI, provider, CAS, tool or persistence algorithm changed.

## Verification

Commands run from the repository root. The first new filter rebuilt the linked
Core and Office-neutral source with C# 7.3; later filters reused that binary.

| Command | Result |
|---|---|
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` | baseline 7/7 |
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` | extraction 7/7 |
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | 8/8 new cases |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41, includes characterization and updated media test |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"` | 5/5 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1; explicit old-style source inclusion |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

There are **68 distinct passing targeted harness cases**. New Core tests cover
protection text, HTML, malformed/empty/truncated JSON, schema violation then valid
acceptance, clean repair prompts, typed exhaustion, timeout/network/server/rate
errors before and during repair, cancellation at three boundaries, bounded opt-in
fallback and run isolation, native refusal, trace failure policy and prompt budget.
All endpoint responses are fakes; no live tLLM or HTTP integration was run.

`git diff --check` and 37 relative Markdown links pass. The master plan,
`Directory.Build.props` and all tag refs have the same SHA-256 as the baseline.
Core has no Office/UI references; the removed loop/repair helpers have no callers.

Full harness was not rerun: this is one model/conversation extraction, not a change
to tool domains, resource providers, storage or UI. The last full result remains
320/321 in Phase 1B, with baseline catalog mismatch R22. No green full-suite claim.
UI/Node tests were not rerun because no UI code changed.

Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView: **not performed**.
The harness uses a controller stub, so production controller failure wiring was
reviewed only in code; original provider/cancellation exceptions remain intact.

## Legacy paths and remaining work

- Removed raw completion, parsing, repair, refusal, budget and model trace helpers
  from the loop; removed the old Office repair-message builder. No aliases.
- Existing v2 parser/schema/history remain current; no v3 adapter or cutover yet.
- `Failure.Cause` adapter: Runtime / Application owner, loop/controller consumers,
  Phase 3 removal. Stream callbacks adapt existing presentation, not a second loop.
- R20 stays open: 20 configured retries still permit 21 raw requests. The total
  attempt limit, provider policy (including fallback during repair), v3 schema and
  v2 adapter/cutover are separate remaining Phase 2 work.
- R24: retaining media through repairs can increase request traffic and peak
  lifetime. Release occurs on acceptance/failure/cancellation; byte/provider
  qualification is still pending. Existing R21/R22/R23 remain open.

See [ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md) and
[MIGRATION_MAP.md](MIGRATION_MAP.md). `16.1.0-dev` remains unchanged; no tag,
push or release preparation is performed.
