# План модернизации production UI

Обновлено: 2026-11-02. Owner: Web presentation; согласование карты/scope — пользователь.
Стратегия и продолжение общего chat-среза разрешены пользователем. S1a реализован
и сохранён в fd474f91; S1b реализован и проходит визуальное ревью по эталону Demo.
Canonical план последовательности, проверки, baseline, rollback и удаления.
Границы — [Architecture](UI_ARCHITECTURE.md); маршруты — [Screen Map](UI_SCREEN_MAP.md).

## Сверка с исходным планом

Повторный запрос пользователя 2026-11-02: сохранить первоначальную стратегию,
перенести Shell целиком по Demo и проверить дубли/архитектуру. Исходная
[спецификация v1.0](reference/RNAssistant_UI_Architecture_and_Agent_Rules.md)
сохранена дословно как reference; текущие правила остаются в четырёх canonical docs.
Не возвращаем самостоятельный Demo, не начинаем P4 и не переписываем все views сразу.

| Исходный этап | Соответствие текущей работе | Gate и реальный статус |
|---|---|---|
| A — audit/baseline | Аудит 119aff97 и повторная сверка Shell | Карта исходников и browser baseline есть; полный Windows/process-memory baseline открыт |
| B — UX/map | P0 + UI_SCREEN_MAP | Все основные области перечислены; будущие detail views и их UX остаются Proposed |
| C — shell prototype | P1/P2, production Shell в S1b | Demo выбран эталоном; production adaptation всё ещё проходит визуальное ревью |
| D — minimum contracts | Общие tokens/components, один Dock catalog и ActionMenu | Нужный минимум используется сейчас. Standalone component fixture проверен; публичный panel SDK/plugin export не объявлен готовым |
| E — real Chat | S1a/S1b → S2 | Общий bounded window/DOM/drafts и docking есть. Полная parity, независимые chat instances и catalog paging ещё впереди |
| F — Inspector/diagnostics | Inspector в S1b, остальные views в S3 | Настоящий Inspector сохранён; Dock diagnostics и общий background status ещё открыты |
| G — остальные views | S3 → S4 → S5 | Переносить по одному функциональному срезу через существующий каталог; сохранять native host/session boundaries |
| H — удаление legacy | Обязательная часть каждого среза, финальная сверка S5 | Заменённые paths удаляются сразу после проверки; временные границы имеют owner/removal gate в MIGRATION_MAP |

Очередь уточнена прямым запросом пользователя: **Shell S1b (`2a4831c5`) →
Settings/rail S1c и визуальное ревью → S2a capabilities общего чата → остальные
части S2 → S3/S4/S5**. S1c выделен из Settings-части S4; исходный план сохранён. Единство registry/commands не откладывается
до S5: каждый новый view подключается к уже существующему механизму. Следующий
функциональный срез автоматически не начинается до ревью предыдущего.

## Фактические результаты P0–P3

| Этап | Результат | Evidence / ограничения |
|---|---|---|
| P0 | Найдены Demo, production preview, shared assets; предложена независимая Dockview-оболочка | [История компоновки](UI_LAYOUT_UX.md); аудит тогда относился к Demo, не ко всему production |
| P1 | Один Dockview для групп, chooser, right/below, maximize, восстановление раскладки, адаптация | Commit 95afa3b8; [P1](review/p1-shell.jpg). Точные responsive thresholds и все screens не утверждены |
| P2 | «+» = выбор экрана; возвращён вид до A/B, theme/accent, уменьшены close/split icons | Commit 3e2433c9; [контролы](review/p2-tab-actions.jpg). A/B удалены, конкретные размеры In Review |
| P3 | Первый самостоятельный чат отклонён и удалён. Использован production UI на существующем mock, через 49 строк JS adapter + 16 CSS | Commit **4ea3d6e4**; [чат](review/p3-production-chat.png), [узкий вид](review/p3-production-chat-narrow.png). Inspector P1; frame recreation сбрасывает fixture-переписку |

Атомарная P3-операция завершена и сохранена. JS syntax/diff checks и обязательный
ValidateVersionFormat прошли перед commit. Работает существующий запуск:

~~~sh
python3 -B prototypes/serve_ui.py
~~~

[Замороженный Demo](http://127.0.0.1:8897/index.html?review=p3) и
[production preview](http://127.0.0.1:8897/product-preview.html) остаются reference
fixtures. P4 автоматически не запускается. P3 не утверждает полный дизайн и не
квалифицирует Office/WebView2. Demo больше не развивается как копия продукта.

## Что берём из Demo

- Утверждённый смысл topbar controls, отсутствие бренда, chooser «+», right/below,
  group-local close; вид Dockview до A/B и возможность выбрать акцент.
- Локальный Dockview 8.8.3 с provenance и проверенные сценарии его header API.
  Asset переносится в существующую vendor-систему один раз; Demo затем ссылается
  на тот же asset. Второй постоянный vendor loader или registry не создаётся.
- Review fixtures и список проверок. Иконки уже переиспользуются из production;
  два Codicons SVG имеют локальную лицензию.
- Не берём iframe на каждый chat, mock bridge как production transport,
  Demo store/layout ID в качестве durable contract, фиктивный inspector,
  глобальный renderer-always policy или жёсткие пороги ширины без замера.

Основная повторно используемая реализация — существующий production UI.
Каталог пригодных компонентов и границ — [Design System](UI_DESIGN_SYSTEM.md).

## Оптимизации, которые сохраняем

| Инвариант | Реальная реализация / evidence | Что проверять при миграции |
|---|---|---|
| Metadata catalog, без чтения всех тел на открытии дерева | ChatSessionService.ListHeaders, ChatStore SQLite projection/event locators | Число запросов и body reads; каталог пока не paged |
| Recent/previous messages, общий bounded window | [ChatMessageViewDto](../../src/RNAssistant.Core/Models/ChatMessageViewDto.cs): page 80; [ConversationState](../../web/js/app-conversation-state.js): cap 240 для Office/Workspace/plugin с S1a | start/total/revision, явный jump-to-latest, viewport anchor |
| Incremental live output | [messages](../../web/js/app-messages.js): keyed/signature reconcile, rAF batching, refreshLiveAgentOutput | Token updates не пересоздают историю; hidden view не делает лишний render |
| Stale/duplicate protection | navigation queue + selection versions; Workspace serial; ConversationObserver in-flight dedup | Быстро A→B→A, late errors, repeated send, reconnect receipt |
| Durable drafts / receipts | ChatDrafts, ConversationInput, existing protected runtime store | Смена owner, late acknowledgement, attachment preparation, draft reload |
| Header/projection caches | [ChatStore](../../src/RNAssistant.Core/Storage/ChatStore.cs): 16 projection entries / 32 Mi chars total; 256 headers / 4 Mi chars total | Не добавлять вторую проекцию; не считать эти caps полным process memory budget |
| Diagnostics metadata-first/lazy reader | [diagnostics contract](../diagnostics.md#ограничения-загрузки-и-состояния): 50 строк, active + 2 retained views, shared Input/Raw body | Открытие summary без body read; cancel on hide; release owner/selection cache |
| Leases/media cleanup | ViewerRegistry.unmount/destroy, resource-download AbortController, lazy thumbnails/viewers | Dispose старого viewer, URL/lease release, поздний payload не меняет другой view |
| HTML/plugin frame reuse и native security | preview-surface, html-workspace-preview, PluginWebViewHost | Resize/move не перезапускает session; CSP/permissions/host identity не ослабляются |
| Data Templates | source pagination, bounded byte reads, workers, operation view | Не заменять на eager file hydration; сохранить cancel/dispose и standalone export |

## Baseline и измерения

Новый полный benchmark в этом docs-only аудите не запускался. Уже имеется
[воспроизводимый diagnostics baseline](../diagnostics.md#сопоставимое-измерение),
Chrome/macOS 2026-11-01, synthetic 2000 messages / 4 473 924 B:
медиана трёх Input/Raw roundtrips 6503 → 1803 ms, body reads на summary 1 → 0.
Heap после закрытия ~6.77 → 6.86 MB: ускорение не означает уменьшение памяти.
Это не startup/Office baseline и не новая проверка на commit 4ea3d6e4.

В [PROGRESS](../stabilization/PROGRESS.md) есть отдельный host-neutral профиль
storage: 300 сообщений / 23 MiB, прежний input scan 128 ms, новый inbox 22 ms;
открытие после каталога 47 ms, повторная session load 5 ms. Последние два значения
относятся к разным условиям загрузки, а не к сравнению до/после.
Это историческое измерение отдельных операций, не полное время открытия UI.

Перед изменением первого среза сохранить сопоставимый before на той же машине
и fixtures; after выполняется теми же действиями. Не придумывать миллисекундные
бюджеты до измерения разброса.

| Метрика | Доступный механизм | Статус |
|---|---|---|
| Cold startup / chat open / switch | RNAssistantPerformanceDiagnostics, existing client timings | Browser before/after S1b ниже; реальный Office startup открыт |
| First streamed paint / long tasks / DOM nodes / JS heap | Existing diagnostics + целевой browser trace/fixture | Before/after нужен; не подменять backend/model timing |
| Metadata/body calls и duplicate operations | Существующие mock/bridge observers и tests | До/после один сценарий, один owner |
| Repeated open/close: listeners, timers, pending operations, retained DOM/leases | Targeted lifecycle checks + heap/DOM observation | Общего точного счётчика всех listeners сейчас нет; evidence собирать по изменяемым owners |
| WebView2 process memory, real Office/session transitions, zoom | Windows x64 / Office x64; диагностика читаема на экране | Open evidence; пользователь передаёт только фотографии, не логи/дампы |

## S1a — единое поведение перед переносом

После «Коммит и далее» аудит сохранён как **119aff97**. Пользователь потребовал
единый чат и доверил порядок подготовки/переноса агенту. Выбран небольшой
рефакторинг непосредственно затронутых владельцев; полный rewrite не нужен.

- ConversationState владеет cap 240 для prepend и refresh обоих consumers.
  Фоновый snapshot той же revision не вытесняет читаемую страницу. Явный переход
  к последним сообщениям обходит сохранение старого окна; он добавлен Workspace
  и исправлен Office. Поздний ответ не возвращает прежнего owner.
- Один composer renderer владеет idle/edit/read-only/reply/stop labels и состояниями.
  Подсказка ответа перемещается вместе с composer. Удалены отдельные Workspace
  подписи, edit renderer и два CSS overrides; сбрасываются устаревшие disabled/
  hidden flags. Фоновый Office mode render не меняет Workspace mode.
  Кнопка отмены правки имеет один dispatch по owner: отмена в Workspace больше
  не сбрасывает приостановленную правку Office из-за второго обработчика.
- Reasoning control общий, учитывает известную неподдерживаемую модель;
  выбор модели показывает title из общего каталога. Draft/receipt и domain commands
  сохранены. Меню выбора модели, вложения, context meter и confirmation ещё требуют
  унификации capabilities; один DOM не объявляется полной parity.

Замер одинакового fixture до/после (baseline 119aff97): 1000 сообщений, 12 предыдущих
страниц — пик сохранённых в Workspace сообщений **1000 → 240**, первые сообщения
остаются доступны. Это размер массива, не измерение RSS/heap. Browser check на пяти
подгрузках отдельно подтвердил ≤240 сообщений в данных и DOM для Office/Workspace;
возврат к последним даёт 80. Native WebView2 и полный startup baseline открыты.

Проверки: chat-history-paging, chat-run-completion, chat-window-layout,
workspace-chat-layout, chat-composer-layout, workspace-shared-draft,
workspace-chat-progress. Проверены drafts/late ack/retry, scope, mode, streaming,
границы и узкая ширина. Старый VM fixture workspace-chat-v8 падает на baseline
до изменений (`$ is not defined`); это открытый test gap, не проходящая проверка.
Релевантная browser-проверка общего renderer пройдена.

Для ревью открыть [production preview](http://127.0.0.1:8897/product-preview.html?review=shared-chat):
1. Написать разные черновики в Office/Workspace и переключить контекст через дерево.
2. Проверить общие placeholder, clear, mode/reasoning и редактирование сообщения.
3. В узком окне проверить рост ввода и доступность действий.

[Office](review/s1a-chat-office.png), [Workspace](review/s1a-chat-workspace.png) —
снимки настоящего production UI на synthetic bridge; модель и Office не запускались.
Визуальные детали In Review. Откат S1a — revert, durable history не менялась;
второй composer/window implementation и временный adapter не добавлены.

## S1b — размещение общего чата и Inspector

**Одна существующая conversation workspace для Office и Workspace + настоящий
Inspector в одном Dockview**, внутри текущей production Shell.

1. Добавить единственный Dockview instance и минимальные descriptors conversation/
   inspector. Перемещать существующие DOM mounts; не создавать iframe, копии
   messages/composer или новый chat store. Подключить текущую смену owner.
2. Перенести управление этими областями наверх; Dockview владеет их split,
   maximize/restore и сохранённым layout. Inspector сохраняет реальные разделы,
   scope, lazy data и ссылки в Diagnostics.
3. Оставить существующие domain commands, bridge, run ownership и storage.
   Библиотека, материалы, VBA, plugins, diagnostics пока открываются
   существующим switchTab в той же Shell; это временная граница миграции,
   не второе приложение или второй Dockview.
4. Сделать явными activate/deactivate для двух views, сохранив drafts, active
   target, scroll/disclosures и suspend уже существующих visual requests.
5. Переключить оба conversation consumers — Office и Workspace — в одном срезе.
   Plugin owner проходит smoke через тот же composer; native plugin frame не
   переносится на этом шаге.

**Ограничение S1:** один живой conversation view, как сейчас. Несколько одновременно
открытых самостоятельных чатов не обещаются: global IDs/state/owner сначала
нужно сделать view-scoped отдельным срезом. Ограничение видно в chooser; новые
области не предлагают ложную копию чата.

Реализованы Shell/topbar/statusbar, один DockWorkspace, два существующих DOM views,
вкладка выбора, перенос/размер/maximize, group-local close, пресеты и сохранение.
Старые Inspector placement/width/overlay и per-owner toggles удалены. Plugin/HTML
preview сохраняют свои владельцы и общий верхний контрол. Никаких новых C#,
bridge/storage/model contracts. Все изменения среза — static UI, assets и docs.

По замечанию пользователя плоская первая оболочка S1b отклонена. Реализация
приведена к P1/P2 reference: [перенесённое описание](UI_DESIGN_SYSTEM.md#эталон-оболочки-завершённый-dockview-demo),
[команды](UI_SCREEN_MAP.md#команды-оболочки-s1b). Старый popup «+» заменён
согласованной вкладкой выбора; тема/акцент остаются в реальных настройках.

Критерии S1: фактическая отправка/stream/stop/confirmation/receipt semantics прежние;
draft не теряется при owner switch, move, hide/reopen; старый ответ не попадает в
новый chat; resize/maximize не вызывает init/повторных reads; performance не хуже
измеренного baseline сверх согласованного разброса; узкий режим/keyboard/zoom
проверены. Общий bounded window уже введён в S1a; его проверки остаются
обязательными при Dockview и последующем расширении на multiple chat views.

### Уточнение S1b: боковые панели и иконки

По запросу пользователя перенесены режимы и команды Demo для левого дерева:
wide/overlay, постоянный rail, повторный клик раздела, выбор/раскрытие, scrim/Escape,
широкое предпочтение. Отдельная группа Inspector сворачивается без закрытия;
в смешанной/узкой группе активируется его вкладка. Исправлен реальный размер
кнопок группы: общий control CSS больше не растягивает их до 34 px.
Контракт — [Screen Map](UI_SCREEN_MAP.md#боковая-навигация--поведение-demo).

`shell-navigation.test.js` проверяет 900/420/360 px, выбор мышью/клавиатурой,
settings, сохранение draft/DOM, hide/restore Inspector и защиту единственной группы.
Существующие dock-workspace, chat-inspector и workspace-chat-layout проходят.
На этом checkpoint старый settings-navigation-layout имел устаревшие ожидания
количества разделов/версии. В S1c fixture приведён к реальному UI и проходит;
[gap закрыт](../stabilization/BACKLOG.md#settings-navigation-fixture-версия-в-hover--2026-10-30).
Текущий выбор страницы настроек и закрытие её дерева покрыты новым Shell-тестом.
Снимки: [900 px](review/s1b-navigation-900.png), [360 px](review/s1b-navigation-360.png).

После отдельно согласованного Settings/rail S1c следующий предлагаемый подсрез **S2a** — единые model/context/attachment capabilities
и controls Office/Workspace/plugin. View-scoping, несколько живых чатов и paging
следуют отдельными ограниченными изменениями, когда для них готовы владельцы.
Сначала визуальное ревью текущего среза; параллельный большой UI Kit/refactor не нужен.

### Уточнение S1b: общая геометрия после визуального ревью

Пользователь отклонил вид полностью раскрытого дерева: прошлый перенос поведения
не обеспечил геометрию эталона. Найдены прежние ширина/разделитель navigation,
flat indentation Tree, резервирование места скрытыми actions, vendor rem padding
и начальный resize Inspector после установки navigation defaults.

Исправление выполнено в существующих общих tokens, NavigationTree, ShellLayout
и Dockview adapter. Удалены численные defaults отдельных разделов, leaf spacer,
flat depth padding и plugin tree overrides. Tree не захватывает focus на фоне;
создание чата из Office menu по-прежнему переводит focus в выбранный узел.

`shell-navigation.test.js` сравнивает реальные computed размеры Demo и production
при одинаковом viewport: navigation/splitter, workspace/groups, tab/action spacing,
строки/вложенность Tree. Проверяет независимость от root font scale и общий
resize/reset. Из проверяемых browser checks также прошли dock-workspace,
workspace-chat-layout, chat-tree-layout; splitter-preferences покрывает generic
drag/cancel/keyboard. Светлый раскрытый и тёмный overlay осмотрены в Chrome.
Runtime/Bridge не менялись; это не Windows/Office qualification.

Снимки: [исходная ширина](review/s1b-shared-navigation.png),
[максимальная ширина, все документы раскрыты](review/s1b-shared-navigation-expanded.png).
Проверить: resize/Home, повторный клик rail, клавиатура/actions строк, узкий overlay.
Визуальный результат **In Review**. Общая очистка всего feature CSS не заявлена;
следующий срез остаётся после этого ревью. Откат — один diff, без изменения данных.

### Завершение Shell по повторному аудиту

Shell chrome теперь имеет единые владельцы CSS и DOM; контекстные данные и callbacks
передаются из Dock adapter без второго store. Перенесён chooser P1/P2: верхнее
размещение, адаптивная сетка, исходные отступы, поиск/Enter и видимое пустое состояние.
«Панели» и меню групп используют существующий ActionMenu. Закрытие/resize/dispose
освобождают popup; Escape возвращает focus. После responsive fold focus сохраняется
на следующем paint без перехвата у другого элемента.

Удалены заменённые Shell/rail rules из app.css/Dock CSS, старый responsive layout
дерева, неиспользуемый `#tab-workspace` CSS и отдельный Dock menu renderer.
Настройки/библиотека/VBA используют тот же sidebar header. Пользовательский accent
сохраняется; selection/shadow следуют Demo. Демо-переключатель ширины, mock-сценарии
и фиктивные экраны в production не копируются; тема/акцент доступны через настройки.

Проверки: shell-navigation сравнивает computed geometry с настоящим Demo, включая
chooser; dock-workspace — move/split/close/restore/focus; workspace-chat-layout —
Office/Workspace/plugin и drafts; shared-navigation-tree — standalone
Tree/Toolbar/Menu/Progress, cleanup, keyboard и light/dark без Shell/bridge.
Это host-free component fixture, не полная plugin SDK/`file://` qualification.

Повторный before/after — тот же performance fixture, по три страницы, Chrome/macOS.
Before — рабочее состояние после предыдущего ревью геометрии, до повторного аудита
(незакоммиченный diff поверх 93814b4b), не чистый commit baseline.
Готовность: медиана 234,9 → 226,7 мс; диапазоны 234,5–235,8 / 223,8–245,0.
20 циклов Inspector: 136,8 → 135,2 мс; heap ~14,27 → 14,30 MB без forced GC.
DOM после смены owner — 2268 в обоих случаях; getProjectChats/getProjectChat — по одному.
Ускорение не заявляется по трём пробам; увеличения reads/DOM не обнаружено.
[Raw samples](review/s1b-shell-audit-measurements.json).

Результат **In Review**, архитектурные gaps перечислены в повторном аудите;
полный перенос всех domain views и Windows qualification не заявлены.
[Shell со вкладкой выбора](review/s1b-shell-chooser.png).

## S1c — Settings view и rail menus

Scope явно согласован пользователем после commit `2a4831c5`: одна вкладка настроек,
gear снизу слева, сохранение hover menus/подписей в общем стиле. Это ограниченный
перенос существующей формы, не переписывание Settings/domain/runtime и не P4.
Визуальные детали **In Review**.

- Один descriptor/DOM/form/save owner; `#tab-settings` и его глобальный splitter
  удалены. Шесть прежних категорий и поиск внутри view; узкая группа использует
  select из того же списка. Save footer не перекрывает контент.
- Draft живёт в текущей форме при switch/move/close; close предупреждает, что он
  сохранён только до перезапуска. Cancel group-close атомарен; keyboard, preset и
  restore используют тот же guard. Save race/error сохраняют новые правки.
- Прежняя cancellation catalog preview сохранена; late response не гидратирует
  скрытую форму. Новый cache/store не создан. Join сохраняет активный view.
- RailPopovers сохраняет существующие plugin/settings actions и tooltip labels.
  Общий menu CSS заменяет его прежние overrides. Hover intent задерживает
  случайные открытия; Escape не переоткрывает меню, generation отсекает stale reply.

Проверены `settings-dock`, `settings-navigation-layout`, `agent-history-settings`,
`dock-workspace`, `shell-navigation`, `shared-navigation-tree`; отдельно
`settings-version` и `prompt-review` (13 checks). Существующие fixtures обновлены
для нового расположения, шести разделов, common Tree API и container width.
Проверены обе темы, узкие группы/окно, масштаб текста, move/close/restore,
один DOM, сохранение черновика, late save/error и late catalog/hover responses.

Минимальная performance-проверка: четыре open/close чистой формы не вызывают
bridge reads; DOM форма/composer имеют прежнюю identity. Подключения остаются
118 scripts / 44 stylesheets, vendor неизменен, новых dependencies нет.
Это не измерение ускорения и не Windows/Office/WebView2 qualification.

Ревью: `python3 -B prototypes/serve_ui.py`,
[рабочий UI](http://127.0.0.1:8897/preview/web/?scenario=ready&review=settings-dock).
Навести на gear → выбрать раздел; открыть настройки повторно; перенести через «+»;
изменить поле и закрыть вкладку/группу с отменой; сузить окно.
[Широкий вид](review/s1c-settings-dock.png), [меню](review/s1c-settings-hover.png),
[узкая группа](review/s1c-settings-narrow.png).

Rollback: отменить только diff S1c поверх `2a4831c5`; данные/bridge/runtime не
мигрировались. Старый валидатор явно сбросит layout с новым `settings` descriptor,
если такой layout уже сохранён. Неприменённые правки не являются durable data.
Активный заголовок вкладки прокручивается в видимую часть узкой полосы.
Открытая source-audit граница model capability drafts —
[BACKLOG](../stabilization/BACKLOG.md#settings-model-capability-draft-boundary--2026-11-02);
общая изоляция applied/draft capabilities остаётся S2a.
После ревью — предложенный S2a; полного переноса остальных views этот срез не означает.

## Последующие срезы — только после ревью предыдущего

| Срез | Содержание | Что удаляется после проверки |
|---|---|---|
| S1a / S1b | Общие chat rules и production Chat workspace + Inspector реализованы; визуальное ревью S1b по эталону Demo | Дубли composer presentation и старый Inspector grid/split/open-width удалены. Остальные splitters пока нужны |
| S2 | View-scoped chat presentation, полная parity model/context/attachment capabilities, chat catalog paging при необходимом typed contract | Заменённые global renderer/current-selection paths; без второго durable store |
| S3 | Browser/material viewers + diagnostics views, lazy detail, deep links | Старые раздельные placement/toggle paths, redundant viewer mounts |
| S4 | Library/VBA/project navigation; Settings вынесен в S1c; shared reusable UI для app/plugin | Только подтверждённые duplicate controls/styles, legacy route aliases после consumer switch |
| S5 | Оставшиеся plugin views, portable export/public API и финальная сверка единства | Migration adapters, obsolete CSS и Demo runtime-копии; reference screenshots можно сохранить |

Каждый срез: problem/scope → baseline → маленький diff → behavior/perf checks →
визуальное ревью → согласование → удаление заменённого пути. Нет автоматического
массового rename, нового npm/bundler/loader, универсального store или SDK framework.

## Проверки, откат, временные пути

Выбирать существующие tests по риску; как минимум проверить затронутые assertions
в chat-composer-layout, chat-inspector, office-host-chat-layout,
workspace-shared-draft, multi-chat-requests, chat-history-layout, live-run-projection,
splitter-preferences. Добавлять только недостающее meaningful coverage.
Для reader отдельно diagnostics-performance; для shared UI — standalone
Tree/Toolbar/Progress/theme fixture и plugin compatibility. Не запускать всё
подряд на docs-only этапе. Windows/Office не проверять на этой машине.

Каждый срез сохраняется атомарным commit, без миграции пользовательских данных.
Откат — revert среза/предыдущий проверенный UI build; новый layout key/schema не
перезаписывает старые данные и может быть явно сброшен. Feature flag нужен только
при реальной необходимости контролируемого включения, не для постоянной второй
оболочки. Owner/removal gate регистрируются в [Migration Map](../stabilization/MIGRATION_MAP.md).

P3 iframe adapter остаётся только в замороженном Demo и не становится production
зависимостью. В S1 временная граница — немигрировавшие sections той же Shell;
условие удаления старого switchTab placement — переключение последнего его
consumer с проверкой старых route aliases и deactivation side effects.

## Проверка S1b и визуальное ревью

Сравнение на Chrome/macOS, одинаковый ready fixture, 1280×900, один начальный чат
и закрытый Inspector (в S1b сохранён single-view preset), по три свежих страницы.
Before — fd474f91 до изменений; after — текущий S1b.
Медианы/разброс и raw samples сохраняются в [browser measurements](review/s1b-measurements.json).
`dock-workspace-performance.test.js` повторяет fixture: boot, 20 циклов Inspector,
черновик, открытие одного Workspace-чата. JS heap — приблизительный `performance.memory`,
без forced GC; это не RSS и не доказательство отсутствия всех утечек.

| Метрика, медиана (диапазон) | До | После |
|---|---|---|
| Готовность fixture, мс | 238,2 (228,9–249,4) | 242,4 (236,7–242,9) |
| 20 открытий/закрытий Inspector, мс | 35,5 (35–36) | 151,3 (150,5–152,3) |
| JS heap после переключения, MB | 10,86 (9,95–10,86) | 14,18 (14,13–14,18) |
| DOM после переключения | 2171 | 2252 |

Это цена нового Dockview: локальный bundle 561 KB, около 3,3 MB дополнительного
измеренного heap и 5,8 мс на цикл открытия/закрытия. Начальная готовность остаётся
в прежнем диапазоне трёх проб; ускорение или отсутствие регрессии на Windows не
заявляются. После 20 циклов retained DOM увеличивается на те же 22 узла Inspector
в обеих версиях; запросы данных и identity composer/transcript не меняются.

Проверки среза: dock-workspace (настоящий pointer drag, клавиатурный размер группы,
chooser/search/Enter, close/restore, тот же DOM/draft/selection/scroll, скрытый paint,
узкий/широкий режим), chat-inspector, workspace-chat-layout/shared-draft/progress,
chat-window-layout, chat-composer-layout и vendor-gate. Старые assertions размещения
переключены на Dockview; domain/send/retry/receipt assertions сохранены.
Resize не пересоздаёт Inspector; изменения layout не делают domain reads.
Один project switch сохраняет один getProjectChats и один getProjectChat.

Реальные Office/WebView2, browser zoom Windows, COM и native frame positioning
не квалифицированы этой проверкой. Браузерная ширина проверяет адаптацию;
полная memory qualification и несколько независимых чатов остаются открытыми.

Ревью: [production preview](http://127.0.0.1:8897/product-preview.html?review=dock-s1b).
1. Сравнить Shell с [эталоном P2](review/p2-tab-actions.jpg): пропорции, вкладки,
   topbar, меню группы; содержимое чата остаётся production.
2. Нажать «+» или область справа/снизу: найти Инспектор, открыть через Enter,
   перетащить вкладку; «⋯ → Закрыть группу» не затрагивает соседнюю группу.
3. Написать черновик, закрыть/вернуть чат через «Панели», переключить Office/Workspace.
4. Сузить окно: выбранная вкладка сохраняется; затем восстановить широкое окно.

[Office](review/s1b-chat-office.png), [узкий вид](review/s1b-chat-narrow.png),
[Workspace](review/s1b-chat-workspace.png) — локальный production UI на mock bridge.
Результат In Review. S2 не начинается автоматически.
Откат — revert атомарного UI-среза. Layout keys `rnassistant.ui.dock.s1.v1`
и `.saved` не мигрируют durable chat data; старые Inspector width/open keys больше
не читаются. Замороженному P3 понадобилось лишь обновить ссылки на общий vendor и
пропустить внутренний Dockview в preview-frame, чтобы не получить вложенную Shell.
