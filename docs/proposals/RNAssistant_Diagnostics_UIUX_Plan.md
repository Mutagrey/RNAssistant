# RNAssistant — новый интерфейс диагностики
**UI/UX-план для агента · 23 октября 2026**

## Цель
Заменить перегруженные визуальные журналы единым интерфейсом: за секунды понимать, что модель получила, решила, вызвала, что реально вернули инструменты, что показано пользователю и почему запуск остановился. **Это только визуальный план**, без переделки AgentKernel, Resource Fabric, контрактов ответов и хранилища.

## Исходники для аудита
Актуальный найденный GitHub baseline `Mutagrey/RNAssistant` — `0fcf3e9409612673aa1d586cfd4b69a5ce9047de` (сверить HEAD перед изменениями). Проверить:
- `web/js/app-trajectory.js`, `web/js/app-trajectory-payload.js`, `web/css/app-trajectory.css`;
- `web/js/app-model-context.js`, `web/js/app-context-inspector.js`, `web/css/app-context-inspector.css`;
- `web/js/app-chat-inspector.js`, `web/js/app-diagnostics.js`, `web/index.html`, `web/css/app-chat.css`;
- `src/RNAssistant.Office/Services/TrajectoryPayloadService.cs`, `src/RNAssistant.Office/Controller/AssistantController.Diagnostics.cs`;
- `docs/trajectory-query.md`, `docs/trajectory-export.md`, `docs/resource-fabric.md`.
До удаления старого UI составить карту DOM, bridge calls, listeners, exports, потребителей, тестов. Логи, CAS, payload leases и backend authority НЕ удалять.

## 1. Дизайн-принципы
1. Summary first: по умолчанию только смысл действия, результат, время и статус; raw JSON — по запросу.
2. Единый чат Office/Workspace/Plugin; единый компонент активности.
3. Реальная причинная последовательность, не объединять разные вызовы с одинаковым текстом намерения.
4. Разделять **исходный ответ модели**, parsed decision, message, tool calls/results и UI-сообщение.
5. Не показывать вымышленные «мысли». Только действительно возвращённый reasoning/summary, если он существует и доступен.
6. Отличать `stored`, `current`, `delivered`, `complete`; не выдавать metadata-only за прочитанный текст.
7. Ссылки `R1@v3`, `E12`, `O8`, `Q14` кликабельны; canonical IDs — только в деталях.
   Типы и владелец — [ADR-0018](../decisions/ADR-0018-typed-model-references.md):
   E — операция, O — наблюдение; текущая реализация и gaps описаны в
   [матрице результатов](../audits/tool-result-presentation-matrix.md).
8. Минимум кнопок, спокойные цвета, короткие строки, доступность с клавиатуры, responsive, light/dark.
9. Состояния `completed`, `blocked`, `waiting`, `budget stopped`, `error` — по факту runtime, не по обещанию модели.

## 2. Общая компоновка
Три области: слева дерево чатов/проектов (~240px, сворачиваемое); центр — чат (адаптивный, минимум ~460px); справа единый dock Inspector (~360–520px, изменяемая ширина). **Плагин и Inspector — режимы одного правого dock**, а не две конкурирующие панели. При узком окне Inspector превращается в drawer/fullscreen drilldown.

### Чат
- User/assistant текст — основной визуальный уровень.
- Между сообщениями — компактная активность модели: `Проверка книги · 3 действия · 1.2 с`.
- Tool row: иконка + `Read` + `DataModule.bas` + `240 строк` + `0.3 с` + статус.
  Реализованный срез: действие + конкретный target + typed outcome; попытка показывает
  target уже во время выполнения. Разные skills/referencePath не скрываются за одним
  «Изучение». Время остаётся отдельными сохранёнными сведениями, не выдумывается.
- Несколько tools одной модели группируются, но каждый принятый вызов имеет независимый `E` и result.
- По раскрытию показывать ключевые параметры, краткий результат, связанные `R/E`, ошибку.
  Текущий срез сохраняет свёрнутые arguments/result JSON внутри шага и подробную диагностику
  в Inspector; перенос всего raw исключительно вправо — отдельное изменение компоновки.
- Не повторять одно и то же «проверяю структуру» при каждом чтении. Намерение — один раз на decision.
- Под каждым сообщением рядом с Copy/другими действиями — иконка **Inspect**. Она открывает точный шаг справа. Tooltip «Открыть шаг». Сохранить текущие действия и focus behavior.
- Результаты и статус должны отражать реальные события; отсутствие `message` не заполнять вымышленным текстом.

## 3. Inspector — информационная архитектура
Верх: `Шаг 08 · Чтение ресурса`, `Q14`, время, переход предыдущий/следующий, закрепить, закрыть. Одна горизонтальная навигация из пяти вкладок:

**Overview** (по умолчанию): модель получила → решение → tools → результаты → что осталось/почему остановилась. Содержимое одного экрана; кликабельные refs.

**Request**: переключатель `Input / Output`. Input — реальный порядок model messages, роли, объем, раскрываемый текст, включённые вложения. Output — сырой текст ответа, parsed fields, message, tool_calls, finish_reason, ошибки разбора. Raw serialized HTTP request/response — только Advanced, отдельно от логического контекста.

**Context**: состав фактического `Q`: system, task, capabilities, history, resources, omissions; объемы и причины. Вкладки `All / System / History / Tools / Resources`, текст конкретного фрагмента по клику. Отдельно показывать preview/estimate и фактически отправленное.

**Resources**: `R@v` список, exact revision/head, coverage, view, `delivered full/excerpt/metadata/omitted`, payload status, источник, diff, точный фрагмент, причина исключения. Старые версии доступны как historical, не объявляются current.

**Graph**: локальный причинный граф выбранного шага, не вся история: task/decision → operation → evidence → resource revision → request. Клик по узлу синхронизирует Inspector. Фильтры Task/Step/Resources/Changes/Context; fit/zoom; deterministic layout. Граф строится из событий, не отдельной БД.

**Advanced** (один переключатель): canonical IDs, JSON, transport, authority generation, leases, detailed timing, payload. По умолчанию скрыт.

## 4. Визуальные токены
Использовать существующие RNAssistant CSS variables, без фреймворка и сборки; HTML/CSS/plain JS. Отступы 8/12/16px, текст 12–14px, заголовки 15–16px, metadata 11–12px, кликабельные цели ≥32px. Тихие границы, без разноцветных бейджей на каждом поле. Mono для refs/времени/токенов. Collapsed tool row — одна строка с ellipsis; расширение показывает краткую карточку. Иконка + текст для состояний, не только цвет.

## 5. Типы ссылок в UI
| Alias | Значение |
|---|---|
| `R1@v3` | ресурс и версия |
| `E12` | принятая операция и её исход |
| `O8` | наблюдение в диагностике |
| `Q14` | request/context snapshot |
| `T1` | задача |
| `S3` | шаг |
| `C2` | критерий |
| `D4` | решение, только при наличии owner |
| `M5` | память/claim, только при наличии owner |
| `A2` | артефакт |
| `P9` | payload (Advanced) |

Текущий контракт использует только E/R/O/Q. Остальные строки таблицы — прежние
предложения, не реализованные namespaces; они не вводятся без отдельного решения.
E/R выдаёт ChatStore, UI только читает bindings; O/Q выводятся из сохранённых events.
Примеры `R1@v3#lines=40-90`, `R2@v1#pages=1-3`, `R3@v2#procedure=getData` —
эскизы UI-селекторов, не разрешённые model/tool arguments. Реальный selector
типизированный; alias не переиспользуется для другой canonical identity.

## 6. View-model без новой базы
Сформировать read-only DTO из существующих event log, ContextReceipt и resource/evidence owners:
- `TimelineItem { id, kind, timestamp, title, summary, status, relatedRefs, detailAvailable }`
- `StepDetail { requestRef, decisionType, modelMessage, reasoningSummary?, calls[], results[], stopReason }`
- `RequestView { requestRef, orderedParts[], rawAvailable, outputAvailable, usage, warnings[] }`
- `ContextEntry { sourceRef, delivery, coverage, reason, estimatedTokens, ordinal }`
- `GraphNode / GraphEdge { id, kind, label, relation, refs }`

Схемы примерные; адаптировать существующие API вместо дублирования. `reasoningSummary` не синтезировать из скрытых мыслей. При отсутствии точного receipt показывать `unknown`, не восстанавливать «как будто» из журнала. Payload загружать лениво, с правами, лимитами, cancel и lease cleanup. Версионировать UI DTO.

## 7. Обязательные макеты/сценарии
До production code сделать автономный HTML-прототип на фикстурах:
1. успешный цикл: лист → данные → график;
2. несколько параллельных read tools;
3. преждевременный finish и reject;
4. частичный `R1@v3#lines=40-90` и omitted PDF;
5. compaction: summary вместо тела, с понятной причиной;
6. ошибка инструмента/unknown effect;
7. длинный model output, отсутствующий reasoning;
8. узкое окно, dark/light, keyboard navigation.

Сначала согласовать скриншоты и взаимодействия; не начинать широкий UI cutover до ревью прототипа.

## 8. Фазы и коммиты
**Phase 0 — Audit + interactive prototype.** Снять скриншоты старых экранов, описать entrypoints, dependency map, визуальные фикстуры, standalone HTML. Commit `docs(ui): specify diagnostic experience`.

**Phase 1 — Unified right dock.** Inspect/Plugin modes, сохранение состояния браузера плагина, footer Inspect, navigation, focus, resize. Commit `feat(ui): add contextual inspector dock`.

**Phase 2 — Compact chat activity.** Общий renderer, краткие tool rows, grouped calls, без дублирующих сообщений, accurate statuses. Commit `feat(ui): unify activity rendering`.

**Phase 3 — Overview + Request.** Чистые входы/выходы модели, реальные ordered messages, raw Advanced, ошибки, timing. Commit `feat(ui): add step and request inspector`.

**Phase 4 — Context + Resources.** ContextReceipt, coverage, inclusion/omission, refs, resource inspector. Commit `feat(ui): add context resource explorer`.

**Phase 5 — Graph.** Локальный causal graph, синхронизация выделения, zoom, фильтры, performance. Commit `feat(ui): add diagnostic graph`.

**Phase 6 — Remove old presentation.** Только после миграции DOM/bridge/exports/tests и parity; удалить старые визуальные журналы и дублирующие инспекторы, НЕ event store, logs, resource contracts и connection tests. Commit `refactor(ui): remove legacy diagnostic views`.

**Phase 7 — Qualification.** Реальный Windows/Office/WebView2, accessibility, 1000+ событий, безопасность/redaction, long conversations, regressions. Commit `test(ui): qualify diagnostics UX`.

После каждого коммита: screenshot, список файлов, тесты, риски, acceptance и следующий шаг. Не переписывать всю оболочку одним коммитом.

## 9. Критерии готовности
- За ≤10 секунд видны решение модели, вызовы, результаты, остановка.
- Один клик от сообщения чата к шагу; ещё один — к полному model request.
- По умолчанию ни один экран не показывает сырой JSON или гигантские аргументы.
- Чётко различаются model message, decision, tool call/result, reasoning summary (если существует).
- `Q` отражает реальный сериализованный запрос; `R@v` показывает точную версию и delivered coverage.
- Нет дублирующих панелей, модальных журналов и фиктивных графовых связей.
- Работает responsive, keyboard, light/dark, bounded loading, access controls.
- Удаление старого UI не удаляет диагностические данные и не ломает bridge.

**Первое задание агенту: выполнить только Phase 0.** Документировать старый UI, сделать интерактивный HTML-макет и показать 8 сценариев. Затем согласовать визуальную систему перед изменением production chat.

## 10. Дополнение: точный контекст каждого шага и полноэкранный Inspector (23.10.2026)

**Приоритет UX:** исследование фактически отправленного модельного контекста — основная функция Inspector, а не второстепенный debug-экран. Для каждого `Q` необходим неизменяемый снимок запроса с точным порядком сообщений и вложений, ссылками на источники, материализованными фрагментами и явными пропусками. Если фактический payload не сохранён, UI обязан написать `Недоступен точный снимок`, а не реконструировать его молча.

### Три различных представления

1. **История событий:** каноническая хронология user/assistant/tool/decision/результатов без компакции; все события сохраняются, но тела загружаются по требованию.
2. **Составленный контекст:** выбранные ContextAtoms, актуальные task/skills/tools, компакция, исключённые и включённые evidence, причины и бюджеты. Это объяснение *как* был собран запрос.
3. **Фактический модельный запрос:** точный сериализованный input с ordinal/role/content parts, attachments, raw text, материализованными фрагментами и их coverage. Это ответ на вопрос *что действительно получила модель*. Отдельно показывать raw provider response, parsed decision, user-visible message и tool_calls.

Не смешивать estimated preview, final request и raw HTTP payload. Связывать их `Q` и stable source refs. Сохранить порядок, в котором данные реально поступили в запрос, а не искусственно переставлять историю и ресурсы по категориям. Категории используются только для фильтрации.

### Полноэкранный Inspector

Режимы `Docked` (правая панель), `Wide` (изменяемая ширина, расширение влево), `Focus` (занимает всю рабочую область приложения). Переключение без потери выбранного `Q`, фильтра, прокрутки и раскрытий. В Focus: слева дерево/список сообщений запроса, справа текст выбранного сообщения, таблица или изображение; регулируемый splitter. Кнопки Expand/Restore/Close, Esc, доступность с клавиатуры. Состояние чата и встроенного браузера плагина сохраняется; Inspector не создаёт второй параллельной панели.

### Навигация и визуальная структура

- Верхний breadcrumb: `Chat → Run → Step S8 → Request Q14 → Message #17 → R1@v3`.
- Быстрые `Previous/Next step`, поиск по тексту, alias, operation, role, resource, и переход от вызова к результату.
- Для каждого сообщения: порядковый номер, role, смысловое название, токены/объём, статус `full/excerpt/metadata/omitted`, раскрытие тела.
- Для каждого источника: alias, точная ревизия, `actual coverage`, delivered state, место в запросе, причина omission, ссылки на операции чтения.
- Отдельная вкладка `Compaction`: какие события/фрагменты были сокращены, чем заменены, ссылки на оригинал, размер до/после, границы достоверности. Если таких сведений нет — `not recorded`, не придумывать.
- `Compare requests`: два соседних `Q` рядом, diff состава контекста (добавлено/удалено/изменено/сокращено), отдельный diff содержимого по запросу.
- Сохранять единые типографику, spacing, темы и стили RNAssistant; избегать перегруженных вложенных вкладок и бесконечных технических карточек.

### Полные логи и «Ход работы»

`Ход работы` оставить доступным в настройках как отдельный полный журнал событий, не как главную панель диагностики. Полный log viewer: поиск, фильтры, диапазон времени, группировка по run/step/request, технические записи, raw payload по запросу, экспорт с редактированием чувствительных данных. Из Inspector — ссылка `Открыть в полном журнале` с переходом к точному событию. Не удалять backend event store, экспорт, trace и журнальные данные при замене legacy UI.

### Дополнительные acceptance tests

- Для каждого отправленного `Q` пользователь видит фактический порядок сообщений и multipart содержимого; reconstructed view маркируется отдельно.
- Компакция показывает что было сохранено, исключено и заменено; кликабельная связь с исходными событиями.
- При частичной материализации PDF/VBA видны точные границы; `R1@v3` не выдаётся за полностью доставленный, если включён лишь excerpt.
- Compare Q14/Q15 корректно показывает изменения контекста, не путая версии и пропущенные payload.
- Fullscreen/wide/docked режимы сохраняют навигацию, фокус и scroll; чат и plugin state не теряются.
- Полный журнал доступен из настроек и по deep link из Inspector; raw данные не показываются по умолчанию.
- Проверить сценарии после рестарта, отсутствующего CAS, redaction, 1000+ событий и больших multipart-запросов.

### Обновление этапов

**Phase 0:** в автономном HTML-прототипе обязательно показать Docked/Wide/Focus, фактический request tree, compacted-vs-delivered context, Compare Q, full log viewer entry. Не начинать production UI до утверждения этих макетов.

**Phase 1:** реализовать единственный расширяемый right dock и полноэкранный режим, без потери plugin/chat state.

**Phase 3:** реализовать точный input/output request viewer, навигацию по ordered parts и distinction preview/serialized.

**Phase 4:** добавить compaction lineage, omissions, resource coverage, request diff и переходы к source events.

**Phase 6:** старые экраны можно удалять только после миграции полного журнала в настройки и сохранения deep links/exports.
