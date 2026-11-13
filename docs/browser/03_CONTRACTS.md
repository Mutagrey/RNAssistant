# Universal Browser: предлагаемые контракты

## Native consumer cutover — F2c1, 2026-11-13

IBrowserRenderer is the sole native adapter of LocalBrowserPageRuntime. The runtime
serializes placement/navigation/close and retains session/history/open receipts;
metadata alone grants no execution capability. Attach waits for native readiness.
Native page responses use captured exact members; file/query/hash navigation never
turns into arbitrary HTTP access. Document resource members may be addressed as
`/resources/{key}[/versions/{id}]/files/{relativePath}`.

RN.resources open/read/close/subscribe uses a per-page bounded lease owner over the
existing ResourceDataPlane. It never holds its lock across host reads. HTML assembly
uses existing preview/vendor code in a controlled isolated world before authored JS
runs. Plugin SDK/commands share the isolated environment and their original grants.
The final consumer closes the worker broker; profile cleanup waits for process exit.

Input uses native CDP, not DOM event dispatch/value assignment. Semantic admission
pins exact observation/geometry/control state; read-back is bounded and Unknown is
not retried. OS hooks distinguish physical input from agent input. Hook failure
refuses agent input while preserving manual page execution. Native dialogs and file
choosers are not silently accepted by agent actions. Screenshot inspection refuses
private/form/opaque/embedded/shadow content; native privacy evidence remains open.
Coordinate actions and native selection/highlight remain the next adapter slice.

## Interactive content binding — F2c in progress, 2026-11-13

BrowserContentSession is a transient member list over existing captured CAS/package
bytes. Workspace preview uses CaptureSet without publishing a saved version; plugin
working preview uses existing Check/instance ownership. Per-session synthetic HTTPS
serves only declared normalized members, retaining query/hash for local navigation.
Native content must keep RN.resources and plugin SDK routes; internal calls remain
scoped capabilities rather than general Shell access.

Network responses apply Connection-Allowlist `(response-origin)` to documents and
worker scripts, alongside CSP and exact host routes. Chromium 152+ is required for
this enforcement mechanism; older runtimes need an explicit update message. The
[Chromium contract](https://developer.chrome.com/docs/modern-web-guidance/guides/security/restrict-outbound-connections)
includes non-Fetch channels; CSP alone is insufficient. Windows execution and input
qualification remain separate evidence. The previous unconditional execution refusal
is being replaced by actual native delivery, not relabeled as a completed feature.

## Agent open / local transport — F2b, 2026-11-13

`browser.open` accepts an unambiguous semantic source delivered by `core.find` in
this exact run. Runtime allocates tab/operation identity; success requires the exact
visible Dock attachment, not merely a page allocation. Owner/run/call and receipts
prevent cross-session reuse or duplicate dispatch. Timeout/cancellation/terminal
failure leaves Unknown; manual close is required before another open of that source.
Source-only success reports `executionAvailable:false`; it grants no live page read,
input or media. Plan cannot mutate placement. Reset invalidates pending operations.

`browser.navigate` takes a discovered local locator or an observed local link.
A URI's HTTP(S) spelling alone does not imply network: `rnassistant.local-resource`
is the existing in-process byte transport with exact owner/path/lease checks. It is
preserved, but transport URLs/leases are not model navigation capabilities. Internal
links must resolve to an admitted local member; loopback/LAN remain external and denied.
Bounded click/drag/hover/scroll/key/select/check contracts are registered, with no
held input across calls. Native dispatch, coordinates and physical-input attribution
remain unavailable under B02; no synthetic DOM-click fallback is delivered.

## Snapshot transfer and completion — F4c, 2026-11-13

`exportBrowserProject`, `copyBrowserProject`, `closeBrowserProjectTransfer` use a typed
Browser target and exact saved Workspace ID. Export returns the existing chunked
download DTO; its owner is the actual Workspace, with independent expiry/revocation.
The shared download budget/concurrency/ordered-offset/hash checks remain in force.
Copy uses the existing hidden-project/file-mutation/import owner, verifies every
member and stores origin metadata. Source files are never substituted or overwritten.
A removed version refuses new transfer; in-progress file reads retain exact CAS bytes.

Navigation completion reads bounded current metadata and saved-entry/manifest
metadata. `Peek` never creates a catalog/migration or loads member bodies. At most
8 matching families × 8 saved entries are considered before the 12/20 response cap.
Direct locators are resolved independently of this suggestion window.

## Common provider actions — F4b, 2026-11-13

`compareBrowserSources` accepts `BrowserSourceComparisonRequest` with a resolved
owner/target, before saved-entry ID and exact displayed after ID. Each domain verifies
membership/removal; the bounded common diff reads only the selected member. Document
working snapshots retain exact revision references; raw HTML has no fabricated history.
`getBrowserVersions` is the only Browser picker/comparison inventory. The old
getSavedHtmlHistory/getPluginVersions/compareHtmlSources/comparePluginSources bridge
operations and UI DTOs are removed. Automatic HTML revisions remain in the editor's
technical undo/redo history. Delete notifications include the saved entry ID.
Cross-document immutable reads use registered source metadata and the addressed real
chat; they do not create a chat or change its selection. Working editing stays with
the exact document's editor. Browser route data is presentation, never authority.

## StartSurface / UI preferences — F4a, 2026-11-13

StartSurface takes a model (title/search/sections/entries/loading/error/empty) and
onSearch/onSelect callbacks, returning element/update/focus/dispose. It reads no domain
catalog, active chat or bridge. Dock descriptors own discovery/order; adapters own
queries, bounded results and accepted navigation. Local recents schema 1 under the
existing Dock preference namespace stores only locator/title/kind/visitedAt/scope,
maximum 200, with no chat/native session or query/fragment. Browser layout retains
only tab identity, local content locator, bounded title and exact domain owner.
These are preferences, never execution authority. Corrupt recents do not reset layout.

## Plugin saved-version consumers — 2026-11-13

Typed bridge: GetBrowserVersionState / GetBrowserVersions / BrowserVersionCommand
и BrowserVersionStateChanged; пока этот новый checkpoint adapter реализован для plugins.
Activate pin: saved VersionId + expectedActiveVersionId/expectedActiveReleaseId/
expectedActiveRevision; Save pin: expectedStateRevision + expectedContentRevision.
Changed сообщает metadata, не authority. UI owner/run и tab selection не становятся
частью catalog revision. Unknown сохраняет original operation; receipt может ссылаться
на уже Removed entry, без resurrection. Workspace contribution получает exact ChatSession.


F3b: BrowserVersionState adds activeStateRevision; Activate commands pin
expectedActiveRevision plus the selected saved entry and active release. This guard
is independent of unrelated catalog Save operations. Save/Delete keep the catalog
stateRevision guard. Core catalog entry identity is not its ordinal or release ID.


## Browser capability / media — F2a, 2026-11-13

`IBrowserCapabilityContribution` связывается с реальным Workspace owner/chat/run,
не с Office target. Browser branch core.find/read/search использует существующие
semantic handlers и authority; Office route не принимает Browser target/scope.
`IBrowserMediaSource` возвращает native-only bounded BrowserMediaCapture и проверяет
его актуальность. Отсутствие провайдера, privacy proof или exact viewport — отказ.
CAS хранит PNG и metadata immutable observation; ToolResultMaterialization и
ConversationRunCommit сохраняют terminal + RESOURCE_MEDIA_INPUT атомарно. LlmClient
проверяет наличие image parts в фактическом wire перед HTTP. Hydration одноразовая;
historical pixels не дают coordinate authority. Native реализация закрыта B02.


## Local source-only session — F1a, 2026-11-13

Production transport — `browserCommand`/`browserPageState`. Open требует exact
ownerScope, browserTabId, operationId, locator; Attach подтверждает конкретную
operation/session. UI не передаёт транспортные origins или grants. 16 tabs,
64 owner-bound visits/tab, 128 retained receipts; закрытие не отменяет chat run.
LocalBrowserPageRuntime поддерживает source-only admission до закрытия B02;
Unavailable не даёт BrowserPageHandle. F2b подтверждает только открытие source-only UI,
с явным executionAvailable:false.
ReadBrowserSource материализует один разрешённый member (bounded owner read),
выдаёт максимум 32K символов и проверяет hash при следующей странице.


## F0 host-neutral contracts — 2026-11-13

`Application/Contracts/BrowserPageContracts` владеет `IBrowserPageRuntime`,
`BrowserPageHandle`, typed commands/state/read/action DTO. Native implementation
остаётся Office/WebView. Exact handle сравнивает tab/session/owner/content/saved-entry
и navigation generation. `BrowserTargetDescriptor` различает Home, document HTML,
raw HTML, plugin source/release и Workspace project; Removed не подменяется latest.
Capabilities/отказы типизированы. Version state/entry/command DTO фиксируют отдельные
catalog entry, package identity, expected content/definition/catalog/active и operation.
Их durable owners подключаются в F3; пустых successful handlers нет.

`ResolveLocalLocator` / `QueryNavigationSuggestions` — typed bridge methods.
`BrowserLocalLocator` — единственный parser/formatter `/projects`, `/plugins`,
`/resources`, `/`. Traversal, network schemes, double encoding и foreign scope
отклоняются. Query сохраняется для live navigation, удаляется из recent locator.
Suggestions metadata-only, default 12/max 20 и echoed generation. Catalogs поставляют
существующие document/plugin/workspace owners; query не Check и не запускает renderer.
`IBrowserCapabilityContribution` отдельно от Office binding; F2 подключает Workspace
tools/read/media, поэтому наличие interface ещё не означает их доступность.
Старые ExternalBrowser native/bridge route names временно до F1; второго runtime нет.

## Действующее ограничение сети — 2026-11-13

`htmlFetch`, `allowHtmlNetworkOrigin` и developer/deployment bridge operations
удалены из typed catalog. Старые сообщения получают штатный unknown-operation
отказ; старое settings-поле origins игнорируется. Page fetch не проксируется
родителем/host. Внешнее navigation и системный launch из WebView заблокированы.
LLM model/config requests сохраняются. Signed access и HTML HTTP sections ниже
описывают удалённый контракт. Общий local target/runtime contract следует в F0.


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
whole read на сайтах из подписанной политики Browser. Native проверяет доступ до prepare,
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

Общий access guard действует на ручную и автоматическую навигацию, включая
обработчики fill/click. Актуальная подписанная политика ниже заменяет прежний agent-only список. Model open/focus/close, select/scroll/key/wait и screenshots этот срез
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
HTTP(S) redirect и итоговый адрес проверяются по текущей подписанной политике Browser.
Same-document fragment имеет отдельный SourceChanged read-back; IDN нормализуется
до Chromium punycode. Тот же адрес даёт verified no-change, не принудительный reload.

Terminal VerifiedChange требует успешной загрузки/isolated initialization и новой
generation той же session; runtime сверяет binding перед terminal. Timeout (12 s
loading / 15 s native envelope), ошибка, смена владельца, takeover или revoke после
dispatch — unknown, без replay. Redirect gate сохраняется после unknown до явной
ручной навигации/close. Ручные переходы также требуют подписанной политики. После
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

<a id="signed-access"></a>
## Подписанная конфигурация приложения — In Review, 2026-11-12

Scope Approved пользователем: скрытый режим разработчика внутри RNAssistant,
один переносимый конфиг, отдельный manual-any флаг и проверка версии политики.
Owner — `DeploymentConfigurationStore`/`DeploymentConfigurationCrypto` в Core;
Controller/typed Bridge обслуживают редактор, `ExternalBrowserPage` применяет правила.
Прежний BrowserAgentAllowedSites/PFX-прототип и PowerShell setup удалены без fallback.

### Файлы и доверие

- `%APPDATA%\RNAssistant\deployment-config.bin` — общий файл рядом с secret.bin,
  который передаётся пользователям. Один файл для всех установок доверенной сборки.
- `config/deployment-trust.json` в исходниках — публичные параметры RSA и verifier
  пароля (salt/hash/iterations); csproj встраивает его как `RNAssistant.DeploymentTrust`.
  Допускается в Git. Замена файла рядом с готовым exe не меняет доверие приложения.
- `.local/deployment/signing-key.bin` — зашифрованные закрытые параметры RSA,
  gitignored, не embedded/publish asset. Путь выбирается разработчиком при подписании.
  Реальные ключ/пароль не поставляются по умолчанию: до разовой настройки доступ закрыт.
  Если файл есть при сборке, его абсолютный путь записывается как default редактора
  через assembly metadata `DeploymentSigningKeyDefaultPath`; байты ключа не входят в сборку.
  При другом расположении путь можно изменить в редакторе или задать сборочным свойством
  `RNAssistantDeploymentSigningKeyDefaultPath` в локальном `Directory.Build.local.props`.

RSA 3072 создаётся штатным C#, без сертификатов, PFX/CRT и certificate store.
Закрытый .bin: fixed header RNAKEY01, random salt32, IV16, AES-256-CBC/PKCS7 ciphertext,
HMAC-SHA256 над header/salt/IV/ciphertext; проверка MAC до расшифровки. Ключи AES/HMAC
раздельно получены PBKDF2-HMAC-SHA256 (600000 итераций, 64 bytes). Verifier пароля
использует другую salt32 и 32-byte PBKDF2 output. Один введённый владельцем пароль
открывает режим и его зашифрованный ключ; пароль/ключи истории CAS не используются.

Конфиг: base64 JSON envelope `FormatVersion=1`, `Payload`, `Signature`.
RSA/SHA-256 PKCS#1 подписывает UTF-8 `RNAssistant.DeploymentConfig.v1\n` + исходные
payload bytes. В payload входят `FormatVersion`, `PolicyVersion`, UTC `IssuedUtc`,
`Browser { AllowedSites, AllowAnyManualSite }`; подписаны все поля. Открытый ключ
берётся только из embedded trust, не из конфигурации. Base64 — сокрытие при обычном
просмотре, **не шифрование**. Конфиг ограничен 128 KiB, неизвестные поля/формат
отклоняются; новых скрытых возможностей через произвольный JSON добавить нельзя.

### Версия политики и загрузка

`DeploymentConfigurationStore.RequiredPolicyVersion` — единственное место задания
версии политики в приложении (сейчас 1). При повышении и пересборке ранее подписанные
конфиги отклоняются. Требуется точное совпадение: future version тоже не принимается.
Это отдельная версия от product version и FormatVersion: обычный patch/minor продукта
сам по себе не требует перевыпуска конфигов. Save всегда ставит версию текущей сборки;
поле не вводится пользователем/моделью. Смена RSA issuer тоже требует новой сборки.
Утилиты не могут подменить trust уже установленного приложения.

Файл применяется на старте, после сохранения и при последующем обращении к политике.
Bounded read + hash cache не вводят watcher/второй store; подпись проверяется при
смене bytes. Нет файла, неверная подпись/версия, unknown format или повреждение —
все grants выключены, без last-good fallback и без блокировки запуска приложения.
Normal status содержит только `Absent`/`Applied`/`Rejected`: ни masks, ни пути, ни
количества правил. Старый конфиг той же версии всё ещё можно вернуть; повышение
RequiredPolicyVersion закрывает предыдущие версии только в обновлённом приложении.
Это защита штатной сборки, не от патча программы или администратора ПК.

### Режим разработчика

Настройки → Подключение: маленький индикатор конфигурации и свёрнутый «Разработчик».
До входа — только пароль и «Войти». Успешный native unlock выдаёт runtime token на
10 минут и открывает сайты, manual-any checkbox, путь к закрытому ключу и пути/версию
конфига. Путь к закрытому ключу по умолчанию берётся из сборки и остаётся редактируемым;
после выхода очищается вместе с остальными полями. Private key не нужен для входа.
При подписании он расшифровывается в памяти,
сверяется с embedded public key и не попадает в выданный конфиг. Неверный пароль/ключ
или невалидные маски не заменяют прежний файл. Backend защищает Save и OpenDirectory
той же сессией; UI-hidden само по себе не даёт authority.

«Подписать и сохранить» атомарно записывает deployment-config.bin и перечитывает
результат; «Открыть каталог» открывает фиксированный AppData directory через Windows
Shell, без командной строки; «Закрыть режим» отзывает session. Уход/закрытие Settings
и UI timeout очищают code/sites/path/token; late unlock не восстанавливает поля.
Native expiry проверяется независимо от UI. Session password хранится в SecureString,
ключ расшифровывается только на Save; из файлов plaintext password не читается.
Обычные getSettings/status не получают скрытые настройки.

### Browser: пользователь и агент

`AllowedSites`: exact `https://example.com` или `https://*.example.com`; маска включает
домен и поддомены по DNS boundary, scheme/port точны, IDN нормализуется. До 128 правил,
IP только без маски. Пути/query/fragment/credentials, *example.com и arbitrary glob
отклоняются; пустой список не даёт agent access. Bundled local probe доступна всегда.

По умолчанию список действует и вручную. `AllowAnyManualSite=true` разрешает любые
HTTP(S) страницы при explicit native toolbar navigation (Navigate/back/forward/reload)
и последующие redirects/resources в этом ручном контексте. Agent capture/prepare/
dispatch/read-back всегда проверяют список; Resume запрещён на странице вне списка.
До dispatch агента network context становится Agent и сохраняет ограничения, включая
late redirects после unknown. Только новое явное ручное navigation command может
снова расширить его; модель не передаёт флаг «это пользователь».

Native NavigationStarting, FrameNavigationStarting и WebResourceRequested all source
kinds используют текущий context. Для agent-owned CDN/API нужны отдельные rules.
Уже полученные данные/открытые соединения не отзываются задним числом. Это не системный
firewall: WebSocket/WebRTC/DNS и полнота interception требуют отдельного Windows
исследования; real WebView2/Office delivery пока Open.

<a id="issuer-setup"></a>
### Разовая настройка на Mac или Windows

Обе утилиты используют один C# owner `tools/RNAssistant.DeploymentKeys/IssuerFiles.cs`
и production crypto; никогда не запускаются автоматически при сборке. Новые пакеты,
скрипты, сертификаты и shell signing на Windows не нужны.

**Mac:** .NET 8 C# utility, пароль вводится скрыто в локальном терминале; не аргумент
команды и не сообщение в чат. Из корня исходников:

```sh
dotnet run --project tools/RNAssistant.DeploymentKeys/RNAssistant.DeploymentKeys.csproj -- .
```

**Windows:** из корня исходников запустить `build-deployment-keys.cmd`. Скрипт отдельно
собирает `tools/RNAssistant.DeploymentKeys.Windows/RNAssistant.DeploymentKeys.Windows.csproj`
через VS 2022 MSBuild; в основной проект утилита не добавляется. Затем запустить
`tools/RNAssistant.DeploymentKeys.Windows/bin/Release/RNAssistant.DeploymentKeys.Windows.exe`:
обычное WinForms-окно .NET Framework 4.8 без консоли. Выбрать папку исходников,
ввести и повторить пароль, нажать «Создать ключи». Сам скрипт ключи не создаёт.

1. Получить public trust и encrypted private .bin по указанным выше путям; приватную
   резервную копию хранит владелец. Public trust можно закоммитить, private key — нет.
2. Перезагрузить проект в VS, если он уже был открыт, и пересобрать RNAssistant с этим
   public trust. Без него приложение штатно запускается
   с default deny и недоступным входом в developer mode.
3. На Windows в Developer ввести пароль и путь к .bin, задать sites/manual flag,
   подписать, открыть каталог и передать пользователям только deployment-config.bin.
4. Получатель кладёт файл рядом со своим secret.bin и запускает приложение; пароль
   и закрытый ключ ему не нужны. Browser пока opt-in: RNA_BROWSER_PILOT=1 можно задать
   через пользовательские переменные среды Windows; затем новый процесс Office/app.

Замена ключей — только явный `--replace` на Mac или checkbox в Windows. Старые файлы
сначала копируются в gitignored backup directory; новые проверяются подписью/read-back.
После замены нужна новая сборка и новые конфиги. Для отзыва старых конфигов без смены
ключей достаточно повысить RequiredPolicyVersion и пересобрать/перевыпустить конфиг.
Windows GUI/WinForms/WebView2 qualification Open; [evidence](05_TEST_MATRIX.md#signed-access).

### Отдельный HTTP-доступ HTML-превью

`HtmlNetworkAllowedOrigins` принадлежит `HtmlNetworkService` и разрешает exact
HTTP origins для preview bridge с проверкой redirect. Эта прежняя настройка не
выдаёт Browser authority и не меняет подписанную политику. Модельные endpoints,
preview bridge и external Browser — разные владельцы сетевого доступа.

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

## Workspace HTML version binding — 2026-11-13

WorkspaceHtmlProject uses `/projects/{workspace}/source/{member}` and
`/projects/{workspace}/versions/{savedEntry}/{member}` in the same Browser. Definition
and saved catalog have separate revisions. `GetHtmlProjectDefinition` /
`SetHtmlProjectDefinition` carry explicit owner/workspace and expected fingerprint;
version Save binds both definition fingerprint and exact captured content fingerprint.
Manual User provenance omits chat; Run requires the real active source run. The
checkpoint record does not grant new file access or native execution.

## Document checkpoint binding — 2026-11-13

A live document project uses `/resources/{logicalId}`, a checkpoint uses
`/resources/{logicalId}/versions/{savedEntry}`. Immutable artifact locators remain
exact evidence, without implying a checkpoint. `logicalId` is runtime-owned metadata.
Save pins current aggregate URI and catalog revision; Delete additionally pins the
current aggregate URI through expectedActiveRevision. Browser versions are metadata
reads over DocumentArtifactStore, with the exact document/chat checked independently
of the active UI selection. Raw HTML still has no SaveVersion/history capability.
