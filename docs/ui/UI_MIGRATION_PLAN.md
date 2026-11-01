# План модернизации production UI

Обновлено: 2026-11-02. Owner: Web presentation; согласование карты/scope — пользователь.
Стратегия и продолжение общего chat-среза разрешены пользователем. S1a реализован
и сохранён в fd474f91; S1b реализован и проходит визуальное ревью по эталону Demo.
Canonical план последовательности, проверки, baseline, rollback и удаления.
Границы — [Architecture](UI_ARCHITECTURE.md); маршруты — [Screen Map](UI_SCREEN_MAP.md).

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
   Библиотека, материалы, VBA, plugins, settings, diagnostics пока открываются
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

По запросу пользователя перенесено поведение Demo целиком для левого дерева:
wide/overlay, постоянный rail, повторный клик раздела, выбор/раскрытие, scrim/Escape,
широкое предпочтение. Отдельная группа Inspector сворачивается без закрытия;
в смешанной/узкой группе активируется его вкладка. Исправлен реальный размер
кнопок группы: общий control CSS больше не растягивает их до 34 px.
Контракт — [Screen Map](UI_SCREEN_MAP.md#боковая-навигация--поведение-demo).

`shell-navigation.test.js` проверяет 900/420/360 px, выбор мышью/клавиатурой,
settings, сохранение draft/DOM, hide/restore Inspector и защиту единственной группы.
Существующие dock-workspace, chat-inspector и workspace-chat-layout проходят.
Старый settings-navigation-layout останавливается на устаревшем количестве разделов
(7 против текущих 6); gap уточнён в [BACKLOG](../stabilization/BACKLOG.md#settings-navigation-fixture-версия-в-hover--2026-10-30).
Текущий выбор страницы настроек и закрытие её дерева покрыты новым Shell-тестом.
Снимки: [900 px](review/s1b-navigation-900.png), [360 px](review/s1b-navigation-360.png).

Следующий предлагаемый подсрез **S2a** — единые model/context/attachment capabilities
и controls Office/Workspace/plugin. View-scoping, несколько живых чатов и paging
следуют отдельными ограниченными изменениями, когда для них готовы владельцы.
Сначала визуальное ревью уточнённого S1b; параллельный большой UI Kit/refactor не нужен.

## Последующие срезы — только после ревью предыдущего

| Срез | Содержание | Что удаляется после проверки |
|---|---|---|
| S1a / S1b | Общие chat rules и production Chat workspace + Inspector реализованы; визуальное ревью S1b по эталону Demo | Дубли composer presentation и старый Inspector grid/split/open-width удалены. Остальные splitters пока нужны |
| S2 | View-scoped chat presentation, полная parity model/context/attachment capabilities, chat catalog paging при необходимом typed contract | Заменённые global renderer/current-selection paths; без второго durable store |
| S3 | Browser/material viewers + diagnostics views, lazy detail, deep links | Старые раздельные placement/toggle paths, redundant viewer mounts |
| S4 | Library/VBA/settings/project navigation; shared reusable UI для app/plugin | Только подтверждённые duplicate controls/styles, legacy route aliases после consumer switch |
| S5 | Оставшиеся plugin views, portability fixture, общий registry/команды | Migration adapters, obsolete CSS и Demo runtime-копии; reference screenshots можно сохранить |

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
