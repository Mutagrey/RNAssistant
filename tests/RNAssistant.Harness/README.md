# RNAssistant Harness

Data Templates M0/M2 is JavaScript-only: run `node tests/web/tabular-core.test.js`,
`node tests/web/tabular-matrix.test.js`, `node tests/web/tabular-universal.test.js`,
`node tests/web/tabular-xlsx.test.js` and
`node tests/web/tabular-browser.test.js` (Playwright, optional
`BROWSER_EXECUTABLE`). They cover bounded selectors, immutable capture, Worker
cancel/deadline/reload, viewer-independent lifetime and offline file:// parity.
Matrix checks add an independent 16-record oracle, typed expressions/plans,
scoped mappings, decimal/missing semantics, source/rule lineage, bounded batches
and consumer backpressure/failure. Universal checks compare normal/transposed
matrices, record fields and nested JSON against an independent four-record oracle;
they cover native provenance, exact scale/round/JSON numeric tokens, input pull pages,
conflicts and result-sink completion/abort. Raw CSV/JSON example files do not imply
production file-reader readiness. Browser checks cover all source shapes, batch acknowledgement,
cancellation and offline matrix parity. XLSX checks cover a real selected file,
exact numeric lexemes/formula-cache evidence, streamed ZIP/XML, CRC/size/schema
refusals, bounded capture across 30,000 rows and existing SheetJS writer
interoperability. The browser checks XLSX Worker transfer without UI body reads,
byte progress/cancel/rerun and explicit refusal of cooperative execution. These
require no Harness build and do not qualify Office capture, representative 80 MB
workbooks, Windows/WebView2, the application processing queue or publication.

`node tests/web/tabular-byte-source.test.js` checks bounded exact byte reads, buffer
ownership, single flight, pending-read revocation and malformed replies/cleanup.
`node tests/web/tabular-source-browser.test.js` uses real Workers and a test-only
binary endpoint: the XLSX oracle from a virtual 80-MiB source transfers less than
1 MiB, with 256-KiB range bounds, cancellation/deadlines, late data, protocol guards,
preflight-before-read and staging abort on failure. XLSX tests also cover a ZIP
directory larger than one transport chunk. No C# build is needed; these do not
qualify native source transport/module pins or representative 80-MB Excel throughput.
After vendor changes run `node tests/web/vendor-gate.test.js` for hashes/licenses.
The scope and manual probe are in [Data Templates](../../docs/data-templates.md).

`workspace files:` includes an actual 80-MiB source retained/read without a
whole-body allocation, exact history after drift/deletion/restart, operation
budgets, busy-file cancellation and missing/corrupt/unpublished source refusals.
`storage: source streams`, `storage: file CAS` and `storage: plaintext CAS` cover
stream ownership, cancellation, compression/encryption and envelope-prefix
compatibility. These are storage checks; they do not qualify large Excel parsing,
queue throughput, UI latency or Windows/Office behavior.

Data Templates shared manifest storage: filter `tabular project:` checks
create/reopen/read-only and workspace relocation, closed metadata/path refusal,
external drift/foreign workspace, exact retained bytes and unknown write recovery.
Source declaration checks cover stable IDs across restart/reordering, empty selections,
closed schemas and count/byte bounds, missing paths and unknown-save recovery without replay.
The owner is the existing `WorkspaceFileService`; these tests do not qualify the
future Office UI, capture or template publication. The existing CLI exposes
`project create/open/sources/prepare` for process-level smoke checks without a model or Office.

`workspace application:` checks selection/permission binding, refusal projections,
controller-independent lifetime, file dispatch off the UI callback and typed
unknown/reconciliation through the real bridge with a fake controller. Source saves
require the selected manifest/revision and revoke old pins before writing; stale saves,
unfinished queues and unknown-write recovery are covered. It also
excludes runtime clear during file dispatch/read-back and revokes selection after
an explicit reset. Source queue coverage adds serialized capture, 50-item pages,
search/filter/sort, pause/cancel/explicit retry, byte budgets, exact retained reads,
project drift, maintenance exclusion and disposal drain across controller changes.
Metadata browsing checks add stable 50-entry pages, directory navigation, protected
names, cancellation and stale bindings through the actual file owner/bridge without
capturing bodies. These are preparation/lifetime checks, not parser qualification.
CLI `project prepare` accepts saved declarations or an explicit JSON array of paths and
file/batch budgets; process smoke covers mixed outcomes and read-only capture without
manifest changes. Browser
`node tests/web/workspace-projects.test.js` checks the shipped UI/CSS with controlled
replies: single flight, readonly/stale binding, inert metadata, pinned-path explicit
inspection, malformed receipts and narrow light/dark layouts. Use the usual
Playwright `BROWSER_EXECUTABLE` override. These do not qualify Windows/Office.

`node tests/web/workspace-sources.test.js` checks the shipped chooser/queue UI with
controlled bridge replies: paged selection and explicit budgets, independent current
progress, filtering/paging, pause/cancel/retry, remount, invalid/lost reply recovery,
late context results and narrow/wide light/dark layouts. `RNA_DATA_SCREENSHOTS` saves
previews. It also covers persisted composition, bounded selection navigation/removal,
save/revert/empty selection and lost/mismatched save replies with explicit pinned-path
inspection. These checks do not qualify native WebView2 access or Excel parsing.

`node tests/web/data-templates.test.js` checks the loader's real browser file/folder
selection, drops and directory batches, cancellation, stale-context callbacks,
atomic enumeration limits, inert names, paged selection/filtering/sorting and keyboard
navigation. It checks progress coalescing, unknown totals, view remount during
enumeration and an 80 MiB sparse metadata-only selection with body reads forbidden.
All views are checked at narrow/wide widths in light/dark themes. Optional
`RNA_DATA_SCREENSHOTS=/absolute/path` saves visual previews. This metadata-only
slice needs no C# build; it does not qualify parsing or Windows/WebView2 file access.

`chat relocation:` covers occupied destinations, blocked parent creation, invalid
projection rollback, deletion while waiting for the document lock, explicit retry
and transient drafts. It checks unchanged event bytes on refusal and history after
cold reload. `chat sessions:` and `storage: stale chat save` cover document
rebinding consumers and optimistic concurrency. These do not cover crashes or I/O
failure between the metadata append and physical rename, or real Office Save As.

`kernel: read inspection after unknown effect` and `kernel: unknown inspection`
cover premature completion, failed inspection, unrelated writes, replay guards,
iteration limits and restored read policy. `agent: Excel unknown` reproduces a
clear-range read-back mismatch through the production conversation with a fake
host, rejects empty completion and blind replay, then reads the affected range
and completes remaining work. It covers both response formats and tool/user result
roles with custom prompts. These checks do not qualify real Excel or model behavior.

`storage: metadata conflict preserves stale revision` checks title/model/reasoning
conflicts through the conversation port. A refused write cannot adopt a foreign
revision and authorize a later stale commit that removes an artifact or overwrites
settings; exact journal bytes and successful write after explicit reload are checked.

CAS publication integrity: `storage: CAS supplied` covers explicit restoration of
the same artifact after raw/compressed and plaintext/encrypted corruption.
`storage: CAS original` rejects damaged original/extraction parts before retaining
metadata. `workspace files: corrupt` covers move/delete/restore refusal before
dispatch and an explicit retry after source repair. `shared context:` rejects
corrupt dependencies before snapshot publication and filters raw/already projected
model claims. Existing `storage: unchanged artifacts`, `document originals:`,
`artifact transfer:` and `shared HTML: text discovery` cover reuse, publication,
copy/restart and member reads. No real-model or Office qualification is implied.

Confirmation input: after a host-neutral MockDemo build, run
`dotnet demo/RNAssistant.MockDemo/bin/Debug/net8.0/RNAssistant.MockDemo.dll --confirmation-input-test`.
Ten controlled cases cover user/developer/tool result roles, native images,
unchanged/changed analysis helpers, two approvals with restart and image steering.
Every primary request checks freshly loaded analysis; helper and mutation counts
reject repeated work. Existing `--unified-agent-test`, `--inbox-test`, Harness
`kernel replay:`, `protocol context:` and `storage: runtime commits` cover adjacent
failure/recovery boundaries. `inbox:` also checks bounded terminal receipt lookup
and replay; `bridge: typed input` checks its addressed DTO. Web
`chat-inbox.test.js` covers local/remote removal, late acknowledgements, stale
receipts and history ordering without redundant queue-only history fetches.
These are fake-host/model checks, not Windows/Office qualification.

`storage: CAS` covers misplaced/renamed published and unpublished resource journals,
file/directory links and supported sibling cache/metadata paths.
`resource cutover: frozen evidence` and `context continuity:` cover removed heads,
archived diagnostic provenance, accepted operation targets, bounded optional
materialization and inline/CAS parity after checkpoint/save/restart. The controlled CLI `compaction_resume_smoke.py` checks
the Runtime consumer through process loss, external drift and same-run recovery.

`authored file: HTML viewer and revision history` also round-trips prompt targets
through the real resolver before and after UTF-16 restart. `artifacts: prompt`
covers the shared metadata projection. `resource cutover: retained whole and line
reads materialize once` checks one retained read, exact excerpt evidence and no
cross-operation cache. `context continuity: selects active and archived bodies
once` covers projected body cost, zero-budget/no-CAS admission and active/archive
deduplication, diagnostic-only evidence, missing active payloads and frozen
`DeliveredObservations`; `resource cutover: current source` covers writes and
compaction. `tests/cli/compacted_read_guard_smoke.py` exercises real automatic
compaction followed by a guarded edit for tool/user result roles;
`omitted_read_guard_smoke.py` verifies that excluded bodies cannot authorize edits.
`bridge-bootstrap.test.js` applies real init/chat projections across Office host
switches and reset, with a separate v2 handshake and invalid-bootstrap rejection.

Architectural simplification: `storage: runtime commits`,
`authored file: HTML viewer and revision history`, `agent: native read batch`,
`plan mode: native question` and the three `agent:` resource-media cases cover the
explicit persistence boundary. `bridge:` covers contract version, cancellation and
typed routing; `tests/web/bridge-bootstrap.test.js` uses controlled timers for lost
and late replies without replay. The nine domain slices (`excel write:`,
`excel find replace:`, `excel sheet:`, `excel range mutation:`, `excel table:`,
`excel chart:`, `word tools:`, `powerpoint tools:`, `outlook tools:`) cover the shared
outcome contract through real domain services and fake backends. Confirmation
replay, protocol-context and causal-trace fixtures select the actual authoring/VBA
skill before mutation. Startup recovery records a typed execution boundary;
UI activity labels cannot manufacture one.

`storage: damaged sqlite metadata preserves startup and history` checks ten
sidecar corruption variants through catalog, load, pagination and startup recovery,
plus a healthy chat beside malformed JSONL. Every rebuild preserves event bytes.
`live-run-projection.test.js` checks folded progress after completion/failure/steer
and restores every original comment on expansion/reload.


VBA patching incident: `skill packages: VBA schema inspection does not loop`
exercises the production conversation through project/source reads, skill-only
discovery before and after selection, and one verified fake-host patch. Discovery
does not grant access or duplicate the schema into history. `model diagnostics:`
covers JSON/SSE provider errors after answer deltas; `agent: model projection hides
runtime evidence` checks capability projection. Web checks are
`completion-guard.test.js`, `agent-activity-layout.test.js` and
`run-changes-layout.test.js` (the latter two use Playwright). `vba: fresh read releases
rejected model write` covers external drift and an identical retry through the
production conversation, not just manual tool invocation. `vba: patch uses live
state and whole write returns recovery` checks that diagnostic source alone cannot
release the failure. `plan mode: native question` checks terminal pairing and
answer continuation after reload in both result roles; `plan mode: question
presentation` covers externalized and unavailable question data.
`tool-result-presentation.test.js` uses Playwright to verify exact question loading,
visible missing-body recovery and submitting the answer. These do not qualify
Office or autonomous model behavior.

Office chat navigation: `node tests/web/office-host-chat.test.js` covers queued
creation for all four hosts, duplicate clicks, visible progress/refusal/error,
preserved binding after failure and target initialization.
`office-host-chat-layout.test.js` uses Playwright with shipped CSS/handlers to
click PowerPoint, observe queued progress/error, check narrow light/dark error
wrapping, and explicitly retry into target initialization. Use `BROWSER_EXECUTABLE`
for an installed Chromium browser. `chat-sync.test.js`
covers navigation/catalog ordering; `bridge: Office chat` covers the typed bridge.
`chat sessions: Office host row` checks empty-chat persistence for all four hosts
and refuses current-document selection of a different host's chat.
`skill packages: built-ins` selects every visible built-in against each host's
published catalog and rejects unsupported Outlook VBA procedures. Tool/backend
slices are `powerpoint`, `word `, `excel ` and `outlook `; the trailing space narrows
the latter three. These use fake hosts. Cold COM activation, retained STA/RCW
lifetime and real cross-host panel switching still require Windows qualification.

Resource-workflow regression: after building MockDemo, run
`dotnet demo/RNAssistant.MockDemo/bin/Debug/net8.0/RNAssistant.MockDemo.dll --resource-workflow-test`.
This uses the actual controller with a fake Excel host: create/read Library sources,
Task List, structured compaction during pending approval, four approval/restart
boundaries, preserved selected skills, HTML patch/full replacement/identical write,
exact effect counts, immutable editor downloads, new-turn continuity and explicit
selection removal. The Harness filters `skill packages:`,
`html tools:`, `shared HTML:`, `tools: discovery is complete and exact`,
`vba: confirmed mutation rejects stale snapshot` and `vba: patch` cover its owners.
`resources: HTML semantic reads preserve workspace snapshot` checks working versus
explicit historical targets, sibling/no-op/delete/recreate and restart.
`resource cutover: HTML write publishes current member sources` checks per-file
evidence, including equal-byte restore. `vba: large stale patch recovers from
relevant lines` checks the typed recovery route.
`tools: manual read-only run skips chat lease` checks that a read in another
session does not authorize stale delete/rename or alter exact patch matching.

The opt-in real-model fixture runs through the production conversation/runtime:
set `RNA_BASE_URL`, `RNA_MODEL`, `RNA_RESPONSE_MODE` and `RNA_CONTEXT_TOKENS`, then run
`dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll --resource-workflow-live /tmp/new-evidence-directory`.
An optional `RNA_REQUIRE_TASK_LIST=true` explicitly requests task tracking; keep it
separate from the unprompted-selection case. `RNA_API_KEY` is optional and never
saved. The fixture retains synthetic before/after sources, requests/responses,
settings, session and store, and grades HTML/CSS/JS/VBA against exact expected text.
Capture the verified model tag/digest and endpoint profile with the evidence.
This fixture uses automatic approval; the controller regression covers manual
approval/restart. Neither qualifies Office/COM or rendered WebView2 behavior.

Skill-package checks: after the normal host-neutral Harness build, filter by
`skill packages:`, `tool pack:`, `skill editor:` and the two `agent:` prompt cases.
The package slice covers selection, next-request exposure, exact dispatch, shared
dependencies, version conflicts/revocation, budget, compaction-independent state,
restart/new-turn selection, fresh authority and inert UI metadata.
`chat sessions: interrupted mixed` covers completed/open exchanges in three result roles, serial
and batch ordering, before dispatch and in flight, with idempotent recovery.
`tool pack:` includes the production HTML skill
loader/local-write flow with a fake host. `agent: new skill and tool are readable in
same run` covers publication, source/schema reads, selection and next-request
exposure after creation. After building the CLI, run
`python3 -B tests/cli/skill_packages_smoke.py` for the controlled HTTP pilot with
two approval restarts, follow-up without skill/source rereads, and persistent
explicit removal, permission restriction and refusal to reuse a prior write guard.
`interrupted_run_smoke.py` kills a process after a completed read batch and checks
retained source bodies, external drift and no automatic replay. These make no
real-model quality claim.

`node tests/web/skills-contract.test.js` and
`node tests/web/skill-editor-resource.test.js` cover manifest transport and editor
preservation. `tests/web/agent-arsenal.test.js` uses Playwright/Chromium with the
shipped HTML/CSS. Set `BROWSER_EXECUTABLE` if the browser is not bundled and optional
`LAYOUT_SCREENSHOT=/tmp/arsenal.png` for light/dark screenshots. It checks narrow
panes, full scrollable tools, tabs/search/focus retention, restrictions and stale
chat responses. These checks are not Windows/Office/WebView2 qualification.

Unified-agent checks: `unified policy:` covers the permission matrix, prohibition
before preparation, forged planning bindings, retired history byte preservation,
and native Office-adapter confirmation pinned across setting changes/restart.
`auto confirmation:` covers typed settings and default-off snapshots, dynamic code,
preparation/refusal/cancellation, automatic multistep execution, next-run disable
and unknown-effect non-retry through the production runtime with fake hosts. Also use
`tool runtime:`, `kernel:`, `no-tool`, `plan mode:`, `conversation: stream`, and
`storage: pre-cutover` for the corresponding changed boundaries. These are
host-neutral, not real Office qualification. After a CLI build,
`python3 -B tests/cli/unified_agent_smoke.py` checks direct answers, reads, per-call
approval/rejection, auto-confirm multistep writes, captured policy/restart,
unread-file guards, next-run defaults, invalid flags, policy changes, Plan
persistence, document injection under Deny and emergency disable.
`tests/web/chat-composer-layout.test.js` checks the setting's default, save/reload,
reset and composer status alongside the existing responsive layout.
`turn_decisions_smoke.py` checks both response
formats/result roles. The former Chat-only and implicit-auto-confirm assumptions
are not the new contract.
`mode_questions_smoke.py` verifies Plan/Agent typed questions across processes.
`dotnet run --project demo/RNAssistant.MockDemo -- --unified-agent-test` exercises
the actual controller with a fake host, including permission snapshot cloning,
approval after settings changes/restart, AutoConfirm and planning wrappers.

`conversation v7:` checks batch policy in both parser and actual HTTP response
schema: authorized reads, singleton effects, mixed batches, contradictory policy
and the 32-call bound. `protocol context: immutable complete snapshots` also
rejects schema generation with missing/incomplete authority. Request-size and
tool-pack checks cover schema projection and admission with both array branches.

`no-tool` checks distinct terminal outcomes, rejection and frozen repair of the
removed `continue` action, and resuming tool work only after an actual user reply.
It also checks missing-call repair followed by either a complete code answer or a
real tool step/result before that answer, in both response formats and result roles.
These scripted responses check control flow and emitted guidance, not whether a
real model will select `done` at the right time.
`kernel: rejects narrative checkpoint` and `protocol preflight:` reject old
checkpoints/history without migration or dispatch. After building the CLI,
`python3 -B tests/cli/turn_decisions_smoke.py` checks production HTTP requests in
both result roles and response formats; `mode_questions_smoke.py` checks typed
question/answer persistence. These are controlled host-neutral checks; they do not
qualify real-model task completion or Office delivery.

`python3 -B tests/cli/compaction_resume_live.py --base-url http://127.0.0.1:11435
--model <verified-model-tag> --digest <verified-digest>` runs the separate real-LLM
E-CONTEXT/E-RESUME fixture in a fresh temporary workspace. Start/probe the real
local endpoint first and retain its model/profile and memory evidence; use a
profile with at least 24K context. The runner plans with 24K/4096 and fixes the task
at 40 iterations / 48 tool steps / 600 total seconds across both invocations.
`--cli <dll> --source-root <checkout>` pins an already verified binary and its
matching source for comparisons; the evidence retains binaries, a source archive
and the working-tree patch. API keys stay in `RNA_API_KEY`, not arguments.
The runner accepts explicit `--response-mode`, `--instruction-role` and
`--thinking`; defaults remain `json_schema`, `system`, and `off`. Its audited
tool-result role remains `user`. For the unattended v7 fixture, pass `--auto-confirm on` to
authorize the disposable workspace writes; the default remains `off`, matching
the CLI. This choice is captured at run start and retained across resume. These
profile options do not change the task, source checks or 40/48/600 limits.
This is not a replacement for fixed CSV acceptance.

`python3 -B tests/cli/response_mode_smoke.py` also checks that the initial HTTP
instructions carry the raw call envelope in both formats, all three result roles
preserve call/result pairing, and a manual approval retains its accepted profile.

The task changes only release-note headers while retaining source bytes. An HTTP
proxy forwards exact real requests/responses and holds one request after a
published checkpoint, before provider dispatch. The runner kills only its own CLI,
edits one observed file externally and calls explicit same-run continuation.
Task/final-byte grading remains outside the workspace. Evidence includes original
files, binary hashes, canonical HTTP/CAS, exact task, prefix continuity, call/result
pairs, counters, limits, external-edit preservation and a receipt that must not be
rewritten after restart. Invalid model summaries remain failures; the runner
does not generate claims or modify accepted history. A run that never publishes a
checkpoint leaves real resume untested. This live case currently uses `user`
tool-result role; the controlled test below also covers native `tool` pairs.
The reserved `.rnassistant/workspace.json` is recorded before the first provider
dispatch and must remain byte-identical. Only that unchanged runtime manifest is
excluded from extra-file grading; unexpected files and changed metadata fail.
Counter auditing counts the fixture's dispatched workspace steps separately from
accepted calls: typed `NotDispatched` repeat denials close exchanges without a
tool-budget charge.

`readback-order-audit.json` independently grades the task's prescribed sequence.
For each note, a complete CAS-backed read must match the verified mutation's
before revision; a separate complete resource read must match its after revision
and expected bytes before the first accepted call to the next note. Internal
mutation read-back, `ok` prose, partial reads and a late final read batch do not
satisfy that order. The anchor review requires a fresh read after all eight notes.
Only the runner's recorded marker-only external edit can admit a newer read-back
revision at the saved restart boundary. Missing/corrupt evidence fails explicitly.
The order audit runs on every exit, including timeout or completion before a
checkpoint; `readbackOrderPassed:false` prevents overall acceptance. It does not
replace canonical continuity, final-byte or counter checks.

`python3 -B tests/cli/compaction_order_smoke.py` checks this grader with synthetic
canonical operation/CAS fixtures: ordered and native-role reads, the recorded
external edit, late/prefetched reads, missing before evidence, unverified/unknown
effects, unchanged/stale revisions, partial/missing bodies and broken correlation.
It needs no build or model. These are grader unit checks, not authenticated stream,
production runtime or real-model qualification. Retained real logs can be graded
read-only with `audit_readback_order(root)`; keep their original result files intact.

After building the CLI, `python3 -B tests/cli/compaction_resume_smoke.py` checks
24K context pressure through actual structured compaction, process loss, external
file edits and same-run continuation. It verifies checkpoint persistence, exact
user task, stale-claim exclusion, read-before-write guards, final read acceptance,
original counters, no repeated effects and native pairs in `user`/`tool` profiles.
This uses controlled HTTP claims, not real-model compaction quality. Run
`compaction` for shared checkpoint/publication regression and durable batch
boundaries. It also checks actual source-bound compaction schemas, both explicit
response modes, schema budget, mixed provenance and unknown-source rejection.
Completed receipts keep correlation without blocking a safe checkpoint as if they
were unanswered calls. Original incomplete calls/batches still block that boundary.
`context continuity:` checks source/result projection. The omitted-read
CLI smoke also uses 24K: it admits the current catalog while omitting the oversized
body, and still requires the original bytes to survive a refused replacement.
The bounded-working-set case also checks recovery chronology: an earlier success
or later failed read cannot release a failure; a matching later success can do so
even when excluded from the prompt.

`context continuity: narration survives compaction and restart` covers independent
assistant provenance through repeated-read folding, empty updates, two actual
compaction-helper requests, checkpoint save/reload and claim-promotion rejection
in all three result roles. Claims are scripted; this proves transport/provenance,
not real-model summary quality. The large-call archive case also retains exact
narration without hydrating arguments. `settings: prompt`, `conversation v7:` and
`no-tool` cover schema-44 migration, silent tool responses and shared default/schema/
repair guidance. `tests/web/live-run-projection.test.js` verifies that one comment
can cover several visible tool activities in live and restored history.

After building the CLI, `python3 -B tests/cli/event_subscription_smoke.py` checks
bounded `events --follow` pages across real processes, snapshot continuity, Ctrl+C
isolation from an active run, timeout and explicit corruption resync without replay.
The `event feed:` harness filter checks bounded paging without full history
materialization, unchanged-file polling without rescans, changed-prefix/torn-tail
rejection, callback backpressure and storage-lock release before delivery.
These use controlled HTTP/canonical temporary stores, not a real LLM or Office.

After building the CLI, `python3 -B tests/cli/interactive_smoke.py` checks real Mac/
Unix PTYs and redirected input with controlled HTTP: typed single/multiple/free-text
answers, invalid input, leave/restart, retained native result role/context/limits,
prior-question effects, explicit approval/denial, Ctrl+C, an external edit during
approval, non-interactive/JSONL behavior, bounded terminal narrative and the latest
32 canonical file operations. Failed/no-change calls and model claims cannot
populate changed files. It does not qualify Windows terminals or real-model quality.
`python3 -B tests/cli/interactive_smoke.py --browser-only` checks that the presented
saved browser result matches exact snapshot/record IDs while stale current
acceptance still fails. It verifies an already-correct counter fixture and does
not replace the separate verify/repair/reverify acceptance scenario.

After building the CLI, `python3 -B tests/cli/resource_preview_smoke.py` checks
bounded descriptor discovery, metadata without decoding, inert source fragments,
exact Unicode continuation across external edits, missing-payload refusal,
path confinement and absence of model write authority from UI reads. Real
Chromium checks cover immutable preview routes, historical reopen after live
move/deletion, deadline/Ctrl+C profile/server cleanup, missing-browser reporting,
fresh storage, absent native bridge, sandbox headers and blocked off-origin
requests/navigation. Preview creates no run or verification record. The controlled
model is used only to check a refused write after a UI read, not task quality.
Run `web_checks_runtime_smoke.py` when changing their shared browser runner to
preserve functional verification, repair/retry and stale-final rejection.

After building the CLI, `python3 -B tests/cli/browser_lifetime_smoke.py` checks
abrupt caller loss on Mac/Linux (qualified on Mac): real Chromium after preview
open, a controlled process before DevTools readiness, and a real verification
interrupted during navigation. It asserts helper/browser/descendant/profile/server
cleanup, a held-pipe safety deadline, another live viewer's isolation, retained
snapshots, and `Pending` without invented diagnostics or replay. It touches only its own launches and isolated temp
roots. This does not cover losing the helper itself or the machine. Run browser
suites sequentially; `resource_preview_smoke.py` also checks global temp-profile
leaks. No real-model or Office qualification is implied.

After building the CLI, `python3 -B tests/cli/run_control_smoke.py` checks local
cross-process cancel and explicit same-run continuation: exact cursors, late model
response exclusion, unchanged limits/counters/history, retained verified writes,
stale/settings conflicts, active lease exclusion, owner loss, consumed abandoned
iterations and cancellation after a confirmed deletion. It uses controlled HTTP,
not a real LLM, and checks reopened turn segments and synthetic closure of abandoned
model steps in canonical events. Run `kernel:` for the shared boundary: stopped checkpoint accounting,
no replay/unknown-effect continuation, stale append claims and retained no-progress
limits, alongside existing cancellation and confirmation tests. Plain non-executing
resume remains covered by `tests/cli/interrupted_run_smoke.py`. Windows/Office
control transport is not qualified by these Mac checks.

After building the CLI, `python3 -B tests/cli/application_requests_smoke.py` checks
typed SubmitInput and snapshot/event reconnect through controlled HTTP waits:
active/completed/older/pre-delivery/crashed duplicates, conflicting request IDs,
offline receipt lookup, no extra model/file effects, canonical contiguous pages
with pinned high-water, projection rebuild and explicit resync for invalid,
cross-session, replaced or incomplete history. It verifies that duplicate intake
does not close an abandoned run; explicit resume still owns that transition.
`python3 -B tests/cli/mode_questions_smoke.py` also checks duplicate/conflicting
typed answers. For shared input persistence, run the `inbox:` and
`storage: typed event port preserves classified wire` harness filters. These are
scripted/host-neutral checks, not real-model or Office qualification.

Office typed answers/receipt conflicts and CAS questions across restart are covered
by MockDemo `--unified-agent-test`; `--inbox-test` covers steer and shared controllers.
`tests/web/chat-inbox.test.js` verifies typed retries and composer preservation;
`tool-result-presentation.test.js` verifies addressed cards and typed answer IDs.
`storage:` exercises explicit conversation writes/removals and replay; its semantic
operation tests use runtime commits, not the test-only bulk fixture `Save` helper.
The latter only seeds/upserts fixture messages and cannot infer tool lifecycle.

`python3 tests/cli/skills_runtime_smoke.py` checks the production CLI with a scripted
HTTP model: metadata-only discovery, Common/Workspace and enabled filtering,
built-in precedence, exact core/reference CAS reads after authoring drift/deletion,
native call/result pairing, no file authority/count from a skill read, file repair
and continuation in a new process. It is contract evidence, not LLM qualification.
For changes to the shared publication owner, use `skills:`, `skill editor:` and
the `resource cutover: ... catalog ...` tests for immutable publications, exact
continuations and missing/corrupt snapshots. No Office/VSTO build is required for
these host-neutral checks; real Windows/Office behavior remains a separate gate.
`tests/cli/skill_file_task.md` is the narrow real-model skill/create/read fixture:
use a fresh workspace with `--expect-files note.txt --min-reads 1 --min-writes 1`,
and additionally audit an accepted complete skill body in the actual HTTP context
and exact `note.txt` bytes. It does not replace the autonomous CSV task/follow-up.

The independent CLI's optional real-browser check is
`python3 tests/cli/web_verifier_smoke.py` after building `RNAssistant.Cli`. It uses
a temporary workspace and fresh Chromium profile; it is not an Office/WebView2
qualification or an agent functional grader. It checks the exact snapshot digest
with BOM/CRLF and rejects a missing retained CAS payload before browser launch,
even when the workspace file still exists. Focused `workspace files:` tests cover
the identity/lease race through metadata, UTF-8 and raw reads, with an absent or
reused source path after a managed move. Deterministic interleaving also verifies
that stale-path recovery cannot resolve an interrupted write at the new path.
These cases check unchanged authority generation, preserved bytes/history,
released leases and explicit recovery after restart, without timing sleeps.
The same filter covers retained provider evidence after edit/deletion, empty text, unpublished revisions,
corrupt/missing payloads and the existing mutation/recovery guards.
`workspace files: exact raw and image views` additionally covers shared identity
across text/raw, binary drift, snapshot reads after deletion/restart, foreign refs,
publication barriers, CAS GC/corruption, size bounds and unresolved effects.
The `binary_resource_smoke.py` acceptance cases also assert browser-state metadata
with no read minimum: an externally changed PNG invalidates the next model context
even when that intervening model read only targets HTML.
After the CLI build, `python3 -B tests/cli/binary_resource_smoke.py` checks PNG/JPEG
metadata and exact byte export, model authority separation, real browser asset
delivery/historical replay, MIME mismatch and broken image decoding. Its controlled
HTTP cases accept unchanged image evidence and reject an external image edit during
the final model wait. It does not qualify real-model or Office behavior.
Run browser suites sequentially: `resource_preview_smoke.py` also checks the shared
temporary-profile directory for leaks, so concurrent suites can interfere with it.
Focused `workspace snapshots:` tests cover manifest publication barriers, pending
and terminal records, historical reads after move/replacement/deletion, workspace
isolation and CAS GC retention. The browser smoke reopens the saved snapshot in a
new CLI process with current paths changed/deleted and reads saved verification
records without rerunning the browser.
`python3 -B tests/cli/web_diagnostics_smoke.py` checks real PNG/CDP capture against
a failed functional check, exact hashes/correlation, restart after source deletion,
workspace isolation, bounded Unicode event floods, oversized screenshots and an
unavailable browser. PNG unavailability cannot change a passing check outcome.
Set `RNA_DIAGNOSTIC_EVIDENCE=<directory>` to retain the failed counter PNG, trace
and result for inspection. CLI `diagnostic --kind screenshot|trace` emits metadata;
`--raw` emits exact retained bytes to redirected stdout (never with `--jsonl`).
The `workspace snapshots:` diagnostic case additionally checks rejection before
terminal publication, CAS roots through GC, missing/corrupt payloads and no
regeneration fallback. These are browser/storage checks, not real-LLM or Office
qualification. Trace format and bounds are documented in
[Resource Fabric](../../docs/resource-fabric.md#retained-browser-diagnostics).
`python3 tests/cli/web_functional_checks_smoke.py` exercises real pointer/text
checks against exact snapshots: a click-time exception missed by page load, repair,
no-op handler, historical broken bytes after repair, missing browser, wrong entry
and ambiguous selector. `python3 tests/cli/web_checks_runtime_smoke.py` uses a
scripted HTTP model through the production runtime for verify/read/patch/read/
verify/done, frozen admission checks and rejection of a source edit after the last
passing verification. It checks `webAcceptance.currentSnapshotVerified` in actual
HTTP: absent/failed verification, passing snapshot, invalidation after a patch and
successful re-verification, including native tool-result transport. An edit during
the final model wait leaves the format-repair context frozen but fails final
acceptance. Browser-only requirements also appear without a read minimum. The
fixture selects the published browser skill and explicitly enables auto-confirm
for its isolated operations. This is deterministic contract evidence, not LLM quality.
It also checks failed verification → blocked identical retry, unrelated write,
fresh source read and no-op write → still blocked → verified dependency repair →
one passing re-verification. Only two browser records may be published, and runtime
dependency references must remain absent from actual HTTP model requests.
It also inspects the actual HTTP context after each step: current diagnostic,
historical failure after an external dependency edit, completed write result with
its target/change status, then current source bytes. `context continuity:` covers source-bound
failure invalidation after CAS hydration, original argument-error preservation,
semantic mutation receipts through compaction and existing repeat/recovery guards.
`context continuity: exact dependency change recovery` checks serialized/replayed
dependency versions, rejection of partial/indirect/mismatched/unknown/no-change
effects and unchanged unknown-call non-replay protection.
It also checks the latest call/result pair through compiler and wire in all three
result roles, frozen repair, budget-only folding of older writes and target recovery
from accepted arguments. `resource cutover: large call` checks exact CAS hydration
for the latest call versus archived older arguments; `agent continuity: oversized
source` keeps pair/status/role while removing overwrite authority in all three roles.
The `workspace snapshots:` filter also checks that incomplete/substituted
assertions or mismatched actual text cannot become a durable passing result.
`python3 tests/cli/tool_result_projection_smoke.py` checks bounded semantic find,
exact read delivery, invalidation after external edit, failed browser-result
delivery, frozen format repair and the persisted context receipt. It also compares
saved request bytes with actual HTTP bodies and checks rejected-response retention,
attempt/snapshot correlation, exact verification-resource linkage with origin and
API-key exclusion from trace metadata/payloads.
`python3 tests/cli/omitted_read_guard_smoke.py` verifies that an exact file read
omitted by the compiler's budget cannot authorize a repeated replacement after
`RefreshRequired`, and preserves the original bytes.
`python3 tests/cli/read_recovery_smoke.py` covers replace/patch/copy/move/delete:
failure → unrelated read → rejected identical call → exact target read → one
successful mutation, with typed recovery and no runtime identity in HTTP. Move/delete
still require approval. A separate-process approval test proves that recovery
survives accepted-history restoration and an unrelated verified deletion does not
clear the target's failed call. These use scripted HTTP models, not real-model quality evidence.
`python3 -B tests/cli/read_acceptance_context_smoke.py` checks the dynamic final-read
projection: distinct counts, invalidation after edits, the single required block
after current results, frozen JSON repair, native result transport and no credit
from an earlier run. Its isolated writes explicitly use `--auto-confirm on`.
The focused `context continuity: preserves recent pair` Harness check also verifies
trailing-context budgeting, receipt indices and frozen repair across result roles.
Premature `done` with unmet explicit criteria stays out of accepted history and
continues in the same run. Checks include refreshed reads after the final model
wait without `--expect-files`, missing files/write counts, frozen completion
feedback during format repair, exhaustion, and unchanged `blocked`/`needs_input`
or no-criteria behavior. The browser runtime
smoke confirms that verification/read-back do not add file reads; the omitted-read
smoke confirms that this metadata does not grant overwrite authority.
`python3 -B tests/cli/task_list_smoke.py` verifies shared Task Lists in Plan/user
and Agent/native-tool transport: save/reorder/progress/notes/block/close, invalid
index atomicity, process restart and pinned current context, journal/publication
read-back, crash before dispatch and after publication, unknown effect denial,
retry after a verified change of the refused list's exact logical state,
identity-free model results and no credit toward file-write acceptance. It uses
scripted HTTP, not real-model qualification.
`python3 -B tests/cli/plan_document_smoke.py` verifies shared Markdown Plans with
workspace authority in native-tool and user transport: exact whitespace and
versions, current/historical evidence, frozen selection in read batches, restart,
discovery/selection across chats, stale-write rejection, duplicate titles, restore/tombstone, workspace isolation,
prepared/published/uncertain crash states and no file-write credit. It uses scripted
HTTP through the production CLI. For shared owner changes also run the focused
`plan mode:`, `plan document:`, `document Plan:` and `artifact working set:` harness
filters; these do not qualify real Office delivery or real-model Plan usage.
`python3 tests/cli/response_mode_smoke.py` checks explicit CLI `json_schema` /
`json_object` selection, `system` / `developer` instruction roles, all three
`--tool-result-role` transports through compiler/actual HTTP and persisted metadata.
It verifies exact native call IDs/arguments without orphans or a second generation
catalog, invalid-role rejection and cross-process approval inheriting both roles;
a conflicting role must leave
the pending file unchanged and send no model request.

The small real-model continuation task is `tests/cli/counter_task.md`. Use a fresh
workspace/session and the normal CLI with these arguments:

```text
--task-file tests/cli/counter_task.md --expect-files index.html,styles.css,app.js
--min-reads 3 --min-writes 3 --require-web-verify --max-iterations 12 --max-tool-steps 12
```

Record the exact model
digest, context, instruction role and `--thinking on|off`; see [model comparison rules](../../docs/development-rules.md#сравнение-моделей).
This is an opt-in real-model task, not a deterministic harness test. A passing CLI
contract proves file/read/verification evidence. For button behavior, use selectors
from `tests/cli/counter_checks.json` in the task and add `--web-checks` with that file.

For the real M5 repair task, copy `tests/cli/fixtures/counter-repair/` into a new
disposable workspace. Keep the checks outside it and run the normal CLI with:

```text
--task-file tests/cli/counter_repair_task.md --web-checks tests/cli/counter_checks.json
--expect-files index.html,styles.css,app.js --min-reads 1 --min-writes 1
--max-iterations 12 --max-tool-steps 12
```

The initial page loads normally; clicking plus raises a reproducible JavaScript
exception. Success requires fixing it, rereading the current source and passing
all nine steps against a retained snapshot. Preserve the CLI trace, verification
records, model config/digest, exact fixture/checks and code/binary revision.

The M5 CSV dashboard task is `tests/cli/csv_dashboard_task.md`. Run it with
`--web-checks tests/cli/csv_dashboard_checks.json --expect-files index.html,styles.css,app.js
--min-reads 3 --min-writes 3`. Continue with `tests/cli/csv_dashboard_followup.md`
in the same session, `--web-checks tests/cli/csv_dashboard_followup_checks.json`
and `--min-reads 3 --min-writes 1`. The base contract has 24 steps; the follow-up
has 31, including upload, both sort directions, category/amount filtering,
totals, proportional bars, actual CSV downloads and invalid/empty input handling.
The same `web.verify` owns snapshot capture, browser checks and durable results;
there is no longer a separate Node grader or direct project reader. Checks and
fixtures remain outside the writable workspace. A standalone `verify --web-checks`
can inspect results without a model, but cannot retroactively pass an agent run.
Run `python3 -B tests/cli/csv_dashboard_checks_smoke.py`: wrong export, equal-size
bars, lexical sorting, broken JS, missing browser and outbound attempts fail;
retained historical verification still passes after live files change/disappear.
`workspace snapshots:` also rejects forged passing CSV/table/chart results at
publication and checks exact result reopening with the original expectations.

After building the CLI, run `python3 tests/cli/interrupted_run_smoke.py` for a
process-killed model wait. It checks durable interrupted/unknown acceptance,
idempotent `resume`, one new model request only after explicit input, and the
one-iteration CLI limit.
Run `python3 tests/cli/capability_smoke.py` for E-CAPABILITY: with Chromium
unavailable, the CLI hides `web.verify`, advertises no Office tools, returns
`not-run` for explicit verification and rejects required browser acceptance
before starting a model or session.

Host-neutral tests run on this machine without Office COM. Locate the relevant test first; do not read or execute the full suite by default.

`python3 -B tests/cli/mode_questions_smoke.py` checks the production CLI's Chat,
Plan and Agent catalogs, rejected skill/write intent in Chat, no Plan writes,
mode pinning, typed question stops, same questions after process restart and
invalid/stale/replayed answer rejection before any durable change or model call.
It covers single/multiple/free-text constraints, native call/result pairing and
runtime-ID removal, acceptance across answers without repeating prior writes, and
isolation of a new task's counts. This uses scripted HTTP responses. Shared Office
paths are covered by `plan mode:`, `chat: uses only read-only` and
`architecture: mandatory dependency direction`; these are not Office qualification.
`tests/cli/questions_task.md` is a separate narrow real-model question test:
create a temporary workspace containing notes.txt, run in Plan with `--min-reads 1`,
then submit an explicit test-user choice with `answer`. Keep the two CLI traces,
actual HTTP/CAS, selected profile/digest and source/binaries. Success proves this
interaction route, not autonomous clarification quality or full M4/M5 acceptance.

`python3 -B tests/cli/resource_selectors_smoke.py` checks closed file/skill argument
alternatives in the actual prompt and structured-output wire, wrong-kind selectors
rejected before dispatch, nullable file defaults, invalid root paths with repair
guidance, relative directory discovery and exact file/skill reads. It uses scripted
HTTP through the production CLI and preserves separate file-read acceptance.

## Find a test

```bash
rg -n 'Test\(".*resource' tests/RNAssistant.Harness/Program.cs
rg -n 'TargetMethodOrBehavior' tests/RNAssistant.Harness
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- --list
```

## Run a focused slice

The trailing argument is a case-insensitive substring matched against category or test name:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "resources:"
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "bridge: typed resource"
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba: patch"
```

After an unchanged successful build, `--no-build` avoids recompilation:

```bash
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: CAS"
```

Filtering limits executed tests. The harness source-links Core and Office-neutral production files, so a normal run still compiles that full linked source set. Compilation does not require reading every test file into agent context.

For large-chat storage profiling, run
`RNA_CHAT_PERF=1 dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage perf:"`
after a successful build. It creates temporary 30/50 MiB JSONL fixtures with 6,000
messages and HTML revisions, then reports cold/warm load, save, library, context
usage and HTML metadata times. The normal harness run skips this expensive fixture.

For request compilation profiling, run
`RNA_CONTEXT_PERF=1 RNA_CONTEXT_PERF_OUTPUT=/tmp/rna-context-perf.json dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "context perf: request compilation"`.
The 72 cases cover short/long histories, three result roles, media, calibration,
generous/tight/impossible budgets. JSON records content fingerprints, receipt costs,
refusals and median compile time; compare saved before/after binaries on identical
fixtures. `context perf: remaining hotspots` separately measures mandatory
working-set restoration and HTML snapshot parsing/one-item metadata pages. These
fixtures are opt-in and have no timing gates. `context budget:` checks incremental
costs against the whole-request estimator, including removal of the last media.
`shared HTML: metadata pages preserve exact descriptors` compares paged metadata
and cursors with complete descriptors (UTF-8, empty files, raw access and data
bindings), and rejects continuation after an unreturned member changes.

Permanent test policy is defined in
[development rules §9](../../docs/development-rules.md#9-тестирование-по-риску);
current priorities and open evidence are in
[PROGRESS](../../docs/stabilization/PROGRESS.md). The master plan is historical.
Docs-only changes need diff/affected-link checks, not a harness build. A recorded
pass is reusable only when its relevant sources/tests, dependencies, build settings
and environment are unchanged. Explicit phase/release gates and the pre-commit
version check still apply.

## Artifact catalog and transfer

The same working-set filter covers document removal for originals/Plan/HTML/MD/JSON,
stale and foreign targets, confirmation, repeat clicks at the bridge/UI boundary,
other chats, restart, stale writes, CAS retention and failed post-publication saves.
`node tests/web/artifact-catalog-layout.test.js` checks the shipped tree/view layout
at 1100 and 420 px with Chromium (bundled Playwright via `NODE_PATH`, optional
`BROWSER_EXECUTABLE`). `artifact-working-set.test.js` checks tree paging, late
responses and guarded removal. MockDemo's transfer test also contains real-controller
removal/restart assertions; its current fixture compile gap is in the backlog.

Use `artifact transfer:` for project/archive roundtrips, saved Office snapshots,
independent copies, repeated operation refusal and original bytes. Use
`artifact working set:` for membership, stale selection and paginated discovery.
`node tests/web/artifact-working-set.test.js` covers the full catalog, dirty editors,
double clicks, late responses and sandboxed preview. Run
`dotnet run --project demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -- --artifact-transfer-test`
for real controller upload/import/download/copy delivery in an initially empty chat.
This is host-neutral evidence, not Windows/Office qualification.

## Incoming messages / steer

Run the focused `inbox:` filter for cancellation isolation, stale responses, batch
closure, final/approval races, durable FIFO editing, recovery and Stop behavior.
`dotnet run --project demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -- --inbox-test`
checks first-chat intake, two controllers sharing one data root, actual controller execution and materialized model context
without Office. Run `node tests/web/chat-inbox.test.js` for UI acknowledgement,
idempotent retry, consumed-before-ack receipts, draft preservation, chat isolation
and queue controls. `node tests/web/chat-run-completion.test.js` checks chat-list
spinners after fast completion, background pushes and idle polling, including
stale-inbox and pending-request isolation. `node tests/web/chat-input-delivery.test.js` uses real markup,
the Send button and state/render handlers in Chromium with controlled bridge replies
to check immediate Steer/Queue visibility, empty-queue/history races, exact-ID handoff
and manual scrolling after submission. It uses the same Playwright environment as
the layout test below. `node tests/web/live-run-projection.test.js` covers transcript
order across applied steers, silent-step grouping under commentary, preserved
disclosure state and visible active/attention rows. Accepted progress comments fold
with their actions after completion, cancellation, failure, reload and steer; opening
history restores each original comment once. Inline stream coverage checks readable
drafts, retry invalidation, accepted/final handoff and retained action/reasoning nodes.
`stream-render-cost.test.js` checks first-token frame coalescing, immediate resets,
empty retries and background navigation without restoring rejected drafts.
`node tests/web/agent-activity-layout.test.js` checks history in Chromium at wide/narrow
widths. `node tests/web/chat-transcript-layout.test.js` uses
the bundled Playwright (`NODE_PATH`, optional `BROWSER_EXECUTABLE` and
`LAYOUT_SCREENSHOT`) to check narrow light/dark bubbles, safe full text, editor
focus, manual/follow scrolling, late message growth, viewport resizing, deferred
gestures and history anchoring, including normal-contrast inline streaming and the
absence of a final-answer divider during generation.
These browser checks are distinct from Windows/Office/WebView2 qualification.

## Test map

`html assets:` covers manual package discovery, private content pinning, atomic
create-only import, cross-workspace publish/reuse and unsafe manifest rejection.
`html tools: native ownership` checks exact read/write policies and bindings.

`model context: request schema` measures exact retained request bytes for Excel
Agent core plus one loaded extension, and checks strict projection, direct
`json_object` and schema-rejection fallback during repair. The optional-schema
admission test checks the production model-loop projection.

`agent continuity:` checks retained read counts above the 8192/32768-character UI
thresholds after save/reload, exact CAS preview and missing bodies; revised plan
state and explanations after persistence/compaction; native open/blocked finals
across two runs; CSS-only repair with a reasoned no-op for JS and no forced extra
edit; oversized-source recovery through exact mixed-newline excerpts. Use
`task lists:`, `context continuity:`, `resources:`, current-source carry-forward,
prompt/schema and R61 inventory checks for the affected contracts. UI checks are
`completion-guard.test.js`, `task-list-continuity.test.js` and `plan-document.test.js`.
These scripted runs do not qualify target-model quality or Windows/WebView2.

`agent: keeps tool result within prompt budget` checks complete next-request JSON
delivery below/above the 8192-character archival and 8192-token artifact thresholds
(1/16/160 KB, all three result roles). `agent: tool result rejects insufficient
prompt budget` checks refusal before model dispatch without losing durable bytes;
`agent: tool result uses calibrated prompt budget` covers calibrated capacity.
Storage thresholds must not become separate limits on data sent to the model.

`resource cutover: correctness before budget` covers aggregate overflow from eight
individually affordable source reads in all three result roles. It checks exact
retained bodies, explicit omissions with no write authority, adjacent call/result
pairs, unchanged durable evidence and failure when required instructions overflow.
It also retains stale-source notices through model projection; the diagnostic
payload fixture uses the supported `details` field of the current VBA envelope.
The omitted-read CLI smoke checks the actual mutation refusal and preserved bytes.

`shared context:` checks automatic compaction publication, competing generations,
second-chat discovery/native paged reads, externalized archive projection with a
model stub, per-claim invalidation, raw archive masking, alternate-view refusal,
lineage unlink, inherited source republication and origin deletion with CAS GC.
Use `storage: CAS` for fail-closed reachability regression after checksum handling.

`context: compaction` includes typed claim/source-role admission, Tool Result
envelope-role isolation, exact evidence preservation, per-claim invalidation after
source changes, interpretation-promotion refusal, persistence and old-version skip.

`artifacts: prompt preserves decision context` and `resource cutover: correctness
before budget` cover optional-purpose admission, exact target preservation,
ownership/read hints and semantic recovery after stale evidence exclusion.
The latter also checks that stale call input cannot invalidate a fresh result,
archived mutation diagnostics survive folding in every result role, and oversized
exact reads become explicit body-omission receipts without usable write evidence.
`agent: VBA mutation batch rejects before dispatch` checks complete after-source
and the verified write's unchanged success status in the next model request.
`agent: confirmed VBA after-state reaches next request` covers a confirmed 100-hunk
patch, result archival, semantic source labeling, one write and provider message
serialization. `agent: resource media projection failure is explicit` also checks
that delivery failures preserve mutation outcome/effect metadata. The source
compaction check rejects input-only references as substitutes for visible bytes
and covers calibrated admission of complete mutation source.
`agent: large VBA read and edit survive compaction` uses the production run service
with a scripted model and fake host: create/read a CRLF module over the internal
32K resource page, restore its source after a persisted checkpoint, then edit it.
At 24K context it checks omitted-source overwrite refusal followed by an exact
line read and patch, preserving all unrelated source. Large conflict recovery
must not pin a stitched head/tail body that prevents the next request.
`vba: patch disambiguates with exact context` also covers explicit line/column
selection, duplicate procedures, stale-address refusal and candidate completeness.
`vba: pure patch text contract` covers copied blocks in mixed-newline source.
`html tools: patch mismatch returns current source` checks atomic multi-hunk
rejection, bounded recovery and exact copied anchors across mixed line endings.
`resource cutover: current source survives writes and compaction` and `resource
cutover: HTML write publishes current member sources` cover the verified after-state,
CAS carry-forward, stale-version exclusion and next guarded HTML overwrite.

`resources: Markdown sections` checks native semantic reads from another chat,
exact nested-section/fence/CRLF/Unicode boundaries, partial retained evidence and
whole-refresh refusal, whole-read independence, missing/duplicate/oversized
headings, uploaded/incomplete/unsupported text, missing CAS and generation drift.
Use `resources:`, `shared Markdown:`, `document Plan:`, schema mirroring and the
reviewed R61 inventory for the selector/schema/scanner change. Model-quality and
Windows gates are not exercised by these harness checks.

`excel discovery:` reproduces the reported document-read failure with 201 defined
names. It verifies independent document/A1 reads, strict point-name admission,
partial discovery/negative search, terminal paging, coverage-only cursor drift,
result clipping and bound-document refusal. Use `excel names:`, `excel tables:`,
`excel read:` and `resources:` for affected consumers. Live workbook/502 qualification
requires the reported Windows/server environment.

`excel search:` checks exact regex snapshots and native literal search over a sparse
sheet beyond the full-snapshot bound, including zero matches and the compact result
limit. `excel find replace:` checks the bound search/mutation path and scope semantics.
These host-neutral checks do not qualify Excel COM `Find` on Windows.

`resources: document discovery bounds source pages` uses 73 resources and 1000
unrelated receipts to verify per-page metadata IO, no full authority capture on
provider discovery, exact point identities, complete traversal/search, cold index
rebuild and writer drift. The generic incomplete-coverage test also bounds empty
source-page loops. Startup and ordered-insertion allocation remain qualification gaps.

`resources: document text index` verifies late cross-chat Markdown matches, exact
cross-part/CRLF/Unicode offsets, fenced-heading exclusion, model section context,
bounded negatives, derived-byte repair, restart, current versus historical indexes,
missing source refusal and last-page writer drift. The existing discovery-coverage
test also checks descriptions/section context for search-only candidates. Use
`resources:`, `shared Markdown:`, `document Plan:` and `html` for affected consumers.

`resources: original text index` covers late uploaded Markdown/plain-text matches
from another chat, exact snippet reads, source-versus-extraction hashes, heading
semantics, derived-byte repair, GC/restart and missing extraction. It also checks
explicit truncation/omitted PDF pages on positive and negative queries, including
model-facing incomplete negatives. Use `resources:`, `document originals:` and
`document Plan:` for this extension; no PDF/Office rendering is involved.

`resources: document discovery` verifies healthy partial results with missing/corrupt
current metadata, body loss, exact reads, metadata recovery, current-head selection
without historical fallback, unknown original/logical heads and continuation drift.
`attachments: message targets isolate exact originals` covers direct native reads,
search and outline despite unrelated unavailable metadata, same-name uploads,
compaction/restart, foreign ownership, missing links, ambiguous labels, selected
metadata/byte loss and explicit removal. `resources: original text index streams a
large one-line source` also checks its initial production prompt target at 32K.
`resources: discovery preserves` covers completeness across pages/providers and
unique-target refusal. Use `resources:`, `html`, `shared Markdown:` and
`document Plan:` for affected consumers; this slice does not alter browser layout.

`shared Markdown:` verifies independent document ownership, exact text and metadata,
cross-chat discovery/editing, prepared stale guards, same-step call identities,
restore, immutable uploaded MD, missing-body unlink, failed link save, fork, GC
and the full agent execution/event-replay cycle.
Use `tool runtime:`, `tools: R61 built-in contract inventory`, `artifact working set:`
and `artifact viewer:` for these consumers. The existing working-set browser test
also covers MD attachment/refresh and late navigation responses.

`shared HTML:` covers document publication, failed chat-link replay refusal,
second-chat selection/edit/restart, member URI round-trip, concurrent stale writers,
new-revision undo/redo, shared fork, origin deletion/GC, independent workspaces and
unlink with a missing body. Sequential CSS/HTML patches preserve exact source,
historical cursor isolation and retained run/activity attribution. The production
conversation regression checks full-run/per-call diffs after activity archival and
restart, including a later verified no-op that cannot claim prior revisions.
Use `html`, `resource cutover: local restore`,
`resource cutover: chat lifecycle` and `artifact working set:` for the ownership
cutover; the same working-set browser test covers HTML selection and dirty drafts.

`shared HTML: text discovery` checks late source matches after cross-chat selection,
exact member snippet reads, HTML semantic scope, binding-versus-bound-data search,
parent-scoped coverage, unchanged authority, derived-byte repair, GC/restart,
historical reads and missing parent bytes. It injects selection/generation drift
during indexing and member-index failure during combined resource search. Use
`html`, `resources:` and `document Plan:` for affected consumers.

`html actions:` covers exact chat/snapshot/session controls, ABA revision drift,
refusal before mutation dispatch, unchanged authority/no unresolved effect, fresh
retry and all seven typed bridge routes. UI companion:
`node tests/web/html-workspace-actions.test.js` (dialogs, duplicates, navigation,
late edits and export lease cleanup). Use the existing HTML import/export/editor
checks when changing their domain behavior. Live controller document switching
remains a Windows qualification gate.

`artifact recovery:` verifies per-resource metadata loss/corruption, unavailable
Plan heads, healthy picker entries, strict reads, durable unlink, metadata return
and fork. Run with the working-set tests when changing reference-only recovery.

`artifact working set:` covers chat-local link persistence, document Plan selection
and native editing from an empty second chat, unlink/fork/history/clear, missing
body reads, stale chat and Plan versions, document lease conflicts, foreign scope,
duplicate-title paging and cursor drift. Use `document Plan:`, `document originals:`
and `artifact library:` when changing shared ownership/projection. The UI companion
is `node tests/web/artifact-working-set.test.js`; real WebView/layout remains a
separate qualification gate.

`run changes:` checks retained CAS fidelity, parent-chain/HTML member pairing,
missing baselines, durable VBA read-back/no-op/unknown evidence, source bounds and
confirmation-call attribution. `tests/web/text-diff.test.js` checks exact line
counts against an independent LCS oracle; `run-changes-layout.test.js` exercises
the completed-run renderer, narrow/wide Chromium layout and stale chat delivery
with the same Playwright environment variables as the checks below.

`tool presentation:` covers exact call/confirmation attribution, sibling changes,
no-op versus missing/unverified evidence, artifact parent boundaries and typed bounded
content blocks with exact numeric cells. `tests/web/tool-result-presentation.test.js`
covers lazy requests, collapse/removal cancellation, retry, wrong-source delivery,
in-flight revision changes and unknown block kinds using the Playwright setup below.

`tests/web/agent-activity-layout.test.js` checks action/target/result wrapping at
wide and narrow chat widths, shipped Markdown line breaks/lists/code/sanitization,
single diagnostic bodies, zero-action failures, concise step details and bounded
typed result previews in light/dark narrow layouts. Silent-step groups additionally
check actual visibility, keyboard expansion, restored active state and narrow layout.
It uses the same bundled Playwright and optional browser/screenshot variables
as the artifact/diagnostics layout check below.

`tests/web/artifact-diagnostics-layout.test.js` checks actual Chromium geometry for
artifact names/metadata, single selection, inline JSON values, left-aligned events
and split dragging. Use the bundled Playwright through `NODE_PATH`; optionally set
`BROWSER_EXECUTABLE` and `LAYOUT_SCREENSHOT`. This is not Office/WebView2 qualification.

`resource cutover: two chat mutations` covers three outcomes through separate
executors/shared document authority, native Excel read/write, persisted read facts
and the next actual model request: no-op keeps evidence, verified change without
captured after-state and lost read-back exclude uncertain content. Historical CAS
and old frozen requests survive; compile/retained reads do not call Office/replay.
`vba: confirmed mutation rejects stale snapshot` also checks the competing chat's
new revision: refusal has no mutation commit/backup, while separate conservative
guard drift may make the head Unknown without deleting the retained replacement.

`resource cutover: frozen prompt publication` also checks catalog publication
between model-session capture and final compile: mixed generation is refused,
the last receipt remains unchanged, fresh rebind succeeds, and the original
request/repair stays pinned. Use with the existing model-session compaction and
durable admission checks when changing this boundary; no Office validation here.

`resources: discovery preserves` covers ordinary vs terminal pagination, filtered
and empty incomplete captures, multi-provider accumulation, truncated search and
refusal to infer a unique semantic target from incomplete enumeration. Use with
`resources: gateway` for changes to intent discovery/completeness behavior.

`resource search:` covers generic live document/VBA zero-match drift publication,
exact retained pages without Office reads, missing-CAS refusal, bounded snippet vs
whole-capture coverage, truncated VBA prefixes and body-free backup metadata.
Use with `resources: live Office and VBA` and `resources: gateway reads searches`
for changes to the shared search capture/publication contract.

Excel range values/formulas/profile direct cutover: `Program.ExcelReadTests.cs`
(`excel read:`) covers generic exact source evidence, retained profile continuation,
drift supersession, bounds before materialization, removed-call rejection and shared
HTML/bound-STA routing. The legacy public `excel.read_range` fixture path is removed.
`excel search:` covers exact cell snapshots, zero-match drift, historical pages,
missing-CAS refusal, metadata discovery and invalid/oversized capture rejection.
`excel find replace:` retains scope/pattern/formula semantics and also checks search
evidence invalidation after replacement and bound-STA/closed-workbook search.
`excel tables:` checks named ListObject discovery, stable identity through moves/
resizes (including equal cells), exact historical source/records without Office,
missing-CAS refusal, incomplete/ambiguous catalog and pre-capture extent bounds.
`excel names:` checks global/sheet-qualified identity, dynamic definition/extent
drift with equal cells, historical source/records after live removal, missing CAS,
metadata-only unresolved/foreign/multi-area names and complete bounded catalogs.
Only explicit bound-range proof admits cells; invalid proof/extent fails closed.
`excel read:` also checks named-table/name bound-STA/closed-workbook delivery.
These host-neutral cases do not qualify real ListObjects or Defined Names COM.

Word source cutover: `word resources:` and `word tools:` cover complete exact
document/selection/range evidence, single-capture continuation, historical no-I/O,
removed-call refusal, pre-materialization bounds and bound-STA/closed-document
source access. Fake checks do not qualify real Word COM text or selection semantics.
`word search:` covers exact story-scope CAS evidence (including zero matches),
regex/coordinates/output bounds, historical no-I/O and malformed/oversized capture
refusal; `word tools:` also checks mutation invalidation and search bound-STA/close.

PowerPoint source cutover: `powerpoint resources:` and `powerpoint tools:` cover
document/slide text and notes, exact CAS evidence, one-capture paging, historical
no-I/O, explicit bounds, removed-call refusal, shared HTML capture and bound
STA/closed-presentation routing. Real COM shapes/notes and selection remain unqualified.
`powerpoint search:` adds regex/notes/coordinate/output semantics, complete evidence
including zero matches/empty scopes, retained historical pages, missing-CAS refusal
and malformed/oversized capture rejection. `powerpoint tools:` also checks search
evidence invalidation after mutation and bound-STA/closed-presentation search.

Outlook mail cutover: `outlook resources:`, `outlook capture:` and `outlook tools:`
cover exact source/evidence/CAS paging, header-only discovery and attachment views,
scope refusal/ambiguity, removed-reader rejection, HTML and bound-STA/closed-window
reads, malformed captures and preparation refusal before mutation. These do not
qualify real COM membership/unsaved-mail or OOM pre-materialization bounds.
Collection cases cover body-free discovery, explicit folder/preview coverage,
exact CAS-derived records, membership drift with retained continuation, shared HTML
binding, empty/oversized snapshots, Inspector refusal and removed-tool rejection.
`outlook search:` covers exact field snapshots, zero-match drift, historical no-I/O,
missing-CAS refusal, header-only body isolation, explicit body-prefix truncation,
invalid capture/input rejection and removal of inline body/EntryID output and its
old argument. Existing Outlook tool cases also check bound-STA/closed-window search.

| Area | Main files | Useful filters |
| --- | --- | --- |
| Conversation and Agent | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ToolDiscoveryTests.cs` | `conversation:`, `agent:` |
| Tool Result v1 / strict JSON | `Program.ToolResultWireTests.cs`; projection checks in `Program.ToolRuntimeTests.cs` | `tool result wire:`, `tool result materialization:` |
| Native ToolRuntime / typed contracts and effect evidence | `Program.ToolRuntimeTests.cs`; native read in `Program.ResourceGatewayTests.cs` | `tool runtime:` |
| Immutable ToolPack authority / finite core / atomic callable admission | `Program.ToolDiscoveryTests.cs`; confirmation and policy regressions in `Program.SimpleAgentTests.cs` and `Program.AgentSafetyTests.cs` | `tool pack:`, `agent: confirmation`, `protocol context: batch safety uses local authority` |
| Typed Excel reads/writes/range/table/chart families / native, HTML, bounds and effect evidence | `Program.ExcelReadTests.cs`, `Program.ExcelWriteTests.cs`, `Program.ExcelRangeMutationTests.cs`, `Program.ExcelTableTests.cs`, `Program.ExcelChartTests.cs`; paired Agent regression in `Program.AgentSafetyTests.cs`; host access in `Program.ParserDesktopTests.cs` | `excel read:`, `excel write:`, `excel range mutation:`, `excel table:`, `excel chart:`, `protocol context: loop tracks only accepted calls`, `tools: html workspace updates session`, `host runtime:` |
| Host document gate / neutral bound session / direct context and catalog reads | `Program.ParserDesktopTests.cs`; live-read/guard integration in `Program.VbaPromptTests.cs` and `Program.ResourceGatewayTests.cs` | `host runtime:`, `vba: queued guard`, `waits for active mutation`, `vba: confirmed mutation`, `tool runtime: native resource tools manual and model paths` |
| Artifact Library bounded ChatState transport, exact paged history, stale cursors, HTML branch selection, media gallery projection and chat resource cards | `Program.ArtifactLibraryTests.cs`; UI contracts in `tests/web/artifact-library-projection.test.js`, `tests/web/artifact-media-gallery.test.js` and `tests/web/chat-resource-card.test.js` | `artifact library:` |
| Chat image loading after transcript refresh/cache eviction, uncropped thumbnails and long-image fit | `tests/web/chat-image-display.test.js` (real Chromium/Playwright, optional `BROWSER_EXECUTABLE`); existing `artifact-media-gallery.test.js`, `artifact-text-viewer.test.js` and `stream-render-cost.test.js` | UI only; no Office/WebView2 qualification |
| Exact bounded artifact text/source and Markdown viewer projection | `Program.ResourceGatewayTests.cs`, `Program.ContextBridgeTests.cs`; UI contracts in `tests/web/artifact-text-viewer.test.js` and `tests/web/artifact-json-viewer.test.js` | `artifact viewer:`, `bridge: typed artifact viewer`, `resources: gateway reads searches resolves and pages`, `resources: duplicate artifact ids fail closed`, `resources: empty text remains exact` |
| HTML whole-workspace revision lineage and branch recovery | `Program.HtmlArtifactStorageTests.cs`; replay/recovery in `Program.SessionEventStoreTests.cs` | `html lineage:`, `storage: html navigation`, `storage: html redo branches`, `storage: html recovery` |
| Inert uploaded-HTML shared data-plane viewer and exact Gateway import | `Program.HtmlArtifactStorageTests.cs`, `Program.ContextBridgeTests.cs`; UI contracts in `tests/web/html-upload-import.test.js` and `tests/web/artifact-json-viewer.test.js` | `html import:`, `bridge: typed html import`, `artifact viewer:` |
| Exact HTML binding checkpoint/recovery/refresh/export and typed bridge payload | `Program.HtmlArtifactStorageTests.cs`, `Program.HtmlWorkspaceToolTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ContextBridgeTests.cs`; UI/export contracts in `tests/web/html-workspace-export.test.js` and `tests/web/html-workspace-echarts.test.js` | `html export:`, `html tools: native ownership and typed binding`, `tools: html workspace updates session`, `bridge: typed html export` |
| Existing HTML file replacement from the current model-visible source | `Program.HtmlWorkspaceToolTests.cs` | `html tools: replacement requires current source` |
| Changed calls that repeat one source conflict without a refresh | `Program.AgentKernelTests.cs` | `kernel: changed conflicts without refresh stop` |
| R61 HTML semantic schemas, accepted-read binding, automatic preflight and model-result/history isolation | `Program.HtmlWorkspaceToolTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ToolContractAuditTests.cs`; UI policy and standalone runtime checks in `tests/web/html-workspace-export.test.js` and `tests/web/html-workspace-echarts.test.js` | `html tools:`, `tools: html workspace updates session`, `tools: html source`, `tools: R61 built-in contract inventory` |
| R61 Prompt/Tool/Skill semantic authoring, installed-package review and replay isolation | `Program.ChatSettingsTests.cs`, `Program.ToolStoreTests.cs`, `HarnessAdditionalToolTests.cs`, `Program.ToolContractAuditTests.cs` | `tools: authoring intents are semantic`, `tools: validate payload without saving`, `chat: prompt save preserves global model`, `tools: agent CRUD preserves omitted fields`, `skills: CRUD preserves omitted fields`, `tools: R61 built-in contract inventory` |
| R61 VBA/macro semantic intents, runtime-owned patch/backup state and replay/result isolation | `Program.VbaPromptTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.ToolStoreTests.cs`, `Program.ToolContractAuditTests.cs` | `vba: semantic intent contracts isolate runtime state`, `vba:`, `agent: exposes safe VBA editing tools`, `resources: live Office and VBA are bounded and guarded`, `tools: VBA facade is common across hosts`, `tools: R61 built-in contract inventory` |
| R61 Tool Library UI-only documentation, typed Test controls and internal semantic continuation | `Program.ToolLibraryUxTests.cs`, `Program.ContextBridgeTests.cs`; browser contracts in `tests/web/tool-library-ux.test.js`, `tests/web/tools-contract.test.js` and `tests/web/tools-editor.test.js` | `tools: built-in documentation`, `tools: Library continuation`, `bridge: typed tools and skills` |
| Plan exact Markdown lineage, restore/removal and pinned-URI handoff | `Program.PlanModeTests.cs`; UI restore/preflight/handoff contract in `tests/web/plan-document.test.js` | `plan document:`, `plan mode:` |
| Pure AgentKernel / typed run evidence | `Program.AgentKernelTests.cs` | `kernel:` |
| Immutable run/UI projection and ordering | `Program.RunViewStateTests.cs`, replay/recovery in `Program.SessionEventStoreTests.cs`, boundary check in `Program.ProjectStructureTests.cs`; static UI in `tests/web/run-view-state.test.js` | `run view:`, `kernel replay:`, `kernel recovery:`, `architecture:` |
| Physical/layer dependency direction | `Program.ProjectStructureTests.cs`: Core.Agent, ModelProtocol, VBA, resources (including no legacy execution adapter in the resource catalog), OfficeHosts and UI boundaries, dependency identifier-prefix regression, explicit VBA marker contract, root application façade, plus production source inclusion | `architecture: mandatory dependency direction`, `harness: production projects include all source files` |
| Local native portable publishing | `Program.ProjectStructureTests.cs`; manifest-scoped replacement, `lib` layout and architecture destinations | `build: portable publish` |
| Office model-context owner | `Program.ToolDiscoveryTests.cs`; result/projection coverage in `Program.AgentSafetyTests.cs` | `agent: model session`, `agent: bounds oversized`, `context inspector:`, `protocol context:` |
| ModelProtocol boundary | `Program.AgentSafetyTests.cs`; media integration in `Program.ResourceGatewayTests.cs` | `model protocol:`, `agent: hydrates artifact media`, `causal trace:` |
| Active wire / compatibility probes | `Program.AgentSafetyTests.cs` | `model compatibility:`, `agent: supports selectable`, `model protocol:` |
| Prompt schema review / settings | `Program.ChatSettingsTests.cs`, `Program.AgentSafetyTests.cs`, `Program.ContextBridgeTests.cs` | `settings:`, `bridge: typed settings`, `chat: prompt save` |
| Prompt editor exact source, body-free settings, changed-only uploads and publication guards | `Program.PromptEditorResourceTests.cs`, `Program.ContextBridgeTests.cs`; UI/shared transfers in `tests/web/prompt-review.test.js` | `prompt editor:`, `bridge: typed settings`, `resource cutover: prompt resources`, `resource cutover: frozen prompt publication`, `chat: prompt save preserves global model` |
| HTML workspace mutation uploads, exact head guards and pre-dispatch validation | `Program.HtmlWorkspaceEditorResourceTests.cs`, `Program.HtmlWorkspaceToolTests.cs`; UI/shared upload in `tests/web/html-workspace-write.test.js` | `html editor:`, `tools: html workspace updates session`, `html tools:`, `resource cutover: local restore logical lineage` |
| HTML workspace metadata-only projections and exact source/preview/export downloads | `Program.HtmlWorkspaceEditorResourceTests.cs`, `HarnessAdditionalToolTests.cs`; `tests/web/html-workspace-source.test.js`, `html-workspace-export.test.js` | `html editor:`, `tools: html workspace history is bounded`, `html export:`, `bridge: typed html export` |
| Conversation v5 contract/context | `Program.SimpleAgentTests.cs`, `Program.AgentSafetyTests.cs` | `conversation v5:`, `protocol context:` |
| History/context preflight | `Program.AgentSafetyTests.cs` | `preflight`, `protocol context:`, `model protocol:` |
| Resources and attachments | `Program.ResourceFabricTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.AttachmentTests.cs`; UI pre-dispatch ordering in `tests/web/attachment-ingestion-order.test.js` | `resources:`, `attachments:` |
| Document-owned sent originals: two-chat discovery/read/viewer, link retry, concurrent publication, fork, restart/deletion/GC, missing extraction and foreign-owner refusal | `Program.AttachmentTests.cs`; host-neutral only, real Office/WebView2 qualification remains open | `document originals:` |
| Document Plan publication: two-chat reads/writes, stale and concurrent writers, independent Plans, selected fork, restart/GC, failed chat link, missing body and exact restore authority | `Program.PlanModeTests.cs`; chat ownership cleanup in `Program.ResourceAuthorityTests.cs`. Arbitrary-chat Plan selection UI and Windows qualification remain open | `document Plan:`, `plan document:`, `plan mode:`, `resource cutover: chat lifecycle atomic publication` |
| Bounded attachment upload / capability lifetime / metadata-only bridge | `Program.ResourceUploadTests.cs`, `Program.ContextBridgeTests.cs`, `Program.WebViewSecurityTests.cs`; UI transport/cancellation in `tests/web/resource-upload.test.js`, real DOM picker/drop/paste/status in `tests/web/attachment-actions.test.js` (Playwright; optional `BROWSER_EXECUTABLE`), and pre-send barrier above | `attachments: upload`, `bridge: typed resource ingestion`, `webview:` |
| Disposable trajectory ZIP / bounded download and shared transfer lifetime | `Program.SessionEventStoreTests.cs`, `Program.ResourceDownloadTests.cs`, `Program.ContextBridgeTests.cs`; UI checks in `tests/web/resource-download.test.js` and `tests/web/trajectory-json-viewer.test.js` | `trajectory export`, `bridge: typed trajectory query` |
| Diagnostic event payload / fully verified bounded CAS prefix | `Program.TrajectoryPayloadTests.cs`, `Program.ContextBridgeTests.cs`; shared browser reader and lifecycle in `tests/web/trajectory-payload.test.js`, `tests/web/trajectory-json-viewer.test.js`, `tests/web/run-journal.test.js` | `trajectory payload:`, `verified CAS prefix`, `bridge: typed trajectory query` |
| Session storage and CAS | `Program.SessionEventStoreTests.cs`, `Program.CasMaintenanceTests.cs` | `storage:` |
| Chats, context and bridge | `Program.ChatSessionTests.cs`, `Program.ChatEditTests.cs`, `Program.ContextBridgeTests.cs`, `Program.PromptContextInspectorTests.cs` | `chat:`, `chat sessions:`, `context:`, `bridge:` |
| Composer add menu, mode availability, context ring, shared context/connection tooltips and usage panel | `tests/web/context-usage.test.js`; `tests/web/chat-composer-layout.test.js` (Playwright, both themes at 1100/740/360 px, keyboard dismissal and long/live diagnostics; optional `BROWSER_EXECUTABLE`) | Web only; no Office qualification |
| Model-context request/response window, provenance and full payload | `Program.ModelContextInspectorTests.cs`, existing request-before-dispatch check; `tests/web/model-context.test.js` | `model context:`, `storage: model request trace precedes dispatch` |
| Inspector raw request download / metadata-only bridge | `Program.PromptContextInspectorTests.cs`, `Program.ContextBridgeTests.cs`; `tests/web/context-inspector-download.test.js`, `tests/web/context-json-viewer.test.js`, shared `tests/web/resource-download.test.js` | `context inspector:`, `bridge: typed prompt context inspector` |
| Binary view discovery, early admission, retained limits and missing/corrupt metadata/part refusal without recapture | `Program.ResourceAuthorityTests.cs`; existing image/PDF cases in `Program.ResourceGatewayTests.cs` | `resource cutover: canonical binary`, `resource cutover: binary capabilities`, `resource cutover: binary metadata`, `artifact viewer: exact image`, `artifact viewer: exact PDF` |
| Raw attachment/stored artifact/workspace file bytes / exact member and historical CAS identity / inert binary delivery / no inline or broken-attachment fallback / binding-vs-data refusal | `Program.ResourceAuthorityTests.cs`; shared browser consumers in `tests/web/resource-data-plane.test.js`, `tests/web/html-workspace-export.test.js` | `resource cutover: raw`, `html editor: exact source`, `html tools: native ownership and typed binding` |
| Binary chunks / shared reservation / busy close / verified viewer blobs | `Program.ResourceAuthorityTests.cs`, `Program.ResourceDownloadTests.cs`; `tests/web/resource-download.test.js`, `resource-data-plane.test.js`, `html-workspace-export.test.js`, `artifact-text-viewer.test.js`, `artifact-media-gallery.test.js` | `resource cutover: binary`, `resource cutover: canonical binary`, `trajectory export: bounded capture`, `attachments: upload lease` |
| Tools and disabled pipelines | `Program.ToolStoreTests.cs`, `Program.PipelineToolTests.cs`, `Program.SearchToolTests.cs` | `tools:`, `pipeline:`, `search:` |
| Native Tool authoring and typed Tools UI boundary | `Program.ToolStoreTests.cs`, `Program.ContextBridgeTests.cs`, `Program.ProjectStructureTests.cs`; strict UI contracts in `tests/web/tools-contract.test.js`, `tests/web/tools-editor.test.js` and `tests/web/tool-package-actions.test.js` | `tools:`, `bridge: typed tools and skills`, `architecture:` |
| Native Skill authoring and typed Skills UI boundary | `Program.ToolStoreTests.cs`, `Program.ContextBridgeTests.cs`, `Program.ProjectStructureTests.cs`; strict UI contract in `tests/web/skills-contract.test.js` | `skills:`, `bridge: typed tools and skills`, `architecture:` |
| VBA reader, mutation/journal, pure patch and text canonicalization | `Program.VbaPromptTests.cs`, `Program.VbaToolPackageTests.cs`; catalog gate regression in `Program.ParserDesktopTests.cs` | `vba:`, `vba: mutation`, `vba: reader validates typed snapshots`, `host runtime: direct VBA catalog reads share access`, `vba: pure patch text contract`, `vba: live hash preserves line structure`, `vba: code hash normalizes export` |
| VBA editor exact Gateway snapshot, single-use source upload and save guard | `Program.VbaEditorResourceTests.cs`, `Program.VbaEditorUploadTests.cs`, `Program.ResourceUploadTests.cs`, `Program.ResourceGatewayTests.cs`, `Program.ResourceAuthorityTests.cs`, `Program.ContextBridgeTests.cs`; UI in `tests/web/vba-resource-read.test.js`, `tests/web/vba-resource-write.test.js`, shared writer in `tests/web/resource-upload.test.js` | `vba editor:`, `attachments: upload`, `vba: editor module waits`, `resources: live Office and VBA`, `resource cutover: live and retained logical continuations`, `bridge: typed vba` |
| HTML, plans and charts | `Program.HtmlArtifactStorageTests.cs`, `Program.PlanToolTests.cs`, `Program.ChartArtifactTests.cs` | `artifacts:`, `plans:`, `chart:` |
| Desktop/WebView-neutral | `Program.ParserDesktopTests.cs`, `Program.WebViewSecurityTests.cs` | `desktop target:`, `webview:` |

For the derived chat index, run `storage: sqlite`, `storage: corrupt sqlite`,
`storage: CAS` and `node tests/web/model-navigation.test.js`. The opt-in
`RNA_CHAT_PERF=1` case `storage perf: large chat profile` records first index
build, indexed cold/warm reads and save times on temporary 30/50 MiB chats.

Closed-document chat navigation: run `chat sessions:`, `agent: closed document`
and `host runtime:`. These cover retained metadata after bound-session closure,
archived selection without authority reassignment, HTML creation in the selected
closed chat, catalog/model availability and guarded live reads. The UI route and
reselection after reopening are covered by `node tests/web/chat-sync.test.js`.
Desktop/VSTO attachment and real COM lifetime still need Windows/Office evidence.

The `harness:` slice also verifies that every production `.cs` file is explicitly included in its old-style `.csproj`, preventing source-linked harness globs from hiding a broken production project.

The bound-session fixtures test operation ownership, STA handoff/cancellation,
Save As, close/reopen rejection, gate cleanup and direct context/catalog root isolation
using supplied fake identities. Capture service tests do not run the real controller;
the harness uses a bridge stub, so controller wiring remains a Windows gate.
They do not validate real Excel COM identity, production binding or Windows UI
reentrancy. Those remain Phase 5B2 gates in [ADR-0005](../../docs/decisions/ADR-0005-bound-document-session.md).

Panel/model responsiveness uses `bridge: navigation leaves caller context` and
`bridge: model catalog cancellation releases binding`, paired with
`node tests/web/model-navigation.test.js` and `node tests/web/stream-render-cost.test.js`.
They cover worker dispatch, cancel/drain before exclusive rebinding, stale catalog
responses, settings-preview cancellation/reload, chat preference ordering, hidden
model DOM work and activity batching.
`bridge: agent run keeps controls responsive` installs the Office selection
coordinator and checks same-document chat round trips during an active request,
refusal of document rebinding, and cancellation of the original request.
`node tests/web/multi-chat-requests.test.js` and `node tests/web/chat-sync.test.js`
cover per-chat responses and serialized UI navigation.
The bridge controller is a stub; real controller/HTTP wiring and Desktop/Office
UI responsiveness still need Windows evidence.

The former Excel identity probe/helper and Qualification Center tests were removed.
Real Excel COM identity, production binding and UI reentrancy remain Windows gates
under [ADR-0005](../../docs/decisions/ADR-0005-bound-document-session.md).

`harness: production projects include all source files` requires explicit file
includes in old-style projects. SDK projects use MSBuild `-getItem:Compile`
evaluation for defaults, conditions, globs and exclusions; missing explicit files
and an empty compilation are rejected. This check does not build, restore or
evaluate Office/VSTO projects. Disposable fixtures preserve both project styles.

Versioning changes use the existing `Program.ProjectStructureTests.cs` suite:

```bash
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"
node tests/web/settings-version.test.js
```

The `versioning` substring selects only versioning cases. These need Git and dotnet;
they invoke MSBuild against disposable small projects, commits and local bare remotes.
Fixture refs never affect the working repository or its origin. Coverage includes
unchanged product versions across ordinary builds/commits, source archives without Git
(Git/GitHub commit recovery, Debug/Release, explicit/unknown metadata and release
rejection), invalid metadata,
release-only gates, tag uniqueness and SDK/old-style assembly attributes. No Office
projects or PowerShell release workflow are executed by this slice. The focused
JavaScript check covers the Settings product/short-commit label and provenance
fallbacks without WebView layout execution.

## Stabilization characterization

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization
```

Phase 1A extends `Program.SimpleAgentTests.cs`: write ok/error/unknown/no-write,
twentieth-response recovery, rejected history isolation and the current 20-retry
(21-request) cap. These tests use fake LLM/Office and the real local VBA journal.
Phase 1C replaces false-success expectations with independent runtime-health
assertions while preserving v2 model status. Before the fix, four evidence tests
were red; after it, all seven pass. This is host-neutral safety coverage, not
Windows qualification. See [Phase 1A evidence](../../docs/stabilization/PHASE_1A_CHARACTERIZATION.md).

## Kernel and production adapters (Phase 3B2)

`kernel:` uses fake generic model/tool/store ports and covers deterministic
outcomes, IDs, limits, cancellation and append failures. `kernel: completion rejection`
covers same-run continuation, durable rejection before the next dispatch, budget
exhaustion, cancellation/append failures and the unchanged unknown-effect guard.
`kernel replay:` uses
actual ChatStore events, isolated AppData, the current executor and a fake Office
adapter: summary/pending replay, cancelled/stale confirmation, and interrupted
execution/materialization boundaries. Neither is COM/Office validation.

`agent:` / `protocol context:` cover the connected production service and its
external model-session owner, including native read-batch call/result pairing
through save/reload. Test construction requires a fixture-local ChatStore; it
never falls back to real user AppData. `model protocol:` retains endpoint-boundary
coverage under `IMaterializedModelProtocol`; no second parser/retry loop is added.

The harness uses a controller stub. The actual controller is checked by the
separate Windows build and delivery gate; [Phase 3B2 evidence](../../docs/stabilization/PHASE_3B2_KERNEL_CUTOVER.md)
records the historical MockDemo compile.

## Typed Excel read owner (Phase 7B)

`excel read:` covers exact native registration, Agent/manual routing, bound-session
owner-STA dispatch, every inspect selector, values/formulas/profile, explicit empty
cells, host-before-materialization range bounds, domain snapshot validation,
closed/switched target refusals and the shared HTML bind/refresh route. The fake host
implements the direct typed backend; a public read reaching generic host dispatch
fails, so dual execution is observable. These tests do not compile or execute real
Excel Interop. Protected sheets, large live workbooks, actual COM errors and
desktop/VSTO/native composition remain WQ-EXCEL gates. See
[Phase 7B evidence](../../docs/stabilization/PHASE_7B_EXCEL_READ.md).

## Typed Excel find/replace owner (Phase 11T1)

`excel find replace:` covers direct native ownership, the bound backend, literal and
regex matching, values/formulas and workbook/sheet/range/selection scopes,
`replaceAll`, bounds, verified no-op/change, exact pre-dispatch drift, post-dispatch
failure/read-back divergence and closed/switched-document refusal. The fake generic
host path rejects both public ids, so dual dispatch is observable. Real Excel COM,
protected/large ranges and partial assignment remain WQ-EXCEL gates. See
[Phase 11T1 evidence](../../docs/stabilization/PHASE_11T1_EXCEL_FIND_REPLACE.md).

## Typed Excel sheet lifecycle owner (Phase 11T2)

`excel sheet:` covers direct native ownership, add/rename defaults and active-sheet
selection, worksheet-name/collision rules, exact collection pre-state, verified
no-op/change, pre-dispatch drift, post-dispatch failure/read-back divergence and
closed/switched-document refusal. The fake generic host path rejects both public ids,
so dual dispatch is observable. Real Excel protected workbook structure, COM rollback
and case-only rename remain WQ-EXCEL gates. See
[Phase 11T2 evidence](../../docs/stabilization/PHASE_11T2_EXCEL_SHEETS.md).

## Typed Excel range mutation owner (Phase 11T3)

`excel deletion:` checks table/worksheet row and column deletion, header-clear
recovery, preservation of cells outside the table, invalid selectors, pre-dispatch
drift, missing/divergent read-back, non-retry, and exact prepared-state confirmation
(including new worksheet data outside the original window). It uses the production
runtime/domain with a fake backend. `tool run result: rejection is not cancellation`,
`tool runtime: cancellation`, `kernel: unknown call remains non-repeatable` and
`node tests/web/completion-guard.test.js` cover the separate cancellation projection.
No Office/VSTO build or real COM qualification is implied; Excel formula rewriting,
totals/empty-table COM behavior and protected/shared tables remain Windows evidence.

`excel range mutation:` covers exact native ownership for clear/sort/filter/format,
public defaults, separate values/formats clear behavior, whole-row sorting, filter
state, verified no-op/change, selector rejection, exact pre-dispatch drift,
post-dispatch failure/read-back divergence and closed bound-document refusal. The fake
generic host rejects all four public ids, so dual dispatch is observable. Real Excel
sort locale, AutoFilter normalization, mixed/conditional formatting, autofit,
protected ranges and partial COM effects remain WQ-EXCEL gates. See
[Phase 11T3 evidence](../../docs/stabilization/PHASE_11T3_EXCEL_RANGE_MUTATIONS.md).

## Typed Excel table owner (Phase 11T4)

`excel table:` covers exact native ownership, existing defaults, generated and
explicit names, style/header projection, source and workbook-collection bounds,
case-insensitive collision, exact pre-dispatch drift, post-dispatch failure,
read-back divergence and closed bound-document refusal. The fake generic host rejects
the public id, so dual dispatch is observable. Real Excel `xlNo` header/range
semantics, style localization, overlap/protection, rollback and partial COM effects
remain WQ-EXCEL gates. See
[Phase 11T4 evidence](../../docs/stabilization/PHASE_11T4_EXCEL_TABLES.md).

## Typed Excel chart owner (Phase 11T5)

`excel chart:` covers exact native ownership for `create_chat_chart`, `upsert_chart`
and `delete_chart`, chat-artifact source reads, create/update/delete/default/strict-mode
semantics, ambiguous names, collection/range bounds, verified no-change/change,
pre-dispatch drift, unknown-after-dispatch/read-back divergence, dry-run and bound
owner-STA/closed-workbook behavior. The fake generic chart route fails closed. These
tests do not execute Excel Interop; live series formulas, axes/types, generated names,
protection and partial COM effects remain WQ-EXCEL gates. See
[Phase 11T5 evidence](../../docs/stabilization/PHASE_11T5_EXCEL_CHARTS.md).

## Typed VBA mutation outcome (Phase 6D)

`agent: VBA mutation batch` covers a proposed patch/read/stale whole-write batch being
rejected before dispatch, followed by separate patch, read and corrected write
responses. Both real writes retain committed journal evidence. The existing
`vba: queued guard` and `vba: confirmed mutation` checks cover
competing chats and stale confirmation guards.

`vba: mutation` covers the typed service boundary and injected prepare, terminal,
backend, read-back and cancellation faults. The broader `vba:` slice reuses
restart, normalization, collision and not-found cases. These are fake-host ordering
checks, not real COM/VBE qualification. See
[Phase 6D evidence](../../docs/stabilization/PHASE_6D_VBA_MUTATION_OUTCOME.md).

## Whole-module VBA write owner (Phase 6E)

`vba: whole write service owns workflow` checks the direct typed owner, normalized
create, existence refusals before persistence/dispatch and same-source/different-type
create-race classification. The full `vba:` filter retains confirmation races,
upsert/update, invalid snapshots, read-back drift, journal correlation, UserForm and
VBE normalization coverage. See
[Phase 6E evidence](../../docs/stabilization/PHASE_6E_VBA_WHOLE_MODULE_WRITE.md).

## VBA delete owner (Phase 6F)

`vba: delete service owns workflow` checks the direct typed owner: guard and
component policy, dry-run without persistence/dispatch, live-source CAS hash,
verified absence, durable call correlation and protected-component refusal before
journal/dispatch. The full `vba:` filter retains stale confirmation, document
identity, backend success without deletion, rollback backup and COM policy cases.
See [Phase 6F evidence](../../docs/stabilization/PHASE_6F_VBA_DELETE.md).

## VBA restore owner (Phase 6G)

`vba: restore service owns workflow` checks the direct typed owner: a prepared
guard is mandatory and binds the exact backup id/live-source hash plus current target state;
backup substitution, altered backup evidence, stale target and incompatible type
stop before persistence/dispatch. It also covers dry-run, compare-and-swap replace,
verified source/type and durable accepted-call correlation. The full `vba:` slice
retains latest-backup pinning across confirmation, missing-target typed creation,
journal/CAS recovery and VBE normalization coverage. See
[Phase 6G evidence](../../docs/stabilization/PHASE_6G_VBA_RESTORE.md).

## VBA package lifecycle owner (Phase 6I)

`vba: package` covers the typed package owner and R41 boundaries: package validation,
marker+journal-aware state, one session lifecycle correlation, persistent and
temporary execution, explicit orphan cleanup, prepare/backend/read-back/terminal
faults, cancellation, marker drift/strip, probe/preparation and pre-run races,
post-prepare backend CAS, undeclared catalog components and mixed multi-component
recovery. Since 11J2 it also checks immutable `ToolPackageSource` v1 revision
pinning, exact native custom-id routing without a case alias, no dispatch before
confirmation, result v1 and conservative `unknown` effect after arbitrary macro
dispatch. The shared COM helper guard is exercised against a fake VBProject; the
full `vba:` filter retains document discovery, code-only UserForm, macro failure,
VBE normalization, journal/CAS and rename regressions. These are fake-host ordering
checks; real VBIDE/Trust Access/crash behavior remains Windows qualification. See
[Phase 6I evidence](../../docs/stabilization/PHASE_6I_VBA_PACKAGE_LIFECYCLE.md) and
[Phase 11J2 evidence](../../docs/stabilization/PHASE_11J2_VBA_PACKAGE_NATIVE_RUNTIME.md).

## Published prompt reads

Use `resource cutover: prompt resources` for metadata-only field discovery, exact
current/default source, Unicode/empty text, root-dependent evidence, historical
reads and explicit failures for corrupt/missing/oversized values. It verifies that
reads neither reload settings nor activate defaults, and rejects the retired reader.
`resource cutover: frozen prompt publication` and `chat: prompt save preserves global model`
retain capture, confirmation, stale guards, no-change and read-back coverage.
`tools: authoring intents are semantic` / `tools: prompt metadata is model-friendly`
check the switched guidance/catalog. The existing R61 baseline gap stays open;
only the retired prompt-reader row is removed, without a blanket baseline refresh.

## Tool Library source reads and mutation uploads

Model source inspection uses `resource cutover: tool source replaces definition read`
in `Program.ResourceGatewayTests.cs`: metadata-only host-visible discovery, complete
exact source, no callable admission, publication-dependent evidence, retained history
and no authoring-file fallback/Office discovery. It also rejects the retired reader
and its stored call. Existing `tools: agent CRUD preserves omitted fields` reads back
through the same generic resource tool; `tools: authoring intents are semantic`
checks the replacement guidance. CRUD also rejects invalid entry/supporting VBA
source before create/update storage; JS source/policy checks cover explicit
executor alternatives and rejection of mixed VBA/JS fields.
`tools: R61 built-in contract inventory` checks
the final reviewed [baseline](../../docs/stabilization/R61_TOOL_PROPERTY_INVENTORY.tsv)
(66 ids/66 host variants); its pass does not qualify real Windows/model behavior.

For Tool Library source reads and mutation uploads, use `tool editor:` in
`Program.ToolEditorResourceTests.cs` and
`tools: typed library mutations are revision guarded`, plus
`bridge: typed tools and skills` / `webview: restricts` for control wiring.
They cover the shared capacity, chat/consumer isolation, single-use and complete
typed UTF-8/hash validation, then the existing guarded authoring/publication owner.
Source checks cover committed custom/builtin snapshots, historical exact children,
document-local live drift, metadata-only delivery and capacity before live capture.
Builtin documentation checks verify committed CAS parts/exact children, no Office
discovery, historical downloads, runtime-registration mismatch and no generator
fallback after CAS corruption. `tests/web/tool-documentation-resource.test.js`
covers lazy complete Markdown delivery, one selected cache, integrity/bounds,
pending slots and late chat/selection/page/cancel responses.
`tests/web/tool-editor-resource.test.js` covers exact source contracts, hydration
neutrality, read-only failures, clean-cache bounds, dirty conflicts and late leases.
`tests/web/tool-package-actions.test.js` covers Save/pre-install transport, empty
and Unicode bodies, verified prefixes, later drafts and cancellation before/after
dispatch. The catalog's document read remains covered by
`host runtime: direct VBA catalog reads share access`. Real WebView2/Office
save/install/remove qualification remains open.

## Skill authoring native runtime (Phase 11K1)

For Library source reads and mutation uploads, use `skill editor:` in
`Program.SkillEditorResourceTests.cs` plus `skills: UI mutations use typed revision guards`.
They cover the published Gateway/CAS snapshot, complete UTF-8/BOM handling, source
drift/save guards, explicit unread-body preservation, built-in/core metadata-only
delivery, exact retained reads and shared capacity/lifetime. Uploaded core batches
and references also cover single-use consumption, chat/consumer isolation,
hash/shape/extent validation before domain dispatch, existing publication guards,
Unicode and empty reference text. Browser read/write, begin/fetch/late-dispatch
cancellation, frozen save plans and draft-conflict cases live in
`tests/web/skill-editor-resource.test.js`; `tests/web/resource-upload.test.js`
covers the shared writer. Use `bridge: typed tools and skills` and
`webview: restricts` for control DTO/cancellation wiring. These do not qualify real WebView2.

The `skills:` filter covers the complete current-package revision, core/reference
CRUD and collision/validation behavior. The CRUD case also verifies the four exact
core `common.skills_upsert/delete` and reference
`common.skills_reference_upsert/delete` registrations, Agent-only confirmed-write policy,
versioned result data, no dispatch before confirmation, verified change/no-change
and rejection of a stale prepared package. Focused `agent: confirmation`,
`protocol context:` and `kernel replay:`/`kernel recovery:` cases keep durable
confirmation and effect evidence across continuation and faults. See
[Phase 11K1 evidence](../../docs/stabilization/PHASE_11K1_SKILL_AUTHORING_NATIVE_RUNTIME.md).

## VBA rename owner (Phase 6J)

`vba: rename` covers the direct typed owner, both-name/source-type confirmation
guard, prepare/backend/read-back/terminal faults, cancellation boundaries,
post-prepare collision and read-only recovery of complete-before,
complete-intended and mixed states. `vba: rename intent is strict and atomic` keeps
the public schema/result/journal projection regression, while `vba: COM rename`
checks component identity plus source hash/type CAS in the shared host helper. The
full `vba:` slice retains module/package/VBE and serialized reconciliation coverage.
These are fake-host checks; real VBIDE/Trust Access/confirmation/cancellation remain
Windows qualification. See
[Phase 6J evidence](../../docs/stabilization/PHASE_6J_VBA_RENAME.md).

## Stabilization completion guard

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "completion guard:"
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "storage: turn lifecycle"
node tests/web/completion-guard.test.js
```

The guard tests extend `Program.AgentSafetyTests.cs` / `Program.SimpleAgentTests.cs`:
single-result legacy mapping, cumulative error/unknown precedence, kernel confirmation,
cancelled-summary replay and fresh-turn reset. Phase 9D5 adds `run view:` replay
equality, immutable wire, source-evidence separation and explicit pending state;
`tests/web/run-view-state.test.js` covers strict normalization, per-chat revision
ordering and integrated stale transcript/outcome rejection. The lifecycle test
continues to cover event replay and exclusion of UI projection from model transport.
The Node test loads the real static JS projection/render functions with a minimal
DOM and stubs only unrelated trace/media helpers. No npm dependencies are needed.
It verifies warning visibility outside collapsed trace, not browser layout or
production controller delivery. See [Phase 1C evidence](../../docs/stabilization/PHASE_1C_COMPLETION_GUARD.md).

## Stabilization causal trace

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"
```

Phase 1B uses `Program.SimpleAgentTests.cs` and `Program.SessionEventStoreTests.cs`:
ok/error/journal unknown, twentieth-response repair correlation, confirmation,
async scope isolation and harmless optional trace failures. Real host-neutral
runtime/store/journal run with fake LLM/Office. Controller wiring is not executed:
this harness uses `AssistantControllerBridgeStub`. Scope/projection marker tests
do not prove production bridge delivery or WebView rendering. See
[trace evidence and boundaries](../../docs/stabilization/PHASE_1B_CAUSAL_TRACE.md).

## Stabilization v5 contract / runtime IDs

`conversation v5:` covers strict ID-less parser/schema/arguments, runtime-owned
sequential batch safety, schema transport and the separate accepted-history reader. Model-owned
IDs are rejected; identical calls remain distinct ordered positions. `kernel:`
checks runtime allocation, collisions/invalid allocator output before acceptance,
and reuse of pending IDs without allocation. No tool retry or deduplication is added.

`protocol context:` checks full-turn IDs/origins across compaction and confirmation,
incomplete history, batch safety and all three result roles. `agent:` includes
complete HTML preservation in user/native history and native read-batch pairing.
`causal trace:` links each accepted runtime ID to the exact raw model attempt and
call position, including repair. `kernel replay:` uses the real event store.
`context: clone preserves values` checks fork URI rebasing without changing ISO
argument strings. Production controller reconstruction is source-reviewed here
but not executed by the stubbed harness; compile and delivery remain Windows gates. See
[R29 evidence](../../docs/stabilization/R29_RUNTIME_CALL_IDS.md).

The `preflight` filter covers incompatible full history in all three modes
and accepted-history forms, including suppressed/compacted-away records; incomplete
confirmation, current/new chat success, and zero raw attempts/repair/progress for
missing CallContext. Snapshots prove no history/checkpoint/run mutation on rejection.
The real shared confirmation guard is exercised, but production controller ordering
and manual-compaction wiring are reviewed in source only; controllers are stubbed
in this harness. Existing confirmation fixtures now carry the current LastRun
marker and mandatory origin that production writes. Historical cutover evidence remains
[2C3C](../../docs/stabilization/PHASE_2C3C_V3_CUTOVER.md); current gates are in R29 evidence.

Phase 2C3A extends the two `model compatibility:` cases across both formats and all
three tool-result roles, strict sentinels/status/casing and one raw attempt per
probe. Phase 2C3C switches runtime/probes together through ModelProtocolWire and
rejects native refusal even when JSON content matches the expected sentinel.
See [2C3A evidence](../../docs/stabilization/PHASE_2C3A_WIRE_OWNER.md).

Phase 2C3B replaces the obsolete destructive-reset characterization with prompt
preservation/review tests: missing/old/current/future markers, real settings
load/save, failed approval, explicit reset, and neutral Agent/Chat/Plan/continuation
entry guards. SettingsService is now source-linked; ProtectedSecretStore remains
excluded and a test-only stub throws on any secret-file read/write. Absent fixture
secrets are supported; DPAPI/protection changes are not being qualified.
The existing typed-settings bridge test uses the controller stub, not production
controller execution. `node tests/web/prompt-review.test.js` verifies actual form
serialization and action handlers with minimal DOM/transport substitutes, including
cancel/failure/reset and Plan preservation; it does not verify WebView layout.
R29 introduced v4/schema 13; Phase 4B introduced Tool Result v1/schema 14; Phase 8B
uses schema 15 for atomic callable-pack admission and no-eviction guidance; Phase 8C
uses schema 16 for durable turn-scoped reconstruction; R61/11O1 uses schema 17 for
the semantic Resource/Capability boundary; R61/11O2 uses schema 18 for semantic
questions, Plan documents and Task Lists; R61/11O3 uses schema 19 for semantic HTML
authoring and accepted-read binding; the user-requested R61/11O1 correction uses
schema 20 for whole resource reads and the direct bound VBA-project target;
R61/11O4 uses schema 21 for semantic Prompt/Tool/Skill authoring; R61/11O5 uses
schema 22 for semantic VBA/macro intents; the user-reported readiness/completion
correction used schema 23 for prerequisite skill/tool selection, Task List lifecycle,
root tool arguments, dependency order and evidence-reconciled completion. Schema 24
makes the six-stage operating workflow and prompt/skill/tool authority split
explicit, strengthens all built-in skill completion criteria, and adds exact
skill-reference-to-catalog validation. Schema 25 tightens successful finish around
closing active Task Lists and HTML bound-data render evidence. Schema 26
adds explicit v5 `final` intent. Current schema 32 requires `final=true` for every
empty-call answer and repairs `final=false` with no calls before acceptance; it also
keeps the final read-back, regression check and quality handoff decision before a
successful final. Tests
preserve, review, or reset saved older/future markers explicitly. JS review behavior
is unchanged.
See [2C3B evidence](../../docs/stabilization/PHASE_2C3B_PROMPT_REVIEW.md).

Phase 8D moved the original resource data plane to native handlers. R61/11O1 now
publishes semantic `common.resources_find/read/search/outline`, keeping provider routing,
revision-pinned `ResourceRef` and continuation state inside runtime/durable evidence.
Use `tool runtime: native resource tools`, `resources:`, the focused model-projection,
media/replay cases, and `harness: production projects include all source files` to
cover the intent boundary, internal guards and old-style project inclusion. See
[8D evidence](../../docs/stabilization/PHASE_8D_RESOURCE_DATA_PLANE.md) and the
[R61 audit](../../docs/stabilization/R61_TOOL_CONTRACT_AUDIT.md).
`resources: original text` covers >10 MiB one-line navigation, extraction completeness,
cross-part search, derived-view repair and C#/VBA/TS outline hints.
`authored file:` covers 10 MiB copy/patch/export, code copied under a requested `.md`
name with exact bytes, immutable originals, stale and
ambiguous edits, large `.md`, UTF-16 BOM/CRLF, semantic navigation, viewer paging,
bounded Run Changes hunks and restart. `run changes:` retains the smaller-source
contracts; `node tests/web/run-changes-layout.test.js` checks hunk rendering when
Playwright is available.
`authored file: HTML viewer` covers created/patched HTML and its historical reads
after restart, version grouping, and exact source pages of a copied UTF-16 HTML.
UTF-16 chat reopening remains a separate [open risk](../../docs/stabilization/RISK_REGISTER.md#authored-utf-16-file-blocks-chat-reopening--2026-10-14).
`node tests/web/artifact-json-viewer.test.js` also checks automatic inert source
loading for authored HTML without uploaded-original controls.

The schema-20 correction makes `common.resources_read` return one complete bounded
representation or an explicit error. Broad VBA inspection starts from
`RUNTIME_CONTEXT.document.vba_project_target`; unfiltered VBA find is only fallback
discovery and keeps the project target first, while queried find remains filtered.

R61/11O2 replaces Plan create/update with `common.plan_doc_save`, replaces the
three Task List lifecycle ids with `common.task_list_set`, and removes caller-owned
question/option/plan/artifact/list/step/revision identity. Use `plan mode:`,
`plan document:`, `task lists:`, the focused model-projection case and the R61
property inventory. The services still bind exact active revisions and stable ids
internally; incompatible retained calls require a new chat/reset.

R61/11O3 replaces HTML inspect/set-active/general upsert with internal diagnostics/
selection and separate semantic file/data writes. Patch is exact-only; delete takes
one readable target; bind reuses the latest successful eligible accepted Office read
from the current run without nested source arguments; refresh takes only an optional
name. Use `html`, `tools: R61 built-in contract inventory` and
`node tests/web/html-workspace-export.test.js` plus
`node tests/web/html-workspace-echarts.test.js`. A refresh must capture the reread
JSON in a new active HTML revision; standalone export embeds that exact snapshot and
the pinned local ECharts runtime, with no live Office bridge. Prompt schema 19 and
accepted-history validation introduced the HTML switch; schema 20 added
whole-resource correction.

R61/11O4 makes prompt save one exact key/value mutation, removes model-facing
`common.tools_validate` and Tool read list mode, reduces Tool upsert to semantic
package source/docs, and splits Skill core/reference mutations. Runtime derives
manifest metadata, applies conservative authority and rejects plumbing-shaped
custom arguments without `Domain identity rationale:` both on validation and load.
Use the focused authoring filters in the matrix above plus the R61 inventory.
Prompt schema 21 was the 11O4 boundary; its retained calls remain valid only when
they also satisfy every later switched-family contract.

R61/11O5 separates identity-preserving VBA rename from whole-source write, removes
the fixed patch operation and raw backup identity from model arguments, resolves
restore by readable target or latest-for-module inside runtime, and hides mutation/
backup ids, hashes and guards from model results. Use `vba:`, the focused semantic
contract case, the VBA Agent/resource/facade cases and the R61 inventory. Prompt
schema 22 was the 11O5 boundary. Current schema 27 requires explicit prompt
review/reset before Agent/Plan execution; retained pre-switch calls still require a
new chat/reset.

## Full suite

Run the complete harness only for broad cross-cutting changes:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

COM/VSTO behavior remains Windows-only: validate with Windows x64, Office and VS 2022.

Tool editor smoke after pipeline removal: `node tests/web/tools-editor.test.js` exercises VBA draft creation, editor source synchronization and built-in clone rejection against the shipped HTML IDs. This is not Windows/WebView layout validation.

Tool Library layout: `node tests/web/tool-library-layout.test.js` requires Playwright
(resolvable through `NODE_PATH`) and Chromium; `BROWSER_EXECUTABLE` optionally selects
installed Chrome. It checks actual shipped HTML/CSS and CodeMirror geometry at four
pane/window sizes in both themes. Set `LAYOUT_SCREENSHOT=/tmp/library.png` to save
per-size/theme/tab screenshots. This does not qualify Windows WebView2.

Typed package-action normalization: `node tests/web/tool-package-actions.test.js`
checks that install/remove accepts only result contract v1 with exact lowercase
fields/effects and rejects the deleted PascalCase compatibility shape.
