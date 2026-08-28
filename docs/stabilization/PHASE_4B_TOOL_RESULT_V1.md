# Phase 4B — Tool Result v1 cutover

Date: 2026-08-28. Baseline: `85cc3f4` (4A). Branch: `stab/4b-tool-result-v1`.
Status: complete host-neutral; Windows/Office qualification remains open. Phase 5 is not started.

## Scope and invariant

[Master Phase 4](STABILIZATION_MASTER_PLAN.md#phase-4--tool-contracts-и-toolruntime)
and [ADR-0003](../decisions/ADR-0003-tool-result-three-states.md#phase-4b-wire-gate)
require one coordinated writer/consumer switch. Splitting these files into separate
commits would leave incompatible prompts, result history, schema loading or native
transport. No general refactor, file moves, new tool, Office binding or Phase 5–9
feature is included.

- Core `ToolResultWire` is the only model result writer/reader: exactly
  `tool_call_id/name/status/message/data` plus optional exact `ResourceRef` entries;
  status is `ok/error/unknown`, code belongs in `data`. Strict JSON preserves literal
  strings and rejects old aliases/extra roots/duplicate keys and non-resource transport.
- Native results reach `ToolResultMaterialization` directly. The legacy domain
  boundary uses runtime outcome; Activity/manual UI projection is separate and never
  feeds back into the model writer. The recorded result/evidence remains immutable
  when media, conversion or bounded context preparation fails.
- Accepted call and result messages carry local result marker 1. All three roles
  use the same v1 envelope; full-history gate validates marker/role/identity/pairing
  across compaction. IDs are unique within a user run, not artificially across the
  whole chat. Typed confirmation/user-input pauses remain pauses. Old pending work
  may be cancelled without inventing an ID or upgrading old result history.
- Prompt projection no longer appends prose after current call/result JSON. Clone,
  replay and fork rebasing retain markers, runtime IDs, literal strings and exact
  resource revision; native raw result JSON is rebased into the new chat scope too.
- Prompt schema 14 preserves saved custom text/old markers until explicit review/reset.
  R31 built-in authoring guidance now assigns IDs to runtime and distinguishes tool
  success from verified effect. Conversation Response v4 and product version do not change.

## Verification

Environment: this Mac, .NET 8 host-neutral harness/MockDemo with C# 7.3; no Office/VSTO.
**127 distinct harness cases passed.** Filter counts overlap; they are not additive.
No full harness or versioning suite was needed for this bounded wire cutover.

The build/run command is:

```sh
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "tool result wire:"
```

After compilation, each filter below uses:

```sh
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"
```

| Filter | Evidence |
|---|---|
| `tool result wire:` | 8/8 — three states, JSON strictness, literals, exact resources, no inferred controls |
| `tool result materialization:` | 5/5 — legacy terminal mapping, pause rejection, code/data preservation, UI isolation, actual-run conversion fault with known ok/error/unknown evidence |
| `tool runtime:` | 14/14 — existing typed runtime plus manual/model native read |
| `kernel replay:` | 10/10 — real-store evidence, pending/stale/cancelled continuation, append/recovery and known effect before optional projection |
| `protocol context:` | 6/6 — full-run/confirmation scope and safe batching |
| `agent:` | 36/36 — progressive tools, three-role history, known/unknown effects, confirmation, media, bounds, complete HTML and runtime IDs |
| `model protocol:` / `model compatibility:` | 15/15 and 2/2 — shared probes/roles, retry/fallback and preparation gates |
| `preflight` | 3/3 — full incompatible history including old result/pending markers, before dispatch |
| `settings:` / `bridge: typed settings` / `chat: prompt save` | 5/5, 1/1, 1/1 — R31/schema14, preservation/review/reset, stub bridge forwarding |
| `context inspector:` / `context: clone preserves values` | 3/3, 1/1 — projected history and three-role resource fork/clone |
| `resources:` | 8/8 — exact URI/revision and gateway constraints |
| `chat: uses only read-only resource loop` / `chat: rereads referenced artifact` | 1/1 each — Chat runtime and later explicit read after evidence leaves context |
| `plan mode:` / `context: compaction` | 2/2 each — local questions/pause, plan tools and preserved protocol pairs |
| `chat sessions: saved run boundary` / `chat: tool deletion` | 1/1 each — stored v1 marker/body and metadata-only exchange selection |
| `tools: discovery is complete and exact` / `harness: production projects` | 1/1 each — discovery evidence and old-style source includes |

Initial integration checks found one fixture variable-shadowing compile error and
three fixture problems: an outdated prompt substring, a projected pair without the
required RunId, and a positive resource-read scenario still emitting v3 model IDs.
Only these fixtures were corrected; production/parser acceptance was not weakened.
Their reruns used normal `dotnet run` when compilation was needed, followed by:
`agent: default prompts`, `agent: supports selectable tool result roles`, and
`chat: rereads referenced artifact`. All passed. Final review also added explicit
JSON-null token handling and coverage at generic result externalization. After that
source change, materialization, `agent:` (36/36), `kernel replay:`, `plan mode:` and
both Chat read filters were run again, and MockDemo was rebuilt. Earlier passing
cases were reused only where their source/test/helper/dependency inputs were unchanged.

```sh
dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj --nologo -v:minimal
```

MockDemo: **0 errors**, 3 existing `ModelAttachmentService` CA1416 warnings. This
compiles the actual controller, including cancellation; the harness controller is
a stub. This is not runtime validation of Office, WebView2 or controller cancellation.

Pre-commit `ValidateVersionFormat` passed; `git diff --check` and all 114 local
links in 13 changed Markdown files passed. Product-version properties and tag refs
match the baseline exactly. No release preparation or full versioning suite ran.

## Cleanup and remaining gates

Removed `NativeToolRuntimeAdapter.ProjectLegacy`, the legacy model writer/error
object/resource-kind envelope, permissive schema result reader, duplicated probe
JSON, and body-ID fallback in history edit selection. No legacy/v1 dual writer or
old-chat migration/reset automation remains. Existing data is not deleted.

[Migration map](MIGRATION_MAP.md) records active consumers/owners/removal gates:
`LegacyToolDefinitionAdapter` (catalog/handler switches, Phase 8/6–7/11),
`LegacyToolOutcomeAdapter` and `LegacyToolResultAdapter` (domain handlers 6–7/11),
`ToolResultUiProjection` (Activity Phase 9; manual/domain consumers 6–7/11).
VBA preparation/guard/journal, Office binding and LRU/resource lifecycle remain with
their current owners; the v1 wire does not qualify domain effects.

R31 is fixed host-neutral. R30's Phase 4 resource transport gate passes; Phase 8
lifecycle remains open. R28 streaming, R29 original incident/live-provider evidence,
R23 domain verification and Windows x64 + Office + VS 2022 remain open. R32 diagnostics
requirements are maintained separately, not integrated or activated in this commit.

Next: a separate Phase 5 bound DocumentSession/HostRuntime change. Required context
is linked in [PROGRESS](PROGRESS.md). Development target remains `16.1.0-dev`;
this is not a release and creates no tag or push.
