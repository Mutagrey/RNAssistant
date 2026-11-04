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

## Инструменты

| Tool | Назначение | Статус первого этапа |
|---|---|---|
| `agents.list(query?, agent_id?)` | Saved participants и runtime status своей области | Реализовано, v3 |
| `agents.spawn(title, initial_message?)` | Создание ordinary thread, отдельно optional intake | Реализовано, v1 |
| `agents.send(agent_id, text)` | Durable intake и явные ответы | Реализовано, v3; корреляция ниже |
| `agents.wait(timeout_seconds?)` | Ожидание совместимого входящего сообщения в текущем Run | Существующий transient v2; durable contract впереди |
| `agents.pause` | Остановка продвижения на безопасной границе | Этап 3 |
| `agents.resume` | Продолжение сохранённого ожидания/agent pause с проверкой полномочий | Этап 3 |
| `agents.cancel` | Отмена выполнения с отдельным подтверждением terminal state | Этап 3 |
| `agents.group` | Создание/изменение необязательной Collaboration | Этап 4 |
| `agents.resolve` | Явное разрешение опционального поручения с результатом | Этап 4 |

Нереализованные tools не добавляются в registry/skill. Точные текущие JSON Schema
находятся в `AgentCommunicationToolCatalog.GetTools`. Следующие версии фиксируются
вместе с их runtime semantics, до переключения consumers.

Для send предполагаются optional `expect_reply`, `reply_to`: runtime выдаёт
`message_id`; request использует ID исходного сообщения, отдельный синоним request_id
не нужен без дополнительного сценария. Ответ обязан ссылаться на известный request
и проверенного отправителя. Несколько ожидаемых ответов не коррелируются по одному
только тексту/названию чата. Control tools принимают semantic `agent_id`; runtime
закрепляет exact Run при admission и отклоняет замену Run перед dispatch.
User Stop/restart pause не снимается peer-командой без пользовательского разрешения.

## Этапы и интеграция

| Этап | Изменения и зависимости | Критерий готовности и главный риск |
|---|---|---|
| 1. Каталог/spawn | `AgentConversationScope`, `AgentCommunicationService.Spawn`; `ChatSessionService.PrepareAgentChat`; Workspace scope binding; catalog/skill; удаление presence bridge/leases | Работа без views, сохранение provenance, общий recursive budget, truthful partial receipt. Риск: binding/права при закрытом target. Локальные проверки пройдены; Office/controller evidence отдельно |
| 2. Корреляция/durable wait | Inbox events + kernel continuation, `ConversationRunCoordinator`, `AgentRunContinuation`, history restoration и exact tool admission. Зависит от 1 | wait registration и scan durable inbox выполняются под одной синхронизацией; ответ до регистрации не теряется; suspend освобождает worker/document gate; resume даёт terminal wait result тому же accepted call. Не обходить текущий запрет восстановления незавершённых exchanges |
| 3. Run control/лимиты | Existing run owner/leases/input channel, typed pause/resume/cancel; глобальный scheduler admission и budgets. Зависит от 2 | Нет второго Run/thread, user pause сильнее peer wake; cancel requested отличается от confirmed cancellation; unknown tool effect сохраняется. Справедливость очереди и лимиты параллельных Run проверены |
| 4. Группы/поручения | Optional membership/assignment events и bounded projections; `group`, `resolve`. Зависит от 2–3 | Один thread в двух группах; смена ролей без смены identity; finish не закрывает поручение; terminal assignment не доказывает внешний эффект без evidence |
| 5. Inspector | Typed DTO/cursors поверх существующих SessionEvent/kernel/trajectory readers; production Inspector, без demo/store | Участники + отдельные communication/dependency graphs + timeline, навигация thread/message/Run/step/resource/E; control использует этап 3; subscriptions очищаются при dispose, Run сохраняется |
| 6. Ресурсы/qualification | Existing Resource Registry, patch/preparation/read-back и HostRuntime/DocumentAccessGate; конфликтные multi-agent сценарии | Stale snapshots отклоняются, инструменты с неизвестными side effects не повторяются; Windows/Office и live-model evidence отдельно; никакой новой истории или COM abstraction |

На этапе 1 нет общего token budget, durable correlation, group control или нового
Inspector. Лимит 8 созданий на causal chain — ограничитель разрастания, не глобальный
лимит одновременно работающих Run. Новых LLM-запросов для discovery/наблюдения нет.
Большие каталоги читаются через headers, без историй; model output ограничен 50 peers.

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

Ожидания этапа 2: конкретный reply, следующее сообщение после cursor, terminal Run,
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
| Ответ пришёл раньше wait; несколько отправителей | Этап 2: race/fault injection до/после регистрации и durable cursor replay |
| Циклическое ожидание/timeout/cancel | Этапы 2–3: dependency graph и восстановление exact suspension |
| Chess: согласовать роли, создать/сыграть игру | После 2–6: несколько ordinary threads, проверяемый игровой артефакт/правила ходов, смена ролей, close/restore, запись в Inspector; пока не qualified |
| Общий Office/file ресурс и unknown mutation | Этап 6: contention, stale revision, подготовка до dispatch, read-back и запрет автоматического replay |

Новые поля этапа 1 добавлены к существующим metadata/provenance/events, без копирования
историй, dual-write или удаления пользовательских данных. List/send admissions меняют
версии; stale admissions требуют текущего контракта. Старый peer envelope без policy
не исполняется с неявным grant; chain без root требует нового человеческого ввода для
spawn. Сохранённый журнал не мигрирует в параллельную инфраструктуру.

Локальная проверка этапа 1: 17 `agent communication:`, 12 `inbox:`, 6 `unified policy:`,
1 `tool pack: core`; browser `agent-communication.test.js` проходит. Harness проверяет
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
