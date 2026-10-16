# Data Templates

Status: M0 started, portable capture/execution proof implemented (2026-10-16).
Owner: Data Templates domain; portable implementation in `web/tabular`.
This is the canonical contract for the implemented slice, not a second roadmap.
Retire it only if the domain is removed or its contract moves to another named
owner. Architecture decision: [ADR-0014](decisions/ADR-0014-data-templates.md).

## Current slice

[The M0 page](../web/tabular/index.html) runs a normalized, bounded capture through
the same plain JavaScript in a dedicated Worker or explicit cooperative mode.
Select [disjoint-capture.json](../web/tabular/examples/disjoint-capture.json) with
the file picker. It includes two separated value bands and their headers, plus a
right-hand year marker. The result preserves `J12` as row 12 / column 10 and keeps
`P5` / `P12` at their original addresses. No period or total is inferred yet.

This is a capture probe, not a template language, file-backed project, published
dataset or Office integration. No `tabular.*` tool/skill is exposed. It does not
read a live workbook, parse XLSX/YAML, create a project, write source files or
export a complete project. Download produces only the probe result JSON.

| Module | Responsibility |
|---|---|
| `selectors.js` | Bounded geometry and physical-coordinate set operations |
| `snapshot.js` | Closed normalized cell schema, immutable reader, explicit coverage |
| `engine.js` | Pure inspection; receives cancellation/progress/yield callbacks |
| `executor.js` | One active job, Worker lifecycle, deadline, cancellation, contract revision check |
| `worker.js` | Fixed shipped module loading and a single inspection message contract |
| `probe.js` | File picker, explicit mode, result presentation and download |

Core modules have no DOM, filesystem, network, Office or native bridge access.
No C# DSL implementation, second agent loop, CAS, journal, package manager or
vendor dependency was added. Existing HTML artifacts and limits are unchanged.

## Probe input and coverage

The version-1 **probe request** has only `version`, `snapshot`, `bounds`,
`selection`, and optional `maxCells`. It is not the future project/template
format. Unknown fields and versions fail; selectors are never executable JS.

The snapshot has `version`, `source`, `sheet`, and `ranges`. Each range has an
explicit A1 `address` and a dense rectangular `cells` array. Array positions are
offsets within that physical range. Overlapping capture rectangles are rejected.
Source metadata has only a descriptive `label` and `kind` (`fixture` or
`retained-snapshot`). Uploaded metadata cannot assert a verified live capture and
does not convey resource authority, permissions or publication evidence.

Cells are closed tagged values: `number`, `text`, `boolean`, `blank`, `error`,
`unavailable`. Value kinds require a corresponding `value`; blank has no value;
unavailable requires `reason`. Zero, empty text, blank, Excel error text and an
unavailable cached formula result remain distinct. Numbers retain IEEE-754
semantics; identifiers must be text. Decimal arithmetic, dates, display text,
formulas, number formats, merges and hidden/filter metadata are not implemented.

The reader copies source metadata and cells. The engine parses a bounded immutable
input string once per run. A selected address outside the supplied capture fails
with `CAPTURE_INCOMPLETE`; no synthetic blank or partial success is returned.
`coverage.complete` means every selected coordinate has an explicit captured cell,
including cells explicitly marked unavailable. It does not prove whole-sheet
coverage, live consistency, semantic validity or numeric availability. Counts
include unavailable cells separately. Diagnostics carry a code and field/address
path; source line locations await the template parser.

## Selectors and limits

Supported: A1 rectangles/single cells, column and row selections, quoted sheet
names (including doubled apostrophes), `A8:A*`, union `|`, difference `\` and
parentheses. Difference binds more tightly than union; both associate left.
Unions deduplicate coordinates. Output order is physical row then column.
Column/row selections and terminal row `*` expand only inside explicit bounds.
Other wildcard forms, reversed ranges, foreign sheets and explicit coordinates
outside bounds fail; nothing is silently clipped. An empty selection is allowed.

| Limit | M0 value |
|---|---|
| Engine input | 1,000,000 UTF-16 code units of JSON |
| Picker input | 1,000,000 bytes, strict UTF-8 decoding |
| Capture | 64 rectangles, 100,000 cells total |
| Declared selection area / returned cells | 100,000 / 100,000; optional smaller `maxCells` |
| Selection work | 200,000 atom-cell visits, before set operations |
| Selector text / terms / nesting | 2,048 code units / 64 / 16 |
| Cell text | 32,767 code units |
| Execution deadline | Default 5 seconds, explicit range 1–30,000 ms |
| UI result preview | First 20 cells; download contains all successful results |

These limits bound this probe only. They do not increase HTML aggregate, transfer,
Resource Fabric or web verification limits. The browser check prints exact asset
count/bytes; M0 has eight executable/presentation files and no vendors. Future
YAML/XLSX dependencies need a separate size/license/capability review.

## Execution and portability

The executor outlives any result viewer. Closing the viewer does not cancel a
run; cancellation, reset or disposal settles its Promise and releases resources.
It rejects concurrent runs, stale protocol/revision handshakes and foreign job
responses. Each Worker loads fixed relative scripts once, accepts one job and is
terminated on completion, error, cancellation or deadline. Worker absence or
load failure returns `EXECUTOR_UNAVAILABLE`; there is no automatic fallback.
The deadline includes startup and is checked against a monotonic clock before
accepting results. Late results cannot replace cancellation or timeout.

`file://` uses an explicitly displayed cooperative mode with the shipped bounded
core and periodic yields. It needs no server, Node, npm, fetch of adjacent JSON,
browser flags or RNAssistant credentials. The user selects the input file.
Cooperative cancellation is checked at bounded checkpoints; it cannot interrupt
arbitrary JS. Worker mode is tested on a loopback HTTP origin supplied by the
test harness, not a production server.

The revision handshake is a **contract label**, not a content hash or trust
proof. Exact captured module bytes, `engine.lock.json`, approved extensions and
native execution leases are not implemented. A Worker is not a permission
sandbox. Imported JS is unsupported; the root RNAssistant UI must never execute
project scripts through this probe. CSP and native-origin enforcement for the
eventual WebView2 execution realm still require implementation and qualification.

## Integration gaps and next boundary

Source review at `c33da7df` found these concrete gaps:

| Boundary | Existing owner / remaining work |
|---|---|
| Office → project files | `WorkspaceFileService` already owns guarded file writes, CAS observation and recovery in CLI. Office has no scoped project-root composition. Add typed create/open through that owner; no direct second file writer. |
| Live Excel capture | `ExcelResourceProvider` resolves explicit ranges through `ExcelReadService` and the bound document scope. It does not yet feed this probe; capture consistency, unsaved values, dependencies and richer cell availability remain open. |
| Application-owned execution | `AssistantPaneControl` owns the current WebView2 and trusts its exact root document; frame navigation is limited to about:blank/srcdoc. A browser proof does not authorize broadening that policy or prove WebView2 Worker behavior. |
| Publication | Project identity, draft/test/publish, manifest read-back and exact source/module/dictionary pins must use existing authority/journal owners. Probe JSON cannot authorize publication. |
| Transfer | Picker/download works for this probe. File-backed project closure, missing dependencies, source inclusion and complete export/import are not implemented. |

Next slice: compose a user-selected project root with the existing file owner and
the exact Office session; verify create/reopen and external drift before exposing
project tools. The native capture/execution path also needs pinned inputs and
typed DTOs. This is still M0;
M1–M3 completion and full matrix DSL are not claimed. Tracked gates live in
[the risk register](stabilization/RISK_REGISTER.md#data-templates-integration-gates--2026-10-16).

## Verification

Run only the affected scripts (Playwright/browser must already be available):

```sh
node tests/web/tabular-core.test.js
node tests/web/tabular-browser.test.js
```

The browser script accepts the repository's usual `BROWSER_EXECUTABLE` override;
an unavailable browser fails the check instead of reporting a pass. No build or
Harness execution is needed for this JavaScript slice.

Core checks use a hand-authored cell oracle independent of extraction, cover
geometry/budgets/schema/missing states and cancellation. Browser checks exercise
the real Worker, an intentionally non-returning test Worker, deadline and cancel,
viewer removal, disposal/recreation, revision/job guards and unavailable executor.
They compare Worker results with offline `file://` picker/download results and
reject stale output after a failed capture. Those checks do not establish live
Excel, Windows/WebView2, project publication or real-model workflow evidence.
