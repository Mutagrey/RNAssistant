# Data Templates

Status: portable M0 proof and M1 project-manifest foundation implemented (2026-10-16).
Owner: Data Templates domain; portable JS in `web/tabular`, shared manifest storage
in Core `TabularProjectStore` over `WorkspaceFileService`.
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

The page remains a capture probe, without template language, published datasets
or Office integration. It does not read a live workbook, parse XLSX/YAML or write
project files. Download produces only probe result JSON. Shared native manifest
creation/opening is described below; no `tabular.*` tool/skill is exposed.

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

The **Рабочая папка** tab accepts an absolute path to an existing directory and an
explicit read-only choice. It creates/opens the domain manifest by a relative path
and shows its name/location as inert text. The current view covers metadata only.
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

## Integration gaps and next boundary

Remaining boundaries after the initial source review at `c33da7df` and the
manifest implementation:

| Boundary | Existing owner / remaining work |
|---|---|
| Shared workspace application → project files | Compatible shared application commands, CLI and a thin Office panel metadata view are implemented. Real Windows UI evidence, workspace-owned UI chats, the file tree/resource views and full M7 lifetime/reconnect integration remain open. |
| Live Excel capture | `ExcelResourceProvider` resolves explicit ranges through `ExcelReadService` and the bound document scope. It does not yet feed this probe; capture consistency, unsaved values, dependencies and richer cell availability remain open. |
| Application-owned execution | `AssistantPaneControl` owns the current WebView2 and trusts its exact root document; frame navigation is limited to about:blank/srcdoc. A browser proof does not authorize broadening that policy or prove WebView2 Worker behavior. |
| Publication | Draft/test/publish and exact source/module/dictionary pins must use existing authority/journal owners. Verified manifest creation is not a template publication; probe JSON cannot authorize one. |
| Transfer | Picker/download works for this probe. File-backed project closure, missing dependencies, source inclusion and complete export/import are not implemented. |

Next slices, coordinated with the workspace owners:

1. Qualify the shared project commands in Windows/Office and extend the existing
   shared application boundary as workspace M7 consumers are adopted. Keep browser
   and host adapters separate; Office remains Framework 4.8 without a direct
   reference to the net8.0 Runtime assembly. Host-neutral independent work below
   does not wait for Office qualification.
2. Attach the exact Excel source independently of project identity, then connect
   capture/execution and domain draft/test/publish. Keep source grants, project
   write authority and immutable evidence separate. Multi-file activation remains
   a domain publication step: stage members, verify read-back, then switch the
   manifest pointer through the existing owner; do not claim atomic folder writes.

This sequencing replaces the earlier Office-session-first project-root wording.
The portable JS work remains applicable unchanged. M0 and M1–M3 are incomplete;
full matrix DSL is not claimed. Tracked gates live in
[the risk register](stabilization/RISK_REGISTER.md#data-templates-integration-gates--2026-10-16).

## Verification

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

For JS changes, run only the affected scripts (Playwright/browser must already be available):

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
