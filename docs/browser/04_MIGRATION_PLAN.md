# Universal Browser: план реализации

Статус: P0 documentation подготовлен; P1, P2a и P2b **реализованы / In Review**;
P2c automatic lifecycle — **проверен host-neutral**; P3a/P3b plugin fixes — **In Review**;
P5a native pilot и P6a semantic reads — **реализованы / In Review**;
P4 и остальная часть P5–P7 — **Planned**.
Дата: 2026-11-11. Пользователь продолжил работу после
docs-only P0; реализованы описанные ниже срезы. Основание:
[исходник](00_SOURCE_SPEC.md), [аудит](01_AUDIT.md), [ADR](02_ARCHITECTURE_ADR.md),
[контракты](03_CONTRACTS.md). Общая очередь UI остаётся в
[UI Migration Plan](../ui/UI_MIGRATION_PLAN.md); названия P0–P7 здесь относятся только
к Universal Browser, не к замороженному Dockview Demo.

## Этапы и приёмка

| Этап | Owner и результат | Gate и удаление заменённого пути |
|---|---|---|
| P0 Audit/ADR | Web/Application: реальная source/authority map, reuse/remove, contracts, Windows spike protocol | Docs-only diff/links/source integrity; native и runtime evidence остаются открыты |
| P1 HTML Artifact | Web: address/modes в общем PreviewSurface, один saved HTML consumer | Сохранены current features/limits; browser baseline + визуальное ревью; локальный mode toolbar удалён |
| P2 Targets/Lifecycle | P2a import и P2b HTML target/capabilities реализованы; P2c existing lifecycle проверен host-neutral без production-правок | Hide/move/restore, close/reopen и late cleanup проходят; dirty guards сохранены, Run cancellation не вызывается; Windows отдельно |
| P3 Plugin | Plugin Application + Web: source/candidate/release adapters, общий navigation/modes UI | Grants/revoke/check/publish/restore неизменны; удалены переведённые provider UI controls, instance ownership остаётся |
| P4 Versions/Annotations | Domain owners + Web: bounded diff, version semantics, events-backed annotations и anchors | Branch/active pointer, unresolved/verified resolution, view-close retention; нет BrowserRevisionStore/Annotation DB |
| P5 External WebView2 | Native adapter + Application: renderer по результату spike, profiles/navigation/popup/download policy | Windows embedding matrix либо явный управляемый separate-window fallback; привилегированный Shell control не используется |
| P6 Agent automation | Application/ToolRuntime/Resource Gateway: semantic reads/actions/verify и evidence | Local fixture workflow, stale/unknown/user conflict/shared sessions, bounded output/redaction; без второго kernel/agent roles |
| P7 Qualification | Владельцы затронутых контуров: stress/security/performance/recovery | Host-neutral + отдельные Windows/Office results, визуальное принятие, закрытые removal gates; release решается отдельно |

Native spike подготовлен P0 и может выполняться независимо от P1–P4. P5a по решению
2026-11-11 начинает WinForms embedding до доступности Windows; его qualification
зависит от native evidence. Внутренние UI-срезы не ждут Windows. Не начинать одновременно миграцию
всех viewers. Каждый этап заканчивается обновлением canonical docs, фактическим
evidence и ровно одним следующим шагом в [Progress](06_PROGRESS.md).

## Порядок оставшейся работы — 2026-11-11

**Approved:** пользователь продолжил P6a после оценки очереди и переноса P4.
Приоритет — первый рабочий сценарий агента в браузере. Это изменение
очереди, не исключение возможностей из общей цели и не новые постоянные ограничения.

| Срез | Достаточный результат | Порядок / зависимость |
|---|---|---|
| P1–P3 baseline | Существующие HTML/plugin views, импорт, check/publish/restore и automatic lifetime; P3b — `13a4a5b8`. Остаются visual acceptance и известный focus/pointer gap | Не начинать новый P3c ради общего DTO; plugin capability projection расширять вместе с конкретным consumer |
| P5a ручной Browser | Уже подготовленный пилот одной внешней страницы; исправления по реальному Windows HWND/DPI/focus/cleanup evidence | Проверить на первом доступном Windows-запуске; независимо от P4. До evidence не расширять embedding на все viewers |
| P6a чтение | Один external target из P5a → bounded semantic observation через existing Resource Gateway; exact runtime binding, document generation и исключение sensitive values | Реализован / In Review; host-neutral Gateway и Chrome capture проверены, WebView2/controller delivery на Windows открыт |
| P6b действие | Одна локальная fixture: открыть → прочитать → заполнить → нажать → проверить. Existing ToolRuntime/policy/evidence; stale/ambiguous target и unknown effect без replay | После P6a; проверка эффекта и конфликта с пользователем входят сразу, не откладываются в P7 |
| P4a сравнение | Bounded code/source diff выбранной пары через владельцев HTML/plugin versions; явно отличать code revision от данных preview | После первого рабочего P6b либо раньше при конкретной потребности сравнивать перед apply/restore |
| P4b аннотации | Persistent lifecycle/reanchoring/verified resolution поверх existing events/CAS/attachments | Отдельно после P4a или по потребности вести замечания между открытиями; не условие P5/P6a–b |
| Остальные P5/P6 capabilities | Дополнительные targets/страницы, persistent profiles, разрешённые popup/download, screenshots, shared sessions | Самостоятельные продуктовые срезы после базового workflow; screenshot включается только с privacy gate. Не создавать весь перечень API заранее |
| P7 qualification | Совместная проверка поддержанных сценариев, Windows/Office, security/performance/cleanup и release evidence | Локальные проверки сопровождают срезы; широкий итоговый прогон — на интеграционной точке/кандидате, а не после каждой правки |

Перенос P4 не отменяет existing history/restore и отправку visual selection в чат.
P6 actions используют observations/effect verification, а не annotation status или
Browser Diff. Screenshot/annotate/compare не нужны первому P6a–b и не изображаются
поддержанными. Для будущего P4 сохраняются exact source/revision, различие code/data
и существующие evidence owners; новый annotation service/store заранее не создаётся.

Один срез — один пользовательский результат у существующего owner, с минимальной
проверкой его риска. Предыдущий pass переиспользуется, пока релевантный код, tests,
dependencies и environment не изменились; новый baseline нужен для конкретного
сравнения/дефекта. Не повторять общий Browser-suite ради очередного commit; известный
focus/pointer gap разбирать отдельным адресным сценарием. Docs-only — diff/links,
pre-commit version check сохраняется. Это применение [действующих правил](../development-rules.md),
без новых обязательных review, отчётов или общих build/test gates.
Windows evidence и независимые domain fixes можно вести параллельно; изменения
одного Shell/native owner выгоднее делать последовательно, без повторного аудита
одних и тех же файлов в нескольких задачах.

## P6a: чтение одной external page — In Review, 2026-11-11

Owner: Office ResourceGateway/ResourceAuthority и существующий native page owner.
`core.find scope=browser` и `core.read` используют semantic target одной страницы,
закреплённой за chat при Open. Bound Workspace использует прежний `type=office`
маршрут; Workspace без Office binding этим срезом не подключён. До/после capture
проверяется exact document guard; во время ожидания WebView2 Office gate не держится.
Close/rebind/navigation и cancellation не позволяют принять поздний снимок.

Фиксированный script выполняется в isolated world: bounded role/name/state/text,
без текстовых значений controls, URL/title, private regions, frames и shadow DOM.
Existing Gateway публикует capture в CAS/authority; model не получает runtime IDs.
Навигационное поколение не является revision изменяемого DOM: новое чтение получает
новый content hash, а element binding/action verification остаются P6b.
Новых Browser session stores, UI controls или model-facing CDP/evaluate нет.
[Контракт](03_CONTRACTS.md#p6a-semantic-read), [проверки](05_TEST_MATRIX.md#p6a).

## P1: конкретный production slice

Реализован от baseline `c6659ed2`; ниже сохранено задание среза.
Три browser fixtures до/после проходят, заменённый toolbar удалён. Результат и
открытое визуальное принятие — в [Progress](06_PROGRESS.md).

Owner: Web Browser/PreviewSurface; data owner — existing HtmlArtifactView/export.
Цель: сохранённый HTML artifact получает общие address/modes без потери своего
точного target, versions/download и ограниченного исходника.

1. На актуальном чистом baseline запустить три browser fixtures из [матрицы](05_TEST_MATRIX.md#p1-checks).
   Снять narrow/zoom/focus screenshots и текущие load/read/source-size показатели.
   Существующий mock product служит fixture; новый Demo не создавать.
2. Расширить [app-preview-surface.js](../../web/js/app-preview-surface.js), `attach`,
   интерфейсом [P1 presentation](03_CONTRACTS.md#p1-presentation). Общие controls:
   read-only address и «Страница / Исходник»; existing title/path/status/actions остаются.
   Keyboard/aria используют общие form-control/view-switch.
3. В [app-browser-object-view.js](../../web/js/app-browser-object-view.js), `load/reload/stop/dispose`,
   связать loaded presentation с surface. Ready hidden DOM и текущий mode сохраняются;
   callbacks/leases поздней загрузки не применяются к закрытому view.
4. В [app-html-artifact-view.js](../../web/js/app-html-artifact-view.js), `load/mode/create`,
   вернуть presentation/selectMode вместо локальных mode buttons. File picker остаётся
   над исходником в provider content и скрыт в preview. Остаются один iframe,
   `renderSource` с pagination и position per file, artifact version picker/actions.
   Адрес — текущий target.resourceUri; выбор другой версии использует прежний Dock route.
5. В [app-preview-surface.css](../../web/css/app-preview-surface.css) использовать common
   tokens для document controls. Удалить заменённые `.browser-document-toolbar` rules;
   сохранить только необходимый layout file picker/source. Проверить consumers через rg.
6. В existing `browser-saved-views.test.js` добавить только отсутствующие assertions
   read-only address, единственного mode owner и отсутствия unsupported actions.
   Повторить три fixture, сравнить baseline, провести визуальное ревью.

P1 не меняет app.js boot, bridge, model tools, storage, annotation lifecycle, plugin
grants или HTML editor. Новых script files/loader/registry не требуется.

### Приёмка P1

- Artifact из каталога/истории открывает тот же exact chat/document/URI; version picker,
  копирование ссылки, export и сообщения ошибок работают через прежних owners.
- Input в preview, выбранный файл/source page и mode переживают switch/hide/move/split/
  layout restore; close освобождает provider и поздние leases. Это существующий P1
  view lifetime, подтверждённый решением пользователя от 2026-11-11.
- Нет второго iframe/второго mode switch и полной копии source в DOM. Сохраняются
  лимиты 16 MiB preview transfer, 100 project files, 32 000 source chars/page,
  existing snapshot/download bounds и максимум шесть material views.
- 1280×900, 420 CSS px и 125% zoom: адрес/название не раздвигают Shell, controls
  доступны с клавиатуры, hidden surface не перехватывает focus. Размеры shared controls
  сверяются с принятым Dockview reference при одинаковом viewport/state.
- Diff и web navigation отсутствуют, пока capability не реализована. Read-only address
  не создаёт ложного ожидания открытия сайта. Plugin и live HTML продолжают тот же путь.
- Изменения готовы к review: запуск, 2–5 действий, изображения/наблюдения, ограничения.
  Визуальная приёмка и Windows qualification не считаются завершёнными по автотесту.

## P1: компактный toolbar и связь с Chat View — In Review, 2026-11-05

Согласованное продолжение: существующие icons/components, play/code вместо текста,
annotation/history/discuss icons; rare plugin actions в ActionMenu. ConversationPanel
открывается рядом средствами DockWorkspace, один exact экземпляр. Plugin annotation
target закрепляется явно; навигация не меняет адресата. Исправлен общий preserveChatOwner.
Удалены заменённые plugin action DOM/CSS и локальные размеры иконок. Нового adapter,
registry, store или bridge нет. [Результаты и оставшийся scope](06_PROGRESS.md).

P2 требует отдельного domain решения: независимый project owner, managed CAS или
рабочая папка как backing, сохранённые версии отдельно от текущих правок, ссылки
документов/чатов и GC roots. Публикация plugin остаётся отдельным действием, данные
плагина — у прежнего owner. UI не создаёт второй durable version/annotation store.
При появлении реального сравнения Diff использует иконку двух колонок с +/−.

## P2a: импорт самостоятельного проекта — In Review, 2026-11-10

Реализованы [project ownership/backing](02_ARCHITECTURE_ADR.md#p2a-самостоятельный-проект-и-backing--in-review-2026-11-10),
далее resolver и sessions. Scope P2a: явный import exact сохранённого HTML
в новый managed workspace; выбранная папка подключается существующим directory path.
Managed здесь — файлы в каталоге приложения плюс existing CAS history, не второй
CAS-only source store. Owner — Application Workspace + существующие HTML export/file owners.

1. Typed command `createHtmlProjectFromArtifact` передаёт exact source и managed backing.
2. Preflight проверяет entry point, безопасные пути/коллизии, 100 файлов / 16 МиБ;
   document-bound data, распознаваемые SDK/vendor/external refs отклоняются до записи.
3. Application import создаёт скрытый workspace, file owner пишет и сверяет SHA-256;
   только затем association публикуется. Сохранённый исходник и его история не меняются.
4. «Сохранение проекта» → «Создать проект из снимка» открывает existing project navigation.
   Повторный переход использует тот же workspace; поздний ответ не меняет навигацию.
   Новый чат/Run автоматически не создаётся; source editor и чат остаются у workspace owner.
5. Три focused import checks, typed bridge и existing file-path check проходят host-neutral;
   новый browser flow и saved Browser fixture проходят. Старый transfer roundtrip падает
   также на чистом baseline при следующем HTML-tool вызове; риск записан отдельно.

P2b реализован ниже. P2c проверил автоматический lifetime существующих views:
закрытие вкладки завершает страницу; detached sessions и новый UI не вводятся.
Runtime ID не записывается в Dock layout.
Pointer-routing/Windows evidence остаются открытыми и не блокируют domain-срез P2a.
Import и managed source backing реализованы. Перенос зависимостей, standalone preview,
native/agent session APIs, Windows/controller delivery и пользовательское визуальное принятие открыты.

## P2b: HTML resolver и capabilities — In Review, 2026-11-11

Scope: existing saved HTML, raw HTML source и live HTML editor. Stateless Application
resolver использует проверенные metadata прежних owners; различает kind/availability
и сверяет exact source/scope. Descriptor передаётся существующими responses без
добавочного запроса и без чтения тела для определения возможностей.

Web использует подтверждённые modes/actions. Foreign document/revision response
отклоняется до скачивания saved body; source-only не создаёт iframe. Read-only live
сохраняет preview и блокирует edit/restore/annotations. Не заменены редакторы,
data/revision owners, bindings, caches или operation guards. Классификация raw HTML
в native reader переключена на общего Application owner, прежняя копия удалена.

Пять focused host-neutral checks и три browser fixtures проходят. Baseline/final:
saved reads 2 при пяти выборах, source 32K, controls 30 px, overflow 0 на 1280/100%
и 420/125%; retained draft/frame и late cleanup сохранены. Отдельный raw import
fixture падает также без P2b: [evidence](05_TEST_MATRIX.md#результаты-p2b--2026-11-11).
UI/Windows acceptance остаются открытыми. Проверка P2c завершена ниже;
plugin/semantic tools/native URL не входят в P2b.

## P2c: автоматический lifecycle — scope Approved, 2026-11-11

Owner: Web BrowserObjectView/BrowserViewer, DockWorkspace placement и существующие
domain owners. Решение пользователя заменяет прежний план detach/reattach, списка
сессий и отдельного завершения. Начатый незавершённый код этого варианта удалён;
P2b (`36a44073`) сохранён. Нового registry, bridge API или UI для sessions нет.

Проверка **завершена host-neutral 2026-11-11**: четыре адресных browser fixtures
прошли на текущем production-коде. Добавлены только отсутствующие close/reopen и
late-body assertions в существующие saved/editor fixtures; дефектов, требующих
production-правок, не обнаружено. [Evidence и границы](05_TEST_MATRIX.md#результаты-p2c--2026-11-11).
Ниже — выполненный scope проверки, не очередь повторных прогонов.

1. Сначала saved HTML: сверить existing coverage и проверить готовую страницу при
   switch/hide/move/split/restore, затем close → удаление iframe → reopen с новой
   загрузкой и исходным вводом. Добавлять только отсутствующие assertions.
2. Проверить поздний export/read reply после close: lease закрывается, страница не
   воскресает. При scope switch ответ не попадает в другую view; закрытие не отменяет
   импорт или Run. Ограничения открытых material views сохраняются.
3. Затем сверить live HTML dirty/`canClose` и provider cleanup с существующими checks;
   plugin lifetime сверять с его instance owner в P3, не переносить его в новый store.
4. Исправлять только воспроизводимые gaps у текущего владельца. Если поведение уже
   покрыто, достаточно evidence/docs; не вводить сервис ради формального выполнения P2c.
5. Пользовательские данные/история/annotations принадлежат прежним domain owners;
   DOM/формы после close/restart не replay. Windows/WebView2 evidence отдельно.

Независимая фоновая browser session возможна позднее только с явным сценарием,
owner и правилами завершения в P5/P6. Она не является условием текущего P2c/P3.

## P3a: ошибка исходников без подмены release — In Review, 2026-11-11

Сверка показала: source/candidate/release уже используют общий Browser и текущие
instance owners. Воспроизведён конкретный gap: при первом failed check workbench
автоматически открывал active release. `PluginView.ready` теперь выбирает source
preview и ранее явно выбранную публикацию разными ветками; ошибка source оставляет
сообщение без iframe. Existing screen при failed refresh сохраняется. Watcher,
ручной retry, явное открытие active release и публикация остаются у прежних owners.
Новых DTO, service/registry, bridge commands или UI controls нет.

Focused regression red → green и `plugin-view-selection` прошли; общий `browser-views`
остановился до нового сценария на focus assertion и не засчитан как pass.
[Evidence и ревью](05_TEST_MATRIX.md#результаты-p3a--2026-11-11).
## P3b: актуальность восстановления release — In Review, 2026-11-11

В сохранённой вкладке доступность restore зависела от active release на момент её
создания. `PluginReleaseView` теперь читает текущую публикацию через existing
`getPlugins` при открытии меню. Guard принадлежит команде этого меню, а Dock route
содержит только identity сохранённой версии и title. Loading/error/missing head
не разрешают restore; late response не меняет новое меню. Runtime conflict между
чтением и действием остаётся отказом без retry. Pinned iframe не перезагружается.
Новых contracts/services/controls нет. Existing `browser-saved-views` прошёл после
воспроизведения старого дефекта. [Evidence/ревью](05_TEST_MATRIX.md#результаты-p3b--2026-11-11).

Остаток P3 — source/release presentation и нужный consumer plugin capabilities:
сначала сверить существующие DTO/views. Общий descriptor без конкретного потребителя
и новый session слой не требуются. Native renderer/agent automation отдельно.

## P5a: WinForms сразу внутри Dockview — In Review, 2026-11-11

Owner: Office/WebView — непривилегированная страница и отдельный владелец размещения;
Web — existing Dockview/PreviewSurface. Пилот включается `RNA_BROWSER_PILOT=1`,
использует одну внешнюю вкладку с HTTP(S), навигацией, локальной контрольной страницей
и экранной диагностикой. Separate-window реализация появится только при конкретном
блокере embedding; общие browser policy/navigation/profile не копируются.

Локально проверены typed admission и Shell lifecycle с synthetic native responses.
Следующий gate этого среза — первая Windows compilation и проверка настоящего HWND:
focus/IME, DPI, overlays, close/process exit. До неё P5 целиком не закрыт. Multi-tab,
persistent profiles, разрешённые popup/download и agent actions в P5a не входят.
Rollback — отключить флаг и закрыть экспериментальную вкладку; внутренних viewers
и пользовательских данных этот путь не заменяет. [Запуск/проверки](05_TEST_MATRIX.md#p5a).

## Развитие после P1

P2 использует typed target/import contracts и existing lifecycle owners.
Новые session records/commands понадобятся по scope native/agent P5/P6;
runtime resource binding остаётся у Resource Gateway.
Web target adapters делегируют HtmlWorkspaceView/Source/Editor и MaterialView; raw HTML
остаётся inert до явного preview. Exact route keys/rna ссылки сохраняются. Runtime
session identity нельзя сериализовать в Dock layout как восстановленную живую страницу.

P3 использует PluginApplicationService и PluginStore без смены публикации/данных от
выбора вкладки. `plugin_source` и `plugin_release` — разные typed targets. Старые
provider-specific toolbar sections удаляются при переключении каждого consumer.

P4 расширяет существующие ArtifactVersions/HtmlWorkspaceNavigationService и TextDiff;
source pair разрешает owner. Annotation lifecycle становится typed events поверх
существующего attachment body. CAS retention учитывает новые refs. Новые persisted
форматы переключаются вместе с readers; прежние пользовательские данные не удаляются,
несовместимое состояние обозначается явно, без hidden fallback/dual-write.

P5 реализует .NET 4.8 native renderer port рядом с Office/WebView. BrowserHost verifier
остаётся optional CLI механизмом; общую CDP семантику переносить лишь при конкретном
consumer и локальной проверке, без механического копирования private verifier.

P6 регистрирует закрытые semantic tools/skill в existing catalog, не меняя v8 loop;
новые source snapshots идут через Resource Fabric/CAS и compiler, а не отдельную
историю. Diagnostics/Inspector расширяется существующими typed projections.

## Rollback и удаление временных путей

Работа последовательно на `main`, без автоматических commit/push. P1 — изолированный
UI diff без данных; rollback — его обычный revert. Пока изменение не принято,
не создавать дополнительный feature toggle для второго toolbar.

Постоянный target adapter — явная граница делегирования, не временный migration shim.
Если нужен временный dual path/fallback, до его подключения добавить в
[MIGRATION_MAP](../stabilization/MIGRATION_MAP.md) owner, consumers, flag при необходимости
и точный removal gate. При принятии среза переключить consumers и удалить старый код.
Docs-only P0 не вводит ни adapter, ни migration entry с ложным статусом «реализовано».

После durable P2/P4 rollback прекращает новую capability, но сохраняет читаемость
уже записанных facts/CAS refs; возможные внешние эффекты не откатываются replay.
Любая новая схема требует отдельного explicit version/reset решения перед кодированием
этого среза. Product bump выполняется один раз на завершённый продуктовый срез по
[Versioning](../operations/VERSIONING.md); docs-only P0 и промежуточные commits
обходятся без bump. Перед фактическим commit обязателен existing
ValidateVersionFormat. Commit/push в P0/P1 не выполнялись.
