# Vendored PDF rendering

These files make PDF page rendering available without NuGet restore:

- `managed/PDFtoImage.dll` — PDFtoImage 5.2.1 (`net471`)
- `managed/SkiaSharp.dll` — SkiaSharp 3.119.2 (`net462`)
- `managed/net8.0/*.dll` — matching managed binaries for MockDemo
- `runtimes/win-x64/native/pdfium.dll` — bblanchon.PDFium.Win32 147.0.7690
- `runtimes/win-x64/native/libSkiaSharp.dll` — SkiaSharp.NativeAssets.Win32 3.119.2

The files were taken unchanged from the corresponding NuGet packages. Their
licenses and SkiaSharp third-party notices are in `licenses/`.

Only Windows x64 is intentionally included. The supported deployment target is
Office x64; an x86 deployment requires adding matching native binaries and
project copy items.
