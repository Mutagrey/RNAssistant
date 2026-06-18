# RNAssistant

VSTO AI assistant skeleton for Excel, Word, PowerPoint and Outlook.

## Target

- Windows 10
- Visual Studio Community 2022 with Office/SharePoint development workload
- Office x64
- .NET Framework 4.8
- C# 7.3
- No admin rights required for normal build/run

## Structure

- `src/RNAssistant.Core` - settings, DPAPI secret storage, chat/context stores, OpenAI-compatible chat client, skill parser.
- `src/RNAssistant.Office` - shared WebView2 task pane, JS bridge, ribbon XML and assistant controller.
- `src/RNAssistant.ExcelAddIn` - Excel VSTO add-in and built-in Excel skills.
- `src/RNAssistant.WordAddIn` - Word VSTO add-in and built-in Word skills.
- `src/RNAssistant.PowerPointAddIn` - PowerPoint VSTO add-in and built-in PowerPoint skills.
- `src/RNAssistant.OutlookAddIn` - Outlook VSTO add-in and built-in Outlook skills.
- `web` - static local task pane UI, no npm build.
- `packages` - vendored NuGet packages for offline restore.
- `vendor/webview2-runtime` - optional fixed WebView2 x64 runtime folder.

## Windows Quick Start

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

- `settings.json` - API base URL, model, headers, token limits, prompt.
- `secret.bin` - API key protected with DPAPI CurrentUser.
- `tools` - central editable tool library.
- `chats` - per-document chat sessions.
- `contexts` - per-document context.

## Tool Protocol

The API is OpenAI-compatible chat completions: `/v1/chat/completions`.

Native tool calling is not required. The model is prompted to return local actions as:

````
```rnassistant-skill
{"skillId":"excel.read_range","arguments":{"address":"A1:D20"}}
```
````

The add-in parses these blocks and executes known local tools when `Auto-run tool calls from LLM` is enabled.

## Tool Library

Custom tools are stored under:

`%AppData%\RNAssistant\tools`

Each tool is a folder with editable files:

```text
tools/<host>/<tool-name>/
  tool.json
  pipeline.json
  code.vba
  README.md
```

`tool.json` contains metadata shown to the LLM and the task pane. `pipeline.json` can call existing built-in tools in sequence. `code.vba` is kept as editable executor/source code for VBA-backed tools.
Tools marked `requiresConfirmation` require manual Run or the `Auto-confirm tool actions` setting.

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

Supported placeholders are `{{args.name}}`, `{{steps.stepId.message}}`, `{{steps.stepId.dataJson}}`, and `{{steps.stepId.success}}`.

The Tools tab can run a selected tool with ad hoc JSON arguments. `Dry Run` resolves the planned calls without changing the Office document. `Run` is treated as explicit user confirmation.

For Excel, `executor: "vba"` inserts `code.vba` through `excel.insert_vba_module`; if the run arguments include `macroName`, it then calls `excel.run_macro`.

## Tool Usage

In chat, ask for the desired Office action in normal language. For example:

`Создай новый лист Sales Demo, сгенерируй таблицу продаж по месяцам и построй линейный график.`

The model can respond with one `rnassistant-skill` block containing an ordered JSON array, for example `excel.add_sheet`, `excel.write_table`, and `excel.add_chart`. If `Auto-run tool calls from LLM` is enabled, the add-in executes those tools in order.

Use the Tools tab to create or edit reusable tools:

- `New Tool` creates an editable custom tool.
- `Pipeline JSON` defines ordered calls to existing tools.
- `VBA / executor code` stores executable VBA source for `executor: "vba"`.
- `Dry Run` previews execution without changing the document.
- `Run` executes the selected tool and counts as explicit user confirmation.
- `Edit in Chat` sends the selected tool definition and code to the LLM for improvement.
