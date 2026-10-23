# Conversation v8: phase-2 kernel candidate

2026-10-23. Owner: Core AgentKernel / canonical event-store adapters. Branch:
`conversation-v8`, following phase-1 commit `38182e8e`. Main remains v7.

The kernel now accepts `execute`, bounded `reflection` and evidence-gated `finish`
without adding another loop. Two consecutive reflections are allowed. A third is
rejected with one bounded corrective feedback; another reflection ends the run.
The counter and feedback flag are carried by the existing run checkpoint and
restored with accepted history. A new user input or terminal tool result resets
the counter. Reflection does not dispatch a tool or become evidence.

Before accepting `finish`, the existing run store reads the complete validated
append-only session stream. The resolver checks current session/run/turn, exact
accepted call ID/name/step/model-attempt origin and one canonical
`tool.execution.finished` record per referenced call. Pending, naked, foreign and
invented references are refused. `results=[]` is valid only when this run accepted
no calls; after calls, at least one claim and terminal reference is required.
Failed, unknown and undispatched terminals may support honest claims; a
`satisfied` claim backed only by non-OK outcomes is rejected. Model summaries
remain claims, not verified effects. Existing unknown-effect inspection still
blocks finish and never replays a mutation.

Office and Workspace/CLI use the same kernel gate. Their accepted no-call
decisions and per-call execute records carry v8 reason, model-attempt origin and
runtime IDs. The Office result is accepted only after the execution-finished
fact is saved; Workspace now writes the same typed terminal activity. Visible
Office terminal text is a separate runtime projection excluded from the model
history; the exact accepted v8 body remains in the durable protocol record.
History decoding refuses old/unknown response versions rather than inferring a
v8 action. Existing `RunViewState.GoalVerification=not_assessed` continues to
separate a completed run from task success.

## Host-neutral checks

- `sh tests/RNAssistant.V8KernelSmoke/run.sh`: source-linked Core/Office/Workspace
  compilation plus 17 focused passing checks. The checks cover direct finish,
  current-run terminal/failed/unknown evidence, forged/pending/naked references,
  confirmation-to-terminal transition, rejection without repeat dispatch,
  reflection limits after serialized checkpoint/history restoration, and
  reflection → execute → terminal result → finish after restoration.
- `dotnet run --project tests/RNAssistant.ProtocolTests/RNAssistant.ProtocolTests.csproj -- --export <empty-dir>`:
  153 protocol cases / 2156 assertions still pass.
- The Core net48 project cannot compile on this macOS host because the .NET
  Framework 4.8 targeting pack is absent. The source-linked net8 build is not
  Windows/Office qualification.

## Remaining gates

The full Harness still contains v7 test call sites and does not compile against
the removed v7 API. Phase 3 must migrate prompts, compatibility probes, skill
instructions and integrated tests; phase 4 must cover real ChatStore replay,
compaction, confirmation, steer/cancel and UI outcomes. The focused runner
round-trips the checkpoint/history DTOs but does not claim a process-level
ChatStore restart. The phase-0 premature-finish HTTP reproducer still needs its
v8 regression form. Default enablement remains gated by the phase-5 real-model
comparison and separate Windows x64 + Office/WebView2 evidence.
