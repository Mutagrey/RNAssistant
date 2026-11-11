# Universal Browser: архитектурное решение

Дата: 2026-11-11. Общая архитектура — **Proposed**; P1, P2a и P2b реализованы / In Review.
Согласованы документы + план и страницы во вкладках существующего Dockview.
Native renderer остаётся отдельным решением по evidence. Контекст — [аудит](01_AUDIT.md).
Автоматический lifecycle Browser-вкладки **Approved 2026-11-11**; решение ниже
заменяет прежнее предложение сохранять страницу после закрытия вкладки.

## Владельцы и границы

Схема ниже — целевой native/agent контур P5/P6. Для текущего saved HTML новый
BrowserSessionService/registry не требуется: renderer принадлежит существующей view.

```text
DockWorkspace → Browser View / PreviewSurface → target presentation adapter
                        ↓ typed application commands
              BrowserSessionService (Application)
              ├─ existing target/domain owners → CAS / authority / events
              └─ renderer port → sandboxed iframe channel / native WebView2
ConversationRunService → AgentKernel → ToolRuntime → тот же Application owner
                                      Resource Gateway → bounded observations
```

Core: typed domain records, pure validation, existing CAS/events/resource model.
Application: resolver, session coordination, admission, prepared operations и verify.
Web: общий toolbar, modes и lifecycle view; выбранная вкладка не определяет tool target.
Native adapter размещается у существующей композиции Office/WebView, без Office COM
в shared services. Не требуется новый проект или перемещение старых WebView hosts.
Сохраняются C# 7.3/.NET 4.8; новые `.cs` явно подключаются в old-style project.

Каждая страница — Dock tab. Общий Browser UI расширяет имеющийся renderer/PreviewSurface.
View, session, content target, renderer и authority имеют разные lifetime/identity.
Adapters не копируют source model, dirty buffers, grants, revisions или active pointers.

## Решения и альтернативы

| Вопрос | Решение | Причина / отвергнутая альтернатива |
|---|---|---|
| Placement | Existing Dockview tabs/move/split/chooser | Собственные browser tabs удваивают навигацию и lifecycle |
| Internal HTML/plugin | Existing sandboxed iframe и typed channels | Перенос всех previews в native не обоснован и расширяет риск изоляции |
| External web | Отдельный непривилегированный WebView2 renderer, если проходит spike | Shell iframe не обходит CSP/X-Frame-Options; main Shell control нельзя навигировать на сайт |
| Session owner | Application service в bound document/workspace scope | DOM registry не может владеть доступом и агентскими действиями |
| Revisions | Делегирование HTML/Artifact/Plugin owners | BrowserRevisionStore создаёт вторую authority |
| Address | HTTP(S) для web; existing exact identity/display label для internal targets | `rn://` из задания не становится новой схемой; `rna://` остаётся runtime identity, не доверенным origin |
| Model API | Semantic targets и core.find/read/search; runtime pins exact session/document/element | Новые opaque handles в аргументах противоречат canonical contract |
| Evidence | Typed SessionEvent/trajectory + bounded CAS snapshots | Отдельного AgentEventLog/store не вводим; полный DOM не копируется в несколько context blocks |
| Annotations | Расширить existing VisualAnnotations/attachment pipeline общим Application owner | Сохранить source evidence; добавить lifecycle/reanchoring, а не второй payload store |

## P2a: самостоятельный проект и backing — In Review, 2026-11-10

Реализован первый срез — явная копия сохранённого HTML в самостоятельный workspace.
Owner identity — existing `WorkspaceId`; исходный document artifact остаётся exact
снимком. Чат, Browser tab и BrowserSession не становятся владельцами исходников.

`WorkspaceStore` сейчас открывает каталоги, а file authority принадлежит
`WorkspaceFileService` / ResourceAuthority/CAS. Поэтому **managed** реализован как
обычный workspace в каталоге приложения: изменяемые исходники — файлы, CAS хранит
точные версии/evidence. **Directory** использует выбранную пользователем папку через
тот же owner. Это следует существующей [workspace-first границе](../decisions/ADR-0013-workspace-first-runtime.md),
без CAS-only редактора, второго каталога проектов или BrowserRevisionStore.
Нынешняя подпись «В приложении · CAS» описывает saved snapshot, не managed workspace.
Меню «Сохранение проекта» различает скачивание архива и создание копии исходников.

Application координирует import через существующих export/file/workspace owners;
`WorkspaceApplicationService` с mutable selected workspace не становится глобальным
resolver. Target содержит exact workspace binding, а не «открытый сейчас проект».
Plugin source/release/data grants сохраняют прежних owners; import HTML не публикует plugin.

Начальный import переносит HTML/CSS/JS-исходники. Document-bound data,
распознаваемые SDK/vendor/external refs — явный отказ **до записи**, без
молчаливой потери данных. Проверяются entry point, пути, коллизии имён и общий размер.
Новый workspace появляется в каталоге только после чтения записанных файлов и сверки;
сбой не удаляет исходный artifact и не повторяет неизвестный effect автоматически.
Existing document HTML остаётся у DocumentArtifactStore: это copy, не массовая миграция.

`HtmlProjectImportService` создаёт скрытый workspace в `<AppData>/html-projects/<id>`;
`WorkspaceFileService` пишет файлы с existing journal/CAS и повторно читает их для
сверки SHA-256. Только после этого `WorkspaceStore.ConnectProject` открывает запись
каталога. Optional `htmlEntryPath` — конфигурация existing workspace manifest, не head.
`Created` подтверждает перенос исходников, а не выполнение страницы. `Rejected` не
создаёт проект; `Unknown` сохраняет частичную папку для явной проверки, без cleanup/replay.
Предел — 100 файлов и 16 МиБ UTF-8. Распознаваемые SDK/vendor references отклоняются;
vendor activations берутся из того же embedded `web/vendor-manifest.json`, что и UI.
Проверка исполнения произвольного JS и перенос зависимостей не входят в P2a.

## P2b: resolver существующих HTML targets — In Review, 2026-11-11

Application `BrowserTargetResolver` получает проверенные metadata от прежнего owner:
ArtifactCatalogService, ArtifactViewerService или HTML owner после RefreshSelected.
Он проверяет exact URI/scope/kind и различает HtmlArtifact, HtmlResource и HtmlWorkspace.
Текущий снимок не получает право редактирования от совпадения с head; raw HTML остаётся
inert source. Live recovery сохраняет доступное чтение, запрещая edit/restore/annotations.
Нет выбора первого кандидата при неоднозначности или отсутствующем selected source.

Typed descriptor передаётся в существующих preview/page/workspace responses: нового
bridge запроса, I/O, session store или registry нет. Web сверяет адресата и источник,
пересекает capabilities с поддержанными controls; несоответствие не создаёт iframe.
Capabilities описывают доступные команды, не выдают authority и не гарантируют успех:
каждая операция повторяет прежние guards/preflight. Semantic tool binding — P6;
plugin grants — P3, проверка автоматического lifetime — P2c, managed source preview ещё впереди.

## Lifetime, authority и восстановление

Решение пользователя от 2026-11-11: обычная Browser-страница живёт, пока открыта её
вкладка. Switch/hide/move/split/layout restore сохраняют тот же renderer и ввод.
Закрытие Browser-вкладки освобождает renderer, readers/leases и её временные bindings;
повторное открытие загружает target заново. Несохранённый ввод внутри preview не
восстанавливается. Сохранённые исходники, данные и durable annotations/evidence
остаются у domain owner. Source editor сохраняет существующий dirty/`canClose` guard.

Список sessions, отдельная команда «Завершить сессию» и лимит живых страниц с уже
закрытыми вкладками не вводятся. Существующие bounds открытых material views остаются.
Native window Dismiss/скрытие панели не равны закрытию Browser-вкладки. Закрытие UI
не отменяет Run; будущий browser tool получает явный closed/stale target вместо
автоматического переоткрытия страницы или повторения действия.

Альтернатива с detached sessions отложена до отдельного согласованного сценария
фоновой browser automation/shared ownership. Для текущего HTML preview она добавляет
невидимые ресурсы и второе пользовательское управление lifetime без необходимой задачи.
Техническое отделение renderer от Dock placement сохраняется; независимый lifetime
после закрытия вкладки из него не следует. P2c проверяет текущих owners, не вводит
заранее Application registry и новые bridge commands.

При проверке скрытия учитывать: CSS-hidden iframe сам по себе не получает новое
visibility state; нельзя обещать остановку его JavaScript только от скрытия view
([Page Visibility API](https://developer.mozilla.org/en-US/docs/Web/API/Page_Visibility_API)).
Сохранение пользовательских данных и освобождение runtime resources — разные задачи;
на `unload` как единственный cleanup/save hook не полагаться
([Page Lifecycle](https://developer.chrome.com/docs/web-platform/page-lifecycle-api)).

В будущем native/agent контуре sessions private для исходного conversation по умолчанию. Явно shared session
допускает только участников того же разрешённого document/workspace scope; каждый
вызов проверяется снова. Model wait/confirmation не удерживают session или Office
gate. Mutations сериализуются; human input инвалидирует конфликтующий prepared plan.
После dispatch interruption не превращается в доказанное отсутствие эффекта.

Layout хранит только безопасный target для повторного открытия. При restart новая
session получает новые IDs; живая страница, POST и agent operation не replay.
Закрытие исходного document scope освобождает его sessions; чужое активное Office
окно не становится заменой. Durable annotations/evidence сохраняются по существующей
retention policy независимо от Dock view. Browser session registry остаётся live state,
не новым durable store.

HTML restore создаёт новую причинную ревизию существующим owner. Existing plugin
«Восстановить версию» переключает published head с guard и не меняет source files.
В UI различать «Восстановить исходники» и «Активировать опубликованную версию»;
не переопределять прежний вызов под общей кнопкой. Read-only view/checkout не публикуют.

## Изоляция и privacy

External renderer не получает Shell bridge, host objects, plugin grants или privileged
frame channel. Проверяются exact frame/origin/session/generation и bounded DTO.
Page text всегда недоверенные данные. Native CDP доступен только adapter, не модели.

Default external profile — temporary isolated session. Persistent profile — отдельный
явный выбор пользователя, без импорта cookies из обычного браузера; agent sessions
не получают его автоматически. Downloads/popups требуют явного допуска и destination;
clipboard/file picker выполняются через явное пользовательское действие; camera/mic
по умолчанию запрещены. Navigation принимает HTTP(S), внутренние targets — typed
resolver; file/javascript/data не становятся внешними адресами с привилегиями Shell.

Пароли, cookies, tokens, значения приватных controls не входят в snapshots/logs/context.
Screenshot capability включается только после проверки маскирования sensitive областей;
если renderer не может безопасно обработать frame/область, capture отказывается с
объяснением. Это gate P6, не обещание текущей VisualInspector эвристики. Prepared
значимое внешнее действие проходит existing confirmation policy; unknown → verify.

<a id="native-spike"></a>
## Windows spike: renderer внутри Dock

Owner: Office/WebView adapter; status **Open**, запуск на этой машине не выполняется.
Исследуем WinForms child control как ближайший существующей композиции вариант,
WPF child hosting как сравнительный вариант, CompositionController — как отдельный
кандидат с собственным visual/input integration. При равной корректности предпочесть
WinForms child: новый UI stack не нужен. Если оба класса embedding ненадёжны —
явное отдельное непривилегированное WebView2 окно с тем же Session API.
OS browser, запускаемый сейчас Shell, не является этим управляемым fallback.

Microsoft документирует у CompositionController visual target, bounds и mouse/pointer
input. Наличие этих API не подтверждает интеграцию с Dockview внутри Office:
[CompositionController](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller?view=webview2-dotnet-1.0.2903.40).
Для CDP есть async вызов; зависимые команды нужно ожидать последовательно:
[CallDevToolsProtocolMethodAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.calldevtoolsprotocolmethodasync?view=webview2-dotnet-1.0.3719.77).
Документация просмотрена 2026-11-04; доступность точных APIs сверяется с закреплённым
SDK и фактическим WebView2 Runtime, без автоматического обновления пакета.

Проба использует настоящую production Shell и локальную тестовую страницу без секретов.
Это диагностический режим, не параллельный UI. На экране показываются вариант renderer,
Office/Runtime versions, HWND parent, CSS/native bounds, DPI/zoom, focused control,
session/document generation, число живых controls/listeners и память. Итог и причина
ошибки читаемы на фотографии; логи/дампы/файлы с Windows не запрашиваются.

| Проверка | Наблюдение / критерий |
|---|---|
| Task pane, resize, clip, z-order | Surface совпадает с Dock rect; меню/drag overlays доступны; нет перекрытия соседнего view |
| DPI 100/125/150/200%, zoom, два монитора | Bounds и hit testing совпадают после переноса; narrow 420 CSS px остаётся доступным |
| 20 move/split/hide/reopen/layout restore | Document не теряется от placement; hidden renderer не перехватывает input; счётчики controls не растут |
| Focus, Tab/Shift+Tab, IME, shortcuts | Переход между Office/Shell/page корректен, нет невидимого keyboard owner |
| Popup/menu/download/profile | Все пути проходят явно выбранную policy; session/profile isolation сохраняется |
| Crash/dispose/recreate/document close | Старые refs не принимаются; owned controls/leases освобождены; эффекты не replay |
| 1/4/6 tabs, idle и повторные циклы | Зафиксировать память/latency baseline и после закрытия; необъяснённый рост остаётся блокером embedding |

Результаты записываются здесь по каждому кандидату: окружение, фото/evidence, ограничения,
вердикт. До этого выбран только критерий решения, не победивший renderer. Отдельный
fallback при введении регистрируется в MIGRATION_MAP с owner и removal gate:
успешная та же матрица native embedding. В docs-only P0 такого adapter ещё нет.
