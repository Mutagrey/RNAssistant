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
| D05 — agent-loop contraction | Controller / Tool and Skill authoring. Fresh/confirmed finalization и authoring canonicalization дублируются | После текущей regression-коррекции и обязательной Windows репродукции/квалификации либо как отдельно одобренные host-neutral slices; не смешивать slices | Ниже: один owner и один удаляемый path на slice, без второго loop/store/router/base service. Каждый slice уменьшает production LOC/representation hops и проходит точные protocol/resource/tool/run gates |

Не проводить общий предварительный split/rename, массовый namespace move, новый
service locator, второй store/read model или универсальный Office abstraction.

### D05 — последовательность очистки основного цикла

Сохраняются как необходимые инварианты: один `AgentKernel`, exact captured
tool policy/binding, bound `RuntimeKey`, per-chat run lease, VBA guard/journal и
read-back, append-only event evidence и запрет автоматического повтора после
possible effect. Их количество не является redundancy: они предотвращают неверный
target, двойной dispatch и ложный success.

| Slice | Удаляемая сложность и owner | Gate завершения |
|---|---|---|
| D05.5 — one controller run finalizer | В существующем `AssistantController` объединить совпадающие fresh/confirmed checkpoint, failure, terminal save и lease cleanup; `ConversationRunService → AgentKernel` остаётся единственным loop. Не создавать второй orchestrator/service | two different chats run concurrently; same-chat duplicate rejected; confirmation/cancel/store-failure recovery; никакой global coordination/document gate не удерживается во время model wait |
| D05.6 — authoring duplicate deletion | Tool/Skill authoring удаляет дубли `ArgumentPayload/Canonicalize/Hash` через один уже существующий canonical representation owner. Не вводить общий authoring base/service: Tool, Skill и Prompt сохраняют отдельную domain validation | prepare/confirm state hash и read-back для Tool/Skill/Prompt; net deletion обязательна |

Каждый slice — отдельный commit. Перед реализацией фиксируются baseline сценарий,
удаляемые методы/переходы и ожидаемое сокращение; новый interface/class допускается
только если в том же slice удаляются более крупные production paths. Общий rewrite,
массовый rename и “унификация” без воспроизводимого дефекта не входят в план.

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
