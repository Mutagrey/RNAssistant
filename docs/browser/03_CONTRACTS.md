# Universal Browser: предлагаемые контракты

Статус: **Proposed**, 2026-11-04. Owner: Core contracts / Application services;
Web и native host — consumers. Это минимальные границы будущих P2–P6, не объявление
существующего runtime API. P1 presentation contract в конце реализован в Web,
статус In Review; P2–P6 остаются Proposed.
Действующие [v8](../protocols/CONVERSATION_RESPONSE_V8.md),
[Resource Fabric](../resource-fabric.md), [Tools](../tool-library.md),
[events](../session-events.md), [Plugins](../plugins.md) сохраняются.

## Runtime entities и capability negotiation

| Тип | Минимальное содержание | Единственный owner |
|---|---|---|
| `BrowserTarget` | Закрытый kind: external_web/html_resource/html_workspace/html_artifact/plugin_source/plugin_release; exact identity, authority scope, revision policy | Application resolver делегирует domain owner |
| `BrowserSession` | Runtime session ID, bound document/workspace + conversation, private/shared access, target, generation, renderer/profile, navigation state, capabilities, lifecycle | Application BrowserSessionService |
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
2. Attach/activate/deactivate/detach управляют view. В P2 detach сохраняет session;
   explicit close session освобождает renderer и bindings. Close view не отменяет Run.
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
## Model API и observations

Исходный перечень browser tools — capabilities, а не требование создать каждый ID.
Публичные schemas должны соответствовать текущему semantic contract:

| Capability исходника | Model-facing маршрут | Внутреннее выполнение |
|---|---|---|
| list / inspect / snapshot / versions / annotations | `core.find/read/search` с semantic target | Browser resource provider получает bounded observation/metadata, runtime pins cursor/document/element refs |
| open / focus / navigate / close | Семейство `browser.*` действий, закрытые schemas по действию | Application session owner; close различает view и session в UI, tool close закрывает session |
| click / fill / select / scroll / keypress / wait | Семантический page target + role/name/context цели, содержимое/условие | Runtime resolve → exact binding → admission → recheck → dispatch → verify |
| screenshot | `browser.screenshot` с semantic target | Безопасный capture в existing CAS/media pipeline, не base64/полный DOM в Tool Result |
| annotate | `browser.annotate` с semantic target/comment | Existing selection intake + общий annotation lifecycle |
| compare | Сравнение выбранных предметных версий через owner; результат читается как resource | Existing bounded diff, exact pair закреплена runtime |
| checkout / restore / apply / publish | Existing owner tools/commands; новые aliases без необходимости не добавлять | Отдельные domain operations, не универсальная запись по browser URL |

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
  `setDocument(presentation, onModeChange)` и `clearDocument()`; это UI-only
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
