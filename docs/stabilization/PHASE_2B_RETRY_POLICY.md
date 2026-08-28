# Phase 2B — Total protocol attempts and provider retry policy

Baseline: `d911826c23998a1a526565402823a179cd850751`, clean `stabilization/16.1`.
Scope: one model retry policy; four Core production files and the text/tooltip of
one settings input. No loop, tool, Resource Fabric, VBA, persistence or UI logic
changes. Phase 2C and Phase 3 are not started.

## Changed invariant

`MaxAgentFormatRetries` now means total protocol responses, including the initial
response, with unchanged numeric default 10 and supported range 1–20. One means
no format repair. Twenty invalid responses stop at twenty; a valid twentieth is
accepted. The existing key/value is retained without alias or settings rewrite.

`ModelProtocolRetryBudget` is created once per logical step. Typed timeout,
network and server failures get at most two extra requests for the entire step,
with cancellable delays of 1s then 2s. Format repair never resets that budget.
Authorization/other HTTP errors, 429, size failures and invalid provider envelopes
remain terminal. HTTP parsing and classification are unchanged; fixtures exercise
the typed adapter boundary, not a real server.

One explicit enabled schema fallback is independent of both budgets and works
during repair too. Provider retries/fallback reuse the exact current prompt and
options, without another repair instruction or a consumed protocol response slot.
With configured limit N, the raw completion-call ceiling is N+3, at most 23 per
step, not per run. No automatic Office tool retry is introduced.

Cancellation is observed during backoff and before/after raw completion and
rejection. A late valid response or the final rejected response cannot win over
observed cancellation. Accepted messages, runtime health and diagnostics stay on
their existing paths. Rejected trace `Attempt` remains zero-based; repair message
and exhaustion counts include the first response.

## Red → green evidence

Before edits, `model protocol:` passed 8/8 and `characterization` 7/7.
New assertions, before the production fix, gave:

- `model protocol:` — 7 pass / 2 fail: limit 1 made two requests; explicit schema
  rejection during repair returned failure instead of using fallback.
- `characterization` — 5 pass / 2 fail: configured 20 and out-of-range 99 both
  allowed twenty-one invalid responses.

After the fix and four additional provider-budget tests, `model protocol:` is
13/13; all seven characterization cases pass inside the 41-case Agent slice.
Existing streaming/progressive-tool fixtures now configure two total attempts
when they require one repair. Their behavior assertions are unchanged.

## Verification

Commands ran from the repository root; all endpoint completions are fakes and
provider delays are injected. No real provider request or long delay was executed.

| Command | Result |
|---|---|
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | baseline 8/8 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` | baseline 7/7; new assertions red 5/7 |
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | red 7/9 → green 13/13; C# 7.3 linked build |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41, includes characterization |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"` | 5/5 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1; new source explicitly included |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "settings: invalid numeric values"` | 1/1 |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

**74 distinct targeted cases pass.** Coverage includes exact clean prompts and
attempt numbering, provider recovery without losing response slots, shared retry
budget across protocol rejections, reset only at a new step, bounded combined
20+2+1 requests, permanent failures, cancellation during backoff/final rejection/
late completion, refusal, trace policy, streaming, media lifetime and tool safety.

`git diff --check` and 43 relative Markdown links pass. An HTML parser verifies
the unchanged single input id/type/range and the new caption/tooltip; this is not
layout validation. Master plan, product-version properties and all tag refs have
the same SHA-256 as the baseline.

Full harness was not rerun: only model retry behavior and one settings caption
changed. The last full result remains 320/321 in Phase 1B (baseline R22), not green.
Node/browser/UI layout tests were not run; no JS or UI projection logic changed.
The harness uses a controller stub. Windows x64 + Office x64 + VS 2022 / VSTO /
COM / real WebView and live endpoint/timeout qualification: **not performed**.

## Legacy, risks and remaining work

- Old initial+N control flow and initial-only fallback are removed, without alias.
  `MaxAgentFormatRetries` remains a stable settings key, not a second retry path.
- V2 parser/status/history remain; `Failure.Cause` still adapts typed failures to
  the controller until Phase 3. No new compatibility adapter was introduced.
- R20 is resolved host-neutral. R24 media cost remains; R25 records potential
  duplicate billable generation and added latency after a lost provider response.
  Both need real endpoint qualification before Phase 12. R21/R22 remain open.
- Phase 2C: v3 parser/schema, explicit v2 adapter and canonical v3/cutover document.
  Phase 3 AgentKernel is not part of this commit.

Policy details: [ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md).
Product version remains `16.1.0-dev`; no tag, push or release preparation.
