# Plugins

Статус: основа v1, 2026-10-19. Владелец — application plugin services;
причина решения — [ADR-0015](decisions/ADR-0015-plugins.md).
Это canonical contract установки, публикации, допуска и протокола плагинов.
Документ действует до удаления контура или передачи контракта другому владельцу.
Windows/WebView2/Office qualification остаётся отдельной.

## Сущности и полномочия

| Сущность | Идентичность | Полномочия |
|---|---|---|
| Изменяемые исходники | `SourceWorkspaceId` | Обычный workspace, files tools и его собственный чат |
| Опубликованный код | `PluginId` + runtime-owned `ReleaseId` | Неизменяемый CAS-снимок; происхождение от исходников не даёт права их менять |
| Выбранные данные | `DataWorkspaceId` + runtime-owned `ProjectId` | Отдельный отзывной доступ к папке, ограниченный возможностями пакета |

`ProjectId` канала — идентичность выданного доступа. Поле `projectId` внутри
Data Templates `project.json` остаётся доменной идентичностью существующего формата;
оно не является полномочием канала. Manifest, vendor lock, каталог, source identity
и успешное чтение ресурса не выдают никаких прав исполнения или доступа к данным.
Папки исходников и данных не могут совпадать или содержать друг друга; проверяются
workspace identity, canonical paths и отсутствие symlink/reparse-переходов.

Runtime clear получает эксклюзивный файловый lease; живые plugin instances,
публикация, vendor resolution/import и исходный чат удерживают совместный lease.
Очистка отказывается работать до их завершения, включая другой процесс/панель.
Явная очистка runtime удаляет registry/CAS и чаты согласно общему контракту;
source/data папки сохраняются, после неё исходники подключаются явно заново.

Глобальная установка не открывает проекты. Пользователь явно выбирает папку данных
в native picker; v1 диалог сообщает о доступе на чтение/запись. Каждый файловый RPC
проверяет экземпляр, выданный `ProjectId`, возможности manifest и право операции.
Команда с `writesData:false` не получает запись даже при writable grant.
Новая публикация не наследует grant предыдущей. Предпросмотр использует отдельную
папку `%AppData%/RNAssistant/plugin-preview-data/<pluginId>/`, не рабочий проект;
обновление снимка сохраняет его тестовые данные.

## Владельцы и хранение

| Владелец | Ответственность |
|---|---|
| Core `PluginModels`, `PluginPackageFiles`, `PluginStore` | Закрытые manifest contracts, состав, registry heads и immutable release metadata через `ResourceAuthorityStore`/CAS |
| Core `VendorCatalog` | Точные версии, зависимости, файлы, SHA-256 и лицензии; никогда не запускает JS |
| Application `PluginApplicationService`, `PluginInstance` | Create/connect/check/apply, выдача/отзыв доступа, command lifecycle и guards |
| `WorkspaceFileService` | Единственный владелец выбранных файлов; exact reads, журнал, guarded streaming write, read-back и explicit reconciliation |
| Application `PluginResultStore` | Ограниченные временные результаты команды через тот же file/CAS owner; не проект данных и не второй durable read model |
| Application `Workspace/WorkspaceConversationService` | Existing `ConversationRunService` → `AgentKernel`, чаты исходников и общий ToolRuntime |
| Office `PluginWebViewHost` | Frame API, отдельные origins, скрытый controller/Worker, byte transport, native picker и drain |
| `web/plugin-host` | Host-owned SDK/runner; не входит в редактируемую поставку плагина |
| `web/js/app-plugins.js` | Навигация, создание, публикация, iframe и workspace-чат; не владелец полномочий |

Registry хранит связи с исходниками и активный release, без копии workspace.
Публикация удерживает полное дерево файлов и разрешённых vendor bytes в CAS;
исходники после проверки могут изменяться без изменения ранее полученного снимка.
Временный результат адресуется в SDK семантическим относительным именем, а его
transport handle остаётся внутри SDK/runtime. Результат доступен только исходному
экземпляру/его командам на той же публикации. Discard и закрытие последнего
экземпляра освобождают private scratch и его current file head; сохранённый
пользовательский результат в data project не удаляется. После аварийного завершения
процесса private scratch может остаться; это открытый cleanup gate, не основание
восстанавливать или автоматически повторять операцию.

## Пользовательский цикл

После divider находятся иконки плагинов и `+`, настройки остаются снизу.
Наведение или фокус на значке показывает общую подсказку справа; у пользовательского
плагина она содержит действия «Редактировать» и «Удалить с панели».
Редактор меняет название и иконку на панели через общий каталог и генератор.
`PluginStore` сохраняет typed `PluginAppearance` в существующей регистрации с
exact-head guard и read-back. Это оформление переживает публикацию, перезапуск,
удаление/восстановление на панели и удаление исходной иконки из библиотеки;
оно не меняет manifest, PluginId, исходники, текущие экземпляры или release.
Удаление отзывает открытые
экземпляры и доступ к проекту, скрывает регистрацию из панели и доступных tools,
но сохраняет исходники, чат, данные и опубликованный release. Встроенный Data Templates
убирается с панели тем же способом; его исходники и публикация сохраняются.
Наведение или фокус на `+` показывает скрытые плагины для
восстановления и ниже действие «Создать плагин». Обычное нажатие `+` сразу открывает
форму; восстановление возвращает прежний release и состояние включения.
Форма показывает будущую иконку и имя плагина над редактором; заголовок меняется
вместе с полем названия. Нажатие на иконку открывает выбор базовых значков из общего
`RNAssistantIcons`, сохранённых SVG и текстовое описание нового значка. По кнопке
«Нарисовать» текущая модель создаёт обычный контурный SVG; после строгой проверки он сразу
сохраняется в библиотеке и копируется в исходники при создании плагина. Базовые
значки нельзя удалять.
При создании исходники по умолчанию получают отдельную уникальную папку в
`%AppData%/RNAssistant/plugin-workspaces`; полный путь возвращается в каталоге.
Другой путь указывается только по выбору пользователя. Уже созданная пустая папка
допустима, а непустая не перезаписывается; готовый `plugin.json` подключается отдельно.
Ответ модели с единственным SVG внутри пояснения или Markdown-блока извлекается и
проходит ту же строгую проверку. Стандартные атрибуты обводки и простые фигуры
принимаются; host нормализует геометрию в пути общего реестра и задаёт оформление.
При неверном SVG модель получает одну попытку исправления с предыдущим ответом и
точной причиной отказа. Для ответа выделяется 4096 токенов; настройки пользователя
не меняются. Невалидный результат не сохраняется, причина отказа видна в UI.
Пользовательский SVG можно добавить вручную или получить из проверки исходников; `plugins.check`
сохраняет каждый валидный SVG-значок manifest в `%AppData%/RNAssistant/plugin-icons` с
идентичностью по SHA-256 путей. Повторная проверка не создаёт копию. Удаление
записи из библиотеки не затрагивает уже скопированные исходники и публикации.
Каталог значков сохраняется при очистке runtime-данных.
При выборе сохранённого SVG новая папка получает свою копию `icon.svg`; при выборе
базового значка manifest хранит его имя. Модель генерации изображений не нужна;
рисование SVG идёт через текущие model settings и сохраняет только валидные пути.
В mock preview создание рисунка и запись исходников имитируются в памяти вкладки.
Scaffold содержит `plugin.json`, UI, CSS, пример Worker-команды и README для агента.
Описание передаётся в чат исходников. Чат использует действующие model settings,
вопросы, confirmation и cancel; API key остаётся у settings owner.
Экран плагина показывает текущий снимок и чат исходников рядом; разделитель
меняет ширину обеих частей. При изменении объявленных файлов исходников открытая
панель разработки через короткую задержку проверяет пакет и обновляет предпросмотр.
Значок обновления запускает ту же проверку вручную. Ошибка проверки сохраняет
последний рабочий экран и явно показывается пользователю. Ни проверка, ни
предпросмотр не публикуют версию. Действия публикации находятся в панели
разработки; это прежние application operations.
Кнопка правой панели в верхней строке открывает и закрывает чат разработки через
общий `RNAssistantPanelToggle`, как инспектор основного чата. Enter/Space работают
как на обычной кнопке; Escape из чата закрывает панель и возвращает фокус на кнопку.
Сворачивание сохраняет выбранную беседу и черновик.

Чаты исходников и workspaces используют `app-workspace-chat.js`, общий
`renderMessageArticle`, Markdown, пустое состояние и размеры composer с Office-чатом.
Копирование, раскрытие действий, ввод Enter/Shift+Enter, рост до шести строк и
вопросы общие; работа с Office-вложениями и document history остаётся у Office owner.
Планирование и reasoning передаются в существующий workspace input contract;
модель берётся из общих настроек. Черновики раздельны по сессиям и сохраняются при
ошибке доставки; повтор того же ввода использует тот же request ID.

В заголовке чата плагина `+` создаёт отдельную workspace-сессию с пустой историей,
сохраняя прежние беседы и их черновики. Кнопка истории открывает текущую сессию
в главном разделе «Проекты». Все беседы исходного workspace собраны под именем
плагина; в дереве можно выбрать старую, переименовать её или открыть рядом с
предпросмотром. Выбор сессии общий для двух экранов в пределах открытой панели.
История хранится только у `WorkspaceConversationService`/`ChatStore` и переживает
перезапуск. `getPluginChat` лишь открывает исходный workspace и последнюю беседу
(создаёт первую, если история пуста); отдельный серверный кэш выбранного чата удалён.
Создание, чтение, отправка, confirmation и отмена используют общие project-chat
operations с точными `WorkspaceId`/`SessionId`; отдельные plugin submit/control
contracts удалены. Поздний ответ не переключает выбранную пользователем сессию.
После явного удаления истории повторное открытие разработки создаёт новую сессию
и возвращает исходный workspace в дерево; файлы плагина остаются на месте.

1. Встроенная **проверка** захватывает immutable candidate, проверяет manifest,
   протокол, состав, хеши и plugin vendor admission. Это не проверка бизнес-логики.
2. **Предпросмотр** открывает именно проверенный снимок; тестовые данные отделены.
3. **Применить** сверяет исходный workspace, candidate и ожидаемый active release,
   атомарно публикует registry head и проверяет его чтением. Известная ошибка до
   переключения оставляет предыдущую версию; потерянный receipt даёт `effect_unknown`.
4. Открытый экран и текущие команды остаются на старой публикации. **Открыть активную**
   создаёт новый экземпляр; новая команда со старого экрана получает явный отказ.
5. **Отключить** запрещает новые рабочие вызовы, отменяет команды и отзывает доступы.
   Исходники, workspace-чаты и пользовательские data projects сохраняются.

Дерево «Проекты» перечисляет подключённые workspaces с их отдельными workspace
sessions. Пользователь может подключить существующую папку или создать новую;
чаты привязаны к точному `WorkspaceId`, а не к Office document session или
текущему grant плагина. Подключённый workspace доступен чату для изменений.
Смена, отключение и удаление плагина не удаляют workspace-чаты. Create/rename/delete,
submit/cancel/resolve идут через `WorkspaceConversationService` и typed bridge.
Удаление истории удаляет сессии выбранного workspace и скрывает его из дерева,
сохраняя папку и файлы; оно доступно и когда папка временно отсутствует.
Повторное подключение возвращает прежний `WorkspaceId`.
Отмена возвращает точный статус запроса; stale и неподтверждённая отмена не
показываются как успешное завершение.
Файлы проекта не прикладываются к сообщению автоматически.

Команды живут независимо от iframe: закрытие экрана не отменяет принятую работу.
Явная отмена, отключение, завершение хоста или пятиминутный deadline закрывают
управляемый controller/Worker; native I/O дренируется перед завершением.
Смена публикации никогда не подменяет код выполняющейся команды.

## PluginManifestV1

Единственный source manifest — `plugin.json`. Неизвестные поля, несовместимый
`hostProtocol`, traversal, ссылки, дубликаты путей/команд, неполный состав и
неразрешённые зависимости отклоняются. Все entrypoints должны входить в `files`.
ID: `[a-z][a-z0-9-]{0,63}`, версия точная `major.minor.patch[-suffix]`.

```json
{
  "formatVersion": 1,
  "id": "example",
  "name": "Example",
  "version": "1.0.0",
  "hostProtocol": 1,
  "entry": "index.html",
  "icon": "puzzle",
  "files": ["plugin.json", "index.html", "app.js", "commands.js"],
  "capabilities": ["project.read"],
  "commands": [{
    "id": "hello",
    "description": "Return a greeting",
    "entry": "commands.js",
    "argumentsSchema": {"type":"object","properties":{"text":{"type":"string","description":"Greeting"}},"required":["text"],"additionalProperties":false},
    "resultSchema": {"type":"object","properties":{"text":{"type":"string","description":"Greeting"}},"required":["text"],"additionalProperties":false},
    "writesData": false
  }],
  "vendors": []
}
```

Schemas используют существующий `ToolSchemaSupport`: закрытый корневой object,
`properties`, `required`, `additionalProperties:false`; каждое корневое поле имеет
`description`. Host проверяет аргументы и результат. Manifest лишь запрашивает
`project.read`/`project.write`; write command также требует `writesData:true`.
`icon` задаёт имя значка из общего реестра `web/js/app-icons.js` либо путь к
`icon.svg` внутри `files`. SVG ограничен 4096 байтами, `viewBox="0 0 24 24"`
и 1–16 фигурами `path`, `circle`, `ellipse`, `rect`, `line`, `polyline`, `polygon`
в необязательных группах `g`. Разрешены `fill="none"`, простая обводка, её ширина,
окончания и соединения; SVG всегда показывается контурным в цвете темы.
Скрипты, стили, текст, ссылки, `use`, изображения, transforms и произвольные атрибуты
запрещены. Проверка отклоняет недопустимый SVG до публикации.
Rail получает только проверенные координаты путей из текущей публикации; до
первой публикации — из исходников. Имя плагина остаётся доступной подписью.

Vendors перечисляются как `{"id":"fuse","version":"7.1.0","mount":"vendor/fuse"}`.
Все транзитивные зависимости должны быть явно смонтированы точными версиями.
Пересекающиеся mounts допустимы только для одинаковых bytes; коллизия запрещена.
Пакет: до 512 файлов, 8 MiB на файл и 32 MiB суммарно. Команды/dependencies — до 32.

## SDK и command service

UI подключает `/.host/sdk.js`, затем использует `RNPlugin`:

```js
const project = await RNPlugin.pickProject();
const call = RNPlugin.invoke('hello', {text: 'Привет'}, message => showProgress(message));
const answer = await call;
// Явная отмена: await call.cancel();
```

Worker entrypoint регистрирует обработчики без DOM, Office и внешней сети:

```js
RNPluginCommands.register('hello', async (args, context) => ({text: args.text}));
```

`context.files`: `open(path)` с bounded `readRange`/`close`, `readText`,
`writeText`, `createWriter`, `root` с directory/file handles и `reconcile(path)`.
`context.results.create(name)` создаёт временный writer; `results.open(name)`
возвращает retained file/stream. `context.progress(message)` передаёт progress.
Writer принимает последовательные chunks, `close` публикует, `abort` удаляет staging.
Открытие существующей записи закрепляет exact observation до dispatch. Read-only
команды могут создавать временный результат, но не писать пользовательские файлы.
Native filesystem handles не реализуют recursive delete: неясный save сохраняет
частичные файлы как evidence и требует явной проверки.

Одна операция закрепляет release, grant и принятые source snapshots. Файлы/результаты
ограничены 256 MiB, native chunk — 256 KiB, полное JS `arrayBuffer` — 16 MiB;
для большего файла используются ranges. До 256 открытых readers, 4 writers,
128 результатов/1 GiB на живого владельца; SDK — 32 pending RPC, message — 1 MiB.
Неопределённый эффект остаётся неопределённым после последующих успешных записей.
Автоматического retry нет; `reconcile` инспектирует существующие bytes и сохраняет
неопределённость причинности первоначальной операции.

UI и workspace-агент вызывают один `PluginApplicationService.InvokeAsync`.
`plugins.<id>.<command>` подключены через существующий ToolRuntime с manifest schemas,
замороженным release binding, policy, prepared state, journal/read-back evidence.
Выбранный grant фиксируется до confirmation; его замена отклоняет продолжение.
`plugins.check`/`plugins.apply` доступны только чату source workspace; аргументы пусты,
candidate удерживает runtime. Новый check инвалидирует ожидающее подтверждение apply.
Model-facing resource reads остаются `common.resources_*`; модели не выдаются
candidate/release IDs, `ResourceRef`, paths native staging или grant tokens.
Полезный результат команды — её доменные данные/семантические имена.

Workspace application source импортируется в Framework/CLI через `.projitems`.
Сохранён namespace `RNAssistant.Runtime`, реализации в `RNAssistant.Application/Workspace`.
Chromium verifier вынесен за `IWorkspaceWebVerifierFactory`: CLI подключает BrowserHost,
Office не получает зависимость от .NET 8; при отсутствии factory `web.verify` не объявлен.

## Изоляция

Каждый iframe получает уникальный `https://instance-….rnassistant-plugin.invalid`
origin. Main bridge не принимает plugin messages. Host связывает точный iframe,
instance, entry URL и типизированный protocol v1 через
[WebView2 Frame API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/frames).
Navigation/destroy отзывает канал; nested frames, popups и host objects запрещены.
Свой CSP разрешает только захваченный код, self Worker/byte routes и локальные styles;
внешняя сеть, формы и объекты запрещены. Команды работают в скрытом in-private
controller с Worker и тем же admission; открытый экран им не нужен.

Protocol/file routes не являются общим filesystem/http proxy. Каждый exact resource
проверяет origin, живой экземпляр, retained member/handle, диапазон и grant. Transport
ограничивает запросы и ответы; `plugin://`/`file://`/Office bridge недоступны плагину.
Windows проверка Frame events, redirect, permissions, download stream, cancel/drain,
renderer failure и нескольких панелей обязательна до native qualification.

## VendorManifestV1 и отдельный допуск

Поставляемый каталог `web/vendor-packages.json` генерируется без выполнения JS:
`node tools/generate-vendor-packages.js`. Источники — существующий vendor inventory
и portable Data Templates vendor lock. Локальные поставки:
`%AppData%/RNAssistant/vendors/<id>/<version>/vendor.json` и listed files.
Можно импортировать папку через UI или разместить её вручную и обновить каталог.
Scanning показывает metadata, ничего не исполняет. Resolve проверяет весь граф,
циклы, точные версии, SHA-256 и фиксирует содержание версии через authority/CAS.
Граф ограничен 64 пакетами/32 MiB; каталог — 300 версиями.
Изменение уже разрешённой версии запрещено; изменение выпускается новой версией.

```json
{
  "formatVersion": 1, "id": "example-lib", "version": "1.0.0",
  "description": "Purpose", "source": "local", "license": "MIT",
  "licenses": ["LICENSE"],
  "files": [{"path":"library.js","sha256":"<64 lowercase hex>"}, {"path":"LICENSE","sha256":"<64 lowercase hex>"}],
  "entrypoints": [{"path":"library.js","kind":"Script"}],
  "dependencies": []
}
```

Entrypoint kinds: `Script`, `Style`, `Module`, `Worker`, `Asset`.
`vendor.json` не включает себя в `files`. `source` — provenance, не URL автозагрузки.

| Контекст | Допуск |
|---|---|
| HTML-материал | Собственная sandbox/CSP; classic scripts/styles. Modules/Workers отклоняются; inline delivery дополнительно отклоняет внешние assets. Точная local зависимость задаётся `rn-vendor:id@version` в HTML/JS. |
| Плагин | Проверенный состав и package mounts, изолированный Worker, отдельно выданный project grant. |
| Экспорт | Полнота переносимого состава, hashes, licenses; без native SDK, grants, credentials и user data. Data Templates exporter отдельно сверяет plugin dependencies с portable vendor lock. |

Общего флага approved нет. Например Module разрешается как plugin/export package,
но отклоняется HTML admission. Допуск пакета не доказывает работоспособность библиотеки
в конкретном браузере и не переносится между контекстами.

## Data Templates и проверки

Первый обычный plugin — `web/data-templates/plugin.json`. При первой установке он
копируется в отдельный source workspace и проходит тот же check/apply. Форма
`project.json`, UI/core, browser processing и standalone folder export сохранены.
Native `ui/plugin-adapter.js` даёт scoped directory handles и передаёт inspection/XLSX
в общий Worker service. `extract` создаёт временный canonical result;
`extract-to-file` сохраняет его в выбранную относительную data path с подтверждением
ToolRuntime. UI сохраняет прежние `results/.../data.json` и receipt формата проекта.

Exporter, inventory, vendor gate и export tests переключены на `plugin.json`;
`application.json` удалён вместе с root-DOM adapters. `application.lock.json` —
только generated export inventory, не второй source manifest. Старый compiled
Tabular pin остаётся лишь у перечисленных [migration consumers](stabilization/MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16),
не как fallback нового plugin path.

Минимальные проверки: Harness `plugins:`, source inclusion; browser
`tests/web/plugins.test.js` (реальный Chromium Worker, mock native transport,
creation/publication/lifetime, XLSX/result read-back); vendor gate и HTML vendor tests;
затронутые Data Templates file/engine tests. Полный standalone export-suite проходит
в Chromium, включая версии общего справочника, mapping, результаты и отмену.
Browser mock не квалифицирует WebView2, DPAPI, native picker или live model provider.

Память, conversation events, прямые Office-мутации, внешние API, marketplace,
автозагрузка vendor из сети и перенос остальных вкладок не входят в v1.
