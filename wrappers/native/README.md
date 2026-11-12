# RNAssistant in-process Office wrappers

These VBA modules call `RNAssistant.NativeHostCli.dll` directly. The DLL loads the
existing .NET Framework 4.8 WebView2 panel into the current Office process. This
path uses no RNAssistant EXE, VSTO startup, COM registration, RegAsm, HKLM or HKCU
installation.

## Bitness

Office and native DLLs must match:

- Office x64: build `Debug|x64` or `Release|x64`, use the x64 WebView2Loader.
- Office x86: build the native project as Win32 and use the x86 WebView2Loader.
  The Win32 project uses `NativeHostCli.x86.def` to expose undecorated VBA names.

Do not copy an x86 native DLL into an x64 Office deployment or vice versa.

## Portable layout

From the repository root, build and publish both architectures:

```cmd
build-local.cmd
```

Use `build-local.cmd x64` or `build-local.cmd x86` for one architecture. Output
is written under `artifacts\portable\Release` and also published directly to
`C:\Temp\RNAssistant-x64` (x64) or `C:\Temp\RNAssistant-x86` (x86). The x86 package
contains the managed AnyCPU PdfPig reader plus matching PE32 x86 PDFium/Skia native
libraries. Project output keeps architecture subdirectories; the portable publisher
copies only the requested architecture's pair into `lib`, which is the
loader's matching fallback. Never copy the x64 pair into an x86 package. Exact
Windows x86 import, page preview, scanned-page rendering and image-capable model
sending remain qualification gates despite the complete package wiring. See the exact
[PDF bitness boundary](../../docs/dependencies.md#pdf-bitness-boundary).

Each publish replaces current product files and removes stale files recorded in
`.rnassistant-portable-files`. It preserves unrelated files and directories,
including user-created add-ins and `logs`. On the first migration from the old
flat layout, named product DLLs and worker files are removed from the root after
their new copies reach `lib`; unrelated root files remain. The existing
obsolete `panel-owner-mode.txt` is left untouched but is no longer read. Close Office before rebuilding
because loaded DLLs cannot be replaced.

Expected layout:

```text
artifacts\portable\Release\x64\
  .rnassistant-portable-output
  .rnassistant-portable-files
  RNAssistantExcel.xlam      # if a compiled add-in was supplied
  RNAssistantWord.dotm       # if supplied
  RNAssistantPowerPoint.ppam # if supplied
  lib\
    RNAssistant.NativeHostCli.dll
    RNAssistant.Core.dll
    RNAssistant.Office.dll
    RNAssistant.OfficeHosts.dll
    RNAssistant.JsWorker.exe
    RNAssistant.JsWorker.exe.config
    Microsoft.Office.Interop.*.dll
    Microsoft.Web.WebView2.*.dll
    Newtonsoft.Json.dll
    UglyToad.PdfPig*.dll
    PDFtoImage.dll
    SkiaSharp.dll
    pdfium.dll               # matching architecture
    libSkiaSharp.dll         # matching architecture
    WebView2Loader.dll       # matching architecture
  web\
  addins\sources\
  docs\
  logs\
```

The managed `InProcessWindowHost` owns the window and document session. The unchanged
Excel VBA launcher passes `Application.Hwnd`; OfficeHosts resolves the active workbook
window. A caller may also pass `ActiveWindow.Hwnd`. The form has no separate
taskbar entry. Pin prevents automatic collapse without TopMost. The former owner-mode
file and environment override are no longer used.

Closing the window (× / Alt+F4), `Host_ClosePanel`, and `Host_SetPanelVisible(0)`
end the session and cancel active runs. Opening from another workbook asks the Web UI
to check editors and persist drafts before closing the old exact session and creating
a new one for the new HWND. Compact and the native companion share one WebView only
within the current session. Windows/Office visual qualification remains open;
see [window review](../../docs/ui/UI_MIGRATION_PLAN.md#native-window--in-review-2026-11-11).

## Native API

`RNAssistant.NativeHostCli.dll` exports undecorated `__stdcall` functions:

```text
Host_ShowPanel(HWND, rootPath)
Host_ShowPanelEx(HWND, rootPath, hostKind)
Host_ClosePanel()
Host_SetPanelVisible(visible)
Host_GetLastErrorMessage(buffer, bufferChars)
```

Host kinds are Excel=1, Word=2, PowerPoint=3 and Outlook=4. Lifecycle calls
return zero on success. `Host_GetLastErrorMessage` returns copied UTF-16
characters excluding the null terminator; call it with a null/zero buffer to get
the required size including the terminator.

## Add-in sources

- Excel: import `excel/RNAssistantExcel.bas` into `RNAssistantExcel.xlam`.
- Word: import `word/RNAssistantWord.bas` into `RNAssistantWord.dotm`.
- PowerPoint: import `powerpoint/RNAssistantPowerPoint.bas` into
  `RNAssistantPowerPoint.ppam`. Keep this exact file name because the VBA module
  uses the PowerPoint `AddIns` collection to resolve the portable root.
- Add the matching `ribbon/<host>/customUI14.xml` with an Office Ribbon editor.
- Outlook 2013: follow `Outlook2013_Setup.md`.

Place finished `.xlam`, `.dotm` and `.ppam` files in
`wrappers\native\binaries\` before running `build-local.cmd`; the publisher
copies them to the root of each architecture package. They are Office-authored
artifacts and are not generated from `.bas`/Ribbon XML by MSBuild. Outlook uses
the user's `VbaProject.OTM` rather than a distributable add-in file.

For Excel, Word and PowerPoint the binary add-in normally lives under
the package root; the VBA code also accepts the historical `addins` subfolder
and resolves its parent. It loads the native DLL from `lib`.

The DLL remains loaded and locked until the owning Office process exits. Close
Office before rebuilding or republishing. The publish script deliberately does
not terminate Office processes.

## Runtime limitations

The window is an owned WinForms tool window, not a real CustomTaskPane; Office
does not reserve layout space. WebView2 Runtime still creates Microsoft runtime
child processes.

COM attachment starts from the active Office object. Excel additionally resolves
the application by HWND. With multiple Word, PowerPoint, or Outlook instances,
the provider rejects a detectable HWND mismatch, but Outlook exposes less
reliable window identity. Passing a COM object from VBA or resolving it through
Accessibility is a future hardening step.

Corporate macro policy must permit the VBA project. Use a signed project and/or
an approved Trusted Location where required.
