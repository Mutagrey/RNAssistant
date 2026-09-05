# Карта документации RNAssistant

Этот файл — единственная точка входа в документацию. Не загружай весь `docs/`:
сначала выбери владельца области по таблице ниже, затем читай только нужный раздел.

## Минимальный контекст задачи

1. Прочитай только начало [PROGRESS](stabilization/PROGRESS.md): текущую работу,
   следующий шаг, gates и required context.
2. По таблице ниже выбери один canonical document области и нужный раздел в нём.
3. Раздел [master plan](stabilization/STABILIZATION_MASTER_PLAN.md) открывай только
   когда изменение затрагивает scope, порядок или gate текущей стабилизации.

[Правила разработки](development-rules.md) нужны для cross-cutting responsibility,
testing или migration; [архитектура](architecture.md) — для изменения layers,
owners или dependencies. Не читай оба документа автоматически для каждой задачи.
Старые phase reports и ADR не являются текущим контрактом.

Для текущего единого Resource direct cutover сначала прочитай
[Resource MASTER](stabilization/resource-cutover/MASTER.md), затем его три
нормативных документа в заданном порядке. [Resource Fabric](resource-fabric.md)
содержит карту текущих владельцев и незакрытых consumers, а не отдельную реализацию.

## Канонические документы

| Область | Владелец текущего контракта |
|---|---|
| Model loop, modes, tool result/effect | [Conversation protocol](conversation-protocol.md) |
| Wire JSON v5 | [Conversation response v5](protocols/CONVERSATION_RESPONSE_V5.md) |
| Resources, URI, providers, ingestion | [Resource Fabric](resource-fabric.md) |
| Tool catalog, schemas, authoring | [Tool Library](tool-library.md) |
| Skills | [Skill Library](skills.md) |
| Durable events, replay, recovery | [Session events](session-events.md) |
| Trajectory queries and export | [Trajectory query](trajectory-query.md), [export](trajectory-export.md) |
| CAS lifecycle and GC | [CAS maintenance](cas-maintenance.md) |
| Artifacts and viewers | [Artifact Library](artifact-library.md) |
| VBA safety, packages, UserForms | [Mutation journal](vba-mutation-journal.md), [packages](vba-tool-packages.md), [UserForms](vba-userforms.md) |
| Desktop shell and Office target selection | [Desktop runtime](desktop-runtime.md) |
| Qualification and evidence | [Qualification](qualification.md) |
| Versioning and release | [Versioning](operations/VERSIONING.md), [release](operations/RELEASE_PROCESS.md), [build evidence](operations/BUILD_EVIDENCE.md) |
| Targeted checks | [Harness guide](../tests/RNAssistant.Harness/README.md) |

`host-fabric.md` и `local-automation-agent.md` описывают отложенные контуры, а не
действующий stable-core scope.

## Куда писать

| Информация | Место |
|---|---|
| Точное текущее поведение одной области | Владеющий canonical document выше |
| Общее инженерное правило | `development-rules.md` |
| Текущий подэтап, следующий шаг, открытый gate | Начало `stabilization/PROGRESS.md` |
| Риск, который уже влияет на текущую систему | `stabilization/RISK_REGISTER.md` |
| Временный adapter, его consumers и removal gate | `stabilization/MIGRATION_MAP.md` |
| Отложенная ограниченная работа или product decision | `stabilization/BACKLOG.md` |
| Причина архитектурного решения | Новый ADR в `decisions/` |
| Команды и evidence сложного завершённого этапа | Phase/WQ report в `stabilization/` |
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
