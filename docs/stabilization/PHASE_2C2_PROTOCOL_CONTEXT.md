# Phase 2C2 — Adapt accepted-run validation context

Baseline: `5a6b550f6cd1f9226f3f153bde61d9270287c9c0`, initially clean
`stabilization/16.1`. This commit completes the context adaptation and includes
parallel governance edits with the user's explicit approval. No Phase 3 work,
version change, tag, push or release script.

## Invariant and scope

Each logical model request receives detached accepted-call IDs for the entire
user turn and a conservative batch-safe projection. Rejected raw attempts cannot
reserve IDs; the entire accepted response is observed before tool execution.
Confirmation reconstructs the full latest user turn across compaction and a new
runtime RunId. Missing/ambiguous history stays an explicit incomplete context,
not a valid empty or partial set. The v3 parser's context overload rejects it.

This is **adapt, not wire cutover**: active requests, schema, repair and accepted
writes still use v2. The v2 client does not enforce the new context. Phase 2C3 must
require completeness before dispatch and use it in every v3 parse, along with
coordinated prompts/schema/history/version switching and deletion of live v2 paths.

The nearest change simplified is that client/writer switch: Core no longer needs
to discover Office session/user-turn/compaction boundaries or pipeline safety.
One transient Office context owner supplies a small Core snapshot contract; no
durable index, new loop, planner or shared session state crosses that boundary.
Nine production files change, counting two project includes and the deleted
adapter. Tool definitions/executors, Resource Fabric, VBA, UI and persistence
formats are unchanged. No general monolith split is performed.

## History and safety decisions

- `ConversationResponseHistoryReader` reads only explicitly marked current v3
  assistant records: canonical envelopes, one native call with exact canonical
  metadata, or literal final text. Unknown versions, ambiguous native batches,
  inconsistent metadata and JSON injection fail. It never rewrites history or
  grants execution authority. Tests create v3 fixtures explicitly; the production
  writer is still v2 and is not claimed to have switched.
- Current v2 confirmation needs only existing typed IDs in its transcript. Its
  temporary `ReadCurrentV2CallIds` consumer does not parse v2 JSON/status or preserve
  old-chat compatibility. Delete it at the v3 writer/version switch. The existing
  controller protocol-version gate must keep old pending runs out of continuation;
  fresh incompatible chats still need an explicit skip/reset guard before cutover.
- The unused historical v2 read adapter had only harness consumers. It, its
  `legacyV2` structural branch, include and obsolete tests were removed now.
  Historical evidence/ADR text is retained as history, not an active requirement.
- Legacy flags cannot establish absence of external effects. The context permits
  only listed built-in local reads plus safe effective metadata; all pipelines
  and unknown/external IDs remain singleton. This projection does not change the
  executor's policy. Rebuild it for every run/confirmation. Its owner/removal gate
  is Runtime/ToolRuntime, Phase 4 typed external/nested metadata and equivalent
  safety tests. Accepted-ID bookkeeping moves to AgentKernel in Phase 3.

## Changed files

| Files | Reason |
|---|---|
| `src/RNAssistant.Core/ModelProtocol/ModelProtocolContracts.cs` | Detached complete/incomplete call context on model requests |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseParser.cs` | Require complete context in the new v3 overload |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseHistoryReader.cs` | Current-v3 accepted history projection without authority/rewrite |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseJson.cs` | Remove unused legacyV2 parsing branch |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseV2Adapter.cs` | Delete unused historical-format adapter |
| `src/RNAssistant.Core/RNAssistant.Core.csproj` | Remove old include; include current history reader |
| `src/RNAssistant.Office/Services/ConversationProtocolContext.cs` | Full-turn accepted IDs and conservative effective safety |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Supply snapshots and observe acceptance; internal factory seam for real-boundary tests |
| `src/RNAssistant.Office/RNAssistant.Office.csproj` | Include context owner in old-style project |
| `tests/RNAssistant.Harness/Program.SimpleAgentTests.cs` | Current-v3 history cases replace obsolete v2-adapter tests |
| `tests/RNAssistant.Harness/Program.AgentSafetyTests.cs`, `Program.cs` | Six focused context cases, including two real loop/executor integrations |
| `tests/RNAssistant.Harness/README.md` | Current filters and explicit verification boundaries |
| `docs/protocols/CONVERSATION_RESPONSE_V3.md`, `docs/decisions/ADR-0002-model-protocol-boundary.md` | Current contracts, cleanup, responsibilities and next switch gates |
| `docs/architecture.md`, `docs/conversation-protocol.md` | Distinguish live v2 behavior from context adaptation |
| `docs/stabilization/PROGRESS.md`, `MIGRATION_MAP.md`, `BACKLOG.md`, `RISK_REGISTER.md` | Current/next task, concrete consumers, removal gates and R26 status |
| `AGENTS.md`, `docs/stabilization/STABILIZATION_MASTER_PLAN.md` | Include authorized parallel policies; remove the obsolete v2-adapter recommendation from master plan §21 |
| `docs/stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md` | This evidence |

Parallel edits appeared in `AGENTS.md`, the master plan, PROGRESS, MIGRATION_MAP,
ADR-0002 and the v3 canonical doc after work began. They were preserved and govern
the cleanup/compatibility policy. The user explicitly authorized their inclusion
in this commit; stage documentation is layered on top. Their policy changes are
preserved; local cleanup also aligns the master plan's §21 commit example with §7.1.

## Verification

Baseline v3 was 13/13 (including the now-obsolete adapter tests), Agent 41/41.
Current v3 replaces two obsolete cases with two current-history matrices; six
new context cases use existing fixtures, not a new test file. The positive context
overload, invalid history and accepted-only reconstruction are covered.

| Command | Result |
|---|---|
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation v3:"` | 13/13; linked C# 7.3 build |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "protocol context:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | 13/13 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"` | 5/5 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1 |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

`git diff --check` passes. All 63 local links in the 12 changed Markdown files
resolve, including anchors. Source/test search finds no remaining deleted-adapter
or `legacyV2` references; the production project include check passes. Pre-commit
Directory.Build.props and tag-ref hashes match the initial baseline. AGENTS retains
the authorized parallel edits; the master plan also includes the §21 cleanup above.
Approval and cleanup finalization changed documentation only; the tested code is
unchanged. Behavior slices were not rerun; the existing project-include case was
rerun without rebuilding, alongside version-format and docs checks.

The requested §15.1 closure audit found no further dead production path in this
scope. Live v2 parser/schema callers are ModelProtocolClient,
ModelCompatibilityService and ConversationRunService; current transcript writers
and the typed-ID helper remain necessary until 2C3. The migration map records their
owners/reasons/gates with exact consumer names. Obsolete instructions were removed,
the PROGRESS introduction shortened, and historical evidence/ADRs retained.

**86 distinct targeted cases pass.** The two boundary integrations use real
ConversationRunService, ModelProtocolClient and OfficeToolExecutor with fake LLM
and Office. The confirmation case writes only the fixture's disposable skill
store and uses actual compaction; it simulates the controller's TurnId/RunId
transition, not production controller execution. Rejected raw IDs do not enter
later snapshots, and a new user run starts empty.

No full harness, Node/UI, VSTO/Office build or live endpoint run was necessary for
this adaptation. Last full suite remains the Phase 1B result 320/321, with known
baseline R22; it was not rerun or fixed here. Windows x64 + Office x64 + VS 2022,
COM, production controller and real WebView qualification: **not performed**.

## Remaining gates and next context

R26 is contained at context wiring, not closed for active v3 enforcement. The
conservative positive registry intentionally keeps some harmless tools singleton;
do not broaden it based only on false legacy flags. No new issue outside this
contour was fixed. R16/R21 Windows and R22 baseline qualification remain open.

The separate Phase 2C3 needs only the canonical v3 doc's
[context](../protocols/CONVERSATION_RESPONSE_V3.md#accepted-context-and-current-v3-history-phase-2c2)
and [remaining gates](../protocols/CONVERSATION_RESPONSE_V3.md#remaining-cutover-gates),
the active [model boundary](../conversation-protocol.md#modelprotocol-boundary-phase-2),
and [migration map](MIGRATION_MAP.md). Historical reports/ADRs are evidence to
consult as needed. Saved custom prompts and BuiltInSkillProvider's instruction
authoring guidance, compatibility probes, v3 writes/version marker, complete
context enforcement and explicit incompatible-chat guard must be coordinated;
delete replaced consumers in that switch. Phase 3 is separate.

Product version remains `16.1.0-dev`; AssemblyVersion remains `16.0.4.0`.
No product bump, Git tag, push or release script invocation.
