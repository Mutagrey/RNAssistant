# Phase 2C1 — Introduce v3 contract and explicit v2 read adapter

Baseline: `a51bdda327aae2067c111342808b6aacc1199019`, clean `stabilization/16.1`.
This is **introduce/read-adapt**, not a runtime cutover. Full coordinated switching
touches more than ten production files, so master plan §14.3 requires splitting.
Only five new Core files and their old-style project includes change production;
all active v2 model/loop/tool/history/UI paths are untouched. No Phase 3 work.

## Delivered invariant

The new Core contract has only message and ordered calls. Its explicit canonical
writer emits only v3 root fields. The v3 parser does not accept model status or
silently fall back to v2. It rejects malformed/non-JSON envelopes, duplicate
properties/call IDs, unloaded/unknown tool names, invalid native argument schemas
and unsafe batches before accepting any calls.

The caller supplies all accepted IDs in the run and a trusted batch-safe read-only
set. Parsing never updates those sets or reserves IDs from a rejected attempt.
Mutation/local/confirmation flags force singleton even if incorrectly listed as
batch-safe; absence from the trusted set also forces singleton. External and
unresolved effects must be excluded by execution authority. This API contract is
tested; **runtime production seeding/classification is not wired** (R26).

The explicit v2 read adapter discards status instead of converting it into runtime
truth. It preserves historical exact tool names/arguments without requiring a
current catalog and without making those tools executable. Its only current
consumer is the harness. It does not migrate history or write accepted events.

## Changed files

| File | Change |
|---|---|
| `src/RNAssistant.Core/ModelProtocol/ConversationResponse.cs` | Status-free DTO/result and canonical v3 envelope writer; reuse existing call records |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseJson.cs` | Shared strict structural reader for v3 and explicit v2 input; no implicit detection |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseParser.cs` | Callable/schema, accepted-run ID and singleton validation; no execution/history mutation |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseSchemaBuilder.cs` | Strict v3 schema from exact callable set, optional-null conversion, bounded calls |
| `src/RNAssistant.Core/ModelProtocol/ConversationResponseV2Adapter.cs` | Explicit historical-envelope read; discard legacy status |
| `src/RNAssistant.Core/RNAssistant.Core.csproj` | All five new sources explicitly included |
| `tests/RNAssistant.Harness/Program.SimpleAgentTests.cs` | Extend existing parser fixtures with v3/adapter negative and positive matrices |
| `tests/RNAssistant.Harness/Program.AgentSafetyTests.cs` | V3 schema/parser/transport and callable/limit coverage |
| `tests/RNAssistant.Harness/Program.cs` | Register 13 focused v3 tests |
| `tests/RNAssistant.Harness/README.md` | Targeted filter and explicit non-cutover coverage boundary |
| `docs/protocols/CONVERSATION_RESPONSE_V3.md` | Canonical v3 spec, parser inputs, read-adapter limits and switch gates |
| `docs/decisions/ADR-0002-model-protocol-boundary.md` | Record staged v3 decision, adapter owner/consumers/removal |
| `docs/conversation-protocol.md`, `docs/architecture.md` | Distinguish introduced v3 components from active v2 runtime |
| `docs/stabilization/PROGRESS.md`, `BACKLOG.md`, `MIGRATION_MAP.md`, `RISK_REGISTER.md` | Current stage, pending coordinated switch, legacy ownership and R26 |
| `docs/stabilization/PHASE_2C1_V3_CONTRACT.md` | This evidence and limitations |

AGENTS/README v2 runtime rules remain accurate and unchanged. No user-visible
runtime behavior changed, so the internal work is tracked here, not advertised
as a released v3 feature in CHANGELOG.

## Verification

Baseline `model protocol:` was 13/13 and `agent:` 41/41. The first 13 new v3 tests
passed. Adding an oversized-integer case then reproduced an uncaught
`InvalidCastException` in the new structural reader: the focused malformed-JSON
test failed. Extending only that reader's normalization failure mapping made the
case green; no existing v2 parser or shared tool normalizer was changed.

| Command | Result |
|---|---|
| `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation v3:"` | 13/13; C# 7.3 linked build |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "model protocol:"` | 13/13 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` | 41/41 |
| `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects"` | 1/1 |
| `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` | pass |

**68 distinct targeted cases pass.** V3 fixtures cover strict root/call fields,
JSON extensions/duplicates/depth/numbers, date-shaped strings, status-free round
trips, exact callable schemas, required/type/constraint violations, optional nulls,
accepted-run ID reuse and rejected-attempt isolation, singleton effect categories
in either batch position, ordered read-only calls, 32/33 bounds, every legacy
status, removed historical tool names, no v2 auto-fallback and actual schema
placement in the existing LLM request-body builder. The schema is inspected and
locally validated; no live provider accepted it during this turn.

`git diff --check`, 48 relative links (including referenced heading anchors) and
the 6-production-file scope audit pass. The master plan, `Directory.Build.props` and all tag refs retain their
baseline SHA-256 values. Active v2 production sources are byte-for-byte unchanged.

No full harness was needed for an isolated, not-yet-wired Core contract. The last
full result remains Phase 1B **320/321**, with baseline R22; this is not full-suite
green. No Node/browser/UI tests, Office builds or live endpoint requests were run.
Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView: **not performed**.
The harness uses the existing controller stub and does not prove production
controller delivery, history cutover or Windows qualification.

## Legacy and remaining gates

- Live v2 parser/schema/AgentResponse DTO, prompts, repair, probes, transcript and
  accepted history remain in use. `AgentResponseProtocol.CurrentVersion` is still 2.
  Remove superseded live v2 paths at the coordinated Phase 2C2 switch, not by adding
  aliases or a second runtime choice. There is no dual-write or feature flag.
- V2 read adapter owner: ModelProtocol; consumers now: focused harness; intended:
  accepted-history projection Phase 2C2. Removal phase: 10, after explicit removal
  of legacy consumers. It currently supports JSON envelopes, not all historical
  native-tool/final-text record forms.
- R26 gates run-ID seeding across confirmation/compaction and effective nested/
  external batch safety. Saved custom v2 instructions and every history form also
  need explicit cutover handling. New accepted events must be v3-only **after**
  that switch. Phase 2 is not complete; Phase 3 has not started.
- R21/R22 and Windows/release qualification remain open; no tool, Resource URI,
  VBA journal, persistence format or UI behavior was changed here.

Protocol and gates: [CONVERSATION_RESPONSE_V3.md](../protocols/CONVERSATION_RESPONSE_V3.md).
Product remains `16.1.0-dev`; assembly remains `16.0.4.0`. No tag, push or release
preparation is part of this work.
