# Архитектура UI RNAssistant

Обновлено: 2026-11-02. Owner: Web presentation; host/runtime contracts — их текущие
domain owners. Стратегия и продолжение общего chat-среза разрешены пользователем;
детали визуальных решений остаются на ревью.
Это canonical document владения, зависимостей и lifecycle UI.

Основание: запрос «UI Strategy Transition» и сохранённый без изменений
[исходный документ](reference/RNAssistant_UI_Architecture_and_Agent_Rules.md), v1.0 от 2026-11-01.
Документ-источник задаёт цель, а не доказательство наличия архитектуры.
Исходный аудит ниже относится к baseline **4ea3d6e4**; изменения S1a отмечены явно.

## Единственные владельцы документации

| Документ | Ответственность |
|---|---|
| Этот документ | Текущая архитектура, результаты аудита, целевые границы и lifecycle |
| [Screen Map](UI_SCREEN_MAP.md) | Экраны, подэкраны, маршруты, размещение, состояния и контекст |
| [Design System](UI_DESIGN_SYSTEM.md) | Tokens, компоненты, повторное использование и статус визуальных решений |
| [Migration Plan](UI_MIGRATION_PLAN.md) | Срезы, baseline, проверки, откат и удаление временных путей |
| [Decisions](UI_DECISIONS.md) | История согласований, без повторения нормативных правил |

[UI_LAYOUT_UX](UI_LAYOUT_UX.md), [старый UI design](../ui-design.md) и Prototype
Package сохраняются как история/evidence. Их старые планы, запрет production и
порядок P0–P4 больше не управляют работой. Точные runtime, resource, plugin и
storage contracts остаются в domain docs из [README](../README.md).
Новые документы про SDK, компоненты или performance отдельно пока не нужны.

## S1b: фактическая граница переноса

Owner: Web presentation. Один [DockWorkspace](../../web/js/app-dock-workspace.js)
размещает существующие `chatDropZone` / `workspaceChatView` как один `conversation`
и существующий `chatInspector`. Одни messages/chatForm/input, один активный owner.
Прототипный iframe не является production dependency. Shell reference перенесён
в [Design System](UI_DESIGN_SYSTEM.md#эталон-оболочки-завершённый-dockview-demo),
команды — в [Screen Map](UI_SCREEN_MAP.md#команды-оболочки-s1b).

Dockview — единственный владелец групп, вкладок, split, maximize и serialization.
Старые Inspector grid/overlay/splitter, width/open preference и перенос двух
кнопок между chat/preview headers удалены. Навигационный splitter и ещё не
перенесённые HTML/plugin preview splitters остаются до их среза.
`switchTab` размещает остальные sections той же Shell, временная граница описана
в [Migration Map](../stabilization/MIGRATION_MAP.md).

Геометрия Shell имеет общий источник в production tokens; ShellLayout читает их
при инициализации. Размер navigation устанавливается до создания Dockview, чтобы
начальная ширина Inspector не менялась вслед за поздним применением defaults.
NavigationTree остаётся одним renderer для Office/Workspace/plugin и других
деревьев; feature owners передают данные/действия, не свои размеры и состояния строк.
Владение CSS и границы version-pinned vendor adapter — в
[Design System](UI_DESIGN_SYSTEM.md#применение-правил-сейчас).

Три bounded singleton views (conversation, inspector, settings) сохраняют свои DOM nodes: renderer `always` выбран только для
них, не как глобальная политика. Закрытие паркует узел, а не уничтожает controller,
черновик или run. Listener компонента Dockview удаляется на dispose. На скрытом
conversation transcript paint откладывается; возвращение применяет актуальное
состояние и восстанавливает scroll/follow position того же exact owner/chat.
Фоновый mount не открывает скрытый чат. Inspector отменяет lazy usage request
при deactivation; resize не вызывает повторный render или bridge read.
Chooser — обычный дешёвый view без retained domain data. Полный view-scoping
глобального chat renderer остаётся S2; дубли экземпляров запрещены.

Навигация также перенесена по поведению Demo: один ShellLayout управляет wide/overlay,
rail repeat, scrim/Escape, выбором экрана и широким предпочтением. Старые проверки
820/620 px и fullscreen-дерево удалены; feature content breakpoints остаются у экранов.
Общий Tree сообщает о принятом выборе, не знает Shell и не держит второй state.
Dockview скрывает самостоятельную группу Inspector без dispose/повторного mount;
смешанная и единственная видимая группы защищены от случайного скрытия.

Опора lifecycle: [Dockview rendering API](https://dockview.dev/docs/core/panels/rendering/).
Локальная pinned версия 8.8.3, лицензия/provenance и hashes включены в существующие
vendor manifests. Demo использует тот же файл; второго loader нет. Host проектов
уже включает `web/**`, C# project/bridge/runtime/storage не менялись.

## Settings view S1c

Пользователь согласовал этот ограниченный срез после checkpoint Shell `2a4831c5`.
`settings` добавлен в существующий каталог DockWorkspace. Он размещает тот же
`#settingsView`, не копию формы. Прежние `#tab-settings`, navigation splitter и
root section удалены. Совместимый вызов `switchTab("settings")` только направляет
команду в workspace; собственного маршрута размещения больше нет.

`app-settings.js` владеет формой, dirty/revision/save и hooks `activate`,
`deactivate`, `canClose`. Переключение/перемещение не сбрасывает правки; фоновый
render также не перезаписывает dirty форму. Deactivation сохраняет поля, но
отменяет catalog preview прежним механизмом generation/cancellation. Сохранение
использует прежние contracts и подтверждённую revision; UI не считает ранний click
успехом. При согласии закрыть dirty вкладку её форма паркуется до перезапуска
приложения, без нового persistence/store. Save-in-flight не закрывается.

Все пользовательские close paths (крестик, Delete/Backspace, меню, группа,
замена раскладки) проверяют view guard до изменения группы. Движение/resize
не является закрытием. Pinned vendor DefaultTab сохраняет собственный renderer;
локальный adapter маршрутизирует close action. Browser reload восстанавливает
placement, но не обещает сохранение неприменённых настроек на диск.

Hover/focus остаётся у `RailPopovers`, click menus — у `ActionMenu`; поверхность,
строки и иконки принадлежат общему `app-product-components.css`. Старые отдельные
rail menu CSS удалены. Нового popup engine, registry, библиотеки или vendor нет.

## Фактический аудит

### Повторная сверка с исходным планом — 2026-11-02

Перечитаны исходная спецификация, четыре canonical UI-docs, решения P1/P2,
реальные CSS/JS/HTML и замороженный Demo. В текущем `web/index.html` один Shell,
шесть root sections, 118 script и 44 stylesheet подключения. Dockview один;
его каталог пока содержит conversation/inspector/settings, а не все целевые виды.

| Responsibility | Текущий единственный owner | Результат сверки / открытая граница |
|---|---|---|
| Shell chrome, navigation, topbar/footer DOM | `app-layout.js` / ShellLayout; `app-product-navigation.css` | Убраны дубли rail/topbar/footer в Dock CSS, прежний flex Shell и старые navigation media rules. Шестерёнка находится снизу rail; настройки — singleton Dockview view S1c |
| Dock placement и presentation lifecycle | `app-dock-workspace.js` / Dockview | Передаёт Shell вычисленный context/layout snapshot и callbacks команд; не владеет DOM topbar/footer. Domain hooks conversation/Inspector пока адаптированы здесь для трёх singleton views |
| Menus | `app-action-menu.js` / ActionMenu | Один компонент для существующих меню и портального варианта в clipped Dock headers; прежний Dock menu renderer/keyboard path удалён |
| Tokens и навигационные элементы | `app-product-tokens.css`, `app-product-components.css` | Общие размеры/типографика/темы. Заголовки sidebar, дерево и контроли проверены в нескольких sections. CSS содержимого settings возвращён его screen owner |
| View catalog / command routes | `views` в DockWorkspace; ещё не мигрировавшие sections — `switchTab` | Единого расширенного публичного panel/command API для plugins пока нет. Расширять существующий каталог при переносе следующего view; не откладывать единство до финальной чистки |
| Chat / runtime data | Прежние ConversationState, WorkspaceChat, Office domain owners | Общие bounds/composer сохранены. Multiple live views, model/context/attachment parity и paged catalogs ещё открыты |
| StatusBar | Shell отображает переданные mode/connection/layout facts | Общая агрегация фоновых operations пока не подключена; новых guessed progress/events/stores не вводилось |
| Portability | Общие Tree/Toolbar/ActionMenu/OperationView в browser fixture | Проверены без Shell/bridge, включая dispose и обе темы. Это ещё не экспортированный plugin, полная `file://` или WebView2 qualification |

Архитектура соответствует направлению исходного плана, но **не объявляется полностью
реализованной**. До повторной сверки оставались реальные расхождения Shell CSS,
chooser и menu ownership; они исправлены в этом срезе. Временные root sections и
native previews остаются зарегистрированными migration boundaries, не вторым
продуктом. Сопоставление A–H / S1–S5 и gates — в
[Migration Plan](UI_MIGRATION_PLAN.md#сверка-с-исходным-планом).

### Исходный аудит 4ea3d6e4 и уже внесённые изменения

| Область | Что существует | Следствие для переноса |
|---|---|---|
| Production entry | Один [web/index.html](../../web/index.html), 7 корневых tab-секций; 116 внешних script-тегов и 43 stylesheet-тега | Это подсчёт подключений, не замер загрузки и не причина переписывать приложение |
| Host | [AssistantPaneControl](../../src/RNAssistant.Office/WebView/AssistantPaneControl.cs) + [AssistantWebBridge](../../src/RNAssistant.Office/WebView/AssistantWebBridge.cs); Desktop и Office shells используют этот UI | Не создавать Office UI и Desktop/Workspace UI отдельно; host boundaries сохраняются |
| Shell/navigation | [switchTab](../../web/js/app.js), [ShellLayout/splitters](../../web/js/app-layout.js), [PanelToggle](../../web/js/app-panel-toggle.js), rail/popovers | S1b: app-dock-workspace владеет conversation/inspector placement; route switch ещё сохраняет side effects остальных sections |
| Office/Workspace/plugin chat | Один messages, chatForm и chatInput; [WorkspaceChat.mount](../../web/js/app-workspace-chat.js) перемещает существующие узлы между owners office/project/plugin | Уже есть общий UI. Клонирование DOM создаст конфликт глобальных IDs и состояния |
| Data owners | Office: [chat-session](../../web/js/app-chat-session.js), [chat-state](../../web/js/app-chat-state.js), [inbox](../../web/js/app-chat-inbox.js). Workspace: [project-chats](../../web/js/app-project-chats.js), Application [WorkspaceConversationService](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | Различия target/receipts допустимы; не заменять их универсальным UI-store |
| Общие conversation rules | [ConversationState](../../web/js/app-conversation-state.js), [ConversationInput](../../web/js/app-conversation-input.js), [ConversationObserver](../../web/js/app-conversation-observer.js), [RunViewState](../../web/js/app-run-view-state.js) | Повторно использовать identity, revision, receipt и stale-response guards |
| Inspector/browser | Один [ChatInspector](../../web/js/app-chat-inspector.js) с owner switching; отдельный [ChatBrowser](../../web/js/app-chat-browser.js) для HTML preview | Inspector уже размещает Dockview; у browser остаётся прежний preview placement до S3 |
| Viewers | [ViewerRegistry](../../web/js/app-viewer-registry.js), text/Markdown/JSON/image/PDF/task-list, [ViewerUI](../../web/js/app-viewer-ui.js) | Это registry форматов содержимого, не второй workspace/panel registry |
| Plugins/vendor | [RNPlugin SDK](../../web/plugin-host/sdk.js), [plugins](../../web/js/app-plugins.js), [vendor packages](../../web/vendor-packages.json), существующие generators | Есть Ready handshake и resource/command APIs; общего публичного UI/panel SDK пока нет |
| Data Templates | Отдельное переносимое приложение, [shell](../../web/data-templates/ui/shell.js), [browser](../../web/data-templates/ui/browser.js), [plugin adapter](../../web/data-templates/ui/plugin-adapter.js) | Его предметные вкладки и standalone entry не являются второй RNAssistant Shell |
| Demo | Dockview 8.8.3, P1/P2 оболочка; P3 iframe готового mock preview | UX evidence. Iframe на каждый чат не переносится в production |

Браузерный spot-check существующего product preview подтвердил смену
chatForm.data-owner project → office при единственном chatForm и messages.
Это проверка композиции на mock; реальный Office и Windows не запускались.

## Дубли и разрывы — с границами причинности

| ID | Наблюдение / доказательство | Решение |
|---|---|---|
| UI-01 | Demo [tokens.css](../../prototypes/ui/tokens.css) и production [app-product-tokens.css](../../web/css/app-product-tokens.css) имеют разные значения и имена | S1b переносит reference-палитру и shell-токены в app-product-tokens; production не зависит от Demo tokens |
| UI-02 | .composer/.messages оформляются в app-composer/app-chat и переопределяются в app-product-screens/app-responsive; старые aliases остаются в app.css/app-product-tokens | Каскад не равен двум приложениям, но ответственность размыта. Консолидировать только затронутые компоненты, удаляя заменённые selectors |
| UI-03 | Размещение сейчас распределено между app-layout, ChatInspector, ChatBrowser, plugin preview и switchTab | Workspace/Dockview получает размещение; виды сохраняют контент/lifecycle. Навигация вызывает один маршрут команд |
| UI-04 | Global state.activeChatId, один activeOwner, глобальные DOM IDs и renderer cache | Первый срез — один живой conversation view. Несколько одновременных чатов требуют отдельного среза scoping; копии UI запрещены |
| UI-05 | В baseline Office ограничивал prepend 240 сообщениями, Workspace накапливал страницы без лимита | Исправлено в S1a: один bounded window в ConversationState для Office/Workspace/plugin; возврат к последним и сохранение позиции. Evidence — Migration Plan |
| UI-06 | Office [GetChatSummaries](../../src/RNAssistant.Office/Services/ChatSessionService.cs) читает ListHeaders; Workspace [refreshCatalog](../../web/js/app-project-chats.js) запрашивает getProjectChats целиком | Metadata-only не означает paged catalog. Не объявлять целевую пагинацию списка уже реализованной |
| UI-07 | Разные generation/serial counters и active snapshots в Office/Workspace; общий observer уже есть | Убирать только доказанное дублирование presentation, сохраняя domain-specific binding/receipt semantics |
| UI-08 | Data Templates [browser.css](../../web/data-templates/styles/browser.css) имеет собственные standalone defaults, host передаёт appearance | Общие tokens/API нужны для реальных двух consumers; не ломать standalone режим и не строить SDK заранее |
| UI-09 | P3 iframe может сбросить mock-переписку при пересоздании; две ошибки MutationObserver в browser log не локализованы | Ограничение Demo, не доказанный production-дефект и не основание править runtime |
| UI-10 | Один DOM composer имел разных владельцев labels/edit/reply/stop/reasoning; модель и вложения всё ещё подключены по-разному | S1a объединяет presentation и bounds, но не закрывает полную parity. S1b объединяет размещение; следующий срез — единые model/context/attachment capabilities. Domain identity/receipts сохраняются |

NavigationTree + TreeAdapter — компонент и resource adapter, не два дерева.
Office и Workspace — разные domain owners общего UI, не обязательно дубли.
Внутренние EditorTabs, Diagnostics tabs и Data Templates tabs не заменяют Dockview:
они выбирают режим содержимого, а Dockview размещает независимые виды.
Статусы UI-05/UI-06/UI-09/UI-10 — в [BACKLOG](../stabilization/BACKLOG.md).

## Целевые границы

~~~mermaid
flowchart TD
  Shell["Одна Shell: topbar, rail, navigation, status"] --> Workspace["Один Workspace / Dockview: placement"]
  Workspace --> Views["Одна карта / registry видов"]
  Views --> Kit["Общие tokens и UI-компоненты"]
  Views --> Domain["Существующие chat / resource / diagnostics adapters"]
  Domain --> Host["Существующие typed bridge / plugin SDK / host"]
  Host --> Runtime["Domain services, storage, AgentKernel"]
~~~

- Shell отвечает за chrome, команды и навигацию; Workspace — только за группы,
  вкладки и раскладку. View владеет presentation state, domain — данными,
  runtime — execution/model context, host — privileged API.
- Одна registry описывает UI identity/type/factory/default placement/capabilities/
  persistence key/disposal policy. Это не service locator и не runtime authority.
  ViewerRegistry остаётся специализированным registry форматов.
- Один dispatch маршрута команд для topbar/rail/menu/shortcut; существующие
  handlers сохраняются. Новый universal command bus не требуется.
- Shared UI не импортирует Shell, Office или private bridge. Host services
  передаются предметным views/adapters. Plugins получают только публичный API;
  доверенная проверка capabilities/permissions остаётся у host.
- Layout persistence, presentation state и durable runtime state раздельны.
  В layout нет сообщений, файловых тел, токенов authority и несохранённых текстов.
- Shared components выделяются при реальном повторном использовании.
  Vendor/theme/UI-kit/SDK — границы ответственности, не задание создать пакеты.

## Lifecycle и async

У каждого тяжёлого вида: mount, activate, deactivate, dispose и явная политика
keep-alive/suspend/dispose. Dock-перемещение не запускает init или запрос заново.
Закрытие UI не означает отмену run; отмена выполняется отдельной командой владельца.
На deactivate приостанавливать только ненужную визуальную работу; на dispose
освобождать listeners, observers, timers, leases/object URLs и scoped requests.
Нельзя глобально отменять shared run или запрос другой сессии.

Чат сохраняет draft/selection/scroll anchor и минимальное окно данных.
Смена target увеличивает generation; поздние snapshots сверяются по owner,
session, run, revision и navigation generation. Подтверждение отправки и очистка
draft остаются привязаны к receipt, не к успешному завершению Promise.
Нельзя превращать lost connection/unknown effect в completed или автоматический retry.

Прогресс строится по текущим operation/run events. Queued/running/waiting/
cancel requested/cancelled/failed/unknown различаются, где это поддерживает
контракт. Неизвестный total — indeterminate. UI не вычисляет kernel outcome.
Существующие observer/push/fallback polling сохраняются; новый глобальный polling
или progress/task store без доказанной необходимости не вводится.

## Производительность, portability и поддержка

Сохранить bounded message windows, incremental rendering, lazy payload/media,
ограниченные caches, stale guards и read batching; перечень evidence и замеров —
в [Migration Plan](UI_MIGRATION_PLAN.md). DOM virtualization не доказывает bounded
data; закрытие вкладки не доказывает освобождение памяти.
Не делать eager preload всех чатов, parsing всего журнала или rerender ленты
на каждый token. Порог регрессии выводится из baseline, не придумывается.

Dependencies локальные, с версиями/лицензиями/хэшами. Dockview 8.8.3 уже проверен
в Demo только для документированных там операций; popout/floating/auto-hide и
реальная WebView2-совместимость не объявлены проверенными.
L1 — host-independent component; L2 — browser app с явными browser requirements;
L3 — host capabilities. File API/worker/module/storage/file:// поддержка
проверяется отдельно. Глобальный объект не заменяет handshake.
При изменении shared UI нужен standalone пример с Tree/Toolbar/Progress/theme,
на существующих assets; ещё одна инфраструктура сборки/loader не создаётся.

Продолжение общего chat-среза разрешено; следующие срезы сохраняют визуальные gates. Временный dual path
допустим только с owner, флагом при необходимости и условием удаления в
[Migration Map](../stabilization/MIGRATION_MAP.md). После проверки старый путь
удаляется вместе с consumers и мёртвыми зависимостями.
