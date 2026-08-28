# Phase 2C3A — One active wire owner for runtime and probes

Baseline: `c9f8b076579aabbe4d33b55cf2455b672c52e37d`, clean `stabilization/16.1`.
This is the bounded preparation for 2C3B, not the live v3 switch. Seven production
files change; product `16.1.0-dev`, assembly `16.0.4.0`, response version 2 and prompt
schema version 11 remain unchanged. No tag, push, release script or Phase 3 work.

## Invariant and cleanup

The next coordinated switch still exceeded §14.3's production-file budget because
schema selection, validation, JSON envelopes and probe instructions repeated the
contract. Per §15.2, ModelProtocolWire now gives those responsibilities one permanent
Core owner. Its callers no longer need independent protocol changes. It receives
only contract inputs, not Office/session callbacks or mutable controller state.

ModelProtocolClient uses shared validation; the loop adds reasoning/cache/trace
fields to fresh shared response options. AgentJsonProtocol uses the shared JSON
writer and retains native mapping/history metadata. Probes derive fixed sentinels
from the same writer, compare validated responses, and use actual transcript call
messages, including canonical native metadata. Their raw-attempt policy is unchanged:
one request per check, no conversation repair/provider retry/schema fallback.

Removed now: private AgentOptions, duplicate response-schema selection in the loop,
manual probe call envelopes/native call construction, local status/field comparison
branches and the duplicated transcript JSON writer. Prompt-authoring guidance reads
the active defaults instead of copying a second version-specific contract.
ModelProtocolWire is not a compatibility adapter, conditional dialect or second loop.

Current v2 parser/schema/DTO, repair instructions and typed-ID helper remain necessary
for active consumers. Owners and nearest 2C3B removal gates are in MIGRATION_MAP.
Tool-result `status` is a different contract and is unchanged. No Office tool,
Resource Fabric, VBA, storage format, UI or saved settings behavior was changed.

## Files

| File | Change |
|---|---|
| `src/RNAssistant.Core/ModelProtocol/ModelProtocolWire.cs` | One active schema/JSON/parser owner; explicit context supplied for future v3 enforcement |
| `src/RNAssistant.Core/RNAssistant.Core.csproj` | Include the new source in the old-style project |
| `src/RNAssistant.Core/ModelProtocol/ModelProtocolClient.cs` | Shared validation; remove private parser instance |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Shared response options; retain Office reasoning/cache/trace projection |
| `src/RNAssistant.Office/Services/AgentJsonProtocol.cs` | Shared canonical JSON-call writer; keep native/history mapping |
| `src/RNAssistant.Office/Services/ModelCompatibilityService.cs` | Shared schema/validation/writer/transcript; delete duplicated builders |
| `src/RNAssistant.Office/Services/BuiltInSkillProvider.cs` | Prompt-authoring guidance follows active defaults without copying v2 field rules |
| `tests/RNAssistant.Harness/Program.AgentSafetyTests.cs` | Extend two existing compatibility tests; no new test file or redundant suite |
| `tests/RNAssistant.Harness/README.md` | Focused filters and verification boundaries |
| `docs/protocols/CONVERSATION_RESPONSE_V3.md`, `docs/conversation-protocol.md`, `docs/architecture.md`, `docs/decisions/ADR-0002-model-protocol-boundary.md` | Shared ownership, bounded preparation and remaining switch gates |
| `docs/stabilization/PROGRESS.md`, `BACKLOG.md`, `MIGRATION_MAP.md`, `RISK_REGISTER.md`, this file | Current/next context, cleanup, exact consumers and R27 |

## Verification

Baseline compatibility: 2/2. The extended two cases cover both formats × three
result roles, exact v2 request text, native IDs/canonical metadata, isolated options,
wrong sentinel/status/casing and exactly three raw calls even with retries/fallback
enabled in settings. Existing runtime tests check the shared options/writer/parser.

| Command | Result |
|---|---|
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model compatibility:"` | 2/2; C# 7.3 linked build |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | 13/13 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "protocol context:"` | 6/6 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation:"` | 4/4 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"` | 5/5 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "plan mode:"` | 2/2 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "chat: uses only read-only resource loop"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "settings: hard cutover legacy Agent prompts"` | 1/1; characterizes existing R27, not a fix |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

**76 distinct targeted cases pass.** Fake endpoints/Office and disposable fixtures
only. Windows x64 + Office x64 + VS 2022, VSTO, COM, production controller/WebView and
live provider qualification: not performed. Full harness and UI builds were not
run; the last full result remains 320/321 from 1B with known baseline R22.

Repository checks: `git diff --check` passes; all 65 local Markdown links/anchors in
changed docs resolve. Targeted source/test/include search finds no AgentOptions,
ConversationResponseV2Adapter or legacyV2 references. ModelProtocolWire is the only
production parser/schema caller and has exactly one explicit project include.
Before commit, the 18-file scope contains seven production files; the index is
empty and product-version, tag-ref and master-plan hashes match the baseline.

## Remaining gates and required context

R26: shared validation still uses v2 and does not enforce CallContext. 2C3B must
require completeness before any dispatch/materialization that can call a model,
switch the parser/schema/writer and native refusal handling coherently, and remove
superseded consumers. Incompatible chats need explicit skip/reset, not truncation.

R27: code reading and the existing test confirm NormalizeAgentPrompts overwrites
custom instructions on schema-version mismatch. This behavior predates 2C3A.
Before a v3 prompt-version bump, implement explicit review/reset handling and
preservation tests; do not silently overwrite user prompts. No settings were reset
or migrated by this change. Saved-prompt handling may affect the next change budget;
recheck it rather than bypassing §14.3.

Next required context: canonical [active wire owner](../protocols/CONVERSATION_RESPONSE_V3.md#active-wire-owner-phase-2c3a),
[remaining cutover gates](../protocols/CONVERSATION_RESPONSE_V3.md#remaining-cutover-gates),
[migration map](MIGRATION_MAP.md) and R26/R27 in [risk register](RISK_REGISTER.md).
Historical phase reports are optional evidence, not mandatory rereading.
