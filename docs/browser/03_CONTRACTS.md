# Universal Browser: предлагаемые контракты

Статус: **Proposed**, 2026-11-04. Owner: Core contracts / Application services;
Web и native host — consumers. Это минимальные границы будущих P2–P6, не объявление
существующего runtime API. P1 presentation contract в конце реализован в Web,
статус In Review; P2a import и P2b HTML resolver реализованы ниже.
Автоматический lifetime вкладки Approved 2026-11-11; новые session APIs и P3–P6
остаются Proposed за исключением реализованных ниже P4a baseline, P6a read,
P6b local actions, P6c presentation, P6d control/access, P6e navigation, P6f external actions и P6g research observations.
P2c не добавляет UI/bridge управления detached sessions.
Действующие [v8](../protocols/CONVERSATION_RESPONSE_V8.md),
[Resource Fabric](../resource-fabric.md), [Tools](../tool-library.md),
[events](../session-events.md), [Plugins](../plugins.md) сохраняются.

<a id="p6g-research"></a>
## P6g: сохранённые наблюдения для исследования — In Review, 2026-11-11

`core.read {target:"Browser page: external"}` сохраняет immutable observation и
возвращает `snapshotTarget: "Browser observation: <UTC capture time>"`. Native owner
фиксирует адрес и время, isolated capture — bounded title/text. В результате есть
source URL без credentials/query/fragment; это адрес источника с явно указанными
пропусками, не обещание canonical URL. Title и содержимое страницы недоверенные.

`ExternalBrowserResourceProvider` использует существующие scoped ResourceAuthority/
revision views/CAS: mutable page head нужен guards действий, отдельная immutable
identity — фактам исследования. Текст один в CAS; маленький typed source metadata
view даёт discovery без DOM capture. Provenance — `immutable-snapshot` dependency
на exact live observation; переход/unknown live effect её не актуализирует и не
удаляет исторические факты. ModelContextCompiler и storage protocol не изменены.

`core.find {scope:"browser"}` показывает сохранённые наблюдения; query ищет их
metadata, `core.search` — текст выбранного наблюдения. `core.read` по snapshotTarget
читает exact CAS после закрытия/перезапуска без открытия сайта. Discovery paged/bounded;
точный semantic target разрешается по времени в существующем head index, без полного
сканирования истории. Чужой chat, ambiguous time, missing/corrupt payload дают отказ,
без подмены текущей страницей. Восстановление в контексте подчиняется общему budget;
сохранённый target позволяет явно перечитать пропущенное наблюдение. Close по-прежнему
освобождает renderer/session; сохраняются факты чата, а не скрытая открытая страница.

Только evidence свежего live read имеет descriptor-at-read `browser-page`; архивный
read имеет `browser-observation`. Action handler v3 извлекает live provenance только
из первого, затем native повторно проверяет exact latest observation, document,
element и manual/unknown guards. Исторический read, даже последнего снимка, не даёт
action authority. Старый mutable read без этого provenance действие не разрешает.

Research text расширен до 2000 UTF-16 символов на text node при прежних общих limits
160 nodes / 16 000 символов / 3000 visits / 100 ms. Element names остаются до 160;
обрезанный text node явно помечает capture partial. Полная DOM pagination, iframe/
shadow capture и screenshots сюда не входят. [Evidence](05_TEST_MATRIX.md#p6g).

## P6f: external actions и переход по ссылке — In Review, 2026-11-11

`browser.fill` и `browser.click` используют те же semantic target/name и fresh delivered
whole read на сайтах из BrowserAgentAllowedSites. Native проверяет доступ до prepare,
dispatch и после read-back. Fill поддерживает обычные text/search/email/url/tel inputs
и textarea; sensitive/read-only/private поля не становятся доступными. In-page button
click ожидает единственное новое `expectedText` до 1 s; это проверка наблюдаемого DOM,
не доказательство сохранения бизнес-данных. Async effect, сменивший документ, отсутствие
read-back или user takeover после dispatch — unknown, без replay. Частичная capture,
двойственное имя, заменённый/закрытый элемент или отсутствующий hit target дают отказ.

`browser.navigate` теперь принимает ровно одно из `url` / `linkName`. Для linkName
runtime фиксирует HTML anchor и его href из observation; href не передаётся модели.
Перед действием сверяются тот же элемент, href, имя, доступность и trusted-input epoch.
Переход использует existing navigation owner и текущую вкладку, включая target=_blank;
download/javascript/mailto и нестандартные JS-only links не поддержаны. На ссылке
показывается existing cursor. URL/redirect/loading проверяются как в P6e. У всех трёх
handlers новые v2 bindings; старый probe-only path удалён.

Agent navigation guard также действует на автоматическую навигацию из обработчиков
fill/click и остаётся до явной ручной навигации. Subresource/XHR network этим списком
не фильтруется. Model open/focus/close, select/scroll/key/wait и screenshots этот срез
не добавляет. [Проверки](05_TEST_MATRIX.md#p6f); native Windows delivery Open.

## P6e: navigation tool — In Review, 2026-11-11

`browser.navigate {target:"Browser page: external", url:"https://…"}` переходит в
существующей странице своего chat/session. Новый tab/session не создаётся. Требуется
свежий whole `core.read`, реально доставленный этому conversation. Existing
ToolRuntime/policy/confirmation и prepared/dispatch/terminal evidence сохраняются;
mutation admission перепроверяет document binding и manual latch перед dispatch.

Fixed isolated script проверяет trusted-input epoch и однократно запускает navigation;
модель не получает JS/selector/native IDs. Native `ExternalBrowserNavigation` связывает
initial URL, NavigationId/redirects и final loaded URL/new document generation. Каждый
HTTP(S) redirect и итоговый адрес проверяются по текущему BrowserAgentAllowedSites.
Same-document fragment имеет отдельный SourceChanged read-back; IDN нормализуется
до Chromium punycode. Тот же адрес даёт verified no-change, не принудительный reload.

Terminal VerifiedChange требует успешной загрузки/isolated initialization и новой
generation той же session; runtime сверяет binding перед terminal. Timeout (12 s
loading / 15 s native envelope), ошибка, смена владельца, takeover или revoke после
dispatch — unknown, без replay. Redirect gate сохраняется после unknown до явной
ручной навигации/close. Ручной Browser не ограничивается agent allowlist. После
успеха нужна новая observation; ограниченный external fill/click добавлен P6f выше.

Порядок и корреляция событий сверены с [WebView2 navigation events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events);
реальная Windows compilation/delivery остаётся Open. [Проверки](05_TEST_MATRIX.md#p6e).

## P4a: сравнение сохранённых исходников — baseline / In Review, 2026-11-11

Typed UI reads `compareHtmlSources` (`chatId`, `documentId`) и `comparePluginSources`
(`pluginId`) принимают exact `beforeId`, `afterId`, optional `path`. HTML owner разрешает
два снимка одного logical project в указанном документе; plugin owner — две опубликованные
версии одного плагина. Candidate, dirty source, current selection и preview data не
подменяют пару. Новый model tool/store не вводится; строка будущего Model API compare
ниже этим UI-срезом не реализована.

`SourceComparisonDto` возвращает exact IDs/labels, список `path/change`
(`Added/Removed/Changed/Unchanged`), выбранные before/after text и `complete/message`.
Список не читает plugin payloads; читается только выбранная пара файлов. HTML snapshot
ограничен 16 МиБ/100 файлами, объединённый список — 1024 файлами. Один текст ≤256000
символов, plugin payload также ≤256000 байт; invalid UTF-8/binary/oversize обозначаются
явно без частичного diff. TextDiff сохраняет свои ограничения работы/строк/вывода.
HTML DataSources не разрешаются. Чтение не переключает head и не запускает экземпляр
плагина; ошибка preview не закрывает явный доступ к сравнению.

Пользователь отложил дальнейшую доработку diff и P4b; они не блокируют P6.

## P2a: реализованный import contract — In Review, 2026-11-10

`createHtmlProjectFromArtifact` принимает typed `HtmlProjectImportRequest`:
`chatId`, `documentId`, exact `resourceUri`, enum `backing: Managed`. Source разрешает
ArtifactCatalogService; UI не присылает исходники, absolute destination или session ID.
Application `HtmlProjectImportService` возвращает `HtmlProjectImportResult`:
enum `status: Created | Rejected | Unknown`, backing, workspaceId/name/rootPath/entryPath
и message. `Created` выдаётся после file read-back и публикации existing workspace;
`Rejected` — до создания папки, `Unknown` — после возможного эффекта. Нет auto-retry.
Это explicit UI command, не browser tool или новый agent protocol.

Закрытие Browser не отменяет начатый import. Поздний результат не открывает другую
вкладку; retained view предлагает открыть уже созданный workspace. При `Unknown`
повторное создание блокируется в этой view; пользователь проверяет каталог/точную папку.

## P2b: реализованное описание цели — In Review, 2026-11-11

`BrowserTargetDescriptor` в Application Contracts: enum `kind` = HtmlArtifact /
HtmlResource / HtmlWorkspace; enum `state` = Ready / Empty / Unavailable;
chatId/documentId, exact resourceUri, artifactId/revision и typed capabilities
preview/source/edit/versions/restore/exportProject/createProject/annotations.
Document ID пуст только у источника с chat ownership; URI всё равно сверяется точно.
Empty editor не выдумывает snapshot; отсутствующий selected source — Unavailable.

Поле `browserTarget` добавлено в ArtifactTransferDownload (saved HTML),
ArtifactViewerPageDto (raw HTML после проверки source evidence) и HtmlWorkspaceResponse.
Другие formats этим resolver не обслуживаются. Existing routes/reads/leases сохранены;
свойство не приходит из UI и не является admission token. Флаги отражают команды,
а их payload, revisions, permissions и возможные зависимости проверяет domain owner.
Web отклоняет несовпадение chat/document/exact source до применения ответа; export
lease закрывается и при отказе. Unknown kind не выбирает иной renderer.
Live редактор использует descriptor вместе с dirty/read-only/pending guards;
state Ready подтверждает разрешённый source, а не возможность записи или успех Run.

<a id="p6a-semantic-read"></a>
## P6a: реализованное чтение external page — In Review, 2026-11-11

Open P5a получает chatId из выбранного разговора; последующие navigation/layout
не меняют владельца. `IExternalBrowserReadSource` даёт только runtime binding
chat/session/document generation и bounded semantic content. Binding не хранится
в Dock layout и не становится аргументом модели. При controller rebind старый
read source отключается; новый подключается после native reset.

Office: `core.find {scope:"browser"}` → `core.read {target:"Browser page: external"}`.
Bound Workspace добавляет existing `type:"office"`; без Office binding Browser
provider в workspace route пока недоступен. Discovery читает metadata, не DOM.
Exact document guard действует до/после capture, без удержания Office gate во время
WebView2 await. Provider проверяет owner/session/navigation generation повторно;
close/reopen/navigation/host replacement/cancel дают отказ без принятия позднего body.

Capture — fixed script в isolated world верхнего документа. До 160 nodes, 160 UTF-16
символов на element name (с P6g до 2000 на text node), 16 000 суммарных символов names
и 3000 посещённых nodes; scanning budget 100 ms, native read timeout 3 s. Text values
controls, href, private/hidden regions, frames и shadow DOM исключены; доступны
role/name, disabled/checked и text. P6g добавляет native source URL/time и bounded title.
SourceComplete=false: это ограниченный semantic view, не full DOM. Повторное чтение
обновляет capture, а не продолжает DOM pagination. Model scripts/evaluate и screenshots
не предоставляются. P6f расширяет прежние probe-only actions на разрешённые страницы;
semantic labels/text остаются недоверенными данными.

Existing ResourceAuthority/CAS сохраняют exact body. P6g разделяет immutable research
identity и mutable observed page head для действий. Live revision включает navigation
generation, уникальную capture identity и hash body: одинаковый текст не подменяет
exact DOM binding. Это evidence чтения, не доказательство будущего DOM. Второго store нет.

<a id="p6b-local-actions"></a>
## P6b: действия на локальной probe-странице — In Review, 2026-11-11

Ниже первоначальный scope P6b; restriction на probe заменён P6f выше. Остальные
exact observation/control/effect guards сохраняются.

Одна manually opened P5a page: `core.read` → `browser.fill` → новое `core.read` →
`browser.click` → `core.read`. Модель передаёт `target: "Browser page: external"`,
точный observed `name` и `value` для textbox либо `expectedText` для button.
Инструменты доступны при подключённом native pilot в Office и bound Workspace;
standalone Workspace и произвольные external websites не поддержаны. Host и fixed
isolated-world code допускают mutations только на `https://rna-browser-probe.invalid/index.html`.

Admission использует evidence accepted call исходного разговора: модель должна
получить весь bounded semantic view. Omitted/partial body не даёт права действовать;
`sourceComplete=false` сохраняется, поскольку observation не является полным DOM.
Read сохраняет bounded references на actual elements внутри isolated world; новое
чтение заменяет их и отменяет старую preparation. Runtime pins capture/document,
semantic role/name и одноразовую preparation identity; opaque IDs модели не видны.

Prepare и dispatch повторно проверяют exact element identity, единственность,
visibility/hit testing, disabled/read-only/private state и trusted user interaction.
Unrelated DOM changes допустимы; замена одноимённого элемента — stale. После prepare
программная смена значения textbox также отклоняется. Между чтением и действием
пользовательский ввод требует нового чтения. Нет автоматического scroll/focus.

Existing ToolRuntime выполняет policy confirmation, durable prepared/dispatch и
terminal через ResourceMutationJournal/ResourceAuthority. Во время model/user wait
gates свободны; native operation сериализуется на странице, а dispatch/read-back/
publication удерживают exact Office document gate на worker. Закрытие/навигация/
смена владельца не могут перенаправить подготовленное действие.

Fill использует native DOM value setter и input/change events; read-back сравнивает
значение внутри renderer, не возвращая его модели. Click использует DOM `click()`;
`expectedText` должен отсутствовать до нажатия и появиться ровно в одном entry после.
Это проверка локального синхронного результата, не доверенный физический ввод и не
доказательство server-side операции. Неуникальный/неполный capture не допускает action.

Возможный эффект без read-back, cancellation/timeout после dispatch — `Unknown`.
Поздний queued callback не выполняет действие после timeout; in-flight operation
сохраняет native gate до завершения. Unknown блокирует дальнейшие mutations этой
страницы, включая после нового чтения; доступны inspect и ручное close/reopen.
Автоматического retry/replay нет. Успех потребляет observation, следующий action
требует нового чтения. Приёмка native WebView2/controller на Windows остаётся открытой.

Прежняя защита P6b инвалидировала только observation, а новое чтение снова допускало
preparation. P6d ниже добавляет постоянный ручной режим и связанную паузу run.

<a id="p6c-action-visualization"></a>
## P6c: визуализация действий — In Review, 2026-11-11

Presentation поверх P6b: `app-browser-action-view.js/css` и общие product tokens
встроены в Office assembly и устанавливаются в isolated world, в closed shadow root
отдельного inert/aria-hidden элемента. Нет Shell overlay над HWND, нового bridge,
model API, execution queue или store. Scope остаётся local probe.

После confirmation native page получает actual element через P6b check и показывает
указатель/outline/метку. Timings определены view; native ожидание ограничено 300 ms
(обычно 220 ms, reduced motion/hidden — 0). После ожидания сохраняются document и
exact element/user rechecks. Декорация не даёт authority и не подменяет DOM input.

Renderer завершает read-back, но сам не показывает успех. Existing ToolRuntime
публикует terminal, затем `IExternalBrowserActionSource.PresentResult` best-effort
передаёт effect в ту же native view. Exception/потеря канала после dispatch отображается
как Unknown, если страница ещё доступна; failure UI не меняет terminal. Result закреплён
за exact page/document/preparation; late reply другого action или скрытой view игнорируется.

Нет значений полей в метках, перехвата pointer/focus или данных декорации в semantic
capture. Через idle курсор удаляется из DOM; trusted input, scroll/resize, native hide,
close/navigation убирают его без replay. Нет фонового polling/непрерывного animation loop.
Motion и light palette — [Design System](../ui/UI_DESIGN_SYSTEM.md#browser-action-visualization),
[проверки/ограничения](05_TEST_MATRIX.md#p6c). Windows WebView2 delivery остаётся Open.

Само скрытие указателя остаётся presentation; управление и pause принадлежат P6d.

<a id="p6d-user-control"></a>
## P6d: ручное управление и доступ агента — In Review, 2026-11-11

Owner ручного режима — existing `ExternalBrowserPage`; фиксированный
`ExternalBrowserControl.js` устанавливается до скриптов top document в isolated world.
Trusted pointerdown/keydown/input/change/wheel сразу блокируют JS mutations и очищают
observation/preparation. Pointermove только скрывает декоративный курсор. Native CDP
binding принимает ограниченный typed сигнал только от текущего top execution context;
страница не получает Shell bridge или функцию продолжения. Native navigation controls
тоже включают ручной режим. Hide/move/navigation не возвращают управление автоматически.

Native owner запрашивает паузу текущего Run исходного чата через существующий
`AgentCommunicationService` endpoint Office/Workspace и `ConversationInboxService`.
`RequiresUserResume` запрещает peer resume. Kernel достигает прежней безопасной границы,
не делает новых model calls в ожидании; уже начавшийся model/tool/read-back не откатывается.
Ошибка записи паузы не открывает mutations: UI показывает необходимость Stop в чате.
Чтение разрешённой страницы допустимо, но не снимает ручной режим. Новый Browser read
или action при ручном режиме также запрашивает паузу, если Run появился позже жеста.

Typed `ResumeAgent` требует exact session/control revision и неизменившийся input epoch.
Кнопка «Продолжить агентом» отпускает только захваченную live invocation/pause; поздний
resume не снимает более новую паузу или Stop. Старое observation и preparation удалены,
следующее действие требует нового чтения. Очереди отложенных кликов нет. Close не
продолжает Run автоматически: прежние chat controls остаются способом его продолжения.
Unknown effect этим действием не очищается; прежний close/reopen guard сохраняется.

Chrome проверяет actual isolated scripts и Shell; native CDP event delivery, Office
threading и HWND ещё требуют Windows evidence. Ввод внутри iframe/отдельных окон не
квалифицирован; general external actions этим срезом не включаются.

## Сетевой доступ: текущие границы

Настройка «HTTP-доступ HTML-превью» (`HtmlNetworkAllowedOrigins`) принадлежит
`HtmlNetworkService`: разрешает HTTP-запросы preview bridge к точным
scheme/host/port, включая повторную проверку каждого redirect. Маски пока не
поддерживаются. Это не список разрешённых сайтов external Browser и не разрешение
на действия агента. Внешний Browser вручную открывает HTTP(S); P6b fill/click
по-прежнему допускаются только на локальной probe-странице.

Отдельный `BrowserAgentAllowedSites` — «Сайты, доступные агенту». Пустой список
отказывает model-facing чтению и agent navigation внешних страниц; bundled probe остаётся доступной.
`ExternalBrowserPolicy` проверяет actual URL перед capture и перед выдачей результата,
а также перед prepare/dispatch. Отзыв действует для последующих обращений, включая
in-flight capture; уже доставленный модели контекст назад не изымается. Redirect или
navigation меняют документ/URL и требуют новой проверки. Ручная навигация и сетевые
подресурсы страницы этим списком не фильтруются. Разрешённый сайт не включает новые tools.

`HttpOriginPattern` допускает точный `https://example.com` или `https://*.example.com`:
маска включает корневой домен и его поддомены с DNS label boundary. Scheme и port точны;
без порта используется 80/443. IDN нормализуется, IP допустим только без маски. Пути,
query/fragment, credentials, `*example.com`, wildcard scheme/port и произвольные glob
отклоняются при Save, не затирая прежние настройки. На строку одно правило, до 128;
per-request HTML grant остаётся exact origin и Browser authority не выдаёт.

## Runtime entities и capability negotiation

| Тип | Минимальное содержание | Единственный owner |
|---|---|---|
| `BrowserTarget` | Закрытый kind: external_web/html_resource/html_workspace/html_artifact/plugin_source/plugin_release; exact identity, authority scope, revision policy | Application resolver делегирует domain owner |
| `BrowserSession` | Будущий native/agent runtime: bound scope, target, generation, renderer/profile, capabilities; обычный lifetime заканчивается с вкладкой | Application owner P5/P6; новый registry в P2c не вводится |
| `BrowserObservation` | Session/document generation, observed URL/title, bounded semantic nodes, runtime element bindings, snapshot/CAS ref, completeness/bounds, redaction metadata | Renderer capture + Resource Gateway |
| `BrowserOperation` | Runtime operation ID, actor/run/step, prepared exact target/element, preconditions, policy admission, dispatch fact, verification/result/effect | Application + existing ToolRuntime/evidence |
| `BrowserAnnotation` | Exact target/revision/document, existing selection body ref, comment/author, semantic/source/visual anchors, state и verification ref | Application annotation service + existing events/CAS |

Это runtime/domain records. Bridge команды/ответы — typed DTO в существующем Contracts
контуре; произвольные строковые statuses и ad-hoc JSON parsing не добавляются.
Session ID браузера не равен chat ID, Run ID, Dock panel ID или plugin instance ID.
`BrowserTarget.kind` — новый resolver discriminator, не переименование существующих
artifact kinds: сохранённый HTML по-прежнему приходит как `html_workspace`.

Resolver принимает typed target из проверенного UI route или semantic intent из
tool binding; проверяет scope/authority и выбирает существующего owner. Не выводит
право записи из URI, MIME, display title или успешного чтения. Logical address не
становится WebView origin. Target не поддержан → явный отказ, без другого renderer
или editable copy по умолчанию.

Capabilities вычисляются как пересечение target authority, host/renderer support,
session policy и текущего grant. UI только отображает доступное; локальный
application owner проверяет операцию повторно. Группы возможностей: preview/source/diff,
navigation, interaction, screenshot, annotations, versions/restore/apply/publish.
Недоступный режим скрыт; отозванная возможность прекращает новый dispatch.

| Target | Предлагаемые возможности и ограничения |
|---|---|
| external_web | Navigation, semantic reads/actions; source/version/edit/publish не имитируются; screenshots только после privacy gate |
| html_resource | Bounded inert source; preview только явно через sandboxed adapter; изменение/импорт у существующего resource owner |
| html_workspace | Existing editor/preview, source, lineage и guarded mutations; UI selection не меняет model target |
| html_artifact | Pinned code preview/source; compare/annotations после P4; сохранение только через явную owner operation/copy |
| plugin_source | Workspace source tools; check создаёт candidate preview; publish только через PluginApplicationService |
| plugin_release | Immutable code; отдельный instance/data grant; pinned view использует пробные данные; переключение active release не редактирует код |

## Session и operation lifecycle

1. Open разрешает target, фиксирует owner scope и доступ, затем создаёт renderer.
   Ошибка открытия не публикует готовую session и освобождает частичные ресурсы.
2. Activate/deactivate и временный DOM detach при Dock move/split/restore сохраняют
   страницу и ввод. Закрытие вкладки освобождает renderer/leases/bindings; повторное
   открытие создаёт новую страницу. Отдельных detach/close-session UI commands нет.
   Dirty source editor использует existing `canClose`. Закрытие view не отменяет Run;
   future operation на закрытом target получает явный отказ без auto-reopen/retry.
3. Навигация в новый document меняет generation и инвалидирует element bindings.
   DOM mutation сама по себе не инвалидирует всё: перед действием проверяются exact
   document, frame, единственность цели, состояние и interactability.
4. Read может быть конкурентным только при поддержке renderer; observation всегда
   отмечает document generation. Mutations идут последовательно в session gate.
   User interaction делает конфликтующий prepared plan stale; новое наблюдение
   предшествует новому admission. Между scope gate и Office gate нет удержания во
   время model/user wait.
5. До внешнего эффекта сохраняется prepared fact. После dispatch — read-back либо
   explicit unknown. Timeout/закрытие канала после dispatch не означают no-effect.
   Автоматический retry POST, submit, purchase, publish или delete запрещён.
6. При document close/crash session прекращается; evidence остаётся. Restart layout
   открывает target только как новую session, без восстановления DOM/форм и replay.

Shared session задаётся явно, без фиксированных agent roles. Admission проверяет
участника и текущие grants на каждой операции. Неоднозначная semantic цель требует
уточнения; нет выбора «активной вкладки» или первого совпадения за модель.

<a id="model-api"></a>
<a id="model-results"></a>
## Что получает модель: чтение и исследование — 2026-11-11

Внешний Browser должен быть уже открыт пользователем из нужного чата. Пример
реального последовательного маршрута (каждый read доставляется модели до действия):

```text
core.find {scope:"browser"}
core.read {target:"Browser page: external"}
  text: source URL/time/title + "heading: …\nlink: Подробнее\nbutton: Найти…"
  snapshotTarget: "Browser observation: <UTC capture time A>"
browser.navigate {target:"Browser page: external", linkName:"Подробнее"}
  result: подтверждённый переход / отказ / unknown; не текст новой страницы
core.read {target:"Browser page: external"}
  text: новое bounded semantic наблюдение; sourceComplete:false
  snapshotTarget: "Browser observation: <UTC capture time B>"
core.read {target:"Browser observation: <UTC capture time A>"}
  text: сохранённое наблюдение A; не открывает страницу, не разрешает действие
```

ExternalBrowserResourceProvider → Resource Gateway/ToolRuntime → existing typed tool
result/чат → ModelContextCompiler → следующий model request. Текст попадает в `text`
model projection, точный body и ResourceEvidence сохраняются в existing CAS/authority;
внутренние URI/IDs остаются runtime-only. Это проверяет
`ExternalBrowserSemanticGateway`: model text совпадает с сохранённым CAS body.
Eligible E citations используют текущий общий protocol, не отдельные browser IDs.

Модель получает role/name, disabled/checked, bounded text, source URL/time и title.
Href, credentials/query/fragment URL, input values, full DOM, iframe/shadow DOM и
image bytes в этой projection отсутствуют. `core.find/search` здесь ищут browser
resources/retained text, не весь интернет.
Переход не выполняет скрытый дополнительный model call или автоматическую доставку DOM.

**P6g реализован:** immutable observations сохраняют факты A/B после перехода и
compaction через existing context/CAS. `snapshotTarget` позволяет перечитать их после
закрытия страницы или при исключении из model budget. Только live read с exact native
binding разрешает подготовку действия. [Контракт выше](#p6g-research).

**Image/web snapshots:** P6a semantic snapshot внешней страницы уже есть. CLI
`web.verify` сохраняет исходники локального web-проекта, viewport PNG и CDP trace
через WorkspaceWebSnapshotStore; это dev verification, не захват произвольного сайта.
PNG/trace доступны typed application/CLI diagnostic path; bytes/refs не входят в model
tool result. `core.read` image/raw capability модели также не выдаёт.
[Существующий контракт](../resource-fabric.md#retained-browser-diagnostics).
Для external Browser ещё не реализованы `browser.screenshot`, privacy/redaction и
bounded image delivery модели. Они составляют P6h; повторно создавать CAS/media store
не нужно.

<a id="model-api"></a>
## Model API и observations

Исходный перечень browser tools — capabilities, а не требование создать каждый ID.
Публичные schemas должны соответствовать текущему semantic contract:

| Capability исходника | Фактический статус | Model-facing маршрут / owner |
|---|---|---|
| list / inspect / semantic snapshot | P6a реализован, bounded top document | `core.find/read/search`; runtime pins observation, не full DOM |
| navigate / follow link | P6e/P6f реализованы | `browser.navigate` с url либо linkName, существующая owned page |
| fill / in-page button click | P6f реализован с ограничениями выше | `browser.fill/click`; fresh read, native site policy и bounded verification |
| open / focus / close | Сейчас ручной UI; model commands Planned | Application/Dock/native lifetime; не отдельный session-close UI |
| select / scroll / keypress / wait | Planned, по конкретным workflows | Semantic actions; reuse admission/effect owners, не список API заранее |
| screenshot / image observation | P6h Planned для external Browser | Privacy capture → existing CAS/media → model delivery; без inline base64 в Tool Result |
| retained research observations | P6g реализован | Existing Resource Gateway/CAS/context; source/time и semantic snapshotTarget, historical read без live action authority |
| annotate / persistent annotations | Existing visual selection; P4b отложен | Общий annotation owner, не Browser Annotation DB |
| compare | P4a UI baseline; model compare Planned, доработка отложена | Existing saved HTML/plugin owners, не новый browser version store |
| checkout / restore / apply / publish | Existing domain commands | Owner tools; не универсальная запись по browser URL |

BrowserSessionId, observationId, document revision, CSS/backend node IDs, UUID,
ResourceRef, rna URI, cursor и confirmation token остаются runtime-only. Модель не
составляет CDP/JavaScript и не получает `evaluate`. User-provided HTTP(S) URL — обычный
адрес навигации, не capability token. CSS selector из страницы — hint, не authority.
Короткие новые aliases session/element также не вводятся как обход этого правила.

Observation содержит role/name/state и ограниченные text excerpts; pagination и
truncation явные. Полный DOM не доставляется по умолчанию. Runtime связывает действие
с последним пригодным exact observation исходного разговора; иначе требует новое
чтение. Cross-origin iframe доступен только через поддержанный host adapter с
отдельной frame identity; shell JS не читает его DOM.

Prepared external effect проходит текущую policy confirmation; model `outcome` не
доказывает успех. Tool Result использует существующий envelope и bounded domain summary;
внутренние IDs/result references остаются в typed execution evidence. Eligible `E`
выдаются по текущему v8 contract, не новым browser citation namespace.

## Ошибки, события и аннотации

Typed error codes: `SESSION_CLOSED`, `NAVIGATION_FAILED`, `PERMISSION_DENIED`,
`STALE_DOCUMENT`, `STALE_ELEMENT`, `ELEMENT_AMBIGUOUS`, `ELEMENT_NOT_INTERACTABLE`,
`TIMEOUT`, `UNKNOWN_EFFECT`, `UNSUPPORTED_CAPABILITY`. Code не выводится из текста
ошибки. Internal result содержит action identity, dispatch/effect, observation refs
и разрешённый recovery; model projection — понятную причину без opaque handles.
`UNKNOWN_EFFECT` допускает inspect/verify, но не повтор mutation. Даже `TIMEOUT`
сохраняет отдельное поле effect: неизвестный dispatch нельзя скрыть этим кодом.

Значимые navigation/observation/action/verify и annotation изменения сохраняются
typed facts в существующий SessionEvent stream; большие bodies — CAS. Tool events
переиспользуются, если уже выражают факт; browser-specific событие добавляется только
для отсутствующего domain факта. Общий root event связывает shared operation с actor,
а не копируется полным payload в журналы всех участников. Inspector читает refs лениво;
compaction/request compiler не получает вторую копию DOM через Current Resource View.

Annotation owner сохраняет selection evidence через существующий attachment/CAS путь;
новые события lifecycle ссылаются на него. Target/source identity устанавливает runtime.
Draft до отправки остаётся у draft owner. Submitted annotation переживает view close;
его `open/unresolved/resolved` восстанавливаются из events, без отдельной Browser DB.
Старые message attachments остаются исходными фактами, без автоматического назначения
им статуса исправления или переписывания их тел.

Anchors включают semantic/DOM hints, source locator только при доказанной связи,
rect/viewport/scroll и exact revision/document. Координаты — последний fallback.
Неоднозначная перепривязка даёт unresolved; пользователь выбирает заново.
Resolved требует verification исправления и сохраняет ссылку на evidence;
успешный patch или только source read-back не доказывают видимое исправление.
Task list из annotations не создаётся автоматически.

<a id="p1-presentation"></a>
## P1: минимальный presentation interface

Реализован 2026-11-04, In Review: изменены существующие JS modules, без новых bridge
DTO или C# types. Текущее UI ownership описано в [UI Architecture](../ui/UI_ARCHITECTURE.md).
UI presentation содержит `address`, optional `mode`, `supportedModes` и `details`.
Названия режимов задаёт общий UI: «Страница» / «Исходник». Дополнительные callbacks
`navigate`, `back`/`canGoBack`, `forward`/`canGoForward` только включают соответствующие
controls; они не создают browser session и сейчас не передаются saved providers.
Label/attach options содержат storage metadata (`managed`, `directory`, `external`),
`details` и пояснение unavailable annotation. Live owner передаёт свою существующую
кнопку через `setAnnotation`; общая строка не получает annotation authority.

- `HtmlArtifactView.load` возвращает прежние `element/frame/dispose` плюс
  `presentation` и `selectMode(mode)`, возвращающий обновлённую presentation.
  Provider владеет выбранным mode/file/page; raw source не входит в descriptor.
- `PreviewSurface.attach` возвращает дополнительно
  `setDocument(presentation, onModeChange)`, `clearDocument()` и `refresh()`
  доступности reload; это UI-only
  rendering read-only адреса и доступных modes, без data fetch и send().
- `BrowserObjectView.load` передаёт descriptor в PreviewSurface; при выборе вызывает
  `value.selectMode` и отражает возвращённое состояние. Reload/stop при незавершённой
  загрузке/clear/dispose не оставляют callback старого provider. Hidden ready view
  сохраняет mode и DOM; activate не сбрасывает их.
- Адрес может приходить из provider label без mode descriptor. Общая строка остаётся
  той же; source/history/publication принадлежат provider. `collapse` и `clear`
  закрывают раскрытие/меню и снимают временные focus/pointer listeners.

Address immutable artifact — exact URI из trusted target, read-only, label «Адрес
материала»; Enter ничего не навигирует. Diff скрыт без capability, back/forward
выключены. Фокус расширяет адрес, controls не меняют высоту content/iframe.
В P1 это не полноценная навигация, новые sessions или новый version contract.
