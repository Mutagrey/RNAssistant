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
Excel, Word, PowerPoint и Outlook, включая состояние без чатов. Выбор строки
открывает приложение Office или выводит на передний план его запущенное окно;
текущая in-process панель остаётся привязанной к своему документу. Desktop после
запуска подключает обнаруженный документ. Для Outlook сначала проверяется
запущенный `OUTLOOK.EXE`: повторный процесс не создаётся, а Desktop подключает
обнаруженный mailbox по StoreID. Если Office ещё не открыл документ или Outlook
не предоставил mailbox, Desktop показывает ожидание и предлагает обновить список.
Когда Desktop ещё не подключён к Office, те же четыре действия доступны в его
начальном окне.

## Activation and target selection

Desktop принимает `--host`, `--hwnd`, `--pid`/`--process-id`,
`--document-path`, `--document-title`, `--selection`, `--target`,
`--target-base64` и `--action`. Native wrappers передают window handle и target
JSON. Приложение single-instance; последующие launches пересылают activation через
user-scoped named pipe.

Target registry хранит только descriptors: host, hwnd, process id, document
path/title, folder/mail id и selection reference. Долгоживущие COM-объекты в нём
не сохраняются; bound adapter разрешает live object во время операции.

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
  `init`, `confirmAgentTool` и `runTool` сначала переходят на cancellable worker boundary;
  Office-owned действия внутри них по-прежнему маршалятся через bound dispatcher.
  Поэтому долгий run не блокирует доставку cancel и chat-navigation команд.
- Bridge request разбирается один раз как typed envelope. Любая terminal response,
  включая внешний аварийный путь, сериализуется штатным `BridgeResponse` и сохраняет
  request id. Если повреждение envelope не позволяет восстановить id, UI помечает
  transport недоступным и отклоняет все pending promises с явным
  `bridge_transport_failed`; mutation автоматически не повторяется.
- Фоновая синхронизация чатов в WebView использует catalog-only projection
  `listChats`: summaries чатов/документов, active id и run view. Полный transcript,
  context, artifacts и HTML workspace загружаются только через `init`, явный выбор
  или действие чата, либо через `getChatState`, когда требуется обновить более
  новую revision активного чата. При фокусе/возврате вкладки проверка запускается
  сразу; обычный фоновый опрос идёт раз в минуту и пропускается во время активной
  отправки сообщения.
- WebView не рендерит скрытые transcript, Artifact Library/HTML workspace и
  Library surfaces при применении состояния. CodeMirror создаётся только при
  первом открытии владеющей вкладки; ECharts загружается только для фактической
  диаграммы/HTML preview, а каталог моделей — при первом открытии model picker или
  явном запросе из настроек. Выбор артефакта обновляет detail surface без
  пересоздания дерева; `on_preview` data refresh запускается только для выбранного
  HTML file/data surface.
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
ответа чата и чтения/отрисовки Skill в тот же runtime log.
Записи содержат время и размеры, без текста чата или запроса.

Полное состояние чата сохраняет порядок сообщений для точных индексов редактирования,
но передаёт WebView только поля для отображения. Скрытые protocol messages содержат
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
