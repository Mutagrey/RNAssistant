# Universal Browser: статус

Дата: 2026-11-11. Scope: P0/P1 + Browser controls/freshness + P2a import + P2b targets + P2c lifecycle checks + P3a source failure + P3b restore freshness.
Owner: Web Browser/PreviewSurface; Application/Core — sessions/contracts/evidence;
Office/WebView — experimental P5a native adapter. P1, P2a и P2b реализованы, **In Review**.

## P5a: WinForms в Dockview — In Review, 2026-11-11

По выбору пользователя начинаем с целевого Dockview, без предварительного отдельного
окна. Под `RNA_BROWSER_PILOT=1` готова одна ручная внешняя вкладка с общим toolbar,
HTTP(S), назад/вперёд/reload, временным InPrivate-профилем и экранной диагностикой.
Page/policy отделены от native placement; fallback-окно появится только при выявленном
блокере. Agent tools, persistent profiles и второй session store не вводятся.

Два host-neutral Harness checks и новый browser fixture проходят; existing Browser
lifetime также проходит. Wide/narrow Shell screenshots просмотрены с synthetic native.
Windows compilation, фактические HWND/DPI/focus/IME/overlays/process cleanup и
визуальное принятие открыты. P5 целиком не закрыт. Следующий шаг P5a — native evidence
при первом доступном Windows-запуске; P3/P4 продолжаются независимо.
[Запуск и точные границы проверки](05_TEST_MATRIX.md#p5a).

## P3b: актуальность восстановления release — In Review, 2026-11-11

P3a закоммичен `8d337666` (`16.3.2-dev`). В следующем срезе исправлен stale restore
сохранённой plugin-вкладки: current head читается при открытии меню, guard больше
не хранится в Dock route. Поздний ответ/ошибка каталога не разрешают действие;
конфликт после чтения не повторяется автоматически. Instance и iframe сохранены.
Existing `browser-saved-views` red → green, включая 1280/100% и 420/125%; новых API
или stores нет. [Evidence/ревью](05_TEST_MATRIX.md#результаты-p3b--2026-11-11).
Код и локальная проверка завершены; visual acceptance и Windows/WebView2 evidence открыты.

## P3a: ошибка исходников без подмены release — In Review, 2026-11-11

Первое открытие plugin workbench после failed source check больше не подставляет
active release. Ошибка остаётся видимой без iframe; прежний рабочий preview при
failed refresh сохраняется. Watcher, manual retry и явная команда active release
проверены. Исправлена существующая ветка PluginView; новых services/DTO/UI нет.
Focused red → green и независимые plugin views прошли; общий browser fixture
остановился на прежней focus assertion до нового сценария. [Evidence/ревью](05_TEST_MATRIX.md#результаты-p3a--2026-11-11).
Остаток P3, визуальное принятие и Windows/WebView2 evidence открыты.

## P2c: автоматический lifetime — проверен host-neutral, 2026-11-11

Пользователь утвердил автоматическое управление: switch/hide/move/split сохраняют
страницу и ввод; close вкладки освобождает renderer/leases; reopen загружает target
заново. Отдельные список sessions/команда завершения и лимит закрытых живых страниц
не нужны. Run, сохранённые данные и dirty source guards остаются у прежних owners.
Начатый незавершённый код detached sessions удалён; P2b закоммичен `36a44073`.
План/контракты и canonical UI docs приведены к [решению](02_ARCHITECTURE_ADR.md#lifetime-authority-и-восстановление).
P2c проверил existing lifecycle: четыре browser fixtures прошли, production-правки
не понадобились. В existing saved/editor fixtures добавлены close/reopen готовой
страницы и поздний body после повторного открытия. Новый iframe, одно source read,
сброс preview-ввода, dirty guards и отсутствие Run cancellation подтверждены.
[Evidence и границы](05_TEST_MATRIX.md#результаты-p2c--2026-11-11). Изменены tests/docs,
без нового runtime/UI и bump версии. Windows/Office и прежнее визуальное принятие открыты.

## P2b: цели и возможности HTML — In Review, 2026-11-11

Общий Application resolver проверяет exact source/scope/kind по metadata существующих
owners. Typed descriptor приходит в прежних preview/page/workspace responses, без
отдельного запроса, store или дополнительной гидрации. Saved snapshot остаётся
неизменяемым, raw HTML — inert source; доступное live чтение сохраняется при запрете записи.
UI проверяет адресата до применения и использует подтверждённые capabilities;
неподдержанный preview не создаёт iframe, поздний/чужой ответ не заменяет источник.

Пять focused host-neutral checks и три browser fixtures прошли. Старый raw import
fixture падает также без P2b, вынесен в риск. [Evidence и ревью](05_TEST_MATRIX.md#результаты-p2b--2026-11-11).
Визуальное принятие и Windows/controller delivery открыты. Model semantic bindings,
plugin grants, managed source preview и runtime session lifetime этим срезом не заявлены.

## P2a: самостоятельный проект из снимка — In Review, 2026-11-10

«Сохранение проекта» → «Создать проект из снимка» копирует exact сохранённые
HTML/CSS/JS в managed workspace. Application import использует существующие
WorkspaceStore/WorkspaceFileService; исходники — файлы, история/evidence — CAS.
Каталог открывает проект после read-back всех файлов. Исходный artifact не меняется.
Пределы: 100 файлов / 16 МиБ UTF-8; data bindings, распознаваемые SDK/vendor/external
references отклоняются до записи. `Created` доказывает копирование исходников,
не исполнение страницы; сборка/preview самостоятельного проекта ещё впереди.

Typed результаты Created/Rejected/Unknown различают подтверждённую копию, отказ
до записи и возможный эффект. Unknown сохраняет папку для явной проверки без replay.
Поздний ответ не меняет активную вкладку; повторное открытие использует созданный
workspace. Автоматического чата/Run нет. Новый revision store или adapter не введён.

Пять focused host-neutral checks и два browser fixtures прошли; старый transfer
roundtrip падает также на чистом `c3a9427e` и записан отдельным риском. Office/VSTO
не запускались. [Scope и остаток](04_MIGRATION_PLAN.md#p2a-импорт-самостоятельного-проекта--in-review-2026-11-10),
[проверки и скриншоты ревью](05_TEST_MATRIX.md#результаты-p2a--2026-11-10).
Визуальное принятие пользователем, pointer routing и Windows delivery остаются Open.

## Продолжение: проверки P1 и граница P2a — 2026-11-10

`browser-views` исправлен под текущий responsive toolbar: «Обсудить» открывается
через «Инструменты страницы», когда кнопка скрыта. Полный live HTML/plugin сценарий
прошёл на локальном Chrome 157.0.8103.51: annotations, exact recipient, retained iframe,
restore/move, narrow/zoom и late cleanup. Это исправление теста, не pointer routing.

`BROWSER_POINTER_PROBE=1` теперь проверяет доставку pointerdown/up/click и печатает
parent/iframe recipients. Повтор поймал все три события в «Сохранённый HTML A» вместо
«Сохранить раскладку», без таймаута на следующем действии. Обычный saved fixture прошёл
(2 exact HTML reads, retained input, source ≤32K, 420 px/125%). Изолированные простые страницы
без RNAssistant не воспроизвели сбой; точная причина и Windows evidence остаются Open.
Ненадёжные CSS/таймерные обходы в production не добавлены.

На этом этапе подготовлен конкретный [P2a](04_MIGRATION_PLAN.md#p2a-импорт-самостоятельного-проекта--in-review-2026-11-10):
самостоятельный workspace из exact снимка, managed source files под existing workspace
owner, CAS для версий/evidence. Последующая реализация import описана выше.

## История сохранённого HTML — исправлена host-neutral, 2026-11-10

Typed getSavedHtmlHistory разрешает current document publication из exact target;
существующие DocumentArtifactStore и ArtifactLibraryProjectionService возвращают
метаданные порциями по 50. Нет чтения HTML bodies, нового store или смены chat selection.
Clock доступен без captured head и перечитывает список при каждом открытии. Поздние
ответы старого меню не применяются; retry stale cursor начинает с первой страницы.
Открытый снимок, iframe, файл исходника и ввод сохраняются.

Прошли CAS history fixture (52 публикации другого чата, foreign document, paging,
stale cursor), typed artifact bridge и browser-saved-views (свежая версия, no captured
head, late menu response, другой активный чат, сохранённый iframe/input, 420 px/125%).
C# проверен в isolated checkout d0ab9d09 + этот fix; в основном checkout параллельные
agent/storage edits. Windows delivery и визуальное принятие пользователем открыты.
[Меню истории](../ui/review/browser-history.png): v3 — текущая публикация, v1 — открытый снимок.

Pointer-routing остаётся Open: быстрые клики по menuitem попадают в sandboxed iframe
даже с нативным modal dialog. `BROWSER_POINTER_PROBE=1` сохраняет воспроизведение;
обычный saved fixture использует keyboard layout actions, новые history actions —
мышь над source view. Ненадёжные CSS/top-layer эксперименты удалены.

## Строка браузера и обновление HTML — In Review, 2026-11-10

Реализовано уточнение пользователя: слева назад/вперёд/reload/аннотация, затем адрес
и provider actions. Фокус раскрывает адрес на всю строку и показывает инструменты;
blur/Escape сворачивает. До 600 px правая группа открывается через ⋯. Постоянная
подпись снимка и отдельная title/path row удалены. Название/версия/хранение открываются
у адреса через общий ActionMenu. CAS/files — место хранения; export не объявляется
созданием независимого workspace. Публикация plugin сохраняет прежние проверки.

Исправлен Web bug живого HTML: reload вызывал только reloadPreview локальной копии,
а resourceChanged уведомлял только привязанные данные. Теперь reload читает metadata
через existing getHtmlWorkspace/RefreshSelected, document notice обновляет чистую view,
hidden view читает при activate. Новое событие во время read требует свежего результата.
Dirty buffer сохраняется; смена исходника блокирует stale write. C#/bridge не менялись.

Проверены saved Browser, live HTML editor и plugin selection в локальном Chromium.
Новые HTML checks покрывают manual reload, другой agent/chat, hidden activation,
событие во время read, чужой scope, повторную generation и dirty buffer. Saved fixture
проверяет ширину адреса, неизменную геометрию iframe, focus/menu/narrow/125%, retained
input/modes и late cleanup. Два HTML читаются дважды, два release открываются дважды;
source page 32K, overflow 0. Ранее пройденный lifetime повторно не запускался.

[Строка](../ui/review/browser-address-wide.png),
[фокус адреса](../ui/review/browser-address-focused.png),
[420 px / 125%](../ui/review/browser-address-narrow.png),
[сведения](../ui/review/browser-address-info.png),
[live HTML](../ui/review/browser-address-live.png), [замеры](../ui/review/browser-address.json).

Открыто: saved exact view перечитывает выбранный снимок, его lifetime не превращён в
follow-latest. Независимый managed workspace, CAS → workspace/plugin, Diff и native URL
остаются следующими domain-срезами. Свежая saved history реализована адресным чтением
метаданных в срезе выше; выбранный exact snapshot автоматически не заменяется.
Pointer routing поверх iframe по-прежнему Open: saved layout restore проверяется
клавиатурой, быстрый mouse click воспроизводит старый риск. Windows/Office и визуальное
принятие пользователем открыты. Build/commit/push не выполнялись.

## Продолжение P1: toolbar и обсуждение — In Review, 2026-11-05

Пользователь одобрил концепцию и выбрал **отдельный Chat View рядом в Dockview**.
Общий PreviewSurface получил compact icons: play/code, selection/clock/layers/chat;
редкие plugin actions перенесены в общий ActionMenu. Публикация использует прежний guarded handler; после уточнения — upload icon. HTML source modes сохраняют aria/title/pressed и прежние guards.
История HTML/plugin также перенесена в ActionMenu: собственные details/popover и CSS
удалены; проверены pagination, Escape/focus и меню при 420 px / 125% без обрезания.

«Обсудить» повторно открывает exact существующий Chat View рядом; narrow использует
одну группу. Draft/composer не клонируются, iframe не перезагружается. Plugin view
закрепляет workspace/session по явному выбору исходного чата, ошибки и annotation
drafts адресуются ему. Исправлен preserveChatOwner: переход из менеджера к Browser
больше не возвращает старый чат через отложенную загрузку project catalog.

Пройдены Chromium проверки: browser-saved-views, browser-lifetime,
plugin-view-selection, html-editor-views. browser-views остаётся нестабильным. Проверены reuse/draft retention, две загрузки
двух saved HTML/releases, 32K source pages, одна iframe load за пять restores,
аннотация после выбора другого чата, late cleanup, narrow/125% и keyboard.
Baseline и повторные live HTML annotation проверки периодически завершаются timeout;
последняя попытка красная. Зафиксировано попадание pointer events в iframe под
кнопкой сохранения без вызова stage; устойчивого исправления нет.
[Открытый риск](../stabilization/RISK_REGISTER.md#browser-annotation-pointer-routing--open-2026-11-05).

[Компоновка с чатом](../ui/review/browser-toolbar-wide.png),
[420 px / 125%](../ui/review/browser-toolbar-narrow.png),
[сверка controls с frozen Demo](../ui/review/browser-toolbar.json).
Концепция Approved; production visual acceptance **Open**. CAS/project ownership,
управляемые и файловые workspace, настоящий Diff, browser sessions и external native
URL не реализованы этим presentation-срезом. Runtime/bridge/storage не менялись;
Windows/Office/WebView2 qualification остаётся отдельной. Commit/push/build не выполнялись.

## Вход и документы

| Документ | Назначение |
|---|---|
| [00_SOURCE_SPEC](00_SOURCE_SPEC.md) | Точная копия задания пользователя, historical target input |
| [01_AUDIT](01_AUDIT.md) | Реальные routes/owners, reuse/remove и открытые gaps |
| [02_ARCHITECTURE_ADR](02_ARCHITECTURE_ADR.md) | Proposed решения, native alternatives и Windows spike |
| [03_CONTRACTS](03_CONTRACTS.md) | Proposed semantic API/runtime bindings и P1 presentation interface |
| [04_MIGRATION_PLAN](04_MIGRATION_PLAN.md) | Очередь P0–P7, конкретный P1, проверки/removal/rollback |
| [05_TEST_MATRIX](05_TEST_MATRIX.md) | Existing coverage, минимальные новые сценарии, Windows gates |

Исходное приложение: `RNAssistant_Universal_Browser_Agent_Spec(1).md`, сохранено
2026-11-04 без изменения байтов. SHA-256:
`0776c4c3d8f5a02c596b1a5b1d83554e8aa6f9a443b191ba7b27f86250f373d6`.
Аудит закреплён за `b9173e0a7c060e7c60fceb2e2a4ce39996f4b5db` (`main`).
Прежний предварительный обзор был на `ee43b98a`; источник задания ссылается на ещё
более ранний `2c8de087`. Эти hashes не заменяют фактический baseline аудита.

## Исходный P0/P1: выполнено и статус решений

- Сохранён исходник и подготовлены шесть документов по фактическому коду.
- Согласовано пользователем: сначала документы + план; затем продолжение P1.
  Страницы — вкладки общего Dockview, без собственных вложенных tabs.
- Учтены existing Browser/PreviewSurface, exact saved views, HTML lineage, plugin
  publication/grants и annotation intake. Повторная миграция удалённых viewers не нужна.
- Зафиксированы отличия от задания: runtime-only IDs, core reads, SessionEvent вместо
  отдельного AgentEventLog, разные semantics HTML restore и plugin active-release restore.
- P1 реализован от `c6659ed2`: PreviewSurface владеет read-only address и modes,
  BrowserObjectView — lifecycle связи, HtmlArtifactView — содержимым/mode/file/page.
  Локальный mode toolbar и его CSS удалены; повторной модели/iframe нет.
- Other providers, exact links/versions/export, limits и data authority сохранены.
  Остальные архитектурные решения Proposed; native renderer не выбран без Windows evidence.

P0 documentation подготовлен; P1 browser implementation проверен локально.
На момент исходного P1 срезы P2–P7 ещё не начаты; текущий статус — в начале документа.
Native/Office qualification не закрыта.
Глобальные приоритеты стабилизации и очередь других UI-срезов не меняются.

## Исходный P0/P1: проверки и ограничения

P0: source integrity, diff и 88 внутренних ссылок/anchors проверены без ошибок,
builds/tests не запускались. P1: JS syntax/diff и три browser fixtures на baseline
и после изменения прошли: `browser-saved-views`, `browser-views`, `browser-lifetime`.
Среда: Chrome 157.0.8103.38 на macOS, synthetic bridge, без моделей/Office.

Сохранены два чтения двух HTML и два открытия двух plugin releases при пяти выборах,
32 000 source chars/page, retained input и одна загрузка Browser iframe после пяти
layout restores. Expanded saved fixture проверяет readonly/Enter, единственные controls,
ready narrow/zoom и очистку старого descriptor при failed reload. Live HTML/plugin
annotations проходят. [Замеры до/после](../ui/review/browser-p1.json).

Локально просмотрены ready source/preview screenshots. [Wide source](../ui/review/browser-p1-source-wide.png),
[420 px / 125%](../ui/review/browser-p1-source-narrow.png): overflow 0, address 30 CSS px,
mode button 28 CSS px, font 12 px. Frozen Demo report/source измерен при тех же
viewport/zoom: button 24 px, font 12 px. Сохранён существующий production view-switch,
новых size overrides нет; визуальное принятие этой компоновки остаётся открытым.
Builds, harness и VSTO/Office validation не выполнялись; Windows evidence отдельно.

Открыты UB-1–UB-5 из [аудита](01_AUDIT.md): native embedding; semantic bindings;
session/annotation lifecycle и screenshot privacy; code/data revision distinction;
raw HTML/plugin authority. Windows evidence только по фотографиям, без запроса файлов.
Новые adapters/dual paths не введены; MIGRATION_MAP не изменяется преждевременно.
Параллельные изменения agent runtime/UI не входят в P0/P1 и не изменялись в их рамках;
исторический аудит сохраняет свой baseline. Commit/push не выполнялись.

## Ровно следующий шаг

Далее остаток P3: source/release presentation и сверка существующих capabilities;
новый descriptor добавлять только при конкретном consumer/расхождении.
Instance owner, grants, публикация и данные остаются у прежних owners.
[План](04_MIGRATION_PLAN.md#развитие-после-p1). Отдельный session слой не нужен заранее.
Визуальное ревью P1/P2a/P2b, pointer routing и Windows evidence — отдельные открытые gates.
