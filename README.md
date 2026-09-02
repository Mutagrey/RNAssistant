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
- `src/RNAssistant.Office` - shared WebView2 task pane, JS bridge, ribbon XML and assistant controller.
- `src/RNAssistant.OfficeHosts` - shared Excel/Word/PowerPoint/Outlook COM adapters.
- `src/RNAssistant.NativeHostCli` - C++/CLI in-process DLL host for VBA.
- `src/RNAssistant.Desktop` - standalone WinForms/WebView2 desktop shell.
- `src/RNAssistant.*AddIn` - VSTO compatibility add-ins and ribbon/task pane wiring.
- `wrappers/native` - VBA source modules for Office-native launcher wrappers.
- `web` - static local task pane UI, no npm build.
- `packages` - vendored NuGet packages for offline restore.
- `vendor/pdf-rendering` - vendored PDFtoImage/PDFium/SkiaSharp binaries for Windows x64.
- `vendor/webview2-runtime` - optional fixed WebView2 x64 runtime folder.

The documentation entry point and placement rules are in
[`docs/README.md`](docs/README.md). Canonical engineering rules are in
[`docs/development-rules.md`](docs/development-rules.md); current architecture and
ownership are in [`docs/architecture.md`](docs/architecture.md).
`AGENTS.md` adds the active stabilization and environment-specific instructions;
current phase and gates are tracked in
[`docs/stabilization/PROGRESS.md`](docs/stabilization/PROGRESS.md).

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

The x86 output still includes the managed PdfPig reader used for PDF text and page
metadata extraction. Those assemblies are AnyCPU; this path is structurally x86-
compatible, although the exact Windows x86 runtime check remains open. The package
intentionally omits the x64-only native `pdfium.dll` and `libSkiaSharp.dll`, so PDF
page-to-image rendering, including scanned-page input for a vision model, is not
available in x86. The managed `PDFtoImage.dll`/`SkiaSharp.dll` facades do not remove
that native bitness requirement. The complete supported target remains Office x64.
Package/import the VBA and Ribbon sources from `wrappers\native`; see
`wrappers\native\README.md`.

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

Commit is not a release. The stabilization branch is `stabilization/16.1`, with historical baseline `v16.0.4` and development target `16.1.0-dev`. Ordinary commits neither change the product version nor create Git tags. New product features are frozen; follow the current phase in the mandatory [stabilization master plan](docs/stabilization/STABILIZATION_MASTER_PLAN.md) and [progress record](docs/stabilization/PROGRESS.md).

The product version comes from `RNAssistantVersionPrefix` and `RNAssistantVersionSuffix` in `Directory.Build.props`. Build identity adds the commit SHA without changing that product version. Assembly compatibility, numeric file/application versions and protocol versions are separate; see [VERSIONING.md](docs/operations/VERSIONING.md). Every ordinary build validates format and derived metadata, without comparing against `HEAD` or requiring a clean tree. The same check runs without compiling:

```powershell
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
```

Product version changes and annotated tags belong only to qualified release milestones. Release checks are separate from ordinary builds; the explicit release workflow is [RELEASE_PROCESS.md](docs/operations/RELEASE_PROCESS.md). Never move or reuse a tag, and never tag an ordinary commit. Internal refactoring does not justify a major bump. User-visible changes go into [CHANGELOG.md](CHANGELOG.md); internal stabilization work goes into `PROGRESS.md`.

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

Diagnostics shows passive timing for real model requests (local preparation, HTTP headers, first response data and total duration), offers one manual short model check, and exposes the current chat trajectory. The trajectory lists the last 500 canonical events with run/turn/step correlation. Large request, response, and bounded streaming-frame payloads remain in local CAS and are loaded as previews only on demand. Diagnostics does not poll the endpoint in the background.

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

The model returns conversation-response v4. A tool turn contains one or more calls:

```json
{
  "message": "Read the table before editing.",
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

An empty `tool_calls` array ends the model loop but does not prove that a document
changed. Tool results use `ok`, `error` or `unknown`; a possible unverified external
effect is never retried automatically. Office tools execute locally and remain
subject to exact schemas, policy, confirmation, bounds and read-back.

Accepted history, confirmation and cumulative limits survive restart. Interrupted
effects recover conservatively without replay. The exact v4 envelope and retry
rules are documented in the
[v4 contract](docs/protocols/CONVERSATION_RESPONSE_V4.md) and
[conversation protocol](docs/conversation-protocol.md).

## HTML Workspace

The HTML tab is tied to the active chat session. Agent-created HTML pages are stored with the chat, not inside the Office document.
There is no separate HTML mode: Agent chooses the workspace from the request and available tools when a visual artifact materially improves the result.
Agent mode and document-independent local tools remain usable when that chat's Office document is closed. Office reads, writes, VBA actions, and Office-backed HTML bindings become available again only after the bound document is opened.

- Use `common.html_workspace_upsert` with `resourceType:"file"` for `index.html`, CSS, and scripts; runtime infers file kind from the extension. Default `mode:"upsert"` creates or updates, while `createOnly` and `updateOnly` enforce strict existence.
- Use the same tool with `resourceType:"data"` for JSON data sources exposed as `window.RNAssistantData`.
- List `provider:"chat"` with exact kind `html-file` or `html-data`, then use `common.resources_search` for bounded literal discovery across current HTML members.
- Use `common.html_workspace_inspect` after material edits for bounded static preflight checks across the selected entry, injected CSS/scripts, and data references. It reports CSP/assembly conflicts and likely missing references but does not execute JavaScript or render WebView.
- Use `common.html_workspace_apply_patch` for atomic ordered edits to one current file. Exact replace/insert operations reject ambiguous anchors; line and bounded regex replacements are also supported.
- Use `common.html_data_bind` to create a refreshable data source from an approved read-only Office tool. `sourceArguments` accepts only fields from the selected source schema; for `excel.read_range` those are `sheet`, `address`, and `content`—never `kind`. The binding stores exact source arguments and can keep raw JSON or normalize row arrays to `{columns, rows, rowCount}`.
- Use `common.html_data_refresh` to update one or all bindings locally without another LLM request. `refreshPolicy:"on_preview"` is refreshed by the Artifacts UI; `common.html_data_freeze` keeps the current JSON and removes the binding.
- Use `common.html_workspace_delete` with `resourceType` and `name` to remove an item. Deletions are recorded in workspace history and can be undone.
- Read the `activeHtml` canonical URI with `representation:"structure"` for the compact manifest. Read an exact listed member URI with `source` or `text`; large bodies continue through `nextCursor`. Use `common.html_workspace_set_active` to choose the displayed HTML file.
- Every workspace mutation also records an immutable chat artifact revision. Full revision bodies are addressed by SHA-256 in the shared CAS; editing or forking from an older message activates the exact existing revision instead of duplicating it.
- Undo/redo history is bounded by item count and stored content size. UI responses carry only snapshot ids/labels/timestamps; Agent reads return a manifest or one targeted current item, never history bodies.
Workspace upsert/patch/delete resolve and validate current state internally; a separate read is needed only when the model must inspect existing content first.
HTML preview and its scripts are always enabled inside a sandboxed iframe. Pages can use `window.RNAssistantData`, `window.RNAssistantDataMeta`, or `window.RNAssistant.data`. The UI can export the assembled page, current JSON, CSS, and JavaScript as one offline HTML file.
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
