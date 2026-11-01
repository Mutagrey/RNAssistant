# План модернизации production UI

Обновлено: 2026-11-02. Owner: Web presentation; согласование карты/scope — пользователь.
Стратегия и продолжение общего chat-среза разрешены пользователем. S1a реализован
и передан на визуальное ревью; S1b — перенос общего view в Dockview.
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
| Cold startup / chat open / switch | RNAssistantPerformanceDiagnostics, existing client timings | Instrumentation есть; актуальный before S1 ещё не снят |
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

Минимальные предполагаемые файлы: web/index.html, app.js, app-layout.js,
app-workspace-chat.js, app-chat-inspector.js; один тематический app-workspace
Dockview adapter; затронутые production tokens/navigation/screens CSS и vendor
manifest/package entry. Точный diff уточняется после утверждения карты.
Новые C# contracts, bridge/storage/model changes не нужны для размещения views;
если аудит реализации выявит необходимость — scope пересогласовывается.

Критерии S1: фактическая отправка/stream/stop/confirmation/receipt semantics прежние;
draft не теряется при owner switch, move, hide/reopen; старый ответ не попадает в
новый chat; resize/maximize не вызывает init/повторных reads; performance не хуже
измеренного baseline сверх согласованного разброса; узкий режим/keyboard/zoom
проверены. Общий bounded window уже введён в S1a; его проверки остаются
обязательными при Dockview и последующем расширении на multiple chat views.

## Последующие срезы — только после ревью предыдущего

| Срез | Содержание | Что удаляется после проверки |
|---|---|---|
| S1a / S1b | Общие chat rules (S1a на ревью), затем Chat workspace + Inspector placement, Office и Workspace вместе | Дубли composer presentation удалены; далее старый grid/split/open-width path этих областей. Остальные splitters пока нужны |
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

**Текущее ревью:** поведение общего чата S1a. Следом — перенос этого же view и
Inspector в Dockview по S1b, без одновременных экземпляров чата и второго UI.
