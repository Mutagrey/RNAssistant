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
   а не обёртка polling или второй чат. Сначала требуется сохраняемый suspension
   contract; нынешний wait 1–10 секунд не выдаётся за его реализацию.
4. Chat events/CAS остаются источником истины. Нет второго mailbox/task store.
   Новые projections — удаляемые индексы. Exactly-once неизвестных side effects
   не обещается. Prepared без доказанного результата не переисполняется.
5. Discovery и доставка ограничены exact document/workspace; известный адрес не
   предоставляет доступ к другой области. Истории автоматически не объединяются.
   После restart продолжение требует явного действия пользователя.
6. Collaboration — необязательная many-to-many связь. Thread в нескольких группах
   сохраняет один общий контекст. Группа фильтрует наблюдение и бюджет, не превращает
   сообщения других участников в user instructions и не создаёт implicit control rights.
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

## Инструменты

| Tool | Назначение | Текущий статус |
|---|---|---|
| `agents.list(query?, agent_id?)` | Saved participants и runtime status своей области | Реализовано, v3 |
| `agents.spawn(title, initial_message?, expect_reply?)` | Создание ordinary thread, отдельно optional intake/request | Реализовано, v2 |
| `agents.send(agent_id, text, expect_reply?, reply_to?)` | Durable intake, корреляция и явные ответы | Реализовано, v4 |
| `agents.wait(timeout_seconds?, requests?, mode?)` | Следующее сообщение или any/all ответов на свои запросы | Transient v3, этап 2a; durable suspension впереди |
| `agents.pause` | Остановка продвижения на безопасной границе | Этап 3 |
| `agents.resume` | Продолжение сохранённого ожидания/agent pause с проверкой полномочий | Этап 3 |
| `agents.cancel` | Отмена выполнения с отдельным подтверждением terminal state | Этап 3 |
| `agents.group` | Создание/изменение необязательной Collaboration | Этап 4 |
| `agents.resolve` | Явное разрешение опционального поручения с результатом | Этап 4 |

Нереализованные tools не добавляются в registry/skill. Точные текущие JSON Schema
находятся в `AgentCommunicationToolCatalog.GetTools`. Следующие версии фиксируются
вместе с их runtime semantics, до переключения consumers.

В send реализованы optional expect_reply/reply_to: runtime выдаёт message_id;
request использует ID исходного сообщения, отдельная выдаваемая сущность request_id
не нужна. В Matches поле request_id обозначает ID исходного сообщения, message_id —
ID ответа. Ответ ссылается на полученное сообщение и проверенного отправителя;
его цепочка не зависит от более поздних обсуждений. Корреляция переживает restart,
само ожидание — пока нет. Control tools будут принимать semantic agent_id; runtime
закрепляет exact Run при admission и отклоняет замену Run перед dispatch.
User Stop/restart pause не снимается peer-командой без пользовательского разрешения.

## Этапы и интеграция

| Этап | Изменения и зависимости | Критерий готовности и главный риск |
|---|---|---|
| 1. Каталог/spawn | `AgentConversationScope`, `AgentCommunicationService.Spawn`; `ChatSessionService.PrepareAgentChat`; Workspace scope binding; catalog/skill; удаление presence bridge/leases | Работа без views, сохранение provenance, общий recursive budget, truthful partial receipt. Риск: binding/права при закрытом target. Локальные проверки пройдены; Office/controller evidence отдельно |
| 2a. Корреляция | Existing Prepared/Accepted + input provenance, source recipient verification и Inbox matcher. Реализовано после 1 | Ранние/применённые ответы учитываются, any/all различаются, нет чужих match/потери сигнала, unrelated/human/policy input не выдаётся за ответ. 22 целевые проверки проходят |
| 2b. Durable wait | Kernel continuation, `ConversationRunCoordinator`, `AgentRunContinuation`, history restoration и exact tool admission. Зависит от 2a | Сохраняемый suspension освобождает worker/document gate; resume даёт terminal wait result тому же accepted call. Не обходить текущий запрет восстановления незавершённых exchanges; crash/deadline/cancel проверяются отдельно |
| 3. Run control/лимиты | Existing run owner/leases/input channel, typed pause/resume/cancel; глобальный scheduler admission и budgets. Зависит от 2b | Нет второго Run/thread, user pause сильнее peer wake; cancel requested отличается от confirmed cancellation; unknown tool effect сохраняется. Справедливость очереди и лимиты параллельных Run проверены |
| 4. Группы/поручения | Optional membership/assignment events и bounded projections; `group`, `resolve`. Зависит от 2–3 | Один thread в двух группах; смена ролей без смены identity; finish не закрывает поручение; terminal assignment не доказывает внешний эффект без evidence |
| 5. Inspector | Typed DTO/cursors поверх существующих SessionEvent/kernel/trajectory readers; production Inspector, без demo/store | Участники + отдельные communication/dependency graphs + timeline, навигация thread/message/Run/step/resource/E; control использует этап 3; subscriptions очищаются при dispose, Run сохраняется |
| 6. Ресурсы/qualification | Existing Resource Registry, patch/preparation/read-back и HostRuntime/DocumentAccessGate; конфликтные multi-agent сценарии | Stale snapshots отклоняются, инструменты с неизвестными side effects не повторяются; Windows/Office и live-model evidence отдельно; никакой новой истории или COM abstraction |

На этапе 1 нет общего token budget, durable correlation, group control или нового
Inspector. Лимит 8 созданий на causal chain — ограничитель разрастания, не глобальный
лимит одновременно работающих Run. Новых LLM-запросов для discovery/наблюдения нет.
Большие каталоги читаются через headers, без историй; model output ограничен 50 peers.
В этапе 2a добавлена durable correlation. Сам wait всё ещё ограничен 1–10 секундами
в текущем Run; await не держит рабочий поток/Office gate, но Run/queue ownership ещё
не освобождается как отдельный durable suspension. Generic no-progress guard при
повторяющихся timeout receipts остаётся открытым до следующего среза.

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

Ожидания этапа 2b: durable reply, следующее сообщение после cursor, terminal Run,
terminal assignment, any/all набора. Один matcher и journal registration; timeout
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
| Ответ пришёл раньше wait; несколько отправителей | 2a: ранний/applied ответ, any/all, частичный timeout, late signal, restart и настоящий Workspace spawn→reply→filtered wait; durable suspension — 2b |
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
