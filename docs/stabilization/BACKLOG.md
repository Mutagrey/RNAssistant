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

## Existing defects outside the active slice

- Resource intent discovery: `EnumerateIntentResources` does not propagate a
  provider's `Truncated=true` when its final page has no continuation. Filtered
  discovery can therefore report completeness for an incompletely captured
  collection. Owner: `ResourceGatewayService.Intent`; address in the remaining
  consumer audit with a bounded partial-provider fixture. Named Excel tables reject
  incomplete catalogs and do not rely on this path to prove unique resolution.

- Closed-document agent fixture still expects `common.html_data_bind` to be absent
  from the catalog (`agent: closed document keeps local tools`). The current
  resource-backed binding is not intrinsically Office-only; its Gateway guards
  the selected provider. The unchanged catalog assertion fails before the updated
  Excel resource-read assertion (2026-09-06). Owner: closed-document capability/HTML
  qualification. Review that expectation and the remaining guarded-read assertion
  together; do not hide local bindings or mark this check green to fix a fixture.

- HTML source-tool fixture still exercises retired `RNAssistantData.missingData`
  while expecting the current `script.data_source_missing` finding. The focused
  `tools: html source read search and patch` check passes its read/search/patch
  assertions but fails this later diagnostic assertion (2026-09-06 source-download
  slice); the diagnostic owner is unchanged by this slice. Owner: HTML diagnostics
  qualification. Review the fixture against `RN.resources` in a separate slice;
  do not reintroduce the removed data API or report the entire check as passing.
- Web Plan fixture has stale unrelated VBA delivery evidence:
  `tests/web/plan-document.test.js:102` still requires
  `app-vba.js?v=resource-intent-20260902-1`, while the preceding committed UI already
  delivers `vba-upload-20260906-1`. Observed during the HTML mutation-upload slice
  on 2026-09-06; its four preceding Plan assertions pass, but this assertion stops
  the remainder of the file. Owner: VBA/UI qualification. Review the handoff
  fixture against its current canonical consumer in a separate slice; do not
  report the full Plan file as green or change product behavior to satisfy it.
- R61 property inventory baseline trails preceding Resource cutover changes:
  `tools: R61 built-in contract inventory` still expects old HTML binding shapes,
  resource find/read descriptors, no schema/mapping/derived tools, and earlier
  VBA descriptor revisions. These mismatches were observed on 2026-09-06 while
  removing the tool definition reader; only that retired row was removed in its
  slice. Owner: Tool catalog/qualification. Review each changed property against
  its canonical owner and update the baseline before final post-cutover Windows
  qualification; do not regenerate unchecked hashes or report this gate as green.
- VBA canonical hash versus raw source snapshot: after reading an LF module,
  changing only line endings to CRLF can reuse the same canonical revision while
  attempting to register different immutable view bytes. The Gateway then fails
  with `An immutable revision view was changed`; it does not return a fresh editable
  snapshot. Reproduced by the 2026-09-06 Tool source check using the existing VBA
  provider/authority path. Owner: ResourceAuthority/VbaResourceProvider. Resolve
  revision/view identity without rewriting retained snapshots or using equal hashes
  as revision identity; verify LF/CRLF-only drift and historical exact reads in a
  dedicated authority slice. No editor fallback or automatic write is admitted.
- Tool package README leading `U+FEFF`: `StorageFileSystem` writes a UTF-8 sidecar
  without a separate BOM, while `ToolStore.TryReadUtf8` strips its first BOM-shaped
  character. A literal leading `U+FEFF` therefore yields a different read-back and
  `unknown`, not a false successful mutation. Observed during the 2026-09-06 Tool
  upload check; transport preserves the submitted text. Owner: ToolStore/package
  authoring. Resolve exact sidecar text semantics in an explicitly scoped storage
  slice; verify ordinary/BOM-prefixed Unicode and preserve existing user files.

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
