# Data Templates

Status: independent application foundation and early folder export, alongside the
existing M0 proof, M1 manifest/source selection and M2 universal core/XLSX reader
(2026-10-17). The same loader UI and JS core now belong to `web/data-templates`.
The exported source project runs without RNAssistant and includes its own entry,
examples, semantic tests and export tool. A standalone XLSX queue, template editor,
browser project template catalog, browser/native raw drafts and browser result saves
work; recognition, dataset publication and other file readers remain open.

RNAssistant's optional adapter still saves source declarations and controls native
preparation. Exact byte reads, the pinned resource session, WebView2 executor and
acknowledged native output exist; actual Windows host qualification and parser
dispatch remain open. Their completion follows the standalone application boundary.
Owner: Data Templates application; native file authority remains with
`WorkspaceFileService`, with the existing `TabularProjectStore` integration adapter.
This is the canonical domain contract; planned scope is marked separately from
implemented behavior and uses the original proposal's M0–M7 stages.
Retire it only if the domain is removed or its contract moves to another named
owner. Architecture decision: [ADR-0014](decisions/ADR-0014-data-templates.md).

## Application project and data projects

Data Templates is an independent workspace-project web application. RNAssistant
can develop it, test it and use it through tools; ordinary use does not require
an agent, chat, Office or RNAssistant runtime. Keeping its source in this repository
does not make it part of the assistant's business domain. The product host remains
C# 7.3/.NET Framework 4.8; neither that host nor the existing .NET 8 CLI/Harness
is required by the browser application.

| Scope | Authority and responsibility |
|---|---|
| Application source project | `web/data-templates/`: HTML/CSS/JS, UI, core, Worker, examples, tests, docs and `application.json`. Agent edits ordinary workspace files. |
| User data project | Separate files: `project.json`, sources, templates, dictionaries, mappings and results. Selecting a project does not load executable JS or grant filesystem/native access. |
| Data Templates application | UI and processing commands, processing queue, recognition, readers, language, validation, canonical results and export. This is the target owner even where a feature is not implemented yet. |
| Browser adapter | User-selected Files, browser execution/save capabilities and explicit limitations. No RNAssistant globals, path grants or native credentials. |
| RNAssistant adapter | Agent workflow, generic workspace file/history owners, exact source capabilities, isolated execution and optional Office. The preparation queue supplies immutable inputs; it must not become another parser or business queue. |

There is one UI and one engine. Browser `ui/browser.js` and native
`web/js/app-data-templates.js` compose the same owner/view modules; native controls
occupy named shell slots and the Templates view. Domain markup lives once in
`ui/shell.js`, visual tokens in `styles/`, source state/File lifetime in
`ui/loader.js`, draft state in `ui/draft-session.js`, and parsing in `core/`.
Changing the UI does not require a second UI implementation or C# recompilation.
Future functional screens use this same boundary; workspace M7 must reuse it.

The native root currently loads **installed shipped UI only**. Editable application
code must run in a dedicated isolated viewer/executor after scoped package admission;
neither a Worker nor a manifest hash alone grants native authority. Trusted transport
(`web/tabular/native-*`, native DTO/channel/resource owner) stays outside the app
project. The current compiled `TabularRuntimePin` is transitional: preserve its
checks until immutable per-run project-package capture, host protocol checks,
capability admission, isolation and revocation replace it. That replacement must
allow app/template/operator updates without rebuilding RNAssistant while its host
protocol stays compatible. See [migration gates](stabilization/MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16).

Skills are development/use instructions for the agent. Tools operate on project
files, tests and the application's typed commands; they do not duplicate the JS
importer in C# or make an agent run part of normal file processing.

## Standalone application and export

[Open the application](../web/data-templates/index.html).
It preserves the loader's pick/drop/search/navigation/progress/cancel behavior.
The standalone Templates view validates and edits JSON rules, manages a project-local
template catalog and runs the XLSX queue; the separate core probe remains a
development tool. Native chooser/preparation
controls and project draft commands exist only in the RNAssistant adapter. Its
Templates view can select, edit, save, list and reopen raw JSON drafts through
the same draft session and editor. The root UI does not validate rules or run a
template; those capabilities belong to the isolated application Worker/host path.

In browsers with the File System Access directory picker, the project card opens a
user-selected folder containing a format-2 `project.json` or creates a **new
subfolder** under a selected parent. The browser adapter never overwrites a project
manifest. It retains the selected directory handle only for the current page session.
Other browsers can still download the current draft explicitly.

The application is an ordinary static file project without npm or a bundler.
Export from the repository with this development-only Node command:

```sh
node web/data-templates/tools/export.js /absolute/path/to/new-folder
```

Only the explicit `application.json.files` are captured. The export refuses outside
paths, symlinks, duplicate members, an existing destination and an output inside the
source project. It includes code, examples, tests and the XML-parser license;
it includes no native bridge, chats, grants, credentials or user data. The generated
`application.lock.json` records every member's bytes and SHA-256. It is a package
inventory, not a native permission, passing test or result-publication receipt.
The exporter bounds **application code** to 2 MiB/member, 8 MiB total and 200 members;
these are unrelated to input workbook limits. Failed filesystem writes report failure;
any partial output must be inspected or replaced explicitly, never reported complete.

### Local vendor policy

The standalone application carries its own [vendor lock](../web/data-templates/vendor-lock.json)
and license files. Export verifies every locked file's hash and refuses unlisted vendor
files. It does not depend on RNAssistant's HTML-workspace vendor activation or CDN.
Pinned browser assets are:

| Library | Application owner and use | Current activation |
|---|---|---|
| [ECharts 5.6.0](https://echarts.apache.org/handbook/en/concepts/dataset/) | Results/quality visualization from bounded aggregates | Browser-only lazy `DataTemplatesVendors.load("echarts")`; no chart screen yet |
| [Tabulator 6.5.0](https://www.tabulator.info/docs/6.x/virtual-dom/) | Parameter, mapping and result grids with bounded page input | Browser-only lazy `load("tabulator")`; no grid screen yet |
| [SheetJS CE 0.20.3](https://docs.sheetjs.com/docs/miscellany/formats/) | Future XLSB/legacy Excel adapter and Excel export | Bundled, inactive; Worker reader/export adapter pending |
| sax 1.6.1 | Incremental XML tokens in the existing selective XLSX Worker | Active only in that Worker |
| [CodeMirror 5.65.16](https://codemirror.net/5/) | Standalone JSON and focused expression editing | Local assets load only when the editor opens; application-owned adapter and expression mode |

Charts do not own canonical calculations or transformation rules. Grid sorting and
editing do not become another durable store; the app supplies limited pages and
keeps mapping decisions in its domain state. SheetJS requires a whole workbook in
memory even when preview rows are limited. Its future adapter needs explicit input
admission, one heavy job at a time, Worker termination, typed cell/provenance checks
and format-specific fixtures before XLSB/XLS/ODS or 80 MB-scale support is advertised.
SheetJS parsing is synchronous inside that Worker: the UI can report byte-read and
stage/elapsed progress and hard-cancel the Worker, but must not invent a granular
parse percentage. Large XLSB/XLS may require a different bounded reader.
The selective XLSX reader is the current large-workbook candidate, pending the
[reader-strategy decision](#open-decision-excel-reader-strategy). Existing simple source
selection does not need a grid library. CSV streaming and dialect handling may merit
[Papa Parse](https://www.papaparse.com/docs.html) when the raw CSV reader is implemented; no CSV vendor or adapter is
included now. Fuse.js and a UI framework are not needed for the current bounded
search/static UI. Future shared views receive chart/grid factories from their
composition adapter; they must not reach into browser globals. The RNAssistant
adapter must admit the same capabilities explicitly when those views are connected.
Main RNAssistant WebView admission remains unchanged.

### Open decision: Excel reader strategy

Owner: Data Templates application. The current selective XLSX reader plus SheetJS
for other formats is a provisional approach, **not** a final commitment to maintain
two readers. Before routing production XLSX imports through SheetJS, removing the
selective reader, or claiming 80 MB-class Excel support, make and record an explicit
choice: SheetJS for all Excel formats, a format/size-aware hybrid, or another bounded
reader. An XLSB smoke test and the virtual 80-MiB XLSX fixture do not decide this.

Compare the alternatives on representative real XLSX (including 80 MB-class files,
many sheets, large selected sheets and deep selection ranges), XLSB and legacy XLS,
both singly and in a mixed-file queue. Measure elapsed time, peak process/Worker
memory, bytes read, responsiveness, cancellation latency and honest progress on the
standalone browser and the target Windows WebView2 host. Check supported workbook
variants and exact canonical parity: physical addresses, raw numeric lexemes or an
explicit precision policy, leading-zero identifiers, blanks/errors, formula-cache
availability, provenance and failure behavior. Include maintenance cost and vendor
license/update path in the decision.

Record the result and dispatch rule in this contract or a named ADR before the
integrated Excel import is finalized. If SheetJS replaces the selective path, remove
the old reader and its unused dependencies after parity checks; if both remain,
define when each is selected and make the outcome visible. Until then, neither path
is qualified by this document for representative 80 MB workloads.

On `file://`, the loader and explicit cooperative normalized-input probe work.
Template preflight and XLSX require a real Worker and static HTTP(S) delivery where
file-origin Workers are unavailable; there is no server-side application runtime or
hidden fallback. The browser UI shows this limitation explicitly.
Export verification runs the exported Files/XLSX/Worker scenario outside the repository.
Full data-project import/export, source-file persistence, archive/single-HTML
packaging and Library launch remain separate capabilities to implement.

Folder/ZIP is the target primary distribution; HTML is a derived export. Existing
HTML artifacts may link/launch the application after the capability adapter exists.
Do not make the old HTML aggregate a second mutable store or include 80 MB sources
by silently increasing its 20 MiB package budget. User data is exported separately
and only by explicit selection. [Application guide](../web/data-templates/README.md).

## Current core slice

[The core probe](../web/data-templates/core/index.html) offers three explicit operations through
the same executor: M0 snapshot inspection, [M2 extraction](#language-and-execution-core)
and [bounded XLSX extraction](#bounded-xlsx-reader).
For inspection it runs a normalized, bounded capture through
the same plain JavaScript in a dedicated Worker or explicit cooperative mode.
Select [disjoint-capture.json](../web/data-templates/core/examples/disjoint-capture.json) with
the file picker. It includes two separated value bands and their headers, plus a
right-hand year marker. The result preserves `J12` as row 12 / column 10 and keeps
`P5` / `P12` at their original addresses. No period or total is inferred yet.

For extraction select `Выполнить шаблон` and
[matrix-extraction.json](../web/data-templates/core/examples/matrix-extraction.json). It produces
16 canonical observations with source/rule evidence, including two years and
separate annual totals. The download is a report with at most 20 preview records,
not a published dataset. For a real XLSX select `Прочитать XLSX`,
[matrix-xlsx-extraction.json](../web/data-templates/core/examples/matrix-xlsx-extraction.json)
and [matrix-source.xlsx](../web/data-templates/core/examples/matrix-source.xlsx). The file stays
outside the JSON envelope and is read only inside its Worker. None of these
operations reads live Excel/YAML or writes project files. Native manifest creation/opening is described below; no `tabular.*`
tool/skill is exposed and the main loader is not yet connected to this executor.

| Module | Responsibility |
|---|---|
| `selectors.js` | Bounded geometry and physical-coordinate set operations |
| `snapshot.js` | Closed normalized cell schema, immutable reader, explicit coverage |
| `json.js` | Bounded JSON decoding, duplicate/reserved-key refusal and exact source numeric tokens |
| `decimal.js` | Exact lexical decimals, scale and explicit rounding |
| `sources.js` | Prepared matrix, pull-record and nested JSON adapters with native locations |
| `byte-source.js` | Owned fixed-length byte capability, bounded serial reads, detached buffers and synchronous revocation; Blob is one adapter |
| `expression.js` | Expression syntax tree, type/scope binding to instructions, explicit value conversion and evidence |
| `template.js` | Closed source/binding/dictionary schemas, context dependencies and immutable extraction plan |
| `transform.js` | One canonical observation executor, grain/unit checks, lineage and awaited provisional batches |
| `xlsx-zip.js` | Selective ZIP32 reads, streamed native deflate, size/CRC checks and byte progress |
| `xlsx-reader.js` | Bounded SpreadsheetML adapter, exact numeric lexemes, cache evidence and complete rectangle coverage |
| `engine.js` | Declared bounded operations over immutable input; no DOM or publication |
| `executor.js` | One active job, Worker lifecycle, deadline, cancellation, contract revision check |
| `worker.js` | Fixed shipped modules, declared operations, bounded source pulls and one acknowledged result batch in flight |
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
not reconstructed. Version 1 rejects these new fields/kinds. Cell capture performs no arithmetic. Exact scale/round are explicit expression
operations; date conversion, display formatting, formula calculation and
merge/hidden/filter semantics remain unimplemented.

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
count/bytes; the current probe has eighteen executable/presentation files including
the pinned 50,818-byte sax XML parser. Its license/source hashes are in the shared
[vendor manifest](../web/vendor-manifest.json) and [notices](../web/vendor-notices.md).
Future dependencies require their own size/license/capability review.

## Execution and portability

The executor outlives any result viewer. Closing the viewer does not cancel a
run; cancellation, reset or disposal settles its Promise and releases resources.
It rejects concurrent runs, stale protocol/revision handshakes and foreign job
responses. Worker protocol v5 declares `inspect`, `extract` and `extractXlsx`; old handshakes fail
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
proof. The [native resource session](#native-resource-session) now captures and
serves exact shipped modules against a generated package lock and a compiled hash
pin. Project `engine.lock.json`, extensions and native execution/result receipts
remain open. A Worker alone is not a permission sandbox. Imported/project JS is
unsupported; the root RNAssistant UI must never execute it through this probe.
Dedicated-origin CSP and the native result channel are tested in Chrome;
the WebView2 adapter's enforcement and lifetime still require Windows qualification.

## Language and execution core

This implements the universal **v1 foundation**, not every construction in the
proposal. Serialized language revision is **2**: the former language-1 matrix
schema cannot express the contract and is explicitly rejected. Product stage/v1,
language revision, capture version and Worker protocol are separate numbers.
All shipped consumers/examples use revision 2; there is no legacy interpreter or
implicit orientation adapter. User files are not rewritten or deleted.

### Targeted reconciliation with the original plan

The supplied `RNAssistant_Data_Templates_IMPLEMENTATION_PLAN.md` is design input;
these rules and executable examples are the current contract.

| Plan requirement | Verified existing basis | Required correction / current evidence |
|---|---|---|
| §3.1–3.3 one portable engine and distinct adapters | Plain JS, bounded selectors/cells, Worker; native `WorkspaceSourceQueue.OpenPreparedAsync` uses `WorkspaceFileService.OpenExactRead` | Preserve those owners. `sources.js` prepares matrix/records/JSON inputs; `transform.js` is now the one observation executor. No C# interpreter or new storage/runtime. |
| §6.4 roles independent of physical axes | Old compiler permitted row parameter/unit and column period only; `matrix.js` also hardcoded that association | Both axes now accept explicitly typed semantic bindings. Normal/transposed fixtures independently match one business oracle. Old executor removed. |
| §6.7 construction rollout | Defers horizontal axes alongside repeated blocks/joins | It cannot defer §6.4's core invariant. Orientation independence is implemented now; richer layout discovery, reusable blocks and joins remain deferred. This is completion of original scope, not a new feature track. |
| §6.1–6.2 schema → AST → typed bounded plan | Existing expression parser, type checks, context DAG and frozen plan were useful | Preserved and generalized. Plan records native source dependencies and scoped bindings; no executable generated JS. JSON notation only; YAML awaits a decoder into this same model. |
| §5 grain, mappings, precision, explanation | Decimal text, exact mapping, missing tags and provisional batches already existed | Reused. Added record dimensions, native field/pointer evidence, exact scale/round and sink completion/abort. Entity/transaction **outputs** still require their own schema. |
| §13 bounded execution | XLSX streamed reads; 64-record acknowledged output batches; shared source-preparation queue | Records now have a pull-page adapter; source declarations persist in the manifest. Queue prepares bytes only; native parser dispatch/module pins and publication remain open. |

### Closed schema, syntax and immutable plan

An extraction request is `{version:2, kind:"extraction", template, snapshot,
dictionaries, dataset}`. A template has **only and all** these top-level fields:

```text
language: 2
id, revision
source: Matrix | Records | Json
context: {name: {expr: text}}
bindings: {parameter?, unit?, period?, value?, role?, dimensions?: {name: {expr}}}
output: "observations"
policies: {missing:"preserve", mapping:"error", duplicates:"error", conflicts:"error"}
```

Each binding value is `{expr: text}`. Required roles are parameter, unit, period,
value and role; every output iteration must bind each **exactly once** across the
common bindings and applicable axis groups. Dimensions are separately named text
bindings. Missing roles and duplicate/conflicting assignments fail during compile;
there is no object-merge precedence or implicit orientation.

| Source model | Source schema / read syntax / native location |
|---|---|
| Matrix | `{kind:"matrix", sheet, bounds, rows:[{id,select,bindings}], columns:[{id,select,bindings}]}`. A1/row/column selectors keep physical coordinates. `cell("A",row)` / `cell(col,5)` read scalars. Evidence: `{kind:"cell",sheet,row,column,address}`. |
| Records (including normalized CSV) | `{kind:"records", fields:[unique field names], maxRecords}`. `field("code")` uses a declared name, never an Excel column. Capture: `{version:1,kind:"records",source,records:[{field:taggedScalar}]}`. Evidence: `{kind:"field",record:1,field:"code"}`; record is the 1-based logical data record, not a physical CSV line. |
| JSON structure | `{kind:"json", collection:"/report/observations", maxRecords}`. The collection must resolve to an array; `path("/measure/code")` is a literal pointer relative to the current item. Capture: `{version:1,kind:"json",source,document:{...}}`. Evidence: `{kind:"json-pointer",pointer:"/report/observations/0/measure/code"}`. No implicit flattening. |

The matrix capture retains its existing snapshot v1/v2 schema and XLSX adapter.
Records use the same scalar tags (including exact `numeric` lexemes), plus `null`
and `absent`; formula metadata remains matrix-specific. JSON capture retains nested
objects/arrays, text, booleans and null. Its numeric tokens are captured **before**
JS Number conversion by the bounded envelope decoder; private token identity
prevents an input object resembling `{kind:"numeric",lexeme:...}` from impersonating
a scalar. Direct in-memory API numbers must be safe integers; precision already
lost by a caller cannot be restored. Null, absent path, empty text and zero differ.
Pointers use `~0`/`~1` escaping, no wildcards, predicates, scripts or property calls.
Resolving a scalar binding to a structure is an error.

`context` is an acyclic graph evaluated once, in dependency order. It accepts
literals/other contexts and fixed matrix cells; per-record fields/paths and variable
row/column coordinates are unavailable there. A row-group binding can use `row`,
a column-group binding can use `col`, and common matrix bindings can use both.
Records/JSON common bindings run per item. This permits the same period or parameter
role on either matrix axis, or directly in a record field/path. Axis group overlap
fails; disjoint coordinates remain in physical order, without shifting gaps.

1. `RNTabularExpression.parse` returns frozen AST nodes with character spans.
2. `compile` resolves literal names/functions, scope, context references and types.
   Instructions retain expression text, rule path, offsets and typed reads.
3. `RNTabularTemplate.compile` validates the closed source/output/policy schemas,
   orders dependencies, resolves selectors and role ownership, and returns a frozen
   `extraction-plan` v2. Source layout, dependency reads, contexts, regions, numeric
   policy and output budget are separate from source values. No DOM/handle/authority
   is stored in the plan, and it is not a second editable/durable template format.
4. `RNTabularSources.open` prepares detached normalized input for that exact plan;
   `matrix(plan, reader)` wraps the existing XLSX/snapshot reader. Only compiler-owned
   plans and prepared inputs paired with that same plan reach `RNTabularTransform`.
   `records(plan, metadata, count, readBatch)` is the bounded pull-source seam.

Unknown versions, fields, models, functions and policies fail. Diagnostics use a
JSON field path plus expression offset and, where available, a native source
location. Full source line/column mapping and YAML decoding remain open.

### Types, transformations and business semantics

Grammar: JSON string and unsigned integer literals, `row`, `col`, `ctx.name` and
registered calls. No eval/Function, loops, arbitrary member access, imports, network,
DOM, CLR or tools. Arithmetic is provided only through reviewed typed operations.

| Function | Type / behavior |
|---|---|
| `cell`, `field`, `path` | Native scalar access in the correct model/scope; names/paths are literals. |
| `text(scalar)`, `raw(scalar)` | Text key/header or tagged raw value. No automatic trimming/case folding of mapping keys. Required unusable key/header fails. |
| `parse.year(text)`, `parse.monthRu(text)` | Explicit 1000–9999 year and Russian month name/abbreviation. No guessed year/date system. |
| `period.month(year,month)`, `period.year(year)` | Distinct monthly and annual periods. |
| `lookup("parameters" or "units",text,"scope")` | Exact scoped mapping. Dictionary/scope are literals; unknown/ambiguous mappings fail. |
| `decimal(raw,"ru-RU" or "en-US")` | Canonical decimal text or explicit missing tag. Locale affects text only. |
| `scale(value,"1000")` | Exact decimal multiplication by a literal decimal factor. Missing tags propagate. |
| `round(value,2,"half-even" or "half-away" or "truncate")` | Explicit 0–18 fractional places; signed ties have the named behavior. Missing tags propagate. |

Decimal semantics: at most 38 digits and 18 fractional places, exact base-10 strings,
normalized insignificant zeros/negative zero. `decimal.js` uses lexical conversion
and BigInt coefficients; it never uses floating arithmetic for scale/round. Input
and operation results exceeding the budget fail; an overflowing intermediate is
not rescued by a later round. Rounding is never implicit. Russian text has decimal
comma and validated space/NBSP/narrow-NBSP groups; English text uses decimal point
and comma groups. Invalid grouping, whitespace-only text or locale mismatch fails.
Legacy `number` snapshot cells disclose already-rounded IEEE-754 values and reject
unsafe integer keys/values; `numeric` cells preserve raw decimal/exponent lexemes.

Dictionaries remain a separate closed v1 contract: parameters define stable ID,
name, decimal value type, unit, dimension names and temporal meaning; mappings use
exact `(scope,source) → targetId`. Identifiers remain strings. Source unit
must equal the parameter's canonical unit **after explicit mapping**. A required
scale must appear in the value expression; a unit label never triggers arithmetic.
Currency conversion, fuzzy matching, candidate approval and rich parameter-registry
metadata are not implemented. They cannot silently change an existing record.

`dataset:{id,dimensions}` supplies external dimension bindings. A source binding
may supply another dimension; a name present in both is a conflict even when the
values match. No hidden default/override/candidate precedence exists. The resulting
sorted dimension names must equal the parameter's grain. Future external-binding
policies require explicit scope/evidence and conflict semantics, not object spread.

Supported missing reasons are `blank`, `empty-text`, `null`, `absent`, `source-error`
(with detail), and `unavailable` (with reason). Zero is numeric; missing required
parameter/unit/period/dimension text cannot invent a binding. Malformed numeric text
is an error, not a missing value. No error-row skip, fill-down, null-to-zero or review
fallback is implicit. `missing:"preserve"` is the only implemented missing policy.

### Canonical output and explanation

One observation contains `parameterId`, named `dimensions`, typed `period`, tagged
`value`, `unitId`, `role`, `temporalMeaning` and `provenance`. Supported roles are
`observation` and `reported_total`; an annual report total coexists with months
without automatic summation. The uniqueness key within the dataset/run is
parameter + sorted dimensions + full period + role. Source identity is evidence,
not an accidental business key. Duplicate observations fail, even if equal. The
bounded grain index does not implement cross-file deduplication or consolidation.

Provenance contains descriptive source, template ID/revision/language, dictionary
revision, layout region and numeric policy. Each semantic binding includes the
expression text, rule path, transitive context rules, captured values with native
locations and exact mapping decisions. Dimensions explain source expressions or
explicit `/dataset/dimensions/...` bindings. Layout changes alter provenance, while
the canonical business fields remain equal. Stable observation IDs and exact native
resource/module pins belong to later application publication, not this probe.

The real XLSX example explains July 2026 volume using `J12`, parameter mapping
`A12:0012 → production.volume`, unit `B12`, month `J5` and year context `P5`.
`P12` is a separate reported annual total; stored formula/cache metadata is retained.

### Input preparation, execution and result sink

The application pipeline is:

```text
source adapter/descriptor → exact prepared inputs → compile/execute
    → validate records/coverage → result sink completion → separate publication
```

Compilation also has a preflight role: it declares bounded selections/dependencies
**before** expensive input capture. Native preparation must pin source inclusion,
source bytes/revision, template, dictionaries, dataset bindings and shipped module
bytes; the same pinned bundle then executes. UI supplies intent and displays progress;
it owns neither transformations nor success inference. The present probe prepares
immutable JSON plus a selected immutable Blob, not verified native authority.

`WorkspaceSourceQueue` remains the one application preparation owner. Its
`OpenPreparedSourceAsync` must feed the format adapter/executor with retained exact
byte ranges; `Prepared` is not parsed/imported/completed. Native parser dispatch and
isolation/CSP/module-pin enforcement are **not wired yet**. This contract is their
integration seam, not an additional queue, runtime, CAS or journal.

The portable executor API is `extract(inputText,{signal,deadlineMs,onProgress,sink})`,
`extractXlsx(inputText,sourceFile,settings)` and
`extractXlsxSource(inputText,byteSource,settings)`. Both XLSX entry points use one
reader/execution path; only input acquisition differs. The required result sink is:

```text
writeBatch({version:1, sequence, records}) -> awaited Promise
complete(extractionSummary)              -> awaited Promise; validate/finalize staging
abort(error)                            -> synchronous staging revocation
```

Only successful completion resolves the job. `complete` does **not** publish a
project/dataset. Cancellation/deadline also applies while completion is pending.
`abort` runs once on failure/cancel/disposal, including a pending write/completion;
the sink must revoke access immediately and ignore its own late asynchronous work.
It must not throw or return an asynchronous cleanup promise. Publication is a later
application command through existing prepare/commit/read-back owners. Unknown
publication effects remain unknown and are never automatically replayed.

Worker protocol v5 transports provisional batches through internal `onBatch` hooks,
one outstanding sequence at a time. Acknowledgement follows `writeBatch`; final
summary counts must equal acknowledged batches. Low-level engine hooks are transport,
not UI semantics. Partial output, callback failure or late source failure cannot
return a completed summary. The probe keeps only 20 preview records and clears them
on abort; it does not materialize/publish a dataset.

Record input is pull-driven: `readBatch(offset,limit,hooks)` returns exactly
`{offset,records,done}`. Declared count may be `null` (unknown); explicit EOF must
agree with a known count, and total records cannot exceed the plan budget. At the
budget boundary one bounded EOF probe is permitted; any extra record fails. A page
may be short, but an empty nonterminal page is invalid. One page is copied/validated and consumed
before another is requested. A waiting sink stops input read-ahead. The adapter owns
stream disposal and exact source pins. Cancellation is checked around reads, output
and cooperative checkpoints; a dedicated Worker provides hard termination.

| Bound | Implemented limit / qualification |
|---|---|
| Normalized request | 1,000,000 UTF-16 units; detached in memory, **not** a streaming CSV/JSON file reader |
| Matrix capture / selection | 100,000 cells; XLSX streams bytes but retains the bounded rectangle |
| Record pull page | Up to 64 items / 262,144 JSON units including envelope |
| Output / uniqueness index | 10,000 observations; no unbounded grain map |
| Expression | 1,024 characters / 128 nodes / depth 16 |
| Context / groups per physical axis | 32 / 32 |
| Dictionary / mappings | 1,000 parameters or units / 4,000 per dictionary |
| Dimensions / record with lineage | 16 / 65,536 JSON units |
| Output batch | 64 records / 262,144 summed record JSON units, plus array delimiters |
| Deadline | Normalized 1–30,000 ms; XLSX 1–300,000 ms, default 120,000 ms |

Progress separates source byte/item reading, binding, extracting and validation.
Unknown totals stay indeterminate. JSON decoding/compile are bounded synchronous
work; source/output loops yield. Records can pull pages; JSON capture currently
retains its bounded tree; XLSX retains its bounded rectangle. These limitations do
not establish representative 80 MB throughput/RAM or native Office qualification.

### Bounded byte source

`RNTabularByteSource.create({byteLength,label,readRange,close})` creates an owned
capability over already-pinned immutable bytes. It resolves no paths/URLs and grants
no authority. Length and label are immutable; reads return exactly the requested
`ArrayBuffer` or byte view. Reads are serialized, limited to 256 KiB, checked against
the fixed length before dispatch, and copied out of provider-owned buffers before
transfer. Shared buffers, short/oversized/wrong-type replies fail explicitly.
The source passes an abort signal to its provider. `dispose` revokes access and
settles a pending read immediately even if the provider ignores cancellation;
`close` runs once and must synchronously revoke its backing access.

`extractXlsxSource` takes ownership only after admission. Invalid mode/input/deadline,
an already-cancelled signal or a busy/disposed executor leaves ownership with the
caller. An admitted job closes its source on success, failure, cancellation,
deadline or disposal. Closing the source externally also terminates its Worker.
Cleanup failure aborts provisional staging instead of returning a successful result.
One source cannot be reused after the job; explicit retries require new admission.

Worker v5 sends one `{id,protocol,revision,sequence,offset,count}` source request at
a time. The owner returns a transferable binary buffer with the same fields; neither
side accepts a foreign or mismatched response. A result batch/summary cannot overtake
a pending read. Per-job source traffic is bounded to three times its fixed length,
accounting for ZIP metadata rereads. ZIP directories up to their existing 2-MiB
limit are assembled from bounded reads; body decompression still consumes 8-KiB
chunks. The engine compiles/validates its request before the first byte read.

Selected browser Files still transfer directly to the Worker, where `fromBlob`
provides the same byte-source contract. The ZIP/XML readers no longer require Blob
methods. No browser file is inferred to be a workspace path, and no whole workbook
or base64 body crosses the result/control messages.

The shared application's runtime-only `OpenPreparedSourceAsync(queueId,itemId,token,progress)`
now admits a `WorkspaceSourceReader` against that queue's retained pin. It validates
the exact project before and after opening, holds the existing storage maintenance
activity and shared heavy-reader slot, and exposes fixed `ByteLength`, `Label`
and `ReadRangeAsync(offset,count)`. Open/verification/staging/reads run off the caller
thread. One read is allowed at a time, each at most 256 KiB; the cumulative bound is
three source lengths. Invalid ranges are refused before I/O. No consumer can supply
another path/ref. Source-file drift does not replace retained bytes; project drift
refuses admission. Source selection/replacement stays blocked until the reader drains.

Cancellation, cancel-current/all and application disposal revoke the reader
synchronously. `CloseAsync`/`Drained` complete after in-flight I/O and all handles
are closed; maintenance and the shared reader slot remain held through that drain.
Late bytes from a cancelled read are discarded. An I/O/open failure closes the
reader; callers must observe cleanup before dispatching another source. Viewer or
Office controller changes do not own this lifetime. The old unowned stream entry
point was removed.

Sequential CAS encodings use the existing storage owner's explicit `requireSeek`
mode: after verification, one decoded pass is staged into 256-KiB blocks in a single
temporary handle. Encrypted storage stays encrypted with authenticated per-file and
per-block purposes; all blocks retain in-memory SHA-256 checks. RAM holds bounded
buffers, one decoded block and 32 bytes of hashes per source block (plus index/object
overhead). Disk staging is the decoded length plus the existing protection envelope
overhead per block. Unencoded CAS uses its verified seekable handle directly.
Staging has its own typed byte-progress phase, is cancellable and is deleted on
close/failure; it has no reopen/recovery path or durable authority. See
[source stream storage](resource-fabric.md).

The resource session below connects these ranges to the JS byte capability and
pins the shipped modules. This does not yet connect the main WebView2 application
or dispatch parsing from its queue. No new C# interpreter or server runtime is introduced.

### Native resource session

`TabularRuntimeBundle.Capture` accepts the installation's `web` directory from
application configuration, never a workspace/UI path. It verifies the generated
`tabular/runtime-lock.json` against `TabularRuntimePin` compiled into the application,
then validates all 18 approved files and their lengths/SHA-256 hashes. The package
is bounded to 512 KiB; immutable captured bytes serve every later request, including
Worker imports. Changed, missing, linked or foreign modules fail admission. Updating
a source file and its manifest together cannot bypass the compiled pin. Package
updates use `node tools/update-tabular-runtime-lock.js --write` after code review;
`--check` detects a stale manifest or compiled pin. Pinned text assets have LF
checkout rules so Windows gets the same bytes. This is one approved shipped engine,
not a project extension loader or publication receipt.

`WorkspaceTabularResourceSession.OpenAsync` binds this bundle to one prepared source
through the existing workspace application. Opening progress remains a typed callback
from the reader. It creates a random per-session prefix on
`https://rnassistant.tabular.invalid`, a native cancellation/deadline scope (default
120 seconds, maximum 300 seconds), and a host-neutral resource handler:

- `GET` serves the fixed entry/modules, a closed metadata descriptor and serial
  `source?sequence=…&offset=…&count=…` binary ranges, bounded to 256 KiB.
- Canonical method/URL/origin, range bounds and monotonically increasing sequence
  are checked before source I/O; stale/foreign/aliased requests fail explicitly.
  Responses have exact length/sequence/offset receipts, no-store/nosniff, same-origin
  resource policy and no permissive CORS. No filesystem path or base64 body is exposed.
- The entry CSP permits only its exact Worker URL and its descriptor/source fetches;
  inline/evaluated scripts, frames, arbitrary/blob Workers and external fetches are
  blocked. The Worker's response CSP denies network reads and child Workers while
  permitting imports from the captured package. Main UI policy is unchanged.
- `DELETE source` confirms native cleanup only after reader I/O drains. Native
  cancellation, deadline or application disposal revokes the entire prefix. A later
  cleanup request cannot turn that revocation into successful execution.

`runtime.html` is a separate empty execution page; `native-runtime.js` feeds the same
byte-source/executor/Worker APIs with a single explicit XLSX extraction. It verifies
bounded descriptor/range receipts, forwards progress/cancellation and awaits native
cleanup (bounded to five seconds) before returning success. A failed cleanup aborts
even already-completed provisional sink output. `sink.complete` remains staging
validation, never publication. There is no retry, arbitrary URL or cooperative path.
The native owner must always call `CloseAsync` when disposing the realm; browser
cleanup is not its sole lifetime mechanism. The native source deadline revokes reads;
the dedicated host adapter observes source drain/revocation and closes its controller.

The Harness-only loopback host forwards real requests to this production handler;
it is not shipped as an application server. The Chrome test traverses retained CAS →
C# source reader → binary resource response → JS byte capability → real Worker →
the existing 16-record oracle, including after the live file changes. It also checks
CSP, cancellation, sink failure and native revocation after parsing. It does not
qualify Windows/WebView2 request interception, top-level navigation/permissions,
application-owned hidden-realm lifecycle, template/dictionary/dataset pins, durable
result staging/publication or representative 80-MB throughput. Those remain integration gates.

### Dedicated native executor

`AssistantPaneControl` owns a `TabularWebViewExecutor` independently of loader/result
DOM views and Office-controller rebinding. It creates a separate invisible controller
only when `ExecuteAsync` admits an explicit native resource session, bounded input
and `ITabularResultSink`; no parser dispatch or new main-UI bridge command is enabled
by this slice. One job can run per executor, including cleanup. Pane disposal cancels
it and includes its drain task in the existing application shutdown boundary.

The adapter uses an InPrivate profile and the installed WebView2 environment. It
intercepts **all** request sources using the SDK's three-argument filter, including
Worker imports, and delegates them to the pinned resource session. Only the first
exact entry navigation is admitted. Frames, popups, permission prompts, downloads,
script dialogs, host objects and developer tools are disabled/refused. Renderer
failure ends the job. Missing native capabilities fail admission; there is no
network, main-view, cooperative or older-filter fallback. Main UI CSP is unchanged.

`native-channel.js` and the shared `TabularExecutionChannel` use typed protocol v1:

- Native-generated job identity; one `Ready` → `Start` exchange, exact module pin,
  closed canonical envelopes, monotonic output sequences and bounded message sizes.
- At most one unacknowledged batch (64 records, at most 263,168 payload characters).
  Native sink acceptance precedes acknowledgement; validation is separately awaited.
  The host queues messages in delivery order outside the Office STA, with 32 pending
  messages maximum. Source bytes still use the separate binary range transport.
- Reading progress reports actual transferred bytes with an unknown total: selective
  ZIP reads can skip or revisit ranges, so physical source length is not a progress
  denominator. Extraction progress uses records and the known/unknown source total.
- Result JSON remains opaque engine output inside typed batch/result objects. The
  transport checks identity/order/bounds/count receipts; it neither reimplements the
  DSL in C# nor supplies publication validation. Native output is always provisional.
- `Validate` alone is not success. `Succeeded` requires the native source-close
  receipt. Cancel, source revocation, deadline, sink/transport failure or later native
  cleanup failure calls the sink's synchronous, thread-safe `Abort`; a pending write
  must observe cancellation. Late completion cannot acknowledge or restore output.

Cancellation closes the separate native controller without waiting for JS. The owner
then waits for source I/O, resource deferrals, message consumers and sink drain.
`TabularResponseBodyPool` retains at most 2 MiB/64 unconsumed response bodies, releases
each at EOF, and revokes all remaining streams on close. Memory does not accumulate
all source ranges for the duration of a job. Native controller creation itself has
no cancellation API: the source/output are revoked immediately, late creation is
closed before navigation, and the executor remains busy until that startup drains.
Unconfirmed cleanup refuses subsequent jobs until the application owner is recreated.
The shutdown behavior uses the SDK's [controller close API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.close?view=webview2-dotnet-1.0.2903.40), not browser-process termination shared with other views.

Harness checks exercise channel state/receipts, pending-write/validation cancellation,
late output and body budgets. Chrome uses a test-only `chrome.webview` shim to exchange
the real typed messages with C#, the real Worker and retained source; it reproduces
the 16-record oracle, native sink failure, both cancellation points and bad acks.
It does **not** execute the WebView2 adapter. Windows build and actual interception,
sequential response-stream consumption, hidden-controller behavior, startup disposal,
renderer failure and hard-stop latency remain unqualified. No Office/VSTO validation
was run on the development Mac. Exact job input pins, a durable sink and queue parser
dispatch are the next application integration slice.

### Working examples and remaining boundaries

Select any extraction envelope below with `Выполнить шаблон` in the existing probe.
The raw CSV sample uses UTF-8, comma separation, double-quote escaping and its first
record as the header; these are explicit example assumptions, not parser detection.
The raw CSV/JSON samples show the native source structures; the normalized envelopes
are the runnable fixtures. No production CSV byte parser or streaming JSON file
reader is claimed.

| Requirement / contract | Working example | Focused check | Remaining limit |
|---|---|---|---|
| Explicit roles independent of axes | [matrix-roles](../web/data-templates/core/examples/matrix-roles.json), [matrix-transposed](../web/data-templates/core/examples/matrix-transposed.json) | `tabular-universal`: same independent four-record business oracle, native cell lineage; browser Worker | One bounded matrix block, no layout recognition/anchors |
| Disjoint bands, context dependencies, totals | [matrix-extraction](../web/data-templates/core/examples/matrix-extraction.json) | `tabular-matrix`: independent 16-record oracle, DAG/type/bounds refusals | Quarter/custom calendars and repeated blocks deferred |
| Records/fields, dimensions, mapping | [records-extraction](../web/data-templates/core/examples/records-extraction.json), [CSV source](../web/data-templates/core/examples/observations-source.csv) | `tabular-universal`: canonical oracle, native field evidence, missing/conflicts | CSV encoding/dialect/header/malformed-record byte reader pending |
| Nested paths, null/absent, exact numbers | [json-extraction](../web/data-templates/core/examples/json-extraction.json), [JSON source](../web/data-templates/core/examples/observations-source.json) | `tabular-universal`: same oracle, pointers/escapes, exact large numeric token, structure/type refusal | One explicit array, bounded capture; streaming file decoder pending |
| Numeric precision/transforms | `round(...,2,"half-even")` in all four fixtures; exact `scale` tests | `tabular-universal`: signed ties, overflow, exact multiplication; `tabular-matrix`: locale/missing checks | No general arithmetic, currency conversion or implicit rounding |
| Real XLSX → explanations | [matrix-xlsx-extraction](../web/data-templates/core/examples/matrix-xlsx-extraction.json) + [matrix-source.xlsx](../web/data-templates/core/examples/matrix-source.xlsx) | `tabular-xlsx`: independent values, cell/rule/context/mapping/formula evidence; browser Worker | Supported XLSX subset only; other Excel formats still required below |
| Bounded input/output, cancellation, sink lifecycle | `RNTabularSources.records` 130-item pull fixture; existing executor/probe | `tabular-universal`: delayed sink stops reading, malformed page refusal, complete/abort; browser sequence/cancel checks | Queue parser dispatch, durable staging and publication pending |

### Extension and version boundary

Mandatory v1 foundation: distinct source models, independent semantic roles, closed
typed language, immutable bounded plan, explicit precision/missing/conflict policy,
canonical observations/explanation and cancellable backpressured execution. These
are implemented within the stated capture budgets. Full product v1 completion still
requires the reader/integration gates; this section does not close them.

Deferred constructions: YAML decoding, quarter/date/calendar support, repeated/field
forms, anchors/end rules, hierarchical inheritance, reusable fragments, cross-sheet
or cross-source enrichment, joins with declared cardinality, aggregations, external
binding override/review and entity/transaction output schemas. A new construction
adds a named typed source/operator/output variant plus a concrete fixture. It reuses
the schema → AST → plan → prepared source → validated sink boundary; it must not
reintroduce orientation assumptions or another interpreter. Entity/transaction rows
need their own keys/types, not invented numeric parameters.

Named pure functions are compiled by the shipped allowlist with input/output types,
source dependencies, determinism/precision rules, budgets and provenance. They are
not project-supplied JS callbacks. Compatible additions need capability/version
checks and tests; changing existing syntax or semantics requires a language revision
and coordinated consumer switch. Unsupported versions/capabilities fail explicitly.
Capture and output versions evolve independently; protocol v5 and engine revision
`tabular-core-2` are handshake labels, **not content hashes**. Exact module pinning
and permission/isolation for any future executable extension remain separate gates.

## Bounded XLSX reader

Owner: the portable Data Templates source adapter. This is a single-file reader
proof, not the application import/recognition queue. It introduces no source store,
workspace binding, file grant, native bridge operation or publication writer.
CSV/JSON now have native normalized source models; their production file readers
remain open (CSV dialect/encoding and streaming JSON byte decoding).

`executor.extractXlsx(inputText, sourceFile, {signal, deadlineMs, onProgress, sink})`
passes an immutable File/Blob to a dedicated Worker without UI body reads, base64
or whole-workbook JSON. `extractXlsxSource` uses the same reader with the
[bounded byte capability](#bounded-byte-source). The closed version-2 `xlsx-extraction` envelope has
`template`, `dictionaries`, `dataset`, and `source:{formulaPolicy:"stored-cache"}`.
The template's exact `source.sheet/source.bounds` select the capture. The same compiler, mappings,
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

The selective XLSX reader's only parser dependency is
[sax 1.6.1](https://github.com/isaacs/sax-js), unmodified,
with BlueOak-1.0.0 license and exact npm tarball integrity/file hashes recorded in
the shared vendor manifest and the standalone application's vendor lock. It is not
activated in the main WebView or authored HTML pool. Native deflate support is a
required capability, not a JS decompression
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

The current portable `project.json` uses format 2 with exactly five fields:

```json
{
  "kind": "data-templates",
  "formatVersion": 2,
  "projectId": "tp_0123456789abcdef0123456789abcdef",
  "name": "Quarterly reports",
  "sources": [
    { "sourceId": "src_0123456789abcdef0123456789abcdef", "path": "input/report.xlsb" }
  ]
}
```

This version contains metadata and source declarations, not executable modules,
templates, prepared snapshots, queue outcomes or published results. `sources` is a
required ordered array of 0–500 entries containing only `sourceId` and `path`.
Paths are workspace-relative, use `/`, and exclude absolute paths, traversal and
case-insensitive duplicates. Stable domain IDs survive saves/reordering of retained
paths; a new path receives a new ID. They identify declarations, not file authority.
Sources may be missing: preparation checks their actual accessibility and bytes.

Unknown/duplicate fields, wrong types, unsupported versions and invalid identities
fail without rewriting the file. Metadata-only format 1 is explicitly unsupported;
there is no implicit upgrade. To retain an old manifest, inspect it and explicitly
set `formatVersion` to 2 and add `sources: []`, or create a new project at another path.
The manifest limit is 256 KiB UTF-8; names allow 1–200 characters without controls. The file must
be named `project.json`, at the root or in a subdirectory. All containment, link,
case-collision and protected-path checks remain with `WorkspaceFileService`.

Create uses the owner's guarded create and verified read-back; an existing target
is never overwritten or adopted as success. Open observes current bytes and
returns an immutable `TabularProjectSnapshot` with workspace ID, relative manifest
path, exact file reference/hash, domain metadata and current read-only status.
`RequireUnchanged` refuses a foreign workspace or changed file identity/revision.
It checks an observation, not a long-lived lock or future write permission; later
mutations still need the file owner's exact revision guard at dispatch.

`SaveSources` validates the complete selection and manifest size, preserves stable
source IDs, rechecks the observed manifest, then uses the existing guarded
`ReplaceText` and verified read-back. It does not open/copy source files or create a
second store. Changes from another editor fail without overwriting them. Removing
a declaration never deletes its source file or retained history. Unknown writes
use the same explicit reconciliation/open path as creation; they are never replayed.

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
rna project sources --workspace /absolute/workspace --path reports/project.json --sources-json '["input/report.xlsb","input/data.json"]'
rna project draft save --workspace /absolute/workspace --path reports/project.json --name "Mapping" --input /absolute/mapping-draft.json
rna project draft list --workspace /absolute/workspace --path reports/project.json
rna project draft open --workspace /absolute/workspace --path reports/project.json --file draft-1760000000000-0123456789abcdef0123456789abcdef-Mapping.draft
```

These commands need no chat, model, workbook or browser. Files are discoverable
through the existing `resources`/`open` operations. Draft commands create,
list and read raw snapshots; template validation, test, publication and
multi-file activation remain open.

The standalone browser keeps raw template draft snapshots as separate UTF-8 files
under the chosen project's `drafts/` directory. Each explicit save creates a new
`draft-<timestamp>-<uuid>-<label>.draft` file (at most 1,000,000 bytes), then reads
it back exactly. An invalid JSON draft can be saved for later correction. The app
lists the 50 newest matching files without reading all draft bodies; opening a
snapshot reloads it through the existing template-validation path. Before listing,
opening or saving, it re-reads and compares the exact `project.json` text observed
when the folder was selected. A changed manifest requires explicit reopen. These
snapshots are project-local files, not entries in format-2 `project.json`, validated
templates or published datasets. Browser File System Access has no native
revision-guarded replace; unique snapshot files avoid overwriting other edits.
In addition, its `templates/` folder holds a derived catalog of validated templates.
Each stable template ID has immutable, uniquely named JSON version files; a
`.removed` event archives it without deleting its history, and restore appends a
new validated version. The UI can import up to 50 JSON templates at once, open one
for editing, search/page the catalog and append a version after Worker preflight.
Files are at most 1,000,000 UTF-8 bytes and the folder is bounded to 1,000 entries.
The browser adapter checks the observed project manifest and expected template head
before writing, then reads back the exact new file and checks its visible head.
An uncertain effect identifies its exact path for manual reconciliation; it is not
retried automatically. `project.json` stays format 2 and does not mirror this
derived index. These are project-local validated versions, **not** tested/published
templates or native project-indexed templates. The editor still has one active JSON
draft; changing templates asks about unsaved edits.
Parameters, units and scoped mappings currently live inside each template JSON.
Structured add/edit/delete uses the same validated draft and Worker preflight;
deletion refuses referenced parameters/units. A project-wide shared dictionary
requires a separate versioned contract and migration decision rather than a second
implicit mutable copy. Source-value suggestions and published bindings remain open.
RNAssistant's typed `saveTabularProjectDraft`, `listTabularProjectDrafts` and
`openTabularProjectDraft` operations use the selected binding, manifest path and
application revision. `TabularProjectStore` checks manifest drift and delegates
all file effects to `WorkspaceFileService`; there is no second draft registry.
Save is create-only, checks strict UTF-8 and the same 1,000,000-byte limit,
and returns success only after read-back. The request names its unique target
before dispatch, so an unknown result can be inspected at that exact path;
the selected project is revoked until explicit reconciliation. List reads
metadata only, caps the directory at 1,000 entries
and returns the 50 newest matching names. Read accepts only a snapshot filename
and returns raw text, including invalid JSON. A changed manifest requires
explicit project reopen. Native raw-draft UI is available; project-indexed template
lifecycle remains open.

The standalone browser can explicitly copy a completed extraction into the selected
project's `results/result-<timestamp>-<uuid>-<source>/` directory. `data.json` is the
unchanged version-1 extraction export; `receipt.json` records the project ID, source
path, template filename, record count, byte length and SHA-256 digest of each 1 MiB
chunk. The app copies and reads back in bounded chunks, checking the manifest both
before and after the copy; it writes and reads back the receipt **last**. Listing
shows at most 50 complete results among the 100 newest result folders, and reports
incomplete/damaged folders for manual inspection. Opening a saved result verifies
every chunk again before offering a download. Copy and verification show progress
and support cancellation; an interrupted write is removed when possible, while
failed cleanup reports the exact folder as `effect_unknown`. The selected project's
folder permission is session-local. The result receipt is separate from format-2
`project.json`: it does not register a source, validate/publish a dataset, or imply
RNAssistant/native result persistence. Browser File System Access, local quota and
real large-file performance remain environment-dependent.

### Shared application and UI

The **Рабочая папка** tab contains the Data Templates viewer. Its project settings
start expanded, present folder selection and project opening as two steps, and can
be collapsed. They accept an absolute path to an existing directory and an explicit
read-only choice. It creates/opens the domain manifest by a relative path and
shows its name/location as inert text. Project-manifest operations cover metadata and source declarations;
the separate preparation commands retain source bytes as described below.
Office chats and their existing artifacts retain their current ownership; this
tab does not attach workspace files to model context or bind an Excel source.

`src/RNAssistant.Application` is one shared application source module, compiled via
one `.projitems` import in Office Framework 4.8 and the existing CLI Runtime build.
It references Core only. The previous Runtime-only project facade was removed;
CLI `project create/open/sources/prepare/draft` and the bridge now consume `WorkspaceApplicationService`
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
`openTabularProject`, `saveTabularProjectSources`, the three project-draft
operations and `reconcileWorkspaceFile` use the existing token/operation
catalog and worker boundary. UI serializes commands, retains the exact uncertain
path for inspection and does not upgrade an opened read-only binding when a
checkbox is edited. Closing the panel drains accepted work through the existing
bridge lifecycle. No Office gate is held by workspace file work.
Runtime clearing uses the bridge's exclusive request boundary, so it cannot remove
the file journal during an accepted workspace operation; successful clearing also
revokes the selection.

Source saves match the binding, selected manifest path and selection revision,
and refuse unfinished queues. Before saving, the old project/finished queue pins
are revoked; the new project becomes usable only after verified read-back. A stale
save cannot mutate a newer selection. State projects bounded declarations; opening
or refreshing a project never starts a queue. Read-only bindings may use a temporary
selection and prepare sources but cannot persist changes.

Unknown writes and lost replies never trigger mutation retries. **Проверить
состояние файла** invokes explicit existing reconciliation and returns its typed
outcome and existence observation. It does not claim the original attempt succeeded
or open a project implicitly; a subsequent explicit open validates the current file.
Malformed responses cannot report project success or file absence.

### Workspace source preparation queue

`WorkspaceApplicationService` owns a transient queue of 1–500 distinct workspace-relative
paths. `prepareWorkspaceSources` accepts the exact workspace binding, project selection
revision and explicit file/batch byte budgets; `getWorkspaceSourceQueue` and
`controlWorkspaceSourceQueue` address its opaque queue ID. `getWorkspaceState` exposes
that ID for remounts. Queue progress has its own revision and does not invalidate the
project selection. Browser names/metadata do not grant native file access.

The background worker retains one source at a time through `WorkspaceFileService`,
checks the pinned project before and after capture, and exposes only metadata in
pages of at most 50 items. Search, state filters and path/size sorting are supported.
Pending entries hold no opened files or bodies. One reader slot is shared across
applications using the same state directory; waiting is cancellable. Active work
participates in the existing runtime maintenance gate, without creating a chat/run.
Workspace/project replacement and runtime reset refuse unfinished queues. Office
controller changes retain the queue; panel disposal cancels it and drains its worker.

States are `Pending`, `Running`, `Prepared`, `Failed`, `Cancelled`; queue completion
does not imply every item succeeded. **Prepared means an exact retained source, not
recognized, parsed or imported data.** Runtime-only `OpenPreparedSourceAsync` resolves the
accepted pin; the UI receives no selectable resource ref, hash or body. Byte progress
describes the current storage pass. Batch budgets count every successful capture,
including one rejected by a later project check; retries do not reset the budget.

Pause finishes the current item before stopping dispatch. Cancel current/pending/all
and explicit retry of selected failed/cancelled items are separate commands. Local
missing/oversized inputs fail individually; shared storage failures pause dispatch.
Project drift or a missing manifest requires cancelling remaining work and explicitly reopening the project.
No automatic retry or durable queue journal is introduced. Restart requires explicit
workspace/project opening and preparation; saved declarations restore the selection,
and already retained authority/CAS history remains intact.

The CLI accepts `project prepare --workspace <workspace> --path <project.json>
--sources-json '["source/book.xlsx","source/data.csv"]' --file-mib 96 --batch-mib 1024`.
It reports progress and paged terminal results; Ctrl+C cancels and drains. Exit 0 means
all sources prepared, 5 means failures/incomplete work, 130 means cancellation.
Omitting `--sources-json` explicitly prepares the manifest's saved selection;
supplying it uses a temporary selection without saving it. `project sources` saves
declarations separately; an empty array explicitly clears them.
Read-only workspace bindings may retain snapshots without changing project files.
Durable batch requests, common execution events and parser dispatch remain the next
integration boundary. Retaining XLSB/CSV/JSON bytes
does not imply support for parsing those formats.

The loader's **Из рабочей папки** picker uses typed `browseWorkspaceFiles` over the
existing file owner. It navigates one directory at a time, searches names and returns
50 metadata rows per page, sorted within a bounded 5,000-entry scan. Truncation is
visible; no recursive import or content read occurs during browsing. Source choice
is explicit, workspace-relative and bounded to 500 files. Preparation accepts any
accessible nonempty file, including the planned Excel families; this is not a reader
capability claim. UI budgets initially allow 96 MiB per file and 1 GiB per batch and
can be changed explicitly. Read-only project permissions are preserved.

The chosen composition has a separate searchable 50-row view with per-file removal,
explicit **Сохранить в проекте** and **Вернуть сохранённое**. Unsaved/read-only states
are visible; clearing all entries can be saved. A saved composition restores on
explicit project opening and remains distinct from preparation. A lost/malformed
save receipt reveals the existing inspection controls pinned to the attempted
manifest path; no save is resubmitted and no queue is implicitly restarted.

Queue progress stays above project sections. The current item is projected separately
from the filtered page, so progress survives search/paging. The UI offers pause/resume,
cancel current/pending/all, selected retries, state filters and path/size sorting.
Preparation blocks context replacement until work is settled, including paused pending
items. A lost/invalid reply requires state refresh and never automatically resubmits
an action. Restoring `sourceQueueId` reads the existing queue after page reload; late
progress cannot overwrite another context or a newer revision. While running, reads
poll every 500 ms after the previous response, without rebuilding rows for byte
progress alone. Prepared/failed/cancelled counts remain distinct; a finished mixed
batch is not success.

### Source selection UI

The implemented M1 viewer has Sources, Templates, Parameters, Mapping, Results and
Format/help sections. Wide layouts use keyboard-navigable tabs; narrow panels use
one section selector. Project settings are collapsed by default. Sources start
with a drop zone and a short workflow guide; after selection the drop zone becomes
compact and gives space to the file list. Parameters and Mapping remain future
editors; Mapping shows a conceptual diagram, not fabricated rules. The standalone
Templates and Results sections now have a limited XLSX workflow; native composition
still shows their future-state views.

`ui/source-intake.js` owns bounded browser file/directory enumeration and
one immutable format-hint catalog for XLSX, CSV and JSON. The controller owns the
temporary selection; separate views own presentation, as described below.
These modules do not read file contents, parse source data, write project files,
invoke a model or claim an import.
The existing workspace application remains the only native project/file owner.
This browser selection does not invoke the native preparation contracts described
above, create a durable store or change the Office target.

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
path access or copy the source files. Only the separate explicit workspace-relative
selection above can be saved; browser names never become native associations implicitly.

### Loader presentation architecture

The separation applies to the whole loader, including future template, parameter,
mapping, result and format screens. It uses static HTML/CSS and thematic plain-JS
modules without a framework, npm or a bundler.

| Owner | Responsibility |
|---|---|
| `ui/source-intake.js` | Browser enumeration, metadata hints, limits and coalesced progress; no DOM or content parsing. |
| `ui/loader.js` | Portable owner factory: browser File handles, selection/context and enumeration lifetime; passes metadata/actions to presentation. |
| `ui/processing.js`, `ui/browser-core.js` | Standalone queue owner and fixed lazy core loader. One Worker job per selected file; no parser logic or result bodies in the view. |
| `ui/draft-session.js`, `ui/template-editor.js` | Shared raw JSON draft state and CodeMirror presentation. Browser composition supplies Worker inspection; native root supplies no inspector and labels snapshots unverified. |
| `ui/browser-template-library.js`, `ui/template-library-view.js` | Browser project catalog from immutable template versions, archive events and read-back checks; view owns only navigation/filter state. |
| `ui/parameter-editor.js`, `ui/mapping-editor.js`, `core/template-draft.js` | Structured edits to the active template JSON, validated before acceptance; no second dictionary store. |
| `core/template-inspection-worker.js`, `ui/template-inspection.js` | Interruptible, time-limited standalone preflight using the same fixed language compiler and dictionary validator as extraction; no workbook read or project script execution. |
| `ui/result-staging.js` | Provisional per-file sink: acknowledged chunks to origin-private browser files, with explicit bounded memory mode when disk storage is unavailable. Completion gates export; failure/cancel discards staging. This is not durable project storage. |
| `ui/processing-view.js`, `styles/processing.css` | Standalone template picker, status navigation, progress and 20-record preview. Uses shared tokens and operation view. |
| `ui/template-inspection-view.js` | Template scope, rules, parameters and searchable/paged mapping lists; opens dedicated editors. It receives bounded inspection data and owns only DOM/filter state. |
| `ui/browser.js`, `web/js/app-data-templates.js` | Separate browser/native composition. The native adapter translates context and mounts native controls in named slots; it owns no domain parsing. |
| `ui/loader-view.js` | Screen navigation, filters, pages, metadata details and DOM events; no bridge, File handles, readers or job ownership. |
| `app-workspace-sources.js` | Typed native browsing, transient edits to saved relative paths, queue commands/recovery and bounded polling. Saves use the shared workspace controller; no body transfer or durable client store. |
| `app-workspace-sources-view.js`, `app-workspace-sources.css` | Workspace chooser, queue navigation and progress presentation using the same loader tokens; no bridge or job lifecycle. |
| `app-workspace-drafts.js`, `app-workspace-drafts-view.js`, `app-workspace-drafts.css` | Native Templates composition for raw project snapshots; the workspace controller owns guarded typed bridge calls and recovery. No second validator, parser or durable client store. |
| `ui/operation-view.js` | Shared progress component; renders supplied stage, counters, duration and terminal state; delegates cancel/dismiss without a timer or inferred success. |
| `styles/tokens.css` | Loader-wide palette aliases, typography, spacing, radii and card/row density. Inherits the host light/dark theme. |
| `ui/shell.js`, `styles/loader.css` | Single shared semantic markup and component layout; no business state derived from CSS. |
| `styles/browser.css`, native `app-workspace-projects.css` | Host palettes/base styles and native workspace controls respectively. |

View listeners are root-scoped, delegated for dynamic rows and disposed explicitly.
Unmounting/remounting a view neither cancels accepted work nor drops the selection;
the controller survives it. Context replacement remains an explicit owner action.
New functional screens can have their own view module using the same snapshot/action
boundary, without adding another state store. Visual changes should change tokens,
markup or renderers without modifying execution. Native controls, labels, keyboard
navigation, focus restoration, explicit empty/error states and reduced-motion
support remain part of the presentation contract.

The operation card is outside the individual screens and stays visible when
switching sections. The browser-selection card covers **enumeration only**: current path,
file count, total source size, elapsed time and cancellation. A percentage appears
only for a known file count; folder traversal is indeterminate. Intermediate
updates are coalesced to approximately 10 Hz and terminal counters are exact.
The elapsed clock updates independently at 500 ms and is not a worker heartbeat.
Progress updates do not rebuild the source list. Bytes are sums of File.size,
not bytes read or measured RAM. A large-file hint starts at 64 MiB; this is a UI
hint, not an admission limit or a claim of parsing support.

### Standalone XLSX processing slice

The browser composition snapshots selected File handles when a run starts and
locks selection until it ends. It loads only fixed local core modules, runs one
XLSX at a time in a Worker and displays a 50-row page of the queue with search and
status filters. The current file has stage progress and elapsed time; cancellation
terminates its Worker and marks remaining files cancelled. Failed files stay in
the queue with explicit errors; successful files remain available for inspection.
CSV/JSON selection is retained, but this queue reports `READER_UNAVAILABLE` for
them. Unknown Excel extensions are not admitted by source selection.

The `xlsx-extraction` v2 JSON template is selected separately from source files
and limited to 1,000,000 UTF-8 bytes. Before the queue starts, a dedicated Worker
parses the bounded JSON with duplicate-key refusal, compiles the existing language
plan and validates dictionary targets, dataset metadata and XLSX formula policy.
Invalid templates leave Run disabled with a code/path diagnostic. A summary view
shows the selected sheet and bounds, planned record count and axis groups; the
Parameters and Mapping tabs show searchable/paged lists. Parameters, units and exact
scoped parameter/unit links can be added, changed and deleted in the same JSON draft.
The full template is checked in the Worker before each structured edit is applied;
a pending or rejected edit blocks Run. Empty mapping lists are valid drafts, though
required lookups may then fail when processing a workbook. Source-value suggestions
and template publication remain open. Changing the template terminates stale
validation. A JSON source editor retains invalid text
for correction; a focused expression editor selects an `expr` binding and checks
the complete candidate in the same Worker before applying it. Pending or invalid
edits cannot authorize a run. An explicit download exports the in-memory JSON
draft, without claiming project save or template publication. CodeMirror is pinned
locally and loaded only on opening the editor; the app owns its thin wrapper and
DSL mode, not RNAssistant's VBA/JSON editor callbacks. The DSL is the versioned
`expr` language **inside** the template JSON, not a separate persisted document
or interpreter. JSON source is the sole draft representation; the rule picker is
a view of its last validated structure. Mapping controls derive their target
options from the same validated dictionaries and do not own a second draft.
Pure `core/template-draft.js` applies structured changes to the validated JSON;
the shared draft session owns raw text and pending edits, while the standalone
Worker/compiler decides whether a candidate is usable. Explicit project-local
raw snapshots are available in the standalone browser and native Templates UI;
the browser has a project-local versioned template catalog, while native indexed
template activation remains open. Native save/list/open use selected
workspace binding and revision, with exact-file recovery for uncertain writes.
The root UI deliberately labels raw drafts unverified and has no Worker preflight
or Run action. CodeMirror 5 is pinned in both hosts: standalone assets load on
opening the editor, while RNAssistant reuses its existing local instance. The
static no-bundler package adds about 207 KiB of local editor assets;
an editor-major upgrade is a separate decision. This preflight cannot prove
that the selected workbook has the sheet, valid values or resolvable mapping keys:
the same core validation and source-dependent checks still occur per file. XLSX
inputs are limited to 96 MiB each. The provisional sink writes acknowledged
batches to origin-private browser files when that API is available, with up to
256 MiB of serialized records **per file** and 1 GiB retained **per run**. It keeps
only 20 preview records in the view. If disk staging is unavailable, the UI states
that the bounded in-memory mode admits at most 8 MiB per file. Disk errors do not
silently switch modes. Error/cancellation revokes the result and awaits staging
cleanup before the next file; cleanup failure is reported. A successful download is
version-1 `data-templates-extraction` JSON with `source`, `templateFile`, the
engine `summary` and every canonical `record` for that file. This is an extraction
export, not a published dataset. The browser can save that exact export as a separate
project result with the receipt described above. Temporary results are removed when
the user starts another run; closing the tab requests cleanup, but a browser crash
can leave orphaned files. The next disk-stage creation attempts to remove files
older than 24 hours without touching recent files another tab may own. Project
persistence beyond browser extraction files, quota/large-batch measurements and
reliable crash cleanup remain open.
Neither the 96-MiB input cap nor the staged-output budgets qualify real 80-MB
workbooks.

### Input formats and adapter boundary

Excel, CSV and JSON are accepted product scope. Each will have a portable reader
behind a common source-to-canonical transformation workflow; acquisition, reading,
template/mapping and publication are separate steps. Source identity must not
require a workbook or sheet. Excel selectors stay specific to the matrix path.
Canonical observations and entity/transaction tables remain independent of input
format. Native C# integration will not duplicate these readers or the JS engine.
The source contract exposes its shape explicitly: matrix
cells, CSV records/fields or JSON object paths. `WorkbookReader` applies only to
the matrix branch. Readers share typed values/diagnostics and tagged source
locations; they must not invent Excel addresses for CSV/JSON or flatten arbitrary
objects to fit the matrix grammar. Normalized matrix/record/JSON adapters and the
bounded XLSX reader exist in the separate probe; CSV byte reading and streaming
JSON file reading are not implemented. See [the language/source contract](#language-and-execution-core).

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
capabilities and diagnostics to the portable domain owner. CSV/JSON normalized contracts and M2 fixtures are implemented above; the current
extension catalog does not claim their production file readers exist. CSV/JSON source input is also distinct from JSON/YAML
template definitions and the project manifest's metadata/source declarations.

### Large files and processing queue — planned

Accepted requirement: many mixed-size inputs, including XLSX around 80 MB. The
main viewer enumerates browser files and prepares explicitly selected workspace
files through its queue; parser dispatch is not implemented. The separate XLSX probe uses one Worker per file with
streaming reads, resource budgets and hard cancellation.
The 500-file intake cap is an explicit prototype bound, not a final batch size.
The shared file owner now supplies `CaptureFile` and `OpenExactRead` for streamed,
revision-pinned sources without the one-MiB whole-read barrier. Format-specific
byte budgets remain explicit. The storage check uses a real 80-MiB binary fixture,
not an Excel parsing benchmark. The [preparation queue](#workspace-source-preparation-queue)
now uses this owner in the background and retains exact pins across navigation.
Parser dispatch must reuse these pins, dispose streams per item and connect the
existing execution/sink contracts. See [large-source contract](resource-fabric.md).
The following requirements belong to the first real reader/recognition workflow
in M2–M3; M5 extends it with consolidation rather than retrofitting safe execution.
Owner: the independent Data Templates application. Its processing queue owns job
lifetime across views and consumes browser/native file capabilities. RNAssistant
retains native source preparation, file authority and effect receipts only. No queue
owned by a tab, second model loop or duplicated native status database.

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
In RNAssistant, use the existing workspace file/resource views and scope identity.
The browser adapter uses explicitly selected Files/directories; neither creates a
second native folder-root registry.

Large collections require bounded pages from the application queue projection
and incremental updates, not all rows in the DOM or all bodies in browser memory.
Native preparation pages remain an adapter projection. The current 50-row presentation is the initial UI boundary, not proof of
thousand-file processing. Source declarations now persist in the versioned manifest;
batch requests still need versioned project files through the selected project
storage adapter. Native file effects use the existing file owner/event contracts;
never mirror its durable journal into project JSON/localStorage.
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

Accepted UX scope (2026-10-16); current delivery includes source selection,
workspace preparation controls and the viewer structure described above.
Owner: Data Templates for all project views, processing semantics and operation
lifetime; the RNAssistant adapter retains generic workspace authority, exact native
file access and the preparation queue. Browser capabilities use their own adapter. The original
proposal defines logical views and canonical data, but leaves drag-and-drop and
detailed interaction design unspecified. Its M5 UI expansion must not postpone
design or the first usable loader until after the extraction engine is complete.

The M1 source-selection slice establishes the screen/state design and a reviewable
viewer shared by the standalone application and RNAssistant. The native adapter
can place it alongside the current chat and Library. Define both narrow Office-panel and wider layouts, navigation,
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
validation errors. Keep this distinct from the implemented project declaration
`project.json` and M0 probe envelope; neither is the canonical extraction format.
Unknown format versions must fail explicitly. Help examples must match the
versioned schema and fixtures used by the engine.

| Original stage | UI delivery |
|---|---|
| M1 source selection (implemented) | Responsive modular viewer, paged file/folder intake, cancellable enumeration progress, persisted workspace source declarations and guarded save/recovery. Typed extraction commands remain open. |
| M2 matrix-language foundation (implemented) | JSON template → typed plan → normalized snapshot → batched canonical observations with lineage, in the separate portable probe. Native integration and full readers remain open. |
| M2 bounded XLSX adapter (implemented) | Explicit file + template → selective ZIP/XML reading in a Worker → exact typed cells → the same canonical batches. Single-file probe only; queue parser dispatch and 80 MB qualification remain open. |
| M1–M3 (standalone queue/preview slice implemented) | XLSX File + JSON template run through a bounded sequential Worker queue with per-file outcomes, progress, cancellation, preview and full export. Browser disk staging raises the output budget to 256 MiB per file/1 GiB per run; memory mode stays at 8 MiB. JSON/`expr`, parameter, unit and scoped mapping edits work in the active template. The browser has a versioned project template catalog and verified result saves; browser and native Templates UI also keep raw draft snapshots. The native root editor does not validate or execute rules. Source-value suggestions, project-wide dictionary decisions, template test/publish, native template catalog/results and parser dispatch remain open. Add other Excel families (including XLSB), CSV/JSON behind the same workflow and fixtures. |
| Early M0/M4 (foundation implemented) | One portable source project, shared loader, folder export, exported semantic tests and XLSX Worker probe. Complete standalone mapping/validation, persistent data projects and large-result quota/performance qualification before native parser dispatch. Single-HTML/ZIP packaging is still open. |
| M5 | Extend the existing queue with advanced batch execution, scoped dictionary/mapping edits, comparison/consolidation, multiple blocks and output workbook templates. |

M1 file read-back establishes stored bytes, not a validated template publication.
Domain draft/test/publish in M3 depends on M2 semantics and exact input/test evidence;
the original plan's M1 publication wording must not bypass that dependency.

Domain feature code belongs to thematic modules inside the application project.
Native `app-*.js` modules compose adapters; `app.js` only binds shared navigation. UI and agent operations consume the same typed domain
commands and rules. Drafts, mapping rules and results belong to project files;
view state is not another durable store. Later workspace M7 adoption must reuse
these views/contracts instead of introducing another project shell or file owner.

## Integration gaps and next boundary

Remaining boundaries after the initial source review at `c33da7df` and the
manifest implementation:

| Boundary | Existing owner / remaining work |
|---|---|
| Shared workspace application → project files | Compatible application commands, CLI, project UI and a bounded directory chooser are implemented. Real Windows UI evidence, workspace-owned UI chats, richer resource views and full M7 lifetime/reconnect integration remain open. |
| Live Excel capture | `ExcelResourceProvider` resolves explicit ranges through `ExcelReadService` and the bound document scope. It does not yet feed this probe; capture consistency, unsaved values, dependencies and richer cell availability remain open. |
| Application-owned execution | `AssistantPaneControl` owns a lazy, separate WebView2 executor with navigation/permission/interception/lifetime controls and a typed acknowledged native channel. Chrome exercises the C#/JS channel, not the WebView2 adapter; actual Windows behavior and qualification remain open. |
| Publication | Draft/test/publish and exact source/module/dictionary pins must use existing authority/journal owners. Verified manifest creation is not a template publication; probe JSON cannot authorize one. |
| Source input and transfer | Browser intake and the workspace chooser feed preparation; native source declarations persist in the manifest. Exact range transport, a pinned runtime session and the dedicated host adapter are implemented; job input pins and queue parser dispatch remain open. The separate probe executes M2 over normalized snapshots and bounded XLSX. Application folder closure/export is implemented; CSV/JSON readers and user data-project export/import remain open. |
| Large files and batch execution | Enumeration/bounded lists, a separate XLSX Worker, shared source streams and an application-owned preparation queue with loader controls/progress are implemented. Storage has an actual 80-MiB binary test; parser dispatch, durable batch requests, native execution/CSP and representative Excel qualification remain open; see the [processing contract](#large-files-and-processing-queue--planned). |

Next slices, coordinated with the workspace owners:

1. Extend the integrated **standalone** workflow: the XLSX queue, preview,
   validated template/parameter/mapping edits, browser template catalog, project-local
   raw draft snapshots and verified browser result saves now work. Add native catalog,
   shared dictionary decisions and publication through explicit domain commands. Keep real
   file-format capability reporting;
   XLSB/other Excel families and CSV/JSON require their own readers and fixtures.
2. Replace the compiled application-module whitelist with immutable per-run package
   capture and a trusted host adapter outside that package. Add the isolated project
   viewer and common typed application commands. UI and agent must call the same
   commands; app changes must not require a C# rebuild. Keep current checks until
   the replacement's scope/isolation/cancel/drain evidence is complete.
3. Connect the same commands to exact prepared workspace sources and durable native
   staging/publication. Reuse `WorkspaceFileService`, journal and source capabilities;
   attach live Excel independently. Stage members, verify read-back and then switch
   the manifest pointer; never claim atomic folder writes or replay unknown effects.
4. Qualify the affected native paths on Windows/Office and representative 80 MB-class
   batches. Browser/source-project work does not wait for Office qualification.
   Full editors, recognition, format coverage and advanced M5 batch features continue
   inside the independent application with explicit scope and evidence.

This supersedes the previous native-parser-first and M4-late-export sequencing.
No completed low-level work is discarded. The initial folder export and independent
probe are evidence of portability, not completion of the loader's processing UX,
full data-project lifecycle or the large-workbook gates. M0–M4 remain partial.
Tracked gates live in [the risk register](stabilization/RISK_REGISTER.md#data-templates-integration-gates--2026-10-16).

## Verification

`data-templates-export.test.js` captures the application outside the repository,
verifies exact hashes and outside-path/symlink/overwrite refusal, then runs exported
semantic tests, the real file-origin loader and normalized extraction/download.
A static test origin exercises the XLSX/Worker queue, JSON/`expr`/mapping editing, browser project snapshots and draft
download without RNAssistant or assets outside the exported package. Narrow/wide
light/dark views are checked. Native host and 80-MB workbook behavior remain open.


`web/data-templates/tests/universal.test.js` covers the cross-model/orientation oracle, native
provenance, exact transformations, pull-input backpressure and sink lifecycle.
`web/data-templates/tests/matrix.test.js` covers a hand-authored 16-record oracle with disjoint axes,
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

`tabular-byte-source.test.js` covers exact lengths/ranges, provider buffer reuse,
serialization, revocation during a stalled read and invalid replies/cleanup.
`tabular-source-browser.test.js` executes the same 16-record XLSX oracle through
bounded binary range reads from a virtual 80-MiB ZIP envelope. Less than 1 MiB is
transferred; this proves sparse random access, not large-workbook throughput.
Real Workers cover cancel/deadline/disposal, external source revocation, late bytes,
old/foreign/concurrent/out-of-order requests, excessive rereads, premature output,
mismatched source receipts and cleanup failure. The source endpoint is test-only;
these checks do not qualify native transport, module pins or Windows/WebView2.

`tabular native:` in the Harness checks the compiled package pin, exact captured
modules after installation drift, canonical resource routes, byte receipts,
revocation/drain and the independent native deadline. After one Harness build,
`tabular-native-browser.test.js` starts its test-only host and verifies the real
C#/Worker path, CSP and the provisional-result failure cases described above.
Run `node tools/update-tabular-runtime-lock.js --check` after runtime changes.

Shared project storage: Harness filter `tabular project:` covers create/reopen,
read-only refusal, relocation, malformed metadata, foreign-workspace binding,
external drift, historical bytes and uncertain-write recovery. Source checks add stable
IDs across restart/reordering, empty selections, schema/count/byte bounds, missing
file declarations and unknown-save reconciliation without replay. A focused CLI
smoke verifies the actual create/open consumer across processes, refusals and
discovery through the shared resource catalog. Save/open/prepare smoke also checks
cross-process declaration restoration, read-only preparation from saved paths and
explicit clearing without deleting source files.

`workspace application:` covers stale/readonly binding refusal, failure projections,
Office controller replacement, off-UI file dispatch and typed unknown/reconciliation
through the real bridge with a fake Office controller, plus runtime-reset exclusion
while file read-back is pending. Queue checks add serialized capture, bounded pages,
pause/cancel/explicit retry, budgets, exact reads after source drift, project drift,
maintenance exclusion and bridge disposal drain. Source-reader checks add range and
amplification bounds, isolated buffers, selection/maintenance retention, project drift,
off-caller verification, cancellation during a slot wait/read/open and disposal drain.
Storage seek checks cover raw/compressed/encrypted combinations, backwards/cross-block
reads, per-pass staging progress and cleanup on success/cancel/failure. CLI smoke covers mixed outcomes
and read-only preparation without manifest changes. `workspace-projects.test.js`
uses the shipped UI and CSS with controlled typed replies for duplicate clicks,
readonly/stale bindings, inert metadata, explicit pinned-path inspection, malformed
receipts and narrow light/dark layout. These do not qualify real Windows/Office UI.

`workspace-sources.test.js` adds native chooser/queue presentation with controlled
typed replies: 61-file paging, inert filenames, read-only admission, explicit budgets,
filter-independent progress, remount, pause/retry/cancel, invalid/lost reply recovery,
late directory results after a context change and narrow/wide light/dark layouts.
Saved-source checks cover restored composition, bounded search/removal, explicit
save/revert, read-only refusal and lost/mismatched save receipts with pinned inspection.
Harness checks exercise the actual directory owner/bridge without source capture,
including stable pages, protected entries, cancellation and stale binding refusal.

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

`data-templates-export.test.js` also exercises the exported mixed XLSX/CSV/invalid
queue, exact 16-record complete download, result pinning to the original template,
cancel-before-Worker, a complete disk-staged JSON result above 8 MiB, discard after
revoke and the memory fallback, plus narrow result layout. Large-book parsing and publication
remain unqualified.

For JS changes, run only the affected scripts. The draft-session and workspace-draft
command checks are Node-only; the other browser checks require Playwright:

```sh
node web/data-templates/tests/core.test.js
node web/data-templates/tests/matrix.test.js
node web/data-templates/tests/universal.test.js
node web/data-templates/tests/draft-session.test.js
node tests/web/workspace-draft-commands.test.js
node tests/web/data-templates-export.test.js
node tests/web/tabular-xlsx.test.js
node tests/web/tabular-byte-source.test.js
node tests/web/tabular-browser.test.js
node tests/web/tabular-source-browser.test.js
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
