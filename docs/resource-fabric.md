# Resource Fabric

Status: unified direct cutover **complete host-neutral** (2026-09-07), with explicit
unresolved Outlook source-allocation limits and open Windows gates.
This is not bounded-source qualification or beta/RC/release readiness. Execution order and
acceptance gates belong to [Resource MASTER](stabilization/resource-cutover/MASTER.md).
Read its normative documents in order: [URF](stabilization/resource-cutover/UNIVERSAL_RESOURCE_FABRIC.md),
[Authority](stabilization/resource-cutover/RESOURCE_AUTHORITY.md), then
[Evidence/Compiler](stabilization/resource-cutover/EVIDENCE_CONTEXT_COMPILER.md).
This file maps implementation owners, not a fourth normative architecture.
Earlier R61 whole-read/accepted-tool-result binding explanations are superseded.

## Goals

Workspace file slice (2026-10-05): `WorkspaceFileService` is the current file
owner for the development CLI. It assigns stable logical file IDs through the
user-state locator catalog, observes exact UTF-8 bytes into the existing CAS and
`ResourceAuthorityStore`, and journals create/patch/replace before dispatch.
An unresolved possible effect blocks ordinary reads and later writes to that file;
explicit recovery inspects the current bytes and records unknown causality without
replaying the command. External edits advance observation rather than silently
accepting a stale replace. Exact historical text reads use published retained CAS
bytes and fail when unavailable. `WorkspaceFileResourceProvider.ReadExact` exposes
that retained text as a typed `ResourceReadObservation` with whole-view payload,
byte length and exact evidence. It enforces the one MiB text bound and rejects
unpublished revisions and missing/corrupt payloads without a live-file fallback.
Reading an old revision does not refresh or republish the head; its currentness
still comes from frozen authority.

Current file metadata/body reads, mutations and explicit recovery share
`WorkspaceFileService.AcquireFileScope`. It resolves the locator, acquires that
resource's lease, then verifies that the path still names the same file ID (or its
unresolved move intent). A managed move may finish while the caller waits; a
changed binding returns `target_conflict` before observing bytes, publishing a head
or resolving an attempt. It neither allocates a replacement ID nor retries the
operation. The resource lease then excludes another managed move until release;
unrelated files retain independent leases. Immutable historical/snapshot reads
continue through their existing published-CAS path.

Large source files (2026-10-16): `WorkspaceFileService.CaptureFile` retains a file
through the same locator, per-file lease, CAS and authority. The caller supplies
an explicit byte budget for that operation; the one-MiB whole-view budget is not
a source-file size limit. Hashing, retention and verification use fixed 80-KiB
buffers and the same opened source handle. A completed capture returns a
`WorkspaceFileSnapshot` with exact runtime evidence and no body array. Reobserving
the same bytes reuses the revision only after verifying its retained body; it
cannot silently restore a missing/corrupt historical payload from the live file.

`OpenExactRead` validates path identity, published revision and whole raw view,
then verifies the full retained body before returning a read-only stream on that
same CAS handle. It does not acquire a live-file lease for the parser or refresh
the head. Raw bodies support seeking; compressed/encrypted bodies decode
sequentially by default. A consumer may explicitly request `requireSeek: true`:
sequential bodies are staged once into a delete-on-close temporary handle using
256-KiB blocks. Storage encryption/authentication is preserved per block, with a
unique scratch/block purpose and in-memory SHA-256 checks. The last decoded block
is cached; the whole body is never allocated. Disk use is the decoded byte length
plus protection overhead per block; hash metadata costs 32 bytes per block plus
index/object overhead. Raw seekable CAS needs no staging. Scratch has no durable
manifest, recovery or authority and is removed on success/cancel/failure; staging
errors do not fall back to live files or whole-body allocation. The caller must
hold a runtime storage activity until the stream and pending reads drain; the
workspace application's `WorkspaceSourceReader` owns that lifecycle and the shared
heavy-reader slot. Dispose the stream after each item.
`CancellationToken` covers capture, a busy file-lease
wait, verification and subsequent reads. `FileTransferProgress` reports typed
per-pass byte counters (including `Staging`), coalesced to about 10 Hz plus stage boundaries. Its
callbacks run on the caller's thread; the application executor must schedule
capture/open/consumption off the UI and marshal metadata updates to presentation.

Existing `ReadText`/`ReadBytes`, model/preview delivery and guarded text mutations
retain their bounded whole-body contracts. Source capture admission refusal does
not publish a new observation. Cancellation before authority publication cannot
claim a completed source; any unreferenced fully retained CAS body is handled by
ordinary GC. No source registry, second durable cache or queue store was added.
The focused storage test processes an actual 80-MiB fixture with no whole-source
allocation; it does not qualify Excel parsing, UI responsiveness or Office.

An explicit CLI `--read-only` open denies file mutations even when the workspace
already has a manifest. The descriptor and root mount retain that permission for
the open; workspace identity and later explicitly writable opens are unchanged.

CLI exposes relative paths through `common.resources_find/read`; a workspace-bound
filesystem provider now uses the shared Core `ResourceProviderRouter<TProvider>`.
The Office `ResourceGatewayService` also uses this router and retains its authority,
publication and host guards. Both consumers use Core `ResourceFindPage`, semantic
candidates and `ResourceReadProjection`; `ResourceReadObservation` binds the read
to its exact evidence. Provider dispatch remains specific to each host. Historical text
restoration is guarded by a current accepted revision and records the exact source.
CLI `common.resources_find` scans one selected directory, returns at most 200
accessible semantic targets, and accepts an optional case-insensitive filename substring.
It scans at most 5000 entries; truncation reports `complete=false` with a narrowing
hint. Unavailable entries populate `unavailableScopes` and cannot prove an empty
result. `scannedEntries` remains diagnostic metadata, outside the model projection. File descriptors expose metadata/stat without reading content or
claiming an exact revision. The returned targets are sorted within the
captured window, not across a truncated directory. Model discovery has no pagination
or recursive search. The same file owner's application browser uses offsets and
50-row metadata pages within that sorted window; it reports scan truncation separately
from remaining pages. Browsing stats entries without retaining bodies or publishing
source authority. Protected metadata and credential-shaped names stay hidden.
The file branch defaults to `type="file"`. Omitted/null/empty `directory` selects
the workspace root; `/`, `.`, `..` and absolute paths are not aliases. A rejected
directory keeps the file owner's error code and supplies this root-selection hint.
File and skill selectors are closed schema alternatives in both the runtime gate
and model wire: `directory` belongs only to file discovery, `referencePath` only
to skill reads, and the skill branch requires explicit `type="skill"`. Wrong-kind
selectors fail before dispatch rather than being silently ignored. Chat exposes
only the file branch. Existing nullable optional arguments preserve their defaults.
CLI `common.resources_read(target)` returns complete whole UTF-8 text up to 16000
characters with matching retained payload, coverage and exact evidence. File reads
also accept paired `startLine` (1-based) and `lineCount` (1–500), with the same
16000-character excerpt bound. Each call captures the current revision; ranges
from different revisions are never combined into a whole observation. Line slicing
preserves CR/LF/CRLF and Unicode; exact character coverage uses UTF-16 offsets.
The existing text owner still limits the captured UTF-8 file to 1 MiB. Longer
single lines and larger text files require a separate bounded-view extension.
CLI prepares fresh current-run
authority through the same file owner, then passes the frozen tuple and accepted
kernel facts to the existing `ModelContextCompiler`, now in Core. The compiler owns
projection, freshness filtering, budgeting, receipt and frozen format repair for
both CLI and Office; it never calls a live provider. Office supplies its pure domain
projection through an explicit dependency. The separate CLI result projector and
request assembler are removed. A prior-turn read without freshly admitted authority
is unavailable. A body omitted by the compiler, or an incomplete/stale read, cannot
authorize a CLI overwrite. A delivered current excerpt can authorize `files.patch`
only when it contains the exact nonempty `oldText`; the owner still requires the
pinned full-file revision and one matching start across the complete source.
Overlapping starts count separately. Empty, missing and ambiguous anchors return
`invalid_patch`, `patch_not_found` and `patch_ambiguous`; external drift returns
`target_conflict`. Patch leaves untouched line endings intact. Replace/copy/move/
delete retain the complete-read prerequisite.

Verified mutations return exact after-source evidence through the common authority
and context compiler. A partial patch returns the corresponding verified new
excerpt with `complete=false`; a cache entry alone grants no subsequent write.
Prepared file mutations persist that exact observation (prepared-state version 1)
across explicit approval/restart. Unsupported preparations fail explicitly and
require a new read/action; execution never reconstructs authority from a nearby
historical read. `inspect` exposes the persisted context receipt.

CLI resources also accept `type="skill"`: discovery returns up to 50 enabled
host-visible metadata entries with explicit incompleteness; read selects the
published core by skill id or a listed `referencePath`. Core
`SkillPublicationService` supplies the same exact CAS body/descriptor used by
Office's catalog provider. Its evidence depends on the committed catalog root;
raw authoring drift is not activation. The CLI adds that catalog scope and skill
generation to the compiler's frozen authority. Skill reads grant neither file
write authority nor file-read acceptance. The 16000-character skill-read bound
fails explicitly; CLI skill-reference continuation is open scope. See
[skills](skills.md#independent-workspace-runtime) for selection and publication.

Task Lists use the shared Core `TaskListPublication` owner for
`state/conversation/<chat>/task-list`. Their immutable bodies and revisions remain
in the existing ChatStore/CAS. The CLI pins this conversation scope alongside file
and skill scopes; its required current-list projection is admitted only when its
selected artifact matches the frozen published head. Unknown authority yields an
unavailable projection and cannot authorize another save. Workspace mutation
composition persists and reads back the appended revision before publication;
interrupted possible effects are not replayed. Office retains its bound gate and
uses the same logical publisher. This does not introduce a filesystem reader or
an artifact store, and does not implement workspace-owned Markdown Plans.

The file owner publishes mutation effects. Guarded `files.delete` moves an accepted complete
UTF-8 file into `.rnassistant/trash`, verifies its retained preimage and publishes an
unavailable head. Explicit `files.restore` moves that managed preimage back only
when the original path is absent and the latest head is the matching deletion; it
publishes a new revision with `RestoredFrom` provenance. An interrupted delete
requires explicit reconciliation before restore. This is workspace-local managed
trash, not the OS recycle bin or permanent deletion. Guarded `files.move` preserves
the logical file ID, advances its revision, and changes its locator only after an
accepted complete source read. It requires an existing target parent and never
overwrites a target or prior target identity. A durable two-path move intent lets
`recover` inspect either path after an interrupted dispatch; both paths are blocked
from ordinary reads until reconciliation. An uncertain move is never replayed.
Move/delete/restore verify the retained CAS body before preparation/dispatch;
corrupt bytes yield `snapshot_unavailable`, even when file size and envelope match.
Cross-volume move semantics, binary mutations, large files, trash retention policy
and full symlink/hardlink race guarantees remain open M3–M5 work. Bounded raw and
PNG/JPEG application views are implemented below. They and the shared compiler do
not complete workspace-first plan §8.1: large/format-specific views, remaining preview/viewer
consumers and a shared Office/filesystem operation API remain open scope. These
close when the corresponding consumers use the common contracts with bounded
coverage, exact evidence and existing host guards; the target architecture is unchanged. See
[progress](stabilization/PROGRESS.md#workspace-first-implementation--2026-10-04-in-progress).

Development `verify` / `web.verify` select the workspace-bound filesystem provider
through the same router as model resource reads. They observe the current reference,
read its exact retained bytes through that provider, check byte length/hash, refresh
each source and require current evidence against one frozen authority snapshot.
The verifier no longer reads `WorkspaceFileService` directly. Only the captured
bytes are served from a bounded in-memory snapshot on loopback to isolated Chromium;
missing retained bytes fail before browser launch. The browser cannot
open the writable workspace through that server. The tool records exact file
evidence alongside its result; CLI acceptance refreshes those files and requires
the evidence to remain current at completion. Dependency discovery currently
covers quoted HTML script/stylesheet/image/icon references, CSS imports/URLs and
static JS imports, including bounded PNG/JPEG assets. Dynamic asset discovery,
other binary formats, behavior beyond the observation window
and configured functional checks, and remaining preview/viewer routing stay open.
CSV assertions use this same verifier and retained manifest; the separate Node
grader and its workspace reader have been removed.

### Retained web project snapshots — 2026-10-05

Core `WorkspaceWebSnapshotStore` owns immutable manifests and verification records
in the existing `ResourceAuthorityStore` journal and CAS, under the workspace-bound
`workspace-web` scope. It adds no database or mutable source mirror. A manifest pins
the entry and at most 32 relative asset routes / four MiB to exact file revisions
and retained payloads. Metadata is bounded to 128 KiB; each source body is bounded
to one MiB. Members pin their exact `text` or `raw` view; existing text manifests
remain historical text observations. Revision dependencies record provenance and view `Parts` root
the source payloads for CAS maintenance. A staged revision/view is unreadable until
its head publication commits. Missing/corrupt metadata or bodies fail explicitly.

The publication owner checks every member through `WorkspaceFileService` before
issuing a `RetainedWebSnapshot` handle. Only this workspace-bound runtime handle
allows the filesystem provider to reopen its exact members after a source move,
replacement or deletion. The provider delegates byte reads to the same file owner;
historical routes never resolve to new live files or republish their heads.

`verify` and model `web.verify` publish a pending verification record before capture,
attach the manifest before browser launch, then append a terminal revision. Records
include source mode, snapshot, timestamps, bounded errors/hints, browser and optional
session/run/tool-call origin. A killed process leaves `Pending` (no terminal result);
cancellation records `NotRun`. Reading/listing records never retries browser work.
An optional immutable `WebFunctionalChecks` contract is accepted before the run
and included in the pending record. Terminal records retain ordered typed step
results bound to the same snapshot. A passing result requires every requested step
to pass, with observed values matching the operation's typed expectation. Core
rechecks text/numbers, ordered CSV rows and proportional bar geometry at publication.
Smoke-only,
missing/renamed assertions and checks for another entry cannot satisfy that contract.
`verifications` returns at most 20 records in resource order with total, generation
and next offset; concurrent new publications require refreshing the listing.
Terminal results cannot be overwritten. Tool results retain the exact verification
resource reference as runtime metadata; these IDs do not enter model prompts.
Failed browser evaluations also retain the exact source evidence of their
published snapshot. CLI refreshes all those dependencies before freezing context,
including files the model did not explicitly read. The common compiler keeps the
original diagnostic but marks it historical when its sources change; it cannot
be presented indefinitely as a failure of the current files. Preparation failures
without a published snapshot do not claim complete source evidence. Browser error
evidence does not authorize writes or satisfy run acceptance.
Failed published snapshots declare typed `ChangeRequired` recovery against their
exact dependency revisions. A subsequent confirmed whole-resource mutation of a
checked dependency can release the identical verification call; ordinary reads,
no-op writes, unrelated changes, partial impacts and uncertain effects cannot.
The recovery references remain runtime-only and survive canonical history replay.
Retry eligibility does not certify repair: the new browser snapshot and its
assertions must pass independently, with current-file acceptance rechecked at done.

CLI `verify --snapshot <id>` reopens a retained manifest after restart and performs
a new explicitly historical browser check. Its result does not certify current
workspace files; existing agent acceptance still refreshes current file evidence
at `done`. Acceptance reads the exact saved result reference, checks its
session/run/tool-call origin, functional contract and manifest members against the
tool's evidence, then freezes refreshed authority. Model-facing data contains only
semantic check results; the contract digest and resource references stay in runtime.
`verification --id <id>` reads a recorded result without running a browser.
The saved digest describes captured routes/bytes; the manifest's own CAS payload
also pins entry and exact revisions. Additional binary formats and Office UI preview
routing remain open M5–M7 work. Development snapshot preview and retained browser
diagnostics are described below.
The development verifier supports bounded pointer clicks, text/numeric assertions,
input/select changes, CSV uploads, table/export comparison and SVG bar geometry
in one captured document, without caller-supplied evaluator code or navigation.
Inputs/expected outputs are frozen values from a caller-selected contract outside
the writable project. Fixture files and actual downloads exist only in a fresh
browser profile, removed on teardown. Uploads use a file input; export checks click
the real button and observe one completed browser download, named by its browser
UUID rather than a page-suggested path. Permission to download is scoped to that
assertion. The download must come from the snapshot origin or a blob/data CSV,
fit 64 KiB and 700 decoded characters, and match the expected ordered CSV cells.
Malformed/truncated/extra data fails. No project file is opened by this check runner;
all source materialization remains with the filesystem provider and file owner.

### Retained browser diagnostics

Each development verification attempts a viewport PNG after checks/observation
and a typed CDP observation journal. `WebDiagnosticDescriptor` reports `Captured`
with a payload and exact terminal verification reference, or `Unavailable` with
a bounded reason. Older records with an empty diagnostic list have no recorded
capture. A missing browser produces `NotRun` and unavailable diagnostics. Capture
failure or a size limit does not change the check outcome; a screenshot is never
an acceptance assertion or source-read authority.

The existing `WorkspaceWebSnapshotStore` publishes `browser-screenshot` and
`browser-trace` views before committing the terminal head. The primary verification
view's `Parts` roots both payloads in the existing CAS. PNG admission checks its
header, dimensions (at most 2048 per side) and 512 KiB byte bound; it is not a
general image decoder. Trace JSON is limited to 128 KiB and 256 entries, with
explicit `Truncated`/`DroppedEntries`. Names/details are bounded to 128/700 UTF-16
characters. The trace pins verification, snapshot, digest and outcome, and records
ordered lifecycle, navigation, request/response, error and check events. Network
entries omit query strings, fragments and user information; request bodies,
headers, cookies, evaluator code and upload fixture paths are not recorded.
This is a bounded CDP journal, not a Playwright archive or full DOM recording.

The typed application `OpenWebDiagnostic` validates workspace ownership, published
exact reference, whole-view coverage, payload integrity and trace correlation.
Missing/corrupt bytes fail explicitly; reads never rerun the browser, resolve live
files or change the saved check outcome. CLI returns metadata by default and exact
bytes only with redirected `--raw` stdout; `--raw` and `--jsonl` are incompatible:

```text
rna diagnostic --workspace <path> --id <verification-id> --kind screenshot --jsonl
rna diagnostic --workspace <path> --id <verification-id> --kind screenshot --raw > screenshot.png
rna diagnostic --workspace <path> --id <verification-id> --kind trace --raw > trace.json
```

Use `verification --id` to inspect unavailable captures. Binary bodies are excluded
from metadata JSON, and diagnostic references remain runtime/application data,
outside model tool results. Preview does not create these verification records.
Office UI presentation remains part of M7. Caller process loss is covered by the
development lifetime owner below; interrupted verification stays `Pending`, with
no fabricated terminal screenshot/trace or automatic retry.

### Application resource views and development preview

The workspace application service exposes typed `FindResources`, `OpenResourceView`
and `OpenPreviewAsync` contracts for the CLI and later UI consumers. Files still
use `ResourceProviderRouter` → `WorkspaceFileResourceProvider` →
`WorkspaceFileService`; these entry points add no reader or authority store.

```text
rna resources --workspace <path> [--directory <relative-path>] [--query <filename>] [--limit 50] --jsonl
rna open --workspace <path> --path app.js [--view text|metadata|raw|image] [--max-chars 4096] --jsonl
rna open --workspace <path> --path app.js --uri <returned-uri> --revision <returned-revision> --offset <NextOffset> --jsonl
rna open --workspace <path> --path app.js --snapshot <retained-web-snapshot> --jsonl
rna open --workspace <path> --path picture.png --view image --jsonl
rna open --workspace <path> --path picture.png --view raw --raw > exported-picture.png
rna preview --workspace <path> [--entry index.html | --snapshot <id>] [--timeout-seconds 300] [--headless] --jsonl
```

Discovery returns descriptors, a 1–200 item bound (default 50), scanned-entry count
and explicit incompleteness. It does not load source bodies or claim an exhaustive
directory when truncated. `open --view metadata` can inspect directories/binary
files without decoding them. Text uses the existing one-MiB UTF-8 whole-read limit
internally; delivery is 128–32000 UTF-16 characters, default 4096, without splitting
surrogate pairs. Each fragment reports exact reference, coverage, returned/total
characters and `NextOffset`. Continuation requires that exact URI/revision or a
retained manifest, so external edits cannot silently join two versions. Only a
whole body has `complete=true`; the last fragment alone is not whole evidence.
Missing historical payloads fail without a current-file fallback. Retained manifest
members remain readable after live paths move or disappear. These runtime references
are application transport metadata, not model arguments.

Raw views return whole exact bytes up to one MiB, with coverage and payload metadata.
They share the text reader's confinement, identity, mutation lease, unresolved-effect
guards, authority and CAS. Materializing another view of unchanged bytes keeps the
same revision/head. UTF-8 text and raw views share that retained body. Exact reads
require a published matching view; missing/corrupt historical bytes cannot borrow
current files. A retained manifest permits its exact members after move/deletion.
An unmaterialized view is explicitly unavailable, without a hidden read fallback.

Image delivery admits PNG and common 8-bit baseline/extended/progressive JPEG
headers up to 4096 pixels per side, within the same one-MiB bound. The pure Core
`RasterImageHeader` supplies MIME/dimensions and also serves screenshot admission;
it does not decode pixels or create thumbnails. Only an admitted exact image gains
the `image` capability. Other binaries remain metadata/raw resources. The typed
application result carries `ResourceBinaryView`; bytes are excluded from JSON.
CLI emits bytes only with redirected `--raw` stdout, without `--jsonl`. Raw reads
are whole-file, without text offsets. Model discovery does not advertise these
application-only views as model read capabilities; `common.resources_read` remains
complete UTF-8 text. Binary/UI inspection never grants a model write observation.

The browser snapshot uses this exact byte path for both text and PNG/JPEG, with
extension/header MIME agreement for raster assets. HTML image/icon and CSS URL
routes are captured before launch; unsupported/missing dependencies fail explicitly.
Both preview and verify serve the pinned bytes/MIME with `nosniff`. Verification
also rejects incomplete/undecoded HTML images, bounded to 256 elements. This does
not grade image content or establish complete CSS/pixel-rendering correctness.
Context preparation and final acceptance refresh raw dependencies through the same
file owner, so an external image change invalidates a previously passing snapshot.
Neither browser evidence nor raw application reads count as accepted model text reads.

`open` is inert, including for HTML. Neither resource views nor preview append
accepted model reads, alter a conversation or grant file mutation authority.
Preview freezes the existing bounded, coherent web snapshot and opens a fresh
Chromium profile through the same development runner and exact-route server as
`verify`. It serves only captured assets, never the workspace root. It has no
agent/native bridge. Preview-specific CSP sandbox blocks frames, workers, forms,
popups and downloads; CDP denies permissions/downloads and permits only same-origin
GETs, with document navigation pinned to the selected entry. The existing dead
proxy and disabled non-proxied WebRTC policy further constrain browser networking.
Request interception follows the [Chromium Fetch contract](https://chromedevtools.github.io/devtools-protocol/tot/Fetch/).
Diagnostics are bounded to 16 entries of at most 701 characters.

`preview.opened` includes source mode `snapshot`, manifest, captured paths and an
ephemeral URL. The foreground lifetime defaults to 300 seconds, maximum 3600;
the application caller closes it with its cancellation token. CLI Ctrl+C closes
only this viewer (exit 130). Normal expiry returns `preview.closed`/exit 0 after
browser, temporary profile and server teardown. An aborted/closed WebSocket during
a requested observation deadline is classified by the actual tokens: caller cancellation propagates,
own lifetime expiry completes normally. Without a requested deadline, transport
errors remain failures. Missing Chromium returns
`NotAvailable`/exit 4. Browser startup/transport failures return exit 5; `Closed` certifies viewer
lifetime completion, not functional correctness. Preview never publishes a
verification record or satisfies run acceptance. Its URL is diagnostic; the
supported viewer is the owned isolated browser, not a user's existing profile.

The shared development runner delegates Chromium/profile lifetime to the small
`RNAssistant.BrowserHost` process. This framework-dependent .NET 8 helper is shipped
with Runtime/CLI; it has no workspace, resource, model or conversation dependency.
It creates a fresh profile and launches the browser itself. Its private stdin pipe
is held by the caller and is not inherited by Chromium. EOF (including caller
`SIGKILL`) or normal release closes only that browser tree and removes that profile.
There is no persisted PID lookup or global process/profile sweep. The exact-route
HTTP server remains in the caller and its sockets close on caller process loss.

Normal completion awaits the helper; failed/timed-out cleanup is an explicit
failure. The helper has a safety lifetime of 120 seconds for verification, or the
requested preview lifetime plus 60 seconds (at most 3660). It drains browser output
without retaining it. It cannot read or update verification/session records.
Real headless Chromium on Mac covers loss after opening, isolation from another
active viewer and interrupted verification; a controlled process checks loss before
DevTools readiness. Ordinary expiry/Ctrl+C and a published CLI distribution pass.
Simultaneous termination of the helper, OS/machine loss, self-contained packaging
and Windows/Linux behavior are not qualified by these checks; no next-start orphan
sweep is implemented. Existing user browser profiles are never cleanup targets.

This is a development snapshot viewer, not a production localhost application
server. Working previews, additional image/PDF renderers, Office UI integration
remain open. Retained verification screenshots/traces are implemented above.

One resource identity, shared current-state authority, immutable historical evidence
and one model-context compiler serve model reads, HTML and viewers. Reading content
never admits a tool schema, activates a stored package or authorizes a mutation.
Domain owners retain typed guard/dispatch/read-back responsibilities.

## Domain model

### Large document text navigation — 2026-10-14

Current uploaded text/code and extracted Office-document attachments are
resource-only in model requests, including the initial request, preview and format
repair. The compiler excludes their implicit extracted bodies before admission;
the bounded resource index supplies discovery targets, and content enters through
explicit resource reads. Durable originals and their extraction metadata remain
intact. Current image/audio/PDF delivery keeps its existing routing.

Committed attachments of user messages expose a scoped semantic target:
`message attachment: <filename> [sent <UTC timestamp>; uploaded <UTC timestamp>]`.
`ChatResourceReferenceService` derives it from the retained message and attachment
metadata and binds it to that message's exact document `ResourceRef`; it creates
no registry or durable alias. The prompt index prioritizes the latest input and
discovery returns the same target for locally linked originals. Gateway resolves
this target through `ChatArtifactResourceProvider` without enumerating the document
catalog. Unavailable unrelated records therefore cannot block this selected input.
Reads, within-file search, outline and copy share that resolution path.

The provider still verifies document ownership, committed publication, original
metadata and explicit removal; content reads retain exact hash/length checks.
Missing links/metadata, ambiguous labels and damaged originals fail explicitly;
there is no filename/latest-upload or message-body fallback. Same-name uploads
have distinct targets, and an earlier target keeps its original after compaction
and restart because full durable messages retain their resource links. Runtime
URIs remain outside model projections. Incomplete global discovery still reports
`complete=false`; it does not make explicit message attachment targets unreadable.

Document-owned uploaded text/code, authored text/code files and authored Markdown/Plans retain a deterministic
`artifact-(extracted-)?text-index-v3` view under their exact revision. Its CAS
manifest holds 32,000-character parts, UTF-16 character coverage, line ranges,
bounded Markdown headings and advisory C#/VBA/JS/TS declarations. The source is
verified through the protected CAS decoder while the index is built incrementally;
no plaintext temporary file, head, second store or read evidence is created.
The index supports up to 20 MiB of retained UTF-8 text and a single line of that
size. Derived parts remain CAS retention roots. A missing/corrupt part is rebuilt
from the same exact source; unavailable source bytes fail closed.

`common.resources_find` discovers semantic targets under its existing bounded
cross-resource budget. `common.resources_search(target, query)` scans one selected
indexed text completely within the 20 MiB source bound and returns bounded literal
snippets, a total match count, `scanComplete`, `sourceComplete` and a matching
`partNumber`. A negative result proves absence only when `scanComplete=true`.
`common.resources_outline(target, fromPart, limit)` pages exact part/line ranges;
Markdown heading names and code declarations are navigation hints. The code
symbol grammar is deliberately partial (`symbolsComplete=false`), including
minified/very long lines. `common.resources_read` accepts `partNumber` for indexed
artifact text/source and returns at most 32,000 characters with `partCount` and
character-range evidence. A successful selected part reports
`selectionComplete=true` and `complete=false` in the model result; its exact
coverage and `sourceComplete` remain separate. Neither a part nor a
search/outline result grants whole-source mutation authority. Other provider
families retain their own bounds;
this contract does not claim 20 MiB Office live, HTML-member or CLI file reads.

Within-target search requires an advertised `text` or `source` representation.
A VBA project exposes its component inventory as `structure`; search its module
names and code with `common.resources_find(scope=vba, query=...)`, then use
`common.resources_search` on a returned VBA module target for literal occurrences.
Project discovery and tool guidance expose this route. A structure-only target
fails with non-retryable `resource_search_unsupported` and actionable guidance
before a text read; it is never silently searched as a different scope.

Agent `common.file_create` creates a small text/code file with an explicit extension;
`common.file_copy` creates an independent authored `File` from an exact complete
uploaded or authored text source (up to 20 MiB); `common.file_patch` replaces 1–16
unique non-overlapping exact fragments on the selected current authored revision.
Copy accepts an optional output filename to choose an extension such as `.md`;
the rename does not alter the source bytes.
Each call carries at most 32,000 replacement characters in total. The tool never
accepts a runtime URI or a 10 MiB whole-body argument. Copy retains source
provenance and exact bytes; patch preserves supported UTF-8/UTF-16/UTF-32 encoding,
BOM and untouched line endings, rejecting unroundtrippable text. The runtime
checks the selected head before dispatch and verifies the complete new CAS bytes
at read-back before publishing a new immutable version. Unknown possible effects
are not replayed. The model receives a bounded hunk manifest and semantic target;
the original bytes, hashes and exact references remain runtime evidence. Large
authored files use the same indexed parts for subsequent reads/search/outline.

The shared layer currently owns identity, exact revision, view capabilities,
coverage (`character-range`, `line-range`, `page-range`, `cell-range`,
`record-range`), bounded reads and cursors. It does not yet expose one provider
navigation contract. `resources_outline` is an indexed document-text operation;
PDF page rendering selects `pageIndex` separately, while PDF extracted text is
one flattened text view. `PageTextLengths` is not an exact page-to-text offset map.
Adding shared navigation requires a typed optional provider capability with
coordinate kind, exact covered range, completeness and bounded continuation;
page extraction must record exact offsets before PDF text pages can use it.

`sourceComplete` distinguishes a complete retained text representation from a
complete original. A clipped extraction or PDF with unextracted pages cannot
produce whole-source evidence even when all retained text was read. Model result
projection exposes semantic targets, typed bounded content fields and the actual
`coverage` kind and coordinates for a read. Successful `startLine`/`lineCount`
reads also report `selectionComplete=true`: every existing line in the requested
interval was returned, including when it reaches EOF. Their `complete=false`
still denies whole-representation observation; `sourceComplete` concerns the
original source. Exact `rna://` references, revisions, cursors, source hashes and
the internal `offset` remain runtime-only. Literal URI-shaped text inside source,
snippets or headings is preserved as user content, while operational envelopes
are sanitized.

Model `table` reads report requested `offset`/`limit` as `requestedOffset`/
`requestedRows` and actual `record-range` coverage. `selectionComplete=true`
means the returned rows fill the requested window through its limit or EOF;
byte-bounded early stops report false. Model `complete=false` for every bounded
table page, including a terminal page with provider `Complete=true`: a completed
row window is not a whole-resource observation. The table's `totalRows` and
actual coverage give the next safe offset.

Model `common.resources_read` may select an exact unique ATX heading with `section`
and `representation=text` for document Markdown/Plans and complete uploaded
Markdown. This uses the same provider/authority/evidence path and retains only
character-range coverage, even for a complete selected section. It cannot satisfy
a whole-resource refresh requirement. The model result reports
`selectionComplete=true` and `complete=false`; exact runtime evidence may mark
the selected section complete, but its character-range coverage still prevents
whole-resource authorization. Bounds, unsupported sources and ambiguity
fail explicitly; see [Artifact Library](artifact-library.md#implemented-markdown-section-reads--2026-09-08).

Sent originals now have `DocumentArtifactStore` as their immutable publication
owner in the existing document authority journal and CAS (artifact slice 1a).
The retained metadata view roots original/extraction payloads; chat projections
hold links only. `ChatArtifactResourceProvider` projects these records for Gateway
discovery and exact viewers independently of the origin chat. Historical `chat`
in the URI provider namespace does not imply conversation ownership: these refs
carry `DocumentAuthorityId` as owner, and descriptors expose document scope.
Original and extracted payloads must pass CAS integrity verification before any
revision or head is published; availability metadata alone is insufficient.
New Plan snapshots and per-Plan logical heads are also document-owned (2026-09-08).
The existing mutation observer prepares the selected exact base and publishes
snapshot/head read-backs together; chat persistence follows publication and keeps
selection only. Current reads depend on the logical Plan head. Native binding v3
rejects stale selection before dispatch; chat clear/fork no longer publishes a
conversation Plan head. The CLI Plan slice (2026-10-05) extends the same owner to workspace
scope: `ChatArtifact.WorkspaceId`, never a fabricated document identity. Shared
Core Plan preparation/retention/publication owns both document and workspace
lineages; the Office wrapper retains its gate. CLI semantic Plan reads use exact
CAS snapshots and logical-head dependencies through the same frozen compiler.
Current title reads select their exact version; historical reads do not. Chat
projections retain links only. This service artifact path does not yet turn Plans
into mounted `.md` source files. File-backed authored resources, other independent
Markdown ownership and their workspace UI remain pending under
[Artifact Library](artifact-library.md#authorized-document-ownership-cutover--2026-09-07).

`ResourceIdentity` is logical identity; `ResourceRef` adds an exact revision.
`DocumentAuthorityId`, runtime binding and physical locator are separate identities.
A revision ID is not a content hash. Restoring equal bytes creates new lineage
with Parent/RestoredFrom while the existing CAS deduplicates bytes.
VBA journal preparations record the exact known preimage resource when proven.
Restore pins that origin before confirmation, or pins the independently captured
backup resource itself. Equal hashes never select a historical origin.
Unknown heads never advertise an old revision as current. Explicit document artifact
removal publishes typed `HeadKnowledge.Removed`, with no current revision. Discovery,
attachment and mutations exclude that logical identity; immutable historical
publication evidence remains retained. Exact semantics belong to
[Artifact Library](artifact-library.md#document-artifact-removal--2026-10-13).

Descriptors separate physical type, capabilities, views, coverage, schema/mapping
refs and dependencies. `ResourceCoverage` qualifies the observation; a transport
batch is not a complete source. Core contracts live in `Resource*Models.cs` and
`ModelContextModels.cs`. `PayloadRef` is durable; `ResourceLease` is transient access.

## Providers

Core `ResourceProviderRouter<TProvider>` owns canonical provider registration, selection
and URI routing for Office and CLI. It is a router, not the common find/read contract.
`ResourceGatewayService` owns Office-specific
authority, publication, guarded reads and bounded projections; the workspace file
provider delegates discovery/read/frozen authority to `WorkspaceFileService`.
Registered Office owners are chat artifacts/attachments, bound Office
document/VBA, Excel ranges/formulas, conversation state/definitions, typed context
and catalogs. `ContextResourceProvider` discovers attached supplied data in the
conversation scope and Office observations in the exact bound document scope.
Instructions/untyped notes are not resources; display previews never supply bodies.
Whole-read assembly preserves absent text for body-free metadata views. Retaining
tool evidence must not replace a published null payload with an empty-text CAS
payload for the same revision/view/coverage.
HTML data bindings expose disjoint complete, structural and page-view argument
shapes. Structural paths are limited to `$` and explicit object properties; Excel
search scopes remain discovery evidence and cannot substitute for range/table/name
tabular sources.
Model-facing discovery marks Excel search scopes as discovery-only and points value
reads to range/table/name targets. Oversized search reads carry the same explicit
recovery route and prohibit an unchanged retry.
`ResourceSnapshotReadService` reads state/context and retained Office views from
whole captured views or the canonical revision payload. Partial views cannot substitute for whole bodies;
continuations bind logical revision and URI/view, never an equal content hash.
Exact historical reads retain access after drift/removal without moving heads.
Gateway Office continuations require an exact logical reference and reject
cross-revision, URI/view or old hash-bound tokens before provider dispatch.
`ResourceAuthorityService` translates the logical token to the retained physical
view guard only inside dispatch, validates the provider continuation and returns
a logical token. It owns no second cursor/head store. Available exact Office views
use the common snapshot reader even for the last known head; fresh head reads still
observe the provider. Missing/corrupt retained CAS never falls back to live bytes.
An exact revision with only prepared metadata cannot borrow another revision's head
or cached index to become readable; copied context becomes readable after the
owning atomic fork publication.
`ResourceGatewayService.Binary` captures CAS image/thumbnail/PDF-page views using
the existing renderer owners. Providers retain interpretation/materialization.

VBA component source capture now retains the complete bounded source already read
under the document gate in the existing CAS. A returned page still has partial
coverage; the retained whole view enables later exact reads without another COM
read, even after external drift. New unpinned reads still capture live source and
publish its current logical revision. Truncated captures never acquire whole-view
coverage, and corrupt retained bytes never fall back to current Office content.
`VbaEditorResourceService` uses this same Gateway capture and shared download data
plane: `getVbaModule` carries typed metadata and an exact resource reference, not
source code inside `ToolRunResult.DataJson`. The editor only enables complete,
integrity-checked code and keeps the normalized VBA write guard separate from the
raw CAS/transport SHA-256. See [VBA journal](vba-mutation-journal.md#editor-source-reads).
Equal normalized guards do not certify equal immutable bytes: a fresh complete
capture with changed line endings receives a new logical revision. Repeated exact
bytes retain that revision; historical LF/CRLF snapshots are never rewritten.

Model discovery/read uses `common.resources_find/read` with runtime-resolved
semantic targets and exact internal references/continuations. Live document/VBA
search providers return typed, non-serialized scan captures independently of matches.
For HTML, `structure` belongs to the workspace root and lists members; an exact
`HTML file` target exposes complete `source`. If a model requests `structure` on
that file target, the model-facing reader explicitly returns complete `source`
and reports the actual representation. Other unsupported views still fail.
The selected workspace uses `HTML workspace: current`. Its file/data targets keep
the member creation timestamp and name under that working parent. A working read
resolves the selected published head directly; a sibling edit does not force a
history scan or change that target. `snapshotTarget` in discovery/read results is
the explicit retained version: it stays on its exact parent after later edits,
deletion and restart. Historical lookup is bounded to 1,000 snapshot metadata
records; missing bodies, ambiguous labels and incomplete metadata fail explicitly.
Current file evidence depends on the file's published logical head. Verified
changes advance the affected file head; equal unchanged siblings retain theirs.
Delete/recreate gets a new file identity even with equal bytes, and restore
advances affected heads. Older publications without file heads conservatively
depend on the aggregate until that file receives a verified publication.
This projection does not rewrite retained structure bytes or move logical heads.
Ordinary filename/content discovery still searches current members; an exact
qualified HTML query is a point lookup into retained snapshots. `backups` is the
VBA journal scope, not HTML workspace history.
Whole-file HTML replacement requires a complete source observation shown to the
model in an accepted request of the same run. The guard checks the stable file
member identity and exact content hash, so later context reduction or changes to
other workspace files do not force another read. A changed file, another run's
observation, or a read excluded from model context cannot authorize replacement.
Verified HTML writes attach complete current member source observations from the
published aggregate snapshot. The next accepted request carries those file bodies
and their currentness evidence, so a following whole-file replacement can use the
source the model has just seen. The conversation compiler applies the same source/text
carry-forward rule to VBA and document-authored text resources; an older revision is
excluded when the authority head or dependency changes.
For a single complete source that cannot fit the request budget, the compiler keeps
the successful operation/effect receipt and a `bodyIncluded=false` observation
notice with a bounded-read recovery action. Omitted bytes confer no whole-source
write authority; they remain in local CAS. Other request/invariant budget failures
remain explicit and do not silently trim user instructions or task state.
`common.resources_read` accepts `startLine` (1-based) plus `lineCount` (1–500) for
text/source, with an exact 32,000-character maximum excerpt. This uses the existing
revision-pinned whole snapshot (still bounded to 2,000,000 characters), preserves
CR/LF/CRLF bytes, and emits only character-range incomplete evidence. It cannot be
combined with section or row selectors. Semantic metadata projections such as a
workspace root/shared-context archive cannot be bypassed with line reads. A single
oversized line needs a narrower snippet or scope; this is not unbounded streaming.
An exact domain target query such as `Sheet!A1:B20`, `Excel table: Sales`,
`Excel name: Sales`, `Word range: 0:100`, or `PowerPoint slide: 2` is a point
lookup: it resolves that target without scanning unrelated resource catalogs or
inheriting their availability. Provider descriptors retain canonical record paths
as runtime-owned routing. Model and HTML structured reads omit `path` for those
targets; runtime applies the canonical value, while a conflicting explicit value
is a no-effect `Replan` rejection.
Current `document:` and `selection:` targets are singleton point lookups too; their
resolution does not enumerate Excel ranges, tables or defined names. All exact Tool
Result resource references, including produced resources and `relation=result`,
remain durable/runtime evidence and are removed from every model projection.
Generic result prose/data also cannot carry runtime `rna://` values into the model;
semantic discovery is the only continuation path.
Gateway publishes each captured view through the shared authority before binding
bounded match evidence to that exact logical revision. Zero-match scans therefore
publish observed drift too. Complete captures stay in the existing CAS for historical
reads; bounded VBA prefixes retain only character-range coverage. Backup searches
can retain an already-materialized whole body while honestly reporting a truncated
search prefix. Metadata-only discovery neither reads nor publishes source bodies;
it may advertise an existing authority head, never a provider hash as a revision.
Discovery consumes ordinary list continuations before claiming completeness.
`Truncated=true` on a terminal provider page remains incomplete source coverage,
even when a query leaves only one or zero matches. A later complete provider cannot
erase that flag. `complete=false` reports incomplete coverage; `unavailableScopes`
reports unavailable sources. An empty result proves absence only when `complete=true`.
The internal `Empty` and `Partial` conveniences are derived, not independently stored
flags; the model receives neither these duplicates nor `refineQuery`.
Artifact search reports incomplete coverage for a bounded text prefix, truncated
attachment extraction, unavailable expected source, or an unscanned remainder at
the result limit. A missing attachment text read is not converted to empty text.
Artifact/member search matches retain the creation timestamp needed to reconstruct
the exact semantic target even when the item lies outside the bounded list.
The prompt working set admits only complete, JSON-quoted semantic targets within
the whole-index token budget; shown counts reflect admitted rows and omitted
resources have a `common.resources_find` recovery route.
The generic semantic-target resolver refuses incomplete enumeration with
`resource_scope_incomplete` rather than assuming the observed name is unique.
Explicit `message attachment:` targets instead resolve the selected message's
committed original directly, with the binding and original guards described above.
A `document: <title>` target is resolved against the live document provider's
explicit document kind; the exact returned target/title and bound-session guards
still apply. It no longer enumerates unrelated Excel names/tables or artifact
catalogs to establish document identity. Other domain-owned target resolution and
already pinned exact reads retain their own admission rules.
A mutable semantic
target captures its current head on the first read, then pins all internal pages
to that exact revision; it cannot get stuck on discovery's previous observation.
The `catalogs` scope discovers definitions without execution admission.
`CatalogResourceProvider` serves committed metadata, exact skill bodies/reference
Markdown and tool-source JSON children, including historical publications. Its text continuations bind publication
revision plus URI/view through the shared exact cursor rules, not payload hashes;
equal-byte publications/restores remain distinct. Capability reference `action=next`
reconstructs the same logical cursor from durable evidence. Catalog children have
no synthetic heads: the common reducer checks their immutable identity and exact
root-publication dependency. Open leases remain pinned after publication changes.
Model-facing semantic targets are opaque labels copied verbatim from
`RUNTIME_CONTEXT` or `common.resources_find`; they never use a `scheme://` form.
The shared resolver rejects every URI-shaped target (`rna://`, retired `vba://`,
`cas://`, file/network URIs and equivalent schemes) before provider or Office
access. Current-history validation applies the same rule to resource reads, HTML
data bindings and VBA backup restores; retained calls containing such targets are
explicit reset-only, not replayed or silently translated.
`CatalogPublicationService` proves visibility from canonical authority commits,
never prepared metadata alone; missing/corrupt CAS fails with
`RESOURCE_SNAPSHOT_UNAVAILABLE`. Historical reads neither activate a generation nor
heal Unknown authority. Each member read hydrates its root once; public root
projection remains in the catalog owner. Remaining domain-specific read consumers
are tracked below.

Model source inspection now discovers host-visible `tool source` targets through
`common.resources_find` and reads those same catalog children through
`common.resources_read`. It records publication-dependent resource evidence, not
callable admission. The direct authoring-file reader is removed; exact snapshots
and generic bounds are shared with the Library. See
[Model source reads](tool-library.md#model-source-reads).

Prompt inspection uses the same provider: metadata-only keys lead to exact field
children of `prompts` and the source-owned `prompt-defaults` publication. The latter
is captured once at builtin publication, not regenerated on read. Bounded individual
values carry their exact root dependency; current-settings drift cannot replace
published text and reads cannot activate/reset defaults. The direct model settings
reader is removed. Bounds, fields and save authority belong to
[Published prompt inspection](conversation-protocol.md#published-prompt-inspection).

`PromptEditorResourceService` reuses those exact field snapshots for selected UI
source reads via shared downloads. Initialization/settings DTOs expose only controls
and prompt refs; dirty field batches use the same single-use upload service before
the existing guarded settings writer/catalog commit barrier. There is no inline
settings-body fallback or second publication owner. Bounds, stale-draft preservation,
explicit reset/review and cancellation belong to
[Prompt editor transport](conversation-protocol.md#prompt-editor-transport).

`SkillEditorResourceService` uses that published catalog and exact Gateway/CAS
source for Library core/reference editing, including read-only built-ins.
Catalog DTOs contain body metadata only; opening a source pulls its text through
`readSkillSource`. This one read action carries metadata and
a shared bounded download lease, not Markdown or a whole package. It reserves
capacity before hydration and requires the displayed package revision; no authoring
file fallback or model observation is created. Complete-source/read-only, cache
conflict and existing save-guard rules belong to
[Skill Library](skills.md#editor-source-reads). Metadata-only edits explicitly
preserve the guarded body without fetching it. The same owner consumes core batches
and reference upserts as bounded typed UTF-8 JSON through the existing single-use
upload route. Bridge write controls carry only chat/lease/hash; structural/body
validation precedes the existing sequential guarded authoring/commit path.
No upload publishes a resource or creates model evidence. Limits, cancellation,
partial outcomes and draft rules belong to
[Skill mutation uploads](skills.md#editor-mutation-uploads).

`ToolEditorResourceService` reads metadata-addressed Library source through the
same Gateway/CAS and shared bounded download. Custom and source-owned built-in
tool children depend on existing catalog publications; document-local tools prove
their exact live VBA component refs, with no second document catalog authority.
All outgoing Library DTOs omit source bodies. Hydration, draft conflicts and
selected-source cache rules belong to [Tool source reads](tool-library.md#library-source-reads).
Generated builtin human documentation now shares this owner: publication captures
source-owned policy-derived Markdown into CAS parts, and exact `/documentation`
children are read without Office access. UI controls carry only metadata and a
bounded download; generated bodies never enter callable registrations or compact
catalog projections. Bounds, comparison-only verification, cache and cancellation
rules belong to [Tool documentation](tool-library.md#human-documentation-without-model-context-cost).
The same owner consumes the single-use upload route:
ordinary Save and save-before-VBA-install send chat/lease/hash-only
controls and one bounded typed batch. Existing Tool authoring, catalog commit and
document access owners retain their responsibilities. No upload grants execution
or publication authority. Bounds and verified-prefix/draft rules are owned by
[Tool Library](tool-library.md#library-mutation-uploads).

## Excel range reads

`excel.read_range` is removed from catalog, native bindings and the Excel core pack.
Model/manual callers use `common.resources_read` with an explicit semantic target
such as `Excel range: Data!A1:B4`: `text` returns values, `formulas` formulas,
`structure` the existing domain-owned profile, and `table`/`records` bounded rows.
When sheet and A1 address are known, the caller can form this exact range target
and read it directly; metadata discovery is not required first. A search tool is
for locating text, not enumerating values from a known range.
Generated range targets use Excel single-quote syntax, for example
`Excel range: 'Sales Report'!A1` or `Excel range: 'O''Brien'!A1`.
The quotes delimit the reference; doubled internal apostrophes decode to one
literal apostrophe. Separate `sheet` fields and host-enumerated names remain
literal, including spaces and quotes. Double quotes are not alternative reference
delimiters. This follows Microsoft's [cell-reference grammar](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oe376/e531ebe0-a152-4978-a876-28e2a68f746e)
and [worksheet-name rules](https://support.microsoft.com/en-gb/excel/rename-a-worksheet).
Resource URI segments preserve significant boundary whitespace: `Sheet` and
` Sheet ` are distinct identities, with spaces percent-encoded. Whitespace-only,
dot and slash-containing segments remain invalid; providers own semantic names.
Discover used ranges through `common.resources_find`; omitted active-sheet/selection
defaults of the removed tool are not silently translated. `excel.inspect` remains
the bounded workbook-object metadata action.

Range discovery uses the existing `workbook` inspection snapshot, whose sheet
entries include `UsedRange`. The lightweight `sheets` snapshot remains sufficient
for search-scope discovery. Missing used-range metadata is an explicit snapshot
error, not a reason to silently omit a worksheet. Enumeration materializes no cell
values or formulas; a used-range address alone does not prove non-empty contents.
The 200-item inspection bound and 50-item pages remain. Range/table/name/search
catalogs share revision-bound paging: terminal pages preserve source truncation,
and changes to either the descriptors or source coverage invalidate continuation.
Semantic discovery returns at most 20 candidates. Browsing without a query reads
no cell bodies; a query may additionally search the existing bounded document
preview. The Excel preview's 2000-cell-per-sheet ceiling remains unchanged.

Resource reads of range bodies use `ExcelResourceProvider` over `ExcelReadService`
and the bound backend. `excel.summarize_columns` is a separate bounded local
analysis action over selected cells. The 100000-cell raw-read ceiling precedes
values/formulas materialization.
For ranges within that ceiling, the profile keeps existing counts, headers and
ten-row sample; it is not a complete values/formulas representation. Each requested view records exact resource evidence
and complete captured bytes in the existing CAS. Internal text continuations stay
on the first logical revision; retained historical profile reads perform no Office
I/O. Drift observed in a fresh view uses shared authority and evidence reduction.
HTML bindings use the same Gateway/provider, not accepted direct-tool JSON.
Old accepted `excel.read_range` calls fail explicit protocol validation; no alias,
replay translation or public domain-output wrapper remains. Real Windows Excel/STA
and final catalog/model qualification are still open.

### Large Excel range overview

Owner: Excel range provider / read domain. A `structure` read of an explicit range
above 100000 cells returns a typed `range-overview`: the live resolved sheet/address,
row/column/cell counts, `valuesRead=false`, and small `sampleTargets`. The internal
metadata read validates the bound worksheet and rectangle without reading `Value2`
or `Formula`; unexpected cell matrices fail domain validation. An absent worksheet
is an error, not an arithmetic-only successful read.

The plan contains at most nine non-overlapping rectangles, up to three rows by
four columns each, spanning the beginning, middle and end of both axes. All targets
are ordinary quoted `Excel range:` references. No samples are automatically loaded;
the model chooses relevant slices or a different explicit rectangle. This is spatial
orientation, not representative sampling or proof of whole-range totals, missing
values or formula counts. Exact evidence and retained bytes describe the complete
**structure view**, never a values/formulas read of the large range.

Raw reads retain the 100000-cell ceiling. Current `table`/`records` selectors apply
after source capture, so `limit=1` does not bypass it; refusal points to `structure`
or a smaller A1 target. A small cell count is not a token guarantee either: long
cell contents still go through the existing request budget. Internal 32000-character
pages are assembled for complete reads and are not a model-response size limit.

The overview implements orientation and exact slice access, not full large-table analysis.
See the [large-range follow-up](stabilization/BACKLOG.md#large-excel-analysis-without-full-model-context--2026-10-21).
The full-sheet host-neutral fixture covers 17179869184 cells without materializing
them, then reads one selected small slice. Real Excel metadata latency/COM delivery
and live model choice/token cost remain unqualified.

### Large Excel column analysis

Owner: Excel read/analysis domain. `excel.summarize_columns` takes a literal `sheet`,
explicit A1 `address` rectangle and 1–8 distinct absolute column letters inside it.
It scans every row of those columns locally, including empty cells, in single-column
chunks of at most 1024 cells. At most 2000000 selected cells are admitted per call;
the bound is checked before Office access. It returns a compact typed result with
`rowsScanned`, `cellsScanned`, `complete=true`, and per-column numeric, empty and
other counts plus numeric sum, minimum, maximum and average. Text such as `"12"`,
booleans and errors are not coerced to numbers. A column with no numbers has null
numeric statistics. The source is raw Excel `Value2`: dates are numeric serials,
and formulas contribute their calculated values. Neither cell values nor the
scanned matrix reach the model.
The scan uses the existing document-bound read gate and checks cancellation between
chunks and rows. A failed chunk or cancellation cannot publish a partial result.

`complete=true` means that all selected cells were visited successfully; it does
not mean that the entire wide rectangle was read or that the workbook was frozen.
Coordinates and dimensions are checked before, during and after the scan. Values
can still change concurrently without changing those coordinates. The result names
this limit `consistency=sequential-observation`; it is not a transactional snapshot
or a claim about unselected columns. For large ranges, use the `structure` overview
to choose an exact rectangle, then request the needed columns explicitly. Exact
row/column pushdown for `table`/`records`, filtering and grouping remain separate
work. Windows/Excel latency, COM behavior and live model tool choice are unqualified.

### Named Excel tables

`Excel table: Sales` is discovered and read through the same generic tools and
`ExcelResourceProvider`, not a separate table reader. The `excel-table` kind uses
the bound document token plus case-normalized table name as its logical identity;
moving or resizing the table does not turn a binding into a fixed A1 range.
The existing typed `CaptureStructure("tables")` supplies metadata only. Named
resolution requires a complete bounded catalog (up to 200 entries), one matching
name and a local A1 rectangle. Ambiguous/missing names, incomplete catalogs and
invalid extents fail explicitly; the 100000-cell bound precedes range capture.

`text` and `formulas` retain the existing typed range snapshot, including sheet,
address and dimensions. Those coordinates are part of the captured bytes, so a
move with unchanged cell values still supersedes old evidence. `structure` keeps
the existing profile. `table`/`records` use explicit path `$.values`, including the
full table range (headers/totals when present), with shared row coverage and CAS
parts. No second JSON store or fallback to the former address is introduced.
Historical exact reads/projections need no table lookup or Office I/O; missing CAS
does not read the current table. Native bound-STA/closed-workbook behavior is checked
with fakes; real Windows ListObject/model/WebView2 qualification remains open.

### Excel catalog discovery and document reads

Name/table discovery returns the bounded captured metadata (up to 200 entries)
with terminal `Truncated=true` when the backend capture is incomplete. Visible
pages do not close that source gap. Collection fingerprints include completeness,
so a coverage change invalidates continuation even if the captured descriptors
are identical. Duplicate catalog identities still fail explicitly.

Excel metadata search scans one list page (up to 50 items) and returns at most 20
matches. It propagates both source/page truncation and result clipping. Filtering
to zero matches cannot claim complete absence. This uses existing completeness
fields and bounded capture, without raising limits or adding a catalog store.
Point reads of a named table/name still require complete unambiguous identity
evidence; explicit A1 range and `document:` reads do not depend on that catalog.

The user's 2026-09-08 failure (`RESOURCE_SNAPSHOT_TOO_LARGE`, incomplete defined-name
catalog during `document:` text read) is covered by a host-neutral 201-name fixture:
model-facing document/range reads succeed, strict name reads still refuse incomplete
identity, and terminal/search coverage remains honest. The workbook itself has not
been retested on Windows/Office. The separately reported upstream HTTP 502 is not
resolved by this resource-routing correction.

### Excel Defined Names

`Excel name: Sales` and `Excel name: Data!Sales` use the existing Excel provider
and generic discovery/read tools. The `excel-defined-name` identity is the bound
document token plus the case-normalized exact name reported by Excel; sheet
qualification is preserved, never guessed or aliased to a workbook-scoped name.
Resolution requires one match in a complete catalog of at most 200 names.

The typed backend classifies `RefersToRange`: only `BoundRange` (one area whose
worksheet belongs to the exact bound workbook runtime) permits cell capture.
`Unresolved`, `ForeignRange` and `MultipleAreas` expose metadata only, even if a
snapshot contains sheet/address fields. Constants, unresolved formulas and external
references are not evaluated by the provider or redirected to a local sheet.
Dynamic definitions use their observed single bound extent when available. Local
A1 validation and the 100000-cell bound precede capture; unsupported/full-column
extents remain discoverable as metadata but fail an unsupported/oversized body read.

`metadata` retains the full definition; discovery carries only a bounded preview.
`text`/`formulas` capture the definition and typed range snapshot together;
`structure` wraps the existing domain profile with the same definition.
`table`/`records` use `$.range.values`. Definition changes or extent changes advance
the revision even when cell values remain equal. Exact retained reads/projections
use shared CAS without resolving the live name, including after its removal;
missing CAS never falls forward. Fresh observations are not continuous monitoring.
Host-neutral tests cover this routing and bound-STA/closed-session refusal. Real
Windows name resolution, external/dynamic/multi-area COM behavior and WebView2
qualification remain open, as does finer Excel impact/coverage qualification.

### Excel search

`excel.find_cells` routes ordinary `mode=literal` queries of at most 255 characters
to bound Excel `Range.Find`/`FindNext`. The backend visits candidate cells only;
`ExcelFindReplaceService` applies the requested case, whole-word and
values/formulas/both filters and counts all matches. The Excel provider publishes
the completed, query-bound result through Gateway/CAS as exact evidence, including
zero matches. Its model result contains coordinates and bounded previews, not a
whole-sheet cell snapshot; returned match JSON is capped at 32,000 characters.
The result resource is runtime evidence, not a model-facing read target.

`Excel search scope: workbook`, `selection`, `sheet 'Name'` and
`range 'Name'!A1:B10` expose exact text JSON with scope and captured cell fields.
Workbook, selection and named-sheet scopes are body-free discoverable; explicit
range scopes can also be discovered/resolved. Apostrophes in sheet names are doubled.
Omitting a sheet retains the existing bound active-sheet behavior; omitted tool
scope still infers range from address, sheet from sheet name, otherwise workbook.
An explicit address with any scope other than `range`, or `scope=range` without an
address, is rejected before Excel access; the address is never silently ignored.
Named/multi-area range interpretation remains with the bound Excel backend.

Regex and longer literal queries still capture through `ExcelSearchResourceService`
→ the existing Excel provider/Gateway/CAS and run pure matching over the exact cell
snapshot. The operation cancellation token passes through Gateway/provider reads
and is checked before capture, between cells and after capture; an in-flight
synchronous COM call is not forcibly interrupted. Capture admits at most
100,000 cells and one million aggregate field characters; non-formula cells retain
only their value, avoiding a duplicate `Formula` body. Serialized JSON has its
own one-million-character cap. Bound range cell counts are checked before cell
materialization. Invalid/duplicate cells and oversized sources fail explicitly.
For these bounded scans, use `excel.inspect` with `kind=workbook` for UsedRange
addresses and search smaller explicit `scope=range` slices if necessary.
Literal native search bounds candidate cells at 100,000 and reports an error if the
scan cannot complete; a limited match list sets `truncated=true` while preserving
the complete match count. No prefix is reported as a complete search.
Invalid regex is refused before capture. Positive, zero-match and blank-cell results
retain complete exact evidence; drift and replacement invalidate previous evidence.
Historical pages do no Excel I/O, and missing CAS never falls forward. Search scopes
share the existing provider's exact paging owner, not a second store or transport.
Replacement preparation/read-back stays with its existing domain owner. Windows COM
selection/named-range/count limits, real model and WebView qualification remain open.

## Word text reads

`word.read_text` is removed. Document and selection targets discovered through
`common.resources_find`, and explicit `Word range: start:end` targets, use
`common.resources_read` (`text`). Range positions are main-story, zero-based,
start-inclusive/end-exclusive; reversed, out-of-document and noncanonical targets
are rejected, never clamped or given an implicit end.

The existing `LiveDocumentResourceProvider` routes all Word source reads through
`WordService.CaptureText` and the bound `IWordBackend`; it does not call the generic
adapter's document/selection text fallback. Complete captures use existing CAS and
authority, so internal pages and historical exact reads do not recapture live Word.
The one-million-character capture ceiling is checked before COM `Range.Text`;
oversized document/selection reads fail explicitly and require a narrower target,
not clipped text presented as complete. Empty exact ranges remain readable.
HTML uses this same provider. Word inspection and mutations keep their specialized
owners. Real Word range/selection/STA and final catalog qualification remain open
on Windows.

`word.find_text` keeps its semantic literal/regex/main/selection/all contract but
executes through `WordSearchResourceService` → Gateway → the existing document
provider → `WordService.CaptureSearch`. Matching is a pure Word domain operation
over the retained snapshot; the direct tool-adapter/backend search path is removed.
`Word search scope: main|selection|all` are discoverable scope-collection resources:
their exact JSON contains scope plus story kind/start/end/text, not runtime story
IDs. They share the existing document authority and CAS; no search store or separate
freshness state is introduced. Historical source pages can be read through the
ordinary resource reader without new Word I/O.

Capture bounds are 256 stories and one million aggregate text/range characters;
the provider also caps serialized JSON at one million characters. The bound backend
checks story count and aggregate ranges before `Range.Text`, then verifies the actual
text extent. Invalid regex is refused before capture; malformed/oversized snapshots
fail explicitly. Matching retains absolute coordinates within each named story,
case/whole-word behavior and output limits. Both positive and zero-match searches
carry complete exact CAS evidence; later observed scope drift or document mutation
invalidates previous evidence through the shared reducer. Search results no longer
expose scope/content hashes as public coordinates. Missing retained payloads never
fall forward. Actual Word story enumeration, COM and WebView qualification remain
open; other host-specific searches are covered by their own sections. Generic
`resources_find` scan publication uses the shared provider/Gateway path above.

## PowerPoint slide reads

`powerpoint.read_slides` is removed. Use `common.resources_read` with a discovered
document target or `PowerPoint slide: N` (positive one-based index). The `text`
view contains slide text; `source` is an ordered JSON array of slide id/index,
text and speaker notes, including for a single slide. `structure` remains metadata,
not a slide-content alias. Noncanonical and nonexistent slide targets fail explicitly.

The existing document provider calls `PowerPointService.CaptureSlides` and the bound
`IPowerPointBackend`; document/slide reads no longer call the adapter's clipped
document snapshot. Complete captures use the shared authority, CAS and evidence;
internal continuations and retained historical reads never recapture Office.
The backend enforces 500 slides, 1000 shapes per slide/notes page and a shared
one-million-character capture budget. Shape text length is checked before COM
`TextRange.Text`; over-limit decks/slides fail without a prefix-as-complete result.
Serialized view size is bounded too. HTML binds through the same Gateway.
Search, object inspection and mutations remain specialized. The existing selection
observation is separate and unchanged; it does not expose the slide `source` view.
Real COM shape/notes capture, selection and final catalog/model qualification remain
open on Windows.

### PowerPoint search

`powerpoint.search_text` keeps its literal/regex, case/whole-word, notes and
shape-local coordinate semantics. `PowerPointSearchResourceService` captures through
Gateway/CAS and invokes pure `PowerPointService.Search` over that exact snapshot;
the direct adapter search path and public scope/content hashes are removed.
`PowerPoint search scope: deck` and `deck+notes` are body-free discoverable resources;
`slide:N` and `slide:N+notes` resolve a positive one-based slide explicitly. They
expose `metadata` and exact `text` JSON containing slide index, shape name, kind and
text, not private target IDs. As before, tool `slideIndex=0` searches the deck.

Bound capture enforces 500 slides, 1000 shapes per slide/notes page, 5000 text
targets and one million aggregate text characters; serialized JSON is also limited
to one million characters. Search checks native text length before materialization
and does not swallow COM read errors as empty text. Invalid regex is refused before
capture. Complete evidence accompanies positive, zero-match and empty-scope results;
observed drift or document mutation invalidates prior evidence. Historical pages
use retained CAS without Office reads; missing payloads fail without live fallback.
Mutation preparation/read-back retains its existing domain owner and is not a
second search path. Real COM limits/enumeration and WebView qualification stay open.

## Outlook mail reads

`OutlookService.CaptureMail` is the typed owner of complete mail-source capture
and reply/update preparation. `outlook.read_mail` and its output wrapper are removed.
Model/manual reads and HTML bindings use the existing Gateway/document provider,
shared authority, CAS and evidence. Source continuations retain the first capture;
historical pages do not read Outlook or fall forward to another mail.

`common.resources_find` discovers `Outlook mail` targets from header-only bounded
discovery (up to 500 folder items; truncation remains explicit). Targets use subject,
sender and received time; identical targets fail as ambiguous. Runtime owns EntryID
and resource keys. An Inspector admits only its retained mail, including an unsaved
mail; a folder lookup supplies its StoreID and verifies parent StoreID/EntryID.
Moved/out-of-scope mail cannot be freshly read through a previously discovered URI.
The document/selection aliases mean current selected/open mail, not the whole folder.
Use a discovered mail child for stable mail identity within this bound scope.

The `text` view contains the full body; `source` includes headers, body and attachment
metadata (not attachment bytes); `structure` includes headers/attachment metadata
without reading body. Message reads return the entire body within the ceiling (at most
one million characters) or fail explicitly. They never return a clipped body.
The backend reads body once and derives the preparation token from those same
captured fields; source/attachment getter failures are not replaced by empty values.
Attachment-only capture does not access body or create a mutation token.
`BodyCaptured` distinguishes no body capture from a genuinely empty body.
Missing body/token/attachment metadata, mismatched explicit targets and cancellation
fail before the snapshot reaches a caller or a mutation dispatch.

Outlook OOM exposes `MailItem.Body` as one string: the character ceiling is enforced
after that property read, **not** before COM materialization. Bounded body acquisition
and real large-mail execution remain open. Mutation read-back remains a specialized
existing contour; folder search uses the exact projection described below. Real Inspector/folder/store
membership, unsaved-mail identity, WebView2 and final catalog/model qualification
remain open; this reader switch does not close those gates.

### Outlook attachment content (user-authorized, 2026-09-08)

`Outlook attachment: <mail title> / <index>: <file name>` is a semantic target.
Filename discovery covers the selected/open mail only; Explorer coverage is explicitly
partial. Other mail `structure`/`source` views advertise attachment targets; pass a
copied target to `common.resources_find`, then `common.resources_read`. Target resolution
requires unique complete mail discovery (at most 500 folder items); open an Inspector
when folder discovery is incomplete. Attachment coverage does not gate other resource
families. No mailbox-wide attachment-content search is introduced.

`metadata` reads no file bytes; `text` reads text/CSV or extracted PDF/OOXML text; `media`
uses the existing model attachment/vision routing for images and PDFs. Auto selects
media for images and PDFs with little/no page text. Explicit PDF text includes a warning
when visual content is absent from extraction. No separate OCR, model protocol or UI is
introduced. DOCX/XLSX/PPTX now extract bounded OOXML text (Word paragraphs/tables,
spreadsheet cells with sheet/address labels, slides and notes) without Office
automation. Legacy DOC/XLS/PPT, archives, embedded messages and OLE attachments
remain unsupported; malformed or protected content fails explicitly.

`OutlookService.CaptureAttachment` validates the typed request/result. The bound
`OutlookInteropBackend` saves only `olByValue` into a uniquely named temporary directory,
checks reported size before saving and actual file length before allocating bytes, then
checks attachment metadata and mail modification time again. Limit: 20 MiB per file;
complete extracted text: 1,000,000 characters. Temporary files are removed in `finally`;
new attachment/collection and owned Explorer-mail COM references are released. No mail mutation/open/execute
occurs. Attachment capture itself never obtains `MailItem.Body`.

Runtime keys pin the parent mail, attachment index and metadata fingerprint, preventing
an already-resolved slot from silently following a renamed/replaced attachment. Original
bytes and extracted text are parts of one private CAS source view registered before
shared authority publication. `CompleteViewParts` is runtime-only capture metadata;
there is no new store or legacy reader. Text continuations and historical media use that
exact source, never another live capture; later observed byte changes publish drift.
The private manifest and its payload references never enter model-facing text.

Host-neutral Outlook, CAS retention/drift, model media and bound-STA/closed-window checks
pass. Actual Windows x64 + Office x64 + VS 2022 build and Outlook/COM checks remain open:
Inspector/Explorer and store identity, SaveAsFile/read-back/cleanup, large files,
protected/corrupt PDFs, unsaved/inline attachments, and real vision-model delivery.
Existing Windows/x86 PDF and source-allocation gates are unchanged.

Folder collection uses the same document provider, shared authority and CAS;
`outlook.collect_mail`, its handler/request/output and monthly JSON wrapper are
removed, including accepted-call replay. `common.resources_find` exposes an
`Outlook collection` target. `text` is the complete JSON projection; `records/table`
at `$.messages` and HTML bindings derive from that exact retained text snapshot.
No separate collection store, bulk result tool or compatibility alias remains.
The singleton collection resolves directly, so a truncated list of individual mail
targets cannot block it with `resource_scope_incomplete`. The semantic target
`Outlook collection: latest:N` (N = 1–500) limits the Outlook capture itself;
`maxRows` limits only a derived records page after capture. A failed oversized
capture should be retried with a smaller N, not the same collection target.

This is a bounded projection of at most 500 newest **folder items** (mail rows only),
not a complete mailbox or full-body capture. The JSON envelope reports
`totalFolderItems` and `collectionTruncated`. Rows expose subject, sender, received,
`month` (`yyyy-MM`), `bodyPreview` (at most 1000 characters) and `bodyTruncated`.
Read the envelope before interpreting record totals; `complete` on a records page
means completion of this captured projection, not the whole folder. Monthly grouping
belongs to the consumer; full bodies use individual mail resources. Folder paths and
EntryIDs are not projected into the body. Empty folders/previews remain valid.
An individual mail target from an incomplete folder listing still cannot be
assumed unique; that read returns `resource_scope_incomplete` with guidance rather
than silently selecting one matching message.

The typed `OutlookService.CaptureCollection` validates row identity/extent and a
750,000-character aggregate budget; serialized JSON has the provider's one-million
character ceiling. The collection backend captures each preview once, with explicit
truncation and no mutation-token/second body read. Header/getter failures cannot
become empty successful captures. OOM still materializes the full body before
trimming, so this does not close the pre-COM allocation gate. Inspector runtimes
cannot read their parent folder. Fresh reads publish observed collection drift;
exact historical text/records and opened continuations never fall forward.

### Outlook search

`outlook.search_mail` retains literal/regex, case/whole-word, field-local coordinates
and subject/sender/recipients/body matching. `OutlookSearchResourceService` obtains
one Gateway/CAS snapshot and invokes pure `OutlookService.SearchMail`. Search output
contains semantic mail targets and bounded matching previews, not EntryIDs, folder
paths or repeated body copies. The obsolete `maxBodyChars` argument and direct tool
adapter search branch are removed without fallback; complete mail bodies use
`common.resources_read`. Duplicate semantic mail targets remain explicitly ambiguous.

The document provider exposes `Outlook search scope: latest:N` (headers only) and
`latest:N+body`, where N is 1–500 newest folder items; the default 100-item scopes are
discoverable without body reads. The exact text JSON includes the capture limit,
body-capture flag, folder extent/truncation and mail header/body-prefix rows. Bodies
are capped at 100,000 characters per mail with explicit `bodyTruncated`; the
aggregate retained header/body bound is 750,000 characters and serialized JSON is
limited to one million. Oversize aggregate captures fail and request a lower N.
Changing the search query alone does not reduce this capture; `maxItems` or the
selected fields must change.
Subject and sender names stay exact for semantic mail targets and reject values above
4,096 characters. Sender email and `To`/`CC`/`BCC` capture at most 4,096 characters
each, preserving surrogate boundaries; `senderEmailTruncated` and
`recipientsTruncated` mark incomplete prefixes. One long recipient list no longer
rejects a subject, sender or body search by itself. `sourceTruncated` includes these markers
only when the corresponding sender or recipient field was searched, so zero-match
results never claim complete coverage of an incomplete searched field.
Header-only search never reads Body; body search reads it once without a mutation
token, preserves surrogate boundaries and never substitutes empty text after errors.

`sourceTruncated` and the returned overall `truncated` flag include incomplete
searched-field prefixes as well as folder truncation; `matchCount` counts only
captured fields.
Complete evidence means the complete bounded projection, not the entire mailbox or
full mail bodies. Positive, zero-match and empty-folder searches retain exact CAS
evidence and publish observed drift; historical reads do no Office I/O and missing
payloads never fall forward. Inspector runtimes cannot search their parent folder.
The old search-body duplicate field and unused folder-path snapshot field are removed.
Real Windows/COM, OOM pre-materialization and WebView/model qualification remain open.

### Outlook mailbox archive (host-neutral implementation; Windows COM open)

Desktop offers stable mailbox chats keyed from StoreID. `outlook.index_archive`
scans the selected mailbox and optionally attached PST stores for an inclusive UTC
date range; each call persists up to 20 batches of 25 matching mails and may be
repeated to resume. Folder or mail chats receive `outlook_mailbox_required`; the
user must select the Outlook account chat for a mailbox scan. The backend walks
folders, excludes recognised search folders, reads each matching body once, and
records folder/read errors. A source signature
checks folder identities and item counts between batches; unchanged counts do not
prove that messages stayed unchanged during the scan. `refresh=true` explicitly
starts a new scan. Large bodies have separate exact mail targets and are read through
the normal resource cursor.

CAS holds indexed bodies and pages; a pointer is a rebuildable cache checkpoint.
CAS GC traverses its manifest/page/body references. `common.resources_find/read`
exposes `Outlook archive page` text JSON with period, coverage, page/row source
context, duplicate flags and attachment counts. Search over indexed page text is
literal and reports unavailable pages. Each row exposes up to 20 exact attachment
targets. Reading one captures its live bytes from the indexed store (including an
attached PST), verifies the stored modification timestamp and attachment count,
then retains original and extracted content through the existing attachment
resource path. Unread attachment contents remain unexamined; old checkpoints
without timestamps require an explicit refresh before attachment reads.
`Outlook archive digest` groups distinct indexed messages by ConversationID, or by
subject and sender when that ID is absent. Pages rank groups by message count and
show bounded first/last samples with source page and row; the rank measures activity,
not severity or importance. The model must inspect cited full rows before claiming
decisions or complete issue coverage.
Automatic multi-run classification/report coordination remains open. Real Outlook 2013/2016 COM
enumeration, binding, cancellation and WebView navigation need Windows qualification.

## Conversation loop

Installed skill publication is distinct from task selection. `SkillTaskService`
resolves the frozen published skill/tool catalogs; it never reads live package
files during compilation. Complete selected instructions are compiler invariants,
independent of retained historical tool results. Supporting files keep ordinary
resource evidence semantics. Per-request Arsenal metadata records exact bindings;
the dispatcher checks the originating receipt and rejects subsequent catalog drift.
This uses the existing catalog authority and `run.updated`, with no new runtime,
store or execution path. See [skills](skills.md#manifest-and-task-selection).

Controller clear/edit/message-delete/fork use typed `ChatResourceMutationIntent`
through the existing mutation observer/journal. `ChatHistoryEditService` and
`ChatCloneService` own history/resource preparation; conversation state is durable
before workspace/plan/task/membership heads become visible in one commit. Clear
removes active conversation definitions too, retaining exact revisions/CAS. Edit
publishes a new restore revision only for changed logical state. Failed publication
blocks fresh captures; recovery records Unknown and never replays the command.
Fork keeps the live document authority and creates child conversation resources.
Copied artifact bindings are rebased into a new workspace snapshot, not rewritten
inside old immutable bodies. Missing exact checkpoints fail closed.
`ResourceForkService` prepares the bounded dependency graph for schema/mapping/
virtual and materialized derived resources plus supplied context. Required copy
preparation retains exact CAS/revision metadata without publishing heads. One fork
commit publishes all selected definitions and workspace state after persistence.
`ResourceCopyLink` facts in conversation events preserve exact copy provenance and
source publication order, including nested forks and multiple retained revisions;
they never become a current-head store or an implicit cross-chat read alias.
Fork preparation and retained reads use the same authority-owned publication proof:
copy provenance is eligible only after the verified fork commit, including historical
copies that were not selected as heads. Preparation caches only its bounded graph's
publication order, not an independently rebuilt publication history.
Only deliberately copied artifacts and references are rebound. Materialized data
reuses its CAS body; typed definitions get new bodies when their internal refs change.
Cycles, missing/unpublished dependencies and size/depth bounds fail before publication.

`ConversationRunService` → `AgentKernel` remains the only lifecycle/outcome loop.
`ConversationKernelAdapter` captures published catalogs at request boundaries.
`ConversationModelSession` freezes history/high-water, resource authority, tool pack,
skills and schemas before `ModelContextCompiler`. Normal requests, protocol repair,
compaction and Inspector use that compiler; compilation does not consult COM,
mutable files/catalogs or another chat's live transcript. Explicit shared-context
reads use immutable CAS archives published by the existing DocumentArtifactStore
facet, with source revisions/payloads retained by the same authority graph.
The compiler derives per-claim currentness; see
[shared context](artifact-library.md#discovery-descriptions-and-model-context).

`EvidenceStateReducer` classifies Current/Superseded/Unknown/Unavailable against
frozen authority. Only a `Known` head supplies a current revision. `Removed` maps
to `Unavailable` for both the observation and its current-state dependencies;
immutable-snapshot dependencies keep their exact historical meaning. Removal
does not rewrite observations or delete retained bytes. Correctness filtering,
terminal-write collapse and deduplication
precede budget selection and CAS hydration. `ContextReceipt` records generations,
exclusions and hydration. Compaction retains structured source-grounded claims;
free summary prose cannot resurrect superseded resource bodies.

`ContextNote.Role` distinguishes explicit user instructions, supplied data and
bound Office observations. Instructions use an exact `InstructionPayload`; data
and observations use `ResourceEvidence.Payload`. The compiler hydrates these only
after correctness filtering, never from mutable `Text`/`Preview`. Those fields are
bounded UI previews. Draft skill/tool/prompt attachments remain data, not activation
authority. Untyped old notes require explicit reattachment; normalization cannot
infer their role. Upsert and fork preserve the typed role and exact payload/evidence.

## Ingestion and derived data

Existing ingestion promotes chat drafts before model dispatch. Immutable bodies
reuse `ChatBlobStore` CAS, without a second resource payload store.
`ResourceStructuredViewService` stores bounded indexes/parts for record/table
views; ambiguous JSON paths/properties and unsafe integer precision fail explicitly.
Structured/virtual continuations use shared exact-reference guards before source
capture, index/definition CAS hydration or implicit artifact identity resolution.
Their binding pins URI, view and the requested projection: JSON path and ordered
field names remain case-sensitive; omitted/empty fields both mean all fields.
The first head read rebinds outgoing cursors to its resolved exact address.
Collection-style guards and lowercased projection bindings are removed without
compatibility tokens. Excel snapshot bounds remain explicit.

`ResourceDefinitionToolHandler` owns draft/validated publication. Only exact
published schema heads enter `SchemaRegistrySnapshot`. Mappings pin source/schema
revisions. `ResourceDerivedViewService` preserves transitive dependencies for
virtual/materialized views; drafts have no authority before publication. Virtual
tables expose only the root record projection and reject unsupported paths.
`ResourceAuthorityService.RequirePublished` guards retained text, structural indexes,
virtual definitions and catalog roots against prepared-revision exposure. Committed
history remains readable after head changes without publishing or healing authority.
The shared retained reader also owns typed CAS read failures for those definitions,
indexes, parts and catalog references. Domain owners keep their size/shape bounds;
missing/corrupt payloads return `RESOURCE_SNAPSHOT_UNAVAILABLE`, never current-source
replacement or an invented empty body.

## Context and storage

`ResourceAuthorityStore` is the shared append-only head/effect/revision/view
authority, scoped to document, conversation or catalog. One scoped commit atomically
publishes all affected heads, one effect and generation. Cross-window catch-up reads
journal tails; the rebuildable in-memory scope cache is bounded. Known heads require
durable exact revision metadata before publication.
An explicit local-data reset invalidates that cache in all controllers of the same
process and clears transient document authority bindings; an unexpected journal
truncation still fails closed.

`ResourceMutationJournal` records Prepared and possible dispatch before invocation.
`ResourceMutationAuthorityObserver` durably captures verified read-back before
terminal publication and the tool-result event. Uncaptured affected views become
Unknown. Interrupted attempts are never automatically replayed/restored.
HostRuntime/document serialization covers guard through read-back, not model/user waits.

When the context compiler folds identical current reads, it checks the final
budgeted request before describing the surviving body as delivered. If budgeting
omits that body, earlier completed-read receipts explicitly say `bodyIncluded=false`
and carry no pointer to a delivered result. The durable reads and exact evidence
remain unchanged; omitted bodies confer no write authority.

Session events use schema 4; incompatible streams require explicit new/reset without
deleting user history or fallback. Large accepted arguments, activity/results and
pending execution payloads use CAS refs. Registered revisions, views and their parts
remain retention roots even before the first head commit. Current retention is
conservative. New checkpoints/retention optimization are not prerequisites for
cutover unless a concrete correctness or boundedness failure requires them.

## Domain projections and UI

HTML bindings hold ResourceRef, head/exact policy, view/path and optional schema/
mapping refs, never current dataset JSON or executable tool calls. Head policy is
resolved on open; existing handles remain exact. Static JSON is a chat resource.
New built-in model reads, discovery and HTML/JS inputs select `table` for bounded
rows. Historical saved bindings, v1 transfer packages and custom JS inputs may
still carry `records`; the internal data plane reads their original view/path
without silently rewriting them. Its removal gate is recorded in the
[migration map](stabilization/MIGRATION_MAP.md#historical-structural-records-view--2026-10-21).

`common.html_data_write` stores JSON and immediately creates a named `text`
binding. Its result exposes the readable JSON `sourceTarget`, view and policy;
no second bind is needed. Mutation member entries and model workspace-structure
reads expose directly readable semantic targets. `scope=html` includes the
document-owned workspace root as well as its members. The prompt working set
omits superseded HTML snapshots already known in the chat; their history remains
durable, but current discovery cannot resolve those old root targets.

HTML member listing validates continuation against metadata for the complete
ordered collection, including UTF-8 byte lengths and binding metadata. Content
hashes are not part of that collection fingerprint and are computed only for the
returned page. Returned descriptors remain complete; structure/member resolution
still computes every requested hash. Snapshot parsing remains whole-snapshot work
under the existing owner, without a separate metadata index.

An `HTML data` member is binding metadata, not the dataset. Model text reads expose
name/view/path/policy/sourceTarget instead of internal references. Bind refuses a
workspace or binding member as a data source before mutation; structured model and
JS reads of those targets direct the caller to the source. An identical binding is
a verified no-op. Binding metadata is not restored as raw source after compaction.
Page stream views are fixed at binding/open: `text/source` yields `batch.text`,
while `table` and historical `records` yield `batch.rows` and `batch.columns`.
The HTML skill does not override a saved binding's view with a hard-coded table
stream view.

`RN.resources` opens named capabilities, reads bounded batches/streams and closes
them. `ResourceDataPlaneService`/`ResourceDataRouter` serve the internal
`https://rnassistant.local-resource/v1/<opaque-lease>` WebView route, not a server.
The agent JS worker uses the same data-plane service through a local process pipe:
the parent resolves semantic bindings and retains exact revisions, then serves
bounded `open/read/close` requests. The pipe is an execution transport, not a
second resource authority or a model-visible `rna://` route.
Access is owner-scoped, exact, sequential and cancellable: one read per handle,
four opens, 64 leases and ten-minute expiry bound work.

Attachment ingestion uses the same router at `/v1/upload/<opaque-lease>`.
`beginChatResourceUpload`, `completeChatResourceUpload` and cancellation carry only
typed metadata/capabilities. Binary POST chunks have an exact acknowledged byte
offset and count, at most 256 KiB each; CORS preflight admits only POST/Content-Type
from the opaque local origin. There is one in-flight operation per upload, at most
four uploads within the shared 50 MiB transfer-buffer and 64-lease budgets.
The browser materializes one chunk as an `ArrayBuffer` before POST, with explicit
`application/octet-stream`; it never posts a Blob/File-backed body or reads the
whole file up front. This avoids WebView2's reported missing binary request-content
path ([upstream issue](https://github.com/MicrosoftEdge/WebView2Feedback/issues/2162)).
Cancellation/currentness is rechecked after the file read and before dispatch.
The existing 20 MiB/file and 50 MiB/message limits remain. Ten-minute expiry is
checked on access and swept periodically; close/dispose releases idle buffers.
A cancelled busy lease retains its reservation until the operation actually exits.
Chat ingestion accepts XLSM/XLSB/XLS/ODS and common template/add-in variants as
signature-checked, immutable `file` originals with spreadsheet MIME types. They
have no host-side extracted text; the HTML workspace can bind their exact `raw`
bytes for local SheetJS parsing. XLSX keeps its existing bounded text extraction;
CSV/TSV/FODS remain text originals. A parsed file is not live Excel document authority.

Incomplete, malformed or cancelled uploads cannot create a resource publication.
Completion consumes the capability and stages managed bytes through the existing
`ChatResourceIngestionService`/`AttachmentStore`; CAS promotion and message resource
linking still happen only at send. Known late drafts are discarded, uncertain
chunks/completion are not replayed. The UI awaits staging in the addressed chat,
even if another chat becomes active.
File picker, clipboard and file drops over the conversation/composer share this
ingestion path. Progress and failures are visible beside the composer and scoped
to the addressed chat. Text drags retain normal editing behavior; file drops
outside the conversation cannot navigate the app away. Navigation, message editing
and pending submit refuse new attachments explicitly.
Extraction runs off the UI thread; the bounded WebView request stream is consumed
on its STA. Each active upload read uses at most 64 KiB of scratch space in addition
to the reserved body: stream reads always use buffer offset zero, then copy into
the exact upload offset. This handles partial reads and the WebView2 SDK's
`COMStreamWrapper`, which rejects nonzero destination offsets. Unexpected WebView
response failures are logged with their exception and return typed
`RESOURCE_TRANSPORT_UNAVAILABLE` JSON instead of an empty 503 response.
No base64 staging route/adapter or
second durable upload store remains. Real Windows POST/preflight/close-during-PDF
qualification is still open.

VBA editor save/create now uses the same upload route, byte reservations and shared
sequential browser uploader. Its capability is scoped to the addressed chat and
`vba-editor` consumer, distinct from attachment staging. `VbaEditorResourceService`
consumes verified complete UTF-8 source once; only the existing guarded mutation
owner can then write/publish live VBA. Control DTOs contain upload identity and raw
source hash, never source code; save also requires its normalized editor read guard.
There is no attachment/publication side effect from uploading alone. Bounds and
cancel/late-response semantics are in [VBA journal](vba-mutation-journal.md#editor-source-uploads).

HTML workspace file and JSON Save/create now consume the same upload route under
the distinct `html-editor` owner. Only typed target/chat/expected-workspace/lease/hash
controls cross the bridge; complete source enters the existing CAS-backed mutation
intent and existing HTML writer/commit barrier. Before dispatch, the editor guard,
Known logical publication and exact immutable source must agree under the mutation
lease. Draft/lease cancellation and bounds are in
[Artifact Library](artifact-library.md#html-editor-uploads). Outgoing workspace
files are now metadata plus exact existing member refs in every init/chat/mutation/
export projection. Selected source, preview and export use the same HTML member
provider's complete CAS views and shared download owner, with one bounded browser
producer and no inline body fallback. Unloaded source cannot become an empty draft;
dirty drafts keep their original workspace guard across newer metadata pushes.
Read/cancel/reload bounds are in [source downloads](artifact-library.md#html-editor-source-downloads).

Trajectory ZIP export uses the same owner-scoped data plane at
`/v1/download/<opaque-lease>`, with metadata-only bridge setup, sequential 256 KiB
GET chunks and full-payload SHA-256 verification before download. At most two
downloads share capture/lease limits and the 50 MiB transfer budget with uploads;
reservation precedes source validation and production. The ZIP remains a transient
projection of one validated event snapshot, not a synthetic published resource or
second store. Redaction, bounds and lifetime are owned by
[Trajectory export](trajectory-export.md). Binary/raw resource views use the shared
route described below; a separate cold-replay optimization is not a blanket gate.

Diagnostic event payload previews also use these download slots and the same
sequential reader. `TrajectoryPayloadService` resolves the exact event in a complete
validated journal, and the existing CAS codec verifies the whole bounded source
while retaining only a prefix. Source and preview hashes remain separate; bridge
setup contains no text. Both raw diagnostics and Run Journal use one cancellable
UI consumer, with no new publication/store. Bounds, UTF-8 handling and remaining
qualification gates are owned by [Trajectory query](trajectory-query.md#payload-preview-delivery).

Context Inspector's opt-in request JSON uses `PromptContextInspectorDownloadService`
and the same download slots/reader. The bridge carries snapshot metadata and a
`rawData` lease, never `rawRequestJson`. Reservation precedes inspection; the existing
512,000-character preview is surrogate-safe and transported as strict UTF-8 inert
text within 2 MiB. The browser verifies the payload before rendering, retains one
bounded raw-text cache and closes the lease on success/failure, panel close or chat
change, including late setup responses. Closing cancels delivery, not an already
running synchronous compiler capture. This is a disposable diagnostic projection,
not a new resource publication/store; compiler and context-budget semantics are
unchanged. Raw JSON now serializes directly into a bounded preview writer and stops
enumerating the request at the first character beyond the limit. String/property
prefixes also bound Json.NET escape-buffer allocation before serialization. Small
requests retain their exact JSON; large ones retain the same surrogate-safe prefix
and explicit truncation marker. No full intermediate JSON string is built. This
closes the Inspector serialization-allocation defect host-neutral, not the memory
cost of the existing compiler/input snapshot or real WebView2 qualification.

The unused `getRuntimeLog`/`clearRuntimeLog` bridge commands, inline response DTO
and exclusive tail/clear helpers are removed, not replaced by a new download.
The existing UI log is WebView-session-local (`app-core.js`/`app-logs.js`), with no
runtime-file reader. Runtime file logging remains unchanged; existing log files
are neither cleared nor migrated by the cutover. Retired commands fail as unknown
bridge messages.

Text/Markdown/PDF text pages, images, thumbnails and PDF renders travel through
that data plane. Typed bridge DTOs carry metadata/leases, not page text or base64.
Text leases close after each bounded page; media leases close on replacement,
eviction and chat change, including late responses. Full text is an explicit
bounded user action, not an eager workspace cache.

Artifact list/resolve descriptors now advertise admitted binary views through the
existing `representations` and typed `viewCapabilities`: image (20 MiB), image
thumbnail (512 KiB), PDF page (10 MiB) and PDF thumbnail (1 MiB). The existing media
owner supplies these metadata-only capabilities only for matching exact attachment
evidence and admitted kind/MIME/extent; an unconfigured attachment reader advertises
no attachment binary views. These are `maxPayloadBytes` object bounds, not renderer availability.
Binary views advertise sequential byte offsets/streaming and separate 256 KiB
`maxBatchBytes`/`maxItemsPerBatch` limits. Gateway rejects unsupported views and row/field
selectors before source hydration. Captured and retained views obey the same
per-view byte limit and MIME contract; retained data cannot bypass negotiation.
The same owner also exposes `raw` for exact attachment-backed image/file/attachment
artifacts, including empty originals. It reads and verifies original byte length
and SHA-256, never extracted text or rendered output. The retained `binary:raw`
view uses the existing CAS and must still match the exact source evidence; missing
CAS never falls back to the original reader. Control setup contains metadata only.
Delivery uses the same binary lease with sequential byte offsets and chunks up to
256 KiB; raw has no row/field/page selectors. The source MIME stays
in the descriptor; the raw payload/HTTP MIME is inert `application/octet-stream`
under the shared no-sniff/CSP route. HTML bindings accept `view: raw`, and the same
`RN.resources` binary consumer and standalone export return original byte chunks.
The same raw owner also admits committed CAS originals for file, Markdown, plan,
task-list and chart artifacts, without requiring an attachment reader. Discovery
checks kind, exact hash/length, the same 20 MiB bound and absence of attachment
provenance, without loading bodies. Reads verify the original CAS bytes directly;
they never reserialize JSON, normalize text, hydrate `InlineText` or reconstruct a
missing original. Broken/ambiguous attachment provenance and malformed metadata
cannot become a stored-body fallback, including on retained reads. Internal
checkpoints and HTML workspace aggregates are not raw files. Exact raw views and
their byte payloads use the same authority retention roots and historical access.
Workspace file members also offer `raw`: UTF-8 bytes of the exact committed file
source, not bytes of the parent workspace JSON. BOM, CRLF and Unicode are preserved;
the byte hash matches the same member's `source` CAS payload. Empty files are valid.
The existing member catalog checks the 20 MiB bound before encoding. Data-binding
members deliberately do not offer `raw`: a binding descriptor is not the bound data;
consumers read that resource's advertised view instead.
`ChatArtifactResourceProvider` owns all raw capture through `IResourceRawSource`;
the former viewer raw-reader is removed. Gateway resolves the exact descriptor,
preserving member identity, verifies length/hash before registering a view and uses
the existing binary retention/chunk route. Workspace edits do not replace historical
file reads, and missing retained CAS is not rebuilt from the parent snapshot.
No new reader tool or store is introduced. The implemented original-byte domains
(attachments including PDF/image/text, stored file/JSON artifacts and workspace
files) now negotiate raw through this path. `UpsertDataSource` already publishes a
JSON file artifact; its binding is not another raw source. This does not require
inventing `raw` for every live Office, catalog or state projection. Real
Windows/WebView2 qualification remains open.

Retained binary metadata is read through the shared snapshot reader, with a 4 KiB
JSON bound, strict UTF-8 and bounded typed parsing. Missing/null/corrupt records,
invalid payloads and extra JSON return `RESOURCE_SNAPSHOT_UNAVAILABLE`, never
recapture or renderer fallback. The embedded payload must match the view hash and
its exact retained part (hash, length, MIME and protection), so readable metadata
cannot bypass the authority's CAS retention roots. Failed reads publish no head
or generation and do not repair the retained record.

Binary opens reserve capture capacity before provider work in the existing 50 MiB
upload/download transfer budget. A lease verifies and retains its bounded CAS body
once on the first chunk, then serves sequential slices without per-chunk CAS
rehydration. Completion frees the buffer/reservation; close, expiry and failure
invalidate the lease, keeping busy-operation capacity until that operation exits.
Empty resources still verify their CAS on a zero-byte read. No second durable
store or producer queue is introduced; full bounded CAS verification remains, not
constant-memory decoding from disk.
Artifact image/PDF/thumbnail consumers use the shared verified chunk accumulator
before creating cache-owned blob URLs; cache eviction/chat close cancels pending
delivery and revokes URLs. No image element points directly at the capability URL.
`RN.resources.read/stream` yields byte chunks with `offset`, `nextOffset`, `done`;
fields and out-of-sequence offsets are rejected. Standalone export manifest v2
stores bounded binary parts with byte offsets and lazy verified offline slicing;
v1 manifests are explicitly refused by the current assembler, with no fallback or
rewriting of previously exported HTML files.

Authority notifications coalesce to bounded scope/generation metadata.
`RN.resources.subscribe` receives authorized binding names only; it does not create
a pushed dataset or another freshness cache. Notifications currently reach
in-process windows; other processes catch up on fresh shared authority capture.

`HtmlWorkspaceExportService` prepares export leases through the same data plane.
After capture, all head bindings must match one frozen authority tuple; explicitly
historical bindings stay exact. Head opens and the final export set use the common
`EvidenceStateReducer` against resource and dependency scopes. Matching the derived
head alone cannot pass when its schema/mapping/source has changed or is unknown.
Exact historical views remain readable. Export never changes binding policy or creates a
second head/body store. The UI pulls the complete export in bounded sequential
batches: at most 32 bindings, 1024 parts, 32 MiB of transport bytes and 8 MiB per
part. Failure, cancellation/chat change or mixed revisions prevent download and
release prepared leases. No bulk body crosses the ordinary control bridge.

The standalone HTML contains inert, integrity-checked snapshot parts plus exact
resource/coverage metadata. The same `RN.resources` handles/streams use a read-only
snapshot transport; parts hydrate only when read, with field/row/character bounds,
expiry and close/backpressure. Text/source, table/records and negotiated binary
views are supported. There is no live host/network fallback or head subscription.
Integrity checks require Web Crypto; missing/tampered parts fail explicitly.
ECharts remains the pinned local dependency. These host-neutral contracts do not
qualify actual Windows WebView2 capture or downloaded-file browser behavior.

## Audit decisions

The three normative contracts supersede conflicting pre-cutover area explanations.
[PROGRESS](stabilization/PROGRESS.md) records current work/Windows gates and
[MIGRATION_MAP](stabilization/MIGRATION_MAP.md) records completed consumer removal
and the remaining qualification, separately from optional improvements.
Existing Office adapters, mutation journal, event store and CAS remain canonical
owners, not parallel legacy/new paths.

The [2026-10-22 Resource Context audit](audits/resource-context-current-state.md)
adapts the new proposal to these existing owners. It distinguishes Office whole/
part reads from Workspace text limits and records request-provenance gaps. It
does not restart direct cutover or introduce another Gateway/compiler/store.

## Removed architecture

Removed in touched contours: per-chat VBA observation/refresh hash dictionaries and
callbacks; `VbaToolExecutor.Observations`; `HtmlAcceptedReadSourceResolver`; HTML
binding tool IDs/arguments/transforms/current JSON; independent model history/repair
assembly; free-summary resource authority; inline large pending payload duplication;
viewer text/base64 bridge transport; attachment base64 staging bridge and decoder;
trajectory ZIP base64 bridge body and decoder;
diagnostic event payload inline bridge text and whole-body-then-clip preview reads;
direct VBA editor module reads, controller JSON parsing and inline source bridge
body (now the same Gateway/CAS snapshot and bounded download owner);
inline VBA save/create code DTOs and the attachment-only browser chunk loop
(now shared bounded upload transport with separate consumer capabilities);
uploaded-HTML source bridge/DTO and independent preview cache, plus direct
attachment-text import reads (now exact Gateway pages through the same owners);
mutable-disk skill reference activation;
direct Skill Library reference reads and their inline body/fake mutation-result
bridge response (now published catalog → Gateway/CAS → shared bounded download);
inline SkillPackageDto core text in catalog/mutation projections and the separate
reference-only bridge action (now one pull-based skill source reader);
inline Skill Library core/reference mutation bodies and reference source echo
(now one bounded upload consumer, retaining the existing guarded mutation owner);
inline Tool Library mutation requests in Save and save-before-install
(now one bounded upload consumer through the same guarded authoring/commit owner);
inline Tool Library schema/code/README/component catalog and response bodies,
plus the UI whole-catalog body serializer/component reconstruction fallback
(now exact selected source through Gateway/CAS and shared download);
inline generated builtin documentation controls and state-wide documentation/body
request caches (now committed documentation CAS parts, exact catalog children and
the same bounded download owner, with one selected cache and no generator fallback);
`common.tools_definition_read` and its direct authoring-file reader/native binding
(now metadata discovery and exact tool-source reads with catalog-dependent evidence);
`common.prompts_read`, its current/defaults bundle and direct-settings native reader
(now exact published prompt/default field resources through the same generic reader);
separate state/Office retained text readers, externally hash-bound Office/state/catalog
continuations, and read-side exposure of prepared state/context/catalog identities.
HTML workspace file/data Save/create inline bridge bodies are also removed; the
four UI callers share one upload writer and the existing guarded domain owner.
Outgoing `HtmlWorkspaceDto.Files` domain-body cloning is removed from all four
projection sites; editor/preview/export now pull exact source through the same
member provider/CAS/download. Preview assembly refuses missing source bodies.
No compatibility alias, dual-write or feature flag restores these paths.

The acceptance reconciliation below replaces the former blanket TODOs for
"remaining consumers", named resources and cold-replay/retention optimization.
Do not treat those labels as authorization for further provider expansion.

## MASTER acceptance reconciliation — 2026-09-07

This reconciles MASTER §8 with the implementation and focused host-neutral checks.
It is not Windows qualification or an assertion that every repository path was
exhaustively proved. Final closure is host-neutral only (2026-09-07); the user
initially deferred two source-allocation limits; Inspector serialization is now
corrected host-neutral, while Outlook remains unresolved.
Test method names below are in `tests/RNAssistant.Harness`; implementation owners
are in `src/RNAssistant.Office/Services` unless another layer is stated.

| MASTER scenario | Current implementation / existing check | Remaining acceptance gap |
|---|---|---|
| Historical exact revision; continuation cannot cross revisions | Gateway/ResourceSnapshotReadService; `ResourceLiveContinuationsUseLogicalRevisions`, `ResourceRetainedPayloadsFailClosed` | No new architecture required by this review |
| Restore/rollback create new lineage even with equal bytes | ResourceMutationAuthorityObserver; `LocalResourceRestorePublishesNewLogicalRevision`, `VbaRestorePreservesExactSourceLineage` | Real Office restore remains Windows evidence |
| Save As preserves logical document identity | Document authority binding; `DocumentAuthoritySurvivesSaveAsAndSeparatesCopy` | Real COM/window lifetime remains Windows evidence |
| Guarded write cannot publish over unexpected head | HostRuntime/domain guard, mutation lease, authority compare-and-publish; extended `VbaConfirmedMutationRejectsStaleSnapshot` | Checked host-neutral: another chat observes a replacement revision while confirmation waits; stale confirmation performs no mutation/publication or backup. Guard mismatch may separately publish conservative ExternalDriftObserved/Unknown; the competing historical snapshot remains intact |
| Changed/no-op/unknown effects; cross-chat invalidation | ResourceMutationAuthorityObserver/OfficeResourceMutationDomain + Core EvidenceStateReducer; `ResourceTwoChatMutationsReachCompiler` | Checked through two executors sharing document authority, native Excel read/write, persisted paired read facts and actual ConversationModelSession.CreateRequest. No-op retains current evidence; changed-without-captured-after-state and lost-read-back remove uncertain content. Compiler/historical reads perform no Office I/O or replay |
| One coherent frozen authority tuple | UseInput carries the captured catalog generation through CreateAsync/RebindAuthority; CompileCurrent checks it against the same frozen CaptureMany tuple | Fixed host-neutral: intervening publication fails before compilation/request dispatch; the extended `ResourcePromptPublicationIsFrozen` checks rejection, fresh rebind and retained request/repair |
| Every normal model request uses one compiler | ConversationKernelAdapter.Model → ConversationModelSession.CreateRequest → ModelContextCompiler; repair also uses compiler; `ResourceCompilerFiltersBeforeBudget` | Preserved in final cleanup; no second builder |
| Large payloads remain reference-first/bounded | Existing CAS/download/upload owners; `ResourceRuntimePayloadStorage`, `ResourceCompletedCallDoesNotHydrateArguments`, binary/raw admission cases | Known source-allocation limits below are not closed by transport bounds |
| HTML/viewers use Gateway, not copied current JSON | ResourceDataPlaneService and resource-backed bindings; `ResourceBoundedTableLeaseUsesOneSnapshot`, `tests/web/resource-data-plane.test.js` | Real WebView2 qualification; HtmlWorkspaceDataSource now contains binding metadata, not the removed Json body |
| Slow consumer cannot create unbounded buffering | Pull-based reads, busy/lease/byte limits; `ResourceBinaryChunkBudget`, sequential stream/download/upload browser cases | Real WebView2 responsiveness qualification |
| Schema/mapping change invalidates derived currentness | ResourceStateProvider/ResourceDerivedViewService + reducer dependencies; `ResourceSchemaMappingDerivedPublication` | No new semantic layer required by this review |
| Wave 5 retention roots and unavailable historical payloads | Core CasMaintenanceService scans chats, VBA journal, authority revisions/views/parts and mutation journal; `ResourceUnpublishedRevisionRetention`, retained missing/corrupt-CAS cases | Conservative retention already protects roots; do not invent a new GC/checkpoint subsystem |

Finite closure status:

1. **Catalog capture correctness — complete host-neutral.** The generation from
   `UseInput` accompanies the active tools/skills/prompts into the model session.
   `CompileCurrent` compares it with the catalog scope in its single frozen
   `CaptureMany` result, before compilation or receipt publication. An intervening
   publication returns `RESOURCE_CATALOG_CHANGED`; no mixed request reaches the
   provider, and no automatic model/tool replay is introduced. Fresh capture/rebind
   permits the next request. Publication after freeze cannot alter that request;
   repair also closes over the original catalog/settings/budget rather than later
   rebound session fields. The existing frozen-prompt test now deterministically
   covers this interval. Published global-tool projection does not rediscover
   document VBA; bound host/document registrations come from the input owner.
   Local closed-document runs and default publication refresh perform no incidental
   Office discovery. Explicit Library/document discovery keeps its existing guarded
   owner. Owner: existing catalog capture/model-session owners.
2. **Integrated mutation acceptance — complete host-neutral.** The new two-chat
   scenario covers changed/no-op/unknown through native Excel execution, the shared
   journal/authority and the next actual model request. Verified change without a
   captured complete after-state yields an Unknown head, while the durable effect
   remains VerifiedChanged (distinct from UnknownAfterDispatch). No-op preserves
   the head/generation; prior frozen requests and historical CAS remain unchanged.
   The existing stale VBA confirmation case now checks competing authority state,
   no mutation publication/backup, separate conservative guard-drift publication
   and retained replacement bytes. Two focused checks cover all four scenarios;
   no production change was required. Finer sectional validity is not mandatory:
   Evidence/Compiler §5 and Authority §47 permit conservative head semantics.
3. **Source allocation — Outlook unresolved; Inspector fixed host-neutral.** Outlook `MailItem.Body`
   allocates before its ceiling is checked (tracked in RISK_REGISTER). Inspector
   JSON serialization now stops at its bounded preview, including bounded escape
   buffers; compiler/input allocation is unchanged. Owners and revisit triggers are in
   [BACKLOG](stabilization/BACKLOG.md#user-deferred-source-allocation--2026-09-07);
   risks remain open. This is permission to finish host-neutral cleanup, not evidence
   of bounded-source safety or a new diagnostics/performance project.
4. **Final cleanup — complete host-neutral.** The removed public readers have no
   execution/catalog path; their remaining identifiers reject incompatible accepted
   history explicitly. The copied HTML data API/store is absent. Final corrections
   separate normalized VBA guards from immutable raw bytes and remove incidental
   document discovery from global publication projection. Reviewed R61 inventory
   (66 ids/66 variants), current HTML diagnostics and delivered Plan/VBA resource
   assets replace stale fixtures; completed backlog items are removed. Fourteen
   focused harness cases and eight Plan browser-script cases pass, not a full-suite
   or Office validation. Conservative retention roots are already implemented;
   checkpoints/finer coverage are optional improvements, not unfinished migration.
5. **Remaining required qualification — Windows/Office/WebView2.** Accumulated
   live-provider, COM/lifetime, model and UI evidence remains open before formal
   release or claims that those environments are qualified. No new provider kinds,
   universal raw expansion or generic
   cleanup workstream is scheduled.

## Delivery order

MASTER waves are one dependency-ordered implementation: shared foundation →
mutation/evidence → frozen compiler → reference-first HTML/viewers → schema/derived/
catalog/retention cleanup. Host-neutral checks do not close real Windows x64 +
Office x64 + VS 2022/WebView2 qualification or any release gate.

## Shared selectors and retained context — 2026-10-01

Read, HTML binding and JS share `ResourceSelectorContract`: semantic target,
explicit value view, optional record-array path and separate integer `pageIndex`
for rendered pages. Office record paths are runtime-owned; omit path. HTML's view
is required and binding policy defaults to head, with explicit exact snapshots.
Whole resource and core skill reads use the same bounded, revision-pinned Gateway
assembler; partial pages cannot masquerade as complete loads.
The retained reader transfers its already verified complete text within the read
operation. `ReadWhole` consumes that text directly instead of rereading CAS for
each internal page; `ReadLines` slices the same materialization and records only
the excerpt as observation bytes. Publication, exact revision and whole-read size
checks remain required. Other paginated providers still use the bounded assembler.
No body survives as a cache for another operation.

After compaction `ContextWorkingSet` supplies original archived call/result
candidates and mandatory/optional priority. `ModelContextCompiler` owns currentness,
exact coverage deduplication, hydration and projected token budgeting for both
active and archived bodies. Active visible coverage takes priority; an input
reference alone cannot replace source bytes. Archive admission retains at most
32 bodies, considers at most 64 body candidates and charges cumulative input bytes
against the request payload limit before hydration. Bodies then consume their
actual projected token cost, removing the earlier byte-as-character estimate.
Current mutation sources use the same lazy path. The compiler creates operation
receipts from the original facts and applies the residual budget. Mandatory unresolved outcomes
remain required, including with no optional budget. Optional receipts are selected
newest first within the 24-receipt/2,048-token bound. Materialization examines at
most 64 optional candidates and charges call/result input bytes before CAS reads,
capped by the smaller of 256 KiB and the request payload limit. Rejected receipts
still consume this allowance; earlier affordable candidates remain eligible
within it. Accepted calls supply semantic targets absent from result data, without
including write arguments in the receipt. Missing selected exact bodies fail
explicitly rather than substituting an externalization header. The context receipt
records actual hydration and each original payload, including omitted candidates.
No operation is executed by this path. It never rereads
Office to construct a prompt and never replays mutations. Bodies may leave the
request budget without changing their original terminal outcome or existence.
See the [continuity contract](conversation-protocol.md#agent-continuity-audit--2026-10-01).
