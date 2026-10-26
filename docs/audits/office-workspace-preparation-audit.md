# Office / workspace: аудит оставшихся различий подготовки контекста

Первичный срез: 2026-10-26, baseline `f1d86260`. Owner: Core context/skills/protocol,
Application workspace, Office orchestration. Это был статический аудит исходников;
production-код тогда не менялся, новые модельные/Windows прогоны не выполнялись.
Результаты девяти проверок предыдущего cutover относятся к общему request builder,
а не доказывают полную эквивалентность Office и workspace.

## Повторная проверка контекста и диагностики — 2026-10-26

Owner: Core LLM diagnostics / web Model Context Inspector. Этот срез уточняет
историческую таблицу ниже после объединения профилей и discovery/admission.

| Этап | Текущий общий путь | Различия входов |
|---|---|---|
| CLI / проект / plugin source | WorkspaceConversationService | CLI — клиент того же runtime, отдельного compiler нет |
| Office / workspace | ModelContextCompiler + ContextWorkingSet + ContextCompactionRuntime | Office notes и document authority; workspace filesystem authority и AcceptedFacts |
| Tools и skills | CallableToolProfile, CallableToolSet, admission journal, SkillTaskService | Минимальный Office bootstrap и широкий workspace profile остаются разной policy |
| Подготовка запроса | ModelRequestPreparation → ModelProtocolClient | Доменные инструкции и schema projection adapters |
| Материализация и сохранение | LlmMessageBuilder → LlmClient → trace/CAS → HTTP | Одни сериализованные UTF-8 bytes сохраняются до отправки и используются transport |
| Просмотр фактического запроса | ModelContextInspectorService + app-model-context.js | Exact chat/workspace/event scope; CLI не имеет отдельного Web UI |

Подтверждённый дефект: DescribeWireMessages выбирал первый receipt по ID даже при
дубликате или несовпадении wire index и мог показывать Full с чужой привязкой.
Регрессия до исправления падает: expected Unknown, got Full. Теперь presentation
и оценка состава требуют одного ID и одного совпадающего индекса; неоднозначные
источники не дают полноту, оригинал или evidence. Provider body не меняется.

Фактический Inspector по умолчанию раскрывает раздел → сообщение → содержимое,
показывает доли, оценку токенов и количество сообщений. Ссылки/уведомления ресурсов
выделены отдельно; история сгруппирована по исходным сообщениям. Есть переключение
в порядок отправки, сохранение раскрытий и страницы по 50 без повторного чтения CAS.
Смешанные сообщения считаются целиком, базовая оценка не выдаётся за API usage.

Проверено host-neutral: 12 `model context:` (одна fixture исправлена на полный
snapshot вместо несогласованного подмножества), 14 `context continuity:`,
workspace history в object/strict режимах, trace-before-dispatch, mixed attachment
manifest, frozen evidence и однократная материализация retained reads — 32 проверки.
Web: model-context, model-context-layout и workspace-inspector; Chromium,
светлая/тёмная темы, узкая панель, история, раскрытия, переключение порядка,
точный scope и повторное использование payload. Полный Harness не запускался.

Отдельно найдено [расхождение Office-предпросмотра](../stabilization/RISK_REGISTER.md#office-next-request-preview-diverges-from-active-preparation--2026-10-26):
на момент этого аудита он не проходил selected-skill surface и strict projection.
Следующий host-neutral срез устранил оба расхождения через общие owners и учёл
tool-pack restoration state; [текущий статус](../stabilization/PROGRESS.md#office-next-request-preview-uses-active-skill-and-wire-preparation--2026-10-26)
важнее этой исходной находки. Сохранённый фактический запрос от предпросмотра не зависит.
Полная parity исторических role projections, реальные модели и Windows/Office/
WebView2 не квалифицированы. Потеря данных или двойная отправка в выбранных
проверках не воспроизведены.

Для отдельного Wiki-этапа брать текущие контракты из [карты документации](../README.md),
оперативный статус из PROGRESS и открытое evidence из RISK_REGISTER; таблица
исходного аудита ниже остаётся историческим срезом, а не текущей спецификацией.

Ниже сохранён исходный статический срез `f1d86260`; его «следующий cutover» исторический.

## Вывод

Общие kernel, protocol, compiler, compaction, request preparation и stores уже есть.
Остались разные входы в эти компоненты: callable surface, подготовка authority,
представление принятой истории и lifecycle hosts. Новый runtime-баг этим аудитом
не воспроизведён. Найдены подтверждённые различия и места возможного расхождения;
их нельзя автоматически считать дефектами или удалять как legacy.

Главный следующий кандидат — явный контракт выбора callable tools для Office и
workspace. Сейчас широкий workspace-каталог и минимальный Office bootstrap дают
разные стартовые условия одной модели, даже при одинаковом подключённом документе.
Не надо устранять это механическим копированием одного профиля в другой.

## Карта ответственности

| Участок | Office document chat | Workspace / CLI / plugin source chat | Оценка |
|---|---|---|---|
| Loop и outcomes | ConversationRunCoordinator → AgentKernel | Тот же coordinator/kernel | Уже общее |
| Wire / format repair | ModelProtocolClient и v8 | Те же компоненты | Уже общее |
| Schema, projection coordination, budget, request | ModelRequestPreparation | ModelRequestPreparation | Последний cutover завершён |
| Callable bootstrap | CallableToolPack: пять common tools; в Agent/Excel ещё inspect | В SkillTaskService передаётся весь _catalog | Подтверждённая разница policy |
| Дополнительные schemas | capabilities_read, pending admission, journal, carry-over | Все non-Skill entries поступают как direct input; отдельного такого Office journal path нет | Не эквивалентные стратегии exposure |
| Выбранные skills | SkillTaskService + published skill snapshot | Тот же resolver, Workspace-фильтр | Общий механизм, разные пакеты |
| Skill admission | CompileCurrent с candidate selections | CompileCurrent с candidate surface | Общий расчёт, разная подготовка входов и текст отказа |
| Catalog refresh | Catalog generation проверяется; refresh/rebind и повтор подготовки при RESOURCE_CATALOG_CHANGED | Каталог фиксируется runtime instance; skills читаются из frozen publications перед request | Разные freshness/lifetime contracts |
| Authority | Conversation/document/catalog scopes; host refresh и exact document binding | Перепроверка observed/retained file paths; task/plan reconciliation; capture scopes | Доменные различия необходимы |
| История для compiler | PromptBudgetComposer берёт active window; запись пар через AgentJsonProtocol | AcceptedFacts дополнительно гидратирует и нормализует history/roles | Кандидат parity-проверки, не готовый механический merge |
| Working set после compaction | ContextWorkingSet.Restore с durable session archive по умолчанию | Тот же Restore, но explicit archive после AcceptedFacts | Алгоритм общий; входное представление различается |
| Compaction | ContextCompactionService — wrapper над Core runtime с Office projection/artifact owner | Прямой ContextCompactionRuntime; checkpoint commit и progress публикуются здесь | Второго алгоритма нет |
| Evidence для дальнейшего редактирования | DeliveredObservations + Office mutation preparation/guards | DeliveredObservations фильтруют _observed по exact revision/payload/current state | Сходный инвариант, разные owners |
| Result projection | Office ModelToolResultProjection / тематические handlers | Core projection; bound Office contribution использует Office projection | Часть различий нужна для типов данных |
| Resume / recovery | Document session и host/controller orchestration → общий coordinator | Session lease, PrepareResume/abandoned-run reconciliation, затем explicit continuation → coordinator | Общий kernel, различная инфраструктура запуска |
| Владение содержимым | Document-owned HTML/authored artifacts | Workspace filesystem + canonical CAS/history | Отдельная миграция ADR-0013 |

## Подтверждённые наблюдения

### 1. Общий resolver не означает одинаковый набор tools

В [WorkspaceConversationService](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs)
`SkillTaskService.Compile` получает `_catalog` и как полный каталог, и как directTools.
В [SkillTaskService](../../src/RNAssistant.Core/Services/SkillTaskService.cs) directTools
фильтруются по `Exposure != Skill`; resolver сам не вводит Office bootstrap.
В [CallableToolPack](../../src/RNAssistant.Office/Services/CallableToolPack.cs) Office
выбирает стартовые IDs и управляет дополнительным admission отдельно.

[WorkspaceOfficeCapabilityService](../../src/RNAssistant.Office/Services/WorkspaceOfficeCapabilityService.cs)
добавляет host catalog в bound workspace (за исключением excel.create_chat_chart).
Следовательно, non-Skill Office entries там доступны шире, чем в стартовом Office
чате. Точные binding/confirmation/effect guards от этого не исчезают. Это различие
модельного exposure, а не доказанное повышение прав или обход подтверждения.

Риск: сравнивать качество/стоимость модели между этими чатами без фиксации effective
catalog некорректно. Унификация bootstrap должна быть явным продуктовым решением:
широкий профиль даёт меньше discovery calls, узкий — меньше schemas в контексте.

### 2. История и working set имеют разные входные представления

[Workspace AcceptedFacts](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs)
читает durable accepted-call payload, проверяет v8, переводит calls/results в
настроенный ToolResultRole и превращает finish в пользовательский текст.
[Office AgentJsonProtocol](../../src/RNAssistant.Office/Services/AgentJsonProtocol.cs)
формирует role/correlation при записи; [PromptBudgetComposer](../../src/RNAssistant.Office/Services/PromptBudgetComposer.cs)
выбирает active history без такого же отдельного преобразования.
Оба контура используют Core history readers/compiler.

[ContextWorkingSet](../../src/RNAssistant.Core/Services/ContextWorkingSet.cs) получает
в Office исходный session archive, в workspace — нормализованный archive. Перед
извлечением общего преобразования надо сравнить replay после смены result role,
compaction, hydrating payload, failed/unknown result и возврата сохранённого source.
Двойное преобразование или отсутствие преобразования пока не воспроизведено.

### 3. Authority нельзя заменить универсальным refresh

Файловый путь перепроверяет наблюдённые и сохранённые в истории файлы до заморозки
snapshot; это не выдаёт нового разрешения на mutation. Office rebind привязан к
catalog generation и document/session guards; см.
[ConversationKernelAdapter.Model](../../src/RNAssistant.Office/Services/ConversationKernelAdapter.Model.cs)
и [ConversationModelSession](../../src/RNAssistant.Office/Services/ConversationModelSession.cs).

Одинаковый метод RefreshAll не доказал бы свежесть Office state и мог бы добавить
лишние COM-захваты. Нужна parity инвариантов: смена target, внешний edit, stale read,
прерывание после dispatch. Host-specific acquisition и gates остаются отдельными.

### 4. Compaction не нужно объединять повторно

[ContextCompactionService](../../src/RNAssistant.Office/Services/ContextCompactionService.cs)
оборачивает Core ContextCompactionRuntime. Отличаются domain projection,
artifact publication, authority capture и публикация checkpoint. Новый compactor,
context store или recovery engine здесь был бы дублированием.

### 5. Preview Inspector и фактический request — разные продукты

[PromptContextInspectorService](../../src/RNAssistant.Office/Services/PromptContextInspectorService.cs)
строит preview estimate через canonical schema builder. Общий request builder не
превратил этот preview в фактический будущий HTTP request: runtime state может
измениться. Для диагноза остаются authoritative сохранённые wire bytes и receipt,
а не только preview total. Полную parity preview и active selected skills этот
статический аудит не утверждает; это отдельный контрольный сценарий.

## Следующий cutover: явные профили callable surface

Обновление 2026-10-26: первый extraction выполнен через Core
`CallableToolProfile`: Office сохраняет explicit core/admissions, workspace явно
использует `workspace-direct` для опубликованных non-Skill tools. Общий resolver
skills, admission persistence и policy не заменялись. Текущий контракт и таблица
профилей — в [Tool Library](../tool-library.md#shared-direct-tool-profile-selection--2026-10-26).
Наблюдение выше о `_catalog` как direct input относится к baseline аудита.
Общий discovery/admission затем перенесён в Core и подключён к workspace; текущий
широкий профиль сохранён. Сужение defaults остаётся отдельным этапом сравнения. Поведенческие изменения требуют отдельной проверки
на одинаковом task/model corpus, а не автоматического переноса Office bootstrap.

Сначала зафиксировать различия, затем извлечь только одинаковые правила.
Не добавлять generic Office abstraction, второй registry или второй журнал.

| Этап | Конкретные владельцы / файлы | Проверка и критерий приёмки |
|---|---|---|
| 1. Зафиксировать contract профилей | docs/tool-library.md; Office CallableToolPack; Application WorkspaceConversationService; WorkspaceOfficeCapabilityService | Для Office, writable/read-only workspace, bound Office и plugin записаны точные initial IDs, discovery/skill-only правила и лимиты |
| 2. Parity fixtures до переноса | Program.SkillPackagesTests.cs, Program.WorkspaceOfficeTests.cs, Program.ToolDiscoveryTests.cs | Сравнивать effective IDs, descriptor/policy/binding revisions и admission после load/remove; ожидаемые различия указаны явно |
| 3. Извлечь общие правила выбора | Core SkillTaskService + application composition; profile data остаётся у catalog owners | Убрать неявное использование whole catalog как direct surface; сохранить исходное поведение через явный workspace profile. Старые повторяющиеся selectors удалить после переключения consumers |
| 4. Проверить lifecycle | Тот же registry/runtime; существующие continuation tests | Disabled/missing skill, revision drift, explicit remove, carry-over budget refusal, read-only workspace, target switch и unknown non-replay остаются корректными |
| 5. Только затем менять размер профилей | Согласованный policy + одинаковый task/model corpus | Независимая acceptance, calls/context bytes и ошибки выбора; не обещать улучшение по одному task |

Отдельное согласование требуется **для изменения поведения**: ограничивать ли
стартовый workspace catalog, вводить ли там Office-style discovery admission,
сохранять ли широкий bound-Office profile. Извлечение общей реализации с прежним
поведением не требует изменения продуктовой политики.

Следующий кандидат после этого — role/history projection. Перенос возможен после
парных fixtures; нельзя одновременно менять сериализацию durable history и
семантику compaction. Файловая матрица/cutover остаётся отдельным этапом: сначала
create/copy/patch/replace/delete/restore и preview/data linkage, затем переключение
конкретного owner без dual-write. Не объединять mutation owners в рамках контекстного аудита.

## Evidence и ограничения

Исследован текущий код, canonical architecture/skills/tool library и migration map.
Последний request-preparation cutover уже проверен девятью целевыми тестами;
они не запускались повторно ради docs-only аудита. Live model/Windows/Office/COM
qualification, реальная гонка catalog refresh и сопоставление всех history-role
комбинаций остаются открытыми. Новых воспроизведённых runtime-дефектов не заявлено.
