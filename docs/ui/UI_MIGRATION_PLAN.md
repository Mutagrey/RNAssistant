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
| F — Inspector/diagnostics | Inspector в S1b; Request в S3a по прямому запросу | Request перенесён; журнал/Lab/Приложение и общий background status ещё открыты |
| G — остальные views | S3 → S4 → S5 | Переносить по одному функциональному срезу через существующий каталог; сохранять native host/session boundaries |
| H — удаление legacy | Обязательная часть каждого среза, финальная сверка S5 | Заменённые paths удаляются сразу после проверки; временные границы имеют owner/removal gate в MIGRATION_MAP |

Очередь уточнена прямым запросом пользователя: **Shell S1b (`2a4831c5`) →
Settings/rail S1c (`26f4d87f`, Approved) → Request/diagnostics controls S3a (прямое указание пользователя) → S2a capabilities общего чата → остальные
части S2 → S3/S4/S5**. S1c выделен из Settings-части S4; исходный план сохранён. Единство registry/commands не откладывается
до S5: каждый новый view подключается к уже существующему механизму. Следующий
функциональный срез автоматически не начинается до ревью предыдущего.

После уточнения пользователя о единой системе навигации частный plugin-only S3b
заменён проектом S3b.1–S3b.3 ниже. Предыдущая карта экранов не считалась готовым
контрактом объединения; следующий шаг здесь — ревью общего маршрута и его owners,
затем первый законченный срез с двумя реальными consumers.

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
Текущий результат **Approved** по «Меня устраивает»; commit `26f4d87f`.
Production sources входят в Office/Desktop через `web/**`; commit сам по себе
не обновляет установленное приложение. Push и новая Windows-сборка ещё не выполнены.

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
общая изоляция applied/draft capabilities реализована в S2a ниже.
После ревью — предложенный S2a; полного переноса остальных views этот срез не означает.

## S2a: модель чата и настройки возможностей

**Реализовано, In Review (2026-11-02).** Пользователь явно расширил scope:
выбор модели должен работать и для Workspace; общие возможности чата не должны
дублироваться по типам Office/project/plugin.

Перенесён общий picker, сохранение override/default через existing chat store,
единый `ChatSettingsResolver`, защита модели принятого run. Удалён Workspace-only
переход в настройки вместо выбора. Исправлены applied/draft capabilities,
attachment priority/reset/calibration и поздний ACK; catalog preview отделён от
applied catalog. Скрытая Settings view и закрытое меню сохраняют lazy rendering.
Меню укладывается в узкую группу без обрезания.

Проверки: `model-settings`, `model-navigation` (500 моделей, 100 закрытых render —
0 созданных catalog nodes), `prompt-review`, `settings-navigation-layout`,
`settings-dock`, `workspace-shared-draft`. Harness: `workspace: chat model
preferences and wire`, `chat: session model overrides global settings`,
`plugins: approval continuation belongs to application` (complete/stop/close/deny/terminal).
Проверены реальные HTTP bodies локального scripted provider: override/reopen,
изменение default/reset, принятая модель после approval; busy lease отказывает сразу.
Устаревший `files.create` в approval fixture заменён действующим `core.write`.
Это host-neutral evidence; реальный Office/WebView2 и provider не квалифицированы.

Ревью: открыть production preview `/preview/web/?scenario=ready`, выбрать модель
в проекте и чате плагина, вернуться в Office; переключить default; проверить меню
при 420 px. В Settings изменить возможности без Apply, затем применить: composer
меняется только после подтверждения сохранения. Mock не запускает реальную модель.
Снимки — в Design System. Визуальные детали ждут согласия пользователя.

Откат — revert атомарного commit S2a; новые store/формат сессий/vendor не добавлены.
Сохранённое предпочтение модели остаётся обычным `ChatSession.Model`.

### Остаток S2: единая реализация и независимые чаты

S2a (`abfd7c85`) завершает модель/preferences, но **не весь общий чат**. По уточнению
пользователя Office/project/plugin не должны иметь отдельные реализации chat UI;
целевая граница — [единый чат](UI_ARCHITECTURE.md#единый-чат-обязательная-целевая-граница).
Сейчас Dockview имеет один `conversation`: выбор другого чата заменяет его target.
Открыть одновременно несколько независимых чатов пока нельзя.

Порядок оставшейся работы, небольшими проверяемыми срезами:

1. Выделить общий Chat View со state/lifecycle конкретного target; инвентаризировать
   и переключать global/owner-specific render/commands на явный scope. Начать с
   существующего одного view, удалить заменённые пути; runtime/store не дублировать.
2. Через эту границу объединить вложения, контекст, подтверждения/вопросы и остальные
   действия. Сверять весь набор возможностей на каждом источнике; недостающие typed
   contracts закрывать у domain owners, а не постоянным скрытием кнопок по типу чата.
3. В существующем DockWorkspace разрешить отдельные views разных exact chat targets.
   Повторное открытие того же target активирует его view. Проверить одновременные
   draft/edit/model/history/stream, late replies, move/split/close/reopen; закрытие
   вкладки не отменяет run и не очищает чужое состояние.
4. Сохранить cap 240 на view, lazy hidden rendering и отмену лишних reads; проверить
   общие bounds памяти/подписок при нескольких чатах. Catalog paging вводить при
   необходимом typed contract. Удалить временные adapters после переключения consumers.

S2 завершён только после этих behavioral gates и визуального ревью; Windows/Office
evidence остаётся отдельным gate. S3b сохраняет свой scope и не подменяет эту работу.

### S2b.1: общий owner отрисовки — 2026-11-02

Scope разрешён «Давай» после уточнения цели единого чата. Первый атомарный шаг
пункта 1: ConversationView владеет target/generation, кешем DOM, прокруткой и
paint lifecycle для всех источников. Заменённые Office/Workspace caches и Dock
scroll/dirty state удалены. Визуальная компоновка, composer/draft store, contracts
и runtime не меняются. На target switch/reparent/hide отменяются прежние frames;
ожидающий paint вызывает refresh при возвращении. Позиции чтения bounded (16),
без накопления histories/DOM. Cleanup media не затрагивает соседний view.

Проверки среза — `conversation-view` (два независимых presentation instances,
A→B→A/late frame, hide/restore, loading anchor, bounds/dispose), существующие
`live-run-projection`, `stream-render-cost`, `message-actions`, Chromium
`dock-workspace`, `workspace-chat-layout`, `chat-transcript-layout`,
`chat-image-display`, `chat-input-delivery`. Визуальный статус **In Review**;
headless mock не квалифицирует Windows/Office. Нет C# build или Office validation.

Ревью в production preview: прокрутить длинный чат вверх; переключить источник и
вернуться; закрыть/вернуть вкладку; перенести её справа/снизу во время ответа.
Проверяются прежняя позиция чтения, неизменность ввода и отсутствие чужих сообщений.
Откат — атомарный diff S2b.1: изменён только presentation, без durable migration.

Далее завершить пункт 1: common builders/composer/commands и подписки получают
explicit view scope вместо global active owner. Только затем включать разные
chat targets одновременно. Ни общий чат целиком, ни multiple chat UI этим срезом
не объявлены реализованными.

### S2b.2: ввод и незавершённые правки — 2026-11-02

По «Коммит и далее» S2b.1 сохранён в `bb53539b`; продолжен пункт 1 S2.
Общий ChatComposer заменяет Workspace registry/bindings и Office `state.editing*`.
Одинаковое хранение edit records на exact target, восстановление исходного ввода,
единые keyboard/Send rules. Snapshot больше не сбрасывает Office edit; renderer не
перезаписывает его текст. Отложенный focus/quick-action и accepted edit защищены
target/generation либо identity отправленного record. Draft persistence, input
receipts и runtime не изменены. Нового CSS/экрана/библиотеки нет.
Исправлена потеря Office-правки при отказе bridge: record удаляется после успеха,
при ошибке остаётся доступным. Pending edit блокирует изменение отправляемого
текста; обычный черновик следующего сообщения не блокируется.

Ревью — **In Review**: начать правку в Office и в проекте, переключиться между ними;
обновить чат; отменить одну правку и проверить другой чат/исходный ввод. При смене
чата отложенное восстановление cursor не должно захватывать фокус. Mock запускается
по прежнему `/preview/web/?scenario=ready`; это не Office qualification.

Проверки: `conversation-composer` (два экземпляра, targets, A→B→A, IME/disabled Send,
focus/dispose); `message-actions`, `multi-chat-requests`, `attachment-ingestion-order`;
browser `workspace-chat-layout` с refresh/switch/failed-edit checks, `chat-input-delivery`,
`workspace-shared-draft`, `chat-draft-reload`. Последний fixture ждёт существующий
Dock tab вместо скрытого прежнего header. Откат — diff этого presentation-среза,
без изменения формата пользовательских данных.

Остаток пункта 1: builders/snapshot/subscriptions и preference/draft adapters должны
получать конкретный view вместо global selection. Затем — оставшиеся общие функции
вложений/контекста и несколько разных chat targets в Dockview. S2 целиком не закрыт.

### S2b.3: адресные builders и действия ленты — 2026-11-02

По «Коммит и далее» S2b.2 сохранён в `19a271ed`; продолжен пункт 1 S2.
Общие saved-message/run builders, live projection и footer получают явные данные
и commands представления. Удалены переключение Office/Workspace внутри footer,
неявный выбор view в live builders и global full-render flag. Existing service
adapters сохраняют transport/receipts и правила доступности действий.

Исправлен риск переадресации последовательного удаления: между awaited delete
ответами пользователь мог выбрать другой чат, а следующая команда читала его ID.
Теперь captured binding проверяется перед каждой командой, включая A→B→A;
оставшаяся последовательность останавливается с уведомлением. Отложенная кнопка
истории также проверяет binding до начала чтения.

Проверки: `conversation-view`, `message-actions` (два borrowed snapshots,
async delete/navigation, deferred history), `live-run-projection`,
`stream-render-cost`; Chromium `workspace-chat-layout`, `workspace-chat-progress`,
`chat-inspector`, `run-changes-layout`. Сохранены 1000 attached history nodes в
изолированном render-cost fixture и один кадр на burst из 100 событий; это проверка
прежних инвариантов, не заявление об общем ускорении или новом лимите истории.

Статус **In Review**. В прежнем `/preview/web/?scenario=ready` проверить смену чатов
и возврат, edit/fork/историю, «Открыть ход работы»; Dock move/hide не должны отключать
кнопки уже показанных сообщений. Компоновка и стили не менялись. Mock/browser checks
не закрывают Windows/Office evidence. Откат — diff этого presentation-среза.

Далее: адресовать snapshot delivery/subscriptions и preference/draft adapter,
затем inbox/approval/context/attachment consumers в общей границе; только после
этого независимые exact targets в Dockview. S2 и несколько chat tabs не завершены.

### S2b.4: наблюдение и поздние ответы — 2026-11-02

По «Коммит и далее» S2b.3 сохранён в `60b04d71`. Shared ConversationObserver теперь
принадлежит конкретному view. Его lifecycle общий для Office inbox/history и
Workspace snapshots; убраны Office singleton observer и отдельная глобальная
карта history-refresh. Existing intervals, push freshness, exact receipts и runtime
сохранены. Source adapters предоставляют данные и transport; новых stores нет.

Stop/hide/dispose не позволяют ожидающему ответу/ошибке обновить прежний binding.
Смена чата не блокируется старым запросом. Refresh bursts дают максимум один
следующий read; пропущенное hidden обновление даёт один catch-up даже без таймера.
Без такого события возврат соблюдает source `shouldPoll`; idle layout не читает данные.
Это локальная экономия UI-опроса; общее ускорение приложения не заявляется.

Закрыт [поздний notice deleteMessage](../stabilization/RISK_REGISTER.md#позднее-уведомление-deletemessage--2026-11-02):
source команды передаёт captured scope, поздний error не попадает в другой чат,
поздний success не заменяет его active snapshot. Вложенный inbox→history read также
получил общий binding/lifecycle guard.

Проверки: `conversation-observer` (два view, stop/hide/dispose, A→B→A, passive
catch-up, 100 refresh→1 follow-up), `conversation-view`, `chat-inbox` с поздним
history response, `message-actions` с реальным deleteMessage delivery,
`multi-chat-requests`; Chromium `dock-workspace`, `workspace-chat-progress`, `workspace-chat-layout`,
`chat-input-delivery`. Последний fixture ждёт результат в DOM вместо удалённого
global pending flag. Scope — Web presentation, без C#/Office validation.

**In Review**. В mock `/preview/web/?scenario=ready` запустить ответ, закрыть/вернуть
conversation, переключить источник и вернуться; проверить актуальную ленту и
сохранность ввода. Стили прежние. Закрытие UI не останавливает run. Windows/Office
evidence остаётся открытым; откат — diff среза без migration пользовательских данных.

Далее: service adapter конкретного target вместо Office global state / Workspace
active selection, затем preference/draft, inbox/approval/context/attachment
consumers. Пока общая карта Dockview сохраняет один conversation; S2 не завершён.

### S2b.5: адресные настройки общего composer — 2026-11-02

По «Коммит и далее» S2b.4 сохранён в `e7e8c47f`. Следующий ограниченный участок
пункта 1 — модель/режим/reasoning. Один ChatComposer владеет командами и ожиданием;
источники предоставляют факты и exact-target preference service. Заменённые global
saving flags, Workspace preference dispatch/render и ветка runChat удалены.
Нет нового store, registry, CSS, C# или bridge contract.

Сохранены input barriers, draft versions, uncertain read-back без повторной записи,
Office navigation drain и Workspace revisions. A→B→A получает свежий read вместо
применения старого ответа. Office mode не показывается принятым до ACK; пока настройка
сохраняется, можно писать следующий текст, но Send заблокирован. Workspace mode и
reasoning по прежнему сохраняются в ChatDrafts для следующей отправки.

Baseline: `model-navigation` и Chromium `model-settings`. Проверки среза:
`conversation-preferences`, `conversation-composer`, `model-navigation`,
`multi-chat-requests`, `attachment-ingestion-order`, `message-actions`;
Chromium `model-settings` и `workspace-shared-draft`. Браузерная проверка отделена
от параллельного S3b.2: baseline `e7e8c47f` + только этот diff. Office preferences
добавлены в существующий mock для проверки ACK/режима, не в замороженную Shell Demo.
Это local mock evidence; Windows/Office и объединённое состояние параллельных срезов
не объявляются квалифицированными.

**In Review**. Запуск production preview — `python3 prototypes/serve_ui.py`,
`/preview/web/?scenario=ready`. Проверить: выбрать модель и включить план/reasoning;
написать текст и переключить Office → проект → плагин → назад; проверить свои
настройки и черновик. Компоновка прежняя. Откат — diff среза без миграции данных.

Остаток пункта 1: адресные snapshot factories/full push routing, общий draft/file
binding и вспомогательные inbox/approval/context/media consumers. Затем независимые
chat targets в Dockview. Полный S2 и несколько вкладок чатов пока не завершены.

## Последующие срезы — только после ревью предыдущего

| Срез | Содержание | Что удаляется после проверки |
|---|---|---|
| S1a / S1b | Общие chat rules и production Chat workspace + Inspector реализованы; визуальное ревью S1b по эталону Demo | Дубли composer presentation и старый Inspector grid/split/open-width удалены. Остальные splitters пока нужны |
| S2 | View-scoped chat presentation, полная parity model/context/attachment capabilities, chat catalog paging при необходимом typed contract | Заменённые global renderer/current-selection paths; без второго durable store |
| S3 | Browser/material viewers + diagnostics views, lazy detail, deep links | Старые раздельные placement/toggle paths, redundant viewer mounts |
| S3b — пересмотрен после zoom out | Общая навигация → exact объект → независимый view; общий Browser для HTML/plugin, существующие специализированные viewers | Дубли navigation chrome/commands, inline preview placement, ChatBrowser/plugin splitters после переключения их consumers |
| S4 | Предметные Library/VBA/project views и редакторы на общей navigation/view основе S3b; Settings вынесен в S1c | Только подтверждённые duplicate controls/styles, global editor-selection paths и legacy routes после consumer switch |
| S5 | Оставшиеся plugin views, portable export/public API и финальная сверка единства | Migration adapters, obsolete CSS и Demo runtime-копии; reference screenshots можно сохранить |

Каждый срез: problem/scope → baseline → маленький diff → behavior/perf checks →
визуальное ревью → согласование → удаление заменённого пути. Нет автоматического
массового rename, нового npm/bundler/loader, универсального store или SDK framework.

### S3b.1: общая навигация — In Review, 2026-11-02

План сохранён в `535da5b3`; первый production-срез реализован. Owner: Web navigation
presentation; данные/команды у Library и ArtifactCatalog, placement у Shell.
Общий NavigationPane, стабильные library keys и explicit target routes подключены
к двум consumers. Выбор HTML больше не вызывает `changeArtifactLink`; фильтр не
подменяет редактор/материал. Заменённые headers/search/menu handlers и CSS удалены.
VBA-подобное дерево проверяет применимость компонента без source-name условий;
реальный VBA consumer переносится в S4 по таблице ниже.

Минимальные проверки: `instruction-loading`, `tree-adapter`, browser
`shared-navigation-tree`, `artifact-catalog-layout`. Покрыты lazy/coalesced loading,
retry/late replies, stale scope, предел 500 строк, exact preview/version/download,
delete/dirty guard, whole-row/keyboard, два production consumers и disposal.
Старый изолированный artifact fixture переведён на настоящий Shell layout.
Chromium: 1280/420 px, light/dark, CSS zoom 125%, menus внутри viewport и видимый focus.
Windows/Office/реальные модели не запускались.

Ревью (запуск `python3 -B prototypes/serve_ui.py --port 0`, затем напечатанный URL +
`preview/web/?scenario=ready`):
1. Библиотека → открыть навык → поиск другой записи: редактор остаётся; Escape
   возвращает полное дерево. Переключить раздел и вернуться — query/collapse/scroll
   принадлежат прежнему источнику.
2. Материалы → открыть HTML в таблице/дереве → посмотреть исходник/историю. Просмотр
   не меняет контекст чата; команда использования остаётся явной.
3. Сузить до 420 px → открыть навигацию верхней кнопкой → «+» → Escape. Дерево
   использует высоту панели, menu не обрезается. Повторить в тёмной теме.

[Библиотека, wide](review/s3b1-library-wide.png),
[Библиотека, narrow/dark](review/s3b1-library-narrow-dark.png),
[Материалы, wide/dark](review/s3b1-materials-wide-dark.png),
[Материалы, narrow](review/s3b1-materials-narrow.png).

Измерение на одинаковых 2000 metadata items: 12 синхронных render/layout, первые
2 исключены; медиана **86,6 → 89,1 ms**. Ускорения полного дерева нет. Один filter
route **13 → 4,2 ms** и перестал менять selected editor; короткая выборка не
доказывает общее ускорение. Тела не входили в замер, DOM фильтрованного списка
одинаков — 22 узла. [Raw samples](review/s3b1-navigation-metrics.json),
[повторяемый probe](../../tests/web/navigation-pane-benchmark.js): передать URL
production preview (можно отдельного baseline checkout). Крупная Библиотека пока
рисуется целиком; virtualisation не объявлена выполненной.

Остаток: S3b.2 — независимые exact-target viewers и bounded/cancellable body reads;
S3b.3 — единый Browser HTML/plugin и iframe lifetime; S4 — предметные Library/VBA
редакторы и остальные adapters. Сейчас preview/editor остаются singleton, выбор
чата ещё сбрасывает material view; legacy chat artifact route и detail toolbar
заменяются с соответствующим viewer, а не дублируются новой системой сейчас.
Move/split/restore для новых object views ещё не существует и не квалифицирован.
Откат среза — его UI diff, без изменения durable данных или product version.

### S3b: общая навигация и независимые просмотры — Proposed

Пользователь сначала разрешил отдельный plugin preview, затем потребовал zoom out:
Материалы/Библиотека/будущие Файлы используют общую навигацию; HTML/plugin — общий
browser; остальные форматы — независимые экземпляры действующих viewers.
Прежний plugin-only порядок заменён. Код размещения S3b ещё не менялся; выполнены
source audit и локальный iframe probe на `c01daa34`. S3a завершён (`c01daa34`),
S2a model/capability work имеет свой scope и не смешивается с этой миграцией.

[Границы и owners](UI_ARCHITECTURE.md#единая-навигация-и-вкладки-объектов--проектирование-2026-11-02),
[маршруты и схема экрана](UI_SCREEN_MAP.md#общая-навигация-и-независимые-объекты--proposed-2026-11-02),
[общие элементы](UI_DESIGN_SYSTEM.md#общая-панель-навигации-и-object-views--proposed-2026-11-02).

| Порядок | Законченный результат | Удаление и проверка |
|---|---|---|
| S3b.0 — общая модель, план `535da5b3` | Source/object/commands/view type/view instance/renderer, матрица возможностей и группировка всей карты по семействам UI; явная точка подключения в существующие каталоги | Не начинать с копирования конкретного экрана. На трёх маршрутах проверить ownership, identity, dirty/close/restore и отсутствие особых условий по имени source в Shell |
| S3b.1 — навигация и команды | Общий каркас на существующем NavigationTree для Материалов и Библиотеки, adapters источников; один open route в нынешний DockWorkspace. Открытие отделено от attach/use/edit/delete | Заменённые headers/search/menu handlers и CSS удаляются. Два реальных consumers; одинаковые focus/selection/empty/error/paging; открытие не меняет контекст чата |
| S3b.2 — самостоятельные object views | Exact target и local presentation state на вкладку; текущие Markdown/JSON/text/PDF/image/task/sequence renderers. Несколько разных материалов одновременно; source/revision видимы | Старый singleton catalog preview переключён и удалён. Bounded lazy reads, cancel/stale reply, leases; смена активного чата не подменяет открытые объекты. Нельзя клонировать global IDs/active-selection controllers |
| S3b.3 — общий Browser | HTML из материалов/чата и plugin используют общий Browser surface/lifecycle. Providers сохраняют существующий resource builder, sandbox и plugin instance authority | Удалены раздельные ChatBrowser/plugin placement и splitters; нет plugin-only renderer. Проверены iframe document identity при move/restore, независимый chat, source change, аннотации и dispose |

S3b.1 не выдаёт legacy singleton редактор Библиотеки за несколько независимых:
переход к нему сохраняет dirty/save guard и явно регистрируется как временный
adapter до предметного editor-среза S4. Общая навигация не переносит globals
редактора в общий компонент. Просмотр файлов подключается будущим adapter лишь
после определения реального owner/root и typed paged contract; отдельный файловый
store, произвольный path read и фиктивный Files экран сейчас не создаются.

Подключение остальных панелей к той же основе (уточнение пользователя):

| Панель | Общий слой | Срез подключения / что остаётся предметным |
|---|---|---|
| Материалы, Библиотека | NavigationPane + NavigationTree, поиск/состояния/команды | S3b.1, первые два production consumers |
| VBA | Тот же NavigationPane, дерево проекта/модулей | S4; typed Office reads, locked/unavailable, выбор процедуры и команды записи/запуска у VBA owner |
| Документы/чаты, проекты/чаты | Тот же каркас и renderer с metadata adapters | S2/S4 после текущего chat scoping; pagination, run/rename/delete guards не переносятся в общий UI |
| Проект плагина | Общие navigation elements и Browser lifecycle | S3b.3/S4; предметные разделы plugin и SDK остаются у plugin owner |
| Будущие Файлы | Тот же каркас, scoped paged adapter | После typed root/read contract; нового tree widget не требуется |
| Settings categories, оглавление запроса, встроенные деревья Inspector | Общие Tree/search/menu primitives | Внутренние навигационные элементы, не обязательная копия полной панели источника |

VBA не получает отдельный navigation framework. Первый общий компонент не должен
зависеть от названий Материалы/Библиотека; его контракт проверяется также на форме
данных дерева модулей без запуска Office. Перенос всех consumers не объявляется
завершённым после первых двух.

До первого production-среза: конкретный макет на существующих components и 3 маршрута
из Screen Map; baseline на тех же objects/размерах. Для каждого среза — счётчики
metadata/body reads, DOM/retained view bounds, open/close/move/restore, narrow/theme/
zoom, keyboard и late response. Общий компонент подтверждается двумя consumers,
а не названием файла. Число тяжёлых views и лимиты байтов задаются явно до gate.
Итоговый gate — два разных объекта открыты одновременно, смена каталога/чата
сохраняет их состояние, ни один read не меняет domain selection, старые consumers
удалены или имеют точный owner/removal gate в MIGRATION_MAP.

Общие типы и возможности проектируются **до** частных экранов. Первая реализация
сразу подключает реальные разные consumers; последующий экран добавляет свой
adapter/descriptor и предметный контент. Новые `if (source === ...)` в Shell,
второй registry размещения, новый store или копия renderer означают, что граница
ещё не выделена правильно. Исключения допустимы только как названный временный
adapter с точным условием удаления; большой рефакторинг без ближайшего consumer
не становится самостоятельным этапом.

Iframe probe выявил отдельный риск: vendor `renderer:always` сохраняет document
при move, но не при `fromJSON`. Постоянный frame host — кандидат общего Browser,
а не уже утверждённый отдельный overlay плагина. Его реализация должна брать
placement только из Dockview и пройти реальные операции restore/close/reopen.

## S3a: запрос и общие controls диагностики — production 2026-11-02

После проектирования пользователь поручил «Переключи на продакшен и коммит пуш».
[Карта/макет](UI_SCREEN_MAP.md#структура-запроса--production-2026-11-02),
[владельцы](UI_ARCHITECTURE.md#request-view-владельцы--2026-11-02).
Это выбранный срез S3; остальная очередь не объявляется завершённой.

Включено: request singleton в существующем DockWorkspace; Состав/Ответ/Изменения;
оглавление recorded composition/wire, 50 строк с заменой страницы; один выбранный
Markdown/JSON/Текст reader, части content, источники/полнота, точные сообщение/запрос.
Обзор читает metadata, тела — по действию. Existing typed DTO/compiler/transport/CAS
сохранены. Active + 2 retained views, ограниченные presentation snapshots, отмена,
leases и stale guards принадлежат прежнему reader. После restart вид открывается
с приглашением выбрать запрос; scope не восстанавливается из active chat.

Удалено: прежнее размещение запроса в Diagnostics, семь tabs, grouped/wire ленты
раскрытий, старые CSS/handlers; локальные control/menu rules request/trajectory/log,
самостоятельные меню JSON nodes/history. Общий ActionMenu обслуживает эти consumers;
Lab и Приложение получили общие controls, их сценарии и данные не менялись.

Проверено: model-context, diagnostics-navigation, model-context-layout, JSON/text
viewer и artifact consumers, run-journal, chat-history-layout,
model-diagnostics-layout, dock-workspace, shell-navigation.
Основные маршруты: rejected tool_calls → retry → comparison → exact message,
source → resource → message, request → run → request; no eager model checks,
metadata-only overview, late response/cancel, partial/empty/error, pinned owner,
move/split/restore без потери DOM. Light/dark, 320/390/420/1440, narrow list/detail,
whole-row tree/keyboard, Escape/focus и portalled menus проверены в Chrome.

Расширенный `workspace-inspector` адаптирован к новому reader, но не проходит
целиком: старый переключатель панели перекрывается project splitter/overlay.
Это открытая placement-граница S3b; полный preview pointer route и сохранность
iframe после него не квалифицированы. Exact workspace scope/leases покрыты
model-context. Исправлено обновление видимости preview после закрытия Inspector;
новый отдельный preview view в этом срезе не создаётся.

Production на synthetic bridge: [Markdown](review/request-production-markdown.png),
[JSON-строка](review/request-production-json.png),
[меню в узкой тёмной панели](review/request-production-menu-narrow.png),
[масштаб 150%](review/request-production-zoom.png).

Измерения — одинаковый fixture 2000 сообщений / 4 473 924 байта, Chrome 156,
HEAD до среза `a341f7f8`, 5 повторов и отдельно 20 open/read/close. Время включает
browser action/wait; это не latency модели. Точные отчёты и production screenshots
сохранены в `review/`. Windows/WebView2/process memory отдельно не квалифицированы.


| Проверка | До | После |
|---|---:|---:|
| Открытие metadata, медиана | 12 ms | 15 ms |
| Открытие + чтение первого сообщения | 111 ms | 75 ms |
| 3 перехода сообщение → исходник → сообщение | 1817 ms | 2713 ms |
| Metadata / body reads на обзоре | 1 / 0 | 1 / 0 |
| Body reads за все переключения | 1 | 1 |
| Смонтированные строки сообщений | 50 | 50 |
| DOM оглавления/reader | 504 | 175 |
| JS heap после закрытия/GC | 7823856 | 8051276 |
| JS heap после 1-го → 20-го дополнительных циклов | 7854164 → 7997088 | 8087836 → 8383424 |

[Точные before](review/request-production-before.json) / [after](review/request-production-after.json).
Первое сообщение стало доступно с меньшим DOM. Общего ускорения не заявляем:
открытие добавляет Dock placement, raw round-trip теперь включает открытие меню
вместо отдельного таба. После 20 циклов heap изменился на ~143 KB до / ~296 KB после;
это общий V8 heap с layout/JIT, не доказательство отсутствия утечек и не process RSS.
Следующая оптимизация маршрута: сравнить одинаковое число действий и отдельно
измерить parse/render; рассмотреть прямую команду исходника рядом с форматом.
Эти числа измеряют пользовательский маршрут с ожиданиями Playwright, а не CPU.
Кэш тела уже исключает повторное чтение; дальнейший выигрыш в нём не заявляется.
Тела не перечитываются; полный body одного запроса и максимум 2 неактивных view
ограничены прежними лимитами. Долгий Windows memory/latency профиль остаётся открытым.

Не закрывает: полный backend event scan/отмену синхронного preview, полную
attribution D3, scenario editor/matrix D4, несколько живых request views,
общий поиск/role filter списка сообщений, Windows/Office/real endpoints.
Старый qualification plan не закрыт UI-срезом. Откат — revert этого UI-среза,
без изменения данных; параллельный reader/feature flag не оставлен.

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
