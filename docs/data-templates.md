# Data Templates

Status: portable M0 proof, M1 project-manifest/source-selection foundation and the
first M2 matrix-language and bounded XLSX-reader slices implemented (2026-10-16).
Matrix extraction works over normalized snapshots and explicitly selected XLSX
files in the separate Worker probe. Application import/queue and publication remain open.
Owner: Data Templates domain; portable JS in `web/tabular`, shared manifest storage
in Core `TabularProjectStore` over `WorkspaceFileService`.
This is the canonical domain contract; planned scope is marked separately from
implemented behavior and uses the original proposal's M0–M7 stages.
Retire it only if the domain is removed or its contract moves to another named
owner. Architecture decision: [ADR-0014](decisions/ADR-0014-data-templates.md).

## Current slice

[The core probe](../web/tabular/index.html) offers three explicit operations through
the same executor: M0 snapshot inspection, [M2 matrix extraction](#matrix-language-and-execution-v1)
and [bounded XLSX extraction](#bounded-xlsx-reader).
For inspection it runs a normalized, bounded capture through
the same plain JavaScript in a dedicated Worker or explicit cooperative mode.
Select [disjoint-capture.json](../web/tabular/examples/disjoint-capture.json) with
the file picker. It includes two separated value bands and their headers, plus a
right-hand year marker. The result preserves `J12` as row 12 / column 10 and keeps
`P5` / `P12` at their original addresses. No period or total is inferred yet.

For extraction select `Выполнить шаблон` and
[matrix-extraction.json](../web/tabular/examples/matrix-extraction.json). It produces
16 canonical observations with source/rule evidence, including two years and
separate annual totals. The download is a report with at most 20 preview records,
not a published dataset. For a real XLSX select `Прочитать XLSX`,
[matrix-xlsx-extraction.json](../web/tabular/examples/matrix-xlsx-extraction.json)
and [matrix-source.xlsx](../web/tabular/examples/matrix-source.xlsx). The file stays
outside the JSON envelope and is read only inside its Worker. None of these
operations reads live Excel/YAML or writes project files. Native manifest creation/opening is described below; no `tabular.*`
tool/skill is exposed and the main loader is not yet connected to this executor.

| Module | Responsibility |
|---|---|
| `selectors.js` | Bounded geometry and physical-coordinate set operations |
| `snapshot.js` | Closed normalized cell schema, immutable reader, explicit coverage |
| `json.js` | Bounded JSON decoding, duplicate/reserved-key and non-finite-number refusal |
| `expression.js` | Expression syntax tree, type/scope binding to instructions, explicit value conversion and evidence |
| `template.js` | Closed matrix/dictionary schemas, context dependency order and immutable matrix plan |
| `matrix.js` | Canonical records, grain/unit checks, lineage and awaited provisional result batches |
| `xlsx-zip.js` | Selective ZIP32 reads, streamed native deflate, size/CRC checks and byte progress |
| `xlsx-reader.js` | Bounded SpreadsheetML adapter, exact numeric lexemes, cache evidence and complete rectangle coverage |
| `engine.js` | Declared bounded operations over immutable input; no DOM or publication |
| `executor.js` | One active job, Worker lifecycle, deadline, cancellation, contract revision check |
| `worker.js` | Fixed shipped modules, declared operations and one acknowledged batch in flight |
| `probe.js` | File picker, explicit mode, result presentation and download |

Core modules have no DOM, filesystem authority, network, Office or native bridge access.
The XLSX adapter receives a selected Blob capability, not an arbitrary path/URL.
No C# DSL implementation, second agent loop, CAS, journal or package manager was
added. One pinned XML parser is loaded only by the probe Worker. Existing HTML
artifacts, main UI CSP and limits are unchanged.

## Probe input and coverage

The version-1 **probe request** has only `version`, `snapshot`, `bounds`,
`selection`, and optional `maxCells`. It is not the future project/template
format. Unknown fields and versions fail; selectors are never executable JS.
JSON input rejects duplicate keys (including escaped duplicates), reserved
prototype keys, non-finite numbers and nesting beyond 32 levels before domain validation.

The snapshot has `version`, `source`, `sheet`, and `ranges`. Each range has an
explicit A1 `address` and a dense rectangular `cells` array. Array positions are
offsets within that physical range. Overlapping capture rectangles are rejected.
Source metadata has only a descriptive `label` and `kind` (`fixture` or
`retained-snapshot`). Uploaded metadata cannot assert a verified live capture and
does not convey resource authority, permissions or publication evidence.

Version-1 cells are closed tagged values: `number`, `text`, `boolean`, `blank`, `error`,
`unavailable`. Value kinds require a corresponding `value`; blank has no value;
unavailable requires `reason`. Zero, empty text, blank, Excel error text and an
unavailable cached formula result remain distinct. Numbers retain IEEE-754
semantics; identifiers must be text. Snapshot version 2 explicitly adds
`{kind:"numeric", lexeme:"12345678901234567890.125"}` and optional
`formula:{text,type}` evidence. A numeric lexeme is at most 128 characters with
an optional decimal exponent; it never passes through JS Number. Formula type is
`normal`, `shared`, `array` or `dataTable`; empty shared-formula text is retained,
not reconstructed. Version 1 rejects these new fields/kinds. Decimal arithmetic,
date conversion, display formatting, formula calculation, merge/hidden/filter
semantics are not implemented.

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
count/bytes; the current probe has fifteen executable/presentation files including
the pinned 50,818-byte sax XML parser. Its license/source hashes are in the shared
[vendor manifest](../web/vendor-manifest.json) and [notices](../web/vendor-notices.md).
Future dependencies require their own size/license/capability review.

## Execution and portability

The executor outlives any result viewer. Closing the viewer does not cancel a
run; cancellation, reset or disposal settles its Promise and releases resources.
It rejects concurrent runs, stale protocol/revision handshakes and foreign job
responses. Worker protocol v3 declares `inspect`, `extract` and `extractXlsx`; old handshakes fail
explicitly. Each Worker loads fixed relative scripts once, accepts one job and is
terminated on completion, error, cancellation or deadline. Worker absence or
load failure returns `EXECUTOR_UNAVAILABLE`; there is no automatic fallback.
The deadline includes startup and is checked against a monotonic clock before
accepting results. Late results cannot replace cancellation or timeout.

`file://` uses an explicitly displayed cooperative mode with the shipped bounded
core and periodic yields. It needs no server, Node, npm, fetch of adjacent JSON,
browser flags or RNAssistant credentials. The user selects the input file.
Cooperative cancellation is checked at bounded checkpoints; it cannot interrupt
arbitrary JS. Worker mode is tested on a loopback HTTP origin supplied by the
test harness, not a production server. XLSX explicitly requires Worker execution;
cooperative mode fails with `XLSX_WORKER_REQUIRED`. A browser that denies Workers
on `file://` can still run the two normalized-input operations, but not XLSX.

The revision handshake is a **contract label**, not a content hash or trust
proof. Exact captured module bytes, `engine.lock.json`, approved extensions and
native execution leases are not implemented. A Worker is not a permission
sandbox. Imported JS is unsupported; the root RNAssistant UI must never execute
project scripts through this probe. CSP and native-origin enforcement for the
eventual WebView2 execution realm still require implementation and qualification.

## Matrix language and execution v1

This is the first supported M2 profile, not the full proposal's language or a
production dataset publication format. The runnable fixture is the authoritative
worked example alongside these rules and the closed validators. JSON is the
implemented template notation; YAML is deferred as another decoder into the same
syntax/model, never another interpreter or synchronized source file.

The entry envelope is `{version:1, kind:"matrix-extraction", template, snapshot,
dictionaries, dataset}`. Its immutable JSON text captures all inputs for this
bounded run. `template.language:1` and `template.kind:"matrix"` select the profile.
All fields shown in the example are required; unknown fields/versions are errors.
No project/source/module authority is granted by IDs, revision labels or reports.

The implementation separates these boundaries:

1. `RNTabularExpression.parse` produces syntax nodes with character spans. It does
   not read cells or execute expressions.
2. `RNTabularExpression.compile` resolves names, permitted functions and types to
   instructions; each expression retains its rule path, cell reads and context
   references. It rejects variables outside their declared axis scope.
3. `RNTabularTemplate.compile` orders context dependencies, rejects cycles and
   conflicting columns, projects selectors to physical row/column axes, and
   checks bounds/output budgets. The immutable plan contains no cells, UI, file
   handles or authority. It is transient, not a second editable template format.
   Execution accepts only plans produced by this compiler instance.
4. `RNTabularMatrix.execute` binds headers/dictionaries against the immutable
   snapshot, then yields records to a required asynchronous batch consumer. The
   application owns staging, cancellation and any later publication.

### Supported language profile

Expression grammar is deliberately limited to JSON string literals, unsigned
integer literals, `row`, `col`, `ctx.name`, and registered function calls. Nested
calls are supported; arbitrary property access, executable JS, infix arithmetic,
conditionals, loops and imported functions are not. Unsupported syntax/functions
fail rather than being interpreted by JS. Diagnostic paths contain a JSON field
path and expression character offset; source read/conversion errors also carry
the available cell location.

| Function | Typed result / rule |
|---|---|
| `cell("A", row)`, `cell(col, 5)` | Cell; literal column letters or `col`, literal positive row or `row`. Dynamic addresses are refused. |
| `text(cell)` | Text from text/finite numeric cells; no implicit trimming or case folding for dictionary keys. Unsafe integer keys fail. |
| `raw(cell)` | Tagged raw cell, preserving missing/error distinctions. |
| `parse.year(text)` | Explicit four-digit year, 1000–9999. No guessed year. |
| `parse.monthRu(text)` | Declared Russian month name/abbreviation, optional final dot; no date-system inference. |
| `period.month(year, month)`, `period.year(year)` | Distinct monthly and annual periods. |
| `lookup("parameters" or "units", text, "scope")` | Exact scoped dictionary target; dictionary and scope names are literals. Unknown/ambiguous mappings fail. |
| `decimal(raw, "ru-RU" or "en-US")` | Canonical decimal text or a tagged missing value; explicit numeric locale. |

Context expressions cannot use `row`/`col`. Row parameter/unit expressions can
use `row`; column period expressions can use `col`; the value expression can use
both. Context dependencies are evaluated once in topological order. Selectors
retain M0 geometry/precedence and are projected to sorted unique physical axes.
Each column belongs to exactly one semantic group; overlap is an error. Multiple
groups do not shift physical coordinates across gaps.

This profile puts parameters on rows and periods on columns. Horizontal parameter
axes, multiple blocks, quarter/date functions, bindings, review proposals,
aggregation, joins and extensions are later explicit capabilities. This resolves
the original proposal's §6.4/§6.7 ambiguity: their possible architecture is not a
claim that both orientations already work in v1. The current closed schema does
not silently accept deferred constructs or policy fields.

### Canonical observations and value semantics

Each record contains `parameterId`, named `dimensions`, `period`, tagged `value`,
`unitId`, `role`, `temporalMeaning` and `provenance`. Parameter IDs and dimension
values are text. Supported roles are `observation` and `reported_total`; annual
totals are not summed with months. Parameter metadata declares its dimension names,
decimal value type, canonical unit and temporal meaning. The source unit must
match exactly; implicit scaling/conversion is forbidden in this slice.

The explicit uniqueness key within this extraction is parameter + dimension
values + complete period + role. A duplicate is `DUPLICATE_OBSERVATION`, not an
automatic sum/deduplication. This is a single-source example; cross-source inclusion
and consolidation semantics must be declared before batch dataset publication.
No stable observation identity or native dataset handle is synthesized by the engine.

Decimal output is `{kind:"decimal", value:"1234.5"}`: canonical base-10 text,
at most 38 total digits and 18 fractional places, without rounding or arithmetic.
Leading/trailing insignificant zeros and negative zero are normalized. Russian
text uses decimal comma and optional groups of three separated by space/NBSP/narrow
NBSP; English text uses decimal point and optional comma groups. Invalid grouping,
locale mismatch and overflow fail. Text decimals never pass through JS Number.
Version-1 `number` cells retain their already-rounded IEEE-754 value; unsafe integers
are rejected and lost input precision cannot be recovered. Version-2 `numeric`
cells retain the raw decimal/exponent lexeme and normalize it without floating-point
conversion. Numeric conversion is locale-independent; locale applies only to text.
`text(numeric)` uses the exact normalized numeric value, never an Excel display mask.
Identifiers and explicitly textual high-precision values remain text. The XLSX
reader declares this exact-lexeme capability rather than relabeling numbers as text.

Missing values preserve distinct reasons: `blank`, `empty-text`, `source-error`
with the error detail, and `unavailable` with the capture reason. Zero is a decimal.
Whitespace-only numeric text is invalid, not silently blank. A required missing
header/key is an error rather than an invented parameter or period.

Provenance has separate value/parameter/unit/period rule paths, source evidence
including captured cell values, and exact scoped mapping decisions. Excel addresses
are confined to `{kind:"cell", sheet, row, column, address}` source locations, not
canonical business keys. For example, the July 2026 volume record uses `J12` for
its value, `A12` for parameter code `0012`, `B12` for the unit, `J5` for July and
`P5` for 2026. `P12` remains a separate reported annual total.

### Batches, completion and limits

`executor.extract(inputText, {signal, deadlineMs, onProgress, onBatch})` runs through
the same application-independent executor as inspection. `onBatch` is required
and awaited; Worker protocol v3 sends the next batch only after acknowledgement of
the current sequence. Final summary counts must match acknowledged batches. There
is no application processing queue yet; the executor still accepts one job.

Batches are **provisional**. A consumer must discard/unpublish staging after an
error, deadline, cancellation or consumer failure. Receiving a batch or returning
from `onBatch` cannot activate a dataset. Only successful operation resolution
establishes complete extraction; native publication still requires its separate
guards/evidence/read-back. A final `coverage.complete` refers to execution over
required captured dependencies, not absence of missing values or suitability for
all workbooks. Missing-value counts are reported separately.

| Bound | Current value |
|---|---|
| Input / snapshot / selector limits | Existing probe limits above; no increase |
| Expression text / nodes / depth | 1,024 characters / 128 nodes / 16 levels |
| Context expressions / column groups | 32 / 32 |
| Parameters or units / mappings per dictionary | 1,000 / 4,000 |
| Dataset dimensions / output records | 16 / 10,000 |
| Record JSON including lineage | 65,536 UTF-16 code units |
| Batch | Up to 64 records and 262,144 summed record JSON code units, plus array delimiters |
| Deadline | Normalized input: 1–30,000 ms; XLSX: 1–300,000 ms, default 120,000 ms |

Binding/extraction yield at bounded checkpoints and expose record progress. Hard
Worker cancellation also works while waiting for a batch consumer. The explicit
cooperative mode remains for the bounded shipped fixture/core; synchronous JSON
decoding/compilation are bounded but cannot be interrupted mid-call there. Neither
mode demonstrates arbitrary-extension isolation or qualifies 80 MB XLSX parsing. Axes are
compact in the plan, but the existing bounded selector compiler and snapshot
reader still materialize cells. The XLSX adapter scans its worksheet incrementally
and retains only the bounded rectangle; full qualification requires the large-file gate below.

The standalone probe stores only the first 20 records for display/download and
marks whether that preview is complete. It never accumulates an unbounded result
array. A failed run clears the previous report. Full dataset export, exact module
hash pins, live capture, template publication and loader integration remain open.

## Bounded XLSX reader

Owner: the portable Data Templates source adapter. This is a single-file reader
proof, not the application import/recognition queue. It introduces no source store,
workspace binding, file grant, native bridge operation or publication writer.
CSV and JSON still need their own readers and native source-location contracts.

`executor.extractXlsx(inputText, sourceFile, {signal, deadlineMs, onProgress, onBatch})`
passes an immutable File/Blob to a dedicated Worker without UI body reads, base64
or whole-workbook JSON. The closed version-1 `matrix-xlsx-extraction` envelope has
`template`, `dictionaries`, `dataset`, and `source:{formulaPolicy:"stored-cache"}`.
The template's exact sheet/bounds select the capture. The same compiler, mappings,
canonical record semantics and acknowledged batches then process the reader.
The summary adds versioned `sourceCapabilities` and bounded `sourceDiagnostics`;
source name and capabilities are descriptive, not verified revision/publication pins.

The ZIP reader loads a bounded directory and reads only package metadata, the
selected worksheet and referenced shared strings. It feeds 8 KiB compressed
chunks through native `DecompressionStream("deflate-raw")` and incremental strict
XML parsing. Required parsed parts must pass local/central boundary, actual size,
CRC, XML and semantic checks before the matrix can emit records. Unconsumed parts
are admitted by directory budgets but not decompressed or CRC-validated. This is
not whole-package integrity validation or a source content hash. Native stream
buffers are implementation-controlled; configured budgets are not measured RAM.

The worksheet is scanned to completion while only cells in the declared rectangle
are retained. The reader retains only needed shared-string entries while scanning
their table. An absent cell becomes blank **only after** a complete successful
worksheet parse; truncated/corrupt parts never yield a partial reader. The
worksheet `<dimension>` hint does not establish coverage. Missing shared-string
indices, duplicated/unordered coordinates and conflicting values fail explicitly.

Supported profile: unencrypted ZIP32 with stored/deflated entries and optional
data descriptors; UTF-8 transitional XLSX with explicit ordered cell coordinates,
shared/inline/rich text, raw numeric lexemes, booleans, errors and stored formula
caches. Relationship targets must stay inside the package; external sources are
never followed. ZIP64, XLS/XLSB/XLSM, Strict OOXML, DTD/entity declarations,
markup alternatives, captured cells with value/cell metadata, ISO-date cells and
unsupported value structures are refused. Rich text is plain text with phonetic
runs excluded; SpreadsheetML escapes are decoded once. Styles are not loaded or
applied; numeric date serials stay raw numbers, with the workbook's 1900/1904 date
system disclosed. No display-mask inference, implicit forward fill or date conversion.

Formula evaluation is absent. Stored caches carry `{formula:{text,type}}` in cell
evidence and `cacheFreshness:"unknown"` in source capabilities. A missing cache
is `unavailable/formula-cache-absent`; cached empty text and zero remain distinct.
The explicit `stored-cache` policy is required even when a selected book happens
to have no formulas. Large or stale formula-heavy workbooks still need source
validation appropriate to the eventual publication workflow.

| Reader budget | Current proof limit |
|---|---|
| Compressed source | 96 MiB |
| ZIP directory / entries | 2 MiB / 4,096 |
| Declared expanded total / any part | 512 MiB / 256 MiB |
| Metadata / selected worksheet / shared-string XML | 2 MiB / 256 MiB / 128 MiB |
| Scanned worksheet cells / shared-string entries | 2,000,000 / 2,000,000 |
| Retained rectangle / canonical observations | 100,000 cells / 10,000 records |
| Retained cell strings, formulas and needed shared strings | 8 Mi UTF-16 code units |
| XML nesting / attributes per element | 32 / 64; parser token buffer is also bounded |
| Deadline | 120 seconds by default; explicit maximum 300 seconds |

These are prototype admission limits, not a promise that all 80 MB workbooks fit
or an automatic sizing rule. Progress reports the current part, expanded/part
bytes and source bytes actually read, coalesced to about 10 Hz plus part boundaries.
Source-read totals include metadata rereads and exclude skipped members; they are
not a whole-file percentage. The probe shows part progress, elapsed time and cancel,
then canonical extraction progress. Parent-owned termination also cancels a busy
parser or an unacknowledged batch. No completed report survives a failed/replaced run.

The only new vendor is [sax 1.6.1](https://github.com/isaacs/sax-js), unmodified,
with BlueOak-1.0.0 license and exact npm tarball integrity/file hashes recorded in
the shared vendor manifest. It is not activated in the main WebView or authored
HTML pool. Native deflate support is a required capability, not a JS decompression
fallback. Format references: [ZIP APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT),
[Compression Standard](https://compression.spec.whatwg.org/),
[SpreadsheetML cells](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.cell).

Focused checks cover a hand-authored 16-record XLSX oracle, malformed archives/XML,
exact 38-digit numbers, formula availability, a 30,000-row selective-read fixture,
existing pinned SheetJS writer interoperability and real browser Worker lifecycle.
They do **not** measure representative 80 MB workbook memory/latency, multiple-file
queue throughput or Windows/WebView2 compatibility. Those gates remain open.

## Relationship to workspace-first

Data Templates is a domain project inside the workspace architecture accepted in
[ADR-0013](decisions/ADR-0013-workspace-first-runtime.md). It must not introduce a
separate project-root registry, conversation owner or filesystem authority. The
[workspace reconciliation](stabilization/PROGRESS.md#workspace-plan-reconciliation-and-event-subscription--2026-10-05)
records the existing implementation and open cutovers. Its M-numbers and Data
Templates M-numbers describe different plans.

| Concern | Shared workspace owner | Data Templates responsibility |
|---|---|---|
| Root, identity, writable/read-only association | Core `WorkspaceStore`, `WorkspaceDescriptor`, `.rnassistant/workspace.json` | Select a project manifest by a checked path inside the opened workspace; do not allocate another filesystem scope. |
| Sessions and execution | `ChatStore`, coordinator/kernel; workspace application composition already exists in Runtime | Target workspace-owned sessions; attach an exact Office source separately when needed. Do not create a domain-specific chat or silently relocate existing document chats. |
| Files, revisions, drift, uncertain effects | Core `WorkspaceFileService`, resource provider, authority/CAS/journal | Validate domain content and allowed project paths; delegate every file effect to that owner. |
| UI resource reads, receipts, reconnect and run lifetime | Workspace application contracts and existing event feed; Office adoption remains open | Add a project viewer/commands to that application boundary, without another file tree, event stream or model loop. |
| Template language, dictionaries, capture plan, test/publish | Data Templates domain and portable JS core | These are new domain semantics; a successful file write or generic web verification does not prove a valid template publication. |

`project.json` owns domain metadata; templates, dictionaries, bindings and datasets
will extend its versioned contract in later slices.
`.rnassistant/workspace.json` describes RNAssistant's workspace association.
They are not duplicate manifests and must not be synchronized as alternative
sources of truth. The portable `projectId` is domain metadata, not
workspace permission or a replacement for `WorkspaceId`. Runtime binds the opened
workspace and exact project-manifest locator/revision. Start with the existing
root-directory mount; multiple/external mounts are still workspace M2/M8 scope.

The portable export must omit RNAssistant's `.rnassistant` control directory,
workspace grants, chat state and resource leases. A copied/imported project gets
an explicit workspace association through `WorkspaceStore`; copying the original
workspace identity into a second existing root is already rejected by that owner.

Workspace M2/M3 supply root and file ownership; M7 supplies the shared application
and UI boundary; M8 supplies bound Office sources. New Data Templates files do not
require completing M6 migration of every old HTML/MD/JSON authoring path first.
The existing writers remain scoped to their existing artifacts until their own
explicit cutover; they never write the new project in parallel.

## Workspace project files

`TabularProjectStore` is a domain storage facet using the existing file owner;
it has no direct file I/O, separate registry, mutable CAS copy or journal.
Its models and implementation are included in both the existing Core Framework
4.8 project and the shared-source development build. Office gains no .NET 8 dependency.
`WorkspaceApplicationService` now exposes the same typed operations to CLI and
the Office panel, independently of conversation orchestration.

The current portable `project.json` has exactly four fields:

```json
{
  "kind": "data-templates",
  "formatVersion": 1,
  "projectId": "tp_0123456789abcdef0123456789abcdef",
  "name": "Quarterly reports"
}
```

This version is metadata only: it does not declare templates, sources, executable
modules or published results. Unknown/duplicate fields, wrong types, unsupported
versions and invalid identities fail without rewriting the file. The manifest
limit is 16 KiB UTF-8; names allow 1–200 characters without controls. The file must
be named `project.json`, at the root or in a subdirectory. All containment, link,
case-collision and protected-path checks remain with `WorkspaceFileService`.

Create uses the owner's guarded create and verified read-back; an existing target
is never overwritten or adopted as success. Open observes current bytes and
returns an immutable `TabularProjectSnapshot` with workspace ID, relative manifest
path, exact file reference/hash, domain metadata and current read-only status.
`RequireUnchanged` refuses a foreign workspace or changed file identity/revision.
It checks an observation, not a long-lived lock or future write permission; later
mutations still need the file owner's exact revision guard at dispatch.

Multiple projects can share a workspace. A copied project may retain its domain
ID, but must be explicitly opened in its own workspace/file authority. Workspace
relocation is handled by `WorkspaceStore`; no project association is persisted
separately. An uncertain write stays unresolved through restart. Ordinary open
cannot conceal it; explicit existing `recover` inspects without replaying creation.

Development CLI usage (existing CLI target; no Office migration is required):

```sh
rna workspace open /absolute/workspace
rna project create --workspace /absolute/workspace --path reports/project.json --name "Quarterly reports"
rna project open --workspace /absolute/workspace --path reports/project.json --read-only
```

These commands need no chat, model, workbook or browser. Files are discoverable
through the existing `resources`/`open` operations. This single-file foundation
does not yet implement draft/test/publish or multi-file activation.

### Shared application and UI

The **Рабочая папка** tab contains the Data Templates viewer. Its project settings
start expanded, present folder selection and project opening as two steps, and can
be collapsed. They accept an absolute path to an existing directory and an explicit
read-only choice. It creates/opens the domain manifest by a relative path and
shows its name/location as inert text. Native project operations cover metadata only.
Office chats and their existing artifacts retain their current ownership; this
tab does not attach workspace files to model context or bind an Excel source.

`src/RNAssistant.Application` is one shared application source module, compiled via
one `.projitems` import in Office Framework 4.8 and the existing CLI Runtime build.
It references Core only. The previous Runtime-only project facade was removed;
CLI `project create/open` and the bridge now consume `WorkspaceApplicationService`
and its typed contracts directly. No file logic or conversation runtime is copied.
The packaging seam and removal gate are in [MIGRATION_MAP](stabilization/MIGRATION_MAP.md#workspace-first-adapters--2026-10-04).

Each panel/CLI invocation owns one ephemeral workspace selection. Opening a root
issues a new binding ID, including when reopening the same root with different
permissions. Project commands must match it; selection changes and file operations
are serialized. The binding is application authority, not portable manifest data
or a model parameter. Failed root selection retains the previous selection; failed
project operations clear the old project projection. The service uses the host's
configured state directory and the bridge keeps it across Office controller changes.
Panel reload reads this state; application restart requires explicit root selection.
No extra selected-root/project registry is persisted.

The bridge's `getWorkspaceState`, `openWorkspace`, `createTabularProject`,
`openTabularProject` and `reconcileWorkspaceFile` use the existing token/operation
catalog and worker boundary. UI serializes commands, retains the exact uncertain
path for inspection and does not upgrade an opened read-only binding when a
checkbox is edited. Closing the panel drains accepted work through the existing
bridge lifecycle. No Office gate is held by workspace file work.
Runtime clearing uses the bridge's exclusive request boundary, so it cannot remove
the file journal during an accepted workspace operation; successful clearing also
revokes the selection.

Unknown writes and lost replies never trigger creation retries. **Проверить
состояние файла** invokes explicit existing reconciliation and returns its typed
outcome and existence observation. It does not claim the original attempt succeeded
or open a project implicitly; a subsequent explicit open validates the current file.
Malformed responses cannot report project success or file absence.

### Source selection UI

The implemented M1 viewer has Sources, Templates, Parameters, Mapping, Results and
Format/help sections. Wide layouts use keyboard-navigable tabs; narrow panels use
one section selector. Project settings are collapsed by default. Sources start
with a drop zone and a short workflow guide; after selection the drop zone becomes
compact and gives space to the file list. The other domain editors are explicitly
marked as future work; Mapping shows a conceptual diagram, not fabricated rules.

`app-data-source-intake.js` owns bounded browser file/directory enumeration and
one immutable format-hint catalog for XLSX, CSV and JSON. The controller owns the
temporary selection; separate views own presentation, as described below.
These modules do not read file contents, parse source data, write project files,
invoke a model or claim an import.
The existing workspace application remains the only native project/file owner.
There are no new bridge contracts, durable stores or Office target changes.

The same intake path accepts picker files, a folder picker when supported, and
dropped files/directories. Directory readers drain every batch and retain relative
paths. Enumeration is atomic with respect to the current selection: failure,
cancel or deadline adds no part of the incoming batch. Limits: 500 files in the
selection, 2,000 enumerated entries per drop, 16 nested directory levels for entry
traversal, 4,096 characters per displayed relative path and a 15-second deadline.
These are enumeration limits, not parsing or workbook-memory budgets.

The list supports search by relative path/name, format and inclusion/attention
filters, natural name/path sorting, largest-first sorting, removal and metadata
details. Pages contain at most 50 rows, with previous/next and direct page selection;
"Select shown" affects only the current page, while selection totals cover all
files. Filters reset the page; removal clamps it to the remaining pages. The
attention filter includes unavailable files and possible duplicates. Additional
filters/sorting are collapsed by default. Unknown extensions and empty files
remain visible but cannot be included. Matching relative path, size and modification time marks a possible
duplicate; it neither proves equal content nor deduplicates business records.
Extensions are descriptive hints, not validation of bytes or a parsed schema.
XLS/XLSB/XLSM and JSONL are not advertised as supported input adapters.

Browser File handles and inclusion choices are held only in the current page.
They are not resource authority or a saved project draft. The current workspace
binding and selected manifest path delimit the selection; changing them clears
it and cancels enumeration. Late callbacks cannot refill another context. Native
workspace commands prevent starting new intake; refreshing the same context does
not cancel an active enumeration. Page reload clears the list; the UI says so.
Selecting a source folder does not open/change the workspace root, grant native
path access or copy the source files. Persisting source selections is later domain
work through the existing workspace file owner, with explicit source association.

### Loader presentation architecture

The separation applies to the whole loader, including future template, parameter,
mapping, result and format screens. It uses static HTML/CSS and thematic plain-JS
modules without a framework, npm or a bundler.

| Owner | Responsibility |
|---|---|
| `app-data-source-intake.js` | Browser enumeration, metadata hints, limits and coalesced progress; no DOM or content parsing. |
| `app-data-templates.js` | File handles, selection, context guards and operation lifetime; passes immutable metadata snapshots and explicit actions to presentation. |
| `app-data-templates-view.js` | Screen navigation, filters, pages, metadata details and DOM events; no bridge, File handles, readers or job ownership. |
| `app-data-operation-view.js` | Shared progress component; renders supplied stage, counters, duration and terminal state; delegates cancel/dismiss without a timer or inferred success. |
| `app-data-templates-tokens.css` | Loader-wide palette aliases, typography, spacing, radii and card/row density. Inherits the host light/dark theme. |
| `app-data-templates.css`, `app-workspace-projects.css`, `index.html` | Layout, reusable visual classes and semantic markup; no business state derived from CSS. |

View listeners are root-scoped, delegated for dynamic rows and disposed explicitly.
Unmounting/remounting a view neither cancels accepted work nor drops the selection;
the controller survives it. Context replacement remains an explicit owner action.
New functional screens can have their own view module using the same snapshot/action
boundary, without adding another state store. Visual changes should change tokens,
markup or renderers without modifying execution. Native controls, labels, keyboard
navigation, focus restoration, explicit empty/error states and reduced-motion
support remain part of the presentation contract.

The operation card is outside the individual screens and stays visible when
switching sections. Current progress covers **enumeration only**: current path,
file count, total source size, elapsed time and cancellation. A percentage appears
only for a known file count; folder traversal is indeterminate. Intermediate
updates are coalesced to approximately 10 Hz and terminal counters are exact.
The elapsed clock updates independently at 500 ms and is not a worker heartbeat.
Progress updates do not rebuild the source list. Bytes are sums of File.size,
not bytes read or measured RAM. A large-file hint starts at 64 MiB; this is a UI
hint, not an admission limit or a claim of parsing support.

### Input formats and adapter boundary

Excel, CSV and JSON are accepted product scope. Each will have a portable reader
behind a common source-to-canonical transformation workflow; acquisition, reading,
template/mapping and publication are separate steps. Source identity must not
require a workbook or sheet. Excel selectors stay specific to the matrix path.
Canonical observations and entity/transaction tables remain independent of input
format. Native C# integration will not duplicate these readers or the JS engine.
The future source contract must expose its shape/capabilities explicitly: matrix
cells, CSV records/fields or JSON object paths. `WorkbookReader` applies only to
the matrix branch. Readers share typed values/diagnostics and tagged source
locations; they must not invent Excel addresses for CSV/JSON or flatten arbitrary
objects to fit the matrix grammar. Normalized snapshots and the bounded XLSX matrix
adapter exist in the separate probe; CSV/JSON source adapters are not implemented.

Accepted Excel scope also includes XLSB, XLS, XLSM, XLTX, XLTM, legacy XLT/XLW and
XML Spreadsheet 2003. These are planned reader capabilities, not formats already
read by the XLSX probe. XML-based OOXML workbooks/templates can share the streaming
XML path after explicit package/content-type validation. XLSB and binary XLS/XLT/
XLW need a binary adapter with separate limits and fixtures; XML Spreadsheet needs
its own schema reader. Do not route them through an XLSX extension alias or silently
coerce all numbers through JS `Number`. Macros are never executed during import.
Encrypted/password-protected inputs, unsupported legacy variants and add-in
packages must yield a specific diagnostic; opening a file in Excel is not proof
that the data reader supports it. Each adapter must expose seek/sequential access,
precision, formula-cache and memory-budget capabilities before claiming support.

| Reader / remaining scope | Configuration and source evidence |
|---|---|
| XLSX | The probe accepts an exact template sheet/rectangle and returns physical cell evidence, exact numeric lexemes and explicit formula/cache availability. General workbook preview/recognition and live Office capture remain separate work. |
| Other Excel formats (planned) | XLSM/XLTX/XLTM use explicit OOXML package profiles; XLSB and XLS/XLT/XLW use a binary adapter; Spreadsheet 2003 XML uses its native schema. Each family needs independent fixtures, typed source evidence and measured limits. |
| CSV | Explicit encoding, separator, quoting and header selection; record/column locations and raw text before declared type conversion. Preview must expose ambiguous separators/decimal conventions and malformed records. Leading-zero codes must not become numbers implicitly. |
| JSON | Explicit record-array/object and field paths; preserve nesting, null/missing/type distinctions and JSON-pointer provenance. No implicit flattening of arbitrary nested objects. Numeric precision, duplicate keys and size/depth limits must be defined before accepting data. |

Parsing success is separate from format recognition, template matching and valid
canonical data. Further readers return typed values, native source locations,
capabilities and diagnostics to the portable domain owner. CSV/JSON schema/adapter
contracts will be fixed with their own M2 fixtures; the current extension catalog does not
claim all those readers exist. CSV/JSON source input is also distinct from JSON/YAML
template definitions and the metadata-only project manifest.

### Large files and processing queue — planned

Accepted requirement: many mixed-size inputs, including XLSX around 80 MB. The
current main viewer only enumerates files; its processing queue/parser connection
is not implemented. The separate XLSX probe already uses one Worker per file with
streaming reads, resource budgets and hard cancellation.
The 500-file intake cap is an explicit prototype bound, not a final batch size.
The shared file owner now supplies `CaptureFile` and `OpenExactRead` for streamed,
revision-pinned sources without the one-MiB whole-read barrier. Format-specific
byte budgets remain explicit. The storage check uses a real 80-MiB binary fixture,
not an Excel parsing benchmark. The queue must call this owner from its background
executor, dispose source streams per item and preserve the source pin across UI
navigation. See [large-source contract](resource-fabric.md).
The following requirements belong to the first real reader/recognition workflow
in M2–M3; M5 extends it with consolidation rather than retrofitting safe execution.
Owner: Data Templates application integration over the shared workspace application,
existing execution/event lifetime and file authority. No queue owned by a tab,
second model loop, filesystem registry or independent durable status database.

- Start with **one heavy file at a time**, under a shared resource budget. Pending
  entries retain descriptors, not opened workbooks or all source buffers. Release
  reader/Worker resources after each item. Increase concurrency only after measured
  memory and UI-latency evidence; do not load every queued file in parallel.
- Run decompression, parsing and matching outside the UI thread in an interruptible
  application-owned executor. Do not silently run a large reader in cooperative
  UI mode if that executor is unavailable. Capture exact module/source revisions
  and settle the existing WebView2 lifecycle/CSP gate before native integration.
- Bound compressed input, expanded ZIP parts, shared strings/styles, cells, nesting,
  preview/result pages and caches separately. Compressed XLSX size is not a RAM
  estimate. Use bounded reads/transfers and incremental parsing where the selected
  reader actually supports it; avoid base64/full-text copies through UI/bridge.
  A reader that materializes whole parts must disclose and qualify that limit.
- Report real per-file stages (read, unpack, parse, recognize, validate, publish),
  counters and diagnostics, plus queue totals by outcome. Unknown totals remain
  indeterminate; byte counts are labeled by stage. Show a stalled/unresponsive
  executor explicitly. CPU/RAM are shown only with actual telemetry, otherwise
  unavailable; source volume and configured budgets are not measured consumption.
- Cancel from outside the busy executor, with hard Worker termination where needed;
  a queued cancel message alone cannot interrupt synchronous parser work. Discard
  incomplete output. File effects already dispatched retain the existing owner's
  read-back/unknown semantics; cancellation does not prove rollback or authorize
  replay. Tune stage deadlines against real large inputs rather than inheriting
  the small M0 probe's deadline.

Queue entries need typed queued/running/completed/failed/cancelled states, with a
separate unresolved-effect outcome where a write cannot be confirmed. Keep phase
separate from outcome. Commands include pause **before the next file**, resume,
cancel current/pending/all and explicit retry of selected failed items. Preserve
verified completed outputs; never automatically retry unknown publication effects.
One failed file produces an item diagnostic and a mixed batch summary, not false
batch success. A failure of the shared executor/storage authority stops dispatch
until resolved. Reordering pending items must not mutate running job pins.

Navigation needs search by name/relative path, format/state/error filters, sorting,
folder/template grouping and quick access to the current item and failures. Each
item links to source preview, applied template/mapping and result/diagnostics.
Navigation remains usable while processing; closing a screen does not stop work.
Context switches cannot retarget an existing batch to another workspace/project.
Use the shared workspace file/resource views, not a separate folder-root registry.

Large collections require bounded pages from the workspace application projection
and incremental event updates, not all rows in the DOM or all bodies in browser
memory. The current 50-row presentation is the initial UI boundary, not proof of
thousand-file processing. Persist batch requests/source associations in versioned
project files through the existing file owner; execution outcomes use the shared
event contracts. No duplicate mutable queue journal in project JSON/localStorage.
Restart recovery requires explicit resume and revalidation of source, template,
dictionary and module revisions; no automatic effect replay or assumed File access.

Qualification must include real 80 MB-class workbooks, multiple queued books,
high-expansion/many-shared-string cases, malformed/over-budget inputs, cancellation
at each stage, repeated runs and memory release. Record peak memory, elapsed time,
UI responsiveness and cancel latency on the actual Windows/Office/WebView2 target.
The sparse 80 MiB selection fixture only proves that intake does not read bodies.
No workbook size or batch-throughput guarantee is made before these measurements.
The memory distinction follows Microsoft's [large spreadsheet parsing guidance](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-parse-and-read-a-large-spreadsheet);
hard cancellation uses the [Worker termination contract](https://developer.mozilla.org/en-US/docs/Web/API/Worker/terminate).

## Planned loader and project UI

Accepted UX scope (2026-10-16); current delivery is limited to source selection
and the viewer structure described above.
Owner: Data Templates for project views and semantics; the shared workspace
application owns selection, file access and operation lifetime. The original
proposal defines logical views and canonical data, but leaves drag-and-drop and
detailed interaction design unspecified. Its M5 UI expansion must not postpone
design or the first usable loader until after the extraction engine is complete.

The M1 source-selection slice establishes the screen/state design and a reviewable
viewer in the existing application. Use one project viewer with the current
chat and Library. Define both narrow Office-panel and wider layouts, navigation,
empty/loading/error/read-only states, progress/cancellation, unsaved edits and
source/project switching before wiring the first complete workflow.

| View | Required interaction |
|---|---|
| Sources | Drag-and-drop and explicit file/folder pickers for the declared Excel families, CSV and JSON; multiple files, visible folder contents and inclusion selection; source queue with recognition status, format-appropriate preview (sheet/block, CSV records, JSON fields), template candidates and per-item diagnostics. Reader support and selection hints must remain distinct. Folder recursion, unsupported files and duplicates must be visible choices/outcomes. |
| Templates | Searchable catalog, recognized signatures, draft/published versions, test results and reusable template creation/editing. Show source-grid roles and a preview before applying a transformation. |
| Parameters and dictionaries | Search/filter by stable parameter ID, name, meaning, type, unit, dimensions and status; aliases, proposals and conflicts remain visible. |
| Mapping | A source-to-canonical map showing labels/codes/units and their target parameters/dimensions; inspect rule scope, priority, confirmation and affected records. Unmapped/ambiguous values are explicit; suggestions become rules only through the shared domain workflow. |
| Data and validation | Source/result grid, normalized records, quality filters and diagnostics; explain a value through its exact source cells, extraction rule and mapping. |
| Results and export | Run status, produced datasets, export contents and supported output formats; distinguish preview, tested draft and published result. |
| Format and help | Versioned canonical format reference with examples, field meanings, folder layout and supported capabilities, available from the viewer and in portable export. |

These are logical sections, not a requirement to squeeze seven permanent tabs
into a narrow pane. The design must make the main sequence clear: select sources
→ recognize/choose a template → resolve mapping → inspect/test → publish/export.
Simple file loading must not require opening every section. Drag-and-drop has
equivalent keyboard-accessible picker commands. Folder selection/import is
adapter-specific: native workspace access uses existing authority; standalone
offers supported file/folder import and states its save capabilities explicitly.
Browser file metadata never grants native path access.

Canonical data documentation is a deliverable alongside the M2 schema, before
mapping controls depend on it. Specify parameter/dimension/period/value/unit,
record grain, observation/total roles, temporal semantics, missing/error states
and provenance, with a worked source → mapping → observation example. Also
document template, dictionary, binding and dataset files, schema versions and
validation errors. Keep this distinct from the implemented metadata-only
`project.json` and M0 probe envelope; neither is the canonical extraction format.
Unknown format versions must fail explicitly. Help examples must match the
versioned schema and fixtures used by the engine.

| Original stage | UI delivery |
|---|---|
| M1 source selection (implemented) | Responsive modular viewer, paged file/folder intake, cancellable enumeration progress and explicit metadata-only states. Persisted source association and typed extraction commands remain open. |
| M2 matrix-language foundation (implemented) | JSON template → typed plan → normalized snapshot → batched canonical observations with lineage, in the separate portable probe. Native integration and full readers remain open. |
| M2 bounded XLSX adapter (implemented) | Explicit file + template → selective ZIP/XML reading in a Worker → exact typed cells → the same canonical batches. Single-file probe only; native source association, queue and 80 MB qualification remain open. |
| M1–M3 | First real end-to-end flow for a supported source: loading, preview, parameter/mapping inspection, diagnostics and draft/test/publish through the common domain service. Include the bounded processing queue, off-UI readers, resource budgets and cancellation from the first reader. Connect the bounded XLSX matrix through streamed workspace sources; add the remaining Excel families (including XLSB), CSV/JSON behind the same workflow and their own fixtures. Deliver usable controls alongside each capability. |
| M4 | Portable picker/import/export and the format/help reference using the same engine and documented capability differences. |
| M5 | Extend the existing queue with advanced batch execution, scoped dictionary/mapping edits, comparison/consolidation, multiple blocks and output workbook templates. |

M1 file read-back establishes stored bytes, not a validated template publication.
Domain draft/test/publish in M3 depends on M2 semantics and exact input/test evidence;
the original plan's M1 publication wording must not bypass that dependency.

Feature code belongs in thematic `app-*.js` modules; `app.js` only binds shared
navigation/rendering. UI and agent operations consume the same typed domain
commands and rules. Drafts, mapping rules and results belong to project files;
view state is not another durable store. Later workspace M7 adoption must reuse
these views/contracts instead of introducing another project shell or file owner.

## Integration gaps and next boundary

Remaining boundaries after the initial source review at `c33da7df` and the
manifest implementation:

| Boundary | Existing owner / remaining work |
|---|---|
| Shared workspace application → project files | Compatible shared application commands, CLI and a thin Office panel metadata view are implemented. Real Windows UI evidence, workspace-owned UI chats, the file tree/resource views and full M7 lifetime/reconnect integration remain open. |
| Live Excel capture | `ExcelResourceProvider` resolves explicit ranges through `ExcelReadService` and the bound document scope. It does not yet feed this probe; capture consistency, unsaved values, dependencies and richer cell availability remain open. |
| Application-owned execution | `AssistantPaneControl` owns the current WebView2 and trusts its exact root document; frame navigation is limited to about:blank/srcdoc. A browser proof does not authorize broadening that policy or prove WebView2 Worker behavior. |
| Publication | Draft/test/publish and exact source/module/dictionary pins must use existing authority/journal owners. Verified manifest creation is not a template publication; probe JSON cannot authorize one. |
| Source input and transfer | The viewer selects files/folders with XLSX/CSV/JSON hints; no source parser, native association or persisted selection is connected there yet. The separate probe executes M2 over normalized snapshots and bounded XLSX. CSV/JSON readers, project closure and complete export/import remain open. |
| Large files and batch execution | Enumeration progress/bounded lists, a separate single-file XLSX Worker and shared workspace source streams with cancellation/progress are implemented. Storage has an actual 80-MiB binary test; the application-owned queue, native execution/CSP integration and representative Excel qualification remain open; see the [processing contract](#large-files-and-processing-queue--planned). |

Next slices, coordinated with the workspace owners:

1. Qualify the shared project commands in Windows/Office and extend the existing
   shared application boundary as workspace M7 consumers are adopted. Keep browser
   and host adapters separate; Office remains Framework 4.8 without a direct
   reference to the net8.0 Runtime assembly. Host-neutral independent work below
   does not wait for Office qualification.
2. Use the implemented [matrix contract](#matrix-language-and-execution-v1) and
   independent oracle and XLSX reader to connect source association, the bounded queue and
   the [loader/project UI](#planned-loader-and-project-ui). Qualify real reader
   capabilities/memory before raising prototype limits. Deliver incrementally
   through M1–M3 using the existing workspace/execution owners.
3. Attach the exact Excel source independently of project identity, then connect
   capture/execution and domain draft/test/publish. Keep source grants, project
   write authority and immutable evidence separate. Multi-file activation remains
   a domain publication step: stage members, verify read-back, then switch the
   manifest pointer through the existing owner; do not claim atomic folder writes.

This sequencing replaces the earlier Office-session-first project-root wording.
The portable JS work remains applicable unchanged. M0 and M1–M3 are incomplete;
full matrix DSL is not claimed. Tracked gates live in
[the risk register](stabilization/RISK_REGISTER.md#data-templates-integration-gates--2026-10-16).

## Verification

`tabular-matrix.test.js` covers a hand-authored 16-record oracle with disjoint axes,
two years, annual roles, exact decimal/missing semantics and rule/cell/mapping
lineage. It rejects invalid schemas/expressions, duplicate JSON keys, context cycles,
ambiguous mappings, incompatible units/grain and missing captures. Larger normalized
fixtures exercise bounded batches, backpressure, cancellation, consumer errors and
failure after provisional output. `tabular-browser.test.js` checks real Worker
acknowledgement/sequence guards, consumer failure/cancellation and complete record
parity with cooperative execution and the offline file-picker report.

`tabular-xlsx.test.js` covers the selected-file reader with an independent XLSX
oracle, numeric precision, formula-cache evidence, XML/ZIP refusals and resource
limits. A 30,000-row fixture forbids whole-File reads and verifies that an unrelated
ZIP member is skipped while capture stays bounded. The existing pinned SheetJS
writer generates an additional interoperability fixture; it is not a runtime
reader dependency. These tests use Node's native streams/deflate and zlib CRC32.
Browser checks exercise the real XLSX File transfer, reject body reads on the UI,
verify byte progress/cancellation/rerun and refuse cooperative fallback. Vendor
changes also require `node tests/web/vendor-gate.test.js`.

Shared project storage: Harness filter `tabular project:` covers create/reopen,
read-only refusal, relocation, malformed metadata, foreign-workspace binding,
external drift, historical bytes and uncertain-write recovery. A focused CLI
smoke verifies the actual create/open consumer across processes, refusals and
discovery through the shared resource catalog.

`workspace application:` covers stale/readonly binding refusal, failure projections,
Office controller replacement, off-UI file dispatch and typed unknown/reconciliation
through the real bridge with a fake Office controller, plus runtime-reset exclusion
while file read-back is pending. `workspace-projects.test.js`
uses the shipped UI and CSS with controlled typed replies for duplicate clicks,
readonly/stale bindings, inert metadata, explicit pinned-path inspection, malformed
receipts and narrow light/dark layout. These do not qualify real Windows/Office UI.

`data-templates.test.js` checks actual browser File and folder pickers, file drops,
all directory batches, cancellation and late callbacks across project changes,
atomic refusal of oversized/failed intake, inert names, duplicate choices and
keyboard navigation. It also covers 50-row pages, page-scoped selection, state
filters/sorting and focus when a filtered row disappears; exact/coalesced progress,
unknown totals, cancellation across views and remount/refresh during a running operation.
An 80 MiB sparse file is selected with browser body-read methods instrumented to
fail; this verifies metadata-only intake, not XLSX parsing or its memory cost.
It verifies all viewer sections at 360/740/1160 pixels in
light/dark themes. Set `RNA_DATA_SCREENSHOTS` to save visual previews. Parsing,
publication and real Windows/WebView2 picker/drop behavior remain unqualified.

For JS changes, run only the affected scripts (Playwright/browser must already be available):

```sh
node tests/web/tabular-core.test.js
node tests/web/tabular-matrix.test.js
node tests/web/tabular-xlsx.test.js
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
