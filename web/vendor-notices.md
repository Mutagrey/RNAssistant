# Web Vendor Notices

Files under `web/js/vendor` and `web/css/vendor` are fixed, local browser assets
for the main RNAssistant WebView UI and selected HTML-workspace vendors. The machine-readable authority is
[`vendor-manifest.json`](vendor-manifest.json): it records every runtime file,
exact byte length/SHA-256, package version, npm tarball integrity/git commit,
license files and transitive browser-asset policy. Its HTTPS URLs are provenance
only. The runtime loads the small generated `app-html-vendor-catalog.js`, never the
manifest or those URLs.

| Package | Version | License | Local license text |
|---|---:|---|---|
| DOMPurify | 3.4.14 | MPL-2.0 OR Apache-2.0 | [`licenses/dompurify-3.4.14`](licenses/dompurify-3.4.14/) |
| marked | 12.0.2 | MIT (plus bundled Markdown notice) | [`licenses/marked-12.0.2/LICENSE.md`](licenses/marked-12.0.2/LICENSE.md) |
| highlight.js | 11.9.0 | BSD-3-Clause | [`licenses/highlight.js-11.9.0/LICENSE`](licenses/highlight.js-11.9.0/LICENSE) |
| CodeMirror | 5.65.16 | MIT | [`licenses/codemirror-5.65.16/LICENSE`](licenses/codemirror-5.65.16/LICENSE) |
| Feather Icons | 4.29.2 | MIT | [`licenses/feather-icons-4.29.2/LICENSE`](licenses/feather-icons-4.29.2/LICENSE) |
| KaTeX | 0.16.11 | MIT | [`licenses/katex-0.16.11/LICENSE`](licenses/katex-0.16.11/LICENSE) |
| Apache ECharts | 5.6.0 | Apache-2.0; bundled d3 notice BSD-3-Clause | [`licenses/echarts-5.6.0`](licenses/echarts-5.6.0/) |
| Fuse.js | 7.1.0 | Apache-2.0 | [`licenses/fuse.js-7.1.0/LICENSE`](licenses/fuse.js-7.1.0/LICENSE) |
| Tabulator | 6.5.0 | MIT | [`licenses/tabulator-tables-6.5.0/LICENSE`](licenses/tabulator-tables-6.5.0/LICENSE) |
| SheetJS Community Edition | 0.20.3 | Apache-2.0 | [`licenses/sheetjs-0.20.3/LICENSE`](licenses/sheetjs-0.20.3/LICENSE) |
| Wunderbaum | 0.14.1 | MIT | [`licenses/wunderbaum-0.14.1/LICENSE`](licenses/wunderbaum-0.14.1/LICENSE) |
| Viewer.js | 1.12.0 | MIT | [`licenses/viewerjs-1.12.0/LICENSE`](licenses/viewerjs-1.12.0/LICENSE) |

KaTeX ships only the 20 WOFF2 files used by current WebView2. Its local CSS is a
documented derivative of the exact 0.16.11 distribution: unused `.woff`/`.ttf`
fallback URLs were removed so every URL resolves to a manifested local file.
ECharts uses its prebuilt browser bundle; `zrender`/`tslib` are embedded and no
separate dependency is loaded. The HTML-workspace pool is explicitly limited to
`echarts`, `Tabulator`, `Fuse` and `XLSX` (SheetJS CE full browser build). A workspace
receives only the pinned libraries whose globals occur in its classic JavaScript or
inline script source; existing `echarts` HTML references also activate it. They load on demand,
appear as read-only Dependencies, and are embedded before workspace code in the
sandbox and standalone HTML export. The main WebView does not parse the new vendor
implementations at startup. `fuse.source.js` and `tabulator.source.js` carry their
upstream browser JS/CSS as inert strings; only the authored sandbox/export executes
them. The Tabulator carrier strips upstream source-map trailers, so browser developer
tools cannot request unbundled map files. SheetJS parses local raw spreadsheet bytes,
including XLSB and legacy XLS; the full build embeds its ZIP and codepage logic, so
there is no separate JSZip dependency. The manifest records original and embedded
hashes. No CDN or separate runtime package is loaded. Tabulator is used with local
row arrays; its optional AJAX and extra export-library paths are outside this pool.

Adding a workspace vendor requires one pinned manifest package and license, a
manifested browser source carrier, `htmlWorkspace` metadata with its exact global,
`node tools/generate-html-vendor-catalog.js`, HTML skill selection guidance, and a
preview/export test. `node tests/web/vendor-gate.test.js` checks that the generated
catalog matches the manifest and every carrier has exact bytes/hashes. A package used only by the
main UI is not automatically exposed to authored HTML. The curated pool is kept in
the HTML skill body so normal model requests do not carry the manifest or minified
sources; no extra model-facing tool is needed for this small stable pool.

CodeMirror ships only the pinned core, selected modes/addons and its `show-hint`
popup. RNAssistant owns the bounded VBA keyword/module/procedure provider; it reads
only current drafts and already loaded local source and performs no bridge, COM or
network lookup per keystroke.

Wunderbaum ships its pinned UMD and CSS only. RNAssistant's `TreeAdapter` accepts
bounded local arrays and does not expose URL/lazy loading, edit, DnD, grid or
persistence capabilities. Its local icon layer uses CSS masks; no icon font or
remote asset is loaded.

Viewer.js ships its pinned UMD and CSS only. It receives one already admitted local
Blob image at a time and owns inline fit, focal wheel/pinch zoom, pan, rotation and
its toolbar. RNAssistant retains download, gallery/PDF sequence navigation and Blob
URL teardown; the vendor cannot call the bridge, fetch a page or retain a document-
level cache. The shared virtualized thumbnail rail remains RNAssistant-owned and
gives Viewer.js no additional images, identifiers or execution authority.

Selected existing inline SVG paths are adapted from Feather Icons. Feather is
source-only: its JavaScript package and npm dependencies are not loaded at runtime.
Lucide is not currently bundled; a future shared icon adapter requires its own
pinned manifest entry and consumer switch.

The main UI admits only its local resource route in `connect-src`, keeps
`font-src 'self'` and, while the worker
allowlist is empty, `worker-src 'none'`. There are no runtime WASM or worker files.
A future local worker is permitted only after one atomic change adds its exact
manifest entry, local host factory/allowlist, cancellation/`terminate` ownership,
CSP update and zero-network test. User-approved HTML-workspace HTTP access is a
separate host bridge and does not relax the main UI vendor policy.
