# Карта документации RNAssistant

Единственная точка входа: выбери владельца области по таблице и читай нужный раздел.
Текущий статус принадлежит [PROGRESS](stabilization/PROGRESS.md); эта карта не копирует
ход реализации и результаты проверок.

## Минимальный контекст задачи

1. Определи изменяемое поведение и его владельца по таблице ниже. Начало PROGRESS
   нужно для приоритета и открытых рисков; историю читай только для конкретного вопроса.
2. Сначала найди файлы через `rg --files` или `rg -l -F` в каталоге владельца,
   затем строки через `rg -n -F` и нужный диапазон файла. Для длинного документа
   сначала найди заголовок через `rg -n '^#{1,3} ' путь`.
3. Расширяй поиск по consumers и зависимостям по мере необходимости. Перед удалением
   проверь ссылки по всему проекту, project/import files, регистрацию и динамические
   имена. Отсутствие совпадения в одном каталоге не доказывает, что код мёртвый.
4. Найди существующую проверку через [Harness guide](../tests/RNAssistant.Harness/README.md).
   Если вывод поиска обрезан, сузь каталог, маску или символ; не загружай весь файл.

[Правила разработки](development-rules.md) нужны для общих границ, testing и migration;
[архитектура](architecture.md) — для слоёв, owners и dependencies. Не читай оба документа
автоматически. `AGENTS.md` задаёт обязательные ограничения независимо от выбранного пути.
`.ignore` исключает vendor, generated output и raw evidence из обычного поиска;
при работе с ними указывай каталог явно или используй адресный `rg --no-ignore`.

## Канонические документы

| Область | Владелец текущего контракта |
|---|---|
| Model loop, modes, tool result/effect | [Conversation protocol](conversation-protocol.md) |
| Межагентные адреса, доставка, ожидание и максимум участников | [Conversation protocol](conversation-protocol.md#межагентное-общение--2026-11-03); tools — [Tool Library](tool-library.md#agent-communication-tools--2026-11-03), skill — [Skill Library](skills.md#current-implementation) |
| Wire JSON v8 | [Conversation response v8](protocols/CONVERSATION_RESPONSE_V8.md); [v7](protocols/CONVERSATION_RESPONSE_V7.md) — исторический формат |
| Совместимость model endpoint и проверка ролей/формата | [Model endpoint compatibility](model-endpoint-compatibility.md) |
| Resources, URI, providers, ingestion | [Resource Fabric](resource-fabric.md) |
| Общие текстовые изменения | [Text mutations](text-mutations.md) |
| Диагностика и Inspector | [Diagnostics](diagnostics.md) |
| Tool catalog, schemas, authoring | [Tool Library](tool-library.md) |
| Skills | [Skill Library](skills.md) |
| Durable events, replay, recovery | [Session events](session-events.md) |
| Trajectory queries and export | [Trajectory query](trajectory-query.md), [export](trajectory-export.md) |
| CAS lifecycle and GC | [CAS maintenance](cas-maintenance.md) |
| Artifacts and viewers | [Artifact Library](artifact-library.md) |
| Plugins: публикации, workspace-чат, scoped execution и vendor-каталог | [Plugins](plugins.md), [ADR-0015](decisions/ADR-0015-plugins.md) |
| Data Templates: independent app workspace, export, UI/core and host adapters | [Data Templates](data-templates.md), [ADR-0014](decisions/ADR-0014-data-templates.md) |
| VBA safety, packages, UserForms | [Mutation journal](vba-mutation-journal.md), [packages](vba-tool-packages.md), [UserForms](vba-userforms.md) |
| Desktop shell and Office target selection | [Desktop runtime](desktop-runtime.md) |
| UI architecture, аудит и ownership | [UI Architecture](ui/UI_ARCHITECTURE.md) |
| Экраны, навигация и состояния Office/Workspace | [UI Screen Map](ui/UI_SCREEN_MAP.md) |
| Общие tokens, компоненты и визуальные решения | [UI Design System](ui/UI_DESIGN_SYSTEM.md); согласования — [UI Decisions](ui/UI_DECISIONS.md) |
| Порядок UI-миграции, performance, rollback | [UI Migration Plan](ui/UI_MIGRATION_PLAN.md); [исходник v1.0](ui/reference/RNAssistant_UI_Architecture_and_Agent_Rules.md) сохранён как reference, не параллельный нормативный документ |
| Исторические UI/UX источники | [Прежний UI design](ui-design.md), [Demo P0–P3](ui/UI_LAYOUT_UX.md), [Prototype Package](ui-prototype/00_START_HERE.md); не параллельные нормативные планы |
| Проверка на Windows/Office и evidence | [Проверка](qualification.md), [release](operations/RELEASE_PROCESS.md) |
| Версии завершённых продуктовых изменений и выпуск | [Versioning](operations/VERSIONING.md) — patch/minor/major на завершённый срез; отдельные [release](operations/RELEASE_PROCESS.md) и [build evidence](operations/BUILD_EVIDENCE.md) |
| Targeted checks | [Harness guide](../tests/RNAssistant.Harness/README.md) |
| Сравнение моделей и безопасное переключение | [Правила разработки §9](development-rules.md#сравнение-моделей) |

## Планы, решения и история

Открывай только материалы выбранной области. Планы не запускают следующий этап
автоматически; ADR и phase reports объясняют решения и прежнее evidence, а текущий
контракт остаётся у владельца выше. Непроверенные Windows/Office/WebView2 сценарии
остаются открытыми независимо от локальных проверок.

- Режим работы и слои: [master plan](stabilization/STABILIZATION_MASTER_PLAN.md),
  [рабочий baseline / ADR-0011](decisions/ADR-0011-working-baseline-and-trunk-development.md),
  [SQLite-проекция / ADR-0012](decisions/ADR-0012-chat-sqlite-projection.md),
  [workspace-first / ADR-0013](decisions/ADR-0013-workspace-first-runtime.md).
  `host-fabric.md` и `local-automation-agent.md` описывают отложенные контуры.
- Хранение и контекст: [план](proposals/RNAssistant_Architecture_and_Implementation_Plan.md),
  [JSONL/CAS/index / ADR-0019](decisions/ADR-0019-bounded-jsonl-storage.md),
  [baseline и реализация](audits/storage-context-optimization.md).
- Resources: [история cutover](stabilization/resource-cutover/MASTER.md),
  [план Resource Context](stabilization/resource-context/PLAN.md),
  [аудит Wave 0](audits/resource-context-current-state.md).
- Общая подготовка Office/Workspace: [аудит owners](audits/office-workspace-preparation-audit.md),
  [сборка и стоимость шага](audits/office-workspace-preparation-audit.md#сборка-контекста-и-стоимость-шага--2026-10-30).
- Сборка model context: [целевой план](proposals/RNAssistant_V8_Context_Protocol_Target.md),
  [request composition](conversation-protocol.md#p3-request-composition--2026-10-30),
  [хронология и source bodies](conversation-protocol.md#p4-chronological-bodies--2026-10-30),
  [typed references revision 4](protocols/CONVERSATION_RESPONSE_V8.md#typed-references-revision-4),
  [аудит коротких ссылок](audits/model-reference-contract-review.md),
  [решение ADR-0018](decisions/ADR-0018-typed-model-references.md).
- Диагностика и Lab: [план](proposals/RNAssistant_V8_Diagnostics_Qualification_Plan.md),
  [baseline и действующие пробы](model-endpoint-compatibility.md#v8-diagnostics-first-slice--2026-10-30),
  [Lab UI](model-endpoint-compatibility.md#diagnostics-ui--2026-10-30),
  [исходное UIUX-предложение](proposals/RNAssistant_Diagnostics_UIUX_Plan.md).
  Условия real-model проверок принадлежат Model endpoint compatibility.
- Сбои и повторения: [разбор фото](stabilization/RISK_REGISTER.md#разбор-десяти-фотографий-чат-протокол-и-достоверность--2026-10-26),
  [очередь восстановления](stabilization/BACKLOG.md#восстановление-рабочих-сценариев-чата--2026-10-26),
  [карта блокировок](stabilization/RISK_REGISTER.md#карта-повторов-и-блокировок--аудит-2026-11-03),
  [finish correction](stabilization/RISK_REGISTER.md#finish-коррекция-и-ограничение-цикла--2026-11-04),
  [текущий admission contract](protocols/CONVERSATION_RESPONSE_V8.md#admission-persistence-and-presentation).
- Мультиагенты: [план runtime](proposals/multi-agent-runtime-plan.md).
  Текущий контракт адресов, доставки, ожиданий и участников — в таблице выше.
- Tools и skills: [план Tools](proposals/tools-architecture-plan.md),
  [аудит каталога](audits/tools-architecture-current-state.md),
  [инвентаризация 2026-10-29](audits/capability-inventory-2026-10-29.md),
  [аудит описаний](audits/skills-tools-description-review.md),
  [матрица результатов](audits/tool-result-presentation-matrix.md),
  [broad/compact пилот](audits/tool-profile-comparison.md).
- V8 architecture: [аудит](audits/v8-architecture-drift-2026-10-26.md),
  [карта зависимостей](audits/v8-ownership-dependency-matrix.md),
  [ограниченный план](proposals/v8-stabilization-execution-plan.md).
- История Conversation v8: [аудит](stabilization/conversation-v8/AUDIT.md),
  [ADR-0016](decisions/ADR-0016-conversation-v8-admission.md),
  [фаза 1](stabilization/conversation-v8/PHASE1.md),
  [фаза 2](stabilization/conversation-v8/PHASE2.md),
  [фаза 3](stabilization/conversation-v8/PHASE3.md),
  [фаза 4](stabilization/conversation-v8/PHASE4.md),
  [пилоты фазы 5](stabilization/conversation-v8/PHASE5_PILOT.md).
- Universal Browser: [статус](browser/06_PROGRESS.md),
  [исходная спецификация](browser/00_SOURCE_SPEC.md), [аудит](browser/01_AUDIT.md),
  [ADR](browser/02_ARCHITECTURE_ADR.md), [контракты](browser/03_CONTRACTS.md),
  [план](browser/04_MIGRATION_PLAN.md), [проверки](browser/05_TEST_MATRIX.md).
  Публикация HTML и recovery — [Artifact Library](artifact-library.md#implemented-shared-html-publication--2026-09-08).
- Shell и аннотации: [Unified Shell](proposals/RNAssistant_Unified_Shell_Plan.md),
  [Visual Annotator](proposals/RNAssistant_Visual_Annotator_Plan.md),
  [аудит](architecture/visual-annotator-audit.md),
  [ADR-0017](decisions/ADR-0017-unified-shell-and-visual-annotations.md).
  Текущее поведение принадлежит canonical UI docs, Plugins, Resource Fabric и Session events.

## Куда писать

| Информация | Место |
|---|---|
| Точное текущее поведение одной области | Владеющий canonical document выше |
| Общее инженерное правило и workflow | `development-rules.md` |
| Рабочий baseline, текущий приоритет и открытое evidence | Начало `stabilization/PROGRESS.md` |
| Риск, который уже влияет на текущую систему | `stabilization/RISK_REGISTER.md` |
| Временный adapter, его consumers и removal gate | `stabilization/MIGRATION_MAP.md` |
| Отложенная ограниченная работа или product decision | `stabilization/BACKLOG.md` |
| Причина архитектурного решения | Новый ADR в `decisions/` |
| Команды и evidence завершённой миграции или release qualification | Phase/WQ report в `stabilization/` |
| Установка и пользовательский обзор | Корневой `README.md` |

## Что не создавать

- Второй `roadmap`, `audit`, `followups`, `notes` или `cleanup` файл, если запись
  помещается в canonical doc, `PROGRESS`, `RISK_REGISTER`, `MIGRATION_MAP`,
  `BACKLOG` или ADR.
- Копию protocol/runtime-контракта в README, progress или phase report.
- Phase report для маленького изменения, которому достаточно diff, tests и
  короткой записи в progress.
- Общий список «переписать/почистить всё» без владельца, следующего изменения,
  удаляемой зависимости и проверки.

Новый долгоживущий документ допустим только с одним владельцем, ссылкой из этой
карты и явным условием, когда он перестанет быть актуальным. При смене владельца
сначала обновляются ссылки и consumers, затем удаляется старый документ.
