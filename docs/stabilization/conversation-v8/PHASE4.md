# Conversation v8: phase-4 persistence/UI slice

2026-10-23. Owner: Core chat format/run projection, Office transcript and
Workspace/CLI recovery. Branch: `conversation-v8`. This is a checked slice,
not completed phase-4 or release qualification.

New chats use stream format 8. Formats 6 and 7 remain readable, but run policy
refuses execution in them; no history is rewritten or deleted. The
[candidate wire contract](../../protocols/CONVERSATION_RESPONSE_V8.md) owns
the v8 envelope, terminal evidence and model-claim boundaries.

The Office run projection reconstructs accepted `finish.results` from the
identified v8 protocol record for the exact run and exposes assessment,
summary and evidence IDs in typed `RunViewState.ModelResults`.
`GoalVerification` remains `not_assessed`. The UI shows partial, unverified
and unsatisfied claims as model assessments with the IDs; an ordinary clean
completed answer still has no service card. Accepted `reflection` also has
one visible saved step excluded from model context. Old
`model_done/model_blocked/model_needs_input` presentation paths are gone.

Workspace preserves a terminal honest failure: when explicit acceptance
criteria remain unmet, a finish containing an incomplete assessment can end
the run with `acceptance=failed` and CLI exit 5. A finish with no incomplete
assessment remains rejected with feedback. This preserves a way to stop at
an irreparable obstacle without reporting success or replaying a tool.

## Host-neutral checks

- `sh tests/RNAssistant.V8KernelSmoke/run.sh`: source-linked production build
  and 26 focused checks plus 57 migrated `AgentKernel` and 14 context-continuity
  Harness fixture cases,
  including format-7 read-only policy,
  finish-claim projection/serialization, stale-response discard on steer and
  closure of a steered read batch with exact terminal IDs. A controlled HTTP
  request through production `ConversationRunService` and fake Excel reproduces
  the phase-0 sequence in v8: schema read, one verified sheet mutation,
  rejected empty-results finish, then an honest partial finish. The rejected
  promise is absent from accepted history; replay preserves the partial claim
  and sheet evidence without presenting task verification.
  The kernel fixtures exercise v8 terminal citations, unknown-effect inspection,
  continuation, confirmation, cancellation and append failures. Confirmation
  retains the accepted run ID so finish can resolve its terminal evidence.
  Context fixtures preserve paired call/result bodies, bounded archived source
  selection and recovery facts; the controlled Office loop tests duplicate-read
  feedback followed by discovery, corrected read and evidence-citing finish.
  The fake-Excel unknown-effect case runs in both response modes and result roles:
  a clear with inconclusive read-back is not replayed, inspection clears the
  workflow guard, the remaining sheet write executes, and finish keeps the
  clear unverified while runtime health remains unknown.
- `node tests/web/run-view-state.test.js`: 7/7, including immutable model
  results, a visible partial outcome and no card for an ordinary answer.
- `dotnet build src/RNAssistant.Cli/RNAssistant.Cli.csproj --no-restore`:
  passes. Controlled CLI/HTTP smokes pass:
  `response_mode_smoke.py` (response modes, result roles, approval),
  `mode_questions_smoke.py` (Plan/Agent typed questions, answer/restart and
  old-run evidence isolation),
  `interrupted_run_smoke.py` (process death and source revalidation),
  `compaction_resume_smoke.py` (checkpoint, source drift, honest failure,
  native result pairing), and `run_control_smoke.py` (cancel/continue, exact
  cursor, lease, approved write with no replay).
- `python3 -B tests/cli/conversation_v8_finish_smoke.py`: controlled HTTP/CLI
  file analogue of the phase-0 failure. After one verified write, an empty
  `finish.results` receives `missing_finish_results`; the rejected proposal is
  absent from accepted model context. The run then performs a second write and
  finishes with both terminal IDs, without replaying the first write.
- The full host-neutral Harness now builds. Focused Harness runs pass for
  Plan questions after restart, optional schema admission, Markdown/HTML
  publication and replay, VBA source recovery, approval/unknown-effect
  accounting, and event-store replay. Scripted VBA and Excel-report workflows
  complete with runtime-assigned terminal IDs in their v8 finish claims.
  A tight-context VBA continuation refuses before model/tool dispatch when
  its complete causal context does not fit.
- The migrated `conversation v8:` parser/schema group passes 13/13 checks.
  The two `agent: no-tool` cases now cover rejected `continue`, typed question
  and post-answer continuation, plus empty-`execute` repair with evidence-citing
  finish. Related schema fallback, Task List and VBA patch fixtures pass.
- The `model protocol:` group passes 15/15 with v8 accepted responses and
  current repair guidance; retry, fallback and provider budgets retain their
  original assertions.
- The `protocol context:` group passes 6/6. Confirmation continuation keeps
  the exact run identity through the evidence-citing finish; accepted call IDs
  survive compaction without being regenerated.
- Prompt-schema migration and default-prompt checks pass with the shared v8
  action guidance; migrated settings complete direct Agent and Plan turns.
- Model compatibility probe fixtures pass in both response modes and all three
  result roles (2/2 cases): exact v8 sentinels qualify, while altered fields,
  model-owned IDs and loose responses remain rejected.
- The cancellation-after-unknown and three tool-result prompt-budget checks
  pass with v8 execute calls and a terminal-evidence finish; the budget checks
  still verify complete result delivery or refusal before model dispatch.
- The `kernel replay:` group passes 10/10, `kernel recovery:` 2/2, and the
  optional causal-trace failure check passes. Confirmation keeps the accepted
  run identity through the v8 finish; interrupted recovery retains a complete
  schema-read pair but excludes the unpaired write call from model context.
- Two `agent continuity:` workflow checks pass with v8 calls and cited finish
  evidence: an unfinished task list remains available to the next run, and a
  CSS edit closes the plan without inventing a redundant JS edit.
- The complete `agent continuity:` group passes 5/5 after updating the resource
  presentation fixture to the current `unavailableScopes` wire and runtime-owned
  media hydration evidence.
- The `conversation:` streaming group passes 4/4 with v8 finish envelopes:
  chunked escaped messages and provider reasoning remain separate, while a
  rejected finish with an extra field resets the visible stream and reasoning
  before its accepted repair.

The full Harness still has legacy v7 wire bodies and assertions outside these
migrated groups; its complete test suite is not yet green.
The original v7 red audit and its route have been retired; its captured evidence
remains historical. The full Harness suite and Office
steer/UI integration checks are not yet migrated. Playwright layout qualification is
unavailable in this worktree (`playwright` module absent); Windows x64 +
Office x64 + WebView2 and real-model comparisons remain open.
