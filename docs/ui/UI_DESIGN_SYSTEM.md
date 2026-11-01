# Дизайн-система и компоненты RNAssistant

Обновлено: 2026-11-02. Owner: Web presentation; визуальные решения утверждает
пользователь. Canonical document tokens, компонентов и их повторного использования.
Стратегия — [Architecture](UI_ARCHITECTURE.md), экраны — [Screen Map](UI_SCREEN_MAP.md).
Направление Approved; продолжение общего chat-среза разрешено. S1b проходит визуальное ревью по эталону завершённого Demo.

## Визуальная основа

Действующий production UI — основа сообщений, composer, материалов, форм и меню.
Согласованные изменения оболочки берём из Demo, улучшения связываем с конкретным
неудобством. Самостоятельный редизайн экранов и возвращение отклонённых A/B не нужны.
Макет P3-инспектора не является утверждённым образцом настоящего Inspector.

| Решение | Статус |
|---|---|
| Кнопки сворачивания/разворачивания областей сверху; бренд в topbar убран | Approved |
| «+» выбирает экран; две кнопки создают область справа/снизу с выбором | Approved |
| «Закрыть группу» действует только на эту группу | Approved |
| Вид Dockview до A/B, возможность выбрать тему/акцент | Approved — направление |
| Крестики и split icons меньше, область нажатия остаётся удобной | Approved — направление; точные размеры In Review |
| Имя документа в topbar, конкретная компоновка настроек, responsive thresholds | In Review |
| P3 production-чат в Dockview без повторного заголовка | In Review; iframe только Demo evidence |
| S1a: общие подписи ввода, edit/reply/stop, reasoning и окно истории Office/Workspace/plugin | Реализовано, детали presentation In Review; не полная унификация capabilities |

## Эталон оболочки: завершённый Dockview Demo

Пользователь уточнил 2026-11-02: **Demo — эталон стиля и компоновки Dockview**,
и попросил перенести его описание в текущие документы. Основа — завершённые
P1/P2 (`95afa3b8`, `3e2433c9`), после возврата оформления до A/B и уменьшения
крестиков/кнопок. Это уточняет прежний статус «Approved — направление»:
оболочку не переосмысляем заново. Приёмка её production-адаптации остаётся In Review.

Источники переноса: исторические разделы [Оболочка](UI_LAYOUT_UX.md#оболочка),
[Раскладки и состояние](UI_LAYOUT_UX.md#раскладки-и-состояние),
[Проверка P2](UI_LAYOUT_UX.md#проверка-p2) и [журнал решений](UI_DECISIONS.md).
Визуальные опоры: [P1 Shell](review/p1-shell.jpg),
[P2 вкладки и действия](review/p2-tab-actions.jpg), [тема/акцент](review/p2-accent-dark.jpg).
Фактические старые значения доступны в `prototypes/ui/tokens.css` и `ui.css`;
источник новых production-значений — только `app-product-tokens.css`.

- Верхняя полоса без бренда: слева документ/проект или название текущего раздела;
  справа компактная группа navigation / Inspector / maximize, затем «Панели»,
  «Компоновка», настройки. Отступы и иконки соответствуют reference.
- Узкая вертикальная полоса разделов и отдельное дерево слева. У активного раздела
  спокойная заливка и тонкий акцентный маркер. Дерево не является Dockview-группой.
- Светлая нейтральная подложка, небольшие отступы вокруг рабочей области и между
  группами, тонкие разделители, небольшие радиусы. Без новой A/B-палитры и без
  плоской сетки S1b, которую пользователь отклонил.
- Компактная строка вкладок. Название чата находится во вкладке; ниже остаются
  полезные сведения о документе/проекте. Второй крупный заголовок чата не нужен.
- Заголовок группы: «+», оригинальные Codicons справа/снизу, «⋯»; уменьшенный
  крестик вкладки. «Закрыть группу» находится в меню, не дублирует крестик рядом.
- «+» открывает **вкладку выбора с поиском**, а не прежнее popup-меню. Поведение
  описано в [Screen Map](UI_SCREEN_MAP.md#команды-оболочки-s1b).
- Нижняя строка сохраняет роль reference: источник/режим интерфейса, состав
  компоновки, переход в диагностику. Mock обозначается только в preview;
  успешность модели или Office по состоянию layout не выводится.
- Содержимое сообщений, composer, подтверждения, материалы и настоящий Inspector
  остаются production-компонентами. Статический Inspector P1 не переносится.

Срез использует общий набор нейтральных цветов, исходные accent/theme controls,
и `--shell-*` для размеров оболочки. Публичные `--dv-*` только связывают Dockview
с этими tokens. [app-dock-workspace.css](../../web/css/app-dock-workspace.css)
владеет размещением и оформлением Shell/Dockview, не заменяет feature CSS экранов.
Размеры hit targets и знаков разделены; точные production breakpoints и поведение
на реальной Office-панели ещё проверяются.

## Источники tokens

Исходная production-база — [app-product-tokens.css](../../web/css/app-product-tokens.css):
семантические поверхности, текст, границы, accent/status, spacing, типографика,
радиусы, размеры и обе темы. CSS хранит фактические значения; таблицы значений
в Markdown не дублируем. [app-accent.js](../../web/js/app-accent.js) уже общий
каталог акцентов и foreground calculation.

Сейчас [app.css](../../web/css/app.css), [app-theme.css](../../web/css/app-theme.css),
app-product-components/screens и feature CSS образуют каскад; aliases text/ink,
line/border нельзя удалять до переключения consumers. В срезе компонента:
найти его правила → назначить один источник → переключить consumers → удалить
заменённые selectors, не добавляя ещё один override sheet.

[Demo tokens](../../prototypes/ui/tokens.css) — зафиксированный эксперимент,
не второй источник для production. Нужные Dockview tokens добавляются к общей
production-базе и связываются с публичными --dv-* hooks. Отдельная тема каждого
вида запрещена. Data Templates сохраняет standalone defaults до общего
portable token entry, после чего host и standalone используют один источник.

## Оформление и акцент

Светлая/тёмная темы, компактная читаемая иерархия, умеренные границы, общий focus.
Статус передаётся текстом/иконкой вместе с цветом; reduced-motion учитывается.
Выбор темы/акцента не пересоздаёт views и не меняет runtime settings или layout.

Исторические снимки P2: [светлая тема](review/p2-settings.jpg),
[тёмная с акцентом](review/p2-accent-dark.jpg), [tab controls](review/p2-tab-actions.jpg).
P1/P2 оболочка выбрана пользователем эталоном; её значения перенесены в общие
production tokens. Адаптер размера close icon ограничен Dockview 8.8.3 и повторно
проверяется при обновлении vendor; другие компоненты от него не зависят.

## Каталог существующих компонентов

L1 = host-independent component; L2 = browser requirements; L3 = host integration.
Указанная пригодность — source-аудит, не полная standalone qualification.
Новые public interfaces вводятся только для реального второго consumer.

| Компонент / источник | Повторное использование и граница | Состояния / lifecycle |
|---|---|---|
| [Dock Workspace](../../web/js/app-dock-workspace.js) | Одна production Shell/Dockview; descriptors conversation/inspector и временный chooser; не chat store | move/resize/close/restore, narrow, pending layout save, DOM lifecycle |
| [Icons](../../web/js/app-icons.js), [accent](../../web/js/app-accent.js) | Общие уже сейчас; кандидаты L1 без Shell | Theme/size/accessible label у consumer; shared catalog |
| Buttons/toolbar/forms/menu CSS: app-product-components, app-theme, app-composer | Сохранять привычные controls; извлечь затронутые rules, не копировать по views | default/hover/focus/pressed/disabled/invalid/loading |
| [NavigationTree](../../web/js/app-navigation-tree.js) | Реальный общий renderer, options/callbacks + icons; L1 candidate | selected/expanded/filter/keyboard; unmount освобождает state |
| [TreeAdapter](../../web/js/app-tree-adapter.js) | Resource/domain adapter поверх NavigationTree, не второй widget | read/loading/error, destroy; host calls не уходят в UI Core |
| [EditorTabs](../../web/js/app-editor-tabs.js), [ActionMenu](../../web/js/app-action-menu.js) | Внутренние режимы/меню; не docking engine | focus/keyboard/active; callback actions |
| [ViewerRegistry](../../web/js/app-viewer-registry.js), [ViewerUI](../../web/js/app-viewer-ui.js) | Сохранить register/mount/unmount форматов | destroy старого controller и decorations при замене |
| [JSON](../../web/js/app-json-viewer.js), [text/Markdown](../../web/js/app-text-viewer.js), text-diff | Повторно используют registry; portable rendering выделять без bridge | bounded DOM/depth, search/copy/source, unmount |
| [ResourceViewer](../../web/js/app-resource-viewer.js), [SequenceViewer](../../web/js/app-sequence-viewer.js) | Общие PDF/image/sequence/task-list; resource/lease adapter остаётся L3 | lazy pages, zoom, unavailable, destroy/release; не загружать всё заранее |
| [PreviewSurface](../../web/js/app-preview-surface.js) | Общая шапка/локация/reload для HTML и plugin preview | load/error/reload; frame owner не принадлежит chrome-компоненту |
| [OperationView](../../web/data-templates/ui/operation-view.js) | Готовый presentation-only progress: create/render/dispose, callbacks cancel/dismiss; L1 candidate | Фактический snapshot; unknown total indeterminate, без таймерной симуляции |
| [Questions](../../web/js/app-questions.js), [RunChanges](../../web/js/app-run-changes.js) | Общие conversation composites, пока domain-aware | awaiting/submitting/rejected/failed, owner/receipt сохраняются |
| [WorkspaceChat](../../web/js/app-workspace-chat.js), [Messages](../../web/js/app-messages.js), composer | Уже общий domain UI Office/Workspace/plugin; не primitive UI Kit | single active owner, draft/scroll, keyed reconcile/live batching |
| [Composer presentation](../../web/js/app-chat-composer.js), [reasoning control](../../web/js/app-model-render.js) | Один renderer состояний ввода и controls; owners передают факты и capabilities | idle/edit/awaiting reply/read-only/running/cancelling; не хранит второй draft |
| [ConversationState](../../web/js/app-conversation-state.js), history buttons в Messages | Один cap 240 и prepend/refresh для обоих доменов; explicit latest bypass | stale page rejection, bounded data, reading anchor; durable history не обрезается |
| [ChatInspector](../../web/js/app-chat-inspector.js) | Один domain view, настоящие context/capabilities/usage/run | Scope/generation, lazy detail, disclosure/focus, suspend |
| [ModelContext](../../web/js/app-model-context.js) | Domain reader, сохранять существующие cache/abort/bounds | metadata → lazy body, retained views, stale guards |
| [RNPlugin SDK](../../web/plugin-host/sdk.js) | Существующий Ready/command/files/results protocol; не новый SDK | L3 host, L2 browser adapter отдельно; permissions у host |

Нельзя обобщать Chat/Inspector в UI Core только из-за общих кнопок. Сначала
выделяются независимые controls с реальными consumers и инъекцией зависимостей.
Не создавать универсальный VirtualList/DataTable, если существующих bounded
renderers/Tabulator достаточно для конкретного сценария.

## Контракт компонента и ревью

Для выделяемого компонента здесь фиксировать source/public API, реальные consumers,
владельца данных, states, keyboard/focus, tokens, события, cleanup, portability и
пример. Mount не стартует domain operation без команды пользователя/владельца.
Версия public API нужна при появлении внешнего consumer, не для каждого helper.

Узкий режим, увеличение текста, menu clipping, hit areas, tooltip и Escape
проверяются на конкретном срезе. Не путать хороший screenshot с исправлением
late responses, утечек или Office qualification. После согласования обновлять
компонент и tokens в том же срезе; эксперимент остаётся Proposed/In Review.
Отдельный UI_COMPONENT_CATALOG.md сейчас не нужен: этот каталог имеет одного owner.
