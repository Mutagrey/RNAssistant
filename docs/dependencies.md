# Vendored Dependencies

NuGet packages are committed in `packages/`:

- `Microsoft.Web.WebView2 1.0.2903.40`
- `Newtonsoft.Json 13.0.3`

Task pane JS/CSS is committed in `web/`:

- `marked 12.0.2`
- `DOMPurify 3.1.6`
- `highlight.js 11.9.0`

The fixed WebView2 runtime is intentionally not expanded here because it is over 250 MB. The code supports it from `vendor/webview2-runtime/<version>/` and falls back to Evergreen runtime when absent.

