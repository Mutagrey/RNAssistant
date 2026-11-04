# Мультиагентный runtime: последовательная реализация

Дата: 2026-11-04. Scope явно открыт пользователем. Owner — Application/Conversations;
Core владеет protocol/kernel/events/policy, Web — представлением и Inspector,
Office/Workspace — binding к существующим execution owners.
Это план следующих изменений, не объявление всей системы реализованной.
Текущее поведение: [Conversation](../conversation-protocol.md#межагентное-общение--2026-11-03),
[Tools](../tool-library.md#agent-communication-tools--2026-11-03),
[Events](../session-events.md#agent-communication-journal--2026-11-03).

## Решения с наибольшими последствиями

1. Участник — обычный ChatSession, последовательные Run принадлежат ему.
   Отдельной сущности исполняемого subagent/runtime нет. Creator — provenance,
   не обязательный координатор. Peer communication и произвольные роли сохраняются.
2. Один незавершённый Run на thread. Peer input включается после model request/tool
   read-back, перед следующим действием; незапущенные calls завершаются NotDispatched.
   Параллельные независимые Run одного thread отклонены: нарушают порядок контекста
   и принятую policy. Разные threads исполняются параллельно через прежних owners.
3. Durable wait должен продолжать тот же Run. Это изменение kernel continuation,
   а не обёртка polling или второй чат. В 2b.2 реализован сохраняемый suspension
   contract для input/replies. Живой Run сохраняет exact chat lease; рабочий поток и
   document gate свободны. Глобальные execution slots относятся к этапу 3.
4. Chat events/CAS остаются источником истины. Нет второго mailbox/task store.
   Новые projections — удаляемые индексы. Exactly-once неизвестных side effects
   не обещается. Prepared без доказанного результата не переисполняется.
5. Discovery и доставка ограничены exact document/workspace; известный адрес не
   предоставляет доступ к другой области. Истории автоматически не объединяются.
   После restart продолжение требует явного действия пользователя.
6. Collaboration — необязательная именованная many-to-many группа чатов: одна сущность
   для организации дерева и выбора участников в Inspector. Thread в нескольких группах
   сохраняет один общий контекст. Membership не запускает Run, не меняет права/бюджет
   и не превращает сообщения участников в user instructions. Общие лимиты выполнения
   назначаются отдельно; перегруппировка не обнуляет causal-chain quotas.
7. Поручение — опциональное коррелированное обязательство, не обязательный Task List.
   Ответ на вопрос, завершение Run и завершение поручения — разные факты.

## Постоянный Agent или переиспользуемая роль

Уточнение пользователя 2026-11-04. Выбор: сейчас переиспользовать роли через Skill
Library; не вводить постоянный Agent ради копирования инструкций. Это разные оси:
профиль описывает поведение, thread изолирует разговор, постоянная идентичность
объединяет несколько разговоров одного участника. Профиль и постоянный Agent могут
появиться вместе позднее, но ни один не требует отдельного execution runtime.

| Модель | Преимущество | Дополнительная ответственность |
|---|---|---|
| Thread + выбранные skills | Свободная совместная работа, изолированный контекст, роли меняются сообщениями | Уже существующие Run/Inbox/policy |
| Профиль роли → новые threads | Тиражирование удачной роли, набора skills и defaults | Выбор точных ревизий и проверка перед первым model request; профиль не содержит историю, approvals или live state |
| Постоянный Agent → несколько threads | Стабильный адрес/контакт, агентная память, общие настройки и бюджет нескольких диалогов | Маршрутизация сообщения в конкретный thread/новый диалог, права доступа к общей памяти, конкурентные обновления, архивирование/удаление и области видимости |

В коде уже есть `skills.upsert/delete`, clone в Library, publication revisions/CAS,
`core.skills.load` и `SkillTaskService.Compile`. `ChatRunRecord.SelectedSkills`
переживает новые Run одного thread; каждый запрос перепроверяет pins и policy.
Подробности — [Skill Library](../skills.md#publication-and-authoring). Поэтому роль
«рецензент VBA» можно сохранять как обычный skill, загружать в разные чаты и улучшать
через существующий revision-guarded authoring. Skills не хранят личность или память
экземпляра и не дают новых прав инструментам.

Будущий UX: «Сохранить роль», «Создать чат с ролью» в существующей Library/Chat View.
Один skill достаточен для первой роли; профиль из нескольких skills — тонкая настройка
над текущими selections, не новый каталог исполняемых агентов. Перед автоматической
загрузкой при spawn нужен общий bootstrap, проверяющий package/tool pins, policy и
бюджет контекста до первого запроса; одного текстового обещания initial_message мало.
Сейчас такого bootstrap/UX ещё нет. Динамические факты и приватная история не попадают
в сохраняемую роль автоматически; обмен памятью остаётся явным scoped resource access.

Постоянный Agent вводить при конкретном сценарии: «обращаться к одному аналитику из
разных независимых дел, сохраняя его общий контакт/память». Тогда добавляется optional
связь Agent–Thread и отдельный адрес Agent; нынешний публичный `agent_id` сохраняет
значение thread-address и не переопределяется молча. Исполнение остаётся Thread + Run,
истории не сливаются, в одном thread по-прежнему один незавершённый Run. Общую память
не следует делать обязательной даже для постоянной идентичности.

## Сообщения в контексте и UI — Proposed, 2026-11-04

Уточнение пользователя: насыщенный макет отклонён. Ленте достаточно имени/цвета/ID
отправителя с текущим переходом к его чату; reply link опционален. Нельзя переносить
в каждую строку всю диагностику. UI-контракт — в
[Migration Plan](../ui/UI_MIGRATION_PLAN.md#представление-совместной-работы--proposed-2026-11-04).

### Что действительно реализовано

`ChatMessage` хранит `Role="user"`, `Content=input.Text` и отдельно typed
`AgentCommunication` (Sender, MessageId, ReplyTo, ExpectReply и runtime metadata).
Office `ApplySteeredInputAsync` и Workspace input owner создают одинаковую запись.
`ConversationInputMessage.ModelText` при сборке запроса добавляет к тексту заголовок
с sender agent_id/title, message_id, положительным expect_reply=true и optional reply_to.
С 2026-11-04 scope и длинная инструкция в каждом заголовке удалены; общие правила
формируются один раз в RUNTIME_CONTEXT с учётом доступных tools и наличия peer history.
Исходный Content не переписывается. `LlmMessageBuilder` отправляет обычное
`{role:"user", content:"<заголовок>\n<текст>"}`; дополнительного JSON agent envelope нет.
Текст, отправленный B через agents.send, становится peer input в A; принятые V8 решения
B остаются assistant-историей B, не копируются в A целиком. Ответ B не пересылается
автоматически только потому, что B выполнил finish.

| Содержимое production context | API роль и внутреннее представление content |
|---|---|
| Основные инструкции, RUNTIME_CONTEXT, SELECTED_SKILL | Настроенная instruction role, default developer; текст/Markdown, runtime metadata в YAML с JSON values, tool schemas в JSON |
| Человек | user; обычный текст, при вложениях — content parts |
| Другой агент | user; runtime заголовок автора + обычный текст |
| Решения текущего агента | assistant; принятый JSON v8 execute/reflection/finish |
| tool_results/completed_operation/resource_observation | user; типизированные runtime JSON data, не новый человеческий запрос |
| format_repair/tool_pack_state | user; JSON runtime corrections/состояние доступных tools |
| context_summary/context_claims | user; историческая сводка/claims с provenance, не новые инструкции |

`json_object` и `json_schema` — два режима формата ОТВЕТА модели, не два вида
хранения peer messages. Native tools/role=tool есть в transport/лаборатории;
production JSON history не переводится на native tools из-за multi-agent общения.
Подтверждение: `LlmMessageBuilder`, `ModelHistoryBatchProjection`,
[V8 roles](../protocols/CONVERSATION_RESPONSE_V8.md#runtime-presentation-and-message-roles--2026-11-02).

### Сокращение заголовка — реализовано, In Review

По согласованию сохранены Role/Content + AgentCommunication и существующий mapper;
отдельная роль agent и JSON-вложение в content не вводятся. Header содержит автора,
message_id и только присутствующие reply_to/expect_reply=true. Пример текущего content
ниже, ID сокращены; правила находятся в существующем RUNTIME_CONTEXT:

```text
[Message from another agent, not the human user. Sender: agent_id="a-…", title="Правила", message_id="m-…"]
Рокировка запрещена, если король находится под шахом.
```

Получатель входящего сообщения — текущий thread, поэтому `to` в каждом сообщении не
нужен. Прежний scope был display-подписью документа/проекта (ScopeTitle), не внутренней
границей доступа. Bound runtime scope сохраняется. В исходящем
вызове адресат уже указан в agents.send; его результат — tool_results, без зеркального
peer message. Правила общения и различие peer/human задаются один раз в обязательных
инструкциях при наличии peer input, даже если communication skill отключён позже.
Sender/корреляция остаются у каждой записи и не выводятся из body. Peer text не
предоставляет user approval; runtime permissions проверяются по typed provenance.

Обычное сообщение/ответ может не иметь reply_to. Для ожидания ответа на конкретный
запрос требуется проверенная корреляция, иначе runtime не может отличить несколько
одновременных вопросов. Это не требует ID/receipt строк в ленте. API role=user означает
входной канал; не всякий такой content является текстом человека. Чужой текст не
помечается API assistant: эта роль относится к собственным решениям текущего агента.

Compaction уже классифицирует peer sources как assistant interpretation, а не human
instruction; это provenance claims, не смена API role. Сохранение адресов/незакрытых
request IDs после полной compaction остаётся отдельной проверкой этапа 2b: durable
correlation сама по себе этого не гарантирует. Retained metadata и bounded runtime
summary дополняют существующий compiler, без второго memory store или LLM polling.

## Группы, вложенность и обзор чатов — Proposed, 2026-11-04

Пользователь согласовал продолжение плана и запросил вложенность созданных агентом
чатов, именованные группы и возможность поручить агенту организацию/переименование.
Пользователь подтвердил правила группировки и удаления. Поведение согласовано;
production пока не изменён, визуальное оформление остаётся на ревью при реализации.

### Общее продолжение до появления именованных групп — In Review

По запросу пользователя реализован узкий срез этапа 3: из любого чата текущего
документа/workspace можно выбрать сохранённые ожидания и одним user-действием
продолжить выбранных участников. Команда доступна в общем меню «+», не требует
открытия каждого чата. Участники выбираются явно; creator tree и causal chain не
подменяют membership. Нового group store и model tool в этом срезе нет.

Owner — `AgentCommunicationService.Recovery`, существующие Inbox/leases/queue ports.
`getRecoverableAgentWaits` читает до 20 чатов за страницу без LLM; `resumeAgentWaits`
принимает до 32 exact session/run/revision целей. Каждый участник получает отдельный
receipt, stale/foreign/busy/binding refusal не блокирует других. Все разрешения
сохраняются до первого старта, но атомарность между чатами не обещается. Requested
не означает completed; ambiguous failure не replay. Stop до binding сильнее
отложенного старта. Run/call/deadline/бюджеты остаются прежними.

После реализации Collaboration её membership заполняет тот же явный выбор; удаление
группы и добавление участника не дают автоматического разрешения resume. Inspector
сможет использовать эту Application-команду без второго control owner. Холодный
cancel, agent pause/resume tools и execution slots остаются в этапе 3.
Проверки: exact selection, partial failures, stale Stop/retry, bounded catalog,
два реально сохранённых Workspace wait → restart → одна команда → оба прежних Run.
UI: общий ActionMenu, пагинация, partial/unknown result, клавиатура/zoom/dispose.

### Текущее состояние и выбор

`app-chat-state.chatDocumentTreeNode` показывает Document → chats,
`app-project-chats` — Workspace → chats. Произвольных тематических групп пока нет;
Dockview groups относятся к размещению вкладок и не являются группами чатов.
`AgentThreadCreation.Creator` уже сохраняется в `AgentCommunicationService.Spawn`.
Для вложенности используется эта связь; `ChatSession.ParentSessionId` относится
к fork истории и не переиспользуется. Headers/summary DTO пока не передают creator.

| Понятие | Назначение | Выбор |
|---|---|---|
| Создатель → созданный чат | Происхождение участника | Существующая provenance; вложенность в дереве, без управляющей иерархии |
| Группа / Collaboration | Именованный набор чатов | Одна optional сущность для дерева и Inspector; можно добавить ранее независимые чаты |
| Поручение | Кто принял обязательство и чем оно завершилось | Отдельный optional contract этапа 4b; группа не требует поручений |

Один чат может входить в несколько групп без копирования истории/вкладки. Внутри
группы дочерний чат вложен под создателем, только если оба представлены в ней;
иначе он находится на верхнем уровне. Чаты без группы остаются под документом/проектом.
Удаление группы снимает membership, не удаляет чаты. Удаление создателя не удаляет
дочерние чаты. Membership не наследуется во все группы создателя автоматически:
для spawn в конкретную группу нужен явный optional group_id с runtime проверкой.
Первые группы ограничены текущим документом/workspace, как и discovery; глобальная
организация разных документов не подразумевает междокументного доступа.

### Автогруппировка и удаление

Уточнение пользователя: один чат может быть виден в нескольких группах, поэтому
изменение membership и удаление самого чата должны быть разными командами дерева.
Каталог обходится по уникальным ChatId, а не по строкам дерева. Membership уникален
по `(group_id, chat_id)`; повторный add не создаёт вторую строку в той же группе.
Организатор учитывает существующие группы. По умолчанию для неразобранного чата
выбирается одна группа; дополнительное включение в другие группы должно быть
осмысленным выбором, а не автоматическим добавлением по каждому ключевому слову.
Существующие memberships не снимаются без поручения перегруппировать чаты.
Поручение «сгруппируй» не включает удаление чатов или их истории.

| Действие | Результат |
|---|---|
| Убрать из группы | Удаляется только указанная связь; другие группы, история, Run и вкладка сохраняются. После последней связи чат виден под документом/проектом |
| Удалить группу | Группа логически удалена, её memberships больше не действуют. Чаты сохраняются в других группах либо под документом/проектом; Run/wait/поручения продолжаются |
| Удалить чат везде | Existing deletion owner удаляет сам чат; он исчезает из всех групп. Сохраняются текущие проверки занятого Run/несохранённых изменений и подтверждение удаления истории; дочерние чаты не удаляются |

Удаление группы доступно и через `agents.group`, и в её меню дерева. Отдельного
действия «удалить группу вместе с чатами» в этом срезе нет. Удаление создателя группы
не удаляет группу: её lifetime не должен зависеть от хранения чата-создателя.
Исторические сообщения/события других чатов не переписываются из-за удаления цели.
Projection исключает удалённые чаты/группы даже после restart/rebuild. Add в уже
удалённую группу или добавление удалённого чата отклоняется; retries не воскрешают
сущности. Гонка add/delete разрешается общим domain owner по актуальной ревизии.
Незавершённое ожидание удалённого адресата получает явную недоступность адресата,
а удаление только группы не удовлетворяет и не отменяет ожидания участников.

`agents.group` планируется для list/create/rename/add/remove/delete группы;
delete относится только к группе. Вложенные папки групп пока не нужны. `agents.list`
получит фильтр группы и bounded pagination для обхода более 50 чатов. Для rename
расширяется существующий `chat.rename(title, agent_id?)`: без адреса — текущий чат.
Сейчас handler меняет только bound session. Целевую запись должен выполнять общий
Application owner с проверкой scope и наблюдавшейся ревизии metadata; нельзя сохранять
полную устаревшую session поверх чужого активного Run. Конфликт возвращается явно.

### Как организатор узнаёт содержание

Сейчас `agents.list` возвращает адрес/название/runtime status, не смысловую сводку.
`ChatSessionSummary` — UI metadata, несмотря на название. После успешной compaction
Office может публиковать Shared context через `ContextCompactionRuntime` и
`DocumentArtifactStore.PublishContext`; это исторические claims с provenance,
не гарантированное описание последней темы. Workspace создаёт compaction runtime
без sharedArtifacts, поэтому общего опубликованного обзора для всех чатов пока нет.

Для поручения «разбери чаты» выбран следующий путь:

1. Читать bounded каталог и доступные опубликованные сводки. Не загружать все истории.
2. Если сведений мало — по запросу читать краткий обзор выбранного чата через
   существующие `core.find/read` и Resource Registry. Это новое read-only представление:
   название, доступная сводка либо ограниченные выдержки запросов/ответов, источник,
   свежесть и признак усечения. Оно не исполняет старый чат и не требует отдельного
   вызова LLM. Exact revisions/locators остаются runtime-owned. Из несовместимого
   stream читаются только поддерживаемые сведения; отсутствующее не выдумывается.
3. `agents.send` использовать для содержательных уточнений у доступных участников,
   не для обязательного опроса каждого чата. Stop/restart pause не снимать. При
   недостатке данных оставлять чат неразобранным, не выдавать догадку за сводку.

Обзор — явно предоставляемый ресурс в разрешённой области для пользовательского
поручения, не право на полную историю по одному agent_id. Автоматического объединения
контекстов, чтения скрытых инструкций/approvals и фонового LLM summarizer нет.
Общий Application projection обслуживает Office/Workspace; кеш производный,
инвалидируется по исходной ревизии, не становится новым хранилищем памяти.

### Старые чаты и актуальные инструменты

Для совместимого сохранённого чата новые Run получают текущий каталог. `CallableToolProfile.CoreIds`
уже включает list/send/wait/spawn, если policy разрешает их публикацию. Office composer
и Workspace CompileCurrent собирают RUNTIME_CONTEXT при подготовке запроса; правила
peer input не застывают в старой истории. Проверять body сообщения и специально
подкладывать reply schema не нужно. Остальные инструменты доступны через обычные
`core.tools.search/read`; восстановление optional admissions проверяет exact revisions.
Это не hot replacement принятого вызова в исполняющемся Run.

`RequireCurrentSession` отклоняет несовместимый формат как read-only history.
Новый peer message не мигрирует такой чат и не снимает пользовательскую паузу/запрет
общения. Обзор не требует запуска, поэтому каталог можно организовать и при этих
ограничениях; изменение metadata несовместимого stream не разрешается скрытым fallback.

Интеграция: existing session events + disposable header projection, typed catalog DTO,
Application group/overview/metadata owner, Resource Registry и общий NavigationTree.
Точный владелец group journal/revision фиксируется перед этапом 4a; второй durable
catalog и dual-write memberships запрещены. Требуемые проверки: reopen/rebuild,
чат в двух группах, отсутствующий creator, независимость budgets/Run от membership,
rename занятого чата с revision conflict, каталог >50, отсутствующая/устаревшая сводка,
организация без запуска получателей и без дополнительных LLM calls на наблюдение.

## Инструменты

| Tool | Назначение | Текущий статус |
|---|---|---|
| `agents.list(query?, agent_id?)` | Saved participants и runtime status своей области | Реализовано, v3 |
| `agents.spawn(title, initial_message?, expect_reply?)` | Создание ordinary thread, отдельно optional intake/request | Реализовано, v2 |
| `agents.send(agent_id, text, expect_reply?, reply_to?)` | Durable intake, корреляция и явные ответы | Реализовано, v4 |
| `agents.wait(timeout_seconds?, requests?, mode?)` | Следующее сообщение или any/all ответов на свои запросы | Durable v4, 2b.2 In Review; длительность 1–10 секунд сохранена |
| `agents.pause` | Остановка продвижения на безопасной границе | Этап 3 |
| `agents.resume` | Продолжение сохранённого ожидания/agent pause с проверкой полномочий | Этап 3 |
| `agents.cancel` | Отмена выполнения с отдельным подтверждением terminal state | Этап 3 |
| `agents.group` | Именованные группы чатов: list/create/rename/add/remove/delete | Этап 4a |
| `agents.resolve` | Явное разрешение опционального поручения с результатом | Этап 4b |
| `chat.rename(title, agent_id?)` | Переименовать текущий или выбранный доступный чат | Сейчас только title / текущий чат; расширение в 4a |
| `core.find/read` | Читать опубликованные ресурсы и краткий обзор выбранного чата | Ресурсы реализованы; общий chat overview — 4a |

Нереализованные tools не добавляются в registry/skill. Точные текущие JSON Schema
межагентных tools находятся в `AgentCommunicationToolCatalog.GetTools`. Следующие версии фиксируются
вместе с их runtime semantics, до переключения consumers.

В send реализованы optional expect_reply/reply_to: runtime выдаёт message_id;
request использует ID исходного сообщения, отдельная выдаваемая сущность request_id
не нужна. В Matches поле request_id обозначает ID исходного сообщения, message_id —
ID ответа. Ответ ссылается на полученное сообщение и проверенного отправителя;
его цепочка не зависит от более поздних обсуждений. Корреляция переживает restart,
сохранённый wait продолжается явно в том же Run (2b.2). Control tools будут принимать semantic agent_id; runtime
закрепляет exact Run при admission и отклоняет замену Run перед dispatch.
User Stop/restart pause не снимается peer-командой без пользовательского разрешения.

## Этапы и интеграция

| Этап | Изменения и зависимости | Критерий готовности и главный риск |
|---|---|---|
| 1. Каталог/spawn | `AgentConversationScope`, `AgentCommunicationService.Spawn`; `ChatSessionService.PrepareAgentChat`; Workspace scope binding; catalog/skill; удаление presence bridge/leases | Работа без views, сохранение provenance, общий recursive budget, truthful partial receipt. Риск: binding/права при закрытом target. Локальные проверки пройдены; Office/controller evidence отдельно |
| 2a. Корреляция | Existing Prepared/Accepted + input provenance, source recipient verification и Inbox matcher. Реализовано после 1 | Ранние/применённые ответы учитываются, any/all различаются, нет чужих match/потери сигнала, unrelated/human/policy input не выдаётся за ответ. 22 целевые проверки проходят |
| 2b.1. Повтор ожидания | Реализован typed timeout progress через существующий terminal result; общий kernel не парсит имя tool/body. Зависит от 2a | Четвёртый одинаковый timeout исполняется; JSON/ChatStore replay сохраняет различие, остальные repeat guards и run limits действуют. 23 agent communication + 1 kernel wait + 15 tool runtime checks проходят; Windows отдельно |
| 2b.2. Durable wait | Kernel continuation, `ConversationRunCoordinator`, `AgentRunContinuation`, history restoration и exact tool admission. Зависит от 2a/2b.1; реализовано, In Review | Waiting + PendingToolWait в прежних events/CAS; явный resume того же call/Run, абсолютный deadline, однократный учёт. Exact chat lease остаётся за живым Run, рабочий поток и document gate свободны. Shutdown сохраняет wait, Stop отменяет живой wait. Непроверенная Windows document binding остаётся открытым gate |
| 3. Run control/лимиты | User batch resume сохранённых ожиданий реализован, In Review (см. выше). Далее existing run owner/leases/input channel, agent pause/resume/cancel; глобальный scheduler admission и budgets. Зависит от 2b | Нет второго Run/thread, user pause сильнее peer wake; cancel requested отличается от confirmed cancellation; unknown tool effect сохраняется. Справедливость очереди и лимиты параллельных Run ещё предстоит проверить |
| 4a. Организация чатов | Optional group events/projection, creator в headers/DTO; `group`, целевой `chat.rename`, bounded overview через core.find/read и общее дерево. Зависит от 1, не требует durable wait/control | Один чат в двух группах, restart/rebuild, rename без потери параллельной записи; нет неявного запуска/обмена историей/изменения прав или budget. Общий UI slice с визуальным ревью |
| 4b. Поручения | Optional assignment events и bounded projection; `resolve`. Зависит от 2–3, membership не обязателен | Смена ролей без смены identity; finish не закрывает поручение; terminal assignment не доказывает внешний эффект без evidence |
| 5. Inspector | Typed DTO/cursors поверх существующих SessionEvent/kernel/trajectory readers; production Inspector, без demo/store | Участники + отдельные communication/dependency graphs + timeline, навигация thread/message/Run/step/resource/E; control использует этап 3; subscriptions очищаются при dispose, Run сохраняется |
| 6. Ресурсы/qualification | Existing Resource Registry, patch/preparation/read-back и HostRuntime/DocumentAccessGate; конфликтные multi-agent сценарии | Stale snapshots отклоняются, инструменты с неизвестными side effects не повторяются; Windows/Office и live-model evidence отдельно; никакой новой истории или COM abstraction |

На этапе 1 нет общего token budget, durable correlation, group control или нового
Inspector. Лимит 8 созданий на causal chain — ограничитель разрастания, не глобальный
лимит одновременно работающих Run. Новых LLM-запросов для discovery/наблюдения нет.
Большие каталоги читаются через headers, без историй; model output ограничен 50 peers.
В этапе 2a добавлена durable correlation, в 2b.1 — typed timeout progress, в 2b.2 —
сохраняемый незавершённый wait. Диапазон 1–10 секунд сохранён; расширение длительности
не требуется для проверки восстановления. Освобождать exact chat ownership во время
wait отклонено: это допускает второй Run и конфликт контекста. Владение чатом не
равно занятому потоку/Office gate или будущему execution slot. Тот же coordinator
продолжает kernel после события/таймера. При restart resume остаётся явным; peer input
не снимает recovery pause. Wait v4/skill v6 используют прежнюю форму JSON; старые
admissions требуют актуального контракта, скрытой миграции suspended calls нет.

## Семантика и проверяемые границы

| Действие | Факт завершения | Что не следует из него |
|---|---|---|
| Создать thread | Commit обычного ChatSession и read-back provenance | Run запущен |
| Отправить сообщение | Source reservation и destination Inbox read-back | Сообщение уже в контексте |
| Доставить | Pending input зарегистрирован в exact Inbox | Модель его обработала |
| Включить в контекст | Durable ChatMessage + accepted model request содержит input | Получен ответ или поручение выполнено |
| Обработать | Трасса принятого model action после этого input | Текстовое обещание стало runtime proof |
| Ответить | Отдельный send; на этапе 2 — validated reply_to | Другой Run завершён |
| Завершить Run | Kernel terminal lifecycle/outcome | Все поручения или работа группы выполнены |
| Завершить поручение | Этап 4: terminal assignment event и ссылки на результат | Внешние эффекты подтверждены без read-back |

В 2b.2 реализованы durable reply any/all и следующий доступный peer input через
прежний Inbox matcher; отдельного event cursor для произвольных событий нет.
Terminal Run/assignment и dependency graph — последующие этапы 3–4. Timeout
по сохранённому UTC deadline, отдельные cancelled/expired/failed outcomes. Polling
оставляется только bounded reconnect сверке, не основному механизму.
Wait dependency graph отделён от communication graph. Цикл без внешнего источника
прогресса возвращает диагностируемую зависимость; runtime не выдумывает ответ и не
помечает работу completed. Взаимное общение само по себе не является deadlock.

## Проверки и миграции

| Сценарий/инвариант | Механизм и проверка |
|---|---|
| Закрыть все вкладки и продолжить обмен | Saved catalog + queue owner; Workspace model-wire без views, browser close/rebind checks |
| Создать peer без поручения | Durable child, пустая история/LastRun; spawn identity test |
| Создать recursive/concurrent peers | Root SpawnPrepared, общий Inbox lock; 8 creations, restart и два конкурирующих bound services |
| Ошибка между созданием и intake | Раздельный receipt; fault injection, повтор не создаёт замену/dispatch |
| Plan/Deny или выключенный auto approval | Policy ceiling; pre-intake отказ, safe-boundary deferral, real Workspace child run без broader global approval |
| Busy recipient, user FIFO, текущая запись | Existing boundary/input channel; no cancellation/replay in-flight tool; queue tests |
| Stop/restart | Durable paused queue; shared-controller recovery и explicit resume checks |
| Ответ пришёл раньше wait; несколько отправителей | 2a: ранний/applied ответ, any/all, частичный timeout, late signal, restart и настоящий Workspace spawn→reply→filtered wait; durable suspension — 2b.2, cold recovery в Office service/Workspace |
| Циклическое ожидание/timeout/cancel | Этапы 2–3: dependency graph и восстановление exact suspension |
| Chess: согласовать роли, создать/сыграть игру | После 2–6: несколько ordinary threads, проверяемый игровой артефакт/правила ходов, смена ролей, close/restore, запись в Inspector; пока не qualified |
| Общий Office/file ресурс и unknown mutation | Этап 6: contention, stale revision, подготовка до dispatch, read-back и запрет автоматического replay |

Новые поля этапа 1 добавлены к существующим metadata/provenance/events, без копирования
историй, dual-write или удаления пользовательских данных. List/send admissions меняют
версии; stale admissions требуют текущего контракта. Старый peer envelope без policy
не исполняется с неявным grant; chain без root требует нового человеческого ввода для
spawn. Сохранённый журнал не мигрирует в параллельную инфраструктуру.

Срез 1 закоммичен в `8b6ba5f2`; browser `agent-communication.test.js` проходит.
Локальная проверка с этапом 2a: 22 `agent communication:`, 12 `inbox:`,
6 `unified policy:`, 1 `tool pack: core`. Harness проверяет
реальные Application/Kernel/Workspace и Office services/bridge со stub Controller.
Production Controller/COM/WebView2 на Windows этим не квалифицированы.

Перед этапом 3 снять p50/p95/p99 intake→durable, intake→context (отдельно idle/busy),
wakeup→next-step; 1/2/4/8 peers, bounded queue depth/активные Run, восстановление после
crash в каждом prepared/accepted boundary. Начальные цели: отсутствие потерь после
подтверждённого intake, отсутствие duplicate execution при recovery, 0 дополнительных
LLM calls для runtime status, отсутствие занятых worker/Office gate при durable wait.
Latency targets фиксировать после локального baseline, исключая длительность provider
inference/COM. Coordination tokens считать отдельно от work tokens и summary tokens;
сводки модели опциональны и не обновляются постоянным фоновым inference.
