# Phase 2C3B — Explicit prompt schema review

Baseline: `330aa7978dbc65a76089a8190c407536421e98ff`, clean `stabilization/16.1`.
This closes the host-neutral/JS part of R27 before the coordinated v3 switch.
Ten production files change. Product `16.1.0-dev`, assembly `16.0.4.0`, response
version 2 and prompt schema 11 remain unchanged. No tag, push or release script.

## Invariant and scope

On a schema mismatch, loading/normalizing settings preserves authored instructions
and does not approve the marker. Blank fields still select current defaults.
SettingsService stages saves on a clone and retains a stored mismatched marker on
ordinary saves, even when a caller supplies a fresh current marker. An explicit
request-local review advances it; failed saves leave the caller's marker untouched.

The existing typed saveSettings bridge carries reviewAgentPrompts (default false).
Library → Prompts → actions → **«Подтвердить проверку»** asks the user to confirm all
five conversation instructions and saves the current form. The existing reset-all
action clears drafts; a subsequent save/review selects defaults. Ordinary saves,
prompt tools and diagnostic saves do not opt in. There is no durable approval flag.
The UI now includes PlanSystemPrompt and preserves stored prompts if the editor is
unavailable; marker 0 is no longer changed to 1 by the form.

AppSettings owns the readiness guard. Controller turn entry invokes it before
prepareTurn, attachment analysis and context compaction; confirmation invokes it
before marking/removing pending state or executing the tool. The neutral loop
guards direct start and continuation before materialization. This is a visible
configuration error, not model format repair. Existing fixed endpoint probes remain
available and manual cancellation does not require prompt approval.

No Office tool, Resource Fabric, VBA, chat event/storage format, model wire or
AgentKernel changes. The UI change is limited to this prerequisite. Approval is a
user review of instructions, not an automatic semantic validator; the strict active
response parser still determines which model responses can be accepted.

## Files

| File | Change |
|---|---|
| `src/RNAssistant.Core/Models/AppSettings.cs` | Preserve text/marker; one readiness guard |
| `src/RNAssistant.Core/Storage/SettingsService.cs` | Explicit review on a clone; ordinary save cannot approve stored mismatch; remove duplicate Chat/Plan defaulting |
| `src/RNAssistant.Office/Contracts/BridgeDtos.Settings.cs` | Typed request-local review flag |
| `src/RNAssistant.Office/Controller/AssistantController.Settings.cs` | Forward explicit review to settings owner |
| `src/RNAssistant.Office/WebView/AssistantWebBridge.cs` | Dispatch typed review flag without relabeling settings |
| `src/RNAssistant.Office/Controller/AssistantController.ChatExecution.cs` | Guard before turn preparation and auxiliary model requests |
| `src/RNAssistant.Office/Controller/AssistantController.Agent.cs` | Guard before consuming pending confirmation |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Guard direct neutral entry and continuation |
| `web/js/app-settings.js` | Explicit confirmed review action; preserve all prompts and marker in form saves |
| `web/index.html` | Review action in existing prompt menu; settings asset cache key |
| `tests/RNAssistant.Harness/Program.ChatSettingsTests.cs` | Replace obsolete reset test; real load/save, failed review and explicit reset |
| `tests/RNAssistant.Harness/Program.AgentSafetyTests.cs` | Block all three modes/continuation before dispatch; reviewed v2 flow unchanged |
| `tests/RNAssistant.Harness/Program.ContextBridgeTests.cs` | Extend existing typed settings test with non-sticky review flag and Plan payload |
| `tests/RNAssistant.Harness/AssistantControllerBridgeStub.cs` | Capture explicit review for bridge tests |
| `tests/RNAssistant.Harness/Program.cs` | Replace reset registration; register two focused review cases |
| `tests/RNAssistant.Harness/RNAssistant.Harness.csproj` | Source-link real SettingsService; keep production DPAPI implementation excluded |
| `tests/RNAssistant.Harness/ProtectedSecretStoreHarnessStub.cs` | Fail fast on secret reads/writes; only absent fixture secrets supported |
| `tests/web/prompt-review.test.js` | Real form/actions with HTML-derived IDs and minimal DOM/transport substitutes |
| `tests/RNAssistant.Harness/README.md` | Current filters and explicit platform/test-double boundaries |
| `docs/conversation-protocol.md`, `docs/protocols/CONVERSATION_RESPONSE_V3.md`, `docs/architecture.md`, `docs/decisions/ADR-0002-model-protocol-boundary.md` | Replace reset instructions; document owner, action, guards and next cutover |
| `docs/stabilization/PROGRESS.md`, `BACKLOG.md`, `MIGRATION_MAP.md`, `RISK_REGISTER.md`, this file | Current/next context, cleanup, R27 status and verification evidence |

## Verification

Baseline: settings 2/2, typed settings bridge 1/1. The former reset characterization
was replaced before implementation: the new preservation test failed because
normalization changed marker 0 to 11. It passes after removing that destructive path.
The matrix also preserves text/whitespace through clone/JSON for missing, old,
current and future markers.

| Command | Result |
|---|---|
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "settings:"` | 4/4; C# 7.3 linked build |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "bridge: typed settings"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: prompt save preserves global model"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "protocol context:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent: confirmation replays one final result"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent: confirmed tool failure continues"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `node tests/web/prompt-review.test.js` | 5/5 |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

**22 distinct harness cases and 5 JS cases pass.** JS covers ordinary save, absent
editor, cancel/explicit review, failed save and reset-all; the server supplies the
reviewed marker rather than a UI version constant. Real settings persistence uses
disposable fixtures; the test-only DPAPI boundary throws before reading an existing
secret file or writing any secret. It never simulates encryption.

Windows x64 + Office + VS 2022, production controllers/WebView, DPAPI and live
provider qualification: **not performed**. Controller placement is source-reviewed,
not executed by the controller stub. The neutral tests do not prove the complete
controller/COM path. Full harness/UI builds were not run; the last full result
remains 320/321 from 1B with known baseline R22.

Repository audit: 28 changed files, including ten production files.
`git diff --check` passes and all 70 local Markdown links/anchors resolve. Current source/test
consumers no longer reference the obsolete reset test. Product metadata, tag refs
and the master plan match the baseline hashes; response/prompt constants also match
the baseline. The pre-stage index is empty. Controller guard ordering is checked
from source only, not counted as a runtime test.

## Cleanup and next context

Removed: mismatch reset/automatic approval branch, redundant Chat/Plan defaulting,
obsolete SettingsHardCutoverLegacyAgentPrompts test/registration, destructive UI
blank fallback and marker 0→1 fallback. Current tests replace the obsolete assertion;
canonical docs no longer instruct implicit reset. Historical reports remain evidence.
No production adapter, alias or second runtime was added. The DPAPI stub is a
test-only boundary, not a production fallback.

R27 is fixed in the verified host-neutral/JS paths; Windows qualification remains
open. Actual saved v2 prompts must still be checked with the v3 defaults at cutover.
R26 remains open: require complete accepted-run context and explicit old-chat
skip/reset before any controller analysis/compaction or model dispatch, then enforce
it on every v3 attempt. The active v2 parser/schema/DTO and typed-ID helper still
serve current runtime consumers; nearest removal gate is coordinated Phase 2C3C.

Next required context: [saved-prompt review](../protocols/CONVERSATION_RESPONSE_V3.md#saved-prompt-review-phase-2c3b),
[cutover gates](../protocols/CONVERSATION_RESPONSE_V3.md#remaining-cutover-gates),
[migration map](MIGRATION_MAP.md), R26/R27 in [risk register](RISK_REGISTER.md).
Recheck §14.3's change budget before the switch; do not start Phase 3 in that change.
