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

## Windows Build

1. Open `RNAssistant.sln` in Visual Studio 2022.
2. Select `Debug | x64`.
3. Restore NuGet packages from local `packages` folder if VS asks.
4. Build one add-in project at a time.
5. Start the selected Office host from Visual Studio.

The add-in projects use the VSTO project flavor (`ProjectTypeGuids`) so Visual Studio shows Office/VSTO icons and enables the VSTO property pages. If Visual Studio says the projects are incompatible, install or enable the `Office/SharePoint development` workload and the `Visual Studio Tools for Office` component in Visual Studio Installer.

ClickOnce/VSTO manifest signing is disabled in the repository because certificate thumbprints are machine-local. If the Visual Studio Signing page is disabled, run the local helper in Windows PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\tools\New-LocalClickOnceCertificate.ps1
```

The script creates a CurrentUser code-signing certificate and writes ignored `Directory.Build.local.props` with `SignManifests=true` and `ManifestCertificateThumbprint`.

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
- `skills.json` - custom editable skills.
- `chats` - per-document chat sessions.
- `contexts` - per-document context.

## Skill Protocol

The API is OpenAI-compatible chat completions: `/v1/chat/completions`.

Native tool calling is not required. The model is prompted to return local actions as:

````
```rnassistant-skill
{"skillId":"excel.read_range","arguments":{"address":"A1:D20"}}
```
````

The add-in parses these blocks and executes known local skills.
