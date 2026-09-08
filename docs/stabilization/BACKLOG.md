# RNAssistant backlog

Здесь находится только незавершённая отложенная работа. Это не текущий план:
активный подэтап и gates находятся в [PROGRESS](PROGRESS.md), порядок стабилизации —
в [master plan](STABILIZATION_MASTER_PLAN.md), действующие риски — в
[RISK_REGISTER](RISK_REGISTER.md), временные adapters — в
[MIGRATION_MAP](MIGRATION_MAP.md). Завершённые этапы остаются в phase/WQ evidence и
сюда не копируются.

Новые product features заморожены. Запись в этом файле не разрешает начать работу
до её явного включения в текущую фазу.

## Built-in inventory drift — 2026-09-08

Owner: tool contracts / harness. During Markdown section-read verification against
`22575b8c`, R61 inventory still differs for unchanged `common.resources_find`
(schema fingerprint) and `common.tools_upsert` (fingerprint and property paths:
runtime has `display.*`, baseline has `mode`). Review these inherited contracts
in a separate approved slice before updating their baseline. The new
`common.resources_read` section schema matches its reviewed inventory row.
This gate remains failed; it is not Windows qualification.

## Resource prompt test expectation — 2026-09-08

During the shared HTML slice, `artifacts: historical attachments stay reference-only`
fails only at its old unquoted `target=attachment: Untitled` assertion. The committed
`ChatResourcePromptIndex` already emits a quoted complete semantic target; neither
that formatter nor this test changed in the HTML slice. Owner: resource context /
harness. Reconcile the assertion with the canonical complete-target contract in
the next resource-context slice; retain the no-historical-body/no-runtime-URI checks.
The other 27 tests in the `artifact` filter pass. This is not Windows evidence.

## Plan/HTML operation identity in batches — 2026-09-08

Owner: document artifact mutation domain. `PlanDocumentService.CreationId` hashes
chat/run/step; HTML derives its receipt key from it. AgentKernel gives independent
calls within a batch the same model step, so a second Plan/HTML mutation can be
refused as already published. This is outside the new Markdown owner: its operation
key includes runtime call id and has same-step regression coverage. Next approved
artifact-authority slice must include call identity for Plan/HTML and explicitly
handle already prepared/persisted operations; do not silently change replay keys.
Evidence: `AgentKernel.LoopAsync`, `PlanDocumentService.CreationId`,
`HtmlWorkspacePublication.OperationKey`. This records a false-refusal risk, not a
verified lost write or permission to replay unknown effects.

## Web cache-key assertions — 2026-09-08

Owner: Web tests. `tests/web/run-view-state.test.js` already expects
`app-agent-model.js?v=run-replay-20260907-1` on baseline `0becf772`, while that
baseline ships `catalog-display-chat-20260908-1`. The behavioral assertions before
it pass; the stale key assertion fails. Update brittle cache-key expectations in a
separate Web-test maintenance slice, with current asset-version checks. This does
not close Windows/WebView2 delivery qualification.

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

- Review verification (2026-09-08, base `c3273e4972fb357d153ac7e3cba545804977e2c2`):
  `AgentKernel.ExecuteOneAsync` clears both failed
  call collections after any `Ok` tool with `MayHaveSideEffects`, without proving a
  relevant dependency changed. An unrelated successful mutation, including a no-op,
  can therefore re-admit the unchanged failed call and bypass a `RefreshRequired`
  dependency. Unknown-effect blocking is separate and remains intact. Owner:
  kernel/domain recovery contract. Replace the blanket reset with domain-owned
  correction evidence, preserve relevant correction/whole-view refresh behavior,
  and align the canonical conversation contract. Existing corrective-success
  coverage passes but does not cover unrelated/no-op mutation interleaving.

- Host-neutral `artifacts: historical attachments stay reference-only` was rerun
  without rebuilding on 2026-09-08 (same base; no newer Core/Office/harness C# sources).
  The historical-attachment removal assertion now passes. The first failure is the
  stale `target=attachment: Untitled` expectation: the actual index quotes the full
  target and includes its creation discriminator. The later compaction stub still
  uses the removed free-form `summary` shape and was not reached. Owner:
  artifact/model-context regression fixture. Update target-format and structured
  compaction setup, then rerun the complete case while retaining the body/analysis
  exclusion and semantic discovery assertions; do not classify this failure as
  evidence that historical attachment bodies still replay.

- Tool package README leading `U+FEFF`: `StorageFileSystem` writes a UTF-8 sidecar
  without a separate BOM, while `ToolStore.TryReadUtf8` strips its first BOM-shaped
  character. A literal leading `U+FEFF` therefore yields a different read-back and
  `unknown`, not a false successful mutation. Observed during the 2026-09-06 Tool
  upload check; transport preserves the submitted text. Owner: ToolStore/package
  authoring. Resolve exact sidecar text semantics in an explicitly scoped storage
  slice; verify ordinary/BOM-prefixed Unicode and preserve existing user files.

## Chat / diagnostics UX follow-ups — 2026-09-07

The user-authorized 11E presentation slice is implemented host-neutral on
`stab/11-chat-projection-ux`. Lifecycle/history separation, RunId grouping,
semantic targets, one current-action shimmer, disclosure retention and readable
cause cards with lazy technical JSON replace the reviewed presentation paths.
Canonical behavior: [conversation projection](../conversation-protocol.md#effect-mapping-and-ui-projection)
and [Issue Center](../qualification.md#11-phase-11-issue-center).

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
- **Full Issue Center:** source/build/catalog/qualification aggregation and
  redacted issue export remain the existing Phase 11 scope. The current journal
  cause cards derive only from loaded correlated rows; no additional issue store
  or implicit whole-history lookup is introduced.
- **Delivery qualification:** local browser component scenarios cover narrow
  320/400/600px layouts, themes and disclosure transitions; actual Office/WebView2,
  DPI, keyboard focus during live replacement, multi-window replay/confirmation
  and large live histories remain Windows gates. No real Office build was run.

## Deferred product decisions

Эти пункты требуют отдельного решения после stable core; они не являются Phase 12
prerequisites.

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
  archived through `ResultPayload`, then selected compiler atoms hydrate CAS.
  The 2026-09-07 correction removes the unused pre-archival model-message copy and
  capability-admission wire parsing for ordinary results; capability reads retain
  exact pre-archival descriptor checks. Before replacing a family cleanup path,
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
