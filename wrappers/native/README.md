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
`C:\Temp\RNAssistant` (x64) or `C:\Temp\RNAssistant-x86` (x86). The x86 package
contains the managed AnyCPU PdfPig text reader, but not the x64-only native PDF
page-rendering binaries. Thus text extraction/page metadata are the intended x86
ingestion path; scanned or visual PDF pages cannot be converted to images in x86.
The current request builder may enter that visual path when the selected model
supports images, even for a PDF whose text was extracted, so complete PDF sending is
not x86-qualified. Presence of the managed `PDFtoImage.dll` and `SkiaSharp.dll`
facades does not provide a matching native runtime. See the exact
[PDF bitness boundary](../../docs/dependencies.md#pdf-bitness-boundary).

Each publish replaces its known output directory as one exact package, so files
removed or renamed since an older build cannot remain in `artifacts\portable` or
`C:\Temp`. Custom destinations are cleaned only after the publisher has created
its `.rnassistant-portable-output` ownership marker; an existing unmarked custom
directory is rejected. Close Office before rebuilding because loaded DLLs can
prevent the old package from being removed.

Expected layout:

```text
artifacts\portable\Release\x64\
  .rnassistant-portable-output
  RNAssistant.NativeHostCli.dll
  RNAssistant.Core.dll
  RNAssistant.Office.dll
  RNAssistant.OfficeHosts.dll
  Microsoft.Office.Interop.*.dll
  Microsoft.Web.WebView2.Core.dll
  Microsoft.Web.WebView2.WinForms.dll
  Newtonsoft.Json.dll
  UglyToad.PdfPig*.dll
  PDFtoImage.dll
  SkiaSharp.dll
  pdfium.dll                 # x64 package
  libSkiaSharp.dll           # x64 package
  WebView2Loader.dll
  panel-owner-mode.txt
  web\
  addins\sources\
  docs\
  logs\
```

`panel-owner-mode.txt` accepts `OwnerWindow` (default), `None`, or
`TopMostDebug`. Environment variable `RNASSISTANT_PANEL_OWNER_MODE` overrides the
file.

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

For Excel, Word and PowerPoint the binary add-in normally lives under
`C:\Temp\RNAssistant\addins`; the VBA code checks that folder and its parent for
the native DLL.

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
