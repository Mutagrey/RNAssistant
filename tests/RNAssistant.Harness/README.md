# RNAssistant Harness

Host-neutral tests run on this machine without Office COM. Locate the relevant test first; do not read or execute the full suite by default.

## Find a test

```bash
rg -n 'Test\(".*resource' tests/RNAssistant.Harness/Program.cs
rg -n 'TargetMethodOrBehavior' tests/RNAssistant.Harness
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- --list
```

## Run a focused slice

The trailing argument is a case-insensitive substring matched against category or test name:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "resources:"
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "bridge: typed resource"
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba: patch"
```

After an unchanged successful build, `--no-build` avoids recompilation:

```bash
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: CAS"
```

Filtering limits executed tests. The harness source-links Core and Office-neutral production files, so a normal run still compiles that full linked source set. Compilation does not require reading every test file into agent context.

Permanent test policy is defined in
[development rules §9](../../docs/development-rules.md#9-тестирование-по-риску);
the active stabilization application remains
[master plan §22.1](../../docs/stabilization/STABILIZATION_MASTER_PLAN.md#221-минимальная-достаточная-проверка).
Docs-only changes need diff/affected-link checks, not a harness build. A recorded
pass is reusable only when its relevant sources/tests, dependencies, build settings
and environment are unchanged. Explicit phase/release gates and the pre-commit
version check still apply.

## Test map

| Area | Main files | Useful filters |
| --- | --- | --- |
| Conversation and Agent | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ToolDiscoveryTests.cs` | `conversation:`, `agent:` |
| Tool Result v1 / strict JSON | `Program.ToolResultWireTests.cs`; projection checks in `Program.ToolRuntimeTests.cs` | `tool result wire:`, `tool result materialization:` |
| Native ToolRuntime / typed contracts and effect evidence | `Program.ToolRuntimeTests.cs`; native read in `Program.ResourceGatewayTests.cs` | `tool runtime:` |
| Immutable ToolPack authority / finite core / atomic callable admission | `Program.ToolDiscoveryTests.cs`; confirmation and policy regressions in `Program.SimpleAgentTests.cs` and `Program.AgentSafetyTests.cs` | `tool pack:`, `agent: confirmation`, `protocol context: batch safety uses local authority` |
| Typed Excel reads/writes/range/table/chart families / native, HTML, bounds and effect evidence | `Program.ExcelReadTests.cs`, `Program.ExcelWriteTests.cs`, `Program.ExcelRangeMutationTests.cs`, `Program.ExcelTableTests.cs`, `Program.ExcelChartTests.cs`; paired Agent regression in `Program.AgentSafetyTests.cs`; host access in `Program.ParserDesktopTests.cs` | `excel read:`, `excel write:`, `excel range mutation:`, `excel table:`, `excel chart:`, `protocol context: loop tracks only accepted calls`, `tools: html workspace updates session`, `host runtime:` |
| Host document gate / neutral bound session / direct context and catalog reads | `Program.ParserDesktopTests.cs`; live-read/guard integration in `Program.VbaPromptTests.cs` and `Program.ResourceGatewayTests.cs` | `host runtime:`, `vba: queued guard`, `waits for active mutation`, `vba: confirmed mutation`, `tool runtime: native resource list manual and model paths` |
| Excel identity owner/helper protocol (no Office execution) | `Program.ParserDesktopTests.cs`; source-linked `OfficeHosts.Qualification` | `excel identity probe:` |
| Qualification pack/catalog/runner/event/build authority | `Program.QualificationTests.cs`; strict manifest/coverage, fake action/verifier ports, pause/replay/fault barriers, real chat CAS and signed exact-build admission | `qualification:` |
| Artifact Library classes, exact heads/history, HTML branch selection, media gallery projection and chat resource cards | `Program.ArtifactLibraryTests.cs`; UI contracts in `tests/web/artifact-library-projection.test.js`, `tests/web/artifact-media-gallery.test.js` and `tests/web/chat-resource-card.test.js` | `artifact library:` |
| Exact bounded artifact text/source and Markdown viewer projection | `Program.ResourceGatewayTests.cs`, `Program.ContextBridgeTests.cs`; UI contracts in `tests/web/artifact-text-viewer.test.js` and `tests/web/artifact-json-viewer.test.js` | `artifact viewer:`, `bridge: typed artifact viewer`, `resources: gateway reads searches resolves and pages`, `resources: duplicate artifact ids fail closed`, `resources: empty text remains exact` |
| HTML whole-workspace revision lineage and branch recovery | `Program.HtmlArtifactStorageTests.cs`; replay/recovery in `Program.SessionEventStoreTests.cs` | `html lineage:`, `storage: html navigation`, `storage: html redo branches`, `storage: html recovery` |
| Inert uploaded-HTML preview/import and typed bridge payload | `Program.HtmlArtifactStorageTests.cs`, `Program.ContextBridgeTests.cs`; UI contract in `tests/web/html-upload-import.test.js` | `html import:`, `bridge: typed html import` |
| Exact HTML binding checkpoint/recovery/refresh/export and typed bridge payload | `Program.HtmlArtifactStorageTests.cs`, `Program.HtmlWorkspaceToolTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ContextBridgeTests.cs`; UI/export contracts in `tests/web/html-workspace-export.test.js` and `tests/web/html-workspace-echarts.test.js` | `html export:`, `html tools: native ownership and typed binding`, `tools: html workspace updates session`, `bridge: typed html export` |
| R61 HTML semantic schemas, accepted-read binding, automatic preflight and model-result/history isolation | `Program.HtmlWorkspaceToolTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ToolContractAuditTests.cs`; UI policy and standalone runtime checks in `tests/web/html-workspace-export.test.js` and `tests/web/html-workspace-echarts.test.js` | `html tools:`, `tools: html workspace updates session`, `tools: html source`, `tools: R61 built-in contract inventory` |
| R61 Prompt/Tool/Skill semantic authoring, installed-package review and replay isolation | `Program.ChatSettingsTests.cs`, `Program.ToolStoreTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ToolContractAuditTests.cs` | `tools: authoring intents are semantic`, `tools: validate payload without saving`, `chat: prompt save preserves global model`, `tools: agent CRUD preserves omitted fields`, `skills: CRUD preserves omitted fields`, `tools: R61 built-in contract inventory` |
| R61 VBA/macro semantic intents, runtime-owned patch/backup state and replay/result isolation | `Program.VbaPromptTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.ToolStoreTests.cs`, `Program.ToolContractAuditTests.cs` | `vba: semantic intent contracts isolate runtime state`, `vba:`, `agent: exposes safe VBA editing tools`, `resources: live Office and VBA are bounded and guarded`, `tools: VBA facade is common across hosts`, `tools: R61 built-in contract inventory` |
| R61 Tool Library UI-only documentation, typed Test controls and internal semantic continuation | `Program.ToolLibraryUxTests.cs`, `Program.ContextBridgeTests.cs`; browser contracts in `tests/web/tool-library-ux.test.js`, `tests/web/tools-contract.test.js` and `tests/web/tools-editor.test.js` | `tools: built-in documentation`, `tools: Library continuation`, `bridge: typed tools and skills` |
| Plan exact Markdown lineage, restore/removal and pinned-URI handoff | `Program.PlanModeTests.cs`; UI restore/preflight/handoff contract in `tests/web/plan-document.test.js` | `plan document:`, `plan mode:` |
| Pure AgentKernel / typed run evidence | `Program.AgentKernelTests.cs` | `kernel:` |
| Immutable run/UI projection and ordering | `Program.RunViewStateTests.cs`, replay/recovery in `Program.SessionEventStoreTests.cs`, boundary check in `Program.ProjectStructureTests.cs`; static UI in `tests/web/run-view-state.test.js` | `run view:`, `kernel replay:`, `kernel recovery:`, `architecture:` |
| Physical/layer dependency direction | `Program.ProjectStructureTests.cs`: Core.Agent, ModelProtocol, VBA, resources (including no legacy execution adapter in the resource catalog), OfficeHosts and UI boundaries, explicit VBA marker contract, root application façade, plus production source inclusion | `architecture:`, `harness: production projects include all source files` |
| Local native portable publishing | `Program.ProjectStructureTests.cs`; exact owned-destination cleanup and full current-file copy | `build: portable publish` |
| Office model-context owner | `Program.ToolDiscoveryTests.cs`; result/projection coverage in `Program.AgentSafetyTests.cs` | `agent: model session`, `agent: bounds oversized`, `context inspector:`, `protocol context:` |
| ModelProtocol boundary | `Program.AgentSafetyTests.cs`; media integration in `Program.ResourceGatewayTests.cs` | `model protocol:`, `agent: hydrates artifact media`, `causal trace:` |
| Active wire / compatibility probes | `Program.AgentSafetyTests.cs` | `model compatibility:`, `agent: supports selectable`, `model protocol:` |
| Prompt schema review / settings | `Program.ChatSettingsTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ContextBridgeTests.cs` | `settings:`, `bridge: typed settings`, `chat: prompt save` |
| Conversation v5 contract/context | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs` | `conversation v5:`, `protocol context:` |
| History/context preflight | `Program.AgentSafetyTests.cs` | `preflight`, `protocol context:`, `model protocol:` |
| Resources and attachments | `Program.ResourceFabricTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.AttachmentTests.cs`; UI pre-dispatch ordering in `tests/web/attachment-ingestion-order.test.js` | `resources:`, `attachments:` |
| Session storage and CAS | `Program.SessionEventStoreTests.cs`, `Program.CasMaintenanceTests.cs` | `storage:` |
| Chats, context and bridge | `Program.ChatSessionTests.cs`, `Program.ChatEditTests.cs`, `Program.ContextBridgeTests.cs`, `Program.PromptContextInspectorTests.cs` | `chat:`, `chat sessions:`, `context:`, `bridge:` |
| Tools and disabled pipelines | `Program.ToolStoreTests.cs`, `Program.PipelineToolTests.cs`, `Program.SearchToolTests.cs` | `tools:`, `pipeline:`, `search:` |
| Native Tool authoring and typed Tools UI boundary | `Program.ToolStoreTests.cs`, `Program.ContextBridgeTests.cs`, `Program.ProjectStructureTests.cs`; strict UI contracts in `tests/web/tools-contract.test.js`, `tests/web/tools-editor.test.js` and `tests/web/tool-package-actions.test.js` | `tools:`, `bridge: typed tools and skills`, `architecture:` |
| Native Skill authoring and typed Skills UI boundary | `Program.ToolStoreTests.cs`, `Program.ContextBridgeTests.cs`, `Program.ProjectStructureTests.cs`; strict UI contract in `tests/web/skills-contract.test.js` | `skills:`, `bridge: typed tools and skills`, `architecture:` |
| VBA reader, mutation/journal, pure patch and text canonicalization | `Program.VbaPromptTests.cs`, `Program.VbaToolPackageTests.cs`; catalog gate regression in `Program.ParserDesktopTests.cs` | `vba:`, `vba: mutation`, `vba: reader validates typed snapshots`, `host runtime: direct VBA catalog reads share access`, `vba: pure patch text contract`, `vba: live hash preserves line structure`, `vba: code hash normalizes export` |
| HTML, plans and charts | `Program.HtmlArtifactStorageTests.cs`, `Program.PlanToolTests.cs`, `Program.ChartArtifactTests.cs` | `artifacts:`, `plans:`, `chart:` |
| Desktop/WebView-neutral | `Program.ParserDesktopTests.cs`, `Program.WebViewSecurityTests.cs` | `desktop target:`, `webview:` |

The `harness:` slice also verifies that every production `.cs` file is explicitly included in its old-style `.csproj`, preventing source-linked harness globs from hiding a broken production project.

The bound-session fixtures test operation ownership, STA handoff/cancellation,
Save As, close/reopen rejection, gate cleanup and direct context/catalog root isolation
using supplied fake identities. Capture service tests do not run the real controller;
the harness uses a bridge stub, so controller wiring remains a Windows gate.
They do not validate real Excel COM identity, production binding or Windows UI
reentrancy. Those remain Phase 5B2 gates in [ADR-0005](../../docs/decisions/ADR-0005-bound-document-session.md).

The [Excel identity fallback](../RNAssistant.ExcelIdentityProbe/README.md) now uses
the same `OfficeHosts.Qualification` decoder/lease as the in-app WQ0 pack. The
harness filter checks bounded OBJREF/helper protocol parsing and non-Windows refusal
only; it does not execute COM, helper processes, marshal cleanup, the PowerShell
driver or Windows qualification.

Versioning changes use the existing `Program.ProjectStructureTests.cs` suite:

```bash
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"
node tests/web/settings-version.test.js
```

The `versioning` substring selects only versioning cases. These need Git and dotnet;
they invoke MSBuild against disposable small projects, commits and local bare remotes.
Fixture refs never affect the working repository or its origin. Coverage includes
unchanged product versions across ordinary builds/commits, source archives without Git
(Git/GitHub commit recovery, Debug/Release, explicit/unknown metadata and release
rejection), invalid metadata,
release-only gates, tag uniqueness and SDK/old-style assembly attributes. No Office
projects or PowerShell release workflow are executed by this slice. The focused
JavaScript check covers the Settings product/short-commit label and provenance
fallbacks without WebView layout execution.

## Stabilization characterization

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization
```

Phase 1A extends `Program.SimpleAgentTests.cs`: write ok/error/unknown/no-write,
twentieth-response recovery, rejected history isolation and the current 20-retry
(21-request) cap. These tests use fake LLM/Office and the real local VBA journal.
Phase 1C replaces false-success expectations with independent runtime-health
assertions while preserving v2 model status. Before the fix, four evidence tests
were red; after it, all seven pass. This is host-neutral safety coverage, not
Windows qualification. See [Phase 1A evidence](../../docs/stabilization/PHASE_1A_CHARACTERIZATION.md).

## Kernel and production adapters (Phase 3B2)

`kernel:` uses fake generic model/tool/store ports and covers deterministic
outcomes, IDs, limits, cancellation and append failures. `kernel replay:` uses
actual ChatStore events, isolated AppData, the current executor and a fake Office
adapter: summary/pending replay, cancelled/stale confirmation, and interrupted
execution/materialization boundaries. Neither is COM/Office validation.

`agent:` / `protocol context:` cover the connected production service and its
external model-session owner, including native read-batch call/result pairing
through save/reload. Test construction requires a fixture-local ChatStore; it
never falls back to real user AppData. `model protocol:` retains endpoint-boundary
coverage under `IMaterializedModelProtocol`; no second parser/retry loop is added.

MockDemo compilation includes the actual controller; the harness's controller
remains a stub. [Phase 3B2 evidence](../../docs/stabilization/PHASE_3B2_KERNEL_CUTOVER.md)
separates that compile/source review from the unperformed Windows delivery gate.

## Typed Excel read owner (Phase 7B)

`excel read:` covers exact native registration, Agent/manual routing, bound-session
owner-STA dispatch, every inspect selector, values/formulas/profile, explicit empty
cells, host-before-materialization range bounds, domain snapshot validation,
closed/switched target refusals and the shared HTML bind/refresh route. The fake host
implements the direct typed backend; a public read reaching generic host dispatch
fails, so dual execution is observable. These tests do not compile or execute real
Excel Interop. Protected sheets, large live workbooks, actual COM errors and
desktop/VSTO/native composition remain WQ-EXCEL gates. See
[Phase 7B evidence](../../docs/stabilization/PHASE_7B_EXCEL_READ.md).

## Typed Excel find/replace owner (Phase 11T1)

`excel find replace:` covers direct native ownership, the bound backend, literal and
regex matching, values/formulas and workbook/sheet/range/selection scopes,
`replaceAll`, bounds, verified no-op/change, exact pre-dispatch drift, post-dispatch
failure/read-back divergence and closed/switched-document refusal. The fake generic
host path rejects both public ids, so dual dispatch is observable. Real Excel COM,
protected/large ranges and partial assignment remain WQ-EXCEL gates. See
[Phase 11T1 evidence](../../docs/stabilization/PHASE_11T1_EXCEL_FIND_REPLACE.md).

## Typed Excel sheet lifecycle owner (Phase 11T2)

`excel sheet:` covers direct native ownership, add/rename defaults and active-sheet
selection, worksheet-name/collision rules, exact collection pre-state, verified
no-op/change, pre-dispatch drift, post-dispatch failure/read-back divergence and
closed/switched-document refusal. The fake generic host path rejects both public ids,
so dual dispatch is observable. Real Excel protected workbook structure, COM rollback
and case-only rename remain WQ-EXCEL gates. See
[Phase 11T2 evidence](../../docs/stabilization/PHASE_11T2_EXCEL_SHEETS.md).

## Typed Excel range mutation owner (Phase 11T3)

`excel range mutation:` covers exact native ownership for clear/sort/filter/format,
public defaults, separate values/formats clear behavior, whole-row sorting, filter
state, verified no-op/change, selector rejection, exact pre-dispatch drift,
post-dispatch failure/read-back divergence and closed bound-document refusal. The fake
generic host rejects all four public ids, so dual dispatch is observable. Real Excel
sort locale, AutoFilter normalization, mixed/conditional formatting, autofit,
protected ranges and partial COM effects remain WQ-EXCEL gates. See
[Phase 11T3 evidence](../../docs/stabilization/PHASE_11T3_EXCEL_RANGE_MUTATIONS.md).

## Typed Excel table owner (Phase 11T4)

`excel table:` covers exact native ownership, existing defaults, generated and
explicit names, style/header projection, source and workbook-collection bounds,
case-insensitive collision, exact pre-dispatch drift, post-dispatch failure,
read-back divergence and closed bound-document refusal. The fake generic host rejects
the public id, so dual dispatch is observable. Real Excel `xlNo` header/range
semantics, style localization, overlap/protection, rollback and partial COM effects
remain WQ-EXCEL gates. See
[Phase 11T4 evidence](../../docs/stabilization/PHASE_11T4_EXCEL_TABLES.md).

## Typed Excel chart owner (Phase 11T5)

`excel chart:` covers exact native ownership for `create_chat_chart`, `upsert_chart`
and `delete_chart`, chat-artifact source reads, create/update/delete/default/strict-mode
semantics, ambiguous names, collection/range bounds, verified no-change/change,
pre-dispatch drift, unknown-after-dispatch/read-back divergence, dry-run and bound
owner-STA/closed-workbook behavior. The fake generic chart route fails closed. These
tests do not execute Excel Interop; live series formulas, axes/types, generated names,
protection and partial COM effects remain WQ-EXCEL gates. See
[Phase 11T5 evidence](../../docs/stabilization/PHASE_11T5_EXCEL_CHARTS.md).

## Typed VBA mutation outcome (Phase 6D)

`vba: mutation` covers the typed service boundary and injected prepare, terminal,
backend, read-back and cancellation faults. The broader `vba:` slice reuses
restart, normalization, collision and not-found cases. These are fake-host ordering
checks, not real COM/VBE qualification. See
[Phase 6D evidence](../../docs/stabilization/PHASE_6D_VBA_MUTATION_OUTCOME.md).

## Whole-module VBA write owner (Phase 6E)

`vba: whole write service owns workflow` checks the direct typed owner, normalized
create, existence refusals before persistence/dispatch and same-source/different-type
create-race classification. The full `vba:` filter retains confirmation races,
upsert/update, invalid snapshots, read-back drift, journal correlation, UserForm and
VBE normalization coverage. See
[Phase 6E evidence](../../docs/stabilization/PHASE_6E_VBA_WHOLE_MODULE_WRITE.md).

## VBA delete owner (Phase 6F)

`vba: delete service owns workflow` checks the direct typed owner: guard and
component policy, dry-run without persistence/dispatch, live-source CAS hash,
verified absence, durable call correlation and protected-component refusal before
journal/dispatch. The full `vba:` filter retains stale confirmation, document
identity, backend success without deletion, rollback backup and COM policy cases.
See [Phase 6F evidence](../../docs/stabilization/PHASE_6F_VBA_DELETE.md).

## VBA restore owner (Phase 6G)

`vba: restore service owns workflow` checks the direct typed owner: a prepared
guard is mandatory and binds the exact backup id/live-source hash plus current target state;
backup substitution, altered backup evidence, stale target and incompatible type
stop before persistence/dispatch. It also covers dry-run, compare-and-swap replace,
verified source/type and durable accepted-call correlation. The full `vba:` slice
retains latest-backup pinning across confirmation, missing-target typed creation,
journal/CAS recovery and VBE normalization coverage. See
[Phase 6G evidence](../../docs/stabilization/PHASE_6G_VBA_RESTORE.md).

## VBA package lifecycle owner (Phase 6I)

`vba: package` covers the typed package owner and R41 boundaries: package validation,
marker+journal-aware state, one session lifecycle correlation, persistent and
temporary execution, explicit orphan cleanup, prepare/backend/read-back/terminal
faults, cancellation, marker drift/strip, probe/preparation and pre-run races,
post-prepare backend CAS, undeclared catalog components and mixed multi-component
recovery. Since 11J2 it also checks immutable `ToolPackageSource` v1 revision
pinning, exact native custom-id routing without a case alias, no dispatch before
confirmation, result v1 and conservative `unknown` effect after arbitrary macro
dispatch. The shared COM helper guard is exercised against a fake VBProject; the
full `vba:` filter retains document discovery, code-only UserForm, macro failure,
VBE normalization, journal/CAS and rename regressions. These are fake-host ordering
checks; real VBIDE/Trust Access/crash behavior remains Windows qualification. See
[Phase 6I evidence](../../docs/stabilization/PHASE_6I_VBA_PACKAGE_LIFECYCLE.md) and
[Phase 11J2 evidence](../../docs/stabilization/PHASE_11J2_VBA_PACKAGE_NATIVE_RUNTIME.md).

## Skill authoring native runtime (Phase 11K1)

The `skills:` filter covers the complete current-package revision, core/reference
CRUD and collision/validation behavior. The CRUD case also verifies the four exact
core `common.skills_upsert/delete` and reference
`common.skills_reference_upsert/delete` registrations, Agent-only confirmed-write policy,
versioned result data, no dispatch before confirmation, verified change/no-change
and rejection of a stale prepared package. Focused `agent: confirmation`,
`protocol context:` and `kernel replay:`/`kernel recovery:` cases keep durable
confirmation and effect evidence across continuation and faults. See
[Phase 11K1 evidence](../../docs/stabilization/PHASE_11K1_SKILL_AUTHORING_NATIVE_RUNTIME.md).

## VBA rename owner (Phase 6J)

`vba: rename` covers the direct typed owner, both-name/source-type confirmation
guard, prepare/backend/read-back/terminal faults, cancellation boundaries,
post-prepare collision and read-only recovery of complete-before,
complete-intended and mixed states. `vba: rename intent is strict and atomic` keeps
the public schema/result/journal projection regression, while `vba: COM rename`
checks component identity plus source hash/type CAS in the shared host helper. The
full `vba:` slice retains module/package/VBE and serialized reconciliation coverage.
These are fake-host checks; real VBIDE/Trust Access/confirmation/cancellation remain
Windows qualification. See
[Phase 6J evidence](../../docs/stabilization/PHASE_6J_VBA_RENAME.md).

## Stabilization completion guard

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: turn lifecycle"
node tests/web/completion-guard.test.js
```

The guard tests extend `Program.AgentSafetyTests.cs` / `Program.SimpleAgentTests.cs`:
single-result legacy mapping, cumulative error/unknown precedence, kernel confirmation,
cancelled-summary replay and fresh-turn reset. Phase 9D5 adds `run view:` replay
equality, immutable wire, source-evidence separation and explicit pending state;
`tests/web/run-view-state.test.js` covers strict normalization, per-chat revision
ordering and integrated stale transcript/outcome rejection. The lifecycle test
continues to cover event replay and exclusion of UI projection from model transport.
The Node test loads the real static JS projection/render functions with a minimal
DOM and stubs only unrelated trace/media helpers. No npm dependencies are needed.
It verifies warning visibility outside collapsed trace, not browser layout or
production controller delivery. See [Phase 1C evidence](../../docs/stabilization/PHASE_1C_COMPLETION_GUARD.md).

## Stabilization causal trace

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"
```

Phase 1B uses `Program.SimpleAgentTests.cs` and `Program.SessionEventStoreTests.cs`:
ok/error/journal unknown, twentieth-response repair correlation, confirmation,
async scope isolation and harmless optional trace failures. Real host-neutral
runtime/store/journal run with fake LLM/Office. Controller wiring is not executed:
this harness uses `AssistantControllerBridgeStub`. Scope/projection marker tests
do not prove production bridge delivery or WebView rendering. See
[trace evidence and boundaries](../../docs/stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Stabilization v5 contract / runtime IDs

`conversation v5:` covers strict ID-less parser/schema/arguments, singleton
safety, schema transport and the separate accepted-history reader. Model-owned
IDs are rejected; identical calls remain distinct ordered positions. `kernel:`
checks runtime allocation, collisions/invalid allocator output before acceptance,
and reuse of pending IDs without allocation. No tool retry or deduplication is added.

`protocol context:` checks full-turn IDs/origins across compaction and confirmation,
incomplete history, batch safety and all three result roles. `agent:` includes
complete HTML preservation in user/native history and native read-batch pairing.
`causal trace:` links each accepted runtime ID to the exact raw model attempt and
call position, including repair. `kernel replay:` uses the real event store.
`context: clone preserves values` checks fork URI rebasing without changing ISO
argument strings. Production controller reconstruction is source-reviewed and
compiled in MockDemo, not executed by the stubbed harness. See
[R29 evidence](../../docs/stabilization/R29_RUNTIME_CALL_IDS.md).

The `preflight` filter covers incompatible full history in all three modes
and accepted-history forms, including suppressed/compacted-away records; incomplete
confirmation, current/new chat success, and zero raw attempts/repair/progress for
missing CallContext. Snapshots prove no history/checkpoint/run mutation on rejection.
The real shared confirmation guard is exercised, but production controller ordering
and manual-compaction wiring are reviewed in source only; controllers are stubbed
in this harness. Existing confirmation fixtures now carry the current LastRun
marker and mandatory origin that production writes. Historical cutover evidence remains
[2C3C](../../docs/stabilization/PHASE_2C3C_V3_CUTOVER.md); current gates are in R29 evidence.

Phase 2C3A extends the two `model compatibility:` cases across both formats and all
three tool-result roles, strict sentinels/status/casing and one raw attempt per
probe. Phase 2C3C switches runtime/probes together through ModelProtocolWire and
rejects native refusal even when JSON content matches the expected sentinel.
See [2C3A evidence](../../docs/stabilization/PHASE_2C3A_WIRE_OWNER.md).

Phase 2C3B replaces the obsolete destructive-reset characterization with prompt
preservation/review tests: missing/old/current/future markers, real settings
load/save, failed approval, explicit reset, and neutral Agent/Chat/Plan/continuation
entry guards. SettingsService is now source-linked; ProtectedSecretStore remains
excluded and a test-only stub throws on any secret-file read/write. Absent fixture
secrets are supported; DPAPI/protection changes are not being qualified.
The existing typed-settings bridge test uses the controller stub, not production
controller execution. `node tests/web/prompt-review.test.js` verifies actual form
serialization and action handlers with minimal DOM/transport substitutes, including
cancel/failure/reset and Plan preservation; it does not verify WebView layout.
R29 introduced v4/schema 13; Phase 4B introduced Tool Result v1/schema 14; Phase 8B
uses schema 15 for atomic callable-pack admission and no-eviction guidance; Phase 8C
uses schema 16 for durable turn-scoped reconstruction; R61/11O1 uses schema 17 for
the semantic Resource/Capability boundary; R61/11O2 uses schema 18 for semantic
questions, Plan documents and Task Lists; R61/11O3 uses schema 19 for semantic HTML
authoring and accepted-read binding; the user-requested R61/11O1 correction uses
schema 20 for whole resource reads and the direct bound VBA-project target;
R61/11O4 uses schema 21 for semantic Prompt/Tool/Skill authoring; R61/11O5 uses
schema 22 for semantic VBA/macro intents; the user-reported readiness/completion
correction used schema 23 for prerequisite skill/tool selection, Task List lifecycle,
root tool arguments, dependency order and evidence-reconciled completion. Schema 24
makes the six-stage operating workflow and prompt/skill/tool authority split
explicit, strengthens all built-in skill completion criteria, and adds exact
skill-reference-to-catalog validation. Schema 25 tightens successful finish around
closing active Task Lists and HTML bound-data render evidence. Current schema 26
adds explicit v5 `final` intent and no-tool checkpoint behavior. Tests
preserve, review, or reset saved older/future markers explicitly. JS review behavior
is unchanged.
See [2C3B evidence](../../docs/stabilization/PHASE_2C3B_PROMPT_REVIEW.md).

Phase 8D moved the original resource data plane to native handlers. R61/11O1 now
publishes only semantic `common.resources_find/read`, keeping provider routing,
revision-pinned `ResourceRef` and continuation state inside runtime/durable evidence.
Use `tool runtime: native resource tools`, `resources:`, the focused model-projection,
media/replay cases, and `harness: production projects include all source files` to
cover the intent boundary, internal guards and old-style project inclusion. See
[8D evidence](../../docs/stabilization/PHASE_8D_RESOURCE_DATA_PLANE.md) and the
[R61 audit](../../docs/stabilization/R61_TOOL_CONTRACT_AUDIT.md).

The schema-20 correction makes `common.resources_read` return one complete bounded
representation or an explicit error. Broad VBA inspection starts from
`RUNTIME_CONTEXT.document.vba_project_target`; unfiltered VBA find is only fallback
discovery and keeps the project target first, while queried find remains filtered.

R61/11O2 replaces Plan create/update with `common.plan_doc_save`, replaces the
three Task List lifecycle ids with `common.task_list_set`, and removes caller-owned
question/option/plan/artifact/list/step/revision identity. Use `plan mode:`,
`plan document:`, `task lists:`, the focused model-projection case and the R61
property inventory. The services still bind exact active revisions and stable ids
internally; incompatible retained calls require a new chat/reset.

R61/11O3 replaces HTML inspect/set-active/general upsert with internal diagnostics/
selection and separate semantic file/data writes. Patch is exact-only; delete takes
one readable target; bind reuses the latest successful eligible accepted Office read
from the current run without nested source arguments; refresh takes only an optional
name. Use `html`, `tools: R61 built-in contract inventory` and
`node tests/web/html-workspace-export.test.js` plus
`node tests/web/html-workspace-echarts.test.js`. A refresh must capture the reread
JSON in a new active HTML revision; standalone export embeds that exact snapshot and
the pinned local ECharts runtime, with no live Office bridge. Prompt schema 19 and
accepted-history validation introduced the HTML switch; schema 20 added
whole-resource correction.

R61/11O4 makes prompt save one exact key/value mutation, removes model-facing
`common.tools_validate` and Tool read list mode, reduces Tool upsert to semantic
package source/docs, and splits Skill core/reference mutations. Runtime derives
manifest metadata, applies conservative authority and rejects plumbing-shaped
custom arguments without `Domain identity rationale:` both on validation and load.
Use the focused authoring filters in the matrix above plus the R61 inventory.
Prompt schema 21 was the 11O4 boundary; its retained calls remain valid only when
they also satisfy every later switched-family contract.

R61/11O5 separates identity-preserving VBA rename from whole-source write, removes
the fixed patch operation and raw backup identity from model arguments, resolves
restore by readable target or latest-for-module inside runtime, and hides mutation/
backup ids, hashes and guards from model results. Use `vba:`, the focused semantic
contract case, the VBA Agent/resource/facade cases and the R61 inventory. Prompt
schema 22 was the 11O5 boundary. Current schema 26 requires explicit prompt
review/reset before Agent/Plan execution; retained pre-switch calls still require a
new chat/reset.

## Full suite

Run the complete harness only for broad cross-cutting changes:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

COM/VSTO behavior remains Windows-only: validate with Windows x64, Office and VS 2022.

Tool editor smoke after pipeline removal: `node tests/web/tools-editor.test.js` exercises VBA draft creation, editor source synchronization and built-in clone rejection against the shipped HTML IDs. This is not Windows/WebView layout validation.

Typed package-action normalization: `node tests/web/tool-package-actions.test.js`
checks that install/remove accepts only result contract v1 with exact lowercase
fields/effects and rejects the deleted PascalCase compatibility shape.
