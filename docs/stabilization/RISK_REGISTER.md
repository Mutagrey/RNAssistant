# Stabilization risk register

Current triage is at the beginning of [PROGRESS](PROGRESS.md).
The dated incident notes distinguish observed symptoms, reproduced host-neutral
causes and remaining Windows/target-model evidence. The R-number table retains
the severity assigned when each risk was opened; a row marked fixed host-neutral,
retired or deferred is not an active P0/P1 implementation defect merely because
that original severity remains in the table. Release qualification is a separate
exact-build gate.

## Source-size audit — 2026-10-22

Fixed source limits were removed across live text captures, HTML and file import,
text indexing, editor libraries and shared transport. A discovered `int.MaxValue + 1`
lookahead overflow in artifact search was corrected with saturated lookahead; generic
first windows preserve UTF-16 boundaries and partial evidence. Transport no longer
allocates a full declared upload at open. Runtime typed consumers, COM properties
and download producers can still materialize a complete source: available memory,
array/string/offset representability and real host behavior remain practical limits.
This is not constant-memory qualification for Office or arbitrary binary editing.
Large-tail and recovery checks also exposed nested 20-MiB CAS visitor, 8-MiB
retained snapshot and 16-MiB accepted/pending action hydration caps. These were
removed at their owners; exact integrity and single-source hydration remain required.

Remaining operation/preview/format limits are classified in the canonical
[source-size policy](../resource-fabric.md#source-sizes-and-context-budgets--2026-10-22).
The optional history/cache budgets are deliberately decoupled from source admission.
CodeOnly UserForm deletion rechecks Designer state at dispatch; actual VBE/COM
creation/deletion/restoration, cancellation and large-source WebView2 responsiveness
still require Windows evidence. The deployed .NET Framework JS worker also needs
target-host execution evidence after removal of its duplicate source/request cap;
its execution and result budgets remain unchanged. No Office/VSTO validation ran
on this machine.

Baseline comparison at `43f71591` reproduces the prompt fixtures' unrelated global
settings-callback count assertions and the old Outlook binding `path` argument and model-facing `records` view.
Those fixtures now assert retained source/failure evidence and the current semantic
binding contract. Stale browser asset-version assertions were aligned with the
existing entrypoint; no product behavior was changed to satisfy them.
The separate `resource cutover: schema mapping derived publication` failure was
resolved on 2026-10-23. The fixture first created a document-owned snapshot without
publishing it through `DocumentArtifactStore`; chat replay correctly omitted that
metadata. After modelling the intended historical chat-owned snapshot, the test
exposed a real fork/recovery bug: rebinding promoted the copy to document ownership,
so its new body disappeared on chat replay, and explicit restoration was blocked
by the degraded active pointer. The HTML owner now preserves snapshot ownership
and re-evaluates recovery after selecting a valid historical body. The focused
fixture and adjacent host-neutral HTML/fork checks pass; Windows/Office behavior
is not qualified by these results.

## Resource Context review — 2026-10-22

Scope: the [Resource Context plan](resource-context/PLAN.md), its implemented
provenance/context work and later shared large-source reads/writes through
`dae0dda8`; current consumer integration was checked alongside those changes.
Owners: existing model projection/compiler, resource gateway/authority, workspace
file and project upload services. The review reproduced and repaired:

- **Final read projection:** `sourceCoverage`, `sourceEndChar` and
  `sourceTotalLines` were removed before model delivery. The final shared projector
  now preserves typed continuation coordinates and still hides runtime identities.
- **VBA currentness:** separately retained module line windows stayed current
  after a module write or observed external drift. Both paths invalidate windows
  belonging to that component; historical bytes and unrelated modules are retained.
- **Managed text restore:** restart inspection of a restored source beyond
  256 MiB inherited the binary budget from its raw payload MIME type. Text restore
  now uses its streamed reconciliation path without that unrelated ceiling.
- **Unicode first windows:** implicit Word/PowerPoint character windows could
  split a UTF-16 pair and fail. The implicit read can shorten its end by one;
  explicit invalid selectors still refuse. The notes fixture reproduces the boundary.
- **VBA whole-read eligibility:** more than 500 lines always produced partial
  evidence, even for a small module. Bounded size probes now allow a fresh whole
  capture when it fits; independently captured windows do not become whole evidence.
- **Project upload integration:** the consumer still marked text above 1 MiB
  unreadable. A bounded model-text probe now validates the entire UTF-8 stream;
  large valid text is admitted and invalid trailing bytes remain rejected as text.

The audit keeps full in-memory VBA validation and whole-module replacement: they
protect the existing mutation path and do not add source bytes to model context.
The existing compiler/CAS/authority, exact revision guards, prepared-before-effect
journal, read-back and non-replay of unknown effects remain the selected design.
No second store/compiler, generic Office abstraction or broad rewrite is justified
by these findings. Common semantics do not imply identical provider capabilities:
bounded selectors, typed Office mutations and UI transfer budgets remain explicit;
arbitrary binary editing is outside the agreed scope. The follow-up source-size audit adds exact character windows for an over-budget VBA
line. Non-text project file views remain
in [BACKLOG](BACKLOG.md#чтение-нетекстовых-файлов-проектным-чатом--2026-10-21).

Focused host-neutral fixtures cover each repair, with existing streamed writes,
history/compaction, compiler admission and saved-request manifest checks. A
pre-existing narration fixture counted format-repair retries as extra compactions;
its retry setting is now explicit without changing production policy. This is not
a full-suite or real Office qualification. Previously recorded unrelated fixture
failures remain open; Windows/VBE/Word/PowerPoint/WebView2 evidence is still required.

## Resource Context provenance gaps — 2026-10-22

Open; owner: model-context/LLM diagnostics. The
[Wave 0 audit](../audits/resource-context-current-state.md#4-находки-и-воспроизведение)
records source evidence separately from pending runtime reproduction:

- **RC-01, fixed host-neutral:** strict-schema projection removes duplicated
  parameter schemas while retaining the message ID. The transport now marks
  changed text `Unknown` per request attempt; exact saved bytes remain authoritative.
  Focused schema/object/repair/fallback and pre-dispatch trace checks pass.
  Windows/WebView2 display of this build remains unqualified.
- **RC-02, attachment-selection slice fixed host-neutral:** the existing request trace records selected
  text/image/audio parts and explicit attachment/budget omissions after transport
  materialization, with final request positions and selected-byte digests. PDF
  page/count claims require renderer evidence; unavailable counts stay unknown.
  The parent message remains `Unknown` after attachment augmentation. Focused
  payload/trace and web inspector checks pass. Typed operation/evidence/observed-
  coverage links now survive the compiler receipt and saved request; positive
  body-delivery claims require verified synthetic hydration. Known compiler
  decisions now give typed exclusion reasons (explicit, evidence currency,
  deduplication, budget and archive-body limits); unattributed transformations
  remain `Unknown`. Zero archive-body budget skips CAS hydration. Optional archive-
  operation budget/selection skips and calls represented by retained receipts now
  have separate typed reasons. A mixed-request golden check compares saved UTF-8
  bytes and the persisted manifest for text/image/PDF/audio selection and a shared-
  limit omission. Other compiler transformations and Windows/WebView2 display
  remain unqualified.
- **RC-03, fixed host-neutral:** two identical complete reads plus a tight request
  budget reproduced a receipt pointing to an evicted body. Folded receipts now
  resolve after budget admission: no stale `retained_result` pointer, explicit
  `bodyIncluded=false`, unchanged durable reads and no delivered write evidence.
  The focused resource compiler/budget check passes. Windows evidence remains open.

The Wave 0 audit did not reproduce a live data-loss/write-effect failure.
No new authority/store/adapter is introduced; Windows qualification remains open.

## Plugin source-chat accepted history — 2026-10-21

The user's Windows screenshot showed `Native history needs one matching runtime ID`
while a Data Templates source chat tried to fix styles. Host-neutral reproduction
confirmed one matching defect: `WorkspaceConversationService` saved accepted
calls with a runtime ID only inside `ToolCalls`, omitting per-call `ToolCallId`,
`ToolName` and the exact
model-attempt origin. New calls now retain all three, including batch index; a
controlled two-call run, restart and next turn pass. Existing records lack the
successful attempt ID and cannot be repaired by inference. They remain visible;
the next turn fails explicitly with guidance to create a new chat. Windows/WebView2
delivery of this exact build remains open.

## Live prompt qualification — 2026-10-20

**Open — model-facing behavior and live eval coverage.** Owners: prompt/skill and
capability catalog owners, model protocol diagnostics, and the existing eval
fixtures. The [paired live comparison](PROGRESS.md#live-prompt-comparison--2026-10-20)
uses one cloud endpoint/profile with unchanged task bounds and acceptance.

- Minimal VBA admission is [fixed and checked live](PROGRESS.md#minimal-vba-admission-and-live-check--2026-10-20)
  at `json_object` / 32K / 4096 output: controlled sequence and two live runs pass.
  Its previous twenty-tool core left only 120 estimated message tokens at the end.
  The [six-tool Excel bootstrap](PROGRESS.md#minimal-excel-bootstrap--2026-10-20)
  lowers actual first-request input from 14,110 to 9201. Its first two combined
  runs complete 3/4 edits. The later [recovery-guidance run](PROGRESS.md#live-recovery-through-compaction-and-skill-removal--2026-10-21)
  passes 4/4 after explicit removal of the completed HTML skill and loading VBA.
  This is one successful trajectory, not a reliability rate. Simultaneous HTML/VBA
  selection can still exceed 32K; active skills and correctness checks must not
  be silently dropped. The [production-text report fixture](PROGRESS.md#excel-report-discovery-with-production-skill-text--2026-10-21)
  now passes all six workbook checks live through direct discovery and, separately,
  explicit report-skill loading. The fake stub is removed. Each case is one
  trajectory; unprompted report-skill/task-list selection and real Excel behavior
  remain unqualified.
- Excel report token overhead: the original direct/skill cases require three
  protocol repairs each after attempted write batches, rejected before execution.
  [Compact admitted-schema receipts](PROGRESS.md#compact-admitted-schema-receipts--2026-10-21)
  remove duplicate model-history descriptors only against the exact current
  callable surface. Five results shrink from 14,077 to 1561 characters; full
  durable evidence remains. The same direct-loading task passes 6/6 again with
  136,712 actual input tokens versus 174,451, but also takes 12 requests instead
  of 13 and two repairs instead of three. This pair establishes neither a general
  saving rate nor reliable format/batch compliance. Owner: capability/model-context
  projection. [Compact format schema](PROGRESS.md#compact-excel-formatting-schema--2026-10-21)
  removes repeated field definitions while preserving arguments and strict output.
  The same task passes 6/6, but cumulative input rises to 153,363 across 14 requests
  with four repairs. Reduced schema size does not yet establish overall savings
  or reliable batching. [Capability source-read semantics](PROGRESS.md#capability-source-reads-do-not-claim-selection--2026-10-21)
  now omit the ambiguous model-facing `loaded` field with local admission/replay
  checks; live model-choice improvement remains unmeasured. `write_range` now
  requires an explicit destination, with literal worksheet names and quoted
  references. Other mutation destinations and live model choice remain open.
- Repair guidance conflict is fixed in `ModelProtocolClient.CreateFormatRepairMessage`:
  generic repair no longer overrides the parser's skill-only route with direct
  schema discovery. Focused checks cover both routes and no execution before
  admission. This does not prove that the model always follows recovery guidance.
- Compaction response failure, owner `ContextCompactionRuntime`: two combined
  runs reach the helper after three verified HTML edits and fail on fenced JSON.
  [Bounded response repair](PROGRESS.md#bounded-compaction-response-recovery--2026-10-20)
  now follows the [shared attempt setting](PROGRESS.md#shared-compaction-attempt-setting--2026-10-21),
  including the first attempt, with unchanged frozen sources, strict provenance
  checks and separately budgeted requests. In the captured two-attempt live
  run that reaches repair, JSON fences disappear, but the response promotes an
  assistant choice to a user decision and cites successful mutation receipts as current observations;
  validation rejects the whole response and publishes no checkpoint. The helper
  inputs are 4871 and 4928 actual tokens, so this failure is not an overflow.
  A second run fails before compaction on an unloaded tool. The later correction
  makes the prompt use `observationEligible` and reports the offending source/kinds.
  Both subsequent live runs publish a valid checkpoint; the final candidate also
  reaches full source acceptance after explicit skill removal. Rejected-batch
  feedback now states that no calls or skill loads executed, and admission recovery
  names `action=remove` without dropping skills automatically. Repeated reliability
  and live interruption/resume remain open. No JSON stripping, larger context or
  fabricated summary.
- Initial empty model responses consume 4096 output tokens in all three new runs;
  ordinary repair recovers each initial step. One run later exhausts its two
  protocol attempts on mixed skill loads and an unloaded tool. The endpoint reports
  no reasoning content; that alone does not explain the empty completions.
- Workspace: both versions produce exact final files, then choose `done` despite
  violating the explicitly required read-after-each-edit order (0/9 sequence stages).
  Current final-file acceptance does not certify arbitrary user-prescribed ordering.
  Review evidence-reuse guidance against explicit verification requests; the trace
  establishes a model behavior failure, not its prompt-level cause.
- Neither workspace run reaches compaction. The live interruption, external-edit
  protection and resume assertions remain unexercised; controlled tests are still
  the available evidence. Make that boundary reachable in a representative live
  scenario without synthesizing model replies/checkpoints or weakening acceptance.

Actual first-request input shrinks and the combined task has one full live pass;
repeatable success remains unqualified. The successful run costs 19 requests and
282,998 actual input tokens, including recovery; workspace cumulative input also
grows in the earlier comparison. No general token, quality or latency gain is established.
Local 12B/9B profiles failed the memory preflight; cloud results
do not qualify those models or real Office/COM/WebView2 delivery.

## R61 inventory drift — 2026-10-21

Contained host-neutral / Tool Library contract-inventory owner. At `f6c3f5f0`,
the focused comparison had 74 expected and 80 actual rows: six later tool IDs
and five changed Common descriptors. Each ID, binding, mode, property path and
descriptor change was reviewed against its source; `common.resources_read`
added a semantic `partNumber`, and the other four Common differences were guidance
wording. The [inventory](R61_TOOL_PROPERTY_INVENTORY.tsv) now has 80 exact rows;
`tools: R61 built-in contract inventory` passes 1/1 after the destination wording
clarifications. This closes the baseline drift, not the separate live-model and
Office qualification gates. The earlier failing comparison remains in
`/tmp/rna-r61-current-review.log` for local diagnosis.

## Excel discovery and name-validation follow-ups — 2026-10-21

- Fixed host-neutral / Excel resource owner: range enumeration now requests the
  existing `workbook` metadata snapshot containing `UsedRange`. Missing metadata
  fails explicitly; populated ranges reach semantic discovery without cell reads.
  Shared paging retains truncation and invalidates changed coverage. Four focused
  checks include actual fake enumeration in the worksheet-name regression, with
  its injected snapshot removed. Real Excel delivery remains unqualified.
- Contained host-neutral / Excel resource owner: very large ranges now expose a
  compact structure overview and bounded sample targets without capturing cells.
  Raw reads still refuse oversize capture, including `table/records` with a small
  row limit. Five focused checks cover full-sheet metadata and selected slice reads.
  The COM metadata branch, latency on large workbooks and actual model choices
  remain unqualified.
- Contained host-neutral / Excel read owner: `excel.summarize_columns` scans up to
  2000000 explicitly selected cells in 1024-cell chunks and returns only coverage
  and typed counts/numeric aggregates. A failed chunk or changed coordinates fails;
  cancellation stops the scan. A concurrent value edit with unchanged coordinates
  is not detectable, so results are marked `sequential-observation`, not an atomic
  workbook snapshot. Windows/Excel COM latency and live model/tool token effects
  remain open. Exact row selectors, filtering and grouping remain in the
  [bounded analysis backlog](BACKLOG.md#large-excel-analysis-without-full-model-context--2026-10-21).
- Open / Excel sheet-domain owner: `ExcelWorksheetNameRules.IsValid` does not reject
  the reserved `History` name listed in [Microsoft's rules](https://support.microsoft.com/en-gb/excel/rename-a-worksheet).
  Align add/rename validation separately; the current write-target work does not
  change sheet creation or qualify host-specific naming behavior.

## Unified stabilization verification gap — 2026-10-20

**Harness / request inspector fixture, closed 2026-10-20.** The unchanged
`model context: request schema duplication size` check failed before its wire
assertions: it tried direct admission of skill-only `common.html_workspace_write_file`
and reconstructed evidence from a manual projection without the native revision.
The fixture now admits direct optional `common.markdown_save` using the native
accepted descriptor result. Production admission is unchanged. Both `model context:
request schema` checks pass, including exact retained bytes for four skill states
in two transports and schema-rejection fallback. See the
[prompt audit](PROGRESS.md#prompt-and-skill-guidance-audit--2026-10-20) for measurements;
live model quality and Windows/Office qualification remain separate evidence.

## Plugin native qualification and interrupted scratch — 2026-10-19

Owner: application plugin services and WebView transport. Host-neutral checks cover
independent grants, immutable update, prepared publication, no preview inheritance,
vendor context refusal, scoped results and uncertain file/directory effects. The
Chromium test runs the production SDK/Worker with mock native callbacks; it does
not qualify actual Windows Frame API event ordering, picker, origin interception,
permission denial, result download streams, renderer failure, multiple panes,
controller cancellation/drain or Framework pipe ACLs. Complete those checks on
Windows x64 + WebView2 + Office x64 before claiming native qualification. The
source-chat model/provider route also needs that live-host evidence; shared CLI
smoke is not a substitute. The old compiled Tabular pin remains under its listed
removal gate, not a plugin fallback.

Normal result discard and last-owner shutdown remove private scratch files and
clear their current file authority; selected data projects are never removed.
A process crash can leave orphan `plugin-staging` / `plugin-results` files and
preview scratch. Implement cleanup with an explicit process/session lease before
removing such folders: other panels/processes may still own them. Do not replay
writes, delete user-selected projects or describe abandoned results as published.

## Data Templates browser mapping check — 2026-10-18

Closed 2026-10-19. `tests/web/data-templates-export.test.js` expected the former
inline-draft notice and removed `data-mapping-path` attribute. It now waits for
the shared dictionary save and selects the mapping by its source value. Read-back
checks cover rejection without writes, successive immutable versions and their
agreement with the downloaded draft. The later cancellation check now restores
a dictionary-compatible draft and waits for the run after asynchronous project
admission. No application behavior changed. The complete suite passes in local
Chromium with 1160/360-pixel light/dark views, export/download, Worker and result
staging checks. Owner: Data Templates export tests. Windows/Office qualification
remains separate.

## Data Templates integration gates — 2026-10-16

Open feature gates, not newly reproduced baseline failures. Owner: Data Templates
application integration, with existing file/resource/WebView host owners.
The [portable M0/M2 core](../data-templates.md) passes focused JS/browser checks;
matrix records/provisional batches have no native tools or publication authority.

- Architecture boundary: the independent application source project and folder
  export now exist, with shared UI/core and exported-package browser evidence.
  The plugin foundation now replaces root UI mounting and provides scoped
  inspection/extraction/result writes. The compiled app-code hash pin remains only
  for listed legacy consumers with [removal gates](MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16).
  Windows qualification of the new isolated path remains open.
  App updates must not require rebuilding C# when the host protocol is unchanged.
- Shared project commands now serve CLI and a thin Office panel through a
  compatible application source module. Core file checks, fake-controller bridge
  lifetime/worker/unknown-effect checks and browser UI checks pass. Real
  Windows/Office validation, general workspace file views and full M7
  reconnect adoption remain open. Plugin source chats now reuse the shared
  WorkspaceConversationService; their live native model/provider evidence is open. Keep Framework 4.8 compatibility and bind the
  Office source independently of workspace/project identity.
- Normalized fixtures do not prove unsaved live Excel values, a consistent
  disjoint capture or metadata availability. Close with bound provider capture,
  exact dependencies and Windows/Excel evidence; do not synthesize unavailable
  date-system, merge, formula-cache or formatting fields.
- Worker contract labels are not module hash pins or security evidence.
  The shared runtime now captures approved shipped modules against a generated lock
  and compiled package hash, and binds a revocable source resource scope. Chrome
  checks the real C#/Worker path, dedicated CSP and typed acknowledged output channel.
  A separate application-owned WebView2 adapter is implemented, but not built or
  qualified on Windows here. Close the remaining gate with actual request interception,
  navigation/permissions, hidden-controller/response-stream behavior, renderer failure,
  startup cancellation/drain and hard-stop latency. Keep project input/result pins separate.
- Application folder export is implemented; user data-project publication/export
  remains open. Probe JSON cannot act as a
  passing test receipt. Close with existing journal/authority publication,
  stale-input rejection, read-back and complete portable dependency capture.
  M2 batch acknowledgement is consumption, not publication: a later failure must
  leave earlier batches unpublished. The native channel revokes output on cancellation
  during write/validation and requires confirmed source cleanup before success. A durable
  plugin sink now uses existing file/CAS ownership, guarded streaming writes and
  explicit unknown-write reconciliation. Native qualification and crash-orphan cleanup
  remain open; this does not close general dataset publication.
- The loader's enumeration progress and 50-row pages do not prove processing of
  many large files. The shared application's preparation queue now has serialized
  captures, exact project/source pins, budgets, pause/cancel/retry, bounded pages
  and maintenance exclusion, with host-neutral lifecycle checks. The former root preparation UI is removed; the plugin selects files inside
  its explicit data grant and invokes the shared Worker service. Legacy preparation
  commands retain their independent CLI/native coverage. Source declarations persist
  through guarded manifest saves with stable domain IDs and explicit recovery;
  plugin parser dispatch is implemented; native qualification, batch requests and
  common execution events remain open.
  The separate XLSX reader now has streamed selective reads, bounded pull-byte input,
  exact numeric cells, resource budgets and Worker cancellation. The shared native
  reader now pins selection/maintenance until I/O drains and supplies bounded ranges
  over protected temporary seek staging. Host-neutral checks cover encoding variants,
  cancellation and cleanup. The resource handler now transfers actual snapshot ranges
  to the Worker under captured module pins and CSP in the test host; actual WebView2
  qualification and representative staging disk/RAM/throughput measurements remain open. Its 30,000-row
  fixture and writer interoperability do not establish 80 MB behavior or RAM cost.
  Stored formula caches have unknown freshness, and only consumed ZIP parts are
  CRC-checked; neither is a publication receipt. Close the [queue/large-file gate](../data-templates.md#large-files-and-processing-queue--planned)
  with integrated workspace-owned parsing, bounded off-UI readers, hard cancellation,
  unresolved-write handling and measured peak memory/UI/cancel latency on real
  80 MB-class XLSX batches in Windows/Office/WebView2. The sparse 80 MiB fixture
  verifies metadata-only selection, not parsing or a workbook-size guarantee.

No existing global HTML/transfer limit was raised; no imported JS, fake live
source claim, hidden executor fallback or second mutable project store was added.

## Architecture and regression review — 2026-10-15

Reviewed `a05ffe96` with the pre-existing follow-up-input test/doc changes retained.
Scope: commit/concurrency boundaries, skill admission, unknown-effect continuation,
chat UI and previously recorded fixture failures. This is a focused review, not
full-suite, live-model or Windows/Office/VSTO qualification.

**P1 fixed host-neutral / Core scalar metadata commits.**
`CommitScalarMetadata` recovered the current journal tail after a rejected stale
write and assigned that revision/hash to the unchanged stale `ChatSession`.
A disposable two-reader reproduction first commits a new model and artifact,
then rejects the old reader's title change. Before the fix, the old revision
advanced 1 → 2; a subsequent ordinary commit succeeded, restored the old model
and removed the new artifact from the current projection. Historical events/CAS
were retained; this was lost current state, not physical blob deletion.
Concurrency refusal now preserves the caller's cursor and timestamp without
replaying the log. The same subsequent commit is rejected; cold reload preserves
the winning model/artifact and permits a new explicit write. Regression covers
title, model and reasoning setters, exact journal preservation and reload.
No controller/COM behavior or on-disk format changed.

**P1 contained for new selections — 2026-10-20 / Core skill selection and Office/Runtime request admission.**
Original finding: `SkillTaskService.Select` checked only bodies/dependency schemas
against half the input budget, then persisted selection without admitting the
complete next request. A scripted Excel conversation with normal ToolStore/settings dependencies,
32,768 context tokens, default 3,072 output tokens and default prompts accepts
`common.skills_load(common.vba_code_editing)` as `ok`, then fails before the next
model call: estimated messages 23,668 tokens versus 23,422 available. After cold
reload, a new user request to remove the skill fails before any model call, with
one selection still retained. At 65,536 tokens the same two-response script
completes; that control is not a fix or real-model acceptance.
The probe uses the built Harness production owners and a fake Excel adapter;
no VBA mutation or HTTP dispatch occurs. This independently establishes an
admission/liveness defect; it does not establish the cause of every VBA test
failure below. The 2026-10-20 change stages the candidate skill/tool surface and
projected load result through the same request compiler and reserves before
replacing selection. The 32K fake-Excel regression now returns a load refusal,
reaches the next model request, and permits another turn after state serialization;
the 64K control performs one patch. Workspace uses the same staged selection
owner and its own existing request compiler. There is no LLM preflight, silent
eviction or enlarged product budget. Older already-persisted oversized selections
still require explicit reset/profile correction; Windows and live-model evidence
remain open. See the [unified report](PROGRESS.md#unified-stabilization--2026-10-20).

**Existing open risks confirmed by source review:** chat relocation still appends
the binding before physical rename; interruption between them is not covered by
the six refusal tests. `AgentProgressTracker` clears its inspection floor after
any successful independent read, even one without evidence for the affected
target. This is the documented workflow floor, not a new claim that unknown
effects become verified: health and replay guards remain unknown/protected.
Concrete owners/removal gates are in [Structural debt](BACKLOG.md#structural-debt).

Current regression evidence (filters overlap; no aggregate coverage claim):

| Check | Result / remaining owner |
|---|---|
| Harness build before/after metadata correction | Pass; two existing PDF CA1416 warnings |
| `kernel:` | 59/59 |
| `agent:` | 52/56: write-unknown characterization, VBA batch, Excel core count 19 vs 20, arbitrary-macro admission fail; Office conversation fixtures/skill admission owners |
| `kernel replay:` | 9/10: known unprojected effect expects all protocol messages excluded, while recovery now retains completed siblings; narrow incomplete-exchange assertion still needs reconciliation |
| `architecture:` | 4/5: the 13 recorded concrete-storage references still fail the conversation-port assertion |
| `tools: R61 built-in` | 0/1: the seven already recorded Common inventory differences remain |
| Metadata conflict / SQLite / stale save / relocation | 1/1 (three setters), 5/5, 1/1, 6/6 after correction |
| Compact catalog / artifact prompt | 1/1 and 2/2; old Outlook count and resource-metadata fixture entries can close |
| Web passing | `chat-sync`, `chat-inbox`, `artifact-library-projection`, `chat-transcript-layout`; last uses bundled Playwright and installed Chrome |
| Web failing | `chat-history-paging` contradicts retained commentary; `plan-document` and `run-journal` pin obsolete asset keys; `html-preview-reuse` omits `missingVendors` |

Earlier `agent: 54/54` and `kernel replay: 10/10` are historical evidence for their
tested snapshots, not a passing result for this tree. Failed checks preceded the
metadata correction; their failure must not be attributed to that correction.
Current CLI smoke suites were not rerun. The previously reported repeated-answer
incident remains unresolved; these scripted probes do not explain the photographed answer.

## CAS reuse and publication integrity — 2026-10-15

**Fixed host-neutral / Core CAS and its publication consumers.** Review began on
`main` at `85159f14` with existing skill/settings/UI/doc changes; those were left
intact and became `79af79ca` during the review. The initial Harness build passed
with two existing PDF CA1416 warnings, and `storage:` passed 75/75.

Root cause: `HasStoredReference` checked presence/length/envelope, but consumers
treated it as integrity evidence. Four regressions failed before the production
change: restoring supplied text into the same artifact left a corrupt blob;
original publication accepted a damaged part; shared-context publication accepted
damaged evidence; workspace move dispatched with a corrupt retained preimage.
New coverage also exercises extracted parts, delete/restore, projected model
claims and raw/compressed/plain/encrypted bodies.

`StoreText` now delegates to the verified byte writer; its known-reference bypass
is deleted. Reference-only consumers use the existing `HasVerifiedReference`.
Shared-context validation precedes snapshot retention, so rejection creates no
revision or head. The renamed internal availability hint has one consumer:
unchanged trusted chat artifacts. A blanket verification pass on every metadata
save was rejected because it would re-read unrelated bodies without publishing
them. No additional store, retry, persistent cache or format migration was added.

Structural comparison: text-write paths 2 → 1; metadata-only authorization checks
at seven publication/evidence/mutation sites → 0. Rejected original/context
publication leaves authority generation and revisions unchanged; rejected file
operations execute zero dispatch callbacks and leave zero prepared attempts.
Existing unchanged-artifact checks still report one externalization for initial
save plus subsequent metadata/equal-text saves, and zero for a metadata save after
reload. Stronger checks read actual CAS bytes; no latency improvement is claimed.

Final commands and results:

```text
dotnet build tests/RNAssistant.Harness/RNAssistant.Harness.csproj --no-restore -nologo -v:quiet
  PASS; two existing CA1416 warnings
dotnet build src/RNAssistant.Cli/RNAssistant.Cli.csproj --no-restore -nologo -v:minimal
  PASS; no warnings
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'storage:'
  PASS 77/77
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'workspace files:'
  PASS 12/12
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'shared context:'
  PASS 2/2
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'shared HTML: text discovery'
  PASS 2/2
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'attachments: import commit delete'
  PASS 1/1
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'artifact transfer:'
  PASS 5/5
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'document originals:'
  PASS 2/2
git diff --check
  PASS
```

Scope/risk: this does not qualify Windows/Office/WebView2, live models, the full
suite or hostile filesystem races after verification. Corrupt bodies without
known original bytes remain unavailable; no automatic historical-data repair is
performed. Rollback is limited to this slice's source/test/doc hunks; event/CAS
formats and hashes are unchanged, with no data migration to reverse.

## Chat relocation refusals and deletion race — 2026-10-15

**Fixed host-neutral / Core `ChatStore`.** Five regressions reproduced: occupied
destination file/directory and blocked parent appended a new binding to the source
before refusing the move; invalid projection normalization left the caller rebound;
deletion while awaiting the document lock recreated an already persisted chat.
The source check preceded lock acquisition, and `allowRelocatedSession` bypassed
the normal missing-stream guard. No user data was used by these fixtures.

The affected path is `ChatSessionService.LoadSession` / alias reconciliation →
`IConversationStore` → `ChatStore.MoveDocument` / `Move` → ordered document locks
→ JSONL append and rename. JSONL remains authoritative; SQLite and memory caches
follow the moved log. Preflight now runs under both locks before publishing any
binding, normalization participates in identity rollback, and all saves reject a
missing stream with a persisted revision. The recreation flag and unused ordinary
save path override are removed. Moving only the destination check would have left
the deletion race and save-policy exception intact.

Before the fix, `chat relocation:` was 1 pass / 5 failures; after it, 6/6 pass.
Refusals preserve exact source/destination bytes, revision and document lineage;
an explicit later move succeeds, and cold reads preserve history. Transient moves
remain unpersisted. This establishes zero event appends for the tested refusals,
not a timing or throughput claim.

Initial working-tree baseline at `dc164915`: Harness build and `chat sessions:`
18/18 passed. During parallel edits, the same build hit CS0246 (`ToolCall`) in the
unrelated new `Program.ExcelDeletionTests.cs`; that file was not edited here.
Final validation used a temporary source archive of `46e2d4ae` plus only this
slice's source, test methods and six registrations, with repository dependencies:

```text
dotnet build tests/RNAssistant.Harness/RNAssistant.Harness.csproj -nologo -v:quiet
  PASS; two existing CA1416 warnings and source-archive provenance warning
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'chat relocation:'
  PASS 6/6
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'chat sessions:'
  PASS 18/18
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll 'storage:'
  PASS 77/77
git diff --check
  PASS in the working tree
```

**Still open / Core chat relocation owner:** metadata append and physical rename
are separate durable operations. A crash or I/O fault after append can still leave
the new binding at the old path; no automatic retry/recovery is added. A separate
commit/recovery design with fault injection is needed, including document-wide
partial moves. Real Windows/Office Save As, the full suite and live models were not
run. Event/CAS formats and public APIs are unchanged. Rollback removes only this
slice's source/test/doc hunks, without moving histories or reverting other work.

## Workspace file identity and lease review — 2026-10-15

**Fixed host-neutral / Core `WorkspaceFileService`.** On `10f3fa53`, eight entry
points resolved a file ID before waiting for its resource lease. A managed move
could finish in that gap. Deterministic interleavings reproduced a missing head
published for the moved file, a reused source path advertised under the moved
file's ID, and recovery resolving an interrupted target write by inspecting the
now-absent source path. These are authority/recovery defects; no user files were
used or lost in the probes.

One `AcquireFileScope` now binds the locator to the acquired resource lease for
all eight callers. A changed binding fails before observation or journal changes;
the rejected lease is released. A read-only local guard would leave recovery
unsafe; a global workspace lock would serialize independent files. The selected
boundary retains per-file concurrency with one additional locator validation,
no retry and no second identity store. Stale reads no longer publish an extra
authority revision, and stale recovery leaves the unresolved attempt intact.

Baseline: Harness build passed with two existing PDF CA1416 warnings;
`workspace files:` 8/8, `host runtime:` 10/10, `storage: runtime commits` 1/1.
After adding only the shared acquisition seam and regression cases, the original
behavior passed 8 and failed 3. With binding validation, `workspace files:` 11/11,
`workspace snapshots:` 3/3 and `workspace:` 2/2 pass. Harness and CLI build pass.

Commands (repository root; filters run separately):

```bash
dotnet build tests/RNAssistant.Harness/RNAssistant.Harness.csproj --no-restore -nologo -v:minimal
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll "workspace files:"
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll "workspace snapshots:"
dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll "workspace:"
dotnet build src/RNAssistant.Cli/RNAssistant.Cli.csproj --no-restore -nologo -v:minimal
python3 -B tests/cli/read_recovery_smoke.py
git diff --check
```

**Open adjacent fixture / CLI tests owner:** `read_recovery_smoke.py` passes its
five replace/patch/copy/move/delete cases, then fails its restart case on the second
`AwaitingConfirmation` (`files.replace`, after one verified `files.delete`). The
script approves only once although this case requests two mutations. The identical
failure was reproduced in an isolated source archive of `10f3fa53`; it is not caused
by binding validation. Follow-up: handle the two explicit approvals with a bounded
count and retain the existing retry, exact-content and request-count assertions.
This fixture was left unchanged; its restart scenario is not claimed as passing.

The on-disk formats are unchanged. This fix does not rewrite earlier incorrect
events or qualify races with uncoordinated editors, whole-workspace relocation,
symlinks/hardlinks or Windows/Office behavior. Reverting this code/test/doc slice
requires no data migration and reopens the reproduced race.

## Restart context continuity review — 2026-10-15

**Closed host-neutral.** Owners: Core skill selection/protocol pairing, Office
recovery and CLI context preparation. The reproduced P1 was run-wide suppression:
a completed read replayed as two messages until a later accepted call lacked a
result, when both completed messages disappeared from model context despite
remaining durable. Successful mutation receipts were affected by the same filter.

Office and CLI now use shared exact call/result pairing and suppress only
unfinished exchanges. Completed batches survive; CLI persists individual call
projections and reconstructs the accepted batch for kernel continuation. Pairing
also handles confirmation results saved under a later runtime invocation. Recovery
does not re-execute calls, erase history or reset known terminal counts.

New turns copy `SelectedSkills` through existing `run.updated`, with independent
pins and fresh request authority. Revisions, dependencies, policy and budget are
revalidated; explicit removal remains durable. Prompt schema 48 directs removal
of irrelevant skills instead of implicitly clearing all choices on each message.
Current CLI source evidence is also revalidated across turns. Previously those
scopes were omitted, forcing fresh reads of unchanged files. External drift still
invalidates old bodies; prior-turn write guards and confirmations are not reused.

Validation: Harness `chat sessions:` 18/18 (mixed recovery includes 12 role/order/
in-flight cases), `skill packages:` 6/6, `context continuity:` 14/14, `settings:`
6/6, scoped exchange deletion and native batch replay pass. MockDemo resource
workflow covers compaction, four approvals/restarts, follow-up and explicit removal.
Controlled CLI `skill_packages_smoke`, `interrupted_run_smoke`,
`omitted_read_guard_smoke` and both `compaction_resume_smoke` cases pass. These
check retained bodies, changed sources, restricted policy and unchanged write
guards. No full suite, real-model or Windows/Office/WebView2 qualification was run.

## Confirmation input ownership and persistence review — 2026-10-15

**Closed host-neutral.** Current-user selection is shared; pending-tool attachment
copies and role-only selection are removed. Model-input preparation declares its
earlier message updates in the existing commit. Inbox terminal receipts and one
Web revision reducer replace manual acknowledgement/removal merging.

Original review: owners were unchanged between
`cf7e1356` and `2e5e6a49`. The isolated probe uses the existing MockDemo assembly,
the real controller and fake Office/model ports with disposable data; no Office,
network model or user document was exercised. Concurrent storage/test edits are
outside this evidence.

- **P1 / Office confirmation input:** `AssistantController.LatestUserAttachments`
  selects the last `Role=user` message without excluding protocol results.
  With the default `ToolResultRole=user`, a capability result therefore replaces
  the actual user input when pending attachments are captured. On approval,
  the refresh callback passes empty attachments to routing and clears the real
  user's `AttachmentAnalysis`. In the image/helper probe, requests 1–2 contain
  the analysis; requests 3–4 after approvals contain neither analysis nor media.
  This also reproduces with the same helper throughout. Original attachment bytes
  remain stored; the defect is loss of model context during continuation.
- **P2 / Office explicit message persistence:** confirmation refresh can change
  `AttachmentAnalysis` on an earlier user message, but neither the kernel's
  `_pendingMessageUpdates` nor the controller's `Skip(firstMessageIndex)` commit
  names that message. With result role `tool` and an explicit helper change from
  vision1 to vision2 while awaiting approval, the next request uses vision2, but
  a fresh store load still contains vision1. A second approval calls vision2 again
  (three helper calls instead of two), and the final stored message still contains
  vision1. This is a missed consumer of the explicit-commit cutover: the previous
  broad save included earlier messages. The same probe also observes the old
  accepted-call `RunId` after controller reassignment; no independent user-visible
  failure is claimed for that field alone.
- **P2 / Web inbox reconciliation:** the existing
  [cross-window removal risk](#chat-transcript-review--2026-10-12) also has a local
  delayed-acknowledgement variant, reproduced below. Both leave a nonexistent
  pending bubble and repeated history fetches.

Implemented changes and regression coverage:

| Owner | Change and code to retire | Required regression |
|---|---|---|
| Office conversation input / model preparation | Shared `ConversationInputMessage` selection supplies the actual user message. Confirmation derives its attachments at refresh time; pending-tool copies and both role-only attachment lookups are deleted. | Nine role/routing cases through two approvals and restart, plus image steering; native images and helper analysis reach the model. |
| Office conversation writers | `ConversationRunInput.UpdatedMessages` feeds the existing kernel commit. Finalization explicitly names earlier touched messages; no generic body diff or second store. | Fresh-load analysis is checked before every model request. Helper change is persisted, second approval does not rerun it; accepted-call replay updates survive reload. |
| Office inbox projection / Web | Bounded terminal receipt lookup uses the existing event projection. A single per-input revision reducer replaces separate submit/removal echo writes. Queue-only changes do not fetch history. | Remote removal, remove-before-submit-ack, applied-before-history, stale receipts and retry with the original operation ID. |

Initial reproduction: `/tmp/rna-review-M0sQTS/{Program.cs,user-role.log,tool-role.log,inbox-repro.js,inbox.log}`.
Resolution: rebuilt Harness and MockDemo; `--confirmation-input-test` 10 cases,
existing `--unified-agent-test` and `--inbox-test` pass. Harness `inbox:` 9/9,
`storage: runtime commits` 1/1, `kernel replay:` 10/10 and `protocol context:` 6/6
pass, including failed append and known effect after preparation failure. Four Web
scripts pass: `bridge-bootstrap`, `chat-inbox`, `multi-chat-requests`, `chat-sync`.
No full-suite, real-model or Windows/Office/WebView2 qualification is claimed.

## Baseline test failures during architecture follow-through — 2026-10-15

**Closed host-neutral.** The original eight Harness failures were independently
reproduced on baseline `4d716414`; all passed at that correction. Later current-tree
failures are recorded in the [new review](#architecture-and-regression-review--2026-10-15).
No Office effect or full-suite qualification is claimed.

| Owner | Cause and correction | Evidence |
|---|---|---|
| Core storage | SQLite materialization bypassed the chat error contract. Four event readers now share validation and error translation. Corrupt JSONL remains untouched and unavailable. | All three original integrity/tail failures pass; `storage:` 75/75. |
| Core SQLite projection | Invalid SQL storage classes, typed root/header fields, missing/foreign header identity and missing schema could abort catalog/startup or hide valid history. Catalog, full load and pages now share one bounded sidecar rebuild. | Ten damage variants across four consumers, including startup recovery; exact JSONL bytes preserved. A damaged authoritative sibling cannot block healthy chats. |
| Office replay fixtures | Schema discovery cannot grant skill-only tool authority. Select the authoring skill before testing confirmation, cancellation or preparation failure; assert that the intended pending boundary is reached. | `kernel replay:` 10/10, `protocol context:` 6/6, `causal trace:` 6/6. |
| VBA fixtures | The large-source fixture omitted required resource dependencies; the approval fixture demanded a complete source without reserving enough input budget. Use the published catalog and an explicit large context; retain the constrained-context refusal/recovery phase. | `agent:` 54/54, including both original failures. |
| Startup and prompt fixtures | UI labels do not prove a started execution. Persist a typed runtime boundary before recovery. Guidance checks follow their current system/tool/skill owner. | `chat sessions:` 17/17; `settings:` 6/6. |

The Web failure also reproduced with baseline `fcba1ce1`. Its expectations
contradicted the canonical completed-history behavior: progress comments fold
with actions. `live-run-projection.test.js` now verifies collapse, reopen, reload,
failure, cancellation and steering without duplicate or lost comments; it passes
without changing production rendering.

These are 174 focused Harness checks and one Web script. Other risk-register
items and Windows/Office/WebView2 qualification remain separate and open.

## Delivered observations and bridge bootstrap review — 2026-10-15

**Fixed host-neutral; owners: Core context compiler, Office bridge/Web, CLI model
adapter.** Review probes against `4d716414` reproduced:

- **P1:** cross-host application init had no bridge token; applying it cleared the
  panel token and the next command failed before posting. Bootstrap v2 now owns
  the token/contract separately. Host switches apply only application state;
  init/navigation use the same chat projection, including inbox.
- **P2:** successful/failed `web.verify` carried source evidence without its body
  and suppressed an archived read. Deduplication now reserves coverage only after
  body materialization; missing active payloads cannot suppress usable archives.
- **P2:** CLI discarded a prior read when compaction delivered it as an assistant
  source view. `ModelContextSnapshot.DeliveredObservations` now owns delivery for
  both Office and CLI; exact current same-run guards remain with the runtime.

Focused evidence: Harness `context continuity:` 14/14, `bridge:` 37/37 and four
resource authority/source checks; CLI compaction/edit (tool/user), compaction/
restart/drift (tool/user) and omitted-source guard; seven Web scripts including
real bootstrap/chat projections; MockDemo four profiles and failed-turn
persistence. Harness, CLI and MockDemo builds pass (existing PDF platform warnings
remain). The separate baseline rendering failure above stays open. Actual
cross-host COM/WebView2 delivery requires Windows x64 + Office x64 qualification.

## Resource journal names and archived operation admission — 2026-10-15

**Fixed host-neutral / Core resource authority and model-context compiler.**
Follow-up probes against `cc48a6dc` reproduced three distinct gaps:

- **P1:** renamed/case-changed journals disappeared from the CAS root set while
  reachability stayed complete; collection deleted their body in an isolated
  temporary store. The inventory now includes every extension and admits only
  canonical journal names and known sibling-owner files. Unsupported paths block
  replay and all deletion; source files remain unchanged.
- **P2:** archived `target_exists` results lost the target when only the accepted
  call carried `relativePath`/`targetPath`. Selection now retains the exact pair;
  the compiler hydrates/projects the call and extracts semantic targets without
  inserting write arguments into the receipt.
- **P2:** 40 oversized optional results caused 40 CAS reads (484,750 bytes) while
  retaining zero receipts. Compiler admission now limits optional candidates to
  64 and cumulative input to the smaller of 256 KiB and the request payload limit.
  Bytes are charged before reads even if the resulting receipt is rejected.
  Mandatory unresolved outcomes retain their existing full-request budget rules.

Validation: rebuilt Harness, `storage: CAS` 10/10 and `context continuity:` 13/13.
Regression cases cover published/unpublished renamed sources and view parts,
explicit restoration, allowed sibling files, inline/externalized accepted calls
through save/restart for three result roles, rejected archive materialization and
unknown outcomes with zero optional budget. No full suite or Office/VSTO
qualification was run.

## Runtime final response classified as tool result — 2026-10-14

**Fixed host-neutral — 2026-10-15 / Core conversation persistence and Runtime adapter.**
The old `ChatStore.ResolveUpsertType` classified protocol-formatted final responses
without calls as `tool.result.recorded`, producing an unknown-tool row. Office and
Runtime now commit runtime facts explicitly; exact call correlation distinguishes
results from protocol media helpers. Final done/blocked/needs_input replay and
causal queries pass, as do paired batch, paused-view and media-release checks.
Existing historical streams are not rewritten. The follow-up also removed the
non-runtime snapshot writer; all production owners use explicit message commands
as described in [MIGRATION_MAP](MIGRATION_MAP.md#runtime-commit-boundary--2026-10-15).

## CAS and resource fabric review — 2026-10-14

Three defects were reproduced with isolated temporary stores against the existing
host-neutral Harness assembly; source reviewed at `4c5dd000` plus the working-tree
changes. The original review changed no production code and did not attribute an actual
user incident to the artificial filesystem-damage scenario below.

- **P1 / Core CAS maintenance and resource authority — fixed host-neutral 2026-10-15:**
  `ResourceAuthorityStore.ScanCasReferences` previously enumerated only top-level
  authority, revision and view journals. Moving a valid authority/revision pair
  into a nested directory under `resource-authority` made its payload disappear
  from the root set without making reachability incomplete. Control: one referenced blob, no
  orphans. Misplaced journals: `ReachabilityComplete=true`, zero references, one
  orphan; `Collect` deleted that blob. One safe recursive inventory now rejects
  misplaced journals and unreadable/linked paths before authority replay, blocking
  all collection. Published and unpublished source/view roots, file/directory
  links, unchanged journals and collection after explicit path restoration are
  covered. Journals are never repaired or relocated by GC.
- **P1 / Core evidence reducer — fixed host-neutral 2026-10-15:**
  `EvidenceStateReducer.Reduce` previously dereferenced the absent revision of a
  `Removed` head for both resources and current-state dependencies. The reducer
  now compares revisions only for `Known` heads; removal becomes `Unavailable`.
  Frozen reduction, working-set restoration and compilation exclude removed
  current bodies without rewriting observations. Immutable-snapshot dependencies
  remain historical; existing Library removal/restart checks retain exact bytes.
- **P2 / Core context working set and result projection — fixed host-neutral 2026-10-15:**
  Archived operation receipts previously projected a CAS header before hydration,
  losing `ResultPayload` and diagnostic fields. Working-set selection now passes
  original facts and priority to the compiler's existing hydration/projection
  path; only then are bounded receipts materialized and budgeted. Tests retain
  `code`, `field` and exact inspector provenance through a checkpoint/save/restart,
  with identical inline/CAS output for user/developer/tool roles and Error/Unknown
  outcomes in Core and Office projection. Missing selected payloads fail explicitly;
  unselected optional receipts perform no hydration. Unknown effects and unresolved
  recovery remain mandatory. Durable headers and bodies are unchanged.

The separate [authored UTF-16 reopening failure](#authored-utf-16-file-blocks-chat-reopening--2026-10-14)
is fixed host-neutral through separate raw-byte ownership and lazy decoded views.

- **P2 / Office prompt index and artifact descriptors — fixed host-neutral 2026-10-15:**
  The incomplete prompt descriptor advertised authored files as `attachment: ...`,
  while discovery/resolution used `file: ...`. The index now consumes the provider's
  shared metadata descriptor; its separate type/representation classifiers are
  removed. Authored-file targets resolve before and after UTF-16 save/restart.

The **Office retained-reader follow-up is complete host-neutral**. Whole and line
reads consume the complete text already verified by `ResourceSnapshotReadService`
within the same operation. The former page loop could reread a 2,000,000-character
CAS body 63 times; retained whole/line delivery now uses one materialization.
Generic provider pagination remains bounded. Line evidence contains only the
selected excerpt, and missing bytes fail on the next operation; there is no
persistent body cache. No wall-clock performance claim is made.

The **Core context-selection follow-up is complete host-neutral**. `ContextWorkingSet`
now supplies original archive candidates only. Active and archived bodies share
compiler currentness filtering, exact coverage deduplication, lazy hydration and
projected token budgeting. Separate working-set filtering/budget estimation and
application-side body injection are removed. A current source that fits after
projection is no longer discarded by estimating CAS bytes as characters. Optional
input work remains bounded; unresolved operation facts remain mandatory. CAS,
authority/publication barriers and append-only events stay authoritative.

Implementation validation — 2026-10-15: Harness and CLI builds pass; 25 focused
Harness checks and both controlled CLI compaction/resume cases pass. Coverage
includes prompt-target restart, whole/line materialization and excerpt evidence,
missing-body isolation, active/archive deduplication before CAS reads, projected
body budgeting, current mutation sources, removed evidence and required outcomes.
No full suite, real-model or Windows/Office/VSTO qualification was run.

Original review validation: existing `storage: CAS` 7/7 and `context continuity:` 10/10 passed without
rebuilding the project. A small separate probe reproduces the three gaps above;
its code, output and source/assembly fingerprints are retained at
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-cas-review-20261014-y33vnjl6/`.
Only that probe was compiled. No full suite, model HTTP, Office/VSTO or Windows
qualification was performed.

Re-review validation — 2026-10-15: the existing assembly at `cc48a6dc` passed
`storage: CAS` 9/9, `context continuity:` 11/11, frozen evidence 1/1, authored-file
viewer/revision history 1/1 and document removal/restart 1/1 (23 checks). All three
original defects and the UTF-16 failure can leave the active defect list. Concurrent
working-tree hardening of GC filenames and archived call/byte admission was read
but is not covered by these no-build results. A separate small probe against a
copied assembly reproduces the prompt/discovery mismatch through the real resolver;
source, output and source/assembly hashes are retained at
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-cas-rereview-20261015-fsqivjdf/`.
Only the probe was compiled; no full suite or Office qualification was run.

## VBA patching and completion photos — 2026-10-14

**Follow-up photos — 2026-10-15:** SceneParser reports
`vba_patch_stale_source` with no write; the user confirms an automatic stop. A later
Renderer turn loads `common.vba_code_editing`, applies one patch, rejects another
and still claims all four modules were fixed. The accepted calls/current source
and terminal run reason are needed to attribute the mismatch and stop; the photos
do not establish that the patch engine or skill retention failed. Inbox currently
pauses on cumulative execution errors as well as failed lifecycle, so its generic
banner alone cannot identify which occurred.

Another photo shows repeated SceneBuilder patches accompanied by the same
`s.ViewData.FOV = FOV` self-reference diagnosis and attempts to change `FOV` to
`fov`. VBA identifiers are case-insensitive; with a declared parameter `fov`, the
unqualified right side refers to that parameter. The VBA skill now includes this
counterexample, accepts VBE casing/spacing and requires a real remaining defect
before another edit. It also covers reserved names such as `Scale`, scope,
current procedure signatures and assignment/call typing. These are guidance
changes, not proof that the model loop or runtime effect attribution is resolved.
This wording-only follow-up was checked by diff; no rebuild, model run or Office
validation was performed for it.

The later Optional/Scale photos do not reproduce a lost write. The user clarified
that the missing change was the parameter rename `scale` to `scaleFactor`, which
the model had not attempted. The displayed patch adds `Optional`, visible in VBE;
the narrative claims a field rename, and VBE already shows
`s.ViewData.ScaleFactor = scale`. The field and parameter are separate identifiers.
This evidence does not establish absent mutation effects or compilation success;
the broader unsupported completion claims remain the model-guidance concern above.

The separate source-inspection finding is now fixed host-neutral: previously
`VbaToolHandler.Effect` mapped `Ok` plus dispatch to `VerifiedChange` without a
before/read-back comparison. The VBA mutation owner now carries typed `Changed`
evidence from actual existence/live hashes. A fake VBE restoring the original
casing returns `VerifiedNoChange`; three identical results reach the existing
no-progress guard. A real identifier rename still reports a change. Exact guards,
VBE-comparable verification and unknown-effect handling remain separate.

`RunChangesService` now prefers the exact source already captured by `CaptureModules`,
through the same call's retained authority commit and revision. CAS integrity and
the terminal actual hash must agree. Restart/later live edits cannot replace that
historical body; absent or corrupt normalized read-back stays an explicit intended
preview. Owners: Office VBA mutation outcomes/handler and run-change presentation.
Validation: 12 focused Harness checks passed (VBE, retained diffs, live hashing,
write/delete drift, rename, restore lineage and native bindings). This reproduces
the classification defect with a fake VBE, not the user's exact model loop.
Real-model and Windows/Office compilation, editing and WebView2 display remain open.

Owners: Core prompts, Office VBA skill and Web presentation. Prompt schema 49
aligns preparation, skill policy, loader and catalog guidance around reuse of
included selections; failed intended edits remain pending until evidence resolves
them. VBA recovery explicitly rebuilds the rejected patch from current source
without reloading the skill. The stale-patch caption now reports a mismatch instead
of asserting that source changed. Autonomous model compliance and the original
automatic stop remain open; no Office qualification is claimed.

Validation: seven focused Harness checks pass (prompt structure/migration,
built-in skill loading, VBA schema discovery and stale-patch recovery), plus all
27 `completion-guard.test.js` checks. JavaScript syntax and diff checks pass.
No real-model run or Windows/Office/VSTO validation was performed.

**Reproduced discovery/presentation defects fixed; model and Office attribution
open.** Photos 1/2/4 show `common.skills_load` alternating with an unchanged
capability read refused as `skill_required`/`repeated_tool_failure`. The capability
reader rejected every skill-only tool regardless of selection; the no-progress
guard correctly retained that refusal. The reader now returns successful bounded
selection guidance without direct admission or a duplicate schema body. The
production conversation regression discovers/reads UniLoader, loads the VBA skill,
reads the same capability again and applies one exact patch. Existing selection,
revocation and confirmation dispatch guards pass.

Photo 3's request for code already in the project is model behavior not established
as a lost resource. Bound runtime context now describes the existing project/source
read route before skill selection; this delivery is tested, autonomous Qwen use is
not. Photo 5's `vba_code_invalid` and `vba_module_create_failed` cannot identify a
validator/backend defect without the accepted arguments and terminal evidence.
Their protections remain intact. A structurally valid Office `done` can still make
unsupported narrative claims; the UI now keeps failed attempts visible and does
not certify task completion. Exact-source preview labels no longer contradict
separate VBE-equivalent write verification.

The visible `3d_scene_authoring` enum error names an unpublished capability; no alias
or fabricated skill is added. A separate reproduced transport defect ignored
top-level SSE provider errors after answer deltas and could accept the accumulated
completion. JSON/SSE errors now produce typed provider failure without format
repair. The photo alone does not prove whether its error was an endpoint envelope
or model-quoted text; quoted content is deliberately not classified by prose.

The deeper audit found three additional boundary defects and fixed them:

- A whole-module conflict with small complete `currentContent` was classified as
  `Replan`. This diagnostic body had no canonical read evidence, while a later
  actual source read could not satisfy `Replan`. The same write therefore remained
  blocked after performing the required recovery. Whole-write conflicts for an
  existing module now use `RefreshRequired`/`whole` regardless of diagnostic size. The previous domain test
  retried manually and bypassed `AgentProgressTracker`; the new production
  conversation case checks external drift, refusal, read and identical retry.
- `AwaitingUser` suppressed terminal model materialization like a pending
  confirmation. A recorded question set left an unclosed call in model history.
  Its terminal result is now recorded before waiting; both native-tool and
  user-role transports continue after reload without repeating the question.
- Activity data above 8,192 characters moves to CAS during save; bridge metadata
  omits that payload handle. Question rendering read only `DataJson` and silently
  returned, leaving a waiting status without cards. Typed result presentation now
  hydrates the exact question set; missing content produces a visible reload path.

The completed transcript had deliberately rendered comments outside the action
disclosure. The user requires a step to fold as one unit. The UI now folds both,
and expansion shows original step/comment/call association; final answers and
attention states remain separate.

**Systemic conclusion:** the confirmed defects are mismatched consumer contracts,
not evidence of one lost VBA snapshot store. Resource existence, known revision,
body delivery in a particular request, callable admission, mutation effect and
user interaction are separate facts. Diagnostic source text cannot stand in for
a delivered observation; a successful skill load cannot satisfy an unrelated
cached failure; a saved question payload cannot be assumed to be inline in UI.
Existing `ContextReceipt`, `ToolRecoveryContract`, `UserQuestionSet`, runtime
step/call identities and append-only events already own these facts. Another
mutable memory/snapshot manager would introduce a competing authority.

Owners should enforce these cross-boundary invariants in existing components:
recovery metadata must name a condition that actually releases the rejected call;
every completed call must retain its terminal result across waits/reload; a pending
interaction must render its exact body or an explicit delivery failure; a progress
comment and its actions must share one presentation lifecycle. Keep checks at the
real conversation/compiler/store/UI seams, not only direct tool invocation.

Source retention after writes/compaction and bounded VBA reads pass existing
focused checks. Known authority is frozen per model request; an external Office
edit is detected by a later read or mutation guard, not inferred from prompt text.
Budget omission is explicit and does not delete the resource. Photo 3 still needs
the actual `ContextReceipt` and serialized request to distinguish no discovery,
omitted source, unresolved target and genuine host failure. Current default wire
guidance already prohibits repeated/paraphrased progress. Prompt edits alone cannot
resolve these contradictory recovery transitions. `needs_input` validates nonblank
text, not whether it semantically contains a useful question; it cannot create
cards without `UserQuestionSet`. Actual saved prompt overrides and Qwen behavior
require the captured request, not an assumption from repository defaults.

Owners: Office discovery/VBA, Core protocol and model evaluation, Web presentation.
Next evidence: original model request/response and tool arguments/results for the
affected chat, then the exact build on Windows x64 + Office x64. Host-neutral tests
and Chromium layout checks do not qualify COM/WebView2 or arbitrary model behavior.

## Authored UTF-16 file blocks chat reopening — 2026-10-14

**Fixed host-neutral — 2026-10-15 / Core ChatStore hydration.** A copied UTF-16
LE BOM HTML file previously blocked `ChatStore.Load`: MIME-driven eager hydration
used `ChatBlobStore.ReadText` on raw UTF-16 bytes. Authored files now stay lazy;
explicit reads use the existing encoding-aware `DocumentArtifactStore` owner.
Ordinary Save cannot re-encode owned bytes. The extended authored-HTML check covers
reopen, explicit read, repeated save, byte-identical BOM/hash/export and missing-body
isolation. No user bytes or historical events were rewritten. Windows viewer/delivery
qualification remains separate.

## Cross-host chat creation from Excel — 2026-10-14

**Contained UI / open Windows evidence.** Reported symptom: clicking PowerPoint
from Excel has no visible effect. A host-neutral regression reproduces an enabled
button silently dropping clicks during navigation; queued creation and inline
status/error fix that path. Cold activation now retains the returned application
and adapter STA instead of shell-launch followed by ROT rediscovery. The latter
remains source-reviewed only; verify installed native/VSTO/Desktop builds on
Windows with PowerPoint closed/open, multiple documents, modal dialogs, Save As,
exact tools/skills after switching and return to the original Excel chat. R52's
multi-instance attach evidence remains open. Owner: Web navigation / OfficeHosts.

## Working source links and completion evidence — 2026-10-14

**Residual / Resource Gateway, skill selection, run projection.** Current HTML
targets and per-file heads remove the reproduced snapshot-link churn and sibling
false staleness. Exact history remains bounded to 1,000 snapshot metadata entries;
missing/corrupt history still fails closed. Old HTML publications without per-file
heads use conservative aggregate staleness until a verified file publication.
Large-workspace publication and resolver latency need measured fixtures; host-neutral
tests do not establish WebView2 editor/preview behavior on Windows.

`GoalVerification=not_assessed` makes model `done` visibly distinct from verified
tool effects. It does not independently judge semantic completion. Gemma 4 selected
both required authoring skills and passed 4/4 exact source comparisons in the new
real-model fixture, but again omitted Task List without explicit instruction and
made two refused VBA capability attempts before selecting its skill.
Runtime requires selected skills for VBA edits and HTML operations; automatic Task
List selection for multi-deliverable work remains a model adherence gap. A selected
skill's own revision is stable under neighboring catalog changes, but catalog
source observations still follow whole-publication revisions: a neighboring change
can force a fresh semantic read even when the source bytes are unchanged.

## CLI response contract and live v7 completion — 2026-10-13

**Instruction gap fixed; autonomous completion remains open / Core model protocol,
workspace conversation and CLI evaluation.** CLI previously supplied an abbreviated
v7 response instruction while Office defaults contained the complete raw envelope.
Cloud `json_object` exhausted ten format attempts without a tool. Reusing the
existing Core contract removes that divergence; a focused HTTP regression and
real counter repair pass. Some real format errors still require bounded repair.

Cloud CSV with thinking repairs the application and passes 24/24 browser checks,
then stops without the three explicitly required final reads. The 24K notes task
preserves all final bytes and note order but omits receipt reading; it never reaches
compaction/restart. Canonical HTTP audits retain the latest tool results and exact
task, so these traces do not demonstrate missing-result delivery. Do not infer a
universal model weakness or complete context correctness from this comparison.
The local Gemma preflight separately fails memory admission. Profiles, unchanged
budgets, failed outcomes and closure priorities are in
[PROGRESS](PROGRESS.md#real-v7-model-regression-and-shared-response-contract--2026-10-13).

The 2026-10-14 prompt/context optimization smoke reproduces the CSV gap: both runs
pass 24/24 browser checks, but final reads vary from 3/3 (accepted) to 0/3 (failed).
The initial HTTP request is identical and CLI's response contract is unchanged;
the pair cannot attribute this variation to prompt simplification. Automatic Task
List selection also remains absent in the passing fake-Office workflow. The
[paired evidence](PROGRESS.md#prompt-simplification-and-incremental-context-cost--2026-10-14)
does not close model completion or real Office qualification.

The bounded 2026-10-14 placement change moves read requirements after current
results without expanding the prompt or adding a completion gate. A small replay
comparison favors that order and one fresh CSV run passes 3/3 final reads plus
24/24 browser checks; it still requires five JSON format repairs. The
[placement evidence](PROGRESS.md#final-read-requirements-follow-current-results--2026-10-14)
is preliminary; autonomous completion and format reliability remain open.

The [JSON terminator investigation](PROGRESS.md#gemma-cloud-json-terminator-failures-diagnosed--2026-10-14)
reproduces Gemma's native `<tool_call|>` suffix in plain response content. Short
prompt clarification, contract placement, instruction-role changes and thinking
off all retain failures; the strict parser and current defaults remain unchanged.
Thinking off reduces format repairs in one full CSV run but fails final acceptance:
after a failed browser check the model edits, reads and returns `done` without
verifying the new snapshot. Read acceptance alone is insufficient. The
[current browser-acceptance projection](PROGRESS.md#current-browser-acceptance-is-visible-to-the-model--2026-10-14)
now exposes existing verification currency evidence without another store or loop.
Deterministic state/repair/invalidation checks and one normal-profile full CSV run
pass; the paired thinking-off replay still terminates early in 3/3 attempts on
both sides. Visibility is verified; improved model completion is not established.

## Skill and resource workflow regressions — 2026-10-13

**Fixed reproduced causes; incident qualification remains open / Core skill/runtime,
Office controller, Library UI and HTML publication owners.** Manual confirmation
lost `SelectedSkills` while replacing the run record. The new invocation id also
failed `SkillTaskService.CheckDispatch` against the originating context receipt.
An actual-controller regression failed first on missing selections, then on
`The exact originating request bindings are unavailable` and absent HTML source.
It now passes four confirmation/restart boundaries, structured compaction while
approval is pending, exact patch/full replacement/no-op effects and historical editor
downloads. Only the exact durable pending call can refer to its originating
request; altered turn/step/call/arguments or revoked binding still fails closed.

Separately reproduced: UI supporting-file reads assumed a `references/` prefix,
and identical full HTML/CSS/JS writes changed timestamps and manufactured new
revisions/verified-change results. These are fixed. The dynamic authoring descriptor
also now preserves its registered skill-only exposure. Source checks do not qualify
browser behavior; full workspace JSON copying was removed from current-state
validation, but end-to-end large-document performance remains unmeasured.

The actual controller additionally reproduced an unreadable `common.tools_upsert`
source when Agent JavaScript was enabled: the configured descriptor changed, but
the immutable built-in publication still contained the disabled schema. Startup
and settings-save publication now use the same configured definitions as runtime
and Library. Enable/disable source reads and enabled documentation reads pass;
historical snapshots are retained, with no authoring-file fallback on reads.

Actual-controller creation/read-back of custom skill and VBA tool Library sources
and all published source downloads passes. This does not explain every reported
Library read failure; the exact error/event evidence and affected application are
still needed for attribution. Four real Gemma cloud runs independently pass exact
HTML/CSS/JS/VBA final-source comparison. Without explicit task-list instructions,
the model still omits `common.task_tracking` despite default guidance; the explicit
fixture creates/closes the list and retains both selected skills. Do not claim
automatic selection fixed, infer deletion from stale historical targets, or replace
missing model/Office evidence with a new runtime heuristic. Reproduction, profile
and counts are in [PROGRESS](PROGRESS.md#skill-and-resource-workflow-regressions--2026-10-13).

Follow-up audit of the original symptoms keeps these gaps explicit:

- **Completion / Core conversation owner:** `AgentKernel` accepts structurally valid
  `done` without a task-specific content acceptance contract. Zero requested writes,
  a wrong edit or a failed call followed by optimistic narration can still end the
  model turn. Errors/unknown effects remain in runtime health; terminal lifecycle
  and Task List completion do not prove that the requested result exists. See also
  [the false-completion evidence](#workspace-cli-false-completion-with-local-qwen--2026-10-04).
- **Selection / skill and prompt owners:** task tracking remains guidance rather
  than a guaranteed task transition. Restart/compaction retention passes, but
  publication revocation or changed required bindings intentionally suspends a skill
  until explicit reload; an independent new user turn starts with empty selections.
- **Resource recovery / Gateway and model owners:** exact current/historical source,
  competing edits, corrupt/late UI responses and stale VBA refusal pass targeted
  checks. They do not prove that a model will recover correctly from every refusal,
  nor explain the unexported incident's missing edits in the latest artifact.
- **Capacity / context, Library and HTML owners:** the 32K fixture was refused before
  model dispatch; instructions are not silently dropped. Large source/context
  pressure needs a suitable profile. Real edits still retain immutable revisions;
  snapshot-history and Library metadata enumeration scale with retained history.
  Removing empty file revisions and one full JSON copy is not an end-to-end latency fix.
- **Qualification / Office and evaluation owners:** four synthetic Gemma runs cover
  exact text edits, not arbitrary redesigns, rendered interaction, another target
  model or real COM/WebView2. The full Harness also retains the separately recorded
  baseline failures; this review ran only the relevant slices.

The repeated controller check asserts clean execution health, five successful
writes (Task List plus four HTML operations) and exactly one verified no-change,
so a failed no-op cannot pass merely because the source stayed identical.

## HTML historical member lookup — 2026-10-13

**Fixed host-neutral / Resource Gateway and artifact provider owners.** The photo
shows a workspace structure read followed by `resource_target_not_found` for its
CSS member. Member resolution enumerated only the active workspace, while member
labels omitted the parent snapshot. A focused native-tool regression additionally
reproduced a historical CSS read silently returning replacement bytes.

Member targets now qualify the exact parent; bounded document snapshot metadata
resolves historical roots and children without switching the active workspace or
rewriting retained bytes. Missing source and ambiguous/incomplete metadata fail
closed. See [Resource Fabric](../resource-fabric.md). The photo's later
`handler_failed` and repeated cancellations need incident events for attribution;
they are not evidence of deleted CSS or a qualified Windows/Office fix.

## Premature terminal narration — 2026-10-13

**Prompt contradictions corrected; incident attribution and real-model behavior
remain open / Core ModelProtocol and prompt owners.** The supplied Excel photo
shows an HTML/CSS redesign request ending with “Сейчас подготовлю современный
дизайн” and intended CSS changes, without the requested CSS deliverable. It does
not expose the accepted action, final run reason or any format-repair attempts.
A premature `done` would explain the stop, but the photo alone cannot prove it.

The audited path preserves explicit action through parser, Office/workspace
adapters, kernel and terminal history. A tool result drives another model step;
`done`, `blocked` and `needs_input` end the turn with distinct reasons. Approval,
questions, cancellation, limits, provider/format failures and unknown effects
retain their existing typed controls. No-call prose cannot schedule more work.

Concrete instruction defects: the completion review forced `blocked`/`needs_input`
whenever anything remained; initial narration was not explicitly paired with a
call; missing-call repair could be satisfied by changing only action to `done`.
Schema 42 requires continued feasible work, delivery of a tool-free answer/code
in the current message, and actual obstacles or indispensable answers for the
unfinished terminal states. Repair preserves the task rather than relabeling a
promise. The shared rule reaches schema, defaults, repair and workspace prompts.
It cannot reject a semantically premature but structurally valid `done`.

No-tool 3/3 (including both formats/result roles), kernel 55/55, model protocol
15/15 and prompt migration 3/3 pass. The separate built-in guidance check failed
on `smallest complete set of clearly applicable skills`: the pre-existing
working-tree skill policy now teaches `common.skills_load`, while that assertion
still expects the former wording. Owner: skill cutover / Harness fixtures; reconcile
that assertion separately without suppressing the completion checks preceding it.
The isolated commit candidate excludes those skill changes and passes no-tool
3/3, prompt migration 3/3 and built-in guidance 1/1 with prompt schema 42.
On 2026-10-14 the skill-policy assertion was reconciled with the current
`common.skills_load` instruction; the complete Agent regression group passes
53/53. This does not close the separate real-model premature-completion risk.
Closure of this incident needs the accepted response, `RunSummary.Reason`, repair
attempts if any, and a controlled reproduction on the reported DeepSeek model.
Real Windows/Office/WebView2 behavior is unqualified.

## Claimed HTML edit absent in current artifact — 2026-10-13

**Open incident / Conversation and HTML workspace owners.** The photo shows an
assistant claiming creation of custom CSS and insertion of a stylesheet link in
`index.html`; the visible change card lists one created text artifact. The reporter
checked both the chat resource card and the latest version in the Artifacts tab
and found the changes absent. An old chat card alone therefore does not explain
the report. Exact build, accepted calls, results and publication events are missing;
the photo does not establish whether an HTML mutation was attempted or published.

The sequential-snapshot/cursor hypothesis was checked host-neutrally through the
production native runtime: publish HTML/CSS, patch CSS, patch HTML, save/reload, then
compare both exact bodies, parent chain and selected/published head. The earlier CSS
edit survives the later HTML snapshot. A historical read cursor retains its old
source; using it with the new exact member is rejected. This is covered by
`shared HTML: sequential snapshots and stale source cursors`, not evidence that the
reported Windows run behaved identically. The existing shared HTML and native HTML
tool checks also pass. Source/write UI behavior checks pass before their unrelated
stale asset-key assertions; exact-action race checks pass. No Office/VSTO validation.

Next evidence: export this chat's accepted model responses, tool results and
trajectory/publication records with the build version. Correlate the claimed
`index.html`/CSS operations, exact workspace head and delivered UI revision. Keep
model narrative, saved file bytes and rendered appearance separate; do not close
the incident based on prompt changes or a scripted successful save.

## HTML run-change attribution can disappear during snapshot reload — 2026-10-13

**Corrected host-neutrally / HTML publication and RunChangesService owners.** New
HTML snapshots previously reached publication without `RunId`/`SourceMessageId`;
later chat linking only filled the mutable projection. The next preparation
reloaded the incomplete retained metadata. The old linker recognized only
`rnassistant.htmlWorkspaceMutation`, omitting patches and archived activity bodies.

A focused diagnostic with two successful native patches (CSS then HTML), save and
reload preserved both actual file edits but produced zero run-change entries where
two were expected. This explains a possible missing change card, not the reported
absence of edits in the latest artifact; that incident remains open.

`HtmlWorkspacePublication.ReadBack` now retains the exact execution run and matching
activity before publication. Matching uses run/step/call/tool identity. Relinking
consumes retained attribution and no longer infers it from result JSON. Existing
snapshots and no-ops keep their original attribution; old missing metadata is not
backfilled. The production conversation regression covers CSS → HTML patches,
full-run and per-call diffs, archived activity bodies, restart, exact before/after
source and a later no-op that must not claim the earlier revisions. Canonical scope
is in [Artifact Library](../artifact-library.md#implemented-shared-html-publication--2026-09-08).
Validation: `shared HTML:` (6), `html tools:` (6), `run changes:` (3), and both
canonical HTML resource-reference/archival storage checks passed; the strengthened
conversation regression explicitly verifies the later no-op's effect evidence.
Windows/Office/WebView2 delivery remains unqualified; no Office/VSTO checks.

## Repeated narration during capability preparation — 2026-10-13

**Host-neutral correction implemented; target-model quality and incident attribution
remain open / model prompt + Core context owners.** Photos from
`v16.1.0-dev / 921c504c` show repeated near-identical narration during capability
loading. The later photo distinguishes three targets: the
`common.html_workspace_authoring` skill, then the `common.html_workspace_read`
and `common.html_workspace_write_file` tool schemas. These are different
`common.capabilities_read` requests; their completed labels prove loading, not
execution of the named HTML tools. The reporter confirms that the repetition
survives completion and reopening the chat.

Code inspection supports separate preparation steps: a skill loads instructions
without admitting named tools, and exact schemas become callable at the next
model-step boundary. `ConversationKernelAdapter` replaces `_stepMessage` from
each accepted response; the UI groups by StepId and displays that step's message.
It does not automatically copy an earlier message when admitting a schema. This
supersedes the initial hypothesis of repeatedly reading one skill. The unchanged
three-identical-results guard does not combine different capability targets, and
removing no-call `continue` does not establish a fix for repeated narration.

Prompt review found concrete pressure toward redundant narration: the old default
and schema requested one or two sentences for every tool response. Prompt schema
41 now shares one `ModelProtocolWire.MessageGuidance` across defaults, schema and
repair: a brief initial approach, then only useful changes in findings, decisions,
uncertainty, blockers or stage. Routine preparation and Task List note updates can
use `message=""`; known independent missing schema reads can be batched. Comment
frequency is independent of calls and note updates. Existing saved-prompt migration
retains its recovery copy. No timer, text classifier or extra model call was added.

The context review also reproduced a retention gap: completed-operation and
repeated-read folding discarded the first accepted call's narration. The compiler
now retains nonempty narration separately with its original message identity and
assistant provenance, within the same context budget. Externalized call headers
retain the exact accepted text without hydrating large arguments; full-body
hydration verifies agreement. Missing narration metadata is explicitly incompatible,
not inferred from old placeholders. Canonical bodies and history remain unchanged.
Existing Task List notes carry current decisions, their basis and remaining
dependencies; compaction preserves uncertainty, user corrections and unresolved
conflicts. Assistant assessments cannot become observations or user decisions.
There is no second durable memory store. See the
[canonical context contract](../conversation-protocol.md#conversation-context).

Host-neutral evidence covers folded mutations/reads, latest-call preservation,
archived payloads and clone integrity, repeated compaction and restart across all
three result roles, rejected source-kind promotion, prompt/schema/repair alignment,
and UI rendering of one comment with three distinct calls. Scripted compaction
checks transport and provenance, not a real model's summary quality. Earlier UI
checks also passed against exact old Git sources. No incident event stream was
supplied: accepted messages and step/call correlations remain needed to attribute
every displayed copy. Repeated-comment rate and continuity still need a controlled
run on the reported DeepSeek model. Windows/Office/WebView2 qualification and the
photographed incident remain open; no Office/VSTO validation ran.

## Resources, links and artifacts review — 2026-10-12

Five P2 findings reproduced against `921c504c`, fixed host-neutral on 2026-10-13.
Original reproductions and corrections by owner:

- **Prompt working set / `ChatResourcePromptIndex`:** only HTML revisions are
  collapsed before constructing semantic targets. Fourteen normal saves of one
  Markdown resource produce twelve advertised targets, eleven of which fail with
  `resource_target_not_found`; discovery resolves only the current document head.
  Historical snapshots consume the manifest limit and displace other resources.
  Fixed: collapse observed revisions by working-set identity before prompt limits,
  preserving independent same-title resources, detach and exact historical access.
- **Artifact transfer / `DocumentArtifactStore.ImportOriginal`:** PDF copy/import
  stores the original bytes but changes its attachment kind to `file` and retains
  no extraction or page metadata. The same PDF through ordinary attachment
  ingestion advertises `metadata,text,media`; its independent transfer copy exposes
  only `metadata`, and an explicit text read fails. Fixed: shared attachment
  inspection retains text/media/page metadata and bounded extraction warnings.
  Copy/restart coverage also caught MIME-driven UTF-8 hydration of raw DOCX/encoded
  text; originals now remain metadata-only until their resource view is read.
- **Semantic search / `ChatArtifactResourceProvider.Search`:** metadata matches
  concatenate artifact IDs and raw `MetadataJson` into snippets. A normal title
  search exposes `artifact_md_*` and `documentId`; the model result projector
  preserves that snippet. Existing `artifacts: prompt uses bounded working set`
  failed its runtime-metadata exclusion assertion (expected 0, actual 1). Fixed:
  explicitly project public searchable metadata; arbitrary user content remains data.
- **Resource descriptors / `ChatArtifactResourceProvider.HasTextHint`:** authored
  JSON uses `ChatArtifactKinds.File`, absent from the retained-text classification.
  Discovery advertises `metadata,raw` for an imported JSON whose explicit/default
  text read succeeds with the exact body. This is incorrect discovery guidance,
  not lost JSON bytes. Fixed: advertise authored JSON/text file views from retained
  metadata without classifying opaque originals as text.
- **Artifact transfer / `ArtifactCatalogService.Import` and `SafeName`:** filename
  truncation runs before extension dispatch. A valid HTML project with a
  180-character title exports a 193-character `*.rna-html.zip` filename; copying
  it truncates off the suffix and successfully publishes an `attachment` instead
  of an editable `html_workspace`. Fixed: bound export/import filenames while
  preserving the extension; the 200-character project title round-trips independently.

Fix validation: isolated source archive of `921c504c` plus these resource changes:
`artifact` 38/38, `attachments:` 27/27, `resources:` 29/29, `shared Markdown:` 4/4.
The initial working-tree build encountered concurrent protocol edits and stale
protocol tests; those edits were preserved and excluded from this validation.
The focused suites cover copy/restart, exact bytes/extraction, media descriptors,
opaque/empty originals, extraction bounds, public search snippets, long filenames,
14 Markdown/Plan revisions, same-title identities and detach. This evidence does
not qualify the combined parallel changes or real Windows/Office/WebView2 behavior.

Original review validation: one host-neutral Harness build, then focused `--no-build` slices:
`resources:` 29/29, `resource cutover:` 36/39, `artifact` 35/36,
`attachments:` 27/27, `context continuity:` 10/10, `storage: CAS` 7/7,
`storage: html` 7/7, and `shared context:` 2/2. Filters overlap; these are execution
counts, not distinct-test coverage. The three cutover failures are the already
recorded [baseline failures](#baseline-checks-isolated-during-continuity-work--2026-10-01):
old copied-workspace restore and the two unexpected mutable-settings load counts.
They do not establish replacement of retained prompt bodies by current settings.

Eight selected web suites pass, including actual Chromium image decoding/layout
with installed Chrome. `artifact-library-projection` still fails its obsolete
`app-chat-session.js` cache-key assertion. A temporary isolated probe using the
built production owners reproduces the five cases above without model HTTP or
user data. No new durable-byte loss was observed in these scenarios; no full-suite,
real-model or Windows/Office/WebView2 qualification is claimed.

## Chat transcript review — 2026-10-12

- **Fixed host-neutral send visibility — 2026-10-13 / web inbox:** the actual Send
  button reproduced an offscreen echo after manual scroll; matching inbox revisions
  also incorrectly removed receipts before their exact history message arrived.
  Explicit submission reveals its bubble, later updates preserve manual scrolling,
  and an empty queue no longer discards an unresolved echo. Chromium covers both
  Steer and Queue through real send/state/render handlers with controlled replies.
- **Fixed host-neutral removal receipts — 2026-10-15 / inbox projection owner:** previously snapshots omitted
  both applied and removed inputs. Removal from another window cannot be distinguished
  from history lag by absence alone; a retained echo needs an explicit terminal
  receipt to resolve that case. Reproduced again on 2026-10-15. A local variant also
  reproduces: a push exposes a pending input, its removal succeeds, then the older
  submit acknowledgement arrives. `submitInboxMessage` reinserts `acceptedInput`
  before `applyChatInbox` rejects the stale snapshot. The bubble returns even though
  the runtime queue is empty; polling then repeatedly requests history for an input
  that will never appear. Bounded terminal receipts and the shared revision reducer
  now resolve both cases; see the confirmation/input review above.
- **Open older-page refresh / web history + chat projection owners:** code inspection
  shows full `getChatState`/push snapshots still carry the latest 80 messages. If a
  reader has loaded earlier pages, such a snapshot can remove the visible anchor
  even though streaming and prepend now preserve scrolling within the loaded
  window. A separate change must refresh the requested revision-bound history
  window; retaining an unverified prefix across edits/deletions is not sufficient.
  Verify a growing run with more than 240 messages, reading an older page, concurrent
  history edits and explicit return to latest. This is not established as the cause
  of the reported incident.
- **Open Windows evidence / typed inbox receipt + WebView presentation:** local
  browser tests cover immediate pending bubbles, exact-ID acknowledgement handoff,
  steer segment order, editor focus and free/follow scrolling. Controller receipt
  delivery and real Office/WebView2 behavior still need Windows qualification.

## HTML resource links and short-source report — 2026-10-12

- **Fixed host-neutral / ChatResourceReferenceService:** message re-linking removed
  every non-user HTML reference before reconstructing only inline
  `rnassistant.htmlWorkspaceMutation` results. Member reads, patch results and
  archived activity bodies therefore lost their exact links. Re-linking now
  preserves runtime refs and only adds missing mutation links. The new regression
  fails before the fix and passes after it, including repeated linking and replay.
- **Open incident correlation / HTML resources:** the reported source reduced to
  nine characters has no exact remaining text or trajectory. A 32,009-character
  regression checks the final nine-character page, partial evidence, full source
  after patch/save/reload and the original historical snapshot. These all remain
  intact; this does not establish the cause of the user's short-source incident.
  Windows/Office/WebView2 qualification was not run.
- **Open test fixture drift / web tests owner:** `html-workspace-source` passes
  its eight behavioral checks, then fails its old `app-chat-session.js` cache-key
  assertion. HEAD already delivers `closed-document-20261012-1`; the fixture still
  expects `office-chat-20260930-4`. The complete suite is not green. Focused HTML
  storage (7), replacement guard (1) and chat resource cards (3) pass.

## Attachment input review — 2026-10-12

- **Fixed host-neutral 2026-10-13 / resource upload owner:** a new photo reports
  `RESOURCE_UPLOAD_FAILED: 503` for `main.part_001.js`; the reporter estimates
  3 MB. Inspection of the pinned WebView2 SDK `1.0.2903.40` confirms that
  `COMStreamWrapper.Read` throws `NotImplementedException` for nonzero buffer
  offsets. Uploads passed their accumulated offset directly, failing at the
  second 256 KiB chunk or an earlier partial read. Reads now use a bounded 64 KiB
  scratch buffer at offset zero, then copy into the reserved body. Unexpected
  WebView response failures retain exception diagnostics and return typed JSON
  instead of an empty 503. A regression fails before the fix; all five upload
  checks pass after it, including exact bytes for 3 MiB + 17 with full/partial
  reads, invalid lengths, cancellation and lease cleanup. JS transport is 9/9.
  Harness evidence uses isolated baseline `4a2814d3` plus this fix because the
  working tree's unrelated arsenal edits duplicate `AgentArsenalProjection` in
  Compile items and lack `GetAgentArsenal` in the bridge test stub. Current-tree
  integration and real Windows/Office/WebView2 delivery remain unqualified.
- **Open Windows evidence / resource upload owner:** the shared browser writer
  now posts bounded `ArrayBuffer` chunks instead of Blob/File-backed bodies, which
  avoids relying on WebView2's reported missing request-content path. Chromium
  with a controlled bridge passes CSS picker/drop/paste, visible failures and
  chat-switch isolation; transport checks cover exact bytes and cancellation.
  This does not reproduce or qualify the user's Windows/Office/WebView2 failure.
- **Open test fixture drift / web tests owner:** `artifact-commit-projection`
  fails on its old messages cache key, and `tool-package-actions` on old
  tool/chat cache keys after their behavioral assertions pass. These expectations
  already disagree with HEAD. `prompt-review` also lacks the accent dependency
  used by the current settings form, so it stops before upload coverage. The
  attachment fix updates shared-upload body mocks, without changing those
  unrelated assertions or the settings fixture; these three suites are not green.
- **Open browser-demo transport / MockDemo owner:** `MockBridgeScript` forwards
  bridge controls, but `MockDemoServer` does not serve the internal resource
  data-plane route. A browser demo upload therefore cannot exercise real chunk
  delivery even when its begin control succeeds. Add an explicit demo-only
  transport to the existing router before treating that demo as upload evidence;
  do not weaken production lease/origin checks or add a second ingestion owner.

## VBA context recovery review — 2026-10-12

- **Fixed host-neutral / VBA recovery:** an omitted-source whole-module write
  correctly refused dispatch, but its recovery appended 8K source characters
  from each end. At 24K context this mandatory error overflowed the message budget
  (20,243 vs 19,400 tokens), preventing the next model call. Large/truncated source
  now yields the semantic target and refresh policy without synthetic source/diff.
  The production-run scripted regression passes refusal → line read → patch with
  unrelated source preserved. Complete small-source recovery remains covered.
- **Open incident correlation / Office runtime:** the reported inability to reach
  or edit a macro has no exact error/trajectory yet. The reproduced budget defect
  is a possible cause, not proof of the user's original failure. The same fixture
  at 16K cannot admit even its mandatory prompts/causal frames (15,159 vs 11,550
  tokens); this is separate context headroom, not lost VBA access. Real model and
  Windows/Office behavior remain unqualified.
- **Open expectation drift / catalog Harness owner:** the R61 inventory differs
  for unchanged `common.html_data_bind`, `common.plan_doc_save`,
  `common.questions_ask`, `common.resources_read`, `common.task_list_set`,
  `excel.find_cells` and `excel.replace_cells`. Only the three descriptors changed
  by this review were updated in the baseline. The separate `tools: prompt
  metadata is model-friendly` test expects an obsolete flat HTML bind schema.
  These checks are not green; review their current contracts separately instead
  of refreshing unrelated expectations as part of the VBA fix.

## Harness follow-up review — 2026-10-05

**Update 2026-10-07:** the three P2 defects below are fixed: unrelated effects do
not release failed calls, Excel snapshot reads receive cancellation, and shared
Core working-set selection skips oversized optional receipts while retaining
unknown effects and unresolved current-run recovery. Core/Office use closed model
envelopes for resources, workspace file mutations and Excel/native VBA. Remaining
family projection work is explicitly tracked in `MIGRATION_MAP`; real Office
cancellation latency and photographed end-to-end reproduction remain open.

Current-code follow-up to the 2026-10-01 continuity audit; its original compiler,
claim-generation and skill-retention findings are not all still-open defects.
Detailed contracts, evidence and public harness comparison are in the
[system review](../conversation-protocol.md#системное-ревью-harness--2026-10-05).

- **P2 / Core recovery:** a `Replan` failure without `ResourceIdentity` is cleared
  by an unrelated verified mutation. A host-neutral probe observes the same
  invalid read rejected before a Task List effect and allowed after it. Retain
  the failure until relevant recovery or corrected arguments; do not reset it
  merely because some state changed. Owner: `AgentProgressTracker`.
- **P2 / Office read cancellation:** snapshot search calls `CaptureSearch` with
  `CancellationToken.None`; its per-cell cancellation checks cannot observe run
  cancellation. Thread the operation token through Gateway/provider reads. This
  proves a cancellation gap, not the duration or cause of the photographed hang.
  Owner: Resource Gateway / Excel search; real Office latency remains open.
- **P2 / context retention:** archived operation receipts use optional residual
  budget and stop selection at the first oversized receipt. With budget 4000,
  a long newest diagnostic prevented an older short receipt from being selected
  (0 vs 1 in the probe). No durable facts were lost. Pin bounded unresolved-effect
  metadata, select remaining receipts within budget and report omissions.
  Owner: `ContextWorkingSet` / context compiler.
- **Projection contract risk:** generic `data` has no closed metadata boundary.
  A synthetic nested hash survives the generic path; this is not evidence that
  the current Excel chart producer emits that field. The concrete nested VBA
  error-string leak is addressed in the photo follow-up below. Prefer explicit
  metadata projection while preserving arbitrary business data, not recursive
  removal by property name. Owner: model projection / domain result producers.

Validation: existing `context continuity:` 8/8 without rebuild; a temporary
host-neutral probe against the existing Harness assembly, no model HTTP and no
Office/VSTO validation. These issues are recorded, not fixed by this review.

## Workspace CLI false completion with local Qwen — 2026-10-04

Owner: shared agent completion contract and CLI acceptance evidence (M4–M5).
A real `rna-qwen35-9b-32k` creation run returned `model_done` and described
`index.html`, `styles.css` and `app.js`, but had zero tool calls and left the
workspace empty. In a controlled repair run, the model read the broken files
then returned `model_done` with zero writes while the defect remained. Its final
message ended mid-sentence. The kernel's `ToolCounts` and the file authority
correctly showed no effect; no file artifact was committed from the model's words.

Explicit feedback produced three verified creates and three reads, and a later
narrow repair produced a verified patch plus read-back. External Chromium checks
passed on the repaired app. The CLI now offers opt-in `--expect-files`,
`--min-reads` and `--min-writes` postconditions. The contract is persisted before
dispatch; assessment counts current complete file-read evidence and verified
changed file effects, not generic tool success. Scripted CLI failure and pass
runs confirmed the exit code and persisted inspection state; earlier live Qwen
runs exercised the shallower postcheck. A scripted confirmed delete after an
external edit preserved the file and failed the minimum-change contract despite
the model's `done`. Tasks without those conditions still
expose unverified `model_done`, and the conditions do not grade file content or
browser behavior. Autonomous completion reliability remains open.

A fresh local-Qwen run after the persisted contract change failed the complete
three-file task twice: first by repeatedly reading missing files, then by batching
mutations despite ten protocol repairs. Narrow single-file turns did create all
three files, but source inspection found a missing form/list and incorrect JS
delete/localStorage behavior; attempted agent repair made no write. The shallow
file/count contract passed those narrow turns but did not certify the app. This
keeps M5 browser verification and agent repair acceptance open.

The development CLI now has isolated `web.verify` and an opt-in
`--require-web-verify` acceptance condition. A successful browser smoke retains
exact file evidence; a later file mutation or observed external edit makes that
verification stale at completion. Scripted verify/repair and stale-check cases
passed, and local Qwen completed one real read/patch/read/verify repair after the
user supplied the precise DOM-ID diagnosis. The initial Qwen request still asked
for input despite accessible files, and its next turn stalled after reads and
failed verification. This does not close autonomous diagnosis, functional browser
assertions or the original multi-file task risk.
An advisory DOM-ID hint was added to a failed browser result and exposed in the
top-level tool message. Two fresh Qwen attempts still repeated either the failed
verification or a patch without accepted source evidence; the no-progress guard
stopped both without writes. The hint improves diagnosis for a human or another
model, but it is not evidence that this local model repairs autonomously.

The independent CSV dashboard grader now checks functional browser behavior
outside the writable workspace. A fresh local-Qwen task again returned `done`
without tool calls; CLI acceptance rejected it. One feedback turn produced three
files and a passing browser smoke, but the grader found broken export and invalid/
empty CSV handling (8/11 assertions passed). A second feedback turn made no
change and falsely claimed all three issues fixed. This reproduces the risk with
an independent functional oracle; model completion still cannot certify the app.

A same-task comparison on the current CLI source found Gemma 4 12B produced three
files and 10/11 browser behaviors, while Qwen 3.5 9B again returned `done` with
no tool calls. Both failed the explicit read-back/verification contract. A Gemma
feature follow-up exposed a separate CLI projection defect: the persisted failed
`web.verify` result was correct, but its current-turn model-request body was null.
The CLI projection now materializes the exact execution result, with a scripted
read/error regression. This removes one cause of blind completion; a fresh Gemma
run still skipped read-back and failed independent invalid-CSV behavior. Agent
completion and two-phase functional acceptance remain open.

## Cloud Gemma v6 protocol mismatch — 2026-10-04

Owner: Ollama OpenAI-compatible endpoint/model profile and ModelProtocol
qualification. `gemma4:cloud` through Ollama 0.35.0 returned Markdown-fenced,
wrong-shaped JSON to a strict `json_schema` request and to the full CLI task in
explicit `json_object` mode. Both fresh CLI runs exhausted ten format attempts
before any tool call or file effect. A simple `json_object` greeting did return
valid v6, so the failure is prompt/task dependent. CLI response mode is now an
explicit saved setting for controlled comparison; it does not relax v6 parsing.
Do not use this tag as a strong reference until the exact full-task protocol
probe succeeds. The local Gemma 12B evidence remains separate.

## Offline managed dependency closure — 2026-10-02

Owner: local packages and host delivery targets. The Windows photos show
`Microsoft.Data.Sqlite` and `SQLitePCLRaw.core` missing at runtime. Host-neutral
inspection found mismatched SQLite strong-name versions and a NativeHost portable
manifest without the four SQLite DLLs. The pinned packages and manifest are now
aligned. A full local assembly-reference audit found no missing non-framework DLL
in the Office or JS worker outputs, but found older worker `System.Memory`,
`System.Buffers` and `Unsafe` versions than the host copied beside it. Worker
references and generated redirects now match Core. Desktop/VSTO targets explicitly
copy the full managed and architecture-matched native closure. Local copy targets
and package integrity passed; exact Windows Desktop, VSTO and NativeHost loading
remain open evidence. The portable `lib/` relocation and x64/x86 VBA DLL search
also require an exact Windows/Office load check with rebuilt macro add-ins;
host-neutral publisher checks establish file placement only.

## Local-data reset and active WebView profile — 2026-10-02

Owner: Core storage paths and Office controller lifecycle. The Windows log shows
`ClearRuntimeData` deleting durable roots before failing to remove the open
`webview/EXCEL` profile; the surviving controller then reported a truncated
resource journal from its stale in-memory projection. The reset now validates
durable roots first, leaves the active WebView profile alone, and invalidates chat,
document-authority and resource caches after a successful explicit clear. Focused
host-neutral checks cover a late invalid root and fresh resource reads after reset.
Exact Windows/WebView2 behavior remains unverified. A filesystem failure during
deletion of a durable root can still leave a partial reset and must be reported as
an error; no automatic replay or silent recovery is permitted.

## HTML patch `replace` mismatch — 2026-10-02

Owner: Core text patch engine and HTML workspace tools. The photo shows a
two-hunk patch for `app.js`; the second `find` is CSS-shaped and returns
`text_patch_not_found`. No write occurred. The user's exact workspace source is
unavailable, so the photo alone does not establish whether that anchor belongs
to another file or differs from current source.

Host-neutral code review found a separate reproducible failure: when `find`
contains normalized newlines and source mixes LF/CRLF, fallback matching could
miss a present span. It now maps newline-equivalent matches back to original
source offsets. A missing exact anchor found in another workspace file reports
that path, while retaining the atomic no-write failure. The focused HTML patch
harness passes; the photographed Windows/model trajectory remains unverified.

## HTML editing memory growth — 2026-10-02

Owner: HTML navigation/source projection and WebView editor.
The user reports progressive slowdown after many HTML revisions in a long chat.
Code inspection and focused checks reproduce eager ancestor-body hydration and
full source replacements accumulating in CodeMirror undo history, including its
hidden editor. Empty source-cache entries also had no count limit.

Navigation now carries at most 20 metadata summaries; restart hydrates only the
active aggregate and restore reads its exact source on demand. CodeMirror clears
history on programmatic source/document changes, retains local typing undo, and
releases its hidden buffer in Preview. Source caching is capped at 200 files and
3 million characters. Preview comparisons avoid another serialized source copy;
an already cleared iframe is not navigated again on every edit render.

Targeted storage/shared HTML checks and a real Chromium/CodeMirror stress case
cover 160 large source replacements, local undo, source cleanup and narrow-panel
layout. This is not a memory profile or qualification of the reported Windows
session. Exact Windows/WebView2 long-chat evidence remains open. Full library
metadata enumeration and authority-journal startup still scale with history;
metadata pagination remains a separate Resource/Library owner task.

## Chat navigation and startup freezes — 2026-10-02

Owner: WebView navigation/bridge, controller startup and ChatStore headers.
The user reports the whole UI freezing during chat creation/selection and Office
application changes. The supplied October 1 log photo shows roughly 300–500 ms
warm header/catalog scans with zero full/incremental header replays, about 4,600
CAS references across 18 chats, and roughly two seconds for init versus 64 ms for
its WebView render. Model `headersWait` also reaches tens of seconds; those network
waits do not establish a blocked UI thread or justify changing model/context limits.

Code inspection confirmed synchronous chat creation/document navigation in bridge
UI callbacks, a full interrupted-run scan in the controller constructor, and a
filesystem metadata probe for every CAS reference on every catalog read. Rapid
selections could race exclusive host rebinding: the newer request failed busy
while the older successful response was discarded. Focus events were truthy
`force` arguments and bypassed the active-send polling guard.

The implementation now serializes navigation and init, drains catalog sync,
coalesces not-yet-dispatched selections and applies each committed binding before
the next request. Composer submission is paused during the transition. Navigation
and Office coordinator dispatch leave the UI callback; startup recovery is lazy,
single-execution and precedes chat reads/writes. Catalog size reporting shares a
fresh prefix-directory metadata snapshot per listing, rechecks misses for concurrent
publication and never reuses that snapshot as read/mutation/GC authority. Slow
navigation now reaches the runtime log as `kind=chatNavigation`; recovery has its
own timing. No event/CAS data or request limits were removed or relaxed.

The October 2 log also shows `EnsureHostSwitchReady` rejecting every Office panel
chat selection while a run is active. The coordinator now permits selection and
creation within the currently bound document while preserving the run's original
chat; it still requires the run to finish before rebinding to another document.
This host path needs an exact Windows/Office check.

Focused Node checks cover switching/creation/init races, failed-next-switch recovery,
focus polling, history paging and run revision ordering. Host-neutral harness checks
cover caller-context isolation, coordinator rebinding, cancellation responsiveness,
typed document dispatch, header cache integrity, CAS deletion/restoration and late
publication, plus interrupted-run recovery. Controller construction/lazy wiring is
source-reviewed: the bridge harness uses a controller stub, so it does not qualify
the production controller or Office adapter.

Remaining evidence: repeat cold start, rapid chat creation/selection and Excel ↔
Word/Outlook activation on the exact Windows/Office/WebView2 build with the same
history. Record recovery, header/catalog, bridge/render and COM/modal delays. Cold
full replay and persistence lock waits remain possible. Desktop `MainForm` also
marshals callbacks back to its UI thread and `AttachTarget` selects the requested
chat synchronously before replacing the pane; a separate Desktop attach slice is
still needed. No measured Windows speedup or complete freeze resolution is claimed.

Panel/model follow-up: model/mode/reasoning persistence and projection also ran in
the bridge UI callback; the closed model picker rebuilt every menu item on composer
updates, and foreground activity events rebuilt the transcript individually.
These paths now use worker dispatch, lazy/cached picker DOM and one render per
animation frame. The duplicate hidden model select and its canvas width measurement
are removed. Hidden settings model tables are not rendered from the chat tab.

Model discovery could hold exclusive host rebinding until its 30-second HTTP timeout,
close its own picker while loading, and use an unsaved settings-form API key from the
chat picker. Discovery is now single-flight and cancellable through the HTTP token;
navigation drains both discovery and its cancellation acknowledgement, plus accepted
chat preference writes. Late results cannot replace a new binding's catalog; init and
settings publication invalidate it. Different-URL/explicit-key catalog previews no
longer persist capabilities to the current server's settings. Leaving settings also
discards their preview catalog; the chat picker can reload after cancellation without
reopening. Regression review covers a failed cancellation acknowledgement as well:
the original request must still drain before navigation. This does not identify
the user's unspecified model error as a context overflow or justify reducing limits.

Node regression checks cover a 500-model catalog without hidden DOM work, cancellation
races and stale binding replies, preference/navigation ordering, and 100 foreground
activities coalesced without lost events. Four targeted host-neutral checks pass:
catalog cancellation releases binding, navigation/model controls leave the caller
context, bound session identity, and per-chat model isolation. These were run against
an isolated HEAD archive plus this slice because unrelated in-flight kernel changes
prevented the shared harness from compiling. Production controller/HTTP wiring is
source-reviewed; the harness controller remains a stub. Desktop also synchronously
discovers Outlook mailboxes on its minute timer, before its foreground-focus guard;
that COM wait and attach preparation still require a separate Desktop owner change
and Windows timing evidence.

Large-chat follow-up (2026-10-02): a 30–50 MiB JSONL file does not cross the
bridge whole; the initial message page is 80 items. A rebuildable per-chat SQLite
projection now holds header state and normalized message/artifact rows. The first
index build still validates the entire event stream on a bridge worker, with a
chat-list loading indicator; subsequent reads check an exact tail cursor, read
only validated suffixes and query history pages by ordinal. JSONL remains the
authority and the explicit CAS health audit detects/repairs a divergent index.
Scalar preference writes avoid a full message diff; generic aggregate writes
still compare all messages. The earlier 16-million-character in-memory cache,
document-scoped write locks, bound selection shell, deferred chat detail and
bounded UI projection remain in place. Temporary synthetic 30/50 MiB fixtures
with 6,000 messages and nine HTML revisions show index build 1.23/1.27 s,
indexed cold load 0.34/0.26 s, warm load 16/24 ms and generic save 0.23/0.29 s
on this host. These are host-neutral synthetic measurements; the reported chat
and Windows/WebView2 delivery have not been measured. First conversion, packaging
of `winsqlite3.dll` from Windows, and real cross-tab responsiveness remain open
evidence. See [the storage contract](../session-events.md) and
[ADR-0012](../decisions/ADR-0012-chat-sqlite-projection.md).

## Agent continuity and deterministic read loops — 2026-10-01

P1 incident evidence remains open; shared host-neutral fixes are implemented.
Owner: Conversation/model context, Core kernel and Resource Gateway.
The additional two Windows photos show the same `resources_read` path rejection
repeated for minutes, followed by an explanation naming a different error. Five
host-neutral probes established pre-fix defects independent of model quality:
the compiler replaces an original failed read with `resource_evidence_unavailable`
in all three result roles; unrelated tool admission drops a resource claim;
folded mutation provenance becomes assistant interpretation at compaction;
compaction notices disappear; six identical failed calls dispatch until the test's
iteration limit. The shared compiler/progress/working-set corrections now cover
these mechanisms, with exact terminal facts retained independently of source bodies.

Follow-up (2026-10-02): the first progress guard ended the whole run on a duplicate
failure or a second source conflict, preventing the model from acting on recovery.
Rejections now return explicit non-dispatch feedback and allow another tool or
corrected input; only three consecutive responses consisting entirely of rejected
calls stop the loop. A compact archived mutation receipt also lost its semantic
target when dropping data. Targets now survive separately from historical source
bodies, with unchanged success/no-op/error/unknown effect distinctions. These
paths are covered host-neutral; they do not close the original live incident.

Cross-turn follow-up (2026-10-02): a new user message previously reset optional
callable schemas to core even when the same chat retained exact accepted admissions.
The next turn now reuses unchanged, retained admissions within its request budget;
edited/cleared history, descriptor drift and budget overflow return to core.
Complete current skill/resource bodies already follow history/CAS and working-set
selection, so their actual inclusion still needs the exact next-request trace.
Focused host-neutral tool-pack checks cover carryover, drift, history edit and
budget fallback. Live Windows/model evidence remains open.

The [canonical analysis and ordered implementation plan](../conversation-protocol.md#agent-continuity-audit--2026-10-01)
separates operation facts from observation freshness, keeps typed provenance through
compaction and uses existing typed recovery for bounded progress. The
[tool audit](../tool-library.md#tool-ergonomics-audit--2026-10-01) covers current
schemas, contradictory guidance, result contracts and merge/split decisions.
Do not add another per-tool reread prompt as the primary remedy. Original accepted
calls/next-request bytes and exact Windows/Office/WebView2/model evidence remain
needed to attribute and close each photographed incident. Host-neutral passes do
not establish live provider or target-model behavior.

## HTML read presentation and premature completion — 2026-10-02

P1 — task continuity/completion; P2 — read presentation. Owners: Conversation/model
context, Task List/completion policy, transcript/UI presentation. Ниже сохранён
исходный аудит до исправления; реализованный follow-up и открытые границы — в конце
раздела. Исходный Windows-инцидент не закрыт.
На фото счётчики HTML/CSS показывают **символы**, не строки. В видимой части run
есть изменение `styles.css`, после которого модель утверждает также исправление
`app.js` и работу взаимодействий. Exact build, полный event stream и отправленные
model requests отсутствуют; нельзя доказать причину этого конкретного финала или
что отсутствующий в показанном участке вызов никогда не выполнялся.

Подтверждённые до исправления механизмы и границы:

1. **Подпись зависит от хранения тела.**
   [RuntimePayloadService.ExternalizeActivity](../../src/RNAssistant.Core/Storage/RuntimePayloadService.cs)
   при `DataJson.Length > 8192` сохраняет результат в CAS и обнуляет `DataJson`.
   Это происходит в `ChatStore.SaveInternal` до отчёта UI.
   [ChatCloneService](../../src/RNAssistant.Office/Services/ChatCloneService.cs)
   не передаёт runtime payload reference в bridge; отдельной краткой сводки нет.
   [activityResultCaption](../../web/js/app-agent-activity.js) читает только
   `DataJson`, дополнительно отказывается разбирать JSON длиннее 32768 символов.
   В обоих случаях успешный read получает общую подпись «Прочитано».
   [ToolResultPresentationService.Read](../../src/RNAssistant.Office/Services/ToolResultPresentationService.cs)
   также использует только `activity.DataJson`: раскрытие архивированного read
   не восстанавливает тело. Наличие файла/байтов в CAS не означает наличие preview.
   Это presentation defect; model result хранится отдельным protocol message,
   который `ModelContextCompiler` гидратирует независимо. Подпись не доказывает
   ни потерю, ни доставку тела модели.
2. **Завершение ответа не проверяет исполнение всей задачи.**
   [EvaluateFinalResponse](../../src/RNAssistant.Office/Services/ConversationKernelAdapter.Store.cs)
   возвращает `Complete`, если активного Task List нет. Список необязателен.
   При его наличии проверяется факт закрытия, а
   [TaskListService.Close](../../src/RNAssistant.Core/Services/TaskListService.cs)
   принимает model-authored `status=completed` без связи шага с mutation/read-back
   evidence. Поэтому один успешный CSS patch и финал с утверждением о двух файлах
   допустимы для runtime. Это gap семантической проверки, а не ошибка wire parser:
   `Completed` корректно означает конец run, не доказанную полноту deliverables.
3. **Точный остаток работы не закреплён после compaction.**
   `ConversationPromptComposer` включает для Task List только discovery entry
   через `ChatResourcePromptIndex`, с общим бюджетом индекса 192–600 tokens.
   Цель/шаги/статусы отдельной обязательной проекцией не передаются.
   [ContextWorkingSet](../../src/RNAssistant.Core/Services/ContextWorkingSet.cs)
   восстанавливает source/resource/capability bodies, но не гарантирует восстановление
   последнего `task_list_set` с полным списком. После ухода результата в compacted
   prefix остаются summary claims и возможность повторного чтения ресурса.
   Это риск пропуска этапов, не доказательство потери списка в исходном run.
4. **Остались противоречия инструкций.** В
   [AppSettings](../../src/RNAssistant.Core/Models/AppSettings.cs) основной tool
   prompt сохраняет admission через compaction, а `ContextCompactionPrompt`
   требует повторный `capabilities_read` и новое admission. Runtime notice самого
   compactor тоже говорит, что повторное admission не требуется. Основной workflow
   разрешает при блокере оставить список открытым и сообщить о блокере через final,
   но completion gate и `RUNTIME_CONTINUE` запрещают любой final с открытым списком;
   два последовательных final дают `task_list_open`. У Task List есть blocked step,
   но закрыть его можно только как completed/cancelled/superseded. Дополнительно
   `RUNTIME_CONTEXT.document.artifacts` советует Plan для complex task, тогда как
   основной prompt ограничивает отдельный Plan запросом пользователя/нуждой в design
   artifact. Исправление только одной строки системного промпта эти слои не согласует.
5. **Большое source read может оборвать продолжение.**
   `ResourceReadToolHandler` читает source целиком; `offset/limit` допустимы только
   для table/records, `section` — для Markdown. Gateway whole-read bound —
   2,000,000 символов, а доставка ограничена меньшим фактическим request budget.
   `ModelContextCompiler` может выбросить `PromptBudgetExceededException` с
   `CanCompact=false` для одного большого complete source. Это честный отказ,
   но модель не получает следующего шага для выбора меньшего фрагмента.
   Поиск даёт snippet targets, однако штатного запроса диапазона строк HTML/JS нет.
   Связанный large-resource gap уже есть в [BACKLOG](BACKLOG.md#large-resource-working-set-and-compacted-action-memory--2026-09-29).
6. **Read-back файла не доказывает исправление взаимодействия.** HTML tools
   выполняют static preflight (`scope=static_preflight`), не браузерный сценарий.
   Встроенный HTML skill это правильно оговаривает. «Запись подтверждена»,
   «статическая проверка прошла» и «Y-zoom/drag проверен» должны оставаться разными
   claims. Фото не подтверждает последний. Живую проверку нельзя заменить
   успешным scripted-model harness.

Исходный порядок предложенных изменений:

- **Presentation owner:** перед externalization сохранять typed bounded read summary
  (representation, returned count/unit, coverage/complete), независимо от тела;
  ленивый preview читать из CAS через существующего владельца storage с явным
  unavailable. Удалить зависимость подписи от полного JSON и согласовать producer,
  clone/bridge и UI. Не увеличивать inline limit ради счётчика.
- **Prompt/context owners:** согласовать основной/tool/helper/runtime prompts;
  сохранить authored prompts и существующий explicit review. Исправить устаревшие
  указания в документации: wire-v5 doc ещё называет prompt schema 32 при текущей 33,
  а backlog «Resource read prompt wording» описывает уже исправленные defaults.
- **Task List + Conversation owner:** из существующего состояния включать в каждый
  запрос компактную точную проекцию цели, оставшихся шагов, blockers и имеющихся
  receipts с резервом бюджета перед большими телами. Не создавать второй durable
  task store; для коротких ответов не навязывать отдельный Plan/ритуальные стадии.
  Task List сейчас требует минимум три шага — пересмотреть это для простых
  задач с двумя независимыми deliverables.
- **Completion policy owner:** связать проверяемые deliverables с semantic targets
  и требуемым видом evidence; runtime связывает их с фактическими terminal events.
  Финал при незакрытых проверяемых обязательствах возвращает модели конкретный
  остаток для продолжения. Отдельно разрешить честный blocked/input-needed исход,
  сохранив незавершённые шаги. Не определять выполнение по словам «исправил» или
  числу успешных calls. Семантическое качество полностью детерминированным gate
  не доказывается и требует model evaluation.
- **Resource/context owners:** добавить ограниченный source excerpt с понятным
  selector/coverage и восстановимым ответом о недоставленном большом body. Runtime
  сохраняет revision/cursor authority; excerpt не разрешает whole-file overwrite.
  Сохранить текущие unknown-effect non-replay и bounded no-progress recovery.

Минимальные проверки реализации: read выше 8192/32768 после save/reload и отсутствующий
CAS; два обещанных файла с записью только первого; compaction между двумя правками
с точным остатком/исходником второго; blocked final с открытым списком; oversized
source с успешным переходом к excerpt. Для целевой модели повторить эти сценарии
и оценивать фактические mutations/проверки/повторы/пропуски, а не текст финала.

Evidence исходного аудита: вызовы shipped UI functions из Node дали счётчики для
3563/6965 символов, общую подпись после simulated activity externalization для
10000 и общую подпись уже inline для 40000; `node tests/web/completion-guard.test.js`
прошёл 19/19. C# storage/preview/completion/context paths проверены чтением кода,
новый end-to-end C# repro не запускался. Build, Office/VSTO, WebView2 и live-model
проверки не выполнялись. Production code этим аудитом не изменён.

Реализованный follow-up (2026-10-02):

- Transcript/UI: typed `ReadSummary` переживает externalization и replay; caption
  не разбирает большой JSON. Preview читает точный CAS payload и явно показывает
  отсутствие тела. Прежние activities без summary остаются с общей подписью.
- Conversation/Task List: точные goal/steps/statuses/notes/reason/blocker закреплены
  в обязательном `RUNTIME_CONTEXT.active_task_list`. Допустимы 1–32 этапа; список
  необязателен. Сохраняется история уточнения цели, замены и перестановки шагов.
- Completion, пересмотр после review: удалены первоначальные `read`/`changed`
  bindings и проверка изменения baseline hash. Они мешали обоснованному no-op,
  пересмотру гипотезы и работе без полного source в контексте. Удалён и прежний
  completion gate с принудительным `RUNTIME_CONTINUE` из-за открытого списка.
  Статусы плана — `agent_assessment`; агент решает о завершении из контекста и
  фактов, может пояснить отсутствие необходимой правки. `close` не требует
  механически завершить все шаги и не меняет неуказанные статусы. `blocked` с reason
  сохраняет список без привязки к одному run. Final не закрывает список и не теряет
  незавершённые шаги; следующий запрос снова получает точное состояние.
  Tool receipts, guards записи и unknown-effect non-replay не заменяются оценкой
  агента. Снятие gate не доказывает правдивость финала модели: это остаётся предметом
  target-model evaluation.
- Resource/compiler: source/text `startLine` + `lineCount` возвращают точный excerpt
  с частичным покрытием. Не помещающееся полное тело заменяется явным notice и
  operation receipt с прежним outcome/effect; это не доказательство чтения тела и
  не разрешение whole-file overwrite. Сохраняются bounds и unknown non-replay.
- Defaults schema 36 согласует основной/tool/Task Tracking prompts с новым
  контрактом. Schema-35 migration выбирает текущие defaults и сохраняет прежние
  тексты перед записью settings. Canonical docs и reviewed inventory обновлены
  вместе со схемой tool.

Дополнение (2026-10-02): response v6 и prompt schema 38 разделили вызов
инструмента, продолжение без вызова, завершение, блокировку и ожидание ответа.
Шаг `continue` теперь сохраняется и виден следующему запросу; повторение без
действий ограничено. Итоговая карточка показывает эффект и известный остаток
Task List отдельно от решения модели. Это устраняет неоднозначность wire-формата,
но не доказывает, что target model выберет `done` только после фактического
выполнения всех требований. Исходный Windows/HTML-инцидент остаётся открытым.

Focused evidence после пересмотра: `agent continuity:` 5/5, `task lists:` 4/4;
пересмотр плана и rationale после save/reload/compaction, open/blocked final в двух
запусках, CSS-only правка без лишней записи JS, read summary/CAS и excerpts.
Defaults/guidance, schema migration, R61 inventory и unknown-effect non-replay
также прошли. UI: task continuity 1/1, completion 20/20, artifact JSON 8/8,
Plan 8/8, artifact text 10/10, run state 6/6. Это scripted-model и host-neutral
проверки, не оценка качества решений реальной модели.
Office/VSTO validation и исходный Windows/WebView2 incident остаются открытыми.

## Repeated resource reads and HTML binding failures — 2026-10-01

Six Windows photos show repeated capability loads, reads/rewrites, missing HTML or
Excel targets, and `records` requests failing with an unavailable `text` view.
Owners: model context compiler, semantic Resource Gateway and HTML workspace.
Code/host-neutral reproductions establish separate causes:

- Evidence deduplication changed an earlier successful read to
  `resource_evidence_stale` merely because a later call used the same snapshot.
  It could also erase different JS outputs. Different results now retain status/data;
  equal complete read results retain one body plus successful linked causal frames,
  avoiding repeated skill bodies consuming the request budget. Completed mutation frames
  retain their runtime `tool_call_id` correlation.
- The prompt index could advertise superseded document HTML snapshots that current
  discovery no longer resolved. Those known old roots are omitted, with history
  retained. HTML scope now includes the document-owned workspace root.
- Static JSON writes already created a text binding, but results did not supply a
  readable source target. Binding/manifest reads exposed runtime metadata instead
  of usable source/member targets. Explicit semantic projections now separate the
  source values from workspace/binding metadata; invalid self-binding is rejected
  before mutation, and an identical rebind is a verified no-op.
- The HTML skill hard-coded `stream({view:'table'})`, conflicting with records/text
  bindings. Guidance now consumes the bound view and describes actual batch fields.
  JS worker reads are separate from page execution and cannot verify its render.

The exact original model requests and accepted arguments are absent from the
photos. Repeated loading after a new user turn or skill-body compaction can be
intentional; same-turn repetition after complete current evidence still needs the
affected trajectory to distinguish model behavior from another delivery defect.
Windows Office/WebView2 and the target model remain open evidence.

## Office panel switch and shutdown race — 2026-09-30

Windows photos show repeated bridge switch-busy errors during chat navigation and
`ObjectDisposedException` from `OfficeStaDispatcher` after an in-process panel
session was disposed. The exact pending request at each switch is not recorded, so
the photos alone do not prove every refusal was false. Code inspection confirms two
risks: background catalog sync could overlap an exclusive chat selection, and panel
shutdown disposed its controller/adapter before accepted bridge work had finished.
Owner: WebView bridge/pane and `AssistantRuntime`; in-process and Desktop binding teardown.

The UI now drains an existing catalog sync before selection and pauses new polls;
timing telemetry does not reserve the binding. Bridge and resource handlers expose
a shutdown drain; runtime and in-process Office binding cleanup wait for that drain
without blocking the Office UI thread. VSTO add-in shutdown also defers its shared
dispatcher cleanup until pane requests leave. Busy switching returns a typed error without
a stack trace. Focused WebView sync and host-neutral bridge checks pass; live
Windows Office/WebView2 and long-running cancellation still need exact-build verification.

Desktop review found the same early-release path in `MainForm`: it disposed its
original Office adapter immediately after `AssistantRuntime.Dispose`, before the
runtime's accepted bridge/resource work had drained. Desktop now defers that adapter
release until `ShutdownCompletion` without blocking its UI thread. The host-neutral
checks above do not exercise Desktop/COM teardown; verify attach replacement and
window close during a long request on Windows before closing this risk.

## Repeated confirmed VBA writes — 2026-09-30

The new screenshot starts the second user request at “Вы правы”. Within that
response, Agent reports three confirmed `common.vba_write_module` actions on
`P5_Consolidator` and proposes another correction after the user had observed the
original compile error disappear. The preceding screen section shows two unverified
VBA changes from earlier activity; those are separate outcomes and do not negate
the three later read-backs.
The screenshot does not show the exact second model request, VBA source or Windows
build, so it cannot establish whether the model missed the first after-state or
ignored it. A confirmed write proves matching source read-back, not compilation.

Follow-up photo shows successful patches interleaved with stale/ambiguous patch
errors and repeated renaming of already changed identifiers. Owner: model context
compiler / VBA patch engine and guidance. Two host-neutral defects are reproduced:

- A call's input evidence was treated as its result's currentness evidence. The
  write superseded that input, so the next request could contain `outcome: Error`
  and “Prior observation is not current evidence” alongside `VerifiedChanged` and
  complete correct after-source. The compiler now filters result observations
  independently; input evidence remains available for audit/guards.
- Completed mutations folded before CAS hydration and discarded `data`, losing
  patch hunk diagnostics and structured recovery. Folding now follows full result
  hydration/projection and retains semantic data within the complete request
  budget. Missing mutation payloads fail explicitly; no terminal status is invented.

The earlier regression checked source presence only; its final-step assertion was
also on an unreachable request number. The corrected Agent test asserts actual
completion and the next request's successful write outcome outside the model stub.
Compiler tests cover fresh reads after stale input, all three result roles,
archived mutation data above 8192 characters, patch coordinates and budget refusal.
The outcome assertion failed before correction and passes afterward.

Patch now accepts exact line/column coordinates with old-text/context verification,
or unique context. Ambiguity returns explicit candidate locations. A stale selected
location cannot redirect to another matching line. Tests cover repeated text in
different procedures, a repeated old patch, newline styles and bounded diagnostics.
Guidance requires a concrete remaining defect before editing a verified after-state.
These host-neutral checks do not establish the exact photo incident's full cause
or real-model behavior; Windows/Office and the affected model trajectory remain open.

Broader result-delivery audit (2026-09-30), owner: conversation model session and
context compiler. Further defects were identified and corrected:

- Confirmation created a model session and compiled its initial snapshot before
  appending the terminal result. That cache could serve the next request without
  the already completed action. Compilation now occurs at the request boundary;
  accepted history appends invalidate cached snapshots.
- After compaction, complete current input evidence on a later assistant message
  suppressed source carry-forward, although that message contained no source bytes.
  Only actual source-bearing observations suppress the archived body. A focused
  regression reproduced zero restored bodies before correction and one afterward.
- Media preparation reconstructed materialization without ResourceEffect,
  ResourceEvidence or AuthorityCommitId. The explicit reproduction failed with a
  missing verified effect. Both media and general delivery failures now preserve
  terminal semantics/evidence; a warning does not imply the mutation did not happen.
- Large mutation archival dropped module/title labels needed before result
  hydration and after compaction. Markers now retain semantic source labels.
- Current after-source and unresolved accepted-call hydration used an uncalibrated
  byte bound. They now use the same calibrated request-capacity admission as other
  payloads, followed by the complete request budget check.

The successful confirmation regression uses 100 ordered hunks, forces result CAS
archival, verifies one dispatch and complete labeled after-source in the next
request, and checks the provider message builder preserves it. The stale-confirmation
fixture now keeps the original run identity so it tests source drift independently
of execution-identity mismatch. These checks do not exercise real Office or claim
that model decisions are deterministic when the correct result is supplied.

## Excel inspection loop — 2026-09-30

Photos show repeated successful `excel.inspect` steps, a separate 155.8 KB result
and a failed `excel.inspect charts` resource search, without visible progress to
the requested dashboard. Exact arguments, model requests and Windows build are
not supplied; the photo incident's complete causal sequence remains unverified.

Confirmed host-neutral defect before correction: `ConversationModelSession.MaterializeToolResultMessage`
archived result envelopes above 8192 **characters** and replaced their data with
`payload_externalized`, `complete` and a size. `ModelContextCompiler.RequiresExactPayload`
hydrated only resource/capability results; `excel.inspect` kept the marker even
with ample request budget. A readable tool-result artifact was created separately,
only above 8192 estimated **tokens**. Results between these thresholds could therefore
reach the model as `ok` / `complete=true` without business data or a discoverable
result resource. The full bytes remained in CAS; this was lost model input, not lost
durable data. Larger results required an additional semantic resource read.

A temporary probe invoked the production materializer and compiler from the
existing Debug harness assembly; portable-PDB SHA-256 checks matched all five
relevant then-current source files. With a 900000-token compiler budget, a 1070-character
result retained its observed chart name. A 16070-character result became a
199-character model message with no chart data and zero readable artifacts;
a 160070-character result became a 360-character message with one artifact but
no chart data. Neither archived payload was hydrated. No Office or live model was
used.

Correction (2026-09-30), owner: model context compiler / result projection (P1).
All selected non-folded result payloads now hydrate from CAS before model projection;
the resource/capability-only hydration filter is removed. The full JSON reaches the
model when the complete request fits, without a separate per-result token cap.
Storage thresholds remain storage decisions. Complete results no longer receive
an instruction to rediscover their already supplied data. Payload admission follows
calibrated request capacity; a real overflow uses existing compaction or an explicit
budget failure, without replacing generic data with a success marker. Focused tests
pass for 1/16/160 KB JSON in all three result roles, calibrated capacity and actual
overflow, plus existing result projection/current-source checks. Exact Windows,
Office and target-model reproduction remain open.

At the initial 2026-09-30 probe, AgentKernel blocked identical unknown-effect calls,
not successful repeated reads; the default 256 model iterations only bounded the eventual run length. Any
separate no-progress policy must preserve legitimate refresh/recovery. Resource
search is literal, so `excel.inspect charts` need not match the generic
`Tool result · excel.inspect` title. Correlate the Windows trajectory before
claiming this delivery defect caused every repeat in the photos.

## Agent recovery photos — 2026-09-30

The reported VBA patch loop, Excel snapshot refusal and empty-tools stall are
corrected at their host-neutral contract boundaries. The exact accepted model
arguments, source bytes, workbook UsedRange and Windows build in the photos are
unavailable, so their full causal sequence and target-model behavior remain open.
Owner: VBA mutation diagnostics / AgentKernel / Excel search / ModelProtocol.
Reproduce on Windows x64 + Office x64 + WebView2 with a redacted trajectory before
claiming the user scenario qualified; do not infer that a rejected patch wrote code.
The older R72 and R78 entries below describe their historical implementations;
the current empty-call and definite no-effect recovery rules are in
`docs/conversation-protocol.md`.

Follow-up photos (2026-10-05) show `RESOURCE_SNAPSHOT_TOO_LARGE` while the
`excel.find_cells` card names a narrow `B115:B137` range. The earlier assumption
of a broad requested range was incorrect. Two host-neutral defects could cause
this: an explicit `scope=sheet` plus `address` silently searched the sheet's
UsedRange even though the activity card displayed `sheet!address` without scope,
and regex capture retained and counted the duplicated `Formula` body
of every non-formula cell. Scope/address conflicts now reject before Excel access;
capture stores only real formulas. A 23-cell range with long values and a large
UsedRange passes the focused regression. Exact screenshot cause remains unknown
without the call arguments; no screenshot shows a `PromptBudgetExceeded` model
diagnostic. The resource target schema also now permits a direct known A1 range
read through `common.resources_read`.

The same photos show a VBA creation verification mismatch reported as generic
`vba_access_error`, and separate rejected replacement/patch attempts. Direct
creation now returns typed failure after verified removal, or unknown effect when
cleanup cannot be verified; typed rollback reaches the mutation journal. A silent
failed removal is also detected by component-count read-back. The comparable-hash
mismatch itself still needs exact submitted/live module source and Windows/VBE
reproduction. The visible hash strings are durable diagnostics, but nested exception text could
previously pass them into the model projection. The projection now redacts runtime
hashes/IDs in nested VBA result strings while retaining the error code and durable
diagnostic. A focused regression covers the archived and model-facing forms.
`vba_snapshot_refresh_required` is a deliberate stale-source guard;
the model's later creation of a different module does not resolve that source
conflict. Chart snapshot rejection now reports the invalid chart index and field
without dispatching a mutation; the photo does not identify which field failed.
The cancelled run with two unknown effects needs an exact trajectory before
assigning a code cause.

## Outlook VSTO pane report — 2026-09-29

Windows photos show two `CS0019` errors in Outlook `OfficeHosts` code; the other
missing-assembly errors follow the failed build. A separate debug photo shows the
Outlook VSTO DLL loading and two first-chance `ArgumentException` entries without
messages or stacks. Those entries do not establish why the pane was absent.

The `CS0019` expressions are corrected. Outlook Inspector/Explorer handle lookup
now uses `IOleWindow`; the prior `HWND` property lookup could yield zero and make
the ribbon action return without showing the pane. The ribbon now reports failure
to identify the active window. Owner: `RNAssistant.OfficeHosts` Outlook window
binding and `RNAssistant.OutlookAddIn` pane activation. Evidence still needed on
Windows x64 + Office x64 + VS 2022: rebuild `Debug | x64`, check the Outlook
COM Add-in load state, click Open Assistant in Explorer and Inspector, and capture
the full exception text/stack if either path fails. No Office qualification is
claimed from the source correction.

## VBA patch incident — 2026-09-08

Original incident open; overwrite defect reproduced and corrected host-neutral.
User reports: assistant claims changes succeeded while requested code was
reported absent. Photos 1–2 show two DemoModule diff rows labelled unverified;
photo 2 shows a subsequent source read and patch attempt. Photo 4 contains
`Debug.Print` in SumRange, FormatHeader and CleanData after the later attempt.
The photos do not expose the initial accepted patch arguments, tool results,
exact read coverage, journal terminal or Windows build revision. Model prose
about a failed/overwritten patch is not execution evidence.

Initial code inspection and 19 focused host-neutral checks established:

- `VbaPatchEngine` / `VbaMutationService.ApplyPatch` reject stale or ambiguous
  hunks before dispatch; ordered replacements are applied to candidate text only.
- `VbaReadBackRejectsWriteDrift` injects backend success without changing source:
  result is `error`, code `vba_patch_verify_mismatch`, terminal `not_applied`.
  Unreadable/divergent final state is `unknown`; terminal persistence failure is
  also `unknown`. Current native/result projection retains these statuses.
- `RunChangesService.AddVba` requires matching live-text hashes for the retained
  diff. A write accepted by VBE-comparable verification can still get the same
  `unverified` label when VBE changes formatting. This is a diagnostic ambiguity,
  not proof that read-back accepted missing executable statements.

Run-diff follow-up (2026-09-08): retained original → intended code is now visible
for unverified results, including comparable-only formatting matches. A separate
planned label and subtotal distinguish it from exact confirmed source; the header
includes all available comparisons. This removes
the hidden-preview limitation; it does not establish the actual source for the
original Windows incident or close its evidence gate.

Owners: VBA mutation/verifier, model result projection, run changes projection.
Follow-up reproduction: one accepted model response contains an exact patch adding
`Debug.Print`, a source read, then a whole write containing the old source plus a
new header. Before correction, the whole write returned `ok`: `MutationCorrelation`
collected evidence from all chat messages, including the sibling read whose result
the model had not yet received. Both writes could match their own read-back; this
is an observation/authorization ordering defect, not concurrent COM dispatch or a
missing full-module patch buffer. Accepted calls now retain their input-snapshot
evidence and the VBA native owner uses that exact call's evidence. The regression
rejects the stale overwrite before dispatch and permits a corrected write after
the next model response receives the current source. Manual/editor guards and
multiple managed writes remain supported.

Next evidence: export the affected chat's accepted calls/results and exact source
read responses, plus correlated `mutation.prepared` / `mutation.terminal` and
before/intended source from `%AppData%/RNAssistant/vba-journals` / shared CAS; record
the actual Windows build revision. Determine whether the missing change was in
the accepted patch, rejected before dispatch, rolled back, left unknown, or replaced
by a later write, and what result reached the model. Do not infer the exact
cause for the original incident from photos alone. Windows/Office reproduction and
the diff-label ambiguity remain open; the reproduced sibling-observation defect is
fixed host-neutral.

Исходная база: `v16.0.4`. Приоритеты ниже — стартовая оценка из master plan,
не утверждение о воспроизведённых дефектах. Phase 0 не проверяла runtime/Office.
Отдельно отмеченные результаты Phase 1A получены с fake LLM/Office, не на реальном COM.
Phase 1B проверяет host-neutral correlation; production controller/Office/WebView
не исполнялись. Known baseline failure указан отдельно от новых trace tests.
Phase 1C проверяет runtime guard, replay/DTO и JS-проекцию без Windows execution.
Phase 2A проверяет ModelProtocol с fake endpoint; live tLLM не проверен.
Phase 2B закрывает R20 на host-neutral tests; provider retries проверены с fake
transport и injected delay, без реального network/backoff qualification.
Phase 2C1 проверяет только новый Core v3 contract; live parser/history остаются v2.
Phase 2C2 проверяет context wiring на host-neutral loop и current-v3 history reader;
active v2 client context не enforce. V3 cutover/old-chat guard и Windows ещё не проверены.
Phase 2C3A проверяет shared active wire и probes; v2 остаётся. Existing prompt-reset
characterization подтверждает R27 как gate будущего cutover, не как новый regression.
Phase 2C3B заменяет этот reset preservation/review flow; Core settings/loop и JS
проверены, production controllers/WebView/DPAPI — нет.
Phase 2C3C переключает actual wire/history на v3 и проверяет preflight, run IDs, singleton
safety, refusal и review/reset на prompt schema 12. Windows/live-provider gates остаются.

Screenshot corrections (2026-09-07, host-neutral): final warning now labels
`WriteError`/`WriteUnknown` as attempt history. Write errors do not by themselves
assert unresolved final failure; unknown effects retain a confirmation warning.
Historical counters remain authoritative and unchanged. Per-effect reconciliation
still requires explicit correction evidence and is not implemented by wording.
Owner: kernel summary / run projection.

Both accepted/rejected parser diagnostics now use the same best-effort helper.
Failure is reported without stopping repair; cancellation/attempt limits remain.
Mandatory runtime acceptance, execution and request/response storage still fail
closed. Owner: ModelProtocol + ModelTracePersistenceService. This fixes optional
parser-trace availability, not persistent storage corruption or queue contention.

Trace queue concurrent-waiter fix (2026-09-28): each scheduled write now carries
its own failure outcome. Previously the first terminal waiter could clear the
shared queue failure after all writes completed, letting a second terminal waiter
report success although its write was skipped. Both waiters now receive the failed
write outcome; a later request can retry only after the failed queue drains. This
is a host-neutral fix; live model/Windows behavior remains unqualified.

Chat navigation / persistence contention (2026-09-07, partially contained): user reports freezes
while streaming, loading artifacts and switching chats. `ChatStore.PersistenceSync`
is static across chats; `SaveInternalLocked` performs artifact externalization,
projection/diff and durable append under it. `SelectChat`/`GetChatState` currently
load and build the full projection, including Office document state. Bridge
dispatch now moves chat catalog/state/selection and complete agent/tool calls to
cancellable worker boundaries; bound Office work still uses its existing STA
dispatcher. This removes Office UI starvation and keeps cancel/navigation delivery
reachable during a long run. A cold validated replay or waiting for an append may
still delay completion of an individual navigation request. LLM raw stream traces
are already batched at 64 Ki characters or one
second of buffer age; this is not evidence of a durable save per token. Owners:
ChatStore + ChatSessionService/controller projection + Web bridge dispatch. Next
storage slice must separate host-neutral loading/projection from bound Office
capture and make lock wait observable/cancellable without weakening flush/commit
barriers. Measure
lock wait, replay/CAS, projection and bridge/UI durations on the actual Windows
candidate before assigning the dominant cause. The host-neutral stream UI fix
removes repeated history serialization/DOM detachment and batches background
sidebar paints; bridge worker dispatch contains UI starvation but does not close
this storage/navigation gate.

Navigation follow-up (2026-09-28): `SelectChat` now records load, active-selection and
full-projection durations above 250 ms in the runtime log; the WebView console
records bridge versus render duration for slow chat switches and exact HTML source
load duration above 250 ms. Full chat-state updates retain an already loaded HTML
source for the same exact revision and file metadata; a bounded in-memory exact
source cache avoids another download when revisiting a chat.
Follow-up inspection found that the 60-second focused WebView poll scans all chat
headers and reloads the active projection. For a chat exceeding the per-entry
projection cache limit (about 4 million characters), each load can validate and
replay the complete JSONL. Skill source selection also loads the addressed chat
before opening the small source; the editor evicts clean text from other skills,
so switching back reopens it. A model request trace synchronously waits for CAS
storage and a durable event append before HTTP dispatch, and these writes share
the static persistence lock with session saves. The background poll now skips an
active send; explicit refresh remains available. Runtime timings above 250 ms now
separate catalog active load, header scan, full chat detail, skill chat/open and
model trace persistence. These are code-path findings, not a measured Windows root
cause. The existing model diagnostics UI already separates preparation, response
headers and first chunk; compare those stages with trace persistence and WebView
bridge/render timing on the affected Windows host before changing storage cache
limits or replay rules.

Windows timing photo (2026-09-29): 25–26 chats account for about 70 MiB of JSONL;
header reads take roughly 0.6–0.9 s. One startup reports session 1.7 s, chats
0.6 s, libraries 2.0 s and projection 0.06 s; its bridge handler takes 4.4 s,
serialization 0.09 s for about 1.1 million characters, and browser render 0.11 s.
One completion reports `finalSave=26ms` and `responseProjection=2525ms`: the visible
“saving history” interval chiefly covers response preparation, not the final
JSONL append. The completion response rebuilds tools and skills and scans chat
headers; Agent mode invalidates the document VBA catalog before that response.
The response now logs those sub-times separately and uses a broader progress label.
The canonical save also reused its already serialized post-change projection for
the in-memory cache, removing a second full session serialization per save; event
bytes, hash validation and durable append ordering are unchanged.
Model setup separately reports compaction around 3 s and catalogs around 1.4 s.
Several failed model requests last about 42 s without an HTTP status, while later
successful requests show roughly 2–5 s to response headers and 70–144 KiB request
bodies. These are stage observations, not proof that JSON format causes the provider
delay or that a 70 MiB stream is safe to trust without replay validation.
The WebView message projection now omits hidden protocol bodies, extracted
attachment text and activity guard material; completion logs no longer repeat
tool `dataJson`, and bridge output avoids the intermediate token tree. Next Windows
run should compare response characters, handler/serialization time, header cache
behavior and the newly separated prompt/tool/skill times on the same chat; inspect
failed-request endpoint/cancellation evidence before changing model request limits.

Resource cutover / catalog freeze (2026-09-07, fixed host-neutral): the generation
captured by `UseInput` is carried into the model session and compared against its
final frozen `CaptureMany` tuple. Intervening publication refuses with
`RESOURCE_CATALOG_CHANGED` before compilation/dispatch; fresh binding is required,
without automatic model/tool replay. Existing request/repair retains its original
publication and settings/budget. The extended frozen-prompt regression covers
publication between capture and freeze, unchanged receipt on refusal and successful
fresh rebind. Owner: existing catalog capture / model-session boundary. Real
Windows execution remains unqualified.
See the finite closure order in [Resource Fabric](../resource-fabric.md#master-acceptance-reconciliation--2026-09-07).

Resource cutover / generic search capture (2026-09-06, fixed host-neutral): Gateway
now publishes typed live document/VBA scan captures independently of matches, then
binds bounded snippet evidence to the published logical revisions. Zero-match drift,
historical no-I/O reads, missing-CAS refusal, partial VBA coverage and body-free
backup metadata pass focused checks. The matched-snippet publication path is removed;
no global polling/background rescan. Owner: Gateway + live document/VBA providers.
Real Windows/COM qualification and other host-specific search consumers remain open.

Resource cutover / Outlook (2026-09-06, open): `MailItem.Body` materializes a whole
COM string (the former character ceiling is removed); exact-capture completion
does not prove bounded source allocation. Owner: Outlook backend/resource cutover.
The former session-wide `ResolveMail(entryId)` lookup is contained host-neutral:
Inspector lookup uses only its retained mail; folder lookup supplies StoreID and
checks parent StoreID/EntryID before capture. Discovery reads bound headers only.
Real COM membership/unsaved-mail and large-mail execution remain unqualified;
no scope/large-mail Windows gate is closed.

Resource cutover / allocation deferral (2026-09-07, explicit user decision): Outlook
whole-string allocation above remains unresolved. Inspector's initially deferred
full-JSON-then-truncate path is now replaced by bounded early-stop serialization,
including bounded string escape buffers; its compiler/input snapshot is unchanged.
Owners/triggers are recorded in [BACKLOG](BACKLOG.md#user-deferred-source-allocation--2026-09-07).
This host-neutral correction does not qualify Outlook source allocation,
real WebView2 responsiveness, Windows/Office or a release candidate.

| ID | Priority | Риск | Владелец | Защита / фаза | Статус |
|---|---|---|---|---|---|
| R01 | P0 | Model completed скрывает write error/unknown или отсутствие write | AgentKernel / Application / UI | Phase 1C warning + Phase 3B2 shared kernel summary, actual event replay; production delivery R21 | contained host-neutral 1C; Windows qualification open |
| R02 | P1 | tLLM protection вместо JSON | ModelProtocol | 2A/2B: typed boundary, clean repair, общий лимит и fake protection/HTML tests; former v4 validation/repair проверены 2C3C + R29; current v6 contract описан в canonical protocol, live endpoint qualification отдельно | contained for fake content; live-provider gate open |
| R03 | P0 | Write применён, ответ потерян | Domain/Host | 6D–6J contain typed VBA writes; 7C and 11T1–11T8 move every current Office mutation family to exact typed owners with precondition/read-back verification and a dispatch marker before the first possible effect. 11T9–11T10 complete VBA/controller/custom switches and delete the result fallback. Verified no-op/change are distinct; apply/read-back failure after possible dispatch is non-retryable unknown | contained host-neutral for all current mutation families through 11T10; real Windows partial-effect/rollback/read-back qualification remains open |
| R04 | P0 | Patch направлен не в ту книгу | HostRuntime | 5B1: общий operation gate до guard/preparation, manual/resource/editor reads, повторная проверка после ожидания/confirmation и нейтральный session port. 5B2: direct selection/context/catalog reads switched host-neutral. 11T0/7D–11T8 bind exact Excel/Word/PowerPoint/Outlook objects or windows for their retained lifetime and remove execution-time active-document/window fallback; WQ0/WQ-SESSION квалифицируют принятое identity assumption | open evidence; all current host target fallbacks are removed host-neutral, but real COM proxy identity, close/reopen, Save As, multi-window and desktop/VSTO/native composition remain unverified |
| R05 | P1 | Schema исчезла из контекста либо snapshot изменился под тем же tool ID | ToolPack | 8A pins descriptor/schema + typed policy + handler/entry point/scope/host + package fingerprint in one immutable execution snapshot. 8B replaces callable LRU with atomic full-budget extensions/new revisions and no eviction/partial publication. 8C persists accepted/rejected decisions before publication and rematerializes only the exact accepted `TurnId` extension chain; drift/broken chain visibly falls to core until an accepted rebase, while raw/rejected evidence has no authority. [8A](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [8B](PHASE_8B_CALLABLE_TOOL_PACK.md), [8C](PHASE_8C_TOOL_PACK_EVENTS.md) | contained host-neutral through confirmation/compaction/crash replay; Windows/live-provider WQ-PACK open |
| R06 | P1 | Модель не знает о tool | ToolPack/Discovery | 8B finite exact mode/host core plus 11O6 reassessment publish the complete routine profile (Excel + VBA write/patch); complete compact registry plus exact read admits rename/restore/delete/macro and other optional schemas atomically and reports rejection visibly. [Evidence](PHASE_11O6_MINIMAL_CORE_PACK.md) | fixed host-neutral for core/discovery/admission; live-provider and Windows WQ-PACK open |
| R07 | P1 | VBE нормализует source | VBA | 6A: один comparable canonicalizer и raw CAS hash отдельно; 6B: typed host read validation без повторного source unescape. Реальный VBE/read-back требует Windows | contained host-neutral 6A/6B; Windows/VBE qualification open |
| R08 | P0 | Journal расходится с live state | VBA | Read-only recovery; 6C–6G centralize module preparation/read-back. 6I package correlates session lifecycle and marker state; 6J rename classifies exact complete-before/complete-intended/mixed identities without replay. Prepared CAS reaches each backend | contained host-neutral through admitted module/package/rename boundaries; Windows reconciliation gate open |
| R09 | P0 | Cancellation после COM dispatch | Host/Domain | 6D module, 6I package and 6J rename: cancellation before dispatch records inspected terminal and rethrows; after dispatch is inspected and maps to non-retryable unknown unless intended/before state is proven. Session package cleanup uses a non-cancelled explicit mutation in `finally`. Real HostRuntime/COM cancellation remains WQ-SESSION/WQ-VBA | contained host-neutral for admitted typed VBA pipelines; Windows gate open |
| R10 | P1 | UI показывает устаревший статус | UI/Persistence | 9D5: every full projection carries session revision; per-chat UI monotonic guard rejects late detail and keeps a newer catalog summary, while canonical stream CAS rejects stale writers. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral 9D5; Windows multi-window/WebView acceptance open |
| R11 | P0 | Replay меняет outcome | Runtime / Persistence | Phase 3B2 stored `RunSummary`; 9D1–9D5 extend actual normal/error/unknown/pending/cancel/append-fault/recovery replay through equal immutable `RunViewState`, without policy recomputation. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral through Phase 9; Windows restart/WebView matrix open |
| R12 | P2 | Локальное исправление затрагивает десятки файлов | Architecture | Freeze, boundaries, change budget; все фазы | monitored |
| R13 | P2 | Версия/tag на каждый commit | Release process | Правила заменены; repeat-build/commit и release gates tests, Phase 0 | mitigated |
| R14 | P1 | Legacy/new paths сосуществуют бессрочно | Migration | 11T0/7D–11T9, 11J/11K and 11T10 switch every active consumer, delete generic host catalog/dispatch plus definition/result/UI projections and add forbidden-symbol/source-inclusion checks. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10; Windows WQ validates the typed direct behavior separately, not legacy coexistence |
| R15 | P1 | Feature flags становятся второй архитектурой | Application | Временный явный release scope, Phases 10–12 | open |
| R16 | P1 | Новые build metadata / ClickOnce не проверены на Windows | Release process | Сохранить исходную AssemblyVersion 16.0.4.0; qualification до release | open |
| R17 | P2 | Чужие незакоммиченные изменения попадут в Phase 0 | Governance | Проверить исходные файлы и stage только явный список Phase 0 | monitored |
| R18 | P2 | Source archive без Git потеряет build identity | Build | Явные SHA/branch/tree-state properties; отказ вместо скрытого fallback | documented |
| R19 | P1 | Two-stage release/signing scripts ещё не выполнены на release workstation | Release process | На Windows проверить prepare-without-tag → exact candidate/WQ → immutable signing → finalize/tag; обычные commits workflow не запускают | open; host-neutral gates cover MSBuild/runtime evidence contracts only |
| R20 | P1 | Лимит 20 retries допускал 21 invalid response вместо 20 attempts | ModelProtocol | Phase 2B: initial включён в total 1–20, valid на 20 принимается; provider retries/fallback считаются отдельно; red→green boundary tests | resolved host-neutral 2B |
| R21 | P2 | Optional trace может быть неполным; controller wiring/реальная UI delivery не проверены | Diagnostics / Application | Fixed-stage error log без payload; no effect decisions from trace; `ui.projected` — только DTO, CAS failure допускает пропуск marker после release lease; Windows validation в Phases 1C/5–9/12 | documented 1B; open |
| R22 | P1 | Full harness: compact catalog ожидал устаревшие host tool counts | ToolPack / Tests | 8A audited current public catalogs (Excel 15, Word 9, PowerPoint 9, Outlook 5), updated only stale expectations and retained removed-alias rejection | resolved host-neutral 8A |
| R23 | P2 | Legacy ToolResult не всегда различает частичный/неизвестный effect; успешный mutating call может быть no-op или иметь слабую domain verification | ToolRuntime / Domains | 3B2 preserved historical uncertainty; Phase 4 introduced typed runtime/result evidence, mandatory 11T/11J/11K switched every current family, and 11T10 deleted the final result/UI conversion. Historical missing evidence stays exact `unknown` and is never reconstructed from prose | resolved for active runtime host-neutral in 11T10; real Windows effect/read-back behavior remains WQ evidence |
| R24 | P2 | Media сохраняются и могут отправляться повторно на protocol repair: больше traffic и дольше lifetime | ModelProtocol / Resources | Один materialized accepted prompt, bounded retry/budget и release в finally; fake image integration pass; проверить реальные media/endpoint budgets до qualification Phase 12 | documented 2A; open |
| R25 | P2 | Provider retry после timeout/потери ответа может повторить оплачиваемую генерацию и увеличить latency | ModelProtocol / Release | Не более двух transient retries на весь step, delays 1s/2s с cancellation; raw ceiling N+3; no Office tool replay, no auth/429 retry; проверить реальные timeout/media/endpoint budgets до Phase 12 | documented 2B; open |
| R26 | P1 | Неполный accepted-run restore или неверная batch projection допустят неверную связь calls / unsafe batch | ModelProtocol / Runtime | R29: runtime IDs/origins и полный preflight. Current policy допускает в одном ответе только независимые локальные reads; каждая mutation — singleton. Batch authority source-owned и не восстанавливается из serialized catalog flags; unknown effect никогда не replay | contained host-neutral; production controller/Office qualification open |
| R27 | P1 | Schema mismatch / UI save могли молча заменить custom prompts или подтвердить старую схему | ModelProtocol / Settings | 4B introduced v4 + Tool Result v1/schema14; 8B advances to schema15 for callable-pack admission; 8C advances to schema16 for durable turn replay. Saved schema15 and older/future markers preserve text during ordinary/failed save; only explicit review/reset approves current instructions. Shared guard remains before preparation/confirmation | fixed host-neutral through schema16; production controller/WebView/DPAPI qualification pending |
| R28 | P2 | Пользователь сообщает об отсутствии live streaming на последнем HEAD; transport / projector / WebView причина не установлена | ModelProtocol / Application / UI | Сохранить progress forwarding при switch 3B2; диагностика существующего SSE → message/reasoning → bridge path, UI qualification Phase 9 и live-provider gate Phase 12 | reported 2026-08-28; not reproduced |
| R29 | P1 | Model-owned tool-call ID вызывал отказ всего ответа и новую генерацию полезного payload при служебной коллизии | ModelProtocol / Runtime | Атомарный v4 switch: kernel выдаёт ID до accepted persistence, сохраняет exact raw attempt/position origin; confirmation/result/replay используют тот же ID. [Evidence](R29_RUNTIME_CALL_IDS.md) | fixed host-neutral R29; original incident payload и Windows/live-provider qualification открыты |
| R30 | P1 | Target Resource/Tool Result contract возвращает CAS `content_ref` как второй model transport либо теряет `ResourceRef` при упрощении результата | Resources / ToolRuntime | Phase 8D переводит list/resolve/search/read на exact native read-only handlers поверх одного gateway, сохраняет bounded data + exact `ResourceRef`; media bytes живут только в request-local materialization, без нового durable transport. [Evidence](PHASE_8D_RESOURCE_DATA_PLANE.md), [ADR-0004](../decisions/ADR-0004-resource-data-plane.md) | fixed host-neutral; Windows/live Office/VBA/media WQ-PACK open |
| R31 | P2 | Встроенный common.prompt_authoring требовал unique model call id вопреки v4 ownership | Prompts / ModelProtocol | 4B: guidance исправлен, schema14/custom review сохранены; targeted skill-content/default prompt/probe checks pass. Runtime IDs/parser R29 не менялись | fixed host-neutral 4B; live skill incident not reproduced |
| R32 | P2 | Диагностика требовала сложной навигации между views/payload; JSON отображался разными read-only renderers, не было общего раскрываемого причинного журнала | Diagnostics / UI | [Phase 9 spec](R32_DIAGNOSTICS_JSON_VIEWER.md): `run-causal` source evidence → общий bounded/lossless viewer → direct latest/exact run journal; no inferred effects, no second durable store. Incident correction: filtered rows no longer turn absence of a loaded terminal row into global `Нет terminal`; metric explicitly stays selection-scoped. [Evidence 9C](PHASE_9C_RUN_JOURNAL_UI.md) | mitigated host-neutral through 9C UI plus selection-scoped terminal correction; Windows/WebView/reload/confirmation/live-append acceptance open |
| R33 | P1 | Exact patch принимал неоднозначный hunk с перекрывающимися совпадениями | VBA | После отдельного 6A: counter проверяет все стартовые смещения (`aaaa` / `aaa` → 2); ambiguity отклоняет весь patch до confirmation/write/нового backup/journal. [Evidence](PROGRESS.md#r33--overlapping-exact-matches) | fixed host-neutral 2026-08-29; 2 regression tests red→green, 8 targeted pass; real Windows/VBE regression pending |
| R34 | P2 | Успешный, но malformed VBA project/module payload мог считаться пустым/недоступным catalog и попасть в минутный cache; raw parsing дублировался | VBA / Tool discovery | 6B: единый typed `VbaReader` валидирует fields/identity/hash/truncation, malformed success прерывает load без partial cache, valid `modules: []` остаётся cacheable; regression red→green. [Evidence](PROGRESS.md#phase-6b--typed-vbareader) | fixed host-neutral 2026-08-29; real VBE/Trust Access/catalog refresh pending |
| R35 | P0 | LLM Markdown очищался устаревшим `DOMPurify 3.1.6`, входящим в уязвимый диапазон GHSA-v2wj-7wpq-c8vv | UI / Security | Отдельный user-requested hotfix: exact upstream `3.4.14`, npm integrity + vendored SHA-256, обе лицензии; `marked → DOMPurify` boundary и CSP сохранены | fixed host-neutral 2026-08-29; headless Chromium malicious-markup checks pass, real Windows WebView qualification open |
| R36 | P1 | Existing UI vendor inventory был неполон: не было единого file manifest/hashes/license texts/transitive asset audit; KaTeX ссылался на отсутствующие fallback fonts, часть icon provenance не отражалась | UI / Supply chain | Exact allowlist 36 runtime files, versions/commits/integrities/hashes/licenses; KaTeX WOFF2-only, Feather source attribution, fail-closed network/worker/WASM policy и admission test. [Evidence](R36_WEB_VENDOR_GATE.md) | fixed host-neutral 2026-08-29; первый новый vendor обязан расширить manifest, real Windows WebView2 qualification open |
| R37 | P1 | Accepted call с Markdown/developer tool-result role мог записаться как `tool.result.recorded`: writer ошибочно связывал semantic call/result с наличием native `ToolCalls`; повторный message upsert мог также дублировать accepted/start/finish causal rows | Persistence / Diagnostics | Current writer классифицирует новый accepted call только по runtime-owned `AcceptedCallOrigin` + `ToolCallId`; result role остаётся независим. Execution boundaries теперь принадлежат переходу состояния exact tool-call/run, а дальнейшие annotations/hydration сохраняются как `message.updated`. Explicit removal decision удалил historical inference: wrong-type retained operation показывается incompatible/reset-only и не входит в tool-execution | fixed host-neutral; semantic boundaries emit once, writer/reader have no compatibility inference, Windows diagnostics UI qualification remains open |
| R38 | P2 | Предпочтённый Web Awesome Tree использует ESM graph, который не регистрируется из текущего `file://` WebView host; скрытый custom bundle или host switch расширил бы build/security scope | UI / Hosting / Supply chain | 9B3: не собирать fork и не менять host; pinned zero-dependency Wunderbaum UMD за bounded local-array `TreeAdapter`, exact manifest/license и zero-network gate. [Evidence](R38_TREE_VENDOR_SWITCH.md) | mitigated host-neutral 9B3 for one consumer; Web Awesome/virtual host, other trees and Windows WebView2 qualification open |
| R39 | P2 | Diff renderer мог скрыто стать вторым diff algorithm или показать UI-generated projection как authoritative mutation evidence | UI / VBA diagnostics | 9B4 gate: проверить все consumers/DTO; Diff2Html admitted только для source-owned bounded unified diff. Текущие before/after consumers сохраняют один legacy bounded formatter без нового vendor. [Evidence](R39_DIFF_VENDOR_GATE.md) | contained host-neutral; vendor deferred, future unified-diff contract and Windows VBA/WebView qualification open |
| R40 | P1 | Restore confirmation guard фиксировал current module, но не exact backup; подмена `backupId` после preparation могла применить неподтверждённый source | VBA | 6G: dedicated guard связывает exact backup id/module/type/canonical live-source hash и current target existence/source hash; mismatch блокируется до journal/dispatch. Raw CAS hash остаётся storage evidence. [Evidence](PHASE_6G_VBA_RESTORE.md) | fixed host-neutral 2026-08-30; real Windows confirmation/VBE qualification open |
| R41 | P1 | Временный VBA package может остаться после применённого session install при потерянном terminal/cleanup; marker-insensitive probe затем считает совпадающий source обычным `installed` и может выполнить его без очистки | VBA packages / Recovery | 6I: один typed package owner; durable lifecycle id записан в install/cleanup и exact session marker. Probe объединяет live marker/source/type с append-only journal, блокирует run/persistent overwrite при незакрытом lifecycle даже после marker strip; recovery read-only, cleanup только fresh explicit journalled Uninstall. Prepared CAS additionally refuses post-prepare source/type/marker drift before install mutation. Terminal/cancellation/restart/cleanup/marker fault cases pass. [Evidence](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md) | fixed host-neutral 2026-08-30; production Windows/VBE/Trust Access qualification open |
| R42 | P1 | Legacy rename guard связывал source только по hash, не по component type, а executor path не передавал cancellation в prepared→dispatch boundary; одинаковый source после type race мог быть переименован вне подтверждённого состояния | VBA rename / Recovery | 6J: rename-specific typed guard связывает оба имени, source hash/type/code-only state и correlation; backend повторно проверяет hash/type до COM. Cancellation проверяется после durable prepare и effect классифицируется before/intended/mixed; terminal loss recovery read-only. [Evidence](PHASE_6J_VBA_RENAME.md) | fixed host-neutral 2026-08-30; production Windows/VBE/confirmation/cancellation qualification open |
| R43 | P1 | `excel.inspect` перечислял workbook collections без общего bound и для defined name читал `RefersToRange.Value2` без cell ceiling; большой named range мог materialize незапрошенный массив и задержать COM/память | Excel read / Bounds | 7B: один typed inspect owner, 200-item/100-series bounds и truncation; defined names возвращают metadata без `Value2`; `read_range` ceiling передаётся backend и проверяется до values/formulas materialization, затем dimensions проверяются повторно domain owner. [Evidence](PHASE_7B_EXCEL_READ.md) | fixed host-neutral; real Excel COM/protected-sheet/large-workbook qualification open |
| R44 | P1 | HTML data bind/refresh вызывал `_adapter.ExecuteTool` напрямую; partial native switch `excel.inspect`/`excel.read_range` оставил бы отдельный legacy result/limits/error path | Excel read / HTML | 7B injects тот же typed read adapter под уже взятым live-document access; switched public IDs не достигают host adapter, второй operation root/fallback отсутствует. [Evidence](PHASE_7B_EXCEL_READ.md) | fixed host-neutral; Windows WebView2/Excel refresh qualification open |
| R45 | P1 | После mandatory append failure controller правильно не сохранял mutated `UnpersistedSummary`, но reconciliation вызывался только при startup; в том же процессе durable open dispatch мог остаться в UI как running до restart | Persistence / Recovery / UI | 9D2: оба controller path release ownership, discard memory и через один `ChatSessionService` reload exact stream/reconcile once. Pre-dispatch pending сохраняется; possible dispatch становится unknown; fabricated terminal, append retry и tool replay отсутствуют. [Evidence](PHASE_9D2_RUNSTORE_RECOVERY.md) | fixed host-neutral 9D2; Windows controller/WebView qualification open |
| R46 | P1 | Broad Office `AppendTrace(type, ...)` не различал authority/diagnostic и mandatory/best-effort; новый consumer мог записать произвольный event type или принять marker за replay authority | Persistence / Diagnostics | 9D3: closed `SessionEventKind` descriptors + один `IEventStore`; storage lifecycle закрыт для port writes, accepted `session.commit`/ToolPack authority отделена от rejected/causal markers, direct Office string append/read удалён, raw `ChatStore` event API internalized. [Evidence](PHASE_9D3_TYPED_EVENT_STORE.md) | fixed host-neutral 9D3; Windows controller/WebView persistence qualification open |
| R47 | P1 | Session/controller/kernel consumers зависели от broad concrete `ChatStore`, поэтому application code мог обойти intended projection boundary, смешать aggregate operations с artifact/CAS/event internals или создать второй write path при следующем изменении | Persistence / Application | 9D4: один минимальный `IConversationStore` + `ChatConversationStoreAdapter` над тем же backend; active aggregate consumers switched atomically, replaced public conversation API internalized. Recovery выражен intent operation, raw lifecycle/CAS не опубликованы; concrete controller access ограничен artifact/HTML revision owners. [Evidence](PHASE_9D4_CONVERSATION_STORE.md) | fixed host-neutral 9D4; Windows controller/restart/multi-window qualification open |
| R48 | P1 | UI собирал outcome из model `ResponseStatus`, flat `RunExecutionSummary` и Activity; verified change/no-change терялись, legacy `ok` мог выглядеть доказанным, а поздний bridge response — заменить более новую историю | Application / UI / Persistence | 9D5: один immutable `RunViewState` из `KernelState` + source-owned effect evidence; unverified success stays unknown, bridge/catalog/message consumers switched atomically, session-revision ordering and stale integration covered. Flat type/getter/fields/readers removed. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral 9D5; Windows WebView/reload/confirmation/live-append acceptance open |
| R49 | P1 | Три host-specific physical files (`DocumentIdentity`, `VbaProjectSupport*`) оставались в host-neutral Office assembly; это противоречит owner boundary и позволяет новым COM dependencies обойти OfficeHosts | Architecture / Host | 10B1/10B2 перенесли exact files в OfficeHosts без aliases/duplicate backends. Скрытая internal marker dependency заменена узким public read-only Office.Vba contract, broad friendship не добавлена; boundary-test запрещает обратных Office consumers. [Audit](PHASE_10A_BOUNDARY_AUDIT.md), [10B1](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md), [10B2](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md) | fixed host-neutral 10B2; production OfficeHosts/VSTO compile, WQ0 и real Windows/VBE qualification open |
| R50 | retired (was P0) | Встроенный runner мог дать ложный pass или затронуть пользовательский документ | Qualification / Application / Host | Qualification Center, packs, helper и in-app admission удалены 2026-09-30; в текущем runtime нет отдельного test executor. Историческое решение: [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md). | retired with removed feature; Windows/Office behavior remains open evidence |
| R51 | P1 | Artifact UI смешивает draft и durable semantics: committed resource может не появиться в library до terminal response, generic upload показывает только metadata, Plan помечен JSON, а `v1`/edit/delete по extension создают ложную модель версий и authority | Resources / Application / UI | 11A1 queues the full committed state before model transport and its live correction queues the same revisioned state after every durable tool result, including confirmation continuation. 11A2 adds one replay-derived server projection for immutable/versioned/derived classes and exact heads/history. 11B1–11B3 establish complete exact Plan lineage/UX. 11C1–11C3 establish one HTML lineage/checkpoint owner, inert exact upload import, binding completeness/integrity and guarded export of one pinned CAS revision without JSON normalization. 11D1 adds exact bounded text/source and complete-only sanitized Markdown viewers over the shared gateway without a second transport. [11A1](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md), [11A2](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md), [11B1](PHASE_11B1_PLAN_REVISION_GUARD.md), [11B2](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md), [11B3](PHASE_11B3_PLAN_HISTORY_HANDOFF.md), [11C1](PHASE_11C1_HTML_LINEAGE.md), [11C2](PHASE_11C2_HTML_IMPORT_PREVIEW.md), [11C3](PHASE_11C3_HTML_BINDING_EXPORT.md), [11D1](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md) | contained through 11D1 host-neutral; live tool-result timing fixed host-neutral, while image/PDF/audio viewers, other committed-resource removal and Windows WebView qualification remain open; WQ/Phase 12 unchanged |
| R52 | P0 | Текущий cross-host выбор чата/документа использует COM/ROT attach: при нескольких Office instances или устаревшем HWND target может быть неоднозначным либо неверным. После bind принятый run закреплён за exact document session, но это не доказывает правильность выбора исходного процесса | Host / Application | Текущий путь: `OfficeHostChatCoordinator` → `OfficeComAdapterProvider` → exact adapter/controller rebind. [Host Fabric](../host-fabric.md) отложен как owner-endpoint registry/lease и typed dispatch на STA владельца без cross-process COM fallback | UI cross-host chats реализованы host-neutral; multi-instance Windows/Office verification и endpoint architecture остаются открытыми |
| R53 | P0 | Browser/files/shell превращают Office assistant в ambient local execution authority; prompt/file/page может расширить доступ, выйти из root, уничтожить данные или использовать Office как application-control bypass | Local Automation / Security | Staged [Local Automation contract](../local-automation-agent.md): prior workspace-session ADR, immutable explicit grants, Resource Fabric reads, guarded/Recycling mutations, browser isolation, typed non-shell process runner and signed isolated worker; raw shell/desktop are separate deny-by-default capabilities | open; design accepted docs-only, no local automation capability is admitted or implemented |
| R54 | P0 | Skill-shaped upload или stale custom package может получить trusted instruction authority без explicit install; flat overwrite/delete теряет provenance/history, а показ skills как chat artifacts смешивает global deletion, context transport и host scope | Skills / Capabilities / Storage / UI | Phase 11 [Skill Library contract](../skills.md): installed package != ChatArtifact, explicit confirmed import, immutable package revisions over core+references, append-only tombstone/restore, exact capability read only, later-run catalog refresh and selected endpoint host scope | open; progressive reads implemented, target history/import/UX accepted docs-only and deferred without expanding WQ-A/Phase 12 |
| R55 | P1 | Release evidence другого build или неподтверждённые сценарии могли быть объявлены квалификацией | Release | In-app admission удалён. Формальный релиз сохраняет signed detached manifest; скрипт проверяет подпись и exact commit/version, а release owner сверяет сценарии и hashes по [release process](../operations/RELEASE_PROCESS.md). | open manual review gate; no in-app pass claim |
| R56 | P0 | Library показывает tool другого host/revision как доступный либо authoring меняет catalog уже принятого run; flat overwrite/delete теряет package provenance и может shadow built-in/skill id | Tools / Host Fabric / Capabilities / UI | Phase 11 [Tool Library](../tool-library.md): сначала read-only selected-endpoint inspector над immutable snapshot, затем exact package history/conflicts, no-shadow, confirmed import and next-run-only refresh after Host Fabric pinning | open; target accepted docs-only, current flat authoring/UI and Windows multi-host behavior not qualified |
| R57 | P1 | Отдельный список проблем мог стать вторым outcome store или скрыть unknown | Diagnostics / UI / Privacy | Proposed Issue Center снят со scope. Текущая [история чата](../trajectory-query.md) читает существующие события, детали и payload открывает по запросу; VBA journal остаётся recovery evidence. | simplified UI delivered; Windows/WebView2 delivery remains open |
| R58 | P1 | Массовая «typed migration» поверх generic `ExecuteTool(ToolCommand)` или unbound/nullable session скрывает старый target/dispatch path, размножает compatibility backends и создаёт ложное ощущение надёжных tools | Office domains / ToolRuntime / Host | 11T0/7D–11T8 switch all current Office families to exact bound sessions/direct typed backends; 11T9A–11T9C and 11J/11K switch VBA/controller/authoring owners. 11T10 deletes `GetBuiltInTools`/`ExecuteTool`, generic catalog, compatibility definitions/results/UI projections and retired fake queues; exact policy/binding is mandatory at snapshot capture. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10; WQ0/WQ-SESSION/host Windows evidence remains open and any failure fixes only the typed direct path |
| R59 | P0 | OpenAI-compatible endpoint может прислать terminal `finish_reason`, но не `[DONE]`/EOF, оставив фактически завершённый model step активным до общего 30-минутного timeout | ModelProtocol / Transport | Streaming reader принимает non-empty `choices[0].finish_reason` как terminal evidence, bounded одну секунду дочитывает optional usage/`[DONE]` и завершает response без inference из model text. Cancellation и общий request timeout сохраняются | fixed host-neutral; open-stream и final-usage regressions pass, real Qwen/live endpoint Windows qualification open |
| R60 | P1 | После direct Outlook switch четыре legacy harness fixture всё ещё ставили queued `outlook.create_draft` result и ожидали generic adapter dispatch; native handler их не потреблял, поэтому full harness был ошибочно красным | Tests / ToolRuntime | 11T10 moved the affected fixtures to direct typed fake state/fault hooks, deleted the retired command/result queue and repeated the complete matrix without restoring Outlook legacy dispatch. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10: full harness 589/589; the original 572/576 result on parent `32a6f7a` remains historical characterization |
| R61 | P0 | Model-facing tool schemas смешивают semantic intent с runtime transport/guard state, поэтому модель или человек в Library вынуждены подставлять URI, UUID, artifact IDs, revisions и cursors; загруженные skill bodies повторяют эту choreography, а flat text-only test form скрывает реальные типы и ограничения | Tools / Skills / Resource Fabric / ModelProtocol / Tool Library UI | Mandatory Phase 11 [all-tool contract audit](../tool-library.md#mandatory-all-tool-contract-audit-r61) и [failure/ownership audit](R61_TOOL_CONTRACT_AUDIT.md): отдельные minimal intent schema и runtime-owned execution context над тем же ToolRuntime; no fabricated opaque values, per-family atomic tool/skill cutover, UI-only built-in documentation, typed Library test controls and reported layout fixes | partially contained host-neutral: 11O1–11O5 switch Resources/Capabilities, planning, HTML, Prompt/Tool/Skill authoring and VBA/macro to semantic intents; installed packages fail closed on unexplained plumbing-shaped arguments. 11O6 finalizes the 21-schema Excel core and moves rename/restore/delete/macro to exact on-demand admission without changing authority. Inventory is 69 unique ids / 72 effective host variants. Library documentation/Test/layout and live-provider/Windows WebView2 WQ-PACK remain open before Phase 12 |
| R62 | P1 | Ошибка после начала основного `SendChat` закрывала activity и flat `LastRun.Status`, но оставляла authoritative `KernelState` в `running`; после bridge refresh UI бесконечно показывал, что модель думает или выполняет действие | Application / Persistence / UI | Main new-run error path теперь, как уже существующий confirmation path, вызывает `AgentRunState.Interrupt` до diagnostic/save/rethrow. Записанные tool counts/effect evidence сохраняются, возможный незавершённый effect остаётся conservative unknown, retry/replay не добавлен. Source-wiring regression фиксирует обязательный terminal переход | fixed host-neutral 2026-09-02; 3 targeted run-view/tool-error/model-failure regressions pass, MockDemo actual-controller compile 0 errors / 3 existing CA1416 warnings; Windows WebView model/tool-error retest open |
| R63 | P1 | После native cutover HTML workspace `common.html_data_bind` / `common.html_data_refresh` брали document gate, но выполняли вложенный Office data-source read вне owner STA; реальный Excel fail-closed возвращал `document_session_thread_mismatch` | HTML Workspace / HostRuntime | Session-aware source callback направляет только вложенный Office read через exact bound `HostRuntime.ReadDocument`; HTML/CAS работа остаётся вне document gate, retry/fallback не добавлен | fixed host-neutral 2026-09-02; bind/refresh bound-dispatch regression red→green, real Windows Excel/WebView qualification open |
| R64 | P1 | Matching x86 PDFium/Skia are now vendored and packaged, but real Office x86 loading/rendering/model-send behavior has not been executed; a loader/export mismatch could still fail only on the target machine | Resources / Attachment routing / Packaging | Never cross-load the x64 pair. Windows x86 qualification must cover text-readable import/extraction, PDF preview, scanned-page rendering, explicit loader errors and image-capable model sending before x86 support is claimed | updated 2026-09-02: PdfPig closure is IL-only `32/64`; exact-package PE32 x86 PDFium/Skia hashes and host-neutral package wiring are recorded; real Windows x86 execution remains open |
| R65 | P1 | В сложной HTML/VBA задаче модель начала mutation до загрузки подходящих skills и исследования source, создала Skill раньше основного решения, вложила лишний `arguments` в вызов write, упростила dashboard после preflight error и завершила run как успешный без binding/verification evidence | Prompts / ModelProtocol / Skills / HTML | Schema 23 ввела readiness, Task List, root arguments, source → primary deliverable → bind/test → requested reusable authoring и evidence reconciliation; schema 24 закрепила это как явный six-stage workflow с completion gate; schema 25 усиливает open Task List как незавершённый финал. Format repair удаляет лишний `arguments`; HTML/Skill guidance запрещает placeholder degradation и преждевременное skill authoring | contained host-neutral in defaults/repair/skill guidance; saved prompts require explicit review/reset, live-provider and Windows Excel/WebView regression remain open |
| R66 | P1 | Full-document HTML preview вставлял workspace JS после внедрения ECharts и мог найти literal `</body>` внутри minified vendor string; ECharts повреждался, browser выдавал SyntaxError, а vendor text отображался вместо dashboard | HTML Workspace / WebView UI | Scripts вставляются относительно closing body/html исходного документа до vendor head injection. Full-document regression исполняет ECharts и workspace JS; зависимость `Dependencies/echarts.min.js` проецируется отдельно как loaded/read-only, а не как mutable workspace file | fixed host-neutral; local Chromium verifies ECharts 5.6.0 + canvas, exact Windows WebView2 preview/export remains open |
| R67 | P1 | Built-in skills и editable prompt layers могли расходиться с текущими tool schemas: host skills не называли точные операции/DoD, HTML guidance не задавал устойчивую multi-file структуру и ECharts/layout/accessibility lifecycle, а drift exact ids обнаруживался только в live run | Prompts / Skills / Tool catalog / HTML | Schema 24 разделяет authority system/skill/tool, задаёт six-stage lifecycle и запрещает terminal scaffolding; schema 25 усиливает finish gate для Task List и live HTML render evidence. Все common/host built-ins получили prerequisites, exact tools, recovery и DoD; HTML требует split HTML/CSS/JS, bundled read-only ECharts, responsive accessible UI and evidence boundary. Harness разрешает каждую capability-ссылку против catalog и pin-ит изменённый descriptor revision | contained host-neutral; saved prompts require review/reset and target-model complex Excel/VBA → HTML plus Windows WebView2 qualification remain open |
| R68 | P1 | `common.html_data_refresh` менял JSON только в transient workspace projection и не создавал новый active artifact; следующий `getHtmlWorkspace` гидратировал старую ревизию, поэтому кнопка `Данные ↻` выглядела успешной, но preview возвращался к прежним значениям. Экспорт также мог молча скачать chart page без runtime, если bundled ECharts не загрузился | HTML binding / Artifact lineage / Export | Refresh после changed reread фиксирует replacement authoritative HTML head без нового Undo step и возвращает exact resource evidence; verified no-change не создаёт artifact. Regression меняет Excel cell и проверяет JSON плюс artifact snapshot/history. Standalone assembly исполняет pinned ECharts и bound `rnassistant.table.v1` до workspace JS, не содержит Office bridge и fail-closed при отсутствующей dependency. HTML skill/tool descriptions задают точную table shape, source→bind→authoring order и refresh/export DoD | fixed host-neutral 2026-09-03; real Excel cell edit → toolbar refresh → WebView2 rerender and downloaded-file ECharts interaction remain open on Windows |
| R69 | P1 | Artifact Library отправляла immutable `rnassistant.chart` artifact в generic JSON preview, потому что ECharts renderer принимал только inline tool activity | Artifact Library / WebView UI | Chart renderer принимает exact artifact JSON; Artifact Detail выбирает domain preview до безопасного JSON fallback, а Details сохраняет точный payload. Cache keys и browser regression покрывают новый route | fixed host-neutral 2026-09-03; real Windows WebView2 Artifact Library preview remains open |
| R70 | P1 | Один Agent run мог создать chart artifact и HTML workspace revision для одной визуализации; карточка ответа показывала `Ресурсы · 2` (`Диаграмма · HTML workspace`), хотя в чате была одна диаграмма | Chat resource cards / Skills / HTML | Run resource cards скрывают supporting HTML workspace, когда в том же run есть chart artifact, но оставляют HTML-only workspace видимым. HTML skill guidance требует выбрать один visual surface и не вызывать `excel.create_chat_chart` для тех же данных при ECharts/HTML dashboard | fixed host-neutral 2026-09-03; real Windows WebView2 resource-card count and target-model behavior remain open |
| R71 | P1 | После успешного refresh JSON bound data обновлялся, но generated HTML dashboard мог оставаться пустым/старым: модель угадывала имя data source или обращалась к Excel label (`Продажи`) вместо canonical key (`продажи`); также run мог завершиться с открытым Task List | HTML binding / Preview / Prompts / Task List | Table transform сохраняет canonical `columns[].key` и добавляет first-row label aliases в rows; assembled HTML добавляет `RNAssistant.data.first/defaultName` и warning fallback при единственном data source; preflight повышает missing data-source reference до error, когда workspace data уже существует. Schema 25 и task-tracking skill запрещают successful final с open Task List | fixed host-neutral 2026-09-03; real Windows WebView2 live refresh/export and target-model behavior remain open |
| R72 | P1 | Empty `tool_calls` мог завершить model loop после промежуточного ответа модели | ModelProtocol / AgentKernel / Persistence | Conversation-response v5 завершает loop только при `final=true` и пустых calls. `final=false` с пустыми calls отвергается до acceptance и получает bounded format repair; повторный invalid ответ не dispatch-ит tool. `final` не является evidence effect/status | fixed host-neutral; live-provider and Windows target-model qualification remain open |
| R74 | P1 | HTML skill guidance accidentally generalized one user example into a preferred dashboard layout shape, and the general completion gate did not explicitly require final read-back plus bug/regression review before handoff | Prompts / Skills / HTML quality | HTML skill is neutralized around page purpose, information architecture, semantic HTML/CSS/focused JS, bundled ECharts only when needed, and content-driven layout with no fixed widget template. Prompt schema 27 requires latest user-visible read-back, likely bug/regression check, in-scope repair loop and explicit fit-to-handoff decision before successful `final=true` | fixed host-neutral 2026-09-03; saved prompts require review/reset and target-model behavior remains open |
| R78 | P1 | User reports an Agent run that said the task was done but continued repeating actions. Screenshot shows different successive writes to `index.html`/`styles.css` and Task List at 1/3. Exact causal sequence is unknown without the trajectory | ModelProtocol / AgentKernel / ToolRuntime / HTML Workspace / Task List | v5 ends only on `final=true` with empty calls; non-final prose may claim completion. ModelProtocol rejects empty non-final calls before acceptance; successful different writes may still continue. A singleton-mutation boundary now rejects batches containing writes. HTML `write_file` was confirmed to replace existing source without model-visible current-source evidence, while context reduction drops the authored source from later write-result frames. Existing-file replacement now accepts a complete source read actually shown to the model in any accepted request of the same run while that file member and its exact content hash stay unchanged. Focused host-neutral tests cover unseen/current/stale replacement, omitted evidence, unrelated file edits and cross-run isolation. Correlate accepted calls/results, source reads, effect records, Task List revisions and compaction boundaries in the affected run | overwrite and source-carry paths contained host-neutral; original loop cause and target-model/Windows/WebView2 behavior still open pending trajectory |
| R79 | P1 | Freshly created HTML files were found, but the model read their exact file targets with workspace-only `structure`; repeated view errors stopped the run | Resource reader / HTML workspace | Model-facing read negotiates this exact mismatch to complete `source`, names the actual representation, and retains exact read evidence. Find/skill usage states which view belongs to root and file; all other unsupported views still fail | fixed host-neutral 2026-09-28; target-model/Windows/WebView2 regression open |
| R80 | P1 | Active Task List could silently lose unfinished stages or permit `final=true` while still open; a Windows screenshot shows `task_list_not_terminal` after an attempted completed close with the list at 1/6, followed by run failure | Task List / Agent completion | Saves preserve goal and prior step text/order; different scope requires `superseded`. Completed close still requires every step completed. Rejected close now names unchanged unfinished indexes; a tool call between premature finals permits correction, while consecutive premature finals fail | mitigated host-neutral; focused harness passes, exact Windows/target-model recovery remains open |

R78 observation follow-up (2026-09-28): a complete read shown to the model in an
earlier accepted request of the same run now remains valid for whole-file HTML
replacement while that file's member identity and exact content hash match.
Unrelated workspace file changes and later context reduction no longer force
repeated reads. A changed file, foreign run or read never shown to the model is
still rejected. The affected Windows run trajectory is still needed to confirm
the exact historical sequence.

R78 context follow-up (2026-09-29): completed mutations were folded without
their exact after-state; compaction could remove the last complete source/text
read. Current published VBA, Markdown, Plan and HTML file source is now carried
as an authority-checked model observation, including across compaction. Older
versions remain folded or stale. Oversized current source fails the request
budget explicitly. Host-neutral tests cover the next HTML overwrite with this
observation; the reported run's historical cause and live model/Windows result
are still open.

Historical R78 follow-up described a `repeated_refresh_required_failure` guard
after three `ConflictNoEffect/RefreshRequired` calls without a satisfying read.
The 2026-10-01 source audit finds no such guard/code in the current runtime;
`AgentKernel` has an identical-unknown-call guard and overall iteration limits.
Do not count the historical statement as current protection. The shared recovery
and no-progress work is open in the current continuity incident above. Model
projection now omits source hashes; exact evidence remains runtime-owned.

## Архитектурный аудит 2026-08-28

Baseline `15dea46` (Phase 3B2). Проверены target contracts и Phases 4–12 master plan против canonical conversation/resource/session/VBA docs и точечных runtime contracts. Это аудит документов и границ, не повторная проверка всего runtime или Windows qualification. R29 — подтверждённый design bug; остальные строки уточняют противоречия/пробелы плана и существующие риски, не объявляют новые runtime failures воспроизведёнными.

| Контур / риск | Найденное противоречие или пробел | Исправленное требование и gate |
|---|---|---|
| ID / R29 | Master §7.1 продолжал представлять model-generated ID как окончательный контракт | Действующий v3 отделён от обязательного runtime-ID correction. Raw response неизменяем, mapping хранится в accepted event; replay не генерирует ID, collision не вызывает model repair после switch |
| Effect evidence / R01, R23 | `WriteOk` invocation count можно было принять за verified writes; policy `verification` не доказывает actual read-back | §§7.3–7.6: typed evidence отдельно от policy/status, no-op отдельно от фактической записи; Phase 4 fake-handler и Phases 6/7 domain cases |
| Batch/control ownership / R26 | Phase 4 поручала whole-response batching ToolRuntime, хотя порт исполняет один call; target v1 не объяснял pending/awaiting-user | Полный batch проверяют ModelProtocol/kernel до первого dispatch, на runtime policy; ToolRuntime сохраняет typed control signals и возвращает record, kernel учитывает/сохраняет один раз. Не считать любой read independent local |
| Resources / R30 | §7.8 предлагал CAS `content_ref`, а §7.4 не сохранял transport для больших результатов | Удалён новый envelope/transport; сохраняются ResourceRef и существующие bounded readers/serializer references. Gates Phases 4/8 |
| ToolPack / R05, R06 | Snapshot по IDs и запрет LRU не определяли изменение handler/policy, compaction и admission limits | §7.7/Phase 8 pin-ят schema+policy+binding, проверяют budget до publication, восстанавливают exact schemas после compaction. Намеренный переход к конечному immutable core pack сохранён, текущий LRU не меняется заранее |
| Host / R04, R09 | Сериализация только writes оставляла guard-read/read-back вне явно заданного критического окна | §7.9/Phase 5: один bound target/reentrant gate для live read/prepare/write/read-back и manual/resource paths, без ожидания LLM/пользователя под document lock; tests гонок/identity/lock order |
| VBA / R07 | Нормализация transport/comparable source не была явно отделена от raw CAS identity | §10/Phase 6: JSON decode один раз, raw bytes и comparable hash различаются; literal backslashes/строки/комментарии не меняют смысл |
| Persistence / R03, R11 | «Разделить events» и «каждый write возвращает terminal result» допускали второй store или выдуманный terminal при сбое persistence | Phases 6/9 сохраняют один chat stream и domain journals, ordered durable barriers и unresolved evidence после сбоя; no replay effects. Actual minimal replay из 3B2 не объявляется полной Phase 9 матрицей |

Также исправлены stale v2/model-owned status и media-repair формулировки в canonical session/conversation docs: они противоречили active v3 и существующей materialized-step lifetime. Последующая реализация должна читать актуальный контракт, а не исторический пример. Diff/локальные ссылки проверяются без build/tests; R28/R29 и Windows gates этим аудитом не закрываются.

## Live report 2026-08-28 — duplicate call ID и streaming

Пользователь указывает последний HEAD (локально `c1628ce`); build SHA на фото не виден. На фото событие `#32 agent.response.rejected`, stage `model.attempt.rejected`, `FailureKind=invalid_model_response`, `Attempt=0`: `Tool call id already exists in the accepted run: call_html_upsert_dashboard. Use a new id.`

- R26 / ModelProtocol Phase 2C3C, ID ownership switch Phase 3B2: источник текста — `ConversationResponseParser`, проверка ID по accepted-run context до dispatch. Этот attempt не выполняет calls и не доказывает ни отказ HTML executor, ни конечный провал run: ModelProtocol допускает bounded repair. Нужны предыдущий accepted call/result с тем же ID и последующие rejected/accepted/terminal events, чтобы отличить повтор ID моделью от ошибки scope/confirmation bookkeeping; защиту не ослаблять и ID скрыто не переписывать. Повтор ID не доказывает повтор той же операции или arguments.
- R28: streaming не отключён фазой. `LlmClient` использует `StreamResponses`; `ConversationStreamProgressProjector` извлекает только root `message` и отдельный provider reasoning, `AssistantWebBridge` передаёт deltas в `app-core.js`. HTML внутри tool arguments не показывается как поток ответа, пустой/поздний `message` может выглядеть как пауза. Это возможное объяснение, не установленная причина жалобы. Для локализации нужны настройка streaming, provider/model, request/response и stream chunk events с таймингами из того же run; отдельно проверить доставку в реальном WebView на Windows x64 + Office + VS 2022.

Уточнение пользователя: после repair получен неполный HTML и показана страница без части кода. В текущем `ModelProtocolClient` repair заново генерирует ответ из accepted history + ошибки, без отклонённого payload; `HtmlArtifactToolExecutor.UpsertFile` заменяет всё содержимое, а `ValidateFile` проверяет путь/размер/тип, не полноту HTML/JS. Это объясняет возможную цепочку, но оба payload и результат записи не предоставлены. Проверка Git: до cutover `dbb8ce1` прежний parser проверял уникальность только внутри ответа; с 2C3C действует accepted-run scope. ID задаёт модель, prompt требует уникальности, schema задаёт непустую строку с description, а локальный accepted-ID snapshot не является ограничением генерации. Host-neutral duplicate tests доказывают отказ, не отсутствие повторов у live provider. Повторное использование ID моделью могло существовать раньше и стать видимым после усиления проверки. Первичная диагностика — ModelProtocol/Phase 2 follow-up; при неверном scope — runtime owner/3B2. Автоматического устранения этого сценария будущим kernel switch план не гарантирует; перед runtime fix нужен отдельный согласованный scope.

R29 зафиксирован отдельным багом, а не допустимым поведением незавершённой фазы. Полный `*.events.jsonl`/trajectory export не предоставлен: точная пара исходного/repaired HTML и корректность accepted-ID scope пока не проверены. Runtime fix, harness, live-provider и Office validation здесь не выполнялись.

## R29 — Runtime должен владеть идентификаторами вызовов

**Текущий статус:** исправлен host-neutral отдельным v4 correction после 3B2 и architecture audit; [точная проверка и ограничения](R29_RUNTIME_CALL_IDS.md). R28 не исправлен. Windows/controller/live-provider gates остаются открытыми; original/repaired HTML пользователя не предоставлены.

- **Исходный дефект:** v3 prompt/schema/parser требовали model-generated ID и run-wide уникальности. ID — служебная связь call → pending confirmation → execution/result → history/trajectory, а не содержимое пользовательской задачи. Обязательность ID в runtime не обосновывает обязанность модели его придумывать. Ранее введённая проверка защищает целостность учёта, но оставляет ненужную причину отказа и полного repair; это ошибка распределения ответственности в контракте.
- **Происхождение и границы:** model-generated IDs существовали до v3; Phase 2C3C (`dbb8ce1`) расширила проверку с одного ответа до accepted run. Новый kernel 3B1 также принимает calls с ID и проверяет повторы, поэтому один switch 3B2 баг не устраняет. Повтор ID не доказывает повтор операции; выдача разных IDs сама по себе также не предотвращает повторение одного действия. Automatic tool retry/deduplication сюда не добавлять.
- **Реализованное исправление:** модель возвращает `message` и calls с `name`/`arguments`; единственный runtime owner назначает уникальный ID каждому принятому вызову после валидации, до записи accepted call, confirmation и dispatch. ID сохраняется один раз и используется для результатов, history/native tool-role transport и continuation; replay не генерирует его заново. Назначать ID только готовому результату слишком поздно. Оригинальные `message`/`name`/`arguments` не требуют новой генерации из-за ID.
- **Scope исправления:** отдельный protocol correction Phase 2 со start/confirmation и kernel consumers Phase 3. Model-facing version, §7.1 master plan, canonical protocol/ADR, schema/prompts/parser и accepted history writers/readers переключены атомарно на v4. Model-ID path удалён без скрытого переименования или dual-contract fallback; несовместимая история — explicit skip/reset без удаления данных. Новая domain/UI feature и реализация Phase 4 сюда не входят.
- **Критерии закрытия:** модель не генерирует ID; последовательные calls и batch получают разные runtime IDs; один call сохраняет ID через confirmation, compaction/replay и смену runtime RunId; results во всех поддерживаемых roles однозначно связаны с calls. Длинный HTML проходит из валидного ответа в tool посимвольно без ID-triggered repair, rejected attempts ничего не исполняют, replay не повторяет effects. Проверки безопасности/schema/arguments сохраняются. Production controller/WebView — отдельная Windows x64 + Office + VS 2022 validation; качество HTML как программы не гарантируется одной этой правкой.

Новые дефекты вне текущей фазы фиксировать здесь или в [BACKLOG.md](BACKLOG.md),
не исправлять попутно. Исключение P0 требует отдельного явно ограниченного изменения.

## Baseline checks isolated during continuity work — 2026-10-01

Clean source archive of HEAD `dfa154e28e0d90c4195f01fdab6dd4243e70b13c`
reproduces three failures also seen with the continuity changes:
`PromptResourcesReadPublishedTemplates` (50 mutable settings loads, expected 0),
`PromptResourcesFailClosed` (8 loads, expected 0), and
`ResourceSchemaMappingDerivedPublication` (old copied workspace restore fails).
Owner: catalog/settings read boundary and resource fork/restore. Investigate as
separate slices; do not waive these tests or label the full harness green. No user
history or copied artifact was deleted to make the checks pass.

## Excel structural deletion and false cancellation labels — 2026-10-15

Owners: Office Excel deletion domain/bound backend; Core execution metadata and
Office/Web presentation. A screenshot showed `clear_range` repeatedly targeting
an extra table header, with runtime replay refusals displayed as cancellations.
The cancellation mapping is reproduced in code and corrected with explicit
`WasCancelled` metadata. The exact original COM failure cannot be established
from the screenshot alone.

`excel.delete_rows_columns` now supports explicit table/worksheet scopes. Table
header clear refuses before dispatch and points to structural deletion. Target
preparation survives approval; drift and possible partial effects never authorize
replay. Five focused fake-backend scenarios pass, including the seven-to-six
column example, both axes/scopes, invalid selection, drift, divergent/unavailable
read-back and confirmation state. Cancellation projection and Web checks pass.

**Open Windows/Excel evidence:** the new COM path was not built or executed here.
Verify table column/row deletion with totals, hidden headers, last data row,
formula reference rewriting, nearby data, worksheet shifts, protection, bound
workbook switching/closure and partial COM failure. Cell/structure read-back is
not qualification of formatting, charts or dependent formula semantics.

The full R61 inventory check still reports seven Common catalog differences
outside this change: `common.file_create/copy/patch`, `common.resources_search`,
`common.resources_outline`, `common.resources_read` and `common.skills_load`.
The updated Excel rows match the captured catalog. Owner: Common catalog/property
inventory maintenance; review those contracts before refreshing their baseline.
This task does not alter those Common contracts.
