# RNAssistant Harness

Host-neutral tests run on this machine without Office COM. Locate the relevant test first; do not read or execute the full suite by default.

## External Browser P5a — 2026-11-11

External Browser P5a: `external browser:` runs two focused host-neutral checks for
navigation/bounds admission and typed bridge trust. `tests/web/external-browser.test.js`
uses production Shell/Dockview with synthetic native responses for split/restore,
hide/close/reopen, overlays, focus dispatch, narrow/zoom and stale/late cleanup.
`BROWSER_P5_SCREENSHOT` optionally sets a wide/narrow screenshot path prefix.
Neither test builds or qualifies WinForms/Office; [Windows gate](../../docs/browser/05_TEST_MATRIX.md#p5a).

## Find a test

P6a semantic read: `external browser:` runs four checks (admission, bridge,
Gateway/CAS/model delivery, ownership/stale/bounds/cancellation). Pair with
`workspace: Office target stays pinned` for the delegated read and pre-capture
document conflict. Build once; the second filter uses `--no-build`.
`tests/web/external-browser-read.test.js` checks the embedded capture script in an
isolated Chrome world, privacy omissions and bounds; `external-browser.test.js`
also checks owner binding only at Open. No Office/VSTO build is required here;
[native delivery is still open](../../docs/browser/05_TEST_MATRIX.md#p6a).

P6b adds three checks to `external browser:` (seven total): delivered-read admission,
prepared/dispatch/terminal evidence, unknown outcome, pending confirmation owner guard,
and the shared bound Workspace handler. Pair with `tests/web/external-browser-action.test.js`
for the actual local probe and isolated-world element/user/read-back guards. The capture
fixture remains relevant because each read now has a distinct runtime observation identity.
WinForms queue timeout/controller delivery still requires Windows; [scope](../../docs/browser/05_TEST_MATRIX.md#p6b).

P6d adds an eighth `external browser:` check for origin patterns/settings. Pair it
with `agent pause: authority` for user-only pause, late resume/newer pause and Stop.
The existing action fixture now installs the real isolated control script before
navigation; a new read cannot release user control. `external-browser.test.js`
checks the production manual banner/resume revision at wide/narrow/125%. Windows
CDP event delivery and Office threading remain separate evidence.

P6c reuses those seven checks and verifies that presentation follows terminal publication,
including Unknown after channel loss. The action fixture also checks the actual cursor
motion, idle removal, manual input/late results, capture/focus isolation, narrow/125%
and reduced motion. `BROWSER_ACTION_REVIEW` saves page screenshots;
[review steps and native gaps](../../docs/browser/05_TEST_MATRIX.md#p6c).

Use the registrations in `Program.cs` to choose a filter before running anything.
Locate the matching fixture, then read its relevant section below.

```bash
rg -n 'Test\(".*resource' tests/RNAssistant.Harness/Program.cs
rg -l -F 'TargetMethodOrBehavior' tests/RNAssistant.Harness -g 'Program*.cs'
```

## Native window presentation — 2026-11-11

`host window:` runs three focused host-neutral checks: return points, minimize,
focus generations/pin/shutdown, preference merge/schema, and typed bridge trust
while an exclusive target switch is pending. No native HWND/Office build is used.
`tests/web/host-window.test.js` (Playwright, optional `BROWSER_EXECUTABLE`) checks
production Shell/Dockview with synthetic native snapshots: retained chat/edit/iframe,
Full placement across Compact and both state/resize orders, native visibility,
deleted identities, initial bootstrap, dirty/failed-draft shutdown guards,
keyboard/narrow/zoom and Demo geometry.
`RNA_WINDOW_REVIEW=/tmp/window` optionally writes two screenshots and measurements.
Native frame/DPI/focus/capture and Windows compilation remain separate evidence.

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

Workspace receipt/reconnect, run/approval lifetime and recovery Web tests share
[`preview-session.js`](../web/preview-session.js): `withPreviewPage` owns the preview
server, browser and cleanup. Callbacks retain scenario setup/assertions; `chatNode`
addresses the single visible Chat View. Split-view tests need explicit target scopes.
`RNAssistantMockBridge.emit` delivers native fixture envelopes without source rewriting.

## Storage/context optimization — 2026-11-10

`storage optimization:` selects isolated rotation/tail/protection tests, common
retained results/context, Office/Workspace adapter parity with a scripted provider,
cross-run file guards, two-process index repair, atomic send/spawn/run/control,
optional diagnostics, verified small-body cache/text windows and canonical CAS
closure/backup/restore. Maintenance fixtures
cover missing cursors, nested refs, unknown effects and a second-process publication.
No Office/VSTO/live model or DPAPI qualification.
New storage is not enabled in production. `RNA_STORAGE_OPT_BASELINE=1` enables the
old aggregate/provider baseline; `RNA_STORAGE_OPT_COST=1` enables the comparable
H=100/1000/10000 curve. Leave both unset for routine checks.
[Evidence and open gates](../../docs/audits/storage-context-optimization.md).

`storage optimization: bounded context exclusion receipt` compares H=100/1000/10000
with fixed delivered content and checks the exclusion sample/counts. Pair changes
to this path with `model context: compiler presentation`, the two `context continuity:`
cases for active/archived selection and final read retention, and the optional
diagnostics/mandatory manifest check. This is not bounded full-session restart evidence.

`storage optimization: frozen skill snapshots without JSON getters` checks nested
copy isolation and catalog getter allocations; selected-skill/dependency/dispatch
regressions cover the common consumers. The old composition role expectation is
recorded separately in the storage qualification risks.

`storage optimization: typed model snapshot isolation` checks constructor/getter/
repair isolation through nested mutable metadata and measures repeated snapshot
access. Relevant regressions: request snapshot/budget, compiler presentation, exact
read cost, saved wire composition, active/archived selection, final read retention,
skill exact dispatch and the scripted Office/Workspace retained-result parity case.

`storage optimization: bounded live run metadata` measures 12 updates/reads at
H=100/1000/10000. `storage optimization: live run` checks canonical catalog/detail,
live recovery ownership and exact Inbox Stop without queue creation/replay. Relevant
regressions: run lease/duplicate confirmation, invocation ownership, catalog reads,
document-key migration, Workspace Stop, stale cancellation and shared run recovery.
This does not qualify full-session persistence, detail I/O or native Office delivery.

`storage optimization: read body` checks repeated preparation cost and invalidation
of content, call/tool binding, transport headers, omission, receipts and payloads.
Pair with chronological read retention, active/archive body selection, budget folding,
bounded working-set/VBA coverage, compiler presentation, exact read cost and the
scripted host-parity/retained-guard check. Metadata reuse ends with the compilation.


`storage optimization: indexed context` covers a backlog above 128 unknown outcomes,
compact prompt/technical budget, addressed details and exact-call replay guards,
scope isolation, lost index acknowledgement and rebuild with externalized arguments.
Recovery reduction is compared with typed contracts, including earlier/failed reads,
partial/whole coverage and changed dependency revisions. Restart compares the actual
retained-source request to archive selection at H=100/1000; set
`RNA_INDEXED_CONTEXT_COST=1` to include H=10000 (durable fixture setup is slow).
The ready-index measurement excludes fixture writes and repair. Pair schema changes
with retained-result, shared-index/process repair, run-checkpoint, late-outcome and
canonical backup/restore cases. These are isolated storage checks, not a production
cutover or Office qualification.

`storage: runtime` selects six checks: existing final-response persistence and five
runtime-boundary cases (history-free state, own-append faults, delta ordering,
capture and backend costs at H=100/1000/10000). The capture probe excludes storage;
the backend probe explicitly reports its remaining full-history materialization.
For consumer changes, pair with host-parity/retained mutation guards, durable model
reference allocation, historical/restart/fork evidence, indexed canonical selection,
resource identity, message state transitions and saved-wait/Workspace-cold cancellation.
These host-neutral checks do not qualify Windows/Office delivery.

P1 common fact projection: `storage: shared runtime accepted projection` checks
exact batch origin, first-call reasoning/usage, large argument CAS and literal
string hydration after restart. Use actual consumer filters `agent: native read
batch keeps paired replay`, `workspace: accepted call history survives a new turn`,
`v8 recovery: accepted tool reasoning survives reopen`, saved-wait cancellation and
host-parity/retained mutation guards. For fault paths select exact replay filters
(`failed append prevents dispatch`, `interrupted write remains unknown`,
`known unprojected effect survives recovery`), not the whole historical group.
Eight independently reproduced gaps are recorded in [Backlog](../../docs/stabilization/BACKLOG.md#host-neutral-harness-baseline-failures--2026-10-13).

## Optional assignments (4b, 2026-11-10)

`agent assignments:` checks explicit result correlation (including failed/declined),
foreign/unapplied/plain-message rejection, early/applied reports and restart,
one terminal report with destination/receipt faults and no replay/replacement,
50-item paging without peer wake or queued-body exposure, human FIFO, scripted
Office Plan/Deny in both JSON formats, Workspace spawn/resolve/wait and the actual
ChatStore/CAS saved completion wait resumed in its original Run. A terminal Run
does not resolve the assignment. `agent communication:` covers existing ordinary
messaging/control/wait boundaries; `tool pack: core and atomic` validates admission.
These are host-neutral checks, not live-model or Windows/Office qualification.
The initial verification uses an isolated d0ab9d09 plus assignment changes; ongoing
storage work in the shared checkout is not included in this evidence.

## Chat overviews and addressed rename (4a.2, 2026-11-10)

Filter `chat overview:` covers bounded SQLite samples/bytes, private content filtering,
existing published summary, incompatible metadata-only reads, restart/stable projection,
model budget, unsupported history selectors, exact scope/disabled/delete guards,
no implicit execution load/wake, busy lease/stale revision, metadata preservation,
post-write unknown/no replay, and immutable evidence retention through replay/CAS GC.
Scripted Office and Workspace model loops exercise core.find/read + chat.rename v2.
Regressions: `chat rename:`, `agent groups:`, `tool pack: core and atomic`.
Cold full-index rebuild cost, live-model classification and native Office/controller/
WebView2 notifications remain separate evidence; no Office/VSTO validation here.

## Agent send batches — 2026-11-10

`agent communication: send batch` checks schema/parser parity, 32-call bounds,
local policy admission and three real Inbox deliveries from one model response in
both JSON modes: Office service under Plan/Deny, Workspace via scripted HTTP with
one recipient run per message. `kernel: agent message batch`,
`kernel: mutations require separate responses` and `kernel: cancelled batch` cover
unknown delivery without replay, ordinary/mixed mutation refusal and cancelled tails.
Use the host-neutral Harness; no Office/VSTO validation is required on this host.
These six cases plus `conversation v8: batches` and `tool pack: core and atomic`
pass locally (eight total); this does not qualify Windows/Office or a real provider.
The standalone [codec/schema runner](../RNAssistant.ProtocolTests/README.md) checks
wire shapes; runtime send/batch evidence belongs to the Harness cases above.

## Named chat groups (4a.1, 2026-11-05)

Filter `agent groups:` covers resource-authority CAS, duplicate memberships,
remove/delete without writing chat history, creator deletion, restart + GC,
competing revision mutations, add/delete races, no resurrection, exact tool binding
and Plan policy, group-scoped discovery and 55-chat paging with no peer startup.
Regressions: `storage: header`, `tool pack: core and atomic`, `agent communication:`
(8 tools when enabled). No Office/VSTO validation on this host.

`node tests/web/agent-groups.test.js` runs the production UI with intercepted typed
bridge fixtures: Office/Workspace/plugin share tree/actions; two links use one tab,
creator nesting, remove one/last link, delete group, stale/unknown responses,
collapse/menu keyboard and narrow/125% bounds. Screenshots go to /tmp. The previous
agent-communication fixture still checks settings/feed/tab-independent execution.
These checks don't qualify native Office controller/WebView2 delivery.

Group-dialog follow-up (2026-11-07): the same browser fixture covers the shared
in-app input/confirmation component instead of accepting `window.prompt/confirm`.
Check create/rename/delete semantics, blank/unchanged names, Escape/Enter/IME,
focus return after tree replacement, one submit while pending, visible errors and
revision conflict without retry, reset/late replies, and Office/Workspace/plugin
at narrow widths and 125% zoom. Native dialog interception is a regression failure,
not an input mechanism. Run browser fixtures sequentially with Playwright and an
allowed Chromium executable (`BROWSER_EXECUTABLE`); current execution evidence is
tracked in [UI Migration Plan](../../docs/ui/UI_MIGRATION_PLAN.md#встроенные-диалоги-групп--in-review-2026-11-07).

`node tests/web/agent-group-dialog-guards.test.js` runs 12 browser-independent VM
checks against the shipped group adapter: pinned scope/revision, stale form and
response lifetimes, one pending write, retained uncertainty and no replay. It does
not qualify dialog keyboard/focus, native top-layer behavior or viewport geometry.

## Active-step admission — 2026-11-05

`agent admission:` covers FIFO, lowering/increasing/zero, cancellation/grant races,
queued pause/Stop, failed queue/admitted journal, shared Office/Workspace settings,
confirmation/read-back occupancy, serialized diagnostic state and no nested permits.
Regressions: `agent pause:`, `agent cancel:`, `agent user control:`,
`kernel: confirmation`, `kernel: durable wait continuation`,
`kernel: timed wait progress`, cancelled/denied/stale confirmation and `agent communication:`.
`RNA_AGENT_BENCH_WORKERS=8 RNA_AGENT_BENCH_SLOTS=1` with `agent concurrency:`
forces eight recipients through one slot, retaining same-Run wait/resume. Without
SLOTS, default 4 is used; zero is unlimited. Peak model requests must not exceed
the chosen cap. This controls concurrency, not an aggregate budget.
Web: agent-communication covers Settings/Office queued progress; workspace-chat-progress
covers queued pre-model/saved Run, cancellation, admission and project/plugin streaming.

## Agent participant limit and concurrency — 2026-11-05

`agent communication:` covers default 8/0/off/1/root-only, typed settings persistence,
read-only old-key migration, lowering the current cap, recursive/recovered membership,
concurrent recruitment + spawn, and 25 repeated replies without a message quota.
Regressions: `agent pause:`, `agent cancel:`, `agent recovery:`, `agent user control:`,
`tool pack: core and atomic`, `bridge: typed settings`.
`tests/web/agent-communication.test.js` checks Settings/reset/narrow zoom, unchanged
sender navigation, no obsolete budget RPC/banner and tab-independent runs.

`agent concurrency:` uses real Inbox/ChatStore/Office service with a fake host and
60 ms scripted model, for 1/2/4/8 recipients plus one idle root. Two model calls per
recipient (wait + same-Run wake), persisted inputs, terminal outcomes and zero status
LLM calls are asserted. MEASURE reports p50/p95/p99 at adapter boundaries and request
characters, not provider/network latency or tokens. Run it alone for timing; this
small fixture is not an SLA/load qualification. `RNA_AGENT_BENCH_WORKERS=8` narrows
failure diagnostics. `resource cutover: concurrent catalog initialization` separately
checks waiting for the existing first-publication lease and reuse of its head.
Production Controller/COM/WebView2 and live-model costs remain separate evidence.

## Browser toolbar / Discuss — 2026-11-05

The shared icon toolbar uses the existing `browser-saved-views` fixture for exact Chat
View reuse, separate Dock groups, draft retention and retained iframe. `browser-views`
checks live annotation intake; `plugin-view-selection` checks the pinned recipient
after another chat is selected and preservation when returning from plugin manager.
`html-editor-views` covers source modes/buffers/guards and current publication refresh:
manual reload, document-scoped notices from another agent, hidden activation, notices
during a read, duplicate generations, unrelated scopes and dirty-buffer preservation.
`browser-saved-views` also checks full-width address focus without iframe reflow,
metadata and narrow tool menus. Its layout actions use keyboard activation; fast
pointer routing over an iframe remains an open gap. `browser-lifetime` covers restores.
Run sequentially with the existing Playwright setup. No C#/Office build is needed.

Saved history follow-up (2026-11-10): `shared HTML: saved history` checks 52 actual CAS
publications by another chat, cross-document reads, 50-item pages, stale continuation
and unchanged selection/links. `bridge: typed artifact viewer` covers the addressed
`getSavedHtmlHistory` DTO. `browser-saved-views` checks missing captured head, fresh
history on reopening the menu, late response rejection, retry from the first page,
another active chat and retained iframe/input. Its history clicks run above Source;
`BROWSER_POINTER_PROBE=1` separately reproduces fast layout-menu clicks above iframe.
This does not qualify Windows/Office delivery or close the pointer-routing risk.

Pointer follow-up: the opt-in probe asserts parent delivery of pointerdown/up/click
and prints bounded event recipients for each iframe, so a misrouted click fails at
that action rather than timing out on the next menu. These are automated Chromium
events, not physical mouse evidence. `browser-views` opens narrow toolbar actions
through the existing Tools button; no pointer delay or production workaround was added.

## Universal Browser P1 — 2026-11-04

`node tests/web/browser-saved-views.test.js` checks shared read-only artifact address,
Preview/Source controls, retained file/page/input, 32K source pages, reload/late cleanup
and ready surfaces at 1280/100% and 420/125%. Run with the existing bundled Playwright
via `NODE_PATH` and optional `BROWSER_EXECUTABLE`. Companion `browser-views.test.js`
checks live HTML/plugin annotations and `browser-lifetime.test.js` checks five Dock
layout restores. Run these browser fixtures sequentially; no C#/Office build is needed.
Baseline/final evidence and remaining gates: [Browser test matrix](../../docs/browser/05_TEST_MATRIX.md).

## Finish correction — 2026-11-04

Run one substring filter per invocation: `kernel: finish correction`,
`kernel: completion rejection`, `model refs:`. These cover bounded quoted diagnostic
data, unchanged-rejection stop through reflection/restart, narration and blocked
replay, reset on input/tool facts, corrected citations without tool replay, and actual
Workspace HTTP requests in both JSON modes. Existing partial recheck remains bounded.
The focused 20 cases pass; unrelated `kernel: external unknown` and five
`kernel replay:` failures reproduce on the pre-change Harness and remain documented
in [RISK_REGISTER](../../docs/stabilization/RISK_REGISTER.md#finish-коррекция-и-ограничение-цикла--2026-11-04).
No real-model, Windows/Office or full Harness qualification is implied.

## Shared HTML targets — 2026-11-03

`shared HTML:` covers explicit creation, same-document discovery across agents,
independent editor selection, sibling-file preparation, same-file ABA conflict,
historical/foreign denial, data binding/freezing, assets, publication recovery,
restore/redo, sequential model after-source and restart/run attribution.
`html tools:` uses the public create/write contract for source-observation guards,
per-call receipts, no-op, binding sourceTarget/table and model-visible read → write.
The selected-view checks also cover a foreign agent's unlinked new snapshot,
recovery to the same publication and refusal of missing/nonempty unpublished views.
Production Controller code is excluded from Harness; its source review is separate
from the still-open Windows build/runtime evidence.
`html editor:` and `html actions:` cover native stale guards, unknown publication,
uploads and typed bridge. Web `html-workspace-source` checks exact lazy hydration;
`html-editor-views` checks dirty drafts and lifecycle. These host-neutral checks
are not Windows/Office or live-model qualification.

## Chat naming — 2026-11-03

`chat rename:` covers exact policy and invalid/foreign target refusal, Office
kernel persistence in Agent/Plan with document writes denied, placeholder/fallback
precedence after reload, and Workspace/plugin source runs with smart naming enabled.
`workspace: first message names the chat` retains automatic naming/manual rename
race coverage. These are host-neutral checks, not Windows/Office qualification.

## Agent communication — 2026-11-03

`agent user control:` covers typed pause/target snapshots, stale/revision/lease/scope
refusal, write failure, user cancellation after restart without LLM/tools, exact live
resume and no live release in a recovery batch. Three cases use real Office service
stores/fake adapter and Workspace scripted HTTP. Regressions: `agent pause:`,
`agent cancel:`, `agent recovery:` (14 cases). Production Office Controller is excluded.
`tests/web/agent-run-controls.test.js` covers the common Office/Workspace/plugin row,
exact scope/Run/revision, stale/unknown without retry, draft, busy/late close cleanup,
narrow/125% and shared button geometry. Use existing Playwright via NODE_PATH and
optional BROWSER_EXECUTABLE; run browser fixtures sequentially. `chat-inbox.test.js`
and `agent-recovery.test.js` retain queue controls and ordinary +/file/compression paths.
Windows/Office/WebView2 and visual acceptance remain separate.

`agent pause:` covers safe model/tool/read-back boundaries, undispatched read batch,
finish admission race, the existing wait deadline, retained budgets/unknown inspection,
stale CAS, resume-before-wait, replacement pauses/lifetimes, human authority, exact
policy binding and prepared-only receipt. Real stores are used for Office service
crash/reload/cold cancellation; scripted Workspace HTTP runs pause/resume, user Stop
and restart recovery without opening views. No UI/controller/COM qualification is
implied. Relevant regressions: `agent cancel:`, `agent communication:`, `agent recovery:`,
`inbox:`, `kernel: durable wait`, `tool pack: core and atomic` and `unified policy:`.
The seven pause cases and these 52 regressions pass on the host-neutral Harness.

`agent cancel:` covers live/cold Workspace model wire without views, cold Office
service projection, exact Run/lifetime guards, competing leases, partial append,
post-flush/receipt failures and no replay after restart. It checks preserved input, budgets,
result roles and unknown effects. Relevant regressions: `agent communication:`,
`agent recovery:`, `inbox:`, `unified policy:`, `tool pack: core`, `kernel: durable wait`.
Production Office Controller and Windows qualification remain separate.

`agent recovery:` covers one explicit selection across saved waits: current scope,
exact run/revision, lease/binding refusal, partial receipts, stale command replay,
unselected chats, Stop before queue binding and bounded 20-chat pages without model
calls. Its scripted Workspace case runs two concurrent waits, closes the runtime,
reopens it and resumes both original Run/call IDs with one command and one charge
per wait. Relevant regressions: `agent communication: durable`, `inbox:`.
`tests/web/agent-recovery.test.js` exercises the production common menu, selection
across pages, exact Office/Workspace commands, partial/uncertain receipts, narrow/125%,
keyboard and stale close cleanup. `active-chat-runs.test.js` covers the shared
ActionMenu detail layout. Real Office/COM/controller qualification remains open.

`agent communication:` checks the real Workspace A → B → A path against a scripted
HTTP provider (including typed settings and model wire), shared participant limits,
idempotent receipts, restart, stopped queues, tab-independent discovery,
immutable sender provenance, uncertain delivery and accepted work after sender
receipt failure. The real model wire also loads the shared communication skill.
Zero is checked through typed settings and disk persistence, live old-handler denial,
Office/Workspace catalogs and re-enable without resetting membership.
The native Office queue check uses real Application/Kernel/bridge owners with a fake
Office adapter and scripted model: bad-address recovery, same-run `agents.wait` wakeup,
late next-turn delivery and exact inbox progress routing. Wait boundaries cover timeout,
cancellation, total budget, Stop, human FIFO and unknown-effect pause. Stable short
addresses, self discovery and individual sender provenance are checked across reload.
This does not qualify real Office/COM or remote-model latency.
The 2026-11-04 slice adds ordinary-thread spawn without views, effective model/policy
inheritance, no history copy, exact operation reuse/conflict, partial/unknown initial
intake, recursive/recovered/concurrent root budget, cross-scope refusal and policy
ceiling checks. A real Workspace model-wire run creates and runs a child without UI;
global automatic approval cannot widen the sender's accepted policy. 17 targeted
checks pass. The production Office Controller remains outside Harness compilation.
Correlation slice 2a extends this to 22 checks: exact reply sender/recipient and
operation identity, original causal chain after unrelated input, any/all, early
and applied replies, partial timeout and event wake, restart without duplicate
delivery, human/policy/unrelated-input priority, and a real Workspace
spawn(expect_reply) → send(reply_to) → wait(requests) exchange. The generic round-trip
fixture accepts both valid arrival boundaries (current or next Run), not fixed timing.
The compact-header check compiles restored peer inputs through the real Office composer,
Core compiler and LLM wire builder in both JSON modes: exact sender/correlation, escaped
titles, unchanged durable body, no display scope, and one runtime authority instruction.
The Workspace round trip also checks a real request after messaging is disabled;
retained peer history cannot lose its authority boundary or prescribe unavailable tools.
The earlier 2a slice tests durable correlation; unfinished waits are covered below.
Slice 2b.1 adds `agent communication: repeated wait timeout progress is persisted`:
four real one-second waits through Office service/ToolRuntime/kernel, then ChatStore
reload. The total is now 23 communication checks. `kernel: timed wait` covers replay,
ordinary run budgets and preservation of unchanged-read/error/unknown guards even
when the result JSON is identical. `tool runtime:` adds 15 relevant regressions.
Slice 2b.2 adds `agent communication: durable Office wait recovery` (interruption after
real CAS commit, fresh Inbox/service, explicit same-call continuation) and
`agent communication: durable Workspace wait recovery` (scripted HTTP, graceful
runtime close, fresh service, empty-queue Resume). The latter checks bind-before-resume
against the current leased projection. There are now 25 communication checks; the
correlation test also rejects a reply newer than a restored deadline.
`kernel: durable wait` checks JSON restore, one start/terminal/charge, unchanged IDs,
stale cursor rejection, changed binding and cancellation. Previous-turn calls remain
model context but never enter the current Run's accepted IDs/counts/finish evidence. Ordinary stopped/confirmation
continuations, cancellation and ToolRuntime gates remain separate regression filters.
`tests/web/chat-inbox.test.js` checks the reused resume control with an empty waiting
Inbox and its absence during live waiting. Windows Office controller and document
session rebinding are still separate, unqualified evidence.
Quota reads use existing indexed canonical events without trace
materialization. Relevant regressions: `inbox:`, `workspace: queue`, `unified policy:`,
`tool pack: core`, `settings: invalid numeric`, `storage: indexed input journal`.
Each Harness invocation accepts **one** substring filter.

`node tests/web/agent-communication.test.js` uses Playwright (bundled `NODE_PATH`,
optional `BROWSER_EXECUTABLE`) to compare agent/human bubble geometry, exercise budget
settings/continuation, reload races and close/rebind without presence calls, and inspect narrow/zoom.
Zero rendering and hiding continuation against a stale response are covered as well.
Office inbox reasoning/streaming is injected into a separate mounted recipient; short
sender labels are checked with the common bubble. Temporary
screenshots go to `/tmp/rnassistant-agent-communication*.png`. It checks bubble bounds,
not full Shell zoom qualification; the existing retained-overlay clipping remains
in RISK_REGISTER. `independent-chat-views` passes; `model-settings` still has a
singleton-selector gap. Inbox polling coverage now uses mounted views as described
below. Windows/Office and live models are separate.

Chat parity follow-up: `plugins: workspace uses common message attachments` checks
scoped upload/CAS, foreign draft/lease denial, exact links, idempotency and bounded
UTF-16 reads without implicit large-text delivery. `workspace: queue uses common
owner and explicit resume` checks cross-service queue intake/edit, next-turn delivery,
exact runtime metadata/Stop, retained work after cancellation and explicit resume.
Web `workspace-shared-draft` uses mounted ChatViews for attachment cards, late upload,
queue receipt/editor/resume and source isolation; `active-chat-runs` covers background
close, exact jump/Stop, pending/stale controls, content width, narrow/zoom and keyboard.
No real Office or model; baseline failures are recorded in RISK_REGISTER.

JSON-profile coverage (no inference/Office): `history protocol:` checks optional
message against the schema, exact batch/result mapping, partial receipts, frozen
repair and early identical-response stop; `model protocol:` checks transport and
independent retry budgets without fallback; `model diagnostics:` checks the 26-case
matrix, null-ID content visibility and the actual two-exchange native lab cycle.
`conversation v8:`, `tool result wire:`, `protocol context:`, `model refs:`,
`context continuity:` and `model context:` cover parser/history/evidence/compaction.
In these focused groups, old native/history/prefix expectations were replaced with JSON-profile assertions.
The tool-schema subset validator expands the generated response's single local
`$defs.calls` reference only in schema fixtures; it is not a server qualifier.
[Offline Qwen rendering](../protocol/qwen38/README.md) uses pinned upstream bytes
with an explicitly declared local Jinja adapter, never model weights or inference.
Web: `agent-history-settings` and `model-diagnostics-layout` reuse production DOM.

`conversation v8: normalizes unambiguous singleton arrays` checks envelope/tool
arrays in both transports while retaining strict invalid-type/evidence checks.
`kernel: opaque` and the write-effects runtime case distinguish normal macro return
from uncertain dispatch, bounded repeats, new user input and replay without making
unknown effects verified. `v8 recovery:` and workspace accepted-call cases retain
`StepReason` through materialization/reopen. Web `agent-activity-layout` checks live
DOM/focus/disclosure retention, accepted narration/reasons and quiet effect details;
`live-run-projection`, `run-view-state` and `stream-render-cost` cover projection and
incremental rendering. These are host-neutral checks, not Office qualification.

Web follow-up: `conversation-projection` checks overlapping pages across revisions
without losing applied inputs, bounds and explicit reset; `chat-inbox` distinguishes
identical deliberate sends after ACK from uncertain retries. `chat-input-delivery`
checks queue → history → next refresh; Chromium `agent-activity-layout` checks zero
disconnects for unchanged calls and retained selection/text through live → saved.

Follow-up audit of `601283cf`: the focused history suite also cold-loads a revision-3
batch without CallCount and rejects its execution; repeated-rejection coverage
includes intervening truncation and null content. `v8 recovery:` checks that Office
and Workspace deliver completionFeedback as an object, without double encoding.
The enlarged 166-case matrix passed 149; all 17 remaining failures reproduce on
parent `a0eea223`. They remain explicit [baseline gaps](../../docs/stabilization/RISK_REGISTER.md#json-profile-audit-baseline-gaps--2026-11-02),
not a passing full Harness or real-model/Office qualification.

Markdown/YAML follow-up: `model context:` checks exact runtime definition sections
and schema deduplication; `context continuity:` checks supplied-data roles and retained
summary provenance; `tool pack:` checks typed admission/restoration and exact loaded
schemas. `text search:` / `universal mutations:` cover explicit newline-pattern
refusal, differing search guards and fresh VBA excerpts alongside stale observations.
`task lists:` uses issued result evidence in its repaired close sequence. Scripted
VBA skill admission covers both JSON transports at 32768 tokens, refusal under a
full context and the next user turn after refusal. Fixed-size padding leaves room
for initial reads; optional-schema fixtures include accepted call origins/RunId or
explicitly exclude manual evidence from model history. The focused follow-up passed
92 distinct cases on the isolated protocol copy; this does not close the earlier
17 baseline gaps. No live models or Office.

`kernel: inspection can refresh repeated read after unknown` reproduces an exhausted
read counter before a possible effect, including replay: fresh affected live reads
are allowed, while immutable/schema reads, unrelated known targets and unknown-call
replay remain guarded. `context continuity: bounds loops and restores progress`
checks the existing no-progress limits. No real models or Office hosts are involved.

`tool pack: macro schema remains callable after repeated read` reproduces the
photographed `core.tools.read` loop in six response/history combinations: one read
admits `office.macro.run`, the compact receipt points to the actual request schema,
and the fourth identical read is rejected without revoking the callable tool.
The macro is parsed for admission only, never executed. Existing `history protocol:`
matrix checks fresh-call field guidance in both JSON outputs and typed repair,
including the distinction between call `id` and `arguments.id`.

Tools/skills persistence (2026-11-03): `skill packages:` checks complete selected
instructions on every request, the next run and a cold chat reload. `tool pack:`
checks v2 per-registration restoration, unrelated catalog drift, isolated
descriptor/policy/binding revocation, latest-pin precedence and explicit v1 reset.
`tools: discovery is complete and exact` enforces tools-only schema reads;
`agent: inspects full skill without selection` and
`skills: references use revisioned resource reads` cover the shared source route.
The workspace discovery case uses actual v8 `tool_results`/`tool_pack_state` messages.
These scripted host-neutral checks do not qualify Windows/Office or live-model behavior.

Chat model parity S2a: `workspace: chat model preferences and wire` checks actual
scripted HTTP bodies, persisted override/default, reopen, exact workspace and busy
lease refusal. `chat: session model overrides global settings` checks the common
resolver; `plugins: approval continuation belongs to application` checks the typed
preference bridge and pinned model through approval, stop/close/deny/terminal.
Web: `model-settings` checks Office/project/plugin picker, late replies and Settings
capability draft/save/reset/catalog preview; `model-navigation` retains the 500-model
lazy-render/cancellation checks. Browser fixtures use the existing mock bridge;
none of these checks qualifies real Office/WebView2 or a live provider.

Chat navigation storage: `storage: indexed input journal` compares exact canonical
input/commit events with indexed replay, counts skipped trace reads, checks reuse
of a header-only index for full-session materialization, foreign appends, damaged
locators/row loss, schema upgrade and incomplete-tail refusal. Also run `storage:
sqlite`, `storage: corrupt sqlite` and `inbox:` when changing this boundary. These
are Core/Application behavior checks independent of UI structure. The opt-in
`RNA_CHAT_PERF=1` case `storage perf: chat navigation with traces` uses 300 messages
and a 23 MiB journal, reporting catalog, load, reference full scan and indexed inbox
times; it has no machine-dependent time gate. Actual Office host-switch delivery
and Windows timings remain separate qualification.

For the typed message-window boundary select `storage: sqlite projection pages
messages`, `storage: sqlite page preserves tombstone context`, `plugins: project
chat history uses shared pages` and `artifact library:`. These retain 80-message
hydration, canonical bytes, off-page tombstones, selected heads/lineage, protocol
visibility and workspace isolation. The existing corrupt-row, damaged-metadata and
catalog/page document-boundary checks cover the changed read result through repair
and locking. No new-journal or Office qualification is implied.
The tombstone fixture also compares actual Office message/card projections for chart,
compaction and a 64 KB HTML workspace: the page preserves wire presentation while
leaving HTML bodies cold. Pair with HTML navigation and compaction reconstruction
checks for the shared full-load helpers. Damaged-root coverage targets `Host`, which
pages consume, rather than unrelated aggregate metadata.

Resource read-scope changes use `workspace Plan: scoped reads preserve publication barrier`
and `document Plan: shared publication and competing writers` through real consumers.
Pair with document-original restart/GC, unavailable-resource discovery, document
removal/restart, shared HTML metadata pages and the tombstone page check. These
exercise existing publication/ownership/body guards; no new store or read model.
The old shared Markdown lifecycle fixture still calls removed `markdown.save`;
its independently reproduced failure is recorded in Backlog.

`chat sessions: diagnostics do not hydrate or recover history` forbids catalog/full
loads and verifies typed immutable metadata, unchanged history/selection and an
unsaved draft. `storage: busy chat stays visible` checks the short validated/complete
event-read timeout and named holder diagnostics. `storage: CAS missing runtime body`
keeps diagnostics and Office history pages addressable with missing/corrupt execution
bodies; full run loading still fails explicitly and canonical bytes stay unchanged.
`plugins: project chat history uses shared pages` covers the same missing-body case
through the real Workspace consumer. `model context: workspace
exchange and scope` checks the same identity-only workspace route; `bridge: typed
trajectory query` checks `chat_storage_busy` instead of generic failure/empty success.
These do not qualify the real Office controller or Windows lock durations.
For metadata/lease changes also select exact leased export, trajectory payload,
bounded upload and cancelled completion; `event feed:` covers its four reader
cases. Pair with actual Office batch/restart, Workspace accepted-turn history and
`inbox: recovery reconciles Office and Workspace receipts`. The DTO change does not
require rebuilding or requalifying the new JSONL/index/restore mechanisms.
`chat sessions: selection loads and binds once` verifies one session load and one
active-pointer write, including explicit navigation during deferred Save As
migration. Both ordinary selection and host initialization use this service owner.

`chat sessions: creation skips stored history` forbids full/history-header reads
for fresh, warm, persistent and archived creation; `creation preserves Save As`
checks history and VBA evidence relocation. MockDemo `--chat-creation-test` exercises
the real host-neutral controller: four creations leave a cold 300-message history
unindexed and unchanged, return complete empty detail and exact catalog deltas.
Web `run-view-state` and `chat-sync` cover additive catalog delivery and navigation
draining. These checks do not qualify Windows/Office/WebView2 latency.

`storage: terminal projection failure is not missing chat` uses a valid event hash
with an unreadable typed field: one verified rebuild, explicit cause on repeated
selection, unchanged JSONL. `storage: terminal commit omits unchanged messages`
checks completion/cancellation of 300 messages, one changed body, cold replay and
bounded appended bytes; timing/allocation output has no machine-dependent gate.
`chat sessions: saved content does not rewrite selection` checks first-persistence
selection, ten subsequent notifications without pointer writes and another pane's
newer selection. Index-damage, append-fault/CAS and typed event semantics checks
remain necessary when changing these storage boundaries.

`storage: canonical record encoding is stable` compares the streamed encoder with
schema-4 canonical bytes for SHA/HMAC, encrypted/plain records, Unicode and large
values. `storage: jsonl byte offsets are exact` also covers a record larger than the
reusable-buffer limit followed by small records and backward/forward seeks.
`chat sessions: recovery omits storage usage` forbids advisory CAS size scans during
startup recovery and document-alias resolution, while ordinary catalog usage stays
fresh. The opt-in `storage perf: structured chat records` case uses 300 messages and
a 21 MiB journal of nested objects; it reports cold allocation volume, cold time,
restart and warm reuse. It complements the earlier large-string profile and has no
machine-dependent time assertion.

UI demand loading: `chat sessions: catalog avoids active history` forbids full
loads during routine catalog reads while checking fresh metadata and selection
rebinds. `inbox: durable FIFO edit and recovery` counts latest-input CAS reads and
checks failed hydration/retry without advancing the projection. `bridge: chat list
is catalog-only` checks the narrow DTO and deferred library metadata (stub controller).
Web checks: `instruction-loading`, `chat-inbox`, `chat-sync`, `bridge-bootstrap`,
`tools-contract`, `prompt-review`, `multi-chat-requests`. Queue turn/confirmation
call sites explicitly skip unused return projections; this controller wiring is
source-reviewed. Real controller/Office delivery remains a Windows gate.

`node tests/web/chat-loading-deletion.test.js` (Playwright) covers deferred startup
with only Settings restored, delayed deletion, duplicate suppression, failed and
confirmed removal. `chat sessions: delete selects remaining projection` also
forbids a redundant deleted-session load and repeated empty-document catalog scan.

`inbox: stop freezes queue and retains target` also checks idle Send after an empty
cancelled queue and fresh input bypassing only its own retained pause. `conversation:
application queue owns FIFO and drain` checks that the empty tail does not reload
the full chat. `model diagnostics: tracker lifecycle`, `token estimate: actual usage
is authoritative` and `conversation: streams` / `conversation: resets stream` cover
scoped live usage, batch attribution, missing usage and immediate retry invalidation.
Web checks: `context-usage`, `agent-timeline-order`, `chat-inbox`, `stream-render-cost`,
`live-run-projection`, and Chromium `agent-activity-layout` / `chat-transcript-layout`.
`conversation-view` checks independent presentation targets, stale frames, bounded
scroll retention and dispose; `dock-workspace` / `workspace-chat-layout` exercise
the same owner on production DOM through the mock bridge. Multiple chat tabs are
not yet enabled in production.
`conversation-composer` covers per-view/per-target edit state, captured commands,
late focus, IME and disabled Send. `workspace-chat-layout` checks that refresh and
Office/Workspace switches retain unfinished edits; `workspace-shared-draft` and
`chat-draft-reload` retain receipt/draft persistence coverage.
S2b.3 adds binding scope to `conversation-view` and borrowed-snapshot / async
run-deletion navigation / delayed-history checks to `message-actions`. Existing
`stream-render-cost` still checks coalescing and retained nodes; Chromium
`workspace-chat-progress`, `chat-inspector`, `run-changes-layout` exercise the
same explicit presentation context, including exact Inspector/changes addresses.
S2b.4: `conversation-observer` checks two view bindings, stop/hide/dispose, stale
success/errors, coalesced follow-up and hidden passive refresh. `chat-inbox` covers
the nested history-read boundary; `message-actions` checks actual late delete
delivery. Pair with Chromium `workspace-chat-progress` / `chat-input-delivery`.
S2b.5: `conversation-preferences` covers exact-target pending/ACK, A→B→A read-back,
uncertain delivery and disposal; `model-navigation` retains discovery/drain guards.
Chromium `model-settings` checks confirmed Office mode with editable text during
save; `workspace-shared-draft` retains project/plugin mode/reasoning and receipt checks.
S2b.6: `conversation-draft` uses the real draft store for versioned ACK/edit/append,
source isolation and stable textarea binding. `chat-inbox` covers Office ACK during
a later edit; Chromium `chat-input-delivery`, `chat-draft-reload` and
`workspace-shared-draft` retain persistence, receipt, late file and preference checks.
The separate composer unknown-warning mismatch remains in the risk register.
S2b.7: `conversation-snapshot-source` checks immutable exact addresses, concurrent
reads and wrong-target rejection before UI delivery; `chat-sync` checks addressed
host init. Chromium `workspace-shared-draft` injects wrong Office/workspace replies.
S2b.8: `conversation-projection` checks independent bounded snapshot owners, address/
revision guards, retained hide, replaced callbacks and disposal over 100 targets.
`conversation-preferences` checks a fresh read-back adapter after A→B→A; Chromium
`workspace-shared-draft` rejects a late cancellation error from the old visit.
Pair with `workspace-chat-progress` / `workspace-chat-layout` for retained live DOM,
shared composer and project/plugin navigation. Office selected state is not yet migrated.
S2b.9: `office-conversation-projection` uses real core accessors and ChatState to
check one Office transcript/preference owner, bounded history, independent Workspace,
empty selection, release and late history/preference replies after same-ID reconnect.
Pair with `chat-sync`, `chat-history-paging`, `model-navigation`, snapshot source and
Chromium draft/delivery checks. The isolated `office-host-chat-layout` fixture misses
NavigationTree on baseline too; see RISK_REGISTER. Per-view Office cutover remains open.
S2b.10: `office-context-projection` exercises real text/selection/remove/clear handlers
across A→B→A and same-ID reset, current ACK/refresh, one context/material projection
and empty selection. It reproduces stale context overwrite on `205b6cf6` (red→green).
Pair with Office projection, `context-usage`, `chat-sync`, Chromium
`workspace-shared-draft` / `material-object-views`. The draft fixture explicitly opens
the plugin source chat under the accepted Browser route. The independent HTML source
read/export repair passes `html-workspace-source` (9/9); Windows delivery remains open.
S2b.11: `chat-run-completion` also checks one borrowed live run per source, disposal
without cancellation/recreation, background delivery, return, new-run replacement
and bounded reasoning. `live-run-projection` now uses the real core/source accessors;
`stream-render-cost` preserves 1000 history nodes and coalesced foreground/background
paints. Pair with Office projection, run-view-state, bridge-bootstrap, message-actions
and Chromium `workspace-chat-progress` / `chat-input-delivery`. The progress fixture
explicitly opens the plugin source chat. Independent chat tabs and Windows evidence
are still open; source/view factories must replace the selected facade first.

S2b.12: `office-conversation-projection` also runs two independent Office factories
and render inputs with all global state access forbidden, checks exact delivery,
bounded/borrowed data, disposal and reading-window preservation after a preference
ACK. Run-view/source fixtures use real core accessors. Pair with context, completion,
message-actions, live projection and stream-cost checks; Chromium shared-draft,
workspace progress and chat-inspector exercise production delivery. The Inspector
fixture publishes its real transient run instead of writing read-only live fields.
`artifact-commit-projection` passes
its four Web cases, but its static C# ordering assertion also fails on `d61a9aff`;
see RISK_REGISTER. It is not a passing full file or Office qualification.

Independent Chat Views (2026-11-03): Chromium `tests/web/independent-chat-views.test.js`
checks shipped boot, parallel Office delivery, Workspace/plugin, source-local drafts
and preferences, edit, close without run cancellation, stale read/ACK and layout
restore. `workspace-chat-progress` now uses scoped nodes and waits for a coalesced
paint; it verifies 30 token updates preserve the leaf and skip tree/transcript work.
Older singleton-DOM fixtures require migration before use with Chat View factory.
The 2026-11-04 correction also checks actual root/messages/composer bounds inside
their Dock group at 125% CSS zoom, narrow/wide restoration, scroll across hidden
views and reopening layouts that still recorded renderer `always`. Four view
instances remain owned; inactive DOM no longer needs an overlay in the document.
`agent-recovery` uses real plus/menu clicks and checks returning to file/material/
compaction actions, including the native filechooser after dismissing recovery.

`chat-navigation-lifetime` covers immediate tree selection during a blocked native
queue, closed-tab catalog sync, late selection/read/failure delivery, retained
history, newer Dockview focus and keyboard selection at 600 px / 125% zoom.

Chat audit (2026-11-03): `parallel-chat-navigation` runs two actual Office views,
holds ACK/native navigation, closes/reopens the sender, and checks draft/focus
isolation. Its 500-row/60-tick streaming probe asserts retained navigation DOM and
zero unrelated composer/Inspector updates; timing is diagnostic, not an SLA.
`chat-history-navigation` exercises Office/Workspace adapters for duplicate reads,
both Previous/Latest response orders, revision changes, the 240-message bound and
close. `chat-inbox-observer` checks compact remote receipts, phase/terminal reads,
stalled sibling reads, hide/reactivate and late history after close. Obsolete
singleton polling/paging assertions were replaced; `chat-history-paging`,
`chat-inbox`, `multi-chat-requests` retain pure bounds/receipt/address tests.
`chat-tree-layout` covers the unified Office/project NavigationPane, full add menu,
shared search/selection, retained views/DOM, split/close/reopen/restore, creation
routing, menu icons and 420 px / 125% themes. `office-host-chat-layout` uses the
shared add menu for queued host creation, progress/errors and explicit retry.
`shared-navigation-tree` passes in the isolated chat commit. A separate Library
deactivation pageerror found in the concurrent editor migration is tracked by
that owner in RISK_REGISTER. No Office/VSTO validation is implied.

`history protocol:` checks JSON-object/schema responses, instruction roles,
optional message, accepted assistant decisions and user-role indexed result batches,
shared concise-field guidance, unchanged user data and frozen typed repair.
Legacy history-role preferences normalize to the single production JSON profile;
native pairing is tested separately in the diagnostics lab. `saved settings apply without
restart` changes controls through the real SettingsService/publication path and
runs the same chat four times through one executor/conversation service, inspecting
the actual wire history and current instructions. `node tests/web/agent-history-settings.test.js`
checks the shipped form, JSON examples, 1100/740/390/320 px layout, save/reopen,
edits during pending saves, tab navigation and visible save failures. Optional
`AGENT_SETTINGS_SCREENSHOTS` writes previews. These do not qualify Windows delivery.
`new execute requires content calls` checks missing calls, JSON-encoded calls or
arguments and copied native wrappers through JSON/SSE in both response modes,
with/without provider-native calls (32 cases). Repair accepts only corrected new
content JSON; raw rejection payloads and accepted history are preserved.
Both are synthetic; provider-native generation is a separate diagnostics capability.

`model diagnostics UI:` covers the typed catalog/run bridge, shared automatic controls
and SSE parity, exact request forwarding, settings isolation, endpoint drift rejection,
bounded redaction before truncation and cancellation without another request.
`node tests/web/model-diagnostics-layout.test.js` uses the shipped UI with a synthetic
bridge: opening never probes, explicit runs/controls, whole-row/keyboard selection,
independent native-history/generation groups, connection refresh after saving,
selectable transport errors, control seeds, inert provider output, JSON export,
cancellation and 1100/740/390/320 px light/dark layout.
Uses Playwright and optional `BROWSER_EXECUTABLE`; optional `RNA_DIAGNOSTICS_SCREENSHOT`
writes desktop/narrow previews. Neither check calls a model or validates real Office.

`model diagnostics:` covers exact native-history probe requests (null/empty/omitted
content), retained decision JSON in B4, shuffled pairing, absent-result control,
production fallback envelopes/aliases, strict marker/ID
assertions, HTTP 500 and malformed HTTP 200 capture, timeout/cancellation and the
existing diagnostics tracker/stream cases. The transport matrix adds instruction
roles, production V8 parser/schema, expected schema rejection, explicit text JSON,
native function generation/delta inspection, synthetic multi-run history and real
SSE versus JSON parity. Failed wrappers, contradictory finish, content pretending
to be a native call, JSON fallback to a streaming request and malformed/error SSE
cannot pass. Raw capture checks bounded exact prefixes and truthful completeness.
These are synthetic checks, not endpoint
qualification. `python3 -B tests/cli/model_diagnostics_smoke.py` exercises the built
CLI's 23 probes, independent native/V8/SSE verdicts, automatic parity control,
exact request/received response files, redaction and no-overwrite/no-retry behavior.
`python3 -B tests/cli/v8_context_baseline.py --output <new-directory> --role tool`
captures a scripted production read/read/patch/read and second-run HTTP baseline.
`--case historical-finish` makes the second run cite delivered prior-run evidence
without another tool call. `model refs:` checks indexed canonical admission across
runs/restart/fork, history membership, selected versus unrelated missing payloads
and disposable-index repair. `context continuity: transport after bounded materialization`
covers all nine stored/request role combinations without reading unselected CAS,
including nested argument types, JSON strings, Unicode and exact source escaping.
Add `--case all` for two consecutive patches, failed-patch correction and the existing
compaction/skill-package fixtures. Exact request/response files include compaction
and titles; the manifest records body coverage/hashes, native pairing, retained-result
destinations and duplicate selected-skill bodies. `--case` can select one fixture.
`model refs:`, `context continuity: folds older frames only for budget` and
`kernel: unknown call remains non-repeatable` supplement this baseline; they do not
qualify real endpoints or exact HTTP unknown-effect delivery.
Neither script builds automatically. Real probes use `rna diagnostics`; scope and
usage are in [endpoint compatibility](../../docs/model-endpoint-compatibility.md#v8-diagnostics-first-slice--2026-10-30).

`--capability-inventory /tmp/rna-capabilities.json` writes the built-in catalog
union and per-host tool/skill IDs from isolated stores (JS off/on, Workspace and
plugin source additions; conditional browser ID is source-declared). It does not
call a model, browser or Office. The [2026-10-29 inventory](../../docs/audits/capability-inventory-2026-10-29.md)
compares 91 → 78 → 71 public IDs, with 16 skills and the 10-tool initial Core profile.
`tools: catalog merges visible tools`, `tools: VBA facade is common across hosts`
and `settings: built-in skill references` check current publication, shared schemas
and full dotted references, including Workspace/default prompts. The shared Skill
Library projection is covered by `node tests/web/skills-contract.test.js`.

`universal mutations:` covers the shared public source contract: original-snapshot
old/new substring edits, ordered anchors_before/anchors_after chains, overlapping/repeated text, UTF-8/BOM/CRLF/EOF and large streamed files,
strict schema/old-approval refusal, explicit literal EOLs, upsert and owner routing, delivered VBA excerpts,
confirmation collisions, stale reads and identical VBA excerpts after authority drift,
copy/move/delete/restore, retained file diffs,
and unknown effects. `bounded public error results` checks real runtime schema
refusals, diagnostic/edit/nextAction parity, bounded candidates, owner codes and
redaction. `RNA_PATCH_RESULT_AUDIT=<directory>` on package/HTML diagnostic checks
writes the actual serialized results for inspection. Package checks cover all-before-write preparation, duplicate aliases,
confirmation drift, per-target rechecks, partial failure/unknown, unexecuted remainder,
model projection, retained before/after and VBA/HTML/Markdown/authored-file routing.
`delivered Office source authority` also reproduces an insertion that incorrectly
uses its own `End Function` as a preceding anchor, then verifies the corrected
unique-fragment insertion through the host-neutral VBA owner. The bounded error
checks cover both anchor directions and exclude undelivered candidate locations.
The sequential VBA case reproduces the photographed three-line change followed by
a two-line reversal, verifies delivery of the latest source, preserves the other
edit, and rejects stale or omitted observations. Confirmed targets remain available
through compaction even if a later target fails or becomes unknown. These checks
do not establish why the user's model chose to reverse an edit.
`shared source:` checks Plan header/body edits, all-target preparation and mode
policy, verified Plan after-source, editor revision guards, prompt/reference owner
routing, retained diffs and reference BOM/CRLF preservation.
`tests/web/run-changes-layout.test.js` checks per-target inline statuses, bounded
inert diagnostics/candidates at narrow widths and prepared preview;
`run-view-state.test.js` checks typed preview delivery.
`tests/cli/structured_patch_model_compare.py` compares isolated manual/structured CLI
builds against one pinned Ollama model using identical tasks and HTTP evidence.
Pass `--manual`, `--structured`, `--manual-revision`, `--model`, `--output`.
Run separately for available Qwen/DeepSeek tags; absence is an open qualification gap.
It does not transform prompts or responses, and does not qualify native tool-calling
or Windows/VBE. Model runs are separate from regression tests.
`tests/cli/stabilization_smoke.py` covers multi-target confirmation across restart,
bounded partial-source reads and intermediate reading before a dependent patch.
`tests/cli/patch_diagnostics_smoke.py` checks actual next-request preflight diagnostics,
ordered NotStarted/Rejected targets, correction, CLI text and retained presentation
after approval/restart. Both scripts use synthetic HTTP and do not build automatically.
`text search:`, `tool pack:`, `authored file:`, `run changes:`
and `tool presentation:` cover their existing owners. No COM/VSTO runs are involved.
`authored file:` additionally checks streamed preparation allocation for a 10 MiB
source, UTF-8/16/32 BOMs, mixed EOLs/astral characters/EOF, partial observation,
unchanged results and confirmation invalidation after a competing write. Retained
before/after views remain available after a partial model read. The two
`context continuity:` mutation-receipt/archived-target cases use current public
schemas, preserve per-target outcomes and the lifecycle destination through restart.
`tests/cli/read_recovery_smoke.py` checks six production CLI refusal/read/retry
paths, including restart; optional lifecycle tools are admitted before use.
`tests/web/universal-tools.test.js` checks grouping by the first namespace only;
`universal-tools-browser.test.js` covers inline diff pagination, safe text, long
lines and unverified previews. `tests/cli/tool_name_migration_test.py` uses only temporary
skill packages to check explicit migration with preserved originals/body/BOM.
See the current [source tool contract](../../docs/text-mutations.md).
Workspace owner tests use the same typed structured edits as the public contract. The
retired insert/context DTOs and duplicate resolved edit type are removed; migrated checks retain large-file no-op,
stale/ambiguous context, invalid Unicode, post-dispatch unknown and read-back
mismatch recovery, plus visual-selection edits against exact plugin source.

The VBA/HTML model-loop scenarios in `Program.SimpleAgentTests.cs` use public
`core.write`, structured `core.patch` and the initial Core profile. The migrated
checks cover closed-document local HTML, confirmation/reopen and full after-source,
stale-source recovery, compaction, rejected mixed read/write responses, skill
inspection/admission budgets and causal trace/journal correlation. HTML persistence
checks run with user/native tool-result transport, discover the exact member through
`core.find`, then read before repeating a complete write; accepted arguments and
runtime IDs survive CAS/replay without rewriting source bytes. Unknown VBA writes
inspect both the module and affected project membership before finishing while
retaining the unknown result and one dispatch. These are scripted model responses
with fake Office adapters, not live-model or VBE qualification. Other catalog/owner
fixtures still need migration; see [BACKLOG](../../docs/stabilization/BACKLOG.md#интеграционные-fixtures-после-перехода-на-core-tools--2026-10-28).

`workspace: failed read activity` verifies a real failed tool outcome remains failed
with its semantic target after persistence; accepted-history checks cover shared
display metadata and summaries. `tools: validates save metadata` covers skill
reference and file source/destination captions. Presentation checks now include
canonical generic-result JSON and receipts. Chrome activity/run-changes checks cover
visible attempt targets, distinct skills and separate actual/planned counts.

`tool presentation: exact source captions and CAS` checks native/user/developer result
roles, exact canonical read vs unrelated activity preview, scoped R resolution,
missing CAS, ambiguity, Error/Unknown, unbound search captions and tree presentation.
`tool-result-presentation.test.js` checks inert captions and exact slice disclosure;
`diagnostics-navigation.test.js` checks unified request routes, partial/rejected bodies and responsive layout.
The historical [result matrix](../../docs/audits/tool-result-presentation-matrix.md)
records the earlier 91 static IDs and presentation paths; current counts are in
the inventory above. Neither claims real Office execution for every handler.

`context composition:` checks exact selected-skill materialization across all result
roles, immutable publication dependencies, full/partial/foreign/changed/unselected
reads, preserved pairing and truthful body provenance. `model context: request schema`
checks exact HTTP schema deduplication, format repair/fallback and schema drift.
`skill packages:`, `v8 recovery:` and `context inspector: builds agent snapshot`
cover selection/budgets, continuation and preview consumers without live models.

`model refs:` checks separate T calls/eligible E terminals/R versions, criterion,
case-insensitive lookup, revision-3 read-only legacy boundary, canonical/raw preservation, all result roles,
atomic allocation and stale writers, failed commits, restart/removal, resource versions
and scope, shared message/request allocation, stale trace refusal and unbound historical
rows, hidden/pending/GUID/type refusal, service-only empty evidence,
failed-result eligibility/contradictions, correction without replay, mandatory
compacted receipts, repair/fallback and the successful request's delivered set.
Source-block cases check exact scope/revision binding beside coverage, multiple slices,
reordered bindings, missing/ambiguous evidence, omitted bodies and literal source bytes;
navigation results get no guessed labels. `context continuity: delivers recovery and
allows another tool` checks the actual next request; `context continuity: selects active
and archived bodies once` checks a hydrated CAS source header.
Workspace accepted/strict history, discovery admission, `tool pack:`, conversation v8,
Inspector and unknown-effect/confirmation checks cover the existing consumers.
Four scripted profile scenarios and eight live runs of the withdrawn lowercase candidate are recorded in the
[comparison](../../docs/audits/tool-profile-comparison.md#typed-short-reference-repeat--2026-10-26).
`tests/web/model-context-layout.test.js` verifies resource navigation, source
coverage and preview in Chrome; screenshots are linked from ADR-0018.
No VSTO/Office validation is performed by these host-neutral checks.

`--workspace-profile-live <fresh-evidence-directory> <broad|compact> <edit|copy>`
is an isolated live diagnostic through the production workspace runtime. The
`tests/cli/tool_profile_comparison.py` runner captures paired requests, model tags,
byte-level file acceptance and run completion separately; `--scripted` validates
four fixture paths without a model. Build Harness once, then pass a fresh output
directory. The runner requires installed tags and never changes production defaults.
See [protocol and results](../../docs/audits/tool-profile-comparison.md) for limits,
commands and evidence. This is not part of the default test run or Office validation.

`workspace: discovery admission restart and binding drift` uses a strict mock HTTP
provider and an explicit small composition profile. A tool absent from the first
request is found/read, admitted only in the next schema, executed once, restored
after reopening, then invalidated on binding drift. A Skill-only schema read never
admits it. The fixture checks one canonical admission event and model receipts
without duplicated schemas, loaded flags or runtime revisions. Default workspace and Office profiles use the shared Core; optional tools require admission. Existing `tool pack:`, Office discovery/skill-reference tests,
workspace strict/object history and pinned-Office tests cover the shared owners.

`tool pack: explicit profiles` compares explicit and all-direct profiles through
the production skill compiler: initial/selected registration fingerprints,
shared dependency deduplication, mutation policy/binding pins, selection removal
and prior-request stability. Existing `tool pack:` cases cover actual Office
host/mode cores, exact admission, restart and budget refusal; workspace strict
request/history and pinned-target cases cover the other consumer. These are
host-neutral checks, not real Office or model-quality qualification.

`tool pack: fitting extension` and `tool pack: fitting prior schemas` retain the
original ASCII fixtures as positive strict-projection regressions. The overflow
and carryover-refusal cases use valid non-ASCII descriptors and assert that schema
options plus reserves alone exceed the input budget. They print projected messages,
options, repair/continuation reserves and total. Oversized retained read evidence
must stop request construction; a larger context and restart cannot turn a rejected
admission into authority. Carryover starts with complete evidence under a larger
prior result budget. Run `tool pack:` for all 11 focused checks.

`text search: default directory browse through runtime` reproduces schema-default
routing through ToolRuntime and the actual workspace schema/handler. Omitted and
strict-null selectors retain visible directories; explicit false/true/globs retain
file-only semantics and hidden paths remain excluded. It failed before the fix.

`text search:` checks closed workspace/Office schemas, language-independent literal/regex
matching, Unicode/CRLF coordinates, recursive globs, hidden paths, cancellation,
exact/stale/lost continuation, cumulative coverage and totals, pathological regex,
long lines, native VBA ToolRuntime and typed presentation. The mixed-project fixture
runs six exploration queries through 505 in-memory sources and prints calls, source
opens, delivered bytes and elapsed time. This is a tool-only experiment, not model
success/latency evidence or a semantic-reference benchmark. Existing original-text,
workspace/VBA/HTML patch, v8 and saved-request checks retain their own coverage.


`visual selection: workspace intake and common resource read` checks one accepted
workspace attachment identity through input journal, chat CAS, model prompt and
`core.read`, plus native plugin-instance staging for active and checked
candidate releases, browser-safe draft projection, duplicate submission and
ambiguous-target refusal.
The same fixture now checks a static annotated plugin button against its retained
release and current source, applies one revision-guarded `core.patch`, reads the
changed source and retains a checked candidate. Ambiguous anchors and stale reads
refuse the write. It does not assert browser behavior.

`v8 recovery:` checks configurable/frozen reflection depth (including positive-limit
correction, settings save/reload and depth 0 in both response modes), disabled-action
schema/instructions through repair and fallback, rejection without a correction
request, and preservation of user text and previously accepted reflection history.
It also covers required finish outcome/nonblank answer, contradiction repair and
outcome replay, separate format retries,
strict response transport, bounded output truncation, accepted reasoning after
persisted reopen, and workspace live/saved presentation. Run
`node tests/web/stream-render-cost.test.js`, `node tests/web/workspace-chat-v8.test.js`
and `node tests/web/workspace-chat-progress.test.js` for the corresponding UI paths;
the last checks project/plugin progress bridge routing and uses Playwright with
optional `BROWSER_EXECUTABLE`. Scripted CLI coverage is
`tests/cli/conversation_v8_finish_smoke.py` and `tests/cli/response_mode_smoke.py`.
The finish smoke checks both modes: empty-answer/outcome corrections without write
replay, and exit 5 for partial/unable model assessments despite Completed/Clean.
The latter requires `jsonschema` from `tests/RNAssistant.ProtocolTests/requirements.txt`.
It validates the actual HTTP `json_object` catalog with an independent JSON Schema
validator: valid read/find/search selectors, incomplete and mixed-selector refusal,
then real search/read dispatch. Native transport call IDs and model evidence refs
are checked separately; scripted finishes use the shared conversation helper.
`tests/cli/model_preview_ipc_smoke.py` checks live SSE preview read by a separate
CLI process, inert repeated reads and terminal retirement; `run_control_smoke.py`
checks cancellation on the same IPC path.
These checks do not qualify a real provider or Windows/Office/WebView2.

Data Templates is an independent source project in `web/data-templates`.
`node tests/web/data-templates-export.test.js` checks exported closure/hashes,
source tests, file-origin UI/extraction/download, and static-origin XLSX Worker
execution with JSON/DSL/mapping draft edits, immutable shared dictionary versions,
conflict refusal without writes, browser project snapshots and validation,
without RNAssistant.
Browser tests use Playwright and optionally
`BROWSER_EXECUTABLE`; no Office/VSTO build is needed.

Data Templates M0/M2 is JavaScript-only: run `node web/data-templates/tests/core.test.js`,
`node web/data-templates/tests/matrix.test.js`, `node web/data-templates/tests/universal.test.js`,
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

Native runtime transport: `tabular native:` covers the compiled module-bundle pin,
immutable captured files, canonical resource routes, bounded range receipts,
revocation/drain and a native deadline. After this Harness build run
`node tests/web/tabular-native-browser.test.js` (Playwright/`BROWSER_EXECUTABLE`).
It launches the Harness-only `--tabular-source-host`, forwarding actual requests to
the production resource handler: retained C# snapshot → binary ranges → real Worker
→ the 16-record oracle. It covers dedicated CSP, cancellation, sink failure and native
revocation after parsing, with native storage activity drained before completion.
There is no production HTTP server or Office dependency. These checks do not qualify
Windows/WebView2 interception/lifecycle, the main loader's parser dispatch or large
Excel throughput. `node tools/update-tabular-runtime-lock.js --check` verifies the
shipped package manifest and generated compiled pin; update both with `--write`
only after reviewing runtime changes. No npm/bundler is needed.

`tabular channel:` checks typed native job/sequence guards, acknowledged sink writes,
confirmed source cleanup, cancellation during pending write/validation, late output,
post-result cleanup failure, and bounded response-body ownership/revocation. The same
`tabular-native-browser.test.js` additionally connects the real C# channel to a
test-only `chrome.webview` shim: the XLSX oracle, native sink failure, both pending
sink cancellation points and a bad acknowledgement all run through the real Worker.
These tests do not execute/compile `TabularWebViewExecutor`; Windows build, native
interception, hidden-controller/stream behavior, startup disposal and hard-stop latency
remain explicit target-host gates. The loader still prepares sources only.

`resources: VBA procedure` covers the common Core parser, live Office read handler
and streamed workspace file selector: exact declaration/terminator spans, mixed line
endings, Unicode, comments/strings, continuations, property accessors, conditional
ambiguity, cancellation, model budget and partial authority. It uses a live-module
fake above one million characters and a 16-MiB UTF-8 file with bounded allocations;
real Windows/Office/VBE validation remains open. `node tests/web/model-context.test.js`
checks procedure-name/kind diagnostics alongside existing coverage metadata.

`resources: JSON` covers the shared streaming JSON Pointer/tree selector, escaped
and duplicate keys, exact array slices and child paging, model budget, 4-MiB
workspace source without whole-body allocation, multi-page Office artifact,
typed schemas, partial CAS evidence and saved read metadata. The same Web check
renders requested pointer/depth and completeness. It does not qualify real
Windows/Office/WebView2.

`resources: YAML` covers decoded scalar navigation, escaped and duplicate keys,
explicit alias/merge/complex-key/multi-document refusals, streamed 4-MiB workspace
sources and paged Office artifacts with exact partial `yaml-path` evidence. It does
not qualify real Windows/Office/WebView2 behavior or YAML schema resolution.

`resource cutover: canonical binary data plane` and `resource cutover: binary
capabilities and retained bounds` also check that model-facing find filters out
application-only image/PDF/raw views while retaining known MIME/byte length; the
runtime descriptors and binary consumers keep their full capabilities.

`workspace files:` includes an actual 80-MiB source retained/read without a
whole-body allocation, exact history after drift/deletion/restart, operation
budgets, busy-file cancellation and missing/corrupt/unpublished source refusals.
`workspace files: bounded character windows` also reads line and UTF-16 character
windows from a 16-MiB UTF-8 file with bounded allocation, exact partial evidence,
BOM/CRLF and surrogate guards, drift detection and invalid-encoding refusal. It
also checks a 40000-character model range and complete read under a larger
configured read budget.
`workspace files: streamed large patch and recovery` covers a unique anchor across
a stream buffer, exact after-source, overlapping ambiguity, stale/external drift,
no-op, read-back mismatch and explicit restart reconciliation after an interrupted
large write.
`workspace files: streamed large copy move delete` covers exact partial-source
guards, byte-preserving copy/move, managed delete/restore, exact continuation and
reconciliation after interrupted large copy/move/delete.
`workspace files: large restore recovery has no binary cap` restores a managed
text source larger than 256 MiB after interrupted terminal persistence, then
verifies exact length/hash without replay or whole-body allocation.
`storage: source streams`, `storage: file CAS` and `storage: plaintext CAS` cover
stream ownership, cancellation, compression/encryption and envelope-prefix
compatibility. Source-stream checks also cover explicit seek staging across all
encoding combinations, backwards/cross-block ranges, progress and scratch cleanup
on completion/cancellation/failure. These are storage checks; they do not qualify large Excel parsing,
queue throughput, UI latency or Windows/Office behavior.

Data Templates shared manifest storage: filter `tabular project:` checks
create/reopen/read-only and workspace relocation, closed metadata/path refusal,
external drift/foreign workspace, exact retained bytes and unknown write recovery.
Source declaration checks cover stable IDs across restart/reordering, empty selections,
closed schemas and count/byte bounds, missing paths and unknown-save recovery without replay.
Raw draft checks cover browser-compatible snapshot naming, invalid JSON preservation,
strict UTF-8/size bounds, manifest drift, read-only access and typed bridge recovery
of an uncertain create without replay.
The focused `tabular project: browser catalog` filter checks read-only native
template/dictionary heads against browser filenames, stale-head refusal, archive
retention and malformed dictionary reporting through the typed bridge. It does not
validate DSL semantics or qualify Windows/WebView2.
The owner is the existing `WorkspaceFileService`; these tests do not qualify the
future Office UI, capture or template publication. The existing CLI exposes
`project create/open/sources/prepare` and `project draft save/list/open` for
process-level smoke checks without a model or Office.

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
The focused `source reader` filter covers bounded ranges, amplification, isolated
buffers, exact manifest/source pins, maintenance/selection guards, synchronous
revocation, slot/read/open cancellation and application close draining in-flight I/O.
CLI `project prepare` accepts saved declarations or an explicit JSON array of paths and
file/batch budgets; process smoke covers mixed outcomes and read-only capture without
manifest changes. These preparation services remain independently tested; their
replaced root UI controllers and browser tests were removed during the plugin cutover.

Plugin foundation: filter `plugins:` covers immutable code across update, failed
validation preserving active publication, source/data/project substitution refusals,
published-only paged history, exact historical preview and guarded restore without
overwriting editable sources,
preview scratch separation, runtime-clear exclusion/drain, command lifetime/cancel, exact vendor hashes and distinct
HTML/plugin/export admission, binary/directory unknown-effect recovery, result scopes
and runtime-owned prepared confirmation. The source-inclusion check verifies old-style
project registration; no Office/VSTO build is needed.
The same filter checks SVG generation/repair through controlled model replies,
SVG viewport normalization with retained colors/gradients/transparency/transforms, legacy icon
conversion and durable name/icon/color editing without changing publication.
Source chat coverage checks independent sessions in the common project catalog,
retained message history across creation/restart, exact workspace/session refusal,
deletion and explicit reopening after history removal. The focused
`plugins: source chat excludes Office` check sends real model requests through the
plugin/project chat route and checks source-only prompt/resource admission and
Office target capture in an ordinary project chat.
`plugins: project chat file upload` includes UTF-8 beyond 1 MiB, a surrogate pair
at the probe boundary and invalid encoding at the end of the file; readable text
is admitted without a source-size cutoff and every upload preserves exact bytes.
`workspace: first message names the chat` checks an accepted workspace turn against
a controlled local model endpoint: shared draft or already committed smart title,
later smart title with a scoped cursor notice, durable project header and manual
rename priority.
`workspace: accepted call history survives a new turn` checks a two-call read
batch, exact runtime/model-attempt origins after restart, the next model turn,
legacy narration display and explicit refusal of an origin-less old record.
`workspace: Office target stays pinned across document and host switches` also
checks the shared Office discovery query limit and bound-document scopes.
`workspace: saved Office results use host projection` checks that a project chat
with a bound Office document removes an archived Word scope hash from the next
model request, including after restart, while preserving the durable result.
`workspace: explicit resume records persisted input delivery` checks the crash
boundary between durable user-message persistence and the `applied` input event:
resume records the missing transition once, while a missing message and duplicate
lookup never replay input or start the model.
`plugins: project chat recovers abandoned run under lease` checks the real
Application/bridge read path: a held lease without IPC cannot change a running
session or wait for the ten-second acquisition retry; after release, recovery
preserves history, marks a possible write unknown without replay, unlocks fork
and is idempotent. Pending approvals and plain inspection are not auto-resumed.
Set `RNA_WORKSPACE_RECOVERY_FIXTURE=/tmp/rna-workspace-recovery/bridge.json` on that
Harness invocation, then run
`node tests/web/workspace-run-recovery.test.js /tmp/rna-workspace-recovery/bridge.json`
to check the same bridge envelopes in Chromium: polling recovery unlocks input
and fork while retaining the draft. This does not qualify Windows/WebView2.
`node tests/web/plugins.test.js` checks creation/automatic check/preview/apply/reopen/disable,
immediate SVG drawing, reusable rail hover menus and remove/restore,
source chat presentation, actual Chromium Worker and native-protocol XLSX extraction
with complete canonical result read-back. It uses a controlled bridge/file transport,
not WebView2. `RNA_PLUGIN_SCREENSHOTS` saves desktop/narrow previews; use the usual
Playwright `BROWSER_EXECUTABLE` override. The complete Data Templates standalone
export browser suite also passes; it does not qualify native plugin transport.
`node tests/web/workspace-chat-layout.test.js` compares Office/workspace/plugin chat
rendering and composer metrics, preserves drafts on failed delivery, checks retry
identity and mode/reasoning preferences, narrow layout, the development splitter,
the shared panel buttons (geometry, Enter/Space/Escape, pointer focus after keyboard use and draft retention),
plugin name/icon/color editing and creation (including rendered SVG color pixels and neutral defaults following the theme), new source chats, navigation through the main history tree,
history access during a run, draft restoration and late replies after switching chats.
`RNA_CHAT_SCREENSHOTS` saves the visual review captures.
`node tests/web/chat-history-paging.test.js` checks the common 240-message window;
`node tests/web/chat-window-layout.test.js` checks Office/Workspace data and DOM
bounds, explicit return to the latest 80 messages and late owner-switch responses.
The shared layout check also covers composer labels, read-only/reply/edit states
and rejection of background Office mode presentation in Workspace.
`node tests/web/workspace-inspector.test.js` checks the shared Office/project/plugin
Inspector DOM, exact workspace/session/run/step routing, stale owner rejection,
draft and live preview frame preservation, and narrow/light/dark layouts. It also
checks saved workspace capabilities, previous-run attribution, confirmation and
unavailable-skill reasons, missing/incomplete receipts and session isolation.
Saved request/response checks cover exact workspace routes, pending-read cancellation,
return focus, owner preservation, resizing without rereads and narrow plugin responses.
`model context: workspace exchange and scope` checks the existing event/CAS reader,
foreign workspace/event rejection, lease ownership, unchanged history and reads
while the source folder is unavailable, plus scoped journal payload and ZIP export.
`model context: workspace diagnostic usage` checks one count for parallel calls,
missing usage and same-run window attribution. Workspace Inspector browser coverage
includes usage, exact journal routes, raw payload, full-chat scope and foreign replies;
`trajectory-json-viewer.test.js` and `trajectory-payload.test.js` cover workspace
exports/lease cleanup and rejection before byte reads. `model-context.test.js` checks response
identity and rejects absent workspace attribution, duplicate message-source positions
and invented omitted-item counts. `model-context-layout.test.js` checks compact
source/coverage disclosures, recorded omissions, exact resource navigation and
restored expansion without a payload reread in both themes at narrow width.
`model context: canonical execution evidence` covers service/failed/unknown/pending
calls, historical delivery, future bindings, ambiguous receipts and canonical finish
ownership across repair attempts. The same Web tests cover lazy evidence metadata,
criteria → E → exact-message navigation, preserved disclosure state, live acceptance
and narrow layouts without model or Office calls. `diagnostics-navigation.test.js`
checks the new routes at 1440/420/320 px; `diagnostics-performance.test.js` compares
a 2000-message request on baseline/current assets. See [diagnostics evidence](../../docs/diagnostics.md).
`model context: saved request composition`, `storage: model request trace precedes
dispatch` and `storage: mixed attachment manifest matches saved request` check
disjoint baseline costs, exact compiler attribution, selected media estimates and
persisted metadata outside the provider payload. The mixed fixture also changes
serialized text, role and image bytes independently: unproved parts become Unknown
without erasing unchanged media. The same two Web tests reject
inconsistent composition snapshots and check chart/message navigation without
normalizing to admission or API usage. `INSPECTOR_SCREENSHOTS` saves
browser captures. Next-request workspace estimates and native Windows/WebView2
delivery, including controller lease liveness, remain open.
`attachments: selected media budget` and `attachments: media cutoff and model notices`
check one selected media reserve, actual audio bytes, text framing, cross-message
image/PDF quota, native/analyzed exclusions and model-visible reasons/pages. Reuse
both saved manifest checks above and `agent: hydrates artifact media only after read`
for persisted wire parity and repair lifetime. They use host-neutral byte fixtures,
not a real provider or Windows PDF renderer.
The saved composition check also rejects duplicate message IDs, shifted or
colliding receipt positions in both presentation and cost attribution; resource
references/notices have a separate group without changing the total. The Web
checks exercise the default section → message → body hierarchy, exact send-order
switching, lazy pages of 50, nested expansion restoration and no extra payload
reads. The Office next-request preview still has the separate
[skill/strict-projection gap](../../docs/stabilization/RISK_REGISTER.md#office-next-request-preview-diverges-from-active-preparation--2026-10-26).
`node tests/web/workspace-shared-draft.test.js` checks one mounted project/plugin
view and composer, one session draft and preferences across both screens, appended file reference, failed retry
identity, double submit and late acknowledgement with identical later text.
It also loses replies after acceptance and reconnects across project/plugin screens,
checking receipt recovery without resubmission and one persisted user message.
`plugins: input receipt crosses bridge before model response` holds the scripted
model response while checking both receipts through the real Application/WebView
bridge, early submit response, retry idempotency while still running, retained
runtime lease, Workspace model HTTP stage delivery and completion after bridge
recreation. Set `RNA_WORKSPACE_RECEIPT_FIXTURE` to an output JSON
path; `node tests/web/workspace-input-receipt.test.js <that-path>` consumes those
actual envelopes in Chromium. It checks early draft clearing, exact input identity,
repeated acknowledgements, a newer identical draft, background Office isolation,
unconfirmed delivery and recovery from a lost progress event. This is host-neutral
bridge/browser evidence, not real Windows/WebView2 qualification.
`node tests/web/workspace-run-lifetime.test.js <that-path>` consumes the same native
running/terminal snapshots: there is no pending submit timer during model wait,
the terminal change notice completes the run without a 1.2-second full-chat poll
and retains a newer draft. A later title notice refreshes the idle chat once
without the removed two-second title watcher. The preview bridge also returns
admission before its simulated run finishes.
`node tests/web/performance-diagnostics.test.js` checks the bounded on-screen
timing view for queue/bridge/render, Workspace/Office model phases, host post and
event-driven long tasks. It does not qualify Office STA or Windows WebView2 timing.
`plugins: admitted run cancellation and shutdown belong to application` checks
cancellation before admission while waiting for a lease, detachment of transport
cancellation after admission, exact Stop and owner shutdown. Both paths persist
kernel cancellation, drain workers and release session/runtime leases.
`plugins: approval continuation belongs to application` creates a real pending file
write, holds the post-approval model response and checks early bridge admission,
busy/cancel before admission, duplicate refusal (including after restart), bridge
recreation, late transport cancellation, exact Stop, shutdown, offline denial and
terminal API cancellation through the same worker. Set `RNA_WORKSPACE_APPROVAL_FIXTURE`
to an output JSON path; `node tests/web/workspace-approval-lifetime.test.js <that-path>`
uses those native snapshots to check consumed approval, no pending resolve timer,
terminal notification and draft retention. These do not qualify Windows/Office shutdown.
`plugins: input receipt reconnect is read-only and scoped` checks journal receipt
lookup after Application/bridge reopen, unapplied/unknown IDs, exact chat isolation,
bounded ID and unchanged event revision. The existing held-model receipt test also
reads the receipt while execution owns the session lease. Set
`RNA_WORKSPACE_RECONNECT_FIXTURE` to an output JSON path;
`node tests/web/workspace-receipt-reconnect.test.js <that-path>` uses those envelopes
to check reload, a newer identical draft, edit/Office isolation, atomic ack/clear,
unknown/foreign/unapplied receipt, no resubmit and removal of a lost submit's timer
both before and after progress acknowledgement. Protected draft tests retain the
submitted-version relation. Actual DPAPI/WebView2 abrupt-close remains unqualified.
`node tests/web/workspace-chat-races.test.js` delays running snapshots past a
terminal snapshot in both screens and switches plugin selection before an input
reply arrives; completed chats must remain writable without stale busy state.
It also opens a run started outside the panel and observes completion through
state polling in both project and plugin views. Late create, cancel, delete
and project-connection replies retain the newer chat selection.
`node tests/web/workspace-chat-cancel.test.js` checks one refresh/retry after typed
stale refusal in project/plugin chats, exact run/view ownership, terminal state,
uncertain delivery, bounded repeated refusal and acknowledgement without false completion.
`plugins: admitted run cancellation and shutdown belong to application` also
verifies stale refusal before the exact Stop. Shared-history/publication fixtures
keep current user-role tool results hidden through the common Office DTO.
`node tests/web/preview-workspace-identity.test.js` checks that the preview bridge
does not reuse a visible project ID after history deletion and reconnection.
`node tests/web/workspace-chat-run-notice.test.js` presents a failed typed run
through the real project and plugin panels, checks their common Office notice,
and verifies shared message rendering of reflection, tool activity and finish
evidence without exposing hidden v8 bodies. It also loads an older history page
through each panel. Harness `plugins: project chat history uses shared pages`
checks the 80-message window, raw indexes, hidden protocol bodies and workspace scope.
`node tests/web/splitter-preferences.test.js` checks the shared divider interaction:
frame coalescing, commit/cancel, capture cleanup, keyboard controls and ARIA values.

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
`chat-inbox.test.js` covers local removal, late acknowledgements and stale receipts;
`chat-inbox-observer.test.js` covers remote removal and history ordering through
actual views without redundant queue-only history fetches.
These are fake-host/model checks, not Windows/Office qualification.

Questions: `node tests/web/questions-interaction.test.js` exercises the product
page in Chrome (Playwright / `BROWSER_EXECUTABLE`): shared Office/project/plugin
forms, pagination/back navigation, single-choice advance, close after acceptance,
stale pending snapshots, explicit skips, failed-send retry identity, draft retention,
keyboard interaction, both themes and narrow layout. `tool-result-presentation.test.js`
covers exact retained question delivery; `chat-inbox.test.js` covers intake
receipts. Harness filters `plan mode: native question` and `plan mode: question`
cover restored accepted questions, readable semantic answers and explicit-skip
validation. These checks do not qualify Windows/Office/WebView2.

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
`context continuity: chronological read bodies and final retention` checks P4's
10 variants across three result roles: original user/call/result order, exact
scope/resource/view/coverage/body matching, full/partial/table/empty reads,
non-body metadata, T/E/R projection and removal of budget-evicted destinations.
`selects active and archived bodies once` also checks exact byte comparison before
suppressing a CAS view. The `v8_context_baseline.py` read/patch, multi-patch,
failure-correction and compaction cases inspect actual HTTP bodies and native pairs;
they use a scripted local server, never a real model.
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

`storage: runtime boundary` also checks indexed write-basis selection: checkpoints
materialize zero message rows at H=100/1000/10000; an edit reads one addressed row,
and a terminal append repairs a damaged call locator and reads its accepted call.
Selection, ordering, exact counts/revision and own-append recovery remain guarded.
Pair with actual Office native read batches, Workspace accepted-call history and
`model refs: durable allocation and finish admission`. The store-cost fixture reports
aggregate/runtime allocations separately; metadata/header work is still history-sized.

`excel write: explicit target required before dispatch` checks missing/null/blank
sheet/address across runtime, prompt and strict schemas and the direct adapter.
`excel write: worksheet names and quoted references` checks literal worksheet names,
Excel-quoted references, embedded apostrophes, spaces and resource round-trips.
`resources: canonical URI roundtrip` protects significant name whitespace.
These fake-host checks do not qualify Excel COM delivery or formula calculation.

`excel discovery: used ranges preserve bounds and completeness` covers workbook
metadata enumeration, non-A1 extents, missing `UsedRange`, bounded terminal pages
and descriptor/coverage changes invalidating cursors. It checks semantic discovery
without cell reads. Pair it with `excel discovery: incomplete catalogs preserve
document reads` and the worksheet-name round-trip check for this provider route.
`excel discovery: large ranges offer bounded overview` uses a sparse full-sheet
extent and long cell text. It verifies small metadata-only discovery/structure,
live worksheet validation, quoted sample targets, narrow offset extents, retained
overview evidence and one exact slice read. Raw reads (including `limit=1` records)
remain rejected before capture. Pair with `excel read: bounds precede materialization`
and `excel read: exact resource evidence and retained profile`. These checks do not
qualify the new COM metadata branch or establish whole-table analytics/model quality.
`excel read: local selected-column analysis is bounded` checks 200001 rows and a
far column without reading intervening cells, typed numeric/empty/other counts,
compact model projection, late capture failure, coordinate drift, and cancellation.
Pair with `excel read: native ownership and exact registration` for exact-schema
admission. These checks do not qualify Excel COM throughput or atomic consistency.

`storage: damaged sqlite metadata preserves startup and history` checks ten
sidecar corruption variants through catalog, load, pagination and startup recovery,
plus a healthy chat beside malformed JSONL. Every rebuild preserves event bytes.
`storage: sqlite cold catalog defers message indexes` checks plain/encrypted cold
startup, absent diagnostic blobs, persisted header-only indexes, append/suffix
catch-up and lazy full materialization of only the selected chat with pagination.
`storage: sqlite catalog and pages share document boundary` checks concurrent cold
readers behind a writer, header-cache reuse and an external append. `storage: sqlite
busy read can retry` holds an exclusive SQLite lock and checks bounded typed refusal,
retry and unchanged JSONL. `storage: sqlite protection checks are bounded` checks
plain/encrypted indexing, restart and pages with fewer than 24 protection-provider
calls for 240 messages. `storage: CAS trace publication shares maintenance boundary`
checks that payload publication waits for the same maintenance window as its event.
`storage: CAS missing runtime body isolates startup recovery` checks deleted/corrupt
in-flight bodies beside a healthy unfinished chat: global recovery proceeds, the
affected chat remains listed but refuses opening, and its JSONL is unchanged.
`bridge: init returns token` and `node tests/web/bridge-bootstrap.test.js` cover
the early settings handshake, delayed history, failure/timeout isolation and retry.
The bridge Harness uses its controller stub; real controller/Office delivery
remains a Windows qualification gap.
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
`vba: large module patch preserves whole-source safety` covers a module beyond the
former one-million-character internal ceiling: complete read-back and retained
source, stale confirmation refusal, explicit restore and restart reconciliation
without replay. The fake bound backend also checks that explicit bounded reads
remain bounded; `vba: reader validates typed snapshots` retains incomplete-source
refusal. These do not qualify real VBE delivery.
`tools: manual read-only run skips chat lease` checks that a read in another
session does not authorize stale delete/rename or alter exact patch matching.

The opt-in real-model fixture runs through the production conversation/runtime:
set `RNA_BASE_URL`, `RNA_MODEL`, `RNA_RESPONSE_MODE` and `RNA_CONTEXT_TOKENS`, then run
`dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll --resource-workflow-live /tmp/new-evidence-directory`.
Live diagnostics use the production conversation completion delegate directly: schema
responses already carry their negotiated envelope. Only scripted diagnostics use
`FixtureCompletion` to wrap domain JSON; applying it to live replies double-wraps v8.
The earlier 32K VBA failure was a snapshot/direct-tool-pack admission mismatch,
not proof of excessive skill size. With snapshot-based admission the unchanged
no-compaction scripted fixture passes at 32768 tokens. The focused
`skill packages: strict VBA selection fits actual request budget` regression covers
selected tools through schema discovery, source patch and finish at that limit;
the existing oversized-selection fixture retains refusal coverage.

An optional `RNA_REQUIRE_TASK_LIST=true` explicitly requests task tracking; keep it
separate from the unprompted-selection case. `RNA_API_KEY` is optional and never
saved. `RNA_INSTRUCTION_ROLE` and `RNA_REASONING_MODE` select explicit endpoint
profile settings (defaults: `developer`, `chat_template_kwargs`); the saved profile
records them. Requested reasoning-off still requires verification in the actual
response. The fixture retains synthetic before/after sources, requests/responses,
settings, session and store, and grades HTML/CSS/JS/VBA against exact expected text.
Capture the verified model tag/digest and endpoint profile with the evidence.
This fixture uses automatic approval; the controller regression covers manual
approval/restart. Neither qualifies Office/COM or rendered WebView2 behavior.

For a minimal VBA case, use `--vba-workflow-scripted` first, then
`--vba-workflow-live`, each with a new evidence directory and the same environment.
Both use 4096 output tokens and a bound fake document. The scripted variant makes
no provider calls and must finish load → read → patch → done without compaction;
the live variant uses the actual endpoint. Acceptance requires completion, exact
final source, one selected VBA skill and one document write. The fixture also saves
context estimates, budget components and exact live HTTP request bodies. It does
not execute VBA. The general `--resource-workflow-live` case remains separate.

Shared request preparation: `model request: snapshot surface budget and fallback`
checks snapshot tool membership, dispatch/admission projection parity and preservation
of full fallback messages. `workspace: strict prepared requests preserve call history`
exercises strict requests through the real workspace HTTP path and a subsequent turn;
`skill packages: strict VBA selection fits actual request budget` covers the Office path.

Skill-package checks: after the normal host-neutral Harness build, filter by
`skill packages:`, `tool pack:`, `skill editor:` and the two `agent:` prompt cases.
For the six-tool Excel bootstrap, `tool pack:` checks standalone Excel write and
format admission in both response modes, including one batched schema read and
rejection before admission. `agent: R61 routine Excel` checks inspection without
discovery; `skill packages: built-ins` checks the report package's complete tool
dependencies. `unified policy: native Office approval` and
`auto confirmation: native multistep` cover approval/restart and unknown effects
after the sheet schema is loaded. These fake-host cases do not measure model
discovery quality or qualify real Excel.

`agent: model projection compacts only callable schema` covers duplicate-schema
removal only against the current exact callable surface, all three result roles,
incomplete/mismatched/stale evidence and unchanged durable admission authority.
`tool pack: optional schema requires exact admission` also verifies compact receipts
in both response modes; the overflow and durable-replay cases retain their existing
guards. `model context: request schema modes and fallback` checks the authoritative
schema placement across strict transport and `json_object` fallback.

For report discovery use `--excel-report-workflow-scripted`, then
`--excel-report-workflow-live`, with the same profile variables and separate new
evidence directories. The real adapter and fake host share the production
`skills.excel.analysis.reporting` definition. The task asks for numeric regional totals,
a named sheet/table, a number format and a workbook chart, preserving source data.
Acceptance compares the final fake workbook independently of the model's message:
six state checks, completed runtime, at least five verified writes and no unknown
effects. The grader regression rejects changed source, numeric strings, missing
table/format and a wrong chart source.

The default live task names no skill or tool; direct schema discovery is valid.
Set `RNA_REQUIRE_REPORT_SKILL=true` for a separate explicit skill-load case; its
acceptance also requires the entire selected production body in a model instruction
message. Evidence separates `taskPassed` from `reportSkillOffered` and retains the
exact task, skill, workbook states and wire requests. Compare identical task/profile
cases; do not treat the explicit-load case as unprompted skill discovery. The fake
backend verifies state and read-back contracts, not Excel calculation or visual
rendering. Use the combined HTML/VBA fixture separately for cross-skill pressure.

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
preservation. `tests/web/product-mock-preview.test.js` checks the real chat inspector's
abilities view through the typed preview bridge; `product-shell-parity.test.js`
checks the shared shell and navigation geometry. Use bundled Playwright through
`NODE_PATH` and set `BROWSER_EXECUTABLE` if needed. These are not
Windows/Office/WebView2 qualification.

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
`conversation_v8_finish_smoke.py` checks rejection of an empty v8 finish after
a verified CLI write, feedback to the model and continuation without write replay.
`agent: Office HTTP premature finish keeps partial outcome` checks the production
Office conversation service over controlled HTTP with fake Excel: rejected empty
finish results, partial-finish recheck, no repeated sheet write and replayed outcome.
It does not run Office/COM.
`dotnet run --project demo/RNAssistant.MockDemo -- --unified-agent-test` exercises
the actual controller with a fake host, including permission snapshot cloning,
approval after settings changes/restart, AutoConfirm and planning wrappers.

`conversation v8:` checks batch policy in both parser and generated transport
schema: authorized reads, singleton effects, mixed batches, contradictory policy
and the 32-call bound. `protocol context: immutable complete snapshots` also
rejects schema generation with missing/incomplete authority. Request-size and
tool-pack checks cover schema projection and admission with both array branches.

`agent: no-tool` checks rejection and frozen repair of removed `continue`,
schema admission followed by a typed question, and a new run after the user's
answer. It also checks empty-`execute` repair followed by either a direct
answer or a real tool result with a cited v8 `finish`, in both response formats
and result roles. These scripted responses check control flow and emitted
guidance, not real-model decision quality.
`kernel: stopped continuation rejects unsafe history` and `protocol preflight:`
reject unsafe checkpoints/history without migration or dispatch. After building the CLI,
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
`context: compaction repairs` covers the shared `MaxAgentFormatRetries` limit:
1 disables repair, valid responses on attempts 3 and 20 succeed, unset/oversized
limits normalize to 10/20, and exhausted replies preserve the prior checkpoint.
Both modes keep frozen sources/schema and only the latest correction, preserve
accepted history, and do not retry refusal, transport errors or cancellation.
Kind-mismatch repair names the supplied source and allowed kinds without copying
the rejected claim. `conversation v8: batches only independent reads` also checks
that rejected skill-load batches expose no partial calls and require a standalone
load; the existing exact-admission and usable-selection checks cover recovery
after a refused load.
`context: compaction repair must fit` makes the initial strict request fit while
the added repair exceeds its budget, and checks refusal before the second dispatch.
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
narration without hydrating arguments. `settings: prompt`, `conversation v8:` and
`no-tool` cover prompt-schema migration, silent tool responses and shared default/schema/
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
path confinement and absence of model write authority from UI reads.
The isolated `large_views` scenario checks 2-MiB streamed text, exact Unicode
continuation after live edit/deletion and missing retained payload refusal.
The existing `views` scripted model-write tail is tracked in `BACKLOG.md`.
Real Chromium checks cover immutable preview routes, historical reopen after live
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
After building the CLI, `python3 -B tests/cli/stabilization_smoke.py` checks a
five-read batch with one narration, a bounded large-file tail, refusal of whole
replacement from partial evidence, multi-target patch → bounded read → patch.
Both mutations resume approval in fresh processes; per-target after-source labels, exact mixed line endings and
Unicode outside the patch are preserved. Focused Harness filters `workspace files:
guarded`, `resources: known VBA target`, `tool runtime: exceptions`, `skill packages:
exact request`, `context continuity:` and `tools: html source read` cover pre-effect
conflicts, direct VBA resolution, dependency-scoped recovery, source admission and
overlapping anchors. These are host-neutral checks, not Office qualification.
`skill packages:` includes complete-next-request selection admission: the default
32K VBA case executes the patch after schema 51 guidance reduction; another 32K
case with extra required user input refuses activation and stays usable after state
restoration. `skill_packages_smoke.py` also rejects an oversized
workspace procedure before activation and resumes the chat in a fresh process.
Neither check changes product context defaults or silently removes selected skills.
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
workspace authority in native-tool and user transport: shared core.write/patch
source headers, sequential and batched edits, refusal before any write, exact
whitespace and versions, current/historical evidence, restart, discovery across
chats, stale-write rejection, restore/tombstone and workspace isolation,
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

`tests/cli/resource_selectors_smoke.py` still uses pre-v8 responses and schema
paths; it is not current end-to-end evidence. Its repair is tracked in the
[risk register](../../docs/stabilization/RISK_REGISTER.md).
Use the focused `resources: VBA procedure workspace streaming`,
`resources: JSON tree workspace exact streaming` and
`resource context: workspace PDF page text` Harness checks for current selector
admission until that scripted CLI smoke is ported.


Conversation v8 has narrow [protocol checks](../RNAssistant.ProtocolTests/README.md)
and integrated Harness cases under `kernel:`, `context continuity:`, `conversation v8:`,
`v8 recovery:` and `agent: Office HTTP premature finish keeps partial outcome`.
The temporary phase-2 smoke runner was retired after its fixture checks moved into
Harness. The controlled
`python3 -B tests/cli/response_mode_smoke.py` exercises v8 through actual
CLI/HTTP requests. `interrupted_run_smoke.py`, `compaction_resume_smoke.py`
and `run_control_smoke.py` cover process loss, compaction, confirmation and
cancellation/continuation; `mode_questions_smoke.py` covers typed questions
and answer/restart on the candidate branch.

The phase-0 v7 red audit route has been retired after its failure was covered
by `agent: Office HTTP premature finish keeps partial outcome`.
The original request/response bodies, decoded event export and UI projection
remain in [Conversation v8 audit](../../docs/stabilization/conversation-v8/AUDIT.md).
The green scenario checks rejection, continuation and replay without repeating
the verified sheet mutation. Neither scenario qualifies real Office/COM.

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

## Browser automatic lifetime (P2c, 2026-11-11)

Use existing `browser-saved-views.test.js` and `html-editor-views.test.js` for ready
close/reopen: old iframe/editor removed, one fresh source read, preview input reset,
exact owner preserved and no Run cancellation sent. Saved views also cover a body
response that arrives after close/reopen without replacing the new page; its lease
is closed even when the synthetic transport ignores abort. Live dirty guards remain.
`browser-lifetime.test.js` covers five restores with one frame load;
`html-project-import.test.js` covers late Created after close without replay/navigation.
All four passed sequentially without production changes or a C# build.
[Evidence and limits](../../docs/browser/05_TEST_MATRIX.md#результаты-p2c--2026-11-11).

## Plugin restore freshness (P3b, 2026-11-11)

`node tests/web/browser-saved-views.test.js` also covers a retained release becoming
restorable after a new publication, menu-scoped head guards, conflict without retry,
catalog error/loading, stale menu responses and the retained iframe after restore.
`RNA_PLUGIN_RESTORE_SCREENSHOTS=/tmp/plugin-restore` saves wide/narrow error menus.
No C# build is needed for this UI-only slice; Windows/WebView2 evidence stays open.

## Plugin source failure (P3a, 2026-11-11)

`node tests/web/plugin-source-failure.test.js` covers initial failed check
without published-release fallback, no retry for unchanged source, watcher recovery,
retained previous preview, explicit retry/active release and narrow/125% geometry.
`RNA_PLUGIN_FAILURE_SCREENSHOTS=/tmp/plugin-failure` saves review PNGs. Pair with
`plugin-view-selection.test.js` for independent instances/annotations/late close.
Both pass; the broader `browser-views` focus assertion remains an open gap.

## Browser target resolution (P2b, 2026-11-11)

`browser target:` covers exact source/kind/scope, head drift without selection,
ambiguous/missing live targets and read-only capabilities through the actual catalog.
Pair it with `html editor: exact source` and `HTML project import:` (5 checks total).
The isolated baseline + P2b passes; controller/Office delivery remains unqualified.
`html import: inert preview` reaches the new typed raw-source assertions but fails
later on read accounting, also with the original f918a56f reader/fixture.

Sequential `browser-saved-views.test.js`, `html-editor-views.test.js` and
`html-project-import.test.js` preserve bounds/caches/dirty/lifetime and test foreign
descriptors, source-only without iframe, read-only editor/annotations and lease cleanup.
[Evidence and visual review](../../docs/browser/05_TEST_MATRIX.md#результаты-p2b--2026-11-11).

## Saved HTML to managed workspace (P2a, 2026-11-10)

`HTML project import:` covers exact CAS source after head drift, independent workspace
reopen/history, unsafe/colliding paths, data/SDK/vendor refs and UTF-8/size bounds before
writes, post-dispatch fault and read-back drift without replay or deleting partial files.
Use `bridge: typed artifact viewer` for typed source/backing/result delivery and
`workspace files: paths and historical view` for the shared path validation regression.
These five checks pass host-neutral; the Harness controller is a stub.

`node tests/web/html-project-import.test.js` checks exact actor/source, duplicate guard,
Created/Rejected/Unknown, existing project navigation, late/closed view, no automatic
chat/Run and 420 px / 125% overflow. Run sequentially with `browser-saved-views.test.js`
for retained content regression. `BROWSER_SCREENSHOT=/tmp/html-import` saves wide/narrow
review PNGs. Use existing Playwright `NODE_PATH` and optional `BROWSER_EXECUTABLE`.
Clicks over Source do not qualify the known pointer-routing gap over iframe.
The older `artifact transfer: project roundtrip and independence` also fails on clean
`c3a9427e` at HTML continuation without explicit workspace; it is an open baseline risk.
[Results and Windows/visual gaps](../../docs/browser/05_TEST_MATRIX.md#результаты-p2a--2026-11-10).

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

`chat sessions: shared recovery respects kernel and cursor` checks Office and
Workspace against the same terminal/abandoned kernel states and rejects recovery
from a stale observed cursor. It uses host-neutral stores, not a real Office host.

Run the focused `inbox:` filter for cancellation isolation, stale responses, batch
closure, final/approval races, exact run/pending steer admission and duplicate
receipt after target close, durable FIFO editing, recovery and Stop behavior.
`workspace: steer uses active run owner` also checks the bridge retry after completion.
`dotnet run --project demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -- --inbox-test`
checks first-chat intake, two controllers sharing one data root, actual controller execution and materialized model context
without Office. `--confirmation-input-test` checks Office confirmation in the
original kernel run, including pending steer. Run `node tests/web/chat-inbox.test.js` for UI acknowledgement,
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

`tools: compact Excel format schema preserves arguments` compares the compact
contract with the previous full variants: all 128 formatting subsets, omitted
targets, false/null, invalid types/enums and unknown fields. It checks parser
normalization, common requirements/defaults, malformed schema rejection and exact
provider-schema equality in both response modes. Prompt character counts are
separate from live API token usage. The Tool Library form uses the same compact
schema shape; its existing conditional/closed-object checks are in
`node tests/web/tool-library-ux.test.js`.

`html assets:` covers manual package discovery, private content pinning, atomic
create-only import, cross-workspace publish/reuse and unsafe manifest rejection.
`html tools: native ownership` checks exact read/write policies and bindings.

`model context: request schema` measures exact retained request bytes for Excel
Agent core, Task Tracking, VBA and HTML selections, each with one directly admitted
extension. It uses native descriptor evidence for `markdown.save`, verifies
complete selected bodies appear once, and prints prompt/skill characters plus the
serialized UTF-8 byte breakdown for `json_schema` and `json_object`. It also checks
schema-rejection fallback during repair. The optional-schema admission test checks
the production model-loop projection. These local fixtures use a fake host; byte
counts are exact, token counts are estimates and model quality is not assessed.
The modes/fallback fixture also checks per-attempt provenance after strict-schema
projection against the serialized request; the pre-dispatch trace check verifies
that a changed message cannot inherit a `Full` compiler label.

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
The repeated-read case also checks that a later budget eviction removes any
earlier receipt link claiming the identical full body was delivered.
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
32K resource page, explicitly reread the current source after a persisted
checkpoint, then edit it without losing unrelated lines. A constrained 32K
request refuses before model or tool dispatch when the complete causal context
does not fit, preserving the exact source.
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
`resource context: exact PDF page text` verifies two-page original extraction,
page-local character windows and coverage, schema/model projection, separate
retention, invalid page and source-byte mismatch without a renderer.
`resource context: workspace PDF page text` checks the same selector in CLI,
including retained exact bytes, discovery usage and Office-bound schema parity.

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
The same Harness filter covers PDF page/range selection completeness, CAS media
receipts without invented R or delivery claims, composite table/list/text and missing
comparisons for verified no-change/change/unknown. `resource context: exact PDF page text`
also traces actual PDF extraction into the presentation service. Activity and model
context Chrome suites check one-based PDF captions, bounded notes and actual-request
attribution. This does not qualify Office/WebView2 or binary media delivery.

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
`tests/web/chat-history-layout.test.js` uses the real preview shell with saved-event
fixtures: run/request/action hierarchy, immediate bounded group overviews, exact result/argument subtrees, model composition
and return, shared NavigationTree typography/selection/density, every alternate view,
wide/narrow light/dark layouts, zoom, multiple-run selection and one adjacent reader,
exact navigation/back and scope, close/reopen/move/split/restore, lazy bodies, empty/error
states and settings-dirty isolation. Use the same `NODE_PATH` / `BROWSER_EXECUTABLE`;
optional `HISTORY_SCREENSHOTS` writes captures. Pair with `run-journal.test.js`,
`trajectory-json-viewer.test.js` and `json-viewer.test.js` for reader/lifecycle checks.
`tests/web/diagnostics-loading.test.js` covers delayed/deduplicated reads, retained
refresh and failed pagination, 5 × 200 events with deferred collapsed branches,
keyboard/scroll order, short 600/390 × 400 panes and cancelled late responses.
It uses the same preview/Playwright setup; it does not measure Office storage latency.
`tests/web/product-shell-parity.test.js` compares shipped palette and joined header/
splitter geometry in all six sections with the prototype, then checks native project
tree search/collapse and shared panel toggles, including pointer focus after keyboard use.
`tests/web/settings-navigation-layout.test.js` checks the
Settings navigation and selected-model controls.

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
Together with `powerpoint resources:`, these check large-source character windows;
PowerPoint notes include automatic first-window shortening at a surrogate boundary
and explicit split-selector refusal.
`word search:` covers exact story-scope CAS evidence (including zero matches),
regex/coordinates/output bounds, historical no-I/O and malformed/oversized capture
refusal; `word tools:` also checks mutation invalidation and search bound-STA/close.

PowerPoint source cutover: `powerpoint resources:` and `powerpoint tools:` cover
document/slide text and notes, exact CAS evidence, one-capture paging, historical
no-I/O, explicit bounds, removed-call refusal, shared HTML capture and bound
STA/closed-presentation routing. Real COM shapes/notes and selection remain unqualified.
`resources: large VBA module line window` checks bounded large-source reads and a
600-line module returned whole when it fits the context. `resources: VBA windows
invalidate with module` checks cross-chat mutation and external-drift invalidation,
unchanged unrelated modules and retained historical windows. `resources: model
projection` checks source continuation coordinates through both final projectors.
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
| HTML visual selection staging, stale-source refusal, CAS and common Office draft | `Program.AttachmentTests.cs`; `tests/web/visual-inspector.test.js`, `html-visual-selection-draft.test.js` | `visual selection:`; run the two Node files directly |
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
| Model-context request/response window, provenance and full payload | `Program.ModelContextInspectorTests.cs`, request-before-dispatch, attachment-part and mixed saved-request golden checks; `tests/web/model-context.test.js` | `model context:`, `storage: model request trace precedes dispatch`, `storage: request attachment parts match payload`, `storage: mixed attachment manifest matches saved request` |
| Inspector raw request download / metadata-only bridge | `Program.PromptContextInspectorTests.cs`, `Program.ContextBridgeTests.cs`; `tests/web/context-json-viewer.test.js`, shared `tests/web/resource-download.test.js` | `context inspector:`, `bridge: typed prompt context inspector` |
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

Changes to versioning implementation use the existing `Program.ProjectStructureTests.cs`
suite. A product-number bump alone needs only `ValidateVersionFormat`, plus the
checks for its actual product behavior; docs-only work needs diff/link checks
(and the format target before an actual commit):

```bash
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "versioning"
node tests/web/settings-version.test.js
```

The `versioning` substring selects only versioning cases. These need Git and dotnet;
they invoke MSBuild against disposable small projects, commits and local bare remotes.
Fixture refs never affect the working repository or its origin. Coverage includes
absence of automatic build-time bumps and acceptance of intermediate commits without
a bump (not a prohibition on completed-change bumps), source archives without Git
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

`excel table:` covers exact native ownership, explicit source, generated and
explicit names, style/header projection, source and workbook-collection bounds,
case-insensitive collision, exact pre-dispatch drift, post-dispatch failure,
read-back divergence and closed bound-document refusal. The fake generic host rejects
the public id, so dual dispatch is observable. Real Excel `xlNo` header/range
semantics, style localization, overlap/protection, rollback and partial COM effects
remain WQ-EXCEL gates. See
[Phase 11T4 evidence](../../docs/stabilization/PHASE_11T4_EXCEL_TABLES.md).

## Typed Excel chart owner (Phase 11T5)

`excel chart:` covers exact native ownership for `create_chat_chart`, `upsert_chart`
and `delete_chart`, chat-artifact source reads, explicit creation source,
omitted-source update preservation, unsupported type rejection,
create/update/delete/default/strict-mode
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
(currently 80 built-in ids/rows); its pass does not qualify real Windows/model behavior.

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
core `skills.upsert/delete` and reference
`skills.reference.upsert/delete` registrations, Agent-only confirmed-write policy,
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
publishes semantic `core.find/read/search/outline`, keeping provider routing,
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

The schema-20 correction makes `core.read` return one complete bounded
representation or an explicit error. Broad VBA inspection starts from
`RUNTIME_CONTEXT.document.vba_project_target`; unfiltered VBA find is only fallback
discovery and keeps the project target first, while queried find remains filtered.

R61/11O2 replaces Plan create/update with `plan.document.save`, replaces the
three Task List lifecycle ids with `task.list.set`, and removes caller-owned
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


Inline chat visuals: `node tests/web/chat-visuals.test.js` (Playwright, optional
`BROWSER_EXECUTABLE`) covers real Mermaid, completed fences, offline HTML/ECharts,
source copy, enlargement, stop/restart, stale loads, runtime errors and both themes
at narrow width. Pair with Markdown raw-HTML/JSON tests, stream-render-cost and
vendor gate. Filter `skill packages: built-ins` verifies the shared instruction-only
visualization skill in Office/workspace. Native WebView2 navigation and frame-name
binding require a separate Windows check; these browser tests do not qualify it.

### V8 architecture stabilization (2026-10-26)

Focused filters: `kernel:`, `kernel replay:`, `model refs:`, `skill packages:`,
`model request:`, `model context: saved request composition`,
`storage: own append`, `storage: partial append`, `storage: metadata conflict`,
`model protocol: fallback`, `model protocol: invalid schema`.
The append fixtures inject before-write/after-record/after-flush failures; they do
not qualify power-loss or Office COM behavior. Inspection fixtures retain target
and completion-time facts across restoration and reject unrelated/stale/partial
or immutable evidence without allowing unknown replay. Saved request composition
opens exact bytes through a new store/Inspector. Actual HTTP/CAS byte equality and
frozen repair are covered by `tests/cli/tool_result_projection_smoke.py`.
Commands/results and scope: [V8 evidence](../../docs/audits/evidence/v8-architecture-2026-10-26/README.md).
