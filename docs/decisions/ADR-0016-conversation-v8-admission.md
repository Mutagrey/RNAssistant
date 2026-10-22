# ADR-0016: Conversation v8 — admission завершения и reflection

Дата: 2026-10-23. Статус: **предложено для реализации после фазы 0**;
активный контракт остаётся v7. Owner: Core ModelProtocol / AgentKernel.
Основание: [аудит HEAD и красная регрессия](../stabilization/conversation-v8/AUDIT.md).
`v8.1` — редакция пользовательской спецификации; планируемый целочисленный
`ResponseProtocolVersion` — **8**, product version отдельно и здесь не меняется.

## Контекст

Валидный v7 `done` после создания листа завершает run, хотя данные и график ещё
не созданы. Форматная проверка и prompt не могут гарантировать смысловую полноту.
Существующие kernel, runtime guards, append-only stream, CAS, model compiler,
Task List/Plan/Questions и Workspace acceptance сохраняются.
Это развитие границ [ADR-0001](ADR-0001-model-does-not-own-completion.md),
а не второй агентский loop.

## Логический контракт

Три закрытые взаимоисключающие формы. Общие обязательные поля: `action`, `reason`,
`message`; `reason` — краткое основание, не private reasoning. `message=""`
разрешён. Writer/schema располагают action первым, reason вторым, message последним;
parser не придаёт порядку JSON семантического значения.

| action | Дополнительные обязательные поля | Запрещены |
|---|---|---|
| `execute` | `tool_calls`: 1..32 calls `{name, arguments}` | `decision`, `results`, модельные IDs |
| `reflection` | `decision`: непустое краткое решение | `tool_calls`, `results` |
| `finish` | `results`: массив `{summary, evidence, assessment}` | `tool_calls`, `decision` |

Все объекты закрыты (`additionalProperties:false`), поля с неверными типами,
дубликаты, лишние root/call/result fields отвергаются. `reason` и `summary`
непустые; `evidence` — массив непустых строк, может быть пустым для отдельного claim.
Assessment: `satisfied`, `partially_satisfied`, `unverified`, `not_satisfied`.
Повтор одной ссылки внутри evidence одного claim отвергается; одна terminal запись
может поддерживать разные claims, не увеличивая execution counts.
`criterion`/`criterion_ref` не вводятся: сохранённого owner критериев C1 нет.
Вызовы проверяются по exact captured tool schema. Максимум один mutation/иной
не-read call; многовызовный execute разрешён только для independent local reads.

```json
{"action":"reflection","reason":"Нужно уточнить область правки.","decision":"Прочитать текущую процедуру перед patch.","message":""}
```

```json
{"action":"finish","reason":"Достаточно прямого ответа.","results":[],"message":"VBA — язык автоматизации Office."}
```

```json
{"action":"finish","reason":"Изменение прочитано обратно.","results":[{"summary":"Создан лист Report","evidence":["call_runtime_assigned"],"assessment":"satisfied"}],"message":"Лист создан."}
```

Последний ID — иллюстрация: модель копирует только ID предъявленного terminal
результата. Даже такой валидный finish не доказывает создание данных/графика.

Schema, parser, writer и repair имеют один owner `ModelProtocolWire` и parity tests
для `json_object`/`json_schema`. Логическая схема — union трёх веток. Поддержку
root `oneOf`/`anyOf`, `const`, `minItems`, `$defs` и dynamic arguments надо проверить
на зафиксированном endpoint. Если root union не поддерживается, допустима только
документированная transport-оболочка `{"response": <logical union>}` с закрытым
root. Её явно выбирает capability profile; history и kernel получают logical
response. Конкретный transport profile ещё не квалифицирован.
Существующий явный `FallbackToJsonObject` остаётся отдельным transport решением;
локальные правила не ослабляются, accepted v7 не конвертируется в v8.

## Finish evidence

`results[].evidence` использует существующие runtime `tool_call_id`, уже доставляемые
в `ToolResultWire`. Это ограниченная ссылка на outcome, не argument инструмента,
ResourceRef, permission или execution authority. Новых E-ID/index/store нет;
Inspector aliases и `ResourceEvidence.EvidenceId` для этой цели не подходят.

Admission выполняет **AgentKernel до ResponseAccepted**, после проверки формы:

1. Определить accepted calls текущего `runId` из kernel/durable state. При нуле
   разрешить `results=[]`; при ненулевом количестве нужны хотя бы один result и
   хотя бы одна evidence ссылка в совокупности. Не требовать ссылки на каждый read.
2. Разрешить **каждую** ссылку в ровно один canonical terminal outcome этого run и
   session. Сверить accepted call ID, tool name, step/model-attempt origin, scope,
   terminal kind и целостность retained metadata/body. Naked call, pending,
   отсутствующий, foreign, orphan или неоднозначный outcome — отказ.
3. Typed resolver принадлежит существующему Core persistence/conversation owner;
   kernel не разбирает `JObject`, UI или model prose. Office использует исходный
   `tool.execution.finished`, Workspace — terminal `tool.result.recorded` с typed
   execution progress. Materialized/reduced result и diagnostic accepted marker
   не становятся новым terminal фактом. При отсутствии нужной metadata — fail closed.
4. Current-run metadata можно держать как восстанавливаемую in-memory projection;
   durable source только существующий stream. В live path terminal fact становится
   доступен gate после успешного append. После restart resolver сверяет origin с
   полным durable history, независимо от folding/compaction и видимого prompt.
5. Error/denied/not_dispatched/unknown — допустимые terminal records для честного
   отчёта. Их нельзя автоматически превращать в verified change. Исторический
   outcome при superseded resource остаётся фактом исполнения, но не доказательством
   текущего содержимого; прежние currentness/read-back guards обязательны.
6. Сохраняются Workspace `RejectCompletion`, pending confirmation, permissions,
   inspection-after-unknown и запрет replay. Reject никогда не запускает старый call.

`unverified` само по себе не снимает `InspectionRequired`: честный отчёт об unknown
допустим после обязательного расследования. Невозможность расследования не создаёт
авторитетный результат; остаётся bounded runtime stop. Этот сценарий нужно явно
проверить при удалении модельного `blocked`, чтобы не ослабить guard и не зациклиться.

Новая запись пользователя после terminal run создаёт новый набор accepted calls.
Confirmation, явное восстановление и steer внутри run сохраняют его ID, накопленные
лимиты и accepted set. Если application создаёт новый run, прежние IDs foreign.
Наличие предыдущих calls в prompt не меняет эти правила.

`summary`/`assessment` остаются **model claims**. Ни `satisfied`, ни валидная ссылка,
ни `ok` не меняют GoalVerification на verified. Runtime проверяет typed факты и
свои acceptance criteria; без typed claim о конкретном эффекте он не может доказать
противоречие между произвольным summary и outcome. Поэтому не вводится эвристика
`assessment + tool name/message => task success`. UI показывает model assessment
рядом с настоящими terminal/effect статусами и сохраняет `not_assessed` для общего
user goal. Failed/unknown evidence всегда остаётся явно видимым.

## Переходы, budgets и persistence

| Вход в acceptance boundary | Действие ядра | Следующее состояние |
|---|---|---|
| Валидный execute | Назначить IDs, append acceptance, обычный dispatch/read-back | Следующий model request или штатный pause/failure |
| Валидный reflection, меньше двух подряд | Append reason/decision/message/version/attempt origin | Следующий request того же run |
| Третий reflection без нового наблюдения | Не принимать; сохранить bounded feedback | Одна возможность выбрать execute/finish в остатке бюджета |
| Повторный reflection после этого feedback | Без tools и fabricated call | Runtime `reflection_limit` |
| Finish с валидными ссылками и всеми guards | Append accepted proposal, terminal summary | `Completed / model_finish`, goal отдельно |
| Finish с нарушением admission | Не принимать, сохранить typed rejection/feedback | Следующая обычная iteration, те же run/IDs/budgets |
| Provider failure, cancel, лимит, отказ в confirmation | Существующий typed runtime путь | Не переименовывать в model finish |

Новое наблюдение для reflection counter — новый canonical terminal tool result
или доставленный user input. Format repair, compaction, checkpoint, rejected finish
и перефразирование reflection counter не сбрасывают. Повторные безрезультатные
calls остаются под существующим no-progress guard. Reflection не обязателен
перед execute и не вызывает Task List/Plan/Questions/memory.

Все шаги расходуют существующий iteration/time/token budget. Finish rejection
использует текущую completion-feedback boundary, а не format repair или внешний
Office loop. Shape/argument errors ремонтирует `ModelProtocolClient` на frozen
context до admission. Typed codes включают `empty_execute_calls`,
`missing_finish_results`, `missing_finish_evidence`, `unknown_evidence`,
`nonterminal_evidence`, `wrong_action_fields`, `reflection_limit`.
Provider/transport failure не выдаётся за format error.

Reflection, finish claims и response-attempt origin сохраняются в существующем
append-only stream. Счётчик/feedback лимита — typed поля текущего `AgentRunState`
в `run.updated`, восстанавливаемые без второго storage owner. `RestoreCheckpoint`
разрешает assistant tail только для принятого reflection; terminal finish не
продолжается автоматически. Компакция сохраняет полезное decision как interpretation
с provenance, никогда как evidence или user permission. Пустой message не создаёт
пузырь; reason/decision не превращаются в provider-private reasoning.

## Cutover и проверки

Один canonical guidance owner — `ModelProtocolWire`. Его используют defaults,
custom-prompt migration/recovery, System/Agent/Plan/Chat, workspace/CLI, repair,
schema descriptions и compatibility probes. Built-in skills проверяются на старые
actions. Пользовательский prompt не отключает validator. Вопросы идут через
`common.questions_ask`, permissions/отмена/ошибки остаются runtime состояниями.

Фазы 1–4 — последовательность разработки **одного атомарного переключения**:
DTO/parser/schema/writer/repair → kernel/evidence/replay → adapters/prompts →
persistence/UI. На main не публикуется промежуточный parser v8 с kernel v7.
Один active protocol, без dual-write, автоматического backfill или aliases старых
actions. V7 история остаётся доступной для просмотра; несовместимый continuation
явно требует новый чат/reset. Открытие старого чата ничего не переписывает и не удаляет.

До default cutover сравнить зафиксированные v7/v8 SHAs в отдельных checkout на
одних задачах/model/endpoint/settings: v7, v8 без обязательного reflection, v8 с
адаптивным reflection; отдельно field-order/reason ablation. Нужны external task
success, premature/false finish, invalid evidence, calls, tokens, latency, loops,
context failures. Scripted pass не заменяет этот gate.

Минимальные обязательные сценарии: closed shapes/IDs; direct finish без calls;
пустые results после mutation; forged/foreign/pending/duplicate terminal records;
failed/denied/not_dispatched/unknown/no-change/change и macro без read-back;
reflection→execute→result→finish и loop limit; frozen repair; compaction/CAS;
restart/confirmation/steer/cancel; Workspace acceptance; Task List/Plan/Questions;
три result roles и оба response modes; пустые сообщения/partial/unverified UI.
Windows x64/Office x64/WebView2 — отдельное открытое evidence.

## Отклонённые альтернативы и ограничения

- Только новый prompt/порядок полей: не создаёт runtime admission, выигрыш не доказан.
- Продолжать после слова «сейчас» или закрывать по Task List: подмена фактов prose/
  model assessment; неверное поведение прямых ответов и блокировок.
- Обязательная reflection перед каждым вызовом: лишние latency/tokens без evidence.
- Второй Office loop, отдельная E-БД, новая task-success БД: дублирование owners.
- Finish только при успешных tools или ссылка на каждый discovery: мешает честному
  сообщению об отказе/неопределённости и не доказывает полноту.
- ResourceEvidence/Inspector aliases как terminal outcomes: разные сущности и scope.
- Автоконверсия v7, hidden fallback и повтор mutation после reject: нарушают provenance/
  no-replay и сохранность данных.

Остаются открытыми endpoint transport profile, эффективность на реальных моделях,
стоимость outcome metadata в контексте, restart/streaming qualification и смысловая
полнота задачи при валидных ссылках. Это решение не обещает их устранение одной схемой.
