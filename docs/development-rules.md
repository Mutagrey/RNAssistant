# Правила разработки RNAssistant

Статус: канонические постоянные инженерные правила проекта.

Этот документ определяет общие правила ответственности, контрактов, размещения
кода, изменений и проверок. Он не копирует точные domain/protocol contracts и не
задаёт временный порядок стабилизации.

Краткая карта владельцев и правило выбора документа находятся в
[docs/README.md](README.md).

## 1. Владение документацией

| Документ | За что отвечает | За что не отвечает |
|---|---|---|
| Этот документ | Постоянные инженерные правила и Definition of Done | Точное runtime-поведение отдельного domain |
| [Architecture](architecture.md) | Текущие слои, зависимости, владельцы и основные потоки | История миграции и evidence |
| Domain/protocol docs | Точный текущий контракт соответствующей области | Общий процесс разработки |
| [Stabilization master plan](stabilization/STABILIZATION_MASTER_PLAN.md) | История маршрута переноса и прежних gates | Очередь текущих задач или постоянные правила |
| [Progress](stabilization/PROGRESS.md) | Рабочий baseline, текущие приоритеты и непроверенное evidence | Новый источник архитектурных правил |
| ADR | Причина и контекст принятого решения | Оперативный статус реализации |
| Phase/evidence reports | Исторические команды и результаты проверки | Текущий контракт |
| README | Установка, запуск и пользовательский обзор | Каноническое описание protocol/runtime |

Если документы противоречат друг другу, не выбирать удобную трактовку молча.
Для поведения исправляется domain/protocol doc, для текущих приоритетов — начало
`PROGRESS.md`, для общего правила — этот документ. Исторический ADR или phase
report не отменяет более новый канонический контракт.

## 2. Приоритеты решений

В порядке убывания приоритета:

1. Сохранность пользовательских данных.
2. Отсутствие ложного успеха.
3. Детерминированность и fail-closed поведение.
4. Наблюдаемость и восстановимость.
5. Простые границы и один владелец состояния.
6. Тестируемость.
7. Производительность и совместимость.
8. Ширина функциональности.

Нельзя сохранять fallback или совместимость, если они скрывают ошибку, меняют
target или делают внешний эффект недоказуемым.

## 3. Границы и ответственность

| Контур | Владеет | Не владеет |
|---|---|---|
| `RNAssistant.Core` | Модели, storage, LLM transport, ModelProtocol, pure parsers/algorithms | Office, COM, VSTO, WinForms, WebView2 |
| Application façade/controller | Начало и продолжение use case, leases, cancel, confirmation, передача typed результата | Domain semantics, COM workflow, JSON protocol repair |
| `ToolRuntime` | Exact id lookup, schema/policy validation, confirmation gate, dispatch/effect record | LLM provider, UI, внутренние правила Excel/VBA/HTML |
| Tool handler | Тонкий typed adapter от tool contract к одному domain owner | Полный workflow domain или второй store |
| Domain service | Предметные правила, guards, mutation boundary, read-back и domain outcome | UI rendering, provider protocol, выбор активного документа |
| `RNAssistant.OfficeHosts` | Bound Office session, STA/COM, host identity и interop backend | Model loop, chat lifecycle, пользовательская интерпретация результата |
| Resource Fabric | Identity, canonical URI, revision, bounded list/search/read и CAS references | Execution authority, confirmation, mutation outcome |
| Persistence | Append-only факты, CAS, replay и projections | Выбор следующего шага и вывод успеха операции |
| `web` | Typed UI state, ввод и presentation | Tool dispatch, effect inference, durable state |

Новый domain не должен требовать изменения `AgentKernel`. Изменение UI не должно
менять правила выполнения tool. Добавление resource provider не должно менять
ModelProtocol.

## 4. Контракты и владение состоянием

- У состояния, решения и durable факта должен быть один владелец. Остальные слои
  получают typed command, result или projection.
- Межслойные и bridge-границы типизированы. Anonymous payload, ad-hoc `JObject`
  parsing и произвольные строковые статусы не добавляются как application contract.
- Model-facing schema описывает semantic intent: цель и действие, понятные модели
  и человеку.
- `ResourceRef`, URI, revision, cursor, offset, UUID, content hash, collection
  fingerprint, confirmation и prepared guard принадлежат runtime. После
  переключения семейства они не входят ни в model-facing schema, ни в обычный
  Tool Result/`RUNTIME_CONTEXT`/history projection: exact значения остаются только
  в typed durable event и execution evidence. Модель получает semantic target и
  предметный результат, а не opaque handle; замена `ResourceRef` на новый
  `candidateId` запрещена.
- Исключения ограничены exact public tool/skill id, когда это реальная выбираемая
  semantic identity, и runtime-generated `tool_call_id` транспортного Tool Result.
  `tool_call_id` нужен только для сопоставления принятого вызова с результатом,
  никогда не является argument и не создаётся моделью.
- Resource — данные, Tool — действие. Resource read не выдаёт execution authority.
- Safety-critical решение не принимается по словам в model message, exception text
  или UI label.
- Контракт должен быть минимальным и закрытым. Большой union из несвязанных
  действий не лучше множества избыточных tools.
- Fallback, alias, compatibility adapter и формат совместимости всегда явны. У
  временного adapter есть owner, consumers, причина и ближайший removal gate.

Текущая детальная граница model-facing tools определена в
[Tool Library](tool-library.md), а Resource Fabric — в
[resource-fabric.md](resource-fabric.md).

## 5. Protocol и внешние эффекты

- Protocol version меняется атомарно: parser, schema, prompts, history preflight и
  consumers переключаются вместе, без dual-write и скрытой нормализации.
- Текущий model response — conversation-response v6: `message`, `action` и
  `tool_calls`; call содержит только `name` и `arguments`. Runtime назначает IDs.
- `action=tool` требует непустой `tool_calls`; `continue` с пустым массивом
  сохраняет шаг и продолжает цикл, с ограничением повторов. `done`, `blocked`
  и `needs_input` завершают ход без вызовов. Эффект ответом модели не доказывается.
- `ok` означает успешное выполнение контракта tool, но не обязательно изменение.
  Изменение доказывается отдельным dispatch/read-back evidence.
- Возможный внешний эффект после dispatch, который нельзя подтвердить, имеет
  состояние `unknown` и никогда не повторяется автоматически.
- Write/mutation recheck выполняется непосредственно перед dispatch, а результат
  определяется read-back или явной невозможностью его получить.
- Provider retry, protocol repair и tool retry — разные политики. Model repair не
  может повторно исполнить уже принятый tool call.
- UI lifecycle, execution health и model narrative остаются независимыми.

Точный wire contract хранится в
[conversation-protocol.md](conversation-protocol.md) и
[CONVERSATION_RESPONSE_V6.md](protocols/CONVERSATION_RESPONSE_V6.md).

## 6. Persistence, target и concurrency

- Единственный durable источник чата — append-only typed event stream. Session,
  history, headers, UI state и trajectory являются replayable projections.
- Большие неизменяемые payloads хранятся в SHA-256 CAS. Производная SQLite-проекция
  чатов допускается по [ADR-0012](decisions/ADR-0012-chat-sqlite-projection.md):
  её можно удалить и пересоздать из JSONL; она не становится источником истины.
  Другой durable индекс или mutable snapshot требует отдельного решения.
- Materialized model request сохраняется до network dispatch.
- Write target выбирается до run и закрепляется за exact document session.
  Переключение активного окна не меняет target уже принятой операции.
- Guard, preparation, dispatch и read-back одной document operation сериализуются
  одним владельцем. Gate не удерживается во время ожидания модели или пользователя.
- Recovery восстанавливает факты, но не переисполняет возможный внешний эффект.
- Удаление CAS разрешено только после полной fail-closed проверки reachability.

Точные durable contracts определены в
[session-events.md](session-events.md) и
[cas-maintenance.md](cas-maintenance.md).

## 7. Файлы и физическая структура

- Размер файла — сигнал для оценки, а не формальный gate. Цель — одна понятная
  ответственность и локально проверяемое ближайшее изменение.
- `AssistantController` содержит orchestration и composition, но не reusable
  domain behavior. Самостоятельное поведение выносится в тематический service.
- Tool handlers и host adapters остаются тонкими; сложный workflow принадлежит
  domain service.
- `partial` допустим для организации façade или как короткий механический шаг, но
  сам по себе не создаёт новую границу ответственности.
- Не создавать `Common`, `Manager`, `Engine`, общий callback-bag или универсальную
  state machine без конкретной границы и действующих consumers.
- Новый `.csproj` нужен только при реальной dependency/platform boundary.
- Folder задаёт тематического owner, но не требует массового namespace rename.
- Сохраняются C# 7.3 и .NET Framework 4.8. Новый `.cs` явно добавляется в old-style
  `.csproj`.
- Host-neutral код не размещается в VSTO/add-ins. `web` остаётся static, без
  npm/bundler.
- Secrets не хранятся в репозитории; API key остаётся под DPAPI CurrentUser.

Рефакторинг оправдан, только если следующее конкретное изменение после него можно
понять и проверить без чтения несвязанных областей. Снижение числа строк само по
себе не является результатом.

## 8. Изменения и миграции

Обычный change затрагивает один domain, его релевантные tests и только необходимые
docs/UI projections.

После основного архитектурного cutover обычная работа идёт в `main`
([ADR-0011](decisions/ADR-0011-working-baseline-and-trunk-development.md)). Ветка на каждую
задачу не нужна; отдельная ветка оправдана параллельной разработкой, рискованным
экспериментом или явным PR. Наличие старой `stabilization/16.1` не запрещает
исправления в `main`; незавершённые чужие изменения нельзя автоматически переносить,
коммитить или удалять. Приоритет — реальные дефекты и качество работающей системы.
Новая возможность требует конкретной пользовательской цели, владельца и проверки,
но не разрешения из исторической фазы. Release qualification не блокирует обычный
bugfix; непроверенное Office поведение не объявляется квалифицированным.

- Source-generating tools и patches проверяют итоговый owner/module после правок,
  а не только отдельные hunks. Изменённый source не должен оставлять duplicate
  top-level procedures, declarations или заменённые legacy copies, если сам язык
  явно не разрешает такую форму. Модель может удалить superseded duplicate только
  когда intended final implementation однозначен; иначе tool завершается
  fail-closed вместо угадывания, какая копия должна победить.

Предпочтительная последовательность для смены контракта:

1. Зафиксировать рискованное текущее поведение, если без этого оно неочевидно.
2. Ввести минимальный новый контракт.
3. Переключить всех consumers текущего slice.
4. Проверить новый путь.
5. Удалить заменённый implementation, fallback, alias и obsolete tests.
6. Обновить канонический документ и текущий progress.

Introduce/adapt/switch/delete не обязаны быть четырьмя commits. Проверенный
switch/delete допустим одним атомарным изменением. Длительное сосуществование
legacy/new/fallback runtime запрещено.

Не смешивать behavior change с массовым форматированием, rename, новой product
feature или работой другого domain. Найденный соседний дефект фиксируется в risk
register/backlog, если он не угрожает данным и не создаёт ложный успех.

## 9. Тестирование по риску

Цель тестов — защитить значимый инвариант и дать локальный сигнал о регрессии, а не
достичь процента покрытия. Процент coverage может быть диагностикой, но не является
Definition of Done.

### Что обязательно проверять

- Pure parsers, schema validation, reducers, guards и нетривиальные algorithms.
- Изменённое поведение: нормальный сценарий и значимая ошибка.
- Write/mutation: отказ до dispatch, успешный read-back, no-change и
  failure/unknown после возможного dispatch.
- Исправленный дефект: regression на его причину на ближайшей устойчивой границе.
- Model-facing schema/discovery: deterministic contract checks; representative
  model/live eval только когда изменение заявляет улучшение выбора, аргументов или
  prompt usability. Scripted правильные аргументы не доказывают понятность модели.
- Persistence/replay/CAS/concurrency: повреждение, interruption и recovery там, где
  изменение затрагивает эти состояния.
- Security, permissions, bounds и destructive operations.
- Versioned wire/bridge/API contract при изменении его формы.

### Что не тестируется автоматически по умолчанию

- Trivial getters/setters, константы, простые constructors и механические DTO
  mappings без отдельного риска.
- Поведение framework/vendor, которое проект не изменяет.
- Каждая внутренняя ветка после того, как публичный инвариант уже надёжно покрыт.
- Косметический CSS/layout без логики.
- Generated/VSTO metadata обычным host-neutral unit test.
- Один и тот же инвариант во всех слоях только ради увеличения coverage.

### Минимальная достаточная проверка

| Тип изменения | Проверка |
|---|---|
| Только документация | Diff и затронутые links/anchors; без build/harness |
| Локальное Core/Office-neutral поведение | Минимальный релевантный harness filter |
| Static UI или typed bridge | Релевантный JS/contract test |
| Model-facing contract/usability | Contract tests и назначенные representative eval scenarios |
| Несколько подсистем или общая инфраструктура без достаточного targeted coverage | Full host-neutral harness |
| COM/VSTO/controller delivery wiring | Windows x64 + Office x64 + VS 2022 gate |
| Release milestone | Только явно назначенная release/qualification matrix |

Full harness не заменяет Windows/COM проверку. Число файлов или assemblies само по
себе не требует full run. Успешный результат переиспользуется только при неизменных
релевантных sources, tests, dependencies, build settings и environment.

Команды и filters перечислены в
[Harness README](../tests/RNAssistant.Harness/README.md).

### Сравнение моделей

Модель — конфигурация environment/profile одного RNAssistant runtime, а не
вариант реализации агента. При переключении допустимо менять только `BaseUrl`,
`Model`, лимит контекста, reasoning/thinking и capability metadata. `AgentKernel`,
`ModelProtocol`, `ToolRuntime`, tool schemas, skills, workspace, правила проверки
и acceptance tests остаются теми же. System prompts меняются только по отдельной
причине, не ради адаптации к слабой модели.

| Модель | Назначение | Стартовый контекст (Mac 24 GB) |
|---|---|---:|
| `gemma4:12b` | Основная локальная development: ежедневные agent loop, tools, workspace, HTML/JS | 32K |
| `gpt-oss:20b` | Другое семейство: cross-model regression prompts/harness/tools | 16K; 32K только после проверки памяти |
| `qwen3.5:9b` | Adversarial robustness: JSON, неверные tools, преждевременный `done`, recovery и защита от ошибочных действий | 32K |
| `qwen3.8:27b` | Milestone и production-parity qualification | Сначала 8K; 16K только после проверки памяти |
| `gemma4:cloud` | Сильный reference для сложных задач и разделения дефекта RNAssistant от ограничения локальной модели | По условиям endpoint; фиксировать фактический лимит |

Обычный цикл: `gemma4:12b` ежедневно → `qwen3.5:9b` после значимого изменения →
`gpt-oss:20b` для cross-model regression → `gemma4:cloud` для сложной/reference
задачи при необходимости → `qwen3.8:27b` на milestone. Успех сильных моделей
при провале 9B может быть нормальным пределом модели; не усложнять harness
эвристиками ради прохождения всех задач 9B.

Перед сравнительным прогоном завершить run и сохранить session/event log. Затем
выгрузить предыдущую тяжёлую модель (`ollama ps`, при необходимости
`ollama stop <model>`), выбрать новый профиль, создать новую test session или явно сбросить
model context, проверить endpoint и точный model tag/digest, записать context и
reasoning mode в run metadata. Запустить ту же задачу с одинаковым исходным
workspace. Не переключать и не обновлять model tag в активном run, кроме
отдельного failover-теста.
После переключения проверить `ollama ps` повторно. На 24 GB Mac держать в памяти
только одну тяжёлую локальную модель; не увеличивать context до максимума модели
автоматически. Ollama OpenAI-compatible endpoint получает `num_ctx` из локального
Modelfile; RNAssistant должен иметь такой же или меньший лимит планирования.

Для сравнения зафиксировать RNAssistant commit, task, исходный workspace, tool
catalog, skills, acceptance tests и по возможности temperature/reasoning policy.
Для каждого run сохранить model/tag и digest, context, thinking/reasoning mode,
число LLM и tool calls, protocol violations, tool errors, retries, final action,
результат независимой acceptance-проверки и duration. `action=done` не является
критерием успеха. Каждый провал пометить хотя бы одной категорией:
`MODEL_ERROR`, `PROTOCOL_ERROR`, `CONTEXT_ERROR`, `TOOL_ERROR`, `HARNESS_ERROR`,
`STATE_ERROR`, `VERIFICATION_ERROR`, `ENVIRONMENT_ERROR`. Повторение на нескольких
сильных моделях — повод исследовать RNAssistant/harness; изолированный провал 9B
сам по себе не требует новой runtime-эвристики. Проверки host-neutral не
закрывают Windows/Office/WebView2 qualification.

## 10. Документация изменения

- Текущее правило живёт в одном canonical document; остальные документы ссылаются
  на него и не копируют подробный контракт.
- `architecture.md` описывает текущую систему, а не хронологию фаз.
- README не содержит второго normative protocol.
- ADR сохраняет решение и rationale; его не переписывают под текущий статус.
- Phase/evidence report остаётся историей проверки.
- В начале `PROGRESS.md` находятся только рабочий baseline, текущие приоритеты,
  открытое evidence и необходимые ссылки. История не становится обязательным контекстом.
- При смене canonical document старый явно отмечается superseded и удаляется после
  обновления consumers/links. Массовое перемещение документов не смешивается с
  изменением runtime.
- Не создавать новый `roadmap`, `audit`, `followups`, `notes` или `cleanup` файл,
  если информация помещается в canonical doc, `PROGRESS`, `RISK_REGISTER`,
  `MIGRATION_MAP`, `BACKLOG` или ADR. У нового долгоживущего документа заранее есть
  owner, ссылка из `docs/README.md` и retirement condition.

## 11. Definition of Done

- Изменение находится в разрешённом scope и имеет одного владельца.
- Контракт минимален, типизирован и не добавляет hidden fallback/dual-write.
- Значимое изменённое поведение проверено минимально достаточным способом.
- Для write/effect честно различены verified change, no-change, error и unknown.
- Заменённый путь и мёртвые зависимости удалены; оставшийся adapter документирован.
- Новые/перемещённые `.cs` включены в old-style project.
- Обновлён владеющий canonical doc; краткий progress меняется при смене общего
  статуса, приоритета или qualification evidence, а не после каждого commit.
- Непроверенный Windows/Office/release gate явно остаётся открытым.
- Обычный commit не меняет product version и не создаёт tag.
