# RNAssistant backlog

Здесь находится только незавершённая отложенная работа. Это не текущий план:
рабочий baseline, приоритеты и открытое evidence находятся в [PROGRESS](PROGRESS.md),
исторический порядок миграции — в [master plan](STABILIZATION_MASTER_PLAN.md),
действующие риски — в
[RISK_REGISTER](RISK_REGISTER.md), временные adapters — в
[MIGRATION_MAP](MIGRATION_MAP.md). Завершённые этапы остаются в phase/WQ evidence и
сюда не копируются.

Общий feature freeze завершён. Запись здесь сама по себе не означает приоритет:
новая возможность требует явной задачи, scope и owner; дефекты работающей системы
приоритетнее расширения без конкретного пользовательского результата.

## Восстановление рабочих сценариев чата — 2026-10-26

Owner: Application conversation delivery. Основание —
[incident review I26-01…10](RISK_REGISTER.md#разбор-десяти-фотографий-чат-протокол-и-достоверность--2026-10-26).
Аудит и воспроизведения выполнены. Исправлен I26-04: workspace prompt использует
canonical schema projection; actual HTTP catalog и search/read dispatch проверены.
Исправлен I26-01: typed receipt проходит bridge до ответа модели; draft очищается
только после подтверждённой записи user message. Application ownership длительных
submit/approval и восстановление недоступного владельца также исправлены.
Остаток S1b и следующий приоритет — receipt reconnect после reload/потери ответа. Самостоятельные подписи
о результате не заменяют исправление выполнения, доставки или проверки эффекта.

Принято с пользователем: Windows evidence — только фотографии экрана; не просить
логи, выгрузки или файлы. Во время run обычная отправка уточняет текущую задачу
на безопасной границе; отдельное действие ставит следующую задачу в очередь.
Основная лента показывает только принятые шаги; во время генерации — стадию
и время. После завершения ход работы сворачивается, итог и токены остаются.
Это ограниченная очередь исправлений по пользовательским сценариям, без новой
общей архитектурной миграции. После закрытия строк здесь остаётся только
незавершённая работа; текущие контракты переносятся в domain docs.

### Целевая граница

Сохранить существующие AgentKernel/ConversationRunCoordinator, ModelContextCompiler,
ToolRuntime, append-only events/CAS и общий ChatView. Разница Office/workspace/plugin —
authority источника, capabilities и delivery adapters. Статусы, composer, группировка
шагов, usage, история и message actions принадлежат общему presentation contract.
Не нужен второй kernel, store, provider cache или универсальный Office layer.

Путь одного запроса должен проверяться целиком:

`draft version → durable input receipt → delivery → run/step/attempt → frozen request
→ HTTP response → protocol admission → accepted action → dispatch/read-back →
terminal facts → shared view → reload`.

UI не выводит состояние выполнения из текста модели, ответа transport или владения
DOM. Для отсечения запоздавших событий используются существующие session/workspace,
run/step/attempt IDs, cursor и generation выбора. Они не попадают в аргументы модели.

### Порядок срезов

| Срез / приоритет / owner | Конкретный результат и удаляемый путь | Условие завершения |
|---|---|---|
| S0 / выполнен частично / delivery + diagnostics | I26-01…10, 5 UI-проб, 2 HTTP-траектории сохранены. Для Windows сделать читаемый экран: версия DLL/web, endpoint/model/format, стадия и длительность ожидания, код и причина ошибки, подтверждённый эффект. Использовать существующие trajectory/CAS, без второго diagnostic store. | По фото можно различить приём ввода, подготовку, API, tool и terminal outcome. Подробные request/response исследуются в собственных локальных воспроизведениях; их передачу от пользователя не запрашивать. Независимые исправления не ждут Windows evidence. |
| S1a / P0 / Core outcome + Application/Web | Разделить model claim, завершение run и подтверждённые изменения в общей сводке. Подключать существующий typed acceptance там, где заданы проверяемые file/operation criteria. Удалить отображение `satisfied`/clean finish как доказательства эффекта. | Scripted «создал» после read-only остаётся model claim; UI явно показывает фактические 0 изменений. Заданный критерий существования/bytes не проходит. Реальный create+read-back проходит; noop, unknown и ошибка различимы. Прямой информационный ответ без writes остаётся допустимым; никакого regex по словам «готово». |
| S1b / P0 / input delivery + Workspace run control | Receipt, точная версия draft, lease-guarded recovery и независимый lifetime submit/approval исправлены; clear-only-on-terminal удалён. Остаётся automatic receipt reconnect. | Сообщение принято один раз; потеря reply, reload, иной чат и новый draft не теряют ввод и не дублируют run. `DeliveryRecorded=false` виден как незавершённая доставка. Проверено: ранние submit/approval responses, продолжение после потери bridge, duplicate approval без replay, exact Stop, отмена и drain при shutdown; IPC timeout не считается остановкой, recovery перечитывает run/cursor под lease без replay. Cancellation имеет Requested/Stopped/Unconfirmed. |
| S1c / P1 / Application active-run/input delivery | Подключить workspace к существующему `IRunInputChannel` и input journal. Общий composer показывает допустимые send/queue/steer действия по capabilities и текущему run. Удалить blanket запрет ввода после подключения producer и consumer. | Уточнение принимается ровно один раз и доставляется нужному run; очередь переживает reload, не запускает второго writer и не меняет другой чат. Несовместимые действия имеют явную причину. |
| S2 / P1 / ModelProtocol + resource/tool owners | Обход `ForPrompt` удалён, actual HTTP read/find/search schemas проверены. Остаётся полный read→patch цикл и strict provider admission. | Валидные target/query, scope/query, line/character/procedure/PDF selections проходят; несовместимые selectors и stale/ambiguous anchors отвергаются до effect. После read→patch exact diff и read-back совпадают; без десяти одинаковых repair. Invalid schema не маскируется fallback, rejected/truncated calls не исполняются. |
| S3 / P1 / shared Web chat + typed delivery | Один run/status/composer projection для всех owners. Принятый шаг сохраняется и затем компактно сворачивается; provisional имеет явный статус попытки. Общий footer: время, actions, API usage; общий route fork/edit/journal. Удалить отдельную workspace строку «Агент работает…» после переключения consumers. | Одна матрица Office/project/plugin: load, accepted input, preparation, model wait/stream, tool, approval, question, cancel, failure, recovery, completed; narrow/wide, reload, переключения. Поздний A→B ответ не меняет B. Принятый текст не исчезает. Fork создаёт новый chat с нужной границей истории, исходник не меняется. |
| S4 / P1 / delivery + Storage + context owners | Разделить progress repaint, state/cursor refresh и message paging. Удалить полный project tree/transcript render на токен и full-session load ради очередной страницы. Начать с существующих SQLite window/event feed и lazy CAS; затем исправлять только измеренные preparation hotspots. | 30 пустых progress events не перестраивают дерево; update затрагивает stream leaf. Warm/cold switch и first request измерены на одинаковых short/long fixtures; counters показывают bounded page/delta. Before/after p50/p95, allocations, hydrated bytes и UI long tasks сохранены. Состояние и data integrity совпадают после restart. |
| S5 / qualification / endpoint и host owners | Прогнать конечные сценарии с настоящей моделью, затем Windows x64 + Office x64 + WebView2. Версия DLL/web и сценарии закреплены. | Доказаны точные изменения файла и VBA, штатный reopen/navigation/cancel/fork, provider schema acceptance, отсутствие повторного эффекта. Не закрывать по scripted HTTP, fake COM или одному красивому screenshot. |

S1a, S1b и S1c — отдельные изменения; каждый последующий срез также режется по одному
инварианту. Диагностика стадий добавляется вместе с исправляемым путём, до заявления
об ускорении. Не удерживать document gate во время model/user wait. Никакого
автоматического удаления чатов, переписывания event history, сброса unknown или
увеличения всех лимитов как способа скрыть проблему.

Целевой submit возвращает durable receipt до завершения run; lifetime/cancel/dispose
и session lease принадлежат application run owner. CLI ожидает terminal result через
того же owner. Не заменять await на fire-and-forget и не привязывать выполнение
к существованию DOM выбранного чата. Workspace использует существующий input journal
и kernel input port; переиспользуемую intake-логику Office inbox перенести в Application,
оставив Office DTO mapping в адаптере. Не создавать второй concurrent writer.

### UI и токены: критерии, а не новая декоративная перепись

- Одинаковая лента и composer во всех трёх поверхностях. Контекст документа/проекта
  обозначен в заголовке, а различающиеся services не создают отдельный жизненный цикл.
- Во время запуска видна конкретная стадия и время ожидания. При длинном JSON
  argument отсутствие `message` ещё не означает отсутствие генерации. Счётчик
  полученных данных не раскрывает сырой код аргументов как принятый результат.
- Завершённый шаг оставляет стабильную строку с действием, объектом и outcome.
  После завершения run подробности сворачиваются; итог и существенная ошибка видны
  один раз. Диагностика открывается по exact step/attempt, не по текущему чужому run.
- Usage последнего запроса, суммарный API usage запуска и оценка следующего контекста
  различаются. Batch siblings и повторная проекция одного attempt не умножают usage;
  реальные format/provider retries учитываются отдельно. Reasoning/cached tokens
  показываются согласно provider accounting и не прибавляются повторно к total.
  Отсутствующий usage обозначается как неизвестный, не ноль.
- Контекст проверяется по actual request: source identity + revision + view + coverage,
  текстовые и бинарные части, skills, catalog, history, reserves. Совпадение CAS/HTTP
  — отдельный gate; оно не исключает лишний контент. Семантическую дедупликацию нельзя
  делать простым удалением одинакового текста из разных причинных сообщений.

### Как не вернуться к бесконечным точечным правкам

Для каждого среза: **один нарушенный инвариант → минимальное воспроизведение → один
owner → producer и consumers вместе → удаление старого пути → проверка полного
сценария и сохранённых фактов**. Локальная зелёная проверка не закрывает внешний gate.
Новый abstraction допустим только при удалении названного дубля или зависимости.
Размер файла и количество interfaces не являются критерием готовности.

Acceptance corpus ограничен задачами пользователя: read/search/patch с Unicode/CRLF,
create без реального write, invalid schema/arguments, длинный output, lost receipt,
owner death, A→B→A navigation, pending approval, cancel, reopen, fork, long-history
first page и состав actual request. Browser regression должна получать настоящие
DTO/events из application/bridge fixture, а не только вручную придуманный snapshot.
Scripted-model проверка проверяет runtime; отдельная live-model проверка — способность
следовать контракту. Предыдущие неизвестные Windows причины остаются неизвестными.

Ненужные расширения схем, новые UI surfaces и косметический рефакторинг этих путей
не смешиваются с recovery scope. Это локальный приоритет инцидента, не возврат
исторического общего feature freeze. На каждом шаге фиксировать проверенные
результаты в canonical doc и evidence; не создавать очередной общий master plan.

## Аннотации временного и исторического HTML-preview — 2026-10-26

Owner: Web visual annotation UI и Core resource binding. Текущий authored HTML
workspace в «Материалах»/правой панели и iframe плагина используют общий редактор.
Запущенный HTML-блок сообщения и отдельный просмотр старой версии проекта пока
не имеют staging-контракта для их точного источника и revision. Если добавлять
аннотации на эти поверхности, сначала закрепить immutable source и права записи;
не подставлять вместо них текущую версию проекта. Загруженный HTML проходит
явный импорт в authored workspace перед аннотированием.

## Геометрия компонентов демо — 2026-10-25

Owner: Web presentation. При сравнении палитры обнаружены отдельные различия
геометрии, перечисленные в [UI design](../ui-design.md#оформление-уведомления-и-длинный-диалог):
высота миниатюр тем 68 / 94 px, ширина и радиус composer демо. Согласовать
компоновку демо с текущим полноширинным правилом и единым радиусом; проверить
узкое окно. Не возвращать в продукт прежнюю узкую колонку демо. Палитра,
каталог акцентов, вложенный hover дерева и цвета статусов уже согласованы.

## Resource read Harness: устаревшая фраза результата — 2026-10-23

Owner: resource gateway tests. `tool runtime: native resource tools manual and
model paths` ожидает `returned its complete source`, тогда как текущий
`ResourceReadToolHandler` сообщает `returned source text with the reported
coverage`. Согласовать assertion с coverage-контрактом отдельно; ошибка не
связана со схемой `common.resources_find`.

## CLI interactive smoke: подтверждение перед вопросом — 2026-10-23

Owner: CLI interactive test fixture. Полный `tests/cli/interactive_smoke.py`
останавливается в `questions_case`: после `files.create` CLI ожидает подтверждение
записи, а тест сразу ждёт `Choice number`. Проверить нужную политику подтверждения
для этого сценария и согласовать фикстуру отдельно; read-acceptance и browser
acceptance smoke проходят. Это не evidence отказа восстановления результатов tools.

## Plan prompt: устаревшая точная проверка текста — 2026-10-23

Owner: planning prompt tests. Harness `plan mode: filters mutations and keeps
planning tools` ожидает прежнюю фразу `Never substitute chat prose or an HTML workspace`.
Текущий prompt выражает то же ограничение через `chat prose or an HTML workspace
cannot replace it`; обновить assertion отдельной правкой. При проверке Question
остальные четыре случая `plan mode:` прошли; это расхождение текста, не закрытый
Windows/Office gate.

## Shell audit: одиночная JS-ошибка — 2026-10-23

Owner: HTML preview / plugin preview lifecycle.

Один полный rapid-navigation probe дал `Cannot read properties of undefined
(reading 'create')` без stack. Повтор со stack-capture завершил 12 циклов без
page errors. Причина не установлена; при следующей preview-правке сохранить
полный stack и условия воспроизведения, не объявлять проблему исправленной.

## Resource Context — остаток исходного предложения — 2026-10-23

Основание — повторная сверка [исходного MD](resource-context/PLAN.md) на `3b865e14`.
Статус этапов и следующий срез принадлежат
[PROGRESS](PROGRESS.md#resource-context-plan-reconciliation--2026-10-23).
Это незавершённые adapted пункты, не новая архитектурная миграция:

1. **Полнота Resource Inspector.** Owner: existing model-context inspector,
   typed bridge и Web UI совместно с resource providers. Chat-local `R@v/E/O/Q`,
   scope/revision mapping, поиск и начальный read-only resource view выполнены.
   Добавлены exact parent/restore/dependency provenance, metadata payload и явная
   потоковая проверка CAS payload ревизии с временем проверки и scope isolation.
   Для common reads сохранены requested/actual coverage и раздельная полнота
   selection/representation/source от evidence до receipt и Inspector.
   Для принятых common reads теперь сохраняется заявленный провайдером снимок
   kind/MIME/byteLength/views/capabilities; он не доказывает доступность сейчас
   или возможность чтения моделью. Inspector теперь находит до одного
   сохранённого варианта по каждому известному имени view и по явному запросу
   отдельно проверяет его payload/части через CAS. Полный inventory
   сохранённых views/coverage доступен постранично для exact ревизии; выбранный
   вариант отдельно проверяется по CAS вместе с его частями. Для прямого UTF-8
   `text/source` без частей и сериализованного JSON `tree` Inspector показывает
   базовую оценку сохранённого тела по байтам. Для неизменённого
   сериализованного текстового read с одним
   exact evidence есть оценка всего tool-сообщения данного Q; это не измеренный
   usage и не цена одного тела view. Остаются
   cost прочих views, availability live providers, provenance преобразований;
   не подменять unknown предположением. При необходимости проверить старые traces
   и compaction/restart на реальном архиве. UI остаётся производной projection
   над canonical IDs без собственного CAS-доступа или model-facing адресации.
   Diagnostic body download переведён на проверенный CAS stream и file-backed
   последовательную запись без прежнего 50-MiB source ceiling. Для старых WebView
   без file picker остаётся whole-Blob fallback; его memory profile и реальное
   Windows/WebView2 поведение требуют target-host evidence.
2. **Полнота manifest и metadata.** Owner: Core model-context/LLM diagnostics и
   resource providers. Сохранённый смешанный запрос теперь проверяет исходный
   текст/роль и выбранные attachment text/media части по итоговому wire; неверные
   части остаются `Unknown`, корректные независимые части сохраняются. Проекция
   текста после compiler не наследует группу стоимости исходника. Дополнять
   доказуемую атрибуцию остальных преобразованных сообщений,
   coverage будущих специализированных selectors и cost/capabilities конкретных
   views. Model find уже скрывает application-only binary views и сообщает известные
   MIME/byteLength; для прямого сохранённого UTF-8 текста и JSON `tree` есть только
   некалиброванная оценка тела; для одного однозначного read — оценка итогового
   tool-сообщения. Разделение стоимости многоресурсных/структурных/media
   сообщений и глобальной обвязки запроса остаётся открытым. Существующий `Unknown`
   сохранять, когда факт нельзя доказать; measured tokens не подменять estimate.
   Проверка: точный serialized request плюс соответствующие golden fixtures.
3. **Специализированные selectors/views.** Owner: соответствующий provider через
   общий Gateway. VBA procedure name/kind с exact partial evidence реализован общим
   parser для Office и workspace/CLI. JSON Pointer/tree для полного JSON text
   реализован общим streaming selector в Office и workspace/CLI. YAML tree для
   однодокументных источников с адресуемыми scalar keys также реализован общим
   streaming selector; алиасы вне выбранного пути/view пропускаются без раскрытия,
   а выбранные aliases, merge/complex keys и multi-document требуют отдельной
   семантики. PDF page text для chat attachment и workspace file с
   ограниченными символьными окнами и диапазоны полных извлечённых страниц
   реализованы по точному оригиналу; их профиль извлечения сохраняется с evidence.
   Остались image crop, PDF table/figure/bbox и provenance будущих derived views; исходная
   таблица включает также дополнительные Word/Excel/HTML semantic views. Наличие
   raw text/media/pageIndex не закрывает эту матрицу. Каждый slice требует своего
   typed contract, exact revision/coverage, unsupported recovery и теста; OCR не
   объявлять готовым по наличию PDF rendering.
4. **Explorer graph/diff/reread/export.** Owner: existing inspector/UI и trajectory
   exporter. Граф только из событий/refs; revision diff использует существующих
   domain owners. Явное повторное чтение идёт через Gateway и создаёт новое
   observation. Переход из Inspector к существующему redacted trajectory export
   сделан для полного выбранного чата; нового формата/store нет. Граф, revision diff
   и явный reread остаются открытыми.
5. **Общий acceptance и target-host evidence.** Owner: qualification плюс владельцы
   изменённого поведения. Сопоставить acceptance §10 MD с existing fixtures, закрыть
   непокрытые visual/PDF и Explorer cases, измерить representative large-source/model
   workflow и выполнить Windows x64/Office x64/VBE/WebView2 проверки. Не выдавать
   предыдущие точечные Harness/Web прогоны за общий проход. Ранее открытый HTML
   restore fixture закрыт host-neutral исправлением; target-host evidence остаётся.

## CLI resource-preview scripted write fixture — 2026-10-22

Owner: `tests/cli/resource_preview_smoke.py` / scripted `Scenario`. The existing
`views` case passes its resource-view checks but stops at the final model-write
assertion: the scripted server reports `Unexpected request after pause/terminal
result` for a later title request. Refresh the scripted response sequence and
retain the negative write-authority assertion separately. The new isolated
`large_views` case passes without a model request.
The `omitted_read_guard_smoke.py` fixture likewise reaches its blocked result
with zero writes, then rejects an extra title request after its four scripted
responses. Update that sequence independently while retaining the omission guard.

## Устаревшие ожидания в тестах Markdown-секций — 2026-10-22

Owner: Harness / conversation-response schema and artifact ingestion tests.
`resources: Markdown sections preserve exact partial evidence` проходит проверки
секции, но падает в последнем assertion: описание
`properties.message.description` больше не содержит `actual upcoming calls`.
`resources: Markdown sections enforce source bounds` ожидает отказ для
`Truncated.md`, хотя fixture около 1 млн символов ниже текущего лимита
`MaximumTextCharacters=2_000_000` и не получает `TextTruncated`.
Согласовать обе проверки с текущими контрактами отдельно, не менять
resource-read поведение ради устаревших ожиданий.

## Чтение нетекстовых файлов проектным чатом — 2026-10-21

Owner: workspace resource provider и conversation input. Проектный чат теперь
принимает файлы любого формата в папку проекта без потери байтов, но модельный
`common.resources_read` читает только строгий UTF-8: целиком в пределах бюджета
контекста либо точными окнами без прежних пределов источника 1/80 МиБ.
Проверка доступности загруженного текста также потоковая и не отсекает его по
размеру; лимиты передачи файлов через UI остаются отдельными. Для PDF,
Office-файлов и изображений нужен отдельный resource view либо извлечение с явным
статусом полноты; до этого UI не включает их путь в запрос к модели и показывает
ограничение. Не выдавать сохранение файла
за выполненный анализ его содержимого.

## Один табличный resource view — 2026-10-21

Owner: Resource Fabric / HTML data binding / model contract. `table` и `records`
дают одинаковые колонки, строки, coverage и batch-поток. Новые built-in model
reads, discovery и HTML/JS input schemas теперь предлагают только `table`;
сохранённые HTML bindings, v1 transfer packages и старые custom JS inputs всё ещё
читаются через внутренний `records` path. Временный seam и условие удаления — в
[MIGRATION_MAP](MIGRATION_MAP.md#historical-structural-records-view--2026-10-21).
Следующий этап: явно перевести сохранённые bindings и transfer/JS consumers,
затем вместе убрать `records` из provider capabilities и internal reader без
потери данных или скрытого alias. Проверить exact/head binding, derived mapping,
Excel/Outlook/JSON reads, transfer и restart host-neutral. Затем сравнить выбор
модели на тех же fixture; Windows/Office и WebView2 — отдельный gate.

## Устаревшая проверка ссылок built-in skills — 2026-10-21

Owner: Harness skill-contract test. `settings: built-in skill references resolve to
catalogs` проверяет `## Definition of done` и schema-authority текст в четырёх
`OfficeHosts/*Adapter.cs`; действующие built-in bodies находятся в
`BuiltInSkillProvider`. Выборочный запуск падает на неизменённом `ExcelAdapter.cs`
до проверки этих bodies. Перенести assertion на текущего owner и сохранить
проверку exact catalog IDs отдельно; это fixture drift, не Office regression.

Сверка 2026-10-15: актуальные воспроизведения и результаты выборочных проверок —
в [ревью](RISK_REGISTER.md#architecture-and-regression-review--2026-10-15).
Старые counts/failures относятся к названным там ревизиям. Приоритетные
оставшиеся архитектурные slices — ниже; D05/D08 и recovery-часть D06 закрыты в [V8 audit](../audits/v8-architecture-drift-2026-10-26.md).
Закрытые короткие записи сохраняют anchors исторических ссылок.

## Загрузка UI-прототипа при серии перезагрузок — 2026-10-18

Owner: bootstrap `prototypes/ui/index.html` / `app.js`. При серийном обходе экранов
через полную навигацию в in-app browser дважды наблюдалась пустая рабочая область:
`refreshSidebar` не получил `productTree.unmount`, а в другом случае
`product-components.js` не получил `window.Prototype`. Повторная загрузка восстановила
экран; обход через hash-навигацию прошёл без новых ошибок. Причина и воспроизведение
вне автоматизированного browser-сеанса пока не установлены. Проверить загрузку
зависимостей и ответы локального сервера отдельно от изменений визуального стиля.
Это наблюдение прототипа, не evidence сбоя production WebView2.

## Устаревшая проверка Skill Library init — 2026-10-19

Owner: `tests/web/skills-contract.test.js`. Выборочный запуск проходит первые три
проверки, затем требует прямое `state.skills = skillLibraryItemsFromContract(init.skills)`
в `app-chat-session.js`. Этой строки нет и на неизменённом `HEAD`; текущая загрузка
проходит через `applyChatState`/`applyLibraryCatalogState`. Сверить assertion с
действующим путём и сохранить negative controls в отдельной правке теста.

## CLI smoke fixtures lag current admission contracts — 2026-10-14

Owner: workspace CLI tests. On isolated `3cb0b69f` plus the explicit completion
check, three older suites stop before their completion controls: `task_list_smoke`
requests removed `chat` mode; `plan_document_smoke` expects skill-owned Plan tools
in the initial capability list; `skills_runtime_smoke` expects a write to execute
under Ask and pauses for confirmation instead. These failures precede `done` and
are not evidence against the completion check. Refresh each fixture's declared
mode/skill/permission setup and result-order assumptions in a separate change,
then run that suite. Their negative completion controls now have explicit budgets.
Evidence: `/tmp/rna-completion-gate-20261014-8e_qd7c5/{task-list,plan,skills}.log`.

## Replay confirmation fixtures — 2026-10-15

Closed for the three host-neutral confirmation replay fixtures: they now select
`common.skill_authoring` and assert the pending boundary before approval/cancellation.
`kernel replay:` passes 10/10; related protocol-context and causal-trace cases
also use actual skill selection, without changing runtime permission rules.

Remaining owner: controlled CLI fixture.
`application_requests_smoke.py` now explicitly supplies `--auto-confirm on`; the
recorded setup omission is corrected in source. It was not rerun in this review,
so application-request acceptance remains unverified here. The separate current
`kernel replay:` failure is listed in the regression entry below.

## Transcript browser fixture dependency — 2026-10-14

Closed 2026-10-15: the fixture supplies `renderChatSessions` and passes with bundled
Playwright and installed Chrome. Queue placement, narrow themes, editing focus,
scroll and history anchoring are covered. Real Office/WebView2 remains unqualified.

## Plan browser test cache-key assertion — 2026-10-14

Owner: Web chat tests. `plan-document.test.js` stops at its existing expectation
that `app-artifacts.js` uses `artifact-library-lazy-20261002-1`; `web/index.html`
already ships `artifact-links-20261014-1-image-display-1-artifact-tree-20261013-1`.
Align stale asset-key assertions with the shipped graph, then rerun this test.
The authored-HTML viewer/import regression checks pass independently.

## Materials browser fixture dependency — 2026-10-20

Owner: Web material tests. `artifact-catalog-layout.test.js` loads `app-layout.js`
without its `RNAssistantPanelToggle` dependency and stops before geometry checks.
Align the isolated fixture with the shipped script graph; direct Chromium checks of
the materials view at 1280 and 420 px pass.

## Large-source mutation and provider parity — 2026-10-14

Owner: Core `DocumentArtifactStore` and Office Resource Gateway. Document-owned
copy/patch and bounded hunk preview are implemented for supported text/code files.
The reported 11.2 MB JavaScript/vendor-splitting task also needs local extraction
of selected source blocks into new files without echoing entire blocks through the
model. Current tools copy a whole original or accept at most 32,000 characters of
patch arguments; indexed reads alone do not supply this operation. Add bounded
semantic boundary selection against an exact source revision, preserve originals
and verify output bytes before any separate removal from an editable copy.
Ambiguous boundaries must fail explicitly; byte preservation does not prove that
JavaScript modules, dependencies or loading order still work. The initial-request
budget defect is fixed independently in the context compiler.
Remaining work is a measured real-model 10 MiB optimization run, language-specific
syntax checks where suitable compilers/parsers are available, and efficient random
viewer navigation: the current viewer retains preceding text pages while advancing.
Profile peak memory by import/copy/patch/index/view/export stage; the combined
host-neutral fixture varies between roughly 0.5 and 1.1 GiB RSS.
Neither a verified byte-level patch nor a syntax pass proves semantic equivalence
or that all unnecessary operations were removed. The 128,000-character whole-file
comparison cap remains; large files show verified bounded hunks instead.

Owner: CLI `WorkspaceFileService`/provider and Office live providers. Model file
delivery follows the configured input budget; current files beyond the old 80-MiB
cap support exact selected windows and streamed `files.patch`. Patch fragments no
longer have a one-MiB bound. Large `files.create`/`files.replace` stage text and
verify read-back. The internal materialized `ReadText` path and some retained web
snapshots still have separate one-MiB bounds; neither limits selected file reads.
Word, VBA and PowerPoint slide/notes text now use the same model read selectors, selected
source coverage and first-window continuation. Resource-provider whole Office capture still has an
internal bound, and large sources require multiple selected reads. Other Office
representations (including large PowerPoint `source` JSON), imported artifact
index/transfer bounds and Windows Word/VBA/PowerPoint validation remain separate work.
VBA mutation snapshots no longer have a configured size ceiling. Whole-module
processing and replacement remain intentional for patch validation and recovery;
they do not require a whole-source model read or a partial COM write redesign. Workspace
copy/move/delete and managed restore now stream large UTF-8 sources from exact
selected reads; do not claim host qualification from the host-neutral checks.
Repeated indexed reads of strongly compressed sources now reuse one verified
encoding fingerprint; every call still hashes all current stored bytes. The local
>10 MiB/329-part fixture is about 40% faster. Larger/weakly compressed encodings
retain full streaming verification; full traversal is not generally linear in
source size. Further changes need measured representative sources and must retain
missing/corrupt-source refusal; file metadata alone is not authority. Add a 3–5 MiB cross-chat race fixture and
focused provider tests; Windows/Office qualification remains separate.

Owner: Resource Gateway and provider-specific navigation owners. `ResourceCoverage`
and exact cursors already support text, pages, cells and records, but `resources_outline`
currently navigates only indexed document text. PDF rendering has a page selector;
extracted PDF text is flattened with page labels and `PageTextLengths`, without
exact page-to-text offsets. A direct exact-original `pageIndex` text read now
selects one PDF attachment or workspace file page and bounded character windows.
Add an optional typed navigation capability at the provider/Gateway boundary, with coordinate
kinds, total/coverage completeness and bounded continuation across providers;
align page text and page render discovery on one page coordinate.
Keep provider-specific selectors and extraction/render owners; do not add a second
cursor store or put CAS references in model-facing output. Validate partial and
scanned PDFs, page boundaries, stale revisions and cross-provider pagination.

## Artifact UI check fixture drift — 2026-10-13

Owner: Web test fixtures. The 2026-10-15 failures below are historical, not a
current suite result. `artifact-library-projection.test.js` passed; both HTML
preview fixtures now supply `missingVendors`, and the old
`office-chat-20260930-3` assertion is absent from the HTML source/write tests.
The current `run-journal.test.js` working-tree edit also removes the dated asset
key and label assertions; its result is not yet committed or a complete-suite
qualification. Keep the behavioral, lazy mount/unmount and navigation checks.

The remaining concrete asset-key drift is in
`artifact-commit-projection.test.js` and `ui-lazy-runtime.test.js`, which still
expect `office-chat-20260930-3` or `action-severity-20260930-1` while
`web/index.html` ships newer keys. Align those assertions with the current
script graph without weakening their behavior checks. Recheck the previously
reported `chat-history-paging.test.js` closed-run commentary expectation; its
2026-10-15 result does not establish the state of this working tree.

MockDemo's missing `SettingsService.HasPromptMigrationEvidence` fixture was repaired
during unified-agent verification. Its artifact removal/restart assertions still
need a separate `--artifact-transfer-test` run; the successful unified-agent
controller check does not qualify them or real Office delivery.

## Host-neutral Harness baseline failures — 2026-10-13

Owners: Harness fixtures and the corresponding domain owners. The 24 failures at
`10f78f40` and ten `agent:` failures at `0410126e` are historical counts. Subsequent
storage/protocol fixes closed part of that set; do not present it as today's list.
The [2026-10-15 correction](RISK_REGISTER.md#baseline-test-failures-during-architecture-follow-through--2026-10-15)
also does not establish that later changes remain green.

At `a05ffe96`, focused current-tree checks have seven failures: four `agent:`, one
`kernel replay:`, the conversation-port architecture assertion and Common R61
inventory. Exact names/results and the independently reproduced skill-budget
defect are in the [new review](RISK_REGISTER.md#architecture-and-regression-review--2026-10-15).
The Excel fixture expects 19 bootstrap/core schemas instead of the current 20.
The replay fixture excludes every protocol message, conflicting with retained
completed siblings; check exclusion of the incomplete call/result boundary instead.
The VBA/macro failures still need exact request/admission localization; do not
relabel them all as harmless fixture drift or raise budgets just to pass.

Bridge responsiveness in full-suite order, manual VBA and the unselected resource
cutover cases were not rerun; their earlier evidence gaps remain. Next change D09
repairs the specific contract fixtures, then runs their filters. A new full-suite
count is not inferred from this focused review.


## Real-model compaction/resume task completion — 2026-10-07

Owner: CLI evaluation / model context. The completed-receipt boundary defect is
fixed. Real Qwen now reaches checkpoint delivery, process loss, external edit and
same-run resume; unchanged counters, exact task, stale-source exclusion and the
external edit are verified. The task still fails its original 600-second deadline:
only five of eight notes are ready, final review is pending and the receipt has a
literal `\n`. Missing current reads cause guarded mutation refusals, followed by
two denied repeats. Summary source-kind eligibility still does not prove semantic
entailment; some Qwen claims cite unrelated eligible sources.

The primary Gemma comparison is complete. Its aggregate read-budget failure is
fixed; the next real attempt produced all ten exact final files within 437.46 s,
30/38 steps. It needed no compaction, leaving restart/external-edit/resume untested.
It also postponed all per-note read-backs to the final read batch. The canonical
sequence assertion is now implemented: it rejects this trace, while the retained
correctly ordered controlled restart/resume trace passes. Original fixture results
remain unchanged; no full real-model acceptance is established by regrading.

At `24fd92cc`, the unchanged real Gemma attempt again produced 10/10 exact final
files, but finished at 442.72 s before any checkpoint. Only 2/10 final reads and
0/9 prescribed read/write/read-back stages qualify. All 30 HTTP requests retain
the task and latest results; 309 cumulative pair checks pass. Normal memory
pressure and zero new paging do not remove this instruction-following failure.
The original fixture result remains failed; no interruption or resume occurred.

Closure requires the prescribed read/patch/read-back order, all exact final bytes
and current reads, an actual checkpoint/process-loss/external-edit/resume boundary,
retained counters and no replayed effect within 24K/4096, 40/48/600. Do not enlarge
budgets, weaken source authority or count the controlled fixture as model
qualification. Exact source/profile/audits are in
[PROGRESS](PROGRESS.md#fixed-gemma-csv-and-compaction-acceptance--2026-10-07).

## Office conversation-port source assertion — 2026-10-05

Owner: Harness / conversation persistence. `architecture: Office conversation
consumers use typed port` flags 13 `_chatStore` calls for DocumentArtifacts,
performance/header diagnostics and runtime-data reset. All 13 matched source lines
are identical in baseline `cf662c60`; the Task List slice did not change them.
Audit each call against the typed conversation port and artifact/CAS ownership,
then update consumers or the narrowly scoped source assertion. Do not suppress the
entire test. Other four architecture checks passed; no Office qualification implied.

## Outlook compact catalog count assertion — 2026-10-05

Closed by current fixture changes; `tools: compact catalog rejects removed aliases`
passes on 2026-10-15. This does not close the separate Common R61 inventory mismatch
or real Outlook qualification.

## Source-inclusion harness guard and SDK projects — 2026-10-05

Closed 2026-10-14. The Harness keeps explicit includes mandatory for old-style
projects and uses MSBuild evaluation for SDK compile items, including defaults,
conditions, globs and exclusions. Missing explicit SDK files and empty compilation
are rejected. The repository check and disposable positive/negative fixtures pass;
no Office/VSTO project is built or evaluated. See the [Harness guide](../../tests/RNAssistant.Harness/README.md).

## CLI question argument adherence — 2026-10-05

Owner: model context / CLI evaluation. Initial runs of the fixed `questions_task.md`
failed with Gemma 12B before any accepted question. Native tool results led to an
invalid file reference selector, then missing option descriptions through bounded
format repair. Textual user results led to invalid file/root selectors and no-tool
progress. Both actual-wire pair audits passed; neither run reached answer admission.
Subsequent wire inspection confirmed a schema-conversion bug: `description` was
required but its declaration removed by recursive annotation stripping. This is
fixed with a failing-then-passing actual-wire regression; details are in progress.
The first schema-only native rerun still failed on file reference/root selectors.
The subsequent declared selector-contract change closes file/skill alternatives
and explains root selection. With the same task/profile/bounds, native Gemma now
passes: read, one question, explicit test-user Table answer in a new process,
fresh read, accepted done. No format/pair errors or user-file changes. This narrow
reproduction is resolved; autonomous clarification quality and general adherence
remain open through full M4/M5 acceptance. Runtime mode/answer contracts are covered
separately by scripted production CLI checks. Earlier failures do not establish
missing results or a context limit. Evidence and exact limits are in progress.

## CLI final-read acceptance after CSV repair — 2026-10-05

Owner: Runtime acceptance / shared model context. After the target-bound read
recovery fix, the unchanged Gemma CSV run created three files, recovered two
identical failed patches after exact reads, and passed all 24 browser checks.
It then returned `done` without reading HTML/CSS or the final JS revision.
`AssessAcceptance` correctly rejected it: 0/3 current complete file reads,
verified current web snapshot. The follow-up was not run. The read criterion is
already present in `WorkspacePorts.Prompt`; this is not evidence of lost results
or an absent task contract. Exact traces/profile are in [PROGRESS](PROGRESS.md).

The subsequent slice now exposes typed read-progress metadata in every required
frozen context, shares its currency predicate with final assessment, and fixes a
confirmed false pass after an external edit without `--expect-files`. Regressions
cover duplicate/stale reads, frozen repair, a new run and premature `done`.
Nevertheless, the next unchanged Gemma task again ended after 24/24 browser checks
with 0/3 final reads. Its last HTTP request includes the remaining count and paths;
matching Ollama renderer/config inspection found no dropped second system message.
Transport evidence does not prove model attention. Traces/metrics are in progress.

The shared published skill catalog and file/web verify-repair instructions are now
connected. A narrow explicit skill/file task passes with real Gemma, including
recovery from an invented referencePath. The fixed CSV rerun still skips skill
reads despite both metadata entries being present, reaches only 17 passed browser
checks (1 failed / 6 not run) and is cancelled at the unchanged 600 s wall bound.
There is no evidence here of autonomous skill selection or CSV completion.
Preserve task/checks/bounds in subsequent model/endpoint comparisons, final
freshness and separate browser/read evidence; do not lower acceptance or add a
second loop to obtain a pass. Closure still needs
autonomous creation with current final reads, then the unchanged feature follow-up
under fixed bounds. Target-bound read recovery is fixed and tested; it no longer
blocks a correctly read identical mutation.

After the shared schema correction and closed file/skill selectors, another
unchanged CSV run timed out at 600.12 s: 9 browser checks passed, chart clipping
failed, 14 were not run and final reads were 2/3. The model still skipped both
skills. HTTP retained all call/result pairs with zero format errors. An identical
verification after a `changed:false` replacement was correctly blocked; a later
real replacement occurred before timeout. Do not remove that guard to force a
pass. Swap use increased substantially on the non-isolated Mac, so this run does
not separate model quality from execution speed/capacity. Creation and follow-up
acceptance remain open; source/profile/result evidence is recorded in progress.

The subsequent memory experiment disabled the local runner's retained prompt
cache (previously about 8 GiB), kept 32K/f16 and the exact RNAssistant binaries,
and sampled paging/pressure throughout. Both fresh runs had zero new swap-out and
normal pressure. Original 16/24 limits ended at `iteration_limit`; a separately
declared 32/48 diagnostic still timed out at 1200 s. The final source passes 17/24
browser steps but clears its invalid-CSV status immediately in `resetUI`.
An independent read-only verification reproduced that defect. Memory cleanup did
not establish autonomous completion; do not attribute these remaining failures
to missing RAM or lost results without new evidence. Full traces and bounds are
in progress; the expanded budget does not replace the original acceptance result.

At HEAD `1c457044`, the fixed `user`-role CSV task again passed 24/24 browser
checks but ended with 0/3 final reads, despite explicit current requirements in
the actual request. A paired diagnostic changed only result role to native `tool`:
one file was created, followed by three repeated no-tool `continue` responses,
and the kernel stopped at `no_tool_progress`. Two responses also violated v6.
Both runs had normal pressure and zero new swap-out; call/result pairs and the
latest result were present on wire. No compaction was needed. These observations
do not establish a context-loss bug or a fix through native role selection.
Keep the user-selectable role setting explicit, and retain the fixed acceptance
for the next cross-family comparison. See the 2026-10-06 evidence in progress.

The fixed Qwen 3.5 9B comparison exposed a shared schema/parser mismatch: the
strict schema admitted multi-mutation responses, which the parser rejected.
The schema now shares the explicit batch authority and excludes those arrays.
In the paired run, protocol rejections fell from 3 to 0; Qwen created all three
files and performed four verified patches. It reached 12/24 browser steps but
exhausted the unchanged 16-iteration budget after a final unverified patch, leaving
2/3 current reads. Category changes still lost sort order in the last verified
snapshot; the final patch's functional outcome is unknown. No follow-up ran.
This closes the schema mismatch, not fixed CSV acceptance or model reliability.
All latest results remained on wire, pressure was normal and no new swap-out was
observed. Source/profile/HTTP and paired evidence are linked in PROGRESS.md.

At `24fd92cc`, Gemma's unchanged 16/24/600 run timed out after 9 iterations /
8 tools. A verified repair now permits the identical browser check to dispatch,
but checks regress from 12/24 to 1/24 and current final reads remain 0/3. The
feature follow-up was not run. One 4096-token response contains 106 `renderUI();`
occurrences inside malformed patch JSON; it was rejected before tool dispatch.
This generation-local repetition is distinct from repeated tool execution. All
latest results remain on wire. There was late warning pressure but no new paging;
do not attribute timing solely to the model or memory. Full evidence and unchanged
closure requirements are in
[PROGRESS](PROGRESS.md#fixed-gemma-csv-and-compaction-acceptance--2026-10-07).

The GPT-OSS preflight at `6fc913d4` passes simple v6 JSON at 16K/32K, but cannot
start the fixed task: 16K fails initial compiler admission (5647 message tokens
vs 3196 budget), and the tested 32K profiles have sustained warning pressure.
No new paging occurred; the full CSV task/follow-up were not run. Its reasoning
metadata offers low/medium/high, and requested `none` still produces reasoning.
This provides no new task-quality comparison with Gemma. A reference run needs
compatible protocol plus qualified input/memory capacity; retain the original
task/checks/bounds. See [preflight evidence](PROGRESS.md#gpt-oss-input-and-endpoint-qualification--2026-10-07).

## CLI continuation after completed-operation receipts — 2026-10-05

Historical isolation below precedes the pair/role and typed recovery fixes.
The newest remaining acceptance failure and concrete next slice are above.

Owner: model context / CLI evaluation. The fresh Gemma 12B counter task after the
shared-compiler integration created `index.html` then repeated the same creation;
runtime stopped it and acceptance failed. Traces retain both the verified change
and subsequent errors as the compiler's existing completed-operation receipts.
Isolation found no missing wire messages: all five requests in a fresh captured run
matched the frozen compiler output. Seven single-request probes selected the next
file with either receipt role; same-seed streaming/non-streaming output also matched.
They do not prove autonomous completion. A full Gemma run still stopped after two
files; reasoning on repeated the first create and timed out. GPT-OSS failed protocol
before any tool, so it did not provide a continuation comparison.

The concrete missing-attempt diagnostic gap is fixed: CLI now uses the common trace
writer for actual HTTP requests, rejected bodies and attempt/snapshot correlation.
That isolation had not established a receipt-role cause. A later compiler test
confirmed unconditional folding of the latest mutation and ignored CLI result-role
selection; both are now fixed. Closure still needs a real multi-step
create/read/verify result with the same production compiler and preserved mutation
guards. Run metadata and local evidence are in [PROGRESS](PROGRESS.md).

Further isolation: GPT-OSS's installed template drops `developer` instructions.
CLI now honors the existing `SystemPromptRole` setting and pins it across approval.
With `system`, GPT-OSS sees the correct tools but still emits provider-native calls
instead of v6 content; explicit `json_object` and `tool_choice=none` probes did not
fix that. Gemma's fresh `system` run still ended after two writes and three empty-call
`continue` responses, with input/output far below their limits and no new swap-out.
Keep the protocol gate strict; do not raise budgets or rewrite receipts without
evidence. GPT-OSS continuation quality remains unmeasured.

The smaller M5 counter-repair case is now fixed and passed one real Gemma run:
failed browser results carry exact source evidence, and the common compiler marks
superseded diagnostics as historical. Deterministic tests cover source changes and
wire-visible outcomes; the same accepted task then completed with one write and
9/9 checks. The earlier failed run contained every completed result in its HTTP
context (73 presence checks, no missing/duplicate results), so this case was not a
missing-step transport bug. Metrics and evidence are in [PROGRESS](PROGRESS.md).
Broader creation/continuation failures above remain open: this one successful repair
does not establish that every historical loop has the same cause or that receipt
roles must change. A new reproducible case should compare durable outcomes,
compiled messages and actual wire requests before changing history or prompts.

The retained CSV-check slice adds a fresh reproduction: Gemma 12B created only
`index.html` and repeated create until the fixed 16-iteration bound. All 16 wire
requests were captured; 120 cumulative checks found no missing or duplicated
terminal results, including the first verified write and target-exists/repeat
errors. It never reached CSV verification; the feature turn was not run. No
protocol rejection or token-cap hit occurred; swap usage/out count increased.
Keep cause open: this evidence does not prove either a weak model or a context
projection/renderer defect. Use the recorded frozen request for a controlled
continuation comparison before changing roles, prompts or budgets. See current
[progress](PROGRESS.md) for exact profile, hashes, metrics and temporary evidence.

## SDK project source-inclusion audit — 2026-10-05

Closed with the [source-inclusion guard correction](#source-inclusion-harness-guard-and-sdk-projects--2026-10-05).
This heading is retained for links from historical evidence.

## Large Excel analysis without full model context — 2026-10-21

Owner: Excel read/analysis domain and Resource Fabric; stages 4/5 of the unified
plan. Priority: before further Excel mutation-destination cleanup, per the user's
large-range requirement. Implemented: metadata-only discovery, large-range
`structure` overviews with exact sample targets, and bounded local selected-column
counts/numeric aggregates through `excel.summarize_columns`. The existing 2000-cell
context preview cap remains. The analysis covers every selected row, with at most
2000000 selected cells per call; it is a sequential observation, not a frozen source.

Next contract work must distinguish two intents: exact row/column inspection and
local computation across all qualifying rows. Current `table/records` selectors
capture the whole source first; pushing selectors into the Excel reader requires
exact source/selection coverage and row/column identity, not silently sampling that
existing result. For further analysis, evaluate reuse of the existing local tabular
execution owner against live Excel data before adding another tool/runtime. Keep
source capture bounded and cancellable, propagate missing chunks/drift as failure,
and return compact typed results plus exact coverage. A sample cannot certify
totals, uniqueness, missing values or absence of anomalies. Current selected-column
statistics do not establish uniqueness, filtering, grouping or values in unselected
columns. No automatic whole-workbook scan, larger raw snapshot limit or model-driven
row-by-row sweep.

Host-neutral checks cover a sparse wide extent, long cell text, bounded captures,
numeric/text distinctions, late-chunk failure, coordinate drift and early
cancellation. Dense long tables, changed values during traversal, representative
Windows/Excel throughput and live-model behavior remain external evidence. Measure
backend captures, model payload size and aggregate correctness separately.

## Large Excel search without manual range slicing — 2026-09-30

Owner: Excel search / Resource Fabric. Literal queries up to 255 characters without
newlines now use bound Excel `Find`/`FindNext`; the prior whole-scope snapshot limit
no longer applies to that path. Regex, longer literals and other exact snapshot
reads still reject scopes above 100,000 cells or one million characters. Recovery
uses sheet UsedRange inspection and explicit smaller ranges. Evaluate runtime-owned
bounded chunk traversal only for searches that still need it, with exact coverage,
revision/drift handling and an explicit incomplete result if a chunk fails. Keep
per-chunk capture bounds; a single larger snapshot increases COM time and memory
without solving coverage. Real Excel `Find` behavior remains Windows evidence.

## Large resource working set and compacted action memory — 2026-09-29

The shared working-set/action-memory implementation is complete host-neutral; its
remaining live validation is tracked in the
[continuity incident](RISK_REGISTER.md#agent-continuity-and-deterministic-read-loops--2026-10-01).
The PDF-specific bounded-view extension below remains a separate Resource Fabric slice.

Owner: Resource Fabric / model context compiler / context compaction. A model-facing
`common.resources_read` assembles a complete text view when it fits the model
request budget. A direct exact-original `pageIndex` selector now supplies bounded
PDF page text and character windows in Office and workspace; `startPage`/`pageCount`
supplies consecutive complete extracted pages with continuation. Search matches remain
discovery evidence, not a substitute for an exact selected text view. Remaining work is
visual/table/figure selection and budgeted working-set
policy. Keep extracted originals in CAS and older read findings in
provenance-checked claims; never label an excerpt as the entire PDF or silently
drop a selected view.


## Outlook attachment follow-ups — 2026-09-08

Owner: Outlook domain / Resource Fabric. The authorized PDF/text/image slice does
not include DOCX/XLSX/PPTX extraction, archives, embedded .msg/OLE items or mailbox-wide
attachment search. Any extension requires a separate approved scope. Windows delivery
and real model/COM checks remain open under the existing qualification gates.

## Outlook exact mail from a large folder — 2026-09-30

Owner: Outlook backend / Resource Fabric. When a folder has more than the 500
discovered items, an `Outlook mail` title cannot be proven unique against older
messages, so its semantic read returns `resource_scope_incomplete`. The folder
collection now resolves independently and supports `latest:N`, but previews do
not replace complete bodies or attachment reads. Provide a bounded exact lookup
or runtime-issued disambiguating semantic target with full-folder uniqueness
evidence before enabling those reads; preserve the no-EntryID model boundary and
verify on real Outlook. Until then, use the mailbox chat archive scan for full
mailbox analysis.

## Resource read prompt wording — 2026-09-28

Source defaults are corrected in prompt schemas 33–34: reuse known semantic
targets, allow the documented row/section/line selectors, and keep opaque paging
state runtime-owned. Focused prompt/schema checks cover the current contract.
Current settings migration selects the new defaults and preserves a recovery
copy of prior prompt texts before saving. Owner: Agent/Chat prompt defaults and Resource Fabric.

## Complex Agent planning and model evaluation — 2026-09-28

Owner: Conversation application / Agent planning / model evaluation. Source changes
allow Agent to use the existing revisioned document Plan after bounded read-only
discovery; `startNew=true` creates an independent Plan for another task. Task List
is an optional, revisionable projection of agent assessments and remaining work.
It preserves history and exact current context without source-hash or final gates. No second outcome store was added.
Host-neutral tracking/context/blocked-final checks are implemented; target-model
evaluation remains open.

Representative target-model scenarios must cover Excel/VBA source inspection before
dashboard design, two differing table layouts mapped to one semantic schema,
source change/refresh, failure without placeholder replacement, Task List replanning, no-op decisions and
remaining-work continuity, and Skill/Tool authoring only after the primary result works.
Score accepted calls and retained evidence, not final prose alone. Harness stubs
remain contract regressions; they cannot establish model usability or Windows
Office/WebView2 behavior. Uploaded XLSX ingestion, if required for sources outside
the bound workbook, is a separate scope: current chat attachment import does not
accept binary spreadsheets.

## Compaction helper prompt contract drift — 2026-09-28

Owner: context compaction. The default now requests
`claims[{kind,text,sourceIds}]`, matching the appended instruction, JSON schema
and parser. Schema 34 also removes the contradictory demand for readmission after
compaction. Authored custom helper text is preserved; focused prompt/context checks
cover the defaults, while saved helper text must be reviewed separately.

## Artifact pagination integration evidence — 2026-10-02

Closed host-neutral 2026-10-15: the complete
`artifact-library-projection.test.js` passes, including historical selection and
paged head metadata. Windows/WebView2 qualification remains separate.

## Resource context fixture drift — 2026-09-08

Closed by the [resource projection correction](RISK_REGISTER.md#resources-links-and-artifacts-review--2026-10-12).
Rechecked 2026-10-15: `artifacts: prompt uses` passes 2/2, including bounded working
set and logical heads. No Windows evidence is implied.

## Plan operation identity edge case — 2026-09-08

Owner: document artifact mutation domain. `PlanDocumentService.CreationId` hashes
chat/run/step without call id. The current in-flight singleton-mutation boundary
rejects multiple model mutations in one response before dispatch, removing the
reported Agent/Plan batch route. HTML receipts already include call id; the former
combined Plan/HTML statement is stale. Before admitting another same-step Plan
caller or changing batch policy, make Plan operation identity call-scoped and
explicitly handle prepared/persisted receipts without replaying unknown effects.
This is a bounded future guard, not a verified lost write in the current model path.

## Remaining bridge duplication — 2026-09-29

**Closed host-neutral — 2026-10-14.** Owner: Office bridge presentation / Web
artifact UI. Messages now expose exact `ArtifactIds`, including HTML checkpoints.
The backend validates owner/revision once and includes those cards in each page;
the UI URI parser and duplicated message runtime refs are removed. Exact history,
tombstones and media checks pass. Measure response size and rendering on the
affected Windows chat before attributing its stalls to this former duplication;
Windows/Office/WebView2 delivery remains unqualified.

## Structural debt

Рефакторинг начинается только вместе с конкретным изменением, которое он упрощает.
Для каждого slice заранее фиксируются удаляемая зависимость/старый путь и локальная
проверка; количество строк или `partial` не является основанием.

| Debt | Owner и проблема | Условие начала | Результат и проверка |
|---|---|---|---|
| D01 — compact current docs | Documentation owners. `architecture.md`, master plan и `PROGRESS.md` содержат длинную историю и повторяют часть domain docs | При изменении соответствующего canonical contract; перенос истории — только с backlink inventory, без прежнего общего R61/16.1 gate | Current docs оставляют layers/owners/flows и актуальный статус; история остаётся evidence. Проверка затронутых local links/anchors |
| D02 — single composition path | Application. `AssistantController` одновременно orchestrates и конструирует concrete storage/runtime/model/catalog/session/diagnostics graph | Следующее конкретное изменение production lifecycle/dependency graph; Windows evidence остаётся отдельным gate | Один existing application owner владеет construction и dispose order. Новый composition type допустим при удалении нескольких construction paths; без factory/interface только ради DI. Targeted lifecycle + architecture checks |
| D03 — typed bridge projections | Office Contracts / Web. Versioned operation catalog, shared cancellation/waits and init/navigation/progress casing are implemented in A3; embedded Core DTOs still have both-case readers | Next change to the specific embedded projection, with exact C#/JS shape identified | Switch the typed producer and consumers together, delete aliases, preserve opaque tool JSON. Remaining seam and removal gate: [MIGRATION_MAP](MIGRATION_MAP.md#runtime-commit-boundary--2026-10-15); Windows WebView delivery stays separate |
| D04 — change-driven hotspot extraction | Владельцы Controller, ToolRuntime, prompts, storage и UI. Крупные файлы затрудняют локальные изменения, но не доказывают смешение ответственности | Только когда ближайший approved change требует чтения несвязанного behavior | Извлекается один тематический owner из `AssistantController*`, `OfficeToolExecutor`, `PromptContextInspectorService`, `ChatStore*` или крупного `app-*.js`; старый path удаляется, targeted tests сохраняют контракт |
| D06 — incremental projection | Core persistence. Общий append outcome/recovery реализован 2026-10-26; save всё ещё клонирует Messages и перестраивает derived bodies | После измерения touched operations, latency и RSS длинной истории | Использовать existing typed operations, удалить только измеренно лишние clone/rebuild; сохранить append faults/replay parity, без второго store и обещаний O(1) |
| D07 — stable chat storage identity | Core chat storage / application binding. Document reassociation требует metadata append + `File.Move`; `MoveDocument` может перенести только часть чатов | Отдельное решение о layout до заявления crash-safe Save As; совместить с M7 partition seam из MIGRATION_MAP | Предпочтительное направление: стабильный путь по ChatId, document binding только в canonical events/catalog projection. Удалить физический rename из обычной перепривязки; не добавлять mutable mapping store. До cutover нужен явный отказ/перенос старого layout без fallback. Проверить crash boundaries, весь набор чатов, destination conflict и холодный catalog |
| D09 — contract-owned regression fixtures | Harness/Web test owners. Hard-coded counts/cache keys, неполная composition и старые replay ожидания скрывают полезный сигнал | Новые воспроизводимые failures текущего HEAD; исторические семь Harness/четыре Web не считать текущим baseline | Проверять membership/semantic boundaries и script dependency graph; fixture composition строить через существующие helpers с явными policy/budget. Удалить дублирующие magic counts/датированные asset assertions, сохранив negative controls для removed aliases, source inclusion, lazy rendering и incomplete exchanges. Запустить только затронутые filters/scripts |

Не проводить общий предварительный split/rename, массовый namespace move, новый
service locator, второй store/read model или универсальный Office abstraction.

## User-deferred source allocation — 2026-09-07

The user initially deferred two corrections. Inspector JSON serialization is now
fixed host-neutral in its canonical owner; only Outlook remains here. Bounded
transport alone does not qualify source allocation.

- Outlook backend/resource owner: `MailItem.Body` allocates the whole COM string
  before rejecting its size. Revisit with a concrete large-mail Windows failure
  or before claiming bounded-source qualification; keep real Outlook gates open.

## Document artifact discovery/recovery — remaining authorized slices

- Artifact resource owner / slice 3: model discovery now pages current roots through
  the existing ordered authority Heads projection, with bounded metadata hydration
  and generation guards. Working-set picker/history still need paging. Cold replay,
  ordered insertion and full-authority consumers still scale with journal size.
  Markdown/Plan and uploaded extracted-text section/chunk views now use existing
  revision/CAS retention. HTML member views now reuse the same engine beneath exact
  parent revisions. Unique ATX section reads now support document Markdown/Plans
  and complete uploaded Markdown; allocation qualification remains open;
  HTML discovery still loads/parses its bounded aggregate even with a warm index.
  No parallel durable library/search store.
- Partial model discovery is implemented host-neutral (2026-09-08): missing/corrupt
  current metadata, bodies and unknown heads preserve healthy matches with explicit
  incomplete coverage. No uniqueness/negative inference from unavailable scopes;
  generation drift invalidates continuation; same-generation metadata recovery is visible to a fresh scan without shifting existing source slots. Exact reads stay
  strict. Markdown/Plan search now supplies bounded heading context and preserves
  search-only descriptions; richer compiler context remains open.
- PDF extraction/read coverage follow-up: the extractor may stop exactly at the
  character bound with remaining pages while `TextTruncated` stays false. Uploaded
  indexed search now detects omitted pages from retained page metadata. Qualify
  producer and exact-read coverage propagation separately; text search does not
  assert visual/image coverage.
- Resource authority / journal recovery: whole-authority capture failure or invalid
  runtime identity still fails explicitly. Per-resource projections cannot invent
  lost authority records. Reconcile only from validated durable evidence; no latest
  fallback, automatic mutation replay or data deletion.

## Chat / diagnostics UX follow-ups — 2026-09-07

The user-authorized 11E presentation slice is implemented host-neutral.
Lifecycle/history separation, RunId grouping,
semantic targets, one current-action shimmer, disclosure retention and readable
cause cards with lazy technical JSON replace the reviewed presentation paths.
Typed Resource/Capability result summaries now survive transcript budgeting and
replay; the UI no longer parses their result JSON for captions.
Canonical behavior: [conversation projection](../conversation-protocol.md#effect-mapping-and-ui-projection)
and [trajectory query](../trajectory-query.md).

Remaining bounded follow-ups:

- **Per-effect reconciliation:** “resolved/using an existing sheet” still requires
  correlated target/inspection evidence. Do not infer resolution from model prose
  or later unrelated success. Native counts/history and unknown-effect warnings
  remain intact. Owner: kernel/evidence projection in a separately scoped slice.
- **Delivery qualification:** local browser component scenarios cover narrow
  320/400/600px layouts, themes and disclosure transitions; actual Office/WebView2,
  DPI, keyboard focus during live replacement, multi-window replay/confirmation
  and large live histories remain Windows gates. No real Office build was run.

## Deferred product decisions

Эти пункты требуют отдельной пользовательской задачи и prioritization; они не
становятся обязательными из-за старого Phase 12 плана.

- **Storage lifecycle:** retention/pruning для chats, payloads, artifacts, VBA
  snapshots и exports; явный re-key; VBA-journal export. Любое удаление остаётся
  reference-aware и fail-closed.
- **Replay and evaluation:** reproducible replay/eval fixtures и aggregate
  latency/token/cost/outcome projections из canonical journal, без второго
  telemetry truth.
- **Persistence seams:** текущие `IConversationStore`/`IEventStore` и disposable
  SQLite уже реализованы. Новый generic persistence/blob port требует отдельного
  consumer и удаляемого пути; ближайшее конкретное сокращение — D06, не второй store.
- **Desktop UX/runtime:** direct wrapper-to-pipe activation, docking modes и более
  широкий typed tool surface — отдельные slices. Controlled macro injection
  требует отдельного safety design, Trust Access detection и confirmation.
- **VBA Designer/FRX:** полная поддержка возможна только как новый protocol с
  export/import, CAS и visual/state verification. Текущий CodeOnly contract не
  расширяется скрыто.
- **Pipelines:** execution/discovery/storage/UI остаются отключены. Возврат возможен
  только отдельным решением через текущие ToolRuntime/contracts, без legacy formats.

## Release-gated maintenance

- Проверить VSTO/ClickOnce update/install и assembly binding на Windows до изменения
  historical `AssemblyVersion=16.0.4.0`; рекомендацию `16.0.0.0` автоматически не
  применять. Владелец контракта — [VERSIONING](../operations/VERSIONING.md).
- Проверить prepare/sign/finalize workflow на release workstation. Обычный commit
  его не запускает; точный gate — в
  [RELEASE_PROCESS](../operations/RELEASE_PROCESS.md) и R19.

Windows/Office/live-provider checks, включая R21/R24/R25, перечисляются только в
`PROGRESS.md` и `RISK_REGISTER.md`, чтобы backlog не становился второй матрицей
qualification.

## Screenshot architecture review — 2026-09-07

Read-only source review; these are bounded follow-ups, not a new cutover or a
claim of measured UI cost. Current priority remains chat navigation/persistence.

- **Result representations:** owner `ModelToolResultProjection` /
  `ConversationModelSession`. Family-specific JSON cleanup and exact-read exclusion
  from generic artifact wrapping still exist. Results above 8192 characters are
  archived through `ResultPayload`; selected non-folded results now hydrate the
  complete CAS payload when the calibrated request budget permits. The 2026-09-07
  correction removed the unused pre-archival model-message copy and capability-
  admission wire parsing for ordinary results; capability reads retain exact
  pre-archival descriptor checks. Before replacing a family cleanup path,
  measure parse/clone/serialization cost;
  preserve one execution result and exact evidence, and remove its old projection
  branch with focused wire/provenance checks. Do not add a second durable store.
- **Provider intent routing:** owner `ResourceGatewayService.Intent`. Explicit
  Excel/Word/PowerPoint/Outlook type branches remain. A future approved provider
  extension may introduce a narrow semantic-resolution capability and remove the
  corresponding branch, with ambiguity/coverage/exact-binding tests. No generic
  plugin platform or provider expansion is scheduled by this review.
- **Tool binding lookup — closed 2026-10-14:** catalogs now supply source-owned
  bindings directly; the native handler validates the captured binding before
  registration. `DirectToolBindingCatalog` and its consumers are removed.
  Domain model-result projection remains the separate measured follow-up above;
  human documentation and model descriptions retain distinct purposes.

The screenshot's free-form-only compaction claim is obsolete: compaction requires
claims with valid sourceIds and attaches source messages/evidence/generations.
`ModelContextCompiler.Compile` consumes atoms/evidence and bounded CAS payloads;
no direct Excel/VBA/PDF/HTML execution was found. `BuildPreview` still accepts an
Office adapter and delegates prompt composition; this is a boundary to watch, not
proof of a domain-executing compiler monolith.

## Reported upstream model HTTP 502 — 2026-09-08

The user's run-log screenshot reports HTTP 502 Bad Gateway from the configured
model endpoint alongside an unrelated incomplete Excel name-catalog read refusal.
The local resource-routing defect is corrected separately. The photograph does not
establish the 502 cause or a causal link to the resource failure. Retest the workbook
with the corrected build; if 502 recurs, correlate the failed request with server/
gateway diagnostics. No speculative model-setting changes or mutation replay.
