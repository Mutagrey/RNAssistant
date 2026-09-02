# Vendored PDF rendering

These files make PDF page rendering available without NuGet restore:

- `managed/PDFtoImage.dll` — PDFtoImage 5.2.1 (`net471`)
- `managed/SkiaSharp.dll` — SkiaSharp 3.119.2 (`net462`)
- `managed/net8.0/*.dll` — matching managed binaries for MockDemo
- `runtimes/win-x64/native/pdfium.dll` — bblanchon.PDFium.Win32 147.0.7690
- `runtimes/win-x64/native/libSkiaSharp.dll` — SkiaSharp.NativeAssets.Win32 3.119.2
- `runtimes/win-x86/native/pdfium.dll` — bblanchon.PDFium.Win32 147.0.7690
- `runtimes/win-x86/native/libSkiaSharp.dll` — SkiaSharp.NativeAssets.Win32 3.119.2

The files were taken unchanged from the corresponding NuGet packages. Their
licenses and SkiaSharp third-party notices are in `licenses/`.

Both Windows machine types are taken unchanged from the same package versions and
are selected by the process architecture (`x64/` or `x86/`); they must never be
cross-loaded. SHA-256:

- x64 `pdfium.dll`: `15df9dddd81eddc5a177946aa5e34cda821ebc46a51440ecb607f91e99644895`
- x64 `libSkiaSharp.dll`: `8b097d433db94fe61aba85213184938fb36118afd542d33252dd83794fdd9afc`
- x86 `pdfium.dll`: `83bd789c4924deb42db18ab42f7479be8d0d13e8cf5363c914e53404382baa6d`
- x86 `libSkiaSharp.dll`: `0a8a5ce7f24837d78b622b123eff0956bdd81437d7aa8554cb1282d4eae186b0`

Repository presence and portable packaging are host-neutral evidence only. Real
Windows x86 Office loading/rendering remains a separate qualification gate.
