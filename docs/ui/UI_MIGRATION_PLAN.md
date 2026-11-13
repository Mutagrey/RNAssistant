# План модернизации production UI

## F4b Provider cutover — In Review, 2026-11-13

Completed from F4a: common document/raw/editor/plugin routes, exact page actions,
three-provider source comparison and old view/bridge removal. Run html-editor-views,
html-project-import, browser-source-diff, browser-saved-views and plugin-browser.
Review: open an HTML material, compare a checkpoint, return to the source editor;
open raw HTML and create a project; switch two plugin rail entries at narrow width.
Historical browser-views/plugin-view-selection/plugin-source-failure iframe fixtures
are retired; current tests assert source-only behavior. Remaining Workspace transfer,
saved completion, local agent open/ack and 07 follow; B02/Windows stays open.

## F4a StartSurface — In Review, 2026-11-13

Review: «+» → Open chat (existing tree search), then «+» → Browser → Home → local
page → Home. Check recents, search and clear at wide / 420 px / 125%; move an existing
singleton through a split chooser and restore layout. StartSurface, Dock placement,
Shell/Demo geometry, Settings guards, scoped recents/storage failure and stale search
checks pass. Old flat chooser, all-chat DOM enumeration and Panels/maximize DOM
entrypoints are removed. Provider actions/route cutover and 07 continue; native B02,
Windows evidence and visual user acceptance remain open.

## F3d Document HTML — In Review, 2026-11-13

Document live Browser and saved HTML use the same checkpoint controls. Review:
open the current project → edit a file → Save version → reopen it from clock →
try deleting the current checkpoint. Failed editor Save must leave the version
catalog untouched. Existing automatic history remains in undo/explicit comparison.
Dirty-owner and import lifecycle fixtures pass; [narrow review](review/document-checkpoints-narrow.png).
F4 still replaces remaining provider routes and adds StartSurface/navigation actions.

## F3c Workspace HTML — In Review, 2026-11-13

Common Browser checkpoint controls now include directory HTML projects. Review:
open an entry-only project → clock → «Состав проекта» → enter declared files → Save
→ reopen saved version. Repeat at 420 px/125%; Save stays visible and plugin Apply
is absent. Backend lifecycle and browser form checks pass. Document HTML adapter,
StartSurface/recents and screen reduction remain next; B02/Windows remain open.

## F3b Plugin Browser — In Review, 2026-11-13

Plugin source/saved consumers переключены на common Browser/checkpoint controls.
Для ревью: открыть плагин, Save, clock → сохранённая запись, удалить неактивную запись,
повторить при 420 px/125%. `browser-saved-views` проверяет эти переходы и Unknown;
`dock-layout-pruning` — сохранность соседних tabs после удаления прежнего route.
Визуальное принятие пользователем и native evidence открыты. Document HTML editor,
Workspace checkpoints и StartSurface продолжаются следующими сквозными этапами.


## F1a Local Browser — In Review, 2026-11-13

Общий local source Browser подключён к production typed bridge. Старый external
pilot/native singleton удалён; active preview принудительно закрыт B02.
`local-browser.test.js`: owner/attach, source paging, stale completion, отказ URL,
hide/show/close и narrow/125% проходят; [wide](review/local-browser-wide.png),
[narrow](review/local-browser-narrow.png). Для ревью: «+» → Browser, ввести local
адрес, выбрать файл, скрыть/вернуть вкладку, попробовать внешний URL.
Пользовательское визуальное принятие и native Windows qualification открыты.
Provider version routes/StartSurface ещё в очереди F3/F4; это не завершение всех views.


## Local Browser package — ранний 02, In Review, 2026-11-13

Удалены HTML proxy/network settings и developer site-policy flow; LLM connection
сохранён. `local-browser-settings.test.js` проходит, wide/narrow/125% просмотрены:
[wide](review/local-browser-settings-wide.png), [narrow](review/local-browser-settings-narrow.png).
Runtime network qualification B02 и визуальное принятие открыты. Далее F0/F1,
не автоматический S2. [Точный прогресс](../browser/06_PROGRESS.md).
Порядок external P6h/deployment ниже superseded новым local-only scope.


## Developer: подписанная конфигурация — In Review, 2026-11-12

По уточнению пользователя: обычный status, Developer password unlock, sites/manual-any,
путь к private .bin с build-time default, sign/save/open-directory/close; exact policy-version gate.
Генерация issuer — общая C# реализация для Mac и Windows GUI, без сертификатов/скриптов.
Scope/owner — [контракт](../browser/03_CONTRACTS.md#signed-access).
11 Harness и production Settings Chrome fixture проходят, wide/narrow/125% просмотрены;
Mac utility generation/rotation проверены. Windows GUI/native/Office evidence и
визуальное принятие открыты; [5 действий ревью](../browser/05_TEST_MATRIX.md#signed-access).
Следующий Browser этап P6h; diff/P4b по-прежнему отложены. Общий S2 не запускается.


## Browser P4a — baseline / In Review, дальнейшая доработка отложена

2026-11-11: общий режим сравнения saved HTML/plugin release, owner-validated immutable
pair, paged history/file selection и existing TextDiff. Preview/input сохранены,
late responses и bounds проверены: 3 Harness cases + 2 Chrome fixtures;
[визуальное evidence](../browser/05_TEST_MATRIX.md#p4a). Windows/acceptance открыты.
Пользователь снял diff и P4b с ближайшего приоритета; далее P6 browser automation.

## Рабочая область: единая навигация — In Review, 2026-11-11

Scope: запрос пользователя по двум фото и уточнение полного меню под «+» рядом
с поиском. Owner — Web navigation/общие NavigationTree, ActionMenu, Icons.
Одна панель/поиск/прокрутка для Office и проектов, общее добавление и строковые меню;
сняты bold, overlay действий, ручной refresh и дубли layout/launcher.
Baseline: исходные фото и успешный `agent-groups`; прежняя строка 32 px / weight 500.
Новая строка остаётся 32 px (сверена с Demo при 1280×900), weight 400.
[Дерево](review/unified-workspace-tree.png), [полное меню «+»](review/unified-workspace-add.png),
[узкий dark/125%](review/unified-workspace-narrow.png) — synthetic preview.

Проверки: `chat-tree-layout` — production preview, обе секции, поиск, единый выбор,
retained DOM/views, split/close/reopen/restore, точные create commands, темы и
420 px / 125%; `agent-groups` — memberships, keyboard/dialogs, stale generations и
cleanup; `shared-navigation-tree` — общий компонент и остальные consumers;
`chat-navigation-lifetime` — поздние reads/selection, закрытие и смена Dockview focus;
`office-host-chat` и `office-host-chat-layout` — очереди, progress/error, явный retry.
Build/VSTO/Office validation не запускаются. Windows/WebView2 и визуальное принятие
пользователем остаются открыты. По запросу пользователя «Коммит» технически
завершённый срез фиксируется с patch bump `16.6.1-dev`; это не release/Windows
qualification и не автоматическое принятие оставшихся UI-срезов.

Ревью: открыть `/preview/web/?scenario=ready` через `python3 prototypes/serve_ui.py`:
1. Раскрыть обе секции и перейти Office → проект → Office.
2. Открыть «+»: новый чат, проект и четыре Office actions находятся в одном меню.
3. Сравнить «…» у чатов/родителей, попробовать общий поиск и Escape.
4. Сузить окно и проверить выбранную строку/меню при 125%.

Откат — только Web diff этого среза, без миграции/удаления данных; старого dual UI
не сохраняется. Общая миграция всех views и Office qualification не закрыты.

## External Browser P5a — In Review, 2026-11-11

Пользователь выбрал сразу WinForms WebView2 в целевом Dockview; отдельное окно —
последующий явный вариант при конкретном native блокере. Scope: одна opt-in external
view, общий toolbar, временный профиль и диагностика. Page отделена от placement;
WPF/Composition и отдельное окно заранее не вводятся. P3/P4 не блокируются Windows.
Host-neutral admission и synthetic Shell lifecycle проходят. Windows compilation,
HWND focus/DPI/overlays/process exit и визуальное принятие — Open.
[Запуск, 4 действия ревью и ограничения](../browser/05_TEST_MATRIX.md#p5a).
P6a реализован / In Review: bounded semantic read той же страницы через existing
Gateway, с chat/document ownership и CAS evidence. Пять Harness checks и два Chrome
fixtures проходят; Windows/controller delivery открыт. [Scope/evidence](../browser/05_TEST_MATRIX.md#p6a).

### Browser P6d: ручное управление / доступ — In Review, 2026-11-11

По «Коммит и далее давай сделаем» реализованы manual latch, exact run pause/resume
и отдельные сайты агента с масками; P6c зафиксирован `67a45b29`. Scope — existing
native page + shared Inbox, общий Settings form; external actions не расширены.
9 focused Harness checks и 3 Chrome fixtures прошли, wide/narrow/125% просмотрены.
[Запуск и 4 действия ревью](../browser/05_TEST_MATRIX.md#p6d). Визуальное принятие и
Windows qualification открыты; далее P4a, без автоматического старта общего S2.

### Browser P6c: визуализация действий — In Review, 2026-11-11

Запрошенный после P6b анимированный указатель, target highlight и runtime result
реализованы у existing native Browser/presentation. Указатель скрывается при idle
и ручном вводе. Нового execution path или Shell нет; после P6c добавлен P6d
по уточнению пользователя, затем P4a. Обычное визуальное принятие и Windows WebView2 evidence открыты.
[Scope/проверки и 4 действия ревью](../browser/05_TEST_MATRIX.md#p6c),
[внешний вид/motion](UI_DESIGN_SYSTEM.md#browser-action-visualization).
Общий S2 и миграция остальных views этим запросом не запускаются.

## Native window — In Review, 2026-11-11

По фото и прямому уточнению 2026-11-12 исправляется Windows pilot: OfficeHosts
разрешает старый `Application.Hwnd` в exact `ActiveWindow.Hwnd`, форма снова
показывается с владельцем Excel и
без отдельного taskbar preview. × и Alt+F4 вызывают полный shutdown; переход к
другой книге через подтверждение и сохранение drafts закрывает старый session и
создаёт новый. Compact/Companion остаются режимами того же WebView только в пределах
одной книги. Видимая группа оконных controls сокращена до меню, режима и ×; native
caption убирается при поддержке non-client regions, Web controls ждут ChromeReady.
Проверка на Windows после правки остаётся открытой.

По приложенному пользователем плану управления окнами реализован scoped native
window slice; общий S2/редизайн других экранов не запускается. Owners: OfficeHosts —
формы/STA/native effects; Office Services — state/preferences; Office WebView —
trusted typed transport; existing Shell/DockWorkspace — presentation/lifecycle.
Заменённый C++ Form/ownership/positioning path и его Drawing/WinForms references
удалены. Старый owner-mode config больше не читается/не создаётся publish; существующий
пользовательский файл не удаляется. Новый EXE, server, global hooks и второй WebView
не добавляются.

Проверено на macOS: три `host window:` checks (две точки возврата, system minimize,
pin/explicit park, stale/unknown focus, shutdown, profile merge/schema, typed bridge
без token/unsupported command и во время exclusive switch). `host-window.test.js`
использует production Shell/Dockview: same chat/composer/edit, retained Browser iframe,
Full placement при обоих порядках native state/resize, hidden view lifecycle,
удаление identity, initial Compact/bootstrap, stale revisions, keyboard/narrow/zoom,
отказ Shutdown при незавершённом edit или ошибке записи draft.
Header/control измерены рядом с frozen Demo при одинаковом viewport. Browser status
не означает native qualification. Старый `dock-workspace.test.js` остановился на
удалённом singleton `#chatInput`; `bridge-bootstrap.test.js` падает также на исходном
f918a56f из-за отсутствующего stub RNAssistantChatGroups. Эти fixtures требуют
отдельного обновления; они не выданы за успешные проверки этого среза.

Windows x64 + Office x64 + VS 2022 compile/delivery и пользовательское визуальное
принятие остаются открыты. Особые native gaps: WebView child-HWND drag/resize,
restore-and-move, Alt+Space/Alt+F4, taskbar/Alt+Tab, mixed DPI/отключение монитора,
file picker/DevTools/modal focus, non-activating companion drag и capture protection.
Hover Snap — отдельное улучшение, не gate базового chrome. Статус companion отражает
живые snapshots, не восстановленные после restart ожидания.

Для ревью новой правки: открыть из Excel и убедиться в единственном предпросмотре
книги на панели задач; увидеть одну строку оконных кнопок без native caption;
Full → Compact → Companion → Open; открыть другую книгу и вызовом RNAssistant
подтвердить переход — новый чат должен принадлежать новой книге; закрыть × и
убедиться, что работа остановлена. Проверить draft/edit/scroll. Нужное Windows
evidence — только фотографии экрана. Два native окна должны сохранять
capture-protection setting.

Rollback — revert только оконного среза; `native-window-state.json` можно оставить,
старый код его не читает. Runtime/storage/tool protocol не мигрируются. По запросу
пользователя «Коммит» технический срез фиксируется с minor bump `16.3.0-dev`.
Визуальное принятие остаётся In Review; commit не означает Windows qualification
или выпуск.

## Группы чатов — In Review, 2026-11-05

Срез 4a.1: группы + creator nesting, agents.group/list paging; owners Application
ConversationGroupService/Core storage, общий Web app-chat-groups. Старые row actions
переведены в общий ActionMenu; удаления/rename/open вызывают прежних owners.
Одна history/tab для всех вхождений; no automatic wake/resume/permission/budget reset.
Нового Shell/demo нет; уточнение пользователя: убрать chat icons, внутри групп
использовать общую цветную точку 6px, для групп — значок участников из Icons. Rollback — код среза без удаления журналов/групп; новые tool
admissions требуют актуальный contract. SQLite header cache перестраивается v9→v10.

Проверки: `agent-groups.test.js` — production Office/Workspace/plugin, creator/dangling
parent, два вхождения/одна вкладка, remove первой/последней связи, delete группы,
stale generation/unknown, collapse/menu Escape, стабильный цвет в разных группах,
shared row geometry (сверено с Demo на том же viewport) и narrow/125%.
[Широкий вид](review/agent-groups-wide.png), [узкий](review/agent-groups-narrow.png).
Harness проверяет CAS/restart/GC/delete races и отсутствие изменения истории/Run.
Windows/Office/WebView2 и пользовательское визуальное принятие остаются открытыми.

Ревью: 1) у документа/проекта создать две группы; 2) через «… → Группы чата…» включить
один чат в обе и открыть обе строки; 3) убрать одну связь; 4) удалить группу и убедиться,
что чат/вкладка сохраняются. Следующий срез 4a.2 — overview + адресный chat.rename;
Inspector и S2 этим не запускаются.

### Встроенные диалоги групп — In Review, 2026-11-07

Уточнение 2026-11-11 по замечаниям от 06.11: общий `accept` сохраняет группы при
ошибке каталога и не снижает generation. Покрыты поздний error, error без generation,
старый success и успешный пустой snapshot для Office/Workspace/plugin — 13 Node
guards и production Chrome fixture проходят; wide/narrow/125%, меню и focus
просмотрены. Геометрия/подсветка меню уже изменены общим деревом от 11.11;
новых CSS overrides нет. Визуальное принятие и Windows/WebView2 остаются открытыми.

В create/rename/delete заменены `window.prompt/confirm` на общий `app-dialog` и
существующие form/button tokens. Scope/revision, `changeChatGroup`, stale guards,
ошибка каталога и запрет повтора неопределённой мутации остаются у прежнего owner.
Нового UI framework, хранилища или механизма команд нет. Rollback — diff этого
дополнения без изменения групп и истории.

Сценарии `agent-groups.test.js`: отсутствие native dialogs, создание/переименование/
удаление, trim/пустое/неизменившееся имя, Escape/Enter/IME, focus return, pending и
двойной submit, ошибка/revision conflict, reset/поздний ответ, Office/Workspace/plugin,
узкие панели и 125%. Baseline и дополненный browser fixture прошли в Chrome на macOS
2026-11-10: реальные DOM/focus, split Dockview, light/dark, 420/418 px при 125%,
длинные имена как текст. IME проверен синтетическими composition/key events;
нативный IME, Windows/Office/WebView2 и пользовательское визуальное принятие открыты.
Просмотрены screenshots диалогов wide/narrow/dark (`/tmp/rnassistant-agent-group-dialog-*.png`).
Прежние screenshots выше относятся к исходному срезу.

Локально прошли 12 VM-сценариев `agent-group-dialog-guards.test.js`: точный scope и
копия показанной revision; cancel/reset/source switch; pending admission; поздние
success/error; неизвестный результат без replay, включая более новое чтение каталога.
Syntax/diff checks пройдены. Это проверка адаптера и команд, не browser evidence.
Source review также исправил возврат фокуса после полной замены Office tree root и
расчёт масштаба по дробной computed width вместо округлённого `offsetWidth`.
Browser review 2026-11-10 обнаружил потерю фокуса при Cancel: action дерева уже скрыт
через `visibility:hidden`. Resolver теперь фокусирует актуальную строку перед её
кнопкой; existing Cancel/create/rename focus assertions проходят без изменения теста.

Ревью: создать группу клавиатурой; переименовать и отменить через Escape; подтвердить
удаление и проверить сохранность чатов; повторить в узкой раскладке/125% и другом
источнике. Остальные native prompts в chat-session/project-chats, skills и HTML/VBA
остаются отдельной очередью: переиспользовать этот компонент с guards каждого owner,
не расширять текущий срез массовой заменой.

## Очередь выполнения — In Review, 2026-11-05

Scope: существующий Settings + общий Chat View; owners Application admission/Core
kernel, Web presentation. Настройка parallel steps 4/0 unlimited не ограничивает общий
бюджет. Очередь использует existing activity; streaming cache учитывает queued state.
Новых CSS, runtime/store UI, controls или Shell нет. Rollback — diff этого среза;
старые settings без поля получают default 4, данные/история не переписываются.

Проверки: agent-communication (Settings edit/reset/0, Office queued/admitted, close),
workspace-chat-progress (queued до первого model/между шагами, terminal clear,
project/plugin progress и сохранение DOM), runtime tests в Harness. Narrow/125%
поле остаётся внутри Dock; [wide](review/admission-settings-wide.png) /
[narrow](review/admission-settings-narrow.png). Визуальное принятие и Windows/WebView2
отдельно. Для ревью: 1) открыть Настройки → Агент, сохранить 1; 2) запустить два чата,
увидеть очередь и остановить ожидающий; 3) сохранить 0, проверить продолжение после
следующего admission/release. Closing tab по-прежнему не отменяет Run. S2 не запускается.

## Максимум участников — In Review, 2026-11-05

Согласованный scope: настройка числа участников одной совместной работы, включая
исходный чат; удаление квоты сообщений. Owners: Core settings, Application membership,
Web existing Settings и common chat. Нового UI/store нет; старые bridge/banner/poll
и CSS удалены. Лимит 8 по умолчанию, диапазон 0–64; прежний disabled choice мигрирует.

Ревью: открыть Настройки → Агент; проверить 8/reset, сохранить 1 и отказ приглашения;
при 2 отправлять повторные сообщения тому же участнику, проверить отказ третьего;
сохранить 0. В узком окне/125% поле остаётся в Dock. Проверить прежний «+»/файлы/сжатие
и recovery/Run controls. [Широкий](review/participant-limit-wide.png) /
[узкий](review/participant-limit-narrow.png) вид. Host-neutral и Chromium проверены;
визуальное принятие и Windows/Office/WebView2 остаются открытыми. S2 не запускается.

## Settings bounds — исправление, In Review, 2026-11-05

При проверке нового параметра участников обнаружен прежний retained overlay Settings:
при narrow/125% форма выходила из вкладки. Изолированный fix меняет только renderer
policy существующего singleton; UI state/controls/runtime сохраняются. Baseline root
находится вне `.dv-content-container`; после fix проверка actual bounds проходит.
`settings-navigation-layout.test.js` сохраняет проверки навигации/формы/hide-return.
Rollback — вернуть одну policy; Windows/WebView2 и визуальное принятие открыты.

## Пауза и отмена сохранённого Run — In Review, 2026-11-04

Scope/owner: общий Chat View (`app-chat-inbox` + Workspace presentation), typed user
bridge и Application Recovery/Control. Прежний banner расширен подписью live pause,
exact Resume и cold Stop; неподкреплённый target Resume сохранённого Run заменён.
Новых stores/events/adapters/CSS нет, rollback — обратный diff без удаления истории.
Runtime cancellation использует existing lease/CAS/read-back, не вызывает LLM/tools.

Baseline: `chat-inbox` и `agent-recovery` до изменений. После: новый browser fixture
`agent-run-controls` (три источника, exact target, stale/unknown без retry, черновик,
busy/close, narrow/125%), прежние Inbox и recovery tests. Минимальный host-neutral
Harness покрывает user control и регрессии pause/cancel/recovery; Office Controller
не входит в Harness. [Замеры](review/agent-run-controls.json).

Для ревью: 1) открыть приостановленный чат и нажать «Продолжить»; 2) после сохранения
Waiting/перезапуска нажать «Остановить» — Run terminal без нового ответа модели;
3) проверить узкую вкладку и обычный «+» с вложениями/сжатием.
[Широкий](review/agent-run-controls-wide.png) и [узкий](review/agent-run-controls-narrow.png)
виды проверены локально. Пользовательское принятие и Windows/Office/WebView2 остаются
открытыми; этот срез не запускает Inspector/группы или новый Shell.

## Chat View bounds — исправление, In Review, 2026-11-04

Пользователь сообщил выход чата за вкладку. Baseline при 640 px / 125%: body области
573 px, retained chat overlay 715.625 px; vendor повторно применял zoom к rect.
Общий Chat View переведён на `onlyWhenVisible`, без CSS-компенсаций или изменения
vendor. Existing view owner сохраняет DOM/черновики, scroll, streaming и runtime.
Все три restore paths применяют renderer policy текущего descriptor к старым layouts.
`independent-chat-views` проверяет bounds внутри собственной группы, wide/narrow/125%,
scroll hide/show, старый `always` layout, четыре точных view, drafts, parallel delivery
и close без cancel. `agent-recovery` проверяет обычный «+» реальными кликами;
`active-chat-runs` — общие меню/Stop. Windows/Office/WebView2 evidence открыто.

## User batch resume — In Review, 2026-11-04

Разрешённый пользователем срез: общее продолжение выбранных сохранённых ожиданий
из меню «+» любого чата области. Owner runtime — Application Conversations,
presentation — общий `app-agent-recovery`/ActionMenu. Именованные группы и Inspector
не начинаются автоматически; нет нового store, LLM summary или фонового опроса.
Проверить: открыть меню → выбрать двух участников → продолжить → увидеть отдельные
receipts → открыть нужный чат. Отдельно: partial/unknown response, Stop/revision race,
пагинация с выбором, Escape/focus, narrow/125%, stale load при закрытии/rebind.
Rollback — обратный diff без удаления событий: сохранённые факты остаются обычными
Inbox Resume. Windows/Office binding и визуальная приёмка пользователем открыты.
Исправлена найденная пользователем коллизия повторного «+»: портал закрывается при
открытии native picker. Regression проходит через реальные клики и filechooser,
проверяет материал и доступное сжатие, без программного обхода обычного меню.

## Universal Browser: компактный toolbar и Chat View — In Review, 2026-11-05

Пользователь одобрил концепцию и выбрал отдельный Chat View рядом в Dockview.
Owner среза: Web PreviewSurface + existing provider views, ConversationPanel и
DockWorkspace. Реализованы play/code, компактные общие действия/версии/аннотации,
ActionMenu плагина и «Обсудить» без второго composer. Plugin annotation target
закреплён за явным source chat; preserveChatOwner больше не ограничен plugin owner.
Уточнение пользователя реализовано: одна строка navigation/annotation/address/actions,
расширение адреса по фокусу, сведения под иконкой, CAS/files metadata, upload/экспорт.
Удалены постоянная подпись сохранённого проекта, title/path row и plugin decision strip;
кнопка публикации сохраняет прежний guarded handler. До 600 px tools раскрываются через ⋯.
HtmlWorkspaceView также исправлен: reload перечитывает metadata/source; resourceChanged
инвалидирует только document scope, hidden view читает при activate; dirty buffer и
новые события во время read защищены. Это Web fix поверх existing RefreshSelected.
История HTML/plugin теперь использует общий portalled ActionMenu; прежние version
popover DOM/CSS удалены. Exact guards и загрузка порциями сохранены.

2026-11-10: saved HTML history отвязана от captured chat head/revision. Typed query
читает актуальную document history без смены exact snapshot; меню обновляется при
каждом открытии, stale cursor начинает чтение заново. Пройдены host-neutral CAS history
(52 публикации, foreign document, страницы/guards), typed bridge и browser-saved-views
(no captured head, новые версии, поздний ответ старого меню, смена активного чата).
Windows controller delivery остаётся открытым; pointer-routing этим не исправлен.

Проверки: browser-saved-views, browser-views, browser-lifetime, plugin-view-selection,
html-editor-views. browser-views имел нестабильный baseline при выборе/сохранении HTML annotation.
Повтор 2026-11-10 проходит после адаптации «Обсудить» к narrow tools; opt-in pointer probe
остаётся красным и явно показывает события в iframe. Pointer risk остаётся открытым.
Проверяются reuse Chat View/draft, retained iframe, точный адресат
после выбора другого чата, source bounds, late cleanup, move/restore/narrow/zoom.
[Новые замеры](review/browser-address.json), [результат и открытые gates](../browser/06_PROGRESS.md).
После изменения строки отдельно прошли saved/HTML editor/plugin selection fixtures.
Saved restore проверяется клавиатурой: быстрый mouse click поверх iframe всё ещё
попадает в открытый pointer-routing риск; это не evidence исправления mouse routing.
Для ревью: saved HTML → ▶/исходник → «Обсудить» дважды → другая вкладка → аннотация;
[подробности](../browser/05_TEST_MATRIX.md#toolbar-review).

P2a (2026-11-10) реализован / In Review: явная копия exact saved HTML в managed
workspace через typed command и existing file/CAS owners. 100 файлов / 16 МиБ;
data/SDK/vendor/external dependencies отклоняются до записи. Только read-back даёт
Created; Unknown не повторяется, поздний ответ не меняет вкладку. 5 focused host-neutral
checks, `html-project-import` и `browser-saved-views` прошли. Старый transfer test падает
также на чистом baseline и вынесен в риск. Ревью: снимок → меню сохранения → создать
проект → вернуться и открыть ту же копию; отдельно проверить отказ на SDK/data пакете.
[Скриншоты/проверки](../browser/05_TEST_MATRIX.md#результаты-p2a--2026-11-10).

P2b (2026-11-11) реализован / In Review: Application target/capabilities приходят
в прежних responses; saved/source/live consumers проверяют exact owner перед
применением. Снимок не становится editable; source-only не исполняется; read-only
live сохраняет preview. Старый native HTML classifier удалён в пользу общего owner.
Пять isolated C# checks и три browser fixtures проходят; отдельный raw fixture
сбой воспроизводится без P2b. Baseline geometry/read counts и текущие lifetime
сохранены. [Ревью/проверки](../browser/05_TEST_MATRIX.md#результаты-p2b--2026-11-11).

P2c уточнён пользователем 2026-11-11: automatic lifecycle, без списка sessions,
отдельного завершения и нового registry. Hide/move/split/restore сохраняют страницу;
close вкладки освобождает её, reopen загружает заново. Проверка завершена host-neutral:
четыре browser fixtures проходят; добавлены только saved/editor close/reopen и
late-body assertions. Production-код/UI не менялись; dirty guards и existing portal
сохранены. [Evidence](../browser/05_TEST_MATRIX.md#результаты-p2c--2026-11-11).
P3a реализован / In Review: failed initial plugin source check не открывает active
release; existing error, watcher, explicit retry/release и retained previous frame
сохранены. Исправлена ветка PluginView, без новых компонентов/контрактов.
[Проверки и ревью](../browser/05_TEST_MATRIX.md#результаты-p3a--2026-11-11).
P3b реализован / In Review: restore получает свежий head при открытии меню сохранённой
версии; stale guard удалён из Dock route. Catalog error/late response не разрешают
действие; existing runtime conflict и pinned iframe сохранены.
[Проверки и ревью](../browser/05_TEST_MATRIX.md#результаты-p3b--2026-11-11).
Остаток P3 — presentation plugin source/release и сверка нужных capabilities у
существующих consumers, без общего descriptor ради самого плана.
Принятый порядок от 2026-11-11: P5a → минимальный P6 workflow, затем P4 diff и
persistent annotations отдельными срезами. Existing history/restore/visual selection
сохраняются; [очередь и зависимости](../browser/04_MIGRATION_PLAN.md#порядок-оставшейся-работы--2026-11-11).
[Scope и порядок](../browser/04_MIGRATION_PLAN.md#p2c-автоматический-lifecycle--scope-approved-2026-11-11).
Preview standalone source, перенос зависимостей, настоящий diff и external native browser остаются в плане. Концепция
Approved; визуальное принятие production и Windows/Office evidence остаются Open.

## Durable wait: existing Inbox control — In Review, 2026-11-04

Срез runtime 2b.2 подключает существующую кнопку Resume к сохранённому Waiting Run
в Office/Workspace/plugin. Typed hasPendingWait разрешает кнопку при пустом Inbox;
живой wait не получает дополнительный banner. Новый Inspector/группы/цвета этим не
начинаются. Application владеет продолжением, Web — общей presentation; геометрия
не меняется. Empty-Inbox/live-state покрыты `chat-inbox.test.js`. Windows controller,
exact document session и визуальное WebView2 ревью остаются открытыми.

## Представление совместной работы — Proposed, 2026-11-04

Пользователь отклонил насыщенный макет и уточнил scope: имя, небольшой цветовой
маркер и аккуратный agent_id; сохранить текущий переход к отправителю по нажатию.
Копирование, recipient/type/receipt строки и обязательный reply preview не добавлять.
Production UI пока не изменён; отдельный Demo не развивается.

1. Ближайший срез: существующий `appendSender` + общие tokens/bubble, без нового DTO
   для цвета. Проверить одну presentation истории/Inbox Office/Workspace/plugin,
   сохранение перехода source/workspace/session, стабильность цвета при rename/reorder/
   close, длинные/одинаковые имена, light/dark/narrow/zoom и отсутствие лишних controls.
2. Model-facing заголовок сокращён по согласованию, In Review: `role=user`, sender и
   корреляция сохранены, scope/display-подпись удалена; общие правила один раз в runtime
   context, в том числе guard при отключённом общении. 22 целевые проверки проходят,
   включая Office composer/Core compiler в обоих JSON-режимах и реальный Workspace
   wire после disable. UI и формат истории не менялись; полная compaction открытых
   ожиданий остаётся следующим runtime этапом.
3. Inspector: сначала текущие участники/состояния и детали по выбору. Графы/время,
   durable wait/control остаются следующими зависимыми этапами runtime-плана.
   Предыдущая компоновка отклонена; не реализовывать её автоматически.
4. Организация дерева — самостоятельный срез этапа 4a после saved catalog/spawn,
   не зависит от durable wait/control: typed creator/group metadata, общий tree builder
   для Office/Workspace/plugin, существующие navigation/rename routes. Уточнение —
   [runtime-план](../proposals/multi-agent-runtime-plan.md#группы-вложенность-и-обзор-чатов--proposed-2026-11-04).
   Проверить collapse/search/focus/narrow/zoom, reopen, один чат в двух группах без
   дублирования вкладки, пропавшего creator и отсутствие скрытого запуска/отмены Run.
   Содержание чатов читается лениво; построение дерева не загружает их истории.
   Дополнительно: повторный add, remove одной/последней связи, удаление группы при
   работающих участниках, удаление чата из нескольких групп, add/delete race и rebuild
   без восстановления удалённых связей; существующие deletion guards не обходятся.

Структура — [Architecture](UI_ARCHITECTURE.md#участники-сообщения-и-совместная-работа--proposed-2026-11-04),
экран — [Screen Map](UI_SCREEN_MAP.md#совместная-работа--proposed-2026-11-04),
оформление — [Design System](UI_DESIGN_SYSTEM.md#участники-и-адресные-сообщения--proposed-2026-11-04).
Для docs-only — diff/links; runtime/UI проверки выбираются при фактической реализации.
Windows/Office/WebView2 и визуальная приёмка остаются отдельными открытыми gates.

## Участники без открытых вкладок — In Review, 2026-11-04

Owner: Application/Conversations; Web — существующая общая presentation.
Первый срез [мультиагентного плана](../proposals/multi-agent-runtime-plan.md): saved
catalog discovery и `agents.spawn`, без нового Inspector/demo. Presence bridge/leases
удалены; UI init не сбрасывает runtime scopes. Проверены production DOM, narrow/125%,
stale state при rebind/close и сквозной Workspace spawn/run вообще без views.
Геометрия и placement не меняются. Office controller проверен по исходникам; Harness
использует его stub. Windows/Office/WebView2 evidence и визуальная приёмка открыты.

Ревью: 1) создать участника агентом, 2) открыть его из обычного каталога,
3) закрыть вкладку во время обмена и отправить ещё сообщение,
4) открыть повторно и проверить историю, 5) нажать Stop и проверить сохранение паузы.
Rollback — обратный diff кода без удаления пользовательских журналов; старый binary
не должен продолжать новые tool admissions/peer envelopes по скрытому fallback.

HTML target cutover (2026-11-03): owner — HTML domain/Controller, явный workspace
для model mutations и обновление только выбранного logical view. Dockview placement
не меняется. Ревью: открыть общий HTML в двух чатах, изменить CSS агентом, проверить
сохранение dirty HTML и отказ stale Save; открыть другой HTML и убедиться, что правка
первого не переключает редактор. Windows/Office evidence остаётся открытым.

## Загрузка и удаление чатов по фото — In Review, 2026-11-03

Owner: Web Chat View/navigation; устранение лишних чтений — ChatSessionService.
Воспроизведён `innerHTML` на отсутствующем контексте при deferred startup с
сохранённой вкладкой Настроек без чата. Renderer использует адресный Chat View;
ожидание истории явно показано в дереве. Удаление показывает pending до bridge
ответа и не допускает повторный запрос; строка сохраняется до подтверждения.
Service использует уже зарезервированную session и не обходит повторно тот же
пустой каталог. Медленное удаление пишет load/deleteAndSelect/projection timings.

Проверки: `chat-loading-deletion.test.js` (production DOM, delayed startup,
очередь/повтор/ошибка/успех удаления) и Harness `chat sessions: delete selects
remaining projection`. Ревью: восстановить раскладку только с Настройками,
дождаться истории, удалить чат и наблюдать строку до ответа. Windows/Office и
реальное время операций на пользовательском хранилище остаются открытыми.

## Продолжение HTML и editor placement — In Review, 2026-11-03

Scope: S3b.3c live HTML/source/annotations и S4a размещение действующих Library/VBA
редакторов. Owners: HtmlWorkspaceView, Library/VBA и DockWorkspace; runtime/Bridge/storage
не меняются. Удалены singleton HTML workbench/browser, global dirty/read/write hooks
на смену выбранного чата и selected annotation facades. HTML — per-chat view;
Library/VBA — retained singletons с явным ограничением, без клонирования globals.

Адресные проверки: `html-editor-views`, `html-workspace-layout`, source/write/actions,
`editor-dock`; Browser и аннотации проверяются отдельными browser fixtures. Проверять
две HTML-вкладки, правки/сохранение в исходный чат, аннотацию при другом активном чате,
dirty close, stale source, move/restore/late close и narrow/dark/125%.

Ревью: открыть HTML двух чатов → изменить код одного → перейти ко второму и обратно;
закрыть грязную вкладку с отказом; открыть Библиотеку/VBA через «+», перенести и вернуть
раскладку; при узкой ширине проверить header и полноразмерный редактор.

Остаток: независимые Library/VBA object editors, cold-start presentation mementos и
Windows/Office/WebView2 evidence. S4 целиком и формальный выпуск не закрыты.

## Общение агентов — In Review, 2026-11-03

Самостоятельный согласованный срез поверх независимых Chat View, не автоматический
старт оставшегося S2. Owners: Application/Conversations, Core provenance/settings,
общая Web presentation. Реализованы discovery/queued send, явные ответы и отличимый
incoming bubble. Историческая квота 20 сообщений и её продолжение заменены
2026-11-05 максимумом участников (срез выше). Ноль отключает межагентные tools даже
для старого handler. Добавлен общий встроенный skill.

После Windows-фото исправлены Office progress route и ложная пауза очереди после
Completed со старыми failed/unknown попытками. Список включает текущий чат с `is_self`;
короткий стабильный `agent_id` используется в tool wire и подписи bubble. Добавлен
`agents.wait` v2: 1–10 секунд за вызов (default 5), без суммарной квоты, немедленное
пробуждение через общий input channel. Skill v4.0.0 требует продолжать работу после
send и учитывать ID/тему. Peer input доставляется между model/tool действиями и без
wait; начатый вызов/read-back завершается, незапущенные calls перепланируются.
Host-neutral native queue + kernel + bridge воспроизводит ошибку адреса, ответ,
ожидание в том же run и поздний следующий ход; Chromium проверяет адресный streaming
и sender label. Windows latency и визуальное принятие пользователем ещё открыты.

Приёмка: открыть три чата; попросить A обратиться к B/C через `agents.list/send/wait`;
проверить self/ID, активность получателей, ответ в текущем run, подпись и переход;
в новой цепочке с лимитом 1 проверить продолжение;
закрыть вкладку получателя и проверить приём новых отправок и сохранение уже принятых.
Сохранить лимит 0, проверить отсутствие обмена/кнопки продолжения; включить обратно.
Визуальное принятие пользователем ещё открыто. Проверки и сценарии — в
[Harness guide](../../tests/RNAssistant.Harness/README.md#agent-communication--2026-11-03).
Известные gaps: реальный Windows/Office/WebView2, live-model cooperation, общий
retained-overlay zoom при узкой Shell и generic no-progress при одинаковых timeout
receipts `agents.wait` (см. [текущий контракт](../conversation-protocol.md#межагентное-общение--2026-11-03)).
Прежний R61 inventory требует отдельного
обновления после переименований. Не смешивать эти gaps с passing адресными проверками.
Rollback — обратный diff среза, без удаления пользовательских событий/CAS.
Прежнего межагентного пути и временного dual path не было.

## Диагностика и дерево запусков — In Review, 2026-11-03

Дополнение по фото повторов: runtime передаёт typed RequestCause в существующем
trace/trajectory contract. Журнал показывает причину и feedback, exact исходный
ответ — по кнопке через прежний reader; API timeout виден в настройках запроса.
Минимальные проверки дополнения: run-journal, stream-render-cost и
diagnostics-navigation (wide/narrow, lazy payload, точные связи, cleanup).
Windows/WebView2 evidence и пользовательское принятие остаются открытыми.
Срез finish 2026-11-04 сохраняет specific feedback через reflection и останавливает
одинаковые отказы в Core. Он не добавляет отдельный semantic verdict исходного
запроса в UI; это открытый gap [диагностики](../diagnostics.md#карта-экранов-и-маршрутов).

Scope — прямой запрос пользователя: Dock-вкладка, общие стили дерева, сокращение
дублирования и удобный выбор запуска/шага с компактными деталями. Owner — Web
diagnostics/trajectory. Исходный UI-срез не менял runtime/Bridge/storage; дополнение
выше расширяет диагностические метаданные существующих trace/trajectory контрактов.

Baseline: собственные вложенные disclosures RunJournal, действия/переходы внутри
строк, повторный заголовок экрана и скрытая команда снятия scope. Теперь один
NavigationTree и соседний reader, явные «Все запуски», общий ActionMenu/JSON viewer.
Старый renderer/CSS удалён. Группировка обменов использует записанные request/attempt
IDs внутри run, с уникальными step/call связями; неоднозначные связи не достраиваются
по соседству. Bounds 200/1000 и lazy
payload сохранены. Изменения обратимы без миграции данных и второго UI path.

Минимальная проверка: run-journal, trajectory-json-viewer, chat-history-layout,
diagnostics-navigation, model-diagnostics-layout, performance-diagnostics,
shared-navigation-tree и json-viewer.
Browser fixture проверяет каждое событие ровно один раз, выбор нескольких запусков,
общие вычисленные font/color/height/selection, возврат из запроса, payload по действию,
close/reopen, move/split/restore, light/dark 390/1440 px и 125% zoom. Unit fixtures
проверяют bounds, неоднозначные корреляции, cancellation/late replies и lossless JSON.
Ускорение не заявляется; latency и память Office здесь не измерялись.

Ревью: открыть запуск → «Все запуски» → выбрать шаг/действие → открыть запрос
и вернуться. Проверить спокойную иерархию дерева и полезность деталей.
Визуальное принятие пользователем и Windows/Office/WebView2 evidence остаются открытыми.

Уточнение источника: rail/status bar явно открывают текущий чат и последний ход;
Dock-вкладка сохраняет прежний разбор. Source caption ведёт в исходный чат,
действие строки существующего дерева открывает диагностику любого выбранного
чата без смены composer. `diagnostics-navigation` проверяет эти маршруты,
обновление закреплённого источника после смены активного чата и отсутствие
переноса run ID между разными чатами. Runtime/store не меняются.
По следующему запросу пользователя строка группы также переключает раскрытие.
Общий NavigationTree владеет режимом; `chat-history-layout` проверяет повторный
клик, Enter/Space и сохранение выбора в reader.
Следующее уточнение: lifecycle events больше не создают группы «Шаг»; запрос/ответ
образуют один обмен, повторные попытки разделены, уровень «Модель» удалён.
Обзор запуска/запроса показывает состав, исходы и результаты действий без body reads;
30 строк на страницу. Fixture с двумя запросами, одним logical step и разными
transport step IDs даёт два обмена; лишние границы остаются служебными событиями.
Проверены exact request links, неоднозначные call-связи, переходы из сводки, общий
JSON reader и таблица. Browser измеряет совпадение колонок и baseline текста
с допуском 1 px на 390/1440 px; общие Tree/Table controls переиспользованы.

Проверка регрессий загрузки 2026-11-03: воспроизведены повторный запуск pending read,
пустой экран на refresh, потеря видимости журнала при ошибке следующей страницы,
скачок scroll при дозагрузке и reader высотой 16 px в короткой вкладке.
Исправлены в существующих Trajectory/NavigationTree/CSS owners. Новый
`diagnostics-loading` проверяет slow/failing reads, 5 страниц по 200 событий,
keyboard после lazy expansion, сохранение scroll и stale responses, 600/390 × 400 px.
На синтетическом журнале из 1000 событий начальный DOM снизился с 12 538 до 1 338
элементов: скрытые ветки монтируются при раскрытии. Это не измерение задержки
хранилища или Windows/Office/WebView2; эти границы остаются открытыми.

## Сохранение сообщений Office-чата — In Review, 2026-11-03

Scope — повторное сообщение пользователя о подмене текста и пропаже ввода из
очереди именно в Office. Owners: общий ConversationState/Messages, Office Inbox;
workspace/plugin используют тот же window/key/patch contract. Runtime/storage и
Dock placement не меняются, отдельный transcript/store не вводится.

Три регрессии воспроизведены до исправлений: хвостовая страница новой revision
удаляла загруженный префикс с применённым вводом; patch временно отсоединял
сохранённые узлы, сбрасывая selection/viewers; повторный одинаковый ввод после ACK
переиспользовал operation ID и не создавал второй элемент очереди. Исправлены merge
по message IDs, patch контейнеров на месте и выбор ID только для неопределённой
повторной доставки. Сегмент run/step сохраняет ключ между live и saved; transient
больше не кодируется общим live-key. Draft обозначен в существующей строке статуса.

Адресные Web/Chromium проверки: окно разных revisions и cap/explicit jump/truncate;
одинаковые отправки, потерянный ACK, stale receipts и очередь → история → следующий
refresh; отсутствие disconnect у завершённого call, selection/focus/disclosures,
live → saved, late deltas, cost/coalescing; shared workspace/plugin progress,
740/360 px, light/dark, 125%, scroll/follow/anchor. Устаревшие fixtures переключены
на действующие source/composer; expectation старой жёлтой карточки удалён.

Ревью: `python3 -B prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. Во время длинного ответа отправить два одинаковых сообщения в очередь:
   оба остаются видимыми и переходят в историю по своим IDs.
2. Выделить принятое пояснение и раскрыть завершённый вызов; дождаться следующего
   шага/refresh: текст и раскрытие сохраняются.
3. Дождаться finish, прокрутить историю и открыть чат заново: принятые сообщения
   доступны, непринятый черновик не выдаётся за сохранённый ответ.
Windows/Office/WebView2 остаётся открытым evidence; локальный Chromium его не
заменяет. Откат — scoped Web diff, без миграции или удаления пользовательской истории.

## Лента, reason и штатный возврат макроса — In Review, 2026-11-03

Scope — запрос пользователя о неверных формах ответа, повторных макросах,
завершении и исчезающей/шумной ленте. Owners: Core parser/ToolRuntime/AgentProgressTracker;
Office macro handler и existing message projections; Web Messages/ConversationView.
Это исправление действующего чата, не новый этап Shell или завершение S2.

Baseline: `agent-activity-layout`, `live-run-projection`, `agent-timeline-order`
и `run-view-state` до UI-правок. Было: общий live article заменялся при новом
действии; пустой step временно показывал служебный title; принятые пояснения
исчезали в закрытой истории при finish; unknown/partial создавали лишние notices.
Теперь: общий patch, сохранение раскрытий/фокуса, видимые `message`/`reason`,
одни детали оценок/счётчиков. Старые warning wrappers удалены.

Адресные проверки покрывают нормализацию без ослабления admission, нормальный
возврат/сбой/replay, broad invalidation, bounded repeats, Office reopen и workspace
projection. Web: сохранение узлов, accepted reasoning, late deltas, cap/coalescing,
скрытые claims/facts, общая лента проекта/плагина, 740/360 px, light/dark и 125%.
Реальные модели и Windows/Office/
WebView2 остаются отдельным открытым evidence; dock placement не менялся.

Ревью: `python3 -B prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. Во время ответа раскрыть вызов, затем дождаться следующего шага: раскрытие и
   фокус сохраняются, `reason` виден в ленте.
2. Дождаться finish и открыть чат заново: принятые пояснения остаются, история
   calls и «Оценка модели и источники» раскрываются отдельно.
3. На Windows запустить разрешённый макрос повторно после штатного возврата;
   проверить данные через Office tools. После сбоя автоповтор по-прежнему запрещён.
Откат — этот scoped diff; миграции/удаления истории и выпуска сборки нет.

## Исправления чата по трём фото — принято, 2026-11-02

Принято пользователем по «Коммит пуш».
Owner: Application plugin/workspace projection, Web project chat adapter.
До исправления native history fixture воспроизводит видимый protocol result с
ролью `user`, Chromium — отсутствие обновления после stale Stop. Служебные результаты
переключены на общую Office DTO-проекцию; Stop получил один scoped refresh/retry,
защиту от смены run/view и читаемый статус. Kernel/storage/форматы не менялись.
Проверены native projection, страницы истории, stale отказ и реальная отмена
loopback model wait; Chromium — единый блок запуска без ложной ошибки протокола,
16 project/plugin сценариев Stop, включая terminal, unknown и уход из view.
Визуально сверены 1280/420 px; после resize геометрия composer совпадает с baseline.

Ревью: `python3 -B prototypes/serve_ui.py --port 0`, `/preview/web/?scenario=ready`;
для плагина открыть Browser → «Чат исходников». Проверить единый блок действий,
Stop во время ответа и переход в другой чат без чужой ошибки. Mock не доказывает
Windows/Office cancellation. Возврат — diff projection/Stop, без миграции данных.
Полный S2 остаётся незавершённым; детали фото и Windows gap — в RISK_REGISTER.

## JSON-профиль агента — In Review, 2026-11-02

Ограниченный срез настроек/Lab: один формат ответа, JSON-история без независимых
role/history/fallback controls, уведомление миграции и существующий JSON viewer.
Общий save/dirty/ACK owner сохранён. Lab отдельно показывает видимость content,
ID и связь результатов; добавлены B5-opaque, B8-cycle, C2-tools.

Локально проверены форма, шесть сочетаний output/instruction role, узкий экран
320/390, light/dark, сохранение с поздними edits и ошибка save. Общая Lab fixture
проверяет 26 строк, whole-row/keyboard selection, raw preview, export/cancel.
Для ревью: открыть «Настройки → Агент», сменить object/schema, раскрыть пример,
применить и начать новый чат. Реальные модели и Office не запускались. Это не
приёмка других параллельных UI-срезов или Windows qualification.


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

## Создание Office-чата без прежней истории — In Review, 2026-11-02

Ограниченное исправление чтения: session service отделяет создание от LoadSession,
controller отдаёт typed catalogChanges вместо повторного каталога. Web использует
существующий merger; DOM, Shell и Dockview не меняются. 13 focused service checks,
реальный host-neutral controller и Web run-view-state/chat-sync прошли.
Ревью: создать чат рядом с сохранённым; повторить создание пустого; создать после
«Сохранить как» — остальные строки/история сохраняются, выбран новый пустой чат.
Уже начатые navigation/catalog requests и настоящий перенос Save As ещё могут
задерживать ответ; Windows/Office/WebView2 timing и визуальное ревью остаются открытыми.

По фото с неоткрываемым «Новым чатом» явное создание теперь сохраняет пустую
запись для повторного выбора; при уходе с автоматического transient его строка
удаляется selection delta. Host-neutral controller проверяет оба пути, включая
возврат к пустому чату. Локальный Chromium проверяет компактное поле/кнопки на
280/320 px и полную высоту Материалов в Dockview; реальный Office остаётся открытым
визуальным evidence.

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

Актуально после контрольной точки 2026-11-03: общая factory и независимые
Office/Workspace/plugin вкладки **реализованы**. Раздельные draft/edit/model/history,
параллельная отправка, close без отмены run, late delivery и восстановление всех
черновиков проверены. Повторять подготовку Chat View или перенос placement не нужно.
S2b.1–S2b.12 ниже — история выполненной подготовки, не очередь оставшихся задач.

Ближайший законченный результат — одинаковые возможности уже работающего чата:

1. Общие вложения и очередь активного Workspace реализованы в срезе ниже.
   Устранить остаток: контекст/ручное сжатие, Steer при pending approval,
   PDF page selectors и явное чтение исторических media-вложений. Проверить
   подтверждения/вопросы и ошибки в нескольких вкладках;
   их адресная доставка уже подключена, но это ещё не полная parity capabilities.
   Недостающие contracts закрывать у domain owners, не постоянным скрытием controls.
2. Переключить оставшиеся secondary consumers с selected facade на explicit
   target/provider и удалить adapter после последнего consumer. Живые HTML/plugin
   editor/annotation providers относятся к S3b.3c, не к новой реализации чата.
3. Сохранить cap 240, lazy paints и границы subscriptions/reads; довести необходимые
   старые browser fixtures до factory и проверить несколько тяжёлых вкладок.
   Catalog paging — только при подтверждённой необходимости и typed contract.

Полный S2 закрывается после parity и visual/behavior review. Windows/Office evidence
остаётся отдельным gate; локальные mock/Chromium проверки его не заменяют.

Проверка удаления путей после `c768fa38`: прежний project-file upload отсутствует
в production, bridge/contracts и project includes. Дочищены пустые snapshot helpers,
неиспользуемые exports/locals и недостижимые ветки старого mount; действующие
selected adapters остаются с removal gate в MIGRATION_MAP. Chromium
`independent-chat-views`, `workspace-shared-draft` и `active-chat-runs` проходят;
измерение длинного названия в последнем теперь ждёт фактического metadata update.
Runtime/VSTO проверки для этой Web-дочистки не запускались. Это не закрытие S2.

### Общие вложения, очередь и меню запросов — In Review, 2026-11-03

Baseline `a96a1500`; owners: Core attachments, Application Conversations/Workspace,
Web ConversationPanel/ActionMenu. Продолжение S2 и нижнее меню разрешены пользователем.

- Workspace/plugin принимают общие staged attachments, immutable CAS и точные
  message links. Upload lease включает workspace/chat; большие тексты входят
  только через bounded `core.read`, без вставки тела в исходный prompt.
  Старый upload в папку проекта/bridge/DTO/UI удалён.
- Queue/Steer используют существующий ConversationInboxService и Application worker.
  Очередь не получает выдуманный run ID; обычное завершение запускает следующий ввод,
  Stop сохраняет его до явного продолжения. Edit/remove/steer адресуют input revision;
  UI использует общие pending cards. Неприменённый Steer старого run не replay.
- «Запросы N» в status bar читает RunOwner текущего assistant runtime без истории.
  Строка открывает exact чат, Stop работает без его вкладки; close UI оставляет run
  в фоне. Generation guards и run ID защищают от отмены нового запроса.
  По уточнению пользователя: без chat icons, content width до 340 px.

Минимальные проверки: Harness `plugins: workspace uses common message attachments`
и `workspace: queue uses common owner and explicit resume`; Chromium
`workspace-shared-draft`, `independent-chat-views`, `active-chat-runs`; pure JS
`resource-upload` и `conversation-draft`. Проверены foreign workspace/draft,
idempotent receipt, bounded UTF-16 окна, отмена/явное продолжение, exact late reply,
общий queue editor, закрытые вкладки, stale Stop, narrow/125% zoom и focus.
Три старых падения воспроизведены на чистом baseline и остаются в
[RISK_REGISTER](../stabilization/RISK_REGISTER.md#проверки-parity-чата-старые-падения--2026-11-03).
Это адресное host-neutral evidence, не зелёный полный Harness или Office qualification.

Ревью: открыть production preview `/preview/web/?scenario=ready`, отправить запрос
из Office и проекта, закрыть одну chat-вкладку, открыть её из «Запросы», остановить
другую строку прямо в меню. Проверить короткое/длинное имя и узкое окно. Native
queue/upload поведение проверяет Harness; mock не запускает модель. Откат — diff
среза, без миграции сохранённых данных и без удаления старых пользовательских файлов.
Остаток S2 перечислен выше; pending-approval/context/media parity и secondary
consumers не объявлены завершёнными. Меню не агрегирует другие процессы/Office окна.

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

Адресная визуальная коррекция 2026-11-02 — In Review: действия дерева не
меняют ширину/высоту строки (32 px) при наведении; повторный render с тем же
выбором сохраняет scroll. Продолжение по замечанию пользователя убирает вертикальную
линию вложенности и лишний inset у всех NavigationTree; действия на desktop
накладываются справа, освобождая ширину названия, а усечённый текст плавно
открывает хвост при наведении/фокусе. Раскрытие карточки чата останавливает
tail-follow до явного перехода вниз. Проверки: `shared-navigation-tree`, `chat-tree-layout`,
`conversation-view`, `conversation-scroll-interaction`; дерево осмотрено в
production preview при 1280/420 px, 125% zoom и touch, лента — при 1280/420 px.
Windows/Office evidence остаётся открытым.

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

### S2b.6: общая привязка черновика — 2026-11-02

S2b.5 сохранён в `159a8fcc`. Owner — ChatComposer; оба источника используют общий
capture/restore/accept/append, без отдельного store. Удалены Workspace
`current/loaded/mode/reasoning` и прямое восстановление ввода из Office ACK.
Исправлен overwrite незавершённой правки поздним ACK; отмена правки больше не
возвращает уже отправленный текст. Поздняя ссылка на файл остаётся в обычном draft.
Версии, protected persistence, receipts, C#/Bridge и визуальные стили не менялись.

Проверки: `conversation-draft` с реальным ChatDrafts (version guards, edit/ACK,
background append, два controller, dispose, 100 restores без записи в textarea),
`conversation-composer`, `conversation-preferences`, `chat-inbox` с реальным
ACK во время правки, `multi-chat-requests`. Chromium `chat-input-delivery` и
`chat-draft-reload` прошли на изолированном `159a8fcc` + этот срез;
`workspace-shared-draft` — в рабочем дереве после `75c59e88` (S3b.2).
Это local/mock evidence, не Windows/Office qualification.

**Принят пользователем для production 2026-11-02**. Реализация находится в `web/`.
Запуск: `python3 prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
Проверить: написать текст и переключить Office → проект → плагин → назад;
начать правку сообщения, переключиться/вернуться и отменить её; отправить сообщение
и сразу писать следующее; перезагрузить preview и проверить draft/план/reasoning.
Компоновка прежняя. Откат — diff среза, без миграции данных.

Следующий участок пункта 1: exact-target snapshot factories и full push routing.
Затем остальные file/context/approval/media adapters перед независимыми chat tabs.
Временные singleton consumers перечислены в Migration Map; полный S2 ещё открыт.

### S2b.7: адресные snapshot sources — 2026-11-02

S2b.6 сохранён в `fa6f2c6a`; оба среза находятся в production `web/`.
По «Переноси в продакшен и далее и коммит» продолжена граница источников данных.
Baseline воспроизвёл запрос Office A → ответ B → выбор B. Общий snapshot source
теперь проверяет адрес до UI/receipt; Office selection/init/full push и Workspace
чтения/команды используют его. Прежние точечные проверки заменены общим matcher.
Источник хранит только адрес и callbacks; store, C#/Bridge, renderer и styles прежние.

Проверки: `conversation-snapshot-source`, `chat-sync`, `chat-history-paging`,
`model-navigation` (включая uncertain preference read-back через общий source);
Chromium `workspace-shared-draft` (включая неверные chat/workspace ответы),
`workspace-chat-progress`, `chat-input-delivery`. Первый параллельный draft-прогон
остановился на перекрытии кнопки plugin preview; отдельный повтор прошёл без изменений
кода. Финальный `workspace-shared-draft` прошёл отдельно на `fa6f2c6a` + staged diff,
без параллельного S3b.3. UI assertions `artifact-commit-projection` прошли, но его старый C# source-order
assertion не проходит и на `fa6f2c6a`; существующий gap уточнён в RISK_REGISTER.

Ревью: `/preview/web/?scenario=ready` через `python3 prototypes/serve_ui.py`;
переключить Office/проект/плагин, отправить текст, проверить план/reasoning и черновик
после возврата. Компоновка прежняя. Откат — отдельный commit, без миграции данных.
Полный S2 остаётся открыт: selected snapshots/full push должны принадлежать scope
view, затем остальные adapters и независимые chat tabs. Windows evidence открыт.

### S2b.8: snapshot у общего представления — 2026-11-02

Срез принят пользователем для production по «Коммит и далее».
S2b.7 сохранён в `182236c6`. Первый consumer общего SnapshotOwner — проект/плагин
в production `web/`: модульный `active` и удерживаемый snapshot run observer удалены.
ConversationView хранит один bounded object; renderer, composer и Inspector
заимствуют его. Смена адреса/dispose освобождает объект и инвалидирует прежние
callbacks до следующего paint; hide/reparent сохраняет данные. Office selected
state ещё не переключён, новые chat tabs не включены. Runtime/storage/Bridge прежние.

Read/open/submit/cancel/preference/history delivery захватывает owner. Поздняя ошибка
отмены после A → B → A не попадает в повторно открытый чат; reconciliation настроек
получает свежий adapter без повтора mutation. Сохранены revision/terminal/live guards,
240 сообщений, раннее окно истории при refresh и явный переход к последним сообщениям.

Проверки: `conversation-projection`, `conversation-view`, `chat-history-paging`,
`conversation-preferences`, `model-navigation`; Chromium `workspace-shared-draft`,
`workspace-chat-progress`, `workspace-chat-layout`. Новый сценарий проверяет позднюю
ошибку отмены; unit-проверка — два независимых owner, A → B → A, dispose и освобождение
100 посещённых targets. Stream сохраняет DOM и обновляет только live leaf.
Browser evidence снят на `182236c6` + diff S2b.8, отдельно от параллельного S3b.3;
production layout визуально сохранён. Это mock bridge, Windows/Office evidence открыт.

Ревью: `python3 prototypes/serve_ui.py`, затем `/preview/web/?scenario=ready`.
1. Открыть проект, перейти в другой чат и вернуться: текст, история и настройки
   принадлежат выбранному чату; незавершённый ввод сохраняется.
2. Переключить проект → плагин → Office → назад, скрыть/показать область чата:
   composer и прокрутка сохраняют прежнее поведение.
3. Во время ответа сменить чат и вернуться: нет чужого текста/ошибок; сообщение
   продолжает обновляться без перестройки всей ленты.

Откат — diff среза, без миграции данных. Следующий участок — Office на том же owner
и адресная full push delivery; затем оставшиеся file/context/approval/media adapters
и удаление singleton перед независимыми chat tabs. Временный adapter и removal gate
указаны в Migration Map; полный S2 остаётся открыт.

### S2b.9: Office transcript/preferences у общего owner — 2026-11-02

Срез принят пользователем по «Коммит и далее».
S2b.8 принят и сохранён в `8a2ee0fd`. В production Office использует общий
SnapshotOwner для messages/window/model/mode/reasoning; прежние шесть stored fields
заменены accessor facade без второй копии истории. Это следующий ограниченный
участок Office cutover: `state` также содержит каталоги, документы и редакторы,
которые не следует передавать владельцу transcript одной массовой заменой.

Owner освобождается при смене чата, init/reconnect и bridge reset. Full push
доставляется только живому выбранному Office owner, не заменяет Workspace и не
воскрешает released source. History/latest/retry и preference read-back проверяют
identity owner, даже если после reset совпали chat ID/navigation version. Bounds,
draft/receipt, navigation drain и revision guards сохранены; новых CSS/DOM нет.

Проверки: `office-conversation-projection` на реальном core state/accessors,
`chat-sync`, `chat-history-paging`, `model-navigation`, `conversation-snapshot-source`;
Chromium `workspace-shared-draft`, `chat-input-delivery`. Browser evidence —
`8a2ee0fd` + diff S2b.9, без параллельных Browser/protocol/storage changes.
`office-host-chat-layout` не проходит и на исходном ChatState из `8a2ee0fd`:
fixture не подключает NavigationTree; этот отдельный gap сохранён в RISK_REGISTER.
Windows/Office qualification не выполнялась.

Ревью: `python3 prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. В Office выбрать модель/режим, написать черновик, перейти проект → плагин →
   Office: настройки и ввод принадлежат прежнему чату.
2. Перейти между Office-чатами, открыть старую историю и вернуться к последним
   сообщениям: окно ограничено, refresh сохраняет место чтения.
3. После переподключения открыть прежний чат: старые ответы истории/настроек
   не подменяют новые данные. Смена домена не создаёт второй transcript.

Откат — diff среза, без миграции данных. Для S2 ещё нужны explicit Office source/view
factory, context/artifacts/live delivery и остальные service adapters. После
переключения consumers удалить selected adapter и accessors (Migration Map), затем
включать независимые chat tabs. Текущий facade не является архитектурой новых вкладок.

### S2b.10: адресный контекст и метаданные материалов — 2026-11-02

Срез принят пользователем по «Коммит».
S2b.9 принят и сохранён в `205b6cf6`. Следующий production-срез передаёт context,
usage, artifacts/library metadata и active artifact/checkpoint IDs тому же Office
SnapshotOwner. Прежние восемь stored fields заменены accessors; источник начальных
значений один. HTML working copy/редактор и runtime/storage не меняются.

На `205b6cf6` воспроизведено: addTextContext из A → перейти B → открыть A снова →
старый ответ заменяет свежий контекст. Теперь text/selection/remove/clear захватывают
owner и после A → B → A или same-ID reset не меняют новое представление и не
запускают его refresh. Empty selection/частичный ответ другого чата не наследуют
контекст и метаданные; dispose освобождает их вместе с лентой. Bound history,
адресные drafts/receipts и MaterialView leases сохраняются.

Проверки: новый `office-context-projection` (baseline red → green, все четыре
команды, reconnect, один объект context/material metadata и empty selection),
`office-conversation-projection`, `context-usage`, `chat-sync`; Chromium
`workspace-shared-draft`, `material-object-views` на `205b6cf6` + diff S2b.10.
Draft fixture приведён к уже принятому Browser route: после выбора плагина явно
открывается «Чат исходников». Прежний сценарий ждал скрытый input в Browser.
Независимые материалы сохраняют scope/DOM, ограниченные pages и late lease cleanup.

При осмотре обнаружен отдельный baseline-дефект HTML source validation:
`html-workspace-source` не читает исходник из-за `skills is not defined`. Production
файл и тест идентичны `205b6cf6`; дефект сохранён в RISK_REGISTER отдельным owner,
в этот срез не включён. Ни чтение HTML source, ни Office/Windows не объявляются
квалифицированными по проверкам mock.

Ревью: `python3 prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. Переключить Office-чаты и открыть Inspector: лента, контекст и материалы
   соответствуют выбранному чату, empty state не показывает прежние данные.
2. Открыть два материала, перейти в другой чат/проект: вкладки материалов остаются
   на исходных объектах; вернуться в Office и проверить черновик.
3. Плагин → «Чат исходников» → проект с тем же source chat: ввод и настройки общие.
Гонки context commands проверены через synthetic DTO; selection/Office mutations
в mock не квалифицированы. Компоновка прежняя; откат — diff без миграции данных.

Далее: отдельное исправление найденного HTML source read, затем live run delivery,
explicit source/view factory и удаление selected facade перед независимыми chat tabs.
HTML editor working copy остаётся у своего owner; фасад не клонировать для вкладок.

### Исправление HTML source после S2b.10 — 2026-11-02

Owner: Web HtmlWorkspace source reader. В validation и export ошибочное
`skills.skills.workspace.files` заменено переданным `workspace.files`.
Это отдельное исправление подтверждённого baseline-дефекта, без изменения
mutation/storage/runtime. `html-workspace-source` — 9/9: lazy read, exact bytes,
bounded cache, cancel/stale/revision guards, late lease cleanup и export.
Cache query обновлён. Реальный Office read/export остаётся Windows evidence.

### S2b.11: live run у адресного source — принято, 2026-11-02

Срез принят пользователем по «Коммит».
S2b.10 сохранён в `29888056`. Owner: Web conversation presentation, существующие
ChatState/ChatRun. SnapshotOwner теперь хранит normalized run DTO и заимствует
transient run; stream/reasoning/activities не копируются в selected state.
Office progress reducer перенесён из bridge boot в `app-chat-run.js`.
Старые live writers и reset helper удалены; повтор, отмена, terminal guard,
reasoning bound и объединение paints в кадр сохранены. Layout/runtime не менялись.

Проверки: `chat-run-completion` включает release → background → A/B/return,
замену run и отсутствие второй копии; `stream-render-cost`, `live-run-projection`,
`office-conversation-projection`, `run-view-state`, `bridge-bootstrap`,
`message-actions` прошли. Fixture завершения теперь действительно запускает B:
прежний мог принять оставшийся текст A за ответ B. DOM fixture использует реальные
source accessors. Chromium `workspace-chat-progress` и `chat-input-delivery`
прошли на изолированном `f0bfba8e` + этот Web diff, без параллельных C#/CSS edits.
Progress fixture явно открывает «Чат исходников» после выбора plugin Browser.
1000 узлов истории сохраняются, 100 background deltas дают одно обновление дерева;
30 Workspace updates меняют только live leaf. Общего ускорения не заявляем.

Ревью: `python3 prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. Начать ответ Office, перейти в другой чат и вернуться: каждый показывает свой
   поток; background delivery не переключает выбранный чат.
2. Повтор/отмена: непринятый текст очищается, поздний токен не возвращает его.
3. Проект и plugin → «Чат исходников»: во время потока раскрыть reasoning,
   прокрутить вверх; раскрытие, фокус и ручная прокрутка сохраняются.
Mock/synthetic delivery проверяет UI, не квалифицирует Windows/Office.
Откат — отдельный Web diff; миграции данных нет.

Далее: explicit source/view factory и удаление selected facade, затем независимые
chat tabs. Attachments/approval/context auxiliary consumers сверяются при этом
с тем же owner. HTML working copy остаётся у HtmlWorkspace; новые вкладки не
должны клонировать selected adapter. Принятие среза не закрывает Windows evidence.

### S2b.12: Office source factory и явный snapshot ленты — принято

Срез принят пользователем по «Коммит». При этом пользователь указал на затянувшуюся
подготовку независимых вкладок; оценка остатка и предложение ниже не означают их готовность.
Baseline `d61a9aff`; S2b.11 принят пользователем. Owner: Web ChatState / transcript.
Фабрика создаёт адресный SnapshotOwner; существующий selected adapter теперь её
consumer. Полный ответ разбирается единственным reducer без global state вместо
набора global setters. Лента принимает snapshot явно и отвергает чужой target.
Каталоги/navigation, draft/inbox, HTML editor и runtime не переписывались.

Проверки `office-conversation-projection`: два source и два render inputs при
запрещённом доступе к global state; exact address/revisions, отсутствие копии
bounded history/metadata, cap 240, dispose/late delivery и сохранение reading
window после preference ACK. `run-view-state`, `conversation-snapshot-source`,
`office-context-projection`, `chat-run-completion`, `live-run-projection`,
`message-actions`, `stream-render-cost` прошли. Старые fixture dependencies
обновлены на реальные source accessors, вместо второго тестового selected state.
Source и render script cache queries обновлены.

Chromium `workspace-shared-draft`, `workspace-chat-progress` и `chat-inspector` прошли на изолированном
`d61a9aff` + Web diff: shared draft/preferences/receipts и stable live DOM сохранены.
В первом параллельном запуске draft fixture ожидал кнопку plugin chat до timeout;
повтор на том же production diff прошёл. Inspector fixture подключён к live run
вместо записи в read-only accessor. После отдельного падения focus/Escape проверка
прошла последовательно и на baseline, и на Web diff. Timing gaps не объявляются закрытыми.
Четыре Web-сценария `artifact-commit-projection` проходят; его отдельное static C#
ordering expectation падает и на `d61a9aff` с актуализированным fixture. Owner/gap
зафиксирован в RISK_REGISTER, весь этот test file не объявляется зелёным.

Ревью: `python3 prototypes/serve_ui.py`, `/preview/web/?scenario=ready`.
1. Office A → B → A: проверить историю, модель и контекст каждого чата.
2. Загрузить прежние сообщения, изменить модель и дождаться refresh: окно истории
   не должно перескакивать к последним сообщениям.
3. Проект/plugin source chat: проверить черновик и раскрытый reasoning при потоке.
Mock не квалифицирует Office/WebView2. Откат — Web diff, без миграции данных.

Далее — общий Chat View с explicit nodes/commands/lifecycle и переключение
оставшихся selected consumers. После этого — независимые chat targets в Dockview,
с проверками одновременного ввода/настроек/потока, close/move/restore и поздних ответов.
Selected facade и singleton DOM пока остаются; S2 и несколько chat tabs не завершены.

### Следующая контрольная точка S2: два работающих Chat View — In Review

Owner: Web conversation presentation / DockWorkspace. Уточнение способа выполнения
после замечания пользователя о темпе; исходная архитектура и остальные срезы плана
сохраняются. S2b.1–S2b.12 дали используемые owners/guards/components, но слишком
долгая последовательность подготовительных срезов не доставила независимые вкладки.
Единство реализации компонента не означает единственный экземпляр его DOM/controller.

Реализовано 2026-11-03 в production Web, без изменения runtime/Bridge/storage:

| Граница | Фактический результат |
|---|---|
| DOM/composer | Одна factory Chat View, scoped nodes, отдельные controllers Office/Workspace/plugin; прежний перенос form/messages удалён |
| Данные/команды | Прежние bounded sources, draft/receipt/run owners; exact target для отправки, edit, модели, истории, файлов, approvals и late delivery |
| Dockview | Per-object descriptor в существующем реестре, несколько видимых чатов, chooser targets, move/close/reopen/restore |
| Очистка | Удалены singleton composer binding, старые Workspace orchestration/edit handlers и Office inbox/history observer; view освобождает subscriptions/listeners/RAF |
| Временный остаток | Selected facade для вторичных HTML/material/model-context consumers; не отдельная копия чата. Gate уточнён в MIGRATION_MAP |

Проверки: `independent-chat-views` запускает shipped Web с mock bridge и проверяет
пару Office views, независимые inputs/model/reasoning, параллельную отправку,
сохранение history DOM, Workspace/plugin submit, edit/cancel, close во время run,
late read/preference ACK после reopen, изолированные ошибки, narrow/restore и
восстановление черновиков всех вкладок после перезапуска, закрытие всех чатов.
`conversation-composer`, `conversation-projection`, `conversation-observer`,
`conversation-draft`, `office-conversation-projection`, `model-navigation` проверяют
bounds 240, stale guards, ACK/draft ownership, hidden polling и отсутствие лишней
перестройки каталога моделей. Snapshot fixture переключён на explicit component
service; поздние reads проверяются теперь реальным Dockview-сценарием.
Mock прежде ограничивал Office reads/prefs выбранным чатом; приведён к существующим
адресным API. Он не доказывает Office qualification.

Старые browser fixtures, вручную монтирующие singleton chat по глобальным IDs,
требуют перевода на factory перед повторным использованием; новая integration
проверяет production boot/placement вместо этого устаревшего пути. Они не объявлены
прошедшими. `workspace-chat-progress` переведён на scoped nodes и ожидание animation frame:
для project/plugin 30 обновлений сохраняют live leaf, не перерисовывают ленту или
дерево; terminal snapshot убирает Stop и running badge. Проверка прошла.

Ревью: `python3 prototypes/serve_ui.py` → `/preview/web/?scenario=ready`.
1. Открыть два чата: справа/снизу → выбор чата. Написать разные черновики.
2. Изменить модель/начать правку в одном; второй должен сохранить свой ввод.
3. Отправить сообщения в обоих; переключить Office/Workspace/plugin.
4. Закрыть выполняющийся чат, вернуть через дерево; переместить/восстановить layout.

Откат — этот Web diff и mock fixture, без миграции данных. Старый layout без target
сбрасывается явно. UI остаётся In Review, не Approved; Windows/Office/WebView2
qualification открыта. Полный S2 не закрыт: очередь/steer capabilities Workspace,
живой HTML provider S3b.3c и остальные secondary consumers ещё требуют
адресной интеграции. Следующая работа — по найденному поведению и этой конечной
карте; отдельный подготовительный helper не объявляется переносом Chat View.

### Возврат закрытых вкладок и переключение чатов — In Review, 2026-11-03

Owner — Web chat navigation / существующий Chat View. Локально воспроизведены
возврат прежней закрытой вкладки перед выбором другой и повторное открытие после
catalog sync. Старый путь сначала показывал прежний conversation и ждал native
очередь; sync принимал backend active pointer за выбор пользователя.

Теперь чат текущего Office-документа активируется сразу; native очередь и проверка
другого документа сохранены. Поздняя selection связана с lifetime/generation,
промежуточный preview сохраняет загруженную ленту; sync/ошибка отправки обновляют
существующий адресный view. Нового store/registry/adapter нет, runtime не менялся.

Проверки: Chromium `chat-navigation-lifetime` — реальное дерево с удержанной native
очередью, закрытие всех вкладок, поздние preview/reply/read, новый Dockview focus,
клавиатура/600 px/125% zoom; `chat-sync` и `model-navigation` прошли.
В параллельном рабочем HTML diff удалён вызов `deactivateHtmlWorkspaceInspector`
из Materials: HTML view уже владеет lifecycle. Эта правка остаётся в HTML-срезе.
Повтор `independent-chat-views` проходит и в изолированном chat diff,
включая drafts/restore. Исправлена доставка selection event после синхронной
замены дерева: narrow overlay закрывается. CSS-zoom clipping, Windows/Office
latency и пользовательское визуальное принятие остаются открытыми.

Ревью: закрыть чат → выбрать другой в дереве → быстро переключить/закрыть его
во время загрузки → вернуть явно через дерево. Откат — обратный Web diff,
без миграции данных. Это исправление текущего среза, не завершение S2.

### Аудит параллельных чатов и производительности — In Review, 2026-11-03

Owner: Web Chat View/source adapters и общий NavigationTree. Исправлены повторная
полная отрисовка Office-дерева на фоновые tokens, обновление чужих composer/Inspector
и гонка Previous/Latest. Удалены скрытый `chatSessionSelect` и мёртвые глобальные
paging/send-controls aliases. Source/lifetime, bounded history и runtime остаются
у существующих owners; нового store, registry или bridge contract нет.

Локальная Chromium-проба: два Office-потока, 500 дополнительных строк каталога,
60 обновлений каждого потока с интервалом 20 мс. До изменения работа дерева заняла
738 мс за 2,4 с; после кеширования — около 6 мс за 1,3 с, DOM сохранён. Финальная
проверка также подтверждает 0 обновлений чужих controls/Inspector и ввод/выбор при
продолжающихся потоках и удержанной native navigation. Это синтетическая проба,
не Office latency/SLA; начальная постройка большого дерева остаётся синхронной.

Проверки: 15 адресных JS-файлов; Chromium `parallel-chat-navigation`,
`chat-history-navigation`, `chat-inbox-observer`, `chat-navigation-lifetime`,
`independent-chat-views`, `active-chat-runs`, `workspace-chat-progress`,
`chat-tree-layout` проходят. Старые singleton polling/paging assertions заменены
реальными Chat View, pure receipts/bounds оставлены. Перед коммитом отдельно
проверен его изолированный состав: три новых browser checks,
`chat-navigation-lifetime`, `independent-chat-views`, `shared-navigation-tree` проходят.
На общем рабочем дереве с незавершённым переносом редакторов получен отдельный
pageerror `syncSelectedSkillFromEditor` при уходе из Library. [Открытые ограничения](../stabilization/RISK_REGISTER.md#аудит-чатов-оставшиеся-ограничения--2026-11-03)
не закрыты успешными чат-проверками. Native код проверен чтением; Office/VSTO,
Windows/WebView2 и живые модели не запускались.

Ревью: запустить `python3 prototypes/serve_ui.py`, открыть `/preview/web/?scenario=ready`.
1. Разместить два чата рядом, отправить оба, выбирать строки дерева и вводить текст.
2. Закрыть работающий чат и вернуть через дерево; соседний draft/focus сохраняются.
3. В длинной истории быстро нажать Previous → Latest → Previous.
4. В узком окне выбрать чат клавиатурой: overlay закрывается.
Откат — обратный Web diff без миграции данных; UI остаётся In Review.

### Общее приветствие по Demo — In Review, 2026-11-03

Owner: Web Messages / ConversationView / общие chat tokens. По запросу пользователя
перенесены иконка, типографика и spacing P2 (`3e2433c9`) в существующий renderer.
Удалены отдельные dark/narrow styles и оформление кнопок подсказок; они используют
общий Button. Низкий split больше не сжимает приветствие с недоступным верхом;
empty follow начинается сверху, после сообщения действует прежний follow.

Сравнение Chromium при 1280×900: Demo/production имеют одинаковые 29 px иконки,
20 px заголовка, 440 px ширины, padding 32/16 px и позиции иконки/заголовка/текста.
Office/Workspace/plugin проверены вместе, включая низкую ленту, адресную подсказку,
light/dark, 420/360 px, font 130% / CSS zoom 125% и переход к первому ответу.
`conversation-view`, `bridge-bootstrap`, `independent-chat-views` прошли;
новых bridge/storage/runtime paths нет. Windows/Office/WebView2 evidence открыт.

Ревью: `/preview/web/?scenario=ready` → пустая «Сводка по регионам» → разделить
область снизу и прокрутить приветствие → нажать подсказку и отправить сообщение.
Откат — Web diff этого приветствия и cache keys, без миграции данных.

## Последующие срезы — только после ревью предыдущего

| Срез | Текущее состояние / остаток | Что удаляется после проверки |
|---|---|---|
| S1 / S1c | Shell, Dockview, Inspector, Settings и rail реализованы; эталон Demo сохраняется | Старый Shell/Inspector placement заменён; повторный перенос не нужен |
| S2 | Независимые Chat View реализованы; остаток — parity действий и secondary consumers, перечисленные выше | Оставшиеся selected adapters; без второго durable store |
| S3a | Запрос и Диагностика в Dockview реализованы | Текущие дефекты/review исправляются адресно, не новым переносом |
| S3b.1–3b.3b | Общая навигация, независимые материалы, сохранённые HTML/plugin версии и Browser реализованы | Заменённые inline previews и старые splitters уже удалены |
| S3b.3c | Plugin instances реализованы ниже; остаётся scoped live HTML editor и адресные аннотации, dirty/save/close/stale guards | HTML singleton и оставшиеся selected annotation consumers |
| S4 | Осталось: Library/VBA/project views и редакторы через существующие registry/NavigationPane; cold-start mode/search/scroll | Заменённые editor-selection paths и legacy routes |
| S5 | Финальная сверка и удаление оставшихся adapters; plugin views/export/public API — отдельный явный scope, не условие завершения Chat View | Мёртвые зависимости/overrides после проверки consumers; без новой платформы или SDK |

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

Остаток на момент S3b.1: независимые viewers и bounded/cancellable reads (реализованы
в S3b.2 ниже); единый Browser HTML/plugin и iframe lifetime — S3b.3; предметные
Library/VBA редакторы и остальные adapters — S4. Старый singleton HTML/editor
сохраняется до своего среза. Move/split/restore новых object views проверен в S3b.2.
Откат среза — его UI diff, без изменения durable данных или product version.

### S3b.2: независимые просмотры материалов — In Review, 2026-11-02

Разрешён пользователем после `20b11dc0`; реализован следующий законченный срез:

- Один catalog DockWorkspace: descriptor содержит identity, factory, lifecycle,
  maximum instances и restore validation. Временный material target route заменяется
  per-object factory; Library route остаётся до S4. Source layouts подключаются
  через descriptor; Shell не получает веток по форматам/названиям источников.
- Каталог Материалов — singleton view в том же Dockview; его navigation остаётся
  слева. Markdown/JSON/text/image/PDF/task/sequence — отдельные exact-target views.
  Текущий HTML preview/editor остаётся явно ограниченным adapter до Browser S3b.3.
- Source adapter передаёт actor chat, document, exact URI, title/kind/mime. Чтение
  идёт через существующие ArtifactViewerActions / ResourceDataPlane; typed payload
  получает documentId для другого сохранённого документа. UI не подменяет scope
  активным чатом, не создаёт второй reader/store и не включает объект в контекст.
- Budget: до 6 открытых material views; текст — существующие страницы 32 000 chars
  и полный readable до 512 000 chars по запросу; image preview до 8 MiB, PDF page
  до существующих 10 MiB, bounded thumbnails. Достижение лимита явно показано.
  Hidden views отменяют незавершённые чтения; готовый DOM сохраняется. Close удаляет
  instance и освобождает readers/leases/URLs. Layout хранит только target, без тел и
  lease IDs. Move/split/restore в текущем окне сохраняет живой view и его состояние.
  После полного перезапуска восстанавливается target, формат/поиск/scroll начинаются
  заново: сериализуемые mementos специализированных viewers остаются отдельным gap.
  Невалидный target не заменяется текущим.

Макет использует существующие Dock tabs, viewer controls и source navigation:

```text
Материалы [поиск] [+] | [Каталог] [Отчёт.md] [Данные.json] | [Методика.pdf]
scope / дерево      | документ · сохранённая версия     | документ · версия
                    | формат / исходник / действия      | страницы / текст
                    | viewer / loading / error+retry    | viewer
```

Маршруты gate: открыть MD и PDF → вернуться без сброса; из чата открыть материал →
сменить чат → исходный target; повторно открыть URI → та же вкладка; другая версия
→ другая вкладка. Проверить narrow/themes/keyboard, move/split/restore, late replies,
close во время lease open, лимиты и повторные чтения на одинаковых fixture данных.

Проверка: `material-object-views` проходит реальные Dockview/typed-reader маршруты
на synthetic bridge, включая отдельные версии, chat card → уже открытый объект,
смену чата, 780 000 chars/17 переходов страниц, 6 views, late lease close, JSON/task/
collection, URL cleanup, restore, focus, light/dark 1280/420 и zoom 125%.
`dock-workspace`, `artifact-catalog-layout`, `artifact-text-viewer`,
`artifact-json-viewer`, `artifact-working-set` покрывают прежние consumers и guards.
Harness: `bridge: typed artifact viewer`, `artifact viewer:`,
`artifact transfer: original and authored files` — typed document scope и exact body
из другого локального документа. Office/VSTO и реальные модели не запускались.

Визуальное ревью: [wide/light](review/s3b2-light-1280.png),
[narrow/dark](review/s3b2-dark-420.png). Компоновка использует existing Dock tabs,
ActionMenu, ResourceEditor header и format controls; секционные CSS prefixes убраны
у переносимых компонентов. Исправлен MutationObserver loop: несколько JSON toolbars
получают иконки в пределах собственного toolbar, а не общий порядковый индекс.

Измерение `material-views-performance.test.js`, baseline `20b11dc0`, одинаковые
fixture и Chromium 1280×900: [числа и метод](review/s3b2-performance.json).
На 20 переключениях MD/JSON: body reads **20 → 2**, p50 **49,1 → 32,7 ms**,
p95 **50,2 → 46,4 ms** (select → mounted viewer → два animation frames).
Это локальный synthetic sample, не гарантия ускорения Office. Цена сохранения двух
view DOM: **42 → 391 nodes**. Для исходника 780 000 chars отображается одна страница
32 000 chars, а не всё тело; DOM source text с номерами строк **948 899 → 37 047 chars**.
Готовность этого просмотра в sample **75,2 → 27,7 ms**; DOM **18 → 35 nodes**.
Лимиты и снижение повторных запросов подтверждены; уменьшения общего DOM не заявляем.

Удалены non-HTML ветви singleton catalog preview/refresh, отдельный collection
renderer и material section target adapter. Shared ArtifactViewers используется
object view и оставшимся HTML-editor detail. Команды attach/use/edit/copy/delete
имеют один owner Catalog, обе точки входа получают одну реализацию.

Далее **S3b.3 Browser**: один HTML/plugin surface, отдельный общий чат, source
providers, sandbox/authority, iframe identity при move/restore. Текущий HTML editor
и preview сохраняются до этого gate; Library/VBA editors и остальные навигационные
панели — **S4**, с теми же NavigationPane и view descriptors. Cold restart viewer
mementos, реальный WebView2/Office и прежние diagnostics qualification gaps не закрыты.

### S3b.3a: общий Browser host и раздельное размещение — In Review

Разрешён пользователем «Далее» после `75c59e88`. Общий renderer `browser` расширяет
PreviewSurface/ViewerRegistry: provider владеет созданием содержимого, чтением,
sandbox, reload и dispose; renderer — стабильным surface, видимостью и размерами.
Iframe остаётся подключённым к одному DOM parent вне перемещаемого Dock subtree;
его место соответствует placeholder view. Это портал отображения, не второй layout
engine: геометрию задаёт существующий Dockview. Layout/drag/resize/restore не меняют
src/srcdoc и не выполняют reload. Native SDK/transport/resource builders сохраняются.

Первый законченный шаг подключает существующие HTML editor preview и plugin owner
как отдельные Browser views; удаляет ChatBrowser и plugin splitter/overlay placement,
а также смешивание Preview/Inspector в одном переключателе. Общий чат остаётся своим
view. Plugin workbench пока имеет один selected source и не клонируется: factory
per plugin/release и exact saved HTML из Catalog — следующий S3b.3b. Source change
не должен незаметно превращать чужой preview в другой объект; текущая зависимость
live HTML editor от выбранного чата явно показывается и остаётся до scoping owner.
Это не завершение всего S3b.3 или перенос Library/VBA editors S4.

Макет: `source navigation | [Чат] [HTML] [Плагин] | [Инспектор]`;
Browser содержит общие location/status/reload и provider actions. Смена Inspector
не прячет Browser; close Browser не останавливает разговор. Narrow — те же вкладки.
Гейты: iframe document marker и пользовательский ввод сохраняются при move/split/
restore/смене source layout, close/reopen освобождает/создаёт provider instance;
stale opens, annotation binding, full keyboard/focus, light/dark и 420/1280/125%.
Реализовано: существующие providers зарегистрированы в одном Dock catalog;
плагин открывается без смены чата, «Чат исходников» — отдельная команда. Старые
ChatBrowser JS/CSS, их DOM/splitters и Preview-пункт Inspector удалены. HTML saved
Catalog/detail adapter сохранён до S3b.3b; он не переводится молча в другой live HTML.

Проверено synthetic Chromium: browser-views (HTML/plugin annotations, точный адрес,
5 restores, реальный drag, смена Office/Workspace/Материалов, late close, error,
420/1280, light/dark, 125%, keyboard entry), dock-workspace и material-object-views.
VersionFormat пройден; реальные модели/Office/VSTO не запускались. Широкие старые
plugin/annotation suites не объявляются полностью зелёными; новый адресный fixture
покрывает именно изменённые маршруты и lifecycle.

[Измерение одинакового iframe](review/s3b3a-browser-lifetime.json): пять restore
при прямом встраивании дают 6 загрузок и сброс ввода; общий Browser — 1 загрузку,
ввод сохранён. Это воспроизведение прежнего механизма в том же Dockview, не замер
всего старого приложения. Медиана UI-действия 85,9/82,3 мс включает automation;
ускорение приложения не заявляется. Ограничение: два singleton provider surfaces,
hidden plugin не poll-ит; новые independent provider factories — S3b.3b.

Ревью: запустить `python3 -B prototypes/serve_ui.py --port 0`, открыть указанный
`/preview/web/?scenario=ready`. (1) Открыть плагин, проверить сохранение текущего
чата; (2) «Чат исходников», затем вкладка плагина; (3) переместить Browser, сохранить
и вернуть раскладку; (4) проверить узкую навигацию и общую вкладку Inspector.
[Wide/error](review/s3b3a-light-1280.png), [narrow/dark](review/s3b3a-dark-420.png).
Следом S3b.3b: scoped HTML/plugin targets и независимые source factories; затем S4
Library/VBA/navigation consumers. Cold restart source state, WebView2/Office и
предыдущий Diagnostics Qualification Plan остаются открытыми.

### S3b.3b: точные сохранённые Browser targets — In Review

Следующий инвариант: выбор материала/версии открывает адресный view, а не заменяет
страницу текущего редактора. HTML target — `(chatId, documentId, resourceUri)`;
plugin target — `(pluginId, releaseId)`. Повторный выбор активирует тот же view.
Заголовок показывает источник и версию; смена чата не подменяет target.

Маршруты и макет до реализации:

```text
Материалы / дерево → [Чат] [Отчёт · v1] [Отчёт · v2]
  Отчёт · v1 | документ / чат / точная версия             ↻ ⋯
  Сохранённый проект · просмотр без связи с Office
  [Страница] [Исходник]    [index.html ▾] (только в исходнике)
  ┌ страница sandbox ИЛИ общий TextViewer по 32 000 символов ┐

Плагин / Версии → [Разработка] [Плагин · v1] [Плагин · v2]
  Плагин · v1 | releaseId                                 ↻ ⋯
  Сохранённая версия · пробная копия данных
  ┌ существующий plugin instance / sandbox ┐
  ⋯ → явное восстановление с expectedActiveReleaseId
```

Общий Browser object lifecycle отвечает только за lazy load, status, отмену,
поздний результат, retention и dispose; Browser renderer — за placement. HTML
provider использует exportArtifact/resource lease и существующий HTML builder;
plugin provider — openPlugin/closePlugin. Новый store, runtime и registry не нужны.
HTML snapshot encoder перенесён из Catalog к HtmlResourceExport. Исходник использует
общий TextViewer; DOM страницы не пересоздаётся при выборе файла или режима.
Catalog сохраняет навигацию/редактор и предметные команды; plugin workbench —
изменяемые исходники, проверку и публикацию. Старые singleton saved previews удалены.

Границы: до 6 material views (включая HTML), до 4 release views; HTML preview до
16 МиБ transfer, исходник по 32 000 символов. Скрытие отменяет незавершённое чтение,
готовая страница сохраняется; close закрывает lease/instance, в том числе поздний.
Ошибка и отмена видимы, повторное чтение — явно. Открытие не запускает модель.

Отдельно остаётся mutable owner текущего HTML editor/annotations и plugin workbench.
Runtime разрешает HTML-аннотацию только для активной версии текущего чата; saved
view не получает это право. Следующий срез scoping live providers должен сохранить
эту authority и адресные annotation routes. S4 Library/VBA editors этим не закрывается.

Реализовано без новых bridge contracts: exact targets используют действующие typed
exportArtifact/openPlugin/closePlugin/restorePluginVersion. Переключены дерево, таблица,
карточки материалов и строки версий. ActionMenu trigger и material download общие;
старые Catalog HTML render/read/CSS и historical mode workbench удалены.

Проверки: `browser-saved-views` — snapshots RN.resources, разные documents/targets,
retained iframe/input и source mode, restore, exact scope после смены чата, 16 МиБ
отказ, late lease/instance close, CAS refusal/success, отсутствие model calls,
420/1280, light/dark, 125%, клавиатура/меню. `material-object-views`,
`artifact-catalog-layout`, `artifact-working-set`, `resource-download` проходят.
`browser-views` имеет успешный isolated run, но повторение нестабильно; это не закрытый
Shell gate. Повторный `dock-workspace` прошёл.
Старые широкие suites имеют отдельные gaps, перечисленные в RISK_REGISTER.

[Парный замер](review/s3b3b-saved-browser.json): пакет 3 968 476 bytes, пять выборов
A/B/A/B/A. HTML reads 5→2, plugin opens 5→2; ввод после возврата `initial→retained`.
Исходник в DOM 2 240 000→32 000 chars. Median selection HTML 165.73→148.95 ms,
plugin 133.40→166.54 ms; source action 37.58→40.05 ms. Timings включают automation;
общего ускорения и сокращения heap не заявляем. Готовые вкладки намеренно удерживают
страницы (ограничены числом views), что дороже singleton по памяти.

[Исходник, wide](review/s3b3b-source-wide.png) · [Лимит, narrow/dark](review/s3b3b-bound-narrow.png).

Ревью: (1) открыть два HTML в Материалах; (2) выбрать CSS в «Исходнике», вернуться
к странице; (3) открыть две опубликованные версии плагина; (4) вернуть сохранённую
раскладку; (5) проверить меню восстановления и его причину отказа при конфликте.

Далее **S3b.3c**: scoped live HTML/plugin providers и адресные аннотации. Сначала
провести общее Shell/Browser ревью после параллельных UI edits; затем переносить mutable owner.
**S4**: Library/VBA и другие редакторы через тот же catalog и NavigationPane;
cold-start mode/search/scroll остаётся отдельным gap. Новый UI не закрывает прежние
Office/endpoint qualification gates.

### S3b.3c: plugin instances и замечания по фото — In Review, 2026-11-03

Scope: независимый live plugin по pluginId; rail selection/оформление SVG;
документ/source-chat группировка и одно меню строки Материалов; общий редактор
исходников без внешних рамок/отступов. Owners: Web ShellLayout, Plugins/PluginView,
ArtifactCatalog, CodeEditor; Core отдаёт provenance существующей retained record.
Singleton plugin-browser удалён. Продолжение live HTML source/annotations и S4a
placement описано в начале документа; независимые Library/VBA objects остаются в S4.

Baseline: `browser-saved-views` до изменения — HTML 2 reads и plugin 2 opens при
A/B/A/B/A; готовый ввод сохраняется. После изменения bounds и эти счётчики прежние.
Проверены Chromium: `plugin-view-selection` (два live plugin, rail, retained frame,
move/restore, адресные аннотации, поздний close, SVG/палитра, 420 px / 125% / dark), `browser-saved-views`,
`artifact-catalog-layout`, `shared-navigation-tree`, `html-workspace-layout`,
`tool-library-layout`; pure text/JSON/working-set и
vendor gate. Один host-neutral Harness case проверил provenance между чатами и
stale session/writer guards. Windows/Office/WebView2 остаётся открытым evidence.
При продолжении S3b.3c Browser/annotation fixtures переключены на адресные views
и keyed drafts. Hit testing и повторный выбор исправлены в общем annotation owner;
актуальные проверки и оставшиеся ограничения перечислены в начале документа.

Локальный запуск: `python3 -B prototypes/serve_ui.py --port 8877`,
`http://127.0.0.1:8877/preview/web/?scenario=ready`.
Ревью: (1) открыть два плагина, вернуться по вкладкам/rail;
(2) «+» или оформление плагина: одно название сверху, палитра раскрыта, переключить
цвет; импорт раскрывает отдельное название SVG, после добавления сворачивается;
(3) Материалы → документ/чат → «…», проверить разные действия удаления/исключения;
(4) HTML → «Исходник», сменить файл/тему, проверить узкую ширину и возврат к странице.

### S3b: общая навигация и независимые просмотры — Proposed

Пользователь сначала разрешил отдельный plugin preview, затем потребовал zoom out:
Материалы/Библиотека/будущие Файлы используют общую навигацию; HTML/plugin — общий
browser; остальные форматы — независимые экземпляры действующих viewers.
Прежний plugin-only порядок заменён. S3b.1 и S3b.2 реализованы и проходят ревью;
Browser S3b.3a, сохранённые providers S3b.3b и live scoping S3b.3c реализованы host-neutral; пользовательское ревью и Windows evidence открыты. Исходный audit/iframe probe — `c01daa34`;
S3a завершён (`c01daa34`),
S2a model/capability work имеет свой scope и не смешивается с этой миграцией.

[Границы и owners](UI_ARCHITECTURE.md#единая-навигация-и-вкладки-объектов--проектирование-2026-11-02),
[маршруты и схема экрана](UI_SCREEN_MAP.md#общая-навигация-и-независимые-объекты--proposed-2026-11-02),
[общие элементы](UI_DESIGN_SYSTEM.md#общая-панель-навигации-и-object-views--proposed-2026-11-02).

| Порядок | Законченный результат | Удаление и проверка |
|---|---|---|
| S3b.0 — общая модель, план `535da5b3` | Source/object/commands/view type/view instance/renderer, матрица возможностей и группировка всей карты по семействам UI; явная точка подключения в существующие каталоги | Не начинать с копирования конкретного экрана. На трёх маршрутах проверить ownership, identity, dirty/close/restore и отсутствие особых условий по имени source в Shell |
| S3b.1 — навигация и команды | Общий каркас на существующем NavigationTree для Материалов и Библиотеки, adapters источников; один open route в нынешний DockWorkspace. Открытие отделено от attach/use/edit/delete | Заменённые headers/search/menu handlers и CSS удаляются. Два реальных consumers; одинаковые focus/selection/empty/error/paging; открытие не меняет контекст чата |
| S3b.2 — самостоятельные object views, In Review | Exact target и local presentation state на вкладку; текущие Markdown/JSON/text/PDF/image/task/sequence renderers. Несколько разных материалов одновременно; source/revision видимы | Non-HTML singleton catalog preview удалён; HTML adapter остаётся до S3b.3. Bounded lazy reads, cancel/stale reply, leases; смена активного чата не подменяет открытые объекты. Нельзя клонировать global IDs/active-selection controllers |
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

### Фото: внешний скролл Shell — 2026-11-03

Owner: Web Shell chrome. Воспроизведён вертикальный overflow 6 px: общий
`min-height: 34px` кнопки диагностики превышал строку StatusBar 24 px.
Снят минимум у controls этой строки, обновлён CSS cache key; размеры 24/23 px
совпадают с Demo при одинаковом viewport. Прокрутка материалов сохранена.
Chromium/mock: шесть основных разделов, пять групп, 360/420/768/1280 px,
light/dark, UI font 100/130% и CSS zoom 125% — внешний overflow 0;
`material-object-views` прошёл (move/restore, late cleanup, format/scroll/focus).
Найденное [обрезание низкого пустого чата](../stabilization/BACKLOG.md#низкая-панель-пустого-чата--2026-11-03)
исправлено в срезе общего приветствия выше.
Ревью: `/preview/web/?scenario=ready` → длинный Markdown → разделить область
→ уменьшить окно: прокрутка документа доступна, StatusBar остаётся на месте.
Откат — правило StatusBar и cache key. Windows/Office/WebView2 остаются непроверенными.

### Исходная проверка S1b

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
