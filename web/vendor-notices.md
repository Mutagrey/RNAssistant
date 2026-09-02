# Web Vendor Notices

Files under `web/js/vendor` and `web/css/vendor` are fixed, local browser assets
for the main RNAssistant WebView UI. The machine-readable authority is
[`vendor-manifest.json`](vendor-manifest.json): it records every runtime file,
exact byte length/SHA-256, package version, npm tarball integrity/git commit,
license files and transitive browser-asset policy. Its HTTPS URLs are provenance
only; the application never loads the manifest or those URLs at runtime.

| Package | Version | License | Local license text |
|---|---:|---|---|
| DOMPurify | 3.4.14 | MPL-2.0 OR Apache-2.0 | [`licenses/dompurify-3.4.14`](licenses/dompurify-3.4.14/) |
| marked | 12.0.2 | MIT (plus bundled Markdown notice) | [`licenses/marked-12.0.2/LICENSE.md`](licenses/marked-12.0.2/LICENSE.md) |
| highlight.js | 11.9.0 | BSD-3-Clause | [`licenses/highlight.js-11.9.0/LICENSE`](licenses/highlight.js-11.9.0/LICENSE) |
| CodeMirror | 5.65.16 | MIT | [`licenses/codemirror-5.65.16/LICENSE`](licenses/codemirror-5.65.16/LICENSE) |
| Feather Icons | 4.29.2 | MIT | [`licenses/feather-icons-4.29.2/LICENSE`](licenses/feather-icons-4.29.2/LICENSE) |
| KaTeX | 0.16.11 | MIT | [`licenses/katex-0.16.11/LICENSE`](licenses/katex-0.16.11/LICENSE) |
| Apache ECharts | 5.6.0 | Apache-2.0; bundled d3 notice BSD-3-Clause | [`licenses/echarts-5.6.0`](licenses/echarts-5.6.0/) |
| Wunderbaum | 0.14.1 | MIT | [`licenses/wunderbaum-0.14.1/LICENSE`](licenses/wunderbaum-0.14.1/LICENSE) |

KaTeX ships only the 20 WOFF2 files used by current WebView2. Its local CSS is a
documented derivative of the exact 0.16.11 distribution: unused `.woff`/`.ttf`
fallback URLs were removed so every URL resolves to a manifested local file.
ECharts uses its prebuilt browser bundle; `zrender`/`tslib` are embedded and no
separate dependency is loaded. HTML workspaces that reference the `echarts` global
receive that exact local bundle inside their sandbox and standalone export; no CDN
or second chart runtime is loaded.

Wunderbaum ships its pinned UMD and CSS only. RNAssistant's `TreeAdapter` accepts
bounded local arrays and does not expose URL/lazy loading, edit, DnD, grid or
persistence capabilities. Its local icon layer uses CSS masks; no icon font or
remote asset is loaded.

Selected existing inline SVG paths are adapted from Feather Icons. Feather is
source-only: its JavaScript package and npm dependencies are not loaded at runtime.
Lucide is not currently bundled; a future shared icon adapter requires its own
pinned manifest entry and consumer switch.

The main UI keeps `connect-src 'none'`, `font-src 'self'` and, while the worker
allowlist is empty, `worker-src 'none'`. There are no runtime WASM or worker files.
A future local worker is permitted only after one atomic change adds its exact
manifest entry, local host factory/allowlist, cancellation/`terminate` ownership,
CSP update and zero-network test. User-approved HTML-workspace HTTP access is a
separate host bridge and does not relax the main UI vendor policy.
