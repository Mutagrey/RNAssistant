# Карта экранов, навигации и состояний

Обновлено: 2026-11-02. Owner: Web presentation.
Исходная карта — source audit baseline 4ea3d6e4; ниже сохранена карта будущих срезов.
S1a объединяет bounds/composer presentation; S1b размещает общий conversation и
настоящий Inspector в одном production Dockview. Settings подключён срезом S1c; остальные целевые views Proposed;
полная parity capabilities и несколько одновременных чатов ещё не реализованы.
Canonical карта UI; владение/lifecycle — [Architecture](UI_ARCHITECTURE.md),
компоненты — [Design System](UI_DESIGN_SYSTEM.md).

## Команды оболочки S1b

Owner: Shell chrome — `app-layout.js`, placement/команды workspace —
`app-dock-workspace.js`, меню — общий ActionMenu; эталон — завершённый P1/P2,
перенесённый из [истории компоновки](UI_LAYOUT_UX.md#оболочка).

| Команда | Реальный результат / граница |
|---|---|
| «+» в группе | Вкладка `choose-N` с поиском, фокусом в поиске и Enter; не создание чата |
| Область справа / снизу | Dockview создаёт группу с той же вкладкой выбора; исходная вкладка остаётся |
| Выбор экрана | Закрывает chooser, открывает экран или переносит существующий singleton сюда без клона |
| «⋯» → закрыть вкладку / закрыть группу | Только выбранная вкладка / вся её группа; остальные группы и domain run продолжают жить |
| «⋯» → собрать в одну группу | Объединяет существующие вкладки без повторного чтения данных |
| Верхние navigation / Inspector / maximize | Навигация по контракту ниже; отдельная группа Inspector скрывается/возвращается с прежним размером; смешанная/узкая группа активирует вкладку Inspector; maximize и Escape/restore |
| «Панели» | Общий каталог доступных **подключённых** экранов: Чат, Инспектор, Настройки; возвращает закрытый или активирует открытый |
| «Компоновка» | Пресеты «Чат и инспектор», «Одна группа вкладок»; сохранить/вернуть пользовательскую раскладку |
| Настройки | Singleton Dockview view S1c; вход снизу rail, повторное нажатие активирует существующую вкладку |
| Диагностика | Запуски/Lab/Приложение — section той же Shell; saved Request — singleton в Dockview |

Chooser использует компоновку Demo, показывает «Ничего не найдено» при пустой выдаче.
«Панели» — компактное меню подключённых видов с отметкой уже открытого.
Общие меню поддерживают Arrow Up/Down, Home/End, Tab, Escape/возврат focus.
StatusBar показывает mode/connection и раскладку только при видимом workspace;
агрегация фактических фоновых operations ещё относится к последующим срезам.

Текущие ID: `conversation` (один Office/Workspace/plugin owner), `inspector`
(четыре настоящих раздела context/abilities/usage/activity), `settings`, `request`.
`choose-N` — временный
элемент Shell, не продуктовый экран и не второй каталог. Незавершённые chooser tabs
сохраняются вместе с layout; поисковая строка — только в памяти view.

Rail пока переключает прежние sections; их переходы, lazy render и deactivation
остаются у `switchTab`. Целевой принцип Demo «навигация меняет дерево и не закрывает
вкладки» сохраняется: разделы скрывают рабочую область, но не уничтожают её.
Все разделы внутри Dockview этим срезом не объявлены перенесёнными.

При узкой рабочей области группы собираются во вкладки. Широкая раскладка
возвращается при расширении; закрытая в узком режиме панель не воскресает.
Split недоступен, если обе части не помещаются. Точные пороги S1b In Review,
проверяются по ширине рабочей области, а не копируются из Demo без учёта дерева.
Native plugin preview и Office HTML preview пока сохраняют прежнее размещение;
их верхний контрол общий, iframe/session не пересоздаются из-за chat docking.

Подтверждённое направление S3b (после завершения S3a): plugin preview открывается
отдельным view рядом с общим Chat View или в другой группе. Открытие/закрытие
предпросмотра не подменяет чат; выбираются и перемещаются независимо. Старый
связанный toggle/splitter удаляется в этом срезе. Пока это план, не текущий placement.

## Боковая навигация — поведение Demo

Owner — `RNAssistantShellLayout` в `app-layout.js`, единственный владелец режима,
видимости, ширины и закрытия. Перенос по прямому запросу пользователя 2026-11-02.

- При ширине Shell больше 1000 px дерево занимает место рядом с рабочей областью.
  Предпочтение открыть/свернуть и изменённая ширина сохраняются.
- Все разделы используют один диапазон ширины и splitter из общих tokens.
  Стрелки изменяют ширину; Home/Enter/двойной клик восстанавливают исходную,
  End раскрывает до максимума. Перетаскивание ограничивается диапазоном;
  сворачивание остаётся у кнопки Shell/повторного клика раздела.
- До 1000 px дерево изначально скрыто; открывается поверх содержимого с подложкой.
  Полоса разделов остаётся доступной, рабочая область временно `inert` и не сжимается.
- Нажатие другого раздела показывает его навигацию; повторное нажатие активного
  переключает её видимость. Верхняя кнопка делает то же для текущего дерева.
- Выбор экрана мышью, Enter или Space закрывает временное дерево; раскрытие папки,
  поиск и действия внутри узла его не закрывают. Фон и Escape закрывают дерево.
  В широком режиме выбор экрана сохраняет дерево открытым.
- Фоновая смена selection не захватывает фокус. Выбор/создание чата из навигации
  сохраняет клавиатурное продолжение в дереве. Действия строки видны при наведении
  и фокусе; на touch остаются доступны постоянно.
- Переход между компактным и узким режимами не теряет текущее временное открытие.
  Возврат к широкому окну восстанавливает отдельное широкое предпочтение.
- До 640 px Dockview собирается в одну группу и отключает drag/split; при расширении
  возвращает широкую раскладку. Дополнительное ограничение по доступному месту
  сохраняется для слишком узкого workspace даже в широком окне.

Кнопка инструментов не закрывает вкладки отдельной группы Inspector, а скрывает её.
Размер, положение и DOM сохраняются. Смешанная группа с чатом не скрывается;
Inspector в ней активируется. Единственную видимую рабочую группу скрыть нельзя.
Крестик и «Закрыть группу» остаются отдельными командами фактического закрытия.
HTML/plugin preview пока управляется прежним adapter через общий верхний контрол.

## Настройки и меню rail S1c

- Gear открывает настройки в группе чата; повторное нажатие активирует существующую
  вкладку на её текущем месте. «+» и «Панели» используют тот же descriptor.
- Поиск и категории перемещаются вместе с формой. При недостатке ширины группы
  тот же список разделов доступен через компактный select. Глобальное дерево
  документов/проектов остаётся независимым; gear не переключает его open/closed.
- Крестик/закрытие группы спрашивает о dirty форме; «Отмена» оставляет всё на месте,
  подтверждение закрывает вкладку с сохранением правок в памяти до перезапуска.
  «Применить» — существующее действие сохранения. Pending save блокирует закрытие;
  ошибка и новые правки во время запроса остаются видимыми и неприменёнными.
- Наведение на gear показывает шесть разделов и версию; выбор открывает нужный
  раздел единственной вкладки. У остальных rail buttons сохранены подписи,
  у plugins — существующие действия. Короткая задержка hover убирает случайные
  открытия и запросы при пересечении полосы курсором.
- В меню можно перейти курсором через зазор, Tab/стрелками — с клавиатуры.
  Escape закрывает его и возвращает фокус без повторного открытия. Уход курсора
  закрывает подсказку; устаревший async plugin response не заменяет новое меню.

## Модель чата S2a

Маршрут: существующий composer → выбор модели, в том же `conversation` view.
Office, project и plugin source показывают одинаковое меню: default, сохранённая
модель вне каталога, каталог с общими badges. Отдельная вкладка/форма не создаётся.

| Состояние / действие | Общее поведение |
|---|---|
| Явный выбор | Сохраняется в exact chat; остальные чаты и глобальный default не меняются |
| По умолчанию | Сохраняется отсутствие override; следующий run берёт текущую общую модель |
| Сохранение / ошибка | Отправка ждёт ACK или read-back при потерянном ответе; подтверждённый отказ сохраняет прежнюю модель, draft не меняется |
| Переход A → B | Поздний ответ A не меняет controls/draft B; возврат читает сохранённый выбор A |
| Running / approval | Выбор заблокирован; принятая модель закреплена за run |
| Каталог настроек | Неприменённые возможности/preview не меняют composer; Apply действует после ACK |

Остальные общие функции чата не должны иметь самостоятельный UI по типу host.
Ресурсы, инструменты и разрешения предоставляет domain adapter; оставшиеся
различия attachments/context перечислены в Migration Plan, не считаются устранёнными.

## Entry points и оболочки

S2b.1 меняет внутреннее владение transcript, без новых экранов или CSS. Office,
Workspace и plugin используют один ConversationView для кеша/прокрутки/frames.
Перемещение не пересоздаёт view; скрытие откладывает paint; возврат к недавно
просмотренному target восстанавливает позицию чтения. Во время загрузки позиция
не считается восстановленной. Одновременных chat tabs ещё нет: ввод, команды и
доставка snapshots должны сначала получить explicit view scope.

| Вход | Что реально запускается | Будущее назначение |
|---|---|---|
| Desktop / Office shells | Один [web/index.html](../../web/index.html), AssistantPaneControl/typed bridge | Одна RNAssistant Shell; существующий host adapter |
| Office documents: Excel, Word, PowerPoint, Outlook | Тот же UI; document/session binding и capabilities различаются | Контекст/недоступность показываются во View, COM не попадает в UI Core |
| Workspace / plugin source chat | Тот же messages/composer с owner project/plugin | Общий conversation view, domain-specific adapters |
| Plugin application | Plugin iframe + host SDK; command execution через существующий PluginWebViewHost | Сохранить sandbox/transport; Dockview размещает host-owned view |
| Data Templates standalone | [data-templates/index.html](../../web/data-templates/index.html), общий предметный shell | Сохранить standalone entry, общий UI API по мере выделения |
| Prototype Demo | [prototypes/ui/index.html](../../prototypes/ui/index.html) | Замороженный UX-reference; не вторая production Shell |
| Product preview / C# MockDemo | [product-preview](../../prototypes/ui/product-preview.html), [MockDemoServer](../../demo/RNAssistant.MockDemo/MockDemoServer.cs) | Проверочные fixtures/hosts, не архитектура production |

## Полный реестр основных областей и подэкранов

Целевой ID — presentation identity, не URI/authority runtime. В первом срезе
conversation singleton; параметризованные chat IDs ниже — последующая цель.

| Экран / подэкраны сейчас | Реализация / владелец данных | Цель и default placement | Ключевые состояния |
|---|---|---|---|
| Rail, разделы, contextual sidebar, поиск | [app.js](../../web/js/app.js), [layout](../../web/js/app-layout.js), rail-popovers, NavigationTree | Shell; слева navigation, сверху общие действия | active/hidden/overlay, keyboard focus, narrow |
| Документы Office и чаты, новый Office target | [chat.js](../../web/js/app-chat.js), [chat-session](../../web/js/app-chat-session.js), ChatSessionService | Навигация слева → chat:office:session | init, metadata list, binding, unavailable/closed doc, error/retry |
| Проекты, каталоги проектных/плагинных чатов, новый проект/чат | [project-chats](../../web/js/app-project-chats.js), PluginApplicationService | Та же навигация → chat:workspace:workspace:session | empty/catalog loading, missing project, selected/running/unknown, rename/delete |
| Office conversation | [chat-state](../../web/js/app-chat-state.js), [messages](../../web/js/app-messages.js), chat-inbox/run/edit | Общий Chat View в центре | recent/older pages, edit, live, waiting, error, cancelled, unknown |
| Workspace conversation | [workspace-chat](../../web/js/app-workspace-chat.js), [project-chats](../../web/js/app-project-chats.js), WorkspaceConversationService | Тот же Chat View, иной adapter | readonly, pending receipt, live, confirmation/questions, stopped/recovery |
| Plugin source conversation | [plugins](../../web/js/app-plugins.js) + WorkspaceChat | Тот же Chat View, связанный source workspace | source changed, run, build/preview/publish failures; не host completion по таймеру |
| Composer: действия, модель, reasoning, plan, draft, edit | [chat-composer](../../web/js/app-chat-composer.js), [model-picker](../../web/js/app-model-picker.js), [WorkspaceChat dispatch](../../web/js/app-workspace-chat.js) | Внутри Chat View, не отдельная dock-панель | empty, dirty, attachment preparation, send/stop, disabled, reply required |
| Вложения, context chips, управление контекстом | [attachments](../../web/js/app-attachments.js), [context](../../web/js/app-context.js), context-data/meter | Локальные controls + deep link в Inspector | queued/preparing/ready/error, unavailable attachment, selection, removal |
| Вопросы/подтверждение/изменения run | [questions](../../web/js/app-questions.js), [agent-approval](../../web/js/app-agent-approval.js), run-changes | Внутри соответствующего conversation, подробности справа | awaiting_user/confirmation, submitting, rejected, unknown; owner-scoped |
| Inspector: Контекст | [chat-inspector](../../web/js/app-chat-inspector.js), context-data | inspector с explicit conversation scope, справа/центр | loading/empty/unavailable/current, disclosure/focus |
| Inspector: Возможности агента | Тот же Inspector, capability library/services | Раздел того же inspector | selected/pending/available/unavailable/permission required |
| Inspector: Токены и прогноз | context-meter, model-context summaries | Раздел того же inspector | usage unknown/known, local estimate отдельно от actual usage |
| Inspector: Текущий запуск | run-view-state, agent-activity, task-list | Раздел inspector, deep link в Diagnostics | live/completed/failed/cancelled/awaiting/unknown, grouped steps |
| Правый HTML preview и аннотации | [chat-browser](../../web/js/app-chat-browser.js), preview-surface, visual-inspector/annotations | browser:resource справа, перемещаемый view | loading/ready/error, reload, selection/annotation, preserved frame session |
| Материалы: дерево, общий каталог, история версий | [artifact-catalog](../../web/js/app-artifact-catalog.js), [artifact-versions](../../web/js/app-artifact-versions.js), ArtifactLibraryProjectionService | materials navigation + resource view в центре | paged heads/history, search/filter, removed/unavailable, revision |
| Markdown / текст / JSON | [text-viewer](../../web/js/app-text-viewer.js), [json-viewer](../../web/js/app-json-viewer.js), ViewerRegistry | resource:ref, центр; один viewer на нужный формат | load/read error, preview/source, truncation, copy/download |
| PDF / изображение / коллекция / task list | [resource-viewer](../../web/js/app-resource-viewer.js), [sequence-viewer](../../web/js/app-sequence-viewer.js) | Тот же resource view | pages/zoom/rotation, lazy media, unavailable/leased/disposed |
| HTML workspace: preview, код, assets/data, export | [html-workspace](../../web/js/app-html-workspace.js) и тематические модули, resource owner | html:resource в центре; preview отдельным browser view при необходимости | dirty/saving/conflict, preview lifecycle, inspection, export error |
| Библиотека: навыки, инструкция навыка, файлы | [skills](../../web/js/app-skills.js), skill-fields-view, library-groups | library navigation + library:kind:id detail | list/search/selection, dirty, validation/save/read failure |
| Библиотека: prompts | [prompts](../../web/js/app-prompts.js), shared instruction editor | Тот же detail, subtype prompt | dirty/preview/validation/save failure |
| Библиотека: tools — основное/schema/implementation/test/docs | [tools](../../web/js/app-tools.js), tools-actions/structured/documentation | Тот же detail, subtype tool; внутренние EditorTabs | definition/test busy/result/error/unsaved, доступность host |
| VBA: код, diff, запуск, история | [vba](../../web/js/app-vba.js), vba-project/actions/completion, VBA services | vba:document:module, центр + navigation | Office unavailable, loading, dirty/guarded write, execute result, history |
| Плагины: каталог/add/settings/source/release/preview | [plugins](../../web/js/app-plugins.js), plugin-host/sdk.js, PluginApplicationService | plugin:id view + contextual navigation | enabled/disabled, loading, source dirty, preview/publish failure |
| Data Templates: источники, шаблоны/черновики | [browser](../../web/data-templates/ui/browser.js), loader/source-intake, template-library/editor | Один plugin view; его предметная навигация | paged source list, unsupported files, draft/validation/save |
| Data Templates: параметры, mapping, формат | parameter-editor, mapping-editor, project-dictionary-controller | Подэкраны того же plugin view | selected/invalid/dirty/published; неподключённые форматы явно отмечены |
| Data Templates: processing, results, project files | processing/operation-view, browser-project-results/files | Plugin view, факт операции в общем StatusBar через будущий публичный API | running/progress unknown or measured/cancelled/failed/completed/staged |
| Настройки: connection/model/context/agent/interface/protection | [settings](../../web/js/app-settings.js), models/model-data/model-render, [HTML](../../web/index.html) | settings, singleton Dockview view; шестерёнка снизу слева | loading/dirty/saving/validation/error, model catalog preview/cancel |
| Диагностика: Запуски, фильтры, события/trajectory/payload | [diagnostics-navigation](../../web/js/app-diagnostics-navigation.js), run-journal/trajectory/payload | diagnostics:runs, tool view | scoped loading, live follow, paged events, pending cancel, read errors |
| Reader запроса: Состав/Ответ/Изменения | [model-context](../../web/js/app-model-context.js), model-context-details / model-context-contents | singleton `request`, exact owner/run/event внутри reader, центр или справа | metadata-first, lazy bodies, unavailable/truncated, bounded retained views |
| Лаборатория моделей | [model-diagnostics](../../web/js/app-model-diagnostics.js) | diagnostics:lab | selected scenario, running/cancel requested/finished/failed, redacted evidence |
| Приложение: health/performance и журнал | [diagnostics](../../web/js/app-diagnostics.js), [logs](../../web/js/app-logs.js) | diagnostics:application | connected/unavailable, timings, log filters/copy/clear; не второй task runtime |

«Файлы» в текущем продукте распределены между project/plugin navigation,
контекстом, материалами и Data Templates. Универсального самостоятельного Files
view из Demo в production нет. Его цель — общий navigation component с domain
adapters, а не новый файловый store. Инструменты уже находятся в Библиотеке;
старые route aliases tools/html в switchTab не являются самостоятельными экранами.

## Сквозная навигация — предлагаемое поведение

1. Rail выбирает контекст левой навигации. Выбор leaf вызывает общую команду
   открыть/активировать view; не создаёт его копию при перемещении.
2. «+» выбирает экран в текущей группе; split right/below создаёт область с выбором.
   «Закрыть группу» закрывает только её views, не отменяет domain runs.
3. Чат → материал открывает resource identity; чат → подробности запуска передаёт
   owner/session/run/event. Inspector следует выбранному conversation; pin
   требует отдельного согласования и видимой подписи scope.
4. Закрытый view восстанавливается из навигации. Runtime и черновик не принадлежат
   Dockview JSON. Неверные сохранённые layout entries сбрасываются явно и локально.
5. При узкой ширине сначала сворачивается navigation, затем группы становятся
   вкладками. Конкретные пороги Demo не утверждены как production thresholds.
6. Материалы, библиотека, diagnostics, plugin работают при скрытом Chat View.
   Настройки доступны снизу слева; управление областями вынесено наверх.

## Структура запроса — production, 2026-11-02

Пользователь попросил сначала продумать переработку: компактный состав запроса,
удобное чтение сообщений как Markdown/JSON. [Макет содержимого](review/request-structure.html)
использует production tokens, NavigationTree и viewers; не копирует Shell/Dockview.
После проектирования пользователь поручил production, commit и push. Ниже — реализованная структура; макет сохранён как история решения, не второй production path.

**Одна вкладка «Запрос Q…»** из чата, Inspector или журнала. Видимый scope:
документ/проект → чат → запуск → попытка. Первый срез — singleton request view;
смена чата не подменяет открытый запрос. Если host больше не даёт доступ к его
источнику, показывается недоступность. Несколько живых запросов требуют отдельного
scoping. Правая панель Inspector сохраняет роль рабочего контекста чата.

Три задачи: **Состав / Ответ / Изменения**. В Составе — локальное оглавление слева
и один reader справа, вместо семи tabs и цепочки вложенных details. Первый выбор —
Обзор: состояние/причина, frozen settings и API usage. Число сообщений видно в оглавлении, сохранённая оценка — в группе/строках. Обзор не читает тело. Полный реестр ресурсов/доказательств доступен узлом
оглавления, конкретные связи — у сообщения/ответа через существующие readers.
«Исходник запроса» всегда доступен в меню действий запроса в его шапке.

| Узел / действие | Содержание |
|---|---|
| Разделы сообщений | Только записанные группы RequestComposition: инструкции и каталог, история, вызовы/результаты, ресурсы и прочие присутствующие группы. Число сообщений и ≈ токены |
| Сообщение | Номер в исходном массиве, роль, подсказка с фрагментом после чтения тела, записанная оценка. Тип и признак исторической проекции/результата — в reader. Вся строка выбирается, содержимое сразу появляется в reader |
| Порядок отправки | Другой порядок того же списка с сохранением selection; группировка не выдаётся за порядок wire |
| Параметры и формат | Точные сохранённые поля запроса; frozen settings отдельно в Обзоре. Формат нового ответа независим от истории. Native tools — отдельный узел только при наличии такого поля в запросе |
| Не отправлено | Записанные пропуски и причины; отдельно от messages и суммы отправленного контекста |
| Источники | Кратко «откуда / в каком виде отправлено», затем точные recorded facts и переход к локальному оригиналу/ресурсу. Без достраивания неизвестных связей |

Не более двух уровней: раздел → сообщение. Части составного content выбираются
в reader, не добавляют цепочку раскрытий в дереве. Смешанная инструкция остаётся
одним сообщением, пока compiler не записывает точные границы навыков/правил.
При отсутствии классификации явно показывается порядок отправки и причина.
Заголовки/числа групп доступны из metadata; тело читается при выборе сообщения,
параметров или исходника. Поиск в текстовом viewer ограничен выбранным сообщением. Общий поиск по сообщениям и фильтр роли остаются вне этого среза; группы и страницы доступны без чтения других попыток.

**Форматы выбранного content:**

- Markdown — существующий безопасный renderer полного текста.
- JSON — общий lossless viewer. JSON внутри строки разбирается только по выбору
  пользователя, с подписью «JSON в строке · в запросе string». Это представление,
  не изменение типа и не повторная проверка model protocol.
- Текст — декодированный string без Markdown; это не исходные байты JSON-конверта.
- «Точное сообщение JSON» — сохранённый JSON сообщения с role/content/tool_calls,
  точными типами/escape. Общий исходник запроса остаётся отдельным действием.

Над содержимым всегда видны область и тип, например `messages[4].content · string`;
историческая проекция и результат инструмента подписаны отдельно. Невалидный JSON
в режиме чтения не означает отказ runtime. Неполный текст не рендерится как полный
Markdown; обрезка/скрытие секретов/отсутствие данных видны. Медиа открываются lazy.
Копирование называет область: содержимое, JSON сообщения или весь запрос.

На узкой **группе Dockview**, независимо от окна: список → detail → «К составу»,
с прежними scroll/focus. На широкой — две колонки без нового drag/splitter.
Tree сохраняет общую клавиатуру, формат — selection и bounded presentation state.
Deep link сразу показывает нужное сообщение/поле и страницу.

Ревью макета: «Открыть сообщения» → Markdown/Текст; выбрать результат чтения →
JSON → меню сообщения → «Точное сообщение JSON»; меню запроса → исходник;
переключить порядок; сузить; выбрать неполную запись.
Response/diff представлены краткими синтетическими состояниями, не новым runtime.
Макет не доказывает production routes, async cleanup или performance.

После уточнения пользователя scope кнопок/меню — **вся диагностика и используемые
ею общие компоненты**, не все экраны приложения. Размещение действий:

| Уровень | Действия и место |
|---|---|
| Shell / Dockview | Раздел диагностики в rail; вкладки/закрытие/move/split/maximize принадлежат workspace. В содержимом запроса нет второй копии placement-кнопок |
| Разбор запуска | Выбор запуска, фильтр и follow рядом со списком событий; экспорт/редкие операции в одном меню этого списка |
| Запрос | Scope в шапке; точный исходник/download/refresh/export в её ActionMenu; Состав/Ответ/Изменения — общий EditorTabs, не ряд обычных кнопок |
| Сообщение | Локальный заголовок и меню с полным сообщением/источниками. Markdown/JSON/Текст — единый segmented control; он не выглядит ещё одним уровнем навигации |
| Viewer данных | Поиск/копирование/сворачивание относятся к показанным данным. Одно место для каждого действия; общий viewer сохраняет свой standalone режим |
| Лаборатория | Endpoint/сценарий и явные Запустить/Остановить рядом; скачивание отчёта у действий сценария. Открытие view не запускает сценарий |
| Приложение | Фильтры рядом с журналом; операции обслуживания — у состояния хранилища. Действия изменения данных не смешиваются с навигацией и выбором формата |

В narrow первыми сохраняются выбранный объект, основное действие и переход назад;
редкие действия остаются в том же меню, не копируются в отдельный mobile toolbar.
Popup портальный через ActionMenu: внутри viewport, без обрезки границей Dockview,
Escape возвращает фокус, смена scope/deactivate закрывает меню.

## Общая матрица состояний

| Состояние | Что обязан увидеть пользователь | Реальный owner / проверка |
|---|---|---|
| loading / empty | Локальная загрузка или отсутствие объектов, не success | Query owner; проверять cold/warm и пустой каталог отдельно |
| dirty / preparing / saving | Изменения ещё не подтверждены; draft не очищается заранее | Draft/resource/settings owner; reject/late receipt |
| queued / running / waiting | Фактический run/operation и доступные действия | Kernel/conversation events; без искусственных процентов |
| cancellation requested / cancelled | Запрос отмены отличается от подтверждённой остановки | Run/operation owner; не прекращать run при закрытии UI |
| failed / unavailable / unknown | Причина, сохранённый ввод, безопасное следующее действие | Exact scope; не выводить success из transport ok |
| stale / switched / disposed | Старый ответ не меняет другую вкладку | generation/revision/identity; cleanup observers/leases |
| narrow / zoom / keyboard | Доступны ввод, menu, focus и recovery закрытого вида | Browser + позднее реальный WebView2; фото с Windows |

Это source inventory всех основных экранных семейств и их перечисленных режимов,
а не утверждение, что каждый runtime-сценарий воспроизведён. Непроверенные размеры,
focus transitions, performance и host behavior остаются gates Migration Plan.
