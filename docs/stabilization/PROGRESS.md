# Stabilization progress

Current target: 16.1.0
Current phase: Phase 11 migration contours — перенос всех существующих tools и удаление active legacy architecture явно обязательны до Phase 12; новые optional product features этим не добавлены
Current task: Resource Fabric cursor-scope fix and atomic 11T0/7D bound Excel cutover are complete host-neutral; 11T1 find/replace is the next typed-family migration. Real Windows/live-provider gates remain open.
Execution mode: Phase 11 migration baseline интегрирован поверх завершённых host-neutral WQ-A1–A5. Existing-tool migration и удаление active legacy обязательны. WQ0 больше не блокирует implementation: текущий `RuntimeKey` exact bound workbook принимается как lifetime assumption; реальные Windows/live-provider gates остаются deferred evidence и не могут быть закрыты локальными tests.

Next step for tools: 11T1 переносит exact existing Excel find/replace family на typed request/domain service/direct bound backend/outcome path и удаляет заменённые host switch/helpers. Расширение schema и новые batch abstractions не входят в этот slice.
Required context: [master Phase 11T](STABILIZATION_MASTER_PLAN.md), [11T0 evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md), [architecture follow-ups](ARCHITECTURE_FOLLOWUPS.md), [migration map](MIGRATION_MAP.md), current Excel find/replace owner.
Open gates / remaining legacy: 11A1–11A2, 11B1–11B3, 11C1–11C3, 11D1 and 11T0 are done host-neutral, but R51 remains open for image/PDF/audio viewers, other committed-resource removal and Windows WebView lifecycle. Remaining Office tools still move family-by-family; the final switch removes `IOfficeApplicationAdapter.GetBuiltInTools/ExecuteTool`, host tool-id switches, `LegacyToolDefinitionAdapter`, `LegacyToolResultAdapter` and remaining UI/domain compatibility projections. Excel read/write compatibility commands/backends and execution-time target fallback are gone. Permanent narrow journal ports and current model-compatibility diagnostics are not legacy because they do not create a second authority. Mandatory host-neutral route 0–10 and WQ-A1–A5 implementation are complete, while all real Windows/live-provider gates remain open; catalog/evidence admission tests are not scenario evidence. Production OfficeHosts/VSTO build, actual COM marshal/cleanup, real DocumentSession lifetime, WQ0, WQ-SESSION and WQ-EXCEL are open evidence, not implementation blockers. Full Phase 6 Windows/VBE, Phase 8 WQ-PACK, Phase 9/R45–R48 WebView/restart/multi-window and R28/R29/R32 live-provider/UI gates remain open. R52 Host Fabric, R53 Local Automation, R54 Skill Library, R56 Tool Library, R57 Issue Center and R58 typed-facade risk remain open. Product 16.1.0-dev, no release/tag.

Live artifact projection correction (2026-08-31): после каждого durable
`tool_result` controller теперь ставит в WebView очередь существующую full
revision-guarded chat projection до дальнейшего progress/следующего model step.
Поэтому chart/tool-result, Plan, Task List и HTML artifacts появляются во время
работы, а не только в terminal response; confirmation continuation использует тот
же путь. Новый progress payload, store, transport или client lineage не добавлены.
Web artifact test 4/4, focused bridge cases 1/1 + 1/1 и реальный MockDemo controller
artifact test pass; только три прежних PDF platform warnings. Windows WebView2/
Office не проверялись, текущий 11T1 next step и остальные R51 gates не меняются.
[Evidence](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md).

Phase 11T0/7D bound Excel cutover (2026-08-31): desktop, VSTO and native
composition now bind one exact workbook to `ExcelDocumentSession`, capture the
current `RuntimeKey` once for that object lifetime and expose one direct
`ExcelInteropBackend`. Native Excel inspect/read/write and HTML Excel reads no
longer roundtrip through generic host commands. Range/selection ownership is checked
against the bound workbook, and a closed session fails instead of rebinding.
`ExcelReadCompatibilityBackend`, `ExcelWriteCompatibilityBackend`, their four
internal ids, `ExcelAdapter.WriteRange.cs` and repeated descriptor/`ActiveWorkbook`
execution resolution are physically removed. Focused Excel read 4/4, write 4/4,
HostRuntime 10/10, accepted-call 1/1, HTML binding 1/1, identity-probe 5/5,
architecture 4/4 and source inclusion 1/1 pass; MockDemo compiles with 0 errors /
3 existing platform warnings, and changed production host/composition sources parse
as C# 7.3 without syntax errors. Real Windows COM/proxy/lifetime and desktop/VSTO/native composition remain
WQ0/WQ-SESSION/WQ-EXCEL evidence; failures fix the new contract without restoring
legacy. [Evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md).

Pre-R37 trajectory inference removal (2026-08-31): `TrajectoryRunProjection` and
`TrajectoryDerivedProjection` no longer reinterpret a persisted
`tool.result.recorded` carrying `AcceptedCallOrigin` as a current accepted call.
`run-causal` preserves the exact operation as `incompatible` with explicit reset
metadata; `tool-execution` grants it neither call nor completed-result authority.
No history is rewritten or deleted. Focused trajectory harness 4/4 passes; Windows
Diagnostics/WebView qualification remains open.

Accepted-risk WQ0 decision and complete active-legacy removal (2026-08-31,
docs-only): пользователь
потребовал перенести все существующие tools на typed architecture и удалить legacy
execution/history paths до Phase 12. Это не допускает big-bang rewrite и не добавляет
новые Browser/Automation/product capabilities: каждый existing semantic family
переключается отдельно с удалением последнего старого consumer. По явному решению
WQ0 больше не prerequisite: 11T0/7D атомарно принимает текущий `RuntimeKey` exact
bound workbook на его lifetime, чтобы bound workbook никогда не сосуществовал в
production с compatibility Excel backend. WQ0 остаётся deferred Windows evidence;
при выявленном расхождении исправляется новый bound contract без возврата legacy.
Canonical route и removal gates обновлены; runtime этим решением не менялся.

Artifact identity authority audit completion (2026-08-31): normalization no longer
selects one of duplicate case-insensitive artifact ids. Exact URI/reachability,
storage hydration/save, bridge/Library, Plan/Task List/HTML navigation, attachment
linking and tool-result reuse now omit or reject ambiguous immutable identities
without rebinding evidence; unrelated unique artifacts remain usable. Attachment
artifact provenance cannot move between source messages or bodies. Focused resource,
attachment and tool-result regressions pass; full host-neutral harness 539/539 and
`git diff --check` pass. No Office/COM/WebView path or Windows gate changed.

Runtime diagnostics/VBA usability fix (2026-08-31): existing raw model request and
response CAS payloads are now opened directly from their Run Journal rows; accepted
arguments, executor result/data and typed effect evidence are present in row content,
while attempt/call/mutation ids stay in a collapsed technical section. The UI no
longer presents one legacy unverified write as both `без проверки=1` and a second
`неизвестный эффект=1`; it labels that single cause as legacy without read-back.
`resources_list(provider=vba)` now returns the project plus live components by
default, and backups remain explicit. `excel.inspect` is no longer described as a
write preflight, and the fixed v4 schema tells the model to check all requested
deliverables before ending its loop. Already callable tool summaries/safety are no
longer duplicated in the compact capability index; unloaded tools and skills retain
selection metadata, and the mandatory Excel/VBA request now has an explicit prompt
headroom regression. No event, id, store, verifier phase or generic batch abstraction
was added. Focused harness and Web UI suites pass; Windows WebView2/Office remain
unqualified.

Resource continuation scope fix (2026-08-31): prior immutable cursors were raw opaque
offsets, while revision-bound cursors carried only offset plus data hash. Reusing a
cursor on another URI/query with identical content or collection hashes could
therefore skip data silently; with different hashes it surfaced as revision drift
and encouraged the repeated read loop visible in the supplied screenshots. Cursor
v2 now independently binds list continuations to provider/kind and read continuations
to canonical URI/normalized representation, while retaining content/collection drift
guards. Cross-scope use returns non-retryable `resource_cursor_invalid`; live drift
explicitly requires a fresh same-URI read with both cursor and revision omitted.
Immutable and identical-source VBA cross-resource regressions, exact-list-query
regression, normal continuation and stale-live-revision cases pass host-neutral.
No retry/dedup state or second resource authority was added; Windows Office remains
unqualified.

Resource member reference fix (2026-08-31): HTML mutation results exposed only an
internal revision artifact id, while exact member discovery used an opaque SHA-256
key. The model could therefore reconstruct a plausible path URI that always fell
through to generic not-found. Mutations now return the exact artifact `ResourceRef`
and current member URIs; the existing `common.resources_resolve` supports one
central parent-revision + member-path lookup. Exact chat resolution now separates
invalid URI, chat mismatch, artifact/revision/member absence, noncanonical member
keys and corrupt payload, with recovery guidance in the typed Tool Result. No new
resource store, transport or correlation id was added. Verification: `resources:`
10/10, native resource runtime 1/1, HTML workspace/source 2/2; four existing
Windows-only COM analyzer warnings. Windows/WebView2/Office were not run.

Empty-text Resource Fabric regression fix (2026-08-31): exact resource reads used
`IsNullOrWhiteSpace` as an availability test, so valid empty and whitespace-only text
attachments/documents were rejected as missing despite exact hash/body evidence.
Availability now distinguishes a missing body from a present empty body; exact reads
and the text viewer preserve zero-length and whitespace content, while an unavailable
CAS-backed body still fails closed. Focused Resource Gateway coverage passes; no
Office/COM path changed.

WebView failed-initialization follow-up (2026-08-31): bridge bootstrap now queues
early non-init calls, but an exception while applying an already-tokened init response
could leave that token live and release queued calls from an unavailable UI state.
Entering the bridge-unavailable state now revokes the token; queued calls fail closed
and a later explicit reinitialization must obtain a fresh token. The focused bootstrap
test covers this partial-init failure; real Windows WebView2 lifecycle remains open.

Artifact identity ambiguity regression fix (2026-08-31): the chat resource
projection previously collapsed case-insensitive duplicate artifact ids by selecting
the first replayed item. Exact URI reads could therefore expose an arbitrary body
after corrupt or inconsistent replay. Ambiguous ids are now omitted from list and
search, exact resolve/read fails closed, and neither shared URI helpers nor the
bounded resource/active-Plan prompt can reintroduce the ambiguous id. Unrelated
unique artifacts remain available. The focused Resource Gateway regression passes;
no Office/COM path was changed.

Attachment provenance regression fix (2026-08-31): chat resource reads and the new
11D1 text viewer previously preferred the declared source message but then searched
all other messages for the same attachment id. A missing/corrupt source binding could
therefore hydrate unrelated bytes or extracted text under another artifact URI. One
shared exact resolver now requires a unique source message, a unique metadata-named
attachment, matching artifact/attachment CAS SHA-256 and byte length, and non-failed
attachment status. Invalid mapped attachments remain metadata-only and cannot fall
back to `InlineText`. Focused Resource Gateway and artifact-viewer tests pass; real
Windows/WebView2 and Office validation are unchanged.

MockDemo qualification composition regression fix (2026-08-31): WQ-A4/A5
expanded `QualificationBuiltInCatalog` to the complete versioned suite, but the
source-linked MockDemo assembly still embedded only coverage and the UI-shell pack.
Constructing the production controller therefore failed on missing
`common.quick.v1.json` before every demo model profile. MockDemo now embeds all
canonical pack files with their exact production logical names. The production
source-inclusion architecture test also compares every pack file and logical name
across Office, Harness and MockDemo so later suite additions fail closed at build
verification. Focused structure test and Release MockDemo full self-test (four model
profiles plus failed-turn persistence) pass; only the three existing PDF CA1416
warnings remain. Direct .NET Framework 4.8 helper build is unavailable on this Mac
without the Windows targeting pack and remains part of the real Windows gate.

Default Excel Agent callable-pack regression fix (2026-08-31): growth of the
complete optional capability index pushed the mandatory Excel/VBA core request to
about 28,742 tokens against the conservative 28,672-token input budget, so Agent
failed before the first model dispatch. The complete catalog, exact ids/revisions,
safety/body metadata and all core schemas remain unchanged; only prompt-index
summaries now keep a 96-character prefix, while explicit capability search retains
the previous 160-character prefix. The existing VBA/core regression now fails with
the actual pre-dispatch error instead of an empty prompt, and focused Agent plus
capability-discovery tests pass. Full host-neutral harness will be rerun at the end
of the branch audit; real provider/Office qualification is unchanged.

WebView bootstrap regression fix (2026-08-31): photo triage and browser smoke
reproduced the UI-wide failure as a script-order regression in
`app-html-workspace.js`: it captured `saveChatMode` before `app-chat.js` loaded,
aborting bootstrap before `initialize()` could receive the WebView bridge token.
The action callback now resolves `window.saveChatMode` only when invoked, and the
shared `send()` path queues non-`init` bridge calls behind `initializePromise`
instead of posting them with a null token. `app-core.js` and
`app-html-workspace.js` cache keys were bumped, and web cache-key assertions were
aligned. Verification: `tests/web/*.test.js`, `node --check` for `web/js` and
`tests/web`, `git diff --check`, plus local browser smoke for top tabs, settings
pages and diagnostics sub-tabs all pass. Real Windows WebView2/Office/VSTO and
live bridge callback validation were not run.

Phase 11T Office tool modernization route (2026-08-31, docs-only): code audit
подтвердил, что production `IOfficeDocumentSession` ещё не реализован, Excel typed
read/write используют explicit compatibility backends до 7D, а Word/PowerPoint/
Outlook и остальные Excel families всё ещё входят через generic host `ExecuteTool`.
План теперь требует parity-first migration: один semantic family получает typed
request/domain service/bound backend/outcome/effect evidence и в том же change теряет
legacy branch/mapper/helpers. Только после qualification/evals отдельно допускаются
`upsert_table`, richer sort/filter/format, exact Word ranges, stable PowerPoint refs,
bounded `compose_slide` и exact Outlook EntryID updates; generic batch writes и
`execute_actions` запрещены. Runtime/UI/schema не менялись; WQ/Phase 12 не закрыты.
Docs-only verification: `git diff --check` и local Markdown targets pass; harness,
build и Windows/Office/VSTO validation не запускались.

Excel interop boundary normalization (2026-08-31): `Excel.Name.RefersTo` теперь
явно приводится к строке перед заполнением typed `ExcelNameSnapshot`; schema,
bounds и read route не менялись. `excel read:` — 4/4 pass, с 4 ожидаемыми
CA1416 warnings из Windows-only identity probe. Реальный OfficeHosts build и live
Excel COM остаются Windows gate.

Phase 11D1 bounded text/source and Markdown viewers (2026-08-31): новый
host-neutral `ArtifactViewerService` принимает только canonical exact artifact URI
активного chat и читает representation через общий Resource Gateway страницами по
32,000 символов с общим limit 512,000. Full copy/download появляется только после
contiguous read с неизменными URI/hash/total/kind; truncated attachment extraction и
over-limit source остаются partial. Text/Markdown adapters UI-only: line numbers,
page search/copy, sanitized Markdown только для complete source и exact Source tab.
Старый generic artifact `<pre>` удалён; JSON и inert uploaded HTML сохраняют своих
owners. Viewer state ephemeral и очищается при chat switch. Harness 4/4 focused,
web 48/48 targeted/reused; changed JS syntax, version format, diff, source inclusion
и local Markdown links pass. Windows WebView2/clipboard/download не проверялись.
[Evidence](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md).

Approved Phase 11 priority: complete Artifact/Plan/HTML Workbench viewers → coherent
Library UI and typed Issue Center → read-only selected-endpoint Tool Inspector →
Excel Host Fabric core → independently qualify Word, PowerPoint and Outlook and add
their endpoint adapters → unified all-host picker → custom Tool Library authoring →
Skills authoring → Browser/Local Automation. Pipelines remain disabled. This order
does not expand Phase 12 or replace real WQ gates. [Tool Library](../tool-library.md),
[Issue Center](../qualification.md#11-phase-11-issue-center),
[Host Fabric](../host-fabric.md).

Phase 11 product-route review (2026-08-31, docs-only): tools are split into existing
runtime execution, early read-only capability truth and later revisioned authoring.
The target UI keeps Artifacts, Tools and Skills as separate Library authorities and
adds a Problems projection over exact trajectory/qualification evidence with
redacted issue export. Host Fabric proves Excel routing first; Word, PowerPoint and
Outlook must each pass local tools/resources/effect gates before endpoint admission.
Master plan, architecture, Artifact/Host/Qualification contracts, backlog and risks
updated; runtime/UI behavior and Phase 12 release scope unchanged. Ten changed
Markdown files have valid local link targets; `git diff --check` and pre-commit
`ValidateVersionFormat` pass. Harness/Office/WebView tests were not run for docs-only
planning.

Phase 11C3 exact HTML binding recovery/export (2026-08-31):
`HtmlWorkspaceArtifactService` остался единственным owner whole-workspace revisions;
`ChatStore.Save` больше не создаёт hidden artifact/revision, старый fallback и dead
helpers удалены. Binding хранит SHA-256 exact transformed JSON и явную completeness
`complete|bounded|truncated`; mismatch при replay/normalization становится error.
Refresh остаётся ephemeral до chat/export checkpoint. Export требует exact non-empty
active head, при изменении создаёт ordinary revision через того же owner и возвращает
typed exact artifact id, canonical `rna://`, CAS hash и полный workspace. UI блокирует
dirty/stale/incomplete evidence; standalone assembly сохраняет raw JSON lexemes без
parse/stringify rounding и публикует completeness/hash metadata. Harness 8/8 focused,
web 21/21 и changed JS syntax pass; version format, diff, source inclusion и local
Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C3_HTML_BINDING_EXPORT.md).

Phase 11C2 inert uploaded HTML import/source preview (2026-08-31): новый
host-neutral `UploadedHtmlResourceService` принимает только exact canonical immutable
attachment revision с совпадающими message/attachment identity, hash/length и HTML
type. Typed source preview идёт через Resource Gateway с bound 32,000 и UI вставляет
его только через `textContent`; chat switch сбрасывает незавершённый read. Explicit
import требует exact active HTML guard, новый `.html`/`.htm` path и полный decoded
payload до 300,000 символов, затем создаёт обычную workspace revision с exact
source URI/hash/relation provenance; original CAS/message ref не меняются. Harness
5/5 focused, web 21/21 и changed JS syntax pass; version format, diff и local
Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C2_HTML_IMPORT_PREVIEW.md).

Phase 11C1 HTML whole-workspace lineage (2026-08-31): новый host-neutral
`HtmlWorkspaceArtifactService` теперь выдаёт revision как global `max+1` по всем
HTML branches, а не `active+1`; alternative branches больше не получают одинаковый
номер. Каждый child сохраняет exact active parent, Library продолжает выбирать
explicit active pointer даже при более новой inactive branch. Duplicate/invalid
revision graph отклоняется до workspace mutation или pointer restore; missing-parent
degraded recovery остаётся readable/mutable и не угадывает ancestry. Новый targeted
lineage test и пять затронутых storage/recovery/Library regressions pass; version
format, diff и local Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C1_HTML_LINEAGE.md).

Phase 11B3 Plan history/removal UX and handoff (2026-08-31): новый host-neutral
Каждая non-head Plan revision получила `Восстановить` с exact server-projected
current/source guards; action создаёт новый head через 11B2 owner и сериализует
повторные UI mutations. Delete сначала выполняет dry-run, затем показывает revision
count и все referencing message ids, и только после confirmation повторяет тот же
exact guard. Ready handoff сверяет active raw artifact, status и byte-exact `rna://`,
переключает Agent и отправляет только URI с `common.resources_read` instruction.
Mutation/handoff logic удалена из detail renderer и осталась в тематическом actions
owner. Web Plan 7/7, Artifact Library 3/3 и syntax четырёх changed JS modules pass;
version format, diff и local Markdown links pass. Windows WebView2 interaction не
проверялась. [Evidence](PHASE_11B3_PLAN_HISTORY_HANDOFF.md).

Phase 11B2 Plan restore and tombstone removal (2026-08-31): новый host-neutral
`PlanDocumentService` теперь владеет restore/delete: restore требует exact active
head, копирует полный body/title/status выбранной revision и добавляет linear child
с `restoredFromArtifactId`; delete требует тот же guard, добавляет `removed:true`
tombstone и очищает active pointer. Ни `artifact.remove`, ни rewrite исторических
message refs не используются. Library/list/search/prompt/checkpoint исключают removed
Plan, exact resolve/read возвращает non-retryable `resource_removed`; replay,
prune и fork сохраняют применимый tombstone. Harness `plan document:` 2/2,
`plan mode:` 2/2, Artifact Library 3/3, resource gateway 1/1 и source inclusion
1/1; web Plan 4/4 и Artifact Library 3/3; changed JS syntax, version format, diff
и local Markdown links pass. Windows WebView2/reload/fork UI не проверялись.
[Evidence](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md).

Phase 11B1 Plan exact revision guard (2026-08-31): новый host-neutral
`PlanDocumentService` владеет create/update lineage; complete Markdown больше не
проходит через `Trim`, включая UI save, и сохраняет leading/trailing/hard-break
spaces. Update требует exact active artifact id и unique contiguous linear head;
stale или broken/skipped/branched state не добавляет revision. Старый create/update
logic удалён из executor, delete временно перенесён без semantic change до 11B2.
Harness `plan document:` 1/1, `plan mode:` 2/2 и source inclusion 1/1; web Plan
2/2 и reused Artifact Library 3/3; changed JS syntax, version format, diff и 307
local Markdown targets pass. Windows WebView2/clipboard/reload не
проверялись. [Evidence](PHASE_11B1_PLAN_REVISION_GUARD.md).

Phase 11A2 Artifact Library projection (2026-08-31): новый host-neutral
`ArtifactLibraryProjectionService` строит из replayed session один revision-stamped
read-only DTO с server-owned resource class/group/display kind, exact head URI и
полной exact history. Plan/Task List группируются по logical id; HTML head выбирает
active pointer и сохраняет ancestor/alternative branch relations; immutable charts/
snapshots не схлопываются по parent. Raw artifacts остаются exact source message
cards/viewers. Client lineage/max-revision inference удалён, original/derived/vN
labels больше не зависят от extension или `Revision>1`; Plan называется Markdown,
не JSON. Direct HTML responses обновляют artifacts/library под тем же revision guard.
Harness 9/9 targeted, web 10/10 и JS syntax pass; MockDemo full self-test pass для
четырёх profiles + failed-turn persistence (три прежних CA1416 PDF warnings).
Version format, diff и 304 local Markdown link targets pass. Windows WebView2/reload/
multi-window не проверялись; Plan/HTML mutation и viewer slices не закрыты.
[Evidence](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md).

Phase 11A1 artifact commit projection (2026-08-31): после durable attachment CAS,
message/artifact linking и mandatory chat save controller синхронно отправляет full
`chatState` с monotonic `sessionRevision`, committed message, exact `ResourceRef` и
artifact revision до attachment analysis/helper и primary model transport. Catalog-
only title updates остались отдельным scope; active UI применяет full push через
существующий per-chat revision guard, background chat не перехватывает navigation.
Composer/message chips различают `Не отправлено`, `Подготовка`, `Оригинал`.
Provider failure после boundary сохраняет user turn/resource. MockDemo focused
controller case pass; bridge 1/1, web 3/3 + reused updated run-view 5/5 pass.
Version format, diff и 270 local Markdown link targets pass. Windows WebView2/reload/
multi-window не проверялись; heads/history/kinds subsequently completed in 11A2.
[Evidence](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md).

MockDemo v4 cleanup (2026-08-31): scripted responses больше не присваивают
model-owned call IDs, completion определяется по Tool Result v1 `status=ok`, а HTML
create/edit загружает exact `common.html_workspace_upsert` через
`common.capabilities_read`; удалён вызов снятого `common.html_workspace_read`.
Полный Release `--self-test`: четыре model profiles и failed-turn persistence pass;
только три существующих CA1416 PDF warnings. Production runtime не менялся.

Qualification Center requirements (2026-08-31, WQ-A0 docs-only): пользовательский запрос на встроенные расширяемые проверки оформлен в [canonical contract](../qualification.md) и [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md). Empty-chat card должна открывать отдельный wizard, а не вставлять prompt. Packs versioned/data-only, complex agent tasks идут через production runtime, pass принадлежит typed verifier evidence; dedicated qualification chat использует тот же events/CAS и causal journal. Первый pack — Excel WQ0 с in-app VSTO/native observations и narrow independent-client helper. PowerShell остаётся временным engineering fallback. Код/UI/helper не менялись; WQ0/5B2/R04 не закрыты. Docs diff/205 local links/anchors в затронутых документах и pre-commit `ValidateVersionFormat` — pass; build/tests не запускались.

WQ-A1 host-neutral core (2026-08-31): добавлены strict schema v1 pack parser,
coverage registry/catalog, конечный runner через narrow allowlisted action/verifier
ports, verifier-only automatic pass, cleanup/cancellation и fail-closed replay без
auto-retry. Четыре closed qualification event operations — Agent authority/mandatory;
step-start предшествует action, большие expected/actual используют тот же chat CAS.
Typed bridge DTO добавлены без controller route/UI. `qualification:` 8/8,
`storage: typed event port` 1/1, production source inclusion 1/1; Harness Release и
MockDemo Release compile без errors (только существующие platform warnings).
ValidateVersionFormat, diff и local links pass. Полный harness, Windows/Office/VSTO,
WebView/live provider не запускались. WQ-A2/A3, WQ0/5B2/R04 открыты.
[Evidence](WQ_A1_QUALIFICATION_CORE.md).

WQ-A2 Qualification Center shell (2026-08-31): добавлены exact embedded
coverage/pack `WQ-A2.shell`/`common.ui-shell`, application service, typed
controller/bridge routes и один WebView wizard из empty chat и Diagnostics.
Run живёт в dedicated document chat, восстанавливается из того же
validated event stream без index, а ordinary conversation turn в нём fail-closed.
Typed shell verifier сверяет только persisted preflight/manual evidence;
UI показывает server-owned status, shared JSON viewer, exact run journal и
bounded report. `qualification:` 10/10, web qualification center 5/5, Harness и
MockDemo Release build без errors; MockDemo restart round-trip и browser preview pass.
Windows/Office/VSTO, COM, live provider/model и full suites не проверялись;
WQ-A3/A4/A5, WQ0/5B2/R04 открыты. [Evidence](WQ_A2_QUALIFICATION_CENTER.md).

WQ-A3 Excel WQ0 implementation (2026-08-31): identity decoder, retained marshal
lease и native HWND resolver перенесены из diagnostic test project в единственный
`OfficeHosts.Qualification` owner. Добавлены exact `IQualificationHostPort`,
UI-thread/dedicated-STA forwarding, embedded release pack `excel.wq0.identity` и
runner-owned fixture matrix: independent client A/B, switch, Save As, second window,
detach/attach C, close/reopen new lifetime, same-name workbook in another process,
typed verifier и cleanup. Narrow x64 helper принимает только bounded
bind/list/observe/release messages по one-time nonce named pipe, explicit HWND/index
и сверяет same owner assembly MVID; no network/shell/custom command. PowerShell
fallback переключён на тот же owner, duplicate sources/project удалены. Host-neutral
checks: `qualification:` 11/11, `excel identity probe:` 5/5, WebView qualification
5/5. OfficeHosts/helper/VSTO и реальный COM/WQ0 на Windows не запускались; поэтому
5B2/7D/R04/WQ-SESSION остаются открыты. [Evidence](WQ_A3_EXCEL_WQ0.md).

WQ-A4 suite catalog (2026-08-31): embedded eight canonical family manifests for
common/provider/storage/UI/Excel/VBA/cross checks. Each manifest pins revision and
content hash, exact readiness capability, finite data-only actions, required typed
final-state assertion and runner-owned fixture/cleanup where mutation is possible.
Coverage registry names every mandatory Excel quick/full/release scenario owner.
No adapter capability is inferred from manifest presence: unsupported packs remain
N/A and cannot start. `qualification:` 12/12 pass; production resource inclusion,
diff/link and version checks are recorded with the commit. Live provider,
AgentTask, Windows/Office/VBE/WebView/restart and host final-state evidence were not
run. [Evidence](WQ_A4_SUITE_CATALOG.md).

WQ-A5 exact-build release evidence (2026-08-31): added strict bounded detached
RS256 envelope/payload, candidate certificate pin, assembly metadata identity,
catalog fingerprint, artifact hashes and full 19-run matrix admission. The
application exposes evidence status/provenance and the read-only
`release.candidate` capability only for a complete exact manifest; run events pin its
SHA-256. Release flow is two-stage: preparation commits without tag, finalization
checks tracked version, exact commit/signature/evidence and creates a tag only after
explicit Windows/pack acknowledgements. Host-neutral `qualification:` 14/14 pass;
versioning 6/6, source inclusion 1/1, Web UI 5/5 and MockDemo Release compile pass.
PowerShell,
certificate store, Office/VSTO and real signed Windows evidence remain unverified.
Pre-A5 qualification chats require explicit reset because their events lack evidence
provenance. [Evidence](WQ_A5_BUILD_EVIDENCE.md).

Artifact Library target (2026-08-31, отдельный user-requested docs-only contract):
[canonical spec](../artifact-library.md) различает non-durable draft, committed exact
resource, immutable original/snapshot, versioned Plan/HTML/authored document и
derived artifact. После mandatory CAS/link/save application ставит monotonic full
projection в WebView очередь до первого model transport без ожидания UI ack; failure
после commit не откатывает resource. Зафиксированы pinned message revisions,
head/history UX, domain-owned edit/delete, append-only removal/GC, inert uploaded
HTML, bounded text/Markdown/image/PDF/audio viewers и context-on-demand через
действующий Resource Gateway. Master Phase 11 и R51 обновлены; runtime/UI/vendor не
менялись и Phase 11 не начата. `git diff --check` и 256 local Markdown links в восьми
затронутых документах — pass; build/runtime tests для docs-only изменения не запускались.

Host Fabric and Local Automation target (2026-08-31, отдельный user-requested
docs-only contract): [Host Fabric](../host-fabric.md) фиксирует одно окно для
нескольких Office processes через ephemeral endpoint registry, exact immutable run
target и owner-STA execution без cross-process COM/ROT fallback. `NativeHostCli`
остаётся in-process DLL; скрытый dedicated Excel launcher допустим только как signed
interactive profile, не как security isolation или обход corporate policy.
[Local Automation Agent](../local-automation-agent.md) разделён на workspace/session
ADR, read-only files, guarded mutations, Browser, typed process runner, optional raw
shell/terminal и desktop control. File mutations/processes требуют отдельного signed
isolated worker и deny-by-default grants. Master Phase 11 и R52/R53 обновлены; код,
tests и current WQ-A1 next step не менялись. `git diff --check` и 207 local links
в шести затронутых Markdown files — pass; runtime/build tests не запускались.

Skill Library target (2026-08-31, отдельный user-requested docs-only contract):
[canonical spec](../skills.md) отделяет installed trusted global/host package от
chat-owned artifact. Upload остаётся untrusted immutable resource до explicit
confirmed install; current exact capability read не меняется. Для custom skills
заданы package-level immutable core+references revisions, append-only history,
restore-as-new-head, tombstone, provenance/import/export, conflict guard и UI-only
links из tool results в Library. Built-ins immutable; selected Host Fabric endpoint
владеет host scope. Master Phase 11 и R54 обновлены; runtime/UI/store не менялись,
WQ-A2 остаётся следующим шагом. `git diff --check` и 233 local links в восьми
затронутых Markdown files — pass; runtime/build tests не запускались.

Phase 10D final architecture audit (2026-08-31): final inventory is 107 Core,
173 Office and 15 OfficeHosts C# files. Replaced source paths/aliases are absent;
all production sources are included by the old-style projects. Forty-nine literal
source references in canonical architecture instructions resolve. The only current
`LegacyToolDefinitionAdapter` call sites are `ToolPackSnapshotFactory` for
`Adapt`/`BindingFor` and `ConversationProtocolContext` for `PolicyFor`; resource files
have none. The audit corrected stale VBA adapter rows that still named completed
Phase 8 as a future removal gate: 5B2 owns document binding, while optional direct
handler/typed-host cleanup is Phase 11 and does not block stable core. Architecture
4/4 and production source inclusion 1/1 pass. Windows gates remain not performed.
[Evidence](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md).

Phase 10C2 resource projection cleanup (2026-08-31): four controller-facing
`common.resources_*` definitions now use `ControllerToolDefinition` and preserve the
exact native handler descriptor, JSON schema and source-owned `ToolPolicy` instance.
The only removed member is `LegacyToolDefinitionAdapter.ProjectRead`; its active
`Adapt`/`PolicyFor`/`BindingFor` consumers remain unchanged. A boundary check rejects
future resource-file dependencies on the legacy execution adapter. Native execution,
bindings, callable ToolPack, model wire and mode policy are unchanged. Focused resource
projection/manual+model 1/1, hard-cutover resource 1/1 and architecture 4/4 pass;
Windows WQ-PACK remains open. Next atomic step is only final audit 10D.
[Evidence](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md).

Phase 10 local Windows build entrypoint (2026-08-31): root `build-local.cmd`
находит штатный VS 2022 MSBuild через `vswhere`; без аргументов последовательно
собирает managed dependencies один раз, NativeHostCli x64/Win32 и публикует оба
portable каталога. `x64`, `x86`, `desktop`, `all`, `doctor` остаются явными
вариантами; configuration фиксирована в `Release`. Packaging перенесён в
declarative MSBuild tasks без PowerShell, install/sign/register/network/process
termination и без удаления destination. Три заменённых native publish `.ps1`
удалены; четыре прежних PowerShell wrapper `.cmd` больше не меняют execution
policy. x86 output явно предупреждает об отсутствии x64-only PDF native runtime.
На этой машине выполнены только XML/static/diff/version checks; Windows MSBuild/C++/CLI,
Office PIA, portable contents и запуск не проверялись и остаются Windows gate.
Следующий шаг был позднее выполнен в 10C2 resource projection cleanup.

Phase 10C1 application façade move (2026-08-31): byte-identical
`AssistantRuntime.cs` moved with `git mv` from `Office/Runtime` to root `Office`;
the production old-style include switched and a physical-owner assertion prevents
return to document/tool Runtime. Namespace, controller/pane lifecycle, disposal,
factories and all consumers are unchanged. Harness intentionally uses a controller
stub and had no façade source-link to rewrite. Architecture 4/4 and production source
inclusion 1/1 pass; real Office/VSTO/WebView lifetime remains a Windows gate. The
resource projection half was later completed in 10C2.
[Evidence](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md).

Phase 10B2 VBA host backend move (2026-08-31): both `VbaProjectSupport` partials
moved with `git mv` from `Office/Vba` to `OfficeHosts/Vba`; namespace, old-style
projects, three host adapters and two harness consumers switched. Old paths/includes,
aliases and Office consumers are absent. COM/VBE and guard algorithms are unchanged.
The move exposed a source-linked-harness blind spot: internal
`VbaPackageOwnershipMarker` was inaccessible across production assemblies. It is now
an explicit public read-only Office.Vba parser contract with private constructor;
no duplicate parser or broad friend assembly was added. Connected COM 47/47,
UserForm helper 1/1, exact package guard 1/1, architecture 4/4 and source inclusion
1/1 pass. R49 fixed host-neutral; Windows OfficeHosts/VSTO/VBE remains open. The
application-façade half was later completed in 10C1.
[Evidence](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md).

Phase 10B1 host document identity move (2026-08-31): `DocumentIdentity.cs` moved
with `git mv` from `Office/Runtime` to `OfficeHosts/Identity`; namespace, both
old-style projects, three host adapters and source-linked harness consumer switched.
The algorithm differs only by namespace. Old source/include, alias and Office
consumer are absent; the boundary-test now rejects any future Office dependency.
Documents/identity 4/4, architecture 4/4 and production source inclusion 1/1 pass.
OfficeHosts/VSTO/real Office were not validated on this machine; WQ0 and Windows
compile remain open. At that point R49 still covered the two `VbaProjectSupport`
partials; 10B2 later moved them and closed R49 host-neutral.
[Evidence](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md).

Phase 10A physical/dependency audit (2026-08-31): inventory 107 Core, 176 Office
и 12 OfficeHosts C# files. Folder/namespace mismatches 0/27/5 не трактуются как
автоматические defects: root Office namespace остаётся у façade/host ports. Реальный
R49 scope на момент аудита — `DocumentIdentity.cs` и два `VbaProjectSupport`
partials без Office service/tool/domain consumers; первый файл позднее перенесён в
10B1, а оба partials — в 10B2. `AssistantRuntime` и resource-only
`ProjectRead` зафиксированы отдельными 10C cleanup invariants; projection позднее
перенесена, а method удалён в 10C2.

Новый `architecture: mandatory dependency direction` проверяет Core.Agent,
ModelProtocol, VBA, resources, OfficeHosts и UI/bridge; вместе с существующими
architecture cases — 4/4 pass. Production old-style source inclusion — 1/1 pass.
Superseded `Core/Tools/AgentResponseParser.cs` удалён из canonical architecture map;
actual v4 parser указан в ModelProtocol. Runtime не менялся, Office/VSTO/WebView не
проверялись. `ValidateVersionFormat`, canonical source paths, diff check и 232 local
links в 10 changed Markdown files — pass. [Evidence](PHASE_10A_BOUNDARY_AUDIT.md).

Deferred Windows qualification mode (2026-08-29, docs-only decision): пользователь
разрешил не ждать регулярных Windows прогонов между dependency-safe подэтапами
обязательного маршрута. Каждый slice по-прежнему закрывает targeted host-neutral tests,
cleanup и отдельный commit; статус до реального прогона — только `done host-neutral`.
Накопленные COM/VSTO/WebView/live-provider scenarios сведены в
[Windows qualification runbook](WINDOWS_QUALIFICATION_RUNBOOK.md) и обязательный
Milestone WQ перед Phase 12. На дату решения WQ0 Excel identity probe считался
prerequisite production factory switch; это условие отменено принятым риском
2026-08-31, а сам WQ0 сохранён обязательным release evidence. Непроверенный build —
`16.1.0-dev` qualification candidate, не stable/beta/RC. 9C UI и Phase 6C–6G
mutation slices выполнены отдельными host-neutral changes; 6H зафиксировал scope,
6I package runtime/R41 и 6J rename/R42 выполнены host-neutral; 7A audit и 7B typed
Excel reads и verified `write_range` завершены host-neutral; 7D позже объединён с
accepted-risk atomic 11T0/5B2 без ожидания WQ0, а
Phase 8A immutable execution snapshot, 8B callable lifecycle/admission, 8C durable
reconstruction и 8D resource data-plane cutover завершены host-neutral; 9D1 audit,
9D2 same-process fail-stop reload/reconciliation, 9D3 typed event
classification/`IEventStore`, 9D4 minimal `IConversationStore` и 9D5 immutable
`RunViewState` завершены host-neutral; 10A audit, 10B1/10B2 host moves и 10C1 façade
move, resource projection 10C2 и final audit 10D также завершены; следующий этап —
Windows Milestone WQ. Текущий следующий runtime step уточнён в заголовке документа.
Windows WQ-UI/VBE/Excel не считаются
закрытыми локальными проверками.

Phase 9D5 immutable run view projection (2026-08-30): один Core
`RunViewStateProjector` строит immutable UI state из authoritative `KernelState` и
source-owned `ToolExecutionEvidence`. Narrative, lifecycle, health, successful
reads, verified change/no-change, unverified writes, failed calls, unknown effects
и exact pending confirmation больше не собираются в JS из model status/prose и
разрозненных Activity полей. Legacy successful mutation без verification остаётся
unverified+unknown; несовместимая evidence не может завысить verified count.

Application result, Init/ChatState/SendChat bridge, chat headers/catalog и Agent/
message/approval UI switched atomically. Full responses несут session revision;
per-chat UI guard отвергает late detail и не даёт stale catalog заменить более
новый summary, а existing event revision CAS остаётся cross-window write authority.
Все switched JS/CSS получили один cache key, поэтому WebView не смешивает новый
bridge с cached flat-status readers.
`RunExecutionSummary`, getter projection, message/run/bridge fields, current catalog
overlay и model-status UI branches удалены; старые JSON fields только игнорируются,
без hidden migration/fallback. Stream/CAS/schema/ports не менялись.

99 distinct targeted harness cases и 70/70 web cases pass; MockDemo actual-controller
compile — 0 errors / 3 existing CA1416 PDF warnings. `ValidateVersionFormat`, diff
check и 249 local links in 11 changed Markdown files — pass.
[Evidence](PHASE_9D5_RUN_VIEW_STATE.md).
Windows controller/WebView/reload/confirmation/live-append/clipboard/multi-window
qualification открыт; Phase 9 имеет статус только done host-neutral.

Phase 9D4 conversation projection boundary (2026-08-30): один
`ChatConversationStoreAdapter` реализует минимальный `IConversationStore` над тем же
`ChatStore`, который остаётся единственным владельцем hash-linked stream, revision
CAS и blobs. `ChatSessionService`, `ConversationRunService`, kernel store adapter и
controller create/load/save/list/active/move/delete paths switched together.
Interruption recovery выражен одной intent operation: storage закрывает прежнюю
open-step boundary и возвращает retained open-tool evidence, а application по-прежнему
владеет recovery policy и финальным save.

Artifact hydration, HTML revision activation, raw events/payloads, reducers и CAS
maintenance в port не вошли. Replaced public conversation methods internalized;
compatibility overload/fallback, второй store, writable snapshot, schema/replay
change и dual-write отсутствуют. 29 distinct targeted cases pass; MockDemo
actual-controller compile — 0 errors / 3 existing CA1416 PDF warnings;
`ValidateVersionFormat`, diff check и 216 local links в 10 changed Markdown files — pass.
[Evidence](PHASE_9D4_CONVERSATION_STORE.md). Windows controller/restart/multi-window
qualification открыт; 9D5 UI projection не начат.

Phase 9D3 typed event store (2026-08-30): closed `SessionEventKind` catalog теперь
классифицирует каждый current top-level chat event по Agent/Domain Diagnostic lane,
authority/diagnostic meaning, mandatory/best-effort durability и storage-internal/
event-port write scope. Один `ChatEventStoreAdapter` делегирует прежнему `ChatStore`;
JSONL/CAS/schema/type strings/correlation/lifecycle не менялись. `session.*`,
`turn.*`, `step.*` нельзя append-ить через port. Materialized request и accepted
ToolPack extension — mandatory authority; rejected attempts и current transport
terminal/chunk evidence — mandatory diagnostics; accepted trace marker и causal
observations — best effort. Accepted response/calls/results остаются canonical
`session.commit`.

Все active Office model trace, ToolPack, causal trace и controller diagnostics
writers/readers переключены вместе. Direct `AppendTrace*`, broad event reads и
writable arbitrary causal `Stage` в Office удалены; replaced raw `ChatStore` event
API internalized. Core storage/recovery и
`ITrajectoryQuery` сохранили свои владельцы. 24 distinct targeted cases pass;
MockDemo actual-controller compile — 0 errors / 3 existing CA1416 PDF warnings.
`ValidateVersionFormat`, diff check и 208 local links в 9 changed Markdown files —
pass.
[Evidence](PHASE_9D3_TYPED_EVENT_STORE.md). Windows controller/WebView persistence
qualification открыт; 9D4 conversation port и 9D5 UI projection не начаты.

Phase 9D2 same-process run-store recovery (2026-08-30): оба Agent controller path
теперь отдельно ловят `RunStoreException`, закрывают stale causal scope, освобождают
`ChatRunLease` и только затем вызывают targeted reload/reconciliation на общем
`ChatSessionService`. Сам service reload-ит exact stream, повторно берёт recovery
lease, под ним заменяет active cache canonical projection и не читает/не сохраняет
`UnpersistedSummary`. Durable confirmation до failed
claim остаётся pending; open possible dispatch становится unknown; saved terminal
остаётся known. Recovery CAS conflict не retry-ится, а повторный вызов idempotent.

Fault matrix start/confirmation × before/after dispatch — 4/4 pass; existing startup
unknown/saved-boundary recovery — 2/2; production source inclusion — 1/1, итого 7
distinct targeted cases. MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings; version format и 177 local links в 7 changed Markdown files —
pass. Старые escape-only catch filters удалены, второй recovery/store не добавлен.
[Evidence](PHASE_9D2_RUNSTORE_RECOVERY.md). Windows controller/WebView
qualification открыт.

Phase 9D1 persistence audit (2026-08-30): targeted source/call-site review подтвердил,
что один `*.events.jsonl` + CAS остаётся единственной chat authority, а действующий
`IRunStore` уже обеспечивает accepted/start-before-effect, terminal-before-next-step,
private invocation cursor/global CAS и no automatic retry. Existing harness покрывает
normal/error/unknown/pending/cancel replay, stale confirmation, result append failure
после write, restart recovery, CAS orphan/fail-closed GC и queued stream terminal
barrier. Эти проверки переиспользованы как evidence; runtime и tests не менялись.

Не закрыты три отдельные архитектурные границы: generic `ChatStore.AppendTrace(string)`
не типизирует authority/diagnostic и mandatory/best-effort; session/controller
consumers по-прежнему зависят от concrete broad `ChatStore`; UI не получает один
immutable `RunViewState`. Дополнительно R45 фиксирует safety/UX gap: при
`RunStoreException` controller правильно не пишет выдуманный terminal и освобождает
run lease, но current process не вызывает single-chat canonical reconciliation, так
что open dispatch может оставаться визуально running до restart. Ordered slices:
9D2 recovery, 9D3 typed event port, 9D4 conversation port, 9D5 RunViewState.
[Evidence](PHASE_9D1_PERSISTENCE_AUDIT.md). Windows gates не закрыты.

Phase 8A immutable ToolPack snapshot (2026-08-30): после окончательного run
filtering один typed `ToolPackSnapshot` копирует и проверяет registration каждого
runnable id. Registration revision включает canonical descriptor/schema, полный
`ToolPolicy`, handler/entry point/scope/host и hash package implementation; pack revision
дополнительно связывает mode, host и ordered membership. Native runtime регистрирует
эту authority напрямую, legacy `Describe` читает её же и перед dispatch повторно
сверяет compatibility definition. Drift возвращает `tool_registration_changed` без
effect; confirmation rebuild сравнивает новый revision с persisted accepted policy.

Старый ad-hoc fingerprint helper удалён. R22 закрыт актуальными counts Excel 15,
Word 9, PowerPoint 9, Outlook 5. Два stale fixtures приведены к уже действующим
`read_range.address` и status-free v4 без product change. 90 distinct host-neutral
cases pass; MockDemo: 0 errors / 3 existing CA1416 warnings; version format, diff и
194 local links в 10 changed Markdown files — pass. В самом 8A LRU, core-pack
selection, atomic admission, compaction/events и remaining resource handlers не
переключались; core/admission позднее закрыты 8B, остальные gates открыты. [Evidence](PHASE_8A_TOOL_PACK_SNAPSHOT.md),
[decision](../decisions/ADR-0006-tool-pack-snapshot.md).

Phase 8B callable ToolPack (2026-08-30): `ProgressiveToolWorkingSet`, `Touch` и
`TOOL_WORKING_SET` удалены одним cutover. `CallableToolPack` пересекает конечные
exact-ID profiles с уже отфильтрованным run catalog: Agent/Excel получает все 15
Excel и 5 public VBA schemas, Word/PowerPoint — доступный VBA core, Chat — четыре
read-only resource tools, Plan — bootstrap discovery/resources. Optional schemas
staged из complete current-revision/current-run evidence и публикуются всей пачкой
только в `EndResponse`, с новой callable revision. Полный prospective request
включает history/media, response schema, output/safety budget и bounded format-repair
reserve; overflow сохраняет прежний pack, не публикует ни одной новой schema и
возвращает видимый `TOOL_PACK_STATE`. Tool execution больше не меняет membership.

Prompt schema 15 заменяет eviction guidance; schema14 и остальные сохранённые custom
prompts не переписываются и требуют explicit review/reset. Capability read теперь
различает complete descriptor evidence и step-boundary admission. Dynamic registry,
execution snapshot 8A, Tool Result/ResourceRef wire, AgentKernel, compaction algorithm/events
и resource handlers не менялись; compaction notice только требует новый admission.
Durable extension event и rematerialization при confirmation/
compaction/crash остаются 8C; до них reconstruction требует нового exact read/admission,
а raw evidence не считается решением admission. Targeted
host-neutral verification: 92 distinct cases (tool pack 5, Agent 34, model protocol
15, settings 5, context inspector 3, Plan 2, Chat 13, conversation v4 13, typed
settings bridge 1, production includes 1) — pass. Harness compile: 0 errors / 4
existing CA1416 identity-probe warnings; MockDemo actual-controller compile: 0 errors /
3 existing CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 214 local links в 14 changed Markdown
files — pass. Product `16.1.0-dev`; tag/release/push не выполнялись. Полная
[evidence](PHASE_8B_CALLABLE_TOOL_PACK.md); Windows WQ-PACK открыт.

Phase 8C durable ToolPack reconstruction (2026-08-30): `ToolPackAdmissionJournal`
пишет `tool_pack.extension.accepted/rejected` в тот же hash-chained chat event stream
до изменения callable membership. Accepted v1 data pin-ит exact requested ID/revision
delta и before/after snapshot revisions; rejected record остаётся diagnostic-only.
Reconstruction проигрывает ordered accepted chain exact logical `TurnId`, поэтому переживает
новый runtime `RunId`, confirmation, compaction и restart, но не переносится в новый
user turn. Raw `TOOL_RESULT` никогда не становится authority. Exact descriptor/profile
drift/broken chain атомарно оставляет core до accepted core rebase и добавляет видимый `TOOL_PACK_RESTORE_STATE`, не скрывая
уже известный confirmed terminal result; новый exact read может создать свежий event.
Append failure не публикует pack и не отправляет следующий model request. Prompt schema
16 заменяет временное re-read-after-reconstruction указание; schema15/custom text не
переписываются без explicit review/reset. AgentKernel, execution snapshot, tool result/
ResourceRef wire, compaction algorithm и resource handlers не менялись. [Evidence](PHASE_8C_TOOL_PACK_EVENTS.md).
Targeted host-neutral verification: 50 distinct cases (Agent 34, ToolPack 6,
settings 5, canonical event log/HMAC/encrypted history/shared trace stream 4,
production includes 1) — pass. Harness compile: 0 errors / 4 existing CA1416
identity-probe warnings; MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 230 local links в 15 changed Markdown
files — pass. Product `16.1.0-dev`; tag/release/push не выполнялись.

Phase 8D resource data plane (2026-08-30): четыре exact
`common.resources_list/resolve/search/read` registrations теперь имеют source-owned
typed descriptors, read-only policies, bindings и handlers. Agent/Chat/manual paths
исполняют их через один `ToolRuntime` и существующий `ResourceGatewayService`; каждый
call создаёт `DocumentAccessGate` operation root, а live Office/VBA access через
`HostRuntime` сохраняет same-operation reentry и serialization с mutation. Старый `ResourceToolExecutor`,
его switch branch и source/test references удалены. Оставшийся
`ResourceToolCatalog` только проецирует definitions текущим mixed catalog consumers
и не исполняет вызовы.

Typed result сохраняет bounded JSON и exact `ResourceRef`. Media bytes передаются
только через request-local materialization keyed by runtime call ID, немедленно
consumed следующим model step и не становятся durable CAS/content_ref transport.
Native UI projection сохраняет provider `retryable` metadata. URI/revision/cursors,
CAS/providers, ToolPack admission/events и AgentKernel не менялись. R30 закрыт
host-neutral; [ADR-0004](../decisions/ADR-0004-resource-data-plane.md) принят.

74 distinct targeted cases pass: resources 8, Agent 34, Chat 13, ToolRuntime 14,
native resource replay 1, bounded VBA resource 1, VBA/document gate serialization 2,
production source inclusion 1. Harness compile: 0 errors / 4 existing CA1416
identity-probe warnings; MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 222 local links в 14 changed Markdown
files pass; product `16.1.0-dev`, release/tag/push не выполнялись. Windows
live-provider/manual/media gate остаётся WQ-PACK.
[Evidence](PHASE_8D_RESOURCE_DATA_PLANE.md).

Phase 7B typed Excel reads (2026-08-30): host-neutral `ExcelReadService`
владеет canonical inspect/range outcomes, profile и second-bound validation.
`NativeToolRuntimeAdapter` регистрирует exact `excel.inspect`/`excel.read_range`
только вместе с handlers; manual/model paths входят в `HostRuntime` с exact chat
expectation. `ExcelAdapter` больше не исполняет public read ids и принимает только
две internal compatibility команды до 7D. HTML bind/refresh вызывает тот же adapter
под уже открытым access. Collections ограничены 200 items/100 chart series;
100000-cell ceiling проверяется host backend до `Value2`/`Formula`; defined names
не читают `RefersToRange.Value2`. 26 distinct focused Excel/native/HTML/
HostRuntime regression cases проходят; real Excel COM, protected sheet и production
factory/identity остаются Windows gates. [Evidence](PHASE_7B_EXCEL_READ.md).

Phase 7C verified Excel write (2026-08-30): host-neutral `ExcelWriteService`
владеет scalar/formula/table normalization, exact target, before/read-back и
typed effect evidence. `excel.write_range` зарегистрирован exact native handler с
`ToolVerification.Tool`; Agent/manual используют один `HostRuntime` scope, а dry-run
не входит в handler. Совпавший before даёт `VerifiedNoChange` без dispatch;
совпавший read-back — `VerifiedChange`; отказ до host boundary — `error`, а throw,
cancellation, unreadable/divergent state после boundary — non-retryable `unknown`.

Ragged tables null-pad детерминированно; 100000-cell/dimension bounds проверяются до
COM matrix allocation/assignment. Public host case и четыре старых write helpers
удалены. Временные read/apply internal commands и current resolver явно остаются до
atomic 11T0/7D; прежний WQ0 prerequisite отменён решением 2026-08-31. 15 distinct
focused write/read/catalog/HTML/source checks pass;
MockDemo compile — 0 errors / 3 existing CA1416. Real Excel formula/value
normalization, mixed formulas, protected sheets, COM fault timing, close/switch и
production factories остаются WQ-EXCEL. Version/diff checks pass; 176 local links
in 8 changed Markdown files have 0 missing targets. [Evidence](PHASE_7C_EXCEL_WRITE.md).

Phase 9C causal run journal UI (2026-08-29): Diagnostics primary view теперь
показывает latest/exact run как один chronological `run-causal` поток; completed
Agent run и failed activity имеют direct action. Bounded summary считает unique
`ToolCallId`, typed failure/unknown/interruption statuses не теряются; filters,
expansion и scroll остаются UI state. Row details lazy-mount общий lossless JSON
viewer, exact source IDs видны, а raw range/CAS payload остаются у existing
Diagnostics owner. Компонент не читает bridge/CAS/network/storage, не вводит durable
index и не использует prose для effect. 12 web test files / 65 internal cases,
syntax/diff checks pass; unchanged Phase 9A 17 Core/bridge cases reused. Chrome
`file://` DOM probe: 12 rows, 1 problem, 2 lazy viewers, component network/data-driven
active elements/page overflow = 0; dark theme/clipboard/keyboard/DPI и real WebView2
не проверены. [Evidence](PHASE_9C_RUN_JOURNAL_UI.md). R28 и full Phase 9 persistence
matrix открыты; R37 read-only adapter сохраняется до Windows/reset decision.

Phase 9B4 compact diff vendor gate (2026-08-29): Diff2Html 3.4.56 не добавлен.
Единственные consumers — VBA editor preview и hydrated mutation detail — передают
exact `before/after` в `RNAssistantVbaDiff.format`; bridge DTO также содержит только
эти source texts/hashes. Текущий formatter сам вычисляет bounded single-change
projection и не создаёт authoritative unified diff. Diff2Html является parser/
renderer готового unified/git diff, поэтому его подключение потребовало бы второго
diff algorithm либо выдало бы синтетическую проекцию за source evidence. Existing
formatter и CSS не менялись, vendor manifest остаётся 38 files. Повторно оценивать
только после отдельного source-owned bounded unified-diff contract. [Evidence](R39_DIFF_VENDOR_GATE.md).
9C выполнен отдельным последующим UI commit; Windows VBA/WebView qualification остаётся открытой.

Phase 9B3 bounded tree vendor switch (2026-08-29): actual `file://` probe
официального Web Awesome Tree 3.12.0 ESM graph не зарегистрировал `wa-tree`; custom
bundle и C#/WebView virtual-host switch не вводились. Вместо этого pinned
Wunderbaum 0.14.1 UMD/CSS (zero npm dependencies) прошёл file-origin probe и
подключён через новый UI-only `TreeAdapter` к одному HTML workspace/artifact tree.
Adapter допускает только bounded local arrays (consumer 1,800 nodes/12 levels;
hard 2,500/16), stable typed keys, local icons и owner callbacks; URL/lazy,
edit/DnD/filter/grid/persistence API не опубликованы. Старый renderer этого consumer
и его dead CSS удалены; search/group/collapse/select/delete ownership сохранён.
Manifest расширен с 36 до 38 runtime files с exact version/git head/npm integrity,
bytes/SHA-256 и local MIT license. Targeted tree 4/4, vendor gate 5/5 и все
`tests/web/*.test.js` 58/58 pass. Local Chrome
`file://`: 19 rows, keyboard active descendant/ARIA/themes pass, malicious title
остаётся text, horizontal overflow и network calls 0. [Evidence](R38_TREE_VENDOR_SWITCH.md).
Windows WebView2 keyboard/focus/DPI/lifecycle gate открыт; workers не используются,
allowlist остаётся пустым и CSP сохраняет `worker-src 'none'`.

R36 web vendor gate (2026-08-29): добавлен exact allowlist для 36 существующих
runtime files: версии/git heads/npm integrity, bytes/SHA-256, local licenses и
transitive browser decisions. Закрыты реальные gaps: KaTeX CSS больше не ссылается
на отсутствующие 20 WOFF + 20 TTF и использует только 20 manifested WOFF2; Feather
Icons зафиксирован как source-only provenance существующих inline SVG без runtime
package. Main UI явно держит `connect-src 'none'`, `font-src 'self'` и при пустом
allowlist `worker-src 'none'`; локальные workers разрешены только после manifested
factory/cancel/terminate/CSP slice. Vendor gate 5/5 и existing web regression 49/49;
local Chromium загрузил 7
existing globals, remote/failed requests и page errors 0. [Evidence](R36_WEB_VENDOR_GATE.md).
Новый vendor/UI consumer не подключался; Windows WebView2 gate открыт.

Phase 9B2B4 Markdown JSON switch (2026-08-29): только закрытые top-level fenced
blocks с exact language `json` в завершённых persisted/Agent/diagnostic сообщениях
заменяются post-sanitize на общий viewer. Exact fenced body остаётся источником raw
copy и сохраняет CRLF, duplicate keys и numeric lexemes; DOM/source mismatch
fail-safe остаётся обычным code block, content sniffing нет. Viewer collapsed/lazy,
уничтожается при collapse и перед message re-render. Незакрытый fence и каждый live
stream delta не парсятся как JSON; обычные code blocks, prompt/skill/plan preview и
Markdown transport не менялись. Новый UI test 8/8; прежние 27 JSON adapter/consumer
cases переиспользованы при неизменных inputs, итого 35. Local Chrome: light
1000×820 и dark responsive 560×820, horizontal overflow 0, malicious JSON не создал
script/image nodes, remote requests 0; fixture/screenshots удалены. `node --check`,
local links, diff check и `ValidateVersionFormat` — pass. Windows WebView2/clipboard
и R28 live-provider gate открыты.

Phase 9B2B3 Artifact JSON switch (2026-08-29): artifact detail больше не
выполняет lossy `JSON.parse → JSON.stringify → pre`. Exact inline JSON и exact
`MetadataJson` передаются общему viewer; `InlineTruncated` из bounded bridge DTO
становится completeness=`preview`, поэтому обрезанный JSON остаётся точным raw
фрагментом с явной ошибкой позиции, без repair. JSON определяется по typed MIME
(`application/json`/`+json`) либо explicit `json`/`data` kind; text/plain не
угадывается по содержимому и остаётся inert `textContent`, при truncation получает
явную preview-метку. Re-render/selection switch вызывает registry unmount до удаления
DOM. Новый integration UI test 5/5 pass. Local Chromium: light 1100×820, dark
responsive 640×820, horizontal overflow 0; fixture/screenshots удалены. HTML
preview/editor, plan Markdown и artifact transport не менялись. JsonAdapter 7/7
повторно pass; прежние 15 consumer cases переиспользованы при неизменных inputs,
итого 27. `node --check`, 88 local links, diff check и `ValidateVersionFormat` —
pass. Windows gate открыт.

Phase 9B2B2 Context/Tools/VBA JSON switch (2026-08-29): exact materialized
request JSON в context inspector использует общий viewer и передаёт `RawTruncated`
как completeness=`preview`, без repair; закрытие inspector/details уничтожает tree DOM.
Context manager показывает явно сериализованную UI state projection и монтирует её
только при открытых manager/details: это не выдаётся за exact wire. Manual tool
run/package result передаётся как structured UI projection, loading/error остаются
inert text; editable schema/arguments не менялись. Исправлен прежний порядок
install/uninstall, где `renderEditor()` стирал только что показанный result: теперь
result монтируется после refresh. VBA info metadata использует тот же viewer,
VBA read/edit/dispatch не менялись. Новый integration UI test 6/6 pass; plain JSON
`pre`/copy paths четырёх surfaces и последний dead `.json-box` selector удалены.
Вместе с JsonAdapter/Agent/diagnostics — 22/22; completion/prompt/tools regression —
14/14. `node --check`, 88 local links, diff check и `ValidateVersionFormat` — pass.
Local Chromium fixture: light 1280×900 и dark responsive 720×1100, horizontal
overflow 0; fixture/screenshots удалены. Windows WebView2 layout/clipboard gate открыт.

Phase 9B2B1 Agent JSON switch (2026-08-29): раскрываемые tool arguments и result
data теперь лениво mount/unmount общий lossless viewer. Удалены второй generic
`JSON.parse → object/table/list`, pretty-copy/raw-pre path и его dead CSS; collapsed
activity не удерживает tree DOM. Chart artifact parsing, который скрыто зависел от
глобального `tryParseJson` из Agent renderer, получил собственный domain-local parser;
chart transport/edit semantics не менялись. Общий `copyTextResult` теперь возвращает
Promise с реальным clipboard/fallback outcome; прежний fire-and-forget `copyText`
сохранён для существующих non-viewer callers, diagnostics/Agent adapters получают
ошибку копирования через callback. Новый Agent integration test 4/4, diagnostics
5/5 и JsonAdapter 7/7 pass; chart invalid/non-chart owner cases включены. Старый
Agent renderer/classes и cross-owner parser references отсутствуют. Реальный
WebView2/message layout/clipboard остаётся Windows gate. Regression UI:
completion 8/8, prompt review 5/5, tools editor 1/1; `node --check`, 88 local
Markdown links, `git diff --check` и `ValidateVersionFormat` — pass.

Phase 9B2A diagnostics JSON switch (2026-08-29): `app-trajectory.js` больше не
парсит/пересериализует `DataJson` через локальный `prettyJson` и не складывает JSON
в plain `<pre>`. Raw/derived event data передаётся adapter как точный bounded string;
`DataTruncated` становится completeness=`preview`, поэтому обрезанный object остаётся
raw с ошибкой позиции. Source event ids/sequences, hashes, payload metadata и
ResourceRef показаны отдельным раскрываемым JSON, не дописываются внутрь payload.
JSON CAS payload использует тот же viewer; HTML/прочие MIME остаются безопасным
`textContent` viewer с exact copy и без ложной JSON error. Stale request guards
остались у diagnostics owner, refresh уничтожает mounted controllers.

Phase 9B2A verification: новый integration UI test 5/5 и JSON adapter 7/7 pass;
existing completion 8/8, prompt review 5/5 и tools editor 1/1 pass. `node --check`
для затронутых JS, `git diff --check`, local links и `ValidateVersionFormat` — pass.
Локальный Chromium fixture с zero-network CSP: light/dark при 1280×720 и responsive
720×800, toolbar overlap 0, clipped buttons 0, body horizontal overflow 0. Fixture
и HTTP server удалены после проверки. Это не Windows WebView2/clipboard qualification.

Phase 9B1 bounded/lossless JSON viewer (2026-08-29): добавлены локальные
`app-viewer-registry.js` и `app-json-viewer.js` с тематическим CSS, без vendor,
network, worker, storage или bridge access. Allowlisted registry принимает только
уже загруженный payload. Adapter сохраняет immutable raw text и token spans,
duplicate keys, порядок и числа вне JS safe integer; raw/node/path/decoded-string
copy разделены. Invalid/truncated JSON остаётся raw с точной позицией ошибки, без
repair. Parse/depth/node/pretty/raw/DOM limits нельзя расширить выше hard bounds;
children создаются страницами, cancellation проверяется при parse. Все данные
рендерятся через `textContent`, completeness/redaction остаются metadata владельца.
7 targeted JSON viewer cases, existing completion/prompt/tools UI suites 14 cases,
syntax 3 JS files, zero-network API scan, 107 local docs links,
`ValidateVersionFormat` и `git diff --check` — pass. Реальный WebView2, clipboard,
responsive/theme visual qualification открыты. Existing consumers пока не switched,
поэтому их старые `prettyJson`/`JSON.parse` paths ещё не удалены. R36 не блокирует
собственный adapter без vendor, но обязателен до первого vendor switch.

Phase 9A diagnostics truth/query (2026-08-29): добавлен хронологический
`run-causal` view поверх canonical `*.events.jsonl` и existing `ITrajectoryQuery`.
Он сохраняет exact source event ids/sequences, model attempt/origin/call/mutation/
journal ids, revision-pinned resources и показывает явный evidence gap только после
typed terminal boundary; отсутствие события не объявляется успехом или ошибкой.
Новых writes, durable index, execution/replay decisions и UI inference нет. R37:
`ChatStore` теперь классифицирует accepted call по runtime-owned
`AcceptedCallOrigin`, независимо от provider result role/native `ToolCalls`; узкий
read-only adapter корректно проецирует ранее ошибочно помеченные current-v4 события,
не переписывая историю. 9B/9C, direct navigation UI и Windows/WebView qualification
остаются отдельными gates.

Phase 9A verification: 17 targeted harness cases pass — trajectory raw/derived/
run-causal/export 4, actual causal traces 6, accepted-call role classification 1,
typed bridge 1, selectable result roles 1, v4 accepted-history forms 1, canonical
event log 1 и complete-HTML runtime IDs 2. Одна актуальная host-neutral сборка,
последующие filters с `--no-build`; 0 errors, 4 existing CA1416 warnings в
`ExcelIdentityProbe`. `ValidateVersionFormat`, 117 local docs links и
`git diff --check` — pass.
Windows x64 + Office + VS 2022 / real WebView не запускались.

Phase 9A early start (2026-08-29, explicit user decision, docs-only baseline
`8d53d91`): из-за отсутствия Windows открытые Phase 5B2/6 и Phases 7–8 не закрываются,
а приостанавливаются; разрешён только host-neutral R32 9A truth/query поверх уже
существующего event stream/`ITrajectoryQuery`. Этот switch не меняет runtime/UI и
не разрешает зависеть от незавершённого Phase 8 ToolPack. 9B и 9C остаются отдельными
commits после acceptance 9A. Diff/4 docs, 74 local links и ValidateVersionFormat —
pass; build/harness не запускались.

Cleanup/readiness review (2026-08-28, baseline `1ea3ce0`): удаление controller-owned capture, catalog guard-only scope и прежних monitor/depth helpers подтверждено targeted search; includes актуальны. Дополнительных мёртвых путей в контуре 5B2 не найдено; legacy/probe сохраняются по действующим consumers/removal gates. Это не аудит всего репозитория. Согласованный пользователем допуск 6A заменяет прежнее предложение; Phase 5 не закрывается и порядок остальных фаз не меняется.

R32 requirements (2026-08-28, docs-only поверх `b754443`): по замечанию пользователя зафиксированы [сквозной журнал запуска и общий JSON viewer](R32_DIAGNOSTICS_JSON_VIEWER.md), inventory read-only consumers и acceptance Phase 9A–9C. Vendor-first оценка компактных готовых компонентов добавлена; конкретный vendor не выбран/не подключён. Runtime/UI не менялись; итоги 4B и следующий Phase 5 сохранены. Docs diff/9 новых локальных ссылок и anchors — pass; build/tests не запускались. Реализация, targeted UI/query tests и Windows/WebView qualification открыты; R28/R29 live gates этим требованием не закрываются.

R35 security hotfix (2026-08-29, отдельно от Phase 9): existing `DOMPurify 3.1.6`,
который очищает результат `marked` перед HTML insertion, заменён точным upstream
`3.4.14`; версия 3.1.6 входит в affected range GHSA-v2wj-7wpq-c8vv. Зафиксированы
npm integrity, git head, vendored SHA-256 и обе license texts. Markdown adapter,
CSP и остальные vendors не менялись. Headless Chromium загрузил vendored bundle
с `file://`, подтвердил version 3.4.14 и удаление script/event handlers в двух
malicious inputs; `node --check`, diff/links и version format — перед commit.
Реальный WebView2 на Windows не проверен; текущая Phase 6 и следующий slice не меняются.

R32 vendor/UI evaluation (2026-08-29, docs-only после R35): проверены existing
vendors и предложенный shortlist по source/package metadata, фактические bundles
четырёх основных кандидатов измерены. [Решение](R32_VENDOR_UI_EVALUATION.md):
Web Awesome Tree допускается только как tree-navigation spike; Wunderbaum — резерв
для measured large treegrid; оба JSON-кандидата отклонены для authoritative payload,
поэтому 9B начинает с собственного bounded/lossless `JsonAdapter`. Monaco/PDF.js
требуют смены текущего `file://` hosting; pinned local Worker разрешён и не считается
сетью, но Monaco всё ещё не оправдан в R32. `xterm.js` не используется для structured
logs. `ViewerRegistry` закреплён как UI-only adapter boundary поверх
Tool Result v1/`ResourceRef`, не новый model transport. R36 фиксирует незакрытый
provenance/offline inventory остальных vendors. Runtime/UI не менялись, Phase 6 и
следующий шаг сохранены; diff/7 docs, 99 local links и ValidateVersionFormat — pass,
build/harness не запускались. Windows/WebView qualification и Phase 9 implementation
открыты.

R32 Worker clarification (2026-08-29, docs-only): offline больше не трактуется как
запрет Web Worker. Текущий `file://` origin действительно блокирует worker path
Monaco/PDF.js; target допускает только pinned same-origin worker через WebView2
virtual-host mapping, host allowlist/factory, CSP и bounded termination, при полном
zero-network gate. Monaco остаётся вне R32 из-за размера/дублирования CodeMirror;
PDF.js — условный отдельный viewer. Runtime/hosting/CSP не менялись; diff/6 docs,
94 local links и ValidateVersionFormat — pass, build/harness не запускались.

R29 (предыдущий commit `6a256f0`): model wire содержит только name/arguments, kernel выдаёт ID до accepted append/confirmation/dispatch; ToolCallId + immutable attempt/position origin сохраняются в том же stream без переписывания raw response. Tests покрывают long HTML, allocator failure, native pairing, repair correlation, confirmation/replay и ISO-preserving clone. [Evidence/ограничения/чистка](R29_RUNTIME_CALL_IDS.md); этот protocol switch завершён до Phase 4, product version остаётся 16.1.0-dev.

Architecture audit (2026-08-28, docs-only commit `1f65f5d`, baseline `15dea46`): уточнены ID ownership, batch/control boundaries, actual effect evidence, ResourceRef transport (R30), pinned/bounded ToolPack, host gate, raw/comparable hashes и durable barriers будущих Phases 4–9. Убраны stale v2/media указания в canonical docs. Решение Phase 8 о конечном immutable pack сохранено; действовавшие на том baseline v3/LRU/runtime не менялись этим docs commit, позднее v4 включён отдельным R29. Критерии привязаны к фазам в master/backlog; R28/R29 и Windows gates открыты. Diff/13 затронутых ссылок — OK; pre-commit `ValidateVersionFormat` — pass. Build/tests не запускались, новые runtime-инварианты не объявлены проверенными. Phase 4 оставалась отдельным следующим этапом.

Historical live report (2026-08-28, docs-only; R29 runtime correction теперь описан выше, R28 открыт): фото показывает duplicate-ID rejection; после repair пользователь получил неполный HTML. По прямому запросу зафиксирован отдельный [R29/P1 — model-owned call IDs](RISK_REGISTER.md#r29--runtime-должен-владеть-идентификаторами-вызовов): целевое исправление — выдача ID кодом до execution, с сохранением correlation/confirmation/replay. Это отдельная правка контракта Phase 2 и consumers Phase 3, не автоматический результат 3B2; действовавший тогда v3 позднее атомарно заменён R29/v4. Полный incident trace не предоставлен, возможная ошибка scope остаётся R26; streaming — R28. Задача и критерии закрытия добавлены в [backlog](BACKLOG.md). Фаза/текущий подэтап на момент записи не менялись; проверены diff/локальные ссылки, build/tests и Windows/Office validation не запускались.

Workflow update (2026-08-28, docs-only): §§14.3, 22–23 — обоснованный единый switch может затронуть более 10 файлов; проверки применяются по изменению, повторные прогоны без новой причины не нужны; отчёт краткий. Runtime и открытые gates не изменены. Docs diff/links — OK; pre-commit ValidateVersionFormat — pass; build/tests для этой правки не запускались.

Migration sequencing update (2026-08-28, docs-only): Phase 3 изолирует kernel от resource lifecycle и проверяет минимальный RunSummary replay через существующие events; Phases 8/9 меняют внешние реализации, не повторяют извлечение. Проверки новых границ — при switch, Phase 10 — общая сверка. Основной маршрут 0–10 → 12 → stable; Phase 11 отдельно. Scope VBA package lifecycle пока не сокращён: общий journal нужен rename. Это уточнение плана, не начало Phase 3 и не закрытие R11/Windows gates; текущий следующий шаг указан в заголовке. Docs diff/затронутые ссылки и pre-commit ValidateVersionFormat — OK; build/tests для этой правки не запускались.

Pipelines disabled (2026-08-28, отдельное согласованное сокращение scope): удалены executor/parser, `PipelineJson`, nested dependency/safety/document/fingerprint traversal, transcript children parsing и editor. Catalog/discovery, direct/manual/dry-run execution, authoring и storage writes закрыты; старые определения skipped без migration/replay и без автоматического удаления файлов. Совместимость не сохраняется; возврат только отдельным решением Phase 11 после stable core. Это не начало Phase 3/11 и не дополнительный gate текущей миграции.

Pipeline verification: `pipeline:` 3/3; `tools:` 22/23 (единственный failure — известный R22); `vba: package` 5/5; `vba: session execution` 1/1; `vba: code-only UserForm authoring skill` 1/1; `completion guard:` 5/5; `agent: bounds oversized tool result data` 1/1; `protocol context: batch safety uses local authority` 1/1; production project includes 1/1. Итого 40 pass + R22; одна актуальная host-neutral сборка, следующие filters с `--no-build`. `node tests/web/tools-editor.test.js` — pass, syntax 5 затронутых JS — OK. Windows x64 + Office + VS 2022 / controller/WebView validation не выполнялась и остаётся открытой. V3 зафиксирован отдельно в `dbb8ce1`; pipelines — `f35e85c`. Pre-commit ValidateVersionFormat — pass.

Source archive build fix (2026-08-28, отдельное исправление по запросу пользователя): убрана блокировка обычной сборки без `.git`, введён явный `source-archive`/`unknown` с предупреждением; отсутствующий SHA/branch/tree state не подменяется выдуманным commit или `clean`. Debug и Release не требуют ручного props-файла; supplied provenance сохраняется, malformed metadata и ошибки Git checkout остаются ошибками. Explicit release gates требуют известного происхождения и Git checkout. Старый unconditional archive error удалён, adapters не добавлены; [canonical versioning](../operations/VERSIONING.md), ADR-0007 и §13.5 master plan обновлены. `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "versioning"` — 6/6 pass (архив без Git, partial/explicit metadata, release rejection, прежние version/tag/assembly cases); одна host-neutral сборка; `ValidateVersionFormat` и `git diff --check` — pass. Windows x64 + Office + VS 2022 / VSTO validation не выполнялась. Фаза и следующий шаг в заголовке не изменены; чужие runtime-изменения не входят в этот fix.

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.

MockDemo compile fix (2026-08-28, по второму скриншоту пользователя): добавлен отсутствующий source-link `Core/ModelProtocol/*.cs`; demo SettingsService обновлён под текущий `Save(..., reviewAgentPrompts)` с сохранением старого prompt marker при unrelated save. После устранения ModelProtocol errors таргетированная сборка обнаружила CS1501 в старой demo-сигнатуре; после её обновления `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 CA1416 warnings в PDF rendering. `git diff --check` — pass; demo runtime/self-test и полный harness не запускались. Старый demo Save path заменён без alias; production runtime не менялся этим fix. CS0006 для production Office DLL со скриншота отдельно не квалифицированы: нужна сборка Windows x64 + Office + VS 2022; Office/VSTO здесь не запускались.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | `10e52bf` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | done (host-neutral) | 1A: `a24feb1`; 1B: `5df587b`; 1C: `40282c0` | 61 targeted harness + 8 UI pass; red→green 4 cases; ValidateVersionFormat pass; last full 320/321 (R22) | not performed | 1A/1B/1C done; production Windows qualification остаётся открытой |
| 2 | done (host-neutral) | 2A: `d911826`; 2B: `a51bdda`; 2C1: `5a6b550`; 2C2: `c9f8b07`; 2C3A: `330aa79`; 2C3B: `4bbb039`; 2C3C: `dbb8ce1` | 2C3C: 100 targeted cases; ValidateVersionFormat pass; подробности в evidence | not performed | 2C3C был v3; current v4 — отдельный R29 correction ниже; old-chat skip/reset и prompt review/reset проверены локально; Windows/live-provider gates открыты |
| 3 | done host-neutral | 3A: `f01c3f2`; 3B1: `c1628ce`; 3B2: `15dea46` | 130 unique targeted cases; MockDemo compile; [evidence](PHASE_3B2_KERNEL_CUTOVER.md) | not performed | Production kernel switch + minimal real-store replay; Phase 4 отдельно |
| 2/3 R29 | done host-neutral | `6a256f0` | 141 unique targeted cases; MockDemo compile; [evidence](R29_RUNTIME_CALL_IDS.md) | not performed | Runtime IDs + v4; no v3 fallback, product version unchanged |
| 4 | done host-neutral: 4A + 4B | 85cc3f4 (4A); b754443 (4B) | 4B: 127 distinct targeted pass; MockDemo 0 errors / 3 existing CA1416 | not performed | [ToolRuntime](PHASE_4A_TOOL_RUNTIME.md), [v1 wire/cleanup](PHASE_4B_TOOL_RESULT_V1.md); domain/Windows gates remain |
| 5 | 5A–5B2 plus production Excel binding done host-neutral in 11T0/7D | 3a6c2aa (5A); a1b3d80 (5B1); 1ea3ce0 (5B2); [11T0](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) | 11T0: Excel read 4/4; write 4/4; HostRuntime 10/10; architecture/source checks | not performed | Exact Excel workbook/session bound under explicit `RuntimeKey` lifetime assumption; WQ0/WQ-SESSION evidence open |
| 6 | 6A–6J done host-neutral | `e0360f3` (6A); `62010c8` (R33); `dde18cf` (6B); through `cd0bd61` (6G); [6H](PHASE_6H_VBA_PACKAGE_SCOPE.md); [6I](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md); [6J](PHASE_6J_VBA_RENAME.md) | 6I package + 6J rename fault matrices; full VBA regression in linked reports | deferred | Full VBA/Windows gate open; R41/R42 runtime fixed host-neutral |
| 7 | 7A–7D done host-neutral; 7D delivered by 11T0 | [7A](PHASE_7A_EXCEL_SCOPE.md); [7B](PHASE_7B_EXCEL_READ.md); [7C](PHASE_7C_EXCEL_WRITE.md); [7D/11T0](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) | 11T0 focused checks above | not performed | Typed reads/verified write and direct bound backend switched; WQ0/WQ-EXCEL evidence open |
| 8 | 8A–8D done host-neutral; WQ-PACK pending | [8A](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [8B](PHASE_8B_CALLABLE_TOOL_PACK.md), [8C](PHASE_8C_TOOL_PACK_EVENTS.md), [8D](PHASE_8D_RESOURCE_DATA_PLANE.md) | 8D: 74 distinct targeted pass; MockDemo compile | not performed | Execution/callable authority, durable reconstruction and four native resource handlers switched; WQ-PACK open |
| 9 | 9A–9D5 done host-neutral; Windows acceptance pending | through `9bbf088` | 9D5: 99 targeted harness, web 70/70, MockDemo compile | not performed | Diagnostics/viewer, typed persistence ports and immutable RunViewState switched; R37/WQ-UI open |
| 10 | done host-neutral: 10A–10D | [10A](PHASE_10A_BOUNDARY_AUDIT.md), [10B1](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md), [10B2](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md), [10C1](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md), [10C2](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md), [10D](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md) | 10D: architecture 4/4; source inclusion 1/1 | not performed | mandatory structural route complete; WQ-A complete, 11T migration active; R49 fixed host-neutral |
| 11A | done host-neutral: 11A1–11A2 | [11A1](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md), [11A2](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md) | 11A2: harness 9/9; web 10/10; MockDemo full self-test | not performed | Commit projection + exact Library heads/history/classes/labels; Plan/HTML/viewer slices and R51 Windows gates remain |
| 11B | done host-neutral: 11B1–11B3 | [11B1](PHASE_11B1_PLAN_REVISION_GUARD.md), [11B2](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md), [11B3](PHASE_11B3_PLAN_HISTORY_HANDOFF.md) | 11B3: web Plan 7/7; Artifact Library 3/3; JS syntax 4/4 | not performed | Complete exact Plan lineage/restore/removal/history/handoff contour; Windows WebView remains |
| 11C | done host-neutral: 11C1–11C3 | [11C1](PHASE_11C1_HTML_LINEAGE.md), [11C2](PHASE_11C2_HTML_IMPORT_PREVIEW.md), [11C3](PHASE_11C3_HTML_BINDING_EXPORT.md) | 11C3: harness 8/8; web 21/21; JS syntax | not performed | Unique lineage, inert exact import and one guarded exact binding/recovery/export checkpoint path switched; Windows WebView/Office remains |
| 11D | in progress: 11D1 done host-neutral | [11D1](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md) | harness 4/4; web 48/48; JS syntax | not performed | Exact bounded text/source + complete-only sanitized Markdown switched; image/PDF/audio and Windows WebView remain |
| WQ-A | A0–A5 done host-neutral | [contract](../qualification.md), [A3](WQ_A3_EXCEL_WQ0.md), [A4](WQ_A4_SUITE_CATALOG.md), [A5](WQ_A5_BUILD_EVIDENCE.md), [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md) | A5: qualification 14/14; versioning 6/6; source inclusion 1/1; web 5/5 | not performed | Exact-build admission implemented; production adapters/live suites, signed Windows evidence and Milestone WQ remain open |
| 11 | in progress; 11T0 done host-neutral, remaining existing-tool migration/final legacy cleanup mandatory | 11A–11D1 evidence above; [11T0](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) | targeted per substep | not performed | 11T1 find/replace next, then remaining families and final generic-dispatch/adapter removal before Phase 12; new optional product contours are not gates |
| 12 | pending | — | — | — | Release hardening / qualification |

## Phase 0 substeps

- Ветка создана от historical baseline; master plan скопирован без изменений.
- Созданы progress, risk register, backlog и migration map.
- Исходные незакоммиченные изменения runtime/tests/UI не относятся к Phase 0; не менять и не включать в commit.
- AGENTS/README: feature freeze, обязательный порядок фаз, per-commit bump/tag отменены.
- Product target однократно изменён `16.0.4 → 16.1.0-dev`; повторного повышения нет.
- Ordinary validation не сравнивает версию с HEAD; release checks отделены и не создают tags.
- Добавлены CHANGELOG, canonical operations docs, ADR-0007 и явный release script без push по умолчанию.
- Build metadata содержит product/SHA/UTC/branch/channel/clean-or-dirty; AssemblyVersion сохранена `16.0.4.0`.
- Старый validation target удалён без alias; runtime/protocol/tools/resources/VBA/UI/persistence не менялись этим этапом.
- В репозитории не создаётся новый tag; `v16.0.4` остаётся исторической точкой.

## Phase 0 verification

- Baseline: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"` — 2/2 pass до изменений versioning.
- После изменений та же команда — 7/7 pass; весь linked host-neutral source set скомпилирован.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Проверены повторные builds/commits без bump, invalid metadata, dirty/staged tree, release tag matching, dev rejection, changelog, local/remote tag collisions и SDK/old-style assembly metadata.
- Git fixtures создаются и удаляются только во временных каталогах; настоящий origin и его tags не изменяются.
- Полный набор runtime tests не запускался: выбран минимальный build/versioning filter, production behavior не менялся.
- PowerShell release script не запускался (`pwsh` отсутствует); Windows x64 + Office x64 + VS 2022 / VSTO / ClickOnce — not performed.

## Phase 1A substeps

- Baseline — `10e52bf`, clean working tree. Производственные файлы не изменены.
- Прослежены model status → ChatTurnResult / accepted message → LastRun → controller/bridge → storage/header → UI.
- Current-to-target map уточнена для ConversationRunService, OfficeToolExecutor, ToolDefinition, ProgressiveToolWorkingSet, VBA executors, Excel adapter, Resource Fabric, persistence и UI.
- Воспроизведены completed после write error, journal unknown и отсутствующего write; сохранён нормальный write ok + final.
- Проверены valid response на запросе 20, отказ после 20 invalid responses и отсутствие rejected content/reasoning/repair instructions в accepted history.
- R01 подтверждён host-neutral тестами; исправление не выполнено. Green characterization не является green safety gate.
- R20: текущий лимит допускает initial + 20 retries (21 request). Поведение не менялось; исправление семантики attempts отложено в Phase 2.
- Новые adapters, protocols, runtime health fields и causal trace не вводились.
- Version остаётся `16.1.0-dev`; tag не создаётся.

## Phase 1A verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent: explicit response status"` — baseline 1/1 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — 7/7 pass.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Полный harness не запускался; весь linked source скомпилирован таргетированным запуском.
- Controller/bridge/UI проверены чтением кода, без интеграционного запуска.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM — not performed.
- Подробные доказательства и границы: [PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md).

## Phase 1B substeps

- Baseline — `a24feb1`, clean working tree; одна тема: causal trace, без completion guard.
- Logical step создаётся до первого model request; repair/fallback сохраняют step и получают отдельные modelAttemptId.
- Request/rejected/accepted diagnostics связаны с transport RequestId; accepted trace связывает точные toolCallIds.
- Top-level executor отмечает start/completion без изменения validation, dispatch, результата или retry.
- Journalled VBA module/rename/package action получает prepared/dispatched/verified markers с существующим mutationId; journal и read-back не меняются.
- Run/turn/document ids сохраняются в async logging scope; confirmation различает execution run и JournalRunId.
- Controller добавляет run.started, legacy run.summary.created и marker построения send/confirmation DTO. Это не runtime health и не подтверждение отрисовки WebView.
- Все metadata markers идут в существующий stream, без новых payload bodies, storage/index или decision state; новые trace failures не меняют execution.
- Version остаётся `16.1.0-dev`; tags не создаются. Phase 1C и последующие фазы не начаты.

## Phase 1B verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` — 6/6 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation: resets stream and thinking between repairs"` — 1/1 pass после обновления expectation для accepted trace.
- Первый full harness — 319/321: старое streaming expectation исправлено; compact catalog failure воспроизведён отдельно на исходном `a24feb1` (expected 16, got 15), R22.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj` — 320/321 pass; единственный failure — R22, такой же на baseline. Полный harness не green; новые trace tests и все 7 characterization tests проходят.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. `git diff --check` и relative Markdown links — pass.
- Baseline failure проверен в отдельном detached worktree; после проверки он удалён. Tags/working files основной ветки им не изменялись.
- Actual controller исключён из harness и заменён stub: его wiring проверено только чтением кода. Scope/summary/projection tests проверяют writer, не реальный controller/bridge delivery.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробности и границы: [PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md).

## Phase 1C substeps

- Baseline — `5df587b`, clean working tree. Одна тема: completion guard и минимальная UI/bridge-проекция; 10 production files включая csproj.
- До production fix новые runtime-summary assertions дали 4 красных characterization cases; после fix — 7/7 green.
- RunSummaryBuilder считает actual ToolResults по effective safety metadata, включая local mutations и nested pipeline policy. Model text/status и forged summary не определяют health.
- `unknown > errors > clean`; pending не считается успешной записью; rejected attempts не создают tool errors; v2 lifecycle/status и retry limits не менялись.
- Confirmation сохраняет summary логического turn и считает подтверждённый вызов один раз. Следующий user turn сбрасывает counts, не переписывая предыдущие snapshots.
- Runtime evidence сохраняется в существующих typed run/message operations; clone/DTO/replay сохраняют её. Нового durable store/index/schema или history migration нет.
- UI показывает отдельное предупреждение перед текстом модели вне свёрнутого trace. No-write — обычный ответ без подтверждённых изменений; boundary без summary не наследует старый clean.
- Legacy mapping и ограничение уровня evidence описаны в MIGRATION_MAP/R23. Domain tools, COM/VBA, Resource Fabric и persistence algorithms не менялись.
- Phase 2 не начата. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 1C verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — red 3/7 → green 7/7.
- Filter `completion guard:` — 5/5; `agent:` — 41/41 (включая characterization); `causal trace:` — 6/6; `conversation:` — 4/4.
- `storage: turn lifecycle` — 1/1 (replay/clone/typed DTO/model isolation); `chat: uses only read-only resource loop` — 1/1; `plan mode:` — 2/2; `harness: production projects` — 1/1.
- `node tests/web/completion-guard.test.js` — 8/8; реальные JS projection/render functions, минимальный DOM, без browser/layout/Office validation.
- Всего 61 различных targeted harness cases + 8 Node cases. Полный harness повторно не запускался; known baseline R22 остаётся открытым, последний full результат — 320/321 в 1B.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Production controller исключён из harness: его wiring проверено только чтением. Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробные команды, red→green evidence и границы: [PHASE_1C_COMPLETION_GUARD.md](PHASE_1C_COMPLETION_GUARD.md).

## Phase 2A substeps

- Baseline — `40282c0`, clean working tree. Один model/conversation contour, 6 production files включая Core csproj.
- В Core введены IModelProtocol, ModelProtocolClient и typed response/failure boundary. Loop больше не вызывает endpoint, не парсит JSON и не считает raw attempts.
- Parse/repair/native refusal/prompt budget/fallback/accepted-rejected diagnostics физически удалены из старого loop; fixed repair builder удалён из AgentJsonProtocol. Aliases/dual execution нет.
- Каждая попытка использует один accepted prompt; rejected body/reasoning/repair не входят в accepted history. Media сохраняются до конца protocol step и освобождаются в finally (R24).
- Provider/network/timeout/cancellation отделены от protocol exhaustion; прежний controller exception path сохранён через nonserialized Failure.Cause adapter до Phase 3.
- One enabled explicit schema fallback остаётся run-local; saved settings не меняются. Progress projector и trace sink сохраняют прежние semantics, step/attempt/request correlation.
- V2, tool policies/dispatch/summary и legacy initial + 1–20 retries сохранены. R20 и fallback при endpoint rejection внутри repair — оставшаяся Phase 2B.
- ADR-0002 фиксирует boundary, временные contracts и границы проверки. V3/schema/adapter/canonical v3 doc — Phase 2C; Phase 3 не начата.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2A verification

- Baseline characterization — 7/7; после переноса — 7/7.
- `model protocol:` — 8/8; `agent:` — 41/41 (включая characterization и media lifetime); `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1.
- Всего 68 различных targeted harness cases. C# 7.3 linked source build pass; ValidateVersionFormat pass. Новый Core source включён в old-style csproj.
- Прежнее media expectation после extraction дало expected 0 / got 1; обновлённый тест подтверждает одинаковый materialized prompt на repair и release после logical step. Это намеренное изменение lifetime, не новый baseline red→green case.
- Full harness/Node UI повторно не запускались: изменён только model/conversation contour, нет изменений UI или domain/storage algorithms. Последний full — 320/321 в 1B, R22 открыт.
- Fake endpoint tests не являются live tLLM validation. Production controller — stub в harness; Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Точные команды, legacy paths и границы: [PHASE_2A_MODEL_PROTOCOL.md](PHASE_2A_MODEL_PROTOCOL.md).

## Phase 2B substeps

- Baseline — `d911826`, clean working tree. Один model retry contour: 4 Core production files + caption/tooltip в web/index.html. Loop, tools, Resource Fabric, VBA и persistence не менялись.
- ModelProtocolRetryBudget считает 1–20 total protocol responses, включая первую. Default 10 и значения/ключ настройки MaxAgentFormatRetries сохраняются; initial + N удалён без alias (R20).
- Timeout/Network/TransientServer получают до двух provider retries на весь logical step, с cancellable delays 1s/2s. Ошибки HTTP/auth/429, size и invalid provider envelope не повторяются; transport parser/classification не менялись.
- Explicit enabled schema fallback работает также во время repair, один раз независимо от других budgets; exact current prompt/options повторно используются. N+3 raw requests maximum (23), не N×3.
- Cancellation проверяется до dispatch, во время backoff, после completion и rejection; запоздалый ответ не принимается. Нет повторного исполнения tools или новых accepted/history events.
- Canonical docs, ADR-0002 и changelog обновлены. V2/Failure.Cause остаются; новых compatibility adapters, v3 или AgentKernel нет. Phase 2C/3 не начаты.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2B verification

- Baseline: model protocol — 8/8, characterization — 7/7. До production fix: новые assertions дали 2 failures в model protocol и 2 в characterization (limits 1/20/clamp и fallback during repair).
- После fix: `model protocol:` — 13/13; `agent:` — 41/41 (включая characterization), `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1; `settings: invalid numeric values` — 1/1. Всего 74 разных targeted harness cases.
- C# 7.3 linked source build и ValidateVersionFormat — pass. Provider delays в tests инъецированы; реального ожидания/endpoint requests нет. Full harness/Node UI не запускались; изменены только model retry policy и текст одной настройки. Последний full — 320/321 в 1B, R22 открыт.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed; production controller остаётся stub в harness. Live provider/timeout/media costs — R25/R24, qualification pending.
- Точные команды и ограничения: [PHASE_2B_RETRY_POLICY.md](PHASE_2B_RETRY_POLICY.md).

## Phase 2C1 substeps

- Baseline — `a51bdda`, clean working tree. Полный v3 switch требует более 10 production files; по §14.3 выделен introduce/read-adapt, без частичного переключения. Изменены только 5 новых Core files + old-style Core project include, tests и docs.
- ConversationResponse содержит только message и ordered calls, без Status. Canonical ToJson пишет только v3 root; parser не принимает v2 автоматически. CurrentVersion активного AgentResponseProtocol остаётся 2.
- Strict envelope/JSON, call shape, 32-call bound, exact callable names и original argument schemas проверяются до acceptance. Optional nulls удаляются, execution defaults не применяются. Date-shaped strings остаются strings; unsupported numeric normalization возвращает typed failure.
- Accepted-run IDs и batch-safe read-only IDs задаёт caller. Parser не резервирует IDs; rejected response не возвращает partial calls. Mutation/local/confirmation и external/unclassified calls — singleton; безопасные read-only batches сохраняют порядок. Runtime wiring этих inputs — Phase 2C2 (R26).
- Explicit ConversationResponseV2Adapter читает только identified historical v2 envelope, отбрасывает model status и не выдаёт execution authority. Owner/consumers/removal указаны ниже; current consumer — harness, не history runtime.
- Canonical v3 doc и ADR-0002 содержат cutover gates: saved prompts, complete accepted-run IDs/confirmation, effective safety, все формы history, v3-only accepted writes, removal live v2 parser/schema/DTO consumers. Phase 3 не начата.
- Active model/retry/prompt/schema/history, Office tools, resources, VBA, persistence и UI не изменены. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2C1 verification

- Baseline: `model protocol:` — 13/13; `agent:` — 41/41.
- Новый `conversation v3:` — 13/13. Дополнительный oversized integer выявил InvalidCastException; focused malformed-JSON case был red, после typed-failure fix — green. Envelope, adapter, schema/wire, run-ID и singleton matrices входят в эти 13 cases.
- Regression: `model protocol:` — 13/13; `agent:` — 41/41; `harness: production projects` — 1/1. Всего 68 разных targeted harness cases, C# 7.3 linked build. ValidateVersionFormat — pass.
- Full harness, Node/UI, Office builds и live endpoint не запускались: active runtime не менялся. Последний full — 320/321 в 1B, known baseline R22 остаётся открытым.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed. Harness использует controller stub и не доказывает runtime cutover или Windows qualification.
- Точные команды, changed files, legacy paths и ограничения: [PHASE_2C1_V3_CONTRACT.md](PHASE_2C1_V3_CONTRACT.md).

## Phase 2C2 — context adaptation and local cleanup

- Baseline `5a6b550`, исходно clean. Полный switch не укладывается в §14.3; этот adapt затрагивает 9 production files (включая project includes и удалённый adapter), tests/docs. Phase 3 не начата.
- Loop подаёт immutable `ModelProtocolCallContext`: accepted-only IDs всего logical turn и conservative batch-safe projection. Confirmation читает full history до compaction, сохраняет scope при смене RunId; incomplete history не выдаётся за пустой set. Live v2 client пока context не enforce.
- Current-v3 history reader поддерживает canonical JSON, single native call с canonical metadata и literal final text; не читает старые форматы и не меняет данные.
- Неиспользуемый v2 read adapter, legacy JSON branch, include и obsolete tests удалены. Current-v2 typed-ID helper нужен текущей confirmation; удалить при writer/version switch 2C3. Local-read registry + effective metadata — до typed ToolPolicy Phase 4; bookkeeping — до kernel Phase 3.
- `conversation v3:` 13/13, `protocol context:` 6/6, `model protocol:` 13/13, `agent:` 41/41, `conversation:` 4/4, `completion guard:` 5/5, `plan mode:` 2/2, Chat read-only 1/1, production includes 1/1: 86 разных targeted cases. Linked C# 7.3 build и ValidateVersionFormat — pass.
- Runtime switch, saved prompts/probes, old-chat skip/reset, live provider и Windows x64 + Office x64 + VS 2022 не проверены/не выполнены. Harness моделирует controller identity transition; production controller остаётся stub. Full harness/UI/VSTO builds не запускались; baseline R22 остаётся открытым.
- Параллельные правки шести governance files включены с явного разрешения пользователя. Исходные docs-only проверки от 2026-08-28: cleanup policy — diff и 5 links/anchors OK; refactoring policy — diff и 7 links/anchors OK, без builds/runtime tests. Правила теперь canonical в master plan §§7.1, 15.1–15.2.
- Повторная чистка по §15.1: consumers/includes проверены, устаревшая рекомендация добавить v2 read adapter убрана из master plan §21, вводная PROGRESS сокращена; исторические evidence/ADR сохранены. Дополнительных мёртвых production paths в текущем контуре не найдено; live v2 callers нужны до 2C3. Ранее проверенный код и version/tag не менялись. Команды и границы: [PHASE_2C2_PROTOCOL_CONTEXT.md](PHASE_2C2_PROTOCOL_CONTEXT.md).

## Phase 2C3A — shared active wire owner

- Baseline `c9f8b07`, clean. По §§14.3/15.2 выделена подготовка coordinated switch: 7 production files; новый ModelProtocolWire — постоянный владелец schema/validation/JSON writing, без второго runtime/version selector.
- Runtime и compatibility probes используют общий contract; Office добавляет только reasoning/cache/trace options и native/history metadata. Дубли AgentOptions, ручного probe-call history и JSON call writer удалены. Prompt-authoring skill отсылает к действующим defaults вместо копии v2 status rules.
- Probes остаются fixed sentinel checks по одной raw попытке, без repair/fallback. Оба formats и все три tool-result roles проверены; матрица wrong status/casing/sentinel не даёт ложной qualification. V2 runtime, native refusal и response/prompt versions сохранены.
- Проверки: compatibility 2/2, model protocol 13/13, agent 41/41, protocol context 6/6, conversation 4/4, completion guard 5/5, plan 2/2, Chat read-only 1/1, project includes 1/1, existing prompt-reset characterization 1/1 — 76 разных targeted cases. Linked C# 7.3 build; ValidateVersionFormat pass. Full harness/UI/VSTO/live provider не запускались; Windows/controller/WebView не проверены.
- R27 подтверждён существующим тестом, но не исправлен: prompt schema mismatch автоматически заменяет custom prompts. Не повышать prompt version до explicit review/reset handling и его tests в 2C3B. Product остаётся 16.1.0-dev; tag/push/release script не выполнялись. Подробности: [PHASE_2C3A_WIRE_OWNER.md](PHASE_2C3A_WIRE_OWNER.md).

## Phase 2C3B — explicit prompt schema review

- Baseline `330aa79`, clean. Закрыт prerequisite R27 перед v3 switch: 10 production files, без изменения wire/prompts versions, Office tools, Resource Fabric, VBA или event-storage protocol.
- NormalizeAgentPrompts сохраняет authored text и missing/old/future marker; только blank fields получают defaults. SettingsService сохраняет clone; ordinary save не подтверждает stored mismatched marker, явный request-local review подтверждает его без перезаписи custom text.
- В typed saveSettings добавлен reviewAgentPrompts. Library → Prompts → «Подтвердить проверку» требует user confirmation; existing reset очищает drafts до save. PlanSystemPrompt больше не теряется из UI payload, отсутствие prompt editor не очищает сохранённые тексты. Обычные/tool/diagnostic saves не дают approval.
- Core guard вызван до controller preparation/attachment analysis/compaction и до изменения pending confirmation; neutral loop защищает direct entry/continuation. Production controller wiring проверен чтением, не execution на этой машине.
- Проверки: settings 4/4, typed settings bridge 1/1, prompt save 1/1, protocol context 6/6, confirmation success/failure по 1/1, Plan 2/2, Chat read-only 1/1, project includes 1/1, conversation streaming 4/4 — 22 targeted cases; Node prompt review 5/5. Reset characterization заменён red→green preservation test (раньше marker 0 автоматически становился 11).
- Реальный SettingsService теперь включён в linked C# 7.3 harness. Test-only ProtectedSecretStore поддерживает только отсутствующие fixture secrets и бросает ошибку при secret-file read/write; DPAPI не эмулируется. Windows x64 + Office + VS 2022, production controllers/WebView, DPAPI/live provider и full harness не проверялись.
- Чистка: удалены destructive mismatch branch, duplicate Chat/Plan defaulting и obsolete hard-reset test; устранены UI marker 0→1 и blank fallback при отсутствии editor. Нового production adapter нет. Product остаётся 16.1.0-dev; tags/push/release script не выполнялись. Подробности: [PHASE_2C3B_PROMPT_REVIEW.md](PHASE_2C3B_PROMPT_REVIEW.md).

## Phase 2C3C — v3 switch/delete

- Shared wire, typed result, repair, mode defaults и accepted-history marker переключены вместе: v3 содержит только `message + tool_calls`, prompt schema 12. Native refusal — отдельный outcome; model-loop end не является proof of effect.
- Полный history/context preflight стоит до подготовки, ручной compaction и подтверждения. Run-wide IDs и singleton safety enforce на каждом response; rejected batch не резервирует IDs и не исполняется частично. Saved prompts schema 11 сохраняются до explicit review/reset.
- Удалены live v2 DTO/parser/schema/includes, typed-ID helper, LastRun-only controller helper и 9 obsolete parser tests; fixtures используют настоящие v3 writers. Один связанный scope: 15 production files, без нового kernel/tool policy/storage/UI.
- Проверка и границы: [PHASE_2C3C_V3_CUTOVER.md](PHASE_2C3C_V3_CUTOVER.md). Windows/controller/Office/WebView/DPAPI и live provider не проверены; Phase 3 остаётся отдельной. Product `16.1.0-dev`, нового tag нет.

## Phase 3A — Office model context boundary

- Baseline `f35e85c`, исходно clean; pipelines уже отключены отдельным commit. По §15.2 извлечение нужно для ближайшего Core AgentKernel: loop больше не требует prompt/compaction/media и working-set implementation. Это один контур, 5 production files включая old-style project include; новый kernel или resource/store protocol не вводятся.
- Постоянный Office-владелец `ConversationModelSession` хранит model messages, request cache/options, read evidence/LRU и bounded result/media lifecycle, используя существующие services. Start/confirmation и prompt inspector переключены; прежние BuildMessages/BuildRequestOptions/materialization/media helpers и DTO из loop удалены без alias. Activity/resource/chart/checkpoint projection перенесена в существующий `AgentTranscript`; callback-связей обратно в loop нет.
- Сохранены v3, preflight, policies, execution/confirmation budgets, accepted IDs, summary и порядок result/history/projection. Failure.Cause, legacy summary и controller path остаются до 3B/4; владельцы/removal gates — в migration map. Phase 3 целиком не закрыта.
- Проверка: baseline `agent:` 32/32 и `protocol context:` 6/6. На изолированном составе Phase 3A (baseline `f35e85c` + 14 файлов; параллельные versioning/demo правки исключены) `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` — 33/33, включая новый auto-compaction/rebuilt callable-set case; существующий oversized-result/chart fixture проверяет перенесённую activity/provenance projection. После той же актуальной сборки `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `protocol context:` 6/6; `preflight` 3/3; `conversation:` 4/4; `context inspector:` 3/3; `causal trace:` 6/6; `completion guard:` 5/5; `plan mode:` 2/2; `chat: uses only read-only resource loop` 1/1; `harness: production projects` 1/1. Всего 64 разных cases pass, C# 7.3 source-linked build pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff/затронутые ссылки — OK. Full harness/JS не запускались: нет новых domain/storage/UI semantics; known R22 не перепроверялся.
- Windows x64 + Office x64 + VS 2022 / VSTO, production controller и real WebView/DPAPI/live providers не проверялись. IRunStore/новый RunSummary replay не реализованы и не считаются проверенными. Product `16.1.0-dev` и tags сохраняются; release script/push не выполняются.

## Phase 3B1 — Pure kernel introduction

- Baseline `68aadc2`, clean; чужие archive/versioning и MockDemo commits сохранены. По §14.3 полный switch разделён: model materialization, два execution path, storage и UI projection ещё требуют связанного wiring. Здесь вводится kernel contract, без production selection, feature flag или второго active loop. 13 production files: 8 новых kernel/contracts, 3 минимальных typed-caller renames, Core и MockDemo project includes; harness/includes/docs относятся к тому же контракту.
- `AgentKernel` знает только generic accepted messages/calls, typed execution records, summary и три ports. Normal/confirmation используют общий учёт; IDs и budgets принадлежат logical turn. Health вычисляется из execution evidence независимо от narrative; ambiguous write остаётся unknown, pending не считается outcome, retry tools не добавлен. Append/CAS failures останавливают работу без выдуманного durable terminal; synthetic result messages сохраняют typed evidence.
- Старый materialized `IModelProtocol.GetResponseAsync` переименован в `IMaterializedModelProtocol` во всех текущих typed callers, без alias или изменения v3/retry. Новый `IModelProtocol.SendAsync` пока имеет только fake implementation. ConversationRunService, legacy summary, Failure.Cause и projections не удалены: они обслуживают production до 3B2; owners/removal gates уточнены в migration map. ADR-0001/0008 и canonical state-model docs добавлены. Tool Result v1, домены, resources, VBA, UI и persistence algorithms не менялись.
- Проверка на изолированном составе этого изменения: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "kernel:"` — 41/41, включая cancellation во время policy recheck. На предыдущей сборке того же изменения `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `model protocol:` 15/15; `protocol context:` 6/6; `harness: production projects` 1/1. Эти 22 regression cases повторно использованы после локального исправления cancellation: active materialized sources/tests и project includes не менялись. Всего 63 разных cases pass. `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 прежних CA1416 warnings в PDF rendering. C# 7.3 source-linked compilation проверена; demo runtime/self-test, full harness и JS не запускались, R22 не перепроверялся. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Fake append/CAS log не доказывает existing-event replay, crash recovery или controller delivery. R11 остаётся открыт; actual IRunStore adapter и validated continuation restore — 3B2. Windows x64 + Office x64 + VS 2022 / VSTO, production controller, WebView/DPAPI/live providers не проверялись. Product `16.1.0-dev`, tags и release workflow не меняются.

## Phase 3B2 — Kernel production cutover

- `ConversationRunService` и controller confirmation используют единый Core kernel. Office model/tool/store ports сохраняют preflight, fingerprint, lease и model-context boundaries; старые loop, `ContinueAfterToolAsync`, `RunSummaryBuilder`, mutable ID bookkeeping и `Failure.Cause` удалены.
- `KernelState` сохраняется через existing `run.updated`, включая pending/in-flight evidence; flat run summary — только getter/projection. Real-store replay, stale confirmation, cancellation и interrupted/materialization boundaries проверены. Контракты — в canonical docs, точная matrix/команды и reused results — в [PHASE_3B2_KERNEL_CUTOVER.md](PHASE_3B2_KERNEL_CUTOVER.md).
- R11 contained только в минимальном контуре Phase 3; полный storage/UI и Windows/Office gates остаются. Domain tools, VBA, Resource Fabric, UI JS и version/release workflow не менялись. Development target `16.1.0-dev` не повышался, tag/push не выполнялись.

## Phase 5A — HostRuntime access boundary

- `Runtime/HostRuntime` стал владельцем текущих expected-document scope, file locks/monitor fallback, live-read depth и leases. Executor передаёт только Host/DocumentKey/RuntimeDocumentKey, access flags и синхронную operation; catalog, safety, tool/resource error mapping и domain preparation остаются у callers. Нет нового partial или второго executor.
- Consumers: обычный/ручной dispatch, VBA install/remove/run/editor, live Office/VBA resources и HTML data access. Старые executor-owned helpers/fields и `System.IO` dependency удалены; Office, Harness и MockDemo source includes обновлены. [ADR-0005](../decisions/ADR-0005-bound-document-session.md) фиксирует текущую границу и следующий switch.
- Не исправляются попутно: stable-key gate, global fallback, nesting без проверки target, stable-key OR runtime-key matching, preparation до gate и Excel ActiveWorkbook/descriptor lookup. R04/Windows остаются открыты; owners/consumers/removal gates — в MIGRATION_MAP. Kernel, v4/v1 wire, storage, UI, Excel/VBA domain algorithms не менялись.

Verification (2026-08-28): один build через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`, затем тот же command с `--no-build` для остальных filters. **16 distinct tests pass**, C# 7.3 / host-neutral .NET 8:

| Filter | Passed |
|---|---|
| `host runtime:` | 2 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: reconciliation waits for active mutation` | 1 |
| `vba: confirmed mutation rejects stale snapshot` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

Diff/16 добавленных или изменённых локальных ссылок и anchors — pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.

Новые boundary tests проверяют cancellation до/после action, отсутствие bypass у другого runtime, nested read и release после exception. Existing integration checks используют fake Office; они не доказывают реальную COM identity или новый bound contract. Production controller/real WebView, Windows x64 + Office + VS 2022 не проверялись; full harness и MockDemo build не запускались. Next — 5B, без Phase 6/9 switch; product version/tag workflow не менялся.

## Phase 5B1 — document access gate

HostRuntime берёт document gate до guard/preparation и удерживает до read-back/существующего journal terminal. Manual/resource/editor/HTML reads используют ту же границу; native list получает отдельный operation root. Reentry разрешён только той же синхронной operation и target, explicit STA transfer не передаёт право child tasks или новому UI/tool root. Owner STA возвращает busy без ожидания; cancellation повторно проверяется перед action на owner. Отмена и gate/guard exception после начала mutation сохраняют uncertain/nonretryable, а возвращённое domain evidence не переинтерпретируется.

Введён IOfficeDocumentSession: runtime/host/gate/dispatcher — cached metadata, stable identity/object/liveness проверяются на STA; wrappers держат одну session на lifetime. HostRuntime поддерживает этот port и строгий runtime match, но production Excel providers пока отсутствуют. Global monitor/per-instance AsyncLocal depth удалены; legacy stable-key/OR identity, actual workbook lookup и прямые context/catalog consumers остаются с owner/removal gate 5B2 в [MIGRATION_MAP](MIGRATION_MAP.md). Domain algorithms, kernel, v4/v1 wire, persistence и UI не переключались.

Verification (2026-08-28): **26 distinct tests pass**, C# 7.3 / host-neutral .NET 8. Final production sources скомпилированы через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`; следующие filters — `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`.

| Filter | Pass |
|---|---:|
| `host runtime:` | 7 |
| `vba: queued guard` | 1 |
| `waits for active mutation` | 5 |
| `vba: confirmed mutation` | 1 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

После исправления только native-list fixture (`kind: vba-component`, чтобы проверять реальный live module-list backend, а не project metadata) выполнен новый build для этого filter. 23 успешных предыдущих cases переиспользованы при неизменных относящихся production/test sources, dependencies и environment; package/includes выполнены затем с `--no-build`. Ранее исправлена новая manual-read fixture (`address`, не `range`); production schemas не ослаблялись. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff и 8 затронутых local links/anchors — pass. Версия остаётся `16.1.0-dev`, release script/tag/push не выполнялись.

Реальная COM identity/STA reentrancy, desktop/VSTO/native factories, active window/close/reopen/Save As и несколько клиентов требуют Windows x64 + Office + VS 2022. Здесь не запускались Office/VSTO validation, full harness или MockDemo build. Phase 5 целиком и R04 не закрыты; следующий шаг 5B2, без Phase 6/9.

## Phase 5B2 — direct context/catalog reads

Закрыт host-neutral read switch внутри 5B2. Конкретный блокер полного switch — предварительная Windows qualification общей runtime lifetime identity до переключения Excel factories (ADR-0005); production identity/binding не вводились. `HostRuntime.ReadDocument` использует существующий gate/guard/STA path отдельным operation root. `OfficeContextCaptureService` убирает прямой capture из controller, держит prepare/capture вместе и возвращает результат до persistence; VBA catalog держит cache identity/list/components под тем же gate. Busy/closed access не кэшируется как пустой catalog. Review выявил и исправил второй путь: failed/null backend result или exception при module list/component read теперь прерывает всю загрузку без публикации пустого/частичного cache и без внутреннего retry. Следующее независимое чтение может загрузить catalog заново; успешный пустой список по-прежнему кэшируется. UI context остаётся best-effort, selection guard/access failure не проглатывается.

Локальная чистка: удалены controller-owned capture implementation и catalog guard-only scope; общая guarded execution переиспользована без второго gate. Новый service включён в old-style Office project. Kernel, protocol, storage, UI и Excel/VBA algorithms не менялись. Consumers/removal gates обновлены в MIGRATION_MAP; legacy stable-key/OR identity и ActiveWorkbook/descriptor lookup остаются до production switch и Windows tests.

Verification после review (2026-08-28): **12/12 свежих targeted cases pass**, C# 7.3 / host-neutral .NET 8. Один build: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"` — 10/10. Расширен существующий catalog case: list/component failure через failed result, gate exception и generic exception; отсутствие cache/internal retry, последующая независимая загрузка и cache успешного пустого списка. Затем `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `vba: document tools discovered` — 1/1, `tools: catalog merges visible tools` — 1/1.

Из первоначального 5B2 read-switch run переиспользованы ещё **9 pass**: относящиеся production/test methods, dependencies, build settings и environment не менялись review-fix; повторного запуска этих filters не было.

| Reused filter | Pass |
|---|---:|
| `harness: production projects include all source files` | 1 |
| `vba: queued guard` | 1 |
| `vba: confirmed mutation` | 1 |
| `waits for active mutation` | 5 |
| `desktop com: adapter dispatches calls` | 1 |

Harness использует controller bridge stub; его tests проверяют production capture service, не controller wiring. Поэтому дополнительно выполнен `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --nologo -v:minimal`: **pass, 0 errors / 3 существующих CA1416 PDF warnings**; actual controller sources компилируются. Demo runtime/self-test и Windows controller/WebView поведение этим не проверены.

Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. Diff и 11 затронутых local links/anchors — pass. Product version остаётся `16.1.0-dev`; release script/tag/push не выполнялись.

Windows x64 + Office + VS 2022 обязательны для controller/WebView, COM identity/STA reentrancy и всех factory/lifetime сценариев. Office/VSTO validation и full harness не запускались. Phase 5/R04 не закрыты; следующий шаг и обязательные документы — в заголовке.

## Phase 5B2 — identity qualification probe

Подготовлен отдельный `tests/RNAssistant.ExcelIdentityProbe` (net48/x64, C# 7.3), не подключённый к production/solution. Кандидат — OXID/OID из стандартного IUnknown OBJREF плюс scope Excel process/start time; original marshal reference удерживается до STA dispose. Неизвестный format, неполный packet и пустая identity отвергаются без fallback. Native-OM driver выбирает explicit HWND/workbook index один раз; последующие snapshots не перепривязывают закрытую книгу. Данные книги не меняются, raw marshal packets не экспортируются.

Проверены primary Microsoft specifications; выбор остаётся кандидатом до реальных proxy/lifetime наблюдений. [Probe README](../../tests/RNAssistant.ExcelIdentityProbe/README.md) содержит Windows команды, реальные desktop/VSTO/native call sites, acceptance observations и ownership/removal gate. Это инструмент для конкретного блокера ближайшего factory switch, не новый runtime adapter. Production `RuntimeKey`, ExcelAdapter/factories и ActiveWorkbook fallback не изменены; cleanup кандидата — при его принятии/отклонении в 5B2.

Исходная verification (2026-08-28): `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "excel identity probe:"` — **3/3 pass**: unsigned LE/object-vs-interface identity, malformed/unsupported/bounded packets, non-Windows refusal до native access. Этот результат переиспользован при review: probe sources/tests, dependencies, build settings и environment неизменны. Свежий read-switch build также скомпилировал probe C# sources; 4 ожидаемых CA1416 warnings у guarded Windows COM calls. Итого для 5B2 **24 distinct cases: 12 свежих + 12 reused**, без повторного полного прогона. Это не net48/PowerShell/Office validation.

Probe project XML/explicit includes/whitespace проверены при исходной подготовке; sources/project не менялись при review. В README уточнено: запись `released` подтверждает только успешный return из Dispose, а полное освобождение ссылок/lifetime требует отдельных Windows наблюдений.

Windows net48 build, PowerShell driver, COM marshal/cleanup, реальная identity и full controller/Office matrix **не запускались**; PowerShell здесь отсутствует. R04/Phase 5 остаются открытыми. Next 5B2 gate — результаты Windows qualification; без них factories не переключать. Последующий явный допуск ограниченного 6A описан ниже; Phase 9 не начата.

## Phase 6A — pure VBA text extraction

2026-08-28, baseline `1ea3ce0`; пользователь разрешил этот локальный подэтап, пока Windows недоступна. Phase 5/R04 и полный Phase 6 gate не закрываются.

`Core.Tools.VbaPatchEngine` выполняет одну текстовую замену и возвращает typed status/text/match count; `VbaTextCanonicalizer` владеет прежними live/package/VBE-comparable правилами. Core выбран из-за действующих parser/storage consumers: размещение в Office создало бы обратную зависимость. Manifest parser, storage, patch/guard/read-back/package/catalog и fake consumers переключены. JSON/result mapping и ordered orchestration остаются у Office; COM, journal/CAS protocol, outcome classification не менялись. [Представления текста](../vba-mutation-journal.md#text-representations) описаны отдельно от raw CAS bytes.

Чистка: прежние normalization/hash methods из manifest parser, newline/count/replacement helpers из Office и неиспользуемый `System.Text` import удалены; aliases и второй text engine не оставлены. Новые `.cs` включены в production `.csproj`. Действующий Office mapping и оставшийся domain orchestration имеют consumers/removal gates в [migration map](MIGRATION_MAP.md); packages/journal не удалялись, поскольку используются, включая rename.

Проверено на текущих sources:

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba:"` — **57/57 pass**: pure patch, exact edit/guard/confirmation, hashes, fake VBE read-back, restore/journal/CAS/recovery, package/ToolStore/catalog. Добавлен один pure-text contract test; существующий hash test расширен для literal backslashes, строк и апострофных комментариев.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects include all source files"` — **1/1 pass**. Итого **58 свежих targeted checks**, одна host-neutral сборка; 4 прежних CA1416 warnings из guarded Windows identity probe. Tests с `COM` в имени используют fake objects и не являются Office validation.
- Source comparison: canonicalizer block совпадает с baseline после переименования API; изменения 11 существующих consumer files — только замена owner/name. Поиск прежних parser helper calls в src/tests/demo — 0. Diff и 17 затронутых локальных ссылок/anchors — pass.

Перед commit (2026-08-29): 58 checks выше переиспользованы при неизменных относящихся к ним sources/tests, dependencies, build settings и environment. Обязательный `ValidateVersionFormat` — pass; повторные runtime tests не запускались.

Full harness, MockDemo build и Windows/Office/VSTO не запускались. Унаследованный **R33** выявлен source review: non-overlapping counter может принять неоднозначное перекрывающееся вхождение; алгоритм сохранён в этом extraction. Нужен отдельный semantic fix с targeted tests до полного VBA gate.

Накопленные Windows проверки (Windows x64 + Office + VS 2022):

- 5B2: [identity probe / acceptance matrix](../../tests/RNAssistant.ExcelIdentityProbe/README.md), реальные proxy/lifetime и wrong-target сценарии; identity evidence требуется до реализации/switch factories.
- 5B2: controller/WebView selection/context/catalog reads, ошибки/закрытие книги/смена активной книги и несколько клиентов под gate.
- 6A: exact patch/guard на реальном VBE с CRLF/LF/CR, literal backslashes и комментариями; read-back/hash normalization, restore и package/rename regression. Journal/CAS evidence проверять без автоматического replay/restore.

Продолжение R33 согласовано отдельно 2026-08-29 и описано ниже. Остальные отложенные Windows gates сохраняются в своих phase reports.

## R33 — overlapping exact matches

2026-08-29, baseline `e0360f3`; отдельный semantic fix после commit 6A. `VbaPatchEngine` считает все стартовые смещения, включая перекрытия: `aaaa` / `aaa` → 2, `aaaaa` / `aaa` → 3. Неоднозначная замена отвергается даже при неизменном replacement. Newline/hash semantics, существующий `vba_patch_ambiguous` mapping, COM и journal protocol не менялись.

Два существующих tests расширены без новых fixtures/files. До fix оба дали ожидаемый **FAIL**: pure engine принимал overlap; executor пропускал его к confirmation. После fix **8 distinct targeted pass**:

| Harness filter | Result |
|---|---|
| `vba: pure patch text contract` | 1/1: overlapping offsets/counts, LF/CRLF/CR, unchanged ambiguous replacement, unique full-source match |
| `vba: patch` | 3/3: addressing/stale/ambiguity; reject до confirmation и при auto-confirm, без частичной записи ранее рассчитанной операции, backend write, backup или mutation record |
| `vba: exact patch` | 2/2: complete lines, boundary newlines |
| `vba: apply patch` | 2/2: valid unique mutation/backup, named target, mixed/all-no-op operations |

Команды: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba: pure patch text contract"`, затем остальные filters с `--no-build`. Одна host-neutral сборка для red tests и одна после production fix; 4 прежних CA1416 warnings из identity probe. Full harness, MockDemo и Windows/Office/VSTO не запускались; Office assertions используют fake adapter.

Перед commit: обязательный `ValidateVersionFormat`, diff и 13 затронутых локальных ссылок/anchors — pass.

Чистка: прежний non-overlapping counter заменён в единственном владельце, без alias/fallback/второй реализации; новые `.cs`/project includes не нужны. Canonical text contract, master/backlog/risk и migration status актуализированы. Позднее отдельно согласованный VbaReader описан ниже и не является частью R33.

Отложено: Windows x64 + Office + VS 2022 — overlapping one/multi-operation patch с обоими режимами confirmation, отсутствие live write/нового backup/journal при отказе. 5B2/R04, production binding и полный Phase 6 gate не закрыты; прежняя очередь Windows scenarios сохранена.

## Phase 6B — typed VbaReader

2026-08-29, baseline `62010c8`; отдельный host-neutral read slice после R33. `Office.Vba.VbaReader` теперь единолично строит internal VBA list/module commands, нормализует fallback-имя и проверяет typed project/module snapshots. Mutation guards, verification, reconciliation и package probes используют один reader; `ToolCatalogService` больше не строит backend-команды и не разбирает raw JSON самостоятельно. Resource adapter получает только уже проверенный backend payload и сохраняет прежний bounded wire.

Review воспроизвёл **R34**: успешный list payload `{}`/не-array `modules` и успешный module payload без `code` трактовались catalog как пустой/отсутствующий результат и могли попасть в минутный cache. Regression сначала дал ожидаемый 0/1; теперь malformed success завершает загрузку без partial publication/cache, следующее независимое чтение повторяет backend access, а настоящий `modules: []` кэшируется. Reader также fail closed проверяет field types, duplicate names, requested/returned identity, SHA-256 и truncation consistency.

Границы после switch:

- `HostRuntime` по-прежнему владеет document gate, operation root и target binding; reader не открывает и не удерживает gate.
- `VbaToolExecutor` владеет reconciliation, observations, guards, mutations, journal/read-back и текущим ToolResult/resource mapping.
- `ToolCatalogService` владеет только discovery/cache; host-specific COM и live Office authority остаются в adapters/`VbaProjectSupport`.
- Старые `TryReadVbaModule`, list/resource read builders, duplicate name-normalization/not-found helpers и catalog raw parsers удалены. Новые production/Harness/MockDemo includes добавлены; alias/dual-read path нет.

Verification: `vba:` — **58/58 pass**; `host runtime: direct VBA catalog reads share access` — **1/1**; `harness: production projects include all source files` — **1/1**, итого **60 distinct targeted cases**. MockDemo compile — 0 errors / 3 existing CA1416 warnings; `ValidateVersionFormat`, diff/check и затронутые docs links — pass. Full harness и Windows/Office/VSTO не запускались.

Отложено: реальные Excel/Word/PowerPoint VBE list/read, denied Trust Access, large/truncated modules, close/reopen/Save As и catalog refresh под production session; затем отдельно согласовать `VbaMutationService`/`VbaVerifier` начиная с apply_patch. COM/HostRuntime/factories/journal/result wire и Phase 7 этим substep не менялись. Phase 5B2/R04 и полный Phase 6 gate открыты.

## Phase 6C — VBA apply-patch mutation service

2026-08-29, baseline `fba247b`; отдельный host-neutral mutation boundary после
6B и завершённой Phase 9C. `Office.Vba.VbaMutationService` теперь владеет полным
`common.vba_apply_patch` workflow: current snapshot/guard, ordered patch,
prepared journal, backend dispatch, read-back и terminal classification.
`Office.Vba.VbaVerifier` стал одним владельцем module write/delete verification
и module assessment; действующие write/delete/restore и reconciliation используют
его без изменения публичного результата или durable evidence.

Границы после switch:

- `VbaToolExecutor` разбирает tool arguments, вызывает сервис и пока маппирует
  legacy `ToolCommand`/`ToolResult`; остальные whole-module entrypoints,
  reconciliation loop и package/rename journal остаются в executor.
- `VbaMutationService` не открывает `HostRuntime` gate и не выбирает документ:
  caller сохраняет один bound operation scope, host adapter остаётся COM authority.
- `VbaJournalStore` и CAS сохраняют прежние bytes/events/hashes; второго store,
  dual-write, recovery replay или нового wire нет.
- Удалён прежний `VbaToolExecutor.Patching.cs`; общие module journal/verifier
  implementations не дублируются. Package assessment не перенесён скрыто.

Новый direct service case проверяет собственные guard/dispatch/read-back/committed
journal evidence. Полный `vba:` filter — **59/59**, production source includes —
**1/1**, итого **60 distinct harness cases**; MockDemo compile — 0 errors /
3 existing CA1416 warnings. `ValidateVersionFormat`, docs links и diff check —
pass. Подробности: [Phase 6C evidence](PHASE_6C_VBA_MUTATION_SERVICE.md).

Открыто: 6D должен заменить service-границу `ToolCommand`/`ToolResult` typed
domain request/outcome, оставить mapping в executor, удалить rollback classification
по тексту и закрыть terminal-persistence/fault matrix. Whole-module/package entrypoints,
5B2/R04, production identity, Windows x64 + Office + VS 2022 VBE/read-back/package
qualification и полный Phase 6 DoD не закрыты.

## Phase 6D — typed VBA mutation outcome

2026-08-29, baseline `7a58825`; отдельный host-neutral outcome/fault slice после
6C. `VbaMutationService` больше не принимает `ToolCommand`, `ChatSession` или
legacy `ToolResult`: patch guard/workflow и общий module journal pipeline используют
typed requests, typed backend action result и финальный `Ok/Error/Unknown`.
`IVbaMutationDocumentContext`, `IVbaMutationReader`, `IVbaMutationBackend` и `IVbaMutationJournal`
ограничивают service capabilities; adapters оборачивают текущие host/store owners,
не создавая второй store или execution path.

`VbaMutationToolResultMapper` в Tools — единственный domain→legacy mapping этого
контура. Unknown всегда non-retryable и материализуется существующим Tool Result v1
как `unknown`. Verified intended state даёт `ok`, включая backend throw после
фактической записи; verified before state даёт definite `error`. Terminal append
failure возвращает `unknown` с `terminalRecorded=false`, оставляет preparation
открытой для read-only reconciliation и не повторяет dispatch.

Rollback больше не определяется по словам `restored`/`removed`/`rolled back`.
`rolled_back` возможен только по явному typed disposition и совпадению live before;
текущий legacy backend adapter его не синтезирует. Общий Tool Result больше не
содержит `journalStatus`/`packageJournalStatus` и не упоминает internal terminal
classification в message; durable status остаётся в journal/diagnostics. Source
read-back не объявляется VBA compile validation.

Новые fault cases покрывают prepare persistence, terminal append, backend throw до
и после effect, unavailable/mismatched read-back и cancellation до/после dispatch.
Существующие tests повторно покрывают restart-after-prepared, VBE normalization,
duplicate target и target-not-found. Полный `vba:` filter — **67/67**; отдельные
Agent unknown/causal, production-source include и MockDemo/format проверки указаны
в [Phase 6D evidence](PHASE_6D_VBA_MUTATION_OUTCOME.md).

Открыто: whole-module write/delete/restore и package/rename полные workflows ещё
executor-owned; package result semantics и host backend disposition будут типизированы
в следующих Phase 6 slices. 5B2/R04, реальный COM/VBE, controller wiring и полный
Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6E — whole-module VBA write service

2026-08-29, baseline `26f678c`; пользователь разрешил следующий отдельный
host-neutral slice. Полный write workflow `upsert/createOnly/updateOnly` перенесён
из `VbaToolExecutor` в `VbaMutationService.WholeModuleWrite`: deterministic name
normalization, missing/existing preparation, observation/confirmation guard,
existence-mode refusal, dry-run, prepared journal, typed create/replace backend,
source/type verification и terminal outcome теперь имеют одного domain owner.

Executor разбирает legacy arguments/mode, сериализует guard и отображает typed
outcome; прежние `WriteVbaModule`, `PrepareWriteGuard` и `BindWriteGuard` удалены.
`mode=rename`, delete, restore, reconciliation outer loop и package operations не
переносились. Existing `VbaMutationBackendAdapter` получил только typed create
action поверх текущего internal host command; COM implementation и store не менялись.

Review закрыл false-commit edge case: после backend error live source hash больше
не считается достаточным, если component type отличается от prepared state.
Same-source/different-type create race возвращает non-retryable `unknown` и durable
`unknown`; `createOnly`/`updateOnly` existence refusals происходят до journal/dispatch.
Полный `vba:` filter — **68/68**; связанные Agent/causal/source-inclusion и compile
проверки перечислены в [Phase 6E evidence](PHASE_6E_VBA_WHOLE_MODULE_WRITE.md).

Открыто: Phase 6F delete, затем restore и package/rename; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6F — VBA delete service

2026-08-29, baseline `57c157b`; пользователь разрешил следующий отдельный
host-neutral slice. Полный `common.vba_delete_module` workflow перенесён из
`VbaToolExecutor` в `VbaMutationService.DeleteModule`: exact existing-target read,
optional observation/confirmation guard, `StdModule`/`ClassModule` policy, dry-run,
prepared journal, typed compare-and-swap delete action, absence verification и
terminal outcome теперь имеют одного domain owner.

Executor разбирает legacy argument, сериализует typed guard и отображает outcome.
Прежние `DeleteModule`, `PrepareExistingModuleGuard`,
`ValidateExistingModuleGuard` и delete-only router helper удалены. Единственный
`VbaMutationBackendAdapter` строит текущий host-prefixed internal command; COM,
HostRuntime, journal/CAS format, public schema/result wire не менялись.

Direct service case проверяет dry-run без persistence/dispatch, CAS hash на backend,
verified absence, durable call correlation и fail-closed запрет DocumentModule до
journal/dispatch. Полный `vba:` filter — **69/69**; связанные Agent/causal,
source-inclusion и compile проверки перечислены в
[Phase 6F evidence](PHASE_6F_VBA_DELETE.md).

Открыто: Phase 6G restore, затем package/rename; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6G — VBA restore service

2026-08-30, baseline `f8c2674`; пользователь разрешил следующий отдельный
host-neutral slice. Полный `common.vba_restore_backup` workflow перенесён из
`VbaToolExecutor` в `VbaMutationService.RestoreBackup`: exact backup selection,
restore-specific confirmation guard, current-state recheck, dry-run, prepared
journal, typed create-or-replace action, source/type verification и terminal outcome
теперь имеют одного domain owner.

Guard фиксирует не только document/chat/module и current existence/source hash, но
и exact `backupId`, backup module/type и hash реально загруженного CAS source.
Подмена id/live source после preparation, stale target и несовместимый component type
отклоняются до journal/dispatch; это закрывает host-neutral R40, найденный при
6G audit. Executor разбирает legacy arguments, сериализует
typed guard и отображает outcome; старый restore workflow, его guard helpers,
прямой journal lookup и verifier alias удалены. Единственный Tools adapter строит
host-prefixed replace/create commands; journal/CAS format, COM, HostRuntime,
public schema/result wire не менялись.

Direct service case проверяет отсутствие dispatch/journal без guard, binding exact
backup id/live source, stale current target, dry-run, compare-and-swap hash, verified
restore, durable call correlation и type refusal. Полный `vba:` filter — **70/70**;
связанные Agent/causal/source-inclusion и compile проверки перечислены в
[Phase 6G evidence](PHASE_6G_VBA_RESTORE.md).

Открыто: отдельное решение и перенос package/rename ownership; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6H — VBA package/rename scope audit

2026-08-30, baseline `cd0bd615`; отдельный docs-only consumer/architecture audit
после 6G. Проверены Agent/manual dispatch, временный install/run/cleanup, Tools UI
persistent install/uninstall, catalog status/discovery, public `mode=rename`, общий
package journal/reconciliation и diagnostics projection.

Решение: весь существующий package lifecycle остаётся в stable core и получает одного
typed owner в отдельном 6I. Отделять persistent UI в Phase 11 нельзя без сохранения
второго safety-critical install/remove path. Dynamic tool definition authoring, новые
package features и pipelines этим решением не включены. Rename является обязательной
public mutation и переносится отдельно в 6J; существующий `package.mutation.*` storage
wire сохраняется без rewrite/dual-write, но domain API остаётся rename-specific.

Найден R41: применённый session install при потерянном terminal/cleanup может оставить
временные components; текущий marker-insensitive probe затем считает совпадающий код
обычным `installed` и не гарантирует cleanup. 6I обязан воспроизвести это, связать
session lifecycle, различать ownership marker и блокировать execution при unknown или
незавершённой cleanup. Automatic replay/remove/overwrite не разрешены. Текущие package
tests проверяют happy path, macro failure cleanup, atomic records и mixed interrupted
reconciliation, но не terminal/cancellation/backend-throw/session-orphan matrix;
rename tests также не повторяют typed 6D fault matrix.

Runtime, tests и UI не менялись; build/harness не запускались. `git diff --check`,
13 новых local-link targets и `ValidateVersionFormat` — pass. Consumer map, ordered
6I→6J scope и gates: [Phase 6H evidence](PHASE_6H_VBA_PACKAGE_SCOPE.md).

Открыто: Phase 6I/R41, затем 6J и полный Phase 6/WQ-VBA; 5B2/R04 и реальный
Windows x64 + Office x64 + VS 2022 controller/COM/VBE gate не выполнены.

## Phase 6I — typed VBA package lifecycle

2026-08-30, baseline `0c6e5db`; отдельный host-neutral runtime slice. Новый
`Office.Vba.VbaPackageService` стал единственным owner package validation/probe,
document-local/persistent/session state, temporary install/run/cleanup, persistent
install/remove/status, package journal/read-back/reconciliation и typed
`ok/error/unknown`. `VbaToolExecutor.Packages` теперь только ToolDefinition/command/
result adapter; executor-owned package mutation helpers удалены. Оставшийся compound
journal код обслуживает только rename и удаляется в 6J.

R41 закрыт host-neutral: session install/remove получают один durable `LifecycleId`,
тот же id входит в exact ownership marker. Probe совмещает live marker/source/type с
append-only journal, поэтому потерянный terminal/cleanup, повреждённый или удалённый
marker и незакрытый lifecycle блокируют macro и persistent overwrite. Reconciliation
только наблюдает и дописывает terminal; cleanup выполняется лишь новой явной
policy-authorized journalled Uninstall над exact неизменённым session-owned package.
Install передаёт exact prepared existence/type/source/marker guard существующему
backend, который отказывает при post-prepare drift до первой мутации.
Lifecycle/search/ownership evidence проецируется в существующую diagnostics detail.

Host-neutral package fault cases покрывают prepare failure, throw до effect,
mutate-then-throw, read-back loss, terminal loss/restart, cancellation до/после
dispatch, cleanup failure, marker drift/strip, race между probe/preparation и перед
macro, post-prepare backend CAS, лишний catalog component, mixed multi-component
recovery и VBE normalization. `vba: package` 22/22 и полный `vba:` 87/87 — pass.
Точные команды/результаты и границы — в
[Phase 6I evidence](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md).

Production source-project inclusion 1/1 — pass. MockDemo actual-controller compile:
0 errors / 3 existing CA1416 warnings. `ValidateVersionFormat`, diff/static cleanup
и 158 local link targets в 10 changed Markdown files — pass. Full harness и
Windows/Office/VSTO не запускались.

Открыто: отдельный 6J rename switch; production COM/VBE/Trust Access/controller и
полный Windows x64 + Office x64 + VS 2022 WQ-VBA. Product `16.1.0-dev`, release/tag
не создавались.

## Phase 6J — typed VBA rename ownership

2026-08-30, baseline `6f9ddcc`; отдельный host-neutral runtime slice.
`Office.Vba.VbaMutationService` теперь владеет полным `mode=rename` contour:
source/destination guard, two-identity preparation, typed backend action, source/type
read-back, terminal `ok/error/unknown` и read-only recovery. `VbaToolExecutor`
оставляет только argument/result mapping и вызов reconciliation; его последний
compound journal path удалён без alias или второго execution path.

R42 закрыт host-neutral: confirmation guard связывает оба имени, source hash,
component type и code-only UserForm state. Typed backend повторно проверяет source
hash/type до COM rename. Cancellation проверяется после durable preparation и перед
dispatch; после возможного effect live identities определяют outcome. Verified
intended state побеждает backend error, complete before даёт definite error, mixed/
collision/unreadable state и terminal loss дают non-retryable unknown. Recovery
дописывает только terminal для complete-before/complete-intended/mixed и никогда не
повторяет rename.

Существующий `package.mutation.*` two-component wire и CAS остаются единственным
durable authority через narrow `IVbaRenameJournal`; новый store/snapshot/dual-write
не введён. Public schema/result shape и package runtime не менялись. Точная граница,
fault matrix и проверки — [Phase 6J evidence](PHASE_6J_VBA_RENAME.md).

Host-neutral `vba:` regression — 91/91 pass, включая 4 новых direct typed rename
cases и прежние schema/confirmation/COM/VBE/package regressions. Production source
include — 1/1 pass; MockDemo — 0 errors / 3 existing CA1416. Version/diff checks
pass; 161 local links в 10 changed Markdown files имеют 0 missing targets. Windows
x64 + Office x64 + VS 2022 COM/VBE/Trust Access,
confirmation/cancellation/restart qualification остаётся WQ-VBA.

Следующий отдельный шаг — Phase 7 Excel read/write; Phase 6 runtime повторно не
расширять. Product `16.1.0-dev`, release/tag не создавались.

## Phase 7A — Excel boundary and consumer audit

2026-08-30, baseline `d362b48`; docs-only prerequisite. Проверены public catalog,
`ConversationKernelAdapter`/`OfficeToolExecutor`, manual execution, HTML data
bind/refresh, `ExcelAdapter`, current neutral DocumentSession contract и fake/demo
call sites. Runtime, schemas, factories, COM и tests не менялись.

Зафиксирован ordered route: 7B атомарно переключает весь `excel.inspect` selector
family и `excel.read_range` на typed read owner/native handlers; HTML data использует
тот же read adapter. 7C отдельно переносит только `excel.write_range` с exact
before/read-back и typed effect evidence. Atomic 11T0/7D подаёт extracted interop
backend только bound workbook и удаляет compatibility resolver/internal command
seam; WQ0 больше не блокирует implementation. Ни один substep не расширяет scope на
другие Excel mutations.

`inspect(kind=charts)` означает только metadata read внутри уже существующего public
tool; `create_chat_chart`, chart/table mutations и formatting исключены. Range limit
должен проверяться до COM materialization. Найдены и открыты R43 (unbounded inspect /
named-range `Value2`) и R44 (HTML direct adapter route). Полная карта и gates —
[Phase 7A evidence](PHASE_7A_EXCEL_SCOPE.md).

Docs-only gates: `ValidateVersionFormat` и `git diff --check` pass; 130 local links
в 7 changed Markdown files имеют 0 missing targets. Windows/Office/VSTO и runtime
harness не запускались. Следующий отдельный шаг — 7B typed reads.

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Legacy ToolResult → LegacyToolOutcomeAdapter | ToolRuntime | Unmigrated Office/domain handlers → kernel records | Excel inspect/read/write bypass it after 11T0; remaining mandatory 11T handler slices remove their consumers; R23 remains |
| LegacyToolDefinitionAdapter | ToolRuntime | Current legacy catalog/schema/authoring and execution consumers listed in `MIGRATION_MAP.md`; resource projection removed | Remaining mandatory 11T built-in switches and 11J authoring switch; `ProjectRead` removed in 10C2, no resource dependency |
| LegacyToolResultAdapter | ToolRuntime | Active legacy domain executors → typed result materialization | Excel inspect/read/write bypass it after 11T0; remaining mandatory 11T handler slices remove their consumers; no old-history reader |
| ToolResultUiProjection | Application / UI | Native manual commands and Activity projection; never model writer | Phase 9 runtime state switched; remaining manual/domain presentation consumers leave with mandatory 11T slices |
| Unbound host identity/access | HostRuntime / host factories | Word, PowerPoint and Outlook production adapters | Excel removed this path in 11T0/7D; remaining hosts switch in mandatory 11T6–11T8. Windows gates remain evidence, not fallback owners |
| `VbaMutationDocumentContextAdapter` / `VbaMutationReaderAdapter` / `VbaMutationBackendAdapter` | VBA / Tools | Typed mutation service over current wide host and legacy read/mutation results; backend serves patch, whole write/rename, delete and restore | Document context after 11T0; mandatory 11T9 direct-handler/typed-host cleanup before Phase 12 |
| `VbaPackageToolAdapter` / `VbaPackageBackendAdapter` | VBA / Tools | Existing 6I package service over current ToolDefinition/host command/result seams | Mandatory 11T9/11J direct-handler/authoring switch before Phase 12; one-way mapping only, no domain legacy result/history |
| `VbaMutationToolResultMapper` | VBA / Tools | Current VBA executor → legacy handler boundary | Mandatory 11T9 direct typed ToolRuntime handler switch before Phase 12; no model-history reader |

`VbaMutationJournalStoreAdapter`, `VbaPackageJournalStoreAdapter` и
`VbaRenameJournalStoreAdapter` — permanent narrow ports к тому же
`VbaJournalStore`, не compatibility store и не второй writer. Permanent
model-session/metadata owners не являются compatibility adapters.
Остальные consumers/removal gates — в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; guard 1C закрывает host-neutral safety assertions, production qualification ещё не выполнена.
- R02 и R07 contained host-neutral, но live-provider/Windows VBE gates открыты; R03–R06 остаются открыты. R08/R09 получили typed module outcome, terminal persistence и cancellation coverage в 6D; delete/restore switched через 6F/6G, package lifecycle/CAS/fault matrix — через 6I, rename — через 6J. Windows reconciliation/COM gates ещё открыты. R10/R11/R48 закрыты host-neutral через Phase 9; real Windows/WebView/restart/multi-window acceptance остаётся.
- R04: operation gate проверен host-neutral в 5B1; production bound Excel/common identity и Windows wrong-target scenarios — 5B2.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R22: compact catalog harness failure воспроизведён до изменений 1B; owner ToolPack/Tests, Phase 8.
- R26: full-history preflight, current v4 writer/confirmation, runtime IDs/origins и singleton enforcement проверены host-neutral; 4A заменил temporary name registry source-owned typed policy. Production controller ordering/Office qualification остаются открыты.
- R27: explicit review/reset проверены на current v4/schema16; custom text schema15 и остальных прежних/future markers сохраняется до явного review/reset. Production controller/WebView/DPAPI validation открыта.
- R29: runtime-owned IDs введены отдельным v4 switch; полного исходного incident trace нет, Windows/live-provider qualification остаётся открыта. Evidence и ограничения — [R29_RUNTIME_CALL_IDS](R29_RUNTIME_CALL_IDS.md).
- R33: overlapping exact-match ambiguity исправлена host-neutral; 2 regression tests red→green, 8 targeted pass. Реальная Windows/VBE regression остаётся открытой.
- R40: restore guard теперь связывает exact backup id/type/live-source hash и current target; substitution block проверен host-neutral, Windows confirmation/VBE gate открыт.
- R41: runtime fixed host-neutral в 6I — durable lifecycle + marker/journal-aware fail-closed state и explicit cleanup regression pass; production Windows/VBE/Trust Access qualification открыта.
- R42: rename guard/backend теперь связывают source type/hash и оба имени; cancellation/fault/recovery matrix pass host-neutral. Production Windows/VBE/confirmation/cancellation qualification открыта.
- R43/R44: исправлены host-neutral атомарным 7B switch; real Excel/WebView2 qualification остаётся открытой.
- R50/R55: runner и exact-build admission contained host-neutral через WQ-A5; missing
  adapters остаются N/A. Реальная подпись, PowerShell/certificate store и полный
  Windows/live pack matrix открыты.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
