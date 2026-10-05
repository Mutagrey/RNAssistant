# RNAssistant

Local AI assistant for Excel, Word, PowerPoint and Outlook.

## Target

- Windows 10
- Visual Studio Community 2022
- Office x64
- .NET Framework 4.8
- C# 7.3
- No admin rights required for normal build/run

## Structure

- `src/RNAssistant.Core` - settings, DPAPI secret storage, chat/context stores, OpenAI-compatible chat client, skill parser.
- `src/RNAssistant.Runtime` - development workspace conversation composition over the shared kernel and stores.
- `src/RNAssistant.Cli` - Office-independent .NET 8 development client.
- `src/RNAssistant.Office` - shared WebView2 task pane, JS bridge, ribbon XML and assistant controller.
- `src/RNAssistant.OfficeHosts` - shared Excel/Word/PowerPoint/Outlook COM adapters.
- `src/RNAssistant.NativeHostCli` - C++/CLI in-process DLL host for VBA.
- `src/RNAssistant.Desktop` - standalone WinForms/WebView2 desktop shell.
- `src/RNAssistant.*AddIn` - VSTO compatibility add-ins and ribbon/task pane wiring.
- `wrappers/native` - VBA source modules for Office-native launcher wrappers.
- `web` - static local task pane UI, no npm build.
- `packages` - vendored NuGet packages for offline restore.
- `vendor/pdf-rendering` - vendored PDFtoImage/PDFium/SkiaSharp binaries for Windows x64 and x86.
- `vendor/webview2-runtime` - optional fixed WebView2 x64 runtime folder.

The documentation entry point and placement rules are in
[`docs/README.md`](docs/README.md). Canonical engineering rules are in
[`docs/development-rules.md`](docs/development-rules.md); current architecture and
ownership are in [`docs/architecture.md`](docs/architecture.md).
`AGENTS.md` adds the current development and environment-specific instructions;
working status and open qualification evidence are tracked in
[`docs/stabilization/PROGRESS.md`](docs/stabilization/PROGRESS.md).

## Workspace CLI (development)

The workspace CLI writes ordinary UTF-8 files in a chosen folder. It does not
require Office, WebView2 or the mock demo:

```sh
dotnet run --project src/RNAssistant.Cli/RNAssistant.Cli.csproj -- workspace open ./project
RNA_BASE_URL=https://example.invalid RNA_MODEL=model-name RNA_API_KEY=... \
  dotnet run --project src/RNAssistant.Cli/RNAssistant.Cli.csproj -- \
  run --workspace ./project --task-file ./task.md --profile development
```

Use an actual approved OpenAI-compatible endpoint and model; the URL above is
only a placeholder. Keys are read from `RNA_API_KEY` or `OPENAI_API_KEY`, never
from CLI arguments or the workspace manifest. `run --session <id> --message ...`
continues a saved chat as a new turn. `inspect --workspace <path> --session <id>`
shows its saved state; `sessions --workspace <path>` lists workspace chats.

For local model comparisons, pull only the needed tag and create its bounded
Ollama profile from `config/ollama/`:

| Source tag | Profile / `RNA_MODEL` | `RNA_CONTEXT_TOKENS` |
|---|---|---:|
| `gemma4:12b` | `rna-gemma4-12b-32k` | 32768 |
| `gpt-oss:20b` | `rna-gpt-oss-20b-16k` | 16384 |
| `qwen3.5:9b` | `rna-qwen35-9b-32k` | 32768 |
| `qwen3.8:27b` | `rna-qwen38-27b-16k` | 16384 |
| `gemma4:cloud` | `gemma4:cloud` (remote; protocol qualification open) | Set an explicit planning limit |

Pull and create profiles one at a time, without running them during setup:

```sh
ollama pull gemma4:12b
ollama create rna-gemma4-12b-32k -f config/ollama/Modelfile.gemma4-12b-32k
ollama pull gpt-oss:20b
ollama create rna-gpt-oss-20b-16k -f config/ollama/Modelfile.gpt-oss-20b-16k
ollama pull qwen3.5:9b
ollama create rna-qwen35-9b-32k -f config/ollama/Modelfile.qwen35-9b-32k
ollama pull qwen3.8:27b
ollama create rna-qwen38-27b-16k -f config/ollama/Modelfile.qwen38-27b-16k
ollama pull gemma4:cloud
```

Set `RNA_BASE_URL=http://127.0.0.1:11434`, `RNA_MODEL=rna-gemma4-12b-32k`,
`RNA_CONTEXT_TOKENS=32768`, and `RNA_THINKING=off` for the CLI command above.
The CLI requests 4096 output tokens; `RNA_REASONING_MODE` defaults to
`reasoning_effort`. Set `RNA_THINKING=on` only for a deliberate reasoning-mode
comparison. `--response-mode` / `RNA_RESPONSE_MODE` explicitly selects
`json_schema` (CLI default) or `json_object` for an endpoint that needs it;
both use the same v6 parser and agent loop. The run saves model, Ollama digest
when available, context, response mode, instruction role and reasoning mode;
`rna inspect` shows that snapshot. `--instruction-role` / `RNA_INSTRUCTION_ROLE`
selects `developer` (default) or `system` using the existing `SystemPromptRole`
setting, with identical instruction text. Select a role that the endpoint's model
template actually renders. Approval inherits the recorded role; a conflicting
override fails before dispatch. Use a new session for each
model comparison. Do not append `/v1` to the CLI base URL; `LlmClient` addresses
`/v1/chat/completions` itself. A loopback Ollama endpoint needs no API key.
For other endpoints, `RNA_MODEL_DIGEST` is an operator-supplied label; verify it
against that provider before using it as comparison evidence.
For the default local Ollama endpoint, CLI checks the selected tag and `/api/show`
before starting a run; an unavailable model or RNAssistant context above the
profile's `num_ctx` fails before model dispatch.
The separate `rna-qwen38-27b-8k` profile is only for context-limit rejection
checks: with the CLI's 4096 output-token request and mandatory continuation and
format-repair reserves, even an empty Agent request cannot pass its 8K budget.
Use the 16K profile for Agent comparisons only after checking memory pressure
and the actual prompt composition; 16K is not a blanket guarantee for long
Office chats or loaded tool/skill bodies.
The `gemma4:cloud` tag through Ollama 0.35.0 has not passed the CLI v6 probe:
both response modes failed the full task before any tool call. See
[current evidence](docs/stabilization/PROGRESS.md#workspace-first-implementation--2026-10-04-in-progress)
before using it as a reference model.
The tested `rna-gpt-oss-20b-16k` template omits `developer` messages. A `system`
probe delivered the instructions, but still returned provider-native tool calls
instead of v6 JSON. This profile has not passed the protocol gate; increasing its
context/output limits is not supported by that failure's evidence.
In the Office UI, set the selected model's capability metadata/context limit to
the same profile values before a separate test session.

Check `ollama ps` before and after switching; unload the previous model with
`ollama stop <model>` before using another heavy local model. Pulling and creating
a profile do not load it for inference. On a 24 GB Mac, start the 27B profile at
8K and keep only one heavy model resident. If Ollama runs parallel requests,
configure `OLLAMA_NUM_PARALLEL=1` on its server. The comparison workflow, error
categories and independent acceptance rule are in
[development rules §9](docs/development-rules.md#сравнение-моделей).

For a three-file creation test, add
`--expect-files index.html,styles.css,app.js --min-reads 3 --min-writes 3` to
`run`. The CLI reads those current files through the workspace service and exits
with code 5 and `acceptance=failed` when the model's `done` lacks the required
effects or files. The accepted criteria are saved before model dispatch and remain
visible through `inspect`. `--min-reads` counts distinct complete file reads whose
evidence is still current at completion; `--min-writes` counts verified changed
file effects, excluding no-op tool calls. `model_done` by itself is the model's
claim. For bounded local experiments, `--max-iterations` (1–256) and
`--max-tool-steps` (1–4096) override the per-run defaults without changing the
shared agent loop. An exhausted limit is a failed run with retained history.
Use `node --check` for generated JavaScript and a browser run for behavior.

`rna verify --workspace ./project --entry index.html` runs a bounded static-web
smoke in an isolated Chromium profile. It serves only an immutable snapshot of
the entry and discovered local HTML/CSS/JS dependencies over loopback, and reports
missing assets, external references, console errors and uncaught JavaScript errors.
Set `RNA_BROWSER_EXECUTABLE` to an absolute Chromium path if automatic detection
does not find one. Missing browser returns `not-run` and exit 4. For agent runs,
`--require-web-verify` requires a successful `web.verify` tool result whose file
observations are still current at `done`; pair it with `--expect-files` for the
required project files. This smoke does not assert application behavior such as
filtering, persistence or export.

Each check returns `snapshotId` (when capture succeeded) and `verificationId`.
`rna verification --workspace ./project --id <verificationId>` reads its saved
result; `rna verifications --workspace ./project [--offset <n>]` lists up to 20
records, including `Pending` checks without a terminal result after interruption.
These commands never rerun a browser. `rna verify --workspace ./project --snapshot
<snapshotId>` explicitly checks the retained historical bytes after restart, even
if sources moved or were deleted. It reports `historical=true` and does not restore
or verify the current workspace. Missing retained data fails without live fallback.

`verify --web-checks <json-file>` additionally executes a fixed functional contract
against that exact snapshot. `run --web-checks <json-file>` freezes the same contract
at admission (also implying `--require-web-verify`); every model `web.verify` uses
it, and `done` requires its saved passing result plus current source evidence.
Keep independent checks outside the agent's writable workspace. The tool cannot
replace them or choose another entry. The saved result retains the complete
contract and each step's `Passed` / `Failed` / `NotRun` outcome and observed text.
Current operations are real pointer `Click` and `TextEquals` (trimmed text content,
exactly one visible HTML element). Bounds: 32 KiB contract, 32 steps including a
text assertion, two seconds for text to match and 30 seconds for the sequence.
The first failed step stops the sequence; later steps remain `NotRun`.
See [counter checks](tests/cli/counter_checks.json) for the JSON format. To exercise
real verify/repair, copy `tests/cli/fixtures/counter-repair/` into a disposable
workspace and use `tests/cli/counter_repair_task.md` with these checks. Page-load
success alone deliberately misses the fixture's click-time exception.

After an interrupted file mutation, `recover --workspace
<path> --path <relative-path>` reports the exact recovery outcome without replaying
the write. Agent `files.delete` moves a previously read UTF-8 file into
workspace-local `.rnassistant/trash`; `files.restore` explicitly restores the latest
managed deletion if its original path is still absent. `files.delete` stops at a
durable confirmation. Use `resume` to inspect the pending id, then `approve` or
`deny` with that exact id. Approval resumes the same kernel run and rechecks the
accepted file before dispatch; denial closes the call without dispatch.
`files.move` also requires confirmation after a complete read. It moves a file
to an unoccupied path with an existing parent while preserving its logical identity;
an interrupted move requires explicit `recover` from its source or target path.
`--jsonl` emits one event per line. See `--help` for the
current command and exit-code surface. Broader real-model quality, browser verification,
full file operations and Windows delivery remain open in
[progress](docs/stabilization/PROGRESS.md#workspace-first-implementation--2026-10-04-in-progress).

## In-process VBA Quick Start

This mode runs the existing WebView2 panel inside Office without an RNAssistant
EXE, VSTO startup, COM registration or RegAsm.

The native-host panel is an owned top-level window. Its enabled-by-default
screen capture protection can be changed under Settings → Protection and is
applied immediately through `WDA_EXCLUDEFROMCAPTURE` or `WDA_NONE`. Capture
tools that honor the Windows display-affinity contract omit the assistant
window while leaving the Office window beneath it visible. Applying the
affinity is fail-open and any Win32 error is written to `logs\native-host.log`;
this is defense in depth, not DRM or protection from privileged capture
software.

Build and publish both x64 and x86 portable folders from a normal Command Prompt:

```cmd
build-local.cmd
```

Useful variants:

```cmd
build-local.cmd x64
build-local.cmd x86
build-local.cmd desktop
build-local.cmd all
build-local.cmd doctor
```

The command uses Visual Studio `MSBuild.exe` directly. It does not install or
register add-ins, change PowerShell policy, create certificates, access the
network or terminate Office. Outputs are written both to
`artifacts\portable\Release\x64` / `x86` and directly to
`C:\Temp\RNAssistant` / `C:\Temp\RNAssistant-x86`; the build log is
`artifacts\build-local.log`. Close Office before building because the native DLL
remains loaded in the Office process and cannot be replaced.

The x86 output includes the managed AnyCPU PdfPig reader and matching PE32 x86
PDFium/Skia native libraries from the same reviewed package versions as x64. The
publisher selects matching machine types; x64 native DLLs cannot be loaded by an
x86 Office process. Repository/package wiring is complete, but real Windows x86
Office import, preview, scanned-page rendering and model-send checks remain required
before x86 is called qualified. Package/import the VBA and Ribbon sources from
`wrappers\native`; see `wrappers\native\README.md`.

## Windows Desktop Quick Start

The standalone desktop mode remains available:

```cmd
install-desktop-local.cmd
```

This builds `RNAssistant.Desktop` and writes `RNASSISTANT_DESKTOP_EXE` to the
CurrentUser environment. The current `wrappers\native` modules target the
in-process DLL path; the desktop executable can be launched directly with the
arguments below.

The desktop shell accepts:

```cmd
RNAssistant.Desktop.exe --host Excel --target "{...json...}" --action summarize
RNAssistant.Desktop.exe --host Word --target-base64 eyJIb3N0IjoiV29yZCJ9
RNAssistant.Desktop.exe --host Excel --hwnd 123456 --action attach
```

It is single-instance: later wrapper clicks send activation to the existing
window through a named pipe and switch the active Office target.

If launched without arguments, the desktop shell can attach to the foreground
Office window as an MVP fallback. Desktop launcher logs remain under
`%LOCALAPPDATA%\OfficeAssistant\logs`; shared runtime logs are written to
`%APPDATA%\RNAssistant\logs\rnassistant.log`. Settings → Service can enable
pretty-printed raw model request/response logging in the runtime log; message
bodies may contain document data, while API keys and HTTP header values are
never logged.

The desktop shell includes a target picker. `Manual` mode keeps the chosen
working document locked even if the user switches Office windows. `Auto follow`
switches the working target from launcher activation. The picker stores only
lightweight target descriptors and resolves live COM objects on demand.

Desktop runtime and target-selection contract: [`docs/desktop-runtime.md`](docs/desktop-runtime.md).

## VSTO Quick Start

VSTO add-ins remain available for compatibility and debugging.

From a clean checkout on Windows:

```cmd
install-local.cmd
```

This creates a CurrentUser ClickOnce certificate, trusts it for the current user, builds all four `Debug | x64` VSTO add-ins, and registers them under `HKCU\Software\Microsoft\Office\...\Addins`. Restart Office apps after it finishes.

Useful variants:

```cmd
install-local.cmd Word Excel
install-local.cmd -Configuration Release
install-local.cmd -NoBuild
uninstall-local.cmd
```

Prerequisites are still required: Visual Studio 2022 with the Office/SharePoint development workload, .NET Framework 4.8 targeting pack, VSTO runtime, and x64 Office.

## Visual Studio Build

1. Open `RNAssistant.sln` in Visual Studio 2022.
2. Select `Debug | x64`.
3. Restore NuGet packages from local `packages` folder if VS asks.
4. Build one add-in project at a time.
5. Start the selected Office host from Visual Studio.

The add-in projects use the VSTO project flavor (`ProjectTypeGuids`) so Visual Studio shows Office/VSTO icons and enables the VSTO property pages. If Visual Studio says the projects are incompatible, install or enable the `Office/SharePoint development` workload and the `Visual Studio Tools for Office` component in Visual Studio Installer.

## Versioning

Commit is not a release. The completed architecture migration is the working
baseline; ordinary bug fixes and scoped development use `main` by default without a
new task branch each time. Historical release baseline is `v16.0.4`; current product
metadata is `16.1.0-dev`, identified by commit SHA. The maintainer considers the
system usable for continued development while reliability defects and Windows/Office
qualification remain open. This working status creates no release tag and does not
claim a signed stable/beta/RC build. See the current [status](docs/stabilization/PROGRESS.md)
and [development rules](docs/development-rules.md); the former
[stabilization master plan](docs/stabilization/STABILIZATION_MASTER_PLAN.md) is retained
as migration history.

The product version comes from `RNAssistantVersionPrefix` and `RNAssistantVersionSuffix` in `Directory.Build.props`. Build identity adds the commit SHA without changing that product version. Assembly compatibility, numeric file/application versions and protocol versions are separate; see [VERSIONING.md](docs/operations/VERSIONING.md). Every ordinary build validates format and derived metadata, without comparing against `HEAD` or requiring a clean tree. The same check runs without compiling:

```powershell
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
```

Product version changes and annotated tags belong only to explicit release
milestones. Release checks are separate from ordinary builds; the explicit release
workflow is [RELEASE_PROCESS.md](docs/operations/RELEASE_PROCESS.md). Never move or
reuse a tag, and never tag an ordinary commit. Internal refactoring does not justify
a major bump. User-visible changes go into [CHANGELOG.md](CHANGELOG.md); current
priority or qualification changes go into `PROGRESS.md`.

## Visual Studio Debug

1. Run `install-local.cmd Excel` once, replacing `Excel` with the host you want to debug.
2. Open `RNAssistant.sln`.
3. Select `Debug | x64`.
4. Keep the shared `Excel Add-in` launch profile, or set another `RNAssistant.*AddIn` project as startup when debugging a different host.
5. Press `F5`.

`RNAssistant.ExcelAddIn` is first in the solution and is the default shared launch profile. The VSTO project metadata points Visual Studio to the Office host executable through the Office 16.0 registry install path. If F5 says the required Office app is not installed, check that Office is x64 and installed locally, then reload the project in Visual Studio.

ClickOnce/VSTO manifest signing is disabled in the repository because certificate thumbprints are machine-local. If the Visual Studio Signing page is disabled, run the local helper in Windows PowerShell:

```powershell
.\tools\New-LocalClickOnceCertificate.ps1
```

Run the helper only when the organization permits local PowerShell scripts. It
does not change execution policy; if policy blocks it, certificate provisioning
must be handled by administrators.

The script creates a CurrentUser code-signing certificate and writes ignored `Directory.Build.local.props` with `SignManifests=true` and `ManifestCertificateThumbprint`.
By default it also imports the public certificate to CurrentUser `Root` and `TrustedPublisher`, so local signed manifests are trusted without recreating the VSTO projects.

If the Signing page is unavailable, unload the project and add a local line manually:

```xml
<SignManifests>true</SignManifests>
<ManifestCertificateThumbprint>YourCertificateThumbprint</ManifestCertificateThumbprint>
```

The add-ins copy `web/**` to output and load `web/index.html` inside a WinForms `WebView2` hosted by a VSTO custom task pane.
Pinned offline browser assets and their licenses are governed by [`web/vendor-manifest.json`](web/vendor-manifest.json) and [`web/vendor-notices.md`](web/vendor-notices.md).

## WebView2 Runtime

The code first checks:

`<add-in output>\vendor\webview2-runtime\...\msedgewebview2.exe`

If found, WebView2 uses that fixed runtime. If not found, it falls back to the installed Evergreen runtime.

Download the official x64 Fixed Version runtime from Microsoft Edge WebView2 page and unpack it into:

`vendor/webview2-runtime/<version>/`

Do not unpack through File Explorer if the archive structure is wrong; Microsoft recommends command-line `expand` or a normal archive tool.

## Settings and Data

Runtime data is stored under:

`%AppData%\RNAssistant`

- `settings.json` - API base URL, model, headers, token limits, safety settings and editable prompts.
- `secret.bin` - API key protected with DPAPI CurrentUser.
- `tools` - central editable executable tool library.
- `skills` - markdown guidance files used by the agent when choosing an approach.
- `chats` - per-document append-only `*.events.jsonl` session streams and active-chat pointers.
- `chat-blobs` - shared SHA-256-addressed immutable model payloads, artifact bodies, committed attachments and VBA source snapshots.
- `vba-journals` - per-document append-only VBA mutation streams; backup lists are replayed from these records.
- `attachments` - temporary attachment staging before content is committed to `chat-blobs`.

Settings has `Clear Chats/Data` for development resets. It clears chat/VBA event streams, CAS blobs, attachment staging, chat context and WebView user data, while keeping settings, saved API key and custom tools and skills.
The reset is rejected while any RNAssistant window owns an active chat operation.

Diagnostics opens at **Ход работы**, which keeps up to 500 short messages from the
current session and offers a jump to the latest event. **История чата** shows saved
steps in order; search, raw events and the VBA recovery journal are available under
technical details. Current code differences belong in the VBA tab. Large event
payloads stay in local CAS and load only when opened. **Проверки и данные** has
manual model connection and CAS controls. There is no Qualification Center,
background polling or separate Office test helper.

For an explicit factory reset, close all Office/RNAssistant processes and run `reset-local-data.cmd`. It validates and deletes only `%AppData%\RNAssistant`; pass `-Force` to skip the typed confirmation. This also removes settings, the DPAPI API key, custom tools/skills and runtime logs. It does not modify document-local VBA modules or RNAssistant properties already saved inside Office documents.

Word, Excel and PowerPoint use an existing `RNAssistantDocumentId` property when one was already persisted; otherwise saved files use their full path and unsaved files use the live COM identity. Identity lookup never dirties a document. When a path/key changes while Office is open, chat history and the VBA journal migrate to the live document identity; the journal records this as an append-only identity event.

## Tool Protocol

This section is a user-facing overview, not a second normative protocol. The exact
current contracts are in
[`docs/conversation-protocol.md`](docs/conversation-protocol.md),
[`docs/resource-fabric.md`](docs/resource-fabric.md) and
[`docs/tool-library.md`](docs/tool-library.md).

The API uses OpenAI-compatible chat completions at `/v1/chat/completions`.
Endpoint-specific behavior is documented in
[`docs/model-endpoint-compatibility.md`](docs/model-endpoint-compatibility.md).

Each chat stores an explicit execution mode:

- `Chat` can answer and read resources but cannot mutate Office or shared state.
- `Plan` performs read-only discovery and maintains a revisioned Markdown plan.
- `Agent` is the default and may run policy-approved local tools with confirmation.

Paste, drag-and-drop and the paperclip use one chat-scoped staging path. Sending a
message commits the bytes and exact resource reference before the model request.
Existing artifacts remain reference-first and are read explicitly when needed.

The model returns conversation-response v5. A tool turn contains one or more calls:

```json
{
  "message": "Read the table before editing.",
  "final": false,
  "tool_calls": [
    {
      "name": "excel.read_range",
      "arguments": { "address": "A1:D20" }
    }
  ]
}
```

Independent local read-only calls may share an array. Writes, external effects and
confirmation-required calls are singleton. The model supplies only `name` and
`arguments`; runtime owns call IDs, document binding, revisions, cursors, guards,
confirmation and effect evidence.

Only `final:true` with an empty `tool_calls` array ends the model loop; `final:false`
with empty calls is a bounded checkpoint. Neither final intent nor message wording
proves that a document changed. Tool results use `ok`, `error` or `unknown`; a
possible unverified external effect is never retried automatically. Office tools
execute locally and remain subject to exact schemas, policy, confirmation, bounds
and read-back.

Accepted history, confirmation and cumulative limits survive restart. Interrupted
effects recover conservatively without replay. The exact v5 envelope and retry
rules are documented in the
[v5 contract](docs/protocols/CONVERSATION_RESPONSE_V5.md) and
[conversation protocol](docs/conversation-protocol.md).

## HTML Workspace

The HTML tab shows the selected chat's workspace; its authored revisions belong to the bound document.
There is no separate HTML mode: Agent chooses the workspace from the request and available tools when a visual artifact materially improves the result.
Agent mode and document-independent local tools remain usable when that chat's Office document is closed. Office reads, writes, VBA actions, and Office-backed HTML bindings become available again only after the bound document is opened.

- Use `common.html_workspace_write_file` with `path` and complete `content` for HTML, CSS, and classic JavaScript files; runtime infers the kind and runs bounded static preflight automatically.
- Use `common.html_data_write` with `name` and exact JSON text for static data sources opened by page code through `RN.resources.open(name)`.
- Use `common.resources_find` and `common.resources_read` with their semantic targets to discover or inspect current HTML members; URIs, revisions, cursors, and guards remain runtime-owned.
- Use `common.html_workspace_apply_patch` for atomic ordered exact replace/insert edits to one current file; ambiguous anchors are rejected.
- Use `common.html_data_bind` with a semantic target returned by resource discovery. The runtime owns the canonical resource reference, view, and exact/head binding policy.
- Use `common.html_data_refresh` to resolve current head-bound sources and `common.html_data_freeze` to pin one binding to an exact revision.
- Use `common.html_workspace_delete` with the exact file path or data-source name. Deletions remain recoverable through workspace history.
- Reuse local JS/CSS/HTML from `%AppData%\RNAssistant\assets\<id>\<version>\manifest.json`: `common.html_assets_list` discovers packages on demand, `common.html_assets_import` copies a listed package into the current workspace, and `common.html_assets_publish` saves selected workspace files as a new package version. See the [asset contract and manifest example](docs/artifact-library.md#reusable-local-html-assets--2026-10-02).
- The runtime selects the displayed HTML entry when files are written or restored; there is no model-facing set-active or inspect tool.
- Every workspace mutation also records an immutable chat artifact revision. Full revision bodies are addressed by SHA-256 in the shared CAS; editing or forking from an older message activates the exact existing revision instead of duplicating it.
- Undo/redo history is bounded by item count and stored content size. UI responses carry only snapshot ids/labels/timestamps; Agent reads return a manifest or one targeted current item, never history bodies.
Workspace write/patch/delete resolves and validates current state internally; a separate read is needed only when the model must inspect existing content first.
HTML preview and its scripts are always enabled inside a sandboxed iframe. Pages consume exact named data handles through `RN.resources`; no eager data globals are exposed. The UI can export the assembled page, current JSON, CSS, and JavaScript as one offline HTML file.
The active HTML file is the entry page. Preview injects all workspace CSS into its head and all classic JavaScript before its closing body in workspace order; local `link`/`script src` references and ES module imports are not the workspace composition mechanism.

## Tool Library

Custom tools are stored under:

`%AppData%\RNAssistant\tools`

Each tool is a folder with editable files:

```text
tools/<host>/<tool-name>/
  tool.json
  src/
    EntryModule.bas
    SupportingClass.cls
  README.md
```

Tool package text files are strict UTF-8. `tool.json` contains metadata shown to the LLM and the task pane; duplicate JSON properties invalidate it. VBA packages keep each standard/class component in `src/*.bas` or `src/*.cls`; their complete contract is documented in `docs/vba-tool-packages.md`.
Tools marked `requiresConfirmation` require manual Run or the `Auto-confirm tool actions` setting.
Tool and skill updates are written per item and atomically; unrelated hosts, unrecognized entries, and additional user files are not removed.

Pipelines are disabled and deferred until a separate Phase 11 decision after stable core. Old definitions are skipped without migration or replay; their files are not automatically deleted. The pipeline executor, parser, authoring fields and UI editor have been removed. Custom tools currently use VBA only.

The Tools tab can run a selected tool with ad hoc JSON arguments. `Dry Run` validates/previews supported tools without changing the Office document. `Run` is treated as explicit user confirmation.

For Excel, Word, and PowerPoint, `executor: "vba"` uses a strict comment manifest and a `Public Function ... As String` entry point with typed positional arguments. A global package is injected for one run and cleaned in `finally`; explicit persistent installation is allowed only in macro-enabled documents. RNAssistant also discovers valid document-local tools through the VBA project object model. Both paths require Trust Access to the VBA project object model.

Agent does not inject the whole dynamic registry. Its finite mode/host core contains resource/capability bootstrap schemas and, on Excel, the complete built-in Excel/VBA pack; the runtime context also carries the complete compact list of exact tool and skill ids. Search returns schema-free metadata only when needed. The unified reader returns one exact revisioned descriptor or skill body. Optional tool-schema reads from one response form one prospective extension: runtime checks the complete next request plus bounded repair overhead, appends an accepted/rejected typed event, and only then publishes the whole batch under a new revision. Overflow publishes none and keeps the previous pack; calls never update recency and no schema is evicted inside the logical turn. Strict `json_schema` is generated only from the current callable pack. Confirmation continuation, compaction, and restart replay the exact accepted extension chain for the same `TurnId`; raw read results and rejected events never grant callable authority. Descriptor drift visibly falls back to the finite core until a fresh accepted extension rebases the chain.

Agent mode manages custom tool definitions through `common.tools_definition_read/validate/upsert/delete`; `tools_definition_read` without id lists compact custom metadata and never loads a callable schema. Upsert creates a missing id or preserves omitted fields while updating an existing one, then validates the effective definition automatically. Optional `createOnly`/`updateOnly` modes retain strict existence semantics; `tools_validate` is only a no-save preflight. In strict Agent output, `parameterDefinitions` provides compact native entries which runtime compiles to a canonical strict `parameters` object. Advanced callers may still pass `parameters` directly; VBA `components` remains a native array. None of these forms is an escaped JSON string. The supported schema dialect is closed to `type`, `description`, `properties`, `required`, `additionalProperties`, `items`, `anyOf`, `enum`, `const`, `default`, `minimum`, `maximum`, `minLength`, `maxLength`, `minItems`, and `maxItems`; unsupported assertion keywords are rejected instead of being advertised to the endpoint while ignored locally. A compact model-facing descriptor over 24,000 characters is omitted instead of partially advertised. Upsert/delete requires confirmation unless auto-confirm is enabled. Built-in, controller, and private backend ids are reserved; a stored collision remains on disk for manual recovery but is omitted from the runnable catalog. The catalog refreshes after confirmation or on the next user run.

## Skill Library

Markdown skills are stored under:

`%AppData%\RNAssistant\skills`

Each custom skill is a concise UTF-8 `SKILL.md` guidance file with front matter (`id`, `host`, `name`, `description`, `version`, `enabled`) and Markdown instructions. Optional detailed UTF-8 Markdown references live directly under the same package's `references/` directory and can be created, edited, or deleted in the Skill Library. A package may contain at most 64 direct `.md` references; malformed UTF-8/front matter, unreadable or reparse-point content, case-colliding names, and an over-limit reference set make the whole package unavailable instead of exposing a partial revision. Every enabled visible skill contributes its exact id, `kind:"skill"`, name, summary, package `revision`, `bodyChars`, and `referenceCount` to `RUNTIME_CONTEXT.capabilities.items`; the revision covers the core body and reference manifest. There is no skill router, activation state, dependency graph, or hidden tool ownership. The model calls `common.capabilities_read` with an exact id for each clearly relevant catalog entry. Agent authoring remains `common.skills_upsert/delete`; `skills_upsert` uses `referencePath` plus `referenceMarkdown` for a reference-only mutation, and `skills_delete` uses `referencePath` to delete one reference. Core and reference mutations are separate calls. Upsert/delete requires confirmation unless auto-confirm is enabled.

```markdown
---
id: excel.monthly_report
host: Excel
name: Monthly report
description: Build a consistent monthly report.
version: 1.0.0
enabled: true
---

# Monthly report

- Inspect the source range first.
- Preserve the requested column order.
```

At runtime the catalog entry is `{"id","kind":"skill","name","summary","revision","bodyChars","referenceCount"}`. The core `common.capabilities_read` result returns `kind:"skill"`, the same package revision, metadata, `format:"markdown"`, the complete `bodyMarkdown`, and explicit `loaded:true`, `complete:true`, `truncated:false`. Generic context bounding removes this loaded marker and returns top-level `data.truncated:true`, so an oversized result cannot be mistaken for a loaded skill. Compaction or a changed revision requires another core read.

The core read lists reference paths, sizes, and independent revisions without loading their text. Read a needed file by passing its exact `referencePath`; `offset` and `maxChars` page it with `nextOffset`. Reference chunks never replace the core loaded-state evidence. Keep `SKILL.md` below roughly 500 lines and move only detailed, selectively useful material into direct `references/*.md` files.

## VBA Workflow

Office VBA support requires Office setting `Trust access to the VBA project object model`.

- Settings has request timeout seconds; increase it for slow local or proxy LLM endpoints.
- Excel, Word, and PowerPoint expose VBA projects, components, bounded pageable source, literal search, and rollback backups through provider `vba` and `common.resources_list/resolve/search/read`. Public `common.vba_*` tools are mutation-only: write/rename, exact patch, delete, and restore.
- `Preview Diff` shows the current editor changes before saving.
- `Save Module` replaces the selected module only after a document-scoped prepared mutation and CAS-backed rollback snapshot are durable under `%AppData%\RNAssistant\vba-journals` and `chat-blobs`.
- `Restore Backup` is itself a confirmed journaled mutation; restoring snapshots the current module first and verifies read-back.
- Existing-module writes fail closed when a rollback backup cannot be created. A failed code write restores the original module when Office still permits access.
- VBA writes retain a CAS-backed rollback snapshot, strict live-code snapshot, ownership, stale-state, and post-write read-back checks inside the VBA tools. A mutation reads and binds the current VBIDE state itself, then rechecks it after confirmation; the model neither performs a preparatory read nor supplies a hash argument. If the model already inspected the module, runtime uses that snapshot automatically for one stale warning and then allows an intentional retry. Post-write verification accepts only VBE-equivalent case/spacing/terminal-line normalization and returns the actual read-back hash. If runtime stops after `mutation.prepared`, the next safe VBA access compares live state with both recorded sides and closes it without replaying the effect.
- `Review in Chat` sends loaded VBA modules to chat for review and improvement suggestions.

Agent uses the same provider `vba` in Excel, Word, and PowerPoint. It discovers `vba-project`, `vba-component`, and `vba-backup` metadata, then reads only needed source chunks by canonical URI; live reads are serialized with document mutations and return content-hash revision evidence. `common.vba_write_module` has strict write and rename schema branches: write requires complete `code`; rename requires `moduleName`, `newModuleName`, and `mode=rename` and rejects write-only fields. Rename guards and journals both names, preserves the component source/type, and never uses write+delete. It does not rewrite explicit textual references to the old module name. Mutations read and bind current state internally, require confirmation unless auto-confirm is enabled, and reject races or mismatched read-back. Removed VBA list/read/search/create/replace-text/read-lines ids are unsupported; host-prefixed whole-module, rename, and macro backends remain hidden.

Patch operations support:

```json
[
  { "op": "replace", "find": "exact current unique block", "text": "exact replacement block" },
  { "op": "replace", "find": "exact anchor", "text": "exact anchor\nnew code" }
]
```

VBA patching has no line-number or fuzzy mode. Every exact hunk is applied to one current full-module snapshot in memory; missing or ambiguous source fails without writing. Strings and boundary newlines are preserved, with only LF/CRLF converted to the module's current style.

## Tool Usage

In chat, ask for the desired Office action in normal language. For example:

`Создай новый лист Sales Demo, сгенерируй таблицу продаж по месяцам и построй линейный график.`

The model returns one JSON response per turn. Independent tools such as separate reads may be returned together and execute sequentially after schema, safety, and confirmation checks. Result-dependent operations remain separate model turns. Every result is returned to the model as JSON so it can choose the next action.

An empty `tool_calls` array ends the model loop; it does not certify applied
changes. The runtime separately projects lifecycle and effect health from actual
tool evidence. Errors and uncertain effects remain visible above the model answer
even with a collapsed trace; a no-write answer does not claim confirmed changes.
See the [runtime completion and effect projection](docs/conversation-protocol.md#effect-mapping-and-ui-projection).

Use the Tools tab to create or edit reusable tools:

- `New Tool` creates an editable VBA tool draft.
- `VBA components` edits the `.bas`/`.cls` sources of an `executor: "vba"` package.
- `Dry Run` previews execution without changing the document.
- `Run` executes the selected tool and counts as explicit user confirmation.
- `Edit in Chat` sends the selected tool definition and code to the LLM for improvement.
