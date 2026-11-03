# Диагностика: структура, владельцы и проверка

Дата решения: 2026-10-30. Owner: Office diagnostics services / Web diagnostics
presentation. Основа аудита: `098b91cf8b18092f59b9497600275f98a8bde4d3`, чистый `main`.
Это пересмотр информационной архитектуры, а не закрытие прежней qualification.
Protocol, event/CAS и runtime authority остаются у своих canonical owners.

Текущая компоновка и evidence S3a — в [UI Screen Map](ui/UI_SCREEN_MAP.md#структура-запроса--production-2026-11-02) и [Migration Plan](ui/UI_MIGRATION_PLAN.md#s3a-запрос-и-общие-controls-диагностики--production-2026-11-02).

## Аудит до перехода

| Поверхность / вход | Назначение и источник | Действия / дублирование |
|---|---|---|
| Правая панель: Контекст | Выбранный документ, чат, материалы; ChatView snapshot | Открыть материалы. Контекст выбранного объекта теряется при уходе в настройки |
| Правая панель: Возможности | `getAgentArsenal` / workspace `contextReceipt.Arsenal` | Навыки, инструменты, переход в библиотеку. Это текущая доступность, не состав прошлого запроса |
| Правая панель: Использование контекста | API usage + `inspectPromptContext` | Оценка следующего запроса, группы и previews; дублирует окно от кольца токенов |
| Кольцо → Prompt Context Inspector | `PromptContextInspectorService`, отдельный download lease | Бюджет, резервы, источники, raw preview. Второе представление тех же estimates |
| Правая панель: Ход работы; footer ответа | Загруженные runtime projections, exact run/step/call | Краткий итог, шаги, результат инструмента; ссылки в другой раздел настроек |
| Model Context Inspector: actual / preview | `ModelContextInspectorService`, trace metadata, CAS; preview compiler | 5 вкладок, отдельные режимы ширины, история/aliases/download/export. Readable, raw, ресурсы, T/E, composition; отсутствует сравнение |
| Settings → Agent: тест совместимости | Старый совместимый probe через LlmClient | Сохранение настроек и запуск, собственный renderer результатов; частично повторяет A/B/C |
| Settings → Service: связь с моделью | Короткий connection probe | Ещё одна точка запуска модели с неочевидным scope |
| Settings → Service: Протокол модели | `ModelDiagnosticsService` → общий Core scenario runner | Дерево A/B/C, 1–3 повтора, Stop, таблица, inspector, report; собственные raw `pre`, eager formatting |
| Settings → Service: История чата | `ITrajectoryQuery`, run-causal/raw/aggregate/VBA projections | Дерево run → step → action → event, фильтры, cursor, CAS payload, export. Связь с Inspector меняет экран и теряет состояние |
| Settings → Service: Ход работы | `app-logs.js`, ограниченный журнал текущей панели | Фильтры/копирование/очистка. Название совпадает с историей runtime, данные недолговечны |
| Settings → Service: Проверки и данные | Bounded performance samples, CAS health | Timings, audit/GC, reset. Не относится к результату выбранного запуска |
| Результат инструмента / изменения / ресурсы | Existing typed tool-result preview, resource viewer, VBA/text diff | Сохраняются как предметные viewers; не ещё одна диагностика |
| Agent protocol example | Черновик controls в настройках | Учебный пример нового ответа и истории; не сохранённый запрос и не проверка модели |

`app-json-viewer.js` сохраняет типы и точный источник, ограничивает DOM/глубину.
В Inspector же content иногда декодировался как V8 непосредственно UI, а JSON
строка выглядела как объект. Эта интерпретация устранена: readable content помечен
строкой/историей, решение и отказ берутся из typed metadata владельца.

## Карта экранов и маршрутов

Единая кнопка **Диагностика** открывает singleton-вкладку Dockview. Три раздела:

1. **Запуски** — закреплённые document/workspace + chat + run. Вначале итог и
   причина, затем последовательность запросов/ответов. Представление «События» и предметные
   связи доступны как технический уровень того же источника.
2. **Лаборатория** — явный запуск встроенного сценария, ожидаемое поведение,
   assertions, результат, exact/ограниченные request/response. Не использует
   документ. Открытие не запускает модель.
3. **Приложение** — журнал этой панели, задержки и обслуживание хранилища.
   Их состояние не является исходом пользовательской задачи.

Запуски используют общее дерево и один reader рядом (In Review, 2026-11-03).
«Все запуски» рядом с источником снимает фильтр запуска/шага и позволяет выбрать
другой запуск того же чата; при необходимости загружается следующая страница.
Выбор строки показывает итог/причину, действия — результат/аргументы, события —
данные/связи. Нажатие группы также раскрывает/сворачивает ветку, стрелка меняет
только раскрытие; body загружается только по явному действию.
Группа запроса объединяет его ответ и связанные действия; технические границы
шага не создают группы и остаются в служебных событиях. Обзор запуска сразу
показывает запросы и результаты инструментов, обзор запроса — его состав и исходы.
Используется общая таблица, максимум 30 строк до «Ещё», без загрузки тел.
Компоновка, lifecycle и ревью — в [Screen Map](ui/UI_SCREEN_MAP.md#запуски--in-review-2026-11-03)
и [Migration Plan](ui/UI_MIGRATION_PLAN.md#диагностика-и-дерево-запусков--in-review-2026-11-03).

Rail/status bar всегда открывают текущий чат и его последний известный ход.
Возврат в уже открытую Dock-вкладку сохраняет прежний источник/выбор. Вверху
видны document/project и название чата; нажатие открывает этот чат. Другой чат
выбирается действием «Диагностика чата» в существующем дереве Office/проектов/плагинов.
Открытая диагностика не следует за composer автоматически; её чтения и связанные
переходы остаются привязаны к указанному источнику.

Из запуска открывается **Запрос** в отдельной вкладке существующего Dockview (S3a, 2026-11-02). Вначале **Обзор запроса**:
какая попытка, сохранён ли ответ, принят ли parser/runtime, конкретная причина.
Три задачи: **Состав / Ответ / Изменения**. Оглавление Состава ведёт к выбранному сообщению, параметрам, пропускам, ресурсам и доказательствам. Сообщение читается как Markdown/JSON/Текст; точные сообщение/запрос доступны из соответствующих меню. Состав/настройки относятся к сохранённому запросу,
не к текущей форме Settings. Новый response format и history transport раздельны.
«Назад к запуску» сохраняет фильтр, раскрытия, выбранный объект и прокрутку.

Правая панель — рабочий контекст, доступные возможности и краткий ход выбранного
запуска. Ссылки открывают общий маршрут. Кольцо токенов и раздел использования
контекста ведут к одному renderer оценки следующего запроса; прежнее отдельное
окно оценки удалено. Предпросмотр явно подписан «Ещё не отправлено».
Настройки отвечают за будущие запросы: самостоятельные diagnostic results и
кнопки скрытого сохранения перед тестом удалены; остаётся переход в лабораторию.

Основные маршруты:

- Ответ чата → выбранный запуск → попытка с отказом `tool_calls[0]` → точный
  ответ → следующая попытка → сравнение → назад с прежним фильтром.
- Кольцо → фактические API tokens / оценка будущего контекста → сохранённый
  запрос → вход → источник / T/E / ресурс → исходник без подмены типов.
- Лаборатория → негативная schema → ожидаемый HTTP 400/422 → Pass assertion;
  ошибка bridge и отмена показаны отдельно от Fail проверки.
- Диагностика → Приложение → задержки / журнал / CAS по явному действию.

Наглядные ключевые состояния до массовой реализации:
[макеты](../prototypes/diagnostics.html): отказ и повтор, пустой/неполный ответ,
ожидаемый отказ лаборатории; переключаемые узкая/широкая компоновка и темы.

## Границы кода

| Ответственность | Owner |
|---|---|
| Источник истины, admission/effects | Existing event stream + CAS; Core ModelProtocol/AgentKernel/ToolRuntime |
| Запросы, scope, bounded страницы, payload lease | `ModelContextInspectorService`, `ChatDiagnosticsScope`, `ITrajectoryQuery` |
| Read-only summary/comparison | Небольшая projection рядом с ModelContextInspectorService, typed DTO; без повторного admission |
| Сценарии/assertions/transport | `ModelDiagnosticScenarios`, `ModelDiagnosticRunner`, `LlmClient`; Office service — adapter |
| Оценка ещё не отправленного запроса | `PromptContextInspectorService` + existing compiler |
| Навигация / восстановление presentation state | Web diagnostics navigation; только scope/выбор/фильтр/scroll, без durable store |
| Получение тела / отмена | Existing bridge + ResourceDownload; request version и exact scope |
| Отображение | Существующие journal, model context, lab и shared JSON/text viewers |

Не создаются общий diagnostics backend controller, event pipeline, transport или
сценарный engine. Визуальные компоненты используют product tokens, editor tabs,
navigation tree, table, buttons, forms. CSS добавляет компоновку, не палитру.

## Последовательность внедрения и приёмка

1. Снять синтетический baseline чтений/DOM/времени на одинаковых длинных данных.
2. Переключить единый вход, перенести существующие readers, убрать старые routes
   и дублирующую оценку; проверить возврат и привязку Office/workspace/plugin.
3. Ввести typed summary, серверный scope и сравнение сохранённых запросов;
   показать поле отказа, независимые форматы, полноту записи. Payloads по действию.
4. Упростить лабораторию, переиспользовать viewers, сохранить выбранный результат
   при поступлении других; отмена и ошибки не превращаются в model Fail.
5. Проверить основные маршруты на synthetic bridge: rejected call → retry,
   unknown effect, нет тела, partial/cancel/error, stale reply, 320px/1440px,
   light/dark, длинные подписи, клавиатура. Повторить baseline; публиковать числа,
   не обещать ускорения без измерений. Только нужный Harness slice, без Office.
6. Удалить заменённые DOM/CSS/handlers, актуализировать canonical docs и риски.

## Состояние прежнего плана на момент аудита

| Этап | Код и evidence | Остаётся |
|---|---|---|
| P0/D0 baseline | Exact synthetic HTTP fixtures, audit | Реальная Office трасса, budget/unknown цепочки |
| P1/D1 A/B/C | Общие 23 сценария/контроля, typed UI runner, cancellation/redaction | Real endpoints NotRun |
| P2–P5 context/reference | Revision 3 T/E, criterion, compiler/dedup, historical evidence реализованы | Не читать прежние revision-2 baseline строки как текущий контракт |
| D2 context regression | Scripted read/patch/compaction/skills + Harness | Полная exact-wire матрица budget/unknown и реальные модели |
| D3 Inspector | Messages, resources, composition, canonical evidence, raw, navigation частично | В этом scope: summary, request comparison, навигация/типы/состояния. Полная provenance всех смысловых blocks — отдельно |
| D4 Scenario Lab | Встроенные сценарии, повторы, seed, report | Редактор/импорт пользовательских сценариев, persistent history, матрица endpoints — явно вне этого UI redesign |
| D5 Qualification | Host-neutral synthetic checks | Windows x64 / Office x64 / WebView2, real models, restart/confirmation и фактическая latency |

Новый UI не изменяет статус этих gates. Windows evidence возможно только в виде
фото экрана; локальные исправления от него не блокируются.

## Реализовано и проверено — 2026-11-01

Последовательность выше выполнена. Новый вход и reader используют существующие
`app-trajectory.js`, `app-run-journal.js`, `app-model-context.js` и
`app-model-diagnostics.js`. Владельцы новых частей:

- `app-diagnostics-navigation.js` — маршрут и закреплённые owner/chat/run,
  возврат к запросу/запуску/чату; данные не хранит.
- `ModelContextRequestProjection` — summary из trace/commit и ограниченное
  структурное сравнение сохранённых CAS-запросов. `ModelContextEvidenceProjection`
  предоставляет общий проход по принятым сообщениям. Parser trace не становится
  доказательством принятия runtime или эффекта инструмента.
- `app-model-context-details.js` — отображение typed summary/comparison;
  `app-model-context.js` — получение, отмена, выбор и lifetime viewers.
- `LlmClient` — снимок реально действовавших параметров перед dispatch внутри
  существующей трассы. Нет credentials; новый формат ответа и роль истории
  независимы. Для старой записи без снимка показано отсутствие данных.
- `app-context-meter.js` — только кольцо и переход в общий usage renderer
  `app-chat-inspector.js`. Оценка загружается при раскрытии и сохраняет раскрытия.

Удалены отдельный Prompt Context popup, его DOM/JS/CSS и обработчики,
режимы dock/wide/focus для запроса, страницы Service в Settings, самостоятельные
connection/compatibility probes, bridge операции/DTO/сервисы этих probes и их
мёртвые зависимости. Core A/B/C runner остаётся единственным владельцем проверок.
Raw preview download service остаётся нужен предпросмотру следующего запроса.
Новые `.cs` зарегистрированы в старом `.csproj`. Временных adapters нет.

### Ограничения загрузки и состояния

Итог использует metadata, Input/Raw делят одно прочитанное тело. Тело разбирается
один раз на выбранный запрос. Показано 50 сообщений на страницу; JSON viewer имеет
свои пределы DOM/глубины. Сохраняются активное и два последних представления DOM;
остальные viewers освобождаются, раскрытия reader восстанавливаются из небольшого
presentation state. Смена запроса/owner освобождает cache. Изменившаяся metadata
инвалидирует summary/response/resources/evidence; неизменяемые байты не перечитываются.
Скрытие останавливает polling, отменяет pending request/trajectory/lease;
возврат повторяет только прерванное чтение. Поздний ответ не меняет другой объект.

Сравнение: только более ранний запрос того же запуска, до 8 MiB на тело и 8000
сообщений; одинаковые начало/конец, позиционное сопоставление середины, до 200
изменённых сообщений и 40 полей на запись с явной пометкой ограничения. Это
структурное сравнение JSON, не минимальный текстовый diff. SHA-256 ведёт к точным
байтам; JSON-строки не декодируются в объекты. Отсутствующее/повреждённое тело,
дубли JSON properties и дополнительные данные после JSON дают unavailable.

Readable и JSON ведут к исходному сохранённому телу. Обрезка захвата, текстовый
preview большого тела, скрытие секретов в лаборатории и отсутствие снимка явно
подписаны. Полный сохранённый payload ограничен существующим download limit
50 MiB; отмена не означает, что сервер модели отменил уже отправленный запрос.

### Проверки

Все проверки синтетические, без реальной модели и без Office/VSTO:

- Harness `model context:` — 14/14: exact scope, parser/commit, comparison,
  missing/corrupt CAS, cancellation, композиция/источники/доказательства/preview.
- `model diagnostics UI:` — 4/4; `storage: model request trace precedes dispatch`
  — 1/1: typed runner, изоляция настроек, trace до отправки и frozen settings.
- `diagnostics-navigation` — отказ `tool_calls[0].arguments` → повтор → сравнение
  → сообщение → событие → возврат; unknown evidence, строки JSON, отсутствие
  eager payload/model probes, отмена скрытого журнала, late reply, empty/error,
  320/420/1440 px, обе темы и клавиатура.
- `model-context-layout` — прежние проверки композиции, пропусков, границ чтения,
  resource/message links, PDF coordinates перенесены на новый маршрут;
  `model-context` — поведение reader и недоступные/чужие ответы.
- `chat-inspector`, `workspace-inspector`, `chat-composer-layout` — общий usage,
  Office/workspace/plugin owner, возврат фокуса, раскрытия, узкая панель.
- `model-diagnostics-layout` — selection всей строкой/клавиатурой, expected negative
  result, cancel/error, смена сохранённого endpoint без hidden fetch, lazy viewers.
- `run-journal`, `context-usage`, `context-json-viewer`, `performance-diagnostics`
  и `chat-history-layout` — существующие предметные проверки. Старое source-ожидание трёх аргументов
  `appendAgentRunViewState` обновлено к существующему четвёртому аргументу.

Просмотрены реальные product screenshots на synthetic bridge:
[отказ](audits/diagnostics-ui-20261030/rejected-wide-light.png),
[частичная запись · 320 px · dark](audits/diagnostics-ui-20261030/partial-320-dark.png),
[источники · narrow](audits/diagnostics-ui-20261030/sources-narrow.png),
[лаборатория](audits/diagnostics-ui-20261030/lab-narrow.png),
[прогноз контекста](audits/diagnostics-ui-20261030/usage.png),
[состояние приложения](audits/diagnostics-ui-20261030/application-health-narrow.png).
Скриншоты являются примерами, проверки выше проверяют остальные состояния.

### Сопоставимое измерение

`tests/web/diagnostics-performance.test.js`, Chrome 156.0.8078.79 на macOS,
1440 × 900, повтор 2026-11-01 с проверкой загрузки stylesheet; один fixture: 2000 сообщений, 4 473 924 UTF-8 байта. Пять открытий,
по три перехода Input → Raw → Input. До: checkout assets из `098b91cf`;
после: reader с metadata summary и bounded DOM cache. Bridge/download отвечают
синтетически без дискового I/O; измерение включает browser/Playwright ожидания
и отрисовку. Это проверка относительной стоимости UI, не измерение Office,
сервера модели или backend event scan.

| Показатель | До | После |
|---|---:|---:|
| Медиана открытия начального экрана | 24 ms | 9 ms |
| Медиана от открытия до Input | 76 ms | 56 ms |
| Медиана трёх Input/Raw roundtrips | 6503 ms | 1803 ms |
| Metadata / body reads при открытии | 1 / 1 | 1 / 0 |
| Body reads после всех переходов | 1 | 1 |
| Строк / DOM nodes в Input | 50 / 472 | 50 / 472 |
| JS heap после пяти прогонов, GC, открытый reader | 6 765 988 B | 6 857 548 B |
| JS heap после закрытия и GC | 6 766 772 B | 6 858 284 B |

[Исходные замеры до](audits/diagnostics-ui-20261030/before.json) и
[после](audits/diagnostics-ui-20261030/after.json) содержат все samples, включая
первый холодный Input. Начальные экраны теперь различаются содержанием, поэтому
для сравнения одинакового чтения также приведено время до Input. Повторные
переходы стали дешевле за счёт отсутствия повторного parsing/rendering. Память
не уменьшилась: около +92 KB; это snapshot JS heap, без RSS/ArrayBuffer и без
утверждения об отсутствии долгосрочной утечки.

Повтор: запустить тест через Node с Playwright (`NODE_PATH`) и установленным
Chrome (`BROWSER_EXECUTABLE`). `DIAGNOSTICS_PERF_OUTPUT` сохраняет JSON;
`DIAGNOSTICS_SOURCE_ROOT` выбирает отдельную распакованную baseline-копию
`web`, `prototypes` и `src/RNAssistant.Office/Contracts/BridgeOperationCatalog.cs`.
Тест не создаёт checkout и не запускает сборку автоматически.

## Открытые границы после внедрения

- D3: полная provenance каждого смыслового блока и attribution отдельных
  инструкций/навыков/частей смешанных сообщений не завершены. Показываются только
  записанные compiler facts; отсутствующие сведения не достраиваются UI.
- D4: пользовательский scenario editor/import, persistent comparison history
  и endpoint matrix остаются вне этого scope. Встроенная лаборатория реализована.
- Metadata запрос выдаёт ограниченную страницу, но существующий service читает
  полный validated event stream. Стоимость backend на длинной истории здесь
  не оптимизирована и браузерным замером не квалифицирована. Отмена bridge/UI
  защищает доставку и освобождает чтения тел; синхронный scan/сборка preview не
  прерываются внутри своего вызова. Это остающийся предел отмены backend.
- Workspace preview следующего запроса и полная сводка всех источников в верхнем
  кольце отсутствуют. Структурное сравнение — только внутри одного запуска.
- Real endpoints, Windows x64 / Office x64 / WebView2, controller delivery,
  restart/confirmation и реальное наблюдение отказа `tool_calls` остаются открыты.
  Фото Windows может дополнить evidence; локальный UI не закрывает эти gates.
