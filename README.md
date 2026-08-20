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

Development rules are in `AGENTS.md`. Architecture boundaries and refactoring targets are in `docs/architecture.md`; review findings and roadmap are in `docs/review-roadmap.md`.

## In-process VBA Quick Start

This mode runs the existing WebView2 panel inside Office without an RNAssistant
EXE, VSTO startup, COM registration or RegAsm.

1. Build `RNAssistant.NativeHostCli`, `RNAssistant.Core`, `RNAssistant.Office`
   and `RNAssistant.OfficeHosts` in Visual Studio 2022 using the same bitness as
   Office.
2. Publish the portable folder:

```powershell
.\tools\Publish-NativePortable.ps1 -Configuration Release -Architecture x64 -Destination C:\Temp\RNAssistant
```

3. Package/import the VBA and Ribbon sources from `wrappers\native`; see
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

Current architecture audit: `docs/desktop-architecture-audit.md`.

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

## Visual Studio Debug

1. Run `install-local.cmd Word` once, replacing `Word` with the host you want to debug.
2. Open `RNAssistant.sln`.
3. Select `Debug | x64`.
4. Right-click `RNAssistant.WordAddIn`, `RNAssistant.ExcelAddIn`, `RNAssistant.PowerPointAddIn`, or `RNAssistant.OutlookAddIn` and choose `Set as Startup Project`.
5. Press `F5`.

The VSTO project metadata points Visual Studio to the Office host executable through the Office 16.0 registry install path. If F5 says the required Office app is not installed, check that Office is x64 and installed locally, then reload the project in Visual Studio.

ClickOnce/VSTO manifest signing is disabled in the repository because certificate thumbprints are machine-local. If the Visual Studio Signing page is disabled, run the local helper in Windows PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\tools\New-LocalClickOnceCertificate.ps1
```

The script creates a CurrentUser code-signing certificate and writes ignored `Directory.Build.local.props` with `SignManifests=true` and `ManifestCertificateThumbprint`.
By default it also imports the public certificate to CurrentUser `Root` and `TrustedPublisher`, so local signed manifests are trusted without recreating the VSTO projects.

If the Signing page is unavailable, unload the project and add a local line manually:

```xml
<SignManifests>true</SignManifests>
<ManifestCertificateThumbprint>YourCertificateThumbprint</ManifestCertificateThumbprint>
```

The add-ins copy `web/**` to output and load `web/index.html` inside a WinForms `WebView2` hosted by a VSTO custom task pane.

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
- `chats` - per-document chat session folders; each chat stores the transcript, context checkpoints, attachments, and artifact revisions.

Settings has `Clear Chats/Data` for development resets. It clears chats, chat context, VBA backups and WebView user data, while keeping settings, saved API key and custom tools and skills.

Word, Excel and PowerPoint documents are identified by a custom document property named `RNAssistantDocumentId` when available, so chat sessions and context survive file rename/move. If the property cannot be read or written, RNAssistant falls back to the document path.

## Tool Protocol

The API is OpenAI-compatible chat completions: `/v1/chat/completions`.
Endpoint compatibility details are in `docs/model-endpoint-compatibility.md`; the Agent flow is in `docs/agent-protocol.md`.

Each chat stores an explicit execution mode:

- `Chat` sends a normal completion without tools, skills, agent JSON, or Office execution.
- `Agent` is the default and uses a direct model/tool loop. It can also answer ordinary questions without tools.

Editable Agent instructions use `developer` by default and may use `system` or `user`. The Prompts page edits the Agent prompt, Chat prompt, context-compaction prompt, and title prompt. Agent-side prompt changes use `common.prompts_read_defaults` and confirmed `common.prompts_save`.

In Agent mode the prompt contains all runnable tools in native-like function JSON and a compact catalog (`id`, `name`, `description`) of enabled skills. When a catalog description is relevant, the model loads that skill's complete versioned Markdown through `common.skills_read`. The model returns one raw JSON object. A tool turn contains one or more calls:

```json
{
  "message": "Read the table before editing.",
  "tool_calls": [
    {
      "id": "call_1",
      "name": "excel.read_range",
      "arguments": { "address": "A1:D20" }
    }
  ]
}
```

Independent calls may be placed in the same array and execute locally in order. Dependent calls and calls that may require confirmation are emitted one at a time. If confirmation pauses a multi-call response, calls after it are not executed; the model selects them again after the confirmed result. There is no persistent batch state.

To answer or clarify, the model returns `{"message":"...","tool_calls":[]}`. Agent mode always requests `json_object`; there are no response-mode fallbacks, native tool-call transport, plans, format repair, router, tool slicer, skill activation, automatic retries, or separate verification phase.

Office tools execute locally. The next model turn receives a string protocol message such as `TOOL_RESULT:\n{"ok":true,"tool_call_id":"call_1","name":"excel.read_range","status":"completed","message":"Range read.","data":{...},"error":null}`. The model decides what to do next. The runtime only enforces exact tool ids, formal argument schemas, safety/confirmation metadata, and iteration/tool-step limits.

Context compaction preserves the full stored transcript and replays a checkpoint plus an exact tail. Pipeline safety is resolved recursively, so nested mutation, risk, confirmation, missing-reference, and cycle errors cannot be hidden by top-level metadata.

## HTML Workspace

The HTML tab is tied to the active chat session. Agent-created HTML pages are stored with the chat, not inside the Office document.

- Use `common.html_workspace_upsert_file` for `index.html`, CSS, and script files (`kind`: `html`, `css`, or `script`).
- Use `common.html_workspace_upsert_data` for JSON data sources. Preview exposes them as `window.RNAssistantData`.
- Use `common.html_workspace_delete_file` and `common.html_workspace_delete_data` to remove workspace items. Deletions are recorded in workspace history and can be undone.
- Use `common.html_workspace_read` to inspect the current workspace and `common.html_workspace_set_active` to choose the displayed HTML file.
- Every workspace mutation also records an immutable chat artifact revision. Editing or forking from an older message restores/copies the exact revision at that point rather than the latest workspace.
Edits and deletions of an existing workspace require a successful workspace read in the current agent run.
HTML preview and its scripts are always enabled inside a sandboxed iframe.

## Tool Library

Custom tools are stored under:

`%AppData%\RNAssistant\tools`

Each tool is a folder with editable files:

```text
tools/<host>/<tool-name>/
  tool.json
  pipeline.json
  src/
    EntryModule.bas
    SupportingClass.cls
  README.md
```

`tool.json` contains metadata shown to the LLM and the task pane. `pipeline.json` can call existing built-in tools in sequence. VBA packages keep each standard/class component in `src/*.bas` or `src/*.cls`; their complete contract is documented in `docs/vba-tool-packages.md`.
Tools marked `requiresConfirmation` require manual Run or the `Auto-confirm tool actions` setting.
Tool and skill updates are written per item and atomically; unrelated hosts, unrecognized entries, and additional user files are not removed.

Pipeline tools use:

```json
{
  "version": 1,
  "steps": [
    {
      "id": "read",
      "toolId": "excel.read_range",
      "arguments": { "address": "{{args.address}}" }
    }
  ]
}
```

Each pipeline step must set `toolId`; step `id` values must be unique. `id` is only the step label used for placeholders. Supported placeholders are `{{args.name}}`, `{{steps.stepId.message}}`, `{{steps.stepId.dataJson}}`, and `{{steps.stepId.success}}`.

The Tools tab can run a selected tool with ad hoc JSON arguments. `Dry Run` resolves pipeline steps without changing the Office document. `Run` is treated as explicit user confirmation.

For Excel, Word, and PowerPoint, `executor: "vba"` uses a strict comment manifest and a `Public Function ... As String` entry point with typed positional arguments. A global package is injected for one run and cleaned in `finally`; explicit persistent installation is allowed only in macro-enabled documents. RNAssistant also discovers valid document-local tools through the VBA project object model. Both paths require Trust Access to the VBA project object model.

Agent mode can also use `common.tools_list`, `common.tools_read`, `common.tools_validate`, `common.tools_save`, and `common.tools_delete` to manage custom tools. Save/delete requires confirmation unless auto-confirm is enabled. Pipeline and VBA packages are validated before save. Built-in and controller tool ids are reserved and cannot be shadowed by custom tools. VBA authoring rules are available to the model through `common.vba_tool_authoring`.

## Skill Library

Markdown skills are stored under:

`%AppData%\RNAssistant\skills`

Each custom skill is a `SKILL.md` guidance file with simple metadata (`id`, `host`, `name`, `description`, `version`, `enabled`) and Markdown instructions. Every enabled visible skill contributes only `id`, `name`, and `description` to `RUNTIME_CONTEXT.skills`. There is no skill router, activation state, dependency graph, or hidden tool ownership. The model calls `common.skills_read` for each clearly relevant catalog entry before following its complete instructions. Agent mode can inspect and edit custom skills through `common.skills_list`, `common.skills_read`, `common.skills_save`, and `common.skills_delete`; save/delete requires confirmation unless auto-confirm is enabled.

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

At runtime the catalog entry is `{"id","name","description"}`. `common.skills_read` returns `{"id","name","description","version","format":"markdown","instructions":"..."}` in a normal `TOOL_RESULT.data` object. That result remains in conversation history; there is no separate activation state.

## VBA Workflow

Office VBA support requires Office setting `Trust access to the VBA project object model`.

- Settings has request timeout seconds; increase it for slow local or proxy LLM endpoints.
- Excel, Word, and PowerPoint can read VBA modules, show source code, and list RNAssistant rollback backups.
- `Preview Diff` shows the current editor changes before saving.
- `Save Module` replaces the selected module and stores the previous version under `%AppData%\RNAssistant\vba-backups`.
- `Restore Backup` restores the selected backup; restoring also backs up the current module first.
- Existing-module writes fail closed when a rollback backup cannot be created. A failed code write restores the original module when Office still permits access.
- VBA writes retain local backup, expected-hash, ownership, and stale-state checks inside the VBA tools.
- `Review in Chat` sends loaded VBA modules to chat for review and improvement suggestions.

The Agent can use read-only VBA inspection tools such as `word.vba_read_module` and `*.vba_list_modules`. Direct VBA mutations remain manual/confirmation-controlled and are not exposed as ordinary Agent calls.

Patch operations support:

```json
[
  { "op": "replace", "find": "old code", "text": "new code" },
  { "op": "insertAfter", "find": "anchor", "text": "\nnew code" },
  { "op": "replaceLines", "startLine": 10, "deleteCount": 2, "text": "new code" }
]
```

## Tool Usage

In chat, ask for the desired Office action in normal language. For example:

`Создай новый лист Sales Demo, сгенерируй таблицу продаж по месяцам и построй линейный график.`

The model returns one JSON response per turn. Independent tools such as separate reads may be returned together and execute sequentially after schema, safety, and confirmation checks. Result-dependent operations remain separate model turns. Every result is returned to the model as JSON so it can choose the next action.

Use the Tools tab to create or edit reusable tools:

- `New Tool` creates an editable custom tool.
- `Pipeline JSON` defines ordered calls to existing tools.
- `VBA components` edits the `.bas`/`.cls` sources of an `executor: "vba"` package.
- `Dry Run` previews execution without changing the document.
- `Run` executes the selected tool and counts as explicit user confirmation.
- `Edit in Chat` sends the selected tool definition and code to the LLM for improvement.
