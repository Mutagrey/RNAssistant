# RNAssistant backlog

Здесь находится только незавершённая отложенная работа. Это не текущий план:
активный подэтап и gates находятся в [PROGRESS](PROGRESS.md), порядок стабилизации —
в [master plan](STABILIZATION_MASTER_PLAN.md), действующие риски — в
[RISK_REGISTER](RISK_REGISTER.md), временные adapters — в
[MIGRATION_MAP](MIGRATION_MAP.md). Завершённые этапы остаются в phase/WQ evidence и
сюда не копируются.

Новые product features заморожены. Запись в этом файле не разрешает начать работу
до её явного включения в текущую фазу.

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

- Artifact resource owner / slice 3: `DocumentArtifactStore.List` and the working-set
  picker still scan committed revision metadata. Response paging is bounded, source
  enumeration is not. Replace the scan with the existing authority projection/index
  when implementing indexed discovery; verify large libraries and continuation drift.
- Gateway/provider / partial model discovery: chat and document-picker inspection
  now isolates missing/corrupt per-resource metadata and unknown Plan heads, retaining
  an explicit unavailable projection and unlink. Strict `common.resources_*`
  collection discovery still fails explicitly on unavailable metadata. Add typed
  partial discovery with correct completeness before returning healthy subsets;
  verify no unique-target or complete-negative inference from incomplete catalogs.
- Resource authority / journal recovery: whole-authority capture failure or invalid
  runtime identity still fails explicitly. Per-resource projections cannot invent
  lost authority records. Reconcile only from validated durable evidence; no latest
  fallback, automatic mutation replay or data deletion.

## Existing defects outside the completed cutover

- Host-neutral `artifacts: historical attachments stay reference-only` currently
  fails before compaction because one historical attachment body is present in the
  model replay where the contract expects zero. Its compaction stub also still uses
  the removed free-form `summary` shape. Owner: artifact/model-context projection.
  Reconcile the document-owned-original publication with reference-only historical
  replay in a separate artifact slice; do not merely weaken the assertion.

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
- **Richer family summaries:** target captions cover accepted scalar selectors;
  structured result counts/coverage and custom-tool display metadata still need
  source-owned presentation fields. UI must not parse arbitrary tool JSON or
  manufacture “found N”/complete coverage. Exact executor messages remain available.
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
