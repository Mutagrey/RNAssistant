# RNAssistant backlog

Здесь находится только незавершённая отложенная работа. Это не текущий план:
рабочий baseline, приоритеты и открытое evidence находятся в [PROGRESS](PROGRESS.md),
исторический порядок миграции — в [master plan](STABILIZATION_MASTER_PLAN.md),
действующие риски — в
[RISK_REGISTER](RISK_REGISTER.md), временные adapters — в
[MIGRATION_MAP](MIGRATION_MAP.md). Завершённые этапы остаются в phase/WQ evidence и
сюда не копируются.

Общий feature freeze завершён. Запись здесь сама по себе не означает приоритет:
новая возможность требует явной задачи, scope и owner; дефекты работающей системы
приоритетнее расширения без конкретного пользовательского результата.

## Large Excel search without manual range slicing — 2026-09-30

Owner: Excel search / Resource Fabric. Literal queries up to 255 characters without
newlines now use bound Excel `Find`/`FindNext`; the prior whole-scope snapshot limit
no longer applies to that path. Regex, longer literals and other exact snapshot
reads still reject scopes above 100,000 cells or one million characters. Recovery
uses sheet UsedRange inspection and explicit smaller ranges. Evaluate runtime-owned
bounded chunk traversal only for searches that still need it, with exact coverage,
revision/drift handling and an explicit incomplete result if a chunk fails. Keep
per-chunk capture bounds; a single larger snapshot increases COM time and memory
without solving coverage. Real Excel `Find` behavior remains Windows evidence.

## Large resource working set and compacted action memory — 2026-09-29

The shared working-set/action-memory implementation is complete host-neutral; its
remaining live validation is tracked in the
[continuity incident](RISK_REGISTER.md#agent-continuity-and-deterministic-read-loops--2026-10-01).
The PDF-specific bounded-view extension below remains a separate Resource Fabric slice.

Owner: Resource Fabric / model context compiler / context compaction. A model-facing
`common.resources_read` assembles a complete text view (up to 2,000,000 characters)
from internal 32,000-character pages. A large PDF can therefore exceed the model
request budget; the current tool has no bounded PDF page/text selection. Search
matches are discovery evidence, not a substitute for an exact selected text view.
Provide a bounded semantic PDF view with explicit page/range coverage and a
budgeted current excerpt working set. Keep extracted originals in CAS and older
read findings in provenance-checked claims; never label an excerpt as the entire
PDF or silently drop a selected view.


## Outlook attachment follow-ups — 2026-09-08

Owner: Outlook domain / Resource Fabric. The authorized PDF/text/image slice does
not include DOCX/XLSX/PPTX extraction, archives, embedded .msg/OLE items or mailbox-wide
attachment search. Any extension requires a separate approved scope. Windows delivery
and real model/COM checks remain open under the existing qualification gates.

## Outlook exact mail from a large folder — 2026-09-30

Owner: Outlook backend / Resource Fabric. When a folder has more than the 500
discovered items, an `Outlook mail` title cannot be proven unique against older
messages, so its semantic read returns `resource_scope_incomplete`. The folder
collection now resolves independently and supports `latest:N`, but previews do
not replace complete bodies or attachment reads. Provide a bounded exact lookup
or runtime-issued disambiguating semantic target with full-folder uniqueness
evidence before enabling those reads; preserve the no-EntryID model boundary and
verify on real Outlook. Until then, use the mailbox chat archive scan for full
mailbox analysis.

## Resource read prompt wording — 2026-09-28

Owner: Agent/Chat prompt defaults and Resource Fabric. Current Chat instructions
require `common.resources_find` before every read and forbid `offset`, although a
semantic target may already be present in `RUNTIME_CONTEXT` and bounded
`table`/`records` reads accept a row offset. Agent tool instructions say resource
reads use only scope, target, representation and action, omitting the supported
`section`, `limit`, `offset`, `fields` and `path` selectors. Correct both defaults
in one prompt-schema change, then review/reset saved prompts through the existing
explicit UI flow and run focused prompt/schema tests. Do not silently replace
saved user instructions while fixing the HTML observation loop.

## Complex Agent planning and model evaluation — 2026-09-28

Owner: Conversation application / Agent planning / model evaluation. Source changes
allow Agent to use the existing revisioned document Plan after bounded read-only
discovery; `startNew=true` creates an independent Plan for another task. Task List
is the concise execution projection, with preserved prior stages and a runtime
completion check. No second outcome store was added. Focused harness and live-model
evaluation are deferred at the user's request.

Representative target-model scenarios must cover Excel/VBA source inspection before
dashboard design, two differing table layouts mapped to one semantic schema,
source change/refresh, failure without placeholder replacement, Task List step
preservation/closure, and Skill/Tool authoring only after the primary result works.
Score accepted calls and retained evidence, not final prose alone. Harness stubs
remain contract regressions; they cannot establish model usability or Windows
Office/WebView2 behavior. Uploaded XLSX ingestion, if required for sources outside
the bound workbook, is a separate scope: current chat attachment import does not
accept binary spreadsheets.

## Compaction helper prompt contract drift — 2026-09-28

Owner: context compaction. The default now requests
`claims[{kind,text,sourceIds}]`, matching the appended instruction, JSON schema
and parser. Authored custom helper text is preserved. Focused harness verification
remains deferred at the user's request.

## Resource context fixture drift — 2026-09-08

Owner: resource context / harness. On the `d29b1f58` branch, unchanged provider
search fails the existing
`artifacts: prompt uses bounded working set` raw-metadata exclusion assertion
(`runtime-secret-id` is searchable). Reconcile with the parallel projection fixes
before declaring this integration gate closed. This failure is not changed by the
working-set purpose/read-hint implementation. This is not Windows evidence.

## Plan operation identity edge case — 2026-09-08

Owner: document artifact mutation domain. `PlanDocumentService.CreationId` hashes
chat/run/step without call id. The current in-flight singleton-mutation boundary
rejects multiple model mutations in one response before dispatch, removing the
reported Agent/Plan batch route. HTML receipts already include call id; the former
combined Plan/HTML statement is stale. Before admitting another same-step Plan
caller or changing batch policy, make Plan operation identity call-scoped and
explicitly handle prepared/persisted receipts without replaying unknown effects.
This is a bounded future guard, not a verified lost write in the current model path.

## Remaining bridge duplication — 2026-09-29

Owner: Office bridge presentation / Web artifact UI. Message artifact links still
expose `ResourceRef` URIs that Web parses to identify revisions; move that
association into typed artifact/message ids before removing those refs. Check
the exact artifact cards and compare response size on the affected Windows chat
before assigning this duplication as a main stall.

## Model request schema duplication — 2026-09-29

Owner: conversation prompt / ModelProtocol. In `json_schema` mode,
`RUNTIME_CONTEXT.tools` includes full callable parameter schemas while
`response_format.json_schema` includes the current callable argument contracts
again. Measure each part of the retained materialized request on the affected
chat before reducing it. Any prompt reduction must keep `json_object` and the
one-time schema-rejection fallback usable with the same accepted prompt, retain
exact callable authority, and pass focused prompt/schema plus target-model checks.
The v5 response envelope itself is already limited to `message`, `final` and
`tool_calls`; do not add a second wire format to save bytes.

## Structural debt

Рефакторинг начинается только вместе с конкретным изменением, которое он упрощает.
Для каждого slice заранее фиксируются удаляемая зависимость/старый путь и локальная
проверка; количество строк или `partial` не является основанием.

| Debt | Owner и проблема | Условие начала | Результат и проверка |
|---|---|---|---|
| D01 — compact current docs | Documentation owners. `architecture.md`, master plan и `PROGRESS.md` всё ещё содержат длинную историю и повторяют часть domain docs | После R61 или при изменении соответствующего canonical contract; historical master/progress archive — только после 16.1 с полным backlink inventory | `architecture.md` оставляет только layers/owners/flows; история остаётся evidence, но исчезает из default reading path. Проверка всех local links/anchors |
| D02 — single composition path | Application. `AssistantController` одновременно orchestrates и конструирует concrete storage/runtime/model/catalog/session/diagnostics graph | Следующее изменение production lifecycle/dependency graph после текущих R61/WQ gates | Один существующий application-owned path владеет construction и dispose order. Не добавлять factory/interface только ради DI; новый composition type допустим лишь при удалении нескольких concrete construction paths и отрицательном production LOC. Targeted lifecycle + architecture checks |
| D03 — versioned bridge operation catalog | Bridge. Большой string switch не гарантирует C#/JS parity и единый JSON casing | Следующее versioned bridge изменение после qualification текущего WebView path | Typed catalog/handlers, handshake version и один canonical casing; удалены ad-hoc operation routing и dual Pascal/camel reads. C#/JS parity, serialization/error-envelope и Windows WebView checks |
| D04 — change-driven hotspot extraction | Владельцы Controller, ToolRuntime, prompts, storage и UI. Крупные файлы затрудняют локальные изменения, но не доказывают смешение ответственности | Только когда ближайший approved change требует чтения несвязанного behavior | Извлекается один тематический owner из `AssistantController*`, `OfficeToolExecutor`, `PromptContextInspectorService`, `ChatStore*` или крупного `app-*.js`; старый path удаляется, targeted tests сохраняют контракт |

Не проводить общий предварительный split/rename, массовый namespace move, новый
service locator, второй store/read model или универсальный Office abstraction.

## User-deferred source allocation — 2026-09-07

The user initially deferred two corrections. Inspector JSON serialization is now
fixed host-neutral in its canonical owner; only Outlook remains here. Bounded
transport alone does not qualify source allocation.

- Outlook backend/resource owner: `MailItem.Body` allocates the whole COM string
  before rejecting its size. Revisit with a concrete large-mail Windows failure
  or before claiming bounded-source qualification; keep real Outlook gates open.

## Document artifact discovery/recovery — remaining authorized slices

- Artifact resource owner / slice 3: model discovery now pages current roots through
  the existing ordered authority Heads projection, with bounded metadata hydration
  and generation guards. Working-set picker/history still need paging. Cold replay,
  ordered insertion and full-authority consumers still scale with journal size.
  Markdown/Plan and uploaded extracted-text section/chunk views now use existing
  revision/CAS retention. HTML member views now reuse the same engine beneath exact
  parent revisions. Unique ATX section reads now support document Markdown/Plans
  and complete uploaded Markdown; allocation qualification remains open;
  HTML discovery still loads/parses its bounded aggregate even with a warm index.
  No parallel durable library/search store.
- Partial model discovery is implemented host-neutral (2026-09-08): missing/corrupt
  current metadata, bodies and unknown heads preserve healthy matches with explicit
  incomplete coverage. No uniqueness/negative inference from unavailable scopes;
  generation drift invalidates continuation; same-generation metadata recovery is visible to a fresh scan without shifting existing source slots. Exact reads stay
  strict. Markdown/Plan search now supplies bounded heading context and preserves
  search-only descriptions; richer compiler context remains open.
- PDF extraction/read coverage follow-up: the extractor may stop exactly at the
  character bound with remaining pages while `TextTruncated` stays false. Uploaded
  indexed search now detects omitted pages from retained page metadata. Qualify
  producer and exact-read coverage propagation separately; text search does not
  assert visual/image coverage.
- Resource authority / journal recovery: whole-authority capture failure or invalid
  runtime identity still fails explicitly. Per-resource projections cannot invent
  lost authority records. Reconcile only from validated durable evidence; no latest
  fallback, automatic mutation replay or data deletion.

## Existing defects outside the completed cutover

- Bridge UI transport: define pending termination for a silent transport loss;
  an operation-specific wait limit must not imply physical mutation cancellation
  or trigger automatic replay. Explicit `postMessage` exceptions are now handled
  by the shared send boundary; ready/queued/init regressions pass host-neutral.

## Chat / diagnostics UX follow-ups — 2026-09-07

The user-authorized 11E presentation slice is implemented host-neutral.
Lifecycle/history separation, RunId grouping,
semantic targets, one current-action shimmer, disclosure retention and readable
cause cards with lazy technical JSON replace the reviewed presentation paths.
Canonical behavior: [conversation projection](../conversation-protocol.md#effect-mapping-and-ui-projection)
and [trajectory query](../trajectory-query.md).

Remaining bounded follow-ups:

- **Per-effect reconciliation:** “resolved/using an existing sheet” still requires
  correlated target/inspection evidence. Do not infer resolution from model prose
  or later unrelated success. Native counts/history and unknown-effect warnings
  remain intact. Owner: kernel/evidence projection in a separately scoped slice.
- **Typed result summaries before budgeting:** owner domain result producers /
  ToolRunResult materialization / AgentTranscript. Catalog-owned action/icon/target
  metadata is implemented; web tool-name dictionaries are removed. Remaining work
  is a typed result summary before transcript truncation, replacing the four
  source-checked Resource/Capability JSON caption branches atomically. Preserve
  representation, coverage and next-request media preparation independently of model
  `message/data/resources` and effect evidence. No arbitrary business-field parsing,
  second result store or promise that every binary format is previewable. Acceptance:
  large results and replay retain accurate summaries; exact model wire, body refs,
  partial coverage and mutation uncertainty stay unchanged.
- **Delivery qualification:** local browser component scenarios cover narrow
  320/400/600px layouts, themes and disclosure transitions; actual Office/WebView2,
  DPI, keyboard focus during live replacement, multi-window replay/confirmation
  and large live histories remain Windows gates. No real Office build was run.

## Deferred product decisions

Эти пункты требуют отдельной пользовательской задачи и prioritization; они не
становятся обязательными из-за старого Phase 12 плана.

- **Storage lifecycle:** retention/pruning для chats, payloads, artifacts, VBA
  snapshots и exports; явный re-key; VBA-journal export. Любое удаление остаётся
  reference-aware и fail-closed.
- **Replay and evaluation:** reproducible replay/eval fixtures и aggregate
  latency/token/cost/outcome projections из canonical journal, без второго
  telemetry truth.
- **Persistence seams:** оценить `ISessionPersistence`/`IBlobStore`; optional
  SQLite разрешён только как disposable query accelerator, не durable authority.
- **Desktop UX/runtime:** direct wrapper-to-pipe activation, docking modes и более
  широкий typed tool surface — отдельные slices. Controlled macro injection
  требует отдельного safety design, Trust Access detection и confirmation.
- **VBA Designer/FRX:** полная поддержка возможна только как новый protocol с
  export/import, CAS и visual/state verification. Текущий CodeOnly contract не
  расширяется скрыто.
- **Pipelines:** execution/discovery/storage/UI остаются отключены. Возврат возможен
  только отдельным решением через текущие ToolRuntime/contracts, без legacy formats.

## Release-gated maintenance

- Проверить VSTO/ClickOnce update/install и assembly binding на Windows до изменения
  historical `AssemblyVersion=16.0.4.0`; рекомендацию `16.0.0.0` автоматически не
  применять. Владелец контракта — [VERSIONING](../operations/VERSIONING.md).
- Проверить prepare/sign/finalize workflow на release workstation. Обычный commit
  его не запускает; точный gate — в
  [RELEASE_PROCESS](../operations/RELEASE_PROCESS.md) и R19.

Windows/Office/live-provider checks, включая R21/R24/R25, перечисляются только в
`PROGRESS.md` и `RISK_REGISTER.md`, чтобы backlog не становился второй матрицей
qualification.

## Screenshot architecture review — 2026-09-07

Read-only source review; these are bounded follow-ups, not a new cutover or a
claim of measured UI cost. Current priority remains chat navigation/persistence.

- **Result representations:** owner `ModelToolResultProjection` /
  `ConversationModelSession`. Family-specific JSON cleanup and exact-read exclusion
  from generic artifact wrapping still exist. Results above 8192 characters are
  archived through `ResultPayload`; selected non-folded results now hydrate the
  complete CAS payload when the calibrated request budget permits. The 2026-09-07
  correction removed the unused pre-archival model-message copy and capability-
  admission wire parsing for ordinary results; capability reads retain exact
  pre-archival descriptor checks. Before replacing a family cleanup path,
  measure parse/clone/serialization cost;
  preserve one execution result and exact evidence, and remove its old projection
  branch with focused wire/provenance checks. Do not add a second durable store.
- **Provider intent routing:** owner `ResourceGatewayService.Intent`. Explicit
  Excel/Word/PowerPoint/Outlook type branches remain. A future approved provider
  extension may introduce a narrow semantic-resolution capability and remove the
  corresponding branch, with ambiguity/coverage/exact-binding tests. No generic
  plugin platform or provider expansion is scheduled by this review.
- **Tool definition duplication:** owner tool catalogs / `DirectToolBindingCatalog`.
  `ToolCatalogEntry` already carries schema/policy/binding, but binding resolution
  and model projection still use separate tool/family dispatch. Consolidate only a
  named approved tool-family change; preserve separate human docs/model descriptions.

The screenshot's free-form-only compaction claim is obsolete: compaction requires
claims with valid sourceIds and attaches source messages/evidence/generations.
`ModelContextCompiler.Compile` consumes atoms/evidence and bounded CAS payloads;
no direct Excel/VBA/PDF/HTML execution was found. `BuildPreview` still accepts an
Office adapter and delegates prompt composition; this is a boundary to watch, not
proof of a domain-executing compiler monolith.

## Reported upstream model HTTP 502 — 2026-09-08

The user's run-log screenshot reports HTTP 502 Bad Gateway from the configured
model endpoint alongside an unrelated incomplete Excel name-catalog read refusal.
The local resource-routing defect is corrected separately. The photograph does not
establish the 502 cause or a causal link to the resource failure. Retest the workbook
with the corrected build; if 502 recurs, correlate the failed request with server/
gateway diagnostics. No speculative model-setting changes or mutation replay.
