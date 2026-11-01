# Dockview core 8.8.3 — Demo only

Owner: Demo presentation. MIT, no enterprise package. This pinned asset provides
workspace tabs, groups, docking and layout serialization for `prototypes/ui/`.

- Official package: https://www.npmjs.com/package/dockview-core/v/8.8.3
- Upstream: https://github.com/dockview/dockview
- [LICENSE](LICENSE) is the unmodified upstream `LICENCE.md`.
- [provenance.json](provenance.json) records the official npm tarball, verified
  SHA-512 integrity, exact file sizes and SHA-256 hashes.
- `dockview-core.min.js` is the unmodified browser UMD bundle with embedded CSS.
  It exposes `window['dockview-core']`; no bundler, CDN or loader is needed.

Styling uses public theme variables. P1 enables the official pointer drag strategy
because HTML5 drag did not move tabs in the tested in-app browser. Floating/popout
windows are disabled. Browser evidence and usage live in
[UI_LAYOUT_UX.md](../../../../docs/ui/UI_LAYOUT_UX.md).

On upgrade, replace the pinned directory and script reference together, recheck
license/integrity and affected docking behavior. Remove this asset when no Demo
consumer remains. Production vendor manifests are outside its scope.
