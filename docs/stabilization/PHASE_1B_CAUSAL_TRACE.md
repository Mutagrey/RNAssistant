# Phase 1B — Causal trace

Baseline: `a24feb1` (Phase 1A), branch `stabilization/16.1`.
Scope: observability only. Phase 1C completion guard and subsequent phases are not included.

## Correlation

| ID from master plan | Source / link |
|---|---|
| sessionId | Existing `ChatSession.Id` / `SessionEvent.SessionId` |
| runId | Snapshot of `LastRun.RunId` at scope/request configuration |
| turnId | Existing `LastRun.TurnId`; stable across confirmation runs |
| stepId | One GUID per conversation iteration, allocated before the first model request; retained during format repair and schema fallback, then copied to `ToolCommand.RuntimeStepId` and VBA journal |
| modelAttemptId | One GUID per completion call, including repair/fallback; not a retry counter |
| toolCallId | Accepted v2 call `id`; `model.response.accepted.ToolCallIds` links the exact accepted attempt to execution |
| documentRuntimeId | Pinned `LastRun.DocumentRuntimeKey`; domain records use the journal's observed `RuntimeDocumentKey`. Chat-only or unavailable identity stays null; no identity is invented |
| mutationId | Existing VBA module/package journal preparation id |

`RequestId` remains the transport correlation id. Existing `llm.*` and `step.*`
events retain their transport `SessionEvent.StepId`, so recovery/trajectory semantics
do not change. **Logical step is `Data.StepId` on model events**, and the envelope
`StepId` on tool/domain events. All parser verdicts now retain `RequestId` and
`ModelAttemptId`; rejected attempts no longer lose their request link.
Helper requests (title, compaction, media) reuse their existing transport request id
as logical step/attempt when no conversation iteration exists. HTTP retries inside
one completion call remain transport diagnostics, not additional protocol attempts.

On confirmation, execution has a new run id but retains the logical turn and call.
`JournalRunId` preserves the preparation guard's original run independently of the
current execution run. A join must not assume these two run ids are always equal.

## Stages and evidence

All observations use the existing per-chat append-only event stream. There is no
second store, mutable trace snapshot, execution state machine or UI decision path.

| Stage | Recorded at / meaning |
|---|---|
| `run.started` | Controller, after initial run persistence; also for confirmation continuation |
| `model.request.prepared` | `llm.request.Data.Stage`, after exact request materialization and before HTTP dispatch; existing payload/CAS path unchanged |
| `model.attempt.rejected` | `agent.response.rejected.Data.Stage`, after parser rejection; diagnostic payload remains outside accepted history |
| `model.response.accepted` | New metadata-only event after valid parser result or explicit provider refusal; declared status and call ids, no duplicate response body |
| `tool.execution.started` | Entry to the top-level `OfficeToolExecutor.Execute` boundary; does not assert that policy allowed dispatch |
| `tool.execution.completed` | Existing executor result/status/code, or cancellation/exception boundary; does not convert tool outcome |
| `domain.effect.prepared` | Entry to the existing journalled VBA action, with an already persisted module/package preparation id |
| `domain.effect.dispatched` | Immediately before invoking that domain action; not a COM acknowledgement or proof of a write |
| `domain.effect.verified` | Existing domain assessment (`committed`, `not_applied`, `rolled_back`, `unknown`), before terminal journal append. It does not assert that terminal persistence succeeded |
| `run.summary.created` | Controller after saving the existing LastRun result; `Boundary=legacy_run_record`. This is not the future runtime-owned RunSummary |
| `ui.projected` | After constructing `SendChatResponse` / confirmation `ChatStateResponse`; **not WebView delivery/render acknowledgement** |

Domain effect stages cover the existing journalled VBA module, rename and package
wrappers. Plain Excel writes/macros have tool boundaries, not invented verification
or mutation ids. Further domain coverage belongs to Phases 4–7/11.
Existing session operations (`run.*`, tool activities) and VBA journal remain
unchanged; diagnostic metadata does not become a competing execution authority.

## Failure, privacy and concurrency

`RunCausalTrace` is a small `AsyncLocal` logging scope bound to the actual controller
run. It captures correlation, restores the previous scope on disposal and rejects
writes from inherited background work after disposal. It never selects tools,
targets, retries, status, recovery or UI text.

New metadata observations are best effort: append failure logs only a fixed stage
message, and cannot change a tool result or mask an execution exception. The existing
mandatory request trace still must persist before HTTP dispatch; existing rejected
response diagnostics keep their original failure behavior. The projection marker is
written after lease release; a concurrent writer can win CAS and cause that optional
marker to be omitted. Missing markers are missing evidence, never proof of success.

No prompts, arguments, source, model text, reasoning, paths, exception text or auth
headers are added to the new metadata events. Existing request/response/rejection
payloads retain their original protection and CAS handling. Trace fields are ignored
by model request option serialization and by chat-history replay.

## Verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` — 6/6 pass.
- Tests cover ok/error/real journal unknown; response 20 accepted after 19 rejections;
  request/verdict/step/call/mutation correlation; confirmation run/turn split;
  concurrent async scopes and late children; optional observer failure without retry.
- Real `ConversationRunService`, executor, local event store and VBA journal run with
  fake LLM/Office. Fake transport records in the correlation test are labelled as
  such; actual HTTP preparation ordering is covered by the existing
  `ModelRequestTracePrecedesDispatch` test.
- Scope/summary/projection marker tests exercise the writer, **not production
  controller wiring**. Harness uses `AssistantControllerBridgeStub`; production
  controller call sites are reviewed statically. No browser acknowledgement exists.
- Final full host-neutral harness (`dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj`): **320/321 pass**. The sole failure is `tools: compact catalog rejects removed aliases` (expected 16 Excel tools, got 15), reproduced with the same targeted command at baseline `a24feb1` in a disposable detached worktree. It is tracked as R22, not fixed in this observability change. The full suite is **not green**; all six trace and seven characterization tests pass.
- `ValidateVersionFormat`, `git diff --check` and relative Markdown links: pass.
- Windows x64 + Office x64 + VS 2022 / COM / VSTO / real WebView: **not performed**.

## Remaining boundaries

R01 (false completion) remains open: error/unknown still coexist with model
`completed`. Phase 1C must replace characterization with runtime-health safety gates.
R20 (initial + 20 retries = 21 requests) is unchanged and remains Phase 2 work.
R21 records optional trace completeness and unverified controller/Windows wiring.
R22 records the independently reproduced baseline tool-catalog test failure (Phase 8).
No compatibility adapter or new product/protocol version was introduced.
Product version remains `16.1.0-dev`; no tag is created.
