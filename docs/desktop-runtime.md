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
Нажатие во время перехода между чатами ставит создание в ту же очередь, а не
теряется. Рядом со строками Office показываются ожидание, причина недоступности
и ошибка подключения; повторное нажатие во время создания не создаёт второй чат.
Выбор строки создаёт сохраняемый чат для точного открытого документа; если в
Excel, Word или PowerPoint нет документа, сначала создаётся пустой документ с
устойчивым document ID, чтобы чат сохранил привязку после Save As.
При холодном запуске Excel/Word/PowerPoint координатор получает application через
COM activation и сохраняет этот же объект на STA будущего adapter: обнаружение,
выбор документа и bind не требуют повторного поиска запущенного приложения в ROT.
Если процесс уже существует, второй не запускается. Подготовка пустого документа
выполняется один раз; ошибки после её начала не проглатываются и не запускают
автоматический повтор. `Office chat creation` log содержит host, этап ошибки и время.
При нескольких документах выбирается активный, неоднозначный выбор завершается
ошибкой без создания чата. Для Outlook сначала проверяется запущенный
`OUTLOOK.EXE`, повторный процесс не создаётся; чат привязывается к открытому
mailbox по StoreID в Desktop; из VSTO/NativeHostCli выбирается активное окно
письма или папки. Если профиль ещё не предоставил нужный target, действие
завершается ошибкой без чата. Черновик письма нужно сохранить перед созданием
чата, чтобы получить устойчивый EntryID. Desktop подключает target и открывает новый чат.
В VSTO и NativeHostCli та же панель переключает привязку на новый документ и
загружает его инструменты и контекст; начатый run перед сменой документа должен
завершиться. Токен принадлежит транспорту панели: bootstrap v2 передаёт его отдельно
от application `init`, а смена документа применяет только состояние приложения.
Следующий запрос использует прежний действующий токен панели. Между чатами текущего
документа можно переключаться во время run:
он продолжает работу в исходном чате. Bridge проверяет принадлежность чата текущему
документу до запроса эксклюзивной смены привязки; незавершённый inbox run не блокирует
такой выбор и последующую отмену исходного run. Выбор уже созданного чата другого документа
подключает точный открытый target, если он доступен. Если документ закрыт или
точная привязка недоступна, панель открывает сохранённый чат с его историей,
контекстом и HTML. Выбор чата не запускает Office и не открывает файл.
Локальные инструменты продолжают работать; Office/VBA и live Office reads
недоступны, что явно отражается в интерфейсе и контексте модели. Другой открытый
документ не становится целью этого чата. Для возвращения доступа к Office нужно
открыть исходный документ и повторно выбрать чат, включая уже выбранный.
После закрытия bound document adapter сохраняет последние известные identity и
title только для навигации; они не доказывают liveness. Каталог и tool policy
проверяют живую exact session. Выбор закрытого чата сохраняет его document
authority и не меняет authority текущего Office target. Владельцы поведения —
`ChatSessionService` и Office chat coordinators; execution guard остаётся общим.
Когда Desktop ещё не подключён к Office, четыре действия создания чата доступны
в его начальном окне.
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

The plugin rail opens isolated HTML applications. Its development pane shows the
source workspace chat; user-data selection is a separate native folder grant.
Data Templates uses this path and its existing project format. Source/release/data
identities do not bind an Office target or attach files to document chat. The shared
application module retains legacy project-preparation commands for CLI/native
consumers, without a second root workspace UI. See [Plugins](plugins.md); actual
Windows/WebView2/Office behavior remains unqualified.

Each source/project workspace run may borrow the currently bound Office document.
The document target is captured at input acceptance and reused on confirmation;
host tools and semantic Office reads keep the exact document guard. A later run
captures the then-current document. Workspace files, history and prompts retain
their workspace owner, while the Office operations use the shared local runtime.

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
  Office UI callback. `listChats`, `getChatState`, `selectChat`, `submitChatInput`,
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
- Выбор чата после точной привязки отправляет связанный с request id короткий
  `selection` state: UI очищает прежний transcript/HTML и показывает загрузку.
  Полный ответ строится из того же загруженного `ChatSession`; поздний shell после
  terminal response игнорируется. `getChatState` для UI не повторяет сканирование
  каталога, уже полученного через `listChats`. Смена модели и reasoning возвращает
  только revision и настройки чата (для модели также оценку контекста), без
  повторной сборки transcript, artifact library и HTML metadata.
- WebView не рендерит скрытые transcript, Artifact Library/HTML workspace и
  Library surfaces при применении состояния. CodeMirror создаётся только при
  первом открытии владеющей вкладки; ECharts загружается только для фактической
  диаграммы/HTML preview, а каталог моделей — при первом открытии model picker или
  явном запросе из настроек. Выбор артефакта обновляет detail surface без
  пересоздания дерева; `on_preview` data refresh запускается только для выбранного
  HTML file/data surface.
- В ленте чата завершённые действия, детали и изменения имеют карточки с фоном;
  transcript и composer занимают доступную ширину окна. Сырой HTML в сообщениях
  показывается как текст, а не как элементы страницы. Акцент интерфейса задаётся
  палитрой из десяти цветов (синий, бирюзовый, зеленый, лаймовый, желтый,
  оранжевый, розовый, пурпурный, фиолетовый, белый), заданной в
  `web/js/app-accent.js`, и сохраняется вместе с настройками. В светлой теме
  рабочие оттенки затемняются для читаемости, а образцы сохраняют исходный цвет.
  Сообщения пользователя получают мягкий фон и контрастный цвет текста из того же
  акцента в обеих темах; ссылки и встроенный код остаются различимыми.
  Обычный завершённый ответ не создаёт отдельную карточку статуса; подтверждённые
  эффекты и незавершённые пункты показываются компактной строкой перед ответом.
  Подтверждённые эффекты,
  ошибки и неизвестный результат видны вне скрытых деталей.
- Меню «+» над полем ввода объединяет вложения, добавление выделения документа,
  режимы Agent/Plan/Chat и доступное сжатие контекста. Выбор модели остаётся справа;
  режим отмечен внутри меню. Во время run режим недоступен, вложения и выделение
  сохраняют прежние правила доступности. Меню закрывается после действия,
  по клику снаружи или Escape. Owner: `app-chat-composer.js`.
- Использование контекста представлено одним кольцом: наведение или фокус
  показывает подсказку, нажатие открывает подробную панель справа. Просмотр
  запроса/ответа модели доступен из этой панели. Доля кольца — точные входные токены
  последнего API-запроса относительно полного окна модели; отсутствие входного
  usage или размера окна явно обозначено, оценка не подставляется.
- Индикатор связи с моделью и кольцо контекста используют общую плашку подсказки
  composer: наведение и клавиатурный фокус, закрытие по Escape и размещение внутри
  окна. Длинная диагностика прокручивается внутри плашки; системный `title` для
  индикатора связи не используется. Owner: `app-chat-composer.js`, содержимое связи —
  `app-diagnostics.js`. Плашки «Ресурсы» и плана расположены ниже меню и подсказок
  composer, чтобы не перекрывать их текст и действия.
- При недостатке ширины группы кнопок composer переносятся на следующую строку;
  элементы управления остаются видимыми и не перекрывают друг друга.
  В узком окне вкладки настроек переносятся на строки, а список модулей VBA
  ограничен по высоте, оставляя основное место редактору.
- Очередь Steer/Queue отображается пузырями пользователя в конце ленты, после
  текущего ответа агента. Пузырь появляется при отправке и остаётся до получения
  точного сообщения истории; поле ввода не теряет новый черновик при позднем ack.
  Опустевшая очередь не удаляет пузырь: её состояние может опережать страницу
  истории даже в одном ответе bridge. Явная отправка прокручивает ленту к новому
  пузырю сразу; поздний ack не отменяет последующую ручную прокрутку.
  Ожидающий текст показывается полностью, вложения — именами файлов; меню пузыря
  позволяет редактировать, удалить или отправить сейчас. Пауза и возобновление
  очереди также видны в ленте. Runtime остаётся владельцем доставки.
- Ручная прокрутка вверх отключает следование за ответом, включая короткое движение
  рядом с концом ленты. Streaming, обновление очереди и подгрузка истории сохраняют
  видимую позицию. Следование возобновляется при прокрутке до конца или кнопкой
  «К последним сообщениям», а также при отправке своего сообщения; отложенный
  animation frame не отменяет жест пользователя. Пока следование включено,
  позднее изменение высоты сообщений (изображения, viewers) и размера области
  чата удерживает фактический конец ленты в поле зрения; изменение разметки само
  по себе не отключает следование.
  Область чата доступна для прокрутки с клавиатуры; статус очереди объявляется
  отдельно от потокового текста. Owner: `app-messages.js` / `app-chat-inbox.js`.
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
`Bridge response timing`, `WebView post timing` и `WebView render timing`. Bridge разделяет выполнение
запроса и сериализацию ответа; WebView передаёт только медленные замеры запуска,
навигации (`kind=chatNavigation`, включая ожидание очереди), ответа чата и
чтения/отрисовки Skill в тот же runtime log.
`Chat storage timing` разделяет ожидание storage gate, холодный replay, попадание
в кеш и подготовку/запись save; `Chat state timing` разделяет сообщения, библиотеку,
каталог, контекст, оценку usage и HTML metadata.
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
каталожный hash и CAS-ссылки аргументов/результата. `submitChatInput` возвращает
inbox receipt; последующие `chatStateChanged` / `getChatState` передают состояние
чата, а детали остаются в activity/trajectory. Полные тела остаются в event/CAS и
доступны через адресные diagnostic/resource запросы. Ответ bridge сериализуется
непосредственно из typed payload без промежуточного `JToken` дерева. `Startup timing`
отдельно показывает `prompts`, `tools`, `skills` внутри `libraries`.
После финального сохранения `Chat response projection` отдельно измеряет
повторную сборку каталогов `tools`/`skills`, заголовки `chats` и остальное
состояние ответа. Статус «Завершаю ответ и обновляю чат» включает эти этапы,
поэтому его длительность не равна времени записи JSONL.

Реальные multi-instance attach, Office modal/busy states и production STA/COM
cleanup требуют Windows x64 + Office x64 + VS 2022 qualification.
