# Universal Browser: архитектурное решение

Дата: 2026-11-04. Статус: **Proposed**; код не изменён.
Согласованы документы + план и страницы во вкладках существующего Dockview.
Native renderer остаётся отдельным решением по evidence. Контекст — [аудит](01_AUDIT.md).

## Владельцы и границы

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

## P2a: самостоятельный проект и backing — Proposed, 2026-11-10

Предлагаемый первый срез — явная копия сохранённого HTML в самостоятельный workspace.
Owner identity — existing `WorkspaceId`; исходный document artifact остаётся exact
снимком. Чат, Browser tab и BrowserSession не становятся владельцами исходников.

`WorkspaceStore` сейчас открывает каталоги, а file authority принадлежит
`WorkspaceFileService` / ResourceAuthority/CAS. Поэтому **managed** предлагается как
обычный workspace в каталоге приложения: изменяемые исходники — файлы, CAS хранит
точные версии/evidence. **Directory** использует выбранную пользователем папку через
тот же owner. Это следует существующей [workspace-first границе](../decisions/ADR-0013-workspace-first-runtime.md),
без CAS-only редактора, второго каталога проектов или BrowserRevisionStore.
Нынешняя подпись «В приложении · CAS» описывает saved snapshot, не managed workspace.
Новая подпись должна различать исходники и снимок; текущий UI этим решением не меняется.

Application координирует import через существующих export/file/workspace owners;
`WorkspaceApplicationService` с mutable selected workspace не становится глобальным
resolver. Target содержит exact workspace binding, а не «открытый сейчас проект».
Plugin source/release/data grants сохраняют прежних owners; import HTML не публикует plugin.

Начальный import ограничен самодостаточным HTML/CSS/JS-пакетом. Document-bound data,
SDK refs или зависимости без переносимого manifest — явный отказ **до записи**, без
молчаливой потери данных. Проверяются entry point, пути, коллизии имён и общий размер.
Новый workspace появляется в каталоге только после чтения записанных файлов и сверки;
сбой не удаляет исходный artifact и не повторяет неизвестный effect автоматически.
Existing document HTML остаётся у DocumentArtifactStore: это copy, не массовая миграция.

## Lifetime, authority и восстановление

В P1 сохраняется нынешний lifetime provider: close освобождает iframe. Отделение
живой session вводится P2 с явными UI-командами, а не скрытым изменением P1.

В P2 hide/move/split закрывают только presentation/читателей view. Close Dock tab
отсоединяет view; session доступна через список sessions и может быть открыта снова.
«Закрыть сессию» освобождает renderer/leases и инвалидирует её bindings; «Остановить
запуск» остаётся отдельной командой существующего runtime. Dirty editor использует
свой `canClose`; закрытие session не обходит несохранённые изменения.

Sessions private для исходного conversation по умолчанию. Явно shared session
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
