# Дизайн-система и компоненты RNAssistant

## 07 Inspector scope — In Review, 2026-11-13

Use the existing Inspector section switcher, status/info/disclosure and action controls.
A compact scope row uses shared spacing/type tokens and wraps long chat/run labels;
its explicit follow command separates live selection from a historical pin. Removed
duplicate timeline row styles. [Wide](review/inspector-selected-wide.png) and
[420px/125%](review/inspector-selected-narrow.png) show chat B with pinned evidence of
chat A. They are Chromium UI evidence, not Windows/Office acceptance.

## Browser find row — In Review, 2026-11-13

Find uses common form/icon controls, spacing/type tokens and an inline status below
its field. It occupies layout space above the native viewport, not an overlapping
second toolbar. [420px/125% fixture](review/browser-find-narrow.png) verifies narrow
geometry; native Windows focus and rendering remain separate evidence.

## Native annotation presentation — In Review, 2026-11-13

Reuse PreviewSurface's selection control, shared annotation markers/editor and accent
palette. No provider-specific toolbar or geometry. [Wide](review/browser-annotations-wide.png)
and [420px/125%](review/browser-annotations-narrow.png) verify the common Shell overlay;
the empty viewport is a native fixture, not Windows rendering evidence.

## Export menu labels — In Review, 2026-11-13

Use the existing ActionMenu/status surface for autonomous HTML and source ZIP;
explicit labels communicate different outputs. No additional dialog or toolbar.
The download status reports preparation, without claiming an unverified disk write.

## Interactive Browser layout — In Review, 2026-11-13

Native content occupies the common preview content slot. The manual-control strip
uses shared spacing/type/border/button tokens and wraps in narrow layouts. The
existing action cursor component is reused in the isolated page world; native input
attribution owns its human-takeover dismissal. [Wide](review/browser-interactive-wide.png)
and [narrow/125%](review/browser-interactive-narrow.png) show the Shell with an empty
native slot; they are placement evidence, not a rendered Windows page or acceptance.

## Agent open presentation — In Review, 2026-11-13

Agent opens reuse the common Browser chrome, focus and source-only status. No new
visual component or synthetic pointer is added while native execution is unavailable.
Visible Dock bounds are acknowledgement evidence, not a viewport capture permission.

## Workspace transfer controls — In Review, 2026-11-13

Workspace snapshot actions use the existing page menu, status and import controls;
no provider-specific geometry was added. The Workspace fixture checks the actions at
420 px/125%. The F4b source-action screenshots remain the component reference.

## Browser provider controls — In Review, 2026-11-13

All provider routes use PreviewSurface tokens and the common toolbar, including raw
Create project and persistent Save. The source editor no longer has parallel Browser
chrome. At 420 px/125% the address occupies its own row and actions use the existing
page menu. [Source actions wide](review/browser-provider-wide.png),
[narrow](review/browser-provider-narrow.png). Synthetic review does not qualify native
input/rendering and is not user acceptance.

## Shared StartSurface — In Review, 2026-11-13

Chooser/Home use one token-based title, search, section and entry component; entries
have an icon, title and secondary source/location. Existing Demo chooser width,
spacing, search/title metrics and control geometry remain; group chrome now has two
buttons. Search supports arrows/Enter/Escape and retains stable focused entries on
refresh. [Wide](review/browser-home-wide.png), [420 px / 125%](review/browser-home-narrow.png).

## Checkpoint controls — In Review, 2026-11-13

Общие compact Save (shared save icon), clock и Apply используют PreviewSurface и
ActionMenu; корзина — sibling action строки, не вложенная кнопка. Save всегда видим,
включая disabled с причиной. В narrow toolbar адрес переносится на полную ширину,
Save остаётся вне скрываемых secondary controls. Общие tokens; дальнейшее сокращение
toolbar — 07. [Wide](review/browser-versions-wide.png), [420 px / 125%](review/browser-versions-narrow.png).


## Local Browser combobox — In Review, 2026-11-13

PreviewSurface расширен local combobox: Up/Down/Enter, Escape/blur без перехода,
поздние responses отбрасываются. Copy копирует committed locator. Список использует
общие tokens, ограничен 20 metadata rows. Новый Browser использует прежний portal
и `onlyWhenVisible` Dock renderer; hide/move сохраняют owned view/session.
Source-only refusal использует общий status, без нового визуального слоя.


## Local-only Settings — In Review, 2026-11-13

Пустой «Разработчик» использует прежний advanced disclosure; новых tokens,
геометрии и overrides нет. Удалены network policy controls. Shared Settings
просмотрены при 1280×900 и 420×900/125%; пользовательское принятие открыто.
Старое Developer authoring описание ниже superseded.


## Developer settings — In Review, 2026-11-12

Общие product-settings-section, form-grid, advanced details, label/input/textarea,
checkline, status и action-group; дополнительных CSS размеров/overrides нет.
После раскрытия Developer до входа показывает пароль; после unlock — список сайтов, manual checkbox,
путь private key (default из сборки, редактируемый), readonly путь конфига и версия политики, save/folder/close.
Обычный индикатор не раскрывает адреса/пути. Ошибка остаётся рядом с редактором;
закрытие очищает поля. Shared metrics и wide/narrow/125% проверены в
`deployment-config.test.js`; Windows/визуальное принятие открыты.
Отдельная Windows key utility использует стандартные WinForms controls;
её DPI/layout acceptance требует запуска на Windows.


## Общее дерево и меню добавления — In Review, 2026-11-11

Заголовки навигации и документа/проекта — обычное начертание 400. Секции Office и
Проекты используют общую строку и отступы; высота строки сохраняется как в Demo.
NavigationTree выравнивает подписи чатов под родителем через общие geometry tokens.
Folder glyph перерисован с мягкими углами/двумя плоскостями; папки проектов берут
`--accent`. Group/ellipsis и glyph диагностики едины в обоих источниках.

У строки один слот «…», постоянно учитываемый в геометрии. Кнопка появляется при
hover/selection/focus; текст больше не закрывается полосой действий и выделение
не получает второй фон. Длинные названия сохраняют tooltip/marquee и reduced-motion.
ActionMenu сохраняет иконки переданных команд, одинаковую сетку строк, muted
заголовок Office, разделители и danger-цвет удаления. Меню под «+» использует тот же
компонент с ограничениями viewport/zoom, Escape и возвратом фокуса.

## External Browser P5a — In Review, 2026-11-11

External view использует PreviewSurface, address/navigation, compact controls и tokens.
Диагностика — disclosure с shared summary; нового toolbar/theme нет.
`onlyWhenVisible` удерживает DOM в existing parking и избегает повторного scale в Dock
при 125%. Над menu/drag нативная страница временно скрывается, её состояние сохраняется.
Shell 1280/100% и 420/125% просмотрена с synthetic native; реальные HWND overlays и
визуальное принятие открыты. [Проверка](../browser/05_TEST_MATRIX.md#p5a).
P6a добавляет read binding без изменения presentation/tokens/controls; existing
External Browser fixture сохраняет narrow/125% coverage. Нового визуального слоя нет.

## Browser: ручное управление — In Review, 2026-11-11

`external-browser-control` использует общий secondary button и existing spacing/type
tokens; строка flex-wrap переносится при сужении. Текст «Вы управляете» имеет
`role=status`, действие «Продолжить агентом» доступно с клавиатуры и disabled пока
ожидает ответ. Никакой блокировки ручного ввода страницы или нового modal overlay.
Settings используют прежние label/textarea/help/save components, без отдельного
pattern editor. [Wide/narrow review](../browser/05_TEST_MATRIX.md#p6d).

<a id="browser-action-visualization"></a>
### Визуализация действий Browser — In Review, 2026-11-11

P6c использует небольшой зелёный SVG-курсор с контрастной обводкой/тенью, outline
actual target и компактную текстовую метку. Геометрия и motion принадлежат
`app-browser-action-view.css/js`; palette/type/spacing берутся из общих product tokens
(базовая light palette), встроенных в closed shadow root native page.

Движение к цели — 220 ms с ease-out, click pulse — 360 ms. После 2500 ms без действий
декорация исчезает за 180 ms и удаляется из DOM; новое действие начинает новый показ.
Trusted pointer/input/keyboard, scroll/resize, hide/navigation убирают её сразу.
Системный курсор не перемещается. Reduced motion: статическая отметка, без pulse,
движения и ожидания анимации. Timings определены один раз в component CSS.

«Заполняю»/«Нажимаю» сменяется на «Проверяю»; «Проверено» появляется только после
runtime terminal publication. Rejected/Unknown имеют текстовые статусы и warning
цвет. Метка ограничена viewport, не содержит значений полей, не забирает pointer/focus
и не включается в semantic capture. [Контракт](../browser/03_CONTRACTS.md#p6c-action-visualization).
[1280/100%](review/browser-action-wide.png), [420/125%](review/browser-action-narrow.png).

## Native window — In Review, 2026-11-11

Используется существующий `workspace-topbar`, без нового бренда и второго header.
Группа оконных действий берёт `--shell-control`, `--shell-icon`, spacing/focus/colors
общих components. `--shell-window-drag-width` сохраняет свободный drag участок.
Геометрия header/control сверена с frozen Demo при 1280×900/100%: 40/28 CSS px.
Controls доступны при 420×520/125%; минимальная Compact проверена при 360×320/125%.

В header остаются только меню окна, Compact/Companion и × с отдельной границей от
действий Dockview. Pin и maximize/restore перенесены в меню; значки Compact и
Companion различаются. × означает полное закрытие сеанса с остановкой запросов и
сохранением черновиков. Pin не означает TopMost. Второстепенные Dock
действия в Compact/narrow перенесены в тот же ActionMenu. Compact сохраняет общий
composer/ленту; другие элементы Shell и group header временно скрыты владельцем.

Trusted header использует app-region drag при `nativeDragRegions`; иначе свободная
область header вызывает native move через typed bridge. Интерактивные элементы
не инициируют перенос. Caption исключён из стиля формы до первого показа и не
возвращается при старом Runtime/сбое root UI. Web-кнопки появляются после ChromeReady
от смонтированной Shell независимо от поддержки drag regions. Hover Snap не
имитируется и не заявляется. Companion — native 48 DIP tool window со стандартными
системными цветами, коротким статусом и tooltip; без второго мини-чата/анимации.
Конкретный внешний вид/Win32 delivery остаются In Review.

[Compact](review/window-compact.png), [Full](review/window-full.png),
[замеры рядом с Demo](review/window-chrome.json). Реальное Excel/Windows ревью после
уточнений 2026-11-12/13 открыто.

## Предел параллельности и очередь — In Review, 2026-11-05

Переиспользуются number input, label/grid и activity row, без новых размеров и CSS.
Поле проверено внутри actual Dock content при 1440/100% и 420/125%; длинная подпись
переносится. [Широкий](review/admission-settings-wide.png) /
[узкий](review/admission-settings-narrow.png) вид. Frozen Demo не содержит этой настройки
и остаётся неизменным. Queue caption не создаёт новый стиль сообщения или автора.

## Поле максимума агентов — In Review, 2026-11-05

Переиспользуется существующий number input раздела «Агент», общие field/grid/tokens.
Новое оформление не вводится. Удалён неиспользуемый `.agent-communication-status`;
подпись отправителя и оформление входящих сообщений сохранены. Поле проверено при
1440/100% и 420/125% внутри реального Dock content. Screenshots:
[широкий экран](review/participant-limit-wide.png), [узкий](review/participant-limit-narrow.png).
Frozen Demo не содержит этого production-поля и не изменяется.

## Размер Settings — исправление, In Review, 2026-11-05

Settings получает геометрию от Dock content без дополнительного CSS zoom overlay.
Tokens, spacing, размеры полей и кнопок не меняются. Проверяется rect всей формы
относительно реальной Dock-группы, а не только overflow внутри увеличенной формы.

## Controls сохранённого Run — In Review, 2026-11-04

Одна строка `inbox-pause` и существующий `inboxButton`, без новых CSS/карточек/tokens.
Resume и cold Stop имеют одинаковые computed min-height 30 px, padding 5 × 8 px,
текст 12 px; при 420 px / 125% подпись переносится, кнопки остаются внутри Dock content.
Геометрия прежнего компонента сохранена; у frozen Demo нет соответствующей pause row.
[Замеры](review/agent-run-controls.json). Состояния передаются текстом и native disabled,
без дополнительных badges; runtime IDs остаются в exact command, не в видимой строке.
Оформление одинаково для Office/Workspace/plugin; Windows/WebView2 ревью отдельно.

## Размер Chat View — исправление, In Review, 2026-11-04

Геометрия ленты/composer берётся из content-container Dockview. Повторного применения
CSS zoom к retained overlay нет; размеры bubbles, шрифты и tokens не менялись.
Проверяются реальные bounds root/messages/composer относительно своей группы, а не
только отсутствие overflow внутри уже неверно увеличенного root.

## Выбор участников для продолжения — In Review, 2026-11-04

Срез использует существующий composer menu и portalled ActionMenu, без нового dialog,
карточек или геометрии. Общий ActionMenu поддерживает menuitemcheckbox (Space/Enter),
сохранение меню при выборе и строку title/detail. Последняя переиспользуется списком
активных запросов; прежние его layout overrides удалены. Размеры, spacing и типографика
остаются общими tokens. Заголовок сокращается ellipsis, detail переносится в узком
окне; полный текст доступен в title. Цвет не является единственным состоянием.
Escape возвращает фокус к «+», close view убирает portal. Windows/WebView2 и принятие
визуального вида пользователем остаются отдельными gates.
Native composer picker и recovery portal взаимоисключаются: «+» всегда возвращает
прежнее меню действий, без перекрывающего списка участников.

## Universal Browser controls — In Review, 2026-11-05

Общий PreviewSurface использует `icon-btn compact`, `view-switch`, `rna-form-control`
и существующий RNAssistantIcons. Страница — `play`, исходник — `code` (`</>`),
аннотация — `selection`, версии — `clock`, разделы — `layers`, обсуждение — `chat`.
У каждой иконки есть title/aria-label; выбранный режим и выбор элемента имеют
aria-pressed. Размеры, hover/focus/active принадлежат общим components/tokens.
Слева: назад, вперёд, reload, selection. Затем округлый адрес; справа — provider
controls. Публикация плагина — upload icon с tooltip; сведения — layers для CAS,
folder для файлов. Все кнопки используют общую compact геометрию.

Редкие действия плагина — общий ActionMenu `⋯`; версии открываются через clock.
История HTML и plugin также использует portalled ActionMenu, чтобы меню не сжималось
до ширины иконки и не обрезалось Dock surface; cursor/порции принадлежат прежним owners.
У saved HTML clock остаётся доступен при одной версии; текущая публикация и выбранный
снимок отмечаются отдельно. Ошибка продолжения предлагает перечитать список с начала.
Постоянной строки title/revision и поясняющей подписи под toolbar нет. Сведения
открываются у адреса через общий ActionMenu с informational rows, без disabled opacity.
Фокус адреса расширяет поле на всю строку; provider controls появляются под ним,
не меняя геометрию iframe. Уход фокуса за пределы controls или Escape сворачивает поле;
меню сохраняет фокус и доступность своего trigger. До 600 px ширины surface правая
группа доступна через ⋯. File picker/source остаются у provider.
P4a baseline: saved HTML/plugin release показывает «Сравнение» знаком двух колонок
с +/−. Выбор baseline — общий text button/ActionMenu, файл — compact select;
existing TextDiff с line numbers и add/remove цветами прокручивается внутри view.
Пара версий и подпись отделяют исходники от данных preview. Общие tokens/controls;
на узкой панели controls переносятся, diff не расширяет Shell. Доработка отложена,
визуальное принятие открыто: [evidence](../browser/05_TEST_MATRIX.md#p4a).

P2a (2026-11-10) добавляет строку «Создать проект из снимка» в существующее меню
сохранения. Общие ActionMenu rows/status/disabled и compact trigger остаются владельцами
геометрии; новых CSS/tokens нет. Во время запроса trigger выключен; Unknown оставляет
создание недоступным с читаемым пояснением. [Ревью 1280 px](review/browser-project-import-wide.png),
[420 px / 125%](review/browser-project-import-narrow.png). Принятие пользователем открыто.

P2b (2026-11-11) использует прежние hidden/disabled/error состояния согласно runtime
capabilities. Геометрия/CSS не менялись: baseline/final controls 30 px, source 12 px,
overflow 0 на 1280/100% и 420/125%. [Замеры](review/browser-p2b.json),
[source-only](review/browser-p2b-source-only.png), [read-only live](review/browser-p2b-live-readonly.png).

Lifecycle Approved 2026-11-11: обычный × Browser-вкладки завершает страницу.
Новых session controls, списка сессий и дополнительного статуса закрытой живой страницы
не вводим. Hide/move сохраняют view; dirty source guard остаётся прежним.
P2c не меняет геометрию/tokens и проверяет lifecycle существующих компонентов.

P3a использует прежний PreviewSurface error/status без новых controls/CSS: при первом
failed source check нет iframe и сообщения о якобы открытой странице.
[1280 px](review/browser-p3a-error-1280.png), [420 px / 125%](review/browser-p3a-error-420.png).
P3b использует existing ActionMenu disabled/detail для проверки active release и
ошибки каталога; новых tokens/CSS нет. [1280 px](review/browser-p3b-restore-1280.png),
[420 px / 125%](review/browser-p3b-restore-420.png) — без overflow.

[Замеры](review/browser-toolbar.json) сравнивают production и frozen Demo при одинаковых
viewport/zoom/theme. Production controls используют общий compact token 30 CSS px,
SVG 16 px; source text 12 px. Сохранённое содержимое проверено при 1280/100% и
420/125%, overflow 0. Новая [строка](review/browser-address-wide.png),
[раскрытый адрес](review/browser-address-focused.png),
[узкая панель](review/browser-address-narrow.png), [сведения](review/browser-address-info.png).
[Новые замеры](review/browser-address.json). Production result In Review;
историческое P1 evidence сохранено в browser-p1.json.
Frozen Demo: header icon 28 px по высоте (на narrow ширина 25 px), source mode 24 px,
текст 12 px. Это явное отличие существующего production compact token; локальные
размеры для имитации Demo не добавляются.

## Ожидание и recovery — In Review, 2026-11-04

Сохранённый wait использует существующие `inbox-pause`, `inboxButton` и waiting
activity общего Chat View. Меняется условие видимости и подпись «Продолжить ожидание»;
новых CSS, размеров, tokens или отдельной карточки нет. Empty-Inbox/live-state
проверяются VM-тестом; визуальная Windows/WebView2 приёмка остаётся открытой.

## Участники и адресные сообщения — Proposed, 2026-11-04

Уточнение пользователя: сохранить существующий sender link. Подпись — небольшой
цветной маркер, имя и agent_id приглушённым текстом, без отдельного badge. Нажатие
всей подписи открывает отправителя; копирование не добавляется. Основной текст,
геометрия bubble и его markdown остаются общими. Стрелки, получатель, message IDs,
типы запроса и delivery statuses не входят в обычный header.

Цвет стабилен для thread во всех представлениях, не меняется от роли/названия/Run.
Первый срез использует фиксированное отображение hash agent_id в palette keys
`RNAssistantAccents.choices` без white, независимо от сортировки picker. Цвет не
уникальный адрес: конечная палитра допускает совпадения, имя/ID различают участников.
История хранит имя на момент сообщения; переход адресует ID. Ручной picker не нужен.
Цвет не меняет общий accent приложения и не обозначает runtime status.

Расширяется `app-agent-communication.appendSender`, общий для истории/Inbox и всех
sources. Используются text-sm, ink-muted, space-1/2 и нынешние bubble tokens;
новых компонентов MessageEnvelope и экранных overrides для этого среза нет.
Заменяемый violet-only стиль удаляется при переключении. Цветовой акцент небольшой,
текст нейтральный; общие font size/zoom/focus и перенос длинных имён сохраняются.
Optional reply link появляется только при полезной связи, не обязателен для каждого
ответа. Точная геометрия нового оформления остаётся In Review до визуальной приёмки.
Прежний насыщенный макет не является UX-reference.

Межагентное обновление HTML использует существующие stale-source/recovery состояния
редактора и обычную кнопку обновления исходников; новых компонентов и геометрии нет.

## Группы дерева чатов — In Review, 2026-11-05

Используются существующие parent/chat nodes NavigationTree, общие отступы вложенности,
высоту строки, раскрытие, selection и ActionMenu. Группы не получают отдельные карточки
или сводки в каждой строке. Цвет участника относится к чату, не к группе; несколько
вхождений сохраняют тот же цвет и адрес. Поиск раскрывает путь к найденному чату.

Действия над группой/членством находятся в существующем ActionMenu. «Убрать из группы»
не оформляется как удаление истории; «Удалить чат везде» сохраняет существующие
проверки и подтверждение удаления истории. Действия чата собраны в одну кнопку «…» вместо ряда кнопок.
По уточнению пользователя 2026-11-05: обычные chat rows без иконки; внутри группы
слева agent-color-dot, размер --agent-marker-size 6px. Один ChatId сохраняет цвет из
общей palette во всех группах и после rename/restart; цвет — подсказка, не уникальный
идентификатор. Группа использует общий значок group (участники), не folder.
Компонент/размер заданы один раз, без screen overrides и новых карточек/badges.
Выделено одно выбранное вхождение чата; все вхождения открывают ту же вкладку.
Ошибка обновления не заменяет группы плоскими строками: сохраняются прежние
узлы и общие состояния selection/menu, рядом показывается существующая строка ошибки.

### Встроенные диалоги групп — In Review, 2026-11-07

Общий [Dialog](../../web/js/app-dialog.js), `RNAssistantDialog.open(options)`, обслуживает
ввод имени и подтверждение удаления в `app-chat-groups`. Это небольшой DOM-компонент:
текст, необязательное поле, подписи действий, callback отправки и resolver возврата
фокуса передаются consumer-ом. Компонент не вызывает bridge и не вводит очередь команд,
registry или store. Публичный plugin API этим не объявляется.

HTML `<dialog>.showModal()` размещает форму в top layer вне clip-контейнеров Dockview.
Используются `.rna-form-control.compact`, `.button.compact`, primary и `danger-soft`;
геометрия композиции — в `app-product-components.css`, общие spacing/type/surface/
line/focus/scrim tokens — в `app-product-tokens.css`. Локальной темы групп нет.
Ширина/высота ограничены viewport, длинные имена и ошибки переносятся, содержимое
прокручивается при недостатке высоты. Ревью требует доступности формы при 125%
и узких панелях.

Состояния: ввод, validation, submitting, error, закрытие. Поле получает фокус при вводе,
«Отмена» — при удалении; Tab/Shift+Tab не выходят из формы. Enter учитывает IME,
Escape закрывает представление. После отправки повтор запрещён, «Закрыть» не отменяет
мутацию. Подписи и inline-ошибка доступны assistive technology; имя группы выводится
через text content. Возврат фокуса учитывает перерисованную или удалённую строку.
Закрытие/reset/scope change освобождают DOM и обработчики, поздний ответ не открывает
форму заново. Browser fixture прошёл в Chrome на macOS 2026-11-10;
[Migration Plan](UI_MIGRATION_PLAN.md#встроенные-диалоги-групп--in-review-2026-11-07)
содержит текущий статус. Windows/WebView2 и визуальное принятие остаются открытыми.

## Редакторы в Dockview — In Review, 2026-11-03

Instance CodeEditor используется для live HTML и ограниченного просмотра исходников.
CodeMirror заполняет доступную область без внешних рамок, скругления и дополнительных
отступов; прежние bounds/history/source-mode сохраняются. Название выбранного HTML-файла
показано один раз в селекторе. Browser/PreviewSurface остаётся общим с плагинами.

Library/VBA header имеет minimum из `--page-header-height`, а фактическую высоту —
по содержимому. При узкой группе и zoom кнопки переносятся, не перекрывая строку
режимов. Геометрия навигационных колонок применяется только к прямым детям layout,
а не к редакторам внутри Dockview. Маркер аннотации у обрезанного левого края
перемещается над/под целью, не накрывая соседнюю кнопку справа. Popup комментария
перехватывает только собственную область; pointer-events всего iframe не выключаются.
Показ уже видимой цели не прокручивает страницу.

## Сообщение другого агента — In Review, 2026-11-03

Общий bubble сохраняет геометрию и типографику user bubble. Отличие — семантические
`--agent-bubble-bg`/`--agent-bubble-border`, основанные на общей violet palette и surface,
и текстовая ссылка «Агент a-… · название чата». Короткий стабильный ID отличает
одинаковые названия. Цвет не является единственным признаком.
История и pending Inbox используют один renderer; заголовок выводится через textContent.
Панель исчерпанного лимита использует общие spacing/text tokens, переносит кнопку и
не захватывает фокус. Отдельных значений для Office/Workspace/plugin нет.
Общее numeric-поле принимает 0; подпись явно поясняет «0 — отключить».
Активная работа/ожидание используют общие status/activity/tool components;
для `agents.wait` — подпись «Жду сообщения агентов», без отдельного spinner/style.
Срез spawn/tab-independent discovery (2026-11-04) не добавляет визуального семейства:
созданные участники используют обычный Chat View, те же bubbles/activity/receipts.
Закрытие вкладки не получает скрытого действия Stop. Геометрия компонентов не меняется.
Сравнение computed padding/radius с обычным bubble на одинаковом viewport прошло;
Chat View zoom overflow исправлен 2026-11-04; другие retained views остаются в RISK_REGISTER.

Обновлено: 2026-11-04. Owner: Web presentation; визуальные решения утверждает
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
| Настройки вкладкой Dockview, gear снизу слева, сохранение hover menus | Approved — текущий S1c (`26f4d87f`), пользователь «Меня устраивает» |
| Имя документа в topbar, responsive thresholds | In Review |
| P3 production-чат в Dockview без повторного заголовка | In Review; iframe только Demo evidence |
| S1a: общие подписи ввода, edit/reply/stop, reasoning и окно истории Office/Workspace/plugin | Реализовано, детали presentation In Review; не полная унификация capabilities |

## Общий Chat View — In Review, 2026-11-03

Production markup служит единственным шаблоном Chat View. Factory создаёт scoped
nodes с уникальными DOM IDs и `data-chat-node`; стили общих ролей используют
последний, а не глобальные IDs. Размеры, spacing, кнопки и состояние composer
остаются в общих tokens/CSS, без отдельных Office/Workspace/plugin overrides.
Два composer рядом — два экземпляра одного компонента. Базовый стиль P1/P2,
малые close/split icons и вкладка выбора сохранены; нового варианта оболочки нет.
В chooser добавлены реальные chat targets. Лимит 12 вкладок и точная presentation
этой точки остаются In Review до пользовательского согласования.

Активная строка дерева и вкладка Office-чата текущего документа отражают выбор
сразу, до завершения native selection. Загруженная лента сохраняется при его
промежуточном ответе; отдельного экрана ожидания и новых loading-styles нет.
Фоновые tokens сохраняют DOM и фокус неизменного дерева. При смене selection общий
NavigationTree обновляет существующую строку; выбор из узкой навигации закрывает
overlay, включая синхронную замену дерева. Новых размеров и локальных CSS нет.

Приветствие — один `renderChatEmptyState` для всех sources. Общие
`--chat-empty-*` в `app-product-tokens.css` переносят P2: акцентный `chat` SVG
29 px без плашки, заголовок 20 px/1.35, блок до 440 px, padding 32/16 px,
отступы иконки/заголовка 16/12 px, пояснение 12 px/1.7 и внешний отступ 8vh.
Кнопки используют `button compact`; отдельные dark/narrow overrides удалены.
При высоте ленты до 360 px общий container rule убирает внешний отступ и
уменьшает вертикальный padding до 16 px. Блок не сжимается с потерей начала,
текст и действия остаются в прокручиваемой ленте. Presentation — In Review.

## Эталон оболочки: завершённый Dockview Demo

Нижнее меню активных запросов использует общий ActionMenu: название чата и
приглушённая подпись проекта/документа и стадии, отдельная кнопка Stop справа.
По уточнению пользователя 2026-11-03 **без иконок чата в строках**, ширина по тексту
до `--shell-runs-menu-max-width: 340px`, длинные строки с многоточием. Максимум
дополнительно ограничен viewport с учётом zoom; popup открывается вверх. Геометрия
строк, кнопок и spacing — общие menu/control tokens. Проверены 390/1440 px,
125% CSS zoom и keyboard focus; результат In Review.

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
  значок Office-документа соответствует host активного чата (Excel, Word, PowerPoint,
  Outlook), а при отсутствии чата — текущей среде;
  справа компактная группа navigation / Inspector / maximize, затем «Панели»,
  «Компоновка». Настройки по согласованию S1c перенесены вниз rail, диагностика над ними. Отступы и иконки соответствуют reference.
- Узкая вертикальная полоса разделов и отдельное дерево слева. У активного раздела
  спокойная заливка; по уточнению 2026-11-03 левый акцентный маркер удалён. Дерево не является Dockview-группой.
  Диагностика в левом rail использует общий `diagnostics` glyph: лупа с линией
  пульса внутри; размер, цвет и толщина линий задаются общим компонентом иконок.
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
с этими tokens. [app-product-navigation.css](../../web/css/app-product-navigation.css)
владеет Shell chrome, [app-dock-workspace.css](../../web/css/app-dock-workspace.css) —
Dockview adapter/chooser. Feature CSS экранов сохраняет отдельного владельца.
Размеры hit targets и знаков разделены; точные production breakpoints и поведение
на реальной Office-панели ещё проверяются.

StatusBar использует `--shell-status-height`; его controls имеют `height: 100%`
и `min-height: 0`. Общий минимум кнопки 34 px не должен растягивать control внутри
строки 24 px. В Chromium после исправления по фото от 2026-11-03 высота кнопки
23 px с учётом границы строки совпадает с Demo; внешний overflow устранён.

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
production tokens. Адаптер tab spacing/close icon ограничен Dockview 8.8.3 и повторно
проверяется при обновлении vendor; другие компоненты от него не зависят.

## Применение правил сейчас

Запуски 2026-11-03 — In Review: общий NavigationTree вместо локальных disclosures.
Опция общего дерева `toggleOnSelect` включает выбор и раскрытие/сворачивание группы
одним нажатием строки (также Enter/Space); Запуски используют этот режим.
`reserveToggleSpace` сохраняет общую колонку иконок у групп и листьев журнала.
`lazyChildren` откладывает создание свёрнутых веток, сохраняя общий keyboard order
и selection. Прокрутка выбора адресует строку, не всю вложенную ветку; дозагрузка
сохраняет reading position. В короткой панели верхнее дерево не имеет фиксированного
минимума, вытесняющего reader; status row резервирует общий `--control-compact`.
Обзор группы использует существующие `rna-table` / `rna-table-link`: названия,
время и результаты выровнены без локальных отступов кнопок. Статусы и пояснения
не повторяются в заголовке и строках; таблица раскрывает полезную структуру,
а не копирует иерархию технических событий.
Корневые группы сохраняют акцент и межгрупповой отступ; вложенные группы используют
обычные `tree` tokens, шрифт, цвет и высоту строки без дополнительного нижнего
интервала. Selected parent/leaf имеют одинаковый общий цвет и фон. Иконки — общий
каталог; крупный parent icon применяется только на первом уровне.
Причина повтора использует обычные текст, кнопки и JSON viewer reader; новые
карточки, цвета и локальные размеры не добавляются. Причина видна до загрузки тела.
«Формат ответа модели принят» обозначает только parser acceptance. Semantic отказ
finish использует существующие feedback/terminal message; отдельной отметки его
допуска у исходного запроса пока нет. Нормализованная цитата в следующем запросе и
точный исходный payload не подменяют друг друга.
RunJournal CSS владеет только компоновкой дерева/reader. Нет второй палитры,
своих selected/hover или копии списка событий в деталях. Повторные названия,
счётчики и служебные пояснения убраны; JSON compact toolbar переносится целиком
через общие стили viewer, не сжимая статус до вертикального столбца букв.
Привязка диагностики видна одной строкой document/project + chat. Это обычная
компактная кнопка-ссылка к исходному чату; IDs остаются в tooltip. Выбор другого
источника использует существующее дерево чатов и его общий слот действий,
без второго каталога/selector и без новой строки постоянных кнопок.

Лента 2026-11-03 — In Review. Общий `.agent-step-reason` использует `--text-sm`
и `--muted`, переносит длинный текст и не добавляет карточку/иконку на каждый шаг.
Reason выводится как текст, без HTML-интерпретации; одинаковый с `message` текст
не дублируется. Принятые пояснения остаются на месте после finish, детали calls
сворачиваются лениво. Claims/счётчики используют одно компактное раскрытие;
жёлтые unknown/partial notices и их старые CSS-обёртки удалены. Вызов макроса
со штатным возвратом выглядит как законченный вызов, без утверждения об эффекте.
Ошибки, подтверждения и вопросы сохраняют действующие controls.

Потоковый текст имеет тихую подпись «Черновик ответа · …» в существующем статусе,
без новой карточки. Сброс попытки может удалить этот черновик; принятые пояснения
и `reason` сохраняются. Обновление соседнего шага сохраняет выделенный текст и
раскрытия без временного отсоединения неизменённых узлов.

Исправление protocol results/Stop по фото 2026-11-02 сохраняет общий transcript,
composer и tokens. Удаляются ошибочные сообщения и разрывы блоков запуска; статус
отмены использует существующую строку и кнопку. Новых controls/CSS overrides нет.

S2b.12 меняет вход данных общего transcript, без новых UI-компонентов, controls или
CSS. История/stream/reasoning используют прежние renderer и tokens. Два адресных
source не означают две реализации чата; фабрика DOM-представления — следующий шаг.

S2b.11 сохраняет общий transcript, composer и существующий вид stream/reasoning.
Убрана выбранная копия live-данных, без новых controls/CSS. Token paints сохраняют
узлы истории, фокус и раскрытые блоки; отмена/повтор удаляют непринятый draft.
HTML source repair также не меняет дизайн: исправлены чтение и экспорт исходника.

Оптимизация создания Office-чата меняет только доставку данных: пустая переписка и
catalog delta используют существующие NavigationTree/composer/transcript. Новых
controls, геометрии и локальных CSS overrides нет; остальные строки дерева сохраняются.

Общие tokens, Icons/Accents, PanelToggle, NavigationTree, composer/transcript,
Inspector и ViewerRegistry уже используются в production. Это действующие правила
каждого среза, а не отдельная финальная чистка. При замене переключаем consumers
и удаляем старый путь в том же изменении. Отдельный пакет UI Kit не создаётся заранее.
Полная переносимость controls и оставшиеся дубли форм/меню/CSS уточняются в S2–S5
по реальному второму consumer, а не объявляются завершёнными.

В уточнении S1b удалены дубли закрытия дерева из `app.js` и прежние fullscreen/compact
CSS paths. Preview layouts получают общие navigation columns; величина занимаемого
места приходит из ShellLayout. NavigationTree сообщает `rna-navigation-select`
(bubbling, без domain payload) после принятого выбора мышью/клавиатурой, включая
синхронную перерисовку дерева; toggle папки и отказ `onSelect === false` не дают событие.
Shell слушает его только внутри `.global-nav`; сам Tree не импортирует Shell/bridge.

Оригинальные Codicons не перерисовывались. Причина увеличенных кнопок — унаследованные
`flex-basis`/`min-height` общего `.icon-btn`: 34 px вместо Demo 25 px. Группа теперь
передаёт общий `--control` из shell token; SVG/mask остаётся 14 px. Второй button
component или копия SVG не создавались.

После повторного визуального замечания исправлена оставшаяся геометрия S1b:

- `app-product-tokens.css` владеет диапазоном/шагом ширины navigation, разделителем,
  внутренними отступами, размерами строк/иконок Tree и геометрией Dockview.
  CSS и JS читают эти значения; числа из `data-default-left`/`data-min-left`
  шести Shell layouts удалены. `data-navigation` обозначает только роль.
- `app-product-components.css` владеет строками, отступами вложенности,
  selected/hover/focus и действиями NavigationTree для всех consumers.
  Вложенные списки не рисуют вертикальную линию и не добавляют внутренний padding.
  Ряд прежних действий на desktop появляется поверх правого края, не резервируя ширину у
  названия; на touch занимают место постоянно. Длинное название плавно проходит
  под наведением или клавиатурным фокусом до конца доступной области; reduced motion
  сохраняет обычное усечение и полную подсказку. Высота строки — 32 px при pointer.
  Office/Workspace используют одну chat icon. Plugin overrides высоты/шрифта удалены.
- `app-product-navigation.css` оформляет общий navigation splitter;
  `app-dock-workspace.css` связывает tokens с Dockview и нормализует vendor `rem`
  padding вкладок, чтобы масштаб текста чата не менял их геометрию.

Повторный аудит завершает перенос Shell целиком: topbar/rail/footer имеют один
CSS owner; sidebar headers используют общую геометрию. Chooser повторяет P1/P2
(вверху области, responsive grid, поиск/Enter, empty state). Menus не имеют
отдельного Dock renderer: используется портальный вариант существующего ActionMenu.
Нейтральные surfaces, selection blend и shadow взяты из reference; выбранный
пользователем accent не сбрасывается в Demo default. UI «Демо» для выбора mock
сценария/ширины не является production-командой; theme/accent — в настройках.

Это исправление существующих общих владельцев, без второго UI Kit или renderer.
Остальные feature-specific CSS ещё существуют: полное устранение всех дублей
не объявляется завершённым. Сравнение с Demo и снимки — в
[Migration Plan](UI_MIGRATION_PLAN.md#уточнение-s1b-общая-геометрия-после-визуального-ревью).

## Settings и hover menus S1c

Текущий результат **Approved**: пользователь «Меня устраивает»; commit `26f4d87f`. Gear сохраняет общий
размер/состояния rail button. Hover cards используют поверхность/строки того же
menu component, общие spacing, icon, radius, shadow и theme tokens. У tooltip
только подпись; у интерактивного меню есть заголовок и действия. Версия остаётся
внизу меню настроек. Отдельные размеры plugin/settings menus удалены.

Категории Settings используют NavigationTree и существующий поиск. Компактный
select потребляет те же nav items и общий `.rna-form-control`, без второй карты
разделов. Responsive зависит от контейнера Dockview. Footer «Применить» остаётся
видимым при dirty/saving, не перекрывает заголовок или поля. Геометрия содержимого
принадлежит `app-product-screens.css`; Shell не управляет внутренней навигацией.
[Снимок с меню](review/s1c-settings-hover.png), [узкая группа](review/s1c-settings-narrow.png).

JSON-профиль агента использует существующие `setting-field`, `setting-note`,
`details` и JSON viewer. Один select формата без дублирующих колонок истории/роли;
миграция — текстовый status, пример — раскрываемый блок. Новые tokens/CSS не нужны.

## Общие controls модели S2a

Owner: существующие composer/model-picker/model-capabilities. Один menu renderer,
строки, badges и подписи используются Office/Workspace/plugin без отдельных CSS.
«По умолчанию» наследует общие настройки; явный выбор принадлежит чату. Running,
awaiting approval, preference save и read-only делают control недоступным.
Подтверждённый отказ не меняет выбранную строку; потерянный ACK требует чтения
сохранённого выбора перед отправкой. Неизвестная каталогу модель остаётся
видна с объяснением. Reasoning учитывает возможности выбранной модели.

Menu позиционируется относительно общей toolbar composer, ограничено её шириной
и использует существующий spacing token. Это устраняет обрезание у левой границы
узкой Dockview-группы. Новой библиотеки controls или локальных owner styles нет.

Статус реализации — **In Review**. Production UI на mock bridge:
[1360 px](review/s2a-model-menu.png), [420 px / dark](review/s2a-model-menu-narrow.png).
Screenshots подтверждают отображение, не работу Office или реального provider.

## Каталог существующих компонентов

L1 = host-independent component; L2 = browser requirements; L3 = host integration.
Указанная пригодность — source-аудит, не полная standalone qualification.
Новые public interfaces вводятся только для реального второго consumer.

| Компонент / источник | Повторное использование и граница | Состояния / lifecycle |
|---|---|---|
| [ShellLayout](../../web/js/app-layout.js) | Shell chrome/navigation; renderWorkspace получает context/layout snapshot, bindWorkspace — callbacks трёх workspace-команд; без mirror store | navigation state, responsive, topbar/footer, notice cleanup |
| [Dock Workspace](../../web/js/app-dock-workspace.js) | Один Dockview; descriptors conversation/inspector/settings и временный chooser; placement и domain-view adapters, не chat store | move/resize/close/restore, narrow, pending layout save, DOM lifecycle |
| [Icons](../../web/js/app-icons.js), [accent](../../web/js/app-accent.js) | Общие уже сейчас; кандидаты L1 без Shell | Theme/size/accessible label у consumer; shared catalog |
| Buttons/toolbar/forms/menu CSS: app-product-components, app-theme, app-composer | Сохранять привычные controls; извлечь затронутые rules, не копировать по views | default/hover/focus/pressed/disabled/invalid/loading |
| [NavigationTree](../../web/js/app-navigation-tree.js) | Реальный общий renderer, options/callbacks + icons; L1 candidate | selected/expanded/filter/keyboard; unmount освобождает state |
| [TreeAdapter](../../web/js/app-tree-adapter.js) | Resource/domain adapter поверх NavigationTree, не второй widget | read/loading/error, destroy; host calls не уходят в UI Core |
| [EditorTabs](../../web/js/app-editor-tabs.js), [ActionMenu](../../web/js/app-action-menu.js) | Внутренние режимы/меню; не docking engine | focus/keyboard/active; callback actions |
| [Dialog](../../web/js/app-dialog.js) | Ввод/подтверждение в app-chat-groups; DOM/controls и callbacks, без bridge/store | input/validation/submitting/error; Enter/IME, Escape, focus return, close/dispose |
| [ViewerRegistry](../../web/js/app-viewer-registry.js), [ViewerUI](../../web/js/app-viewer-ui.js) | Сохранить register/mount/unmount форматов | destroy старого controller и decorations при замене |
| [JSON](../../web/js/app-json-viewer.js), [text/Markdown](../../web/js/app-text-viewer.js), text-diff | Повторно используют registry; portable rendering выделять без bridge | bounded DOM/depth, search/copy/source, unmount |
| [ResourceViewer](../../web/js/app-resource-viewer.js), [SequenceViewer](../../web/js/app-sequence-viewer.js) | Общие PDF/image/sequence/task-list; resource/lease adapter остаётся L3 | lazy pages, zoom, unavailable, destroy/release; не загружать всё заранее |
| [PreviewSurface](../../web/js/app-preview-surface.js) | Общая шапка/локация/reload для HTML и plugin preview | load/error/reload; frame owner не принадлежит chrome-компоненту |
| [OperationView](../../web/data-templates/ui/operation-view.js) | Готовый presentation-only progress: create/render/dispose, callbacks cancel/dismiss; L1 candidate | Фактический snapshot; unknown total indeterminate, без таймерной симуляции |
| [Questions](../../web/js/app-questions.js), [RunChanges](../../web/js/app-run-changes.js) | Общие conversation composites, пока domain-aware | awaiting/submitting/rejected/failed, owner/receipt сохраняются |
| [ConversationView](../../web/js/app-conversation-view.js), Messages/WorkspaceChat adapters, composer | Общие transcript lifecycle и SnapshotOwner; Workspace у view, Office transcript/preferences/context и material metadata через временный selected accessor. Общие builders/footer заимствуют данные | target-scoped cache/frames/scroll/actions; hide сохраняет projection, replace/dispose освобождает; Office source/view factory и singleton adapter ещё мигрируют |
| [Composer presentation](../../web/js/app-chat-composer.js), [reasoning control](../../web/js/app-model-render.js) | Один renderer состояний ввода и controls; owners передают факты и capabilities | idle/edit/awaiting reply/read-only/running/cancelling; не хранит второй draft |
| ChatComposer в том же `app-chat-composer.js` | Один controller bindings/edit state, draft presentation и команд model/mode/reasoning на form; exact target и сервисные adapters | S2b.2/S2b.5/S2b.6: edit/draft retention, scoped ACK/read-back, стабильный cursor, Enter/IME/disabled Send; единые controls, без нового store и экранных overrides |
| [ConversationObserver](../../web/js/app-conversation-observer.js), `ConversationView.observe` | S2b.4: общий lifecycle UI-чтений; source adapters задают transport и callbacks | exact binding, stale reply/error, pause/catch-up/dispose; не отменяет domain run |
| [ConversationState](../../web/js/app-conversation-state.js), history buttons в Messages | Один cap 240 и prepend/refresh для обоих доменов; explicit latest bypass; S2b.7 — адресный приём snapshot | wrong-target/stale page rejection, bounded data, reading anchor; source не хранит второй snapshot, durable history не обрезается |
| [ChatInspector](../../web/js/app-chat-inspector.js) | Один domain view, настоящие context/capabilities/usage/run | Scope/generation, lazy detail, disclosure/focus, suspend |
| [ModelContext](../../web/js/app-model-context.js) | Domain reader, сохранять существующие cache/abort/bounds | metadata → lazy body, retained views, stale guards |
| [RNPlugin SDK](../../web/plugin-host/sdk.js) | Существующий Ready/command/files/results protocol; не новый SDK | L3 host, L2 browser adapter отдельно; permissions у host |

Пустое поле ввода занимает две строки после очистки длинного черновика; длинный
текст прокручивается внутри поля с пределом по высоте chat view. В узкой группе
кнопки модели, отправки и остановки остаются внутри composer. Содержимое вкладки
Материалов занимает всю высоту Dockview, включая пустое состояние HTML-редактора.

Нельзя обобщать Chat/Inspector в UI Core только из-за общих кнопок. Сначала
выделяются независимые controls с реальными consumers и инъекцией зависимостей.
Не создавать универсальный VirtualList/DataTable, если существующих bounded
renderers/Tabulator достаточно для конкретного сценария.

## Структура запроса — production, 2026-11-02

[Макет](review/request-structure.html) — ревью содержимого view, не новая Shell
или развитие замороженного Demo. [Маршруты и типы](UI_SCREEN_MAP.md#структура-запроса--production-2026-11-02).

- Общий NavigationTree: selected/focus, клавиатура, whole-row selection, spacing,
  иконки. Локальное оглавление не становится global-nav; его выбор не закрывает
  Shell navigation. Максимум раздел → сообщение, содержимое читается рядом.
- EditorTabs/общие controls: Состав/Ответ/Изменения, Markdown/JSON/Текст.
  Viewer API расширен внешним управлением toolbar; standalone controls других consumers сохранены. Внутренние controls не скрываются через CSS, renderer общий. В макете Markdown использует общий
  sanitized renderer, JSON/text — ViewerRegistry; это не новый production API.
- Роль/размер/тип — короткие подписи. Причина отказа — текст; статус не только цвет.
  Без новых цветов групп, крупных карточек и повторного текста в каждом раскрытии.
- Feature CSS владеет только оглавление/reader layout и переходом список/detail
  по ширине view. Tree/buttons/forms берут геометрию из общих tokens/components.
  Новый splitter и собственная тема не вводятся.

Ревью: обе темы, narrow/zoom, длинные имена, JSON-строка, partial/unavailable,
видимый фокус и возврат к списку. Формат не маскирует исходный тип; текущий snapshot
не становится runtime proof. Production lifecycle/performance evidence — в Migration Plan.

### Кнопки, формы и меню диагностики

Scope подтверждён пользователем: диагностика и её реальные общие consumers.
Production включён по прямому указанию; визуальная проверка выполнена на synthetic bridge. Новая тема диагностики и локальные варианты кнопок не нужны.
В production используются EditorTabs, mode-toggle, rna-form-control, NavigationTree
и портальный ActionMenu; CSS макета задаёт только раскладку/состояние narrow.

Заменённые дубли (старые paths удалены):

| Удалённый путь | Действующий owner |
|---|---|
| `app-trajectory.css`: собственные размеры icon-btn, SVG, menu buttons/body и fields | `app-product-components.css` + product tokens; в trajectory остаётся layout/filter composition |
| ModelContext buttons смешивают `button secondary compact`; details helper создаёт только `secondary` | Один вариант общего button для соответствующей роли, не новые размеры у каждого helper |
| Компактный JSON viewer самостоятельно собирает details-menu с actions | Общий ActionMenu с адаптером команд viewer; parse/format/copy остаются у JSON viewer |
| Отдельные toolbar controls в source/messages и их metadata details | Один reader toolbar и actions выбранного объекта; никакого параллельного «копировать то же» |
| Feature CSS задаёт мелкие интервалы и control density буквально | Общие tokens/components для повторяемой геометрии; feature CSS только размещает элементы |

Расширение общего compact control допускается там, где текущего варианта не хватает:
значения живут в tokens/components и используются всеми затронутыми consumers.
Не копировать размеры в HTML/JS и не компенсировать старые rules новыми overrides.
Native dropdown списка остаётся form control, меню команд — ActionMenu; смешивать
их только ради похожего вида не надо. Просмотр форматного JSON сохраняет отдельный
выбор дерево/текст/исходный: это формы одного viewer, не глобальные разделы.

Production JSON (включая меню узла), журнал/связи событий и reader используют
портальный ActionMenu. `openButtons` принимает существующие DOM-команды, сохраняя
их handlers и disabled state. При unmount меню закрывается. Viewer API `toolbar:false`
передаёт controls выбранного сообщения reader; standalone consumers сохраняют toolbar.
`sourceLabel` отличает декодированную строку от точного JSON. Compact controls используют
`--control-compact`; log filters — общие pressed buttons без отдельной dark-палитры.

Снимки макета: [Markdown](review/request-structure-wide.png),
[JSON-строка](review/request-structure-json.png),
[меню запроса](review/request-structure-menu.png),
[узкий dark view](review/request-structure-narrow.png).
Локально проверены реальные Tree/EditorTabs/ActionMenu/ViewerRegistry в макете:
форматы, полный JSON сообщения, исходник запроса, Escape/возврат фокуса, меню
в пределах viewport, partial/missing и ширины 320–1200 px. Это ревью layout,
история проектного ревью. Production screenshots и измерения перечислены в Migration Plan.
Запуск из корня: `python3 -m http.server 8898 --bind 127.0.0.1`, затем
`/docs/ui/review/request-structure.html`. Ни bridge, ни модели не подключаются.

## Контракт компонента и ревью

### NavigationPane S3b.1 — production, In Review

[NavigationPane](../../web/js/app-navigation-pane.js) объединяет общий header,
поиск, кнопки команд, footer состояния и NavigationTree; source adapters передают
данные и callbacks. Header — 41 px, controls и focus/selected/menu — существующие
tokens/ActionMenu. Общие title ellipsis и search styles работают без Shell.
Отдельные Materials header/menu CSS и перенос одного menu DOM между sidebar и
каталогом удалены: оба trigger используют одни command handlers через ActionMenu.
Scroll/keyboard/expanded остаются у NavigationTree и source adapter. Повторный
mount не создаёт обработчики; dispose снимает listeners/timer/tree binding.

Ожидание удаления чата использует существующие status/disabled состояния дерева
и префикс «Удаление…» в названии (виден при ellipsis); новые размеры, spinner и
overrides не вводятся.

Проверены реальные consumers и независимый VBA-shaped fixture без Office/bridge;
[ревью и ограничения](UI_MIGRATION_PLAN.md#s3b1-общая-навигация--in-review-2026-11-02).

### Material object view S3b.2 — In Review

Используются существующие Dock tabs, ResourceEditor header/title/meta/actions,
ActionMenu, ViewerRegistry и форматные controls. Новые styles ограничены flex/scroll
компоновкой `.material-object-view`; tokens задают inset/spacing/type/color.
У переносимых Chat/Materials components удалена зависимость от `#tab-chat`/
`#tab-artifacts`: внешний source не меняет оформление view. Sidebar Материалов
использует flex tree body, поэтому «Все материалы» не растягивается при скрытом search.
Whole-row selection остаётся у NavigationTree; Escape возвращает focus из action menu.

[Wide/light](review/s3b2-light-1280.png), [narrow/dark](review/s3b2-dark-420.png):
1280/420 px, light/dark, 125% zoom, source title overflow и keyboard menu проверены
в Chromium. Несколько JSON toolbars используют локальный индекс действий; decorators
не запускают взаимную перерисовку при вложенных viewers. Browser/Office evidence
и cold restart presentation mementos остаются открытыми.

### Общая панель навигации и object views — Proposed, 2026-11-02

Материалы/Библиотека/будущие Файлы переиспользуют один каркас панели: source/scope,
поиск/фильтры, compact toolbar, дерево или табличный список, paging и состояния.
Основа — действующие NavigationTree, TreeAdapter там, где он подходит данным,
ActionMenu, forms и tokens. Новый tree widget, собственная тема каждого источника
и универсальный framework provider-ов не нужны. Предметная группировка остаётся
у adapter: library categories не изображают файловые папки, revision не file path.

Общая строка кликабельна целиком; expander раскрывает группу, явное открытие/Enter
открывает объект. Поиск не загружает тела всех найденных объектов. Selected row,
keyboard focus и active Dock tab — разные состояния. Scope, поиск, раскрытия и
scroll возвращаются при смене источника; ошибки/partial/cancel не выглядят пустым
успешным каталогом. Возможность команды задаёт предметный owner, а не тип иконки.

Общий каркас просмотра содержит название, источник/версию, статус и действия
объекта; controls формата принадлежат ViewerRegistry renderer. Для HTML/plugin
расширяется существующий PreviewSurface: одинаковые loading/ready/error/reload,
единое размещение browser surface, provider-specific действия рядом. При embed
viewer toolbar не дублируется внешним header. Адрес/название показывает источник,
не превращается в разрешение произвольной навигации или доступа к файлам.

Ревью проверяет одновременно два разных объекта, смену навигационного источника,
несохранённый editor, узкий режим, обе темы, move/split/restore и клавиатуру.
Простого сходства отдельных screenshot недостаточно: важны сохранность состояния,
отсутствие hidden side effects и bounded reads/retained memory.

Для общего Browser в S3b используются те же Dock tabs, header/actions и controls.
Чат и preview — независимые views; preview не получает собственный composer
или вторую оболочку. HTML и plugin проверяются вместе на narrow/wide, focus/close
и сохранность frame session; отдельный plugin-only path не является целью среза.

Для выделяемого компонента здесь фиксировать source/public API, реальные consumers,
владельца данных, states, keyboard/focus, tokens, события, cleanup, portability и
пример. Mount не стартует domain operation без команды пользователя/владельца.
Версия public API нужна при появлении внешнего consumer, не для каждого helper.

Узкий режим, увеличение текста, menu clipping, hit areas, tooltip и Escape
проверяются на конкретном срезе. Не путать хороший screenshot с исправлением
late responses, утечек или Office qualification. После согласования обновлять
компонент и tokens в том же срезе; эксперимент остаётся Proposed/In Review.
Отдельный UI_COMPONENT_CATALOG.md сейчас не нужен: этот каталог имеет одного owner.

ActionMenu portalled API: `open(trigger, items, {label, className})`, item —
`label/icon/disabled/status/onSelect` или separator; `closeAll()` и `closeWithin(root)`
для deactivation/dispose. `render()` существующих inline menus сохранён.
Компонент зависит только от Icons/DOM, не от NavigationTree/Shell/bridge.
Arrow Up/Down, Home/End, Tab и Escape доступны; открытие нового меню закрывает
предыдущее, Escape/выбор возвращают focus, resize/pagehide удаляют popup.
API проверен в standalone component fixture; public plugin export ещё не заявлен.

### Browser S3b.3a

Общий renderer — [Browser](../../web/js/app-browser-viewer.js); общий toolbar —
[PreviewSurface](../../web/js/app-preview-surface.js). Provider не задаёт собственный
splitter, ширину, overlay или переключатель инспектора. Dock tabs/menus/buttons и
focus используют общие компоненты/tokens. Toolbar содержит источник/версию, действия
и отдельную строку статуса; до 450 CSS px действия переходят на следующую строку,
имя источника остаётся читаемым. Причина ошибки переносится, не маскируется под успех.

Browser placeholder доступен с клавиатуры: Enter переводит фокус к содержимому.
Hidden view/открытая узкая навигация не перехватывают ввод; при drag поверхность
временно скрыта от hit testing, document не выгружается. Собственные CSS задают
только portal geometry и компоновку content/toolbar. Аннотации используют прежние
VisualInspector/VisualAnnotations и черновик адресного чата.


### Сохранённые Browser objects S3b.3b

Используются те же PreviewSurface, Dock tabs, ActionMenu, `view-switch`,
`rna-form-control compact` и TextViewer с общим CodeMirror для исходника. `ActionMenu.createTrigger` создаёт единый
trigger для обычного материала, HTML и версии плагина. ViewerUI применяет существующий
form-control к поиску общих viewers; отдельного набора цветов/кнопок нет.

Порядок: имя и источник/версия → статус → ограничение/характер данных → содержимое.
У HTML один уровень выбора «Страница / Исходник», список файлов виден только в исходнике.
Текст имеет диапазон страницы, поиск и copy текущей страницы. Нет скрытого полного
исходника в DOM и второй iframe под кодом. Browser CSS добавляет только flex/overflow/
spacing для нового content layout. Длинные названия сокращаются в шапке с полным title;
сообщения ошибок переносятся. Read-only snapshot явно отличается от редактора.


### Исходники и оформление плагина — In Review, 2026-11-03

Единственный owner поверхности кода — `app-product-components.css`: `--code-bg`,
`--mono`, gutter и syntax tokens. CodeMirror заполняет stage, без внешней рамки,
радиуса и отступа; старые theme/base overrides и минимальные высоты VBA удалены.
`app-editors.js` предоставляет instance readonly mount с ResizeObserver/dispose;
HTML/XML/CSS modes — локальные файлы уже закреплённого CodeMirror 5.65.16.
Статус страницы исходника нейтрален, а не warning. Toolbar сохраняет поиск/copy/paging.

У дерева одна кнопка меню в строке; она видна также при selection/keyboard focus.
Единственное поле названия плагина находится выше всегда раскрытой палитры;
повторной шапки и внутренних разделителей нет, интервалы — общие space tokens.
Импорт SVG сохраняет собственное поле «Название SVG»; кнопки раскрытия и добавления
используют `button compact` и общий plus. Карточки и preview рядом с выбором цвета
используют один renderPlugin. Новый выбор SVG возвращает режим «Исходные цвета SVG».

## Project definition form — 2026-11-13

The existing Dialog uses the shared form control and buttons, with an optional
multiline field; focus trap includes textarea, Escape and stale-view cleanup remain
shared. [420 px / 125% review](review/workspace-definition-narrow.png) and
[checkpoint toolbar](review/workspace-versions-narrow.png); no screen-specific geometry.

## Shared checkpoint controls — 2026-11-13

Document and Workspace reuse plugin checkpoint controls; labels avoid duplicate vN.
Save remains visible with a specific disabled/error reason. Source-file saving and
project checkpoints remain separate actions. [Dirty-buffer refusal at 420 px/125%](review/document-checkpoints-narrow.png).
