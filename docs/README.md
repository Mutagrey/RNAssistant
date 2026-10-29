# Карта документации RNAssistant

Этот файл — единственная точка входа в документацию. Не загружай весь `docs/`:
сначала выбери владельца области по таблице ниже, затем читай только нужный раздел.

## Минимальный контекст задачи

1. Прочитай краткий текущий статус в начале [PROGRESS](stabilization/PROGRESS.md),
   если задача зависит от приоритета или открытого риска.
2. По таблице ниже выбери canonical document области и нужный раздел в нём.
3. [Старый master plan](stabilization/STABILIZATION_MASTER_PLAN.md) и phase reports
   читай как историю переноса архитектуры, только когда нужна причина решения или
   точная прежняя проверка. Они не задают очередь обычных изменений.

[Правила разработки](development-rules.md) нужны для cross-cutting responsibility,
testing или migration; [архитектура](architecture.md) — для изменения layers,
owners или dependencies. Не читай оба документа автоматически для каждой задачи.
Старые phase reports и ADR не являются текущим контрактом.

[Resource Fabric](resource-fabric.md) — действующий контракт ресурсов.
[Resource MASTER](stabilization/resource-cutover/MASTER.md) и три спецификации
direct cutover объясняют порядок уже выполненной миграции; они не запускают её
повторно. Текущий режим работы и веток задан в [правилах разработки](development-rules.md).
Причина перехода к рабочему baseline зафиксирована в
[ADR-0011](decisions/ADR-0011-working-baseline-and-trunk-development.md).

[Разбор сбоев по десяти фото от 2026-10-26](stabilization/RISK_REGISTER.md#разбор-десяти-фотографий-чат-протокол-и-достоверность--2026-10-26)
содержит воспроизведения и границы причинности; [очередь восстановления](stabilization/BACKLOG.md#восстановление-рабочих-сценариев-чата--2026-10-26)
задаёт вертикальные срезы, владельцев и приёмку. Текущий приоритет — в начале PROGRESS;
это не повторный общий cutover и не закрытая Windows/Office qualification.

[Аудит оставшихся различий Office/workspace](audits/office-workspace-preparation-audit.md)
разделяет доменные различия и дубли управления разговором; задаёт границу общего
Application-владельца и срез с переключением обоих потребителей. Исторический аудит
подготовки контекста и профилей tools сохранён ниже в том же документе.

[Аудит коротких ссылок модели](audits/model-reference-contract-review.md)
разделяет runtime IDs, finish references, semantic targets и Inspector aliases;
содержит исходный аудит. Согласованное решение и реализация —
[ADR-0018: типизированные ссылки](decisions/ADR-0018-typed-model-references.md) и canonical v8 contract.

[Парное сравнение broad/compact профилей](audits/tool-profile-comparison.md)
фиксирует реальные траектории, размеры запросов и ограничения пилота.

[Текущий подсчёт tools и skills](audits/capability-inventory-2026-10-29.md):
91 → 71 встроенный tool ID, 16 skills, 10 начальных Core tools; сравнение по hosts,
исправленная чистка инструкций и следующие кандидаты на сокращение.
[Предыдущий аудит описаний](audits/skills-tools-description-review.md) и
[матрица результатов tools](audits/tool-result-presentation-matrix.md) сохраняют
исторический каталог из 91 ID, model/storage/UI paths и прежние gaps.

Согласованный [план Tools](proposals/tools-architecture-plan.md) и
[аудит встроенного каталога](audits/tools-architecture-current-state.md) описывают
общий текстовый поиск workspace/Office и сохранение runtime/v8. Текущее поведение
по-прежнему принадлежит Resource Fabric и Tool Library.

По запросу пользователя выполнен [V8 architecture audit](audits/v8-architecture-drift-2026-10-26.md),
с [картой владельцев](audits/v8-ownership-dependency-matrix.md) и
[ограниченным планом реализации](proposals/v8-stabilization-execution-plan.md).
Owner — Core/Application; после принятия diff это исторический срез, текущие
контракты остаются в canonical docs, следующие решения — в BACKLOG.

[Text mutations](text-mutations.md) задаёт единые core.write/patch/replace, пакетные строковые hunks с текстовыми ориентирами,
маршрутизацию по owner, имена tools/skills и встроенное сравнение в шаге.

## Канонические документы

| Область | Владелец текущего контракта |
|---|---|
| Model loop, modes, tool result/effect | [Conversation protocol](conversation-protocol.md) |
| Wire JSON v8 | [Conversation response v8](protocols/CONVERSATION_RESPONSE_V8.md); [v7](protocols/CONVERSATION_RESPONSE_V7.md) — исторический формат |
| Совместимость model endpoint и проверка ролей/формата | [Model endpoint compatibility](model-endpoint-compatibility.md) |
| Resources, URI, providers, ingestion | [Resource Fabric](resource-fabric.md) |
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
| UI design, interactive prototype and migration ownership | [UI design](ui-design.md) — product presentation, not runtime contract |
| Проверка на Windows/Office и evidence | [Проверка](qualification.md), [release](operations/RELEASE_PROCESS.md) |
| Versioning and release | [Versioning](operations/VERSIONING.md), [release](operations/RELEASE_PROCESS.md), [build evidence](operations/BUILD_EVIDENCE.md) |
| Targeted checks | [Harness guide](../tests/RNAssistant.Harness/README.md) |
| Сравнение моделей и безопасное переключение | [Правила разработки §9](development-rules.md#сравнение-моделей) |

`host-fabric.md` и `local-automation-agent.md` описывают отложенные контуры, а не
действующий stable-core scope.

Причина производной SQLite-проекции чатов —
[ADR-0012](decisions/ADR-0012-chat-sqlite-projection.md); текущий контракт чтения
и восстановления остаётся в [Session events](session-events.md).

Workspace-first решение и текущий срез CLI зафиксированы в
[ADR-0013](decisions/ADR-0013-workspace-first-runtime.md); состояние этапов — в
[PROGRESS](stabilization/PROGRESS.md). Граница общего Gateway/compiler для CLI
описана в [Resource Fabric](resource-fabric.md).

По отдельному запросу пользователя сохранены
[предложение Resource Context](stabilization/resource-context/PLAN.md) и
[аудит Wave 0](audits/resource-context-current-state.md) от 2026-10-22.
Owner — model-context/LLM diagnostics. Это проверяемый срез и ограниченный план
следующей правки, не новый runtime-контракт или повторный cutover. После закрытия
этапов документы остаются историей; текущие поведение, статус и риски принадлежат
canonical docs и существующим реестрам.

По спецификации пользователя начат [аудит Conversation v8.1](stabilization/conversation-v8/AUDIT.md)
и подготовлен [ADR-0016](decisions/ADR-0016-conversation-v8-admission.md).
Owner — Core ModelProtocol / AgentKernel. [Фаза 1](stabilization/conversation-v8/PHASE1.md)
проверяет codec, [фаза 2](stabilization/conversation-v8/PHASE2.md) —
kernel и admission по журналу, [кандидат фазы 3](stabilization/conversation-v8/PHASE3.md) —
prompts, adapters и CLI, [срез фазы 4](stabilization/conversation-v8/PHASE4.md) —
format-8 persistence/UI и восстановление в отдельном worktree.
[Изолированные сравнения фазы 5](stabilization/conversation-v8/PHASE5_PILOT.md)
сохраняют точные HTTP-запросы, результаты задач, абляции и открытые условия квалификации.
Код и тесты объединены в `main`; v8 стал текущим контрактом. Полный Harness
после объединения не запускался, а квалификация Windows/Office/WebView2 и
формальная оценка реальных моделей остаются открытыми. Аудит и phase reports
теперь исторические; текущие правила — в canonical protocol docs.

По сохранённым пользовательским планам
[Unified Shell](proposals/RNAssistant_Unified_Shell_Plan.md) и
[Visual Annotator](proposals/RNAssistant_Visual_Annotator_Plan.md) подготовлены
[аудит Unified Shell / Visual Annotator](architecture/visual-annotator-audit.md)
и [RFC / ADR-0017](decisions/ADR-0017-unified-shell-and-visual-annotations.md).
Owner — application/chat UI. Фаза 0 завершена, RFC принят; срез 1a общего
черновика и единый ChatView controller для Office, проекта и плагина в 1b
реализованы host-neutral. HTML-preview получил инспектор и подготовку выбранного
элемента как ресурсного чипа общего Office-чата. Workspace backend принимает
привязанный draft и читает его через общий `core.find/read/search`; plugin-frame
annotation использует тот же Inspector, защищённый черновик и composer.
Общая инструкция и статическая правка исходника через существующий patch проверены
host-neutral; автоматическое сопоставление DOM с исходником и Windows/WebView2
qualification ещё открыты.
Общий экран разработки плагина теперь использует дерево проектов и чатов слева,
тот же ChatView в центре и предпросмотр справа; отправка и история идут через
контроллер проектного чата, отдельные обработчики плагина удалены. Текущий HTML
workspace Office-чата теперь можно открыть
справа от того же ChatView без второго iframe. В нём работает редактор
аннотаций с номерами и списком общего composer; незавершённый ввод
восстанавливается из защищённого черновика. Отправленные номера видны в истории
сообщения и на странице совпадающего HTML revision. Разделы справа выбираются в
заголовке: для HTML доступны предпросмотр и четыре раздела чата, для плагина —
предпросмотр и те же четыре раздела чата. Общий маршрут для произвольных ресурсов и история
браузера ещё не реализованы.
Оба существующих preview получили общую двухуровневую шапку с выбором раздела
и строкой ресурса/загрузки/обновления;
их frame owners и права доступа остаются у HTML и плагина.
После реализации документы остаются историей решения; текущие контракты переходят в
UI design, Plugins, Resource Fabric и Session events, статус — в PROGRESS.

[Предложение диагностики](proposals/RNAssistant_Diagnostics_UIUX_Plan.md) сохранено
по запросу пользователя. После уточнения по фотографиям основой выбрана
существующая правая панель с четырьмя разделами; отдельный макет отклонён.
Owner и текущие правила — [UI design: диагностика](ui-design.md#диагностика-в-существующей-правой-панели--2026-10-23).
Предложение остаётся исходным материалом до переноса согласованных пунктов
в canonical UI document, а не вторым runtime-контрактом.

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
