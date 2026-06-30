# Vendored Dependencies

NuGet packages are committed in `packages/`:

- `Microsoft.Web.WebView2 1.0.2903.40`
- `Newtonsoft.Json 13.0.3`
- `PdfPig 0.1.15` and its managed dependencies

PDF rendering dependencies are committed as selected binaries in
`vendor/pdf-rendering/` and referenced directly by `RNAssistant.Office`:

- `PDFtoImage 5.2.1`
- `SkiaSharp 3.119.2`
- `bblanchon.PDFium.Win32 147.0.7690`

Only Windows x64 native binaries are included because the supported target is
Office x64. Building and running PDF page rendering does not require restoring
these NuGet packages.

Task pane JS/CSS is committed in `web/`:

- `marked 12.0.2`
- `DOMPurify 3.1.6`
- `highlight.js 11.9.0`

The fixed WebView2 runtime is intentionally not expanded here because it is over 250 MB. The code supports it from `vendor/webview2-runtime/<version>/` and falls back to Evergreen runtime when absent.

The in-process VBA host additionally requires these Visual Studio 2022
components at build time:

- Desktop development with C++;
- C++/CLI support for the v143 build tools;
- .NET Framework 4.8 targeting pack;
- Windows 10/11 SDK.

These components are build-time dependencies only. The portable deployment uses
the matching x64/x86 VC runtime, .NET Framework 4.8, Office PIAs and WebView2
Runtime already present or deployed by corporate policy.
