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
Some assertions intentionally capture known false completion; passing this slice
does **not** mean that release safety is fixed. Phase 1C must introduce red→green
runtime-health assertions. See [Phase 1A evidence](../../docs/stabilization/PHASE_1A_CHARACTERIZATION.md).

## Full suite

Run the complete harness only for broad cross-cutting changes:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

COM/VSTO behavior remains Windows-only: validate with Windows x64, Office and VS 2022.
