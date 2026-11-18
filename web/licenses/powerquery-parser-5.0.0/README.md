# Power Query expression parser

The fixed internal browser bundle is built from the **published npm package**
`@microsoft/powerquery-parser@5.0.0`, `grapheme-splitter@1.0.4` and
`performance-now@2.1.0`. Their complete license texts and the parser's upstream
NOTICE are retained in this directory. Each downloaded tarball was checked against
the exact npm SHA-512 integrity before extraction.

The npm release does not declare `gitHead`. Its archive integrity/hash identifies
the acquired source bytes. Microsoft monorepo commit
`0eb2cc282fa594e9bd54c63b843e526878ef85d6` was inspected for API documentation;
it is not asserted to be the published tarball's build commit. Exact provenance,
dependency versions, all used module hashes, builder settings and output hash are
in `tools/powerquery-parser.lock.json` and `web/vendor-manifest.json`.

## Scope

`web/js/vendor/powerquery-parser-5.0.0.js` is an internal fixed analysis asset.
The dedicated Power Query host serves it at `/parser.bundle.js`; it is absent from
Shell startup and the authored HTML/plugin vendor catalog. The wrapper in
`web/powerquery/analyze.js` calls upstream `TaskUtils.tryLexParse` with
`Parser.ParseBehavior.ParseExpression`. It returns the first upstream lexical or
parse diagnostic with absolute UTF-16 offsets. Unavailable/internal failures stay
`not_checked`. It does not evaluate M, resolve workbook names, refresh sources or
format/write code.

The published package declares Node >=22. RNAssistant's fixed browser bundle was
tested in an isolated JS context without Node globals; this is integration evidence,
not an upstream claim of browser support. Windows/WebView2 qualification remains
separate. Node and esbuild are development-time build tools, not product runtime
requirements. Browser dependencies are embedded; there is no CDN, dynamic import,
external package restore or source-map fetch.

## Rebuild

Prepare a disposable development folder with Node >=22 and the exact packages.
This optional restore is not part of an RNAssistant build or product startup:

```sh
npm install --prefix /temporary/pq-build --ignore-scripts --no-audit --no-fund --save-exact @microsoft/powerquery-parser@5.0.0 grapheme-splitter@1.0.4 performance-now@2.1.0 esbuild@0.25.12
node tools/build-powerquery-parser.js --modules /temporary/pq-build/node_modules --check
```

The esbuild platform package must be installed by npm's normal optional-dependency
resolution. The reference build used `@esbuild/linux-x64@0.25.12`; its archive
integrity is recorded. No package installation scripts were executed.
The script itself performs no network/restore. It checks exact package versions,
every input hash, the complete module closure and the expected output hash. Omit
`--check` to rewrite the same verified bundle. An upgrade requires a reviewed lock,
inventory/license update and browser-context tests; the script never selects a
new version or accepts changed bytes silently.

```sh
node tests/web/powerquery-parser.test.js
node tests/web/vendor-gate.test.js
```
