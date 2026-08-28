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

Verification scope and evidence reuse follow [master plan §22.1](../../docs/stabilization/STABILIZATION_MASTER_PLAN.md#221-минимальная-достаточная-проверка). Docs-only changes need diff/affected-link checks, not a harness build. A recorded pass is reusable only when its relevant sources/tests, dependencies, build settings and environment are unchanged. Explicit phase/release gates and the pre-commit version check still apply.

## Test map

| Area | Main files | Useful filters |
| --- | --- | --- |
| Conversation and Agent | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ToolDiscoveryTests.cs` | `conversation:`, `agent:` |
| Office model-context owner | `Program.ToolDiscoveryTests.cs`; result/projection coverage in `Program.AgentSafetyTests.cs` | `agent: model session`, `agent: bounds oversized`, `context inspector:`, `protocol context:` |
| ModelProtocol boundary | `Program.AgentSafetyTests.cs`; media integration in `Program.ResourceGatewayTests.cs` | `model protocol:`, `agent: hydrates artifact media`, `causal trace:` |
| Active wire / compatibility probes | `Program.AgentSafetyTests.cs` | `model compatibility:`, `agent: supports selectable`, `model protocol:` |
| Prompt schema review / settings | `Program.ChatSettingsTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ContextBridgeTests.cs` | `settings:`, `bridge: typed settings`, `chat: prompt save` |
| Conversation v3 contract/context | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs` | `conversation v3:`, `protocol context:` |
| History/context preflight | `Program.AgentSafetyTests.cs` | `preflight`, `protocol context:`, `model protocol:` |
| Resources and attachments | `Program.ResourceFabricTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.AttachmentTests.cs` | `resources:`, `attachments:` |
| Session storage and CAS | `Program.SessionEventStoreTests.cs`, `Program.CasMaintenanceTests.cs` | `storage:` |
| Chats, context and bridge | `Program.ChatSessionTests.cs`, `Program.ChatEditTests.cs`, `Program.ContextBridgeTests.cs`, `Program.PromptContextInspectorTests.cs` | `chat:`, `chat sessions:`, `context:`, `bridge:` |
| Tools and disabled pipelines | `Program.ToolStoreTests.cs`, `Program.PipelineToolTests.cs`, `Program.SearchToolTests.cs` | `tools:`, `pipeline:`, `search:` |
| VBA | `Program.VbaPromptTests.cs`, `Program.VbaToolPackageTests.cs` | `vba:` |
| HTML, plans and charts | `Program.HtmlArtifactStorageTests.cs`, `Program.PlanToolTests.cs`, `Program.ChartArtifactTests.cs` | `artifacts:`, `plans:`, `chart:` |
| Desktop/WebView-neutral | `Program.ParserDesktopTests.cs`, `Program.WebViewSecurityTests.cs` | `desktop target:`, `webview:` |

The `harness:` slice also verifies that every production `.cs` file is explicitly included in its old-style `.csproj`, preventing source-linked harness globs from hiding a broken production project.

Versioning changes use the existing `Program.ProjectStructureTests.cs` suite:

```bash
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"
```

The `versioning` substring selects only versioning cases. These need Git and dotnet;
they invoke MSBuild against disposable small projects, commits and local bare remotes.
Fixture refs never affect the working repository or its origin. Coverage includes
unchanged product versions across ordinary builds/commits, source archives without Git
(Debug/Release, explicit/unknown metadata and release rejection), invalid metadata,
release-only gates, tag uniqueness and SDK/old-style assembly attributes. No Office
projects or PowerShell release workflow are executed by this slice.

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

## Stabilization completion guard

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: turn lifecycle"
node tests/web/completion-guard.test.js
```

The guard tests extend `Program.AgentSafetyTests.cs` / `Program.SimpleAgentTests.cs`:
metadata, cumulative error/unknown precedence, confirmation, cancellation, legacy
mapping and fresh-turn reset. The existing lifecycle test covers event replay,
independent clones, typed bridge serialization and exclusion from model transport.
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

## Stabilization v3 contract

`conversation v3:` covers the status-free parser/schema, strict JSON/arguments,
run IDs, singleton safety, schema transport and current v3 history forms. The
unused historical v2 adapter and its obsolete tests were removed in Phase 2C2.
`protocol context:` covers detached snapshots, full-turn IDs across compaction
and confirmation, incomplete history and conservative batch safety. Two cases
exercise the real host-neutral loop/ModelProtocol/executor with fake LLM/Office;
the controller's identity transition is simulated, not production controller execution.
Since Phase 2C3C, `agent:` and `model protocol:` exercise the **active v3 path**.
Real integration covers run-wide duplicates, a batch of two local reads, unsafe
write-batch rejection/repair to singletons, native refusal and all three result
roles. Fixtures use production v3 writers; the old conversion helper and nine
obsolete v2 parser tests are deleted. Context introduction evidence remains
[2C2](../../docs/stabilization/PHASE_2C2_PROTOCOL_CONTEXT.md).

Phase 2C3C's `preflight` filter covers incompatible full history in all three modes
and accepted-history forms, including suppressed/compacted-away records; incomplete
confirmation, current/new chat success, and zero raw attempts/repair/progress for
missing CallContext. Snapshots prove no history/checkpoint/run mutation on rejection.
The real shared confirmation guard is exercised, but production controller ordering
and manual-compaction wiring are reviewed in source only; controllers are stubbed
in this harness. Existing confirmation fixtures now carry the current LastRun
marker that production writes. See [2C3C evidence](../../docs/stabilization/PHASE_2C3C_V3_CUTOVER.md).

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
Phase 2C3C reruns preservation/review/reset against actual v3 defaults/schema 12,
including saved schema 11. JS review behavior is unchanged by the v3 cutover.
See [2C3B evidence](../../docs/stabilization/PHASE_2C3B_PROMPT_REVIEW.md).

## Full suite

Run the complete harness only for broad cross-cutting changes:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

COM/VSTO behavior remains Windows-only: validate with Windows x64, Office and VS 2022.

Tool editor smoke after pipeline removal: `node tests/web/tools-editor.test.js` exercises VBA draft creation, editor source synchronization and built-in clone rejection against the shipped HTML IDs. This is not Windows/WebView layout validation.
