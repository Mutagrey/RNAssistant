# Ссылки модели: текущий контракт и предложение — 2026-10-26

Статус: аудит и предложение для согласования; **не действующий новый контракт**.
Owner: Core ModelProtocol / context projection / persistence admission.
Срез: `fc282574`, исходники соответствующих owners и сохранённый wire пилотов.
Параллельные незакоммиченные изменения транспорта/Inspector в этот аудит не включены.

## Что происходит сейчас

ID вызова назначает `AgentKernel.AssignCallIds`, не модель: стандартный генератор —
`call_` + GUID в формате N, всего **37 символов**. `execute.tool_calls[]` принимает
только `name` и `arguments`; модельные ID там запрещены.

Однако в основном model wire короткой проекции нет. `ModelResultProjection.Project`
передаёт `wire.ToolCallId` в `ToolResultWire.WriteParsed` без замены.
`ModelContextCompiler` сохраняет тот же ID в сокращённом `TOOL_INTERACTION`.
`ConversationResponseSchemaBuilder` требует эти строки в `finish.results[].evidence`.
`ConversationFinishEvidenceAdmission` сверяет их с canonical terminal events
текущих session/run/turn. Совпадение ссылки не является проверкой смысла задачи.

В `local-edit-compact/wire/007-request.json` исходного
[пилота](tool-profile-comparison.md) присутствуют четыре таких ID; все в точности
совпадают с `ToolCallId` сохранённого session. ID успешного patch виден в запросе;
модель возвращала вместо него путь файла. В данном случае ссылка не потерялась
при сборке контекста. Это не доказательство сохранности ссылок при любой compaction.

```text
execute: name + arguments
    → kernel назначает внутренний call ID
    → accepted call / terminal result в существующем event stream
    → compiler выбирает историю, актуальные наблюдения и краткие записи операций
    → model projection + schemas + бюджет → HTTP-запрос
    → finish.evidence содержит строки из результатов
    → kernel / durable admission проверяют их происхождение
```

В реальном запросе после patch старый read был заменён записью `TOOL_INTERACTION`
со статусом наблюдения Superseded; актуальные байты появились отдельно в
`CURRENT_RESOURCE_VIEW`. Факт операции и актуальность её исходника — разные вещи.
При строгом формате схемы arguments идут в response_format, а инструкции, история,
текущие наблюдения и runtime feedback — в messages. ModelRequestPreparation
проверяет полный подготовленный запрос; source CAS и весь сырой журнал в запрос
автоматически не копируются.

## Разные виды идентификаторов и ссылок

| Вид | Сейчас | Кто владеет / кому нужен | Предложение |
| --- | --- | --- | --- |
| Tool/skill ID | `files.patch`, `workspace.files` | Публичный каталог; модели нужен смысл имени | Сохранить семантические имена |
| Semantic target | `src/app.ts`, `VBA module: Module1` | Model-facing адрес источника; runtime разрешает authority | Сохранить; не подменять все пути бессмысленными короткими кодами |
| ID вызова / terminal result | `call_` + 32 hex | Runtime correlation и durable admission; сейчас виден модели | В model view выдавать короткую ссылку на terminal result |
| ResourceRef | `rna://…` + revision | Exact runtime source identity | Оставить внутренним, не превращать в evidence-ссылку модели |
| ResourceEvidence.EvidenceId | Внутренний ID наблюдения с coverage, scope, payload и dependencies | Evidence/compiler/authority | Не смешивать с ID результата вызова |
| Search continuation / selectors | afterTarget/afterLine/afterColumn, ranges, JSON pointer | Семантическая навигация по источнику | Сохранить типизированные selectors; это не citation ID |
| Compaction source refs | `source-1`, `source-2` | Выдаются только запросу сжатия; enum → точные source records | Отдельная область ссылок; не принимать как finish evidence |
| Inspector aliases | `Q1`, `O1`, `E1`, `R1@v1` | UI saved-request/resource/operation/evidence navigation | Не использовать как authority или model finish refs |
| IDs внутри пользовательских данных | JSON id, ключ строки, business ID и т. п. | Содержимое документа | Никогда не сокращать заменой строк |
| Guard/revision/cursor/run/attempt IDs | Внутренние pins и служебная корреляция | Runtime и диагностика | Не отдавать модели как параметры управления |

Короткие ссылки **уже есть**, но не там, где требуются для основного model loop:
`ModelContextInspectorService.AliasIndex` строит Q/O/E/R из сохранённых trace-запросов;
`ContextCompactionRuntime` отдельно выдаёт source-N и проверяет sourceIds по enum.
Оба механизма имеют собственный scope. Перенос UI AliasIndex в finish admission
создал бы зависимость authority от того, что ранее попало в диагностику.

`results[].evidence` сегодня означает ссылки на исходы вызовов, а `ResourceEvidence`
означает exact наблюдение источника. Одинаковое слово не делает эти сущности
взаимозаменяемыми: одна операция может прочитать несколько ресурсов/окон, а несколько
операций — наблюдать одну ревизию. `ok`, валидная ссылка и успешная задача также различны.

## Предлагаемое правило для модели

Модель видит короткую выданную ссылку и понятное описание результата. Например,
модельное представление результата, **не новый durable ToolResultWire**:

```json
{"ref":"r4","name":"files.patch","status":"ok","message":"File write verified by exact read-back.","data":{"path":"src/app.ts","changed":true}}
```

В finish: `"evidence":["r4"]`. Модель выбирает выданную ссылку; runtime разрешает её
во внутренний ID, затем применяет существующий durable gate. Отдельный произвольный
ID модель не назначает. Короткая ссылка сама по себе не даёт права вызвать инструмент,
записать файл, прочитать ресурс или объявить effect verified.

**`r4` — пример оформления, не тип и не обязательный префикс.** Допустимо другое
короткое обозначение, например `x4`. Parser/resolver не должны извлекать смысл из
буквы, номера или regex вида `^r[0-9]+$`: требуется точное совпадение с выданной
binding. Kind, scope, canonical identity и разрешённое использование принадлежат
runtime. Формат выдачи — маленькая внутренняя policy, без новой пользовательской
настройки и без зависимости от имени модели.

Начать следует только со ссылок на **terminal tool results**. Не вводить сейчас
универсальную адресацию всех ресурсов/skills/guards через один нетипизированный ref.
Если позже понадобится ссылка на конкретный source view, она должна разрешаться в
отдельный typed kind с собственными правилами актуальности, а не приниматься как
результат вызова по одному совпадению строки.

## Требования к реализации

1. Один общий Core owner для model reference projection/resolution; оба host consumer
   используют его. Внутренние GUID, event stream, CAS и mutation journal сохраняются.
   Binding — восстанавливаемая проекция canonical accepted/terminal facts, не второй store.
2. Не нумеровать заново только видимый кусок истории. Рекомендуемая исходная схема —
   стабильный ordinal accepted call в чате, включая пропуски от незавершённых calls;
   выдавать citation только для доступного terminal fact. Это позволяет не переиспользовать
   r1 между run, пока старая история видна. Eligibility finish остаётся current-run.
3. Freeze exact map вместе с model request. Schema repair и transport fallback
   используют ту же map. Новая инструкция/сжатие/изменение working set не должны
   переназначать старую ссылку другому результату. Restart восстанавливает binding
   из того же canonical stream; новые labels не создают execution authority.
4. Проецировать структурные поля, включая tool-result frames, folded interactions,
   retained_result links, accepted finish history и native assistant/tool pairing.
   Не применять Replace/regex ко всем строкам: исходник или пользовательский текст
   может содержать похожий ID. Guard/revision/ResourceRef остаются внутренними.
5. В обратном направлении разрешать finish refs по frozen map **до передачи в kernel**;
   kernel/store получают canonical IDs и проверяют существующие факты. Неизвестный,
   чужой, неверного kind или pending ref отвергается. Не делать fallback с GUID,
   названием инструмента или путём файла. Error/unknown terminal outcome допускает
   честную оценку, но не превращается в verified effect.
6. Считать бюджет после model projection. Выдавать ref рядом с соответствующим
   результатом; не дублировать весь каталог результатов отдельной таблицей. Для
   strict schema проверить enum выданных citation refs, включая нулевой/большой набор;
   json_object использует те же bindings и локальный validator. Цена schema/cache
   и совместимость endpoint требуют измерения, не предполагаются бесплатными.
7. Folding/compaction не должны оставлять finish без доступной валидной ссылки на
   уже выполненную работу. Краткие receipts сохраняют citation и статус. При утрате
   нужного context нельзя заставлять повторять effect ради нового номера.
   Raw response в диагностике сохраняет короткие refs, accepted durable history —
   canonical correspondence; реконструкция связывает их без UI authority.

## Владельцы, проверки и миграция

| Этап | Основные файлы/границы | Приёмка |
| --- | --- | --- |
| Уточнить публичный контракт | protocols/CONVERSATION_RESPONSE_V8.md, decisions/ADR-0016-conversation-v8-admission.md, conversation-protocol.md | Однозначные model refs / internal IDs; определена wire revision и судьба старых model turns |
| Общая frozen map | Core ModelProtocol/ModelRequestPreparation.cs, ModelProtocolContracts.cs; существующие canonical facts | Два формата labels дают одинаковую canonical resolution; restart/foreign run/collision tests |
| Проекция и reverse resolution | Core Services/ModelResultProjection.cs, ModelContextCompiler.cs; ModelProtocol/ModelProtocolClient.cs и schema builder | На wire нет внутренних call GUID; tool role и user role paired; payload text не меняется; unknown refs rejected |
| Оба consumer | Application/Workspace/WorkspaceConversationService.cs, Office/Services/ConversationModelSession.cs и existing protocol adapter | Одинаковая семантика на workspace/Office-neutral; без отдельного mapper на host |
| Сохранность контекста | ContextCompactionRuntime.cs, ContextWorkingSet.cs, context inspector evidence | Нет перенумерации/перепривязки при folding, compaction, budget refusal, resume; unknown effect не replay |
| Сравнение моделей | Существующий tool_profile_comparison.py, те же edit/copy tasks и byte acceptance | Вначале wire/contracts, затем локальные/cloud пары; отдельно task success и finish; без смены default заранее |

Новый Core .cs файл требует включения в old-style RNAssistant.Core.csproj.
Работу следует разбить по инвариантам, а не менять всё одним generic registry.
История и пользовательские данные не переписываются. Old model contract нельзя
молча смешивать с новым; точный reset/skip/новый run должен быть задан до реализации.
На Windows/Office остаётся отдельная qualification.

## Противоречия и решения перед внедрением

- ADR-0016 §Finish evidence **намеренно** требует runtime tool_call_id и исключает
  новые E-ID/index/store. Требование короткой model ссылки меняет это решение.
  Сохранить запрет второго durable store, но явно разрешить typed transport projection.
- Выбрать revision модельного контракта и форму внешнего result envelope (`ref`
  предлагается вместо служебного `tool_call_id`). Оформление rN не должно стать
  зашитой грамматикой или новым видом authority.
- Зафиксировать scope и стабильность labels при restart/старой истории. Нельзя
  случайно принять одинаковый r1 из другого run; current-run resolver остаётся строгим.
- Не считать сокращение IDs решением всех ошибок модели. В пилотах также были
  неверные resource types, literal capability-search misses и create вместо copy.
  Это независимые причины; нужны отдельные наблюдения.

## Эксперимент с инструкциями не внедрён

До уточнения требования коротких ссылок проверялся кандидат с явной инструкцией
копировать tool_call_id и feedback для rejected finish. Восемь live-попыток дали
4 полных успеха; cloud compact copy и три local задачи не прошли. Это не обоснование
нового default. Кандидат также ошибочно называл literal capability search semantic
query; запрос `copy file` не нашёл существующий files.copy. Все экспериментальные
изменения кода этой задачи сняты; инструкция про длинные IDs не закреплена.

[Полный raw evidence кандидата](evidence/tool-profile-guidance-withdrawn-20261026.tar.gz),
[SHA-256](evidence/tool-profile-guidance-withdrawn-20261026.tar.gz.sha256).
Source patch в архиве — исполнявшийся кандидат, а не текущий production код.
Параллельные пользовательские/другие изменения файлов не откатывались и не коммитились.

## Сверка с RNAssistant_v8_short_refs_call_result_spec — 2026-10-26

Проверен актуальный HEAD `c5eb1618b6b3785204ff398f705a91d0e385d247`.
Рассмотрено приложенное ТЗ short refs; упомянутый в нём отдельный
RNAssistant_v8_1_implementation_spec.md среди текущих приложений не найден.
Сравнение выполнено с активным v8, ADR-0016 и кодом, а не с неизвестной редакцией
родительской спецификации. Код не изменялся, новые live-прогоны не запускались.

ТЗ поддерживается как направление. Оно уточняет прежнее предложение: ссылка
обозначает принятую операцию и её outcome, поэтому должна присутствовать **на обеих
сторонах причинной пары**, а не только в результате. Pending alias существует,
но не допускается как terminal evidence.

### Уточнения к прежнему плану

1. **Durable alias вместо вычисления оформления заново.** В accepted metadata
   сохранять canonical call ID, immutable model alias и chat ordinal в том же commit.
   Mapping в памяти по-прежнему является восстанавливаемой проекцией, не новым store.
   Это уточняет §Требования п.1–3 выше: ordinal можно вывести из полного журнала,
   но уже выданное имя нельзя менять при смене formatter, редактировании видимой
   истории, fork или обновлении версии. High-water mark восстанавливается из всех
   принятых allocation facts, включая скрытые/удалённые из проекции вызовы.
2. **Один owner принятия.** Kernel сохраняет владение canonical call IDs; short alias
   allocation закрепить за общим Core-путём durable acceptance. Использовать существующие
   cursor/CAS и document file lock, а не счётчик на host или в UI. Alias+origin+accepted
   calls всего batch коммитятся до dispatch. Проигравший конкурентный commit не запускает
   tools. Неопубликованный кандидат не является выданной ссылкой; новый durable
   reservation event только ради пропусков в нумерации не нужен.
3. **Три разные формы.** Raw response остаётся `{name,arguments}`; accepted metadata
   хранит оба ID; model-facing historical DTO добавляет короткий `id`. Не добавлять
   `id` в raw schema и не прогонять historical DTO через raw decoder. Сейчас
   ConversationResponseHistoryReader использует ConversationResponseJson для body;
   прямое добавление id в этот body сломает чтение. Canonical body можно сохранить в
   прежней форме, добавив typed alias metadata; model history материализует id из неё.
   Явно обозначить runtime-added history, чтобы модель не переносила исторический id
   в следующий execute. Raw attempt сохраняется неизменным.
4. **Формат гибкий, binding строгая.** Рекомендованное оформление для основного model
   loop — r1/r2; E7 из ТЗ также допустим. Ни prefix, ни числовая часть не используются
   для определения kind/authority. Выданная строка immutable, сравнение точное.
   Политика отображения не требует новой пользовательской настройки.
5. **Допуск ссылки по двум независимым условиям.** Alias должен входить в frozen
   delivered-reference set этого запроса, а canonical result — пройти существующий
   session/run/origin/terminal gate. Одного существования в журнале недостаточно.
   Scope первого этапа — только terminal outcomes текущего run. Исторические aliases
   могут быть показаны как история, но не удовлетворяют finish нового run.
6. **Не считать counter доказательством конкурентной безопасности.** ChatStore уже
   имеет межпроцессный file lock и optimistic revision, а AppendAsync обязан завершиться
   до dispatch. Нужен regression на двух writers одного chat, не только unit counter.
   Fork создаёт новый scope; скопированная история не делает старые outcomes текущими.
   Restore/import проверяют alias uniqueness и high-water mark; конфликт — явный отказ,
   не скрытая перенумерация.

### Где буквальное применение ТЗ было бы ошибкой

- В §4 «вся validation до ID» нужно разделить model/schema/catalog/batch admission и
  live execution guards. Сейчас parser проверяет весь batch, kernel затем создаёт
  GUID, проверяет captured policies и сохраняет ResponseAccepted до dispatch.
  Временный GUID до дополнительной policy-проверки ещё не accepted ID и не эффект.
  Условия документа, confirmation и exact read-back продолжают проверяться на своих
  runtime границах; их нельзя считать навсегда выполненными при приёме ответа.
- «Последовательные мутации» означают разные execute-шаги. Текущее правило одного
  mutation/non-independent call в ответе сохраняется; это ТЗ не открывает write batch.
- DTO результата в примере слишком узок для общего контракта: один result может
  содержать несколько observations, partial coverage, таблицу, дерево или effect.
  На первом этапе сохранить name/status/message/data и текущие typed projections,
  меняя только модельную корреляцию. Не сводить ResourceEvidence к одному resource/content.
- R2/R2@v3 в основном model wire — отдельное расширение Resource Fabric. Inspector
  aliases не подходят как runtime authority. C1/S1 не вводить без владельца критериев.
- У reflection сейчас есть только текстовый decision. Упоминание E7 в прозе не равно
  структурированной ссылке и не должно разрешаться regex-эвристикой. В первом этапе
  это интерпретация без authority. Если нужна проверяемая reflection citation, потребуется
  отдельно согласованный typed field; сейчас он не нужен для исправления finish.
- Native provider-required correlation сохраняется. Нельзя без проверки роли/endpoint
  заменить транспортный tool_call_id модельным alias. Не нужен обязательный третий
  allocator: отдельную binding добавлять только там, где transport действительно требует
  своего ID. Причинная пара не должна повторно появляться как вторая операция.
- «Дать доступные refs при отказе» не означает раскрыть весь журнал. Только bounded
  подходящие terminal receipts в новом frozen request, с tool/status/target. Для
  исправления citation не переисполнять tools и не обнулять run/iteration budgets.

### Рекомендуемая последовательность

1. Дополнение ADR-0016: два ID, chat scope, immutable alias, delivered-set gate,
   раздельные raw/history DTO, current-run finish и native mapping. Зафиксировать
   явную revision нового model contract и совместимость сохранённого format-8 stream.
2. Canonical acceptance metadata + allocation под существующим commit; failing tests
   на rejected batch, append failure, competing writers, confirmation и restart.
3. Общая Core projection/resolver в подготовке запроса для Office/workspace; raw
   schema остаётся без id. Проверить literal payload preservation и tool/user/developer
   roles, json_schema/json_object, frozen repair/fallback и реальные размеры запросов.
4. Compiler/working set/compaction сохраняют релевантные причинные receipts с теми же
   aliases; finish отвергает hidden/foreign/pending refs, даже если alias есть в stream.
   Inspector показывает raw, accepted IDs, result, source evidence и фактический HTTP.
5. Повторить существующие одинаковые задачи на cloud/local. Отдельно измерять task
   acceptance, неверные citations, число коррекций и request size. Short refs не
   считаются доказанным исправлением неправильного выбора tools.

В миграции учитывать **текущие v8-чаты**, не только legacy v7. Предпочтителен явный
переход с новым совместимым session/run boundary после определения wire/stream version;
старые события не переписываются. Старый raw GUID в новом model finish не принимать
как скрытый fallback. Product version — отдельное release-решение.

## Согласованное уточнение и реализация — 2026-10-26

Пользователь выбрал смысловые префиксы: `e` для операции/исхода, `r` для ресурса.
Это заменяет раннее предложение использовать произвольный общий `r` для calls.
Действующий контракт и миграция теперь принадлежат [ADR-0018](../decisions/ADR-0018-typed-model-references.md),
[Conversation v8](../protocols/CONVERSATION_RESPONSE_V8.md) и [Session events](../session-events.md).
Этот аудит остаётся историей оценки, а не параллельной спецификацией.

## Коррекция после проверки коллизий — 2026-10-26

Действует revision 2 [ADR-0018](../decisions/ADR-0018-typed-model-references.md):
`E` — операция, `R` — ресурс, `O` — observation, `Q` — request. Все префиксы заглавные.
Ранние примеры lowercase и гибкого общего префикса выше — история обсуждения.
Коллизии создавали два независимых allocator и группировка ресурсов по display alias.
Inspector больше не выделяет E/R; единый ChatStore registry обслуживает сообщения
и сохранённые запросы. Группировка использует exact canonical identity.
