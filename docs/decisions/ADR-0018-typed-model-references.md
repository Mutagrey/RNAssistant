# ADR-0018: типизированные короткие ссылки модели

Дата: 2026-10-26. Статус: принято пользователем; реализация host-neutral.
Owner: Core ModelProtocol / ChatStore / model context.
Исходный HEAD: `c5eb1618b6b3785204ff398f705a91d0e385d247`.
Предшествующий [аудит](../audits/model-reference-contract-review.md) и пользовательская
спецификация рассмотрены критически. Это решение заменяет пункт ADR-0016 о передаче
канонических call IDs в finish, но сохраняет kernel, guards и журнал.

## Решение

- `E1` — одна принятая операция и её исход. Та же ссылка у исторического вызова и
  результата; до terminal она не годится для завершения. Ошибка/отказ не меняют ID.
- `R1` — логический ресурс в exact authority scope; `R1@v2` — короткое имя одной
  наблюдавшейся ревизии. Номер версии — порядок регистрации наблюдений, не номер
  изменения документа и не признак currentness. Возврат к прежним байтам возвращает
  прежнюю ссылку на ту же каноническую ревизию.
- Префикс фиксирует тип, регистр и написание точные. Он не даёт authority. `C`/`S`
  не вводятся без отдельного owner критериев/шагов. Никакого конфигуратора префиксов.
- Canonical call IDs, ResourceRef, ResourceEvidence, coverage и read-back сохраняются.
  Один `E` может иметь несколько наблюдений/ресурсов. Внутренний EvidenceId не есть `E`.

## Владельцы и durability

ChatStore назначает `ExecutionReference` в том же accepted-batch commit, под
существующей блокировкой и после проверки ревизии. Kernel исполняет только после
успешного append. Alias сохраняется рядом с call ID/origin; raw ответ не меняется.
Неперсистированные кандидаты не считаются выданными, отдельного reservation journal нет.

Resource bindings назначаются при сохранении typed observations сообщений и запросов. Каждое сообщение
содержит только свои bindings. Execution high water и resource index — replay-derived
проекции тех же событий, включая удалённые из видимой истории записи; SQLite и
in-memory replay используют общие reducers. Это не отдельный durable store.
Fork наследует mappings/high water, но новый чат остаётся отдельным scope. Export
не превращает citation в authority другого чата.

## Контекст и finish

Общая wire-only projection работает после canonical compiler и strict projection,
перед бюджетной проверкой и HTTP. В JSON-истории появляется `tool_calls[].id=E1`,
в result/свёрнутой причинной записи — `tool_call_id=E1`. Raw decoder по-прежнему
запрещает id. Native transport сохраняет provider pairing; alias виден отдельно в
narration и результате. Бизнес-текст/аргументы не заменяются поиском строк.

В model-only data чтения и заголовке отдельного source view поле `ref` стоит рядом
с semantic target, representation/view и coverage. Общий список `resource_refs`
удалён: он не связывал ссылку с фрагментом и появлялся даже в квитанции без тела.
Привязка требует единственного typed source observation с совпадающим view/coverage
и exact scope/URI/revision. Порядок bindings и совпадение имени не используются.
Несколько срезов одной ревизии сохраняют один `R` и разные диапазоны.
Поиск/discovery/outline без durable per-item связи не получают придуманные `R`;
квитанции операций сохраняют `E`, а `R` показывается на включённом source view.
Это не аргумент tools: чтение/изменение по-прежнему используют semantic target.
Метки не заменяют coverage/currentness и не дают права читать старую ревизию или писать.
Canonical ToolResult, CAS, events и UI projection не меняются. При compaction
последний terminal receipt текущего run сохраняется как обязательный кандидат.
Весь реестр ссылок в prompt не копируется.

Доставленный набор `E` берётся из фактической успешной попытки запроса, после
projection/repair/fallback. Finish проверяет и этот набор, и canonical accepted /
terminal events текущего run. GUID, `R`, guessed/hidden/foreign/pending refs отвергаются.
Отказ возвращает bounded доступные terminal refs; kernel продолжает тот же run без
повторного dispatch и без сброса budgets. Reflection остаётся прозой без regex-authority.

## Устранение двух владельцев ссылок

Ранний кандидат смешивал постоянные bindings ChatStore с независимой нумерацией
Inspector по LlmRequest. У неизвестной второму счётчику ревизии счёт начинался
с единицы; совпавшая строка затем служила ключом группировки и могла слить разные
ревизии. Разделение `r`/`R` скрывало двух владельцев и отвергнуто пользователем.

Теперь единственный allocator в ChatStore обслуживает typed observations сообщений
и exact scope/resource links сохранённых LlmRequest. Binding запроса записывается
в тот же trace event; после успешного append обновляется derived index. Оба replay
пути применяют один reducer. Последующее сообщение повторно использует тот же `R`.
Наблюдение в диагностике не доказывает доставку модели и не меняет finish admission.

Inspector только читает сохранённые `E`/`R`, проверяет прямое и обратное соответствие
и группирует строки по scope + URI + revision. Собственного resource allocator и
fallback для операций больше нет. При отсутствии binding исходные метаданные видны
без короткой ссылки; ссылка не придумывается. `O1` обозначает observation, `Q1` —
request; эти диагностические ссылки выводятся из порядка immutable request events
и не являются finish evidence. Все типовые префиксы заглавные; `@v` — маркер версии.

## Версии и миграция

Raw response v8 и chat format 8 сохраняются по уточнению пользователя: промежуточные
9/10 не применялись. Reference contract revision 2 и prompt schema 56 описывают
текущий контракт заглавных ссылок внутри format 8. Совместимости с промежуточными
lowercase/missing-binding вариантами не вводится: неподходящая история явно
отвергается перед модельным запросом, без переписывания событий и GUID fallback.
Formats 6/7 остаются историей для чтения. Никакой автоматической миграции, downgrade
или удаления пользовательских данных нет.
SQLite projection version 4 пересоздаёт только восстановимую проекцию.
Product version не меняется; Windows/Office/WebView2 и live-model quality имеют
отдельные gates. Минимальные тесты и фактический статус — в Harness guide / PROGRESS.

## Отображение и оставшаяся работа

Inspector показывает текст сообщений, ответы и JSON; E виден в данных операции,
R — в заголовке ресурса, Q/E/O — в строках наблюдений. Поиск короткой ссылки
открывает соответствующий сохранённый запрос; кнопка источника открывает ресурс.
Краткая легенда объясняет четыре префикса и номер наблюдавшейся ревизии.
Chrome layout и визуальная проверка выполнены на синтетической UI-фикстуре:
[легенда](../audits/evidence/references-v8-legend.png),
[ресурс](../audits/evidence/references-v8-resource.png). Это не Windows/Office qualification.

Лента чата показывает narration, короткое описание действия и результат. Из шага
есть переход в запрос/ответ модели. Отдельных E/R badges и автоматических
кликабельных citations внутри текста ленты пока нет; упоминание E/R моделью
остаётся обычным текстом. Для такого UI нужен typed presentation consumer сохранённых
bindings, а не regex-угадывание ссылок или новый нумератор.

Следующие проверки: повторить model comparison на финальном uppercase контракте,
отдельно измерить качество выбора инструмента и корректность finish; затем решать
file cutover по исходному Tools plan. Windows/Office qualification остаётся отдельной.
