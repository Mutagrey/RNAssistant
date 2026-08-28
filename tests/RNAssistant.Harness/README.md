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

## Test map

| Area | Main files | Useful filters |
| --- | --- | --- |
| Conversation and Agent | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ToolDiscoveryTests.cs` | `conversation:`, `agent:` |
| ModelProtocol boundary | `Program.AgentSafetyTests.cs`; media integration in `Program.ResourceGatewayTests.cs` | `model protocol:`, `agent: hydrates artifact media`, `causal trace:` |
| Conversation v3 introduction | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs` | `conversation v3:` |
| Resources and attachments | `Program.ResourceFabricTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.AttachmentTests.cs` | `resources:`, `attachments:` |
| Session storage and CAS | `Program.SessionEventStoreTests.cs`, `Program.CasMaintenanceTests.cs` | `storage:` |
| Chats, context and bridge | `Program.ChatSessionTests.cs`, `Program.ChatEditTests.cs`, `Program.ContextBridgeTests.cs`, `Program.PromptContextInspectorTests.cs` | `chat:`, `chat sessions:`, `context:`, `bridge:` |
| Tools and pipelines | `Program.ToolStoreTests.cs`, `Program.PipelineToolTests.cs`, `Program.SearchToolTests.cs` | `tools:`, `pipeline:`, `search:` |
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
unchanged product versions across ordinary builds/commits, invalid metadata,
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

Phase 2C1 uses `conversation v3:` for the new status-free parser/schema and explicit
v2 read adapter: envelope/JSON strictness, argument validation, accepted-run IDs,
read-only batching, adapter isolation and generated schema transport. These are
Core contract tests, **not a runtime cutover**. The existing `agent:` and
`model protocol:` slices still exercise the active v2 path.
See [Phase 2C1 evidence](../../docs/stabilization/PHASE_2C1_V3_CONTRACT.md).

## Full suite

Run the complete harness only for broad cross-cutting changes:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

COM/VSTO behavior remains Windows-only: validate with Windows x64, Office and VS 2022.
