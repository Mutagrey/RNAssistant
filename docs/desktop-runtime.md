# Desktop runtime

Статус: канонический контракт standalone WinForms/WebView2 shell. Отложенные
продуктовые улучшения находятся в
[BACKLOG](stabilization/BACKLOG.md#deferred-product-decisions), а Windows gates —
в начале [PROGRESS](stabilization/PROGRESS.md).

## Runtime path

```text
Office launcher or manual attach
    -> RNAssistant.Desktop.exe
    -> WinForms shell + WebView2
    -> RNAssistant.Office controller and STA dispatcher
    -> RNAssistant.OfficeHosts bound COM adapter
    -> Office object model
```

`RNAssistant.*AddIn` остаются compatibility/debug VSTO shells. VBA launchers для
Excel, Word, PowerPoint и Outlook находятся в `wrappers/native`.

Список чатов в Desktop, VSTO и in-process NativeHostCli всегда показывает строки
«Новый чат» для Excel, Word, PowerPoint и Outlook, включая состояние без чатов.
Выбор строки создаёт сохраняемый чат для точного открытого документа; если в
Excel, Word или PowerPoint нет документа, сначала создаётся пустой документ с
устойчивым document ID, чтобы чат сохранил привязку после Save As.
При нескольких документах выбирается активный, неоднозначный выбор завершается
ошибкой без создания чата. Для Outlook сначала проверяется запущенный
`OUTLOOK.EXE`, повторный процесс не создаётся; чат привязывается к открытому
mailbox по StoreID в Desktop; из VSTO/NativeHostCli выбирается активное окно
письма или папки. Если профиль ещё не предоставил нужный target, действие
завершается ошибкой без чата. Черновик письма нужно сохранить перед созданием
чата, чтобы получить устойчивый EntryID. Desktop подключает target и открывает новый чат.
В VSTO и NativeHostCli та же панель переключает привязку на новый документ и
загружает его инструменты и контекст; начатый run перед переключением должен
завершиться. Выбор уже созданного чата другого документа также переключает
текущую панель на точный открытый target; если документ закрыт, показывается
ошибка с просьбой открыть его. Когда Desktop ещё не подключён к Office, те же четыре действия
доступны в его начальном окне.
Создание/выбор чата, смена документа и инициализация панели проходят через одну
очередь навигации. Она дожидается текущей фоновой синхронизации каталога чатов и
сохранения модели/режима/reasoning, отменяет и дожидается загрузки каталога моделей и
приостанавливает новые опросы и отправку сообщений до завершения перехода.
Из ещё не отправленных запросов выбора чата выполняется последний; уже завершённый
переход применяется до следующего, поэтому ошибка следующего не оставляет UI на
прежней привязке. Диагностический timing-запрос не занимает эксклюзивную привязку.
Закрытие панели отменяет принятые запросы и освобождает
controller/Office adapter после завершения их bridge/resource обработчиков.
Desktop при замене или закрытии панели также откладывает освобождение исходного
Office adapter до завершения runtime shutdown без ожидания на UI-потоке.

## Activation and target selection

Desktop принимает `--host`, `--hwnd`, `--pid`/`--process-id`,
`--document-path`, `--document-title`, `--selection`, `--target`,
`--target-base64` и `--action`. Native wrappers передают window handle и target
JSON. Приложение single-instance; последующие launches пересылают activation через
user-scoped named pipe.

Target registry хранит только descriptors: host, hwnd, process id, document
path/title, folder/mail id и selection reference. Долгоживущие COM-объекты в нём
не сохраняются; при attach новый adapter разрешает и удерживает exact Office target,
а HostRuntime проверяет эту привязку перед операцией.

- `Manual` — первый target выбирается автоматически, последующие activation только
  обновляют список. Пользователь явно меняет рабочий документ.
- `Auto follow` — launcher activation сразу меняет выбранный target.

Проверка foreground Office в `Auto follow` не запускается, пока фокус находится
внутри окна RN Assistant.

Уже принятый run закреплён за exact document session и не следует за фокусом.

## Размер окна

В «Настройки → Интерфейс» задаётся ширина отдельных окон Desktop и NativeHostCli
в пикселях.
По умолчанию 1600; сохранённое значение ограничено диапазоном 900–3840 и
доступной шириной экрана. Изменение сразу применяется к обычному окну; для
развёрнутого или полноэкранного окна — при возврате к обычному размеру.
Размер VSTO-панелей внутри Office этой настройкой не меняется.

## Safety and runtime limits

- Explicit `hwnd` проверяется против разрешённого COM application/window;
  несовпадение завершает attach ошибкой.
- Excel сначала разрешает application через native Office window object, затем
  использует ROT fallback. Multi-instance enumeration остаётся best-effort;
  launcher/foreground `hwnd` является наиболее точным источником.
- COM calls проходят через `DispatchedOfficeApplicationAdapter` и выделенный STA.
- WebView bridge не выполняет полную загрузку/проекцию чата или agent/tool run в
  Office UI callback. `listChats`, `getChatState`, `selectChat`, `sendChat`,
  `init`, создание/удаление/переименование/fork чатов, открытие/активация документов,
  Office chat coordinators, `setChatModel`, `setChatMode`, `setChatReasoning`,
  `getModelCatalog`, `confirmAgentTool` и `runTool` сначала переходят на worker boundary;
  Office-owned действия внутри них по-прежнему маршалятся через bound dispatcher.
  Поэтому долгий run не блокирует доставку cancel и chat-navigation команд.
- Сканирование незавершённых runs выполняется однократно при первом запросе чата,
  до чтения/изменения его состояния. Конструктор controller не сканирует историю
  на UI-потоке до появления WebView. Медленный этап пишет `Chat startup recovery timing`.
- Bridge request разбирается один раз как typed envelope. Любая terminal response,
  включая внешний аварийный путь, сериализуется штатным `BridgeResponse` и сохраняет
  request id. Если повреждение envelope не позволяет восстановить id, UI помечает
  transport недоступным и отклоняет все pending promises с явным
  `bridge_transport_failed`; mutation автоматически не повторяется.
- Фоновая синхронизация чатов в WebView использует catalog-only projection
  `listChats`: summaries чатов/документов, active id и run view. Страница transcript,
  context, artifacts и HTML workspace загружаются только через `init`, явный выбор
  или действие чата, либо через `getChatState`, когда требуется обновить более
  новую revision активного чата. При фокусе/возврате вкладки проверка запускается
  сразу при отсутствии навигации, инициализации и активной отправки сообщения;
  DOM-событие focus не считается принудительным обновлением. Обычный фоновый
  опрос идёт раз в минуту.
- WebView не рендерит скрытые transcript, Artifact Library/HTML workspace и
  Library surfaces при применении состояния. CodeMirror создаётся только при
  первом открытии владеющей вкладки; ECharts загружается только для фактической
  диаграммы/HTML preview, а каталог моделей — при первом открытии model picker или
  явном запросе из настроек. Выбор артефакта обновляет detail surface без
  пересоздания дерева; `on_preview` data refresh запускается только для выбранного
  HTML file/data surface.
- Меню моделей строится при открытии и сохраняет DOM при неизменном каталоге и
  выборе. Скрытые таблицы моделей в настройках не пересоздаются из чата; дублирующий
  скрытый select удалён. Частые activity/stream events сохраняются сразу, а их
  отрисовка объединяется в один animation frame.
- Загрузка каталога моделей имеет один pending request с отменой до HTTP transport.
  Переключение дожидается его terminal response и ответа на cancel; запоздавший
  каталог прежней привязки/настроек не применяется. Инициализация новой привязки и
  сохранение настроек сбрасывают каталог. Chat picker использует сохранённый API key;
  ввод формы передаётся только при загрузке из настроек. Каталог другого URL или
  с явно введённым ключом не записывает capabilities в сохранённые настройки.
  При выходе из настроек preview-каталог сбрасывается; выбор модели чата повторно
  загружает сохранённые настройки после завершения отмены.
- HTML bind/refresh выполняют вложенный Office source-read через exact bound
  `HostRuntime.ReadDocument`; document gate и owner STA не охватывают последующую
  HTML/CAS работу.
- Mutations используют общий confirmation и ToolRuntime policy; успешный COM return
  сам по себе не доказывает effect.
- Outlook выбирает Inspector раньше Explorer selection.
- Outlook получает HWND окна Inspector/Explorer через COM `IOleWindow` для точной
  привязки панели VSTO и Desktop target. Кнопка панели сообщает об ошибке, если
  активное окно нельзя определить.
- Desktop обнаруживает открытые Outlook mailboxes по StoreID и показывает их в дереве
  чатов независимо от текущей папки. На старте без другого target подключается
  первый обнаруженный ящик. Переход к чату другого ящика сначала перепривязывает
  runtime к нему; сохранённый чат остаётся виден при закрытом Outlook.
  Подключённые PST входят в архивный поиск выбранного ящика, но не становятся
  отдельными mailbox targets, если не служат delivery store учётной записи.

Desktop не требует ClickOnce. `install-desktop-local.cmd` сохраняет
`RNASSISTANT_DESKTOP_EXE` в CurrentUser environment. Logs находятся в
`%LOCALAPPDATA%\OfficeAssistant\logs`; fixed WebView2 fallback — в
`vendor/webview2-runtime`.

Для диагностики задержек Desktop log пишет `Attach timing`; runtime log
`rnassistant.log` в каталоге данных приложения пишет медленные `WebView startup`,
`WebView navigation`, `Startup`, `Chat headers`, `Chat model setup`,
`Model request`, `Chat save`, `Chat turn completion`, `Chat response projection`, `Skill source`,
`Bridge response timing` и `WebView render timing`. Bridge разделяет выполнение
запроса и сериализацию ответа; WebView передаёт только медленные замеры запуска,
навигации (`kind=chatNavigation`, включая ожидание очереди), ответа чата и
чтения/отрисовки Skill в тот же runtime log.
Записи содержат время и размеры, без текста чата или запроса.

Состояние чата передаёт WebView только последние 80 сообщений; предыдущие
читаются страницами по запросу, а окно UI ограничено 240 сообщениями.
Запоздавшая страница или повторная загрузка истории применяется только к тому же
чату, версии навигации и исходной ревизии; переключение чата или более новый
transcript не могут вернуть старую историю.
Идентификаторы сообщений сохраняют точность редактирования, удаления и fork,
даже когда локальные индексы относятся лишь к странице. Закрытые этапы агента
строят подробный DOM только при раскрытии. Скрытые protocol messages содержат
id, role, marker и run id без model/tool body; вложения сообщений содержат только
метаданные карточки без извлечённого текста. Activity передаёт компактную пару
source-owned `Dispatch`/`Effect` для статуса действия, но не включает runtime guard,
каталожный hash и CAS-ссылки аргументов/результата. Завершающий
`sendChat.toolResults` содержит только id инструмента, успех и короткое сообщение;
детали остаются в activity/trajectory. Полные тела остаются в event/CAS и
доступны через адресные diagnostic/resource запросы. Ответ bridge сериализуется
непосредственно из typed payload без промежуточного `JToken` дерева. `Startup timing`
отдельно показывает `prompts`, `tools`, `skills` внутри `libraries`.
После финального сохранения `Chat response projection` отдельно измеряет
повторную сборку каталогов `tools`/`skills`, заголовки `chats` и остальное
состояние ответа. Статус «Завершаю ответ и обновляю чат» включает эти этапы,
поэтому его длительность не равна времени записи JSONL.

Реальные multi-instance attach, Office modal/busy states и production STA/COM
cleanup требуют Windows x64 + Office x64 + VS 2022 qualification.
