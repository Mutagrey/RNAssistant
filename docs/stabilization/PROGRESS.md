# Stabilization progress

## Ожидающий ввод проецируется одинаково — 2026-10-27

Office inbox snapshot и Workspace pending-steer snapshot используют одно правило
`ConversationInboxService`: сохранённый user message больше не показывается как
недоставленный `Delivering`. Workspace больше не собирает этот список отдельным
LINQ-проходом по журналу. Его read-only запрос по-прежнему читает canonical events
и загружает тела только для видимых элементов; обычный Office poll без claim не
сканирует историю сообщений.

Host-neutral Harness: Office/Workspace projection и существующие inbox FIFO,
Workspace active steer/recovery — pass. S1c остаётся открытым: общий scheduler,
receipt lookup, полный recovery и UI-controller ещё раздельны. Windows/Office/
WebView2 не проверены; с Windows-машины доступны только фотографии экрана.

## Восстановление input receipt использует общий inbox — 2026-10-27

Office explicit resume и Workspace activation/resume теперь сверяют `Delivering`
с сохранённым user message в `ConversationInboxService`. Workspace больше не
дописывает `Applied` напрямую в журнал мимо live-проекции; уже загруженный inbox
догоняет canonical events перед восстановлением. Без сообщения Workspace оставляет
receipt неподтверждённым, Office явным resume возвращает ввод в очередь. Новый
event/store не добавлен, run и tools автоматически не повторяются.

Host-neutral Harness: общая проверка Office/Workspace receipt и существующие
Office FIFO, Workspace resume/steer recovery — pass. S1c остаётся открытым:
общий FIFO/run worker, полное run recovery и UI-controller ещё раздельны.
Office controller delivery и Windows/Office/WebView2 не квалифицированы; с
Windows-машины доступны только фотографии экрана.

## Live run и Stop используют общего Application owner — 2026-10-27

Office `ChatRunRegistry` удалён. `ConversationRunOwner` владеет live handle,
точной резервацией запуска и токеном отмены для Office и Workspace. Workspace
передаёт owner резервацию до подготовки run; его named pipe остаётся локальным
transport и направляет exact Stop к этому owner. Kernel и журнал по-прежнему
единолично определяют lifecycle/outcome. Вызов Stop не создаёт terminal success.

Host-neutral Harness build и точечные проверки Office lease/снимка,
Workspace Stop, busy admission, abandoned recovery и bridge Stop — pass.
Office controller partials Harness не собирает. Отдельные Office FIFO worker,
Workspace `RunOperation`, recovery и UI-подключение остаются открытыми S1c;
Windows/Office/WebView2 evidence доступно только по фотографиям экрана.

## Резервация разговора принадлежит Application — 2026-10-27

Office history/admission/recovery и Workspace run/history используют общий
`ConversationLeaseService.Reserve` с прежними путями файловых блокировок.
Короткая глобальная координация Office перенесена из `ChatRunRegistry` в
Application; registry больше не хранит отдельные reservations и не создаёт
для них `running` snapshot. Workspace по-прежнему использует только точную
блокировку разговора. Последующий перенос live snapshot/cancel описан выше;
S1c не закрыт.

Host-neutral Harness build и точечные проверки конкурирующих владельцев,
Office maintenance, Workspace reservation, project-chat admission/recovery — pass.
Office controller partials Harness не собирает; Windows/Office/WebView2 evidence
остаётся открытым. От пользователя доступны только фотографии экрана.

## Навигация чатов отделяет live owner от сохранённого running — 2026-10-27

Owner: shared Web conversation status projection. Деревья Office и Workspace
используют одно правило индикатора: работа подтверждается live inbox/owner,
сохранённый kernel checkpoint без ответа владельца показывается как неизвестное
состояние, а загрузка истории не изображает запуск агента. В Workspace текст
статуса также различает подтверждённую работу и недоступный owner. Kernel
по-прежнему единственный источник lifecycle; этот срез не закрывает S1c и не
заменяет общий Application owner. Локальный Chrome: shared navigation, дерево
Office, Workspace/Plugin races — pass; JS-синтаксис — pass. Windows/WebView2
остаются открытым evidence.

## Обычный ввод Workspace переведён на общий inbox — 2026-10-27

Owner: Application conversation intake. Создание run в Workspace теперь записывает
ввод через тот же `ConversationInboxService`, что Office; после сохранения user
message он переводит receipt в Applied через общий read-back guard. Активное
уточнение использует тот же экземпляр inbox. Ключ live-проекции учитывает
host/workspace/document и chat ID: одинаковые chat/operation ID в разных документах
не смешивают ввод. Прямой Workspace append для обычного ввода удалён.

Host-neutral Harness build, ранний bridge receipt, активное уточнение и проверка
разных scope при одинаковом chat ID — pass. S1c остаётся открытым: FIFO scheduler,
recovery и общий UI-контроллер ещё раздельны; Windows/Office/WebView2 не проверены.

## Общая граница владения запуском и уточнением — 2026-10-27

Owner: Application conversation orchestration. Office и Workspace переведены на
один `ConversationLeaseService` с прежними путями файловых блокировок; общий
`ConversationWorkService` отслеживает и завершает фоновые inbox/run задачи.
Явное продолжение Workspace также проходит через этого владельца: отмена
вызывающего и shutdown связаны с одной задачей, ожидание lease прерываемо.
Служебная резервация Office больше не создаёт `running` snapshot, сохраняя
исключение второго writer. Оба kernel input ports используют общий
`ConversationRunInputChannel`: запись user message остаётся доменной, а перевод
receipt в Applied допускается только после сохранения этой записи.

Это реализованная часть S1c, не завершённое объединение чата. Приём нового ввода,
FIFO scheduler, управление active run/approval, recovery и UI-подключение ещё
разделены; старый registry Office остаётся для них. Временные владельцы и
условия удаления перечислены в [MIGRATION_MAP](MIGRATION_MAP.md#общий-владелец-разговора--2026-10-27).
Host-neutral Harness build и точечные chat lease, workspace steer/recovery,
plugin admission/shutdown/approval и остановка explicit continuation — pass.
Реальные Office controller partials Harness не
собирает; Windows/Office/WebView2 остаются открытым evidence. Пользователь может
предоставить только фотографии экрана.

## Общий чат: пересмотр границы объединения — 2026-10-27

Текущий приоритет — [S1c: общий владелец разговора](BACKLOG.md#s1c-общий-владелец-разговора--2026-10-27),
owner: Application conversation orchestration + shared Web controller. По
[аудиту исходников](../audits/office-workspace-preparation-audit.md#прикладное-управление-разговором--2026-10-27)
общий kernel/DOM пока скрывает раздельные admission, workers, leases, recovery и
UI receipt/composer. Цель — общий чат целиком; Office и Workspace предоставляют
document binding, resources/tools и domain preparation, а не свои chat workflows.
Kernel сохраняет единоличное владение lifecycle/outcomes; журнал и проекция общие.

Ближайший срез переключает оба потребителя для send/queue/steer, запуска/approval,
Stop и reconnect/recovery с удалением прежних владельцев. Поведение проверяется
одной матрицей с двумя адаптерами. History/fork и оставшееся presentation идут
следующим срезом через тот же owner. [Карта временных путей](MIGRATION_MAP.md#общий-владелец-разговора--2026-10-27)
задаёт условия удаления; тотальной переписи и нового durable store нет.

Текущие незавершённые изменения Workspace steer/inbox сохранены, но не являются
реализованным объединением и не закрывают S1c. Настоящий срез — аудит и коррекция
плана; общая Application orchestration ещё не реализована. Независимая гонка
чтения ChatStore исправлена отдельно (`f2dbaa79`, ниже). Windows/Office/WebView2
остаются открытым evidence; от пользователя доступны только фотографии.

## Загрузка чата синхронизирована с записью — 2026-10-27

Owner: Core ChatStore. Воспроизведена гонка при уточнении активного workspace run:
`MaterializeSqliteProjection` обнаруживал изменение JSONL, а `LoadSession` возвращал
null; bridge сообщал «чат не найден». Полное чтение теперь использует существующую
блокировку документа, как страницы истории и append. Чужие документы и ожидание
модели её не удерживают; второй store и retry loop не добавлены.

Targeted host-neutral проверки: чтение ждёт занятую границу записи, другой документ
остаётся доступен, SQLite message page — pass. Сценарий workspace steer проходит
через реальный bridge; сам S1c ещё в работе. Windows/Office/WebView2 и общая
производительность загрузки остаются открытыми.

## Нулевая глубина исключает reflection из запроса — 2026-10-27

Owner: Core ModelProtocol / AgentKernel; Office и Workspace request preparation.
`MaxConsecutiveReflections=0` теперь удаляет reflection из strict schema, встроенных
инструкций и примеров обоих transport modes, включая repair и явный fallback.
Inspector и budget используют ту же проекцию. Значение берётся из frozen run limits;
для изменения нужен новый запуск. При запрещённом action, даже без остальных полей,
runtime возвращает `ReflectionDisabled` без запроса на корректировку. История ранее
принятых решений и поведение положительного лимита сохраняются.

Targeted checks: 9 v8 recovery, 17 model protocol, Inspector и schema/fallback,
179 codec cases / 165 independent JSON Schema cases, CLI HTTP response-mode smoke
с глубиной 0 и 7 UI projection checks — pass. Windows/Office/WebView2 и соблюдение
схемы реальным провайдером остаются открытым evidence; `json_object` сам по себе
не ограничивает набор действий модели.

## Workspace восстанавливает receipt после потери ответа/reload — 2026-10-27

Owner: Application input delivery / shared workspace draft and composer.
`getProjectChat` по сохранённому request ID возвращает receipt из существующего
журнала конкретного чата. Open/navigation/polling подтверждают уже доставленное
сообщение без повторного submit или запуска модели. Найденный receipt завершает
зависшее ожидание submit и снимает его timer, в том числе после раннего progress.
Protected draft сохраняет связь с отправленной версией; новый одинаковый текст,
base draft редактируемого сообщения и composer Office не стираются. Ack и очистка
сохраняются одним снимком. Неизвестный/чужой receipt и DeliveryRecorded=false
ничего не подтверждают. Отсутствие связи версии сохраняет текст.

Targeted host-neutral Harness: read-only lookup после reopen, isolation и bounded
ID, admission/applied, чтение при занятом run lease, protected draft roundtrip — pass.
Chromium с native envelopes: reload, новый одинаковый draft, неизвестное/чужое
подтверждение, background Office, edit draft, полностью потерянный reply и потеря
reply после progress — pass. Existing receipt/shared draft/reload checks — pass.
Первый параллельный browser-прогон дал startup timeout; отдельная startup-проба
и последовательные прогоны прошли. Причина не установлена; ускорение загрузки
этим срезом не заявляется.
Windows DPAPI/Office/WebView2 и abrupt-close evidence остаются открытыми.

S1b реализован host-neutral. Следующий срез — S1c/I26-10: уточнение и очередь для
активного workspace run через существующие input journal/kernel input port.
Это изменение пока не разрешает отправлять второй ввод в активный run.

## Approval workspace больше не ждёт окончания модели — 2026-10-27

Owner: Application Workspace run lifetime / typed project-chat delivery.
`resolveProjectChat` использует тот же tracked worker, что submit. Ответ возвращается
после первого сохранённого kernel checkpoint, потребляющего exact pending approval.
Занятый session lease даёт busy без ожидания; повторное подтверждение, в том числе
после restart, не повторяет effect. Bridge больше не владеет продолжением; terminal
API CLI по-прежнему ждёт результат и учитывает явную отмену вызывающей стороны.
Отказ сохраняет kernel cancellation без dispatch; Stop/shutdown используют общий owner.

Targeted Harness: реальный file write + held HTTP, ранний native bridge response,
duplicate, recreate bridge, cancel до/после admission, Stop, shutdown, offline denial
и terminal API — pass. Existing submit receipt/lifetime checks — pass. Chromium с
native envelopes: pending approval исчезает, нет pending request во время model wait,
polling завершает run и сохраняет draft. Build: 0 errors, 2 existing CA1416 warnings.
Windows/Office/WebView2 и реальное завершение pane остаются неквалифицированными.

Следующий срез S1b — automatic receipt reconnect после потерянного подтверждения
доставки ввода/reload. I26-10 (steer/queue) и ранее зафиксированный `uncleanClaims`
остаются открытыми; этот commit их не закрывает.

## Submit workspace больше не ждёт окончания модели — 2026-10-27

Owner: Application Workspace run lifetime / typed project-chat delivery.
`submitProjectChat` возвращает durable receipt и первый kernel snapshot; дальнейшее
выполнение принадлежит отслеживаемому Application worker с session/runtime leases.
CLI ждёт terminal result того же worker. До admission отмена прерывает и ожидание
lease; после admission потеря bridge не отменяет run. Stop остаётся exact run/cursor.
Закрытие Application отменяет и дожидается workers; pane drain включает этот owner.
Таймауты не увеличены, второй writer и отдельный durable store не добавлены.

Проверки host-neutral: удержанный HTTP-ответ, ранний bridge response, duplicate,
пересоздание bridge, отмена до/после admission, explicit Stop, shutdown и освобождение
lease; existing history/source scope/edit — pass. Chromium с native envelopes
проверяет отсутствие pending submit во время model wait и завершение через polling.
Shared draft/navigation races с ранним admission в preview — pass. Отдельный
`uncleanClaims` в progress suite воспроизводится и на прежнем preview; открыт в RISK_REGISTER.
Harness build: 0 errors, 2 existing CA1416 warnings. Windows/Office/WebView2 и
фактический pane shutdown на целевом host не квалифицированы.

I26-09 исправлен для submit. Следующий срез S1b: `resolveProjectChat` после approval
ещё ждёт terminal result; затем automatic receipt reconnect. I26-10 (steer/queue)
остаётся открытым. Ввод во время активного run этим изменением не разрешён.

## Усилен finish в Conversation v8 — 2026-10-27

Owner: Core ModelProtocol / AgentKernel, Application Workspace и shared RunViewState.
Для finish обязательны непустой ответ и `outcome` модели: `completed`, `partial`,
`unable_to_complete`. Противоречие с `results` возвращается на format repair;
schema, writer, typed kernel proposals и replay соблюдают один контракт.
Outcome сохраняется отдельно от lifecycle, effect health и task verification;
CLI возвращает exit 5 для честного неполного результата даже без acceptance criteria.
Общая инструкция определяет finish как утверждение с ответом, кратким обоснованием
и ограничениями; вопросы идут через `common.questions_ask`. Prompt schema — 57;
старые finish без outcome не угадываются и не переписываются.

Reflection при нулевом остатке явно требует execute/finish; первое превышение
получает коррекцию, повторное — `reflection_limit`. Это проверено вместе с format
repair и сохранением принятого outcome после reopen. Проверки host-neutral:
179 codec cases, независимая JSON Schema, targeted Harness, оба response modes
CLI (коррекции без повторной записи; partial/unable → exit 5), shared Web state.
Windows/Office/WebView2 и поведение реальной модели не квалифицированы.
I26-03 остаётся открыт для общего доказательства цели: `completed` + `satisfied`
со ссылкой на успешный read не доказывает запрошенный write. S1b I26-09/10 также
остаётся отдельным открытым срезом; изменение finish его не закрывает.

## Исправлено восстановление workspace run без владельца — 2026-10-27

Owner: Application Workspace run control / ProjectChat activation. I26-02 исправлен:
открытие и polling чата пытаются получить session lease без ожидания, перечитывают
exact run/cursor и применяют существующее восстановление `interrupted`. Занятый или
недоступный lease сохраняет run; IPC timeout не считается доказательством остановки.
Pending approval сохраняется; неизвестный эффект записи не replay и не success.
Snapshot и сообщения возвращаются из одной восстановленной session; ввод и fork
разблокируются по её canonical state, без нового UI lifecycle.

Проверки: новый Application/bridge regression красный до исправления, зелёный после;
held lease без IPC, repeated recovery, pending approval, unknown write, сохранённая
история и fork. Existing busy-lease/input-recovery checks — pass. Chromium с native
bridge envelopes подтвердил polling → interrupted, сохранение draft, Send и fork.
Host-neutral Harness build: 0 errors, 2 existing CA1416 warnings в PDF viewer.
Windows/Office/WebView2 evidence остаётся открытым; VSTO validation не выполнялось.
Следующий срез S1b — независимый lifetime длинного submit (I26-09), затем steer/queue
(I26-10). Чат с живым владельцем и долгим запросом этим исправлением не разблокируется.

## Исправлена доставка workspace input receipt — 2026-10-27

Owner: Application input delivery / typed WebView bridge / shared workspace composer.
I26-01 закрыт: подтверждение сохранённого user message очищает только отправленную
версию draft до ответа модели. Terminal response и duplicate submit возвращают тот
же receipt; `DeliveryRecorded=false` или чужая identity не означают успешную доставку.
Новый draft и composer Office сохраняются при фоновом подтверждении workspace.

Проверки: targeted host-neutral Harness (реальный Application/bridge до ответа модели,
existing recovery доставки), Chromium с экспортированными bridge envelopes,
shared draft/progress и bridge bootstrap — pass. Build Harness: 0 errors, 2 CA1416
warnings в существующем PDF viewer. Office/VSTO/Windows validation не выполнялось.
Остаются I26-02/09/10: abandoned-run recovery, lifetime длинного submit и steer/queue.
Следующий дефект — сохранённый running без доступного владельца.

## Исправлен workspace tool catalog — 2026-10-26

Owner: Application Workspace prompt / Core schema projection. I26-04 закрыт:
`json_object` больше не объявляет закрытый пустой object, запрещающий аргументы
его `anyOf`-ветвей. Consumer использует существующий `ForPrompt`; runtime validation
и protocol не ослаблены. Служебные подписи в ленту не добавлялись.

Проверка: targeted CLI build — 0 warnings/errors; `response_mode_smoke.py` — pass,
включая independent validation фактического HTTP catalog и search/read dispatch.
Новая проверка `{target}` была красной до исправления. В тесте удалён устаревший
дубль finish helper; transport IDs и `E` evidence проверяются раздельно.
Реальный endpoint и Windows/Office/VSTO не проверялись. Следующий дефект — потеря
input receipt в bridge; UI-реконструкция не считается исправлением выполнения.

## Исходный аудит: восстановление рабочих сценариев чата — 2026-10-26

Owner: Application conversation delivery совместно с Core ModelProtocol/Storage
и Web ChatView. По десяти пользовательским фото выполнен
[incident review I26-01…10](RISK_REGISTER.md#разбор-десяти-фотографий-чат-протокол-и-достоверность--2026-10-26)
на исходниках `bde1ae8b`. Windows evidence от пользователя — только фотографии;
логи, выгрузки и файлы не запрашивать. В production Web JS воспроизведены неочищенный принятый
ввод, блокировка по running при недоступном владельце, отсутствие токенов в общей
сводке, показ/исчезновение непринятого текста и 30 перестроений дерева на 30 пустых
progress events. В имеющейся CLI-сборке через scripted HTTP оба response modes
принимают «файл создан» после одного read, без файла и write; advertised workspace
json_object schemas противоречат стандартному JSON Schema. Это не установленная
поломка file read-back: finish admission и цель пользователя — разные проверки.

[Поэтапная очередь](BACKLOG.md#восстановление-рабочих-сценариев-чата--2026-10-26):
сначала достоверность результата и receipt/recovery, затем schemas/read→patch,
общие состояния UI и usage, bounded loading/rendering, реальные endpoint/Office
сценарии. Runtime в рамках этого аудита не менялся. Product build, полный Harness,
live API и Windows/Office/VSTO проверки не запускались.

Точная ошибка json_schema на пользовательском сервере, hash установленной DLL/web,
потеря accepted messages, отдельная причина отказа fork и измеренное распределение
задержек остаются открытыми. В малой HTTP-пробе дублирования message bodies/source
не найдено; длинный реальный контекст этим не квалифицирован. Предыдущие завершённые
срезы ниже — локальное evidence, а не подтверждение работоспособности всей системы.

## V8 architecture stabilization — 2026-10-26

Owner: Core Agent/Storage/ModelProtocol and application/test composition.
Removed the duplicate half-budget skill gate; tool loading now requires complete
request admission. Normal and scalar commits share own-batch fault recovery;
partial/foreign tails cannot advance a stale writer. Unknown inspection now needs
fresh complete evidence of every affected target and never clears replay guards.
Fixed catalogue-only CLI env composition and stale v8 fixtures; production wire,
reference allocation, shared UI and saved-data layout remain unchanged.

Targeted Harness, 12 controlled CLI scripts, MockDemo workflow and Web checks are
recorded in the [audit/evidence](../audits/v8-architecture-drift-2026-10-26.md).
Exact HTTP bytes match retained CAS, including repair; saved Inspector is checked
through a new store. No full-suite, live-model, Windows/Office/VSTO qualification.
D07 relocation, records migration, incremental projection/performance and host
qualification remain open. No new runtime subsystem or migration adapter.


## Image cutoff and attachment budget corrected — 2026-10-26

Owner: Core LlmMessageBuilder. One request-local selection now supplies image/PDF
parts, actual audio bytes, budget reservation and wire/diagnostic projections.
The separate PDF estimate that counted ordinary images again is removed. Analyzed,
excluded and native-message attachments consume no image slots or media reserve.
Model-visible ATTACHMENT_DELIVERY notices name selected image order/PDF pages and
omissions without runtime IDs; quota, explicit unsupported vision and unavailable
preparation have distinct reasons. Exhausted quota no longer throws a false scanned-
PDF capability error. Attachment-text framing and generated notices are budgeted.
Original messages, storage, media limits, resizing and v8 are unchanged.

9 focused Harness checks pass: two new selection/budget regressions, both saved
attachment-manifest checks, PDF preparation/cache, helper isolation, multimodal
primary, saved request composition and agent media retention across format repair.
An extra fallback test fails on an unchanged stale ReferenceEquals assertion;
[corrected in the V8 audit](../audits/v8-architecture-drift-2026-10-26.md).
Build has only the 2 existing PDF platform warnings. No Office/VSTO or real endpoint
qualification; real media rendering/delivery and model A/B remain open.

## One ChatView history and message actions — 2026-10-26

Owner: shared Web chat presentation; workspace history operations in Application.
Office, project and plugin move the same transcript/composer DOM and use common
saved-message grouping, rendering and footer actions. Workspace fork, edit and
delete-message now use typed bridge operations; edit checks the exact revision and
preserves the previous composer draft. The protected pending-input draft retains
edit identity for uncertain-send retries. Reflection and live output retain their
existing DOM during progress updates. The right panel header stays above its body.
The add, plan, model and reasoning controls are the same DOM elements for each
owner; duplicate workspace controls were removed. File and model actions route
through the current owner's existing authority.

Focused workspace history/storage Harness checks and Chrome chat/HTML suites pass;
[Office](../audits/evidence/unified-chat-2026-10-26/chat-office.png),
[project](../audits/evidence/unified-chat-2026-10-26/chat-workspace.png),
[plugin](../audits/evidence/unified-chat-2026-10-26/chat-plugin.png) and
[HTML](../audits/evidence/unified-chat-2026-10-26/chat-html.png) screenshots use
synthetic preview data. [Workspace edit](../audits/evidence/unified-chat-2026-10-26/chat-workspace-edit.png)
and [live output](../audits/evidence/unified-chat-2026-10-26/chat-workspace-stream.png)
show the same surface; [Office live output](../audits/evidence/unified-chat-2026-10-26/chat-office-stream.png)
uses the same stream leaf. Windows Office/WebView2 streaming remains unqualified.

## PDF/media and composite tool result captions — 2026-10-26

Owner: Office ToolResultPresentationService / shared Web UI; Core result summaries.
PDF cards and actual-request captions show one-based pages and exact character
windows. Selection completeness is separate from source completeness and UI clipping;
PDF extraction does not claim visual/OCR coverage. Media receipts distinguish saved
preparation from actual HTTP delivery, without inventing a binary-body R citation.
Workspace summaries retain the resource-owned hydration flag. Composite results keep
table, list and text independently. Missing comparison notices preserve verified
change/no-change/unknown; raw business counts never become changed-object totals.

6 focused Harness checks pass, including actual two-page PDF extraction into the
presentation service; 3 Chrome suites pass. [Narrow PDF/media cards](../audits/evidence/tool-pdf-media-preview.png)
were inspected with synthetic UI data. Build has only the 2 existing PDF platform
warnings. No Office/VSTO validation. Domain-specific counts/diffs, search provenance,
actual media delivery and Windows/WebView2 qualification remain open in the
[matrix](../audits/tool-result-presentation-matrix.md). Model wire stays v8; no new
storage, runtime authority or product version.

## Tool attempts and workspace result presentation — 2026-10-26

Owner: Core tool display/summary projection; Office presentation service; shared Web UI.
Collapsed tool rows now retain the exact semantic target and typed outcome, including
running attempts and distinct skill/reference reads. Core owns the existing catalog,
target selection and summaries; the former Office-only implementations are removed.
Workspace activities now carry those fields and preserve failed/pending states.
Workspace-scoped result delivery uses the same canonical/CAS body for content blocks
and bounded raw JSON. The old activity-body JSON is not duplicated. Expanded receipts
show saved E/action/target. Source comparison availability remains independent of effect.
Actual/planned diff totals are separate; hunks/copies without full comparison get no
fabricated zero totals. Missing workspace/domain diffs remain explicitly unavailable.

9 focused Harness checks and 3 Chrome suites pass; the build has only the 2 existing
PDF platform warnings. [Attempt rows](../audits/evidence/tool-attempt-targets.png) and
[comparison counts](../audits/evidence/mutation-comparison-counts.png) use synthetic
fixtures. See the [matrix](../audits/tool-result-presentation-matrix.md). Windows/Office/
WebView2 delivery, media/composite PDF captions, search provenance and full per-domain
counts/diffs remain open. Format 8 and product version are unchanged.

## Tool result matrix and source captions — 2026-10-26

Owner: Office ToolResultPresentationService / shared Web UI; Core owns reference
resolution. The [result matrix](../audits/tool-result-presentation-matrix.md) maps
91 static tool IDs across 21 families through runtime, events/CAS, model and UI;
dynamic registrations inherit the shared contract. Document-chat result cards show saved E
and bounded target/range/excerpt captions, with R only from the exact canonical
read's scoped revision binding. Expanding a caption reveals the retained body.
Actual-request diagnostics use the same format with matching saved body-delivery
evidence and the actual request text. Search/find captions do not invent R. Typed
tree previews are included. No new store, wire format or product version.

14 focused Harness checks pass: 4 tool presentation, 7 model references, literal
source projection, selected table rows and persisted questions. Two Chrome suites
pass; [chat](../audits/evidence/source-caption-chat.png),
[narrow chat](../audits/evidence/source-caption-chat-narrow.png) and
[diagnostics](../audits/evidence/source-caption-diagnostics.png) were inspected using
synthetic fixtures. Build has only the existing 2 PDF platform warnings. Per-item
search provenance, media captions, arbitrary-prose citations, live-model A/B and
Windows/Office/WebView2 qualification remain open; this is not execution of all
91 tools on real hosts. Follow-up mutation audit found that workspace activities
still skip typed result previews, and diff totals combine actual and intended line
counts. Both are recorded in the matrix and [follow-up matrix](../audits/tool-result-presentation-matrix.md#дополнительный-аудит-мутаций-и-прочих-исходов--2026-10-26);
the shared renderer does not establish delivery parity. Five focused mutation/
presentation checks on the existing Harness build, seven run-view Node checks and
two Chrome layout suites pass; their current expectations do not close these gaps.

## Resource labels bound to delivered source blocks — 2026-10-26

Owner: Core ModelProtocol. The final model projection now places `ref` inside a
delivered resource-read block or hydrated current-source header, beside its semantic
target, view and coverage. Loose `resource_refs` arrays are removed. Exact typed
scope/revision bindings decide the label; target text, list order and ambiguous
evidence cannot supply it. Multiple slices share the revision label and keep their
own coverage. Navigation results receive no inferred per-item labels, and operation
receipts retain `E` without claiming source-body delivery. Canonical results, CAS,
events, Inspector resource navigation, chat UI and format 8 remain unchanged.

All 7 focused `model refs:` checks and the 2 continuity checks for the actual next
request and CAS working-set hydration pass host-neutral. Harness builds with the
existing 2 PDF platform warnings. Docs now show the model-facing read shape. Live
model usability and Windows/Office/WebView2 qualification remain open.

## One reference owner and uppercase types — 2026-10-26

Owner: Core ChatStore / ModelProtocol and read-only Inspector. [ADR-0018 revision 2](../decisions/ADR-0018-typed-model-references.md)
uses `E` for accepted execution, `R` for resources, `O` for observations and `Q` for
requests. The rejected lowercase/uppercase workaround is removed. Message and saved
request observations use one allocator and the same event-backed reference index;
Inspector only reads bindings and groups by scope + URI + revision. Unbound old
metadata remains visible without invented links. Chat format stays 8 per the user's
predeployment decision; intermediate 9/10 and earlier reference shapes have no compatibility
path. Prompt 56 / reference revision 2 apply within format 8. Formats 6/7 stay read-only;
product version is unchanged.

Focused reference, Inspector, trace persistence and Web UI checks pass, including
mixed message/request versions, restart, stale append and unbound historical rows.
Final format-8 reference/Inspector checks, Web UI checks, Chrome layout and
ValidateVersionFormat pass. Diagnostic search now includes a short E/R/O/Q legend;
[legend](../audits/evidence/references-v8-legend.png) and
[resource](../audits/evidence/references-v8-resource.png) screenshots use synthetic
fixtures in Chrome, not Office. Chat narration/action summaries and diagnostic
navigation exist; inline clickable E/R citations in the chat feed remain a UI gap.
Earlier broader checks and live 5/8 task results belong to the previous candidate;
the [archived comparison](../audits/tool-profile-comparison.md#typed-short-reference-repeat--2026-10-26)
is not qualification of revision 2. Broad defaults remain. Windows/Office/WebView2
and updated live-model quality evidence stay open.

## Saved mixed-request attribution verified against wire — 2026-10-26

Owner: Core LLM request trace and composition. Before publishing a saved request
manifest, source text/role/call correlation and selected attachment text/media
parts are checked against the serialized request. Changed portions lose only
their delivery proof; unchanged parts remain attributable. Projected whole
messages keep their estimated cost in `Unattributed` unless actual selected
attachments identify the mixed-message group. The mixed saved-request and
model-context composition fixtures pass host-neutral. This does not allocate one
message's cost among multiple resources or prove provider receipt. The remaining
Wave 1–2 coverage and Windows/Office/WebView2 gates stay open; later Wiki work
should use the canonical conversation contract and this status.

## Office next-request preview uses active skill and wire preparation — 2026-10-26

Owner: Office PromptContextInspectorService / ModelContextPreview. The preview now
resolves selected skills and their tool dependencies through SkillTaskService, uses
ModelRequestPreparation for the strict message projection, budget and estimate,
and includes tool-pack restoration state. Raw preview and section totals describe
the projected request; selected instructions have their own section. Six focused
context-inspector Harness checks pass, including selected-skill/strict coverage.
The [risk entry](RISK_REGISTER.md#office-next-request-preview-diverges-from-active-preparation--2026-10-26)
records remaining active-request parity and Windows/Office evidence. This is the
source status for later Wiki work; the Wiki itself is a separate stage.

## Resource Context Explorer export handoff — 2026-10-26

Owner: Web Model Context Inspector and existing Trajectory diagnostics. Inspector
now opens the exact chat's existing export options with whole-chat raw events,
cleared filters, metadata redaction and CAS bodies off. The user starts the export
there; no new ZIP owner, store or bridge operation was added. Focused Model Context
and Trajectory Node checks pass for Office/workspace routing, foreign-scope refusal,
filter reset and redaction. Browser layout could not run here because Playwright is
not installed; Windows/WebView2 delivery remains unqualified. Wave 5 still lacks
derived graph, revision diff and explicit reread.

## Exact PDF page-view extent — 2026-10-26

Owner: Office resource Gateway. PDF render and thumbnail selectors now reject pages
outside the exact attachment's known extent before rendering or retained lookup.
Fresh and retained page metadata must report that same page count and selected index;
a mismatched retained record fails without source hydration or recapture. The focused
host-neutral binary Harness check passes, including out-of-bounds and mismatched
retained metadata cases. Windows/Office/WebView2 rendering evidence remains open.

## Directory discovery default repaired — 2026-10-26

Owner: Application workspace schema / Core search descriptions. Remove injected
recursive=false from find: omitted/null selectors again browse directories; explicit
recursive/glob selectors retain file-only semantics. Descriptions scope absence to
the requested depth/filters. Regression fails on baseline and passes after the fix;
all six text-search checks plus workspace admission/restart pass. Repeated cloud
profile pairs now pass 4/4, with exact file acceptance and protocol completion.
[Baseline and repeat evidence](../audits/tool-profile-comparison.md) remain separate.
Broad production defaults remain; local model finish/operation-selection gaps and
Windows qualification are still open. No version bump.

## Paired tool-profile pilot — 2026-10-26

Owner: Core/Application tool composition; Harness diagnostics. The [live pilot](../audits/tool-profile-comparison.md)
compares broad 18-tool and compact 9-tool surfaces on two identical synthetic tasks
with Gemma cloud/local: eight runs, plus four passing scripted diagnostic checks.
Compact reduces initial request size and cloud copy uses exact discovery/admission;
both profiles complete only 1/4 runs. Local edit bytes are correct but finish evidence
fails; local compact copy uses create despite an explicit copy requirement. Broad
production defaults remain. The pilot also reproduces a schema-default routing bug
that hides directories from ordinary workspace find; fix it separately, then repeat
the paired tasks. No Office qualification or product version change.

## Normal CLI build restored — 2026-10-26

Owner: Core.Net8 project composition. Exclude the unused Office UI
ProtectedChatDraftStore together with its already-excluded DPAPI dependency.
The normal CLI build now passes with zero warnings/errors and CLI --help succeeds;
no temporary targets/output overrides, Harness rebuild or Office validation were used.
Framework inclusion and stored drafts are unchanged. The
[Net8 build gap](RISK_REGISTER.md#cli-core-net8-draft-store-build-blocker--2026-10-26)
is closed. Next: compare compact explicit tool profiles against the current broad
workspace profile before changing defaults. Product version unchanged.

## Shared discovery/admission reaches workspace — 2026-10-26

Owners: Core capability catalog / CallableToolSet / ToolPackAdmissionJournal;
Application workspace composition; Office source/profile adapters. Pure discovery,
descriptors, policies, exact membership and same-stream admission replay moved from
Office into Core. Both consumers use them; old implementations were removed. Office
retains its profile factory and ResourceGateway source acquisition. Workspace now
offers capabilities_search/read in Agent/Plan while retaining its broad default
surface. Skill source reads route to existing resources_read. Exact schema receipt
compaction and runtime metadata removal are shared; no second registry or journal.

21 focused Harness checks pass: end-to-end workspace discovery → next-request
admission → execution → reopen → binding drift, skill-only refusal, 11 pack/budget
checks, existing Office discovery/skill references/collisions, strict VBA admission,
Library descriptions, workspace strict/object history, pinned Office target and Plan
policy. Office profile IDs and existing registration contracts remain unchanged.
No real-model quality or Windows/Office qualification is claimed.

The normal CLI build exposes a pre-existing Core.Net8 DPAPI draft-store dependency
failure. A temporary build-only exclusion of that unused CLI type verifies separate
Core → Runtime → CLI assemblies, but does not close the
[normal-build gap](RISK_REGISTER.md#cli-core-net8-draft-store-build-blocker--2026-10-26).
Next: repair that independent build inclusion, then compare smaller explicit profiles
on a shared task/model corpus before changing defaults. Product version unchanged.

## Direct admission budget fixtures corrected — 2026-10-26

Owner: Harness / Office model-context verification. The two prior failures were
obsolete overflow expectations after schema deduplication. Original cases now prove
that fitting schemas remain admitted (15219 / 18464) and carried (9012 / 10464).
Valid oversized cases prove atomic durable refusal, request refusal when retained
evidence itself cannot fit, non-resurrection after restart, and explicit carryover
removal without rebind resurrection. All 11 `tool pack:` checks pass. No production
code, budget formula, reserves, descriptor limits or version changed.
[Measurements and closed gap](RISK_REGISTER.md#direct-admission-overflow-fixtures--2026-10-26).
Next: shared workspace discovery/admission before reducing its initial tool surface.
Provider/Windows qualification remains separate; no live-model run was needed.

## Shared callable profile selection — 2026-10-26

Owner: Core CallableToolProfile; Office CallableToolPack and Application workspace.
Both consumers now select direct inputs through one profile implementation. Office
retains its explicit core, profile IDs and durable admissions; workspace explicitly
uses all published non-Skill entries. Skill resolution, policy/binding checks and
full request budgeting keep their existing owners. No catalog narrowing, new
registry, data migration or version bump. [Profile contract](../tool-library.md#shared-direct-tool-profile-selection--2026-10-26).

Host-neutral verification: 11 of 13 focused checks pass, including profile/skill
fingerprint parity, deduplication, removal, host cores, exact/durable admission,
dependency revocation, strict VBA budget, strict workspace history and target pinning.
Two overflow/carryover fixtures also fail with the baseline `524b3f2f` consumers;
the [validation gap](RISK_REGISTER.md#direct-admission-overflow-fixtures--2026-10-26)
was subsequently resolved by the measured budget-boundary cases above. Workspace
discovery/admission remains the next step before catalog reduction.
No live-model or Windows/Office qualification was performed.

## Shared model request preparation — 2026-10-26

Owner: Core ModelProtocol; Office ConversationModelSession and Application workspace.
`ModelRequestPreparation` now binds callable tools, schema options, host strict
projection, message budget/reserves and final request assembly. Both consumers use
it; replaced per-host budget formulas and Office request-options builder are removed.
Snapshot/tool-ID mismatch refuses request creation; exact runtime binding guards remain.
Host authority capture, prompts/domain projections, trace metadata, compaction persistence
and file/COM owners remain separate. Inspector preview calls the canonical schema builder.
Nine focused checks pass: shared preparation/budget/fallback, Office strict VBA 32K,
oversized selection refusal, strict transport repair, workspace history and strict
HTTP history, pinned Office workspace target, request-local fallback and Inspector.
No live-model rerun or Windows/Office qualification was needed for this extraction;
no prompt/schema/reserve change or quality improvement is claimed.

## Snapshot arsenal fixes strict request-budget rejection — 2026-10-26

Owner: Office ConversationModelSession. The prior VBA 32K diagnosis was incomplete:
final/candidate admission used the direct pack instead of the compiled snapshot's
selected tools. Strict projection then retained inline schemas, inflating admission.
Budget checks and dispatch now share snapshot tool selection. Skill instructions,
guards and reserves are unchanged. The new strict 32K regression fails before the
fix and passes after; the genuine oversized-selection refusal still passes. The real
local Gemma 32K run now edits exact source without compaction, but full acceptance
fails at 24 iterations because finish repeatedly cites prose instead of tool IDs.

[Component measurements and evidence](../audits/skills-tools-model-comparison.md#snapshot-budget-correction--2026-10-26).
Earlier pilot observations below remain historical; they do not establish that the
VBA package needs shortening or that local Gemma cannot perform the edit.

## Skill guidance real-model pilot — 2026-10-26

Owner: Harness diagnostics / Core skills / Office catalogs. Recovered the prior
local Gemma 32K and Gemma cloud profiles. Fixed a live-harness-only double envelope:
scripted FixtureCompletion must not wrap already formatted provider responses.
The same fix was applied to both comparison variants. Cloud A/B each passed the
explicit-load VBA task with four model calls and one verified write. Local json_schema
A/B both hit the existing selected-skill request-budget limit after compaction.
This is not spontaneous-selection coverage or proof of improved model quality.
[Report and retained evidence](../audits/skills-tools-model-comparison.md); context
pressure remains open in the risk register. No production protocol change or Office
qualification is claimed.

## Alpha preparation — 2026-10-26

User-requested product version: `16.1.0-alpha.1`; assembly binding stays `16.0.4.0`.
Source preparation only: no release tag, distribution or qualification claim.
Windows/Office scenarios and signed exact-build evidence remain open under the
[release process](../operations/RELEASE_PROCESS.md).

## Shared text exploration — 2026-10-25

Owners: Core search contracts/matcher, Application workspace, Office Gateway and presentation.
The existing common.resources_search now supports scoped literal/regex exploration for workspace,
VBA and HTML, with closed schemas, exact source pins, semantic continuation and explicit coverage.
Workspace discovery adds recursive/glob selectors. Search cannot authorize patches; v8 admission,
mutation owners, events/CAS and the request Inspector remain unchanged. Existing indexed literal
search keeps long-line/part navigation. Five VBA display entries now use current IDs and module
arguments; typed search previews expose coordinates and incompleteness.
Focused host-neutral search, indexed-source, patch/presentation and saved-request checks plus
Chromium activity/result presentation pass. The mixed-language tool fixture records bounded
output and revalidation amplification; it is not an autonomous-model comparison. Native
Windows/Office/WebView2, large real-project latency and real-model A/B remain open in the
[risk register](RISK_REGISTER.md). [Audit](../audits/tools-architecture-current-state.md) and
[implementation/evaluation plan](../proposals/tools-architecture-plan.md) retain scope and limits.


## Visual annotation source-edit procedure — 2026-10-25

Owner: Core shared authoring guidance, Office HTML and workspace plugin skills.
An accepted annotation now points the agent to the existing owner-specific source
reads and patch tools. The shared instruction treats DOM fields as untrusted hints,
requires one explainable source match and a current unique patch anchor, and refuses
ambiguous or generated targets without a provable source. The plugin prompt directs
source edits to its existing authoring skill. A focused host-neutral fixture reads
an accepted plugin selection, checks its retained release against one static HTML
source, patches it through the existing revision guard, reads back the file and
retains a checked candidate; ambiguous anchors and stale source reads refuse the
write. Office and workspace built-in packages publish the
same Core procedure; no second resolver, writer or store was added.
This is a guided static-source path, not an automatic DOM-to-source resolver or a
verified browser edit. Real model behavior, dynamic DOM mapping, exact candidate
preview checks and Windows/Office/WebView2 delivery remain open.

## Resource Context PDF render-page coverage — 2026-10-25

Owner: Office resource Gateway and Model Context Inspector. Newly retained PDF
render/thumbnail metadata now records the selected page range. Reuse refuses a
stored page index or count that contradicts the request, without recapture.
Inspector shows unknown coverage for older internal page metadata that claimed
`whole`. Focused binary and Web checks pass host-neutral; actual PDF rendering on
this machine is unavailable, and Windows/Office/WebView2 qualification remains open.

## Inline chat visualization — 2026-10-25

Owner: Web chat presentation / Core skill catalog. Completed Mermaid fences now
render inline; HTML/JS runs on Play in an offline sandbox with local ECharts.
Tables, source controls and enlargement share the existing transcript. Common
chat_visualization is instruction-only and shared by Office/workspace. Focused
browser, Markdown/stream, vendor and skill-catalog checks pass host-neutral.
Native WebView2 frame identity/navigation enforcement (including plugin-entry
exceptions) and Office delivery remain unqualified; arbitrary preview JS has no
CPU-isolation guarantee. No new persistence or model wire format.

## Conversation v8 admission and diagnostics — 2026-10-25

Owner: Core ModelProtocol/LLM, Office and workspace context admission, and
Application workspace resource schema. Strict requests now budget their projected
wire prompt through compilation and dispatch; an oversized full prompt cannot
reach `json_object` fallback. Invalid provider schema errors remain typed HTTP
failures rather than format-unsupported fallback. Workspace file procedure,
tree and PDF selectors enter disjoint runtime and v8 strict schema alternatives;
no-call history rejects stray transport metadata. Focused protocol, resource and
saved-exchange Harness checks pass host-neutral. The pre-v8 CLI selector smoke
remains a coverage gap in [RISK_REGISTER](RISK_REGISTER.md); real endpoint and
Windows/Office/WebView2 qualification remain open.

## Saved request composition estimate — 2026-10-25

Owner: Core LLM transport diagnostics / Web Model Context Inspector. New request
events retain each final message's existing baseline token estimate and format
overhead. Disjoint groups use exact compiler source/ordinal links; unknown sources
keep their cost, selected attachments label the whole containing message, and
omitted media adds no share. The compact chart links to section/message details.
Version, complete ordinals and sums are checked before rendering; old snapshots
without estimates stay explicit. API usage and calibrated admission remain separate.
Focused host-neutral/browser checks cover conservation, media, persistence before
dispatch, metadata exclusion from wire, navigation and both themes at narrow width.
Fine-grained skill/tool/mixed-message attribution, upper-meter integration,
compaction/diff and Windows/WebView2 qualification remain open.

## Saved request source presentation — 2026-10-25

Owner: Web Model Context Inspector. Ordered messages now expose saved resource
observations, independent completeness fields and attachment parts before technical
JSON. Recorded message/part omissions are grouped at the top and loaded into the
DOM in bounded pages; local originals remain explicit reads. Duplicate message
positions do not choose an arbitrary source. Source/resource navigation preserves
disclosures without another payload read. Focused JS/Chromium checks cover unknown
attribution, omissions, scope and narrow layouts in both themes. This reuses existing
trace metadata; transformed-source attribution, token shares, request diff/compaction
and native WebView2 qualification remain open.

## Chat history tree — 2026-10-25

Owner: Web trajectory/run journal. Saved history now groups exact run → step →
action → source events. The initial view shows the outcome and step overview;
model exchange and bookkeeping stay collapsed. Problems/unknown effects remain
visible on parent branches. Missing/conflicting correlations stay explicitly unbound,
without sequence-based inference. Parent collapse releases readers/cancels body
loads; back/refresh retain explicit collapsed state. Descriptions wrap and expanded
readers use the pane scroll. Focused JS/browser checks cover grouping, scope,
cancellation, navigation and both themes at wide/narrow widths. Existing exact
workspace leases, lossless JSON and settings isolation remain; native
Windows/WebView2 is still unqualified.
The follow-up uses the same disclosure component as the approved demo (verified
against computed styles), removes search/duplicate counters and coloured edge
cards, and exposes Result/Arguments/Events directly. Exact model-request composition
opens in the existing reader and returns to the history. Scoped lossless subtree
reads, source guards and cancellation remain; all alternate-view screenshots are
browser fixtures, not Office evidence.

## Workspace usage and saved journal — 2026-10-25

Owner: workspace snapshot / chat diagnostics services and Web Inspector. Project
and plugin usage now shows the last accepted response's API counts, its same-run
context window and the separately labelled prepared-context estimate. Missing
usage never reuses an older response; parallel calls count their one completion.
Run/action links open the existing journal with exact workspace/session scope.
Full-chat filters, raw event bodies and ZIP exports retain that scope and close
their original leases. The shared diagnostics scope replaces Model Context's
private workspace loader; no store or alternate reader is added. Focused
host-neutral and browser checks pass. Next-request workspace estimates, complete
source attribution, compaction/diff and graph remain open; native Windows/Office/
WebView2 delivery and lease liveness are not qualified.

## Shared chat integration checkpoint — 2026-10-25

Owner: Web ChatView and project chat routing. Office, project and plugin use one
transcript/composer; plugin source chats use the project controller. This UI
integration is implemented; workspace usage/journal is completed in the slice above.
General resource navigation/browser history and Windows/Office/WebView2
qualification remain open.
Focused browser checks for layout, routing, drafts, session races, Inspector and
plugins pass; focused host-neutral chat history and source-scope checks pass.
Three session-isolation races were fixed: a pending reply blocked another chat,
a late create response changed the selected chat, and a pending cancel disabled
another chat's Stop or showed it an unrelated error. The Office host chat fixture
was updated for the shared icon renderer; its checks pass. No full-suite or
target-host qualification is claimed.
Late chat and history deletion responses also no longer reopen the plugin after
the user selects another project.
An in-flight project file upload now locks text send only in its source chat;
the single file-upload slot stays explicit while another chat remains writable.
Late project-connection responses retain a newer user selection while still
refreshing the tree; a connection without intervening navigation opens its project.

## Right preview header — 2026-10-25

Owner: Web shared preview surface, plugin and HTML chat browser presentation.
The right pane now separates its section switcher from the resource toolbar.
The panel toggle stays on the central chat header on wide screens; the same
button remains reachable inside a narrow overlay. Plugin version actions share
one labeled menu, while Apply/Restore appear only with an actionable candidate.
Selection after reopening plugin management now awaits one source check and
restarts source watching before the frame can be annotated. Focused Chromium
layout, plugin and HTML browser checks pass; Windows/WebView2 remains open.

## Workspace saved request/response in the shared Inspector — 2026-10-25

Owner: model-context diagnostics service and Web chat Inspector. Project and
plugin steps now open their saved model exchange in the existing reader; a run
action opens its request history. Typed queries and responses bind workspace and
session; scoped download leases retain verified CAS bytes. Missing or foreign
scope is rejected, without Office selection or a second diagnostic store.
Saved history remains readable while the connected source folder is unavailable.
Changing owner/session closes pending reads; resizing and return preserve the
selected view and step focus. Workspace next-request preview remains unavailable.
The generic navigation grid no longer overrides workspace chat columns; request
content retains the Inspector width after the summary rows are hidden.

Focused host-neutral scope/exchange and browser checks cover the new path and the
existing Office reader. Workspace usage and event journal are covered in the slice above.
Windows/Office/WebView2 controller delivery and lease liveness remain open evidence.

## Plugin development in the project shell — 2026-10-24

Owner: Web plugin navigation and project chat shell. A plugin icon now opens its
source workspace in the ordinary Projects tree. The shared ChatView stays in the
center; the exact plugin iframe, annotations, version actions and Inspector use
one right dock. Project chat selection and creation use the same tree; the
duplicate plugin history controls, chat handlers and standalone development layout
are removed. Project and plugin sessions use the same project chat controller.
Switching source chats preserves the iframe; switching projects hides it. Source
registration precedes tree navigation, and deletion of the selected source chat
reopens a valid chat or explicit empty state. Focused Chromium plugin, shell,
draft, routing and Inspector checks pass. Windows/WebView2 delivery remains open.

## Workspace capabilities in the shared Inspector — 2026-10-24

Owner: Web chat Inspector presentation. Project and plugin chats now expose
**Возможности агента** from the selected session's existing `contextReceipt.Arsenal`.
Office and workspace reuse one compact renderer for skills and grouped tools.
Confirmation requirements and skill availability stay visible; expanded skills
keep their unavailability reason alongside their description.

The view identifies a previous run and distinguishes absent/incomplete metadata
from a saved empty composition. It does not query the Office chat, execute tools
or add storage. Before the first compiled request no saved composition is claimed.
Focused Inspector browser coverage checks counts, disclosures, previous-run
attribution, missing/incomplete receipts, session isolation and narrow layouts.
Workspace request/response, usage and journal remain the next contract-parity
slice. Windows/Office/WebView2 qualification remains open.

## Shared context and activity Inspector — 2026-10-24

Owner: Web chat Inspector presentation. One dock and section switcher now follow
the Office, project and plugin chat. Workspace context reads the selected session
snapshot; the common activity renderer filters by RunId and opens exact steps
from the transcript. The separate plugin activity renderer is removed. Routes
check owner/workspace/session identity, including a shared session moving between
project and plugin; Office request inspection cannot open in a workspace chat.
Switching from an Office request closes its reader and releases Focus isolation.

Plugin preview keeps its frame and document when switching sections. Drafts stay
with the existing chat owner; narrow diagnostics uses the full pane with a return
button. `workspace-inspector.test.js` covers these boundaries and light/dark/narrow
layout; the existing Office request layout and plugin browser suites also pass.
Workspace request/response, usage, capabilities and journal presentation are the
next bounded contract-parity slice; their Office data is never substituted.
Windows/Office/WebView2 qualification remains open.

## Plugin iframe visual annotations — 2026-10-24

Owner: native plugin frame transport / shared visual Inspector and composer /
workspace chat attachment intake. The bound frame receives the shared Inspector;
the numbered editor stages a bounded DOM selection against its immutable release,
including checked candidate previews. The protected workspace draft preserves
unfinished annotations and staged attachments. Attachment-only input appears in
the shared transcript; sent annotations project from CAS and show page markers
only on the matching release. Focused host-neutral and Chromium plugin UI checks
pass. Real Windows WebView2 injection and Office delivery remain open evidence.

## Request Inspector reading and hidden-panel layout — 2026-10-24

Owner: Web model-context reader / chat Inspector presentation. Ordered input
messages now use compact divided rows, shared typography and a clear reading
area; raw metadata stays behind disclosures. The response opens immediately.
The reader handles v8 `response` wrappers and empty `message` fields by showing
the recorded action, reason, decision and model assessments without claiming
runtime success. Exact source JSON and exports remain unchanged. Returning from
another content tab restores expanded input messages and their reading position.

Closed HTML browser overlay rules no longer override Inspector columns after a
hidden section, and hidden layouts do not clamp the chosen panel width to zero
space. Targeted model-context and browser layout/HTML handoff checks cover these
changes. Windows/Office/WebView2 qualification remains open.

## HTML Inspector hover guidance and toggle state — 2026-10-24

Owner: shared visual Inspector / Web button presentation. With annotation mode
active, pointer hover now outlines the exact element using the selected UI accent
and shows its safe tag/ID and dimensions beside the outline. Leaving the preview
clears hover; scrolling resolves the element currently under a stationary pointer.
The shared pressed-button style makes the active mode visible in the right browser
and Materials. Ordinary page clicks still work with the mode off. Focused Chromium
Inspector and annotation checks pass; target-host WebView2 delivery remains open.

## Right panel sections and sent HTML annotation markers — 2026-10-24

Owner: shared Web chat shell / Office message projection / plugin preview UI.
The right HTML pane now exposes Preview, Context, Agent abilities, Context usage
and Activity from one header switcher; the existing chat button only opens or
collapses the pane. The plugin pane uses the same header and section pattern for
its available Preview and Activity views. Activity is derived from the selected
source chat; switching views keeps the exact live plugin frame. Reload remains
beside the preview. Sent Office HTML annotations are projected from their
committed CAS body into the immutable message and shown as read-only numbered
markers on the matching HTML revision. The page watches at most 100 loaded
annotations at once; an older message can pin its annotation for inspection.
The later plugin iframe annotation slice is recorded above. Windows/WebView2
delivery remains unqualified.

## HTML annotation editor and shared composer list — 2026-10-24

Owner: Web visual annotation UI / Core selection body / Office chat draft projection.
Unfinished selection and comment edits now live in the same protected Office
chat draft as the composer input. Browser hide, mode exit and WebView recreation
retain the text and exact source; an unfinished annotation blocks send until
Done or Delete. Completing it transfers the record to the existing staged CAS
attachment in one draft update. The existing chat side-panel button opens and
closes the browser or chat diagnostics; the browser has no separate hide arrow
or duplicate preview button. Sections are selected in the right header.
The unused Materials attach control stays out of the browser bar. Number badges have
fixed square geometry in the page, editor and composer list.

The right HTML browser now opens a small host-owned comment editor by a selected
element. Done stages the selection and comment in the existing CAS attachment,
adds one entry to the shared Office composer, and leaves a clickable number on
the page. The marker follows page scroll; Show resolves the same unique DOM
selector, and the list marks missing or offscreen targets. Its editor and the
composer list can edit or remove the same draft attachment. Editing stages a replacement before
discarding the old draft. The inspector waits for an activation acknowledgement,
so a click made immediately after toggling does not race its capture listener.
Draft numbering is protected with the existing chat draft; native hydration
projects completed annotation details from the CAS body.

Focused Core/CAS, protected draft recovery and browser editor, marker, scroll,
edit/delete checks pass. Sent annotations reopen read-only from the immutable
message on the matching page. Plugin capture, source edit
read-back and Windows/Office/WebView2 qualification remain open.

## Authored HTML beside the shared Office chat — 2026-10-24

Owner: Web shell route / existing authored HTML preview owner. The current
revision of an Office chat's HTML workspace opens on the right from Materials or
its chat card. The exact existing iframe, Inspector controls and resource renderer
move between Materials and the shared chat; no second frame or chat is created.
The browser and chat diagnostics use the same right slot. Hide/show keeps the live
frame and chat draft; explicit close, Materials navigation or changed chat/revision
returns the frame and revokes the old preview. The initial split is 58/42 with
shared keyboard/drag resizing, a wider mode and a narrow overlay. The browser
header now shows a section switcher, title/path, reload, an Inspector mode button
styled as "Аннотировать", size and a menu with Materials and close actions.
The Materials view retains
"В чат" for attaching a selection without a comment.

Focused browser routing/geometry, Inspector, HTML reuse, shared-chat and product
mock checks pass. Historical HTML/resource routes, back/forward, restored open
state, plugin-frame capture and real Windows/Office/WebView2 qualification remain
open. The later annotation editor slice is described above.

## Preview surfaces: shared location, loading and reload — 2026-10-24

Owner: Web preview presentation / existing plugin and authored HTML frame owners.
One small preview-surface control presents the exact resource label and loading or
error state for both frames. Both preview headers align with the chat/project header;
the plugin's existing refresh action now lives inside its right preview. HTML has
an explicit reload through its existing snapshot
renderer, which revokes inspector generation and resource leases once. Neither
path creates another iframe, bridge or resource store. Focused plugin layout and
lifecycle, HTML preview reuse and browser integration checks pass. The later
authored-HTML route is described above. General resource routing, back/forward,
restoration and Windows/WebView2 lifecycle qualification remain open.
The later numbered annotation editor, page markers and composer list are described
above; their UX contract remains in `docs/ui-design.md`.

## Unified Shell: central plugin chat and right preview — 2026-10-24

Owner: Web shell layout / plugin navigation / shared ChatView. Selecting a plugin
temporarily collapses its tree without overwriting the saved navigation preference.
The same ChatView stays in the center; the existing plugin iframe occupies the
right preview, and its toggle hides only that preview. Reopening it preserves the
live iframe, chat draft and run observer. The shared splitter keeps a separate
chat/browser width preference. Focused Chrome chat-layout and plugin lifecycle
checks pass. The reusable right SurfaceHost for HTML/resources, browser navigation
and restoration, plugin-frame Inspector capture, and Windows/WebView2 qualification
remain open.

## Visual Inspector: workspace selection intake and model read — 2026-10-24

Owner: Core chat-attachment binding / Application workspace resource provider.
Workspace input now accepts only same-session visual-selection drafts whose exact
plugin release belongs to that source workspace. The input event and user message
retain one attachment identity; the body uses the existing chat CAS. Shared Core
linking replaces the Office-only copy. Workspace `common.resources_find/read`
discovers the accepted semantic target and reads its verified immutable body,
including bounded text windows. Ambiguous targets and foreign selectors are
refused. Focused host-neutral intake, duplicate submit, model prompt and resource
read checks pass. Native plugin-frame capture, shared-composer staging, source
mapping/edit read-back and Windows/WebView2 qualification remain open.

## Visual Inspector: one DOM evidence contract in Core — 2026-10-24

Owner: Core selection DTO/admission, Office chat ingestion. The bounded DOM
selection schema and its validation/CAS-body encoding now live once in Core;
Office keeps only its typed source-binding bridge payload. This removes the
Office assembly dependency from the future plugin/workspace capture path without
changing the current HTML selection wire format or Office chat behavior. Focused
host-neutral selection/CAS and bridge-contract check passes. The workspace read
path was added in the later slice above; plugin-frame capture, source mapping/edit
verification and Windows WebView2 qualification remain open.

## Visual Inspector: HTML selection in the shared Office composer — 2026-10-24

Owner: Web HTML preview / Office resource ingestion / shared ChatView. **В чат**
stages the selected bounded element as an ordinary JSON attachment; the same
Office draft, remove/send/retry path owns it. The user's comment is the ordinary
composer text, and several selections can share one message. Native staging derives
the exact active HTML artifact reference from the addressed chat and verifies its
revision; sending refuses a draft whose source is no longer active. The accepted
input and user message retain the source reference, while the selection body uses
the existing attachment CAS and exposes no runtime URI to the model. The DOM is
untrusted evidence and source mapping is unverified; this does not implement an
edit workflow or plugin/workspace annotation ingestion.

Focused host-neutral staging/CAS/stale-source, shared-draft routing and Chrome
inspector/layout checks pass. Office controller/WebView2 delivery, plugin frame capture, scope-aware
workspace ingestion, source resolution/edit read-back and Windows qualification
remain open.

## Visual Inspector: HTML preview inspect-only slice — 2026-10-24

Owner: Web HTML preview / shared visual-inspector JS. The existing isolated HTML
preview now offers **Выбрать элемент**: hover outlines a DOM element, click shows
a bounded read-only selection, Escape or the button exits. Frame messages are
accepted only from the current iframe/generation; replacing its source revokes
the old inspector. The overlay does not intercept ordinary clicks while off,
and selection excludes form values and sensitive descendants. Standalone HTML
exports do not include the inspector. The later Office draft slice above adds
chat attachment and persistence; source editing remains open.

Focused Chrome inspector/viewport and host-neutral HTML preview/export checks
pass. Plugin-frame injection, scope-aware workspace ingestion, revision-pinned
source resolution and source-edit verification remain the next scoped work. Windows
WebView2/CSP/frame lifecycle qualification is open.

## Unified Shell: protected chat-input recovery — 2026-10-24

Owner: Core chat-input storage / Office typed bridge / shared Web ChatView. Office
and workspace unsent text, workspace mode/reasoning, and Office staged-resource IDs
now persist in a separate CurrentUser-DPAPI file, keyed by exact Office chat or
workspace/session. The bridge reloads and validates staged Office attachments;
missing attachments produce a visible warning rather than fabricated content.
Unconfirmed workspace request IDs and Office inbox operation IDs survive a WebView
recreation, and both send paths require a confirmed draft save before dispatch.
Late acknowledgements still clear only the submitted draft version. Native chat
deletion and runtime-data clearing remove corresponding drafts. This file is UI
input state, not another conversation history or resource-body store; no draft
content goes to browser localStorage.

Focused host-neutral storage and browser reload/shared-draft checks pass. Windows
DPAPI, Office/WebView2 lifecycle, staging retention after a long shutdown, and
abrupt-close timing remain unverified target-host evidence.

## Unified Shell: selected plugin frame reuse — 2026-10-24

Owner: Web plugin navigation / shared ChatView. Reselecting a plugin with a live
release or preview now keeps its exact iframe/instance and refreshes only the
selected source chat; an explicit chat target changes the session without
reopening the surface. A different plugin, source revision or explicit preview
refresh still follows the existing close/open path. Browser route, session-draft
and plugin lifecycle checks pass. Protected draft recovery is described above.
Windows/Office/WebView2 delivery remains unverified.

## Unified Shell: explicit plugin source chats — 2026-10-24

Owner: Application plugin source / shared Web ChatView. Opening development now
connects the source workspace and reads its latest session without creating one.
An empty history stays visible with a locked composer and an enabled `+`; a task
entered while creating a plugin explicitly creates its chat before submission.
The same project-chat contract, storage and renderer remain in use. Focused
host-neutral Harness and browser checks pass. Windows/Office/WebView2 delivery
remains unverified.

## Workspace cross-process live preview — 2026-10-24

Owner: Application workspace chat / run control. A read-only request on the
existing current-user run pipe now returns the owning process's bounded transient
model preview for the exact workspace/session/run. Project/plugin ChatView polling
can show live tokens from a CLI or other local process without a second event
format or durable store; the event stream remains authoritative for reconnect and
history. A missing owner returns no preview within 250 ms and explicitly clears a
stale browser preview while leaving the durable run status to recovery. A
separate-process CLI streaming/inspect smoke checks live text, repeated inert reads,
completed response and retirement of preview; the existing cross-process
cancellation smoke checks owner loss. The browser progress check covers stale
preview removal. The first nonempty text or reasoning delta publishes immediately;
subsequent deltas retain the existing coalescing limit.
Windows/Office/WebView2 delivery remains unverified.

## Workspace live progress delivery — 2026-10-24

Owner: Application workspace chat / Office bridge / shared Web ChatView. Project
and plugin submits and approval continuations now forward their existing runtime
model-preview events through the Office `progress` bridge envelope. The shared
ChatView routes only to the mounted owner and exact workspace/session/run; a
monotonic transient sequence prevents a delayed event or same-revision poll from
replacing newer preview. Read-only polling remains the reconnect path, and hidden
project/plugin observers stop on owner changes. The preview is never persisted as
chat history. Focused Harness HTTP/bridge and Chrome project/plugin progress and
navigation-race checks pass. Cross-process preview is covered above; Windows/Office/
WebView2 qualification remains open.

## Unified chat message views and history — 2026-10-24

Owner: Core browser projection / Application workspace chat / shared Web ChatView.
Project and plugin chats now use the Office `ChatMessageViewDto` and
`renderMessageArticle`; `PluginChatMessageDto` and its separate action/result
renderer are removed. Accepted v8 narration, reflection, reasoning, finish
claims and saved tool activity are shown without sending raw protocol bodies or
runtime guards to the browser. Office and workspace history use one 80-record
window contract; project and plugin can load earlier pages. Current snapshots
retain already loaded pages, and a stale page cannot replace a newer revision.
Office and workspace transcripts now use the same keyed DOM reconciliation and
live model output painter. Replacing a saved message preserves an open reasoning
disclosure and its keyboard focus; unchanged messages stay mounted.
Workspace runs now persist the same typed per-message `RunViewState` as Office.
A failed run with no accepted model response appends a visible terminal diagnostic;
older failures and unknown effects remain visible after another run or history paging.
The terminal diagnostic classification is shared with Office, preserving its
existing native-refusal status path.

Focused Harness projection/history and v8 recovery checks, Node reconciliation
checks and Chrome project/plugin layout, paging, progress and reconnect checks
pass after rebasing onto `25dd095b`. Full Office/WebView2 qualification and
cross-process live token delivery remain open.

## Conversation v8 recovery and reflection settings — 2026-10-24

Owners: Core ModelProtocol/AgentKernel, Office adapter, Application workspace and Web
presentation. The current change addresses the incident audit below: strict object
transport with nested action union; 0–20 frozen reflection depth with proactive
remaining allowance; separate format/provider/semantic budgets; bounded output-token
correction; accepted reflection/tool-step reasoning and handoff; workspace accepted metadata,
scoped in-process previews, stable message nodes and explicit read-only history.
Built-in prompt schema 53 separates JSON-object examples from strict schema semantics;
argument descriptions remain in the supplied schema. Independent shared-shell commits
supply typed terminal run notices and navigation/reconnect fixes.

Verification: host-neutral `v8 recovery:` 6/6 (including persisted reopen), kernel
57/57 and targeted protocol/context/settings/compatibility checks passed. The
standalone codec passed 153 response cases / 2156 assertions; the independent schema
validator passed 139 cases with its two documented local-parser distinctions.
Scripted CLI `conversation_v8_finish_smoke.py` and `response_mode_smoke.py` passed.
Web stream handoff, workspace message retention/live progress, shared run notices
and run-state ordering passed, including browser focus/disclosure checks. No full
suite, paid model calls or Office validation. Open: actual target-provider support, Windows/WebView2/Office evidence,
and cross-process live token delivery. The dated audit below
describes its earlier baseline, not the status of every current implementation.


## Conversation v8 / shared-chat incident audit — 2026-10-23

Priority: provider schema admission and visible accepted/terminal chat state.
Review of `7a627c44` reproduced Office reflection feedback and format recovery,
but also found an OpenAI-incompatible strict schema root, lost accepted reflection
display metadata/live handoff, silent workspace failures, discarded output
termination cause, and incomplete workspace streaming/history presentation.
Historical workspace envelopes also lack an explicit readable/read-only view.
The separate uncommitted ChatView anchor fix passes the Office-return probe;
the committed baseline throws. Findings, owners and bounded next steps are in
[RISK_REGISTER](RISK_REGISTER.md#conversation-v8--shared-chat-incident-audit--2026-10-23).
These issues remain open; prior focused protocol checks do not qualify the complete
chat experience. No full suite, real-provider or Windows/Office qualification ran.

## Unified Shell regression review — 2026-10-23

Owner: application/shared ChatView. Browser reproductions exposed five defects:
Office composer restoration referenced the relocated diagnostics node; retrying
an accepted input after a lost reply and project/plugin navigation generated a
new request ID; delayed running snapshots replaced terminal state; reselecting a
plugin during a pending reply retained its busy flag; reopening an already running
chat read history once without observing completion.

The composer now restores through its own DOM anchor, workspace input identity
belongs to the shared draft owner, snapshots retain monotonic session revision,
and plugin busy state follows its selection epoch. A common read-only run observer
replaces the per-action polling loops and follows an opened running session until
completion or selection change. It rejects superseded replies and does not replay
execution. Request IDs remain in memory; full panel reload is not a durable input
retry guarantee.

Focused browser evidence covers shared drafts/lost replies, state ordering and
running-chat reconnect, action routing, Office input delivery and shared layout.
Concurrent browser runs hit startup/scroll-fixture timeouts; isolated reruns
passed. Node workspace-v8 and run-view-state checks cover
presentation. No build, full Harness or Office/VSTO validation was run;
Windows/Office/WebView2 qualification remains open.

## Conversation v8 post-merge regression review — 2026-10-23

Owner: Core accepted-history reader / Workspace replay. Review of `a61b5207`
found two lost history checks: non-native records could carry multiple native
calls, and a later batch sibling could restart at index zero. Both now fail
before another input or model request is accepted. Existing regression cases
cover mixed transports and broken/duplicate batch positions.

The merge also duplicated Harness local variables, left a new Office-history
fixture on the removed call-builder signature/v7 reply, and omitted `window`
from a Web test context after the shared-draft change. These fixtures are fixed.
The host-neutral Harness builds; 38 focused protocol/history/Workspace/question/
context/plugin cases, eight Node cases and the shared-chat browser routing case
pass. No complete suite or Office/VSTO validation was run. Remaining v7 CLI
fixtures are listed in [the V8 audit](../audits/v8-architecture-drift-2026-10-26.md);
formal real-model and Windows/Office/WebView2 qualification remain open.

## Conversation v8 integration on main — 2026-10-23

The `conversation-v8` implementation and its protocol, kernel, workspace, Office-neutral,
CLI and Web tests are merged into `main` together with newer Unified Shell, resource
and question work. Current new chats use v8; old v7 streams remain read-only and
are not rewritten. Workspace replay retains exact accepted call/batch identity,
refuses inconsistent history before accepting another input, and presents final
model narration without exposing the durable envelope. Reflection and finish
claims keep their separate v8 semantics.

Per user request no additional test suite was run for this merge. The last
complete candidate Harness run was 986 passed / 18 failed; several failures
subsequently passed focused checks, but the full suite has not been repeated.
Formal real-model phase 5 and Windows x64 + Office x64 + WebView2 gates remain
open. The earlier candidate checkpoints below are historical.

## Unified Shell model-context no-call narration — 2026-10-23

Owner: workspace application context preparation. A completed v7 response now
enters the next model request as its validated assistant `message` text, matching
Office dialogue, while the original JSON envelope stays in durable history.
The workspace restart/batch Harness case checks the actual next HTTP request and
unchanged saved response. Other context-preparation differences remain open;
Windows/Office/WebView2 are not qualified.

## Unified Shell model-context accepted-call admission — 2026-10-23

Owner: Core `ConversationResponseHistoryReader` and workspace context preparation.
Workspace model facts now use the same accepted-response reader as Office instead
of a second v7 call parser. The reader distinguishes a stored workspace JSON
envelope with runtime call metadata from native tool transport, preserves only
the accepted narration, and refuses missing or mismatched call identity and
arguments. Focused v7, workspace restart/batch and context-continuity Harness
checks pass host-neutral. Full input and context-preparation parity remains open;
Windows/Office/WebView2 are not qualified.

## Existing chat Inspector: composition, capabilities and step details — 2026-10-23

Owner: application/chat UI. The user selected the existing four-view right panel
as the diagnostics baseline; the separate prototype was rejected and removed.
Menu entries now keep one icon. Usage adds an estimated next-request composition
bar and expandable source groups, separately from measured last-request API usage.
Capabilities use compact skill rows and existing library tool groups. Activity
retains independent runtime steps and links each call to its exact journal
correlation, and each step to saved requests filtered by run/step. Missing requests
never fall back to another step. Focused browser/model-context/usage checks cover
the change; no Office/VSTO build was run. Windows/Office/WebView2 remain unqualified.
Exact attribution of all last-request tokens and later diagnostics
features remain open in the [UI design](../ui-design.md).

Follow-up: Office message/activity actions now open that run or exact step in the
same panel. A selected older run remains pinned during updates; missing loaded
steps stay explicit and link to the exact journal scope. Final answers without
StepId do not borrow the last step. Close/Esc returns to the source control and
chat changes reject stale navigation. The focused browser check covers these
routes. Workspace/plugin dock integration remains open.

Request reading now stays in that same dock with Docked/Wide/Focus layouts,
without remounting or refetching the selected payload on resize. It uses shared
controls, visible content tabs, readable ordered message previews and the shipped
document viewers; history/search and message metadata expand on demand. Focus
isolates background input, Escape restores size then the source step. Targeted
browser/model-context checks cover state retention, keyboard navigation, themes
and narrow layout. Next: the shared Office/project/plugin dock and exact request
composition. Windows/Office/WebView2 evidence remains open.

Follow-up regression checks cover delayed payloads versus tab changes, mode
switches during history loading, failed selections, refresh of unopened bodies
and stale export errors. The UI rejects superseded results, keeps old content
out of a new failed selection, and preserves chosen panel widths across narrow
windows. Closing also restores source focus while a responsive resize is pending.
Compact message rows and bounded history controls were checked in light,
dark, short-window and large-text layouts.

## Unified Shell application input identity on replay — 2026-10-23

Owner: Core `ConversationInputJournal`. Office inbox recovery and workspace/CLI
receipt lookup now reject operation-ID collisions and changed immutable input
identity in the same append-only event stream. The workspace-local duplicate
identity scan is removed. Nine inbox Harness cases and the CLI application-request
smoke pass host-neutral, including a forged collision and crash/retry receipts.
Full accepted-input and context-preparation parity remain open; Windows/Office/
WebView2 are not qualified.

## Unified Shell application/resource parity: Office read descriptor — 2026-10-23

Owner: Core resource-read schema. Direct Office and workspace/CLI `type=office`
now use the same closed selector alternatives; CLI adds only its route
discriminator. This admits Office section reads in CLI and rejects incomplete
line windows before dispatch. The replaced Office schema builder and duplicate
CLI Office variants are removed. Focused PDF, VBA, JSON/YAML, gateway and
workspace Office Harness checks pass host-neutral. Accepted-input and context
preparation parity remain open; Windows/Office/WebView2 are not qualified.

## Questions: shared form, explicit skip and readable answers — 2026-10-23

Owner: application/chat UI and Core question admission. Office, Project and Plugin
now share one rounded question card with the demo's surface/radius/control tokens,
one question at a time, a counter and back/forward arrows. Single-choice clicks
advance automatically; the final answer closes the card only after confirmed
intake. Multiple choices/free text use Next/Done. Descriptions, explicit
per-question/all skips and inline errors remain available. Transient drafts and
the active page survive redraw/navigation; unchanged retries
retain their request identity. Core validates explicit skips and emits readable
question/answer text without a JSON envelope or runtime IDs. Chrome checks cover
owner routing, pagination, final acceptance/stale snapshots, retry, Office inbox
intake, draft retention, themes and narrow
layout. Four relevant `plan mode:` Harness cases pass host-neutral; an unrelated
stale prompt-text assertion is recorded in BACKLOG. Windows/Office/WebView2 remain
unqualified.

## Unified Shell application/resource parity: PDF selectors — 2026-10-23

Owner: Core model-facing resource selector contract. Office and workspace/CLI
PDF page reads now use one admission rule for page, range and optional character
window fields. Both reject extra selectors before provider read; the previous
workspace page path could accept `path` while Office rejected it. The existing
two-page Office/workspace Harness cases cover the shared refusal and successful
reads host-neutral. Descriptor and broader context-preparation parity remain
open; Windows/Office/WebView2 are not qualified.

## Resource Context exact read-message cost — 2026-10-23

Owner: Core LLM request trace and Model Context Inspector. After final request
serialization, one unchanged text-serialized `common.resources_read` result with a unique
exact evidence/view/coverage link now records a model-calibrated estimate for
its entire wire tool message. Inspector shows it on the linked observation.
Ambiguous links, changed content and unproved presentations stay unknown;
the estimate neither proves view-body delivery nor allocates global request
overhead or measured provider usage. Focused Harness/Web checks pass
host-neutral. Windows/Office/WebView2 and remaining attribution cases are open.

## Unified Shell ChatView controller (1b host-neutral) — 2026-10-23

Owner: application/chat UI. Office, Project and Plugin now move one physical
composer form and input with their shared transcript. Owner-scoped handlers keep
Office edit, attachments and queue/steer, project file upload and plugin source
chat; switching preserves separate draft identities, and background Office
rendering cannot overwrite workspace input. `app-workspace-chat.js` now owns the
single mount, composer, primary form actions and shared workspace
approval/question controls. Office, Project and Plugin register owner-specific
send/cancel/resolve behavior; the controller routes input, Enter, Send, Stop and
approval by the mounted owner. Focused Chrome layout, draft, delivery,
attachment, approval/question routing and product-preview checks pass. The 1b
controller cutover is complete host-neutral; Windows/Office/WebView2 are not
qualified. Host-recreation draft recovery remains a later shell gate.

## Resource Context retained text-body estimates — 2026-10-23

Owner: Model Context Inspector. Exact retained `text/source` variants with one
explicit UTF-8 text payload and no parts now show the existing bytes/4 token
baseline in known-name metadata and paginated inventory. Indexed, binary,
multipart and unclassified views remain unknown. This estimates only the
registered body, not the projected tool message, complete prompt or measured
usage; CAS verification, currentness and model delivery remain separate.
Focused Harness/Web checks pass host-neutral. Windows/Office/WebView2 remain
unqualified; full per-view cost, live availability and transformation provenance
remain open in BACKLOG.

## Resource Context exact retained view variant checks — 2026-10-23

Owner: Model Context Inspector and existing revision/CAS stores. An explicit
check selects a registered view/coverage variant by its ordinal in a pinned
inventory for an exact saved request. The runtime resolves it in the authorized
scope/revision and verifies its payload and all retained parts; the UI receives
per-body status and check time without CAS addresses. Missing bytes cannot be
reported as verified, and the revision payload is not implicitly checked.
Focused Harness/Web checks pass host-neutral. Windows/Office/WebView2 remain
unqualified; view-specific cost, live availability and transformation provenance
remain open in BACKLOG.

## Unified Shell single transcript (1b continuation) — 2026-10-23

Owner: application/chat UI. This earlier slice mounted one physical
message list. Returning to Office rebuilds its history and restores the reading
anchor; its renderer keeps paging and live output. Background Office updates
cannot replace a mounted workspace transcript. At this checkpoint Project/Plugin
had a shared composer, while Office still had a separate composer and controls.
Focused Chrome chat layout, plugin,
product preview and Office transcript/delivery checks passed. Composer/controller
consolidation was still open at this checkpoint; Windows/Office/WebView2
qualification remains open.

## Unified Shell Office draft acknowledgement (1b continuation) — 2026-10-23

Owner: application/chat UI. Office and workspace drafts now use the same in-memory
draft owner and version sequence, with separate Office chat and workspace session
identities. Office input and attachment changes advance the draft version;
accepted sends clear only the submitted version and remove only submitted
attachments. Late acknowledgements preserve later text even when it is identical,
including after switching chats. Focused inbox, attachment, chat-navigation and
Chrome delivery checks passed. At this checkpoint the physical Office ChatView
was still separate; Windows/Office/WebView2 remain unqualified.

## Resource Context paginated retained view inventory — 2026-10-23

Owner: existing revision authority store and Model Context Inspector. For a
resource in an exact saved request, Inspector now pages through every registered
view/coverage variant in its authorized scope and revision. The first page pins
an append-order count, so later registrations cannot enter an in-progress list;
a fresh request sees them. The UI keeps one page visible at a time. Pages expose
metadata only and do not verify CAS, probe a live provider or prove model
delivery. Focused Harness/Web checks pass
host-neutral, including restart and foreign-scope isolation. Windows/Office/WebView2
remain unqualified. Exact CAS verification of an arbitrary inventory variant is
still open in BACKLOG.

## Resource Context retained view checks in Inspector — 2026-10-23

Owner: Model Context Inspector and existing revision/CAS stores. For an exact
resource in a saved request, Inspector now finds registered view metadata by
observed or read-time-advertised names within the authorized scope. Metadata-only
queries leave bytes unchecked; separate explicit checks verify the revision
payload or one selected view payload and all its retained parts. A missing
part cannot be masked by a healthy revision payload. The list is not an exhaustive
inventory of coverage variants and does not check a live provider or prove model
delivery. Focused Harness/Web checks pass host-neutral; Windows/Office/WebView2
remain unqualified.

## Unified Shell shared workspace view (1b project/plugin slice) — 2026-10-23

Owner: application/chat UI. Project and Plugin now mount the same transcript,
composer, status, approval and question DOM. The plugin-specific form and message
list were removed; both surfaces keep their own navigation and workspace actions.
Background updates cannot write into the other surface's mounted chat, and source
history remains available during a run. The shared status, approval and question
controls now use workspace IDs; the temporary plugin ID adapter is removed.
Office retains its richer actions. At this checkpoint 1b controller
consolidation was still open; host-recreation draft recovery and later shell
work remain open.

Chrome checks pass for shared draft/mount, workspace layout and history navigation,
plugin preview, product mock and Office input delivery. The screenshots were
visually reviewed. This is browser evidence only; Windows/Office/WebView2 remain
unqualified.

## Resource Context read-time descriptor in Inspector — 2026-10-23

Owner: shared resource-read evidence and Model Context Inspector. Accepted Office
and workspace/CLI reads now save a copy of provider-reported kind, MIME, physical
byte length, representations and view capabilities with their existing read
metadata. The Inspector shows these as claims made at read time, including
application-only views; old traces retain unknown fields. This does not verify
current availability, model readability or token cost. Focused Harness and Web
checks pass host-neutral; Windows/Office/WebView2 remain unqualified.

## Resource harness VBA recovery — 2026-10-23

Owner: VBA resource provider and Resource Gateway fixture. The Outlook/HTML
collection check from the old backlog passes on current `main`; no schema change
was needed. The VBA fixture now proves that a partial read after external drift
cannot authorize rename, then observes the complete current module before retry.
The stale semantic target error includes `common.resources_find(scope=vba)` for
recovery. Both focused host-neutral Harness cases pass. Windows/Office behavior
remains unqualified; the separate Markdown section failures remain in BACKLOG.

## Unified Shell shared workspace draft (1a) — 2026-10-23

Owner: application/chat UI. The user accepted [ADR-0017](../decisions/ADR-0017-unified-shell-and-visual-annotations.md).
Project and Plugin composers now share one in-memory draft owner keyed by
`workspaceId/sessionId` in the existing chat state. Both per-composer draft Maps
were removed. Text, plan/reasoning and appended project-file references follow
the same session across screens. Accepted sends clear only the submitted draft
version, so later input survives even when its text matches the sent text.

The focused `workspace-shared-draft.test.js` passes in Chrome with the actual
mock UI: cross-screen state, file link, failed retry ID, double submit and late
acknowledgement. `chat-input-delivery.test.js` passes for the Office inbox.
The former `workspace-chat-layout.test.js` history-control mismatch is resolved in
the project/plugin view slice above. Draft recovery after host recreation and the
Office/common ChatView cutover remain later gates. Windows/Office/WebView2 are
unqualified.
The original user plans are preserved in [docs/proposals](../proposals/RNAssistant_Unified_Shell_Plan.md).

## Unified Shell / Visual Annotator Phase 0 — 2026-10-23

Owner: application/chat UI. [Audit](../architecture/visual-annotator-audit.md)
maps three UI controllers, two preparation paths and one shared kernel/store;
CLI already uses the workspace application service. Browser evidence reproduces
separate drafts for the same project/plugin session and unconditional plugin
preview replacement. Double-send and cancellation on Settings navigation were
not reproduced. The bounded 12-cycle mock run has stable listener/node counts;
native performance and Windows/Office/WebView2 behavior remain unqualified.

[RFC / ADR-0017](../decisions/ADR-0017-unified-shell-and-visual-annotations.md)
proposes shared drafts first, then one ChatView, application/resource parity,
shell, browser lifecycle and annotations. The supplied plan requires RFC
acceptance before implementation; no production code or storage format changed.
The existing chat-input-delivery browser check passes; workspace history policy
and HTML preview fixture failures remain open in [BACKLOG](BACKLOG.md).

## Resource Context streamed diagnostic body download — 2026-10-23

Owner: Model Context Inspector and shared resource download data plane. Saved
model-event/original bodies now open a hash-verified CAS stream and travel through
sequential 256-KiB leases without the former 50-MiB or `Int32` source ceiling.
The file-backed Web download checks each chunk before writing, aborts a failed
save and closes the lease; older WebViews retain a whole-Blob fallback. The
Inspector's 50-MiB in-memory viewer bound remains a display bound, not a source
or download bound. Focused 51-MiB CAS, lease lifecycle, router and Web checks
passed host-neutral. Real Windows/WebView2 file-picker behavior remains unqualified.

## Resource Context exact PDF page text — 2026-10-23

Owner: Core PDF extractor and workspace file owner, Office artifact provider, and
shared resource-read contract. `common.resources_read` now selects a zero-based
PDF attachment or workspace file page and
optional bounded UTF-16 window from the hash-checked exact original, even when
flattened extraction lacks page offsets. Model output carries page-local character
coverage, a parent page range only for complete extracted page text, page count and
separate completeness; retained page
views cannot replace flattened `text`. Two focused two-page Harness cases cover
selection, continuation window, Office/CLI schemas, exact original guard and
invalid page.
Visual/scanned completeness, PDF table/figure/bbox and general provider navigation
remain open; Windows/Office/WebView2 qualification is still outstanding.

## Resource Context exact PDF text page ranges — 2026-10-23

Owner: Core PDF extractor, workspace file and Office artifact providers, shared
resource-read contract. `common.resources_read` accepts `startPage`/`pageCount` for
consecutive complete extracted pages of an exact PDF original. Returned text has
actual `page-range` coverage and a separate retained view; `nextPageIndex` continues
when another whole page would exceed the model budget. An oversized first page
fails with the existing page-local character-window route. Office and workspace
Harness cases cover full and partial ranges, exact evidence, schemas and invalid
selectors. This does not establish visual/scanned completeness or Windows/Office
qualification.

## Resource Context model read offers — 2026-10-23

Owner: Core model result projection. `common.resources_find` now shows only views
accepted by `common.resources_read`. Runtime image/PDF/raw capabilities remain
available to application consumers, but no longer appear as callable model read
representations. Known MIME and physical byte length reach model discovery as
source metadata, not a token estimate or read evidence. Two existing binary
Harness cases cover image/PDF candidates and schema alignment. Windows/Office/
WebView2 qualification remains open; per-view token estimates, image crop and
semantic PDF selectors are still pending.

## Resource Context YAML tree — 2026-10-23

Owner: Core YAML selector, Office Gateway artifact reads and workspace file provider.
Wave 3 now supports `representation=tree` for complete single-document YAML text
through the same pointer/depth/child-paging contract as JSON. It streams one exact
revision, stores only partial `yaml-path` view evidence, and exposes format and
requested coverage to the model and Inspector. Decoded scalar values are previews;
aliases, merge/complex keys in traversed mappings, duplicate addressed keys and
multi-document streams fail explicitly. There is no YAML source-length ceiling.
Two focused Harness checks cover semantics, 4-MiB workspace streaming and paged Office artifacts;
JSON regression and Web metadata checks are retained. Windows/Office/WebView2
remains unqualified. Wave 3 still needs image/PDF semantic views and
cost/capability metadata; broader YAML key/alias semantics remain open.

## Resource Context JSON Pointer tree — 2026-10-23

Owner: Core JSON scanner, Office Gateway artifact reads and workspace file provider.
Wave 3 now includes `representation=tree` with exact RFC 6901 pointer, depth and
immediate-child offset/limit for complete JSON text sources. Both hosts use the
same streaming selector, retain only the selected typed tree as partial CAS
evidence, and expose actual `json-path` coverage, requested depth and separate
completeness. Numeric values are marked previews; raw spelling requires source
windows. No source-length ceiling was added. Three focused Harness checks and
the Model Context Web check cover pointer semantics, large-file streaming,
multi-page Office artifacts, model schemas, CAS/evidence and Inspector metadata.
Real Windows/Office/WebView2 remains unqualified. YAML tree is covered by the
subsequent slice above; image/PDF semantic views and cost/capability metadata remain.

## Resource Context VBA procedure selection — 2026-10-23

Owner: Core resource parsing, Office Gateway and workspace file provider. Wave 3
now includes a common `procedure`/`procedureKind` selector for VBA source and UTF-8
workspace files, including the workspace Office binding. It selects from one exact
snapshot, preserves CRLF/Unicode and reports partial character coverage, selected
accessor and conditional-compilation status. Missing/ambiguous or incomplete
boundaries fail explicitly. Workspace scanning is streamed; live VBA retains its
existing whole-capture and mutation strategy. Only selected text is governed by
the model read budget. Requested procedure metadata reaches saved evidence and
Inspector; whole-source mutation authority is never inferred from a procedure.

Three focused Harness checks cover lexical boundaries, a live module beyond one
million characters, a streamed 16-MiB workspace file, budget expansion/refusal,
exact history/drift, schemas and evidence. These checks, three adjacent Harness
checks (line/character reads and compiler presentation), five Web scenarios and
ValidateVersionFormat pass. Windows/Office/VBE/WebView2 qualification remains open.
YAML tree is covered by the later slice above. Wave 3 still needs image/PDF
semantic views and cost/capability metadata; this does not close the wave.

## Conversation v8 phase-4 persistence/UI slice — 2026-10-23

The [specification reconciliation](conversation-v8/PHASE4.md#specification-reconciliation--2026-10-23)
at `02872af0` found two implementation gaps. Through `1ee68c62`, both are
fixed host-neutral: Workspace chat shows saved reflection decisions and finish
claims with evidence, and direct finish permits evidence-free claims while
rejecting foreign citations. Focused Core, plugin, Node UI and reflection
compaction/reload checks pass. The [risk register](RISK_REGISTER.md#conversation-v8-readiness-review--2026-10-23)
retains the history and remaining target-host evidence.

The candidate branch now creates format-8 chats, keeps v7 readable but
read-only, projects finish claims without upgrading task verification, and
persists visible reflection steps. Controlled CLI checks cover process death,
confirmation, compaction and cancellation/continuation. A v8 production HTTP
run with fake Excel now rejects the phase-0 premature finish and replays an
honest partial claim after the verified sheet write. Workspace permits
honest incomplete finish with failed acceptance; satisfied finish still
requires explicit criteria. See [phase-4 evidence and gaps](conversation-v8/PHASE4.md).
The host-neutral Harness builds and focused v8 workflow/replay scenarios pass.
The latest complete run had 986 passed / 18 failed / 1004 total. All previously
red v8 lifecycle/history fixtures now pass; three remaining failures depend on a
missing local Data Templates package, and independent resource/tool/bridge cases
still need triage. Six failures from that run subsequently passed targeted
checks: Outlook archive/CAS, R61 Excel Agent, three built-in catalog fixtures,
and bounded artifact search. The full suite has not been repeated. Accepted
v8 replay now sanitizes runtime references in model-visible prose while
preserving exact call arguments.

[Pinned real-model comparisons](conversation-v8/PHASE5_PILOT.md) now include
two repetitions of three synthetic tasks for v7 (0/6 accepted), ordinary v8
(6/6), and v8 without the reflection schema branch (5/6). On the same v8
binary, both field orders passed 6/6; a model-facing no-`reason` shim passed
4/6 against 6/6 ordinary v8. Failed runs did not become accepted false
successes. No ordinary v8 run selected reflection, and these small local CLI
samples cannot isolate protocol effects or qualify Windows/Office/WebView2.
Main remains v7. Owner: Core chat/run projection, Office transcript and
Workspace/CLI recovery. Formal phase-5, steer/UI and target-host gates remain open.

## Conversation v8 phase-3 adapter/prompt candidate — 2026-10-23

The isolated branch now pins v8 guidance across default and editable prompts,
projects tool schemas from the v8 root union, reports CLI finish correctly and
passes a controlled CLI/HTTP response-mode and result-role smoke. See
[phase-3 checks and gaps](conversation-v8/PHASE3.md). The full Harness still
contains unmigrated v7 fixtures; process replay/UI, real-model comparison and
Windows/Office gates remain open. Main remains v7. Owner: Core ModelProtocol /
Office and Workspace/CLI adapters.

## Conversation v8 phase-2 kernel candidate — 2026-10-23

The isolated `conversation-v8` branch now has bounded reflection continuation,
an event-stream terminal-evidence gate for finish, and v8 history records in both
Office and Workspace adapters. Source-linked host-neutral compilation and the
[phase-2 focused runner](conversation-v8/PHASE2.md) pass. Main remains v7; the
full Harness has unmigrated v7 tests, while prompts, process-level replay/UI,
real-model comparison and Windows/Office gates remain open. Owner: Core
AgentKernel / Office and Workspace adapters. Next: phase-3 consumer/prompt/test
migration, then phase-4 history/UI cutover.

## Conversation v8 phase-1 candidate — 2026-10-23

Core ModelProtocol now has the v8 closed forms, typed format errors, canonical
writer/repair guidance and a source-linked protocol runner in the isolated
`conversation-v8` worktree. Codec checks pass; see [phase-1 evidence and gaps](conversation-v8/PHASE1.md).
This intermediate candidate is not an integrated application: kernel/adapters/history
still call the removed v7 API, so a full candidate build is not ready. Main remains
on v7. Owner: Core ModelProtocol / AgentKernel. Next: phase-2 evidence admission and
reflection lifecycle, then atomic consumer/persistence/UI migration. Frozen repair
integration, real-model comparison and Windows/Office qualification remain open.

## Conversation v8.1 admission audit — 2026-10-23

Phase 0 reproduces premature v7 `done` through real loopback HTTP and the
production Office-neutral kernel/store with fake Excel: one verified sheet write,
no requested data/chart, then `Completed / model_done`. The existing UI correctly
retains `GoalVerification=not_assessed`; it does not independently verify the goal.
The explicit red regression saves exact wire bodies, event export and replayed UI
state. Existing completion-rejection checks pass 3/3. See
[audit/evidence](conversation-v8/AUDIT.md) and
[proposed ADR-0016](../decisions/ADR-0016-conversation-v8-admission.md).

Owner: Core ModelProtocol / AgentKernel, with Office/Workspace adapters. Production
remains v7; protocol/kernel/adapters/persistence/UI implementation and real-model
comparison are still pending. This scripted reproduction does not attribute the
historical DeepSeek incident or qualify Windows/Office/WebView2. Next slice is
schema/parser/writer/repair parity and endpoint probes before an atomic cutover.

## Resource Context requested coverage and completeness — 2026-10-23

Wave 2 / Wave 1 diagnostics now carry accepted read metadata from the common
Office and workspace/CLI read handlers through immutable evidence, compiler receipt,
saved request traces and Inspector. Requested line/character/row bounds, section/
part selection and actual source coordinates remain separate, as do selection,
representation and source completeness. Automatic windows preserve whole-read
intent; EOF does not inflate actual coverage. Excluded or transport-transformed
bodies retain read facts while delivery stays separate. Old records remain unknown.
Existing mutation guards and authority/currentness semantics are unchanged.

Seven focused Harness checks and five Web scenarios pass, covering Office/VBA and
workspace reads, EOF, row byte limits, sections, receipt/storage round trips,
unknown older traces and Web rendering. Windows/Office/
WebView2 qualification remains open. This closes the metadata-loss slice for existing
common read selectors; Wave 1 cost/transformation attribution and Wave 3 specialized
selectors remain separate backlog items, not completed waves.

## Resource Inspector retained payload and lineage — 2026-10-23

The Resources tab now exposes exact revision parent/restore/dependency provenance
and registered payload metadata from the authorized authority scope. Explicit CAS
verification streams the complete registered revision payload and reports hash/length
validity with a timestamp. It does not change currentness, completeness or delivery,
read live providers, verify dependency/view bodies or store a permanent availability
flag. Missing/corrupt bytes, absent payloads and unknown metadata remain distinct.

Focused Harness/Web coverage checks integrity failures and repair, scope isolation,
empty and 51 MiB bodies, metadata refresh and late replies after changing chats.
Windows/Office/WebView2 qualification remains open. Wave 5 is still partial:
kind/views/capabilities, transformation provenance, broader availability and the
graph/diff/reread/export workflow remain in BACKLOG. Diagnostic body download also
retains its separate Web/Int32 limits; this CAS verification does not use them.

## Copied HTML workspace recovery — 2026-10-23

The reproduced `resource cutover: schema mapping derived publication` failure is
closed host-neutrally. The fixture had created a document-owned HTML artifact
without publishing it to the document store; with a real historical chat-owned
snapshot it exposed a fork bug: rebound copies changed owner and vanished on chat
replay. New revisions now retain chat ownership. An explicit restore of a valid
older body also repairs a degraded active pointer before creating the rebound
revision. The focused fixture plus adjacent recovery, fork and shared-HTML checks
pass. Real Windows/Office/WebView2 behavior remains unqualified.

## Resource Context aliases and resource view — 2026-10-23

The existing Model Context inspector now derives chat-local `Q/O/E/R@v` aliases
from saved request events. Resource identities include authority scope; exact
revisions receive local ordinals and keep their aliases after pagination/restart,
including a new revision after same-byte restore. Alias lookup opens the saved
request; the Resources tab shows exact URI/revision, scope, observed coverage,
completeness, body-delivery evidence and read-only current-head state. Old traces
without scope and unverified CAS bytes remain explicitly unknown. No model-facing
address, live provider read or second store was introduced.

Focused Harness and Web checks cover alias mapping, scope isolation, pagination,
partial/delivery distinctions and UI lookup. Windows/Office/WebView2 delivery is
still unqualified. The original Wave 5 Explorer remains partial: broader Resource
Inspector metadata, graph, revision diff, explicit reread and integrated export
are tracked in [BACKLOG](BACKLOG.md#resource-context--остаток-исходного-предложения--2026-10-23).

## Resource Context plan reconciliation — 2026-10-23

Сверка исходного вложения с `main` на `3b865e14`: сохранённый текст
[предложения](resource-context/PLAN.md) совпадает с исходным MD.
**Весь исходный план не завершён.** Завершение отдельных исправлений и старой
архитектурной миграции не означает закрытие всех его Wave 0–6.
Это проверка исходников, текущих contracts и существующих fixtures; новых
build/test/Windows результатов эта запись не заявляет.

| Этап исходного MD | Подтверждено | Остаток / решение |
|---|---|---|
| Wave 0 — Audit | Трассировка, owners, RC-01…04 и accept/adapt/reject сохранены в аудите | Аудит исходников выполнен; измерений исходного реального Office/model workflow он не содержал |
| Wave 1 — Observability | Exact serialized request в CAS, RequestId/ContextSnapshotId, позиции messages/parts, links к evidence, причины основных exclusions, mixed-media golden fixture | Полная атрибуция всех преобразований не закрыта: strict-schema/смешанные сообщения могут иметь `Unknown`; полной стоимости каждого элемента нет. Сохранённая подготовка запроса не доказывает получение сервером |
| Wave 2 — Coverage/visibility | Requested/actual coverage и selection/representation/source completeness для lines/chars, Markdown sections, indexed parts и table rows; общий путь сохраняет эти факты в evidence, receipt, trace и Inspector | Покрыты реализованные selectors, а не вся матрица новых views из §4; delivery остаётся свойством конкретного request, старые записи без metadata — unknown |
| Wave 3 — Adaptive materialization | Общий бюджет текстового чтения, большие источники/длинные строки, потоковые workspace mutations, Office windows, media/pageIndex; общие VBA procedure и JSON/YAML tree selectors для Office и workspace/CLI; искусственные source ceilings сняты | Для YAML остаются aliases/complex keys; не реализованы crop/bbox и семантические PDF table/figure/OCR views; единая cost/capability metadata по всей матрице не завершена |
| Wave 4 — Compiler cleanup | Один compiler, frozen authority, causal frames, selective hydration, корректировка repeated-read links после budget eviction, restart/archive checks | Подтверждённые исправления сделаны. Деление compiler по размеру файла отклонено; новые refactor допускаются только под конкретный дефект |
| Wave 5 — Context Explorer | Существующий inspector: attempts/messages, exact JSON, evidence/revision/coverage, omissions, оригиналы и downloads; стабильные chat-local `R@v/E/O/Q`, поиск, resource view, revision provenance и явная CAS-проверка payload ревизии | Частично. Нет полной metadata/capabilities/availability для views/providers, derived graph, общего revision diff и явного reread из Explorer. Redacted trajectory export существует отдельно, но не интегрирован как этот Explorer workflow |
| Wave 6 — Qualification | Точечные host-neutral Harness/Web и large-source проверки предыдущих срезов; HTML restore fixture исправлен отдельно | Общий acceptance gate не закрыт: реальный Windows/Office/VBE/WebView2, representative PDF/visual provenance и измерения целевого model workflow остаются открыты |

`R/E/O/Q` — принятая с адаптацией UI-возможность, теперь реализована для
сохранённых request traces как производное представление. Это не model API и не
второй authority. Подробнее — в записи о новом срезе выше.
Создание новых T/S/C/D/M/A/P сущностей, второго observation/store/compiler и
переписывание существующего timeline с нуля отклонены в Wave 0. Позднейшие явные
решения пользователя о размерах источников заменяют исходные советы о 16k/1 MiB;
полная замена VBA в памяти остаётся намеренной.

Следующие функциональные срезы — непокрытые metadata/selectors и возможности
Explorer из BACKLOG; подтверждённые ошибки имеют приоритет по обычным правилам.
Остальные незавершённые slices и owners — в
[BACKLOG](BACKLOG.md#resource-context--остаток-исходного-предложения--2026-10-23).
Основание сверки: `LlmClient.DescribeWireMessages`, `ContextMessagePresentation`,
`ResourceReadProjection`, `ResourceReadToolHandler`, `ResourceGatewayService.WholeRead`,
`app-model-context.js` и текущие contracts Resource Fabric/Conversation protocol.

## Common source-size policy and CodeOnly form deletion — 2026-10-22

The source-size audit removes fixed character/byte ceilings from the common text
resource/editor/import paths. Model delivery uses the configured context budget;
Office and workspace line/character selectors agree, including long VBA lines,
Plans and published skill sources. Shared uploads stage to disposable files with
chunk reservations; complete typed consumers still use local memory. Exact revision,
coverage, UTF-8/hash validation, prepared-before-effect and read-back remain mandatory.
Direct deletion now supports verified CodeOnly UserForms with journaled restoration;
Designer/FRX and intrinsic document modules retain their explicit exclusions.
Owners: existing Core storage/publication, resource Gateway, shared data plane and
VBA mutation services. See [source policy and remaining operation bounds](../resource-fabric.md#source-sizes-and-context-budgets--2026-10-22).
Focused host-neutral checks cover large sources, chunked import/export, editor
integrity, search tails, partial evidence and CodeOnly deletion/restore. Full suite
and real Windows/Office/WebView2 qualification are not claimed. The independently
reproduced old copied-HTML restoration fixture was resolved in the later entry above.

## Resource Context regression review — 2026-10-22

Review of the Resource Context plan through `dae0dda8` reproduced and fixed six
host-neutral gaps: dropped source continuation coordinates in final model
projection; VBA line-window currentness after module mutation/external drift;
binary recovery budget incorrectly applied to large managed text restore;
automatic first character windows splitting UTF-16 pairs; needless partial VBA
reads above 500 lines; and the project upload consumer's obsolete 1-MiB text
readability cutoff. Owners remain the existing projection, authority, resource
gateway, workspace file and project upload services.
Focused read/projection/authority, streamed mutation/recovery, context compiler,
saved-request manifest, history/compaction and web inspector checks pass. A
pre-existing compaction fixture now explicitly limits format repair attempts;
production retry policy is unchanged. Full in-memory VBA replacement, one
compiler/CAS/authority and explicit unknown-effect reconciliation remain intentional.
See [review decisions and remaining evidence](RISK_REGISTER.md#resource-context-review--2026-10-22).
No Office/VSTO validation ran; real Windows/Office/WebView2 qualification and the
separately recorded baseline fixture failures remain open.

## VBA internal mutation reads without a size ceiling — 2026-10-22

The former one-million-character ceiling is removed from internal VBA mutation
snapshots, including preparation, verification, retained after-source, restore and
restart reconciliation. Full in-memory patch validation and whole-module backend
replacement remain intentional; model resource windows keep their separate budget.
A focused host-neutral fixture patches a larger module, rejects late drift, restores
its complete backup and reconciles interrupted terminal persistence without replay.
Real Windows/VBE qualification remains open.

## Large workspace copy, move and delete — 2026-10-22

`files.copy/move/delete` now accept a delivered exact text window for a large
UTF-8 source. The file owner verifies the pinned full revision, streams copy and
read-back, and retains journaled unknown-effect recovery. Large managed deletion
also restores through streamed read-back. Full text is never returned as after
evidence for these operations; exact window continuation remains available.
Focused host-neutral copy/move/delete, restore and interrupted-operation checks
pass. `files.replace` still requires a complete accepted read. Real
Windows/Office/WebView2 qualification remains open.

## Large PowerPoint notes through common resources — 2026-10-22

`PowerPoint notes: N` now exposes speaker notes through the shared `text` read:
metadata bounds, a context-sized first window and exact `startChar`/`charCount`
continuation. The bound backend selects COM text ranges without materializing the
whole notes body. Focused host-neutral PowerPoint resource checks pass, including
notes beyond the former one-million-character capture ceiling and UTF-16 boundary
refusal. The `source` JSON view retains its separate whole-capture bound; real
Windows/PowerPoint/WebView2 qualification remains open.

## Context-sized text reads and streamed workspace writes — 2026-10-22

The common model read now returns complete text when the provider can materialize
it within the configured input budget, otherwise an exact first window with source
coverage and continuation bounds. Word and PowerPoint slide text use character
windows; VBA modules use line windows without first materializing a large module.
Workspace files use the same bounded model behavior, including sources beyond the
former 80-MiB cap. Streamed
`files.patch` no longer limits each fragment to one MiB; large `files.create` and
`files.replace` use staged exact read-back and the mutation journal. Internal
materialized reads, imported artifact limits, whole-module VBA patching and other
Office representations retain their separate contracts. Focused host-neutral checks
pass for these paths, including an 80-MiB-plus workspace file and interrupted
large create; real Windows/Office/WebView2 qualification remains open.
The expanded resource harness has 34 passes and four failures also reproduced
on clean `HEAD`; their owners are recorded in `BACKLOG.md`.

The dated entries below record earlier increments; their former size limits are
superseded by this section and the current `docs/resource-fabric.md` contract.

## Word main-story range navigation — 2026-10-22

The Word document `structure` view now returns typed main-story start/end bounds
without reading the full body. A large document can then be read through an
explicit bounded `Word range: start:end` target. The model result names the exact
document-relative `sourceCoverage`, marks the selected range complete and leaves
whole-document `sourceComplete=false`. Focused host-neutral Word resource checks
pass, including a document beyond the one-million-character whole-capture limit;
real Windows/Word COM and WebView2 qualification remain open. VBA and PowerPoint
large-source provider work remains separate.

## Streamed patch of large workspace text files — 2026-10-22

`files.patch` now accepts an observed unique UTF-8 anchor in files up to 80 MiB,
with anchor/replacement each limited to 1 MiB. The file owner streams the pinned
revision into a staged result, journals its exact CAS payload before dispatch,
checks live source revision and read-back hashes, then publishes a partial
after-source. Possible effects remain unknown across interruption until explicit
reconciliation; they are never replayed. Focused host-neutral unique/overlap,
drift and restart recovery checks pass; Windows/Office/WebView2 qualification
remains open. Whole-file create/replace/copy/move/delete retain their bounds.

## Workspace model file reads use the configured context budget — 2026-10-22

The fixed 16000-character delivery ceiling is removed from workspace file and
skill `common.resources_read`. The file schema and handler now derive the maximum
selected text from this run's configured model input budget; UTF-8 source capture remains
bounded at 80 MiB and non-patch whole-file mutation remains at 1 MiB. The compiler retains
final request-budget admission and explicit omission/refusal semantics. A focused
host-neutral check reads a 40000-character exact range and whole file with the
right partial/complete coverage; Windows/Office/WebView2 qualification remains open.

## Earlier large-source patch admission guard (superseded above) — 2026-10-22

Workspace file reads now expose source windows up to 80 MiB, but the existing
text mutation/read-back and recovery path remains limited to 1 MiB. Patch
preparation and execution reject an observed larger revision before dispatch,
using the retained source length rather than the excerpt payload length. The
model tool and built-in file skill state the same boundary. The focused
host-neutral file check passes. The streamed patch above now closes that specific
gap; Windows qualification remains open.

## Large CLI text-view continuation — 2026-10-22

`rna open --view text` now streams 128–32000 UTF-16 characters from a pinned
workspace source up to 80 MiB when the one-MiB whole read is unavailable. Exact
continuation survives live edit/deletion, trims a surrogate boundary without
skipping it, and keeps `complete=false` for a terminal fragment of a large file.
The focused CLI view scenario passes, including missing-CAS refusal. Whole
reads/writes, raw and retained web-snapshot views keep their existing bounds;
Windows/Office/WebView2 qualification remains open.

## Streamed workspace text windows beyond one MiB — 2026-10-22

Workspace/CLI model line and character selections now pin and stream strict UTF-8
files up to 80 MiB without loading the whole source. Each read still delivers at
most 16000 characters with partial exact evidence; whole reads/writes retain the
1-MiB bound. Context preparation revalidates large excerpts against raw source
revisions. A focused 16-MiB allocation/Unicode/drift check covers the new path;
Windows/Office/WebView2 qualification remains open.

## Workspace character windows for long lines — 2026-10-22

Workspace/CLI model reads now accept paired `startChar`/`charCount` for one long
UTF-8 file line within the existing 1-MiB source and 16,000-character delivery
bounds. The file owner pins the current revision, rejects split UTF-16 surrogate
pairs and retains only partial observation evidence. Model projection reports
requested/actual coverage and `selectionComplete=true`, `complete=false`.
The focused host-neutral handler check passes; larger-source streaming and
Windows/Office qualification remain open.

## Bounded character selection for long lines — 2026-10-22

The next Resource Context slice adds `startChar`/`charCount` to model-facing
source/text reads for long single lines within the existing whole-snapshot bound.
UTF-16 character ranges reject split surrogate pairs, retain exact partial
evidence and expose requested/actual coverage with `selectionComplete=true` and
`complete=false`. Focused host-neutral recovery and VBA tool checks pass; provider
pushdown beyond the whole-read bound and Windows/Office qualification remain open.

## Structured row-selection completeness in model results — 2026-10-22

The next Wave 2 RC-04 slice separates a selected `table` row window from a
whole-resource read. Model results now carry requested offset/count, actual
record-range coverage and `selectionComplete`, which is false if a bounded batch
stops before the requested window. `complete=false` even on a terminal provider
page. Exact row bounds and provider completion metadata are checked before
projection. Focused host-neutral Excel table and short-batch checks pass;
Windows/Office/WebView2 qualification and other provider views remain open.

## Indexed text-part completeness in model results — 2026-10-22

The next Wave 2 RC-04 slice marks a successful `partNumber` read as
`selectionComplete=true` while retaining `complete=false`, `partCount`, actual
character-range coverage and the source-completeness signal. Projection checks
the returned part number, count and exact text range before making that claim.
The focused host-neutral indexed-part check passes. Other selectors, derived
views and Windows/Office/WebView2 qualification remain open.

## Markdown section completeness in model results — 2026-10-22

The next Wave 2 RC-04 slice separates a complete selected Markdown section
from a whole-resource read in the model projection: `selectionComplete=true`,
`complete=false`, with its actual character-range coverage. Exact runtime
evidence still records the complete selected excerpt and cannot authorize a
whole-resource refresh. The new focused host-neutral model-result check passes;
two older Markdown cases still fail on unrelated stale Harness expectations
recorded in [BACKLOG](BACKLOG.md).
Other selectors, derived views and Windows/Office/WebView2 qualification remain
open.

## Bounded line-read coverage in model results — 2026-10-22

The first Wave 2 RC-04 slice now keeps the existing typed actual `coverage` in
model-projected `common.resources_read` results. Successful Office and workspace
file line reads report requested start/count and `selectionComplete=true`, while
`complete=false` continues to deny whole-resource observation. Model projection
keeps coverage in a closed field shape and removes exact references, revisions,
cursors and internal offset. Host-neutral projection and line-read checks pass;
other selectors, derived views and Windows/Office/WebView2 qualification remain
open.

## Mixed-request manifest golden check — 2026-10-22

The Wave 1 RC-02 golden fixture now compares a saved request's UTF-8 CAS bytes
with its persisted diagnostic manifest for text, image, rendered PDF page, audio
and a later image omitted by the shared limit. It verifies final message/part
positions, selected-byte lengths and hashes, confirmed PDF page omissions and
absence of diagnostic fields in provider JSON. The focused host-neutral check
passes with the two existing PDF platform warnings. Other compiler
transformations and Windows/Office/WebView2 qualification remain open.

## Archived operation decisions in context trace — 2026-10-22

The next RC-02 slice is fixed host-neutral. The compiler now attributes both
source call and result when an optional archived operation is skipped by receipt
budget or bounded candidate admission. When a receipt is kept, its original call
is marked as represented by that receipt. Mandatory unresolved receipts remain
selected with zero optional budget. Focused archive materialization and working-
set checks pass; the two existing PDF platform warnings remain. Other compiler
transformations and Windows/Office/WebView2 qualification remain open.

## Compiler exclusion reasons in context trace — 2026-10-22

The next RC-02 slice is fixed host-neutral. Excluded source entries now carry a
typed compiler decision for explicit exclusion, superseded/unknown/unavailable
evidence, duplicate bodies, budget eviction and bounded archive-body admission.
Unattributed omissions say `Unknown` instead of guessing a cause. A zero archive
body budget now skips CAS hydration before eviction. Focused compiler,
saved-event/inspector and web checks pass; only the two existing PDF platform
warnings remain. Attribution for other compiler transformations and
Windows/Office/WebView2 qualification remain open.

## Typed resource evidence links in context trace — 2026-10-22

The next RC-02 slice is fixed host-neutral. Compiler receipt entries now retain
the exact tool-call ID and typed evidence ID, resource revision, view and observed
coverage. The body-delivery flag is true only after verified synthetic payload
hydration, false for an excluded source, and unknown for unproved or later
projected content. The saved request and inspector preserve these links without
adding model-facing fields. Focused compiler, saved-event/inspector and web checks
pass; the two existing PDF platform warnings remain. The known exclusion-decision
slice is recorded above; remaining transformations and Windows/Office/WebView2
qualification remain open.

## Transport-selected attachment manifest — 2026-10-22

The RC-02 attachment-selection slice is fixed host-neutral. The existing saved
request trace now attributes selected attachment text, image, audio and rendered
PDF pages to their source attachment and final JSON part. It records byte counts
and SHA-256 for selected
content, bounded PDF page omissions when the renderer confirms page count, and
explicit reasons for budget-skipped or analysis-replaced attachments. The exact
request CAS body is unchanged and remains authoritative; the whole augmented
message is still `Unknown`. Focused trace/payload and web inspector checks pass.
Windows/Office/WebView2 qualification remains open. No new store or model-facing
metadata was added. The typed evidence-link and compiler-decision slices are
recorded above; other compiler transformations remain open.

## Repeated-read receipt after budget eviction — 2026-10-22

RC-03 was reproduced host-neutral: two identical complete reads were folded, then
the final request budget removed the sole remaining body while the earlier receipt
still pointed to it as delivered. `ModelContextCompiler` now resolves folded-read
links after budgeting. The earlier operation keeps its successful outcome but says
`bodyIncluded=false` and no longer points to absent content. Durable read facts and
write evidence are unchanged. The focused `resource cutover: correctness before
budget` check passes for both the retained and evicted cases; build has the two
existing PDF platform warnings. Windows/Office/WebView2 qualification remains open.

## Request provenance after strict-schema projection — 2026-10-22

RC-01 is fixed host-neutral. The model protocol compares visible accepted and
projected messages for each request attempt. If strict-schema projection changes
text but retains its message ID, the saved request trace uses `Unknown` rather
than the compiler's pre-projection `Full` label. Repair and json_object fallback
recompute the status; the exact request payload and accepted prompt stay intact.

Focused `model context: request schema modes and fallback`, `model context:
compiler presentation`, and `storage: model request trace precedes dispatch`
checks pass. Build retains two existing PDF platform warnings. No live model or
Windows/Office/WebView2 qualification. The subsequent transport-part manifest
slice is recorded above.

## Resource Context audit — 2026-10-22

Completed the requested Wave 0 source audit; the original
[proposal](resource-context/PLAN.md) and
[accept/adapt/reject decisions](../audits/resource-context-current-state.md) are indexed.
Exact request CAS retention, the shared compiler/receipt and the model-context
Inspector already exist. The next bounded slice is RC-01: strict-schema prompt
projection can preserve a misleading `Full` provenance label after changing text.
Per-part delivery metadata is a later extension of the existing trace, not a new store.

This change is documentation only. Existing checks were inspected, not rerun;
no build, Harness, live-model or Office/VSTO validation was performed. Runtime
reproduction and Windows/Office/WebView2 qualification remain open. Shared-checkout
commits observed during review are recorded in the audit; no code was changed here.

## Plugin source-chat call history — 2026-10-21

Workspace accepted calls now persist exact runtime ID, canonical tool name and
successful model-attempt origin per call. A controlled two-call read batch survives
restart and the next turn; old origin-less records still display but explicitly
require a new chat. The user-reported Windows `Native history...` screen and
Data Templates style state still need exact-build Windows/WebView2 verification.

## One new structural view for model inputs — 2026-10-21

New built-in resource reads, discovery and HTML/JS input schemas expose `table`
for bounded rows; new HTML binds reject `records`. Saved HTML bindings, v1
transfer packages and older custom JS inputs still use the internal `records`
reader. Thirteen focused resource, HTML, Excel, JS, inventory and transfer Harness
checks pass, including old binding read after transfer/restart. The legacy seam and its
removal gate are in [MIGRATION_MAP](MIGRATION_MAP.md#historical-structural-records-view--2026-10-21).
Live model choice, Windows/Office and WebView2 remain unqualified.

Next: explicit saved-binding/package migration, then removal of internal
`records` and provider advertisements with host-neutral restart/transfer checks.

## Explicit Excel chart source on creation — 2026-10-21

`excel.upsert_chart` now requires a nonblank `sourceRange` when it creates a
workbook chart, including the missing-target branch of `upsert`; it no longer
substitutes `A1:B6`. Omitted source on an existing chart still preserves its
series, including `updateOnly`. Explicit blank sources fail. Generated names and
the other creation defaults remain. Unsupported explicit `chartType` values now
fail instead of silently becoming `line`. The chart schema and built-in skill
describe this split; domain and backends refuse a source-less create before dispatch.
Four focused `excel chart:` checks and the exact R61 inventory pass. Live
Windows/Excel chart behavior remains unqualified; no Office/VSTO validation ran.

Next: isolated `table`/`records` migration and live model-choice measurement.
Exact large-range selector pushdown remains a separate resource task.

## Explicit Excel table source — 2026-10-21

`excel.add_table` now requires a nonblank `sourceRange`. Schema, adapter, domain,
COM backend and fake backend no longer substitute `A1:B2`; omitted/null/blank
sources fail before backend read or possible document mutation. An explicit
`A1:B2` retains generated table names and default headers. Four focused
`excel table:` checks pass, including ordinary/prompt/strict schema rejection,
model parser rejection and direct domain refusal without schema validation.
The exact R61 descriptor inventory is updated. Changed COM behavior requires
Windows/Excel qualification; no Office/VSTO validation ran here.

Next: the isolated `table`/`records` migration. Large-range selector pushdown
and live model-choice measurement remain separate.

## Tool contract and inventory review — 2026-10-21

Static review of Common resource/authoring, HTML/VBA, Excel, Word, PowerPoint and
Outlook tool intents and result boundaries is recorded in the
[Tool Library](../tool-library.md#tool-contract-review--2026-10-21). The R61
inventory now covers 80 unique built-in IDs, including six later tools and five
reviewed Common descriptor changes; its focused Harness check passes 1/1.
Descriptors clarify chat-chart versus workbook-chart, local Outlook archive
checkpoint versus mail mutation, and document-owned file versus runnable HTML
workspace or Markdown document. HTML skill guidance selects `table` for new
tabular bindings. `table` and `records` remain equivalent in runtime; saved
bindings and transfer consumers require a separate atomic migration before
removal. Static review does not establish model-choice quality or Office behavior.
No Office/VSTO validation ran on this machine.

Next: the isolated tabular-view migration; compare changed model choices in
existing live fixtures when available. Exact large-range selector pushdown
remains a separate resource task.

## Large Excel ranges: bounded selected-column analysis — 2026-10-21

Added read-only `excel.summarize_columns` for an explicit sheet, A1 rectangle and
1–8 named columns. It scans all selected rows in at most 1024-cell native chunks,
with a 2000000-selected-cell admission bound, and returns compact numeric and
occupancy statistics plus exact scan counts. Long cell bodies never enter the
model result. The existing raw 100000-cell and preview 2000-cell bounds remain.

Host-neutral coverage confirms a 200001-row, wide sparse sheet with only two
selected columns, typed numeric/text/boolean distinctions, compact model projection,
early oversize refusal, later-chunk failure, coordinate drift and cancellation.
The native tool requires its exact offered schema. The result reports a complete
*sequential observation* of selected cells; concurrent value edits without a
coordinate change are not detected. The changed COM read path and real Excel
latency need Windows/Office qualification; live model choice and token cost have
not been measured. No Office/VSTO validation ran on this machine.

Next: exact row/column selector pushdown and filtering/grouping if needed, with
explicit coverage and consistency semantics. See the
[bounded analysis backlog](BACKLOG.md#large-excel-analysis-without-full-model-context--2026-10-21).
Discovery and overview are part of the same large-workbook change.

## Large Excel ranges: overview before values — 2026-10-21

The user's large-workbook constraint takes priority over the next mutation cleanup.
History shows that `f15af843` deliberately bounded Excel context previews to 2000
cells per sheet. That protection is retained. The empty range-catalog filter was
already present when the provider was introduced in `ec8b1793`; no separate
intentional catalog-disable decision was found in the inspected history.

Large explicit ranges now support `structure` as a compact `range-overview`, with
live coordinates/dimensions and up to nine small suggested slices, not cell bodies.
The internal metadata path validates the actual sheet/range without `Value2` or
`Formula`. The model can choose exact existing A1 read targets; no new tool/schema
or automatic sweep is added. Raw capture bounds and the model request budget remain.
Current row selectors still follow whole-source capture and are not streaming.

Five focused Harness checks pass: three Excel discovery checks, pre-materialization
bounds and exact retained profiles. The full-sheet fixture has 17179869184 cells:
the overview stays below 4096 characters with zero cell materializations; one chosen
far-corner slice is then read exactly. It also checks 20-result discovery, missing
sheets, quoted/offset targets and retained overview meaning. Build has only the two
existing PDF warnings. The changed COM metadata branch needs Windows/Excel
qualification; no Office/VSTO validation or live model/token claim is made here.

The bounded selected-column analysis above is the first follow-up; exact row/column
access and filtering remain in the
[analysis backlog](BACKLOG.md#large-excel-analysis-without-full-model-context--2026-10-21).
Explicit `add_table` sources and chart destinations remain queued after it.

## Excel used-range discovery — 2026-10-21

Committed capability source-read semantics as `ddd0c535` and explicit Excel targets
with worksheet-name preservation as `a3d6f92c`. The next bounded fix replaces the
range catalog's lightweight `sheets` snapshot with the existing typed `workbook`
snapshot containing `UsedRange`. The old filter silently removed every entry;
missing metadata now fails validation. Search scopes retain lightweight inspection,
and range enumeration reads no cell bodies. Existing shared catalog paging now also owns
range cursors, including invalidation when only source coverage changes.

Four focused Harness checks pass: populated/blank sheet discovery, changed extents,
missing metadata, the 200-sheet bound and coverage-only cursor invalidation;
large unrelated catalogs still permit direct document reads; worksheet-name
round-trips now use actual fake enumeration without an injected snapshot; exact
range evidence remains intact. The fake inspector derives extents from populated
cells instead of returning a fixed `A1:B4`. Build has only the two existing PDF
warnings. No Office/VSTO validation or live-model quality/token claim.

Next: explicit `add_table` sources, then chart-creation destinations. Reserved
worksheet-name validation and the `table`/`records` review remain open.

## Explicit Excel write destinations and worksheet references — 2026-10-21

`excel.write_range` requires nonblank `sheet` and `address` for value, formula and
table writes. Defaults are removed from the schema, adapter, domain and backend;
missing/null/blank targets fail before dispatch. The worksheet argument remains a
literal name. A shared formatter quotes composed references and doubles embedded
apostrophes according to Microsoft's Excel grammar. Resource targets decode this
syntax once; enumeration no longer unquotes literal host names.

A round-trip check also reproduced a resource-identity defect: URI creation trimmed
boundary spaces from worksheet names and could read a different sheet. URI segments
now preserve those spaces, while retaining existing empty/dot/slash rejection.
The regression distinguishes two sheets whose names differ only in boundary spaces.

Twelve focused Harness checks pass: six write checks, URI round-trip/rejection,
exact resource evidence, HTML range binding, named-table reads and range-mutation
semantics. Names cover spaces, apostrophes, repeated apostrophes, `!`, Unicode and
literal double quotes. The scripted production-skill report passes all six workbook
checks in nine requests. No new live-model run, token-saving claim or Office/VSTO
validation; the COM backend change still needs Windows/Excel qualification.
The Harness build retains only the two existing PDF warnings. The write inventory
row is reviewed; the current full inventory comparison still has five unrelated
Common differences, listed in `RISK_REGISTER`.

Next priority: the separately recorded empty used-range discovery path, then
explicit `add_table` sources and chart-creation destinations. The resource
`table`/`records` vocabulary review remains queued. See the
[current Tool Library review](../tool-library.md#tool-contract-review--2026-10-21).

## Capability source reads do not claim selection — 2026-10-21

Formatting-schema compression is committed as `c9c3b33d`. The next bounded
semantic fix removes the root `loaded` flag from model-facing capability
search/read results, including stale evidence and the compiler's oversized-content
refusal. Read completeness, exact source bodies, error codes and skill-selection
guidance remain. `schemaInRequest` still requires the exact current offered tool.
The source result/CAS, revision checks, selection, admission and replay owners
are unchanged; no field is renamed into another model-owned activation state.

Seven focused Harness checks pass: full skill inspection without selection,
current-surface-only schema receipts across three result roles, stale evidence,
oversized content, direct tool admission in both response modes, durable admission
replay and exact discovery. Source metadata stays intact and original schema
evidence still admits the tool; projected receipts do not. Build retains only the
two existing PDF warnings. No new live-model run or Office/VSTO validation: this
closes the result ambiguity locally, not the open model batching/choice risk.

Next: review explicit destinations for Excel mutations, followed by the
`table`/`records` consumer audit in the canonical Tool Library review.

## Compact Excel formatting schema — 2026-10-21

`excel.format_range` now declares its nine fields once, with seven required-only
alternatives. Core schema validation and optional-null normalization combine each
alternative with the closed parent; invalid types/extra fields and all-null
formatting still fail. The old repeating helper is removed. Strict output expands
the same contract to its previous complete variants, without changing accepted
arguments, execution policy, provider limits or prompts.

| Schema representation | Before / after characters |
|---|---:|
| Runtime source | 8009 / 1191 |
| Prompt descriptor (`json_object`) | 7021 / 1191 (−83.04%) |
| Strict argument schema (`json_schema`) | 4250 / 4250, exact equality |

The same unprompted report, workbook, full profile and model route were compared
with `excel-schema-receipts-live-1`. The new live run passes all six workbook
checks and five verified writes, with clean health and no unknown effects or
compaction. It still chooses direct tool admission rather than the report skill.

| Same task/profile | Requests | Actual input / output tokens | Protocol repairs |
|---|---:|---:|---:|
| Compact receipts baseline | 12 | 136,712 / 1097 | 2 |
| Compact format schema | 14 | 153,363 / 1306 | 4 |

Initial input remains 9107. Final input falls from 14,107 to 12,770, and the only
changed descriptor among the same eleven final tools is `excel.format_range`.
Cumulative input rises 12.18%: the new trajectory repairs fenced JSON, an unloaded
write batch and two additional mixed/write batches. Rejected batches execute no
calls. The schema is smaller, but this pair does not establish whole-task savings,
improved model compliance or causation for the extra repairs. No repeat was selected
to replace this result.

Validation: eight focused Harness checks pass, including all 128 option subsets,
strict output/literal preservation, direct admission in both response modes and
fallback. The existing web Tool Library checks pass; its independent JS validator
also agrees on 296 cases against both actual model-facing schemas. Build retains
only the two existing PDF warnings. The changed Excel inventory row is reviewed;
the full R61 inventory gate still has ten unrelated stale rows, recorded in
`RISK_REGISTER`. No Office/VSTO validation.

Evidence in the existing local eval directory: `excel-compact-format-live-1/`,
`compact-format.patch`, route/metadata, `compact-format-results.json`,
`compact-format-comparison.json`, actual schema/case exports and the independent
UI comparison script. Source is `5e066a3a` plus the scoped patch; patch SHA-256 is
`b91c128b30e53c6dbe0a1c0c7d4273fe5801474c355d3a8aee960003e069bdc8`, and metadata
pins the binary. API usage, full profile, task/source bytes and callable descriptors
were independently checked. Next: the bounded semantic review below, starting
with ambiguous capability-read results; batching/format compliance remains open.

## Tool semantics review before further schema compression — 2026-10-21

Committed the report fixture and compact schema receipts as `5e066a3a`. The next
slice now explicitly includes operation overlap, argument meaning and result
ambiguity under stage 5 of the supplied unified plan; stage 4 owns token cost.
The [current review](../tool-library.md#tool-contract-review--2026-10-21) separates
confirmed code facts, candidates and unproven effects on model behavior.

The first review covers Common discovery/resources and Excel report operations.
Next: clarify the ambiguous `loaded` field on capability source reads, then review
mutation destinations and `table`/`records` consumers. Full-family restructuring
is not decided. Factoring `format_range` requires preserving the existing validator
and both schema projections; deleting repeated branch fields alone is unsafe.
This slice changes documentation only; diff and local links were checked, without
a new build, model run or Office/VSTO validation. Other tool families and actual
model-quality effects remain open.

## Compact admitted-schema receipts — 2026-10-21

The frozen compiler now supplies its callable surface separately from the visible
catalog to result projection. A successful complete direct schema read becomes a
compact `schemaInRequest=true` receipt only when its descriptor and revision match
an offered tool exactly. The request keeps the authoritative schema; the durable
transcript/CAS keeps the original read. Admission, budget refusal, stale evidence,
strict-response fallback and replay decisions retain their existing guards.

The same unprompted Excel report and `gemma4:cloud` / 32K / 4096 profile were rerun
with the production fake host. All six workbook checks pass again, with five
verified writes, clean execution health and no unknown effects or compaction.

| Same task/profile | Requests | Actual input / output tokens | Protocol repairs |
|---|---:|---:|---:|
| Before | 13 | 174,451 / 1014 | 3 |
| Compact receipts | 12 | 136,712 / 1097 | 2 |

Actual cumulative input falls 21.63% in this pair. Initial input stays at 9107;
the final request drops from 16,787 to 14,107. Exact callable descriptors in the
final requests are identical. Five schema-result bodies shrink from 14,077 to
1561 characters (88.91%); all five complete durable descriptors and revisions
remain present. Character reduction is not a token estimate. The model trajectory
also changes: the new run repairs fenced JSON and an unloaded write batch, then
executes individual writes. One pair does not establish reliability or attribute
all cumulative savings to projection. No prompt or limit was changed.

Validation: seven focused checks pass, including current-surface-only projection,
three result roles, malformed/stale evidence, direct admission in both response
modes, atomic overflow, durable replay, strict-schema fallback and existing
runtime-evidence/archived-target projection. Harness build has only its two
existing PDF platform warnings. No Office/VSTO validation.

Evidence: `excel-schema-receipts-live-1/`, `schema-receipts-results.json`,
`schema-receipts-comparison.json`, `schema-receipts.patch` and route metadata in
the existing local eval directory. Source is `69bd97b8` plus the scoped patch;
metadata pins patch/binary hashes. Full profile, task/source bytes, saved API usage
and exact callable schemas were independently compared with `excel-report-live-1`.
The old fixture's skill-coverage failure remains distinct from its passing report.
Next: reduce remaining repetition inside the authoritative schemas without changing
accepted arguments; use this same report baseline. Model batching/format repairs,
live interruption/resume and real Excel calculation/rendering remain open.

## Excel report discovery with production skill text — 2026-10-21

The fake and real Excel adapters now share `ExcelBuiltInSkillProvider` in Office.
The shipped 1844-character report body is unchanged; the fake 26-character stub
is removed. The existing live workflow fixture adds a numeric regional report,
named sheet/table, number format and workbook chart. Its six independent state
checks also protect the original workbook. Scripted and real-model runs use the
same production conversation, admission and mutation/read-back owners.

| Live case, same gemma4:cloud / 32K / 4096 profile | Requests / actual input / output | Result |
|---|---:|---|
| No skill/tool hint | 13 / 174,451 / 1014 | 6/6 report checks; direct schema discovery; report skill not selected |
| Explicit report-skill instruction | 12 / 163,612 / 1118 | 6/6 report checks; complete production skill offered |

Both finish `model_done` with five verified writes, no tool errors or unknown
effects. Each needs three protocol repair requests after attempted write batches;
rejected calls execute nothing. Neither run needs compaction or returns an empty
completion. Initial input is 9107/9119 tokens with six schemas. In the skill case,
the next request carries all 14 schemas and full instructions at 13,166 actual
input tokens. No profile limit or guard was raised.

The unprompted case proves direct loading, not automatic report-skill selection.
Its original fixture's `passed=false` records that missing skill coverage despite
all report checks passing. The final fixture separates `taskPassed` from
`reportSkillOffered`; `RNA_REQUIRE_REPORT_SKILL=true` adds the explicit instruction
and requires full selected text in a model instruction message. Raw earlier
evidence is preserved. Neither model run selects task tracking. These are one
trajectory per distinct task, not a reliability rate or a token-saving comparison.

The direct-loading trace also retains five schema results totaling 14,077
characters in history. Their exact parameter schemas are simultaneously present
in the instruction surface; `excel.format_range` alone contributes 7650 result
characters. These are character measurements, separate from API token usage.
Next optimization: remove redundant model-facing schema history after exact
admission while preserving durable evidence and authoritative schemas, then
compare the same task/profile. Repeated write batches remain a model-guidance risk.

Validation: three focused checks (grader rejection, built-in packages, production
project includes), scripted Excel report and existing scripted VBA pass. Harness
build has only the two existing PDF platform warnings. Live evidence is in
`excel-report-live-1/`, `excel-report-skill-live-1/`, `excel-report-results.json`
and `excel-report-schema-repetition.json` in the existing local eval directory.
Metadata pins `69bd97b8` plus `excel-report-fixture.patch` or
`excel-report-fixture-v2.patch` and binary hashes. Saved API usage, wire settings,
full profile equality, initial workbook and the single task-prefix difference
were independently checked. The model route digest is unchanged. This simulates
Excel state; calculation, chart rendering and real Windows/Office qualification
remain open, as do live interruption/resume and repeated success.

## Live recovery through compaction and skill removal — 2026-10-21

The combined HTML/VBA task now has one complete live pass at the unchanged
`gemma4:cloud` / 32K / 4096 profile and two format attempts. All four final sources
match the requested edits byte-for-byte, with four verified writes, no unknown
effects and one published context checkpoint. Office remains simulated.

The helper contract now distinguishes `observationEligible=true` sources from
successful historical mutation receipts. Kind errors identify the source alias
and allowed kinds. Batch errors name the standalone tool and explain that no call
or skill selection executed. After a refused load, repair preserves that blocker;
Office budget feedback names explicit `common.skills_load` / `action=remove`
before retrying a load. Eligibility, admission and effect guards are unchanged;
no automatic skill removal or larger limit was introduced.

| Candidate | Requests / actual input / output tokens | Exact edits | Outcome |
|---|---:|---:|---|
| Source-kind and batch guidance | 17 / 250,153 / 6509 | 3/4 | Checkpoint succeeds; HTML+VBA skill budget still blocks VBA |
| Plus admission recovery | 19 / 282,998 / 6918 | 4/4 | HTML skill explicitly removed, VBA loaded and patched, `model_done` |

Initial input stays at 9201 tokens. In the successful run, removing HTML reduces
the next input to 12,726 tokens; loading VBA then uses 16,960. One recovered
admission failure remains recorded in execution health. Five ordinary repair
requests and one helper repair are included in the totals; the initial empty
response still consumes 4096 output tokens. This is one successful trajectory,
not a reliability rate or an overall token-saving result.

Validation: 12 focused checks pass, covering source-kind rejection, frozen repair,
batch safety and exact skill admission. Evidence is in the existing local eval
directory: `office-recovery-guidance-{1,2}/`, `recovery-guidance-results.json`,
`recovery-guidance.patch` and `recovery-guidance-v2.patch`. Metadata pins `c0d63e93`
plus each scoped patch/binary hash; full profiles, source/task hashes and final
bytes were independently compared. Next: live Excel report/discovery coverage
with production skill text, then repeatable success and fewer repair requests.
Live interruption/resume and real Windows/Office qualification remain open.

## Shared compaction attempt setting — 2026-10-21

`ContextCompactionRuntime` now uses the existing `MaxAgentFormatRetries` setting
and the same normalization as `ModelProtocolClient`: 1–20 total attempts, including
the first; default 10. No new setting or migration. Each compaction operation has
its own counter. Every retry keeps frozen sources/schema and only the latest
correction; earlier repair prompts and rejected responses do not accumulate.
The settings tooltip now includes compaction. Provider/refusal/cancellation and
checkpoint-publication boundaries remain unchanged. The earlier live evidence
below used limit 2; this setting change does not close its model-behavior gaps.

Validation: six focused compaction checks and three existing main-response retry
checks pass. They cover disabled/exhausted repair, success on the last configured
attempt, normalization and budget refusal. Harness build passes with the two
existing PDF platform warnings. No new live-model or Office run was needed.

## Bounded compaction response recovery — 2026-10-20

The initial `ContextCompactionRuntime` recovery candidate permitted one correction
after an invalid helper response. Both attempts retained the frozen sources/schema
and passed separate request budget checks. Invalid output stays outside accepted history; source-role checks,
provider refusal, cancellation and the previous checkpoint remain protected.
That candidate used a separate fixed limit, superseded by the shared setting above.
Six focused compaction checks pass, including both response modes and refusal before an oversized retry.
The live fixture now records the helper's own context receipt and reserves.

The same bound HTML/VBA task and `gemma4:cloud` / 32K / 4096 profile still start at
9201 actual input tokens. The helper is a separate request: 4871 tokens initially,
4928 with repair. It does not need the full Excel tool/skill context.

| Run | Provider requests / actual input / output tokens | Exact edits | Outcome |
|---|---:|---:|---|
| Compaction repair 1 | 10 / 124,704 / 6011 | 3/4 | Repair removes fences; source-kind validation rejects the claims |
| Compaction repair 2 | 5 / 51,643 / 4639 | 0/4 | Ordinary protocol exhausted before compaction |

In the first run, the repaired response promotes assistant skill selection to a
user decision and cites successful patch receipts as current observations. No checkpoint is
published; VBA remains unchanged. The second run mixes skill loads, then calls an
unloaded HTML tool. Neither run reports false completion or unknown effects.
This validates the bounded recovery boundary, not reliable live compaction or
end-to-end success. Next: address claim/source selection and skill-load recovery
using the captured failures, without weakening validation or increasing limits.

Evidence: `compaction-repair.patch`, `compaction-repair-results.json`, and
`office-compaction-repair-{1,2}/` in the existing local evaluation directory.
Metadata records `d04db7b5` plus the scoped patch and binary hashes; exact wire
profiles, task/source hashes and final bytes were independently checked. The
unrelated web-test diff is outside the Harness build. No Office/COM validation.

## Minimal Excel bootstrap — 2026-10-20

`CallableToolPack` now starts Agent/Excel with six schemas instead of twenty:
the five common bootstrap tools and `excel.inspect`. The other fourteen Excel
tools remain in the same catalog and use exact direct admission or selected skill
dependencies. `excel.analysis_reporting` now declares sheet creation, formatting
and workbook charts in addition to its existing dependencies. The changed Excel
profile explicitly invalidates incompatible prior admissions to the new core;
it does not delete history or replay calls. No new loader or registry was added.

`ModelProtocolClient` removes generic repair advice that contradicted the parser's
skill-only recovery route. The parser still supplies the exact skill/direct route;
admission, policy, confirmation, source and effect checks remain unchanged.

The same bound HTML/VBA task, sources, `gemma4:cloud` route and 32K/4096 profile
produce 9201 actual initial input tokens instead of 14,110 (34.8% less).
Two live runs complete all three HTML edits with exact preserved surrounding bytes,
then fail when the compaction helper returns fenced JSON. VBA remains unchanged.
The first run also refuses simultaneous HTML/VBA selection at 24,044 estimated
message tokens against 21,421. No unknown effect or false completion occurs.

| Run | Provider requests / actual input tokens | Exact requested edits | Outcome |
|---|---:|---:|---|
| Bound baseline | 15 / 243,975 | 0/4 | `model_blocked` |
| Minimal bootstrap 1 | 10 / 139,498 | 3/4 | Invalid compaction JSON |
| Minimal bootstrap 2 | 11 / 149,529 | 3/4 | Invalid compaction JSON |
| Helper-wording candidate | 5 / 51,559 | 0/4 | Protocol exhausted before helper |

All three new runs initially return empty content with 4096 API output tokens;
the bounded ordinary repair recovers that step. The helper-wording candidate fails
earlier on mixed skill loads and an unloaded HTML call, so it does not test the
helper. Replaying the exact failing helper request with only an explicit raw-JSON
instruction still returns fenced JSON. That ineffective wording change is removed.
These samples establish lower initial cost and partial task progress, not reliable
end-to-end success or a general total-token improvement.

Validation: 21 focused cases cover tool packs/discovery, both schema modes,
standalone Excel write/format with batched schema admission, built-in dependencies,
inspection without discovery, approval/restart, unknown effects and skill budgets.
Four existing structured-compaction checks also pass. The overflow fixture retains
an actual refusal with 32,000 padding characters after the smaller bootstrap.
Builds use isolated `eb21416c` plus scoped patches; concurrent workspace changes
are excluded. Only the two existing PDF platform warnings remain.

Evidence is in the existing local evaluation directory: `excel-bootstrap.patch`,
`excel-bootstrap-results.json`, `office-bootstrap-1/`, `office-bootstrap-2/`,
`office-bootstrap-helper-1/` and `compaction-format-replay/`. Exact wire profiles,
task/source hashes and final bytes are independently checked. Subsequent bounded
compaction recovery is recorded above; full scenario completion and live Excel
report/discovery coverage with production skill text remain open. The fake report
skill remains a stub; no Office/COM qualification is claimed.

## Bound HTML and VBA baseline — 2026-10-20

The original four-file task was rerun before changing the bootstrap tool set,
using the retained isolated binary for the `286ba967` runtime changes. Its hash
matches the successful minimal VBA runs. The fake document is correctly bound:
all 15 exact HTTP requests advertise `office_tools_available=true`. The profile
remains `gemma4:cloud`, `json_object`, 32K context, 4096 output, developer
instructions, `reasoning_effort=none`, temperature 0 and the existing run bounds.

The task fails with `model_blocked`: all three HTML files and the VBA module retain
their original bytes; zero writes or unknown effects occur. HTML skill admission
needs 24,942 message tokens against 21,375; later VBA admission needs 22,750.
There are 15 provider requests, 243,975 actual input tokens and 2250 output tokens
in about 35 seconds. Four repair requests address fenced JSON, invalid escaping
and two calls to an unselected skill-only tool. Repeated unchanged failed loads
are blocked by the no-progress guard. No successful compaction occurs and the
final answer does not claim that the files were changed.

This is a concrete baseline for the proposed smaller bootstrap, not evidence that
the complete task works. Retain the same task, sources, profile and exact-file
acceptance when comparing the change; count discovery, repair and total input.
The repair messages also contain conflicting skill/direct-schema advice, recorded
in the [risk register](RISK_REGISTER.md#live-prompt-qualification--2026-10-20).
Evidence: the existing local evaluation directory, `office-bound-baseline-1/` and
`office-bound-baseline-results.json`. The saved binary was reused without a build;
concurrent working-tree changes and real Office/COM were not exercised.

## Colored plugin SVG icons — 2026-10-20

The shared create/appearance editor defaults to a neutral color following the theme,
with explicit original SVG palette or custom single-color modes. Core stores normalized SVG instead of reconstructed paths;
viewport sizing preserves coordinate systems, fills, transforms, gradients and CSS.
The old 4 KiB / 16-shape / 1024-character restrictions and the separate UI six-path
gate are removed. Static image isolation and bounded active/external-content checks
remain. Old saved outlines migrate without deleting icons or changing their IDs.
Nine focused plugin Harness cases and Chromium color pixel/create/edit/reopen/theme
checks pass. A live `gemma4:cloud` request through the actual drawing service
returned a colored SVG with one model call in 6 seconds; it normalized and saved.
This is one provider sample, not a general reliability or Office qualification.
Windows/WebView2 delivery remains unverified.

## Minimal VBA admission and live check — 2026-10-20

The isolated `json_object` / 32768-context / 4096-output VBA case now completes:
load one skill, read `ReportModule`, apply one patch, finish. Before the change,
the controlled skill candidate required 21,946 message tokens against 21,375.
The earlier passing default-profile test reserved only 3072 output tokens.

`CapabilityCatalogService` removes duplicate tool names, policy flags and skill
size counters from the prompt discovery index. Exact IDs, summaries, selection
routes and complete callable schemas remain; explicit search retains metadata.
`BuiltInSkillProvider` leaves patch-field mechanics with the supplied tool schema
instead of repeating them in the VBA skill (10,886 → 9558 characters). Budgets,
admission, parser, dispatch and read-back guards are unchanged.

The controlled sequence passes without compaction: message estimates are 15,756,
20,703, 20,881 and 21,137. Two independent `gemma4:cloud` runs also complete with
four requests and one verified write each; exact source and call-order checks pass.
Actual API input totals are 69,333 / 69,335; output totals are 266 / 249, with no
reasoning content, protocol repair or compaction. Profile: developer instructions,
`reasoning_effort=none`, temperature 0; same route digest as the comparison below.
The final live estimate is 21,255, only 120 below the message budget: this proves
the minimal case, not general 32K task capacity.

The fixture now binds the fake document runtime key and retains exact HTTP request
bodies and budget components. Earlier combined Office runs advertised
`office_tools_available=false` because that binding was missing; their failures
are retained observations, not qualification of a correctly bound Office task.
Recheck the combined task with the corrected fixture before drawing conclusions.

Validation uses `5ee439db` plus the scoped patch in an isolated checkout because
concurrent plugin edits temporarily broke the shared Harness build. Discovery
passes 1/1, request-schema checks 2/2, skill packages 7/7. The deliberate overflow
fixture uses 12,000 padding characters; its old 8000 now fit. Evidence, exact patch,
profiles and responses are in the existing local evaluation directory under
`minimal-vba-results.json` and `vba-minimal-*`. The stale-binary attempt is explicitly
excluded. No Office/VSTO validation was run. Combined tasks, explicit workspace
verification order and live compaction/resume remain open.

## Live prompt comparison — 2026-10-20

Compared `cb1537af` (schema 50) with `b87547cb` (schema 51) in isolated checkouts.
Both use `gemma4:cloud` through Ollama 0.35.0, `json_object`, developer instructions,
`reasoning_effort=none`, temperature 0 and 4096 output tokens. The local route
manifest digest is `ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`;
cloud weights are not pinned by this digest. Office uses 32K and the existing
24-iteration/32-tool/480-second bounds; workspace uses the existing 24K/40/48/600
profile. Sources, tasks, guards and acceptance requirements are identical within
each pair. The same two-line fixture patch exposes instruction-role and reasoning
profile options; it changes neither prompts nor acceptance. Actual responses have
no reasoning content. Local Gemma 12B and Qwen 9B probes left sustained memory
warning pressure (22% and 24% free); no task was run on those profiles. Their
temporary server was stopped. The default `chat_template_kwargs` setting did not
disable thinking on these local endpoints; explicit `reasoning_effort=none` did
in the Qwen probe.

| Scenario | Before: model requests / API input tokens | After: model requests / API input tokens | Independent result |
|---|---:|---:|---|
| HTML/CSS/JS + VBA, attempt 1 | 9 / 161,460 | 4 / 65,289 | Both fail; all four requested changes absent |
| Same Office task, attempt 2 | 8 / 142,948 | 2 / 31,063 | Both fail; all four requested changes absent |
| Ordered workspace edits + compaction/resume fixture | 30 / 320,586 | 33 / 361,435 | Final bytes pass 10/10 in both; prescribed read/write/read stages pass 0/9 in both |

Both Office baseline runs refuse skill selection because the complete candidate
request exceeds its 32K budget. The new runs exhaust protocol repair on fenced
JSON, mixed/sibling skill-load calls or invalid escaping before any write. They
do not establish whether the new skill selection fits. Office never claims success.
The first Office request drops from 15,882 to 15,179 actual input tokens (4.4%),
but shorter failed runs are not a task-level efficiency improvement.

Both workspace runs reach `done` with exact final files but defer the user's
required per-file read-back until later. The event/HTTP integrity audits pass;
the independent sequence audit fails. No summary was requested, so the intended
post-checkpoint interruption and resume boundary was not exercised. The initial
workspace launch with an overlong `TMPDIR` failed before any provider request;
its environment error is retained separately from the runs above. These results
do not qualify live compaction/resume, general model quality or Windows/Office.

Evidence is retained locally in `/Users/Shared/Dev/RNAssistant-prompt-eval-2026-10-20/`
(`comparison-results.json`, profiles/digests, raw responses, exact workspace HTTP
payloads, source snapshots and canonical stores). Both pinned CLI builds pass;
Harness builds retain only the two existing PDF platform warnings. Current
priority: [model protocol/admission and explicit verification order](RISK_REGISTER.md#live-prompt-qualification--2026-10-20),
then a live scenario that actually reaches compaction/resume. Do not claim that
schema 51 improves task success from these samples.

## Prompt and skill guidance audit — 2026-10-20

Prompt schema 51 removes duplicated workflow, call/result and editing instructions
across shared defaults, built-in skills, workspace guidance and tool descriptions.
Owners remain `AgentPromptDefaults`, the existing prompt composers, skill providers
and tool catalogs. Shared contracts retain action, batching, source coverage,
unknown-effect and completion rules; skills retain domain procedures. Journal,
storage, guard and restoration mechanics no longer take space in model instructions.
Plan now treats selected skills as instructions under policy, and workspace edits
can reuse current delivered reads or verified after-source. Known VBA targets no
longer require project discovery before every read. Wire/schema shapes, runtime
guards and context limits are unchanged. Existing settings migration activates the
new defaults and archives prior stored prompt texts on save.

The three shared Agent instruction blocks decreased from 13,913 to 10,485 characters
(24.6%). Exact serialized UTF-8 request sizes from the same corrected Harness fixture:

| Selection | `json_schema`, before → after bytes | `json_object`, before → after bytes |
|---|---:|---:|
| Core | 64,876 → 61,183 | 75,325 → 71,632 |
| Task Tracking | 72,443 → 67,404 | 86,033 → 80,574 |
| VBA | 86,080 → 78,384 | 99,509 → 91,813 |
| HTML | 90,746 → 86,139 | 106,149 → 101,542 |

Requests shrink 4.3–8.9% without truncating selected instructions. These are local
serialization/inspector checks with a fake Excel adapter, not provider token counts
or live-model results. The fixture now uses native evidence and a direct optional
tool; its prior admission failure is [closed](RISK_REGISTER.md#unified-stabilization-verification-gap--2026-10-20).
The default VBA selection/patch scenario now passes at 32K. A deliberately larger
required input still refuses selection before publication and remains usable after
restoration; no budget increase or silent selection removal was introduced.

Focused checks pass: request schema/size 2/2, settings 6/6, skill packages 7/7,
compaction 4/4, Task Lists 5/5, both prompt checks, UserForm guidance and semantic VBA
contracts. CLI stabilization, question/resume, compaction/resume and skill-package
smokes pass against controlled endpoints. CLI build has no warnings/errors; Harness
retains its two existing PDF platform warnings. No full suite or Office/VSTO build
was run. Next evidence: representative live-model selection/edit/recovery behavior,
then the separate Windows/Office/WebView2 scenarios recorded below.

## Unified stabilization — 2026-10-20

Applied the user-supplied `RNAssistant_UNIFIED_STABILIZATION_PLAN_2026-10-20.md`
against `main` at `90be14b7741ad4b3087f61a8161e03854727fb7a` plus the existing dirty
workspace. Concurrent UI/model/plugin edits were retained. No version change,
release or VSTO/Office validation was performed. Four invalid multi-variable `var`
declarations in concurrently added `PluginIconSvg.cs` were mechanically changed to
`double` to unblock compilation; its feature implementation belongs to the other work.

Owners remain the existing kernel/ToolRuntime, SkillTaskService and request composers,
WorkspaceFileService/resource providers, ModelContextCompiler and typed UI projections.
There is no new store, registry, generic Office writer or model protocol version.

| Item / original symptom | Applicability before change | Current evidence / owner | Decision | Execution | Validation | Remaining boundary |
|---|---|---|---|---|---|---|
| A01 — narration copied per call | Confirmed in workspace storage | `WorkspaceConversationService.AppendAsync` / accepted-history replay | Keep narration on first sibling; rejoin batch without duplicating it | Implemented | CLI stabilization: five calls, next HTTP request and approval in fresh processes pass; UI projection inspected | Real streaming/WebView2 delivery not qualified |
| A02 — raw plugin protocol | Already resolved in current tree | Workspace presentation projects `message`; shared `app-workspace-chat.js` renders `message.text` | Preserve existing projection | No additional UI change | Static path inspection | No new browser/Windows qualification claim; concurrent UI work kept separate |
| A03 — unrelated catalog invalidates tool | Confirmed in Office and workspace guards | `SkillTaskService.CheckPublishedBindings`; both adapters | Recheck offered descriptor/policy/binding and supplying skills/dependencies | Implemented | Skill exact-dispatch checks pass: unrelated publication allowed, changed supplier refused | Real Office publication race remains host evidence |
| A04 — recovery/no-progress identity | Partially confirmed; random IDs were already excluded | `AgentProgressTracker`, persisted `ToolExecutionProgress.AdmissionRevision` | Replan recovery follows real admission changes; load also pins selected set and target dependencies | Implemented | Skill and continuity checks pass; random IDs/unrelated edits stay blocked; rebind never clears unknown | Source recovery remains target/coverage-bound, not general retry permission |
| A05 — large file tail inaccessible | Confirmed at 16000-character whole-read limit | Workspace resource schema/provider/file owner | Add paired line selectors and exact partial evidence; patch only an observed anchor | Implemented | CLI tail → refused whole replacement → two verified patches; mixed newlines/Unicode pass | Text capture still ≤1 MiB; excerpt ≤16000 characters/500 lines. A longer single line needs a further bounded view |
| A06 — known target depends on discovery | Partially resolved for document/Excel/Word/PowerPoint/HTML/attachments; VBA still enumerated | `VbaResourceProvider.ResolveSemanticTarget`, gateway | Resolve bound project/module directly; preserve target's own availability checks | Implemented | Named module beyond 1000 discovery items reads only its own source | Ambiguous name-only collections still require uniqueness; real COM identity not qualified |
| A07 — after-source only in cache | Confirmed for workspace writes | File owner → ResourceEvidence → compiler → DeliveredObservations | Deliver verified whole/partial after-source; persist exact preparation for approval | Implemented | CLI patch → after-source → patch succeeds across two approval processes; omitted reads still grant no authority | Body omission/drift requires another addressed read; no invented source after uncertain effect |
| A08 — patch base/error semantics | Partially confirmed | Workspace exact patch, VBA mutation owner; existing sequential hunk contracts | Preserve existing sequential coordinates; retain untouched line endings; separate missing/ambiguous/invalid/stale errors | Implemented | Guarded workspace, HTML and six VBA patch checks pass | No filesystem/COM cross-target transaction; UTF-16 coordinates and provider-specific normalization remain explicit |
| A09 — false unknown before effect | Confirmed for file staging/rechecks; guard exceptions also escaped | Workspace mutation journal and ToolRuntime | Move marker after preconditions, directly before possible effect; pre-dispatch exceptions remain no-effect refusals | Implemented | Injected pre-replace conflict, guard exceptions, unknown after dispatch, move/recovery and delete/restore pass | External editor can still race the final filesystem recheck; no OS compare-and-swap guarantee |
| A10 — overlapping occurrences | Confirmed in workspace/structured text; VBA already counted every start | WorkspaceFileService / StructuredTextPatchEngine | Count overlapping starts; reject overlapping replace-all | Implemented | `aaa` in `aaaa`: workspace and HTML replace/replaceAll/insertBefore/insertAfter refuse without write; VBA checks pass | Public editor semantics remain domain-specific |
| A11 — projection/budget and lost archived body | Partially confirmed: transport builder already shared; archived admission preceded folding | ModelContextCompiler / LlmMessageBuilder / ModelProtocol | Reconsider a bounded materialized body after folding only as much old mutation history as it actually needs | Implemented | Continuity 14/14, incremental budget, strict/fallback and exact request trace pass; schema-size fixture repaired in the prompt audit above | Token totals remain estimates |
| A12 — oversized input/compaction dead end | Infinite compaction not reproduced; skill selection liveness defect reproduced | ContextCompactionRuntime; SkillLoadToolHandler + both request composers | Preserve bounded no-prefix refusal; stage complete next request before publishing skill selection | Implemented for new selections | Oversized first-input/protocol-pair check, CLI compaction/resume, skills 7/7; controlled CLI refusal and cold restart pass | Already-persisted oversized selections require explicit reset/profile correction; no silent eviction or larger default budget |
| A13 — waiting/final conflation | Existing v7 action + runtime reason already distinguishes outcomes | AgentKernel, ConversationRunProjection, question handler | Preserve protocol and structured questions; no action-format migration | No additional lifecycle change | CLI Plan/tool and Agent/user question/resume scenarios pass, including invalid answers | Completed lifecycle is not proof of user goal or Office compilation |

The 32K admission regression initially reproduced the recorded P1: the load was
accepted, then the next request used about 24309 message tokens against 23422.
The fix keeps candidate selection and its preview result transient until the existing
complete compiler accepts them. The 32K case now receives an error through the old
usable surface and can continue after state restoration. A separate 64K control
still performs exactly one VBA patch. Workspace HTTP testing additionally rejects a
large procedure before activation and continues after a cold process restart. No
product budget or active-instruction truncation changed. Original evidence is retained
in the [risk entry](RISK_REGISTER.md#architecture-and-regression-review--2026-10-15).

Verification run on this macOS host (all Office adapters are fakes):

- CLI build passes with zero warnings/errors. Harness compilation passes with the
  two existing PDF CA1416 warnings.
- Harness `context continuity:` 14/14; `skill packages:` 7/7; `vba: patch` 6/6.
  Focused guarded-file, uncertain-dispatch, move/recovery, delete/restore, known-VBA,
  runtime-exception, HTML-source, compaction-pair, oversized-prompt, fallback,
  combined-retry, incremental-budget and exact-request-trace checks pass.
- `python3 -B tests/cli/stabilization_smoke.py`, `read_recovery_smoke.py` (six cases),
  `resource_selectors_smoke.py`, `mode_questions_smoke.py` (two modes),
  `compaction_resume_smoke.py` (two transports) and `skill_packages_smoke.py`
  (20 controlled HTTP requests including admission refusal/restart) pass.
- `model context: request schema modes and fallback` passes. The separate unchanged
  `model context: request schema duplication size` fixture initially failed at
  `representative extension stages from exact schema evidence`, before its HTTP
  assertions. The later prompt audit above fixes its projected evidence and invalid
  direct admission of a skill-only tool, and records successful wire-size checks.
  Full Harness, live LLM and Windows/Office/WebView2 were not run.

Supplementary static review retains the bound-document HostRuntime/DocumentAccessGate,
VBE/STA boundary, cancellation/correlation and CAS retention owners. No new P0 was
established there; real Save As/reopen, two-book switching, VBE reset and late callbacks
remain open host evidence. Scripted endpoints test runtime behavior, not model quality.

Existing selectors and evidence cover the observed patch defect, so region handles,
per-procedure versions, Code Mode and a new capability/protocol platform are deferred.
Standalone direct tools remain discoverable; skill dependencies deduplicate schemas;
references are not loaded automatically and load grants no mutation rights. Preferred
editors stay `files.patch` for an observed workspace anchor, structured HTML edits,
and the existing VBA patch/whole-source operations under their own guards.

Next qualification step: on Windows x64 + Office x64 + VS 2022, run one recorded
bound-workbook read → patch → delivered after-source → patch scenario, including
switching to another workbook while awaiting approval and a fault after possible
write. Verify target identity, one narration, explicit unknown and no automatic replay.
Use the same trace to distinguish actual source read-back from VBA compilation.

## Plugin source chat history — 2026-10-20

The plugin header now creates separate source-workspace sessions with `+` and
opens their shared history in the main Projects tree. A tree action returns the
chosen session to the plugin; selection and drafts remain scoped to the session,
and late replies cannot replace another selection. `PluginApplicationService`
only bootstraps the source chat; existing project contracts and workspace storage
own its lifecycle. The private plugin session cache and chat-control routes are
removed. Nested chat headers no longer reserve space for the navigation toggle.
All 9 focused plugin Harness cases and the Chromium workspace-chat flow pass,
including history persistence, deletion/reopening and 390/750/1360 px header
geometry. Screenshots were visually reviewed in the local mock preview;
Windows/Office/WebView2 delivery remains unverified.

## Shared workspace chat and plugin appearance — 2026-10-20

Plugin/workspace chats now reuse the Office message/Markdown/empty-state renderer,
composer geometry and input sizing, with shared workspace questions and controls.
Drafts survive failed delivery and stay scoped to their session. The rail hover
menu opens the shared name/icon editor; verified registry appearance updates keep
source, publication and live instances intact. Ordinary outline SVG shapes and
stroke attributes now pass bounded admission and normalize to registry paths;
generation keeps SVG output, a larger completion budget and one diagnostic repair.
Navigation, inspector and plugin development dividers share one splitter component;
the plugin-specific track and duplicate drag handlers are removed.
Focused plugin Harness and Chromium checks cover admission, persisted appearance,
drafts and visual parity. Real-provider generation and Windows/Office/WebView2
delivery remain open evidence; screenshots use the local mock preview.

## Shared panel controls — 2026-10-20

Navigation, chat inspector and plugin source chat use one panel-toggle component,
including the demo. The plugin checkbox/code icon and duplicate button styles and
handlers are removed. Chromium workspace-chat and product-shell checks pass for
opening/closing, keyboard focus, draft retention and desktop/narrow geometry.
Windows/Office/WebView2 delivery remains unverified.

## Plugin development preview — 2026-10-20

The development panel now uses the shared chat message and composer styling,
projects accepted v7 responses into readable text and action names, and has a
draggable preview/chat divider. Package validation runs when development opens,
when declared source files change, or when the refresh icon is pressed. A failed
check leaves the previous view visible. Preview refreshes retain a separate test
project per plugin; publication remains explicit. Focused host-neutral plugin
checks and local mock UI inspection pass. Windows/Office/WebView2 delivery remains
open evidence.

## Chat activity visual parity — 2026-10-19

Transcript stages now use demo symbols, counts, spacing, text shimmer and expanded
action details; terminal runs and reduced motion suppress animation. The activity
inspector projects existing steps and opens the exact run journal. Persistent error,
unknown-effect and approval surfaces use semantic notice styling; clipboard success
has transient feedback while failure remains dismissible. The Resources chip and
its obsolete popover path are removed; the material catalog and message links remain.
Focused Chromium and projection checks cover disclosure/focus, narrow layouts,
notifications, motion and retained effect evidence. Windows/WebView2 remains open.

## Chat transcript presentation — 2026-10-19

The web transcript now uses compact material links, demo spacing and adjacent-action
disclosures. Active actions start open; explicit final answers fold prior progress,
while interrupted runs retain their commentary. Notices split action groups and a
successful update cannot erase a failed attempt. Runtime effect warnings remain
outside the folded history. Nested expansion and keyboard focus survive refresh;
closed history releases rendered content while retaining disclosure state.
The composer no longer shrinks around its toolbar in a long conversation; its
context ring stays hidden until input usage and window capacity are known.
Focused projection, evidence, paging, resource-card and Chromium layout checks cover
this presentation change. Windows/Office/WebView2 qualification remains open.

## Connected workspace tree — 2026-10-19

The Projects scope now lists connected workspaces and their own chats. Users can
search the tree, connect an existing folder, create a workspace, switch chats,
and delete one chat or a workspace's chat history without deleting its files.
Deleting the history hides the workspace from the tree; reconnecting its folder
restores the same workspace ID with empty history.
Workspace chat operations use exact workspace IDs through the shared conversation
service; plugin selection and publication no longer own the project-chat tree.
Connected workspaces allow edits. Focused host-neutral and Chromium checks cover
separate histories and UI actions. Windows/Office/WebView2 delivery is still open.

## Plugin panel and icon editor — 2026-10-19

The plugin creation screen now shows a live name/icon preview above a compact
left-aligned form. Clicking its icon opens the shared catalog; a model call can
draw and save a validated SVG immediately. A reusable rail popover labels all
section icons, removes user plugins from the panel, and restores them through `+`
without deleting source, chat, data or publication. The shipped Data Templates
plugin uses the same removal and restoration path. Focused Chromium and
host-neutral Harness checks cover the UI and persisted visibility. Real model
output and Windows/WebView2 delivery remain open qualification evidence.

## Library groups and plugin icons — 2026-10-19

Product and demo Library trees now use the same ID-derived category labels beneath
skills/tools; callable IDs and package identities stay stable. The shared SVG
registry owns UI glyphs, with the clipped skill path corrected and the catalog
checked in Chromium for viewBox overflow. Data Templates retains its original bar
glyph. New plugin creation accepts a text icon brief or a ready icon; workspace
guidance directs the model to write path-only SVG source, and plugin admission
validates it before publication. Checked SVGs persist in a user icon library across
runtime clear; deleting one leaves source copies and publications intact. The
new-plugin form is left aligned with the shared panel toggle at the edge.
The chat composer keeps its former two-line height while its action menu follows
the demo. Mock preview simulates creation and publication. Focused Harness and
browser checks pass; actual model authoring and Windows/WebView2 remain unqualified.

## Product project views and chats — 2026-10-19

The Data Templates plugin now sits in the shared project tree/editor composition.
Its existing browser/native UI handles template and dictionary edits, Worker
processing, cancellation and result read-back/export; the tree only navigates
these views. Connected workspaces own separate workspace chat sessions through
typed bridge operations and the shared conversation service.
The five old plugin lifecycle actions live in Development by product decision.
Mock preview seeds a valid in-memory project, exercises real browser processing,
simulates chat/publication explicitly and supplies a model catalog for UI review.
The demo and product keep one navigation toggle beside the central heading,
with 10 px outer and 6 px title gaps (8 px outer on narrow screens); the duplicate
Data Templates tree icon is removed. The shared tree header now supplies project
search/open controls. Early tree selections survive iframe/preview loading, and
template rows open the real editor while the group returns to the catalog.
Focused Harness and Chromium checks pass. Windows/Office/WebView2 behavior remains
an open qualification gate.
The material Details tab remains beside Preview by product decision; it keeps
metadata, exact JSON and version history available.

## Data Templates standalone export — 2026-10-19

The complete export suite passes in Chromium after fixing stale shared-dictionary
notices, mapping selectors and cancellation before asynchronous project admission.
Read-back checks verify conflict refusal without writes, immutable dictionary
versions and agreement with downloaded drafts. XLSX processing, result export,
cancellation and staging cleanup pass. Owner: Data Templates export tests;
Windows/Office qualification remains separate.

## Plugin foundation — 2026-10-19

[Plugins](../plugins.md) and [ADR-0015](../decisions/ADR-0015-plugins.md) now own the
source workspace / immutable release / selected data-project boundary. Core
publishes through existing authority/CAS; shared application services own check,
apply, scoped file access, prepared confirmation, command cancellation and drain.
A shared OS file lease prevents runtime clear from deleting live plugin CAS/data
authority across panels/processes. WorkspaceConversationService is shared by
Framework/CLI with an optional verifier port. The rail adds plugin creation/connection and a source chat beside preview.
WebView2 transport binds individual iframe origins and hidden command Workers.

Data Templates now uses `plugin.json` across publication, exporter, inventory,
vendor gate and export checks; the old source manifest and root-DOM controllers
are removed. The same UI/core, project format and standalone export remain. Native
inspection/extraction uses plugin commands; agent `extract-to-file` publishes a
canonical file with guarded read-back. Shipped/local vendors share exact version/
hash resolution while HTML, plugin and export admission stay separate.

Focused evidence: six `plugins:` Harness checks; actual Chromium Worker/plugin UI
and Data Templates extraction/result checks; HTML vendor/gate checks; portable
project result/template tests; CLI shared conversation/request smoke and source
inclusion. The full standalone export browser suite passes after correcting stale
mapping selectors/notices and waiting for project admission before cancellation.
It checks conflict refusal, immutable dictionary revisions, draft persistence,
result export and staging cleanup. No Office/VSTO build or native qualification was performed.
Windows Frame API, picker, controller shutdown, multiple panels and full agent/provider
workflow remain explicit evidence gates. Retained compiled Tabular pins serve only
the legacy native/CLI consumers listed in MIGRATION_MAP, with no plugin fallback.
Private scratch left by a crashed process needs managed orphan cleanup; normal
discard/instance shutdown releases its data. User data is preserved on disable.

## Product shell correction — 2026-10-19

The working chat header now matches the prototype's single right-panel action;
the Arsenal popover and header clear-history button are removed. Chat deletion
remains on its tree row. The shared panel icons, nested plugin rail buttons and
aligned navigation headers now use one shell style. Navigation and right-inspector
open/close transitions use the prototype's motion timing and respect reduced
motion; the inspector abilities view reads typed metadata for the active chat.
The static preview includes binary and sequence materials as well as text
examples. Browser checks cover shell geometry and the preview; Office/WebView2
behavior remains unqualified.

## Product UI cutover — 2026-10-19

Demo and product mock preview now read one preview-only example set for the
September chat, skill, VBA, report, JSON material and project sources. The sample
chat uses a typed completed run, Markdown table and linked report card; its exact
text read follows the real viewer transport. A new mock message exercises the
real UI progress/stream/final handlers; the material catalog
offers Markdown, JSON and text. The comparison page can show demo and product
side by side. This removes fixture drift, not the remaining separate inner-screen
renderers or native Data Templates/project-chat gaps. Browser preview remains
host-neutral and is not Office qualification.

Prototype and product now use one navigation-tree renderer and one set of tree,
menu and navigation-header styles. All six product sections supply their own
items/actions to that renderer; the retired resource-list tree and Wunderbaum
assets were removed. The static product preview now loads the real product HTML
with a preview-only typed mock bridge before initialization. Its chat text intake,
skill and reference read/save/clone, material preview, and project catalog fixtures
exercise the real UI handlers; unsupported actions reject explicitly. The old direct
fixture injection was removed. Demo and product share the skill fields view and
its CSS, including ID and host. Library/VBA editor tabs and their «⋯» action
menus also share renderers and styles with the demo; the skill form's top
spacing now matches it. Existing feature handlers still own the menu actions.
Most internal screen views remain separate, and
mock preview is not Office qualification.
The project catalog now keeps its initial refresh pending while a concurrent
project operation is busy, so templates and dictionary appear without a manual
refresh. Focused browser checks cover that ordering and the existing catalog guards.

Shell parity correction: the prototype palette now wins over old CSS variables,
and all six product sections use joined 52 px navigation/main headers with a 1 px
splitter. Data Templates navigation now has the prototype's SVG rows, collapsible
groups and search; Library/VBA search moved into their navigation headers. Settings
navigation and the Model page were reshaped around the prototype. A local static
preview serves the demo at `/` and the product UI at `/web/`; native Office actions
remain unavailable there. Focused Chromium checks cover geometry, palette, tree
controls and affected settings/materials/workspace behavior. Internal screen parity,
native processing/results and project chats remain open; no Office qualification.

Correction: the shell cutover below did not complete the prototype's **internal**
screens. The current native Data Templates source screen now renders a compact
project tree, selected-file table and action card backed by its existing typed
workspace/source owners. Its project control takes one full `project.json` path;
source preparation/queue remains in an extra menu, while unsaved-selection revert
was removed by product decision. Browser checks cover project selection, source
queue/recovery and catalog navigation. Native template validation/editing,
dictionary editing, processing/results, project-scoped chats, and internal visual
parity for the other main tabs remain open. The Library tool editor now uses the
demo's sections and displays its ID as the read-only name. Tool enabled state is
in its menu; real duplication remains. The VBA editor now has module-scoped
history, with chat handoff and technical metadata removed by product decision.
Materials now open with the demo's main-pane table, using the same typed catalog
projection as the ownership tree. Search and filters sit above the table; selecting
an item opens the guarded viewer/editor with a demo-style title and action menu.
Returning to the table releases the preview and respects dirty drafts. Focused
browser checks cover both panes at 1100/420 px, versions, deletion and late guards.
Material creation format semantics and remaining old-only controls still need
individual product decisions.
These UI changes have browser/host-neutral checks but no Office qualification.
Project chats must not inherit the
current Office document's actions. Other old-only project functions require an
individual product decision before being added to the new screen. No Windows or
Office qualification is claimed for this slice.

The prototype at `403b375d` is the visual reference. One coordinated cutover
connects the new rail, navigation, palette, editors and viewers to existing
feature owners in all six product sections, without an old-UI switch. The
previously duplicated shell styles were replaced by shared prototype/product
CSS. Materials creation/import uses the existing action owners from the overview
Add entry and sidebar plus; the catalog groups by ownership;
the native Data Templates host reuses its one shared UI. Settings now separate
context/attachments. UserForm creation was removed from the UI in line with the
design decision; existing CodeOnly editing remains. Browser checks cover the
affected layouts, catalog guards, library, composer, settings and 360/1440px
navigation. Windows/Office/WebView2 behavior remains unqualified.

The Data Templates source screen now has one chooser per host. RNAssistant uses
its project-backed workspace selector and no longer loads the standalone-only
File/drop intake; standalone keeps that intake. Both show the selected project
in the header with a real project-controls action, and standalone uses the shared
green palette. Targeted browser checks cover both source paths. The populated
prototype tree and native processing UI are still separate open presentation work.
The full standalone export test still times out at its mapping-conflict assertion
on unchanged `0aacd1af`; [the check is tracked separately](RISK_REGISTER.md#data-templates-browser-mapping-check--2026-10-18).
The Office document/chat tree now uses the prototype's compact rows and keyboard
navigation over the existing chat/document projections. The four permanent Office
launch rows became one menu above the tree; the existing host-chat action and its
pending/error states remain connected. A changed chat selection reveals its row.
Targeted browser checks cover keyboard behavior and host-chat retry; Windows host
behavior remains unqualified.
Native Data Templates navigation now switches real project tabs. Its Templates tab
reads the guarded catalog and opens the exact observed head as raw JSON; Parameters
and Mapping show paged, searchable rows from the shared dictionary. The existing
raw draft editor remains separate. Focused browser checks cover read guards,
project switches, late responses, inert content and narrow navigation. Native
editing, semantic preflight, execution and results remain open; Windows/WebView2
behavior is still unqualified.

The real Data Templates empty/project-connection state differs from the populated
prototype fixture. The UI contract and owner map live in `docs/ui-design.md`.

## Independent Data Templates application and early export — 2026-10-16

The target is now explicit: Data Templates is a standalone workspace-project web
application; RNAssistant develops/uses it through optional adapters. Application
source and user data projects are distinct. This supersedes the native-parser-first
and late-M4-export sequencing below. The product host remains .NET Framework 4.8.

The shared loader markup/views/state, portable engine/Worker, examples, semantic
tests and XML parser/license now live once in `web/data-templates`. Browser and
RNAssistant composition use the same UI; native chooser/preparation stays outside
the exported project. Explicit folder export captures an allowlisted closure with
hashes, rejects outside/symlink paths and refuses existing destinations. No new
runtime/build dependency, second UI or interpreter was introduced.

The standalone viewer now connects browser source selection to a sequential XLSX
Worker queue with a JSON template picker, per-file status/error navigation, progress,
cancellation, 20-record preview and complete per-file JSON extraction export. The
provisional sink now writes acknowledged output chunks to origin-private browser
files (256 MiB per file, 1 GiB per run); explicit memory mode remains limited to
8 MiB per file. Disk failures are visible and do not trigger a silent fallback.
Before Run, a dedicated Worker compiles the selected JSON template and validates
dictionary targets/dataset metadata. Invalid templates block the queue; the view
shows source scope and rules; dedicated tabs show parameters and searchable
mappings. The standalone app now has a lazy local CodeMirror JSON/`expr` editor:
invalid JSON remains correctable, focused rule changes are checked before applying,
and pending/invalid edits block Run. The in-memory draft can be downloaded explicitly.
In a browser with a directory picker, a project card can create a new project
subfolder or open a format-2 `project.json`; explicit saves create read-back-verified
raw draft snapshots in `drafts/` without overwriting earlier edits. Invalid JSON
can be saved and reopened for correction. Separately, the browser now derives a
project-local catalog from `templates/`: multiple JSON imports, stable IDs,
immutable checked versions, archive/restore and one active editable template.
The browser project now has one shared, versioned dictionary under `dictionaries/`.
Catalog template versions contain rules without an embedded dictionary; opening or
running one materializes the selected dictionary version into the existing core
envelope. Import merges compatible parameter/unit IDs and scoped mappings, while
conflicts fail explicitly. Structured changes are Worker-checked and saved once
for all project templates. Old embedded versions remain intact and are converted
only on explicit open/save. This is not template publication. Host-neutral RNAssistant
now reads the same `templates/` and `dictionaries/` heads through typed bridge and
CLI commands with selected-project and observed-head guards. This is read-only
envelope inspection; native UI displays those reads without semantic Worker
validation. Editing and publication remain open.
Native Core/Application, bridge and CLI create, list and open the same raw
project-local snapshots through `WorkspaceFileService`, with selected-project
guards and explicit recovery of uncertain writes. The native Templates screen
now reuses the application-owned draft session and CodeMirror editor to select,
edit, save, list and reopen these snapshots. It does not claim template
validation or execution in the root UI; those still require the isolated app
Worker/host path. Host-neutral browser smoke covers the native round trip,
uncertain-write recovery target and narrow layout; Windows/WebView2 remains open.
Workbook values and mapping keys still require per-file execution. An unapplied or
rejected structured edit blocks Run. Mapping suggestions and native result
persistence remain open.
CSV/JSON and unsupported/invalid books fail visibly; no dataset is published.
The queue and bounded result sink are outside DOM
views. Exported-package browser checks cover a mixed batch, canonical 16-record
output, cancellation and report download without RNAssistant. A browser storage
check covers complete JSON beyond 8 MiB, revocation/cleanup and memory mode.
Native UI/queue regressions and package/resource checks remain focused.
Windows/Office/WebView2 and real 80 MB processing are still unqualified.
The standalone browser can now explicitly save a completed extraction in the
selected project under `results/`: bounded 1-MiB copy and SHA-256 read-back,
receipt written last, progress/cancel, a bounded saved-results list and verified
download. Incomplete folders remain visible as recovery evidence; cleanup failure
reports an exact target. This is project-local extraction persistence, not dataset
publication or native result persistence. The format-2 manifest remains unchanged.
Excel reader strategy is an explicit open decision: compare all-SheetJS, the current
selective XLSX path plus format adapters, and other bounded options on real books
before final Excel import or any 80 MB support claim.

Next: native shared-dictionary/catalog editing UI and result access, real
large-workbook/quota qualification, then
replace compiled app-code pins with per-run workspace-package capture and a
qualified isolated viewer/host adapter. The current root UI accepts installed
shipped code only. Full data-project lifecycle, other format readers and native
publication remain open. [Contract](../data-templates.md#application-project-and-data-projects),
[temporary adapters](MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16).

## Dedicated tabular host and acknowledged native output — 2026-10-16

The application pane now owns a lazy separate WebView2 executor. Its native adapter
restricts navigation/permissions/resources, closes the controller on cancellation,
source revocation or failure, and drains source I/O, resource bodies and message/sink
work. The main UI policy and .NET Framework 4.8 product target are unchanged.

The shared typed channel acknowledges each provisional batch only after native sink
acceptance. Validation and confirmed source cleanup precede success; cancellation or
cleanup failure revokes output, including a pending write/validation. Response buffers
are bounded independently of workbook size. Harness checks and real Chrome/C# channel
checks pass, including the 16-record oracle, sink failure and stale acknowledgement.

The actual WebView2 adapter has NOT been built/qualified on Windows here. Interception,
hidden-controller lifecycle, startup cancellation/drain, sequential response streams
and hard-stop latency remain target-host evidence. No Office/VSTO validation was run.
Next: exact template/dictionary/dataset job pins and a durable provisional sink, then
queue parser dispatch and UI processing progress through the existing workspace owner.
The loader still performs preparation only. [Contract](../data-templates.md#dedicated-native-executor).

## Pinned runtime resources and native-to-Worker transport — 2026-10-16

The shared application now captures 17 approved runtime assets against a generated
lock and compiled SHA-256 pin. One resource session binds those immutable modules
to a prepared source, serves bounded binary ranges with sequence receipts and
revokes access on cancel/deadline/disposal. Its dedicated entry uses the same JS
engine/Worker and waits for native cleanup; native revocation after parsing aborts
provisional output. The main UI bridge/CSP and product runtime remain unchanged.

Validation: focused Harness checks cover package drift/refusals, native scope and
deadline; real Chrome tests use the same C# handler through a Harness-only loopback
adapter. The exact 16-record oracle, CSP, cancellation, sink failure and native
revocation pass, including reading retained bytes after live source replacement.
No Windows/Office/VSTO validation was run and no production server was introduced.

Next: a dedicated WebView2 executor adapter with host navigation/permission/resource
interception and hard-stop lifecycle, then typed job/result integration and queue
parser dispatch. Template/dictionary/dataset pins, publication and representative
large-file performance remain open. [Contract](../data-templates.md#native-resource-session).

## Owned native source ranges and protected seek staging — 2026-10-16

`WorkspaceSourceReader` replaces the unowned prepared-stream entry point. It admits
the queue's exact pin, checks the selected project around opening and holds source
selection, the shared heavy-reader slot and maintenance activity through I/O drain.
Verification and bounded serial ranges run off the caller thread; cancellation
revokes access immediately and discards late bytes. Application close drains reads
as well as preparation. No bridge/body transport or parser was added.

Sequential CAS encodings now support explicit seek staging in 256-KiB protected
blocks using the existing storage owner. Raw CAS needs no staging. Temporary
handles preserve encryption/authentication, verify block hashes and delete on close;
staging has separate cancellable progress. No second durable source store was added.

Validation: focused source-reader, source-stream, queue and bridge-lifetime Harness
checks pass, covering encoding variants, range bounds, project drift, cancellation,
late I/O and cleanup. Framework 4.8/C# 7.3 remain unchanged; no Office/VSTO validation
was run. Next: pinned isolated native execution and binary transport into the JS
byte capability, then queue parser dispatch. Representative large-workbook and
staging disk/RAM/throughput qualification remain open.
[Contract](../data-templates.md#bounded-byte-source).

## Bounded byte-source input for the portable XLSX executor — 2026-10-16

The ZIP/XML reader no longer depends on Blob methods. One owned byte-source contract
serves selected Files and pull-based sources; Worker protocol v5 requests exact
bounded binary ranges with one read in flight. Source revocation/cancel/deadline
terminates work, failed cleanup aborts staging, and provisional results cannot
overtake pending input. Old Worker protocols fail explicitly. No native bridge,
source store, C# interpreter or Office runtime change was introduced.

Validation: focused byte-source, XLSX and real-Worker checks cover range/sequence
guards, provider failures, preflight, cancellation, late bytes and disposal. The
16-record oracle also passes against a virtual 80-MiB ZIP envelope with less than
1 MiB transferred; this is sparse access evidence, not a large-workbook benchmark.
Existing selected-File/offline execution remains covered. No build or Office/VSTO
validation was run. Next: application-owned exact-stream/seek transport and pinned
isolated native execution, then queue parser dispatch. [Contract](../data-templates.md#bounded-byte-source).

## Persistent project source declarations — 2026-10-16

The loader and CLI now save source composition in a closed format-2 `project.json`
through the existing file owner. Stable source IDs, exact manifest/selection guards,
verified read-back and explicit uncertain-write inspection keep declarations separate
from authority and queue outcomes. Opening restores the selection without replaying
work. The UI adds bounded selected-file navigation, save/revert and visible temporary
or read-only state; unfinished queues refuse source saves. Metadata-only format 1 is
rejected without rewriting it; the explicit transition is documented in the contract.

Validation: six project-storage and ten application Harness checks, seventeen browser
groups, CLI build and cross-process save/open/read-only preparation smoke pass.
Expanded composition was inspected at 360/1160 pixels in light/dark themes. No
Office/VSTO validation was run. Next: native parser dispatch from saved declarations
through the existing queue, with exact source/template/module pins; durable batch
requests/common execution events and representative 80 MB Excel qualification remain
open. [Contract](../data-templates.md#workspace-project-files).

## Workspace preparation queue in the loader — 2026-10-16

The loader now selects explicit workspace files through the shared directory owner
and starts/controls the existing preparation queue. Bounded metadata pages, name
search, file/batch budgets, pause/cancel/retry and filter-independent current-file
progress stay separate from parsing. A lost reply recovers by reading native state;
project replacement is disabled while work is unfinished. Browser File handles are
never inferred to be native paths. Presentation/controller modules reuse loader tokens.

Validation: nine focused application checks and the existing bounded-directory check
pass; four source-queue browser groups, seven loader groups and four workspace groups
pass in Chrome. Visual checks cover 360/740/1160-pixel light/dark layouts. No Office/VSTO
validation was run. Durable source/batch requests, parser dispatch, CSV/JSON and other
Excel readers, native execution/CSP and representative 80 MB Excel qualification
remain open. [Contract](../data-templates.md#workspace-source-preparation-queue).

## Data Templates universal language/core foundation — 2026-10-16

The original plan's axis independence is now an implemented invariant. Language 2
uses explicit typed roles over matrix coordinates, named record fields and nested
JSON paths; one immutable-plan executor emits canonical observations with source,
expression/context and mapping evidence. Exact scale/round and JSON numeric tokens,
pull-input pages and explicit sink completion/abort preserve bounded execution.
The orientation-bound executor is removed; shipped consumers/examples switch together.
Existing XLSX, Worker and native file/queue owners remain in place.

Validation: targeted core, matrix, universal, XLSX and browser scripts pass (23 groups,
including real Chrome Worker/offline execution). Independent oracles cover both matrix
orientations, record/JSON models and real XLSX lineage. No build or Office/VSTO
validation was run. CSV and streaming JSON file readers, XLSB/other Excel families,
queue parser dispatch, exact native module pins/CSP, publication and representative
80 MB Windows/Office qualification remain open. This completes part of the original
scope, not all product v1. [Contract, reconciliation and evidence table](../data-templates.md#language-and-execution-core).

## Workspace source preparation queue — 2026-10-16

The shared application now prepares bounded batches through typed bridge/CLI
commands, with one streamed reader at a time, exact project/source pins, byte
progress, pause, cancellation, explicit retry and 50-item pages. The queue survives
Office controller changes; panel disposal cancels and drains it. Runtime clearing
and CAS maintenance cannot overlap active preparation. No parser, second durable
store, model run or Office runtime migration was introduced.

Validation: eight focused `workspace application:` checks, four `tabular project:`
checks, the CLI build and two process-level preparation smokes pass. Evidence covers
mixed outcomes, read-only snapshots, budgets, drift, cancellation and bridge drain.
Windows/Office was not run. Prepared bytes are not imported data: loader association
and queue presentation, parser dispatch, durable batch requests/execution events,
native execution/CSP and representative 80 MB Excel qualification remain open.
[Contract and next boundary](../data-templates.md#workspace-source-preparation-queue).

## Workspace large-source file service — 2026-10-16

The existing file owner now captures large sources through streamed CAS retention
and published exact authority, independently of the one-MiB whole-view budget.
Verified read-only streams, typed per-pass byte progress and cancellation cover
capture, file-lease wait, verification and consumption. No second source store or
runtime was introduced. Office remains C# 7.3 / .NET Framework 4.8.

Validation: 14 focused workspace file groups and three CAS groups pass, including
an actual 80-MiB source, bounded allocations, historical reads, corruption,
cancellation, compression and encryption. Windows/Office was not run. This is
storage evidence, not Excel throughput or UI qualification. Next: bind these
source pins to the shared application queue and executor; XLSB/other Excel families
are accepted reader scope in [Data Templates](../data-templates.md), still open
alongside CSV/JSON and native execution/CSP. [File contract](../resource-fabric.md).

## Data Templates bounded XLSX reader — 2026-10-16

The separate portable probe now reads a real selected XLSX in a dedicated Worker
and feeds the existing typed matrix engine. Selective ZIP/streamed XML reading
retains a bounded rectangle and only needed shared strings; parsed parts are
size/CRC checked. Versioned cells preserve exact numeric lexemes and stored
formula-cache evidence. Byte/record progress, cancellation and acknowledged
batches use the existing executor, with no UI body reads or cooperative fallback.

Validation: six focused XLSX groups, existing core/matrix groups and four browser
groups pass; pinned vendor integrity/license checks pass. Evidence includes a
hand-authored canonical oracle, 30,000 unselected rows, selective reads and an
independent SheetJS writer. No C# build or Windows/Office validation was run.
This does not qualify representative 80 MB books or measured RAM/latency. The
main loader's application queue/source association, CSV/JSON readers, native
execution/CSP/module pins and publication remain open. Next: connect the accepted
reader through the shared workspace application lifetime and bounded queue.
[Reader contract and limits](../data-templates.md#bounded-xlsx-reader).

## Data Templates M2 matrix-language foundation — 2026-10-16

The portable core now compiles a closed JSON matrix template through typed
expressions into an immutable bounded plan, binds scoped parameter/unit mappings
and emits canonical observations with source/rule evidence. The worked fixture
produces 16 records across disjoint columns, two monthly years and separate annual
totals. Decimal text and missing reasons remain explicit; no implicit aggregation,
unit conversion or guessed mapping is performed.

Worker protocol v2 adds acknowledged provisional batches to the existing executor;
the producer waits for its consumer and cancellation rejects the run without a
completed result. The separate offline probe runs the same core and downloads a
clearly bounded report. The main loader and project publication are not connected.

Validation: three existing core groups, four matrix groups and three browser groups
pass, including a hand-authored semantic oracle, compiler refusals, batch failures,
Worker cancellation and offline parity. No C# build or Windows/Office validation
was run. M0/native capture and full M1–M3 remain incomplete; real XLSX/CSV/JSON
readers, processing queue, module pins and publication are the next boundaries.
[Canonical contract and supported profile](../data-templates.md#language-and-execution-core).

## Data Templates source-selection UI — 2026-10-16

The workspace tab now has a responsive Data Templates viewer with collapsible
project settings, file/folder selection and drag-and-drop, paged inclusion lists
with search, filters/sorting, and format help. XLSX, CSV and JSON share one selection workflow; the
[domain contract](../data-templates.md#input-formats-and-adapter-boundary) defines
their distinct future readers and source locations. Templates, parameters,
mapping and result views identify their unimplemented operations explicitly.

The whole loader now separates operation/selection ownership, view rendering,
shared progress presentation and visual tokens. Enumeration progress remains
visible across sections; remounting presentation preserves the job. The
[large-file and queue contract](../data-templates.md#large-files-and-processing-queue--planned)
requires bounded application-owned processing and off-UI parsing from M2–M3,
before M5 consolidation. It reuses workspace lifetime/file owners.

Seven focused loader browser groups pass: real file/folder inputs, directory batch
draining, cancel/context-change guards, atomic limits/failures, progress/remount,
paged filtering/selection and all views in narrow/wide light/dark layouts. An
80 MiB sparse selection fixture is never read; this is not an XLSX benchmark.
Existing workspace UI checks pass. No C# build or Office/VSTO validation was run.
Source contents are not yet parsed or saved to the project; persisted source
association, the real processing queue/readers and publication remain open.

## Shared workspace project application and UI — 2026-10-16

The Office panel and CLI now use one compatible application source module for
workspace selection, project create/open and explicit uncertain-file inspection.
Selections are independent of Office controllers and pinned by an ephemeral
binding; stale requests cannot inherit another selection's write permission.
The old Runtime-only project facade is removed. Common storage/CAS/journal owners
and Office Framework 4.8 are preserved; packaging is tracked in MIGRATION_MAP.

Validation: four focused `workspace application:` Harness checks, four browser
UI groups, bridge-bootstrap regression and process-level shared CLI smoke pass;
CLI builds. The narrow metadata view does not complete M7:
workspace-owned UI chats, file views/reconnect and real Windows/Office validation
remain open. Live Excel capture, template publication and export remain separate
Data Templates gates. [Current contract](../data-templates.md#shared-application-and-ui).
Runtime clear is exclusive with bridge workspace requests and revokes the selected
root, preventing the same panel from clearing its journal during file read-back.

Next Data Templates scope: [loader/project UI design](../data-templates.md#planned-loader-and-project-ui)
starts in M1, with drag-and-drop/file/folder selection, parameter and mapping views,
and versioned format help. The first usable workflow develops through M1–M3;
M5 retains advanced batch/consolidation work. This is accepted scope, not shipped UI.

## Data Templates shared manifest foundation — 2026-10-16

M1 now has a typed Core manifest/storage facet over `WorkspaceFileService`, with
create/open, exact workspace/file binding and drift revalidation. The existing
Runtime/CLI consumes it; Core Framework 4.8 includes the same code. No new root
registry, file writer, CAS, journal or Office dependency on .NET 8 was introduced.

Validation: four focused `tabular project:` Harness checks and a process-level
CLI smoke pass (create/reopen, read-only/overwrite refusal and shared discovery).
CLI builds successfully. Office UI, live Excel capture, draft/test/publication
and export remain open; no Windows/Office/VSTO check was run.
[Current contract](../data-templates.md#workspace-project-files).

## Data Templates portable proof — 2026-10-16

M0 has started with a separate bounded JS capture/selector core, immutable typed
cells, explicit Worker/cooperative execution and an offline file-picker probe.
Both focused Web scripts pass, including real Chromium Worker cancellation and
deadline, viewer-independent lifetime, reload guards and file:// result parity.
No C# build, Harness, Windows/Office/WebView2 or real-model check was run.

This is not a completed M0 or M1–M3 implementation: Office file-root composition,
live capture, module hash pins, publication and project export remain open.
[Current contract](../data-templates.md) and
[integration gates](RISK_REGISTER.md#data-templates-integration-gates--2026-10-16)
define the next slice. Existing stabilization priorities below are unchanged.

Workspace overlap review: Data Templates consumes ADR-0013's generic workspace
identity/file ownership. The next application slice belongs to the shared
workspace boundary (including the net8.0/Framework 4.8 composition gap); Excel is
a separately bound source. The [ownership and sequence](../data-templates.md#relationship-to-workspace-first)
replace the earlier Office-session-first project wording. This review preceded
the shared manifest implementation recorded above.

## Architecture review and metadata conflict correction — 2026-10-15

Review of `a05ffe96` found and fixed a Core stale-write defect: a refused scalar
metadata change adopted another writer's revision, allowing a later stale commit
to overwrite settings and remove a newly committed artifact from the projection.
Refusal now keeps the old cursor; all three setters and explicit reload are covered.
Focused metadata/SQLite/stale-save/relocation checks pass (1/5/1/6).

Next priority: skill selection can persist successfully at 32K while the complete
next request cannot fit; cold restart retains the selection and blocks even a
model request to remove it. Unify candidate admission with the actual compiler
before publication. Chat relocation crash consistency remains open. Proposed
reductions in commit duplication, physical relocation and uncorrelated inspection
are scoped with owners in [BACKLOG](BACKLOG.md#structural-debt).

Current checks are not all green: `kernel:` 59/59, `agent:` 52/56,
`kernel replay:` 9/10, `architecture:` 4/5; Common catalog inventory still fails.
Four selected Web scripts pass, four fail existing fixture assumptions.
[Review evidence and exact failures](RISK_REGISTER.md#architecture-and-regression-review--2026-10-15)
supersede earlier passing counts for the current tree. No full-suite, live-model,
CLI smoke or Windows/Office/VSTO qualification was run for this review.

## Skill and restart context continuity — 2026-10-15

Core selection/pairing, Office recovery and CLI context preparation now preserve
selected skills across user turns and completed tool exchanges across interrupted
runs. Existing `run.updated` stores copied selection pins; every request validates
current publications and policy. Explicit removal/reset still clears selection.
CLI revalidates retained file sources across turns without inheriting write guards;
batch projections retain completed siblings and reconstruct kernel continuation.
Prompt schema 48 documents the new selection lifetime.

Validation: 46 focused Harness checks, MockDemo resource workflow and four CLI
scripts (including two compaction/resume cases) pass. The original run-wide
context-loss P1 is [closed host-neutral](RISK_REGISTER.md#restart-context-continuity-review--2026-10-15).
No full-suite, real-model or Windows/Office/VSTO qualification was run.

## Confirmation input and inbox receipts — 2026-10-15

Three reproduced findings are closed host-neutral. Confirmation and model
preparation share actual-user selection; pending attachment copies are removed.
Initial/updated analysis and replay changes are named in existing commits, so
restart retains the new analysis without repeating the helper. Bounded terminal
input receipts and one Web revision reducer prevent removed echoes from returning
after late acknowledgements; queue-only edits/removals no longer reload history.

Validation: 10 new controller role/routing/restart/steer cases, existing unified
controller and inbox scenarios, focused Harness inbox/commit/replay/context checks
and four Web scripts pass. Details are in
[RISK_REGISTER](RISK_REGISTER.md#confirmation-input-ownership-and-persistence-review--2026-10-15).
No full-suite, real-model or Windows/Office/WebView2 qualification was run.

## Chat startup integrity and regression fixtures — 2026-10-15

Owners: Core chat persistence and host-neutral test fixtures. The eight tracked
Harness failures and the Web narration failure are closed. All event consumers
share validation; damaged SQLite metadata/schema uses one bounded rebuild from
canonical events. Startup, catalog and history reads preserve the journal, and
one corrupt chat cannot stop access to healthy chats.

Regression fixtures now use actual skill selection, complete dependencies,
explicit source budgets and typed execution boundaries. Additional confirmation,
causal-trace, startup and prompt-fixture failures found during this pass are fixed.
Validation: `storage:` 75/75, `agent:` 54/54, `kernel replay:` 10/10,
`protocol context:` 6/6, `causal trace:` 6/6, `settings:` 6/6 and `chat sessions:`
17/17; `live-run-projection.test.js` passes. The metadata test includes 40 damage
scenarios plus a mixed healthy/corrupt startup catalog.

This closes the [baseline-failure incident](RISK_REGISTER.md#baseline-test-failures-during-architecture-follow-through--2026-10-15),
not the other open risks. No full-suite, real-model or Windows/Office/VSTO
qualification was run.

## Delivered observations and bridge initialization — 2026-10-15

Three review findings are fixed host-neutral. Core publishes final delivered
observations once; Office and CLI consume them without interpreting message roles.
Diagnostics no longer suppress archived source bodies, and an included source
retains the same-run CLI read guard after automatic compaction. Omitted or stale
bodies still grant no write authority.

Bridge bootstrap v2 separates transport authority from application state, fixing
token loss on cross-host navigation. Init/navigation share one typed chat DTO and
controller/Web projection, including inbox. The earlier explicit persistence
cutover remains in place; no compatibility path was added.

Validation: Harness 55/55 focused checks, five controlled CLI cases, seven Web
scripts and MockDemo's four profiles plus failed-turn persistence pass. A separate
Web narration test fails identically on baseline `fcba1ce1`; see
[RISK_REGISTER](RISK_REGISTER.md#baseline-test-failures-during-architecture-follow-through--2026-10-15).
No Windows/Office/VSTO or real-model qualification was run.

## Projection, input and persistence simplification — 2026-10-15

The four follow-ups are implemented in priority order 1 → 3 → 2 → 4:

- **Office authoring owners:** closed model envelopes replace recursive field
  removal. User JSON schemas, required fields and literal source text survive.
- **Office bridge / Web:** the unused `sendChat` controller/browser route is
  removed. UI, mock models and bridge fixtures use `submitChatInput` receipts.
- **Core input admission:** Office and CLI share request fingerprints, conflict
  checks and exact question-answer validation, including CAS questions after
  restart. Typed answers preserve the composer and reject stale question sets;
  matching receipts do not replay input.
- **Core persistence / application owners:** explicit message writes/removals
  replace production `Save(session)` and message-shape event classification.
  User cancellation explicitly records its protocol result; UI status cannot
  manufacture an execution outcome. Metadata/artifact deltas and the existing
  event/CAS/revision barrier remain; this is not an O(1) persistence claim.

Focused projection, inbox, bridge, chat/history, resource-lifecycle, Web and CLI
request/question checks pass. MockDemo covers all four scripted profiles,
failed-turn resource persistence, live artifact delivery, typed answers/restart,
shared-controller inbox and resource workflows. Broader storage/agent/kernel
filters have eight failures reproduced on isolated baseline `4d716414`; they are
tracked with owners in [RISK_REGISTER](RISK_REGISTER.md#baseline-test-failures-during-architecture-follow-through--2026-10-15).
No Windows/Office/VSTO or real-model qualification was run.

## Resource descriptors, retained reads and context selection unified — 2026-10-15

The remaining CAS/resource review work is complete host-neutral. Office prompt
index and discovery share artifact metadata descriptors, fixing authored-file
`attachment:`/`file:` target mismatch and removing duplicate representation logic.
Retained whole/line reads reuse one verified body per operation; excerpt evidence
and publication/revision checks remain exact, without a persistent cache.

Core `ContextWorkingSet` supplies candidates only. The compiler owns active/archive
currentness, deduplication, lazy hydration and projected body budgeting. Office
request/preview and CLI consume that path; separate selection and body injection
are removed. Unresolved operation facts stay mandatory.

Harness and CLI builds pass, along with 25 focused Harness checks and both
controlled CLI compaction/resume cases. No real-model or Windows/Office/VSTO
qualification was run. Details:
[RISK_REGISTER](RISK_REGISTER.md#cas-and-resource-fabric-review--2026-10-14).

## Resource journal names and archived operation admission — 2026-10-15

Three follow-up findings are fixed host-neutral. Core resource authority validates
the complete file inventory and blocks GC on unsupported names. Core working-set
selection retains accepted call/result pairs, restoring semantic operation targets
after compaction. The compiler bounds optional materialization before CAS reads;
rejected receipts consume the input allowance and unresolved effects stay mandatory.

The rebuilt Harness passes `storage: CAS` 10/10 and `context continuity:` 13/13.
Coverage includes filename case/suffix/extension changes, published/unpublished
roots, supported sibling caches, accepted-call CAS/restart and rejected archive
candidates. No Office/VSTO qualification was run. Details:
[RISK_REGISTER](RISK_REGISTER.md#resource-journal-names-and-archived-operation-admission--2026-10-15).

## CAS re-review: prior defects closed, authored-file prompt target open — 2026-10-15

The three original CAS/resource findings and authored UTF-16 reopening failure
are fixed host-neutral; 23 focused checks pass on the existing `cc48a6dc` assembly.
Re-review reproduced an Office prompt-index mismatch: an authored file is advertised
as `attachment: ...`, while discovery/resolution uses `file: ...`. Owner: Office
artifact descriptor construction shared by the prompt index and resource provider.
The descriptor mismatch and both architectural follow-ups are now fixed by the
implementation above. These earlier no-build results did not cover the concurrent
hardening; its later rebuilt evidence is recorded separately. Review evidence is in
[RISK_REGISTER](RISK_REGISTER.md#cas-and-resource-fabric-review--2026-10-14).

## Archived operation receipts retain exact diagnostics — 2026-10-15

Owner: Core model-context compiler. Working-set restoration now supplies original
operation candidates and priority, removing early projection of CAS headers. Active
and archived results share bounded hydration and model projection; the compiler
materializes and budgets receipts afterward. Office conversation, preview and
Runtime consumers pass the candidate set. Inspector provenance and hydration
counts reflect the original payload; unresolved outcomes remain mandatory.

Eleven context-continuity checks pass, including inline/CAS parity after checkpoint,
save and restart for three result roles, Core/Office projection and Error/Unknown.
Missing selected payloads fail explicitly; omitted optional receipts do not read
CAS. Harness and CLI host-neutral builds pass; two controlled CLI compaction/resume
cases preserve the original run, external-drift guards and native call/result
pairs. No real-model or Office/VSTO validation was run. All three reproduced defects from
the [CAS/resource review](RISK_REGISTER.md#cas-and-resource-fabric-review--2026-10-14)
are fixed host-neutral. Retained-reader page reconstruction remains a separate
architectural follow-up; Windows/Office qualification remains open.

## Removed resources remain valid historical evidence — 2026-10-15

Owner: Core evidence reducer. Only `Known` resource/dependency heads supply a
current revision. `Removed` now yields `Unavailable` instead of throwing during
compilation or working-set restoration. Immutable-snapshot dependencies and
retained historical bodies keep their existing meaning and storage.

Twelve focused frozen-evidence, context-continuity and Library removal/restart
checks passed host-neutral for this change; see the separate receipt finding in
[RISK_REGISTER](RISK_REGISTER.md#cas-and-resource-fabric-review--2026-10-14).

## CAS collection rejects misplaced resource journals — 2026-10-15

Owner: Core resource authority and CAS maintenance. One safe recursive inventory
replaces the two top-level source enumerations. Misplaced published or unpublished
journals and linked paths make reachability incomplete before companion journals
are opened. Collection then deletes nothing and does not rewrite or relocate sources.
The reproduced data-loss risk is closed host-neutral; see the separate evidence
and receipt findings in [RISK_REGISTER](RISK_REGISTER.md#cas-and-resource-fabric-review--2026-10-14).

Nine focused CAS checks and unpublished-root retention pass on the host-neutral
Harness; the new misplaced-journal regression failed before the fix.
No Office/VSTO validation or product-version change is involved.

## Architectural simplification — 2026-10-14

Implemented host-neutral on 2026-10-15 within the user-authorized scope. Existing
events/CAS remain authoritative. No new store, generic Office executor,
product-version change or automatic effect replay was introduced.

| Slice | Owner and concrete change | Removed path / verification | State |
|---|---|---|---|
| A1 | Core artifact storage: authored bodies stay lazy and use their encoding-aware owner | MIME-driven authored-file hydration and re-encoding; UTF-16 reopen/read/save/export and missing-body isolation pass | Verified host-neutral |
| A2 | Core persistence / Office and Runtime adapters: explicit runtime commits | Runtime message-shape classification and full message-body diff; final responses, paired batches, paused views, released media, durable-before-dispatch and append failure checked | Runtime and remaining conversation writers cut over; follow-up above |
| A3 | Office bridge / Web: versioned catalog, shared cancellation and idle waits; 128 operations after `sendChat` removal | Separate cancellation list and init/navigation/progress casing aliases; missing/late replies, mismatch and no replay checked | Transport cutover verified; embedded projections remain tracked |
| A4 | Office domain boundary: shared immutable `DomainOutcome` and one ToolRuntime conversion | Nine outcome/status/effect definitions and nine handler converters removed; 36 domain/read-back/dispatch checks pass | Verified host-neutral |

Canonical contracts: [Session events](../session-events.md),
[Artifact Library](../artifact-library.md), [Architecture](../architecture.md#bridge-operation-contract)
and [Conversation protocol](../conversation-protocol.md#tool-result).
The general `Save` writer is now removed. Remaining embedded projection adapters
have owners, consumers and removal gates in [MIGRATION_MAP](MIGRATION_MAP.md#runtime-commit-boundary--2026-10-15).
This does not claim constant-time saves or complete removal of all bridge adapters.

Harness, CLI and MockDemo host-neutral builds pass. The real-controller fake-host
unified-agent scenario passes approval/restart and policy changes. Focused checks include 37 bridge
cases, 36 domain cases, 6 permission cases, paired read batches, native question
continuation, 3 media routes, authored UTF-16 and 7 shared-HTML cases. Web bootstrap,
chat synchronization, per-chat requests, Office chat navigation and Library
projection suites pass. Controlled CLI turn decisions pass for both response modes
and user/tool result roles. Seven kernel-replay cases pass; three confirmation
fixtures fail identically on the pre-change HEAD and this implementation. Those
fixtures and the older application-request smoke's missing approval policy are
tracked in [BACKLOG](BACKLOG.md#replay-confirmation-fixtures--2026-10-15).
Windows/Office/WebView2 delivery remains open; no Office/VSTO validation was run.

## VBA recovery, question delivery and step presentation — 2026-10-14

Owners: Office capability discovery/context, Core provider transport, Web run
presentation. Skill-only capability reads no longer produce an unconditional
`skill_required` error that remains blocked after selection. They return bounded
selection guidance; the selected request remains the only source of callable
schemas. A scripted UniLoader discovery/read/load/schema-read/patch sequence now
applies the exact correction once without relaxing dispatch or mutation guards.
Bound VBA context also directs source discovery before asking for module code.
Whole-write conflicts for existing modules now require an exact complete resource
read even when the error includes diagnostic source text. That read releases the cached failure. The
production conversation test covers external drift, refused write, fresh read and
an explicit identical retry with exactly one mutation dispatch.

Questions now close their call/result exchange before awaiting an answer; native
and user-role histories continue after reload. The Web interaction reads the exact
typed question set through the existing result-presentation service, including CAS
bodies externalized above 8,192 characters. Missing bodies are visible and retryable.

Live repeated progress comments share one paragraph and a repeat count. Completed
comments fold with their actions; expanded history retains original step pairing.
Unassessed `done` no longer hides failed-attempt warnings or labels the
task fulfilled. VBA diff previews distinguish unavailable exact text from action
verification. Structured JSON/SSE provider errors fail the model step even after
answer deltas, without accepting partial success or format-repair retry.

Harness build and focused discovery, recovery, question and source-continuity
checks pass; 27 Web checks and focused Chromium presentation suites pass.
No Office/VSTO validation or real-model run was performed. Attribution
of the remaining SceneBuilder failures and autonomous Qwen source discovery stays
open in [the incident record](RISK_REGISTER.md#vba-patching-and-completion-photos--2026-10-14).

## Message attachments resolve independently of catalog completeness — 2026-10-14

Owners: Office attachment resource binding and Gateway intent resolution. Committed
user attachments expose message-scoped semantic targets derived from durable
message metadata and exact resource links. Read/search/outline resolve only the
selected original; unrelated unavailable metadata no longer causes
`resource_scope_incomplete` for that target. Same-name uploads stay distinct after
compaction/restart. Ownership, publication, ambiguity, removal and source-integrity
guards remain in place; global discovery still reports incomplete coverage.

Nine focused host-neutral checks pass: the native attachment path and refusal
cases, >10 MiB initial prompt/navigation, attachment publication ordering, bounded
prompt indexes and generic partial-discovery guards. The photographed incident's
exact unavailable record remains unconfirmed. No Windows/Office or live-model
qualification was performed.

## Large text attachments no longer block the initial request — 2026-10-14

Owner: Core model-context compiler. Current text/code and extracted Office uploads
now enter requests through resource discovery and explicit reads. Their full
extraction no longer consumes admission budget before the first tool call.
Original attachment metadata/CAS and current media routing remain intact.

Six focused host-neutral checks pass. The >10 MiB one-line JS fixture now covers
the production first-request/transport path at 32K context, preview, frozen repair,
retained originals after restart, late search and exact reconstruction of every
part. Small text, historical attachments and current media/helper delivery pass.
No live-model split or Windows/Office qualification was run. Large block extraction
for the reported vendor-splitting task remains a bounded follow-up in
[BACKLOG](BACKLOG.md#large-source-mutation-and-provider-parity--2026-10-14).

## Explicit workspace criteria checked before completion — 2026-10-14

Owner: workspace acceptance / Core kernel. A valid `done` is now checked against
the task's frozen file/count/browser requirements before becoming accepted
history. Unmet conditions produce durable running evidence and one fresh request
inside the existing kernel and original budgets. The same assessment refreshes
files after model wait and final termination; no prose judge, task-list gate,
new service or store is introduced. Generic Office completion is unchanged.

CLI and Harness builds pass. All 58 kernel checks pass, including completion
rejection, exhaustion, cancellation/append failures and unknown-effect non-replay.
Twenty-one focused CLI cases pass: final reads/late edits, missing files and
write counts, frozen repair, blocked/needs_input/no-criteria controls, browser
repair/reverification and recovery guards, binary dependency drift, presentation,
question continuation and compaction/process restart with original accounting.
Older Task List, Plan and skills suites stop earlier on stale fixture admission
assumptions; their separate follow-up is in [BACKLOG](BACKLOG.md#cli-smoke-fixtures-lag-current-admission-contracts--2026-10-14).

A fresh protocol probe and full CSV reference run on Gemma with thinking off pass.
CSV: 7 iterations, 8 tool steps, 3/3 current file reads, 3 verified writes and
24/24 browser assertions; 7 HTTP requests, no format repairs. This run never
attempted premature completion, so it demonstrates compatibility, not a measured
improvement in model behavior. Controlled HTTP cases prove the new continuation
boundary. Semantic completion outside explicit criteria and the previously
reproduced provider JSON suffix problem remain open.

Evidence: isolated `3cb0b69f` plus this change in
`/tmp/rna-completion-gate-20261014-8e_qd7c5/`; exact cloud profile, request/event/CAS
trace and checks retained. Ollama 0.35.0, `gemma4:cloud` / remote `gemma4:31b`,
unchanged digest; 32K/4096, temperature 0.2, top-p 1, developer instructions,
user results, `json_object`, requested reasoning effort `none`. No Office validation.

## Authored HTML source viewer repaired — 2026-10-14

Owner: Office Artifact Library/viewer and Web artifact detail. Created HTML files
were classified as immutable uploads, causing source reads to require a nonexistent
message attachment. Retained `fileEncoding` now identifies versioned authored
documents; their lineage shares one head, and HTML opens as inert exact source.
Upload evidence checks and explicit workspace import remain in place.

Eight focused host-neutral checks and four focused Web suites pass, including created
and patched HTML history/restart, copied UTF-16 source paging and upload import.
Windows/Office/WebView2 qualification remains open. A separate UTF-16 chat-reopen
failure is recorded in [RISK_REGISTER](RISK_REGISTER.md#authored-utf-16-file-blocks-chat-reopening--2026-10-14);
an existing stale cache-key failure in `plan-document.test.js` is recorded in
[BACKLOG](BACKLOG.md#plan-browser-test-cache-key-assertion--2026-10-14).

## Current browser acceptance is visible to the model — 2026-10-14

Owner: workspace conversation/context. Runs requiring browser verification now
receive `RUNTIME_CONTEXT.webAcceptance.currentSnapshotVerified` alongside final-read
state, after current tool results. The projection and final acceptance reuse one
predicate for the accepted run/session/call, exact checks, expected-file coverage
and all snapshot dependencies. Model preparation uses frozen request authority;
final assessment refreshes checked files after the last model wait. Format repair
retains the frozen value. No new store, service, completion gate or automatic loop;
Office and requests without browser requirements keep their existing behavior.

Eleven focused CLI cases pass: failed/current/stale/reverified browser state, frozen
repair with an external edit during the final wait, exact recovery guards,
unchanged/externally changed PNG dependencies and the three existing final-read
cases. They cover user/native tool-result transport and browser-only requirements
without a read minimum. Browser fixtures now explicitly select their published
skill and authorize their isolated operations; the earlier direct unselected
`web.verify` assumption no longer matches the runtime. The CLI build passes.

A fresh full CSV run on the normal thinking-on profile passes 3/3 final reads,
3 verified writes and 24/24 browser checks: 7 iterations, 8 tool steps, 5 format
repairs. All 7 fresh HTTP requests carry the state last; all 5 repairs preserve it.
The state becomes true only after the accepted successful browser verification.
This passing scenario does not establish improved autonomous completion.
In a separate replay of the prior failing thinking-off request, both original and
new context still produce premature `done` in 3/3 attempts. The new metadata and
its description add 59 input tokens in that request; it executes no tools.
The visibility gap is closed, but semantic completion and Gemma's JSON suffix
failures remain open. Do not infer that another prompt clause will fix them.

Profile: Ollama 0.35.0, `gemma4:cloud` / remote `gemma4:31b`, same digest as the
preceding experiment; 32K/4096, temperature 0.2, top-p 1, developer instructions,
user results and `json_object`. The full run requests medium reasoning; the
paired replay retains the original `none`. Evidence/source: isolated `9f484d0d`
plus this change, `/tmp/rna-web-acceptance-20261014-5msuuw64/` (exact profile/digest,
request/SSE pairs, checks and full live event/CAS trace). No Office validation.

## Gemma cloud JSON terminator failures diagnosed — 2026-10-14

Owner: Core model protocol / CLI evaluation. All five format failures in the prior
CSV run contain one complete JSON object followed by `<tool_call|>`. Requests have
no native `tools` field or native history calls; direct SSE replay reproduces the
suffix in provider content. The strict parser correctly refuses it. Google
[documents this token](https://ai.google.dev/gemma/docs/core/prompt-formatting-gemma4)
as part of Gemma's native tool-call format; Ollama Cloud
[does not enforce structured outputs](https://docs.ollama.com/capabilities/structured-outputs).
This identifies a response-format mismatch, separately from accepted premature `done`.

Fresh isolated replays of the five failing requests, with unchanged task/history,
model and sampling, gave the following valid-envelope counts. Calls were not executed.
These small exploratory samples do not establish general success rates.

| Single change from the saved request | Valid envelopes |
|---|---:|
| None | 1/5 |
| Add a short JSON-boundary sentence to the response contract | 2/5 |
| Move the existing response contract to the last instruction message | 3/5 |
| Use system instead of developer instructions (two selected requests) | 0/2 |
| Request thinking off / reasoning_effort=none | 3/5 |

Every rejected replay still carries the same suffix; none has an HTTP failure.
The completed-state protocol control passes. No tested prompt/profile variant
eliminates the reproduced failure, so no prompt, role, parser or default is changed.

A fresh full CSV run with thinking off uses the exact previous CLI binary and
unchanged task, empty workspace, budgets and acceptance checks. It has one format
repair instead of five, but fails acceptance (exit 5): 13 iterations, 14 tool steps,
3/3 final file reads and no current successful browser verification. The last
browser attempt passes 12 checks, fails one and leaves 11 unrun; a later file edit
is followed by a read and `done` without another `web.verify`. Final assessment
correctly refuses success. Observed reasoning text is absent in this run. The
result does not justify disabling thinking or relaxing acceptance.

Next bounded investigation: expose the current required browser-verification state
alongside read acceptance, using its existing snapshot/currency owner. The last
request currently shows reads 3/3 while the required post-edit verification is
missing. This is a visibility hypothesis, not proof that another prompt field
will fix completion; no completion gate or second durable state is introduced.

Profile remains Ollama 0.35.0 / `gemma4:cloud` remote `gemma4:31b`, the same digest,
32K/4096 budget, temperature 0.2, top-p 1 and `json_object` as the preceding evidence.
Requests, SSE, exact profile/digest, comparison table, CLI binary hash and complete
live event/CAS trace: `/tmp/rna-json-boundary-20261014-hfd0q6mg/`. Live binary source
is isolated `9394b7b6` plus `26eca3f3`; no new build or Office validation was run.

## Final-read requirements follow current results — 2026-10-14

Owner: Core context compiler / workspace conversation. Requests with an explicit
read minimum move the existing required `RUNTIME_CONTEXT` behind current history
and tool results. No instruction text, extra message, store or completion gate is
added. Budgeting, receipt order and frozen format repair share this one placement;
ordinary requests and Office retain their existing order. A premature `done` still
ends the run and fails separate acceptance.

A small replay comparison of the same saved final CSV request produced valid
requests for all three remaining reads in 1/3 original-order responses and 3/3
trailing-context responses. The two original-order failures concatenated tool and
done objects; the strict parser rejects them, so they are format failures rather
than accepted premature completion. Replays execute no proposed tools. Both arms
used 10,704 prompt tokens. One completed-state control returned `done` without
extra reads. This sample does not establish a completion rate or general causality.

One fresh production CLI/Chromium CSV run passed: 3/3 distinct final file reads,
3 verified file changes and 24/24 browser checks, across 7 kernel iterations and
8 tool steps. Five earlier responses required JSON format repair. HTTP audit
confirms the block ends all 7 fresh requests and all 5 repairs preserve their
frozen request before appending the repair notice. Format reliability remains open.
Three focused Harness checks (continuity, exact budget, correctness before budget)
and all three read-acceptance CLI cases pass, including terminal early `done` and
an external edit during the final wait.

Profile: Ollama 0.35.0, `gemma4:cloud` → remote `gemma4:31b`, digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`;
32,768-token context, 4,096 response tokens, temperature 0.2, top-p 1, medium
reasoning effort, developer instructions/user tool results, `json_object`.
Source/evidence: isolated `9394b7b6` plus this change, under
`/tmp/rna-completion-prompt-20261014-7n8ybkwg/` (`profile.json`, `audit-summary.json`,
replay payloads and complete live event/CAS trace). Other tasks/models and actual
Windows/Office/WebView2 qualification remain open.

## Exact message artifact IDs replace UI URI parsing — 2026-10-14

Owner: Office bridge presentation / Web artifact UI. `ChatMessageViewDto.ArtifactIds`
replaces message `ResourceRefs` and `HtmlWorkspaceCheckpoint` in browser transport.
The existing Core resolver validates the owner, revision and unique immutable
snapshot once; recent/older-page Library projection and UI use the same IDs.
Checkpoint-only messages now include their historical HTML card even outside the
loaded heads. Durable evidence and opaque viewer/export URIs remain unchanged.
The UI parser and duplicate deduplication are removed: 51 fewer production C#/JS
lines, with no new service, store or fallback. This is no end-to-end latency claim.

Nine focused host-neutral checks pass: Library classes/history/transport/cursors,
message wire projection, older SQLite tombstones, chat paging and lifecycle.
Eleven Node checks and three Chromium image checks pass. Exact historical versions,
shared document resources, member/checkpoint deduplication and rejection of foreign
owners or mismatched revisions are covered. Source/evidence:
`/tmp/rna-artifact-links-20261014-cnwbpv6y/`, isolated `95a1d036` plus this change.
Version format is checked before commit. Real Windows/Office/WebView2 delivery and
performance remain open; these checks do not qualify Office behavior.

## Repeated CAS checks reuse verified encodings — 2026-10-14

Owner: Core `ChatBlobStore`/`CasBlobCodec`. One volatile fingerprint remembers the
plaintext hash/length, stored-byte hash and exact protector instance. Each request
still opens and hashes all current stored bytes. Matching encodings avoid repeated
authentication/decompression/plaintext hashing; a miss is verified against one
immutable encoded snapshot of at most 1 MiB before a new fingerprint is published.
Only encodings smaller than half the plaintext take this path. Larger or weakly
compressed blobs retain the streaming verifier. No stored-body cache, new durable
format or metadata-based integrity shortcut. Raw/compressed verification now shares
one bounded hash loop. Net production change: 12 added lines, with no new service.

13 focused host-neutral checks pass: streaming encrypted/raw/compressed CAS,
verified prefixes, encoding-magic collisions, text index repair/restart/coverage,
large one-line reconstruction, outlines, protected audit and fail-closed GC.
Repeated checks reject same-length corruption with restored timestamps, missing
files, mismatched plaintext length and changed protection keys.

On the same >10 MiB one-line/329-part fixture, three-run wall-time medians are
2.766 → 1.654 seconds (about 40% less). The fixture includes ingestion, cold indexing,
search and full part reconstruction; it is compressible synthetic text on macOS/
.NET 8, not Office/model latency. Stored bytes are still read on each call, so the
change does not establish linear scaling or improve every source encoding.
Source/evidence: isolated `63cc75fc` plus these changes, under
`/tmp/rna-source-cas-20261014-2k1033j4/`. Version format passes before commit.
Windows/Office/WebView2 evidence and CLI/live-provider size parity remain open.

## Source-inclusion guard understands SDK projects — 2026-10-14

Owner: Harness/project structure. Old-style projects still require explicit source
includes. SDK compile items now come from MSBuild evaluation, so defaults, globs,
conditions and exclusions are honored without a second MSBuild interpreter.
The repository check passes, with disposable cases for missing old-style files,
old-style wildcard refusal, SDK defaults/removals, conditional globs, a missing
explicit SDK file and disabled default compilation. Evaluation does not build,
restore or evaluate Office/VSTO projects. Previously reported SDK false positives
are closed; this is not Windows qualification.

Checked against isolated `63cc75fc` source plus this change. The duplicated SDK
backlog entries and completed tool-binding item are reconciled. Evidence:
`/tmp/rna-source-cas-20261014-2k1033j4/`. Version format passes before commit.

## Authored large text/code files — 2026-10-14 (host-neutral)

Owner: Core `DocumentArtifactStore`, Office Resource Gateway and managed mutation
observer. Agent file create/copy/patch now creates versioned Library `File`
artifacts with source provenance, the original or requested output extension and lossless supported
encoding/BOM/newlines. A 10 MiB one-line original can be copied, patched by a
unique bounded fragment and exported as exact bytes; stale and ambiguous patches
are refused. A large uploaded code file can also be copied exactly under a `.md`
name. A large uploaded `.md` no longer needs the 200,000-character
whole-body Markdown save path. Indexed resource parts and bounded Run Changes
hunks support inspection; the authored-file viewer pages beyond the earlier
512,000-character navigation ceiling without enabling full-body copy.

The focused 10 MiB copy/patch fixture covers exact export, immutable original,
late search, viewer continuation, stale/ambiguous refusal, restart, 10 MiB code-to-`.md` copy, >200,000
character `.md`, UTF-16 BOM/CRLF and bounded diff. Host-neutral Harness build and
the focused authored-file, shared Markdown and Run Changes checks pass. Real model
optimization quality, language-specific syntax checks, CLI/live Office parity and
Windows/Office/WebView2 qualification remain open.
The combined fixture takes about 2.1–2.7 s wall time and measured roughly
0.5–1.1 GiB peak RSS across macOS/.NET 8 runs; this includes import, repeated
reads, Library exports, viewer pages, restart and extra format cases, not an
isolated patch or Office latency measurement. Peak memory needs an isolated
per-stage profile before setting a production budget.
Corrupt derived CAS parts now re-store from verified exact source bytes rather
than failing the whole search with an unhandled length mismatch.

## Direct tool bindings and canonical run-view reads — 2026-10-14

Owners: Office tool catalogs/native runtime and bridge/UI run-view projection.
Catalogs pass source-owned bindings directly. The global ID-to-binding resolver
and its project include are removed; the selected native handler checks the pinned
handler ID/entry point before registration. A forged binding reaches no backend.
Read catalog entries reuse the typed projection builder.

The run-view reader accepts the existing PascalCase DTO fields and exact lifecycle/
health values; response session revision remains camelCase. Nested confirmations,
message/summary views and per-chat ordering retain their typed contracts. Dual-case
reads and value normalization aliases are removed from this bounded reader; the
immutable internal UI projection remains camelCase. Storage and wire formats do
not change. Other bridge operations/casing remain separate D03 work.

These slices remove 49 C#/JS lines net plus one obsolete project include. No new
registry, service or store. This is a reduced lookup/normalization path, not a
measured end-to-end latency result. Canonical owners: [tools](../tool-library.md)
and [conversation protocol](../conversation-protocol.md).

Checks: 26 catalog/native/architecture and 4 run-view C# checks; 41 Node checks
across run-view, completion guard, live projection and background completion.
Source checks use an isolated `99525d14` archive plus these changes. The Office
old-style project explicitly includes all 318 current source files. The broader
source-inclusion Harness test still has the known SDK-project false positives
(213 in this source); see [backlog](BACKLOG.md#source-inclusion-harness-guard-and-sdk-projects--2026-10-05).
Version checks pass before commits. Real Windows/Office/WebView2 evidence stays open.
Scoped patches/logs: `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-p2-20261014-e4669h8i/`.

## Bounded simplification of model preparation and editor transport — 2026-10-14

Owners: Core model projection, Office editor data plane and Office/workspace model
request preparation. Shared result data is projected before serialization; the
unreachable Office resource-family branch and intermediate generic result are
removed. Five editors share UTF-8/hash validation, three share strict typed JSON
upload decoding, and source-length checks no longer allocate a throwaway string.
Domain validation, upload ownership/single-use and mutation guards stay intact.

Office/workspace requests reuse one detached messages/receipt pair. Frozen source
and repair isolation remain unchanged. HTML structure rendering reuses its loaded
snapshot for member projection. No new service, cache, store or wire format was
introduced. These four slices remove 34 production lines net; operation/allocation
reductions are established by the code change, not a measured latency claim.

35 focused host-neutral checks pass (23 editor, 2 model projection, 7 shared HTML,
3 request/context checks), and the workspace Runtime builds. Checks use an isolated
`47365ee8` source plus these changes, excluding concurrent large-reading and host/UI
work. Version checks pass before each commit. No Office/WebView2 qualification.
Evidence and scoped diffs: `/tmp/rna-simplify-20261014-4xxsvp4i/`.

## Office chat creation and host skill availability — 2026-10-14

Owner: Web chat navigation / OfficeHosts target coordinator / Office built-in skills.
An enabled host-chat button silently ignored clicks during another navigation;
creation now queues and shows progress, disabled reasons and errors beside the
Office rows. The new four-host Node regression fails on the previous handler and
passes with the fix, including explicit retry and duplicate-click isolation.

Cold Excel/Word/PowerPoint creation now retains the COM-activated application on
the adapter's owner STA through discovery and binding, removing shell-launch/ROT
rediscovery from that route. Existing processes are not duplicated. Blank-document
preparation is attempted once; errors propagate and startup logs identify the
host, failed stage and elapsed time. This OfficeHosts change is source-reviewed,
not compiled or exercised as Office/VSTO on this machine.

The new published-package check exposed unsupported VBA procedures in Outlook's
skill list. Those three built-ins are no longer advertised there; every remaining
built-in selects successfully against its host's actual tool catalog. Focused
PowerPoint (8), Word (8), Excel (39), Outlook (19), skill packages (5), skills (5),
skill references (1), discovery (1), tool pack (8) and host bridge (1) checks pass.
Node host-chat/navigation and skill contract/editor checks pass. Chromium checks
actual PowerPoint clicks, queued progress, long error wrapping in narrow light/dark
panes, explicit retry and target initialization; input delivery also passes.
Changed UI assets have new cache keys. Empty host chats are checked for all four
hosts, including refusal to select another host's chat without rebinding.
Six focused session/navigation checks pass. The closed-document fixture now loads
the required HTML skill before writing; it verifies local HTML in the selected
chat, preservation of the other chat and refusal of live reads after closing.
The transcript layout fixture stops on a missing renderer stub,
recorded separately in [BACKLOG](BACKLOG.md#transcript-browser-fixture-dependency--2026-10-14).
Real cold/warm
launch, modal Office, multiple instances, Save As and panel rebinding remain open
Windows/Office/WebView2 evidence; the reported installed-build issue is not yet
qualified on the user's machine.

## Large document text navigation — 2026-10-14 (host-neutral)

Owner: Core CAS/`DocumentArtifactStore` and Office Resource Gateway. Document-owned
original text/code and authored Markdown/Plans now retain a v3 derived index with
32,000-character parts and line ranges. Cold materialization consumes verified CAS
bytes incrementally without plaintext scratch files. Exact in-resource search
scans up to 20 MiB and reports both scan and source completeness; `resources_read`
supports part numbers, and `resources_outline` pages line ranges with bounded
Markdown headings/advisory code names. Complete retained PDF text no longer proves
complete source extraction. Model projections hide runtime refs/coordinates while
preserving literal URI-shaped source text.

Focused host-neutral cases cover a >10 MiB one-line source with a surrogate pair at
a part boundary, late/negative and case-sensitive search, full part reconstruction,
corrupt derived-part repair, partial PDF extraction, C#/VBA/TS navigation, native
tool binding and both model projection paths. Editable copies and guarded partial
mutation of large originals, 20 MiB CLI/live-Office parity, and repeated-part
cost for larger/weakly compressed encodings remain open with owners in [BACKLOG](BACKLOG.md#large-source-mutation-and-provider-parity--2026-10-14).
No Windows/Office/WebView2 qualification is claimed.

The local >10 MiB one-line fixture with exhaustive read-back of every indexed
part passes in about 3 s wall time and peaks near 280 MiB RSS for the harness
process (macOS, .NET 8). This includes staging, cold indexing, searches and 329
part reads; it is not an Office or model latency measurement. That baseline
re-verified the decoded source on every part read. The bounded encoded-verification
optimization above reduces this cost while preserving fresh stored-byte checks;
full traversal is still not generally linear in file size.

## HTML metadata hashes follow the requested page — 2026-10-14

Owner: Office `ChatHtmlResourceCatalog`. Listing still validates the complete
ordered collection metadata, but hashes source content only for returned members.
The collection fingerprint never included content hashes; descriptors and cursor
bytes therefore stay unchanged. Direct member/structure reads retain full hashes.
No index or cache was introduced.

All 7 shared-HTML checks and the Gateway paging check pass on isolated task source.
The new case compares complete/paged descriptors and cursor bytes across active
files, Unicode, empty content, raw capabilities and data bindings, and confirms
that unreturned-member metadata drift still rejects continuation. Local one-item
page medians for 8/128/512 files of 16 KiB are
0.386/4.525/20.867 → 0.329/3.673/15.111 ms. Full snapshot parsing remains about
11.68 ms for 512 files; this result does not establish Office/UI latency. Further
structural changes need a real latency case, not another metadata store.

Evidence and frozen task source: `/tmp/rna-html-metadata-20261014-gdgtuzv6/`.
The two bounded P2 follow-ups below are complete. P1 model completion/skill
adherence and Windows/Office qualification remain open; large-file reading stays
in its separate workstream.

## Working-set recovery scan reduction — 2026-10-14

Owner: Core `ContextWorkingSet`. A reverse pass retains only successful-result
candidates for earlier recovery checks instead of repeatedly scanning all later
calls and failures. `ToolRecoveryContract` still decides whether evidence satisfies
recovery; unknown effects, receipt ordering and budgets are unchanged. The change
adds one temporary list, with no persistent index or new recovery policy.

All 10 context-continuity checks pass on isolated task source. Added boundary
assertions also pass with the original implementation: earlier success/later
failure does not release recovery, while an excluded later success does. Paired
local profiles for 128/512/1,024 mandatory receipts measure
9.69/35.96/125.13 → 8.46/31.57/83.77 ms. This synthetic result does not establish
Windows latency. A saved Gemma fake-Office trace (38 messages) produces identical
receipt fingerprints/counts at budgets 0/8192, with both versions around 3 ms.
That probe adds an in-memory checkpoint and uses empty body authority; it is not
real model compaction or Office qualification. An initial CLI trace had a different
unmaterialized result format and was excluded from this comparison.

Evidence, frozen sources and before/after binaries:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-working-set-20261014-jvw_38d6/`.
P1 model completion/skill adherence remains open. HTML metadata paging is addressed
in the section above.

## Prompt simplification and incremental context cost — 2026-10-14

Owner: Core prompt/context compiler; Office Task Tracking guidance. Prompt schema
47 removes duplicate completion/call rules and keeps Task List procedure details
in its skill. Default Agent general/tools/skills instructions shrink from 15,770
to 12,678 characters (19.6%); existing archived prompt migration applies.
The compiler updates request-local atom costs during folding/omission and still
checks the final whole request. Evidence selection, durable storage and runtime
completion policy are unchanged.

Paired host-neutral fixtures produce matching content fingerprints, checked receipt
fields and refusals in all 72 cases. For 96 interactions, grouped median compile
time is 7.75 → 4.01 ms with a generous budget and 9.85 → 6.50 ms with a tight budget;
four-interaction fixtures remain about 0.63 ms. These are local synthetic profiles,
not CI thresholds or Windows qualification. All 24 focused budget, continuity,
aggregate omission, prompt migration and skill checks pass on an isolated copy of
this task's source changes, excluding concurrent resource-reading changes.

Real `gemma4:cloud`, digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`,
`json_object`: the fake-Office HTML/CSS/JS/VBA workflow passes exact final-source
acceptance before/after (11/9 requests), while spontaneous Task List selection
remains absent. CLI CSV passes 24/24 browser checks in both runs; before has 3/3
required final reads and passes, after has 0/3 and fails acceptance. Their first
HTTP requests are identical: CLI uses the unchanged shared response contract.
This single pair does not establish a quality regression or improvement; premature
completion remains open. No runtime `done` gate was added.

Evidence, profiles, original compiler, paired binaries and live traces:
`/tmp/rna-context-opt-20261014-3hodfzht/`. P1 remains real-model completion/skill
adherence; the bounded working-set and HTML follow-ups are recorded above.
Large/structural reading is a separate workstream. Office/VSTO/WebView2 was not run.

## Working resource links and verified-effect boundary — 2026-10-14

Skill/tool publication audit: a scripted Agent run now creates a new skill and
tool, reads both through the published catalog in the same run, then receives
the skill body and admitted tool schema on subsequent requests. Skill/package,
tool and Agent groups pass 5/5, 4/4, 49/49 and 53/53. Cross-session VBA source
advancement can no longer authorize delete or rename from an older observation;
exact patch anchors continue to use current live source. The built-in contract
inventory and v7/skill-only Agent expectations were reconciled. Windows/Office
delivery and unprompted model skill selection remain open evidence.

Owner: Resource Gateway/HTML authority, Core recovery contract, Office skill
exposure and run projection. Working HTML targets now resolve the selected current
workspace directly. `snapshotTarget` remains an explicit immutable history read.
The prompt resource index advertises that working target for active HTML. A
post-read currentness check compares the selected head without reloading the
whole aggregate.
Normal file mutations publish only the touched file's logical head; unchanged
siblings retain current source evidence. Restore, deletion/recreation and a real
same-file edit invalidate it. An unrelated document authority publication no
longer aborts current HTML member discovery. No-op writes retain their snapshot.

Large VBA patch refusals now project `readScope=relevant_lines` and require a
corrected patch; whole-source overwrites still require complete current source.
The five VBA editing tools require a selected skill; macro execution retains its
direct optional schema route. Prompt schema 46 aligns direct `capabilities_read`
with skill-only loading and scopes extra final-file reads to explicit CLI
acceptance. `RunViewState.GoalVerification`
explicitly remains `not_assessed`; clean tool health and model `done` do not certify
the whole user goal. The UI shows verified write/no-change counts separately.

Host-neutral evidence: HTML current/history, sibling/no-op/delete/recreate and
equal-byte restore, shared HTML 6/6, VBA large-patch recovery, tool pack 8/8,
skills 4/4, prompt migration 2/2, controller approval/compaction/restart, and
web completion/HTML suites pass. Real `gemma4:cloud` / `gemma4:31b`, digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`,
`json_object`, 128K requested context: 11 model requests, exact HTML/CSS/JS/VBA
source acceptance true after the Office/Core workflow corrections. Evidence:
`/tmp/rna-workflow-20261014-final/` (`acceptance.json` passes, 11 requests).
The model selected HTML and VBA skills but skipped Task List without an explicit
instruction. It attempted one unavailable VBA tool and one direct schema read of
that skill-only tool before loading the VBA skill; neither attempt wrote source.
Source read-back is not Office execution or browser rendering.
Windows/Office/WebView2 qualification and large-workspace latency remain open.

## Real v7 model regression and shared response contract — 2026-10-13

Tested HEAD `9619aba4` plus the captured pending skill/resource fixes. Source
archive, working-tree patch, untracked fixtures, exact binaries, HTTP/CAS,
commands and audits are retained in
`artifacts/cli-evidence/2026-10-13-v7-live/`. Owner: Core response contract /
workspace conversation / CLI evaluation. Model writes used disposable workspaces
and synthetic host state.

Found and fixed a CLI instruction gap: its initial prompt named the three v7
fields but omitted the complete call envelope, escaping and message rules already
used by Office. With `json_object`, Gemma Cloud exhausted ten format attempts
before any tool. CLI now reuses `AgentPromptDefaults.StructuredResponseContract`;
parser, tool authority, task checks and budgets are unchanged. The focused HTTP
regression fails before this change and passes afterward, including all three
result roles and manual approval/profile inheritance. CLI builds cleanly; the
host-neutral Harness build retains its two existing PDF platform warnings.

| Real scenario | Result |
|---|---|
| Gemma 12B / 32K / f16, both server caches disabled | Greeting JSON passes, but memory admission fails: 20/22 warning samples and 1549.5 MiB new swap-out. No full task dispatched; model unloaded. This is an environment limit. |
| Cloud HTML/CSS/JS/VBA + explicit Task List | Exact four-source edits pass, selected procedures and closed Task List retained; 13 requests. Production Office services with a fake host, not COM/WebView2 qualification. |
| Cloud CLI counter repair | Passed in 16.37 s / 8 requests: initial browser failure, verified repair, current source read and final 9/9 checks. |
| Cloud CLI CSV, thinking off | 96.81 s / 18 requests, 16-iteration limit. Repeated clipped-chart failures; 0/3 final reads. Failed. |
| Same CSV, thinking on | 109.28 s / 18 requests, actual reasoning present. Model repaired numeric sorting; final 24/24 browser checks pass, but 0/3 required final reads. Failed; feature follow-up not run. |
| Cloud 24K compaction/resume fixture | 58.62 s / 32 requests: 10/10 exact final files, 9/9 ordered read/write/read-back stages, but 9/10 final reads (receipt omitted). No checkpoint, process loss or resume occurred. Full fixture failed. |

Cloud tag `gemma4:cloud` resolves to `gemma4:31b`, digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`.
CLI profiles use `json_object`, developer instructions, user results, 4096 output,
explicit auto-confirm, 32K planning (24K for compaction); the Office fixture uses
128K, temperature 0 and non-streaming. CSV retains 16/24/600, repair 12/12/600,
and compaction 40/48/600. No operator repairs, hints or increased task limits.
Canonical audits pass for all dispatched CLI runs, including exact task retention,
latest results and 553 cumulative call/result pairs after the fix. Presence in
transport does not establish model attention. Cloud CSV/repair runs have normal
memory pressure and no new swap-out. Owned endpoint is stopped; no models remain
loaded. An ambiguous greeting probe and a runner invocation with an overlong
`TMPDIR` were retained separately and do not count as task evidence.

**M4/M5 remain open.** Priorities: premature completion/current-read adherence and
CSV repair → unchanged feature follow-up → real checkpoint/restart/external-edit/
resume at 24K. Existing Harness fixture debt and Windows/Office delivery remain
separate work. See the [current risk](RISK_REGISTER.md#cli-response-contract-and-live-v7-completion--2026-10-13)
and [next slice](#next-cli-acceptance-slice--2026-10-13).

## Skill and resource workflow regressions — 2026-10-13

Reproduced and fixed through the actual Office controller with a fake Excel host:
confirmation replaced `ChatRunRecord` without `SelectedSkills`, and its new
invocation id no longer matched the originating request's tool bindings. This
lost loaded procedures and rejected confirmed writes before dispatch. Resume now
retains selections and checks the exact durable pending call against its original
request; changed tools, arguments, tasks and steps remain rejected.

Other fixes: the Skill Library reads `scripts/` and `assets/` with the correct
published basename; dynamic `tools_upsert` retains skill-only exposure; identical
HTML/CSS/JS writes retain their snapshot/timestamp and report verified no-change.
With Agent JavaScript enabled, the Library's configured `tools_upsert` source
previously mismatched the permanently published disabled schema and could never
be read. Startup and settings save now publish source/documentation from the same
configured descriptors; the controller regression covers enable/disable read-back.
Current workspace validation avoids a full JSON round trip including navigation.
Schema 45 clarifies singleton skill loads and Task List selection; specialized
catalog entries name the skill to load. Owners/contracts: [skills](../skills.md),
[artifact library](../artifact-library.md), Core runtime and Office controller.

Focused evidence: controller create/read skill and tool sources, structured
compaction during approval and four approval restarts, HTML patch/full replacement/
identical write with exact effect counts, historical editor downloads and
independent-task reset pass. Existing
unified-controller coverage also passes. Harness package 4/4, HTML tools 7/7,
shared HTML 6/6, tool-pack 8/8, discovery 1/1, prompt migration 3/3 and targeted
VBA patch/stale-source/editor checks pass. JS Library/editor and HTML source/write
checks pass; obsolete hardcoded chat-script cache tokens in the HTML fixtures
were replaced with version-presence checks while retaining lifecycle assertions.

Real `gemma4:cloud` (remote `gemma4:31b`, local tag digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`),
`json_object`, developer role, requested 131072 context (provider advertises 262144):
four synthetic runs / 40 requests preserve all unrelated source and produce the
exact requested HTML/CSS/JS/VBA changes. Baseline: 10 requests; two revised runs:
9 each; explicit Task List fixture: 12, creates and closes the list. The latter
recovers from one invented-target read error and one unexposed-tool format repair.
Unprompted Task List selection remains unreliable on this model. A local 32K
profile was refused by the prompt budget before dispatch; this is context evidence,
not a failed model call. Raw local evidence: `/tmp/rna-resource-review-cloud-*`;
reproduction is in the [Harness README](../../tests/RNAssistant.Harness/README.md).

Follow-up review also passes current-source carry-forward/compaction, stale VBA
refusal, large VBA editing after compaction, HTML editor and competing-chat/history
checks (24 focused Harness cases), plus browser-script source/write, artifact-tree
and skill/tool editor checks. These are host-neutral and scripted-controller checks;
no additional real-model run was made for this test-only extension.

Open: `model_done` is not task acceptance; a valid terminal response can still
claim unperformed edits. Runtime effect/health evidence does not grade the user's
requested content. Target-model automatic skill selection, reported Library failures
outside the reproduced supporting-path/configuration cases, small-context budget
refusals, history-dependent metadata work and real Windows Office/WebView2 delivery
remain open. No Office/VSTO validation, release qualification or
product-version change. See the [incident scope](RISK_REGISTER.md#skill-and-resource-workflow-regressions--2026-10-13).

## HTML historical resource reads — 2026-10-13

Owners: Resource Gateway, HTML catalog and DocumentArtifactStore. A workspace
structure could advertise an unreadable historical member, or its label could
silently resolve to current replacement bytes. Parent-qualified semantic targets
now resolve the retained snapshot across edits, file deletion and restart; recovery
targets use the same contract. Current discovery and immutable stored bytes are
unchanged. See [Resource Fabric](../resource-fabric.md) and the
[incident scope](RISK_REGISTER.md#html-historical-member-lookup--2026-10-13).

Evidence: focused native regression 1/1 (including pagination, missing source and
ambiguity), related resource checks 4/4, shared HTML 6/6 and HTML tools 6/6 pass.
No Office/VSTO validation or product-version change; live incident qualification
and attribution of the photo's later `handler_failed` remain open.

## Skill packages and chat Arsenal — 2026-10-13

Scope/owners: Core `SkillTaskService`/compiler/runtime own task selection and exact
request bindings; existing Office/CLI ports supply publications and permissions;
Office controller + `web/app-arsenal` own the metadata projection. Implemented the
attached skill/tool package plan's first runtime slice **without new Code Mode**.
`rnassistant` front matter declares dependencies, `common.skills_load` selects them
for the next request, existing run events preserve selection through compaction and
restart, and specialized tools cannot bypass selection via schema discovery.
Default prompt schema 44 aligns selection, Plan, catalog and compaction guidance.
The Library retains manifests on save/clone; scripts/assets are bounded inert UTF-8.
Canonical contracts: [skills](../skills.md), [tools](../tool-library.md),
[conversation](../conversation-protocol.md), [events](../session-events.md).

Arsenal was redesigned as a compact themed popover: tabs, search, readable skill
cards, short permission/status labels and expandable details. It retains search,
focus, disclosures and scroll, drops stale chat replies and performs metadata reads
only. Checked at 1100/740/360 px, including a short 480 px pane and a 60-tool list.

Evidence: package Harness 4/4; tool-pack 8/8 (includes an HTML skill → actual local
write through the production Office service with a fake host); skill editor 6/6;
JS Library 4/4 and editor 13/13; prompt migration 3/3 and both prompt checks pass;
Arsenal Chromium checks pass. Core CLI pilot passed
source inspection → canonical load → read/patch → report creation → final read-back,
with two process-boundary approvals, exact published instruction recovery, optional
fallback and new-task reset: 8 pilot requests / 11 controlled HTTP requests total.
These are scripted model responses, **not real-model quality evidence**. Host-neutral
Harness, CLI and MockDemo compilation passed; no Office/VSTO validation was run.

Broader `agent:` comparison against clean source archive `0410126e` reproduces ten
pre-existing protocol/history failures; see [baseline backlog](BACKLOG.md#host-neutral-harness-baseline-failures--2026-10-13).
The full Harness is not green. Live model task comparison, actual Office/WebView2,
release qualification, binary assets and archive import remain open, not implicit
requirements to add another execution runtime. Product version is unchanged; no release/tag.

## Premature terminal narration — 2026-10-13

Owners: Core ModelProtocol/prompt defaults and Task Tracking guidance. The reported
CSS task stopped at a promise to continue. Prompt schema 42 removes instructions
that treated every unfinished outcome as a blocker, couples initial progress to a
concrete call, and requires a complete answer/code now when no tool is needed.
Format repair must not merely relabel a missing-call promise `done`.
The v7 actions and kernel lifecycle remain unchanged; see the
[canonical decision rules](../protocols/CONVERSATION_RESPONSE_V7.md).

Host-neutral checks: no-tool 3/3, kernel 55/55, model protocol 15/15 and prompt
migration 3/3 pass. The isolated commit candidate additionally passes no-tool
3/3, prompt migration 3/3 and built-in guidance 1/1; the earlier stale skill
assertion belongs to pending skill changes excluded from this commit.
Exact incident attribution and DeepSeek completion behavior still
need accepted response/run evidence; see the
[incident and remaining gap](RISK_REGISTER.md#premature-terminal-narration--2026-10-13).
No Office/VSTO validation ran.

## Useful commentary and context continuity — 2026-10-13

Owners: Core ModelProtocol/prompt defaults, ModelContextCompiler, accepted-call
storage and web agent transcript. Prompt schema 41 separates user-facing comment frequency from tool calls
and Task List note updates. One shared rule permits silent routine preparation,
asks for useful findings/decisions with their basis and uncertainty, and discourages
repeated plans. Existing notes retain current state; compaction keeps corrections
and unresolved conflicts without promoting assistant assessments to evidence.

Folding an older call now preserves its nonempty comment as an assistant source
within the context budget. Large archived calls retain the exact comment in their
header, verified against the canonical body on hydration; missing metadata requires
an explicit new chat/reset. No extra model calls, scheduler, text deduplication or
second memory store. Details and compatibility are in the
[canonical context contract](../conversation-protocol.md#conversation-context).

In the active feed, silent steps continue the preceding comment. Completed actions
fold below it; current work, questions/approvals, errors and uncertain/partial
results remain visible. Manual expansion survives updates. This uses existing
activity identities and native disclosures without changing stored history.

Evidence: targeted host-neutral context continuity, repeated compaction/restart,
source-kind rejection, archived-call integrity, prompt migration/schema/repair and
live/saved UI projection checks, plus Chromium visibility, keyboard disclosure and
narrow layout checks. These establish retention and protocol behavior;
real-model comment frequency/summary quality and Windows/Office/WebView2 remain
unqualified. The [reported repetition](RISK_REGISTER.md#repeated-narration-during-capability-preparation--2026-10-13)
still needs incident events and controlled DeepSeek evidence. No Office/VSTO checks.

## Automatic action approval — 2026-10-13

Owners: settings UI / Core ConversationRunPolicy and ToolRuntime / Office and
workspace conversation services. The saved default-off setting now authorizes
automatic approval for new Agent/Ask runs in every chat; CLI exposes
`--auto-confirm on|off` per new turn. Accepted runs keep their captured value
through settings changes and restart. Plan/Deny, guards, read-back and unknown
non-retry remain enforced. The completed backlog requirement is now covered by
the [canonical contract](../conversation-protocol.md#automatic-action-approval).

Evidence: auto confirmation 3/3, unified policy 6/6, tool runtime 15/15 and JS
package source/policy 1/1. Controlled CLI checks pass multistep automatic writes,
pending approval after restart, next-turn disable, unread-file guards, Plan/Deny
and invalid flags. Chromium verifies settings save/reload/reset and composer
status/layout at 1100/740/360 px. CLI and host-neutral Harness builds pass (Harness
retains two existing PDF platform warnings). Real Windows/Office/WebView2 and
real-model task quality remain unqualified; no Office/VSTO validation ran.

## Unified agent permissions and Plan toggle — 2026-10-13

Owners: Core ConversationRunPolicy/ToolRuntime, Office/Workspace conversation
services and composer UI. Agent is the default with one optional sticky Plan toggle;
Ask/Deny permissions are separate. Non-planning effect/code approval follows the
captured user setting; Plan/Deny allow only reads and built-in Plan/Task List updates.
No second kernel, routing LLM or mandatory tools. New chats use format 7; format 6
is read-only history with no rewrite or migration. Emergency disable is documented
in the [canonical audit/contract](../conversation-protocol.md#unified-agent-audit-and-cutover-2026-10-13).

Evidence: unified policy 6/6, tool runtime 15/15, kernel 55/55, no-tool 3/3,
Plan 3/3, streaming 2/2 and pre-cutover rejection 1/1. Controlled CLI checks cover
approval across process restart, restored file guards, policy change, injection and
emergency disable. Chromium composer checks pass at 1100/740/360 px. CLI builds;
Harness builds with the existing PDF platform warnings. MockDemo's real-controller
test passes with a fake host, including settings changes during pending approval,
restart, automatic next run, and Office planning wrappers. Prompt/settings tests
pass 11/11 after fixture repair. Review fixed permission loss during session/run
cloning, confirmation and the obsolete Chat compaction estimate. The isolated
commit candidate passes 838/862 Harness checks; all 24 failures also reproduce on
`10f78f40` (see BACKLOG), with no new failures. The draft inspector separately
checks next-run settings without changing accepted authority. Version-format
validation passes.
Real Windows/Office/WebView2 and real-model task quality remain unqualified.


## Artifact tree and document removal — 2026-10-13

Owners: Artifact Library UI / DocumentArtifactStore. A single tree and adjacent
viewer replace the separate catalog/editor screens; selected HTML members stay
inside that tree. Unlink remains chat-local. Confirmed document removal uses an
exact-current guard and durable authority tombstone across all chats, including
originals, Plans, HTML, Markdown and JSON. Historical references/bytes remain.
The Plan editor no longer deletes through an argument-free active-Plan tool call.

Evidence: host-neutral working set/removal 4/4, Library 5/5, recovery 1/1 and typed
artifact bridge 1/1; targeted tree/actions/text/media scripts and actual Chromium
layout at 1100/420 px pass. Stale writers, repeated/unconfirmed deletion, restart,
CAS retention and a failed post-publication chat save are covered. Existing fixture
failures and the unexecuted real-controller smoke are recorded in
[BACKLOG](BACKLOG.md#artifact-ui-check-fixture-drift--2026-10-13).
Windows/Office/WebView2 delivery remains unqualified; no Office/VSTO checks ran.

## Task List retry after state repair — 2026-10-13

Owner: Core TaskListToolHandler / ToolRecoveryContract. A definite state refusal
now declares the conversation's logical Task List identity. `Replan` permits a
new explicit identical call only after a verified change/restoration impacting
that declared identity. This covers missing-list close → save → the same close;
the production shared-runtime regression failed before the fix. Fresh reads,
unrelated changes and no-op/unknown effects do not satisfy that recovery. Unknown
calls remain non-repeatable. No tool is automatically retried, no domain rules
were added to AgentKernel, and identities remain runtime-only.

The old corrective-success fixture now checks the exact dependency and unknown
isolation; stale built-in guidance assertions follow the current prompt/skill
contract without changing production guidance. Host-neutral checks pass: kernel
55/55, context continuity 9/9, Task Lists 5/5 and built-in guidance 1/1. CLI build
has zero warnings/errors; controlled HTTP Task List checks pass Plan/user and
Agent/tool, including interrupted effects and identity-free model context.
Windows/Office and real-model qualification remain open.

## Response v7: tool-driven continuation — 2026-10-13

Owner: ModelProtocol / AgentKernel. Standalone no-call `continue` is removed from
the schema, parser, kernel, checkpoint restore and Office/CLI consumers. Progress
accompanies calls; a no-call response ends the turn as `done`, `blocked` or
`needs_input`. A question retains its distinct awaiting-user outcome and resumes
only after actual input. Typed `common.questions_ask` remains available. Terminal
intent does not certify completion or effects. No prose classifier or case-specific
rules were introduced; transient narrative feedback and its counter are removed.

Prompt schema 40 shares the same action guidance across default modes, response
schema, repair and CLI. Prior prompts retain the existing archive-on-save migration.
Old v6 history is explicitly rejected before dispatch, stays intact and needs a
new chat/reset; `continue` is never silently converted into `done`.

Evidence: host-neutral no-tool flow 3/3, v7 wire 13/13, model protocol 15/15,
preflight 2/2, prompt migration 3/3, default prompt contract 1/1 and native question
1/1 pass. Kernel checks pass 55/55 after the independent Task List recovery fix
described above; the stale built-in guidance assertion also passes.
The CLI builds with zero warnings/errors. Controlled HTTP checks pass all four
response-format/result-role combinations, typed questions in Plan/Agent, Chat
admission, interrupted-run recovery and explicit cancellation/continuation. Web
history rendering also passes, including old no-progress diagnostics. Real-model task quality and
Windows/Office/WebView2 delivery remain unqualified; no Office/VSTO validation ran.

## Ollama checkpoint memory and fixed acceptance — 2026-10-12

Owner: CLI evaluation / isolated endpoint profile. Tested pinned `881596f2`
binaries, original tasks/checks, 4096 output, system/user roles, json_schema and
requested thinking off. Production runtime, prompts and acceptance were unchanged.
CLI build passed with zero warnings/errors; no Office/VSTO qualification was run.

The bundled Ollama 0.35.0 runner retains context checkpoints independently of its
prompt cache. With `LLAMA_ARG_CACHE_RAM=0`, the Gemma CSV run still created ten
checkpoints of about 320 MiB each. It was cancelled at 242.77 s after sustained
warning pressure and new swap-out: three verified files, 12/24 browser checks,
then a correctly refused unread replacement. This is an environment-limited run,
not an original 600-second timeout or a passing task.

Setting `LLAMA_ARG_CTX_CHECKPOINTS=0` on the owned server removes those copies;
runner logs confirm both caches disabled, one slot, Flash Attention and 32K/f16.
RNAssistant's frozen history and durable compaction checkpoints are unchanged.
Both Gemma profiles have digest
`0bb794a6bbd6a906811886d010fa299cf684e5ca7093fdc7228a3dce071360cb`, inherited from
the installed 12B/32K profile. Binary/task hashes match, and the first production
HTTP request is identical except for the temporary model tag. This qualifies a
bounded memory profile; stochastic output and timing are not a quality A/B score.

| Fixed gate with server checkpoints disabled | Result |
|---|---|
| CSV, 32K, 16 iterations / 24 tools / 600 s | 196.81 s, model `done`, exit 5. Three verified files and **24/24 browser checks pass**, but **0/3 final complete file reads**. The unchanged follow-up was not run. |
| Notes, 24K planning, 40 iterations / 48 tools / 600 s | Timeout at 600.43 s before checkpoint/restart. All eight status edits preserve the remaining bytes; the receipt and manifest are correct. Anchor review is missing; prescribed read/write/read-back order passes **0/9**. No restart/resume acceptance is claimed. |

The final CSV request explicitly contains zero current reads, three remaining
reads and all three filenames. Canonical wire audits pass: CSV 5 requests / 10
cumulative call-result pairs; notes 26 requests / 233 pairs, 25 accepted calls.
Both have zero compactions. There is no new demonstrated context-loss defect;
transport presence alone cannot establish model attention. Full M4/M5 stay open.

The combined no-server-checkpoint experiment has 275 memory samples, maximum gap
3.07 s, normal pressure throughout and **zero new swap-out**. Existing pages read
from swap total 12.78 MiB; this is not a zero-swap claim. CSV/notes minimum reported
free percentages are 28/30; peak runner RSS is 9.21 GiB, not total GPU allocation.
The previous zero-swap-in preflight rejected otherwise normal Gemma on less than
1 MiB of reads. The explicitly recorded replacement admission window permits at
most 2 MiB swap-in, zero swap-out and four normal samples with at least 25% free.

GPT-OSS 20B/32K with q8_0 KV passed its simple v6 probe but failed memory admission:
one critical and twenty warning samples, 4.48 GiB new swap-out. No full GPT-OSS
task ran. Its reasoning field remained nonempty despite requested thinking off.
Cloud metadata timed out; Ollama also documents that Cloud does not support
structured outputs. Neither observation qualifies Gemma Cloud for this runtime.

All owned profiles were removed, models unloaded and temporary servers stopped;
user applications/global Ollama settings were unchanged. Evidence, drivers,
HTTP/CAS, hashes and shared source/binaries are retained locally under the ignored
`artifacts/cli-evidence/2026-10-12-model-memory/`; `index.json` maps each run.
The profile instructions are updated in [development rules](../development-rules.md#сравнение-моделей).
Next: a separately labelled thinking-enabled/reference-model comparison on a
qualified endpoint, with the same tasks, read requirements and original budgets.

## Closed-document chat navigation — 2026-10-12

Owners: ChatSessionService, Office chat coordinators and bound adapter metadata.
Existing chats can be selected without opening their document or launching Office.
Local HTML work keeps the selected chat's authority; a live exact session is still
required for Office capabilities and reads. Closing the bound document preserves
navigation metadata without treating it as live evidence. Reopening and selecting
the chat again restores the exact binding through the existing attach path.

Evidence: host-neutral `chat sessions:` 17/17, `host runtime:` 10/10 and
`agent: closed document` pass; web chat navigation 13/13 passes. These include
HTML creation after closure and isolation from other chats/document authorities.
Windows/Office/WebView2 delivery and actual COM closure/reopen remain unqualified;
no Office/VSTO validation was run on this machine. Existing release gaps remain open.

## VBA context recovery and authoring contracts — 2026-10-12

Owners: VBA mutation/recovery, Tool Authoring, built-in skills and Library editor.
A scripted production-run reproduction found that an omitted-source overwrite
refusal embedded a synthetic 16K-character source head/tail in its mandatory
recovery result. At a 24K context window, the next request required 20,243 tokens
against a 19,400 message budget and stopped before the model could recover.
Large/truncated recovery sources now retain the semantic target and refresh
policy without that body/diff; small complete source recovery is preserved.
The same scenario now refuses the overwrite, reads exact lines, applies a patch
and preserves all other source. Guards, confirmation and read-back remain intact.

Guidance distinguishes missing source observation from host access denial,
component/procedure/read selectors and create/update intent. Missing edit targets
resolve their owning component rather than imply creation. Tool authoring uses
manifest-owned VBA metadata, separate JS fields and complete package replacement;
Library metadata controls no longer offer ignored VBA edits. Each package
component now receives the existing static live-source validation before saving.
Built-in skills and canonical docs follow these contracts and automatic archived
prompt migration.

Evidence: focused host-neutral create/read/edit/compaction, 24K refusal/recovery,
VBA guards, tool/skill CRUD and JS schema checks; web editor/resource/UX tests.
The unrelated catalog-test expectation drift is recorded in
[RISK_REGISTER](RISK_REGISTER.md#vba-context-recovery-review--2026-10-12).
The user's exact access error remains uncorrelated without its diagnostic/trace;
no real LLM or Windows/Office/WebView2 qualification was run. Existing M4/M5 and
release evidence gaps remain open.

## Browser lifetime after caller process loss — 2026-10-12

Owner: development browser lifetime. Baseline `bb9a519d` reproduced a CLI
`SIGKILL` leaving 9 Chromium processes and its temporary profile; the in-process
HTTP server closed. Evidence:
`/private/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-preview-loss-baseline-cyvrhl1q/result.json`.
The reproduction cleaned up only its own surviving processes.

The shared preview/verify runner now uses `RNAssistant.BrowserHost` to create and
own Chromium/profile before reporting readiness. Caller pipe EOF closes that
owned tree and removes the profile; normal completion awaits cleanup and reports
failures. There is no PID journal, global sweep, new resource reader/store or
second agent loop. The helper cannot publish verification outcomes: interrupted
verification remains `Pending`, without invented diagnostics or automatic replay.

Checks: CLI build passes with zero warnings/errors. `browser_lifetime_smoke.py`
passes real-browser caller loss, concurrent viewer isolation, retained snapshots,
controlled pre-DevTools loss and real verification interruption. A focused helper
check also confirms safety-deadline cleanup while its caller pipe stays open. Existing
`resource_preview_smoke.py` and `web_checks_runtime_smoke.py` pass expiry/Ctrl+C,
sandbox, repair/retry and stale-final regressions. A framework-dependent Debug
publish includes the helper and passes a real preview lifetime. Diff, touched
links and `ValidateVersionFormat` pass.

Scope: Mac headless Chromium caller loss; simultaneous helper/OS loss, other
platforms and self-contained packaging remain unqualified. No real LLM or
Windows/Office qualification was run. Full M4/M5 remain open: the next priority
is a compatible endpoint with qualified input/memory headroom, then unchanged
autonomous CSV creation/follow-up and real 24K compaction/restart/resume acceptance.
Working-preview UX/additional formats and the M6–M11 cutovers remain separate scope.

## Preview cancellation classification — 2026-10-08

Owner: development browser lifetime. Follow-up to `b86cb6ec`: a cancelled CDP
receive may throw an aborted/closed transport exception. The observation owner
now uses its actual deadline/caller tokens to classify that termination. Caller
cancellation propagates to CLI exit 130; ordinary lifetime expiry returns its
existing close reason. Transport faults without a requested deadline still fail.
Cleanup remains in the existing browser/profile/server lifetime owners.

Checks: clean CLI build; the complete `resource_preview_smoke.py` now passes
resource views, immutable/historical routes, expiry/cancel cleanup and browser
isolation. Focused real-browser cancellation also checks immediate-after-open and
active-observation timing with PNG/JPEG assets, exit 130/Cancelled and no invented
verification record. Diff, touched links and `ValidateVersionFormat` pass.
No new real LLM or Windows/Office qualification is claimed.

The subsequent caller-loss cleanup slice is recorded
[above](#browser-lifetime-after-caller-process-loss--2026-10-12). Full M4/M5 remain open, including fixed
autonomous CSV/follow-up and real 24K compaction/restart/resume. Those need a
compatible endpoint with qualified input/memory headroom and unchanged acceptance.

## Exact raw and raster workspace views — 2026-10-08

Owner: WorkspaceFileService / filesystem provider / development browser. The
existing owner now reads bounded raw bytes and retains their exact views in the
same authority/CAS. Text/raw observations of unchanged bytes share one revision;
both obey confinement and unresolved-effect guards. Historical reads require the
published body, with no live fallback. The application/CLI exposes `raw` and
PNG/JPEG `image` views and explicit redirected byte export. Image header admission
is limited to one MiB and 4096 pixels per side; it does not decode pixels or add
thumbnails. Binary bodies stay outside model JSON and grant no model write credit.

Preview and verification use these exact bytes, including quoted HTML image/icon
references and CSS image URLs. Manifest members pin their source view; PNG/JPEG
headers must agree with their served MIME. Verification rejects broken/incomplete
HTML images. Context preparation and done acceptance refresh raw dependencies, so
an image edited externally after verification cannot retain passing acceptance.
No new file reader, artifact store, writer or model loop was introduced. Additional
formats, large/streamed views, binary mutations and Office viewer adoption remain
open §8.1/M5–M7 scope.

Checks: CLI builds with zero warnings/errors. Focused Harness `workspace files:`
8/8 and `workspace snapshots:` 3/3 pass (the Harness build retains two existing PDF
CA1416 warnings). `binary_resource_smoke.py` passes PNG/JPEG metadata/export,
historical browser replay, MIME/decode failures and controlled acceptance both
with and without an external image edit. Its model-discovery/authority checks
also pass after filtering application-only view capabilities. The existing
`web_checks_runtime_smoke.py` passes repair/retry guards, text evidence and final
freshness rejection. No real LLM or Windows/Office qualification was run.
Diff, touched links and `ValidateVersionFormat` pass.

`resource_preview_smoke.py` passes resource/text and ordinary preview checks, then
fails Ctrl+C classification twice (including an isolated run): WebSocket `Aborted`
becomes exit 5 instead of 130. An isolated source archive of `d1297ced` passes that
case once, so causation is not established. Evidence:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-preview-cancel-baseline-f2uv3cgb`.
The suite was not green at this checkpoint; the subsequent cancellation fix and
passing regression are recorded [above](#preview-cancellation-classification--2026-10-08).
Full M4/M5 and the fixed real CSV/24K compaction-resume gates remain open.

## Bounded browser verification diagnostics — 2026-10-08

Owner: development browser provider / existing workspace verification publication
owner. M5 now retains a viewport PNG and typed CDP observation journal against
the exact terminal verification revision. Both use the existing authority/CAS,
with payload roots published before the terminal head. No new artifact store,
file writer, model authority or agent loop was introduced.

Limits: PNG 512 KiB and 2048 pixels per side; trace 128 KiB / 256 entries, bounded
strings and explicit truncation. Unavailable/oversized captures have a reason and
cannot change the check outcome. The trace correlates snapshot, verification and
outcome; it is a bounded event journal, not a Playwright archive or full DOM
recording. Typed `OpenWebDiagnostic` and CLI `diagnostic` read exact saved bytes
after restart without browser/source fallback. Metadata excludes binary bodies;
raw output requires redirected stdout. Model results retain semantic check data
without diagnostic references.

Checks: CLI build passes with zero warnings/errors; Harness build passes with two
existing PDF CA1416 warnings. All three `workspace snapshots:` tests pass, including
publication rejection, CAS GC retention, foreign workspace and missing/corrupt
payloads. Real Chromium `web_diagnostics_smoke.py` passes failed-check PNG/trace,
restart after source deletion, Unicode event flood, oversized screenshot and
missing browser. Existing `web_verifier_smoke.py` and
`web_checks_runtime_smoke.py` pass observation, historical replay and guarded
verify/read/repair/read/verify/done regressions. The counter PNG was visually
inspected; local evidence is in `/tmp/rna-m5-browser-diagnostics-20261008`.
Diff, touched links and `ValidateVersionFormat` pass.

This implements the screenshot/structured-trace part of §21.3/M5 with the existing
CDP provider. Abrupt process-loss cleanup/capture, real-model quality and
Windows/Office/WebView2 behavior were not qualified by this slice. Full M4/M5
remain open: fixed autonomous CSV creation/follow-up and real 24K
compaction/restart/resume still lack passing evidence. The previous GPT-OSS
input/memory preflight did not admit those full tasks.

Bounded raw/PNG/JPEG views are implemented in the subsequent slice
[above](#exact-raw-and-raster-workspace-views--2026-10-08). Working-preview UX and Office
UI adoption remain separate open scope. Acceptance requires a compatible real
endpoint with qualified input/memory headroom and unchanged tasks/checks/bounds.

## GPT-OSS input and endpoint qualification — 2026-10-07

Owner: CLI evaluation / environment profile. `6fc913d4` has the same production
sources and binary hashes as `24fd92cc`; existing CLI binaries were reused.
The fixed CSV tasks/checks, 4096 output, system/user roles, json_schema and
16 iterations / 24 tools / 600 s remain unchanged. No production source or
acceptance requirement changed. The full GPT-OSS CSV task and follow-up were
**not run**: input and memory preflight did not admit the tested profiles.

- At 16K, the production compiler rejects the initial CSV context before HTTP:
  5647 estimated message tokens exceed 3196 available after schema and mandatory
  repair/continuation reserves (`PromptBudgetExceeded`, zero tools/files).
  This is a measured `CONTEXT_ERROR` at admission, not a model-quality result.
  A separate 32K compiler probe reaches HTTP and is cancelled before any model
  response. Neither admission probe fabricates a model answer or tool result.
- Simple real v6 `done` probes pass on Ollama 0.35.0 at 16K and 32K: 100 prompt /
  114 completion tokens, 4096 output cap, exact three-field JSON. They do not
  qualify the full catalog, streaming tool trajectory or autonomous task quality.
  The endpoint returns a separate reasoning field despite requested
  `reasoning_effort: none`. Its saved `/api/show` advertises only
  `low|medium|high`, default `medium`; effective reasoning-off is not claimed.
- The 16K probe has normal pressure. The first temporary 32K profile reaches
  warning pressure immediately after its probe. A separate bounded retry requires
  four consecutive normal samples five seconds apart within 30 s and zero new
  paging. All seven qualification snapshots stay at warning level 2 (17% free),
  so no full run starts (`ENVIRONMENT_ERROR` / preflight not admitted).
  No other model is loaded, and all attempts have zero new swap-in/out; existing
  swap remains 747.94 MiB. The retry's complete audit has 16 snapshots, max gap
  4.12 s, peak runner RSS 13.20 GiB (not total GPU/system allocation). This does
  not establish that every smaller profile or machine state is infeasible.

Original profile: `rna-gpt-oss-20b-16k:latest`, digest
`236b951778f336db7e66af76ee93038d1fb7de34a790b89a508756373575aca9`.
Both owned 32K profiles inherit the same weights with only `num_ctx` changed;
digest `dba3fbdbfb5b3780cc9be011ccc429d26d895a56b8bc4840dcbcdf4724e95a1f`.
One isolated model, f16/Flash Attention, prompt cache disabled throughout.
Both temporary profiles were removed, models unloaded and servers stopped;
normal pressure returned. No user applications or swap configuration changed.

Evidence under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-gptoss-input-preflight-udl3p5a_` (16K),
`rna-gptoss-input-preflight-ej_s8hx3` (32K),
`rna-gptoss-protocol-preflight-v1_36y2j`,
`rna-m5-csv-gptoss-lmqu1z8e`, `rna-m5-csv-gptoss-settled-yq490byx`.
Drivers, commands, profile/source/binary hashes, protocol bodies, original
preflight failures and complete memory/cleanup audits are retained. Diff, touched
links and `ValidateVersionFormat` pass; no build or Office validation was needed.

Full M4/M5 remain open. The subsequent bounded screenshot/trace implementation is
recorded [above](#bounded-browser-verification-diagnostics--2026-10-08).
Full CSV creation/follow-up and real
compaction/resume remain separate acceptance gates; a reference run needs both
protocol support and a qualified input/memory budget. Repeating the unchanged
Gemma task or lowering its acceptance does not close those gates.

## Fixed Gemma CSV and compaction acceptance — 2026-10-07

Owner: CLI evaluation / model context. Two real runs use unchanged `24fd92cc`
binaries, tasks/checks and original bounds. No production source, prompts, roles,
grader or budget changed. Profile: `rna-gemma4-12b-32k:latest`, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`,
Ollama 0.35.0, system instructions / user results, json_schema, thinking off,
4096 output. Each run used one isolated model with 32K/f16, Flash Attention and
the server prompt cache disabled. v6 probes passed; models were unloaded and
owned servers stopped afterward. Existing binaries were reused; no build needed.

| Fixed acceptance | Result | Remaining failure |
|---|---|---|
| CSV creation, 32K, 16 iterations / 24 tools / 600 s | Failed at 600.44 s, cancelled (exit 130); 9 iterations / 8 tools, three files created and one verified replacement, no unknown effects. | First browser snapshot passed 12/24 checks; after repair the second passed only 1/24. Current final reads: 0/3. The unchanged feature follow-up was not run. |
| Release notes, 24K planning, 40 iterations / 48 tools / 600 s | Failed at 442.72 s; model `done`, 30 iterations / 29 tools, 10 verified writes, no unknown effects. Separate byte audit passes 10/10 files, unchanged runtime metadata, no extra files. | Current final reads: 2/10; prescribed read/write/read-back order: 0/9 stages. No checkpoint, process loss, external edit or resume occurred. Exact files do not close E-CONTEXT/E-RESUME. |

The browser recovery fix is exercised by the real CSV model: after a refused
unread replacement, an exact read and verified replacement allow the same
`web.verify` to dispatch again. Its new functional failure is not a repeat-guard
refusal. One earlier model response hit 4096 tokens with 106 `renderUI();`
occurrences inside an unterminated patch response. Protocol rejection prevented
dispatch, then normal format repair continued. This is repetition within a single
generation, before ToolRuntime; it does not explain every historical loop.
Failure categories: CSV `MODEL_ERROR`, `PROTOCOL_ERROR`, `VERIFICATION_ERROR`;
release notes `MODEL_ERROR`, `VERIFICATION_ERROR`. No new context-loss or runtime
defect is established. Transport presence alone does not prove model attention.

Canonical audits pass: CSV 10 requests / 9 completed responses, 36 cumulative
call/result pairs; notes 30/30, 309 pairs / 29 unique accepted calls. Exact task
and latest tool result remain on wire; no compaction in either trajectory. The
notes run has eight `source_observation_required` refusals, each followed by a
read and successful mutation, and no protocol rejection. Reported prompt/output
tokens: CSV 79615/10463, notes 213104/1709. No operator hints or workspace repairs.

Memory was sampled throughout: CSV 120 samples, max gap 5.12 s, pressure level 1
in 97 samples and warning level 2 in 23 late samples; notes 89 samples, max gap
5.08 s, pressure level 1 throughout. Both have 0 MiB new swap-in/out and no other
loaded model; existing swap stayed 747.94 MiB. Peak runner RSS: 11.02/10.83 GiB
(process accounting, not total unique GPU/system allocation). CSV timing is not
isolated from late memory pressure; its functional failures preceded the warning.

Evidence under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-m5-csv-head-gemma-9xj_im5t`, `rna-live-compaction-resume-rp_l8tgg` and
`rna-live-compaction-head-gemma-w78bka7i` (endpoint/memory). These retain source,
binary/profile/task hashes, drivers, HTTP/CAS, original failed results and separate
wire/byte/order/memory audits. Diff, touched links and `ValidateVersionFormat`
pass. No new native-role or Windows/Office qualification is claimed.

Full M4/M5 remain open. The subsequent GPT-OSS preflight is recorded
[above](#gpt-oss-input-and-endpoint-qualification--2026-10-07); its full CSV task
was not admitted. Do not weaken acceptance or use a smaller context to claim the
fixed 24K compaction gate passed.

## Application resource views and snapshot preview — 2026-10-07

Owner: workspace application service / filesystem provider / development browser
runner. CLI now has `resources`, `open` and `preview` through typed application
contracts. Discovery returns bounded descriptors and explicit incompleteness;
inert metadata/text views use the existing file owner. Text delivery is bounded
and exact continuation remains pinned across external edits, with fragment coverage
and Unicode boundaries. Missing retained bytes cannot fall back to current files.
Application views never grant model read/write authority.

Preview uses the verifier's existing retained snapshot capture, exact-route server
and browser runner. A fresh Chromium profile has no privileged bridge; preview
adds sandbox/permission/download restrictions and request interception. Only the
captured assets are served. Cancellation/deadline closes this foreground viewer;
no conversation run, verification record or acceptance is produced. Diagnostics
are bounded. No second file reader, context composer, agent loop or artifact store
was introduced. CLI/UI adoption in Office remains the later M7 boundary.

Checks: clean CLI build (zero warnings/errors); `resource_preview_smoke.py` passes
resource/authority, immutable lifetime/historical replay and real-browser isolation
groups. The default visible Chromium path also opens/closes on its one-second
deadline. The existing full `web_checks_runtime_smoke.py` still passes repair,
stale-final rejection and typed retry guards. These are Mac browser and controlled
HTTP checks, not new real LLM or Windows/Office/WebView2 qualification.
Python syntax, added local links/anchor, diff and `ValidateVersionFormat` pass.

The subsequent unchanged real CSV and compaction attempts are recorded
[above](#fixed-gemma-csv-and-compaction-acceptance--2026-10-07); both failed. M4's
development resource/text/preview surface is now available; full M4/M5 remain open.
Working previews, binary/image/PDF viewers, screenshots/traces, hard-process-loss
preview cleanup and Office UI application cutover remain unimplemented or unqualified.

## Browser verification recovery after repair — 2026-10-07

Owner: workspace browser provider / Core typed tool recovery. Failed published
browser snapshots now declare `ChangeRequired` against their exact checked source
revisions. A later successful mutation with verified whole-resource before/after
evidence and a new dependency revision permits an explicit `web.verify` retry.
Fresh reads, unrelated writes, no-op writes, partial/indirect or mismatched impacts
and unknown effects cannot satisfy it. Unknown-call protection remains independent.
No kernel exception, automatic retry, second loop or source authority was added.
The contract survives accepted-history replay; dependency references remain
runtime-only. `NotRun` and failures without a published snapshot retain their
existing behavior. A read of external drift alone does not unlock this recovery.

This closes the provider defect reproduced during the interactive CLI slice and
on extracted `cc6d8c62` CLI/Runtime sources with unchanged Core. Baseline evidence:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-cli-baseline-4lr5xgwk`
(`BASELINE.txt`, `web-retry-baseline.json`).

Checks: clean CLI build; host-neutral Harness build with the two existing PDF
CA1416 warnings. `context continuity:` passes 9/9, including serialized recovery,
exact dependency guards and unknown non-replay. `web_checks_runtime_smoke.py`
passes verify/read/repair/read/verify/done and stale-final rejection. Its additional
guard sequence rejects four ineligible retries and publishes exactly two browser
records, ending with nine passing functional assertions after the verified repair.
All six existing file-read recovery cases pass. These are controlled HTTP/runtime
checks; no new real LLM or Windows/Office qualification is claimed.
Python syntax, changed local links/status anchor, diff and
`ValidateVersionFormat` pass.

The subsequent application resource/preview slice is recorded above.
Full M4/M5 remain open: fixed autonomous CSV creation plus
unchanged feature follow-up, complete real compaction/restart/resume acceptance,
and remaining preview/screenshots/traces scope still need evidence or implementation.

## Interactive workspace CLI — 2026-10-07

Owner: CLI host / workspace application presentation. M4 §11.4 now has bounded
human progress, canonical file-effect summaries, typed questions/answers,
explicit approvals and browser/acceptance summaries. Terminal input and output
enable prompts; `--no-interactive`, redirected input and `--jsonl` never prompt.
Explicit `--interactive` requires a terminal and conflicts with machine mode.
Enter leaves approval pending, only `yes` approves, `no` denies without dispatch,
and Ctrl+C preserves stopped/pending state with the existing exit 130. Restarted
`resume` uses the same question/approval identity and application validation.

The typed application projection is rebuilt from accepted facts, including prior
question turns; it retains the latest 32 file operations with explicit truncation.
Model narrative, failed calls and verified-no-change results cannot claim changed
files. Unknown effects remain separate. Browser status/counts come from the exact
saved verification resource and do not override current-file acceptance. There is
no new persistence owner, context composer, agent loop or automatic tool replay.
User waits occur after the application call releases the run lease.

Checks: CLI builds without warnings; `interactive_smoke.py` passes terminal/pipe
questions, restart, typed single/multiple/free-text answers, native role and limits,
approval/denial/Ctrl+C, external-edit guard, failed/no-change/narrative exclusion
and the 32-operation bound. Its `--browser-only` checks pass nine real-browser
assertions on an already-correct counter snapshot, including rejection of stale
current acceptance. Existing `run_control_smoke.py` and
`application_requests_smoke.py` pass. These use controlled HTTP, not a new real LLM
qualification. Windows terminal/Office behavior remains unqualified.
`ValidateVersionFormat` and the scoped diff check pass.

At this slice, the existing full `web_checks_runtime_smoke.py` failed: after
verified repair of `app.js`, repeated `web.verify(index.html)` was rejected as
`repeated_tool_failure` because the provider declared no recovery contract. The
subsequent [recovery fix](#browser-verification-recovery-after-repair--2026-10-07)
restores the full scenario without weakening repeat guards. The one-pass browser
presentation check remains narrower evidence. Full M4/M5 remain open, including
fixed real CSV creation/follow-up and real compaction/restart completion.

## Canonical read-back order grading — 2026-10-07

Owner: CLI evaluation. The fixed release-note fixture now grades the specified
read → verified mutation → separate read-back sequence from canonical accepted
calls and typed terminal evidence. It correlates exact before/after revisions,
whole file coverage, CAS hashes/bytes and the first accepted call to the next note.
The final anchor review needs a fresh read after all eight notes. Internal tool
verification and late final reads cannot replace the requested intermediate reads.
The recorded marker-only external edit is allowed at its saved restart boundary;
an arbitrary changed revision, partial body, `ok` without effect or unknown effect
cannot qualify. No task, model instructions, runtime, counters or limits changed.

The runner saves `readback-order-audit.json` and `readbackOrderPassed` on every exit,
including completion before checkpoint and timeout. A failed/unavailable order
audit forces overall acceptance to fail; file, continuation and counter checks
remain independent. The shared CAS reader still validates exact bytes and hashes.

Checks: 14 pure grader cases passed, including user/native roles, recorded external
drift and the rejection boundaries above. Read-only regrading of retained logs:
real Gemma `rna-live-compaction-resume-ydo0z0tw` passes 0/9 ordered stages; real Qwen
`rna-live-compaction-resume-mumpkbo8` passes only 3/9; the previously successful
controlled restart/resume trace `rna-live-compaction-resume-hm3xen6k` passes 9/9.
Their canonical HTTP/CAS audits still pass (30/32/36 requests, 38/27/31 accepted
calls). Separate `readback-order-audit-current.json` reports pin the grader hash;
original result/event hashes are unchanged. Evidence is under the temporary root
listed in the aggregate-budget report below.
Python syntax, touched status link, diff and `ValidateVersionFormat` passed.

Current-CLI controlled probes also exercised audit publication on completion
without a checkpoint (`rna-live-compaction-resume-kylf7qev`, order passed) and on a
failed resumed run (`rna-live-compaction-resume-l8x7guur`, order failed). The latter
script did not recover from omitted sources and finished with missing changes;
its file/overall acceptance remains failed. These are controlled endpoint probes,
not new real-model qualification. No C# source changed or rebuild was needed.
Full M4/M5 and Windows/Office evidence remain open. M4 §11.4 interactive CLI was
the next slice here and is implemented in the later report above; full real
compaction/restart/resume completion and fixed CSV acceptance remain open.

## Aggregate source budget and observation notices — 2026-10-07

Owner: Core model context / CLI evaluation. On unchanged `ffd69fee` binaries,
primary Gemma read all eight release-note files in one accepted batch. Each body
fit individually, but together they exceeded the post-compaction request budget:
17106 > 11455 estimated message tokens. A checkpoint was published; no subsequent
model request, process interruption or resume occurred. The bounded real run failed
at 57.51 s, 4 iterations / 10 tool steps, with only the receipt written. All nine
source files were unchanged. This is a reproduced `CONTEXT_ERROR`, without memory
pressure or an unknown mutation effect.

The shared compiler now omits older complete source/text bodies when their total
exceeds the budget, retaining each affected call/result pair, semantic target,
actual outcome and explicit body-omission recovery. Omitted bodies grant no write
authority; a fresh needed read can displace older bodies. Required instructions,
failed diagnostics and unresolved effects remain protected. No budgets, system prompts,
agent loops, durable history or filesystem authority were changed.
The existing correctness test also exposed repeated domain projection erasing a
compiler-created stale-source notice. Such notices now survive without another
domain projection. Its old diagnostic fixture now uses the supported VBA
`details` field; raw `currentSource` is outside the current semantic envelope.

Checks: CLI build clean; host-neutral Harness build with the two existing PDF
CA1416 warnings. Correctness/budget 1/1 (including aggregate reads in user,
developer and tool roles), context continuity 8/8, compaction 8/8 and compiler
presentation 1/1 passed. The current-source check also passed separately. Controlled
CLI omitted-read write refusal and both compaction/restart/resume branches passed.
Version gate, diff and updated local status link passed.
Full M4/M5 and Windows/Office qualification remain open.

Real Gemma `rna-gemma4-12b-32k:latest`, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`,
Ollama 0.35.0: task/fixture/grader, 24K planning / 32K model / 4096 output,
system/user roles, json_schema, thinking off and 40/48/600 bounds are unchanged.
The first HTTP request is byte-identical before/after; subsequent model choices
vary. The controlled regression isolates the aggregate-budget behavior.

After the fix, Gemma reached `done` in **437.46 s**, 30 iterations / 38 tool steps.
Independent grading confirms **10/10 exact final files**, unchanged runtime
metadata and no extra files. The canonical stream records 20 successful reads,
10 verified writes, 8 `source_observation_required` refusals and no unknown effect.
The final request exercises aggregate omission while retaining every call/result
pair. Thirty HTTP requests match canonical CAS, with 309 cumulative pairs and
38 unique completed calls; exact task/latest result are retained throughout.
Reported input/output: 214779/1958 tokens (max 10523/275 per response).

This is **not** full E-CONTEXT/E-RESUME acceptance: no compaction was needed in this
trajectory, so interruption/external edit/resume were not exercised and the
original fixture result remains `failed`. The model also deferred every per-note
read-back to the final read batch instead of following the task's specified order.
At that run, the byte/counter grader did not enforce that intermediate order;
the canonical-sequence assertion is now implemented in the slice above. Final-file success
does not establish full instruction following. No operator repairs or enlarged
limits were used. Fixed CSV creation/follow-up and full M4/M5 remain open.

Memory before/after: 13/88 samples, max gaps 5.09/5.08 s, pressure level 1,
0/0 MiB new swap-out, 0/0.125 MiB swap-in, peak test-server RSS 9.99/10.58 GiB.
No other local model loaded; both owned servers stopped and models unloaded.
Evidence under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-live-compaction-resume-10c0f81o` / `rna-live-compaction-resume-ydo0z0tw`
(source archives/patches, binaries, HTTP/CAS, original failed fixture results and
separate final-byte/sequence audits), paired with
`rna-live-compaction-ollama-v6xi63no` / `rna-live-compaction-ollama-h7v9xgau`
(profile/memory). No real native-role or Windows/Office qualification is claimed.

## Compaction past completed tool receipts — 2026-10-07

Owner: Core model context / tool exchange classification. Reproduced the overflow
on clean `add5c646`, including its shared working set and semantic projections.
The cause was `ToolProtocolMessages.IsCall`: a typed `CompletedOperation` still
has an assistant role and correlation ID, so it was mistaken for an unanswered
call. Prefix protection stopped at the first folded receipt. The fix excludes
completed receipts from call classification while retaining their exact IDs.
The original durable batch still needs all results before any checkpoint boundary.
The earlier budget-selection hypothesis was insufficient; changing budget estimates
did not fix the reproduction and that experiment was removed.

Controlled before/after with the same release-note fixture and 24K/4096, 40/48
bounds: before, 12 tools → five-source checkpoint → `PromptBudgetExceeded`
(11698 > 11455 message tokens). After the one-predicate correction, 30 tools /
31 iterations and three compactions complete the task; all ten expected files
match exact bytes. Canonical audits pass: 34 requests, 133 cumulative pairs,
30 unique completed calls. The fixture's expected runtime manifest is handled by
the separate grader correction below.

Checks: clean CLI build; host-neutral harness build with the two existing PDF
CA1416 warnings. `compaction` 8/8, exchange-scoped deletion 1/1 and both controlled
CLI compaction/restart/resume branches passed. The extended existing regression
compacts past completed receipts, preserves correlation/current task and still
rejects incomplete original calls/batches. Version gate and diff passed.

Real Qwen `rna-qwen35-9b-32k:latest`, digest
`2db395653ce79eb0b834f0d454278c80340c2af548153d55769d15a03f50799c`,
Ollama 0.35.0, same release-note task/fixture and original 24K planning / 32K model /
4096 output, system/user roles, json_schema, thinking off, 40/48/600 bounds:
**checkpoint → process loss → external edit → same-run resume reached**. Three
compactions, no post-compaction budget failure. Exact task/latest results remain
on wire, stale anchor content is excluded, external edit and canonical prefix
survive. Receipt creation appears once. Original limits and counters remain:
15/14 at interruption → 29/25 at timeout; two accepted repeated failures were
`NotDispatched` and correctly uncharged.

Full task **failed at 600.45 s**. Only notes 01–05 reached ready; anchor review
remained pending and receipt contained a literal `\n` instead of a newline.
Four mutations without a delivered current read were rejected; two subsequent
identical attempts were denied before dispatch. No unknown effect or operator
repair. The final model wait was killed by the fixture deadline, so saved run state
remains interrupted/running, not a fabricated terminal success. Categories:
remaining `MODEL_ERROR` / `VERIFICATION_ERROR`; full live E-* acceptance stays open.

32 canonical HTTP requests match proxy/CAS, including one held before forwarding;
30 responses completed and one provider request was interrupted at deadline.
131 cumulative pairs / 27 accepted calls pass the corrected audit. Reported
input/output tokens 264051/2381 (30 usage-bearing responses; max 12041/383).
Memory: 120 samples, maximum gap 5.06 s, pressure level 1, 0 MiB new swap-out,
0.9375 MiB swap-in, peak test-server RSS 8.05 GiB, no other local model loaded.
Temporary Ollama stopped and model unloaded. This live run includes the newer
shared projections; the controlled before/after isolates the predicate fix.

Evidence under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-compaction-budget-repro-trdpsetk` / `rna-compaction-budget-repro-yc6qc9hp`
(controlled pair), `rna-live-compaction-resume-mumpkbo8` (real source archive,
binaries, HTTP/CAS, timeout state and corrected audit),
`rna-live-compaction-ollama-agj823dd` (profile/memory).
Full M4/M5 and Windows/Office qualification remain open. The subsequent primary
Gemma comparison is recorded above; interactive CLI presentation remains the next
UX slice. Fixed CSV creation/follow-up remain separate acceptance gates.

## Live CLI grader accounting — 2026-10-07

Owner: CLI evaluation. The live compaction fixture records WorkspaceStore's
reserved `.rnassistant/workspace.json` before the first provider dispatch. Only
that byte-identical runtime manifest is excluded from extra-file grading. A
changed manifest or any unexpected file still fails. Counter audit now excludes
typed `NotDispatched` results from charged workspace steps; accepted exchanges
must still be unique and complete. These correct false failures in the grader.

Controlled HTTP self-check: checkpoint → process loss → external edit → same-run
resume, all ten exact files and unchanged metadata passed. A read omitted from
the resumed request correctly blocked its patch until a fresh read. The retained
real Qwen trace's two denied repeats also reconcile with the original counters:
13 newly accepted calls, 2 not dispatched, 11 charged steps. The real run still
failed its 600-second task deadline; these grader corrections do not turn it into
a pass. Evidence: `rna-live-compaction-resume-hm3xen6k` (controlled) and
`rna-live-compaction-resume-mumpkbo8` (real), under the local temporary directory
listed in the earlier compaction report. Syntax, exact retained-trace audits and
the version gate passed; no production runtime behavior changed in this slice.

## Shared context and compact tool results — 2026-10-07

Owner: Core model context / ToolRuntime; Office resource gateway and domain result
projection. Implemented the bounded harness simplification plan against the working
baseline; retained the parallel compaction-schema correction below.

- Failed-call guards are released only by matching typed refresh dependencies;
  unrelated Task List/Plan writes do not clear deterministic failures. Recovery is
  stored once on the typed runtime record and rendered at request preparation,
  rather than copied into domain `DataJson`. Manual/UI results expose typed recovery.
- Resources and workspace file mutations use one Core model envelope. Excel/native
  VBA use an explicit Office metadata envelope. New diagnostic fields do not enter
  those model results by default. User text/tables and nested business fields stay
  intact; full diagnostics remain in terminal events. Derived discovery flags are
  no longer separate writable state. No new DTO hierarchy, store or protocol version.
- Core `ContextWorkingSet` replaces the Office-owned implementation and serves
  Office request/preview and CLI. Unresolved effects and current-run recovery are
  mandatory, current bodies precede optional receipts, oversized optional receipts
  are skipped. Mandatory overflow fails before sending a request. Source authority
  and exact call/result pairing remain enforced across compaction and restart.
- Excel snapshot search receives the run cancellation token through the provider;
  nested structural/derived reads retain it and post-read cancellation is checked.
  Synchronous Office calls are not forcibly interrupted.

Checks: host-neutral Harness and CLI builds; 41 focused checks across continuity,
model projections/inspector, structured compaction, VBA semantic contracts, Excel
read/write/search, resource discovery, manual result mapping, Task List and HTML
recovery passed. Scripted CLI resource selectors, Plan (user/tool roles) and
compaction/process-loss/resume (user/tool roles) passed. A failed ambiguous Plan
selector with no exact dependency remains blocked in the same run; a fresh user
turn can select the now unique title. This intentional guard behavior is asserted.
The fixed 8000-character diagnostic fixture produces an actual model request less
than half its raw size while preserving error and typed recovery; arbitrary table
fields are unchanged. This is contract evidence, not a production token benchmark.
Touched local links, diff and `ValidateVersionFormat` passed. Existing two PDF
CA1416 warnings remain. No Office/VSTO validation or new live-model evaluation was
run for this slice. Remaining family projection work is bounded in `MIGRATION_MAP`;
Windows/Office behavior and end-to-end reproduction of the photos remain open.

## Compaction schema and real-model context pressure — 2026-10-07

Owner: Core compaction / CLI evaluation. Added a separate reproducible live
E-CONTEXT/E-RESUME fixture: eight release-note files, exact byte grading, a
single creation receipt, real structured compaction, disposable CLI process loss,
external edit and explicit same-run continuation. HTTP is forwarded unchanged;
no model replies, claims or accepted events are fabricated. The task/graders stay
outside the workspace. This does not replace fixed CSV acceptance.

Baseline binaries: `ba384023`. Both real models failed at compaction because
generated claim kinds cited ineligible sources. The helper always requested
`json_object`, ignoring the selected strict response mode. The shared helper now
honors that setting and builds a schema from fully included source IDs, using
the parser's existing source-kind predicate. Request admission includes schema
cost. Validation, evidence ownership, one helper request and checkpoint format
remain unchanged; no retry or automatic fallback was added.

All runs used Ollama 0.35.0, model context 32K / CLI planning 24K / output 4096,
system/user roles, json_schema selected, thinking off, one model/request and
40 iterations / 48 tools / 600 total seconds. Profile sampling defaults retained.
The same task/fixture hashes and bounds apply; Qwen's first HTTP request before
and after the change is byte-identical.

| Attempt | Observed result |
|---|---|
| Qwen 3.5 9B baseline | 147.07 s, exit 5, `model_infrastructure`; 13 iterations / 12 tools. Invalid claim provenance, no checkpoint. |
| Gemma 4 12B baseline | 411.78 s, exit 5, same provenance rejection; 29 iterations / 37 tools. No checkpoint. |
| Qwen after schema correction | 139.81 s, exit 5, `PromptBudgetExceeded`; 13 iterations / 13 tools. A typed checkpoint was published, but remaining context was 11909 estimated tokens at an 11455-message-token budget. No subsequent model dispatch, interruption or resume occurred. |

The accepted Qwen summary also attributed receipt creation to the manifest-read
source. Kind/source eligibility does not establish semantic truth. Both baseline
models wrote a literal `\n` in the receipt; no operator correction or relaxed byte
grader was applied. Categories: original shared `PROTOCOL_ERROR` generation gap,
remaining `CONTEXT_ERROR` budget gap and `MODEL_ERROR`/`VERIFICATION_ERROR`.
Live E-CONTEXT/E-RESUME remains open; a published checkpoint alone is insufficient.

Audits: 13/29/13 canonical requests matched proxy bodies and CAS hashes;
51/269/56 cumulative call/result pairs, complete unique accepted IDs, latest
results and exact task retained, no runtime resource URI on wire. No unknown
write effects. Input/output tokens: 86717/925, 196162/2042 and 91242/833.
Memory: 31/84/29 samples, maximum gap 5.10 s, pressure level 1 throughout,
zero new swap-out; swap-in 0/0.109/0 MiB. No other local model loaded; temporary
servers stopped and models unloaded. These failures showed no memory pressure.

Checks on an isolated `ba384023` checkout plus this schema/test change: clean CLI
build, harness build with the two existing PDF CA1416 warnings, `compaction` 8/8
and both controlled CLI compaction/resume branches passed. These cover strict and
explicit json_object transport, source promotion, unknown IDs, schema budget,
checkpoint publication, native pairs and original counters. Version gate passed.
Concurrent main changes were excluded from those builds. Windows/Office and full
M4/M5 remain unqualified.

Exact binaries, source archive/patch, task/fixture, model profile, HTTP/CAS and
audits are retained locally under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-live-compaction-resume-tkgrqw_j` (Qwen baseline),
`rna-live-compaction-resume-s8gzwtvd` (Gemma, same pinned baseline binaries),
`rna-live-compaction-resume-u3hrrps5` (after fix). Endpoint/memory evidence:
`rna-live-compaction-ollama-wgqmh537`, `rna-live-compaction-ollama-v0tm9czt`,
`rna-live-compaction-ollama-g0wswk64`, respectively.

Next priority: diagnose prefix selection against the actual compiled message
budget/current-source floor, then repeat this unchanged live continuation case.
The concrete open scope is recorded in [BACKLOG](BACKLOG.md). Interactive CLI
presentation follows this context-reliability work; fixed CSV acceptance and its
feature follow-up remain separate open gates.

## Shared batch schema and Qwen CSV regression — 2026-10-06

Owner: Core ModelProtocol / CLI evaluation. The unchanged fixed CSV test on
Qwen `rna-qwen35-9b-32k:latest` (digest
`2db395653ce79eb0b834f0d454278c80340c2af548153d55769d15a03f50799c`)
exposed a schema/parser mismatch: strict generation allowed up to 32 arbitrary
calls, although the parser allowed only independent local reads in a batch.
The model repeatedly proposed three file creations. All were rejected before
dispatch; repaired responses read nonexistent files, then repeated those failed
reads. Runtime stopped at `repeated_tool_no_progress`, without file effects.

Baseline: HEAD `92dd57e8`, runtime binaries identical to the previous Gemma control;
only model/profile changed. Same task/checks, 32K/4096, system/user roles,
json_schema, thinking off, one model/request and 16/24/600 bounds. Model sampling
defaults are retained in the profile evidence. Result: 243.70 s, exit 5, four
iterations, three failed reads, no files, failed acceptance, no follow-up. Three
protocol rejections, 7/7 HTTP requests/responses, 18 valid cumulative call/result
pairs, no missing latest result or compaction. Input/output tokens 38135/8329
(max 5719/2988). Memory: 50 samples, max gap 5.06 s, pressure level 1 throughout,
0 MiB swap-in/out; existing swap 987.94 MiB, peak runner RSS 7.35 GiB, no other
model loaded. This failure was not accompanied by observed memory pressure.

The common strict schema now uses the parser's explicit frozen batch authority:
up to 32 authorized reads, or one other call. Mixed read/mutation batches and
multiple mutations are excluded; every exact argument contract occurs once.
Missing/incomplete authority fails schema construction. CLI, Office request,
budget and inspector paths supply that authority. Prompt schema deduplication
matches unique exact names/contracts across the two array alternatives. Local
parser/kernel guards remain mandatory, including for `json_object` and action/call
correlation. No protocol version, prompt, acceptance threshold or retry limit changed.

Checks: clean CLI build; host-neutral harness build with the same two PDF
CA1416 warnings. v6 13/13, ModelProtocol 15/15, request-schema budget/projection
2/2, immutable context 1/1, tool pack 8/8, compatibility 2/2, agent schema 4/4
and affected model-admission paths 3/3. CLI response-mode/role/approval and resource
selector smokes passed. These include actual HTTP schemas, contradictory batch
policy, incomplete authority, bounded repair and unchanged exact contracts.
Diff, touched document target and `ValidateVersionFormat` gates passed.

After-fix Qwen run: **355.55 s, exit 5, `iteration_limit`, failed acceptance**.
Sixteen model iterations / 18 tool steps; three files created separately,
four verified patches, six successful file reads. All 16/16 model responses
satisfied v6, with **zero protocol rejections**. The only batch was three reads.
Four browser checks progressed 1/24 → 8/24 → 12/24 → 12/24; changing category
still lost the selected sort order. A patch without a current complete read was
blocked, then the model reread and patched. The last patch exhausted the iteration
budget before final read/verification: only 2/3 current file reads, no passing
snapshot and no follow-up. The operator supplied no repair or continuation hints
and did not increase limits. Categories: remaining `MODEL_ERROR` /
`VERIFICATION_ERROR`; the reproduced schema/parser defect is fixed.

The paired test kept the Qwen profile, task, checks and limits unchanged. The
first HTTP body differs only in `response_format`; all 13 argument contracts
are identical. 16/16 HTTP requests/responses and 102 cumulative call/result pairs
passed the CAS/wire audit; latest results and the user task remain present.
Tokens 186946/4922 (max 17302/1699); no compaction. Memory: 72 samples, max gap
5.06 s, pressure level 1, 0 MiB new swap-out / 0.125 MiB swap-in, existing swap
987.94 MiB, peak runner RSS 7.66 GiB, no other model loaded. Both temporary servers
were stopped and models unloaded. These runs demonstrate the specific correction,
not general model reliability or real-model compaction qualification.

Source archive plus exact working-tree patch, binaries, model profile, drivers,
HTTP/CAS, browser results and audits are retained locally:
baseline `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-qwen35-svn31qa3`;
after-fix `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-qwen35-batch-schema-yo5ityvs`.
Full M4/M5 and Windows/Office qualification remain open.

## Fixed CSV evaluation and native-role comparison — 2026-10-06

Production HEAD `1c457044`, clean CLI build, fresh disposable workspaces. Same
four task/check files, Gemma `rna-gemma4-12b-32k:latest` digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`,
Ollama 0.35.0, 32K/4096, system instructions, json_schema, thinking off and
16 iterations / 24 tool steps / 600 s per phase. Each temporary server used one
model/request, Flash Attention, f16 KV and disabled retained prompt cache.
Owner: CLI evaluation / shared model-context and acceptance evidence.

| Attempt | Result |
|---|---|
| Unchanged `user` result role | 160.16 s, `model_done`, exit 5, acceptance failed. Three verified file creations and **24/24 browser checks passed**, but **0/3 final file reads**. Five iterations, four tool calls. The last HTTP request explicitly contains 3 remaining reads and all three paths; the user task and latest result remain visible. Neither published skill was read. |
| Separate native `tool` diagnostic | Only result role changed. 78.56 s, `no_tool_progress`, exit 5, acceptance failed. One verified `index.html` creation, then three accepted `continue` responses with no calls. Two rejected responses: a trailing tool-response marker and `action=continue` with a file call. Four kernel iterations, six HTTP attempts; no browser check or file read. |

Both v6 probes passed. Paired source archives/binaries, task/check hashes, model
digest and other settings match; the entire initial HTTP body is byte-identical.
All 5/5 and 6/6 HTTP requests/responses were retained in canonical events/CAS;
10 JSON/user-role and 5 native-pair checks found no broken exchanges, missing latest
results or runtime resource URIs. The native failure is model/protocol behavior
on this endpoint, not an observed missing tool result. A single attempt per role
does not establish their general quality or the model's internal attention.
Categories: first `MODEL_ERROR` / `VERIFICATION_ERROR`; native `MODEL_ERROR` /
`PROTOCOL_ERROR`. The existing guards rejected both runs without repeating effects.

Input/output tokens: 34059/2717 (max 8953/1636) and 34544/1178 (max 6141/492).
No compaction occurred: these requests fit 32K, so real-model compaction quality
remains unqualified. Neither creation met full acceptance; follow-ups were not run.
No operator hints, file repair or increased limits. Full M4/M5 remain open.

Memory: 33/17 samples, maximum gaps 5.10/5.09 s, no sampling errors, pressure level
1 throughout and **0 MiB new swap-out** in both runs. Swap-in was 0.125/0 MiB;
existing swap stayed about 995.94 MiB. Peak runner RSS was 10.35/11.13 GiB (not
total unique CPU/GPU allocation); no other local model was loaded. Both test
models were unloaded and temporary servers stopped. These failures were not
accompanied by observed memory pressure or context overflow.

Exact source archives, binaries, drivers, profiles, HTTP/CAS, browser result and
audits are retained locally:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-compaction-gemma-cfm_0eor`;
native comparison:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-native-gemma-gg9tk44o`.
This slice changes evidence/status only; Windows/Office remain unqualified.

## Shared compaction and CLI continuation — 2026-10-06

M5 E-CONTEXT/E-RESUME integration slice, owned by conversation application services.
The previous CLI path only failed when history exceeded budget; it now invokes
Core `ContextCompactionRuntime`, extracted from the Office service. Both hosts use
the same structured provenance, prefix selection and ModelContextCompiler. Office
keeps host capture/domain projection. One bounded LLM summary request creates the
existing checkpoint artifact; there is no second agent loop, store or filesystem
reader. CLI saves before using the checkpoint and recaptures frozen authority
after the model wait. A remaining budget failure is explicit.

CLI context normalization now preserves durable message IDs. Checkpoints cannot
split the original accepted batch when presentation expands it into separate
pairs. The latest user task remains verbatim; claims are filtered against current
authority. Kernel resume still restores the complete accepted call history,
original counters and criteria. Summaries provide no exact-source write authority.

Checks: CLI build clean; host-neutral harness build retains the two existing PDF
CA1416 warnings. `compaction` 8/8, `shared context:` 2/2 and `context continuity:`
8/8, exchange-scoped deletion 1/1, diff and version gate passed.
Controlled HTTP/process smoke passed both `user` premature-done and
`tool` repair branches: 24K budget → genuine checkpoint → process stop → external
edit → same-run resume. Stale claims disappear, stale writes fail, current reads
are required, previous effects stay single and counters do not reset. Existing
run-control, response-mode/role, read-acceptance (3 cases) and omitted-read smokes
pass. The omitted-read fixture moved from 16K, which cannot admit today's catalog,
to 24K; the omitted body and refused-write assertions remain unchanged.

This closes the scripted integration gap identified in the previous next slice.
Real-model E-CONTEXT/E-RESUME quality and fixed autonomous CSV creation/follow-up
remain open, as do the rest of M4/M5 below. No new real LLM or Windows/Office
qualification is claimed. The implementation follows §21/M5 without narrowing
the target architecture or changing task acceptance.

## Workspace plan reconciliation and event subscription — 2026-10-05

Reviewed HEAD `cd068c86` against the supplied
`RNAssistant_WORKSPACE_FIRST_UNIFIED_AGENT_IMPLEMENTATION_PLAN.md` (§8.1, §11,
§21, M0–M11 and A01–A27), production entry points, project references and focused
checks. This table supersedes the older milestone snapshot below. Owner:
conversation application services; resource/Office boundaries retain their owners.

| Stage | Current implementation and remaining scope |
|---|---|
| M0 | Baseline, ADR-0013, migration map and SDK limits recorded. Done as a planning baseline; Windows builds were not qualified. |
| M1 | Independent production Core/Runtime/CLI uses ProjectReference, LlmClient/v7, coordinator → AgentKernel, ToolRuntime and the same frozen compiler. No fake Office dependency or second loop. Office/CLI application preparation and handler composition remain separate; full shared application/lifetime cutover is open. |
| M2 | Workspace/session without document authority, stable identity, known root relocation and read-only association tested. Only the root directory mount is supplied; managed/multiple/external mounts and shared-source identity/heads (A24) remain open. |
| M3 | Single WorkspaceFileService owns UTF-8 find/read/create/patch/replace/copy/move/delete/restore, bounded raw/PNG/JPEG views, exact history, drift and non-replay recovery. Focused tests pass. Remaining §8.1 large/format-specific views, directory pagination, complete Office/filesystem dispatch and cross-platform path qualification are open. |
| M4 | First useful real-model multi-file result achieved. Modes, skills, typed questions/answers, approvals, Task Lists/Plans, receipts, cancel/explicit continuation and event following exist. Interactive host UX/recorded file-effect presentation passes Mac PTY checks. Typed application resource discovery, bounded exact text views and isolated development snapshot preview are implemented. Office UI use of shared application contracts is the later M7 cutover. Scripted runs do not close real task quality. |
| M5 | Retained browser snapshots, dependency checks, structured functional verification, repair, bounded same-run continuation and shared compaction implemented. Controlled compaction/resume passes. Schema, completed-receipt boundary and aggregate source-budget defects are fixed. Real Qwen reached restart/resume but timed out. The latest Cloud Gemma 24K run retains 10/10 exact files and 9/9 ordered stages but only 9/10 final reads, without a checkpoint/restart. Full live quality remains open. A real counter repair passed previously; typed dependency-change recovery passes controlled guards and real Gemma dispatches re-verification after repair, but its CSV result still fails. Isolated development snapshot preview passes Mac lifecycle/isolation checks. Bounded PNG/CDP diagnostics have exact retained references and pass browser/CAS checks. The latest Cloud CSV with thinking on passes 24/24 browser checks with normal memory pressure, but 0/3 final reads prevents acceptance and follow-up. Bounded PNG/JPEG assets pass snapshot replay and stale-final checks. Working previews/additional formats remain open; Ctrl+C/expiry and abrupt caller-loss cleanup pass on Mac. Helper/OS loss and other platforms remain unqualified. |
| M6 | Authoring cutover not started. Office HTML/MD/JSON and Plan authoring remain on their existing owners; file materialization/linkage/crash guards and old-writer removal are required. CLI files have no dual writer. |
| M7 | Receipts/cursors/subscription provide shared primitives; Office UI transport/application migration, file tree, preview isolation, bounded body delivery and pane-independent lifetime are not closed. |
| M8 | External Office provider/mount/target cutover remains open; exact binding/discovery/SaveAs and multiple-instance behavior need Windows evidence. |
| M9 | Dedicated safe Excel launcher/standalone UI cutover and Windows qualification remain open. |
| M10 | Explicit VBA source export/compare/apply and workspace file/history transfer remain open. |
| M11 | Replaced-writer cleanup, final packaging/performance/policy parity and qualification matrix remain open, dependent on the prior cutovers. |

The staged CLI milestone follows §23; it does not shrink §8.1 or replace the
single-application target in §11. `ResourceProviderRouter` is still a router, while
typed read contracts and ModelContextCompiler are shared. Corrected the stale ADR
delivery paragraph that still called CLI context assembly a separate future path.
No new live-model or Windows/Office/WebView2 qualification is claimed.

New slice: `SubscribeEventsAsync` / CLI `events --follow` reuse the canonical
IEventStore owner, opaque cursors and bounded metadata pages. Delivery is awaited;
Ctrl+C/timeout stops the subscriber, and unavailable/changed history stops with
resync. No lifecycle writes, model calls, mutation replay or new event store.
The old feed path that materialized the entire event list is removed. Initial or
changed streams receive full streaming validation; unchanged pages/idle polls
reuse exact byte boundaries. External append cannot mask a corrupt earlier record.
Changed-file validation and initial session scope loading remain O(history).

Checks: CLI build clean; harness build with the same two PDF CA1416 warnings.
`event feed:` 3/3, `kernel:` 55/55, `workspace:` 2/2, `workspace files:` 7/7 and
JSONL byte offsets 1/1. Diff, touched local document targets and version gate pass.
CLI event subscription, input receipt/reconnect and run-control process smokes pass
with controlled HTTP. These cover exact continuity, subscriber/run cancellation
isolation, bounded pages, backpressure, prefix/tail corruption, no repeated writes
and preserved continuation budgets. Existing unrelated worktree edits are outside
this slice.

CLI cancellation and bounded continuation slice (2026-10-05): typed controls bind
session/run to the exact canonical inspect cursor. `cancel` signals the local
process holding the run lease through an ephemeral current-user pipe; only that
kernel records lifecycle/effects. Acknowledgement is not completion. Stale state
and unconfirmed delivery are explicit, and a delayed request cannot cancel a
later continuation. Run/answer/approve/continue share Ctrl+C handling; cancelled
invocations now emit `run.cancelled` and exit 130. Owner: conversation application services.

Explicit `resume --continue` uses the shared coordinator/kernel with the original
run, task/acceptance, call history, limits, spent iterations/tool steps and known
outcomes. A mandatory append claims the checkpoint before model dispatch. Closed
history and remaining iterations are required; in-flight, excluded or unknown
effects, pending approvals and completed model decisions cannot continue. Narrative
and rejected-call guards retain their state. Plain `resume` still reconciles and
reports without model calls. There is no new queue/store or agent loop.
Existing stream lifecycle owners close abandoned model steps and record resumed
turn segments under the same turn ID; previous terminal facts stay intact.

Checks: CLI build clean; kernel 55/55 and storage turn lifecycle 1/1. Scripted HTTP/process smoke covers active
cancellation, rejected late tool response, same-run native pairs, no repeated file
write, preserved budgets, stale/conflicting requests, lease exclusion, process loss,
exhausted budget and a verified deletion followed by cancel/continue. Input receipts/
reconnect, modes/questions 3/3, response roles/approval inheritance and plain
interrupted-run recovery also pass. Harness retains two existing PDF CA1416 warnings.
No new real-model or Windows/Office qualification. Full M4/M5 remain open: push
events/interactive UX, UI adoption, remaining resource views and the fixed CSV
creation/follow-up still need their own evidence.

CLI input receipts and event reconnect slice (2026-10-05): `run` and `answer`
now use typed `SubmitInputAsync`. Core `ConversationInputJournal` owns existing
input event/CAS serialization for both the Office inbox and workspace application.
Caller `--request-id` binds the submitted input to its original accepted run;
duplicates return its receipt, changed input conflicts. Duplicate lookup works
while a run is active and with the model offline, without adding events, another
run, acceptance re-evaluation or file effects. Owner: conversation application services.

`session create` permits stable session identity before submission. Typed
`ReadState`/`inspect` returns a canonical sequence/hash cursor. Core event feed and
CLI `events` return bounded metadata pages with pinned high-water; invalid or lost
history produces explicit resync. Kernel counters, model context and mutation
authority are unchanged. Receipt delivery status is not run success; a crash before
delivery or during a run never causes duplicate intake to resume/replay work.
No second journal, queue store, event schema or agent loop was added.

Checks: CLI build clean; shared inbox 9/9 and typed event port 1/1. Scripted CLI
request/reconnect smoke passes, including active/older/abandoned input, a canonical
prefix ending immediately after acceptance, SQLite projection removal, pinned
paging during appends and unavailable/corrupt history. Modes/questions 3/3 includes
duplicate/conflicting typed answers; response roles/native pairs and guarded
approval inheritance pass. Harness build retains two existing PDF CA1416 warnings.
No new real-model or Windows/Office qualification. Output is bounded but event
reads still scan full history; push subscription, interactive UX and Office UI
adoption remain open. Cancellation/continuation are covered by the newer slice. Full
M4/M5 remain open; the failed fixed CSV task/follow-up evidence is unchanged.

Shared CLI Markdown Plan slice (2026-10-05): Plan mode exposes save/restore/delete;
Agent exposes save. Core `PlanDocumentService`, catalog/handler and
`PlanDocumentPublication` now serve both hosts. Office-only implementations are
removed; Office retains its DocumentAccessGate wrapper. The existing artifact
store supports explicit workspace ownership for Plan records only, without a
fabricated document identity. Owner: conversation/planning runtime.

Workspace Plans retain exact Markdown, immutable metadata and linear history in
existing CAS/authority. A prepared mutation publishes snapshot, logical head and
operation receipt after exact body read-back, then persists chat links/selection.
No second writer, store or loop. Plans are service artifacts, not mounted `.md`
files; file-backed authoring remains M6 scope. Save/restore/delete and read-only
file policy do not contribute to file-write acceptance.

CLI `common.resources_find/read type=plan` supports bounded discovery (100 heads,
50 results), exact unique title/current selection and explicit historical versions.
Incomplete discovery cannot prove title uniqueness. Current reads select their
exact revision and depend on the logical head; historical reads do not change
selection. Shared frozen compilation invalidates old current reads after updates.
`inspect.plan` / required `RUNTIME_CONTEXT.active_plan` expose metadata only.
Same-workspace chats share the owner; stale saves reject before dispatch and
another workspace cannot read the Plan. Prepared attempts are abandoned, published
attempts recover only the journal link, and uncertain effects become unknown without
replay. Explicit uncertain Plan/Task List reconciliation is still open.

Checks: CLI build clean; Plan mode 3/3, native Plan 3/3, shared document Plan 2/2,
artifact working set 3/3, dependency direction 2/2. CLI scripted HTTP: Plan 2/2
(native tool and user roles), Task List 2/2, modes/questions 3/3, selectors 1/1.
Plan checks cover exact whitespace, restart, current/historical evidence, competing
chat writes, frozen selection within read batches, duplicate titles, restore/tombstone,
isolated ownership, publication recovery and no replay/file-write credit. New/moved old-style project includes
were checked directly. The known SDK-unaware all-source guard still fails (187
reported entries); its existing BACKLOG item remains open. Harness build retains
two existing PDF CA1416 warnings. These are host-neutral/scripted checks, not new
real-model or Windows/Office qualification. Full M4/M5 remain open; the subsequent
§11 accepted-input/reconnect slice is recorded above.

Shared CLI Task List slice (2026-10-05): Plan and Agent now expose
`common.task_list_set` through Core's existing domain service, catalog and handler;
the Office-only copies are removed. Core `TaskListPublication` owns logical
conversation publication for both hosts. Office retains DocumentAccessGate;
workspace composition uses the existing session lease plus a short authority lease,
prepared/dispatch journal, ChatStore append/CAS save and durable revision read-back.
No document identity, second store, file writer or agent loop was introduced.
Owner: conversation/planning runtime.

The CLI supports save/reorder, progress and notes, blocked resumption and closure.
`inspect.taskList` and required `RUNTIME_CONTEXT.active_task_list` expose the current
identity-free assessment checked against frozen authority. Runtime IDs stay out of
model results in ordinary and native tool transport. Task List writes cannot
satisfy file-write acceptance. A prepared undispatched operation is abandoned;
a published result recovers its journal link; an uncertain dispatched operation
becomes unknown and rejects another save. Explicit uncertain Task List
reconciliation is still open; no automatic mutation or restoration is attempted.

Checks: CLI build clean; shared Task List harness 4/4, Plan mode 3/3; CLI Task List
scripted HTTP 2/2, modes/questions 3/3 and read-acceptance context 3/3. Architecture
filter 4/5: the existing Office conversation-port assertion flags 13 unchanged
ChatStore calls, also present verbatim in `cf662c60`; recorded separately in BACKLOG.
Harness build retains two existing PDF CA1416 warnings. This is host-neutral and
scripted evidence; no new real-model or Windows/Office qualification was performed.
Markdown Plan with actual workspace authority was completed by the slice above; full M4/M5 remain open.

Clean-memory CLI evaluation (2026-10-05): the previous CSV runner log, matched to
its last HTTP request timestamp, recorded 8013.399 MiB of retained prompt cache
(8192 MiB cap), separate from the live inference state. A temporary Ollama 0.35.0
server on loopback used `LLAMA_ARG_CACHE_RAM=0`, explicit Flash Attention, one
loaded model/request and unchanged f16 KV / 32K. Runner logs confirm those settings
and GPU offload. No system pager/swap-file changes or application force-quits.
Owner: CLI evaluation / local LLM environment; no production code changed.

The retained `7d924d76` RNAssistant binaries, model digest, all four task/check
files and model settings match the previous CSV run. The complete initial HTTP
JSON body is identical. Thus later schema-39 edits are excluded from this memory
experiment. Server settings and host state changed; this is not a single-factor
speed benchmark. Existing swap was not zeroed; measured deltas distinguish it
from new paging. The temporary server/model were stopped after this run.

The original 16-iteration / 24-tool run failed at **537.07 s**, `iteration_limit`,
before the old 600 s boundary. Six verified file changes, 0/3 current final reads,
no accepted current browser verification. Browser failures progressed from clipped
chart (9 passed), through category-reset ordering (12 passed), a newly introduced
syntax error, then initial row ordering (1 passed). The model did not finish;
the feature follow-up was not started. Sixteen HTTP requests/responses, 88 pair
checks without errors, no format rejection; tokens 137836/5665, maxima 12942/1754.

Memory telemetry: 108 samples, at most 5.09 s apart, no sampling errors;
pressure level 1 throughout, **0 MiB new swap-out**, 71.33 MiB swap-in, existing
swap 3649.62 → 3577.62 MiB. Minimum free RAM 2.34 GiB; peak runner-process RSS
12.07 GiB (not total unique CPU/GPU allocation). No other model was loaded.
This reproduction did not encounter observed RAM pressure; the stopping condition
was the software iteration limit with still-failing application checks. It does
not prove that every earlier failure had the same cause. Exact source/binaries,
driver, server log, telemetry, initial-wire and result audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-clean-memory-gemma-m_oanimy`.

A separate diagnostic run used a new empty workspace and **32 iterations / 48
tools**, with the same inputs/profile/binaries and the same initial HTTP body.
Its old 600 s checkpoint was recorded as unfinished; it was cancelled at the
declared 1200 s diagnostic bound (1200.71 s), exit 5. Six verified file changes,
0/3 final reads, no accepted current verification; no feature follow-up. Nineteen
HTTP requests / eighteen responses, 114 pair checks without errors; two invalid
action batches were rejected by the existing protocol gate. Tokens 229353/16370,
maxima 22758/2208. The last browser result was 17 passed / 1 failed / 6 not run,
`invalid-message`: the generated code calls `showStatus`, then `resetUI`, which
immediately clears that message. An explicitly operator-initiated `verify` after
termination reproduced the same failure (2.36 s); source hashes stayed unchanged.
This is a visible generated-code defect, not a demonstrated lost tool result.

The diagnostic run has 240 memory samples, maximum gap 5.09 s, no sample errors:
pressure level 1 throughout, **0 MiB new swap-out**, 121.77 MiB swap-in, existing
swap 3577.62 → 3457.62 MiB. Peak runner-process RSS 12.06 GiB. Raw free RAM briefly
reached 0.08 GiB, but `memory_pressure -Q` never reported less than 46% and pressure
stayed normal; a raw free-page minimum alone is not the memory-pressure assessment.
No other model was loaded. Temporary server stopped and model unloaded after both
runs. No forced memory purge, pager change or lowered verification requirements.
The memory concern was tested; neither original nor expanded run completed the
CSV task. Full M4/M5 remain open. This diagnostic does not replace the original
16/24/600 acceptance result. Evidence including the independent final check:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-clean-memory-extended-gemma-vjzeu8rv`.
The repeatable preparation rules are in [development rules §9](../development-rules.md#сравнение-моделей).

Desktop run/settings repair (2026-10-05): screenshots show three no-call Agent
`continue` decisions ending in `no_tool_progress`; the v6 kernel correctly
stops that loop, while default schema 38 guidance allowed repeated preparation
narration. Schema 39 now directs the next needed tool call or terminal decision
after skill/schema loading. A separate reproducible settings fault came from
normalized prompt settings diverging from the committed prompt publication:
`saveSettings` rejected the stale head. Startup now archives old prompt text,
persists the current schema and commits the current prompt publication through
the existing mutation/read-back barrier, including the case where another save
already persisted the new marker. Focused host-neutral migration, dispatch,
kernel and web projection checks pass. Windows/Office/WebView2 behavior remains
unqualified; the cross-document panel switch guard still rejects switching
while a run is active.

Latest fixed CSV evaluation (2026-10-05): **failed**, cancelled by the unchanged
600 s phase bound (600.12 s, exit 5). Model/digest, 32K/4096, system/user roles,
json_schema, reasoning off, sequential requests, 16 iterations / 24 tools and all
four task/check files match the prior skills CSV run byte-for-byte where applicable.
Declared cumulative changes are mode/questions, shared schema correction and
workspace selector contracts; this is not single-factor attribution. Binaries and
source were captured at `7d924d76`; the concurrent schema-39/settings changes in
`dd3ab212` were not in this run (confirmed against all 14 actual HTTP schemas).

Three files were created; one patch and one replacement changed app.js, and one
replacement verified no change (five verified changes total). Both dispatched
browser checks passed 9/24, failed `filtered-chart` (hidden/clipped bar), leaving
14 not run. A third identical `web.verify` was correctly blocked: the immediately
preceding replacement had `changed:false`, so there was no new source state to
verify. That explicit no-change result was present in HTTP context. A later
replacement changed app.js, then model wait hit the wall bound. Current final reads
were 2/3; no verified current snapshot, no operator repair and no follow-up run.
Neither published skill was read. The failed repeat is not a demonstrated kernel
guard defect, and this run does not establish a universal model/loop cause.

Fourteen HTTP requests / thirteen responses, 83 pair checks without errors and
zero format rejections. Tokens 115225/9570 total, maxima 16215/1904. No reported
context/OOM error; swap use rose 3268.56 → 6431.44 MiB on this non-isolated Mac,
so this is not a clean speed/capacity benchmark. The model was unloaded. Source,
binaries, task/profile comparison, HTTP/CAS and result/wire audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-csv-selectors-gemma-ye__blon`.
Full M4/M5 remain open; the narrow native question pass below is unaffected.

Previous workspace selector slice (2026-10-05): CLI resource schemas now use closed
file/skill alternatives in the prompt, structured output and exact runtime gate.
File reads cannot carry `referencePath`; skill discovery cannot carry `directory`.
Skill type is explicit, file type still defaults, and Chat retains its file-only
catalog. Root directory omission/null/empty is documented; `/` and `.` stay
rejected with the same file-owner code and an actionable root-selection hint.
Owner: workspace resource admission. File authority, bounded discovery, exact
evidence, frozen compiler, mutation/recovery and the single kernel are unchanged.

CLI build passes without warnings/errors. The new scripted selector regression
passes actual-wire schemas, pre-dispatch rejection/frozen repair, nullable defaults,
unsafe roots, relative discovery and isolated file/skill evidence. Existing
Chat/Plan/Agent question cases (3/3) and shared skills runtime checks also pass.

The unchanged narrow Gemma question task now **passes**: 20.94 s to one accepted
question, exit 3; explicit test-user Table selection, then 23.44 s to `done`, exit 0
and passed acceptance in a new CLI process. Each run reads notes.txt freshly;
the final plan incorporates Table, and user files are unchanged. Five HTTP
requests/responses, six pair checks without errors, zero format rejections;
tokens 9301/545 total, maxima 2166/202. Task/model digest/profile/bounds match the
previous failed native run; declared changes are selector alternatives and their
guidance, on top of schema fix `536b1b32`. This qualifies the narrow interaction,
not autonomous clarification quality, every historical loop or full M4/M5.
Model unloaded; source/binaries, HTTP/CAS and acceptance/wire audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-selectors-gemma-_679rgxs`.
Windows/Office/WebView2 qualification remains open.

Previous structured-schema correction (2026-10-05): the saved native question
request revealed a reproducible harness bug, not merely a missing model field.
`ForStructuredOutput` recursively removed every JSON property named `description`
or `default`, including argument declarations. Thus the question option schema
required `description`, omitted it from `properties`, and forbade additional
properties: no option could satisfy it. The same unrestricted walkers could
rewrite object-shaped `const`/`enum` literals. Owner: Core tool-schema projection.

All four schema transforms now visit only schema positions (`properties` values,
`items`, `anyOf`). Argument names and literal values survive; actual schema
annotations are still stripped, optional nulls/strict objects/alternatives keep
their existing rules. Prompt projection and structured output share that traversal;
source schemas remain immutable. No parser relaxation, repair-budget change,
endpoint-specific prompt or additional agent loop was introduced.

The real-question wire regression failed before the fix and passes afterward,
including the runtime gate, named fields, literal constraints and immutable source.
Focused host-neutral checks: schema 4/4, strict constraints 1/1, controller catalog
1/1, v6 schema/parser 2/2; all three scripted CLI mode/question cases pass. CLI
build has no warnings/errors; harness retains two existing PDF CA1416 warnings.
Windows/Office/WebView2 qualification remains open.

The same native Gemma question task still fails at file selectors (51.53 s, exit 5,
no_tool_progress), before an accepted question. Ten HTTP requests/responses,
24 pair checks without errors; two format rejections for surrounding prose.
Tokens 17523/773 total, maxima 2185/147. Both a file read with `referencePath`
and root find with an unsafe directory were rejected; repeated failures were not
redispatched. Zero successful reads/writes; no answer phase. Initial HTTP requests
are identical except for the restored `description` declaration; task, model
digest, profile and bounds are unchanged. This isolates the schema correction,
but does not qualify real question completion or explain every historical loop.
Model unloaded; source/binaries, HTTP/CAS and audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-schema-names-gemma-ayxqwnsp`.
Selector-contract clarity is the next local slice; M4/M5 remain open.

Previous CLI mode/question slice (2026-10-05): Core `ConversationRunPolicy` and
`UserQuestionToolCatalog/Handler` now serve Office and CLI; the Office-only copies
are removed. CLI `--mode chat|plan|agent` selects a persisted session mode, rejects
unknown values and mode drift, and filters both model schemas and exact runtime
registrations. Chat exposes only file find/read, without skill selectors. Plan
exposes reads, enabled skills, available web verification and questions, without
file writes or approval. Agent retains guarded mutations and can ask questions.
CLI Plan documents/checklists are still absent; this is not full Plan parity.
Owner: shared conversation policy/questions and workspace application admission.

Typed questions terminate the same kernel with `awaiting_user`; CLI emits
`run.needs_input`, exit 3 and the accepted question set. `inspect`/`resume` recover
it without model work. `answer --answers-file` validates run/set/question/option
identity and answer constraints under the session lease, then starts an explicit
new user turn through the same coordinator/kernel. Only semantic answer text
enters model context. Shared projection removes question IDs while preserving
native call/result pairing. Rejected/stale/replayed answers do not change history.
The existing event stream remains the only pending source. No second agent loop.

Acceptance remains pending while waiting. Typed answers carry criteria and prior
question-run write receipts forward, so already verified file changes need not be
repeated. This runtime-only lineage grants no file observation/overwrite authority;
new-turn reads and web verification still require current evidence. A separate
`run --message` supersedes unanswered questions and starts fresh acceptance counts.
Approval waits still require explicit resolution.

Scripted production CLI checks pass Chat/Plan/Agent, restart/answer, ten invalid
answer variants in each question mode, native/text result roles, exact question
pair/ID isolation, prior-write accounting and new-task isolation. Existing skills
runtime, all six read-recovery/approval cases and three read-acceptance cases pass. Host-neutral harness:
Plan 3/3, Chat 1/1, architecture 2/2, awaiting-user runtime 1/1. CLI build: no
warnings/errors; harness retains two PDF CA1416 warnings. The general source-include
guard fails on SDK default includes (176 paths), recorded in
[BACKLOG](BACKLOG.md#source-inclusion-harness-guard-and-sdk-projects--2026-10-05);
the five new Core files and removed Office includes were checked directly.
Version-format and diff checks passed.
Windows/Office/WebView2 delivery and full M4/M5 acceptance remain open.

Real Gemma question tests **failed** with the unchanged narrow
[task](../../tests/cli/questions_task.md), 32K/4096, system instructions,
json_schema, reasoning off, sequential requests, 16 iterations / 24 tools and
180 s per phase. Model/digest match the prior skill test. Native `tool`: 135.15 s,
exit 5 / ProtocolExhausted. The model supplied file-only-invalid `referencePath`,
repeated the rejected read, then omitted required option descriptions through ten
format repairs. Fourteen HTTP requests/responses, 23 pair checks without errors;
tokens 25308/2368 total, maxima 1960/197. The only comparison change was role `user`:
21.86 s, exit 5 / no_tool_progress after two invalid resource calls and empty-call
progress. Five HTTP requests/responses, seven pair checks without errors; tokens
7336/272 total, maxima 1628/68. Neither produced accepted questions or reached
answer submission; both made zero file changes and failed read acceptance.
No reported context/OOM error. The model was unloaded after each run. These
results do not qualify real-model question completion or isolate a universal
role/model cause. Source/binaries, fixture/profile, actual HTTP/CAS and wire audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-questions-gemma-um15kp23`;
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-questions-user-gemma-ddhvxg5w`.

Previous waiting-status correction (2026-10-05): the Office conversation projection
now maps the kernel's typed `awaiting_user` terminal reason to the corresponding
response status. Previously it showed `completed`, although the kernel had stopped
for questions. The existing `plan mode: native question pauses kernel` test fails
identically on the isolated pre-slice `2a6525fe` baseline and passes after this
one-line correction. Owner: Office conversation result projection. No lifecycle,
question dispatch or resume semantics changed. Source-linked harness build has
the two existing PDF CA1416 warnings; version-format/diff checks passed.
Windows/Office/WebView2 delivery remains unqualified. Committed separately as
`42eaa6bf`; the subsequent CLI slice is recorded above.

Previous shared workspace skills slice (2026-10-05): Office and CLI now use Core
`SkillPublicationService` for committed skill catalogs, exact body/reference CAS
reads and publication dependencies; `SkillCatalogService` selects host metadata.
The former Office-only selection/publication/read implementations are removed.
Existing custom catalog identity, CAS, mutation publication barriers and Office
continuations remain in use. The shared resource publication/payload reader and
typed request failure moved to Core; no second store or agent loop was introduced.

CLI exposes `common.resources_find/read` with optional `type=skill` (file remains
the default), semantic skill id and optional listed `referencePath`. Discovery is
bounded to 50 enabled entries with explicit incompleteness; complete bodies above
16000 characters fail explicitly. Only metadata is present before a body read.
Frozen catalog scope/generation goes to the existing `ModelContextCompiler`;
source omission/currentness stays there. Skill reads do not grant file mutation
observations or count as file reads, including an identical skill/file name.
Installed Common/Workspace skills are visible; Office-only and disabled entries
are hidden, built-ins win collisions, and a workspace-local SKILL.md stays a file.
The two new built-ins cover workspace files and local web verify/repair, filtered
by actual writable/browser capabilities. CLI skill authoring and reference
pagination remain outside this slice. Owner: shared skills storage/context and
Runtime resource admission; the broader Office/filesystem dispatch scope stays open.

Scripted production CLI evidence passes metadata/body separation, exact references,
authoring drift/deletion with separate-process continuation, native tool pairing,
file authority/read-count isolation, repair and unavailable-browser/read-only
filtering. Existing read-acceptance (3 cases), read recovery (6), omitted-source,
browser runtime and capability checks pass. Host-neutral harness: skills 5/5,
skill editor 6/6, frozen prompt 1/1; catalog filter 15/16. Its Outlook tool-count
assertion also fails identically on pre-slice HEAD `2a6525fe`; recorded separately
in [BACKLOG](BACKLOG.md#outlook-compact-catalog-count-assertion--2026-10-05).
CLI build: zero warnings/errors. Harness has two existing PDF CA1416 warnings.
Version-format and diff checks passed. No Windows/Office/WebView2 qualification.

Real Gemma: the new narrow [skill/file task](../../tests/cli/skill_file_task.md)
passes in 29.51 s: an invented referencePath is rejected, the model retries the
core correctly, receives the complete published `workspace.files` body, creates
note.txt, reads its exact bytes and returns done. Acceptance passed, 1 current file
read / 1 verified change. Five HTTP requests, no protocol rejections, 10 cumulative
pair checks without errors. Input/output totals 10164/306, maxima 2524/70. This
explicit skill request proves the route, not autonomous skill selection. Traces,
source/binaries/profile and byte/body audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-skill-file-gemma-v5awtzem`.

The unchanged full CSV task still fails: the driver cancels at the existing 600 s
wall bound (600.17 s, exit 5). Three creates and one replacement verified; the last
browser check passes 17, fails invalid-message, leaves 6 not run. Final reads are
0/3. Both skill metadata entries were present in every request, but the model read
neither. Two multi-write batches were rejected before dispatch. Twelve HTTP
requests / eleven completed responses, 53 cumulative pair checks without errors;
input/output totals 88479/11374, maxima 13671/1831. No reported context/OOM error.
No operator repair, reduced checks, raised bounds or follow-up run. The same
Gemma digest, 32K/4096, system/user roles, json_schema, thinking off, sequential
requests and 16-iteration/24-tool limits were used; the declared change is the
skill catalog/schema/instruction route. Swap use 3266.12 → 3364.56 MiB; this was not
an isolated performance benchmark. Exact failed-run source/binaries, profile,
HTTP/CAS and audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-skills-gemma-2dv8tzmw`.
Both real runs unloaded the model afterwards. The later read-only guard is covered
by the final narrow run and deterministic checks. The replaced file-handler read
dependency/marker was removed; focused recovery checks cover the final write-only
handler. Full M4/M5 remain open; the next concrete scope is below.

Previous read-only correction (2026-10-05): skill capability filtering exposed an
existing permission bug. `--read-only` prevented manifest creation but an existing
manifest still produced a writable descriptor and advertised all file mutations.
`WorkspaceStore.Open` now keeps both the descriptor and root mount read-only for
that open, regardless of manifest presence. Identity is preserved and a later
explicit writable open still works. Owner: Core workspace admission/file owner.

The CLI capability regression failed before the fix and now passes. The focused
workspace harness test also verifies that an exact source read cannot authorize a
replacement through the read-only descriptor and that original bytes are intact.
The skills/runtime test checks that writable built-ins disappear in this mode.
CLI build: zero warnings/errors; harness: two existing PDF CA1416 warnings.
Version-format and diff checks passed. Windows/Office are not qualified. This
permission fix is a separate slice from the shared skill catalog work in progress.

Previous final-read evidence slice (2026-10-05): Runtime now supplies typed
`RUNTIME_CONTEXT.readAcceptance` when the accepted task requires file reads.
It reports current distinct reads, the remaining count and expected paths without
a current read, from this run's accepted facts and the same frozen authority
passed to the shared compiler. One predicate serves both projection and final
assessment. The required projection is budgeted and stays frozen during format
repair; it neither grants source/write authority nor changes kernel `done`.
The instruction now states that writes/read-back/browser checks do not satisfy
this file-read criterion. Owner: Runtime workspace acceptance; no second loop,
durable read model or filesystem reader was added.

A separate false-pass regression was confirmed and fixed in the same assessment:
with only `--min-reads`, an external edit during the final model wait previously
left stale reads counted as current. Final assessment now refreshes every accepted
read target through the existing file owner, not just `--expect-files` paths.
The old result was `passed`; the regression now correctly returns `failed`.

The new scripted context test first failed on the missing projection. All three
cases now pass: premature `done`, repair/next-run isolation and external final
edit without expected files. They cover duplicate-read counting, invalidation
after write/external edit, frozen format repair and native result roles. Existing
browser runtime checks confirm that verification/read-back add no file reads;
the omitted-source test confirms metadata grants no overwrite authority. CLI
build: zero warnings/errors; version-format and diff checks passed. No harness
or Office build was needed; Windows/Office/WebView2 remain unqualified.

Real Gemma CSV rerun: three files created, one replacement after typed read
recovery, one exact file read, one failed and one passing browser verification.
The final snapshot passed **24/24**, but full acceptance **failed**, `model_done`,
exit 5, 370.44 s: the model still skipped final reads. Its last HTTP request
explicitly contained **0 current / 3 remaining** and all three file paths.
This change does **not** establish a fix for premature completion. Category:
`VERIFICATION_ERROR`; feature follow-up **not run**, full M4/M5 remain open.
Nine HTTP requests, zero protocol rejections; 32 cumulative pair checks found no
errors. Tokens: 58585/6337 total input/output, 11106/1840 maximum per request.
Task/checks/model/digest/profile and 16-iteration/24-tool bounds stayed unchanged;
the first request differs only by the declared read guidance and projection.
No operator repair or raised limits. Swap-outs 1003804 → 1027252, swap use
2955.75 → 3314.12 MiB; no reported context/OOM failure. Model unloaded afterwards.

The installed model config selects renderer `gemma4`. Inspection of the matching
[Ollama 0.35 renderer](https://github.com/ollama/ollama/blob/v0.35.0/model/renderers/gemma4.go#L26-L139)
and [prompt assembly](https://github.com/ollama/ollama/blob/v0.35.0/server/prompt.go#L18-L85)
found no dropped second system message. This is source/config evidence, not a
measurement of model attention. Saved HTTP/CAS, exact source/binaries, driver,
model config and audits:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-read-acceptance-gemma-bdaut27s`.
The then-planned skill catalog and file/web instruction slice is implemented
and rerun evidence is recorded above; autonomous creation/follow-up remains open.
The remaining premature-done case stays in
[BACKLOG](BACKLOG.md#cli-final-read-acceptance-after-csv-repair--2026-10-05).

Previous workspace read-recovery slice (2026-10-05): file copy/move/patch/replace/delete
now return target-bound typed `RefreshRequired` when a complete source read is
missing. Owner: Runtime `WorkspaceConversationService.FileHandler`; existing Core
recovery/progress logic is unchanged. `WorkspaceFileService.Describe` supplies
runtime identity without granting read authority. Preparation rejects before
move/delete approval; execution retains the existing observation, version,
journal and read-back guards. The registrar's ad-hoc exception path is removed.
No second loop, filesystem reader or error-string inference was added.

The scripted regression failed on the previous binary, then passed all six cases:
five file operations and recovery restored through a separate-process approval.
Unrelated reads and verified mutations do not unlock the failed target; its exact
complete read does. Omitted-source replacement remains denied with bytes intact.
Existing partial-read, unknown-effect and CLI role/approval checks passed.
CLI build: zero warnings/errors. Version-format and diff checks passed.
Windows/Office/WebView2 remain unqualified.

The unchanged real Gemma CSV task now reached `model_done` in 302.82 s: three
created files, two verified patches, two complete `app.js` reads and three browser
checks. Both `source_observation_required` failures recovered after matching
reads and identical patches; no repeated-call deadlock occurred. The final
immutable snapshot passed **24/24 browser checks** and remained current.
Full acceptance nevertheless **failed** (exit 5): neither HTML nor CSS was read,
and both JS observations preceded later patches, leaving **0/3 current complete
file reads** at completion. The final gate correctly rejected the premature
`done`; browser verification is not a substitute for the explicit read criterion.
Category: `VERIFICATION_ERROR` (missing final read evidence). Feature follow-up
was **not run**. Full M4/M5 remain open; the subsequent final-read projection,
assessment fix and rerun are recorded above. See
[BACKLOG](BACKLOG.md#cli-final-read-acceptance-after-csv-repair--2026-10-05).

Same `rna-gemma4-12b-32k:latest` digest/profile as below: 32K/4096, system/user
roles, json_schema, thinking off, one request at a time, bounds 16 iterations /
24 tools. First HTTP request is byte-identical to the preceding failed run.
13 requests, no protocol rejections; 62 cumulative pair checks found no errors.
Input/output tokens: 88949/4740 total, 10628/1719 maximum per request. No operator
repair, changed task/checks or raised limits. Swap-outs stayed 1003804, swap use
2987.81 → 2979.75 MiB; no reported context/OOM failure. Model unloaded afterwards.
Temporary evidence (profile, hashes, source patch, HTTP/CAS, driver and audits):
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-read-recovery-gemma-a19abh1c`.

Previous context/transport slice (2026-10-05): the shared compiler now preserves the
latest completed call/result pair and its selected role. Older mutation pairs stay
intact while they fit; budget folding keeps operation outcomes, recovery data and
semantic targets. Source omission/staleness remains separate and removes read
authority without fabricating a new outcome or orphaning native results. Exact
latest accepted arguments hydrate from CAS or fail preparation. Office compaction
classifies both paired and folded typed effects as operation evidence, independent
of later source changes. Owners: Core model context, Runtime/CLI transport, Office
compaction. No second loop, provider catalog or filesystem reader was added.

CLI now honors `ToolResultRole` via `--tool-result-role user|developer|tool` /
`RNA_TOOL_RESULT_ROLE`, persists it in run metadata and pins it across approval.
An incompatible/missing pending profile fails before tool dispatch. Generation
remains v6 JSON; `tool` selects native accepted-history transport only.

The regression failed on the previous compiler (one folded message instead of
the latest pair). Focused `context continuity:` passed 8/8, `agent continuity:` 5/5;
correctness-before-budget, latest/archived CAS calls, current-source carry-forward
and compiler presentation passed. Four scripted CLI checks passed: role/approval
HTTP, omitted-read guard, exact trace/frozen repair, browser verify/read/repair.
CLI build: zero warnings/errors; harness retains two existing PDF CA1416 warnings.
Version-format and diff checks passed. Windows/Office/WebView2 remain unqualified.

Real Gemma native-role smoke passed: create `out.txt` → read → done, acceptance
passed, one verified write/read, 19.60 s. Four HTTP requests include one repaired
non-JSON response; 4/4 cumulative native-pair checks found no orphan/mismatch.
Same `rna-gemma4-12b-32k:latest` digest as below, 32K/4096, system instructions,
json_schema, thinking off; bounded to four iterations/tool steps.

Controlled CSV rerun kept the prior task/checks/model/profile, result role `user`
and bounds 16 iterations/24 tools. Its first HTTP request is byte-identical to the
baseline. It created all three files, attempted browser verification, then read
`app.js`; the previous index.html creation loop did not recur in this run. But
acceptance **failed**, `repeated_tool_no_progress`, 597.85 s: three writes, one exact
read, one failed browser check and one rejected replace before repeated refusals.
Ten requests, one repaired mutation-batch violation; 42 cumulative call/result
checks have no pairing errors. Tokens: 70757/12805 cumulative, 13211/3342 maximum,
below caps. No operator feedback or limit increase; follow-up **not run**.

This exposed a separate reproducible recovery-contract gap: file handlers returned
`source_observation_required` without typed `RefreshRequired` recovery, so the
kernel still rejected an identical mutation after the required complete read.
The read is present in HTTP; this failure is not missing model context. The
target-bound recovery fix and its deterministic/live results are recorded above.
**Full M4/M5 remain open.** One improved run does not explain every historical loop.

Temporary evidence (profile/digest, binaries, source snapshot, HTTP/CAS, audits):
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-context-pair-gemma-4j_p1hsv`;
native smoke: `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-native-pair-gemma-37tq3ibj`.
The model was unloaded after both runs. CSV swap-outs rose 996904 → 1003804;
swap use 3000.06 → 3011.81 MiB, without a reported OOM/context failure.

Earlier M4/M5 status and retained CSV checks (2026-10-05): **neither full stage is closed**.
M4's first useful independent real-LLM/file result is achieved. At this historical
point its remaining scope included non-agent modes, the actual skill catalog,
structured questions/answers and complete §11 acceptance. The first three are now
implemented with the explicit evidence limits at the top; full §11 remains open.
Delete/move approvals, cancel and explicit recovery/resume already exist; the
stage table no longer lists these as wholly missing or asks for automatic replay.
M5 has one successful real counter repair; autonomous CSV creation + a feature
follow-up, long-task compaction/resume evidence, general preview and browser
screenshots/traces remain open. M6–M11 have not started: file-based authored
HTML/MD/JSON migration → common UI API/viewer → external Office provider → dedicated
Excel launcher → VBA/export/import → final cleanup/performance/qualification.

This slice adds immutable CSV operations to the existing functional contract and
browser verifier: upload, input/select, ordered table cells, numeric totals,
proportional positive SVG bars and actual downloaded CSV. Base/follow-up contracts
have 24/31 steps. Fixtures and downloads stay in the private browser profile;
source bytes still come only from the retained filesystem provider manifest.
Core rechecks observed values before publishing Passed. All results use the same
authority/CAS records and existing freshness-bound run acceptance. The separate
Node CSV grader/browser/physical workspace reader was removed and its consumers
switched. Owners: Core check contract/publication, Runtime browser checks, CLI
admission; no new loop, source store or project reader.

Final CLI build: zero warnings/errors. Focused `workspace snapshots:` passed 2/2,
including rejection of forged passing table/total/chart/export values. Real
Chromium passed all 31 fixture steps and historical replay after live files were
changed/deleted. Wrong exports, lexical sorting, decorative equal-size bars,
duplicate downloads, missing browser, outbound attempts and broken JS are rejected.
Existing counter functional smoke and scripted production-runtime verify/repair
checks passed, including frozen admission and stale final-evidence rejection.
Version-format and diff checks passed. These tests establish runtime behavior;
they are not autonomous LLM acceptance.

Real Gemma creation attempt: v6 probe passed; `rna-gemma4-12b-32k:latest`, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`, Ollama 0.35.0,
32K context / 4096 output, system instructions, json_schema, thinking off, one
request at a time. Bounds were fixed before the new CSV task: 16 iterations /
24 tool steps. It created only `index.html`, then repeated create. Sixteen requests,
16 tool calls, one verified write, zero reads/verifications, zero protocol rejection;
331.90 s, `iteration_limit`, acceptance **failed**. The second feature turn was
**not run**, since the first did not pass. Failure category: MODEL_ERROR (observed
repetition; its underlying cause remains unresolved). No repair feedback or limit
increase was used. HTTP audit: 120 cumulative completed-result presence checks,
zero missing/duplicate results. Input/output: 46325/7051 cumulative tokens,
3676/504 maximum per request, below the configured caps. Swap-outs rose from
900005 to 954405 and swap use from 6954.69 to 7628.69 MiB; memory pressure was
present, with no reported context/OOM failure. The model was unloaded afterwards.

Temporary local evidence:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-csv-gemma-6x_dj0ux` retains
task/checks, model config/digest, actual binary/source hashes, HTTP trace/CAS,
terminal run result, context audit and memory samples. The live attempt never
reached the browser checks. Windows/Office/WebView2 remain unqualified here.
This is the pre-fix baseline; the controlled reruns and completed recovery slice are
recorded above. Remaining M4 surface and E-* evidence stay explicit.

Historical M5 diagnostic freshness / continuation fix (2026-10-05): the failed counter-repair
run exposed a reproducible context defect. `web.verify` discarded source evidence
when the browser failed, CLI refreshed only successful verification dependencies,
and the common compiler exempted every failed result from source-currentness checks.
Consequently the original `ReferenceError` remained a plain `TOOL_RESULT` even
after a verified repair and a current read showing correct code. A new deterministic
regression failed before the change; this was not inferred solely from LLM prose.

Failed evaluations with a published snapshot now retain exact source evidence;
CLI refreshes all its members before freezing authority. The common Core compiler
keeps the original outcome/diagnostic/assertions but labels changed-source failures
as historical, after CAS hydration and sanitization. Missing exact diagnostic
payloads stop preparation. Argument/dispatch failures without source evidence,
mutation/recovery guards and frozen format repair retain their existing semantics.
File path and web entry labels also survive completed-operation folding. No prompt,
model loop, instruction role, tool-step limit or model profile was changed.

Wire audit of the earlier failed run: 11 HTTP requests, 73 cumulative checks of
completed-result presence, **zero missing or duplicated results**. Its writes and
current reads were present; their coexistence with an unqualified old failure was
the confirmed defect. The new successful run has 23/23 presence checks. This audit
establishes dispatched content, not what the model attends to. The upstream
[Ollama 0.35 Gemma renderer](https://github.com/ollama/ollama/blob/v0.35.0/model/renderers/gemma4.go#L65-L139)
merges adjacent assistant messages, but retains their content; inspection did not
establish a provider-side message-loss bug or justify changing receipt roles.

Real Gemma 12B rerun, same fixture/checks/model digest/config as the preceding slice:
the **first production HTTP request was byte-identical** to the failed baseline.
The agent ran verify (failed) → source reads → one patch → current read → verify
(9/9 passed) → done. Six model requests, seven tool steps, one verified write,
no format rejection, 49.94 s, acceptance **passed**, without operator intervention.
Usage: 14281 cumulative input / 513 output tokens; maxima 3289 input / 128 output.
Swap-out count remained 900005; swap usage remained 7018.75 MiB. The model was
unloaded. `ExecutionHealth.Errors` records the initial failed check cumulatively;
it does not contradict successful current verification and accepted completion.
This is one real successful repair, not proof that every historical repetition
has the same cause or that all models/Office tasks are qualified.

Local temporary evidence:
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-context-gemma-wa5sfdkx`:
fixture/checks, exact config/digest and binary hashes, trace/CAS, both verification
records, context audits, first-request byte comparison, metrics and memory samples.
Final CLI build passed with zero warnings/errors; focused `context continuity:`
passed 6/6. Scripted runtime checks inspect actual request messages before/after
an external dependency edit and verified repair, preserving write targets/effects
and rejecting stale final acceptance. Existing tool-result/trace smoke passed,
including frozen repair and exact request-body persistence. Version-format and
diff checks passed. Windows/Office/WebView2 remain unqualified on this machine.
Next: M5 CSV dashboard checks on retained snapshots; broader creation/continuation
cases remain in [BACKLOG](BACKLOG.md#cli-continuation-after-completed-operation-receipts--2026-10-05).

Historical M5 functional-check slice (2026-10-05): CLI `--web-checks` accepts an immutable
typed contract before the run. The same `web.verify` handler executes bounded
pointer clicks and exact text checks against the retained manifest; no model
arguments can replace the checks. Pending/terminal verification records retain
the contract and every step's outcome/observed value in the existing authority/CAS.
Acceptance now reads the exact saved result, checks origin/contract/manifest
membership and refreshes file evidence against frozen authority. Missing checks,
wrong entry, changed source or a smoke-only result cannot certify the task.
Owners: Core check contracts/publication, Runtime isolated verifier and acceptance,
CLI admission. There is no new agent loop, file reader or mutable project store.

CLI build passed with zero warnings/errors. Focused `workspace snapshots:` 2/2
passed (including incomplete/substituted assertions and mismatched actual values).
Real Chromium functional smoke passed: click-time exception missed by page load,
repair, no-op handler, broken historical snapshot after repair, unavailable browser,
wrong entry and duplicate selector. Scripted production-runtime smoke passed
verify/read/patch/read/verify/done, frozen checks after the input file changes and
rejection of a source edit after the last passing snapshot. These scripted checks
are orchestration evidence only. Version-format and diff checks passed.

Real model: one v6 probe and one fresh counter-repair run with
`rna-gemma4-12b-32k:latest`, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`,
Ollama 0.35.0, context 32768, output 4096, `json_schema`, instruction role `system`,
thinking off, one request at a time. The probe passed. The task made 11 model
requests with no protocol rejection (30954 cumulative input / 1689 output tokens;
largest request 3802 input / 295 output), 12 tool steps, four verified changes,
and stopped after 106.68 s with `tool_step_limit`, acceptance **failed**. Gemma
fixed the click-time `ReferenceError`, then repeated read/replace instead of
re-verifying. An explicit independent CLI check of its final files passed all
nine steps; this does not change the failed agent acceptance or prove autonomous
verify/repair. No model output reached the token cap. Swap-out count stayed
900005; existing swap usage decreased from 7146.75 to 7074.75 MiB. The model was
unloaded after the run. This stop was the tool-step bound, not a context rejection
or observed out-of-memory error.

Local evidence (temporary, not a durable release artifact):
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-m5-functional-gemma-0f2bdqi7`.
It retains the fixture/checks, probe, source commit plus exact binary hashes, run
events/CAS, model configuration, metrics, memory samples and independent result.
The last model request still contains the original failed verification as an
ordinary `TOOL_RESULT` after verified source changes. Freshness/provenance of that
diagnostic is the next isolation target; causality of the model's repetition is
not proven. See [continuation backlog](BACKLOG.md#cli-continuation-after-completed-operation-receipts--2026-10-05).
M5 remains open for autonomous task acceptance, CSV upload/export grading on the
retained snapshot, screenshots/traces and broader E-* scenarios. M6–M11 have not
started. No Windows/Office/WebView2 or release qualification is claimed.

Historical M5 durable web snapshot slice (2026-10-05): Core `WorkspaceWebSnapshotStore`
publishes typed exact manifests and pending/terminal verification records through
the existing resource authority journal and CAS. Source bytes keep their file owner;
there is no mutable project mirror or new physical store. CLI can rerun a selected
historical snapshot, read a saved result and list bounded pages of pending/results
after restart. Model tool results retain an exact verification reference with
session/run/tool-call origin; the model schema remains semantic and unchanged.
Historical success does not authorize a current-file mutation or satisfy agent
acceptance. That still requires refreshed file evidence at completion.

CLI build passed. The workspace harness filter passed 17/17, including the seven
file tests and two new manifest tests. The final focused `workspace snapshots:`
run passed 2/2: publication barrier, pending restart/discovery, terminal non-overwrite,
move/replacement/deletion, cross-workspace rejection, missing/corrupt retained data
and CAS GC retention. Real Chromium smoke passed for current/historical checks,
cross-process result inspection/listing and missing payloads. Scripted tool-result
projection confirmed exact verification-resource persistence with origin, no runtime
IDs in model messages, stale-source filtering and frozen repair. Version-format
validation and diff checks passed. No real LLM, Windows/Office/WebView2 or release qualification is
claimed. Functional assertions, screenshots/browser traces, binary assets and
general preview UI remain open; the new historical CLI route is a development
verification consumer of the retained manifest.

Historical M5 retained text slice (2026-10-05): CLI `verify` / agent `web.verify`
read exact retained UTF-8 through the workspace filesystem provider and shared
router. The direct verifier-to-file-owner reader is removed. `ReadExactText`
replaces the string-only historical read, supplies whole-view evidence, enforces
the text bound and rejects unpublished revisions and missing/corrupt CAS bytes.
Historical reads do not republish a current head. Before browser launch the
verifier refreshes sources and checks evidence against frozen authority; acceptance
still checks freshness at completion. Durable project manifests were open at this
checkpoint and are implemented by the slice above; binary assets, remaining
preview/viewer consumers and common Office/file dispatch remain open.
CLI build and focused `workspace files:` 7/7 passed. Real Chromium smoke passed
with an exact BOM/CRLF snapshot and rejected a missing retained payload before
launch despite an intact live file; existing error/asset/not-run cases also passed.
The scripted CLI tool-result projection smoke passed through `web.verify`, including
failed-result delivery, stale-source filtering, frozen repair and shared trace.
`ValidateVersionFormat` and `git diff --check` passed. The current protocol wording
in `AGENTS.md` / `session-events.md` and the stale next-slice entry are corrected.
Model task quality and Windows/Office/WebView2 were not requalified by this slice.

Shared compiler status (2026-10-05): CLI and Office now use the same frozen Core
`ModelContextCompiler`; the separate CLI assembler is removed. Bounded semantic
file discovery, exact reads, stale-body filtering and omitted-source write guards
passed focused checks. Follow-up isolation found no missing wire messages, but Gemma
still failed autonomous continuation. CLI now uses the shared production trace writer
for exact HTTP requests, rejected bodies and attempt/snapshot correlation. Full
real-agent acceptance and the remaining §8.1 views/consumers stay open.

Provider isolation (2026-10-05): GPT-OSS returns native tool calls with empty v6
content; its installed template also drops `developer` instructions. CLI now honors
the existing `SystemPromptRole` setting and pins it through approval. Gemma still
stopped after two files with `system`. Neither run reached a token limit; no new
swap-out occurred during the Gemma run. Model/task acceptance remains open.

Local model context (2026-10-04): the 8K Qwen 27B profile cannot admit a CLI
Agent request with the current 4096-token output policy and mandatory reserves;
it is retained only for context rejection checks. A 16K profile is the candidate
for Agent comparison. The scripted CLI response-mode smoke passed at 16K;
Qwen 27B memory pressure and real Agent task admission remain unverified.

## Workspace-first implementation — 2026-10-04 (in progress)

Historical milestone snapshot; current stage status is in the plan reconciliation
at the top. In particular, the formerly open CLI Task Lists/Plans and cancellation
surface have since been implemented; complete M4/M5 acceptance is still open.

Baseline `ae05b205519365c9a2b02bce8a218afa84302158` matched the supplied
implementation plan; the starting tree was clean. [ADR-0013](../decisions/ADR-0013-workspace-first-runtime.md)
records the intended boundary and [migration map](MIGRATION_MAP.md#workspace-first-adapters--2026-10-04)
records temporary adapters. This work does not qualify Windows/Office/WebView2.

| Stage | Current result | Evidence / open work |
|---|---|---|
| M0 | Baseline, SDK and target contracts checked; one ADR and migration map added. | .NET SDK 8/10 available. Installed Mono `msbuild` cannot run on this arm64 Mac (`Bad CPU type`); old-style Windows build remains open. |
| M1 | `ToolRuntime` and exact handler registry moved to Core; `ConversationRunCoordinator` is called by Office and CLI. Net8 Core and Runtime assemblies build with project references and no Office/WebView2/WinForms/fake dependency in CLI. | Focused `tool runtime:` 15/15; CLI dependency search/build. Office project compilation and platform delivery remain open. |
| M2 | Portable workspace manifest, user-state association, read-only opening, existing ChatStore workspace sessions without document authority, root relocation and copy-ID conflict. File locators retain identity across a known root move. | Focused `workspace:` 2/2. External moves not associated with an opened workspace and mount/Office identity resolution remain open. |
| M3 | Real UTF-8 files: bounded single-directory discovery/read, create, exact patch/replace, copy from an accepted complete source to a new identity with immutable source provenance, guarded historical restore, recoverable delete to managed workspace trash and explicit restore after deletion. Confirmed `files.move` keeps logical identity, advances revision and relocates the file locator. CAS views, authority heads, mutation journal, read-back, external-edit conflict and non-replay after an interrupted dispatch remain one file-owner path. `recover` distinguishes prepared-only abandonment, an already-published effect and unknown causality after dispatch; it never replays the command. Ordinary reads and writes are blocked while an effect is unresolved. External invalid UTF-8 or oversized text marks the old known head unknown; BOM, CRLF and verified no-change are preserved. | Focused `workspace files:` 7/7. Directory discovery scans at most 5000 entries and returns `truncated` rather than implying a complete result; optional filename search is case-insensitive. Move retains a durable two-path intent; reads of both paths block until an interrupted move is reconciled. A delete verifies absent source plus exact trash preimage before publishing an unavailable head; restore requires that deletion head and creates a new revision. Cross-volume move semantics, trash retention policy, binary/large streaming, full cross-platform path races, directory pagination and the remaining §8.1 provider operations/views remain open. |
| M4 — first useful result achieved; full scope open | Independent Net8 CLI uses production LlmClient/v6, shared coordinator → kernel, ToolRuntime, filesystem owner and frozen ModelContextCompiler. Sessions/JSONL, exact file operations, delete/move approvals, cancel and explicit recovery/resume exist. Shared published skills, mode filtering and typed question/answer admission are implemented. Real file creation, a narrow explicit skill/file task and read → question → answer → fresh read → done passed with Gemma. | CLI Plan documents/checklists, complete §11 host UX and acceptance remain open. The native question pass follows the shared schema correction and closed resource selectors; it does not qualify autonomous clarification quality or every earlier loop. Explicit resume never replays effects. Full independent multi-file task quality/evidence remains distinct from scripted runtime checks. |
| M5 — in progress | Optional browser verifier uses bounded exact retained snapshots; pending/terminal records and historical replay use existing authority/CAS. Frozen click/text and CSV functional contracts feed the same model web.verify and freshness-bound final acceptance. The separate Node CSV grader/source reader is retired. | Counter repair passed once with real Gemma 12B. CSV runtime checks cover upload/filter/sort/total/chart/export/error handling; live two-turn CSV acceptance is recorded at the top. General preview UI, screenshots/browser traces, longer compaction/resume E-* evidence and full M5 acceptance remain open. Binary assets/common Office dispatch are still §8.1/M5–M7 scope. |

Historical M3/M4 resource routing slice (2026-10-04, extended below): canonical provider registration and
URI selection moved from the Office-only registry into Core
`ResourceProviderRouter<TProvider>`. Office `ResourceGatewayService` uses it; CLI
`common.resources_find/read` now select a workspace-bound filesystem provider,
which delegates to the existing file owner and preserves exact read evidence.
The replaced Office registry file was removed. Focused workspace file tests 6/6,
Gateway routing tests 3/3, and scripted CLI find → read → failed browser
verification projection passed. This is provider routing, not a common typed
find/read contract: CLI still returns file names, while Office returns semantic
candidates, and they shape read results separately. Descriptors, stat, raw view,
coverage and exact retained-view read remain open for the filesystem provider.
At that commit CLI prompt assembly had not moved to the shared compiler. The
2026-10-05 slice below replaces that path; Windows/Office behavior remains unqualified.

M3/M4 shared compiler slice (2026-10-05): Core now owns the existing
`ModelContextCompiler`, common result projection and typed bounded find/read
results. Office injects its pure domain projection; its guarded read dispatch is
unchanged. CLI prepares authority through `WorkspaceFileService`, normalizes
accepted kernel facts and passes them to that same frozen compiler. The separate
CLI result projector/request assembler and old Office compiler file are removed.
CLI discovery returns semantic targets and metadata-only descriptors with explicit
incompleteness; exact whole-text reads carry matching retained payload/coverage.
External edits invalidate prior read bodies. Format repair retains the original
snapshot; `inspect` exposes the saved receipt. A read excluded by budget cannot
authorize replacement, even when its durable exact evidence remains current.

Evidence: CLI build; focused `model context:` 6/6, `workspace files:` 6/6,
`agent: model projection` 2/2, `context continuity:` 5/5, `shared context:` 2/2,
Gateway 3/3; frozen evidence, current-source carry-forward, correctness-before-budget
and oversized-source recovery passed individually. Scripted CLI checks passed for
find/read → external edit → failed browser result → frozen format repair, budget
omission → rejected replacement with original bytes preserved, and interrupted-run
reconciliation. The old oversized-read harness assertion was corrected to verify
the existing omission receipt instead of expecting a hard budget failure; durable
success is preserved while source evidence is withheld. These scripted checks are
runtime evidence, not a real-model quality result.
Dependency-boundary tests passed 2/2. The existing all-project source-inclusion
test failed because it treats SDK-default includes as missing; this is tracked in
[BACKLOG](BACKLOG.md#sdk-project-source-inclusion-audit--2026-10-05). Direct checks
of the changed Core/Office old-style projects found no omitted or removed source
entries. No old-style Windows build was run.

Scope at the compiler checkpoint: raw/binary views, exact retained-view provider
access, preview/viewer consumers and common Office/filesystem operation dispatch
were still open. The M5 slice above adds retained text access for browser verification;
remaining owners/removal conditions are recorded in
[MIGRATION_MAP](MIGRATION_MAP.md#workspace-first-adapters--2026-10-04). M4–M11
acceptance and Windows/Office/WebView2 qualification are not declared complete.

Real-model check of this slice (2026-10-05): on `abe17fbd` plus the shared-compiler
working changes, a fresh three-file counter task used `rna-gemma4-12b-32k:latest`,
digest `cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`,
32768 context, 4096 output, `json_schema`, reasoning off, one request at a time.
In 96.9 seconds it made 8 model requests / 8 accepted calls with no protocol
violation: 1 verified create, 3 dispatched `target_exists` errors and 4 rejected
repeat calls. It created only `index.html`, performed no exact reads or browser
verification and stopped with `repeated_tool_no_progress`, acceptance failed,
exit 5. Saved compiler-message traces contain the successful path/effect and every
error receipt; the final context is only 2108 estimated tokens, with no resource
exclusion. This is a `MODEL_ERROR`/possible `CONTEXT_ERROR` case requiring isolation,
not proof that Gemma is weak or that shared compilation improved task quality.
Session `0343a46fdbc847a0a57660cac4539388`; local task/log/state are retained in
`/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/rna-shared-compiler-gemma-6g89vanv`.
The model was unloaded after the run. A successful autonomous create/read/verify/
repair scenario remains open; no model-specific prompt workaround was added.

### CLI continuation isolation and shared model trace — 2026-10-05

Owner: model context / CLI evaluation / Core trace persistence. At `b420fcab`,
all five HTTP message arrays in a fresh Gemma run exactly matched the saved frozen
compiler output. Successful write receipts were present and within budget. Seven
isolated request replays, without tool execution, chose the next missing file:
assistant receipts, user receipts and the original call/result pair all worked.
The same-seed streaming/non-streaming replay produced identical output. This does
not establish a receipt-role or transport defect, or prove full-task completion.
Prompt/model sensitivity remains open; no prompt, result role or agent loop changed.

The task is now retained as [counter_task.md](../../tests/cli/counter_task.md).
Full runs used fresh empty workspaces, the same CLI/catalog/acceptance contract,
Ollama 0.35.0, `json_schema`, 4096 output, temperature 0.2, top-p 1, one request at
a time and limits of 12 iterations/12 tool steps. Required evidence was three
files, three exact reads, three verified writes and current `web.verify` success.
Gemma used `rna-gemma4-12b-32k:latest`, 32768 context, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`.
GPT-OSS used `rna-gpt-oss-20b-16k:latest`, 16384 context, digest
`236b951778f336db7e66af76ee93038d1fb7de34a790b89a508756373575aca9`.

| Run | Observed result | Acceptance / classification |
|---|---|---|
| Gemma, reasoning off, 34.02 s | 5 captured HTTP attempts; 2 accepted/dispatched creates, 0 tool errors, 0 protocol repairs, then 3 no-call `continue` responses. `index.html` and `styles.css` only; 0 reads/verification. | `no_tool_progress`, failed, exit 5; `MODEL_ERROR`, possible `CONTEXT_ERROR`. |
| GPT-OSS, reasoning off, 82.6 s | 10 invalid format attempts in the first step; 0 tool calls/files. The old trace omitted rejected bodies, so their exact malformed shape is unavailable. | `ProtocolExhausted`, failed, exit 5; `PROTOCOL_ERROR`; continuation quality unmeasured. |
| Gemma, reasoning on, 360 s limit | 6 prepared model steps, 5 accepted create calls: 1 verified write, 3 dispatched `target_exists`, 1 rejected repeat. Only `index.html`; 0 reads/verification. Process stopped during step 6; old traces do not establish the total format-attempt count. | Timeout exit 124; explicit idempotent `resume` closed it as interrupted/unknown without replay (exit 3). `MODEL_ERROR`, possible `CONTEXT_ERROR`; no successful terminal action. |

Local evidence is under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-gemma-wire-run-9bnn5i1i` (session `9214de7b920341ee8698039c113946b7`,
including wire comparison and same-seed probes), `rna-shared-compiler-gptoss-eysi7epn`
(session `5ded79ca923b41da8887624d9f8fd335`), `rna-gemma-thinking-run-xbx10c5r`
(session `41fdd63ada144f71996260e597125091`) and
`rna-receipt-diagnostic-z73r_0hs` (first three isolated probes).
The proxy could not expose the model digest to CLI metadata; the exact Gemma digest
was recorded separately in that run's summary. Models were unloaded between full
runs and after the final run. The local Gemma profile uses the native `gemma4`
renderer/parser; its generic template alone is not evidence of a broken profile.
The [Ollama API contract](https://docs.ollama.com/api/openai-compatibility) documents
streaming, structured response formats and `reasoning_effort`; these capabilities
do not guarantee RNAssistant protocol or task acceptance.

Concrete fix from this investigation: the existing `ModelTracePersistenceService`
and bounded `SessionTraceWriteQueue` moved from Office to Core. CLI configures the
same `LlmClient`/`ModelProtocolClient` sink through `IEventStore`; its synthetic
compiler-message request and accepted-only response appends were removed. Actual
request bytes, streamed frames, rejected bodies and verdicts enter the existing
chat stream/CAS with attempt and frozen-snapshot correlation. Office retains its
diagnostic logger through an injected callback. No model-call, mutation, recovery
or source-authority policy changed.

Evidence: CLI build passed; `storage: streaming` 3/3, `storage: model` 2/2 and
dependency direction 2/2 passed. The extended scripted projection smoke verified
all 5 actual request byte payloads, retained rejection, distinct repair identities
under the same frozen snapshot and no API key in trace bodies/metadata. Interrupted
model wait, idempotent non-replaying `resume` and explicit continuation passed.
These are trace/runtime checks; the real-model failures above predate the passive
trace change and are not claimed fixed. Windows/Office delivery remains unverified.

### CLI instruction role and provider isolation — 2026-10-05

Owner: workspace application / CLI profile. At `8cd708f7`, the fresh GPT-OSS
counter run recorded all 10 rejected attempts and raw stream frames. It failed
`ProtocolExhausted` in 73.27 s, exit 5, with zero dispatched tools/files. The
provider returned empty content, reasoning and native `tool_calls`; those calls
were never promoted into the v6 execution contract. This is `PROTOCOL_ERROR`.
The installed GPT-OSS template renders system/user/assistant/tool messages but
omits `developer`, including RNAssistant's tool catalog (`CONTEXT_ERROR` /
`ENVIRONMENT_ERROR`). Two single-request probes with the same seed 17 and unchanged
text confirmed the omission: developer used 158 prompt tokens and invented
`tool.create_file`; system used 1074 and named `files.create`. Both still returned
native calls, not v6 JSON. System-role `json_object` and `tool_choice=none` probes
also failed; no probe dispatched a tool or qualified the full task.

The concrete CLI defect was a hardcoded instruction role despite the existing
`AppSettings.SystemPromptRole`. `--instruction-role` / `RNA_INSTRUCTION_ROLE` now
selects system/developer explicitly; the default remains developer. Runtime validates
the role, passes it into the same frozen compiler and saves it in model metadata.
Approval inherits the saved role or rejects a conflicting override before dispatch;
missing historical role metadata requires denial/new input. No prompt text, native
tool-call fallback, model-name rule, retry/output/context limit or agent loop changed.

A fresh Gemma 12B counter run on this working change with `system` failed in
41.76 s: 5 requests, 2 successful creates, 0 protocol/tool errors or retries, then
3 no-call `continue` responses; `no_tool_progress`, acceptance failed, exit 5.
`index.html` and `styles.css` exist; no `app.js`, reads or browser verification.
It used the previously recorded `rna-gemma4-12b-32k:latest` digest, context 32768,
output 4096, reasoning off and json_schema. Prompt usage was 1032–1408 tokens;
completion usage 43–355. `MODEL_ERROR` / possible `CONTEXT_ERROR` remains open.
GPT-OSS used the previously recorded 16K profile/digest, output 4096, reasoning off
and json_schema; observed native-call stops were `tool_calls`, not `length`.

Memory check: GPT-OSS was resident alone, Ollama reported 12 GB / 100% GPU.
The Mac had 24 GiB physical RAM and about 7.2 GiB system swap in use; this cumulative
value alone does not establish an OOM. During the entire Gemma run, swap use remained
7386.75 MiB and swap-out stayed 900005 pages; swap-in increased by 20 pages (320 KiB).
No OOM/timeout or output truncation was observed. These measurements do not qualify
larger models/contexts, but do not support increasing limits for these failures.
Profiles were run sequentially and unloaded after testing. Ollama's documented
[thinking controls](https://docs.ollama.com/api/openai-compatibility) distinguish
requested thinking output from guaranteed model/protocol behavior.

Evidence directories under `/var/folders/k9/hr_94nt142x1f43jr6g0wr_00000gp/T/`:
`rna-gptoss-trace-e0tu8zz_` (session `4c677a9b088e45f589a1d12c166cdff1`, exact
requests, SSE, rejected bodies, installed template and four isolated probes),
`rna-gemma-system-role-rl9q2919` (session `8942d820582d4941aa6a9ec73b0c1de4`,
trace, memory counters and summary). The targeted CLI response-mode smoke passed:
both roles/formats and durable metadata, identical instruction text, invalid-role
rejection, pending deletion preserved on role drift and exactly one verified delete
after approval inherited the system role. CLI build passed. M4/M5 real acceptance,
the remaining §8.1 provider/view work and Windows/Office qualification remain open.

Previous local-model result: on the same clean CSV task and then-current CLI source,
Gemma 4 12B wrote three real files and passed 10/11 independent browser checks;
Qwen 3.5 9B returned `done` with no calls or files. Neither passed the CLI's
read-back/verification contract. A discovered current-turn tool-result projection
gap is fixed and covered by a scripted read/browser-error test; autonomous M5
completion and the two-phase extension remain open. Details follow below.

Cloud reference check: `gemma4:cloud` through Ollama 0.35.0 failed v6 parsing
before any tool in both `json_schema` and explicit `json_object` modes. The
endpoint returned Markdown-fenced, wrong-shaped tool JSON despite declaring
OpenAI-compatible response formats. Cloud task quality is therefore unmeasured;
this tag is not currently a qualified reference for CLI comparisons.

M4 acceptance slice (2026-10-04): the CLI accepts expected files and minimum read/
write evidence as a typed run contract, saves it in the canonical chat stream before
model dispatch, includes it in the request, and saves the separate assessment at
termination. `inspect` reloads that result. The read minimum counts distinct
complete file observations still current after expected-file refresh; the write
minimum counts verified changed file effects, not no-op or failed calls. A scripted
OpenAI-compatible response that claimed `done` with no file returned
`acceptance=failed`, exit 5 and a persisted missing-file result. Another scripted
run created and read `index.html`, returned `acceptance=passed`, exit 0 and a
persisted 1/1 evidence count. These scripted checks verify contract wiring, not
Qwen's autonomous repair or semantic/browser quality.

M4 confirmation slice (2026-10-04): CLI `files.delete` now requires the existing
`ToolRuntime`/`AgentKernel` confirmation. `resume` shows the durable pending id;
`approve` reloads the accepted call and complete read under the session lease,
then executes through the same handler and file guard. `deny` closes the accepted
call with a persisted not-dispatched result and a cancelled kernel outcome.
Focused kernel denial test passed. A scripted CLI run paused with the file still
present, a separate-process approval deleted it with one verified effect, and a
duplicate approval was rejected. A separate denial kept the file and the next
turn loaded the closed history. An external edit between pending and approval
made the confirmed delete fail without moving that edited file; the scripted
model still said `done`, but the accepted minimum-change contract returned
`acceptance=failed`, exit 5. This is CLI/tool-path evidence; other question
flows, broader approvals, automatic resume and real-model confirmation behavior
remain open.

M4 interrupted-run safety slice (2026-10-04): an abandoned CLI `running` run
is now closed only after the per-session cross-process lease is acquired and the
canonical chat is reloaded. `resume` performs that durable reconciliation without
calling the model or replaying a tool. An in-flight possible write is marked
unknown; an incomplete accepted exchange is excluded from future model context,
and the old CLI acceptance becomes unknown. The user can submit a new turn after
inspecting the workspace and explicitly reconciling any unresolved file attempt.
`python3 tests/cli/interrupted_run_smoke.py` killed a process during a model wait,
then verified repeatable `resume` with no model call and exactly one new request
after explicit input. The CLI now accepts bounded `--max-iterations` and
`--max-tool-steps`; the same smoke checks an invalid bound and a one-iteration
`continue` stopping with `iteration_limit`. A crash during an in-flight file
dispatch, same-run budget continuation and full E-RESUME acceptance remain
unverified.

M3 move slice (2026-10-04): `files.move` now requires an accepted whole-file read
and the same kernel confirmation/resume path as delete. It saves source and target
in a durable move intent before dispatch, checks the source preimage, refuses an
occupied or previously identified target, verifies target bytes/source absence,
then relocates the file locator and publishes a new revision of the same identity.
Fault injection after filesystem dispatch and after locator change passed explicit
reconciliation without replay; ordinary reads of both paths were blocked before
recovery. Root relocation during an interrupted move also passed. Focused file
tests passed 5/5. A scripted OpenAI-compatible CLI run paused before move and a
separate-process approval resumed the same run, moved the real file and passed
the expected-file/minimum-write contract. Cross-volume behavior and exact
Windows filesystem races are unverified.

Fresh local Qwen regression (2026-10-04, after the acceptance/confirmation
changes): one new multi-file ToDo task with three expected files and minimums
of three reads and three writes first attempted reads of three missing files and
failed with `repeated_tool_no_progress`, zero writes and failed acceptance.
Explicit feedback in that chat led to ten invalid batched mutation responses and
`ProtocolExhausted`, still zero writes. In a separate chat on the same empty
workspace, three narrow single-file turns created and read each named file with
verified changed effects and passing per-turn contracts. `node --check app.js`
passed, but external source inspection found no form/list in `index.html`, while
`app.js` expects them; its click handler also deletes a task on any click and
localStorage reload mishandles saved objects. A model repair request read HTML
repeatedly and failed with no write. This run does not satisfy the original
autonomous multi-file verify/repair acceptance; M5 behavior remains open.

M5 browser-verification slice (2026-10-04): CLI `verify` serves an immutable
in-memory snapshot of the entry and discovered local HTML/CSS/JS dependencies,
limited to 32 files and four MiB, through a loopback-only server. A fresh Chrome
profile has a restrictive content policy and unavailable external proxy; its
console/page exceptions and failed loads become bounded structured errors. The
browser sees no workspace root or Office bridge. Valid and broken fixture checks
passed as expected; `python3 tests/cli/web_verifier_smoke.py` reproduces pass,
runtime error, missing asset and no-browser outcomes. Missing browser returned
`not-run` and hid `web.verify` from
the model catalog. Scripted model responses exercised verify → read → patch →
read → verify → done with current exact file evidence; a check made before a
subsequent patch failed `--require-web-verify` at completion. A live local
`rna-qwen35-9b-32k` task initially replied `needs_input` without a tool call;
the next explicit turn verified/read but stalled with `repeated_tool_no_progress`.
After a further message named the exact DOM-ID mismatch, Qwen itself performed
read, `files.patch`, read, `web.verify`, then `done`; CLI acceptance passed with
one verified change and current browser evidence. A separate CLI `verify` passed
on the resulting files. This proves a narrow real-model repair path, not
autonomous diagnosis or the CSV-dashboard functional acceptance.

M5 diagnostic follow-up: `web.verify` now adds an advisory missing-DOM-ID hint
beside a real JavaScript exception, and puts the first error/hint in the tool
message as well as structured data. The browser smoke checks the hint. Two fresh
local-Qwen repair attempts without the exact user diagnosis still failed: one
repeated unchanged `web.verify`, the other repeated `files.patch` without an
accepted file read. `ToolRuntime` rejected repeated calls, no file changed, and
CLI acceptance failed. Autonomous diagnosis remains unverified.

M5 delayed-error correction: `web.verify` now observes console, network and page
events for 1.5 seconds after load instead of stopping after the first ready-state
evaluation. The browser smoke caught a `setTimeout` exception at 650 ms while a
valid app still passed. Later timers and interactive behavior remain outside this
smoke contract and need the independent functional grader.

M5 E-CAPABILITY slice: `tests/cli/capability_smoke.py` opened a disposable
workspace with Chromium deliberately unavailable. `env` advertised the real
workspace file tools but no `web.verify` or Office tools; `verify` returned
`not-run`/exit 4 on a valid HTML entry. `run --require-web-verify` returned
exit 4 before creating a session or calling a model. Browser absence is therefore
explicit in the CLI capability and acceptance path; broader provider/mode
capability changes remain open.

Historical M5 independent CSV dashboard check (2026-10-04; grader retired by the retained-check slice above):
`tests/cli/csv_dashboard_task.md` fixes the user-visible controls; the separate
`csv_dashboard_grader.mjs` captures up to 32 files/four MiB in memory, serves only
that snapshot to a fresh Chrome profile, uploads private fixture CSVs, and checks
real table/filter/numeric-sort/sum/SVG/export/invalid/empty behavior plus browser
errors and outbound attempts. Its smoke passed 13/13 assertions on a working
fixture and rejected broken JavaScript, unavailable Chromium and a CSP-blocked
external fetch without exposing the query string. A fresh `rna-qwen35-9b-32k`
run (`thinking=false`, 32K context, one CLI request at a time) initially returned
`model_done` with zero calls/files; the persisted expected-files/read/write/browser
contract returned exit 5, and the grader found no files. One human feedback turn
made three verified creates and a passing `web.verify` but no accepted complete
file reads, so CLI acceptance still failed. The independent grader found 8/11
behaviors passing on the resulting three files. Export emitted characters instead
of rows; a nonnumeric amount became zero; empty CSV received an invalid-format
message. A follow-up with only those three failed checks attempted an existing-path
`files.create` (rejected), did no write and falsely claimed repair; acceptance
failed again. The initial prompt used for this run also mentioned the future
threshold control, which the model generated early; `--require-threshold` found
that control working (10/13 total). The reusable fixture now separates the base
task and follow-up into two files; a genuine two-phase autonomous run is open.
These checks do not qualify Windows/WebView2 or Office behavior.

Local Qwen evidence (2026-10-04): Ollama 0.35.0 loaded `rna-qwen35-9b-32k`
entirely on GPU with context 32768. `/api/show` reports `thinking=false` as
supported; a direct `/v1/chat/completions` strict-JSON probe with
`reasoning_effort=none` returned valid v6-shaped JSON and no reasoning field.
The first CLI file task returned `model_done` with zero tool calls and an empty
workspace. After explicit feedback, the same session made three verified creates
and three successful resource reads; a duplicate create was rejected without an
overwrite. A controlled external ID mismatch was then read but falsely reported
done without a write. A narrower follow-up produced one verified `files.patch`
and read-back. `node --check` passed; external headless Chromium passed add,
empty-input, filter, toggle, delete, reload-persistence, checkbox accessible-name
and narrow-viewport overflow checks with no JS
exceptions. This is live tool-path evidence, not autonomous verify/repair
acceptance. Opt-in CLI postconditions now check expected current files and minimum
verified read/write counts. A live `model_done` with zero writes and a missing
expected file returned exit 5 and `acceptance=failed`; a live read/patch case
returned exit 0 and `acceptance=passed`. A stricter read-back requirement
correctly rejected a model `done` after only one read. These conditions do not
grade file semantics;
see [risk register](RISK_REGISTER.md#workspace-cli-false-completion-with-local-qwen--2026-10-04).

Local Gemma/Qwen comparison (2026-10-04): Ollama 0.35.0, fresh empty workspaces
and separate state roots, `tests/cli/csv_dashboard_task.md`, the same CLI source,
tool catalog, 24-iteration/80-tool-step caps, expected three files, three current
complete reads, three verified writes and required `web.verify`. Both local profiles
used 32K context, `reasoning_effort=none`, 4096 output tokens and one CLI run at a
time. Gemma profile `rna-gemma4-12b-32k` had digest `cf94f3793da5`; Qwen profile
`rna-qwen35-9b-32k` had digest `2db395653ce7`. Gemma's direct `json_object` probe
returned an invalid v6 action `none`; a strict `json_schema` probe returned valid
`done`. In the controlled post-fix run, Gemma made five model requests, three
verified creates, zero current complete reads and one passing `web.verify` in
122 seconds; CLI exit 5 rejected its `done`. The read-only grader found 10/11
behaviors working, with nonnumeric CSV amount accepted incorrectly. Qwen made
one model request, zero tool calls/files and returned a false `done` after 79
seconds; CLI exit 5 rejected it. These single trials support Gemma as the more
productive local development profile for this task, not a general quality claim.

Earlier Gemma diagnostic runs exposed a runtime defect: after a current-turn
`web.verify` returned `status:error` with a JavaScript `SyntaxError`, the next
model-request snapshot contained a null body for that tool result. The model
claimed browser verification succeeded; CLI acceptance and independent Chrome
both failed. `WorkspaceConversationService` now materializes a missing current-turn
result from the exact execution record, preserves unknown/error status, and uses
the same wire body for durable history. A scripted HTTP/Chrome regression confirms
that complete file content and failed browser results reach the next model request.
Before this fix, one Gemma repair turn with explicit defect feedback produced a
verified JS change and 11/11 independent base checks but only two current complete
reads, so CLI acceptance still failed. Its subsequent threshold-feature turn made
two patches that duplicated `let rawData`; `node --check`, `web.verify` and the
independent grader failed, while the model falsely claimed success. After the
projection fix, the fresh base task still failed on missing read-back and invalid
amount. No run qualified the two-phase M5 acceptance; Windows/Office/WebView2
qualification is unchanged. The trace reported no `llm.failure` events in these
runs; provider-internal retries were not separately measured.

Cloud Gemma protocol check (2026-10-04): `gemma4:cloud`, digest `ef09f235533c`,
reported model context 262144; CLI planning context was explicitly 32768,
reasoning off and output cap 4096. A fresh three-file CSV task using the default
`json_schema` exhausted ten format attempts (`ProtocolExhausted`, exit 5), with
zero calls and files. A direct strict-schema greeting probe returned fenced JSON
with `action`/`response` instead of the required v6 envelope. A direct simple
`json_object` greeting probe returned valid v6, so CLI now exposes an explicit
response-mode setting and records it in run metadata. Repeating the full task in
`json_object` on another empty workspace again exhausted ten format attempts
without tool calls or files. A direct call with its saved production prompt
returned fenced JSON with `Id`/`Schema` tool fields instead of `name`/`arguments`
and no `message`. This is a reproducible `PROTOCOL_ERROR` at the model/endpoint
boundary, not a functional CSV score. Scripted HTTP tests confirmed both CLI
transport modes and saved metadata. The existing v6 parser correctly rejected
these outputs; no permissive JSON repair or separate agent loop was added.

## Next CLI acceptance slice — 2026-10-13

The [v7 comparison](#real-v7-model-regression-and-shared-response-contract--2026-10-13)
replaces the previous next-step proposal: thinking on/off and a cloud reference
have now been exercised. The missing CLI response contract is fixed; remaining
failures involve repair/completion decisions, not demonstrated loss of tool results.

1. Complete the fixed CSV task with current final reads, then its unchanged
   feature follow-up. Thinking-on Cloud passes browser checks but skips all three
   final reads. Keep task/checks and 16/24/600 limits; do not turn `done` into an
   automatic retry or weaken the grader.
2. Complete real 24K compaction/restart/resume, including the external edit and
   non-replayed receipt. Cloud now preserves all final bytes and the prescribed
   note order, but skips receipt reading and never publishes a checkpoint.
   Controlled recovery evidence does not close this real-model gate.
3. Qualify an affordable local profile with measured memory headroom. The latest
   Gemma 12B/32K preflight fails despite disabling both server caches; previous
   successful memory admission does not establish current capacity.

M4's first useful real-model/file result and interactive CLI are implemented;
full M4/M5 remain open. Typed resource discovery, exact text/raw/PNG/JPEG views,
retained browser snapshots/diagnostics, Mac caller-loss cleanup, event following,
questions, selected skills, Task Lists and workspace-owned Plans have bounded
implementation evidence. Remaining working-preview/format/path coverage, uncertain
planning reconciliation and Windows/Office behavior retain their existing scope.
M6 file-backed authoring and M7–M11 cutovers remain separate planned work; the
existing Office authoring owners stay active and CLI sources have no dual writer.

## Current operating status — 2026-09-30

The maintainer accepts the completed host-neutral architecture migration as a
**working baseline** for continued development. RNAssistant runs, edits code and
executes agent tasks; remaining reliability defects are ordinary prioritized bug
work. This is a conditional operational status, not signed exact-build Windows/Office
qualification or a published stable/beta/RC release. Product metadata remains
`16.1.0-dev` until a separate release decision. No tag or release evidence is
created by this status decision. The repository snapshot at the decision is commit
`0f8e5ce8`; in-flight working-tree edits are not part of that snapshot. The
maintainer's manual checks were not tied here to an exact binary or signed manifest.

Default development is sequential work on `main`, without a task branch for each
change. Existing in-flight branches and uncommitted edits are preserved and
integrated deliberately. Transition: review the current in-flight change, bring it
to `main` when ready, and stop opening `stab/*` branches by default; do not force a
checkout or stash a dirty tree. The former Phase 11 → WQ → Phase 12 queue and general
feature freeze are retired for ordinary work; the master plan is historical.
New features need explicit scope and owner, with reliability and data safety ahead
of breadth. Targeted checks follow the changed behavior. Missing Windows/Office,
WebView2 and live-provider evidence stays open and is required when qualifying a
formal release or claiming those environments are verified.

Current order of work (the numbered R entries below retain their historical
severity, not a new phase queue):

| Priority | Next concrete step | Owner / evidence |
|---|---|---|
| P1 | Validate agent-owned planning, completion decisions and context continuity on the original Excel/VBA/HTML trajectories: compare terminal events with exact next-request bytes and actual rendered/Office state. Earlier host-neutral fixes do not establish complete task execution; incident attribution still needs real Windows/model evidence. | Conversation/model context + kernel/Resource owners; [implementation slices](../conversation-protocol.md#agent-continuity-audit--2026-10-01), [active incident](RISK_REGISTER.md#agent-continuity-and-deterministic-read-loops--2026-10-01), [completion and read-presentation follow-up](RISK_REGISTER.md#html-read-presentation-and-premature-completion--2026-10-02) |
| P1 | Verify pane switch/shutdown draining and Outlook Explorer/Inspector activation on Windows; fix a reproduced failure in the bound host path. | Bridge/HostRuntime/Outlook; [current risks](RISK_REGISTER.md), Windows x64 + Office x64 + WebView2 |
| P1 | Verify navigation/model-catalog cancellation, lazy model UI/activity batching, deferred startup recovery and batched CAS metadata reads on the affected Windows history; isolate remaining Desktop attach/Outlook discovery/lock stalls. | ChatStore / bridge / controller / Desktop; [navigation evidence](RISK_REGISTER.md#chat-navigation-and-startup-freezes--2026-10-02), exact build and same chat |
| P2 | Reconcile the remaining host-neutral fixture/prompt checks and bounded resource gaps as separately scoped slices. | Owners and triggers in [BACKLOG](BACKLOG.md) |
| Release gate | Run the exact-build Windows/Office matrix and review signed evidence for a chosen release candidate. | Release owner; [runbook](WINDOWS_QUALIFICATION_RUNBOOK.md) and [release process](../operations/RELEASE_PROCESS.md) |

No new P0 implementation defect is established by the available screenshots alone.
Wrong-target, data-loss and false-success evidence still takes precedence if it is
reproduced. The release gate does not block the P1/P2 work above.

Diagnostics simplification (2026-09-30): Qualification Center, its embedded packs,
Excel identity helper and in-app release admission are removed. The maintainer
continues to build and test the product on the real Windows/Office machine; no
separate helper or automatic verification process is introduced. Diagnostics now
opens at a bounded current-session work log, with saved chronological chat history
and optional technical views. VBA diffs stay in the editor; the journal remains
available for recovery evidence. Historical chat events and VBA journals are not
deleted. Formal exact-build Windows/Office evidence remains open; the release owner
records it directly and reviews the signed release payload before finalization.

## Implementation log — includes current in-flight work and earlier entries

Agent response intent and result card (2026-10-02): conversation-response v6
replaces the ambiguous `final` flag with explicit `tool`, `continue`, `done`,
`blocked` and `needs_input` actions. A no-call progress checkpoint is persisted
and fed into the next request; three consecutive checkpoints fail without a false
completion. Multi-deliverable Agent guidance now expects a concise Task List,
and the run card separates model outcome, source-owned effect counts and known
remaining list steps. Existing v5 chats require explicit new chat/reset. Focused
host-neutral protocol/kernel/UI checks are recorded with this change; live
target-model and Windows/Office/WebView2 evidence remains open.
The final context budget pass also converts an oversized exact capability read
to a typed `capability_evidence_context_too_large` result before rejecting the
whole request; the corresponding host-neutral test now passes.

Large-chat SQLite projection (2026-10-02): JSONL remains the authenticated source;
a rebuildable per-chat SQLite sidecar now indexes header state, messages, artifacts
and exact tail cursor. Routine reads check the tail and catch up from a validated
suffix; message history pages query only their requested rows. Scalar chat preference
writes append direct typed metadata commits. A full CAS maintenance audit validates
the complete stream against the sidecar and repairs divergence. Synthetic 30/50 MiB
fixtures with 6,000 messages: first index build 1.23/1.27 s, indexed cold load
0.34/0.26 s, warm load 16/24 ms, generic save 0.23/0.29 s on this host. First
conversion still scans the entire stream; real Windows/Office/WebView2 timing and
system `winsqlite3.dll` delivery remain open evidence.

Cross-turn capability reuse (2026-10-02): unchanged optional tool schemas now carry
to later turns of the same chat from exact durable admissions and retained read
frames. Edited history, schema drift and budget overflow fall back to core; the
model request reports budget fallback. Skill/resource bodies still require current
evidence and available context space. Focused host-neutral checks pass; live
Windows/model evidence for the reported repeat-loading trajectory remains open.

Model request schema duplication (2026-10-02): the exact model-context inspector
confirmed material repetition on a 20-schema Excel Agent core plus one loaded
extension. A wire-only strict-mode projection removes repeated parameter schemas
from `RUNTIME_CONTEXT.tools` while the accepted prompt retains them for identical
`json_object` fallback; the strict response schema and local argument validation
remain exact. Full fixture request fell from 87,453 to 57,277 UTF-8 bytes (about
7,544 estimated tokens). Focused host-neutral mode, repair/fallback, admission and
inspector checks pass. Live target-model and Windows/Office evidence remains open;
see [model response contract](../conversation-protocol.md#model-response).

Artifact catalog and transfer (2026-10-02): the Artifacts tab exposes chat/document/
all-local-document scopes with full-pane discovery and isolated preview. Explicit
HTML project packages and original/Markdown/JSON import/export support independent
copies; JSON bindings transfer as retained snapshots without live source-document
access. Typed streaming transport and atomic authored publication retain existing
authority/CAS ownership. See [contract and evidence](../artifact-library.md#local-catalog-and-artifact-transfer--2026-10-02).
Windows/Office/WebView2 qualification remains open.

Incoming messages (2026-10-02): runtime-owned durable queue and safe-boundary steer
are added with typed bridge intake, attachment support, a configurable Enter action,
queue cards and a separate Stop control. Steer interrupts model transport while
preserving completed tool evidence and pending-confirmation budgets. Queue recovery
requires explicit resumption; background work participates in shutdown draining.
Focused host-neutral and mock-controller evidence is described in the
[conversation protocol](../conversation-protocol.md#incoming-messages-during-a-run).
Real Windows/Office/WebView2 delivery qualification remains open.

Library and prompt migration (2026-10-02): Library now shows separate icon-marked
Prompt, Skill and Tool trees with clearer host nesting. The prompt action menu
retains only a selected-prompt return to defaults. Prompt schema 35 automatically
selects current built-in texts on load; the next settings save archives the old
eight texts before persisting the new marker. User skills/tools and other settings
are unchanged. Focused host-neutral checks cover the editor and settings path;
Windows/WebView2 qualification remains open.

Read presentation and completion continuity (2026-10-02): read counts now survive
CAS externalization independently of source bodies; lazy preview reports missing
payloads explicitly. Exact active task state stays in required runtime context.
Task planning is now advisory: goals/stages can be revised with rationale, and
notes retain why a proposed edit was unnecessary. Removed source/hash completion
checks and the open-list final gate; final leaves unfinished steps intact. Actual
mutation receipts and unknown-effect guards remain authoritative. Source/text line
excerpts and explicit body-omission recovery avoid repeating an oversized whole read.
Prompt schema 36 aligns the model's decision authority with the tool and kernel
contracts, using the existing schema-35 migration mechanism. Original Windows/model
incident evidence remains open in the [follow-up](RISK_REGISTER.md#html-read-presentation-and-premature-completion--2026-10-02).

Recovery flexibility and mutation receipts (2026-10-02): a repeated-call rejection
now returns to the model with its specific reason before any dispatch. It does not
terminate the run or prevent another tool/corrected request; only three consecutive
responses containing exclusively rejected calls end the no-progress cycle.
Read batches count as one response; domain mutation guards and unknown-effect
non-replay remain in force. Repeated conflicts with different inputs do not stop
recovery reads. Archived mutation receipts now retain semantic module/file targets
alongside call ID, outcome and verified effect, even when data bodies are omitted.
Host-neutral checks cover alternate tools, corrected requests, confirmation,
unknown replay, all result roles and VBA/HTML target preservation. Exact Windows
Office/model incident qualification remains open.

Agent continuity implementation (2026-10-01): terminal errors/actions now survive
context projection and compaction with typed provenance. Existing event messages
retain typed progress for canonical-call guards and confirmation restoration.
A budgeted `ContextWorkingSet` replaces unconditional archived-source carry-forward,
restoring current skills and structured observations from CAS. Gateway whole reads
and read/HTML/JS selectors are shared; prompt schema 33 removes redundant rereads,
readmission and compulsory duplicate planning artifacts. Existing custom prompts
remain intact and require the existing review action. See
[the implemented contract and audit](../conversation-protocol.md#agent-continuity-audit--2026-10-01)
and [tool contracts](../tool-library.md#tool-ergonomics-audit--2026-10-01).
Focused host-neutral checks cover context, kernel, confirmation, skills, HTML and
schema inventory. Original Windows/Office/WebView2/model trajectories remain open.
Three unrelated checks also fail in clean HEAD `dfa154e2`: prompt-resource tests
observe mutable settings reloads (50/8), and copied derived-workspace restore fails.
They are tracked separately; no Windows/VSTO qualification is claimed.

Resource/context repetition correction (2026-10-01): evidence deduplication no
longer turns repeated successful reads into stale errors or drops different JS
results over the same source. Completed mutations retain `tool_call_id`. HTML
results/read metadata now expose usable member/source targets and the automatic
text binding created by JSON writes; known superseded workspace roots leave the
prompt index, and HTML discovery includes document-owned roots. Invalid binding
of workspace/binding metadata is rejected before writes; identical rebinds are
no-ops. The HTML skill no longer forces a table stream on another bound view.
Focused host-neutral context, capability and HTML tests pass; the six-photo
incident's exact trajectory and real Office/WebView2/model behavior remain open
under [the current incident](RISK_REGISTER.md#repeated-resource-reads-and-html-binding-failures--2026-10-01).

Excel literal search correction (2026-09-30): `excel.find_cells` previously captured
every cell in the requested sheet/workbook before matching, so an ordinary heading
search could fail with `RESOURCE_SNAPSHOT_TOO_LARGE`. Literal queries up to 255
characters now use bound Excel `Find`/`FindNext`, with complete match counting and a
32,000-character result cap; only the compact query-bound result enters Gateway/CAS
evidence. Regex and longer literals retain the bounded exact-snapshot path. Focused
host-neutral Excel search/find-replace checks pass, including a sparse sheet beyond
the old 100,000-cell capture limit. Real Windows Excel COM behavior and model delivery
remain open evidence.

Mutation-result delivery audit (2026-09-30): confirmation continuation reused a
snapshot built before the terminal result; compilation is now deferred to the
actual request boundary and appends invalidate the cache. Compaction source
carry-forward no longer mistakes assistant input references for delivered bytes.
Delivery warnings preserve mutation data/effect metadata, archival preserves source
target labels, and source/accepted-call payload admission uses calibrated capacity.
Focused checks include a confirmed 100-hunk VBA patch with archived result and
complete after-source, stale confirmation, normal writes, compaction, media failure
and HTML sources. Office/real-model qualification remains open.

Repeated VBA edit correction (2026-09-30): a failing Agent regression proved that
the compiler converted a verified write to an error when its own input snapshot
became stale. Call input no longer invalidates terminal outcomes or fresh reads.
Mutation results now hydrate before folding and retain full semantic data, including
patch diagnostics, under the complete request budget. VBA patches can address an
exact line/column with old-text verification; stale coordinates never redirect to
another occurrence. Targeted host-neutral context/schema/patch checks pass.
The earlier source-only regression did not check this contradictory outcome;
it now asserts the actual next request and completion. Windows/Office and target-
model behavior for the reported photos remain unqualified.

Excel inspect result delivery correction (2026-09-30): a focused reproduction
confirmed that archived generic JSON was replaced by a success marker even with
ample context. The compiler now hydrates all selected non-folded result payloads;
full data is limited by the complete calibrated request budget, not the storage
threshold or a per-result cap. Full results no longer direct unnecessary resource
rediscovery. Six focused host-neutral checks pass, including 1/16/160 KB next-request
delivery in all result roles, calibrated capacity and actual budget overflow.
The original Windows/Office/target-model loop remains unqualified pending trajectory.

Agent recovery correction (2026-09-30): Windows photos show repeated
`vba_patch_stale_source`, a workbook-wide `RESOURCE_SNAPSHOT_TOO_LARGE`, invalid
Excel regex and a `final=false`/empty-tools stall. Host-neutral changes reject the
empty-call combination before acceptance and repair it as format; VBA patch now
reports hunk/context mismatch and accepts a unique complete-line VBA token match,
including line-aligned context, when VBE changed spacing or identifier case. Definite no-effect failures no longer
end the run solely because a call repeats or several conflicts occur; possible
unknown effects remain non-repeatable. Excel oversize guidance points to sheet
UsedRange inspection and explicit range slices; plain-text search uses literal mode.
The 100,000-cell/one-million-character exact snapshot bounds remain safety limits,
not user settings: raising one bound alone would increase COM work without making
large workbook capture complete. Focused kernel, VBA, Excel, ModelProtocol and
settings review checks pass. The existing built-in guidance assertion remains
open in BACKLOG. Exact Windows
trajectory, real Office/WebView2 and target-model qualification remain open.
Prompt schema 32 requires the existing explicit review/reset for saved prompts.

Tool authoring and R61 inventory correction (2026-09-30): with Agent JavaScript
disabled, `common.tools_upsert` no longer exposes VBA-irrelevant `host` and
`executor`; the VBA manifest supplies the host. Focused semantic authoring, JS
switch and pipeline checks pass. The 69-row inventory now matches the reviewed
Plan `startNew`/Agent admission, resource-find guidance, VBA patch and Excel search
descriptors; its host-neutral gate passes. Windows/Office and live-model checks
remain open.

JavaScript computation addition (2026-09-29): a read-only `common.js_run` and
saved JS tool executor use a separate bounded Jint worker and the existing Resource
Gateway/Data Plane for named, revision-pinned streamed reads. Tool Library accepts
`executor=js` with per-call semantic resource bindings. Host-neutral source and
contract checks pass. The .NET Framework worker cannot be built on this ARM Mac
with the available Mono runtime or without net48 reference assemblies in `dotnet`;
Windows worker/build/runtime and Office/WebView2 delivery remain open evidence.

Entries below may say “current” or “next” relative to their own date. Only the
operating status above controls today's work.

HTML read correction (2026-09-28): screenshots show that freshly written
`index.html`, `styles.css` and `app.js` were discoverable, but the model read each
exact `HTML file` target with `structure`, which is a workspace-root view. The
provider rejected the view, and an identical failed call stopped the run. The
model-facing reader now explicitly returns the file's complete `source` for this
one mismatched view and reports `source` in the result; discovery/HTML skill
guidance distinguishes root `structure` from member `source`. Other unsupported
views still fail. Native tool regression passes host-neutral; the reported
Windows/target-model run remains unqualified.

HTML observation follow-up (2026-09-28): a later screenshot shows complete
`index.html` source read, unrelated reads, then another
`html_source_observation_required` failure. The guard checked only evidence in
the last model request. It now accepts a complete source observation shown to
the model in any earlier accepted request of the same run while the file member
and content hash remain unchanged. Changes to that file, a different run and a
read never shown to the model still fail closed. Focused host-neutral guard and
model-session checks pass; Windows/target-model reproduction remains open.

Current source context follow-up (2026-09-29): verified writes previously folded
to a short outcome, leaving their complete after-state out of the next model
request. A compaction checkpoint could also remove the last complete source/text
read. The conversation owner now attaches exact published read-back for VBA,
Markdown, Plan and all current HTML workspace files; the compiler carries one
authority-checked observation per current source/text view from CAS across compaction and
excludes superseded bodies. Oversized current source stops at the prompt budget
instead of silently disappearing. Focused host-neutral tests cover two writes,
compaction, HTML multi-file after-state and the next guarded replacement. The
reported model loop's exact trajectory and Windows/target-model behavior remain
unverified.
The follow-up check also excludes redundant markers for superseded after-states
and retains the current archived body when a later historical read appears in
active or compacted history. Large PDF text and typed action memory across
compaction remain open working-set gaps in BACKLOG.

Current user-requested mutation boundary (2026-09-28): Agent/Plan now accept only
independent local reads in one model response; every mutation is a singleton.
ModelProtocol rejects an unsafe batch before acceptance, and AgentKernel repeats
the check before dispatch. A later mutation sees the previous result; schema 30
and format repair give the same rule. Focused host-neutral parser, kernel, protocol,
Excel and VBA checks pass. This mitigates R78's same-response stale overwrite risk,
but does not establish the reported incident's root cause or guarantee that a later
model response uses current source. A redacted trajectory and real Windows/Office/
WebView2/live-provider qualification remain open. No release gate is closed.

HTML overwrite follow-up (2026-09-28): screenshot shows differing repeated writes
to `index.html`/`styles.css` while Task List remains at 1/3. `write_file` replaced
existing source without checking what the model saw; the context compiler reduced
write results to short causal frames. Existing-file replacement now requires a
complete source read shown to the model in the same run while that file's exact
content remains unchanged; write results include a bounded file inventory and
source hash. Three changed writes to the same conflict without a satisfying read
end the run. Focused host-neutral replacement, compiler and kernel tests
pass; the actual run trajectory and target-model loop behavior remain open.

Current build correction (2026-09-08): the existing read-only CAS presence check
used by shared-context claim filtering is now public across the Core/Office assembly
boundary. This fixes the reported `CS0122` in `ModelContextCompiler`; the downstream
missing-metadata errors are expected build cascades. No contract or runtime behavior
changed. Host-neutral Core build and shared-context checks pass; Windows/Office/VSTO
qualification remains open.

Current tool-result presentation slice (2026-09-08): local typed text/list/table/change
blocks replace browser payload guessing. Exact chat/run/call requests lazily project
retained results; VBA changes filter calls before folding, and artifact comparisons
require exact source-message attribution. Missing evidence differs from no change;
unverified plans use the shared explicitly labelled diff renderer. Expanded cards
show tool id/error/message, followed by previews and raw JSON. JSON defaults to a
taller resizable tree; diff surfaces support both themes and search/read labels are
shorter. No new durable store/model schema or Windows gate closure.
Checks: typed presentation 3/3, run changes 3/3 and agent JSON viewer 5/5 pass;
Chromium layout and lazy delivery/cancellation checks pass. Version-format and diff
checks pass. HTML/Plan tool-to-card integration and Windows/Office/WebView2
qualification remain open. See [step presentation](../trajectory-query.md#expanded-chat-steps).

Current user-requested diff correction (2026-09-08): unverified VBA results now
expose retained original → intended source with an explicit planned/unverified
label, per-entry counts and an overall header total including planned lines.
Unverified subtotals and unavailable-source notices appear only when applicable.
Verified `after` and journal outcomes remain unchanged; previews never bridge verified comparison chains.
Creation/deletion/rename and source budgets retain explicit missing-data behavior.
Checks: focused run changes 3/3, integrated Chromium summary/narrow/wide checks,
version-format and diff checks pass. Windows/Office/WebView2 qualification remains
open. See [run text changes](../conversation-protocol.md#run-text-changes).

Current slice (2026-09-08): user-authorized Outlook attachment reads are implemented
on `stab/11-outlook-attachment-read` in a separate worktree. PDF/text/image files
use semantic `common.resources_*` targets, typed bound capture, retained source CAS
parts and existing text/media model routing. Limit: 20 MiB; selected-mail attachment
discovery is explicitly partial in Explorer. Replaced runtime slots, historical
missing bodies and out-of-scope targets fail without live fallback. No new store,
compatibility path, model schema or UI; Office files/archives are deferred.
Checks: Outlook 15/15, shared live/retained continuations 1/1, existing PDF extraction
1/1 and project source includes 1/1 pass. Architecture dependency-direction check
still fails on six unchanged `MarkdownDocumentIdentity.` substring matches present
in base HEAD; tracked in BACKLOG. Real Windows build/COM/Outlook/vision delivery
remains open; Phase 12/release gates are unchanged.
See [attachment contract](../resource-fabric.md#outlook-attachment-content-user-authorized-2026-09-08).

Current correction (2026-09-08): reproduced same-response VBA
`patch → resources_read → stale write_module` incorrectly accepting the final
overwrite. Accepted calls now retain their model-input resource evidence; VBA
guards no longer borrow observations from later sibling results. The regression
rejects the stale overwrite before dispatch and permits a corrected whole write
in the next model response. Multiple managed writes remain allowed. The original
Windows screenshot incident still needs correlated chat/journal evidence; no
Windows gate is closed. The diff's formatting-related `unverified` ambiguity remains.
Checks: VBA 100/100, batch overwrite regression 1/1, managed write batch 1/1,
confirmation correlation/replay 2/2 and diff checks pass host-neutral.
See [incident evidence and next step](RISK_REGISTER.md#vba-patch-incident--2026-09-08).

Current slice (2026-09-08), after `66da27f5`: **document-owned shared context**
is implemented through existing DocumentArtifactStore/authority/CAS. Successful
compaction publishes a separate versioned lineage per origin chat; another chat
explicitly discovers and reads it. Frozen authority filters claims individually;
source roles, safe citations and inherited provenance survive recompaction.
Writer drift preserves the old shared head and records a local publication issue.
Origin deletion/CAS retention pass, including externalized paged archives in a
native run with a deterministic model stub. The CAS scanner and header accounting
now distinguish evidence checksums from actual payload references.
Checks: shared context 2/2, compaction 4/4 (one shared-context overlap), resources
29/29, CAS 7/7 and storage headers 1/1 pass. `ValidateVersionFormat` and diff
checks pass.
No separate store, automatic cross-chat instructions or protocol/schema change.
Next: explicit pre-compaction publication/claim-text search require a bounded
follow-up; performance measurement remains deferred. Windows/Office/WebView2,
target-model, layout and previously recorded inventory/search gaps remain open.
Version remains `16.1.0-dev`.

Current slice (2026-09-08), after `b01babf2`: **typed claim provenance prerequisite**
is implemented in existing compaction/compiler. `context-claims-v4` preserves
constraint/decision/observation/interpretation/question/next_action and attaches
source roles in runtime. Source-role checks reject assistant/tool instructions as
user decisions and prevent interpretation promotion during recompaction. Frozen
authority still excludes changed evidence per claim. Invalid extraction leaves
the previous checkpoint intact; older/untyped checkpoints are skipped, not deleted.
This does not publish shared memory yet. Next: document-owned versioned publication
and explicit cross-chat discovery/read of claims through existing Resource Fabric,
including source retention, origin deletion and stale-read qualification. Performance
measurement remains deferred; Windows/Office/WebView2/target-model gates stay open.
Checks: focused compaction 3/3 and historical attachment reference-only 1/1 pass;
`ValidateVersionFormat` and diff checks pass. Version remains `16.1.0-dev`.

Current slice (2026-09-08), after `d29b1f58`: resource decision context now keeps
document/conversation scope, a concrete read representation and bounded optional
Markdown purpose beside complete targets. Historical working-set snapshots are
not advertised as current evidence. Compiler stale-read projections retain the
semantic recovery target and request rediscovery before reading again. No new
store, synopsis generation or authority projection is introduced.
Focused decision-context and stale-evidence checks pass (2/2). Historical attachment
body/URI exclusion assertions pass before a later pre-existing compaction-fixture
failure; the broader working-set test still exposes inherited raw-metadata search
drift. Both integration gaps are recorded in BACKLOG, not counted as passing gates.
Large-library memory/performance measurement is deferred at the user's request.
Next: richer source-backed shared decision context; existing Windows/Office/WebView2,
layout, target-model and inventory gates remain open.

Current slice (2026-09-08), after `22575b8c`: **semantic Markdown section reads and
action-purpose guidance** are implemented host-neutral. `resources_read` now accepts
a unique ATX heading for document Markdown/Plans and complete uploaded Markdown.
Normal Gateway/evidence retains exact selected character coverage, never a whole
read guard. Duplicate/missing/oversized sections, incomplete extraction and writer
drift are refused. Discovery and selection share one scanner; v1 index bytes and
existing whole reads remain unchanged. Schema/usage guidance explains the next
read action and connects findings with actual upcoming calls.
Checks: resources 29/29, shared Markdown 4/4, document Plan 2/2 and schema mirroring
pass. R61 inventory matches the reviewed new read schema but still fails on inherited
`resources_find`/`tools_upsert` drift recorded in BACKLOG. Target-model response quality is unqualified.
`ValidateVersionFormat` and `git diff --check` pass; version remains `16.1.0-dev`.
Next: large-library search/cold allocation measurement and richer compiler context;
picker/history paging, PDF extraction/read coverage follow-up, authority recovery,
Windows/Office/WebView2/layout and upstream HTTP 502 gates remain open.
See [section reads](../artifact-library.md#implemented-markdown-section-reads--2026-09-08).

Current correction (2026-09-08): Excel semantic range targets now preserve exact
worksheet names and accept Excel-quoted sheet names such as
`Excel range: 'новый лист (2)'!A1:A2`. Discovery returns the preserved sheet name
instead of an upper-cased target, so the model can copy the advertised target or
provide the quoted form directly. Focused Excel read harness checks pass
host-neutral; Windows/Office/WebView2 qualification remains open.

Current correction (2026-09-08): unknown side-effecting tool results no longer
stop the current Agent run or close the remaining managed-mutation batch members.
The uncertainty is still retained in cumulative execution health, final status and
duplicate-call protection for the exact same unknown call. HTML ancestor recovery
now renders as a warning while the active revision remains mutable, and uses the
red blocked state only when recovery disables mutation. Focused kernel and HTML
harness checks pass host-neutral; Windows/Office/WebView2 qualification remains open.

Current correction (2026-09-08): every current Tool Result now fails closed on
malformed wire and removes all exact resource references plus runtime `rna://`
values from generic model projections; switched result prose uses the same boundary.
Completed mutation folding consumes that same projection instead of durable result
prose/effect labels. Compaction checkpoint artifacts are excluded from model resource
index/discovery and only authority-filtered claims are replayed. Semantic artifact
search no longer builds snippets from raw runtime metadata. User data fields
and non-runtime URIs remain intact; `RUNTIME_CONTEXT.active_plan` no longer exposes
its internal Plan id. Compaction uses request-local source aliases, omits durable
message/claim/tool-call IDs, and sanitizes retained claims again on replay.
Focused projection, causal-folding, Plan-context and budget checks pass host-neutral;
Windows/Office/WebView2 and target-model qualification remain open.

Current correction (2026-09-08): model-facing `common.tools_upsert` is now a true
create-or-update operation without model-selected existence modes. Tool-manifest
schema failures name the authored `parameters` field, authoring guidance includes
its exact strict outer shape and forbids replacing a failed executable tool with a
skill. Failed action rows expose the stable error code beside the localized status.
Focused authoring/parser and web activity checks pass host-neutral;
Windows/Office/WebView2 and target-model qualification remain open.

Current correction (2026-09-08): HTML authored workspace operation receipts are
now keyed by exact tool call id in addition to chat/run/step. Multiple HTML/CSS/JS
writes emitted in one model batch no longer collide after the first publication
as `html_attempt_already_published`; true replay of the same call remains blocked
before dispatch. Focused HTML batch/shared-publication, VBA mutation/effect and
kernel cumulative-health checks pass host-neutral. Windows/Office/WebView2 and
target-model qualification remain open.

Current correction (2026-09-08): externalized generic/chart tool results retain
their exact `relation=result` reference only in durable runtime evidence; every
model projection removes it while preserving distinct produced resources. Current
`document:`/`selection:` reads now resolve their live singleton directly instead
of enumerating unrelated Excel catalogs. Focused projection/budget, Excel read and
resource-discovery checks pass host-neutral. Windows/Office/WebView2 and
target-model qualification remain open.

Current slice (2026-09-08), after `4d53d366`: **HTML member discovery views** are
implemented host-neutral. Exact parent revision/CAS views reuse the shared text
engine for source files and serialized data bindings, without fetching bound data
or publishing authority heads. Model HTML scope and protected payload refs are
preserved. Exact snippet reads, same-source repair, GC/restart and stale selection
are covered.
Member-only search checks selection/generation after scanning; combined search
preserves member-index unavailability. The catalog still parses its bounded HTML
aggregate; no metadata-only/cold-allocation qualification is claimed.
Checks: HTML 35/35, resources 27/27 and document Plan 2/2 pass.
Version-format validation and diff checks pass.
Next: semantic section reads through the existing resource contract. Picker/history
paging, richer compiler context, cold allocation, Windows/Office/WebView2/layout,
target-model and reported upstream HTTP 502 qualification remain open.
See [HTML discovery](../artifact-library.md#implemented-html-member-discovery-views--2026-09-08).

Previous slice (2026-09-08), after `4729c101`: **uploaded text discovery views** are
implemented host-neutral through the same revision/CAS text index owner. Original
search binds the exact retained extraction hash, finds late content across chats,
and preserves bounded extraction/PDF page coverage. Only Markdown gets heading
context. Derived-byte repair, exact snippet reads, GC/restart and missing-extraction
refusal are covered; no alternate store/extractor or read-evidence changes.
Checks: resources 27/27, document originals 2/2 and document Plan 2/2 pass.
Version-format validation and diff checks pass.
Next: HTML member text views, then semantic section reads. Picker/history paging,
richer compiler context, cold allocation and Windows/Office/layout/target-model
qualification remain open; the reported upstream HTTP 502 is not resolved here.
See [uploaded text discovery](../artifact-library.md#implemented-uploaded-text-discovery-views--2026-09-08).

Previous slice (2026-09-08), after `62ec1f8e`: **exact text discovery views** are
implemented host-neutral for document Markdown/Plans. A deterministic section/chunk
view reuses existing revision journal/CAS; current search reaches late content,
retains source offsets and exposes bounded heading context plus authored descriptions.
Derived-byte loss rebuilds only from the same exact body; missing sources remain
unavailable. Search checks generation after its last page, including indexing races.
No new store, authority publication or whole-read evidence. Cold indexing reads a
bounded source; this is not an inverted full-text index.
Checks: resources 25/25, HTML 33/33, shared Markdown 4/4 and Plan 2/2 pass.
Version-format validation and diff checks pass.
Next: extend existing views to uploaded extracted text/HTML members and useful
section reads. Picker/history paging, richer compiler context, cold allocation,
Windows/Office, upstream HTTP 502, layout and target-model qualification stay open.
See [text discovery](../artifact-library.md#implemented-exact-text-discovery-views--2026-09-08).

Previous slice (2026-09-08), after `cf309b64`: **bounded document discovery pages**
are implemented host-neutral. Existing authority Heads now support ordered bounded
range reads. Model list/search hydrates current roots in pages without copying full
authority snapshots or reading all library metadata. History/receipt ranges are
excluded; exact snapshot identities resolve directly. Source offsets preserve holes
from unavailable/filtered entries; generation changes reject continuation. Metadata
recovery at unchanged generation needs a fresh scan for previously omitted slots.
Gateway/search source-page ceilings preserve honest incomplete results; filtered
counts are explicitly distinguished from source-slot totals. No new durable store.
Checks: resources 23/23 (including source paging), HTML 33/33, shared Markdown 4/4,
Plan 2/2, originals 2/2, atomic authority replay and Excel regression pass.
Version-format validation and diff checks pass.
Next: content/section indexing and richer shared context; picker/history paging and
cold replay/write allocation remain open. Windows/Office, upstream HTTP 502, layout
and target-model qualification are not closed. See [bounded discovery](../artifact-library.md#implemented-bounded-document-discovery-pages--2026-09-08).

Previous correction (2026-09-08), after `ff47c276`: **incomplete Excel catalogs no
longer block document reads**. The reported `document:` read enumerated all document
catalogs and failed on bounded defined-name capture. Resolution now uses the live
document provider's exact kind/title and bound-session guards. Names/tables list
captured metadata with honest terminal truncation; point name/table reads still
require complete identity evidence. Excel search preserves page/source/result
truncation and coverage changes invalidate continuation. No limit increase/store.
Checks: 201-name reproduction 1/1, Excel names 2/2, tables 2/2, reads 5/5 and
resources 22/22 pass host-neutral. Windows/Office retest of the reported workbook
and the separately observed upstream HTTP 502 remain open.
Next: resume bounded source enumeration/content indexing and richer shared context.
See [Excel discovery contract](../resource-fabric.md#excel-catalog-discovery-and-document-reads).

Previous slice (2026-09-08), after `2598e224`: **partial current artifact discovery**
is implemented host-neutral. One authority capture chooses exact current Plan/HTML/MD
snapshots before metadata reads. Missing historical records no longer poison current
search; missing current metadata/body or unknown heads preserve healthy results with
explicit incomplete coverage. No fallback to older revisions or unique-target/absence
inference from unavailable scopes. The model gets a recovery hint; specific live
provider errors remain intact. Page fingerprints include authority generation and
availability. No second store/index or discovery-time publication is introduced.
Checks: resources 22/22, HTML 33/33, shared Markdown 4/4, document Plan 2/2
and production source inclusion pass. Version-format and diff checks pass.
Next: bounded source enumeration/content indexing and richer shared resource context.
Picker/history scans and whole-authority recovery remain separate open work. Plan/HTML
batch operation identity, Windows/Office/WebView2, target-model and Playwright layout
gates remain open. See [partial discovery](../artifact-library.md#implemented-partial-current-discovery--2026-09-08).

Previous slice (2026-09-08), after `b9d434ec`: **independent Markdown is implemented
host-neutral in Agent mode**. The common authored-record/CAS path retains complete
MD and purpose/contents metadata. Native save/restore uses typed prepared intent,
exact semantic targets, document head guards and atomic publication/receipts.
No active-MD singleton, Plan/HTML embedding or additional store is introduced.
Another chat can find/read/edit MD or attach/refresh its exact link through the
existing picker. Multiple MDs (including equal titles), same-step calls, new-revision
restore, historical refs, missing-body unlink, fork and origin deletion/GC are covered.
Chat reopening loads MD history metadata only. The runtime supplies prepared intent
consistently to handler and observer prepare/read-back, preserving kernel execution
identity and event format. Chat mode remains read-only.
Checks: shared Markdown 4/4 (including full agent/event replay), runtime 15/15,
HTML 33/33, agent confirmation replay, R61 inventory, source inclusion, document
Plan 2/2, working set 3/3 and viewers 3/3 pass. Working-set/library/Plan browser
checks, version-format validation and diff checks pass. The earlier unrelated
prompt-target assertion remains open as recorded below.
Next: indexed/partial discovery and richer shared resource context. The pre-existing
Plan/HTML operation-key collision in multi-call model steps is recorded separately
in [backlog](BACKLOG.md#plan-operation-identity-edge-case--2026-09-08); Markdown includes call identity.
Windows/Office/WebView2, target-model and Playwright layout gates remain open.
See [Markdown contract](../artifact-library.md#implemented-independent-markdown--2026-09-08).

Previous slice (2026-09-08), after `c077ab38`: **shared authored HTML is implemented
host-neutral** through the common `DocumentArtifactStore` record path and existing
mutation observer/authority/CAS. Workspace, authored JSON, logical head and operation
receipt publish under the document lease; the chat persists selection/links only.
Another chat can select current HTML through «Ресурсы → Из документа…» and edit it;
stale/concurrent writers are rejected before dispatch. Undo/redo create new causal
revisions, dialogue rewrite/fork preserve shared heads, and root/member refs rebuild
one snapshot on restart. Supported head bindings stay dynamic. Unlink survives a
missing body; origin deletion/GC retain HTML and authored JSON. No separate HTML
store or compatibility fallback is introduced; old chat-owned mutation is rejected.
Checks: HTML 33/33, chat lifecycle 2/2, logical restore and fork refusal, document
Plan 2/2, originals 2/2 and source inclusion pass. Artifact filter: 27/28; the
then-unquoted prompt-target assertion was later updated in the harness; this 27/28
result is historical and does not state the current gate outcome.
Working-set, library, Plan and HTML-action browser checks pass.
The later Markdown slice above closes independent MD authoring; indexed
discovery/recovery and richer resource context remain open. Windows/Office/WebView2, target-model and Playwright layout
gates remain open. See [HTML contract](../artifact-library.md#implemented-shared-html-publication--2026-09-08).

Previous correction (2026-09-08), after metadata recovery commit `bd7a43b1`:
HTML UI delete file/data, entry selection, import/export and undo/redo/recovery now
require the captured chat, active snapshot and session revision. The addressed
controller reserves/reloads the chat, checks the bound document and revalidates
immediately before mutation dispatch. Old unversioned controller signatures are
removed. UI captures before dialogs, suppresses duplicate/competing actions and
preserves new local edits or navigation state when a response arrives late;
stale export responses still release their exact leases. Focused guard/bridge,
HTML editor/import/export/source and UI race checks pass host-neutral.
At that checkpoint shared HTML/Markdown still awaited the ownership move; shared
HTML/Markdown and partial model discovery are now implemented above. Bounded indexing
and authority-journal recovery remain separate open work. Windows/Office/WebView2 and Playwright layout gates
remain open; the production controller's live document switch needs Windows evidence.

Previous correction (`bd7a43b1`): individual metadata loss no longer hides a chat
or healthy document-picker entries. Exact unavailable projections permit unlink;
strict reads/selection refuse guessing, model manifests explain recovery, and
restored metadata clears the transient issue without reattaching links.

Previous slice (2026-09-08): originals/Plan working-set links implemented in isolated
`stab/11-artifact-working-set`. Typed document picker attaches originals and selects
an exact current Plan in any chat of the document; «Убрать» removes only that chat
link and selection, preserving historical refs and shared resources. Membership is
chat-event-backed; prompt/library/new compaction projections, restart, fork and
clear consume it. Missing Plan bodies do not block unlink. Captured chat/revision,
optimistic save and document mutation lease reject stale or competing changes;
UI ignores late navigation responses. Host-neutral working-set, document Plan,
bridge, projection and browser checks cover this slice.
The later shared HTML/Markdown slices above extend these controls. Full indexed/
partial model enumeration and Windows/Office/WebView2 qualification stay open.
Playwright layout is still unavailable locally.
See [canonical artifact contract](../artifact-library.md#implemented-working-set-links--2026-09-08).

Latest chat message-cleanup slice (2026-09-08): user Markdown preserves single
line breaks; raw diagnostics retain newlines in details. Duplicate diagnostic
narration/outcome rows and repeated error-panel labels are removed. One Russian
run cause uses existing kernel reasons; unknown effects and zero-call failures
remain visible. Distinct calls, model narration and stored/model data are retained.
Focused Web and real Chromium Markdown/narrow-layout checks pass. Windows/Office/
WebView2 qualification remains open; no runtime/model contract changed.

Latest user-approved run-diff slice (2026-09-08): completed runs expose retained
VBA/module and authored text/HTML-member comparisons with compact signed totals,
expandable source rows and explicit unavailable/unknown bounds. Exact parent/call
correlation survives replay and confirmation; one shared formatter replaces the
VBA-only changed-span path. Focused retained-CAS/VBA and Web/Chromium checks pass.
The pre-existing run-view-state cache-key assertion failure is recorded in BACKLOG.
Windows/Office/WebView2/controller delivery remains open; Phase 12 is not started.

Latest catalog-display and compact-chat slice (2026-09-08): optional immutable
ToolDisplayMetadata is retained by catalog storage, model/Library authoring,
accepted invocation projection, confirmation replacement and chat forks. Shipped
labels are catalog-owned; web id/verb dictionaries are removed. Custom scalar
target selectors require no web changes. Display-only edits participate in Library
revision/read-back but not execution authority or model invocation serialization.
Chat progress hides raw phase names; zero-call overviews disappear while runtime
warnings remain. Actions have a divider and 6 px answer gap; assistant controls are
left-aligned before metadata, user controls right-aligned. Targeted metadata,
replay, Web and Chromium layout checks pass; Windows/Office/WebView2 qualification
remains open. Typed result summaries before budgeting remain the bounded follow-up.


Latest result-presentation refinement (2026-09-08): action/icon/target/outcome use
one muted color except failed outcomes; literal code-style targets wrap safely.
All tools have a bounded outer JSON-type fallback without a custom renderer.
Source-checked resource captions distinguish text/source/structure, tables, metadata
without content and media prepared for the next model request. Custom result text
remains in details; it cannot establish mutation effects or media delivery. Built-in
action/target maps remain; catalog-owned typed display metadata is recorded as a
bounded design follow-up in BACKLOG, with no new runtime/model contract. Focused Web
and Chromium layout checks pass; Windows/Office/WebView2 gates remain open.


Latest chat presentation correction (2026-09-08): tool rows keep action, full
semantic target and a Russian result caption, with separate operation icons.
Known resource/capability reads show counts and incomplete-search/read outcomes;
unknown effects remain unverified and failed no-ops remain failures. Accepted
Word/PowerPoint/Outlook arguments now supply useful target captions. Raw result
messages, codes and JSON stay in labelled journal details; a guarded link opens
the existing next-request context inspector. Display captions do not rewrite
model-facing/durable results. Focused Web, narrow/desktop Chromium layout and
host-neutral transcript/causal-projection checks pass. Windows/Office/WebView2
and target-model gates remain open.

Latest chat-order correction (2026-09-08): chat-tree siblings sort by a disposable
`LastActivityUtc` header/summary projection from retained messages, run start and
creation time. Opening, renaming or saving metadata does not promote a chat;
background catalog updates sort immediately on render. Equal timestamps use chat
id. Warm/cold storage and live-summary regression passes 1/1; focused Web checks
and the version-format gate pass. No new durable state or migration is introduced.
Windows/Office/WebView2 qualification remains open; Phase 12 is not started.

Latest artifact/diagnostic UI correction (2026-09-08): run cards now collapse
referenced revisions by Library logical identity without changing exact message
refs or merging independent same-name files. Typed `documentScoped` labels expose
implemented document/chat ownership; shared HTML and working-set selection remain
open. Tree titles/metadata use separate lines and one active selection. Markdown
has one source toggle; technical events use a draggable split and one JSON viewer
for data/evidence/lazy payload. The shared viewer renders bounded embedded JSON
strings as nested trees while retaining exact source/copy, duplicates and numbers.
Focused Web regressions, a real Chromium layout/drag check and the host-neutral
clone/DTO ownership check pass. Replaced diagnostic panels/styles are removed.
Windows/Office/WebView2 and target-model gates remain open; Phase 12 is not started.

Latest tool-result correction (2026-09-08): resource-read model projection now
preserves the entire `ResourceTableBatch` payload, including user columns and
nested keys named `id`, `customerId`, `revision`, `hash`, `uri` or `offset`.
Runtime metadata outside that payload remains filtered and durable results remain
unchanged. A focused regression failed before the fix and now passes for table
and records in all three result roles; the existing runtime-evidence projection
check also passes (2/2). Canonical protocol examples now use semantic resource
reads and current prompt schema 29; the two completed audit backlog entries are
removed. No Office/VSTO validation was run; Windows and target-model gates remain
open.

Latest resource-read correction (2026-09-08): exact Office target queries are now
point lookups and no longer scan unrelated document catalogs, so an available Excel
range cannot inherit their partial/unavailable result. Discovery publishes each
provider-owned record-view usage; model and HTML table/records reads keep the path
runtime-owned, while conflicting explicit paths fail before cell capture with typed
`RejectedNoEffect/Replan` recovery. Partial broad discovery now tells the model to
use returned usable targets instead of repeating the same find. Focused Excel
read/table/name, resource-discovery and HTML binding checks pass host-neutral;
Windows/Office/WebView2 and target-model qualification remain open.

Latest UI correction (2026-09-08): the shared `send` boundary now catches
`postMessage` exceptions, removes the affected pending entry and rejects the
original caller promise even when queued behind initialization. Other requests
remain pending; no automatic operation replay is introduced. Bridge bootstrap
checks pass 10/10, including ready/queued/init send failures, and multi-chat checks
pass 3/3. No build or Office validation was run. Silent transport-loss termination
and actual Windows/WebView2 navigation/streaming qualification remain open.

Latest build correction (2026-09-08): the reported Windows solution failure was
one production `RNAssistant.Office` compile break followed by cascading `CS0006`
errors, plus stale source-linked MockDemo contracts. `ChatStore` now exposes its
typed `DocumentArtifactStore` owner across the Core/Office assembly boundary, the
artifact viewer imports its exact `ChatSession` model, and MockDemo includes the
shared `OfficeToolFailure`, stages attachments through the bounded upload data
plane, emits conversation-response v5, consumes folded mutation frames, and uses
the current semantic HTML tools/metadata-only projection. Core and Office Debug
builds and the MockDemo build pass host-neutral; all four mock model profiles,
failed-turn persistence and artifact-commit projection pass. Windows x64 solution,
VSTO/Office/WebView2 validation remains open.

Latest correction (2026-09-08): bridge аварийные ответы теперь используют один
typed JSON contract, сохраняют request id и корректно экранируют exception text;
неразбираемый/некоррелируемый transport failure завершает все pending UI promises
без автоматического повтора mutation. Перед каждым model step актуальный context
снова проходит authority filtering, folding, budget и при необходимости
incremental compaction. Conversation-response v5 допускает только runtime-owned
последовательные batches независимых reads и managed mutations с индивидуальными
guards/verification/commit; unknown закрывает undispatched tail. Prompt schema 29
и корневой `AGENTS.md` выровнены с v5 и semantic model targets. Focused bridge,
context, protocol, kernel, ToolRuntime и managed-write checks проходят
host-neutral. RuntimeLog/UI parse hot path уже был исправлен предыдущим slice;
его вклад в реальные freezes не измерен. Windows/Office/WebView2 и target-model
qualification остаются открыты.

Follow-up audit (2026-09-08): JSON `null`/primitive from the WebView host now
terminates every pending bridge request as an explicit transport failure instead
of throwing inside the UI listener and leaving controls stuck. The stale HTML
workspace delivery-graph expectation was updated to the already shipped cache
key. Rebuilt bridge checks pass 31/31, changed Web tests pass 14/14, and the
production-project source-inclusion check passes. The Playwright-only layout test
was not run because that dependency is unavailable locally; Windows/Office/WebView2
qualification remains open.

Latest correction (2026-09-08): three residual contract gaps are closed
host-neutral. Message-backed ECharts controls pin their source chat/message before
delayed save or Excel refresh; addressed persistence neither mutates a newly active
chat nor changes the backend selection. Superseded or
unavailable exact resource reads now enter only the detached model request as an
explicit `status=error` reread marker; durable successful history stays immutable,
and oversized resource evidence has direct regression coverage. Custom VBA package
results own and transport typed `Replan`, `RetryLater` or `ToolDefect` recovery;
possible effects remain `unknown` without recovery. Every manual `runTool` request
now carries the exact source chat through the typed bridge; missing identity is
rejected, addressed loading does not change the active backend session, and a later
chat switch cannot retarget execution. Addressed loading also preserves a foreign
document's authority/locator, while manual tools reject a chat outside the current
bound document before dispatch. History/settings mutations, pending confirmations,
HTML workspace operations and artifact viewers now also load their captured chat
without changing the backend selection; only explicit navigation owns selection.
Focused browser/bridge/resource/VBA checks pass; Windows/Office/WebView2 and target-model
qualification remain open.

User-authorized document artifact ownership work (2026-09-07): target behavior and
ordered replacement/acceptance are recorded in [Artifact Library](../artifact-library.md#authorized-document-ownership-cutover--2026-09-07).
Slice **1a, document-owned sent originals, is implemented host-neutral** using the
existing authority journal/CAS. Another chat of the same document can discover and
read uploaded MD/files/PDF/images without the origin message. Publication/link
retry is idempotent, conflicting metadata is rejected, fork preserves the exact
ref, and chat deletion/restart/GC retain original and extraction payloads.
The two `document originals:` checks include concurrent store instances, failed
chat-link retry, discovery/resolve/viewer round-trip, missing extraction and
cross-document refusal. Attachment and existing viewer/Gateway/fork checks pass.
Slice **1b, document Plan publication (2026-09-08)** now switches the native Plan
tools to per-Plan document heads and retained snapshots. The observer verifies
the selected base before dispatch and publishes snapshot/head/operation receipt atomically; chat
projections carry selection/links. Fork preserves that selection, history edits
cannot undo a document tombstone, and discovery shows current Plans across chats.
Targeted checks cover competing writers, independent Plans, restart, missing body,
foreign scope, origin deletion/GC, failed chat-link save and runtime-operation reuse;
Eighteen focused host-neutral checks pass, including the existing
Plan/restore/lifecycle/Gateway/viewer and bounded-prompt regressions. Windows and
target-model qualification remain open.
Next: shared HTML and independent authored Markdown owners/tools, plus explicit
working-set selection/current-version refresh. The current Plan tools can edit an
inherited selection; selecting an arbitrary existing Plan in another chat is not
implemented. Bounded indexed discovery remains open. This is not the full cutover.
Existing Resource MASTER invariants and Windows gates remain in force; Phase 12 is
not started.

Latest correction (2026-09-07): the four read-only Office search owners now retain
typed recovery for every rejected call. Invalid query/pattern/domain input is
`RejectedNoEffect/Replan` (or `RetryLater` for an explicitly transient domain
outcome); resource capture/admission errors reuse the shared Office resource
classification. Existing error codes, payloads, capture bounds and legacy
`retryable` fields are unchanged. Excel, Word, PowerPoint and Outlook invalid-input
and incomplete-capture checks pass host-neutral; Windows/Office qualification
remains open.

Previous correction (2026-09-07): the integrated `ModelContextCompiler` again owns
oversized exact-read admission. If a complete successful resource/capability read
does not fit after correctness filtering, its detached request projection becomes
the explicit `resource_evidence_context_too_large` or
`capability_evidence_context_too_large` error instead of failing the whole model
request or exposing a partial success. The append-only durable Tool Result remains
`ok` and immutable. Normal and large in-budget VBA evidence, exact capability
discovery, disabled-skill isolation and the oversized-capability boundary pass
host-neutral; Windows/WQ gates remain open.

Previous validation correction (2026-09-07, test-only): capability/skill fixtures now
enter the same immutable catalog publication used in production before testing
`common.capabilities_read`. Injecting a model-visible skill without an exact
`Publication` had created an impossible split snapshot and correctly failed with
`RESOURCE_SNAPSHOT_UNAVAILABLE`. No fallback or production path was added; exact
publication remains mandatory.

Previous correction (2026-09-07): Prompt/Skill authoring, HTML workspace,
Capability and native Plan-question outcomes now own typed recovery instead of
leaving the kernel to interpret legacy `retryable` booleans. Model-correctable
validation, stale guards and semantic-target refusals are
`RejectedNoEffect/Replan`; unavailable local stores are `ToolDefect/None`; resource
exceptions preserve the shared Office classification. Handlers only transport the
decision. Focused host-neutral checks cover stale Prompt/Skill preparation, invalid
HTML binding and invalid questions; Windows/WebView2 qualification remains open.

Previous prerequisite correction: whole-target prompt index admission, truthful
artifact/member search bounds/unavailability, search-only target metadata, prompt
schema 28 operational summaries and strict compaction claim-array extraction.
No new shared store, adapter, wire root fields or cross-chat access is enabled by
this correction. Eleven focused harness checks pass: resource Gateway, incomplete
artifact search, discovery coverage, bounded prompt index, structured compaction,
default prompts, three prompt-review cases, exact empty text and on-demand reread.
Only host-neutral harness execution; target-model quality and Windows delivery
remain unqualified.

Previous correction (2026-09-07): Tool authoring recovery is now owned by the typed
outcome instead of inferred in the handler. Schema, manifest and package validation
failures are `RejectedNoEffect/Replan`; unavailable authoring storage is
`ToolDefect/None`; both preparation and execution preserve the contract. The exact
authoring regression passes host-neutral. Windows/WebView2 qualification remains
open.

Previous correction (2026-09-07, test-only): the Plan restore/removal fixture now
uses the current provenance-bearing structured compaction reply instead of the
removed free-form `summary` response. The append-only Plan test passes again; no
production behavior changed. A separate historical-attachment projection failure
found during this check is recorded in `BACKLOG.md` and was not folded into this
slice.

Previous correction (2026-09-07): chat-local Task List and Plan handlers now separate
model correction from transient retry. Validation and state refusals before dispatch
are `RejectedNoEffect/Replan`, missing session is non-retryable, and a missing
required dispatch boundary is `ToolDefect`; after-boundary failures remain `Unknown`.
The ambiguous legacy `retryable` boolean is preserved for compatibility but no longer
drives recovery for these tools. Focused native Task List/Plan checks pass
host-neutral; Windows/WebView2 qualification remains open.

Previous correction (2026-09-07): definite typed domain errors from Excel, Word,
PowerPoint and Outlook now retain their existing no-effect guarantee through the
tool boundary as `RejectedNoEffect`. Existing typed retryability maps to `Replan`
or `RetryLater`; handlers do not inspect messages or error-code strings, and
`Unknown` outcomes remain non-recoverable. Focused host-neutral verification covers
both permanent and transient Excel refusal; Windows/Office qualification remains
open.

Previous correction (2026-09-07): native Office handlers now share one runtime-owned
classification for session, document-guard, mutation-lock and resource-admission
failures. Closed/mismatched targets are definite `RejectedNoEffect`, occupied gates
are `BusyNoEffect/RetryLater`, infrastructure identity/lock failures remain
`ToolDefect`, and model-correctable resource requests are `Replan`; none are replayed
automatically. The audit also corrected `excel.create_chat_chart` registration to
the mixed read/mutation handler contract. ToolRuntime, all Excel native families,
Word, PowerPoint, Outlook, VBA and focused resource checks pass host-neutral.
Windows/Office/WebView2 qualification remains open.

Previous correction (2026-09-07): tool policy now derives `ReadOnly`,
`ManagedMutation` or `OpaqueAction`, and registration fails closed on a handler
contract mismatch. Failed execution carries typed failure/retry recovery. Exact VBA
patch binds current live source under the document gate instead of forcing a full
reread after every verified mutation; whole-module overwrite keeps the stricter
model-observation guard and returns bounded current source plus changed-span diff on
conflict. ToolRuntime forces every opaque dispatch, currently VBA macros/packages,
to `unknown`, and any unknown effect
blocks later mutations in the same run while allowing inspection reads. ToolRuntime,
51 kernel, six patch, queued-guard, ten mutation and package-runtime checks pass
host-neutral. The broader R61 descriptor inventory still reports its pre-existing
golden-hash drift for several schemas not changed by this slice. Target-model and
Windows/VBE qualification remain open.

Latest correction (2026-09-07): the reported `model_loop_stalled` did not mean that
the provider omitted `final`; strict v5 parsing proves that the target model returned
`final=false` with empty `tool_calls` three times, including for a complete
user-input request. The causal gap was that a persisted assistant checkpoint was
followed by another model dispatch without an explicit next-turn instruction. The
next request now receives a transient runtime continuation directing the model to
use `final=true` for an already complete answer/user-input request or emit actual
tool calls. It never enters durable chat history; the three-checkpoint fail-closed
guard remains. Fifty-one kernel checks, the production no-tool continuation check
and 13 conversation-v5
wire checks pass host-neutral. Windows/WebView2 and target-model retest remain open.

Latest correction (2026-09-07): oversized Excel search-scope reads now expose the causal recovery route instead of only saying to choose a smaller scope. Resource discovery marks these targets as discovery-only and directs the model to `excel.find_cells` for queried discovery or Excel range/table/name targets for worksheet values; `common.resources_read` repeats this boundary. The identical-call and undispatched-tail events in the report remain correct bounded consequences. Focused Excel search and resource Gateway checks pass host-neutral; target-model and Windows/Excel qualification remain open.

Latest correction (2026-09-07): the user-reported high Agent failure count is separated into expected guards and a model-facing HTML/Excel contract defect. `common.html_data_bind` now exposes disjoint complete/structural/page argument shapes, validates structural/page paths before execution, and refuses Excel search scopes as table sources with the exact range/table/name recovery route. `common.resources_read` publishes the same structural path grammar, while `excel.find_cells` is explicitly discovery-only. Focused HTML native ownership/schema and resource Gateway checks pass host-neutral; target-model and Windows/Excel/WebView2 qualification remain open.

Latest correction (2026-09-07): user-reported Excel search-scope metadata reads no longer fail with `An immutable revision view was changed`. Whole-read assembly preserves absent text instead of inventing an empty CAS body during evidence retention. Repeated native metadata reads retain the same revision without cell capture; the extended Excel search snapshot test and existing Gateway read/search/page test pass host-neutral. Windows/Excel qualification remains open.

Historical target (2026-09-07): 16.1.0
Historical phase: Milestone WQ — обязательный Phase 11 existing-tool migration route и final active-legacy cleanup через 11T10 завершены host-neutral; Phase 12 тогда ещё не начат.
Historical task: user-authorized unified Resource Architecture direct cutover is complete host-neutral on `stab/11-resource-direct-cutover`, including final consumer/legacy cleanup (2026-09-07). The user-requested post-cutover performance hardening and 11E chat/diagnostics presentation slices were limited to dependency-safe host-neutral changes that consume the completed Resource architecture and do not close Windows qualification. The dependency order was [Resource MASTER](resource-cutover/MASTER.md): [URF](resource-cutover/UNIVERSAL_RESOURCE_FABRIC.md) → [Authority](resource-cutover/RESOURCE_AUTHORITY.md) → [Evidence/Compiler](resource-cutover/EVIDENCE_CONTEXT_COMPILER.md). This was one replacement workstream, not three parallel implementations. Explicitly deferred source allocation and all real Windows qualification remained open.
Historical execution mode: mandatory host-neutral route 0–11T10, WQ-A1–A5, R61/11O0–11O7, the separately approved D05.1–D05.6 slices and R65–R70 corrections завершены. WQ0 не блокировал implementation: текущий `RuntimeKey` exact bound Excel/Word/PowerPoint/Outlook object or window принят как lifetime assumption. Накопленные Windows gates по §16.1 квалифицировали итоговый post-cutover catalog/UI; Phase 12 тогда оставался заблокирован.

Historical next step: run the accumulated post-cutover Windows/Office/WebView2 qualification on the final candidate; see [MASTER acceptance reconciliation](../resource-fabric.md#master-acceptance-reconciliation--2026-09-07). Host-neutral cleanup was closed. Outlook MailItem.Body source allocation remained unresolved in [BACKLOG](BACKLOG.md#user-deferred-source-allocation--2026-09-07); Inspector pre-truncation serialization was fixed host-neutral, while real WebView2 qualification remained open. No further provider expansion, finer Excel coverage or cold-replay/checkpoint optimization was scheduled under that route.
Latest slice (2026-09-07): repeat-report chat duplication is reproduced through the real reducer/grouping/DOM pipeline: two saved steps plus replayed markers rendered four steps, and differing inline/CAS argument bodies duplicated calls. Call identity now excludes payload representation; replayed markers reuse the existing step, repeated results update one row, final text/reasoning replay is suppressed and distinct transcript segments have separate DOM keys. The new integrated regression covers active-chat restoration and final delivery; browser QA shows one run, two steps and four calls, then one final answer. Previous synthetic overlap coverage missed these paths. Windows/Office/WebView2 delivery qualification remains open.

Previous slice (2026-09-07): duplicate persisted/live run blocks are merged by exact runtime identity in the transient chat projection. Durable terminal results retain precedence over stale progress, new calls stay visible, resource provenance is retained and final snapshots retire live replay. Regression checks cover overlap, separate runs and unchanged streaming-history retention. Tool-row spacing is reduced to 2 px within a step. Windows/Office/WebView2 delivery qualification remains open.

Previous slice (2026-09-07): user-reported Library Implementation/Test layout is corrected host-neutral. The component toolbar now wraps above metadata and full-width VBA source; Test uses themed controls, neutral argument rows, bounded form width and no empty result box. Removed the displaced two-column package layout and obsolete toolbar breakpoints. Real Chromium geometry checks pass at four pane/window sizes in both themes across tab changes; typed form/docs/actions and editor checks pass. CSS cache keys are updated. Windows/Office/WebView2/DPI qualification remains open.

Previous slice (2026-09-07): the repeated-failure guard now follows causal execution order. An immediate identical `error` remains bounded without redispatch, while a successful intervening state-changing call permits the same operation again; read-only success does not reset the guard. This covers failed Task List close, completed-step save, then valid close. Identical `unknown` possible-effect calls remain non-repeatable for the whole run. Fifty-one kernel checks and the Task List close lifecycle check pass; target-model and Windows/Office qualification remain open.

Previous slice (2026-09-07): user-reported VBA Library creation/save failures are corrected in the manual editor host-neutral. Add now supplies a valid commented manifest/entry stub; Clone changes the copied manifest id, preserving original sources. Repeated drafts use unique ids, unsupported hosts are refused, and generated module names fit VBE bounds. Focused editor/upload/contract checks and native authoring CRUD pass; the CRUD assertion now checks manifest validity rather than obsolete compact JSON formatting. Actual Windows/Office/WebView2 creation/save/install and any separately reported target-model failure remain unqualified.

Previous slice (2026-09-07): model-facing resource target admission is corrected host-neutral. The shared semantic resolver now rejects all URI-shaped targets, including retired `vba://` plus `rna://`, `cas://`, file and network forms, before provider/Office access. Current-history validation applies the same invariant to resource reads, HTML data binding and VBA backup restore; retained incompatible calls are reset-only. Tool descriptions require verbatim targets and prohibit title-derived addresses. Seventy-eight resource-focused harness checks pass; target-model and Windows/Office qualification remain open.

Previous slice (2026-09-07): user-requested compact action rows use muted regular 12 px labels, inline targets/results with natural wrapping, and a 6 px icon gap. Obsolete strong-color and multi-row styles are removed in both themes. Local browser QA at 320/600 px confirms wrapping without horizontal overflow and one current shimmer; the cache-delivery check passes. Windows/Office/WebView2 qualification remains open.

Previous slice (2026-09-07): WebView bridge UI starvation during long agent/tool runs is corrected host-neutral. Chat catalog/state/selection plus `sendChat`, confirmation and direct tool execution cross cancellable worker boundaries before controller work; Office-owned calls retain their bound dispatcher/STA. Focused regressions prove that chat selection/catalog access and request cancellation remain reachable while agent/tool work is blocked. Shared persistence-lock wait is still an open bounded risk; Windows/Office/WebView2 responsiveness qualification remains open.

Previous slice (2026-09-07): agent/tool-authoring failure loops are corrected host-neutral. `common.tools_upsert` now derives the inner VBA manifest component-name list from the single ordered outer source package, removing the conflicting duplicate model contract. The chat-header reducer reads scalar run metadata only, so CAS-externalized large accepted arguments cannot deserialize as a null `ToolCall`; full projection hydration remains exact. `AgentKernel` closes an identical call already ending in error/unknown without a second dispatch. Focused authoring, large active-call header/reload and 47 kernel checks pass; Windows/Office/WebView2 and target-model qualification remain open.

Previous slice (2026-09-07): message edit/fork controls now refresh independently of cached transcript bodies when sending, editing, confirmation or bridge availability changes. The regression covers restored user edit and grouped-run fork actions without remounting content; existing streaming retention and UI delivery checks pass. Runtime history mutation policy is unchanged; Windows/Office/WebView2 qualification remains open.

Previous slice (2026-09-07): runtime-owned resource identity is removed from every identified model-facing history path. Historical projection no longer appends `ResourceRefs` to text; stale-evidence markers omit URI/revision and emit no result resources; terminal mutation summaries retain semantic outcome/coverage only; compaction input no longer serializes durable resource references. Exact refs remain durable runtime metadata for authority, replay and checkpoint provenance. This is host-neutral and applies to Excel, Word, PowerPoint and Outlook; target-model and Windows/Office/WebView2 qualification remain open.

Previous slice (2026-09-07): user-requested live chat grouping correction keeps narration and all action rows visible throughout the run, including nested calls and earlier completed steps. Individual technical details remain expandable; only a terminal run folds the entire process. One current action owns shimmer, with icon-free thinking only between calls. The superseded per-step disclosure path is removed. Focused web checks and local browser component QA cover this presentation change; runtime authority and Windows/Office/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): the model-facing `common.resources_read` schema now matches runtime semantics. Complete `source`/text/structure/media/formulas reads and bounded `table`/`records` reads are explicit alternatives, so table-only selectors such as `limit`, `offset`, `path` and `fields` cannot be emitted with VBA source reads. The invalid combination is rejected at schema validation before provider dispatch; ordinary complete VBA source reads remain successful. Resource authority, bindings and Office behavior are unchanged; real target-model and Windows/Office qualification remain open.

Previous slice (2026-09-07): historical chart rendering and refresh are fixed host-neutral. Chart refresh now reuses the persisted sheet/range from the artifact, including legacy `selection` snapshots, instead of requiring the current Excel selection; the chart renderer cache key is bumped so old WebView2 assets cannot survive installation. Resource authority, bridge contracts, Office binding and Windows/COM/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): user-authorized chat/diagnostics UX is implemented host-neutral on `stab/11-chat-projection-ux`. Lifecycle no longer inherits historical call failure styling; adjacent runtime runs stay separate. Accepted targets and localized action labels are visible, one current status owns shimmer and the action-list disclosure, and live replacement retains disclosure state. Status headers have no icon or separate action count while active; completed steps show the count. Spacing and label-aligned arrows are consistent; separate streaming dots are removed. Existing typed trajectory rows expose cause/target/effect summaries, with unknown evidence prioritized and exact JSON/model bodies opened explicitly. Superseded presentation paths are removed. Focused web/typed-projection checks and local browser component scenarios cover the changed behavior; actual Office/WebView2/DPI/focus/live delivery gates remain open. Runtime dispatch/effect accounting, model wire, append-only events and CAS authority are unchanged. Per-effect reconciliation, richer family result metadata and the full Issue Center remain explicit [follow-ups](BACKLOG.md#chat--diagnostics-ux-follow-ups--2026-09-07).

Previous slice (2026-09-07): user-authorized screenshot corrections implement best-effort rejected/accepted parser diagnostics through one helper, explicit attempt-history final warnings, and capability-only pre-archival result-message copying. Ordinary results no longer enter the capability wire reader. Twelve focused harness cases pass (parser trace/cancellation, write error/unknown, tool-pack admission, oversized results and model projection); the unknown-effect prompt assertion now checks the existing compiler causal-frame contract instead of the retired raw error-code field. Runtime acceptance/dispatch/commit persistence, cancellation, repair limits and historical execution counts remain unchanged. Per-effect reconciliation, provider-intent declarative routing and family projection redesign remain explicit follow-ups, not completed contracts. The shared persistence-lock / synchronous chat-navigation projection and real Windows responsiveness gates remain open.

Previous slice (2026-09-07): stream UI hot-path correction is complete host-neutral. Stream/reasoning paints reconcile live units without reserializing transcript bodies or rebuilding resource navigation; full reconciliation retains unchanged attached nodes. Background progress batches sidebar paints at 100 ms while retaining every delta. A focused 1000-node / 100-delta regression and existing Markdown, lazy UI, run-state and bridge checks pass. The reported chat-switch freeze is not declared fixed: shared persistence-lock contention plus synchronous full chat/Office projection is recorded in RISK_REGISTER for the next bounded dispatch split and actual Windows timing evidence. CAS durability/commit barriers are unchanged.

Previous slice (2026-09-07): user-reported artifact/tab hangs have one additional host-neutral correction. HTML preview no longer rebuilds/reloads its iframe or closes resource leases on unchanged projection updates/tab revisits. Exact source/binding inputs and chat/workspace owner govern transient reuse; unavailable source and mode/detail transitions invalidate it. Focused preview reuse, source lifecycle, lazy UI and ECharts checks pass. This removes repeated script/chart startup, not all possible performance causes; real active-run artifact loading/tab responsiveness remains an open Windows/WebView2 gate.

Previous slice (2026-09-07): UI delivery and stream cache coherence are fixed host-neutral. The streaming transcript reconciliation fix now ships under a new `app-messages.js` cache key, and the web delivery tests/manifest match the current local-resource CSP and snapshot contracts. Resource authority, bridge contracts, Office binding and Windows/COM/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): prompt Inspector compile regression is fixed host-neutral. Inspector now reads enabled tools from the request-boundary published catalog through an explicit no-document-discovery ToolCatalogService entrypoint, preserving the frozen publication used for prompt templates and skills. Resource authority, bridge contracts, Office binding and Windows/COM/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): startup secondary-surface rendering is lazy host-neutral. Init/bridge-unavailable now render the chat first-paint path immediately, keep settings shell state current, and route Library, Artifacts and VBA refresh through `renderVisibleSecondarySurfaces` only when those panels are active; Artifacts and VBA still refresh on first tab activation. Cache keys and focused UI lazy/chat projection assertions are updated. Resource authority, bridge contracts, Office binding and Windows/COM/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): user-requested Inspector raw-JSON allocation fix is complete host-neutral. Serialization writes only the existing 512,000-character preview and stops request enumeration at the boundary; individual strings/property names also cap Json.NET escape-buffer allocation. Exact small JSON, escaped text, boundary flags, surrogate safety, opt-in behavior and download cleanup are checked. The full intermediate JSON path is removed; compiler/context-budget semantics and the existing 2 MiB download stay unchanged. Outlook and real Windows/WebView2 qualification remain open.

Previous slice (2026-09-07): artifact/resource UI navigation now uses a transient projection index instead of repeated head/artifact scans. `app-artifacts.js` builds local maps for artifact id, head by artifact/history id, revision resource URI and removed resource URI per received metadata snapshot; chat resource popover grouping is one-pass and remains a presentation projection only. Cache key and focused artifact/plan/card assertions are updated. ResourceAuthority, ResourceGateway, EventStore, Office binding and Windows/COM/WebView2 gates are unchanged and remain open.

Previous slice (2026-09-07): context usage display no longer recomputes the whole transcript on local UI updates. The meter keeps backend `ContextUsage` as the base/authority, snapshots that receipt on init/chat-state responses, and only adds bounded presentation deltas for recent local pending/failed messages plus changed manual context. It exposes client delta fields for UI diagnostics and does not affect ModelContextCompiler/request budgeting. Cache keys and focused web assertions are updated. Windows/COM/WebView2 gates remain open.

Previous slice (2026-09-07): splitter preference hot-path cleanup is complete host-neutral. Split-pane drag now coalesces visual ratio updates through one animation-frame callback, no longer writes `localStorage` or refreshes CodeMirror on every mousemove, and persists the final ratio plus editor refresh only on mouseup. This is cosmetic UI state only and does not touch Resource authority, EventStore, bridge contracts, Office binding or Windows/COM/WebView2 gates. Focused splitter browser-script test, UI lazy check and version-format gate pass.

Previous slice (2026-09-07): transcript incrementalization begins host-neutral. `renderMessages` now reconciles keyed message units and reuses unchanged DOM nodes instead of clearing and rebuilding the whole transcript; replaced/removed nodes unmount Markdown JSON viewers through the existing cleanup hook. Live agent, reasoning and stream surfaces are represented as separate transient units, so stream ticks replace only the live node. `app-messages.js` cache key and focused web assertions are updated. This does not change Resource authority, EventStore, bridge contracts, Office binding or Windows/COM/WebView2 gates. Focused UI lazy, Markdown JSON viewer, artifact projection and run-view-state browser-script tests pass.

Previous slice (2026-09-07): bridge read dispatch responsiveness begins host-neutral. Trajectory query and Qualification catalog/run reads now cross an explicit async `Task.Run` boundary after envelope/token validation and participate in request cancellation; payloads and response DTOs are unchanged. `init`, `listChats` and `getChatState` remain on the current path because their projection currently includes Office document state and must not be moved off the approved Office boundary in a host-neutral slice. This does not change Resource authority, EventStore, bridge contracts, Office binding or Windows/COM/WebView2 gates. Harness build plus focused trajectory and Qualification bridge tests pass with existing CA1416 warnings only.

Previous slice (2026-09-07): RuntimeLog hot-path pruning and async sink are complete host-neutral. Ordinary WebView control messages no longer write a runtime file-log entry or parse JSON solely for diagnostics. RuntimeLog now uses a bounded in-memory queue, one background writer/open stream, best-effort dropped-record accounting and shutdown flush; `Debug` writes only in DEBUG builds. Startup/failure/security/anomaly logs remain. This does not change Resource authority, EventStore, bridge contracts, Office binding or Windows/COM/WebView2 gates. Harness build, retired runtime-log bridge check and version-format gate pass with existing CA1416 warnings only.

Previous slice (2026-09-07): performance hardening step 1 begins with WebView CSS hot-path cleanup. The global `.shell *` scrollbar/state selectors are replaced by explicit scroll-surface selectors for transcript, chat/resource lists, viewers, diagnostics, Library/VBA panels and CodeMirror scrollbars. This is host-neutral UI CSS only; it does not change Resource authority, EventStore, bridge contracts, Office binding or Windows/COM/WebView2 gates. Version-format gate passes.

Previous slice (2026-09-07): final closure reconciles the reviewed R61 catalog (66 ids/66 variants), current RN.resources HTML diagnostics and delivered Plan/VBA resource assets. Replaced readers remain only in explicit incompatible-history rejection, not execution paths; no copied HTML data store or alternate model compiler remains in the reviewed contour. Final corrections are f78809f (normalized VBA guard versus immutable source bytes) and 01024e0 (published globals without incidental Office discovery). Fourteen focused harness cases and eight Plan browser-script cases pass; this is not a full-suite run. Removal/backlog records are current; Windows/COM/WebView2 and release gates stay open.

Historical reference (2026-09-06): Chat header CAS usage scan no longer treats hash-only `ContentSha256` evidence as a broken CAS reference; only hash+byte-length pairs are counted as CAS refs. The same header scan now also counts lowercase `sha256`/`byteLength` payload refs used by newer resource contracts. This fixes false critical chat-list badges such as "Некорректные или конфликтующие CAS-ссылки" for ordinary resource evidence while preserving missing-blob warnings for real CAS refs. Focused storage header test passes host-neutral. Prior WebView resource-download consumers now bind browser `fetch` before passing it into shared download paths; real Windows WebView2 retest and live complex Excel/VBA → HTML bind remain open. Previous Outlook mail reader consumer switch remains host-neutral complete; OOM pre-materialization and real Windows/COM/catalog gates remain open. This historical checkpoint is superseded by the completed host-neutral closure above.
Required context: [Resource MASTER](resource-cutover/MASTER.md), its three normative documents in the specified order, [resource owner map](../resource-fabric.md), and the exact owning source contour. Previous WQ evidence remains valid only for unchanged contours.
Open gates: the preceding 11T tool-dispatch cleanup and scoped Resource direct-cutover consumer/removal gates are complete host-neutral. Deferred source allocation and qualification are not closed by this implementation status. All current Excel, Word, PowerPoint, Outlook, public VBA/macro, custom VBA package and controller-owned tools use direct typed owners. `ThisAddIn` active-window/document lookup remains only VSTO pane lifecycle discovery, never execution target fallback, and is part of Windows UI/session qualification. Permanent narrow journal ports and current model-compatibility diagnostics share the canonical authority and are not legacy. R61 11O0–11O7 are complete host-neutral; final post-cutover live-provider/WQ-PACK and real WebView2 evidence remain. R62 is fixed host-neutral but requires the exact Windows WebView model/tool-error retest. R63/R68/R69/R70/R71/R72/R73/R74/R75/R76 are fixed host-neutral. Exact real Excel bind, edited-cell toolbar refresh, WebView rerender, Artifact Library chart preview, run resource-card count, downloaded ECharts export, target-model v5 final/checkpoint behavior, schema 27 final-quality behavior, real VBE duplicate-source rejection and real chat/artifact/tab/startup responsiveness remain open on Windows. R65/R67/R70/R71/R72/R73/R74 are contained in prompt/schema/skill/tool guidance; explicit saved-prompt review/reset and a real complex Excel/VBA → HTML bind run on the target model remain open. R66 passes local Chromium but still requires the exact Windows WebView2 dashboard preview/export retest. R51 remains open for audio, other committed-resource removal and Windows WebView image-gallery/thumbnail/PDF/lifecycle qualification; 11D2 image, 11D3 PDF and their thumbnail/sequence navigation remain complete host-neutral and are not removed. R64 has matching exact-package x86 PDFium/Skia wired, but exact Windows x86 PDF extraction/preview/scanned-page/model-send execution remains open. Production OfficeHosts/VSTO build, actual COM marshal/cleanup, real DocumentSession lifetime, WQ0, WQ-SESSION, WQ-EXCEL, WQ-WORD, WQ-POWERPOINT and WQ-OUTLOOK are open evidence. Full Phase 6 Windows/VBE, Phase 8 WQ-PACK, Phase 9/R45–R48 WebView/restart/multi-window and R28/R29/R32 live-provider/UI gates remain open. R52 Host Fabric, R53 Local Automation, R54 Skill Library, R56 Tool Library, R57 Issue Center and R58 typed-facade risk remain open. Product 16.1.0-dev, no release/tag.

Latest correction (2026-09-07): current-head verification found one active P1 B1 gap in generic semantic resource discovery. `common.resources_find` now keeps provider search matches that are outside the bounded listing page set, and semantic target resolution may use a bounded provider-search confirmation when listing is incomplete; incomplete or ambiguous scopes still fail closed. Focused resource fabric/search/continuation checks and the version-format gate pass host-neutral with existing CA1416 warnings. No P0 authority, mutation, compiler, catalog, CAS, bridge, Office binding or Windows gate is closed by this correction.

## Historical checkpoints — superseded by current status above

Open-work wording below records the state at each checkpoint, not the next task.

Inspector download checkpoint (2026-09-06): opt-in request JSON no longer crosses
the control bridge. Shared download reservation, strict UTF-8 preview, verified
chunks, one raw-text cache and cancellation/late-response lease cleanup replace
the inline path. Compiler/context-budget semantics are unchanged. Five Inspector
cases, typed bridge and architecture/project-includes checks plus focused browser
reader/viewer/lifecycle tests pass host-neutral. Real WebView2 qualification and
pre-truncation serialization allocation remain open. The subsequently inspected
runtime-log tail had no UI consumers and is now deleted with its unused bridge
commands. Binary/raw, search/collection and cleanup gates remain open. No Phase 12
or Windows gate is closed by these checkpoints.

Outlook resource checkpoint (2026-09-06): eight Outlook cases plus six
catalog/Gateway/continuation/architecture/project-include checks pass (14 focused
cases). Exact mail source/metadata, one-body-capture paging, historical no-I/O,
scope refusal, ambiguous targets, removed-call rejection, HTML and bound-STA reads
are covered host-neutral. Windows Inspector/folder/store, unsaved-mail identity and
OOM pre-materialization remain open.

Outlook capture checkpoint (2026-09-06): six Outlook cases plus catalog and
dependency-direction checks pass (8 focused cases). Complete/empty/oversized bodies,
body-free attachment metadata, malformed snapshots, cancellation and refusal before
reply/update dispatch are covered, including bound-STA/closed-window source access.
The obsolete implicit HTML fixture now expects the existing missing-target refusal;
it does not assert that Outlook HTML resource cutover is complete.

PowerPoint source checkpoint (2026-09-06): six PowerPoint cases plus six Word
regressions and six catalog/Gateway/continuation/architecture/project-include checks
pass (18 focused cases). Source pages retain the first exact capture; changed notes
supersede prior evidence, and historical pages do no Office I/O. Native/manual/HTML
routes and bound-STA/closed-presentation refusal are covered host-neutral. Real
COM shape/notes limits and final catalog/model/WebView2 qualification remain open.

Unified Resource Architecture work in progress (2026-09-05, explicit user authorization):
append-only shared authority/revision/view journals and mutation-attempt publication;
logical document identity independent of locator/runtime; pure evidence reducer and
one frozen model-context compiler; retained view payloads reuse the existing CAS.
The per-session VBA hash/refresh dictionaries and callbacks are removed. Accepted
conversation evidence supplies model observations; editor hashes are explicit guards.
Verified VBA read-back is captured before authority publication; affected views
without captured replacement become Unknown, not invented Known revisions. Manual
package lifecycle operations use the same attempt/commit barrier. Retained historical
reads do not move current heads. PromptBudgetComposer no longer assembles history;
protocol repair delegates to the owning frozen compiler. HTML bindings now contain
resource references only; `RN.resources` uses revision-pinned bounded leases and an
internal WebView data route. Record views use CAS indexes, published schemas and
exact mappings; virtual/materialized derived views preserve source dependencies.
Catalog activation uses committed CAS generations. Built-in/custom skill bodies
and reference Markdown are exact catalog resources; authoring file drift cannot
change an active publication. Request-boundary catalog capture freezes tools,
skills and editable prompt templates together. Manual settings save uses the same
mutation/commit owner; Inspector preserves the published skill-generation seed.
Compaction emits structured source-grounded claims after correctness filtering.
Large accepted-call arguments, activity payloads and persisted in-flight commands
use the existing CAS.
Text/Markdown/PDF text, images and PDF page/thumbnail viewers now use the same
gateway/data plane. Bridge DTOs contain metadata/leases, not text/base64 bodies.
Lease capacity is checked before capture; cancellation/owner-close invalidates
in-flight opens. Text leases close per bounded page; media leases close on cache
eviction, replacement and chat change, including late responses. Cache budgets fit
the shared lease limit. Bounded/coalesced in-process authority notifications reach
the UI and authorized RN.resources subscribers. Authority readers catch up journal
tails, and Known-head publication now requires durable exact revision metadata.
The canonical owner map and active removal gates are updated in resource-fabric.md
and MIGRATION_MAP.md.
Checkpoint commits: `ec8b179` (integrated foundation), `332b911` (lifecycle),
`904e1ae` (exact lineage across restore/context/fork), `b59e26d` (exact HTML
data-plane export), `75e2aef` (shared context/state reads), `d266895` (exact Office
continuations), `bde3348` (committed catalog reads) and `6a1617e` (exact structured
projections); user-requested checkpoints,
not a completed cutover. The lifecycle checkpoint switches controller clear/edit/message-delete/
fork to typed history intents through the same journal/authority observer. History
and fork preparation belong to ChatHistoryEditService/ChatCloneService; active
workspace, plan, task and membership heads publish atomically after conversation
persistence. Clear removes active conversation definitions without deleting retained
revisions/CAS. Edit preserves unchanged heads and records exact restore lineage.
Persistence/publication failure blocks fresh captures and recovers to Unknown,
never automatic replay. Duplicate controller saves in this contour are removed.
Fork preserves document authority and explicitly rebinds copied artifact references
in a new workspace snapshot; old snapshot bodies stay immutable. Missing exact
checkpoints no longer fall back to newer parent content.
Exact-lineage checkpoint: VBA backup preparations retain a proven exact preimage
reference; restore pins that origin (or the exact independent backup) before
confirmation and publishes Parent/RestoredFrom without selecting history by equal
hash. ContextNoteRole separates user instructions from supplied data and bound
Office observations. The compiler hydrates exact CAS payloads after correctness
filtering; mutable Text/Preview is never a model source. Upsert now replaces evidence
as well as display fields; replay/clone preserve typed roles and payload references.
Old untyped notes require reattachment. Context/continuation and Web fixtures are
updated to structured claims and typed package results, without compatibility paths.
Bounded conversation-definition fork copying now requires ResourceForkService
preparation through the same persistence/authority barrier. Exact copy provenance
replays from conversation events; schemas/mappings/derived definitions and supplied
context publish atomically with workspace state. Nested forks retain older pinned
mappings without borrowing newer parent heads. Typed definition refs are rebased;
immutable materialized CAS and old workspace bodies remain unchanged. Unavailable/
unpublished dependencies, cycles and graph bounds fail closed; failed publication
cannot partially activate a copied graph. The blanket unsupported-definition path
and optional fork-preparation bypass are removed.
Wave 4 export checkpoint `b59e26d`: standalone HTML export captures
typed exact leases through the same resource data plane. Head bindings must agree
with one frozen authority tuple; historical bindings stay pinned. Export pulls
bounded table/text/binary parts, never workspace JSON or control-message bodies.
The downloaded file uses the same RN.resources API over inert integrity-checked
snapshot parts, without a new head store/live fallback. Bounds are 32 bindings,
1024 parts, 32 MiB transport total and 8 MiB per part. Cancel/chat change, tampering,
missing snapshots and mixed revisions fail closed; all prepared leases close on
success/failure, including late responses. The redundant export checkpoint save
after mutation publication is removed. Data-plane head opens and final export
capture use the shared EvidenceStateReducer, including dependency scopes: stale
virtual/materialized heads are rejected without losing exact historical access.
Resource cutover 17/17 includes that preview/export dependency-currentness check.
Actual downloaded-file/WebView2 behavior
remains a Windows qualification gate.
Export checks: host-neutral exact capture/failure cleanup 2/2; typed export bridge
1/1 and production source inclusion 1/1. Web export 11/11, ECharts 7/7 and hosted
resource data-plane contract pass; related static UI fixtures: Plan 8/8, Artifact
Library 5/5, HTML import 5/5. No full harness or Office/VSTO validation.
Context-read checkpoint `75e2aef`: typed context refs now route through
the same Gateway/data plane in their conversation or exact document authority
scope. Instructions and untyped notes are excluded from resource discovery; bodies
come only from exact CAS. State/context share one retained text reader: partial
views cannot replace whole payloads, cursors bind logical revisions (including
equal-byte restores), and reads cannot activate prepared identities or resurrect
removed heads. Fork preparation remains unreadable until atomic publication;
replayed copied context reads in its child scope. Open context leases remain exact
after drift, while new unknown-head reads fail. Semantic mutable reads now capture
the current head once and pin internal pages, fixing reads stuck on an older
discovery observation. Legacy HTML-body/provider-list fixtures use current bindings
and the integrated provider registry.
Context-read checkpoint checks: resource cutover 19/19; resources 10/10; native semantic
resource tools (including multi-page context) 1/1; Agent large whole read 1/1;
native resource evidence replay 1/1; HTML export 2/2 and production source inclusion
1/1. No full harness, Office/VSTO or Windows validation. The required version-format
gate passed before the user-requested context-read checkpoint commit.
Office-cursor checkpoint `d266895`: Gateway Office text/source continuations
now bind exact logical revision plus URI/view. Missing exact refs and old hash-bound
or cross-revision tokens fail before Office dispatch, including equal-byte restores.
Physical view guards remain private to PrepareRead/provider dispatch; PublishRead
validates and rebinds outgoing cursors. The separate Office retained reader is
removed: exact views use the same snapshot reader as state/context, including the
last known head. Historical/unknown-head snapshots and open leases remain exact;
missing CAS never queries Office or publishes a head. Live Gateway access without
shared authority fails closed. Existing bounded-read fixtures now pass exact refs.
Continuation checks: resource cutover 20/20; resources 10/10; bounded VBA source,
native semantic resource tools, Agent large whole read and resource evidence replay
1/1 each; VBA restore 4/4; HTML export 2/2; production source inclusion 1/1 — 41/41.
No full harness, Office/VSTO or Windows validation. The required version-format gate
passed before the user-requested commit; its tree was verified clean before continuing.
Catalog-publication checkpoint `bde3348`: root/skill/reference text
cursors and capability reference next bind exact publication revisions, including
equal-byte restores. Child head reads use the common immutable/dependency reducer,
without independent child heads. Visibility requires a canonical committed
publication; prepared metadata and missing/corrupt CAS fail closed. Historical
reads/open leases stay exact and cannot activate a generation or heal Unknown.
Duplicate root hydration is removed. The Agent skill fixture now publishes through
the real owner and checks full CAS-backed replay plus exact publication evidence.
Checks: resource cutover 22/22; resources 10/10; skill-reference continuation,
native semantic resources and Agent full skill read 1/1 each; HTML export 2/2;
Inspector 3/3; production source inclusion 1/1 — 41/41. No full harness, Office/VSTO
or Windows validation. The required version-format gate passed for the
user-requested catalog checkpoint commit.
Structured-projection checkpoint `6a1617e`: record/table and virtual derived
continuations validate exact refs before source capture and index/definition CAS
hydration. URI/view/path/ordered-field bindings preserve JSON case; omitted/empty
fields mean the same all-fields projection. First artifact reads rebind to the
resolved exact URI, but a cursor cannot trigger implicit current-identity resolution.
Virtual tables reject non-root paths. Collection-style validation and lowercased
projection bindings are removed. Equal-byte restores remain distinct; historical
continuations and leases stay pinned. The export fixture now checks exact B2 rather
than searching random resource IDs for a numeric substring.
Checks: resource cutover 23/23, resources 10/10, native semantic resources 1/1,
HTML export 2/2 and production source inclusion 1/1 — 37/37. No full harness,
Office/VSTO or Windows validation. The required version-format gate passed before
the user-requested checkpoint commit.
Definition-publication checkpoint `831e5d0`: retained text, structural indexes,
virtual definitions and catalog roots share exact publication proof in the existing
authority owner. Prepared revisions cannot borrow an older head or cached index.
Fork preparation reuses the same proof/order; existing event-backed copy provenance
is admitted only after its verified fork commit, preserving historical non-head
copies and nested forks. Independent catalog/fork publication checks are removed.
The retained payload reader normalizes missing/corrupt CAS errors for definitions,
indexes, parts and catalog references; domain bounds remain with their owners.
Historical reads and payload failures never publish or heal a head.
Checks: resource cutover 24/24, resources 10/10, native semantic resources 1/1,
HTML export 2/2, skill-reference continuation 1/1, Agent full skill 1/1, Inspector 3/3,
chat editing 5/5 and production source inclusion 1/1 — 48/48. No full harness,
Office/VSTO or Windows validation. The required version-format gate passed before
the user-requested checkpoint commit.
Attachment-upload checkpoint `158cc26`: the base64 staging bridge/decoder is removed.
Typed begin/complete/cancel and binary POST chunks use the existing data plane;
256 KiB chunks, four uploads/50 MiB reservations, shared lease capacity and exact
chat ownership bound the work. Cancellation/expiry/close cannot release a busy
reservation early or promote partial data. Completion is single-use; extraction
runs off the UI thread, then the existing draft owner handles CAS/linking at send.
UI pre-send ordering and addressed-chat isolation remain. No upload store or
legacy fallback was added. Windows POST/CORS/request-stream/PDF-close gates remain.
Checks: attachments 22/22; shared table/binary leases 2/2, typed ingestion bridge 1/1,
WebView policy/cancellation 1/1, chat editing 5/5, HTML import 1/1 and source inclusion
1/1 — 33/33 host-neutral. UI upload 6/6 and ingestion ordering 4/4 also pass.
Version-format and diff checks pass; no full harness or Office/VSTO validation.
Trajectory-export checkpoint `a6be9d3`: metadata-only bridge setup replaces the ZIP
base64 body/decoder. The existing exporter captures one validated event snapshot;
the shared data plane reserves capacity before source validation/production and
serves exact sequential 256 KiB chunks. Two download slots share the 50 MiB transfer
budget and capture/lease limits with uploads. Cancelled busy transfers keep their
reservation until exit. JSON/ZIP writes and CAS hydration enforce output budgets;
the UI verifies complete SHA-256 and closes success/failure/late leases. Redaction
is unchanged; no ZIP store, published head or legacy fallback was introduced.
Windows/WebView2 delivery/cancel gates and bounded cold replay remain open.
Checks: trajectory export 3/3; typed bridge 1/1; attachment upload 4/4; shared
table/binary leases 2/2; WebView policy 1/1 and source inclusion 1/1 — 12/12
host-neutral. UI download 6/6 and trajectory viewer/export 8/8 also pass.
Version-format, JavaScript syntax and diff checks pass; no full harness or
Office/VSTO validation.
Uploaded-HTML source checkpoint `91c96b2`: original-source preview now uses the shared
ArtifactViewerService/Gateway/data plane and bounded exact text-page cache. Its
separate text-body bridge/DTO, preview cache and UI wrappers are removed. HTML stays
inert until explicit workspace import. Import reads exact Gateway pages rather than
an attachment callback; retained text/byte bounds are checked before hydration,
and contiguous complete content plus extracted-text SHA-256 before workspace
mutation. Original binary identity, provenance and active-workspace/path guards
remain. Text reads/import require an explicit addressed chat, with no active-chat
fallback. No new store, resource identity or compatibility path was introduced.
Checks: HTML source/import 1/1, artifact viewer 3/3, typed import/viewer bridges 2/2,
empty exact text 1/1 and source inclusion 1/1 — 8/8 host-neutral. Web source/import
5/5, artifact JSON 8/8, text/media viewer 10/10, Library projection 5/5, Plan 8/8 and
HTML export 11/11 — 47/47. Version-format, JavaScript syntax and diff checks pass;
no full harness or Office/VSTO validation. Windows source/import gates remain open.
Trajectory-payload checkpoint `fa0f0fa`: raw event previews and Run Journal bodies
now share metadata-only setup and the existing bounded download data plane/reader.
The separate inline bridge text and whole-payload-then-clip read are removed.
TrajectoryPayloadService resolves the exact addressed event in a complete validated
stream; shared capacity is reserved before journal/CAS capture. The existing CAS
codec authenticates/decompresses/hashes the whole source while retaining a bounded
prefix. Diagnostic limits are 32 MiB/source and 512 Ki UTF-16 code units/preview,
without splitting a surrogate pair. Source/preview hashes are separate; exact
empty UTF-8 remains valid. UI verifies SHA-256 and strict UTF-8 before rendering,
caps concurrent captures and closes leases on success/failure/cancel/late metadata.
Selection/context change, row collapse/unmount and page close cannot render a stale
body. No new resource/head/store or compatibility transport is introduced.
Checks: payload 2/2, verified CAS prefix 1/1, adjacent CAS codecs 3/3, trajectory
export 3/3, typed bridge 1/1, WebView policy 1/1 and source inclusion 1/1 — 12/12
host-neutral. Web payload 7/7, trajectory viewer/export 8/8, Run Journal 10/10 and
shared download 6/6 — 31/31. Version-format, JavaScript syntax and diff checks pass;
no full harness or Office/VSTO validation. Whole journal replay and actual Windows
controller/WebView2 capture/read/cancel qualification remain open.
VBA editor resource checkpoint `1f542c3` (2026-09-06): module source now uses the same Gateway,
bound provider, authority and shared bounded download data plane. The direct
ReadVbaModuleForEditor path, controller DataJson parsing and inline code bridge
body are removed. Complete bounded VBA source is retained in the existing CAS
before publishing its logical revision; exact model/editor/HTML continuations
can read that snapshot after drift without re-entering Office. Delivered partial
pages remain partial evidence, and editor reads do not grant model observations.
The editor enables only complete SHA-256/UTF-8-verified source, retaining the
existing 1,000,000-character limit and normalized write guard separately from raw
payload hash. An old snapshot cannot overwrite changed live VBA; the existing
mutation owner still verifies save. Cancel, late response, chat/project/selection
change and bridge/page close release the shared lease without applying stale code.
Checks: editor source/guard/bounds 2/2; live Office/VBA and exact continuation 2/2;
typed reader, model-visible refresh, editor-vs-mutation gate and stale guard 4/4;
typed VBA bridge, WebView policy and source inclusion 3/3 — 11/11 host-neutral.
Web editor 4/4, context/metadata viewer 7/7, completion 1/1, Library 5/5,
commit projection 4/4, chat sync 4/4 and lazy UI 4/4 — 29/29. Cache fixtures were
aligned with the shipped asset revisions. No full harness or Office/VSTO validation;
actual Windows source/read/save/cancel qualification remains open.
Version-format, JavaScript syntax and diff checks pass.
VBA editor upload checkpoint `8cecd0d` (2026-09-06): save/create source now travels through the
existing shared upload router and one browser chunk writer also used by attachments.
Inline code DTOs and the attachment-local chunk loop are removed. Exact chat/consumer
ownership, complete UTF-8/raw SHA-256 checks and one-time consumption precede the
existing manual mutation owner; save requires its explicit normalized editor guard.
Empty source, the existing 1M-character bound, shared quotas, cancellation and late
response cleanup are covered. No attachment, new durable store or authority head is
created by uploading alone. Current edits survive late mutation/refresh responses;
uncertain writes are not automatically repeated. Real Windows source upload,
save/create, STA dispatch/read-back and WebView2 cancellation remain open.
Checks: VBA editor 4/4, attachment upload 4/4, typed VBA/ingestion bridge 2/2,
WebView policy and production inclusion 2/2 — 12/12 host-neutral. Web VBA upload
6/6, shared upload 6/6, source read 4/4, attachment send barrier 4/4, chat sync 4/4,
Library 5/5 and commit projection 4/4 — 33/33. JavaScript syntax, version-format
and diff checks pass; no full harness or Office/VSTO validation.
Skill reference editor checkpoint `1fdd843` (2026-09-06): Library reference Markdown now reads the
published catalog through the same Gateway/CAS and shared bounded download as
other consumers. Direct authoring-file reads and inline read/fake mutation-result
DTOs are removed. Explicit chat/package binding, pre-hydration reservation,
complete UTF-8/hash/extent checks and cancellation cover late responses. Source-file
metadata (including BOM) stays separate from transport hash and logical revision.
Failed reads remain read-only; refresh drops changed clean caches but retains dirty
drafts with a save-blocking conflict. Existing authoring guard/read-back rejects
external file drift; reading neither publishes nor creates model evidence.
Checks: reference editor 2/2, existing UI revision guard, catalog publication,
typed bridge, WebView policy and production inclusion 5/5 — 7/7 host-neutral.
Web reference reader 6/6, skill contract 4/4, chat sync 4/4, Library 5/5,
commit projection 4/4 and VBA reader 4/4 — 27/27. No full harness or Office/VSTO
validation. Real Windows Library read/save/cancel qualification stays open.
JavaScript syntax, version-format and diff checks pass.
Skill core/editor checkpoint `d413d0f` (2026-09-06): core/reference bodies now share
SkillEditorResourceService and readSkillSource; the reference-only owner/action
is removed. SkillPackageDto contains raw text hash/extent metadata, not core text,
in init/catalog/mutation projections. Opening a source pulls its complete bounded
published snapshot; built-ins stay read-only. Clean UI text is selection-bounded;
late/failed reads cannot unlock empty drafts. Metadata-only edits explicitly preserve
the unread body under the existing package guard/commit barrier. Core replacement
requires a loaded/user-created draft; Save acknowledgement cannot erase later text
edits. Clone/context-copy cannot treat an unloaded core as empty.
Checks: skill editor 4/4, existing UI guard, catalog publication, typed bridge,
WebView policy and source inclusion 5/5 — 9/9 host-neutral. Web skill reader 10/10,
skill contract 4/4, prompt review 5/5, chat sync 4/4, Library 5/5, commit projection
4/4 and VBA reader 4/4 — 36/36. No full harness or Office/VSTO validation;
Windows Library/WebView2 read/save/cancel gates remain open.
JavaScript syntax, version-format and diff checks pass.
Skill mutation upload checkpoint `339b5ef` (2026-09-06): the same editor resource owner now consumes
core batches/reference upserts through the shared bounded upload route. The bridge
carries chat/lease/hash only; one typed UTF-8 JSON upload retains batch semantics
without allocating a lease per source. Whole-batch structural/body validation
precedes the existing sequential revision-guarded authoring/read-back/commit owner;
a non-OK member stops the batch, not an atomic multi-package transaction. Uploads
are one-use transient input, not catalog/CAS publication or model observation.
Inline mutation bodies and reference text echo are removed. One UI writer freezes
the submitted plan, retains later edits and closes cancelled/late leases without
automatic replay; successful sources reload exact published snapshots.
Checks: skill editor 6/6 plus existing UI guard, catalog publication, typed bridge,
WebView policy, dependency direction and source inclusion 6/6 — 12/12 host-neutral.
Web skill editor 13/13, skill contract 4/4, shared upload 6/6, chat sync 4/4,
Library 5/5, commit projection 4/4 and VBA reader 4/4 — 40/40.
JavaScript syntax, version-format and diff checks pass. No full harness or
Office/VSTO validation; real Windows Library/WebView2 save/cancel gates remain open.
Tool mutation upload slice (2026-09-06): Save and save-before-VBA-install now share
ToolEditorResourceService and the existing single-use upload route. A bounded typed
UTF-8 batch carries source/schema/README/components; controls carry chat/lease/hash
only. Integrity and batch shape/guards are checked before the existing sequential
authoring/read-back/catalog commit path. Upload does not publish or create model
evidence. Both inline mutation request consumers and the controller batch validator
are removed. One UI writer retains later drafts, acknowledges verified prefixes,
closes cancelled/late leases and never retries an unconfirmed write or continues it
into install. Existing document catalog reads retain their access gate.
Checks: tool editor 2/2, existing library guards, typed bridge, WebView policy,
dependency direction, production source inclusion and document catalog access
6/6 — 8/8 host-neutral. Web package actions 5/5, tool contract 5/5, editor 1/1,
Library UX 4/4, context JSON 7/7, skill editor 13/13, shared upload 6/6, chat sync
4/4, Artifact Library 5/5, commit projection 4/4 and VBA reader 4/4 — 58/58.
JavaScript syntax, version-format and diff checks pass. No full harness or
Office/VSTO validation. A literal leading-U+FEFF README sidecar issue was recorded
in BACKLOG; its read-back was unknown at that checkpoint.
Tool source read slice (2026-09-06): catalog and outgoing Init/chat/mutation/package
DTOs now carry hash/extent-only source metadata. ToolEditorResourceService reserves
shared capacity, checks displayed Library revision and reads exact custom/builtin
catalog children or document-local VBA component refs. Builtin registration uses
the existing catalog publication owner; document-local JSON is disposable transfer,
not another authority. Complete typed UTF-8 source downloads are bounded to 16 MiB;
no model observations, disk fallback or implicit mutation is created. The UI's
whole-catalog body serializer and component reconstruction fallback are removed.
Unloaded/failed source stays read-only, hydration is dirty-neutral, clean cache is
selection-bounded, and conflicting/late drafts survive without silent rebasing.
Checks: tool editor 4/4 plus typed Library guards/bridge, builtin docs, document
access, catalog merge/continuations/missing snapshots and dependency direction
8/8 — 12/12 host-neutral. The catalog merge fixture now passes its ToolStore to
the existing publication owner instead of assuming an implicit disk catalog.
Web source reader 6/6, package actions 5/5, tool contract 5/5, editor 1/1, Library
UX 4/4, context JSON 7/7, skill reader 13/13, chat sync 4/4, Artifact Library 5/5,
commit projection 4/4 and VBA reader 4/4 — 58/58 in 11 focused files.
JavaScript syntax, version-format and diff checks pass. No full harness or Office/
VSTO validation. A pre-existing LF/CRLF canonical-hash versus raw immutable VBA
view collision was reproduced and recorded in BACKLOG; it fails closed and is
not repaired by this transport slice. Real Office/WebView2 gates remain open.
Built-in human documentation slice (2026-09-06): source-owned runtime Policy generates
Markdown at builtin registration; the existing catalog publication retains its CAS
parts. Exact `/documentation` children share the catalog root revision and dependency,
without exposing human docs in the model-facing catalog/ToolPack. The typed bridge
returns explicit chat/tool/Library revision, ResourceRef and shared download metadata,
never inline Markdown. The existing ToolEditorResourceService reserves shared capacity
before lookup and reads at most 2 MiB without live document discovery. Runtime generation
is comparison-only; missing, changed or corrupt publication fails closed, with no fallback.
The UI keeps one selected-tool/chat/revision cache with its exact ResourceRef, cancels
obsolete requests and closes late leases. Global documentation dictionaries are removed.
Checks: tool editor 6/6 plus typed bridge, builtin docs, catalog continuations/missing
snapshots/immutable skill publication, dependency direction and WebView policy 7/7 —
13/13 host-neutral. Web documentation 5/5 plus the preceding 58/58 groups pass in
12 focused files. JavaScript syntax, version-format and diff checks pass. No full
harness or Office/VSTO validation; real Office/WebView2 gates remain open.
Model tool-source slice (2026-09-06): common.resources_find discovers host-visible
custom/builtin `tool source` targets without source bodies or generated human docs.
common.resources_read uses the existing catalog `/source` child, complete bounded
internal pages and publication-dependent CAS evidence. The removed
common.tools_definition_read has no catalog entry, native handler/binding, file
reader or alias; stored calls fail explicit model-history validation. Tool authoring
guidance now uses the resource path. Upsert/delete guards, runtime schema admission
and Library source contracts are unchanged. Published snapshots survive authoring
file drift and remain historical after replacement without another store/authority.
Checks: new source/evidence flow, existing Agent CRUD/semantic authoring, native
resource manual/model paths, catalog continuations/missing snapshots, Library source,
builtin documentation and dependency direction — 9/9 focused host-neutral passes.
The additional R61 full inventory check fails against pre-existing HTML/resources/
VBA baseline drift; only the removed reader row was deleted. The remaining reviewed
baseline refresh is an explicit BACKLOG/final qualification gate, not a passed test.
Version-format and diff checks pass. No web changes, full harness or Office/VSTO
validation; Windows gates remain open.
Model prompt-read slice (2026-09-06): the existing catalog exposes whitelisted
current/default fields as metadata-only semantic targets. Startup publishes
source-owned defaults through the same authority/CAS owner; individual complete
text reads are bounded to 100,000 characters and carry exact root dependencies.
Saving current templates supersedes prior evidence without replacing defaults;
historical reads preserve exact Unicode/empty text. Missing/null/oversized or corrupt
snapshots fail without loading mutable settings, regenerating defaults or healing
authority. common.prompts_read, its include-defaults bundle, native binding and
direct settings reader are deleted, and old accepted calls fail history validation.
Prompt guidance uses the resource pair; existing confirmed save/read-back and frozen
compiler activation are unchanged. No second store, alias or automatic reset.
Checks: new prompt source/failure cases 2/2, existing save/frozen publication,
semantic guidance/model metadata, catalog continuations/missing snapshots, tool
source and dependency direction 8/8 — 10/10 host-neutral. Version-format and diff
checks pass. The known R61 inventory baseline gap remains open; only its retired
prompt-reader row was removed. No web changes, full harness or Office/VSTO validation.
Prompt editor transport slice (2026-09-06): typed body-free settings controls and
exact eight-field metadata replace initialization/settings body bundles. Selected
source uses the existing catalog/Gateway/CAS and shared download owner; bounded
single-use uploads carry only changed fields before the existing settings writer.
Expected publication/live-template guards run under the catalog mutation lease
before dispatch. Stale drafts, omitted bodies and in-flight edits are preserved;
explicit reset/review semantics remain. Read/save cancellation, late-lease cleanup,
one clean cache and confirmed reload have no inline/default fallback or replay.
Checks: prompt editor 3/3, typed settings bridge 1/1, existing model prompt save,
frozen publication and resource reads 4/4, dependency/source inclusion 2/2 — 10/10
focused host-neutral passes. Web: 11 selected files / 64 groups pass, including
real shared byte transport and changed delivery keys. Version-format/diff checks
pass. No full harness or Office/VSTO validation. Real Windows settings/DPAPI,
WebView2, multi-window and existing R61/live-provider gates remain open.
HTML workspace mutation-upload slice (2026-09-06): all four HTML/CSS/JS and JSON
Save/create controls use the shared bounded byte transport with a distinct owner,
complete hash/UTF-8 verification and explicit empty-workspace guards. The existing
domain validators are shared before dispatch, and the existing local mutation owner
persists the logical expected revision/editor guard in its CAS intent and publishes
only after read-back. No new writer, store, inline fallback or replay. Browser
single-flight/cancel/late-close handling prevents stale dispatch and preserves newer
typing on Save acknowledgement. Outgoing workspace source/preview hydration stays
explicitly open for the next coherent read-transport slice.
Checks: HTML editor 3/3, existing HTML/native/restore 3/3 and dependency/source
inclusion 2/2 — 8 focused host-neutral passes. Web: 13 selected files / 90 groups
pass; the Plan file stops at its pre-existing unrelated VBA cache assertion after
four passing groups, recorded in BACKLOG. No full harness or Office/VSTO validation;
version-format, diff and affected documentation-link checks pass. Real
Windows/WebView2 save/cancel/multi-window qualification remains open.

HTML workspace source-read slice (2026-09-06): `HtmlWorkspaceFileDto` replaces
domain-file body cloning at all four controller projection sites. The same HTML
member provider retains complete immutable CAS views for exact source downloads;
background reads capture only their exact parent artifact, not the running session.
The browser loads selected source or the complete preview/export set, verifies
bytes/UTF-8/length, closes each lease and serializes/coalesces read demand. No
source fallback, extra store, mixed-revision assembly or empty editable substitute.
Dirty drafts survive newer metadata without rebasing; explicit source reload
confirms discard and protects edits made during its wait. Upload/writer ownership
is unchanged. Checks: 4 HTML editor checks, 2 existing exports, compact history,
typed export bridge and dependency/source inclusion — 10 focused host-neutral
passes. Web: 15 selected files / 97 groups pass. The separate source-tool check
passes read/search/patch then fails its stale `RNAssistantData` diagnostic fixture;
this unchanged-owner gap is recorded in BACKLOG, not marked green. No full harness
or Office/VSTO validation; Windows/WebView2/editor/preview/export/multi-window gates
remain open. Version-format, diff, JavaScript syntax and affected doc-link checks pass.

Excel range resource-read slice (2026-09-06): removed public `excel.read_range`,
its native range binding, Excel core-pack entry and direct domain-output wrapper.
Values/formulas use existing exact resource views; `structure` reuses the bounded
domain profile. Model/manual reads and HTML share Gateway → provider → bound
backend, canonical authority/evidence and CAS. Explicit ranges replace omitted
active-sheet/selection defaults; historical accepted old calls are rejected rather
than translated. Updated Excel skill guidance and affected catalog/prompt fixtures.
Checks: Excel reads 5/5 (including multi-page single capture, profile drift and
historical no-I/O), catalog 1/1, prompt 1/1, Inspector 3/3, table lease 1/1, logical
continuations 1/1, serialized manual read 1/1, dependency/source inclusion 2/2 —
15 focused passes. The separate closed-document agent check fails its unchanged
HTML-binding catalog expectation before the updated read assertion; recorded in
BACKLOG, not marked green. No full harness, Web tests or Office/VSTO validation.
Real Excel/STA, target-model/catalog and accumulated Windows gates remain open.

Word source resource-read slice (2026-09-06): document/selection text and explicit
main-story character ranges share the existing document provider, typed Word capture
and bound backend. Complete bounded captures are retained through existing CAS;
internal pages and historical exact reads perform no further Word I/O. Removed
`word.read_text`, its catalog/native route, direct output wrapper and Word resource
adapter text fallback. COM range limits now precede `Range.Text`, with explicit
failure instead of clipping, implicit end or coordinate clamping. Word mutations,
search and inspection keep their existing domain owners. The new provider partial
is included in the production project. Checks: Word tools/resources 6/6, catalog
1/1, live Office/VBA and general Gateway 2/2, logical continuations 1/1, dependency
and source inclusion 2/2 — 12 focused host-neutral passes. Diff/version-format
checks pass. No full harness, Web tests, production Office/VSTO build or live COM
validation; Windows Word text/selection/STA and final catalog/model gates remain open.

Next host-neutral contour: remaining PowerPoint/Outlook domain reads
and other bulk upload/export through the existing Gateway/data plane, then finer Excel
coverage/named resources and binary/raw view negotiation in MASTER order.
Still open: remaining definition/domain read and other bulk upload/export consumers,
finer Excel coverage/named resources, complete binary/raw view negotiation, bounded
cold replay/checkpoint/retention optimization,
cross-process push qualification, and final cross-document cleanup.
This is NOT a completed cutover; no Windows gate is closed by these changes.
Exact-lineage checkpoint checks: VBA restore 4/4, mutation 11/11, journal 4/4;
typed context exact-payload/event replay 1/1, context/continuation 12/12, Inspector
3/3, typed context bridge 1/1 and Web context/viewer 7/7. Resource cutover 17/17
includes exact graph copy, nested forks, immutable snapshot restore, event replay
and failed copy publication. Latest chat editing 5/5, bound context capture 1/1 and
production source inclusion 1/1 also pass. `git diff --check` is clean. This checkpoint
records the continuation after `332b911`. No full harness or Office validation.
Earlier focused checks: VBA 98/98 (before latest manual-package/drift integration),
resources 10/10 (earlier), resource cutover 16/16 including publication, lease
cancellation/capacity, history/fork/clear and failure barriers; chat editing 5/5
and production source inclusion 1/1. Kernel replay 10/10, Inspector 3/3, artifact viewer 3/3,
skill-reference publication/continuation 1/1, bounded bridge notifications 1/1,
typed artifact-viewer bridge 1/1; compaction 4/4 (earlier) and package journal 1/1
(earlier). Web checks: artifact viewer 10/10, media gallery 2/2, resource data plane,
HTML ECharts 7/7, HTML export contract 7/7 and chat sync 4/4. The export contract
check verifies explicit missing-host failure, NOT functional offline bound export.
These are focused host-neutral checks, not end-to-end qualification. Foundation and
the lifecycle slice are checkpointed by user-requested commits. No Office/VSTO/
Windows validation, release or tag. Implementation continues in the same cutover workstream.

R77 VBA write-integrity correction (2026-09-04, user-reported host-neutral):
complete model-visible source observation is separated from runtime guard/read-back.
After a verified source mutation the same module requires a complete
`common.resources_read` before another patch or whole-source write; partial chunks
cannot clear the gate, while multiple same-snapshot hunks remain one atomic patch.
Live validation now rejects export headers, unclosed/C-escaped strings, common
non-VBA operators/braces and unbalanced `#If`, in addition to R73 duplicate and
hidden/control checks. Host replacement returns structured stage/root HRESULT and
verified rollback disposition; unverified rollback is unknown. Completed responses
with write error/unknown receive a durable runtime warning, and failed
`VerifiedNoChange` is no longer misprojected as an extra unknown. Qualification pack
revision 2 correctly targets Excel/Word/PowerPoint, not unsupported Outlook.
Macro dispatch in those hosts replaces any incoming document qualifier with the
exact bound document name before `Application.Run`.
Focused host-neutral checks: Harness `vba:` 98/98, `run view` 4/4,
`agent: characterization completed` 3/3 and qualification built-in suite catalog
1/1. Real Excel/Word/PowerPoint VBE replacement/failure/read-back and WebView2
rendering remain open Windows evidence; Outlook VBA remains unsupported by design.

R76 cross-surface WebView startup/render regression (2026-09-03,
user-reported host-neutral): the main document synchronously parsed the 1 MiB
ECharts bundle, created all eight CodeMirror instances for hidden panels and
rendered transcript, Artifact/HTML and Library surfaces regardless of visibility.
Ordinary initialization also started remote model-catalog discovery and probed
qualification state for every populated chat. Artifact selection rebuilt the whole
tree and refreshed a hidden editor; opening the Artifact tab rendered twice and
could refresh bound HTML data while a Plan/media/other artifact was selected. The
correction loads ECharts on first actual chart/HTML-preview use, creates editors per
owning tab, skips hidden surface DOM work, loads the model catalog on first picker
open, limits qualification restore probing to empty chats, updates only Artifact
detail on tree selection and limits `on_preview` refresh to selected HTML file/data
surfaces. Focused Web tests cover lazy startup, editor ownership, hidden transcript
render suppression, exact ECharts sandbox/export assembly and existing chat,
Artifact Library, media, HTML and run-view projections. Real Windows WebView2
startup, selection, tab switching, chart first-use and long-history behavior remain
open qualification evidence.

R75 chat WebView catalog/focus performance regression (2026-09-03,
user-reported host-neutral): background `listChats` no longer serializes full
active transcript, prompt context, artifact library or HTML workspace. It returns
only chat/document catalog projection; explicit `getChatState` owns full
transcript reload for selected-chat recovery and when the active chat summary
revision advances. Web focus-state reporting now coalesces selection/focus/key
bursts and checks `Selection.rangeCount/isCollapsed` instead of materializing
selected chat text. Focused web regressions cover catalog-only sync, one explicit
detail reload for a newer active revision, and selection burst coalescing without
reading selected text. Focused Harness bridge coverage verifies `listChats` omits
heavy fields while `getChatState` remains the explicit full-state path. Real
Windows WebView2 selection/tab responsiveness remains open with the existing WQ
UI gates.

R74 neutral HTML skill and final quality gate correction (2026-09-03,
user-reported host-neutral): previous HTML authoring guidance
accidentally generalized one example into a preferred dashboard layout shape. The
skill is rewritten as neutral HTML page/interface guidance: understand the
requested information architecture, split substantial work into semantic HTML, CSS
and focused JS, use bundled ECharts only when a chart is actually needed, and design
layout from the requested content rather than a fixed template. Prompt schema 27
adds a final read-back/regression gate: before a successful `final=true`, the agent
must inspect the user-visible result, check for obvious bugs, dropped requirements,
broken interactions, stale data, layout breakage and regressions in changed
behavior, then fix in-scope defects and verify again instead of handing off weak
work. Focused host-neutral checks passed: Harness `settings: built-in` 2/2,
`tools: R61 built-in contract inventory` 1/1; `git diff --check` and
`ValidateVersionFormat` passed. Windows/live-provider target-model behavior remains
open.

R73 VBA duplicate-procedure write/patch hardening (2026-09-03, user-reported
host-neutral): VBA source validation is now a shared Core helper used by host
backend writes/packages and by `VbaMutationService` before whole-module write or
post-patch journal/dispatch. The model-facing VBA skill and tool descriptions now
require a final-source self-check for duplicate `Sub`, `Function`, or same
`Property Get/Let/Set` declarations; obsolete earlier copies may be removed only
when the intended final implementation is unambiguous, otherwise runtime fails
closed with `vba_code_invalid`. Regression coverage verifies both public write and
patch paths reject duplicate-producing source without backup/journal/backend write,
while matching Get/Let property accessors remain valid. Focused host-neutral checks
passed: Harness `vba:` 97/97, `tools: R61 built-in contract inventory` 1/1,
`git diff --check` and `ValidateVersionFormat`. Windows Office/VBE validation
remains open with the existing WQ gates.

R72 explicit final response intent (2026-09-03, user-reported host-neutral):
empty `tool_calls` alone could end the model loop even when the model message was a
non-final checkpoint such as “Составляю итоговый отчет”. The correction switches
the active wire to conversation-response v5 with required boolean `final`.
`final=true` with empty `tool_calls` is the only terminal model intent;
`final=false` with tool calls remains a tool turn; `final=false` with empty calls is
accepted into history as an in-progress no-tool checkpoint and the kernel asks for
the next model response. Three consecutive no-tool checkpoints fail closed with
`model_loop_stalled`. `final` is not success, refusal, verification or effect
evidence; runtime lifecycle/health still come from kernel state and tool records.
Prompt schema 26, parser/schema/writer/history/compatibility probes and canonical
docs now describe v5. Focused host-neutral checks passed: Harness `conversation v5`
13/13, `kernel` 46/46, `model protocol` 15/15, `protocol preflight` 2/2,
`model compatibility` 2/2, `settings` 6/6, `conversation` 4/4, `chat sessions`
14/14, `kernel replay` 10/10, `agent` 43/43, `tool pack` 7/7, `resources` 10/10,
`tool result wire` 8/8, `vba: exact patch preserves boundary newlines` 1/1,
`chat: unchanged edit replays turn` 1/1; `git diff --check` and
`ValidateVersionFormat` passed. Windows Office/WebView2 and live-provider
qualification remain open.

R71 Task List finish and HTML bound-data render correction (2026-09-03,
user-reported host-neutral): refresh JSON can update correctly while a
generated dashboard remains visually stale or blank when page code guesses a stale
data-source name or uses first-row labels such as `Продажи` while
`transform=table` exposes canonical keys such as `продажи`. The correction keeps
canonical `columns[].key` as the public contract, adds first-row label aliases to
bound table rows, adds a single-data-source `RNAssistant.data.first()`/
`defaultName()` helper plus a warning fallback for stale names, and upgrades static
preflight so missing data-source references are errors when workspace data exists.
Prompt schema 25 tightens successful completion: an open active Task List is
unfinished work unless the final message honestly reports why it could not be
closed. HTML skill/tool guidance now requires refreshed JSON read-back plus visible
bound-value render evidence before claiming live HTML refresh works.
Focused host-neutral checks passed: Harness `html` 23/23, `settings: built-in` 2/2,
`tools: R61 built-in contract inventory` 1/1; web HTML ECharts 7/7, export 7/7,
plan document 8/8, artifact library projection 5/5, chat resource cards 2/2;
`node --check` for touched HTML workspace/resource-card scripts, `git diff --check`
and `ValidateVersionFormat` passed. Windows WebView2 live refresh and exported file
qualification remain open.

R70 chart/resource duplicate-surface correction (2026-09-03,
user-reported host-neutral): one run could legitimately produce a chart artifact and
an HTML workspace revision for the same requested visual; the final resource bundle
then displayed `Ресурсы · 2` (`Диаграмма · HTML workspace`), although the chat showed
only one chart. Run resource cards now suppress the supporting HTML workspace when a
chart artifact is present, while HTML-only runs still expose their workspace. HTML
authoring guidance now requires choosing one visual surface and forbids calling
`excel.create_chat_chart` for the same data when an ECharts/HTML dashboard is the
deliverable. Existing persisted extra artifacts are not deleted automatically.
Focused web and Harness checks cover the resource-card count, cache busting, chart
preview route and skill/catalog references. Real Windows WebView2 and target-model
behavior remain open gates.

R69 chart artifact preview routing (2026-09-03, user-reported host-neutral):
Artifact Library routed immutable `rnassistant.chart` snapshots through the generic
JSON preview because only inline tool activity cards called the chart renderer. The
renderer now also accepts exact chart JSON, and Artifact Detail invokes it for chart
artifacts before the JSON fallback. Preview therefore renders the ECharts chart;
Details still shows the exact JSON payload. This does not collapse separate
immutable snapshots with identical titles: those remain distinct generated resources
unless a separate cleanup/retention decision removes them. Focused web regressions
cover the Artifact Detail route, cache busting, existing chart activity fallback and
HTML/ECharts assembly. Real Windows WebView2 chart preview remains an open gate.

R68 HTML bound-refresh and standalone-export correction (2026-09-03,
user-reported host-neutral): refresh successfully reread current Office cells but did
not capture the mutated workspace as a new active artifact. The immediately
following UI reload therefore hydrated the previous artifact and discarded the new
JSON, making `Данные ↻` appear ineffective. A changed refresh now captures one
replacement authoritative head after the batch without adding an Undo step and
returns its exact resource evidence; verified no-change creates no artifact. A
regression changes an Excel cell, then checks current JSON, saved artifact snapshot,
stable Undo history and the no-change path.
The HTML skill and bind schema now define the exact `rnassistant.table.v1` envelope
and require source read → bind/shape inspection → dashboard construction, startup
render and revision read-back. Standalone assembly is explicitly a current-data
snapshot with no Office bridge; its regression executes bound table data after the
local ECharts 5.6.0 runtime and fails closed if that dependency is unavailable.
Focused Harness and web checks cover runtime ownership, guidance/catalog drift,
export, ECharts and import cache wiring. Real Excel/WebView2 refresh plus interaction
with the downloaded file remains a Windows gate.

R67 prompt/skill/tool consistency hardening (2026-09-03, user-reported
host-neutral): the prompt layers and all 12 unique built-in skills were audited
against the current strict catalogs. Prompt schema 24 now gives Agent one explicit
Understand → Prepare → Inspect → Execute → Verify → Finish workflow, requires the
Task List before domain work for genuinely complex requests, puts reusable
authoring after the verified primary result and rejects terminal scaffolding or
unsupported success prose. Authority is explicit: system prompt owns universal
lifecycle, skills own domain workflow/quality, and current tool descriptions and
schemas own exact root arguments and evidence. Task, Skill, Prompt and HTML skills
now have ordered prerequisites, recovery and definitions of done; all four host
skills name their actual reads/mutations and evidence boundaries. Substantial HTML
requires separated HTML/CSS/classic JS, responsive accessible layout, wired
controls, preserved rich behavior, automatic bundled ECharts with sized containers
and idempotent resize, plus read+bind/preflight/runtime evidence distinctions. The
write descriptor and ECharts-specific preflight diagnostic teach the same runtime
assembly path. A new cross-host Harness invariant resolves every exact capability
named by built-ins against current tool/skill catalogs. Focused Harness checks pass
9/9 for settings/guidance/reference consistency, R61 descriptor inventory and HTML
source/native binding. Saved prompts require explicit schema-24 review/reset; exact
target-model Excel/VBA → HTML and Windows WebView2 qualification remain open.

R66 HTML/ECharts preview assembly correction (2026-09-03, user-reported
host-neutral): full-document assembly added the ECharts head block before locating
the body insertion point. A literal `</body>` inside the minified ECharts factory
therefore captured workspace JavaScript, produced a browser `SyntaxError`, and
could render vendor fragments as page text although stored HTML/CSS/JS members were
correct. Workspace scripts are now inserted at the last closing body/html tag in
the original document before head injection. A full-document regression executes
the resulting vendor and workspace scripts in order, while a local headless Chrome
smoke reports ECharts 5.6.0 and one 640×360 canvas. When referenced, the bundled
runtime is also projected as one visible read-only
`Dependencies/echarts.min.js` tree item rather than being confused with or copied
into a mutable workspace file. Focused web checks pass 14/14 across ECharts,
tree/selection and Artifact Library projection; affected JavaScript syntax and
diff checks pass. Windows WebView2 preview/export remains open.

R65 Agent readiness and completion correction (2026-09-03, user-approved
host-neutral): the reported complex Excel/VBA dashboard run began authoring before
loading applicable skills or establishing an execution checklist, nested an extra
`arguments` object in `common.html_workspace_write_file`, replaced richer HTML with
a simplified fallback after validation feedback and then described unverified work
as prepared. The write schema itself remains the strict semantic root
`{path, content}`; silently accepting the wrapper would create a compatibility
fallback. Prompt schema 23 instead requires upfront deliverable/capability mapping,
Task List creation for three-stage or discovery → construction → verification work,
source → primary result → bind/test → requested reusable-authoring order, and exact
evidence reconciliation before empty `tool_calls`. Format repair now tells the model
to remove an undeclared arguments wrapper, preserve nested declared fields when
needed, and never retry a rejected object unchanged. Built-in
HTML guidance preserves working dashboard behavior during repair and requires read +
bind evidence; Skill authoring runs last and only when requested. Focused Harness
gates pass 7/7 for prompt defaults/review, skill bodies, repair guidance, native
HTML ownership/binding and separate workspace file/script storage. This is model
guidance rather than a runtime proof of
semantic completeness; live-provider and Windows Excel/WebView qualification remain
open.

D05.6 authoring duplicate deletion (2026-09-03, user-approved host-neutral):
Tool and Skill mutation guards each carried an identical
`ArgumentPayload → Canonicalize → SHA256` implementation, while Prompt settings
carried a third token hash. Existing `ToolArgumentReader` now owns recursive ordinal
object canonicalization plus canonical argument/token SHA256. Tool and Skill delete
both duplicate pipelines; Prompt retains its one-key domain payload but uses the
same hash owner. Their separate validation, prepared contracts, stale-state checks,
dispatch boundaries and storage read-back remain unchanged. Production code is
reduced by 48 net lines. Focused Harness gates pass 7/7 for canonical argument
ordering/value binding, generic prepared-state confirmation, Tool/Skill
create-update-no-change-stale/read-back, typed Library guards/reference lifecycle
and Prompt verified/unknown read-back. The D05 route is complete; Windows/WQ gates
are unchanged.

D05.5 one controller run finalizer (2026-09-03, user-approved host-neutral):
fresh turns and confirmed continuations separately owned the same linked run-lease
setup, progress/checkpoint projection, terminal or waiting save, failure closure and
targeted run-store recovery cleanup. Both controller entries now use one lease
acquisition/release path, one progress callback and one `FinalizeControllerRun` for
success, confirmation pause, cancellation and failure; run-store recovery releases
the same causal trace/lease before canonical reload. `ConversationRunService →
AgentKernel` remains the only execution loop, and no coordination/document gate is
added around model wait. Production code is reduced by 10 net lines. Focused
Harness gates pass 8/8 for typed lifecycle projection, independent-chat concurrency,
same-chat/duplicate-confirm rejection, cancellation and fresh/confirmed pre/post-
dispatch store-failure recovery. D05 is complete; Windows/WQ gates are unchanged.

D05.4 one result materialization (2026-09-03, user-approved host-neutral): the
baseline reparsed strict `DataJson` independently for model projection, result
externalization, request-budget candidates and semantic media routing, and rebuilt
the durable wire during each budget attempt. `ToolResultMaterialization` now owns
one strict detached token; projection sanitizes one cached model result, while
externalization, budgeting and media selection reuse the corresponding cached
representation. Durable raw evidence is written once after the bounded model
projection fits. The two oversized resource/capability failure builders are one
path with unchanged exact codes/status, and three independent JSON reparse paths
plus repeated wire materialization are removed; production code is reduced by one
net line. Focused Harness gates pass 17/17 for strict wire, generic budgeting,
complete resource/capability evidence, oversized failure, media hydration/failure,
effect/status projection and durable replay. D05 is complete;
Windows/WQ gates are unchanged.

D05.3 stable resource target resolution (2026-09-03, user-approved host-neutral):
the baseline scoped `find` and every semantic `read` rebuilt all registered provider
inventories; duplicate suffixes depended on the current sibling set and rounded
creation evidence to seconds. Resource plans are now selected from the target's
exact semantic scope. Resources with creation evidence always carry one
full-precision readable UTC timestamp, so adding a duplicate title cannot rename an
already published target; no URI, revision, hash, cursor or candidate id is exposed.
Conditional duplicate scans were removed from gateway, prompt/compaction and the VBA
backup adapter; production code is reduced by 44 net lines. Focused Harness gates
pass 9/9 for scoped provider isolation, duplicate-title stability, `find → read`,
project/module reads, prompt/compaction, media hydration, reread and live revision
drift. D05 is complete; Windows/WQ gates are unchanged.

D05.2 native compound arguments (2026-09-03, user-approved host-neutral): the
baseline model path stringified native `components` through
`ToolArgumentReader.String` and reparsed it in `ToolAuthoringService`; Library
upsert serialized the same array before that path. `TableArgumentResolver` did the
same for `values`. Model and Library authoring now pass one native `JArray` whose
items are read directly as `JObject`; table dimensions and row shape are calculated
from the supplied `JArray` without a replacement tree. Quoted arrays and malformed
rows fail with stable `invalid_arguments`/exact table diagnostics. The two
`components` String call-sites, both `JArray.Parse` transitions and Library
`SerializeObject` transition are removed; production code is reduced by three net
lines. Focused Harness gates pass 3/3 for tool create/update, two-dimensional values,
malformed component/row shapes and exact errors. D05 is complete;
existing Windows/WQ gates are unchanged.

D05.1 one JSON argument tree (2026-09-03, user-approved host-neutral): the
baseline response path normalized `JObject → Dictionary`, rematerialized it with
`JObject.FromObject`, then normalized it again before canonical `ArgumentsJson`.
ModelProtocol now retains one detached `JObject` through optional-null removal and
schema validation; `ToolRuntime` remains the only handler-dispatch parse/typed
handoff. Both `AddProperties` transitions and the parser `FromObject` transition
are removed, with production code reduced by seven net lines. Wire v4, accepted IDs,
string escaping, defaults, history and replay are unchanged. Focused Harness gates
pass: conversation v4 13/13, ToolRuntime 15/15, malformed authoring manifest 1/1
and accepted native/user replay 3/3. D05 is complete; existing
Windows/WQ gates are unchanged.

Post-11O7 resource/tool recovery correction (2026-09-03, user-reported,
host-neutral): VBA project `structure` now exposes semantic project/component
targets only; runtime-owned `rna://` input is rejected before resolution and the
obsolete accepted-call shape requires explicit reset. Tool upsert no longer derives
host from an id namespace, so missing/mismatched manifests return their exact
validation error instead of `invalid_tool_host custom`; authoring guidance keeps
Library package source out of active-document `common.vba_*`, and normalizes raw
manifest blocks into valid VBA comments before validation/save. Malformed manifest
and schema token shapes now return stable diagnostics instead of escaping as
Json.NET casts. Exact module patching remains atomic and gains optional unchanged
`contextBefore`/`contextAfter` to disambiguate repeated source inside that module;
unsafe first/all/global replacement is not restored. The cleanup route is recorded
as `BACKLOG` D05 and is deletion-first. Focused Harness gates pass 13/13 and the
existing multi-chat request/attachment isolation gate passes 3/3. Real Windows
VBE/WebView2 parallel-run and recovery qualification remains open.

R61/11O7 Tool Library UX (2026-09-03, user-requested host-neutral):
every effective built-in now has on-demand human Markdown through one exact
id/revision UI-only DTO; built-in `Readme`, compact catalog, model descriptors,
capability payloads and ToolPack revisions remain unchanged. Test renders typed
JSON-Schema controls with required/conditional/optional, omit/null, defaults,
bounds and inline validation. Semantic `Далее` reuses one bounded in-memory session
clone only after `hasMore=true`; no URI/revision/hash/cursor/guard is editable or
persisted to the active chat. Implementation/Test containment, wrapping and
post-layout CodeMirror refresh are fixed host-neutral. Targeted Harness passes 3/3
and focused web gates pass 16/16. [Evidence](PHASE_11O7_TOOL_LIBRARY_UX.md).
Real narrow Windows WebView2 and live Office qualification remains open.

11O7 VBA CodeMirror completion slice (2026-09-03, user-requested host-neutral):
the existing pinned CodeMirror 5.65.16 now includes its local `show-hint` JS/CSS,
with the exact web vendor manifest expanded from 40 to 42 files. Both VBA source
editors expose `Ctrl+Space`; a typed dot opens member suggestions only for a known
loaded component. The UI-owned provider combines VBA-only keywords with bounded module/
component and Sub/Function/Property declarations from the current unsaved draft and
already loaded sources, hides another component's private procedures, caps parse
input at 2,000,000 characters and the popup at 80 entries, and makes no bridge/COM/
network call per keystroke. This does not claim Office object-model completion,
type inference, diagnostics or semantic rename. Focused completion and vendor gates
pass; real Windows WebView2 keyboard/focus/theme/layout qualification remains open.
The broader 11O7 built-in documentation, typed Test form and responsive layout work
remains current.

R61/11O6 final minimal core pack (2026-09-03, user-requested host-neutral):
Excel Agent core is now 21 exact schemas: four bootstrap, all 15 Excel and only
VBA whole-source write/exact patch. Word/PowerPoint have bootstrap plus the same
two VBA editing schemas; Outlook has bootstrap, Plan four bootstrap and Chat two
resource schemas. Rename, restore, delete and arbitrary macro remain in the exact
compact catalog with `schemaLoaded:false` and become callable only after complete
exact admission on the next model step. Deterministic routine-Excel evaluation
keeps 2 model calls / 1 read / zero schema loads, repairs and errors while reducing
the admitted first-request estimate from 23,787 to 22,753 tokens (-1,034; 4.35%).
Explicit macro adds one schema-load step and preserves confirmation plus unknown
external-effect evidence. Targeted Harness checks pass 82/82. [Evidence](PHASE_11O6_MINIMAL_CORE_PACK.md).
Live-provider latency and Windows WQ-PACK remain open.

R61/11O5 VBA/macro semantic intents (2026-09-03, user-requested host-neutral):
the public family now has six exact ids. Whole-source write and identity-preserving
rename are separate; patch hunks expose only `find/text`; restore accepts one exact
readable backup target or latest-for-module. Runtime supplies fixed replace,
resolves/pins raw backup identity before confirmation and retains all guards,
hashes and journal evidence. Model result data/messages and replay contain only semantic evidence;
old `op`, `backupId`, combined write/rename and retired/synthetic tool names require
explicit reset/new chat. Prompt schema is 22; inventory is 69 unique built-in ids /
72 host variants. Targeted Harness checks pass 109/109 and architecture checks 4/4.
[Evidence](PHASE_11O5_VBA_MACRO_SEMANTIC_INTENTS.md). Windows x64 Office/VBE,
confirmation, macro and live WebView2 qualification remain open.

Phase 11D2/11D3 scalable artifact-gallery correction (2026-09-02,
user-requested host-neutral): selectable Library groups now open a responsive grid
over the existing exact head projection, while chat images render as a four-cell
mosaic with `+N`. Both enter an ephemeral exact-revision image sequence, so the
user can navigate without reopening every tree item. A separate typed image
thumbnail read is bounded to 320 px / 512 KiB; the client allows four concurrent
reads and 48 cached results per chat. The reusable virtualized
`RNAssistantSequenceViewer` now owns only sequence UI and serves both the horizontal
image filmstrip and vertical PDF page rail. Format bytes and rendering remain in
providers: current images use local Skia plus Viewer.js, a future presentation
provider can supply slide frames without creating child artifacts, and live browser
authority remains separate. Focused artifact viewer/bridge Harness checks pass 4/4;
14 affected web suites and syntax checks for 14 changed JavaScript files pass.
Windows WebView2/Skia x64/x86 gallery, thumbnail and sequence interaction remains
open. [Contract](../artifact-library.md).

R61/11O4 Prompt/Tool/Skill semantic authoring (2026-09-02, user-requested
host-neutral): prompt save is one exact key/value mutation. Tool authoring now has
only exact read/upsert/delete; separate model validation, list mode, executor,
storage and self-granted authority fields are removed. Manifest/runtime derive
callable metadata and conservative safety, and upsert validates before write.
Plumbing-shaped custom arguments require explicit domain-identity rationale both
on validation and installed-package load, so direct package files fail closed.
Skill core/reference authoring is split into four exact ids; the duplicate VBA Tool
authoring skill is merged into the single Tool authoring entry. Model result/replay
keeps semantic ids/paths but omits package revisions/hashes/storage evidence;
incompatible retained calls require explicit reset/new chat. Prompt schema is 21;
inventory is 68 unique built-in ids / 71 host variants. Targeted Harness checks
pass 58/58 and architecture checks 4/4. [Evidence](PHASE_11O4_AUTHORING_SEMANTIC_INTENTS.md). Windows Library,
WebView2 and live package execution remain open.

R61/11O1 whole-resource/VBA discovery correction (2026-09-02,
user-reported host-neutral): `common.resources_read` no longer exposes model-owned
`action=next`; runtime assembles bounded internal pages and publishes one complete
representation or an explicit error. Exact bound Excel/Word/PowerPoint context now
publishes `document.vba_project_target`, so project-wide VBA inspection can read
the complete project structure without a preceding find. Unfiltered VBA find keeps
that project target first even beyond 20 components, while query results are
explicitly filtered matches rather than inventory. The VBA editing skill and both
resource descriptors teach the same route. Prompt schema is 20; retained old
resource calls require explicit review/reset. Targeted harness checks pass 37/37,
including direct runtime-target read, 81-component inventory, whole large-module
read, closed-document omission, strict no-`rna://` model context/history, skill
guidance, source inclusion and R61 property inventory. Windows live VBE/WebView
qualification remains open.

Phase 11D2/11D3 media-viewer UX correction (2026-09-02, user-reported
host-neutral): image and PDF pages now proportionally upscale in Fit mode to use the
whole preview area through locally pinned Viewer.js 1.12.0, zoom by wheel/pinch,
toggle Fit/100% on double-click and accept keyboard zoom shortcuts. PDF navigation
adds a virtualized left thumbnail rail and numeric jump over a typed 320 px / 1 MiB
render, with four reads in flight and 24 cached results at most; centered hover/focus
arrows, the persistent page indicator and Left/Right shortcuts remain independent.
PDF.js stays disconnected because the trust boundary admits verified bounded JPEGs,
not raw PDF bytes; a PDF.js viewer/worker/raw-byte/CSP cutover remains separate.
Focused Harness checks pass 4/4; web viewer/vendor/affected checks pass 37/37 and
five changed JavaScript files pass syntax. MockDemo actual-controller compile is
0 errors / 5 expected PDF platform warnings. A local 1280×720 Chromium smoke shows
eight visible rows for a 60-page rail and proportional uncropped page fit. Windows
WebView2 image/PDF/thumbnail interaction remains open.
[Evidence](PHASE_11D3_PDF_PREVIEW_X86.md).

R61/11O3 HTML semantic intent switch (2026-09-02, user-requested
host-neutral): the eight prior model tools became seven exact verified writes.
File/data whole writes are separate; patch has only exact operations; delete takes
one readable target; inspect and active selection are internal. Bind now consumes
the latest successful eligible accepted Office read from the same run and exposes
no source tool/arguments/candidate/URI; refresh policy is internal. Automatic
bounded preflight runs after mutation and preview projection. Durable revisions,
resource refs, hashes and binding source remain available to runtime/UI, while model
results and replay omit them. Prompt schema is 19; incompatible old HTML calls
require explicit reset/new chat. Inventory is 67 unique built-in ids / 70 host
variants. [Evidence](PHASE_11O3_HTML_SEMANTIC_INTENTS.md). Windows WebView and real
Office bind/refresh qualification remain open. Targeted harness checks pass 46/46;
affected web checks pass 25/25 and changed JavaScript syntax passes.

HTML workspace ECharts authoring correction (2026-09-02, user-requested
host-neutral): the built-in skill now gives only the standard `echarts.init` /
`setOption` pattern and rejects Chart.js/CDN loaders. Workspace HTML/JavaScript that
references `echarts` receives the exact existing local ECharts 5.6.0 bundle inside
the unchanged sandbox and standalone export; ordinary workspaces do not embed it.
Focused web checks pass 15/15 and the built-in guidance harness passes 1/1 with six
known CA1416 warnings. A local headless Chrome `file://` smoke loaded the assembled
artifact, reported ECharts 5.6.0, created one 400×240 canvas and accepted a bar
series. Windows WebView rendering remains open.

Exact source-string escaping correction (2026-09-02, user-reported
host-neutral): the shared v4 response and format-repair contracts now define one
outer JSON escaping layer for every string argument. HTML file/data/patch schemas
and built-in guidance explicitly preserve decoded line breaks and backslashes.
Runtime, accepted history, execution, CAS storage and replay remain exact and add
no auto-unescape, which would corrupt valid JavaScript, CSS, regular expressions
and paths. Focused harness checks pass 7/7, including raw parsing, format repair
and two complete HTML write/history/executor/replay variants. Live endpoint
generation and Windows WebView qualification remain open.

Artifact refresh and multi-chat request isolation correction (2026-09-02,
user-reported host-neutral): entering the top-level Artifacts tab now immediately
rebases a stale library selection to the server-projected head, then forces the
addressed chat-state sync before refreshing preview data. Exact revisions opened
from message/run cards remain pinned, and unsaved editor state is not rebased or
reloaded. Chat send now carries one captured target chat id through bridge payload,
response routing and guarded cleanup; attachment-submit barriers are maps keyed by
chat instead of a global single-flight value, so one chat no longer blocks or
captures another. Focused web regressions pass 31/31 and changed JavaScript syntax
passes. Real Windows WebView2 concurrent chats, streaming, tab timing and
multi-window qualification remain open.

Attachment immediate-send correction (2026-09-02, user-reported host-neutral):
picker/drop/paste staging had no composer send barrier, so an immediate submit could
snapshot empty `resourceDraftIds` and start a text-only model request while the PDF
draft finished in the background. Staging is now serialized per chat and an already
requested send waits for that queue before snapshotting draft IDs; failure preserves
the composer and starts no partial request. Focused UI regression covers successful
PDF ordering, fail-closed staging and current asset keys. Real Windows WebView2
picker/drop/paste timing remains open.

Phase 11D3 PDF preview + x86 native runtime (2026-09-02, user-requested
host-neutral): typed exact PDF info and one-page JPEG render calls now compose with
the existing 32,000 / 512,000-character exact text paging path; metadata no longer
returns the whole extracted body. PDF admission/PDFtoImage rendering is isolated in
`ArtifactPdfViewerService`; the generic viewer only delegates it. UI defaults to
page preview with navigation,
fit/zoom/download and a secondary bounded text tab, while outer metadata/JSON stays
under `Детали`. Matching PE32 x86 PDFium/Skia files from the exact reviewed x64
package versions are vendored and wired to Office and portable outputs; x64 native
DLLs are never loaded in x86. Harness passes 3/3 artifact viewers and 1/1 typed
bridge; affected web checks pass 37/37 and changed JS syntax passes. Real Windows
x86/x64 Office/WebView import, preview, scanned-page, loader and model-send gates
remain open. [Evidence](PHASE_11D3_PDF_PREVIEW_X86.md).

Phase 11D2 image WebView correction (2026-09-02, user-reported): the main UI CSP
now admits the local `blob:` URL already owned by the image viewer; remote image
and network sources remain denied. The image stage uses the remaining artifact
preview height while fit mode preserves aspect ratio. Focused CSP/viewer checks
cover both contracts; real Windows WebView2 reload/download/lifetime retest remains
open.

Phase 11D2 image/preview-first viewers (2026-09-02, user-requested host-neutral):
`ArtifactViewerService` now admits only one exact revision-pinned image attachment
with matching source message, kind, allowlisted JPEG/PNG/GIF/WebP MIME, byte length
and recomputed SHA-256. The typed bridge returns local CAS bytes under the existing
20 MiB bound. The UI-only image viewer provides natural dimensions,
fit/100%/zoom/download, retains at most two image payloads and revokes Blob URLs on
selection/chat/window disposal. Artifact detail defaults to `Просмотр`: Plan and
Markdown render as documents, Task List as goal/progress/steps, image as media and
existing domain JSON remains its domain viewer; generic metadata, raw Task List/JSON
and history are moved to `Детали`. Focused harness passes 2/2 artifact viewers and
1/1 typed bridge; affected web checks pass 36/36 and changed JS syntax passes.
Windows WebView2 dimensions/download/Blob lifecycle and full reload/multi-window
qualification remain open. [Evidence](PHASE_11D2_IMAGE_PREVIEW.md).

R61/11O2 Plan questions/doc/task-list switch (2026-09-02, user-requested
host-neutral): `common.questions_ask` now accepts semantic prompt/options and creates
UI-only question/option IDs after validation. Plan create/update are replaced by
`common.plan_doc_save`; restore accepts only readable version and delete accepts an
empty object while `PlanDocumentService` resolves exact active/source revisions and
guards. Task create/update/close are replaced by typed save/close branches of
`common.task_list_set`; runtime creates list/step IDs and preserves stable step
identity across complete saves. Model Tool Results and submitted question answers
contain no runtime IDs. Prompt schema 18, Plan/Task policies, task-tracking skill,
UI actions, accepted-history preflight and property inventory switched together;
five old lifecycle ids have no aliases and require explicit new chat/reset. The
inventory is 68 unique built-in ids / 71 host variants. Eighteen distinct focused
harness cases cover Plan mode/doc, Task List, model projection and `rna_*`
preflight, prompt review, ToolPack and property inventory; all pass with four known
CA1416 warnings on the compiling run. Plan/HTML/artifact web checks pass 22/22.
No full harness or Office/VSTO validation was run. Windows question/Plan/Task
persistence and WebView qualification remain open.

PDF x86 capability characterization before 11D3 (2026-09-02, user-requested
docs/evidence):
current PDF text/page-metadata ingestion is owned by Core/PdfPig, while visual page
conversion for model images is a separate PDFtoImage/PDFium/Skia path. The complete
production PdfPig `net471` reader closure (six PdfPig plus five managed runtime
assemblies) reports CLR `ilonly, 32/64` and zero unmanaged imports; the existing
focused extraction test passes 1/1, and the exact production `net471` binaries pass
a one-page create/open/text-extract smoke under Mono x64. The only vendored
PDFium/Skia native files are PE32+ x86-64 and the x86 publisher omits them. Thus
project or managed facade bitness is not the text-reader limitation: x86 text
extraction is structurally compatible, while visual/scanned-page rendering is
unsupported and not qualified. Because an image-capable model may invoke visual
rendering even after extraction, complete PDF sending is also not x86-qualified.
This ARM/macOS host cannot execute a Windows x86 process. The later 11D3 slice adds
the matching reviewed x86 native pair and host-neutral packaging, superseding the
earlier repository-availability conclusion; exact 32-bit Office execution remains
R64/open.

R63 HTML Office data-source owner-STA correction (2026-09-02,
user-requested host-neutral): native HTML ownership had removed the old outer
HostRuntime handoff, while `BeginDocumentAccess` only acquired the document gate
and did not dispatch the nested backend call. The session-aware source callback now
uses exact bound `HostRuntime.ReadDocument` for the Office read only; HTML workspace
and CAS mutation remain outside that gate, with no retry or fallback. The focused
Excel bind/refresh test fails before the change and passes after it, asserting two
backend reads on the owner STA. Four existing CA1416 warnings remain. Real Windows
Excel/WebView qualification is open.

R61/11O1 Resources + Capabilities switch (2026-09-02, user-requested
host-neutral): Chat/Plan/Agent now publish semantic `common.resources_find/read`;
the retired public list/resolve/search ids and handlers are deleted without aliases,
while internal provider operations retain exact URI/revision/cursor guards. Find is
fixed top-20 by query/scope. This entry originally shipped resource read as fixed
8,000-character `read|next`; the whole-resource/VBA correction recorded at the top
supersedes that public read shape. Capability search/read likewise expose only
query/kind and exact public tool/skill id plus semantic skill reference path/action.
`ModelToolResultProjection` separates durable exact evidence from all model-visible
Tool Results/history, validates capability evidence against the current catalog and
turns stale evidence into an explicit error. Runtime context, compaction/media and
ready-Plan handoff no longer leak document keys, refs, revisions, hashes or cursors.
Prompt schema is 17; the exact inventory is refreshed to 71 ids / 74 host variants.
The focused harness matrix passes 40 distinct resource, capability, ToolPack,
Chat/Plan, VBA continuation/guard, media/replay, prompt and source-inclusion cases
with four existing CA1416 warnings. Web Plan/handoff, HTML export/upload and
artifact projection pass 22/22; changed VBA JavaScript syntax passes. No full
harness or Office/VSTO validation was run. Windows live-provider/WebView2/WQ-PACK
evidence remains open.

Native Chat Completions tool-result/replay correction (2026-09-02,
user-requested host-neutral): `role=tool` request messages contain exactly `role`,
`tool_call_id` and `content`; the unsupported duplicate message-level `name` is not
emitted or counted. The preceding `assistant.tool_calls` entry now carries the exact
public dotted tool id and its accepted schema arguments instead of a synthetic
`rna_*` alias. Resource/capability and retired resource-call history is validated
against the post-11O1 schemas before dispatch; URI/cursor/page-size replay or an old
native alias requires explicit new chat/reset and cannot reach a model request.
Twenty-eight focused current-binary cases cover native request shape, model
projection, v4 history, protocol preflight, compatibility, compaction, trajectory,
interrupted-run and clone paths; all pass, with four existing CA1416 warnings on the
single compiling run. No full harness or Office/VSTO validation was run. Exact
live-provider/Windows qualification remains open.

Settings build identity visibility (2026-09-02, user-requested host-neutral UI):
the existing informational version is now rendered as an explicit product version
plus conventional 7-character Git commit id in Settings, with the `dirty` marker preserved.
Source-archive/legacy version strings stay visible unchanged, and the asset query is
advanced so WebView does not reuse the previous formatter. Targeted JavaScript and
version-format checks cover the change; Windows WebView rendering remains open.

GitHub archive build identity follow-up (2026-09-02, user-requested host-neutral build):
Git/GitHub source archives now export-substitute their exact full commit into a tracked
MSBuild provenance property. Archive builds therefore expose the same short commit in
Settings without carrying `.git`; branch and post-extraction working-tree state remain
explicitly unknown, so archive builds still cannot qualify as release evidence. Plain
file copies without substituted provenance retain `source-archive.unknown`.

R61/11O0 source-built-in property inventory (2026-09-02): a new targeted gate
enumerates the exact effective built-in catalog across Excel, Word, PowerPoint and
Outlook. It freezes 73 unique ids / 76 host-specific descriptor variants, their
allowed modes, direct binding, exact descriptor revision and recursive schema
property paths. Every URI/revision/cursor/offset/hash/guard/`*Id`-shaped property
requires an explicit semantic-identity or runtime-owned decision; the baseline also
records non-obvious provider/page-size/authority fields already selected for later
internalization. Four `common.html_data_bind` variants remain separate rather than
being falsely collapsed. Dynamic custom-package schemas stay package-revision-owned
and are reviewed in the Tool-authoring/Library slice; names alone are never stripped.
The new targeted filter passes 1/1 with four existing CA1416 warnings. Runtime,
public ids/schemas, skill bodies and UI are unchanged; next is 11O1 Resources +
Capabilities characterization and atomic cutover. Windows/Office was not run.

R61 model-visibility rule (2026-09-02, user-requested docs-only): the accepted
post-cutover contract now excludes `ResourceRef`, `rna://`, revision/hash,
cursor/offset, guards and internal IDs not only from arguments but also from
materialized Tool Results, `RUNTIME_CONTEXT` and replayed model history. Exact state
remains in the existing typed durable/runtime evidence and is resolved from a
readable semantic target without a replacement opaque candidate ID. Only stable
public tool/skill identity and runtime-generated Tool Result `tool_call_id` are
explicit exceptions; implementation starts with the still-open 11O1 Resources +
Capabilities atomic cutover. No runtime/schema/UI or Windows evidence changed.

Engineering rules and documentation consolidation (2026-09-02, user-requested
docs-only): [development rules](../development-rules.md) are the permanent owner
for cross-cutting responsibilities, typed contracts, file placement, risk-based
testing and Definition of Done. The new [documentation map](../README.md) is the
single routing/placement entry point; `AGENTS.md` is reduced to the active
stabilization/environment overlay. `BACKLOG.md` now contains only open bounded debt
and deferred decisions, with owner/trigger/removal/check requirements instead of a
duplicate completed-phase table. Completed 11T architecture follow-ups and temporary
trajectory/runtime cleanup roadmaps were removed; their remaining decisions moved
to backlog and canonical contracts. The desktop audit became the current
[desktop runtime contract](../desktop-runtime.md). Historical master/progress/phase
evidence remains intact but is excluded from default reading. Follow-up review
restored explicit token/context economy, future-phase and secret guards in
`AGENTS.md`, clarified the one-domain-doc reading path and pinned current
Chat/Context controller partial ownership in architecture. Runtime, schemas, UI and
release gates are unchanged. Pre-commit version format, `git diff --check` and all
local Markdown links/anchors pass; build/harness were not run for docs-only work.

R62 stuck error-run correction (2026-09-02): the primary `SendChat` catch closed
visible activities and wrote flat `LastRun.Status=failed`, but did not interrupt
the already persisted authoritative `KernelState`. A bridge error followed by
refresh therefore replayed lifecycle `running` and rendered an indefinite model/tool
spinner although execution had stopped. The new-run path now matches confirmation:
it interrupts the exact state to failed/cancelled before diagnostic persistence,
preserving completed counts and conservatively classifying any in-flight effect.
No retry, replay, second lifecycle owner or UI inference is added. The run-view
source-wiring regression is red→green; existing normal tool-error continuation and
typed model-failure terminal cases also pass, 3/3 targeted total. MockDemo
actual-controller compile passes with 0 errors / 3 existing CA1416 PDF warnings.
The Windows WebView model/tool-error retest remains open.

R61 all-tool contract and Library UX target (2026-09-02, user-requested docs-only):
the source and reported live-run audit classifies `resource_kind_unknown`,
`resource_revision_changed`, `resource_cursor_invalid`, `vba_patch_stale_source`
and successful provider-discovery `items: []`. Cursor/revision/stale-patch guards
are necessary fail-closed behavior; the primary design defect is that provider
vocabulary, URI/revision pairing and continuation state are caller/model-owned.
The accepted target uses minimal semantic intent plus runtime-owned typed
preparation over the same ToolRuntime. It preserves the model's explicit choice
between exact patch and whole-source write, forbids automatic patch-to-write/fuzzy
fallback, and audits merge/split by effect, confirmation, retry and result shape
instead of creating a mega-tool. The dedicated audit records current surface,
ownership table, all 35 conditional built-in `common.*` tool ids and all nine
built-in Common skill ids. Default
directions now internalize `resources_resolve`, HTML inspect/active selection and
`tools_validate`; merge resource discovery and active Plan/Task lifecycle; split
VBA rename, HTML file/data write and Skill core/reference authoring; and simplify
every retained schema. Overlapping `common.vba_tool_authoring` guidance merges into
the general tool-authoring skill, and every retained skill switches with its tool
contract. The audit also records family-complete model evals and
requires evidence before changing current core membership. UI-only built-in
documentation, typed Library Test controls and reported Implementation/Test layout
defects remain in the same R61 gate. Runtime, schemas and UI are unchanged; final
WQ evidence must use the post-cutover catalog. `git diff --check` and targeted R61
link resolution pass; no build/harness/Office validation is required for this
docs-only clarification.

Windows OfficeHosts compile correction (2026-09-02): the reported 13-error build
contained seven primary C# errors and six downstream `CS0006` failures. Excel now
imports `JToken`; Excel, Word and PowerPoint adapters import the moved
`OfficeHosts.Vba` backend; Outlook, PowerPoint and Word helper methods no longer
collide with their property/nested-type names. Runtime behavior and contracts are
unchanged, and no replaced path or compatibility adapter was added. Static source,
diff and version-format checks pass. The exact Windows x64 + Office + VS 2022
OfficeHosts/VSTO rebuild remains required before this WQ blocker is closed.

Windows OfficeHosts compile follow-up (2026-09-02): the next reported build exposed
one remaining primary `CS0136` in `ExcelChartInteropBackend`; its `chart` lambda
parameter collided with the later local chart variable, while the other six errors
were downstream `CS0006`. The lambda parameter is now unambiguous; chart behavior
is unchanged. A Roslyn source audit of all 438 production C# files found no syntax,
member-name or lambda/local-scope collisions. Excel chart 4/4, architecture 4/4,
production source inclusion 1/1, diff and version-format checks pass. The exact
Windows OfficeHosts/VSTO rebuild remains the required gate.

Local portable exact-publish correction (2026-09-02): `build-local.cmd` previously
copied with `SkipUnchangedFiles` into existing `artifacts\portable` and `C:\Temp`
directories, so a removed or renamed file could survive from an older build. The
publisher now validates configuration, architecture and every required input before
replacing the known x64/x86 output directory. Custom destinations are replaced only
when absent or carrying the publisher ownership marker; an existing unmarked custom
directory fails closed. Every current file is copied into the clean destination, so
locked old DLLs fail the build instead of leaving a mixed package. Targeted build
contract 1/1, XML, diff and version checks pass; actual Windows MSBuild/C++/CLI and
locked-Office behavior remain part of Milestone WQ.

Phase 11T10 final active-legacy cleanup (2026-09-01):
`IOfficeApplicationAdapter.GetBuiltInTools/ExecuteTool`, dispatched/UI-thread
wrappers and the remaining host switches are deleted. `OfficeBuiltInToolCatalog`
is replaced by source-owned `OfficeToolCatalog`; `ToolPackSnapshotFactory` consumes
exact `ToolPolicy`/`ToolBinding` from `ToolCatalogEntry` and fails closed when either
is absent. The old Core definition/command/result DTO file is split into mutable
`ToolCatalogEntry`, accepted-local `ToolInvocation`, immutable runtime contracts and
strict `ToolRunResult` v1. Legacy definition/result/UI adapters and the fake VBA
command/result queue are deleted. The Tools editor now accepts only lowercase
`rnassistant.toolLibrary` v1 and explicit revision-guarded mutation/result DTOs via
the same `ToolAuthoringService` owner as model authoring; storage-path identity,
catalog reconcile and unversioned/PascalCase fallbacks are gone. Full host-neutral
harness 589/589, Tool Library WebView 6/6, architecture 4/4 and production source
inclusion 1/1 pass; MockDemo builds and version/diff checks pass. Real Windows/COM,
VSTO pane lifetime, VBE and WebView qualification remain open.
[Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md).

Run Journal API-body visibility correction (2026-09-01): persisted
`llm.request` and `llm.response` CAS body rows now have visible `API request body` /
`API response body` markers plus a dedicated filter. Expanding either row performs
the existing bounded lazy `getChatEventPayload` read automatically, so inspecting
the raw body needs one action instead of a second hidden button; explicit refresh
remains available. Rejected model bodies keep a distinct label and are not called
raw API responses. No event, CAS, bridge operation, transport or durable projection
was added. Run Journal 9/9 and JavaScript syntax checks pass. Real Windows WebView2
rendering remains part of Milestone WQ; the next step and Phase 12 gate do not change.

Model SSE terminal incident fix (2026-08-31): streaming reader теперь распознаёт
non-empty `choices[0].finish_reason`, bounded одну секунду ждёт optional final usage
или `[DONE]` и завершает response, даже если совместимый endpoint оставил соединение
открытым. Final usage сохраняется; cancellation, JSON response path и общий request
timeout не менялись. Red open-stream characterization заменён green regression;
model diagnostics 4/4 и conversation streaming 4/4 pass. Реальный Qwen endpoint и
Windows UI не проверялись; mandatory tool migration route продолжается независимо.

Run Journal causal-boundary correction (2026-08-31): initial accepted call/result и
tool execution start/finish теперь получают semantic operation только при создании
сообщения или переходе состояния exact tool-call/run. Последующие annotation и
result hydration того же message ID сохраняются как `message.updated`, поэтому один
вызов больше не выглядит как несколько accepted/started/finished. Если узкий filter
не содержит terminal row, UI пишет `Не найден в выборке`, а не делает глобальный
вывод `Нет terminal`; renderer cache key обновлён. Storage transition regression 1/1,
соседние storage/trajectory 5/5 и Run Journal 8/8 pass. Windows WebView/live run не
проверялись; mandatory tool migration route продолжается независимо.

Live artifact projection correction (2026-08-31): после каждого durable
`tool_result` controller теперь ставит в WebView очередь существующую full
revision-guarded chat projection до дальнейшего progress/следующего model step.
Поэтому chart/tool-result, Plan, Task List и HTML artifacts появляются во время
работы, а не только в terminal response; confirmation continuation использует тот
же путь. Новый progress payload, store, transport или client lineage не добавлены.
Web artifact test 4/4, focused bridge cases 1/1 + 1/1 и реальный MockDemo controller
artifact test pass; только три прежних PDF platform warnings. Windows WebView2/
Office не проверялись, текущий 11T1 next step и остальные R51 gates не меняются.
[Evidence](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md).

Phase 11T0/7D bound Excel cutover (2026-08-31): desktop, VSTO and native
composition now bind one exact workbook to `ExcelDocumentSession`, capture the
current `RuntimeKey` once for that object lifetime and expose one direct
`ExcelInteropBackend`. Native Excel inspect/read/write and HTML Excel reads no
longer roundtrip through generic host commands. Range/selection ownership is checked
against the bound workbook, and a closed session fails instead of rebinding.
`ExcelReadCompatibilityBackend`, `ExcelWriteCompatibilityBackend`, their four
internal ids, `ExcelAdapter.WriteRange.cs` and repeated descriptor/`ActiveWorkbook`
execution resolution are physically removed. Focused Excel read 4/4, write 4/4,
HostRuntime 10/10, accepted-call 1/1, HTML binding 1/1, identity-probe 5/5,
architecture 4/4 and source inclusion 1/1 pass; MockDemo compiles with 0 errors /
3 existing platform warnings, and changed production host/composition sources parse
as C# 7.3 without syntax errors. Real Windows COM/proxy/lifetime and desktop/VSTO/native composition remain
WQ0/WQ-SESSION/WQ-EXCEL evidence; failures fix the new contract without restoring
legacy. [Evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md).

Phase 11T1 Excel find/replace (2026-08-31): `excel.find_cells` and
`excel.replace_cells` now use direct native registrations, typed requests and
outcomes, one domain service and a narrow backend over the workbook retained by
`ExcelDocumentSession`. Find keeps current literal/regex, values/formulas, scope and
bound behavior. Replace checks exact value/formula state before assignment, marks
the dispatch boundary immediately before mutation and verifies exact read-back with
distinct no-change/change/error/unknown evidence. Production `ExcelAdapter` branches,
range/pattern helpers and legacy result mapping are removed. Focused find/replace
4/4, Excel read 4/4, Excel write 4/4, HostRuntime 10/10, catalog/policy/snapshot/schema,
architecture and source-inclusion regressions pass; MockDemo and C# 7.3 syntax checks
pass. Real Excel COM/protected-range/partial-write behavior remains WQ-EXCEL evidence.
[Evidence](PHASE_11T1_EXCEL_FIND_REPLACE.md).

Phase 11T2 Excel sheet lifecycle (2026-08-31): `excel.add_sheet` and
`excel.rename_sheet` now use direct native registrations, typed requests/outcomes,
one domain service and a narrow backend over the workbook retained by
`ExcelDocumentSession`. The domain validates worksheet names, active/default source
and exact collection state; the backend rechecks ordered pre-state immediately before
dispatch and exact read-back distinguishes no-change/change/error/unknown. Production
`ExcelAdapter` branches/methods and the replaced name helper are removed. Full
host-neutral harness 549/549, architecture 4/4 and source inclusion 1/1 pass;
MockDemo and C# 7.3 syntax checks pass. Real protected-workbook, COM partial-effect,
rollback and case-only rename behavior remains WQ-EXCEL evidence.
[Evidence](PHASE_11T2_EXCEL_SHEETS.md).

Phase 11T3 Excel range mutations (2026-08-31): `excel.clear_range`,
`excel.sort_range`, `excel.filter_range` and `excel.format_range` now use exact native
registrations, typed requests/outcomes, one domain service and a narrow backend over
the workbook retained by `ExcelDocumentSession`. The domain preserves public schemas
and defaults, bounds one contiguous target to 100000 cells and classifies explicit
no-change/change/error/unknown. The backend rechecks an operation-specific pre-state
token immediately before dispatch; content/order/filter/format and autofit dimensions
are read back from the same target. Production `ExcelAdapter` branches, methods and
replaced color/alignment helpers are removed. Full host-neutral harness 553/553,
architecture 4/4 and source inclusion 1/1 pass; MockDemo builds, and all 366
production C# sources parse under C# 7.3 constraints. Real Excel sorting locale, AutoFilter
criteria normalization, Normal-style/conditional formatting, protected/large ranges,
autofit and partial COM effects remain WQ-EXCEL evidence.
[Evidence](PHASE_11T3_EXCEL_RANGE_MUTATIONS.md).

Phase 11T4 Excel table creation (2026-08-31): `excel.add_table` now uses an exact
native registration, typed request/outcome, one domain service and a narrow backend
over the workbook retained by `ExcelDocumentSession`. The domain preserves the
public schema/defaults, bounds the contiguous source to 100000 cells and the exact
workbook collection to 1000 tables, rejects explicit workbook-wide name collisions
and requires one exact new-table read-back. The backend rechecks an opaque token over
source values/formulas and the full table collection immediately before
`ListObjects.Add`. Production `ExcelAdapter` branch/method are removed and the fake
generic path is fail-closed. Full host-neutral harness 557/557 and focused table 4/4,
Excel read 4/4, kernel replay 10/10 and source inclusion 1/1 pass; MockDemo builds
with 0 errors / 3 existing platform warnings. Real Excel `xlNo` header/range
semantics, styles/localization, overlap/protection, rollback and partial COM effects
remain WQ-EXCEL evidence. [Evidence](PHASE_11T4_EXCEL_TABLES.md).

Phase 11T5 Excel charts (2026-08-31): `excel.create_chat_chart`,
`excel.upsert_chart` and `excel.delete_chart` now use exact native registrations,
typed requests/outcomes, one `ExcelChartService` and a direct backend over the
workbook retained by `ExcelDocumentSession`. The read path keeps the existing chart
artifact and a 10000-cell source ceiling. Mutations preserve public schemas/defaults,
bound the workbook to 200 charts and each chart to 100 series, reject ambiguous
workbook-wide names, recheck an opaque collection/source/labels token immediately
before COM and require exact chart plus untouched-peer read-back. Production
`ExcelAdapter` chart branches/methods/replaced helpers are removed; the fake generic
path fails closed. Full host-neutral harness 561/561 and focused chart 4/4 pass.
Real series formulas, localized names/types, axes, protection, generated names,
rollback/partial COM effects and bound selection remain WQ-EXCEL evidence.
[Evidence](PHASE_11T5_EXCEL_CHARTS.md).

Phase 11T6 Word bound vertical (2026-08-31): all nine existing public Word ids now
use exact native registrations, typed requests/outcomes, one `WordService` and a
direct backend over the document retained by `WordDocumentSession`. Desktop binds
an exact open document by identity/window; Word VSTO owns one document-bound runtime
per window. Reads and HTML binding share the typed adapter. Mutations recheck exact
target state before dispatch and require operation-specific read-back; post-boundary
failure is `unknown`, and tables are capped at 10,000 cells. Production
`WordAdapter` public branches/methods/replaced helpers and execution-time
`ActiveDocument`/descriptor fallback are removed; 11T9A later removed its VBA/macro
generic branches as well. Full host-neutral harness 565/565,
focused Word 4/4, architecture 4/4 and source inclusion 1/1 pass. Real Word COM
stories/selection/formatting, multi-window lifetime, rollback/partial effects and
VSTO pane cleanup remain WQ-WORD/WQ-SESSION evidence.
[Evidence](PHASE_11T6_WORD_BOUND_VERTICAL.md).

Phase 11T7 PowerPoint bound vertical (2026-08-31): all nine existing public
PowerPoint ids now use exact native registrations, typed requests/outcomes, one
`PowerPointService` and a direct backend over the presentation retained by
`PowerPointDocumentSession`. Desktop resolves an exact open presentation/window;
PowerPoint VSTO owns one runtime per window. Reads and HTML binding share the typed
adapter. Mutations recheck exact slide/shape state before dispatch and require
operation-specific read-back; post-boundary failure is `unknown`, and table/image
inputs plus returned collections are bounded. Production `PowerPointAdapter` public
branches/methods/replaced helpers and execution-time `ActivePresentation`/descriptor
fallback are removed; 11T9A later removed its VBA/macro generic branches as well.
Focused PowerPoint 4/4, architecture 4/4, source inclusion 1/1,
MockDemo build and C# 7.3 syntax checks pass. Real PowerPoint COM shape/slide
semantics, multi-window lifetime, rollback/partial effects and VSTO pane cleanup
remain WQ-POWERPOINT/WQ-SESSION evidence.
[Evidence](PHASE_11T7_POWERPOINT_BOUND_VERTICAL.md).

Phase 11T8 Outlook bound vertical (2026-09-01): all five existing public Outlook
ids now use exact native registrations, typed requests/outcomes, one
`OutlookService` and a direct backend over an Inspector/mail or Explorer/folder
retained by `OutlookDocumentSession`. Desktop resolves an exact open Outlook window;
Outlook VSTO owns one runtime per window. Implicit selected-mail access stays inside
that retained window, while explicit EntryID reads remain exact. Reads and HTML
binding share the typed adapter and bounded projections. Draft/update mutations
recheck exact target state before dispatch and require operation-specific read-back;
post-boundary failure is `unknown`. Production `OutlookAdapter` public branches,
methods, recursive folder/active-window helpers and execution-time
`ActiveInspector`/`ActiveExplorer` fallback are removed. Focused Outlook 4/4,
architecture 4/4, source inclusion 1/1, MockDemo build and C# 7.3 syntax checks
pass. The isolated full candidate is 572/576: four unchanged baseline failures are
in tool-result materialization/replay outside 11T8. Real Outlook Inspector/Explorer identity,
selection/folder drift, recipient normalization, partial effects and VSTO pane
cleanup remain WQ-OUTLOOK/WQ-SESSION evidence.
[Evidence](PHASE_11T8_OUTLOOK_BOUND_VERTICAL.md).

Phase 11T9A VBA bound backend (2026-09-01): Excel, Word and PowerPoint now expose
one `IVbaHostBackend` over their exact retained `DocumentSession`.
`VbaReader`, module mutation, package install/remove/run and public macro execution
pass typed requests/snapshots/actions directly to `OfficeHosts.Vba.VbaInteropBackend`;
the backend has no `ToolCommand`, `ToolResult` or generic `ExecuteTool` dependency.
Production host VBA/macro command cases, serialized package command payloads,
line-read helper and replaced mutation/package backend adapters are removed without
aliases or dual dispatch. Journal/CAS/guard/reconciliation formats are unchanged.
VBA 91/91, architecture 4/4, source inclusion 1/1, Harness/MockDemo builds and C#
7.3 parse pass. Public controller catalog/result projection remains only for the
atomic 11T9B native-handler switch; real Excel/Word/PowerPoint VBE, Trust Access,
session lifetime and macro effects remain WQ0/WQ-SESSION/WQ-VBA evidence.
[Evidence](PHASE_11T9A_VBA_BOUND_BACKEND.md).

Phase 11T9B native public VBA runtime (2026-09-01): all four public VBA
mutations and `common.office_run_macro` now have exact native registrations,
source-owned policy and one `VbaToolHandler` over the bound backend from 11T9A.
ToolRuntime persists bounded opaque prepared state with the pending kernel summary;
confirmation/replay consumes that exact state without re-preparing or rewriting
accepted arguments. Manual VBA actions use the same guard preparation. Exact
dispatch markers and typed read-back produce effect evidence; arbitrary macro
dispatch remains `unknown`. The VBA controller executor, mutable
`RuntimeGuardJson` route and public legacy result projection are removed without
aliases. VBA 92/92, ToolRuntime 15/15, Agent continuation 1/1, macro loop 1/1,
ToolPack 6/6, architecture 4/4 and source inclusion 1/1 pass; Harness/MockDemo and
C# 7.3 checks pass. Windows/VBE/session/confirmation qualification remains open.
[Evidence](PHASE_11T9B_VBA_NATIVE_RUNTIME.md).

Phase 11T9C1 native Plan questions (2026-09-01): `common.questions_ask`
now has exact source-owned Plan policy and `UserQuestionToolHandler`. It returns
typed `AwaitingUser`, so AgentKernel ends the invocation without another model
step; question prose does not control lifecycle. The old executor class/path and
`ControllerExecutorKind.UserQuestion` branch are removed without aliases. Plan
mode 3/3, architecture 4/4 and source inclusion 1/1 pass; Harness/MockDemo C# 7.3
builds pass. Windows question-panel/answer continuation remains UI evidence.
[Evidence](PHASE_11T9C1_QUESTIONS_NATIVE_RUNTIME.md).

Phase 11T9C2 native Plan Document runtime (2026-09-01): all four
`common.plan_doc_*` mutations now use source-owned Plan-only verified-write policy
and exact `PlanDocumentToolHandler` bindings. `PlanDocumentService` marks dispatch
immediately before the session revision/tombstone append; the handler verifies the
exact artifact and active head before reporting `VerifiedChange`. Semantic failures
remain known pre-dispatch errors. `ControllerExecutorKind.PlanDocument` and the old
executor class/path are removed without aliases. Plan mode 3/3, Plan Document 3/3,
ToolPack 6/6, architecture 4/4 and source inclusion 1/1 pass; MockDemo builds with
the three existing platform warnings. Windows Plan UI/persistence qualification
remains open.
[Evidence](PHASE_11T9C2_PLAN_DOCUMENT_NATIVE_RUNTIME.md).

Phase 11T9C3 native Task List runtime (2026-09-01):
`common.task_list_create/update/close` now use exact source-owned Agent/Plan
verified-write policies and one `TaskListToolHandler` over `TaskListService`.
Validation and current-list checks complete before dispatch; revision append marks
the boundary and exact artifact/active-or-cleared pointer verification produces
`VerifiedChange`. The old executor class/path and `ControllerExecutorKind.TaskList`
are removed without aliases. Task Lists 3/3, Plan mode 3/3, architecture 4/4 and
source inclusion 1/1 pass. Windows Task List projection/persistence remains UI
evidence.
[Evidence](PHASE_11T9C3_TASK_LIST_NATIVE_RUNTIME.md).

Phase 11T9C4 native HTML workspace runtime (2026-09-01): all eight
`common.html_workspace_*` / `common.html_data_*` tools now use exact source-owned
Agent policies and `HtmlWorkspaceToolHandler`. Inspection is an independent read;
the seven mutations mark dispatch before local state change and return verified
change/no-change or explicit unknown effect. Bind/refresh revalidate their exact
source schema and invoke only the shared typed bound Office read adapter under the
document gate. `HtmlArtifactToolExecutor`, `ControllerExecutorKind.HtmlArtifact`,
four adapter `ExecuteDataSource` shims and the generic source fallback are removed
without aliases. HTML-focused harness 23/23, Word/PowerPoint/Outlook native binding
3/3, Plan mode 3/3, ToolPack 6/6, architecture 4/4 and source inclusion 1/1 pass;
MockDemo builds with the three existing platform warnings. Windows WebView/live
Office binding, persistence and export qualification remains open.
[Evidence](PHASE_11T9C4_HTML_NATIVE_RUNTIME.md).

Phase 11T9C5 native capability runtime (2026-09-01):
`common.capabilities_search/read` now use exact source-owned Agent/Plan independent
read policies and `CapabilityToolHandler` over `CapabilityCatalogService`. The
handler receives the immutable run tool catalog and selected skill catalog directly;
tool-schema, skill-core/reference, revision, compact-index and ToolPack admission
evidence are unchanged. Capability ids and handler bindings are exact, with no case
alias. `CapabilityDiscoveryExecutor`, its controller branch, the old
`SkillToolExecutor` reader and capability use of legacy outcome/result adapters are
removed. Discovery 1/1, Skills 4/4, ToolPack 6/6,
oversized evidence 1/1, Agent skill context 2/2, Plan mode 3/3, pipelines 3/3,
strict controller schemas 1/1, architecture 4/4 and source inclusion 1/1 pass;
MockDemo builds with the three existing platform warnings. Windows/live-provider
WQ-PACK and prompt UI qualification remain open.
[Evidence](PHASE_11T9C5_CAPABILITIES_NATIVE_RUNTIME.md).

Phase 11T9C6 native prompt runtime (2026-09-01):
`common.prompts_read/save` now use exact source-owned Agent-only policies and
native handlers over `PromptSettingsService`. Read is an independent local read.
Save requires a non-empty schema-declared update and persists a bounded guard over
the exact accepted arguments and supplied current fields before confirmation;
confirmed execution rejects stale settings before dispatch, preserves unrelated
global values, marks the settings-write boundary and verifies supplied fields by
read-back. Exact no-change avoids dispatch. `PromptToolExecutor`,
`ControllerExecutorKind.Prompt` and prompt use of legacy command/result adapters
are removed without aliases. Prompt/save 1/1, settings 5/5, typed settings bridge
1/1, Plan mode 3/3, ToolPack 6/6, strict controller schema 1/1, architecture 4/4
and source inclusion 1/1 pass; MockDemo builds with the three existing platform
warnings. Windows settings persistence/DPAPI/Prompts UI remains open evidence.
[Evidence](PHASE_11T9C6_PROMPTS_NATIVE_RUNTIME.md).

Phase 11J1 native custom Tool authoring (2026-09-01): all four exact
`common.tools_definition_read/validate/upsert/delete` operations now use
source-owned Agent-only policies and native handlers over `ToolAuthoringService`.
Read/validate are independent local reads. Upsert/delete prepare a bounded guard
over the exact accepted arguments, operation and current effective stored
definition; confirmed execution rejects stale state before dispatch, marks the
storage boundary and verifies the effective definition or absence by read-back.
Exact no-change avoids dispatch. `ToolAuthoringExecutor`, its controller kind and
authoring use of legacy command/result adapters are removed without aliases.
Focused Tool CRUD 1/1, Tools 36/36 and pipelines 3/3 pass; architecture, source
inclusion, MockDemo and version checks are recorded in the linked evidence. Existing
custom package execution/Tools UI and Windows Library/VBE remain open for 11J2 and
qualification respectively.
[Evidence](PHASE_11J1_TOOL_AUTHORING_NATIVE_RUNTIME.md).

Phase 11J2 typed custom package execution/UI (2026-09-01): every existing
global/document-local VBA package is captured as complete immutable
`ToolPackageSource` contract v1 with a deterministic content revision distinct from
its human package version. Exact custom ids bind to
`vba.custom.package.execute.v1` and execute through native `ToolRuntime`; source,
schema, host and entry point stay pinned with the run registration. Backend dispatch
is marked immediately before install/run/remove. Arbitrary macro dispatch remains
`unknown`; persistent install/remove return change/no-change only from package
journal/read-back. Tools UI accepts only typed result v1 for install/remove and uses
the typed source for status. `VbaPackageToolAdapter`,
`VbaLegacyResultProjection`, the legacy custom dispatch branch and PascalCase UI
fallback are deleted without aliases. Focused package 23/23, session execution 1/1,
typed VBA bridge 1/1 and web package action 1/1 pass; remaining checks are recorded
in the linked evidence. Immutable package history, Host Fabric and Windows/VBE are
not claimed.
[Evidence](PHASE_11J2_VBA_PACKAGE_NATIVE_RUNTIME.md).

Phase 11K1 native Skill authoring (2026-09-01): exact
`common.skills_upsert/delete` now use Agent-only confirmed-write policies and native
handlers over `SkillAuthoringService`. `SkillPackageSource` contract v1 fingerprints
the complete current package: stable metadata, normalized core Markdown and ordered
reference revisions. Preparation binds exact accepted arguments plus the current and
intended package revisions; confirmation rejects drift before dispatch, preserves
omitted fields and verifies exact read-back or absence. No-change avoids dispatch.
`SkillToolExecutor`, the final `ControllerExecutorKind`/controller dispatch branch
and Skill use of legacy result conversion are deleted without aliases. Skills 4/4,
Tools 36/36, ToolPack 6/6 and focused confirmation/replay/recovery pass; remaining
checks are recorded in the linked evidence. Existing Skills UI typed mutation/result
boundary remains mandatory 11K2; append-only package history/import/export R54 and
Windows Library/WebView qualification are not claimed.
[Evidence](PHASE_11K1_SKILL_AUTHORING_NATIVE_RUNTIME.md).

Phase 11K2 typed Skills UI boundary (2026-09-01): Init/SendChat and Library
refresh now expose one lowercase `rnassistant.skillLibrary` v1 contract. The editor
sends explicit create/update/rename/delete mutations with the exact loaded package
revision, and reference read/upsert/delete carries the same package guard. All
manual mutations run through `SkillAuthoringService`, return typed dispatch/effect
and exact package metadata, and stop on the first failed guarded operation. The
controller no longer holds `SkillStore`; raw `SkillDefinition[]`, implicit catalog
reconcile deletes, `StoragePath` identity and unversioned/PascalCase WebView
fallbacks are removed. Skills 5/5, bridge 25/25, Skills web contract 4/4,
architecture 4/4 and source inclusion 1/1 pass; MockDemo compiles with no errors.
Immutable package history/restore/tombstone/import/export R54 and Windows
Library/WebView qualification are not claimed.
[Evidence](PHASE_11K2_SKILLS_UI_TYPED_BOUNDARY.md).

Pre-R37 trajectory inference removal (2026-08-31): `TrajectoryRunProjection` and
`TrajectoryDerivedProjection` no longer reinterpret a persisted
`tool.result.recorded` carrying `AcceptedCallOrigin` as a current accepted call.
`run-causal` preserves the exact operation as `incompatible` with explicit reset
metadata; `tool-execution` grants it neither call nor completed-result authority.
No history is rewritten or deleted. Focused trajectory harness 4/4 passes; Windows
Diagnostics/WebView qualification remains open.

Accepted-risk WQ0 decision and complete active-legacy removal (2026-08-31,
docs-only): пользователь
потребовал перенести все существующие tools на typed architecture и удалить legacy
execution/history paths до Phase 12. Это не допускает big-bang rewrite и не добавляет
новые Browser/Automation/product capabilities: каждый existing semantic family
переключается отдельно с удалением последнего старого consumer. По явному решению
WQ0 больше не prerequisite: 11T0/7D атомарно принимает текущий `RuntimeKey` exact
bound workbook на его lifetime, чтобы bound workbook никогда не сосуществовал в
production с compatibility Excel backend. WQ0 остаётся deferred Windows evidence;
при выявленном расхождении исправляется новый bound contract без возврата legacy.
Canonical route и removal gates обновлены; runtime этим решением не менялся.

Artifact identity authority audit completion (2026-08-31): normalization no longer
selects one of duplicate case-insensitive artifact ids. Exact URI/reachability,
storage hydration/save, bridge/Library, Plan/Task List/HTML navigation, attachment
linking and tool-result reuse now omit or reject ambiguous immutable identities
without rebinding evidence; unrelated unique artifacts remain usable. Attachment
artifact provenance cannot move between source messages or bodies. Focused resource,
attachment and tool-result regressions pass; full host-neutral harness 539/539 and
`git diff --check` pass. No Office/COM/WebView path or Windows gate changed.

Runtime diagnostics/VBA usability fix (2026-08-31): existing raw model request and
response CAS payloads are now opened directly from their Run Journal rows; accepted
arguments, executor result/data and typed effect evidence are present in row content,
while attempt/call/mutation ids stay in a collapsed technical section. The UI no
longer presents one legacy unverified write as both `без проверки=1` and a second
`неизвестный эффект=1`; it labels that single cause as legacy without read-back.
`resources_list(provider=vba)` now returns the project plus live components by
default, and backups remain explicit. `excel.inspect` is no longer described as a
write preflight, and the fixed v4 schema tells the model to check all requested
deliverables before ending its loop. Already callable tool summaries/safety are no
longer duplicated in the compact capability index; unloaded tools and skills retain
selection metadata, and the mandatory Excel/VBA request now has an explicit prompt
headroom regression. No event, id, store, verifier phase or generic batch abstraction
was added. Focused harness and Web UI suites pass; Windows WebView2/Office remain
unqualified.

Resource-result admission regression fix (2026-08-31): VBA discovery/read already
returned exact modules, source and cursor v2, but the model-session projection first
filled the Excel Agent request almost to its input limit and then replaced the
structured result with a successful generic preview. One shared calculation now
covers messages, actual response options, bounded format-repair overhead and a
proportional continuation reserve at composition, ToolPack admission, result
projection, Prompt Inspector and final pre-dispatch. Successful
`common.resources_*`/`common.capabilities_*` evidence is passed intact or becomes an
explicit context-too-large error; it is never a successful empty/truncated transport
wrapper. Resource list/resolve/search results also expose returned exact refs at Tool
Result root. Generic oversized results retain the full CAS resource and use only the
largest inline
projection that fits the same reserves. The replaced capability-only budget helper
and fixed 1,200-token envelope guess are removed. Host-neutral `agent:` 36/36,
`model protocol:` 15/15, `tool pack:` 6/6, `resources:` 10/10 and Prompt Inspector
3/3 pass, including an Excel/VBA list→source→cursor loop and explicit
oversized-evidence refusal. Windows WebView2/Office/VBE qualification is unchanged;
the active Phase 11T next step is not advanced by this fix.

Resource-result boundary follow-up (2026-09-01): повторный cursor-loop audit
обнаружил, что прежний proportional continuation reserve мог быть меньше
разрешённого ответа модели, а недоступная media/materialization projection могла
закрыть успешное чтение как `status:ok` без доставленного evidence. Теперь reserve
равен максимуму из proportional headroom и `max output + 512-token result envelope`;
слишком малый context fail-closed уже при composition. Provider-owned text page по
умолчанию уменьшена с 8,000 до 2,048 символов и остаётся lossless через exact
`nextCursor`; явный больший page передаётся целиком либо возвращает
`resource_evidence_context_too_large`, без transport preview. Read projection
failure становится model-facing `error`, mutation outcome/effect не переписывается,
а невозможность вместить даже compact error классифицируется как
`PromptBudgetExceeded`, не infrastructure. Regression реально читает точную первую
и вторую VBA pages, проверяет oversized continuation и media failure. Targeted
`agent:` 39/39, `vba:` 91/91, `resources:` 10/10, `model protocol:` 15/15,
`tool pack:` 6/6, token estimate 6/6 и Prompt Inspector 3/3 pass. Full harness на
текущем составе — 575/579: все четыре failure уже воспроизводятся на чистом parent
`32a6f7a` после Outlook switch (три legacy-Outlook kernel replay fixture и один
legacy conversion fixture больше не dispatch), поэтому не исправляются внутри
resource contour и должны быть удалены/перенесены текущим 11T9 legacy cleanup.
Active 11T route и Windows WebView2/Office/VBE gates не меняются.

Resource continuation scope fix (2026-08-31): prior immutable cursors were raw opaque
offsets, while revision-bound cursors carried only offset plus data hash. Reusing a
cursor on another URI/query with identical content or collection hashes could
therefore skip data silently; with different hashes it surfaced as revision drift
and encouraged the repeated read loop visible in the supplied screenshots. Cursor
v2 now independently binds list continuations to provider/kind and read continuations
to canonical URI/normalized representation, while retaining content/collection drift
guards. Cross-scope use returns non-retryable `resource_cursor_invalid`; live drift
explicitly requires a fresh same-URI read with both cursor and revision omitted.
Immutable and identical-source VBA cross-resource regressions, exact-list-query
regression, normal continuation and stale-live-revision cases pass host-neutral.
No retry/dedup state or second resource authority was added; Windows Office remains
unqualified.

Resource member reference fix (2026-08-31): HTML mutation results exposed only an
internal revision artifact id, while exact member discovery used an opaque SHA-256
key. The model could therefore reconstruct a plausible path URI that always fell
through to generic not-found. Mutations now return the exact artifact `ResourceRef`
and current member URIs; the existing `common.resources_resolve` supports one
central parent-revision + member-path lookup. Exact chat resolution now separates
invalid URI, chat mismatch, artifact/revision/member absence, noncanonical member
keys and corrupt payload, with recovery guidance in the typed Tool Result. No new
resource store, transport or correlation id was added. Verification: `resources:`
10/10, native resource runtime 1/1, HTML workspace/source 2/2; four existing
Windows-only COM analyzer warnings. Windows/WebView2/Office were not run.

Empty-text Resource Fabric regression fix (2026-08-31): exact resource reads used
`IsNullOrWhiteSpace` as an availability test, so valid empty and whitespace-only text
attachments/documents were rejected as missing despite exact hash/body evidence.
Availability now distinguishes a missing body from a present empty body; exact reads
and the text viewer preserve zero-length and whitespace content, while an unavailable
CAS-backed body still fails closed. Focused Resource Gateway coverage passes; no
Office/COM path changed.

WebView failed-initialization follow-up (2026-08-31): bridge bootstrap now queues
early non-init calls, but an exception while applying an already-tokened init response
could leave that token live and release queued calls from an unavailable UI state.
Entering the bridge-unavailable state now revokes the token; queued calls fail closed
and a later explicit reinitialization must obtain a fresh token. The focused bootstrap
test covers this partial-init failure; real Windows WebView2 lifecycle remains open.

Artifact identity ambiguity regression fix (2026-08-31): the chat resource
projection previously collapsed case-insensitive duplicate artifact ids by selecting
the first replayed item. Exact URI reads could therefore expose an arbitrary body
after corrupt or inconsistent replay. Ambiguous ids are now omitted from list and
search, exact resolve/read fails closed, and neither shared URI helpers nor the
bounded resource/active-Plan prompt can reintroduce the ambiguous id. Unrelated
unique artifacts remain available. The focused Resource Gateway regression passes;
no Office/COM path was changed.

Attachment provenance regression fix (2026-08-31): chat resource reads and the new
11D1 text viewer previously preferred the declared source message but then searched
all other messages for the same attachment id. A missing/corrupt source binding could
therefore hydrate unrelated bytes or extracted text under another artifact URI. One
shared exact resolver now requires a unique source message, a unique metadata-named
attachment, matching artifact/attachment CAS SHA-256 and byte length, and non-failed
attachment status. Invalid mapped attachments remain metadata-only and cannot fall
back to `InlineText`. Focused Resource Gateway and artifact-viewer tests pass; real
Windows/WebView2 and Office validation are unchanged.

MockDemo qualification composition regression fix (2026-08-31): WQ-A4/A5
expanded `QualificationBuiltInCatalog` to the complete versioned suite, but the
source-linked MockDemo assembly still embedded only coverage and the UI-shell pack.
Constructing the production controller therefore failed on missing
`common.quick.v1.json` before every demo model profile. MockDemo now embeds all
canonical pack files with their exact production logical names. The production
source-inclusion architecture test also compares every pack file and logical name
across Office, Harness and MockDemo so later suite additions fail closed at build
verification. Focused structure test and Release MockDemo full self-test (four model
profiles plus failed-turn persistence) pass; only the three existing PDF CA1416
warnings remain. Direct .NET Framework 4.8 helper build is unavailable on this Mac
without the Windows targeting pack and remains part of the real Windows gate.

Default Excel Agent callable-pack regression fix (2026-08-31): growth of the
complete optional capability index pushed the mandatory Excel/VBA core request to
about 28,742 tokens against the conservative 28,672-token input budget, so Agent
failed before the first model dispatch. The complete catalog, exact ids/revisions,
safety/body metadata and all core schemas remain unchanged; only prompt-index
summaries now keep a 96-character prefix, while explicit capability search retains
the previous 160-character prefix. The existing VBA/core regression now fails with
the actual pre-dispatch error instead of an empty prompt, and focused Agent plus
capability-discovery tests pass. Full host-neutral harness will be rerun at the end
of the branch audit; real provider/Office qualification is unchanged.

WebView bootstrap regression fix (2026-08-31): photo triage and browser smoke
reproduced the UI-wide failure as a script-order regression in
`app-html-workspace.js`: it captured `saveChatMode` before `app-chat.js` loaded,
aborting bootstrap before `initialize()` could receive the WebView bridge token.
The action callback now resolves `window.saveChatMode` only when invoked, and the
shared `send()` path queues non-`init` bridge calls behind `initializePromise`
instead of posting them with a null token. `app-core.js` and
`app-html-workspace.js` cache keys were bumped, and web cache-key assertions were
aligned. Verification: `tests/web/*.test.js`, `node --check` for `web/js` and
`tests/web`, `git diff --check`, plus local browser smoke for top tabs, settings
pages and diagnostics sub-tabs all pass. Real Windows WebView2/Office/VSTO and
live bridge callback validation were not run.

Phase 11T Office tool modernization route (2026-08-31, docs-only): code audit
подтвердил, что production `IOfficeDocumentSession` ещё не реализован, Excel typed
read/write используют explicit compatibility backends до 7D, а Word/PowerPoint/
Outlook и остальные Excel families всё ещё входят через generic host `ExecuteTool`.
План теперь требует parity-first migration: один semantic family получает typed
request/domain service/bound backend/outcome/effect evidence и в том же change теряет
legacy branch/mapper/helpers. Только после qualification/evals отдельно допускаются
`upsert_table`, richer sort/filter/format, exact Word ranges, stable PowerPoint refs,
bounded `compose_slide` и exact Outlook EntryID updates; generic batch writes и
`execute_actions` запрещены. Runtime/UI/schema не менялись; WQ/Phase 12 не закрыты.
Docs-only verification: `git diff --check` и local Markdown targets pass; harness,
build и Windows/Office/VSTO validation не запускались.

Excel interop boundary normalization (2026-08-31): `Excel.Name.RefersTo` теперь
явно приводится к строке перед заполнением typed `ExcelNameSnapshot`; schema,
bounds и read route не менялись. `excel read:` — 4/4 pass, с 4 ожидаемыми
CA1416 warnings из Windows-only identity probe. Реальный OfficeHosts build и live
Excel COM остаются Windows gate.

Phase 11D1 bounded text/source and Markdown viewers (2026-08-31): новый
host-neutral `ArtifactViewerService` принимает только canonical exact artifact URI
активного chat и читает representation через общий Resource Gateway страницами по
32,000 символов с общим limit 512,000. Full copy/download появляется только после
contiguous read с неизменными URI/hash/total/kind; truncated attachment extraction и
over-limit source остаются partial. Text/Markdown adapters UI-only: line numbers,
page search/copy, sanitized Markdown только для complete source и exact Source tab.
Старый generic artifact `<pre>` удалён; JSON и inert uploaded HTML сохраняют своих
owners. Viewer state ephemeral и очищается при chat switch. Harness 4/4 focused,
web 48/48 targeted/reused; changed JS syntax, version format, diff, source inclusion
и local Markdown links pass. Windows WebView2/clipboard/download не проверялись.
[Evidence](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md).

Approved Phase 11 priority: complete Artifact/Plan/HTML Workbench viewers → coherent
Library UI and typed Issue Center → read-only selected-endpoint Tool Inspector →
Excel Host Fabric core → independently qualify Word, PowerPoint and Outlook and add
their endpoint adapters → unified all-host picker → custom Tool Library authoring →
Skills authoring → Browser/Local Automation. Pipelines remain disabled. This order
does not expand Phase 12 or replace real WQ gates. [Tool Library](../tool-library.md),
[Issue Center](../qualification.md#11-phase-11-issue-center),
[Host Fabric](../host-fabric.md).

Phase 11 product-route review (2026-08-31, docs-only): tools are split into existing
runtime execution, early read-only capability truth and later revisioned authoring.
The target UI keeps Artifacts, Tools and Skills as separate Library authorities and
adds a Problems projection over exact trajectory/qualification evidence with
redacted issue export. Host Fabric proves Excel routing first; Word, PowerPoint and
Outlook must each pass local tools/resources/effect gates before endpoint admission.
Master plan, architecture, Artifact/Host/Qualification contracts, backlog and risks
updated; runtime/UI behavior and Phase 12 release scope unchanged. Ten changed
Markdown files have valid local link targets; `git diff --check` and pre-commit
`ValidateVersionFormat` pass. Harness/Office/WebView tests were not run for docs-only
planning.

Phase 11C3 exact HTML binding recovery/export (2026-08-31):
`HtmlWorkspaceArtifactService` остался единственным owner whole-workspace revisions;
`ChatStore.Save` больше не создаёт hidden artifact/revision, старый fallback и dead
helpers удалены. Binding хранит SHA-256 exact transformed JSON и явную completeness
`complete|bounded|truncated`; mismatch при replay/normalization становится error.
Refresh остаётся ephemeral до chat/export checkpoint. Export требует exact non-empty
active head, при изменении создаёт ordinary revision через того же owner и возвращает
typed exact artifact id, canonical `rna://`, CAS hash и полный workspace. UI блокирует
dirty/stale/incomplete evidence; standalone assembly сохраняет raw JSON lexemes без
parse/stringify rounding и публикует completeness/hash metadata. Harness 8/8 focused,
web 21/21 и changed JS syntax pass; version format, diff, source inclusion и local
Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C3_HTML_BINDING_EXPORT.md).

Phase 11C2 inert uploaded HTML import/source preview (2026-08-31): новый
host-neutral `UploadedHtmlResourceService` принимает только exact canonical immutable
attachment revision с совпадающими message/attachment identity, hash/length и HTML
type. Typed source preview идёт через Resource Gateway с bound 32,000 и UI вставляет
его только через `textContent`; chat switch сбрасывает незавершённый read. Explicit
import требует exact active HTML guard, новый `.html`/`.htm` path и полный decoded
payload до 300,000 символов, затем создаёт обычную workspace revision с exact
source URI/hash/relation provenance; original CAS/message ref не меняются. Harness
5/5 focused, web 21/21 и changed JS syntax pass; version format, diff и local
Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C2_HTML_IMPORT_PREVIEW.md).

Phase 11C1 HTML whole-workspace lineage (2026-08-31): новый host-neutral
`HtmlWorkspaceArtifactService` теперь выдаёт revision как global `max+1` по всем
HTML branches, а не `active+1`; alternative branches больше не получают одинаковый
номер. Каждый child сохраняет exact active parent, Library продолжает выбирать
explicit active pointer даже при более новой inactive branch. Duplicate/invalid
revision graph отклоняется до workspace mutation или pointer restore; missing-parent
degraded recovery остаётся readable/mutable и не угадывает ancestry. Новый targeted
lineage test и пять затронутых storage/recovery/Library regressions pass; version
format, diff и local Markdown links pass. Windows WebView2/Office не проверялись.
[Evidence](PHASE_11C1_HTML_LINEAGE.md).

Phase 11B3 Plan history/removal UX and handoff (2026-08-31): новый host-neutral
Каждая non-head Plan revision получила `Восстановить` с exact server-projected
current/source guards; action создаёт новый head через 11B2 owner и сериализует
повторные UI mutations. Delete сначала выполняет dry-run, затем показывает revision
count и все referencing message ids, и только после confirmation повторяет тот же
exact guard. Ready handoff сверяет active raw artifact, status и byte-exact `rna://`,
переключает Agent и отправляет только URI с `common.resources_read` instruction.
Mutation/handoff logic удалена из detail renderer и осталась в тематическом actions
owner. Web Plan 7/7, Artifact Library 3/3 и syntax четырёх changed JS modules pass;
version format, diff и local Markdown links pass. Windows WebView2 interaction не
проверялась. [Evidence](PHASE_11B3_PLAN_HISTORY_HANDOFF.md).

Phase 11B2 Plan restore and tombstone removal (2026-08-31): новый host-neutral
`PlanDocumentService` теперь владеет restore/delete: restore требует exact active
head, копирует полный body/title/status выбранной revision и добавляет linear child
с `restoredFromArtifactId`; delete требует тот же guard, добавляет `removed:true`
tombstone и очищает active pointer. Ни `artifact.remove`, ни rewrite исторических
message refs не используются. Library/list/search/prompt/checkpoint исключают removed
Plan, exact resolve/read возвращает non-retryable `resource_removed`; replay,
prune и fork сохраняют применимый tombstone. Harness `plan document:` 2/2,
`plan mode:` 2/2, Artifact Library 3/3, resource gateway 1/1 и source inclusion
1/1; web Plan 4/4 и Artifact Library 3/3; changed JS syntax, version format, diff
и local Markdown links pass. Windows WebView2/reload/fork UI не проверялись.
[Evidence](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md).

Phase 11B1 Plan exact revision guard (2026-08-31): новый host-neutral
`PlanDocumentService` владеет create/update lineage; complete Markdown больше не
проходит через `Trim`, включая UI save, и сохраняет leading/trailing/hard-break
spaces. Update требует exact active artifact id и unique contiguous linear head;
stale или broken/skipped/branched state не добавляет revision. Старый create/update
logic удалён из executor, delete временно перенесён без semantic change до 11B2.
Harness `plan document:` 1/1, `plan mode:` 2/2 и source inclusion 1/1; web Plan
2/2 и reused Artifact Library 3/3; changed JS syntax, version format, diff и 307
local Markdown targets pass. Windows WebView2/clipboard/reload не
проверялись. [Evidence](PHASE_11B1_PLAN_REVISION_GUARD.md).

Phase 11A2 Artifact Library projection (2026-08-31): новый host-neutral
`ArtifactLibraryProjectionService` строит из replayed session один revision-stamped
read-only DTO с server-owned resource class/group/display kind, exact head URI и
полной exact history. Plan/Task List группируются по logical id; HTML head выбирает
active pointer и сохраняет ancestor/alternative branch relations; immutable charts/
snapshots не схлопываются по parent. Raw artifacts остаются exact source message
cards/viewers. Client lineage/max-revision inference удалён, original/derived/vN
labels больше не зависят от extension или `Revision>1`; Plan называется Markdown,
не JSON. Direct HTML responses обновляют artifacts/library под тем же revision guard.
Harness 9/9 targeted, web 10/10 и JS syntax pass; MockDemo full self-test pass для
четырёх profiles + failed-turn persistence (три прежних CA1416 PDF warnings).
Version format, diff и 304 local Markdown link targets pass. Windows WebView2/reload/
multi-window не проверялись; Plan/HTML mutation и viewer slices не закрыты.
[Evidence](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md).

Phase 11A1 artifact commit projection (2026-08-31): после durable attachment CAS,
message/artifact linking и mandatory chat save controller синхронно отправляет full
`chatState` с monotonic `sessionRevision`, committed message, exact `ResourceRef` и
artifact revision до attachment analysis/helper и primary model transport. Catalog-
only title updates остались отдельным scope; active UI применяет full push через
существующий per-chat revision guard, background chat не перехватывает navigation.
Composer/message chips различают `Не отправлено`, `Подготовка`, `Оригинал`.
Provider failure после boundary сохраняет user turn/resource. MockDemo focused
controller case pass; bridge 1/1, web 3/3 + reused updated run-view 5/5 pass.
Version format, diff и 270 local Markdown link targets pass. Windows WebView2/reload/
multi-window не проверялись; heads/history/kinds subsequently completed in 11A2.
[Evidence](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md).

MockDemo v4 cleanup (2026-08-31): scripted responses больше не присваивают
model-owned call IDs, completion определяется по Tool Result v1 `status=ok`, а HTML
create/edit загружает exact `common.html_workspace_upsert` через
`common.capabilities_read`; удалён вызов снятого `common.html_workspace_read`.
Полный Release `--self-test`: четыре model profiles и failed-turn persistence pass;
только три существующих CA1416 PDF warnings. Production runtime не менялся.

Qualification Center requirements (2026-08-31, WQ-A0 docs-only): пользовательский запрос на встроенные расширяемые проверки оформлен в [canonical contract](../qualification.md) и [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md). Empty-chat card должна открывать отдельный wizard, а не вставлять prompt. Packs versioned/data-only, complex agent tasks идут через production runtime, pass принадлежит typed verifier evidence; dedicated qualification chat использует тот же events/CAS и causal journal. Первый pack — Excel WQ0 с in-app VSTO/native observations и narrow independent-client helper. PowerShell остаётся временным engineering fallback. Код/UI/helper не менялись; WQ0/5B2/R04 не закрыты. Docs diff/205 local links/anchors в затронутых документах и pre-commit `ValidateVersionFormat` — pass; build/tests не запускались.

WQ-A1 host-neutral core (2026-08-31): добавлены strict schema v1 pack parser,
coverage registry/catalog, конечный runner через narrow allowlisted action/verifier
ports, verifier-only automatic pass, cleanup/cancellation и fail-closed replay без
auto-retry. Четыре closed qualification event operations — Agent authority/mandatory;
step-start предшествует action, большие expected/actual используют тот же chat CAS.
Typed bridge DTO добавлены без controller route/UI. `qualification:` 8/8,
`storage: typed event port` 1/1, production source inclusion 1/1; Harness Release и
MockDemo Release compile без errors (только существующие platform warnings).
ValidateVersionFormat, diff и local links pass. Полный harness, Windows/Office/VSTO,
WebView/live provider не запускались. WQ-A2/A3, WQ0/5B2/R04 открыты.
[Evidence](WQ_A1_QUALIFICATION_CORE.md).

WQ-A2 Qualification Center shell (2026-08-31): добавлены exact embedded
coverage/pack `WQ-A2.shell`/`common.ui-shell`, application service, typed
controller/bridge routes и один WebView wizard из empty chat и Diagnostics.
Run живёт в dedicated document chat, восстанавливается из того же
validated event stream без index, а ordinary conversation turn в нём fail-closed.
Typed shell verifier сверяет только persisted preflight/manual evidence;
UI показывает server-owned status, shared JSON viewer, exact run journal и
bounded report. `qualification:` 10/10, web qualification center 5/5, Harness и
MockDemo Release build без errors; MockDemo restart round-trip и browser preview pass.
Windows/Office/VSTO, COM, live provider/model и full suites не проверялись;
WQ-A3/A4/A5, WQ0/5B2/R04 открыты. [Evidence](WQ_A2_QUALIFICATION_CENTER.md).

WQ-A3 Excel WQ0 implementation (2026-08-31): identity decoder, retained marshal
lease и native HWND resolver перенесены из diagnostic test project в единственный
`OfficeHosts.Qualification` owner. Добавлены exact `IQualificationHostPort`,
UI-thread/dedicated-STA forwarding, embedded release pack `excel.wq0.identity` и
runner-owned fixture matrix: independent client A/B, switch, Save As, second window,
detach/attach C, close/reopen new lifetime, same-name workbook in another process,
typed verifier и cleanup. Narrow x64 helper принимает только bounded
bind/list/observe/release messages по one-time nonce named pipe, explicit HWND/index
и сверяет same owner assembly MVID; no network/shell/custom command. PowerShell
fallback переключён на тот же owner, duplicate sources/project удалены. Host-neutral
checks: `qualification:` 11/11, `excel identity probe:` 5/5, WebView qualification
5/5. OfficeHosts/helper/VSTO и реальный COM/WQ0 на Windows не запускались; поэтому
5B2/7D/R04/WQ-SESSION остаются открыты. [Evidence](WQ_A3_EXCEL_WQ0.md).

WQ-A4 suite catalog (2026-08-31): embedded eight canonical family manifests for
common/provider/storage/UI/Excel/VBA/cross checks. Each manifest pins revision and
content hash, exact readiness capability, finite data-only actions, required typed
final-state assertion and runner-owned fixture/cleanup where mutation is possible.
Coverage registry names every mandatory Excel quick/full/release scenario owner.
No adapter capability is inferred from manifest presence: unsupported packs remain
N/A and cannot start. `qualification:` 12/12 pass; production resource inclusion,
diff/link and version checks are recorded with the commit. Live provider,
AgentTask, Windows/Office/VBE/WebView/restart and host final-state evidence were not
run. [Evidence](WQ_A4_SUITE_CATALOG.md).

WQ-A5 exact-build release evidence (2026-08-31): added strict bounded detached
RS256 envelope/payload, candidate certificate pin, assembly metadata identity,
catalog fingerprint, artifact hashes and full 19-run matrix admission. The
application exposes evidence status/provenance and the read-only
`release.candidate` capability only for a complete exact manifest; run events pin its
SHA-256. Release flow is two-stage: preparation commits without tag, finalization
checks tracked version, exact commit/signature/evidence and creates a tag only after
explicit Windows/pack acknowledgements. Host-neutral `qualification:` 14/14 pass;
versioning 6/6, source inclusion 1/1, Web UI 5/5 and MockDemo Release compile pass.
PowerShell,
certificate store, Office/VSTO and real signed Windows evidence remain unverified.
Pre-A5 qualification chats require explicit reset because their events lack evidence
provenance. [Evidence](WQ_A5_BUILD_EVIDENCE.md).

Artifact Library target (2026-08-31, отдельный user-requested docs-only contract):
[canonical spec](../artifact-library.md) различает non-durable draft, committed exact
resource, immutable original/snapshot, versioned Plan/HTML/authored document и
derived artifact. После mandatory CAS/link/save application ставит monotonic full
projection в WebView очередь до первого model transport без ожидания UI ack; failure
после commit не откатывает resource. Зафиксированы pinned message revisions,
head/history UX, domain-owned edit/delete, append-only removal/GC, inert uploaded
HTML, bounded text/Markdown/image/PDF/audio viewers и context-on-demand через
действующий Resource Gateway. Master Phase 11 и R51 обновлены; runtime/UI/vendor не
менялись и Phase 11 не начата. `git diff --check` и 256 local Markdown links в восьми
затронутых документах — pass; build/runtime tests для docs-only изменения не запускались.

Host Fabric and Local Automation target (2026-08-31, отдельный user-requested
docs-only contract): [Host Fabric](../host-fabric.md) фиксирует одно окно для
нескольких Office processes через ephemeral endpoint registry, exact immutable run
target и owner-STA execution без cross-process COM/ROT fallback. `NativeHostCli`
остаётся in-process DLL; скрытый dedicated Excel launcher допустим только как signed
interactive profile, не как security isolation или обход corporate policy.
[Local Automation Agent](../local-automation-agent.md) разделён на workspace/session
ADR, read-only files, guarded mutations, Browser, typed process runner, optional raw
shell/terminal и desktop control. File mutations/processes требуют отдельного signed
isolated worker и deny-by-default grants. Master Phase 11 и R52/R53 обновлены; код,
tests и current WQ-A1 next step не менялись. `git diff --check` и 207 local links
в шести затронутых Markdown files — pass; runtime/build tests не запускались.

Skill Library target (2026-08-31, отдельный user-requested docs-only contract):
[canonical spec](../skills.md) отделяет installed trusted global/host package от
chat-owned artifact. Upload остаётся untrusted immutable resource до explicit
confirmed install; current exact capability read не меняется. Для custom skills
заданы package-level immutable core+references revisions, append-only history,
restore-as-new-head, tombstone, provenance/import/export, conflict guard и UI-only
links из tool results в Library. Built-ins immutable; selected Host Fabric endpoint
владеет host scope. Master Phase 11 и R54 обновлены; runtime/UI/store не менялись,
WQ-A2 остаётся следующим шагом. `git diff --check` и 233 local links в восьми
затронутых Markdown files — pass; runtime/build tests не запускались.

Phase 10D final architecture audit (2026-08-31): final inventory is 107 Core,
173 Office and 15 OfficeHosts C# files. Replaced source paths/aliases are absent;
all production sources are included by the old-style projects. Forty-nine literal
source references in canonical architecture instructions resolve. The only current
`LegacyToolDefinitionAdapter` call sites are `ToolPackSnapshotFactory` for
`Adapt`/`BindingFor` and `ConversationProtocolContext` for `PolicyFor`; resource files
have none. The audit corrected stale VBA adapter rows that still named completed
Phase 8 as a future removal gate: 5B2 owns document binding, while optional direct
handler/typed-host cleanup is Phase 11 and does not block stable core. Architecture
4/4 and production source inclusion 1/1 pass. Windows gates remain not performed.
[Evidence](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md).

Phase 10C2 resource projection cleanup (2026-08-31): four controller-facing
`common.resources_*` definitions now use `ControllerToolDefinition` and preserve the
exact native handler descriptor, JSON schema and source-owned `ToolPolicy` instance.
The only removed member is `LegacyToolDefinitionAdapter.ProjectRead`; its active
`Adapt`/`PolicyFor`/`BindingFor` consumers remain unchanged. A boundary check rejects
future resource-file dependencies on the legacy execution adapter. Native execution,
bindings, callable ToolPack, model wire and mode policy are unchanged. Focused resource
projection/manual+model 1/1, hard-cutover resource 1/1 and architecture 4/4 pass;
Windows WQ-PACK remains open. Next atomic step is only final audit 10D.
[Evidence](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md).

Phase 10 local Windows build entrypoint (2026-08-31): root `build-local.cmd`
находит штатный VS 2022 MSBuild через `vswhere`; без аргументов последовательно
собирает managed dependencies один раз, NativeHostCli x64/Win32 и публикует оба
portable каталога. `x64`, `x86`, `desktop`, `all`, `doctor` остаются явными
вариантами; configuration фиксирована в `Release`. Packaging перенесён в
declarative MSBuild tasks без PowerShell, install/sign/register/network/process
termination и без удаления destination. Три заменённых native publish `.ps1`
удалены; четыре прежних PowerShell wrapper `.cmd` больше не меняют execution
policy. x86 output явно предупреждает об отсутствии x64-only PDF native runtime.
На этой машине выполнены только XML/static/diff/version checks; Windows MSBuild/C++/CLI,
Office PIA, portable contents и запуск не проверялись и остаются Windows gate.
Следующий шаг был позднее выполнен в 10C2 resource projection cleanup.

Phase 10C1 application façade move (2026-08-31): byte-identical
`AssistantRuntime.cs` moved with `git mv` from `Office/Runtime` to root `Office`;
the production old-style include switched and a physical-owner assertion prevents
return to document/tool Runtime. Namespace, controller/pane lifecycle, disposal,
factories and all consumers are unchanged. Harness intentionally uses a controller
stub and had no façade source-link to rewrite. Architecture 4/4 and production source
inclusion 1/1 pass; real Office/VSTO/WebView lifetime remains a Windows gate. The
resource projection half was later completed in 10C2.
[Evidence](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md).

Phase 10B2 VBA host backend move (2026-08-31): both `VbaProjectSupport` partials
moved with `git mv` from `Office/Vba` to `OfficeHosts/Vba`; namespace, old-style
projects, three host adapters and two harness consumers switched. Old paths/includes,
aliases and Office consumers are absent. COM/VBE and guard algorithms are unchanged.
The move exposed a source-linked-harness blind spot: internal
`VbaPackageOwnershipMarker` was inaccessible across production assemblies. It is now
an explicit public read-only Office.Vba parser contract with private constructor;
no duplicate parser or broad friend assembly was added. Connected COM 47/47,
UserForm helper 1/1, exact package guard 1/1, architecture 4/4 and source inclusion
1/1 pass. R49 fixed host-neutral; Windows OfficeHosts/VSTO/VBE remains open. The
application-façade half was later completed in 10C1.
[Evidence](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md).

Phase 10B1 host document identity move (2026-08-31): `DocumentIdentity.cs` moved
with `git mv` from `Office/Runtime` to `OfficeHosts/Identity`; namespace, both
old-style projects, three host adapters and source-linked harness consumer switched.
The algorithm differs only by namespace. Old source/include, alias and Office
consumer are absent; the boundary-test now rejects any future Office dependency.
Documents/identity 4/4, architecture 4/4 and production source inclusion 1/1 pass.
OfficeHosts/VSTO/real Office were not validated on this machine; WQ0 and Windows
compile remain open. At that point R49 still covered the two `VbaProjectSupport`
partials; 10B2 later moved them and closed R49 host-neutral.
[Evidence](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md).

Phase 10A physical/dependency audit (2026-08-31): inventory 107 Core, 176 Office
и 12 OfficeHosts C# files. Folder/namespace mismatches 0/27/5 не трактуются как
автоматические defects: root Office namespace остаётся у façade/host ports. Реальный
R49 scope на момент аудита — `DocumentIdentity.cs` и два `VbaProjectSupport`
partials без Office service/tool/domain consumers; первый файл позднее перенесён в
10B1, а оба partials — в 10B2. `AssistantRuntime` и resource-only
`ProjectRead` зафиксированы отдельными 10C cleanup invariants; projection позднее
перенесена, а method удалён в 10C2.

Новый `architecture: mandatory dependency direction` проверяет Core.Agent,
ModelProtocol, VBA, resources, OfficeHosts и UI/bridge; вместе с существующими
architecture cases — 4/4 pass. Production old-style source inclusion — 1/1 pass.
Superseded `Core/Tools/AgentResponseParser.cs` удалён из canonical architecture map;
actual v4 parser указан в ModelProtocol. Runtime не менялся, Office/VSTO/WebView не
проверялись. `ValidateVersionFormat`, canonical source paths, diff check и 232 local
links в 10 changed Markdown files — pass. [Evidence](PHASE_10A_BOUNDARY_AUDIT.md).

Deferred Windows qualification mode (2026-08-29, docs-only decision): пользователь
разрешил не ждать регулярных Windows прогонов между dependency-safe подэтапами
обязательного маршрута. Каждый slice по-прежнему закрывает targeted host-neutral tests,
cleanup и отдельный commit; статус до реального прогона — только `done host-neutral`.
Накопленные COM/VSTO/WebView/live-provider scenarios сведены в
[Windows qualification runbook](WINDOWS_QUALIFICATION_RUNBOOK.md) и обязательный
Milestone WQ перед Phase 12. На дату решения WQ0 Excel identity probe считался
prerequisite production factory switch; это условие отменено принятым риском
2026-08-31, а сам WQ0 сохранён обязательным release evidence. Непроверенный build —
`16.1.0-dev` qualification candidate, не stable/beta/RC. 9C UI и Phase 6C–6G
mutation slices выполнены отдельными host-neutral changes; 6H зафиксировал scope,
6I package runtime/R41 и 6J rename/R42 выполнены host-neutral; 7A audit и 7B typed
Excel reads и verified `write_range` завершены host-neutral; 7D позже объединён с
accepted-risk atomic 11T0/5B2 без ожидания WQ0, а
Phase 8A immutable execution snapshot, 8B callable lifecycle/admission, 8C durable
reconstruction и 8D resource data-plane cutover завершены host-neutral; 9D1 audit,
9D2 same-process fail-stop reload/reconciliation, 9D3 typed event
classification/`IEventStore`, 9D4 minimal `IConversationStore` и 9D5 immutable
`RunViewState` завершены host-neutral; 10A audit, 10B1/10B2 host moves и 10C1 façade
move, resource projection 10C2 и final audit 10D также завершены; следующий этап —
Windows Milestone WQ. Текущий следующий runtime step уточнён в заголовке документа.
Windows WQ-UI/VBE/Excel не считаются
закрытыми локальными проверками.

Phase 9D5 immutable run view projection (2026-08-30): один Core
`RunViewStateProjector` строит immutable UI state из authoritative `KernelState` и
source-owned `ToolExecutionEvidence`. Narrative, lifecycle, health, successful
reads, verified change/no-change, unverified writes, failed calls, unknown effects
и exact pending confirmation больше не собираются в JS из model status/prose и
разрозненных Activity полей. Legacy successful mutation без verification остаётся
unverified+unknown; несовместимая evidence не может завысить verified count.

Application result, Init/ChatState/SendChat bridge, chat headers/catalog и Agent/
message/approval UI switched atomically. Full responses несут session revision;
per-chat UI guard отвергает late detail и не даёт stale catalog заменить более
новый summary, а existing event revision CAS остаётся cross-window write authority.
Все switched JS/CSS получили один cache key, поэтому WebView не смешивает новый
bridge с cached flat-status readers.
`RunExecutionSummary`, getter projection, message/run/bridge fields, current catalog
overlay и model-status UI branches удалены; старые JSON fields только игнорируются,
без hidden migration/fallback. Stream/CAS/schema/ports не менялись.

99 distinct targeted harness cases и 70/70 web cases pass; MockDemo actual-controller
compile — 0 errors / 3 existing CA1416 PDF warnings. `ValidateVersionFormat`, diff
check и 249 local links in 11 changed Markdown files — pass.
[Evidence](PHASE_9D5_RUN_VIEW_STATE.md).
Windows controller/WebView/reload/confirmation/live-append/clipboard/multi-window
qualification открыт; Phase 9 имеет статус только done host-neutral.

Phase 9D4 conversation projection boundary (2026-08-30): один
`ChatConversationStoreAdapter` реализует минимальный `IConversationStore` над тем же
`ChatStore`, который остаётся единственным владельцем hash-linked stream, revision
CAS и blobs. `ChatSessionService`, `ConversationRunService`, kernel store adapter и
controller create/load/save/list/active/move/delete paths switched together.
Interruption recovery выражен одной intent operation: storage закрывает прежнюю
open-step boundary и возвращает retained open-tool evidence, а application по-прежнему
владеет recovery policy и финальным save.

Artifact hydration, HTML revision activation, raw events/payloads, reducers и CAS
maintenance в port не вошли. Replaced public conversation methods internalized;
compatibility overload/fallback, второй store, writable snapshot, schema/replay
change и dual-write отсутствуют. 29 distinct targeted cases pass; MockDemo
actual-controller compile — 0 errors / 3 existing CA1416 PDF warnings;
`ValidateVersionFormat`, diff check и 216 local links в 10 changed Markdown files — pass.
[Evidence](PHASE_9D4_CONVERSATION_STORE.md). Windows controller/restart/multi-window
qualification открыт; 9D5 UI projection не начат.

Phase 9D3 typed event store (2026-08-30): closed `SessionEventKind` catalog теперь
классифицирует каждый current top-level chat event по Agent/Domain Diagnostic lane,
authority/diagnostic meaning, mandatory/best-effort durability и storage-internal/
event-port write scope. Один `ChatEventStoreAdapter` делегирует прежнему `ChatStore`;
JSONL/CAS/schema/type strings/correlation/lifecycle не менялись. `session.*`,
`turn.*`, `step.*` нельзя append-ить через port. Materialized request и accepted
ToolPack extension — mandatory authority; rejected attempts и current transport
terminal/chunk evidence — mandatory diagnostics; accepted trace marker и causal
observations — best effort. Accepted response/calls/results остаются canonical
`session.commit`.

Все active Office model trace, ToolPack, causal trace и controller diagnostics
writers/readers переключены вместе. Direct `AppendTrace*`, broad event reads и
writable arbitrary causal `Stage` в Office удалены; replaced raw `ChatStore` event
API internalized. Core storage/recovery и
`ITrajectoryQuery` сохранили свои владельцы. 24 distinct targeted cases pass;
MockDemo actual-controller compile — 0 errors / 3 existing CA1416 PDF warnings.
`ValidateVersionFormat`, diff check и 208 local links в 9 changed Markdown files —
pass.
[Evidence](PHASE_9D3_TYPED_EVENT_STORE.md). Windows controller/WebView persistence
qualification открыт; 9D4 conversation port и 9D5 UI projection не начаты.

Phase 9D2 same-process run-store recovery (2026-08-30): оба Agent controller path
теперь отдельно ловят `RunStoreException`, закрывают stale causal scope, освобождают
`ChatRunLease` и только затем вызывают targeted reload/reconciliation на общем
`ChatSessionService`. Сам service reload-ит exact stream, повторно берёт recovery
lease, под ним заменяет active cache canonical projection и не читает/не сохраняет
`UnpersistedSummary`. Durable confirmation до failed
claim остаётся pending; open possible dispatch становится unknown; saved terminal
остаётся known. Recovery CAS conflict не retry-ится, а повторный вызов idempotent.

Fault matrix start/confirmation × before/after dispatch — 4/4 pass; existing startup
unknown/saved-boundary recovery — 2/2; production source inclusion — 1/1, итого 7
distinct targeted cases. MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings; version format и 177 local links в 7 changed Markdown files —
pass. Старые escape-only catch filters удалены, второй recovery/store не добавлен.
[Evidence](PHASE_9D2_RUNSTORE_RECOVERY.md). Windows controller/WebView
qualification открыт.

Phase 9D1 persistence audit (2026-08-30): targeted source/call-site review подтвердил,
что один `*.events.jsonl` + CAS остаётся единственной chat authority, а действующий
`IRunStore` уже обеспечивает accepted/start-before-effect, terminal-before-next-step,
private invocation cursor/global CAS и no automatic retry. Existing harness покрывает
normal/error/unknown/pending/cancel replay, stale confirmation, result append failure
после write, restart recovery, CAS orphan/fail-closed GC и queued stream terminal
barrier. Эти проверки переиспользованы как evidence; runtime и tests не менялись.

Не закрыты три отдельные архитектурные границы: generic `ChatStore.AppendTrace(string)`
не типизирует authority/diagnostic и mandatory/best-effort; session/controller
consumers по-прежнему зависят от concrete broad `ChatStore`; UI не получает один
immutable `RunViewState`. Дополнительно R45 фиксирует safety/UX gap: при
`RunStoreException` controller правильно не пишет выдуманный terminal и освобождает
run lease, но current process не вызывает single-chat canonical reconciliation, так
что open dispatch может оставаться визуально running до restart. Ordered slices:
9D2 recovery, 9D3 typed event port, 9D4 conversation port, 9D5 RunViewState.
[Evidence](PHASE_9D1_PERSISTENCE_AUDIT.md). Windows gates не закрыты.

Phase 8A immutable ToolPack snapshot (2026-08-30): после окончательного run
filtering один typed `ToolPackSnapshot` копирует и проверяет registration каждого
runnable id. Registration revision включает canonical descriptor/schema, полный
`ToolPolicy`, handler/entry point/scope/host и hash package implementation; pack revision
дополнительно связывает mode, host и ordered membership. Native runtime регистрирует
эту authority напрямую, legacy `Describe` читает её же и перед dispatch повторно
сверяет compatibility definition. Drift возвращает `tool_registration_changed` без
effect; confirmation rebuild сравнивает новый revision с persisted accepted policy.

Старый ad-hoc fingerprint helper удалён. R22 закрыт актуальными counts Excel 15,
Word 9, PowerPoint 9, Outlook 5. Два stale fixtures приведены к уже действующим
`read_range.address` и status-free v4 без product change. 90 distinct host-neutral
cases pass; MockDemo: 0 errors / 3 existing CA1416 warnings; version format, diff и
194 local links в 10 changed Markdown files — pass. В самом 8A LRU, core-pack
selection, atomic admission, compaction/events и remaining resource handlers не
переключались; core/admission позднее закрыты 8B, остальные gates открыты. [Evidence](PHASE_8A_TOOL_PACK_SNAPSHOT.md),
[decision](../decisions/ADR-0006-tool-pack-snapshot.md).

Phase 8B callable ToolPack (2026-08-30): `ProgressiveToolWorkingSet`, `Touch` и
`TOOL_WORKING_SET` удалены одним cutover. `CallableToolPack` пересекает конечные
exact-ID profiles с уже отфильтрованным run catalog: Agent/Excel получает все 15
Excel и 5 public VBA schemas, Word/PowerPoint — доступный VBA core, Chat — четыре
read-only resource tools, Plan — bootstrap discovery/resources. Optional schemas
staged из complete current-revision/current-run evidence и публикуются всей пачкой
только в `EndResponse`, с новой callable revision. Полный prospective request
включает history/media, response schema, output/safety budget и bounded format-repair
reserve; overflow сохраняет прежний pack, не публикует ни одной новой schema и
возвращает видимый `TOOL_PACK_STATE`. Tool execution больше не меняет membership.

Prompt schema 15 заменяет eviction guidance; schema14 и остальные сохранённые custom
prompts не переписываются и требуют explicit review/reset. Capability read теперь
различает complete descriptor evidence и step-boundary admission. Dynamic registry,
execution snapshot 8A, Tool Result/ResourceRef wire, AgentKernel, compaction algorithm/events
и resource handlers не менялись; compaction notice только требует новый admission.
Durable extension event и rematerialization при confirmation/
compaction/crash остаются 8C; до них reconstruction требует нового exact read/admission,
а raw evidence не считается решением admission. Targeted
host-neutral verification: 92 distinct cases (tool pack 5, Agent 34, model protocol
15, settings 5, context inspector 3, Plan 2, Chat 13, conversation v4 13, typed
settings bridge 1, production includes 1) — pass. Harness compile: 0 errors / 4
existing CA1416 identity-probe warnings; MockDemo actual-controller compile: 0 errors /
3 existing CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 214 local links в 14 changed Markdown
files — pass. Product `16.1.0-dev`; tag/release/push не выполнялись. Полная
[evidence](PHASE_8B_CALLABLE_TOOL_PACK.md); Windows WQ-PACK открыт.

Phase 8C durable ToolPack reconstruction (2026-08-30): `ToolPackAdmissionJournal`
пишет `tool_pack.extension.accepted/rejected` в тот же hash-chained chat event stream
до изменения callable membership. Accepted v1 data pin-ит exact requested ID/revision
delta и before/after snapshot revisions; rejected record остаётся diagnostic-only.
Reconstruction проигрывает ordered accepted chain exact logical `TurnId`, поэтому переживает
новый runtime `RunId`, confirmation, compaction и restart, но не переносится в новый
user turn. Raw `TOOL_RESULT` никогда не становится authority. Exact descriptor/profile
drift/broken chain атомарно оставляет core до accepted core rebase и добавляет видимый `TOOL_PACK_RESTORE_STATE`, не скрывая
уже известный confirmed terminal result; новый exact read может создать свежий event.
Append failure не публикует pack и не отправляет следующий model request. Prompt schema
16 заменяет временное re-read-after-reconstruction указание; schema15/custom text не
переписываются без explicit review/reset. AgentKernel, execution snapshot, tool result/
ResourceRef wire, compaction algorithm и resource handlers не менялись. [Evidence](PHASE_8C_TOOL_PACK_EVENTS.md).
Targeted host-neutral verification: 50 distinct cases (Agent 34, ToolPack 6,
settings 5, canonical event log/HMAC/encrypted history/shared trace stream 4,
production includes 1) — pass. Harness compile: 0 errors / 4 existing CA1416
identity-probe warnings; MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 230 local links в 15 changed Markdown
files — pass. Product `16.1.0-dev`; tag/release/push не выполнялись.

Phase 8D resource data plane (2026-08-30): четыре exact
`common.resources_list/resolve/search/read` registrations теперь имеют source-owned
typed descriptors, read-only policies, bindings и handlers. Agent/Chat/manual paths
исполняют их через один `ToolRuntime` и существующий `ResourceGatewayService`; каждый
call создаёт `DocumentAccessGate` operation root, а live Office/VBA access через
`HostRuntime` сохраняет same-operation reentry и serialization с mutation. Старый `ResourceToolExecutor`,
его switch branch и source/test references удалены. Оставшийся
`ResourceToolCatalog` только проецирует definitions текущим mixed catalog consumers
и не исполняет вызовы.

Typed result сохраняет bounded JSON и exact `ResourceRef`. Media bytes передаются
только через request-local materialization keyed by runtime call ID, немедленно
consumed следующим model step и не становятся durable CAS/content_ref transport.
Native UI projection сохраняет provider `retryable` metadata. URI/revision/cursors,
CAS/providers, ToolPack admission/events и AgentKernel не менялись. R30 закрыт
host-neutral; [ADR-0004](../decisions/ADR-0004-resource-data-plane.md) принят.

74 distinct targeted cases pass: resources 8, Agent 34, Chat 13, ToolRuntime 14,
native resource replay 1, bounded VBA resource 1, VBA/document gate serialization 2,
production source inclusion 1. Harness compile: 0 errors / 4 existing CA1416
identity-probe warnings; MockDemo actual-controller compile: 0 errors / 3 existing
CA1416 PDF warnings. Full harness и Office/VSTO не запускались. Final
`ValidateVersionFormat`, `git diff --check` и 222 local links в 14 changed Markdown
files pass; product `16.1.0-dev`, release/tag/push не выполнялись. Windows
live-provider/manual/media gate остаётся WQ-PACK.
[Evidence](PHASE_8D_RESOURCE_DATA_PLANE.md).

Phase 7B typed Excel reads (2026-08-30): host-neutral `ExcelReadService`
владеет canonical inspect/range outcomes, profile и second-bound validation.
`NativeToolRuntimeAdapter` регистрирует exact `excel.inspect`/`excel.read_range`
только вместе с handlers; manual/model paths входят в `HostRuntime` с exact chat
expectation. `ExcelAdapter` больше не исполняет public read ids и принимает только
две internal compatibility команды до 7D. HTML bind/refresh вызывает тот же adapter
под уже открытым access. Collections ограничены 200 items/100 chart series;
100000-cell ceiling проверяется host backend до `Value2`/`Formula`; defined names
не читают `RefersToRange.Value2`. 26 distinct focused Excel/native/HTML/
HostRuntime regression cases проходят; real Excel COM, protected sheet и production
factory/identity остаются Windows gates. [Evidence](PHASE_7B_EXCEL_READ.md).

Phase 7C verified Excel write (2026-08-30): host-neutral `ExcelWriteService`
владеет scalar/formula/table normalization, exact target, before/read-back и
typed effect evidence. `excel.write_range` зарегистрирован exact native handler с
`ToolVerification.Tool`; Agent/manual используют один `HostRuntime` scope, а dry-run
не входит в handler. Совпавший before даёт `VerifiedNoChange` без dispatch;
совпавший read-back — `VerifiedChange`; отказ до host boundary — `error`, а throw,
cancellation, unreadable/divergent state после boundary — non-retryable `unknown`.

Ragged tables null-pad детерминированно; 100000-cell/dimension bounds проверяются до
COM matrix allocation/assignment. Public host case и четыре старых write helpers
удалены. Временные read/apply internal commands и current resolver явно остаются до
atomic 11T0/7D; прежний WQ0 prerequisite отменён решением 2026-08-31. 15 distinct
focused write/read/catalog/HTML/source checks pass;
MockDemo compile — 0 errors / 3 existing CA1416. Real Excel formula/value
normalization, mixed formulas, protected sheets, COM fault timing, close/switch и
production factories остаются WQ-EXCEL. Version/diff checks pass; 176 local links
in 8 changed Markdown files have 0 missing targets. [Evidence](PHASE_7C_EXCEL_WRITE.md).

Phase 9C causal run journal UI (2026-08-29): Diagnostics primary view теперь
показывает latest/exact run как один chronological `run-causal` поток; completed
Agent run и failed activity имеют direct action. Bounded summary считает unique
`ToolCallId`, typed failure/unknown/interruption statuses не теряются; filters,
expansion и scroll остаются UI state. Row details lazy-mount общий lossless JSON
viewer, exact source IDs видны, а raw range/CAS payload остаются у existing
Diagnostics owner. Компонент не читает bridge/CAS/network/storage, не вводит durable
index и не использует prose для effect. 12 web test files / 65 internal cases,
syntax/diff checks pass; unchanged Phase 9A 17 Core/bridge cases reused. Chrome
`file://` DOM probe: 12 rows, 1 problem, 2 lazy viewers, component network/data-driven
active elements/page overflow = 0; dark theme/clipboard/keyboard/DPI и real WebView2
не проверены. [Evidence](PHASE_9C_RUN_JOURNAL_UI.md). R28 и full Phase 9 persistence
matrix открыты; R37 read-only adapter сохраняется до Windows/reset decision.

Phase 9B4 compact diff vendor gate (2026-08-29): Diff2Html 3.4.56 не добавлен.
Единственные consumers — VBA editor preview и hydrated mutation detail — передают
exact `before/after` в `RNAssistantVbaDiff.format`; bridge DTO также содержит только
эти source texts/hashes. Текущий formatter сам вычисляет bounded single-change
projection и не создаёт authoritative unified diff. Diff2Html является parser/
renderer готового unified/git diff, поэтому его подключение потребовало бы второго
diff algorithm либо выдало бы синтетическую проекцию за source evidence. Existing
formatter и CSS не менялись, vendor manifest остаётся 38 files. Повторно оценивать
только после отдельного source-owned bounded unified-diff contract. [Evidence](R39_DIFF_VENDOR_GATE.md).
9C выполнен отдельным последующим UI commit; Windows VBA/WebView qualification остаётся открытой.

Phase 9B3 bounded tree vendor switch (2026-08-29): actual `file://` probe
официального Web Awesome Tree 3.12.0 ESM graph не зарегистрировал `wa-tree`; custom
bundle и C#/WebView virtual-host switch не вводились. Вместо этого pinned
Wunderbaum 0.14.1 UMD/CSS (zero npm dependencies) прошёл file-origin probe и
подключён через новый UI-only `TreeAdapter` к одному HTML workspace/artifact tree.
Adapter допускает только bounded local arrays (consumer 1,800 nodes/12 levels;
hard 2,500/16), stable typed keys, local icons и owner callbacks; URL/lazy,
edit/DnD/filter/grid/persistence API не опубликованы. Старый renderer этого consumer
и его dead CSS удалены; search/group/collapse/select/delete ownership сохранён.
Manifest расширен с 36 до 38 runtime files с exact version/git head/npm integrity,
bytes/SHA-256 и local MIT license. Targeted tree 4/4, vendor gate 5/5 и все
`tests/web/*.test.js` 58/58 pass. Local Chrome
`file://`: 19 rows, keyboard active descendant/ARIA/themes pass, malicious title
остаётся text, horizontal overflow и network calls 0. [Evidence](R38_TREE_VENDOR_SWITCH.md).
Windows WebView2 keyboard/focus/DPI/lifecycle gate открыт; workers не используются,
allowlist остаётся пустым и CSP сохраняет `worker-src 'none'`.

R36 web vendor gate (2026-08-29): добавлен exact allowlist для 36 существующих
runtime files: версии/git heads/npm integrity, bytes/SHA-256, local licenses и
transitive browser decisions. Закрыты реальные gaps: KaTeX CSS больше не ссылается
на отсутствующие 20 WOFF + 20 TTF и использует только 20 manifested WOFF2; Feather
Icons зафиксирован как source-only provenance существующих inline SVG без runtime
package. Main UI явно держит `connect-src 'none'`, `font-src 'self'` и при пустом
allowlist `worker-src 'none'`; локальные workers разрешены только после manifested
factory/cancel/terminate/CSP slice. Vendor gate 5/5 и existing web regression 49/49;
local Chromium загрузил 7
existing globals, remote/failed requests и page errors 0. [Evidence](R36_WEB_VENDOR_GATE.md).
Новый vendor/UI consumer не подключался; Windows WebView2 gate открыт.

Phase 9B2B4 Markdown JSON switch (2026-08-29): только закрытые top-level fenced
blocks с exact language `json` в завершённых persisted/Agent/diagnostic сообщениях
заменяются post-sanitize на общий viewer. Exact fenced body остаётся источником raw
copy и сохраняет CRLF, duplicate keys и numeric lexemes; DOM/source mismatch
fail-safe остаётся обычным code block, content sniffing нет. Viewer collapsed/lazy,
уничтожается при collapse и перед message re-render. Незакрытый fence и каждый live
stream delta не парсятся как JSON; обычные code blocks, prompt/skill/plan preview и
Markdown transport не менялись. Новый UI test 8/8; прежние 27 JSON adapter/consumer
cases переиспользованы при неизменных inputs, итого 35. Local Chrome: light
1000×820 и dark responsive 560×820, horizontal overflow 0, malicious JSON не создал
script/image nodes, remote requests 0; fixture/screenshots удалены. `node --check`,
local links, diff check и `ValidateVersionFormat` — pass. Windows WebView2/clipboard
и R28 live-provider gate открыты.

Phase 9B2B3 Artifact JSON switch (2026-08-29): artifact detail больше не
выполняет lossy `JSON.parse → JSON.stringify → pre`. Exact inline JSON и exact
`MetadataJson` передаются общему viewer; `InlineTruncated` из bounded bridge DTO
становится completeness=`preview`, поэтому обрезанный JSON остаётся точным raw
фрагментом с явной ошибкой позиции, без repair. JSON определяется по typed MIME
(`application/json`/`+json`) либо explicit `json`/`data` kind; text/plain не
угадывается по содержимому и остаётся inert `textContent`, при truncation получает
явную preview-метку. Re-render/selection switch вызывает registry unmount до удаления
DOM. Новый integration UI test 5/5 pass. Local Chromium: light 1100×820, dark
responsive 640×820, horizontal overflow 0; fixture/screenshots удалены. HTML
preview/editor, plan Markdown и artifact transport не менялись. JsonAdapter 7/7
повторно pass; прежние 15 consumer cases переиспользованы при неизменных inputs,
итого 27. `node --check`, 88 local links, diff check и `ValidateVersionFormat` —
pass. Windows gate открыт.

Phase 9B2B2 Context/Tools/VBA JSON switch (2026-08-29): exact materialized
request JSON в context inspector использует общий viewer и передаёт `RawTruncated`
как completeness=`preview`, без repair; закрытие inspector/details уничтожает tree DOM.
Context manager показывает явно сериализованную UI state projection и монтирует её
только при открытых manager/details: это не выдаётся за exact wire. Manual tool
run/package result передаётся как structured UI projection, loading/error остаются
inert text; editable schema/arguments не менялись. Исправлен прежний порядок
install/uninstall, где `renderEditor()` стирал только что показанный result: теперь
result монтируется после refresh. VBA info metadata использует тот же viewer,
VBA read/edit/dispatch не менялись. Новый integration UI test 6/6 pass; plain JSON
`pre`/copy paths четырёх surfaces и последний dead `.json-box` selector удалены.
Вместе с JsonAdapter/Agent/diagnostics — 22/22; completion/prompt/tools regression —
14/14. `node --check`, 88 local links, diff check и `ValidateVersionFormat` — pass.
Local Chromium fixture: light 1280×900 и dark responsive 720×1100, horizontal
overflow 0; fixture/screenshots удалены. Windows WebView2 layout/clipboard gate открыт.

Phase 9B2B1 Agent JSON switch (2026-08-29): раскрываемые tool arguments и result
data теперь лениво mount/unmount общий lossless viewer. Удалены второй generic
`JSON.parse → object/table/list`, pretty-copy/raw-pre path и его dead CSS; collapsed
activity не удерживает tree DOM. Chart artifact parsing, который скрыто зависел от
глобального `tryParseJson` из Agent renderer, получил собственный domain-local parser;
chart transport/edit semantics не менялись. Общий `copyTextResult` теперь возвращает
Promise с реальным clipboard/fallback outcome; прежний fire-and-forget `copyText`
сохранён для существующих non-viewer callers, diagnostics/Agent adapters получают
ошибку копирования через callback. Новый Agent integration test 4/4, diagnostics
5/5 и JsonAdapter 7/7 pass; chart invalid/non-chart owner cases включены. Старый
Agent renderer/classes и cross-owner parser references отсутствуют. Реальный
WebView2/message layout/clipboard остаётся Windows gate. Regression UI:
completion 8/8, prompt review 5/5, tools editor 1/1; `node --check`, 88 local
Markdown links, `git diff --check` и `ValidateVersionFormat` — pass.

Phase 9B2A diagnostics JSON switch (2026-08-29): `app-trajectory.js` больше не
парсит/пересериализует `DataJson` через локальный `prettyJson` и не складывает JSON
в plain `<pre>`. Raw/derived event data передаётся adapter как точный bounded string;
`DataTruncated` становится completeness=`preview`, поэтому обрезанный object остаётся
raw с ошибкой позиции. Source event ids/sequences, hashes, payload metadata и
ResourceRef показаны отдельным раскрываемым JSON, не дописываются внутрь payload.
JSON CAS payload использует тот же viewer; HTML/прочие MIME остаются безопасным
`textContent` viewer с exact copy и без ложной JSON error. Stale request guards
остались у diagnostics owner, refresh уничтожает mounted controllers.

Phase 9B2A verification: новый integration UI test 5/5 и JSON adapter 7/7 pass;
existing completion 8/8, prompt review 5/5 и tools editor 1/1 pass. `node --check`
для затронутых JS, `git diff --check`, local links и `ValidateVersionFormat` — pass.
Локальный Chromium fixture с zero-network CSP: light/dark при 1280×720 и responsive
720×800, toolbar overlap 0, clipped buttons 0, body horizontal overflow 0. Fixture
и HTTP server удалены после проверки. Это не Windows WebView2/clipboard qualification.

Phase 9B1 bounded/lossless JSON viewer (2026-08-29): добавлены локальные
`app-viewer-registry.js` и `app-json-viewer.js` с тематическим CSS, без vendor,
network, worker, storage или bridge access. Allowlisted registry принимает только
уже загруженный payload. Adapter сохраняет immutable raw text и token spans,
duplicate keys, порядок и числа вне JS safe integer; raw/node/path/decoded-string
copy разделены. Invalid/truncated JSON остаётся raw с точной позицией ошибки, без
repair. Parse/depth/node/pretty/raw/DOM limits нельзя расширить выше hard bounds;
children создаются страницами, cancellation проверяется при parse. Все данные
рендерятся через `textContent`, completeness/redaction остаются metadata владельца.
7 targeted JSON viewer cases, existing completion/prompt/tools UI suites 14 cases,
syntax 3 JS files, zero-network API scan, 107 local docs links,
`ValidateVersionFormat` и `git diff --check` — pass. Реальный WebView2, clipboard,
responsive/theme visual qualification открыты. Existing consumers пока не switched,
поэтому их старые `prettyJson`/`JSON.parse` paths ещё не удалены. R36 не блокирует
собственный adapter без vendor, но обязателен до первого vendor switch.

Phase 9A diagnostics truth/query (2026-08-29): добавлен хронологический
`run-causal` view поверх canonical `*.events.jsonl` и existing `ITrajectoryQuery`.
Он сохраняет exact source event ids/sequences, model attempt/origin/call/mutation/
journal ids, revision-pinned resources и показывает явный evidence gap только после
typed terminal boundary; отсутствие события не объявляется успехом или ошибкой.
Новых writes, durable index, execution/replay decisions и UI inference нет. R37:
`ChatStore` теперь классифицирует accepted call по runtime-owned
`AcceptedCallOrigin`, независимо от provider result role/native `ToolCalls`; узкий
read-only adapter корректно проецирует ранее ошибочно помеченные current-v4 события,
не переписывая историю. 9B/9C, direct navigation UI и Windows/WebView qualification
остаются отдельными gates.

Phase 9A verification: 17 targeted harness cases pass — trajectory raw/derived/
run-causal/export 4, actual causal traces 6, accepted-call role classification 1,
typed bridge 1, selectable result roles 1, v4 accepted-history forms 1, canonical
event log 1 и complete-HTML runtime IDs 2. Одна актуальная host-neutral сборка,
последующие filters с `--no-build`; 0 errors, 4 existing CA1416 warnings в
`ExcelIdentityProbe`. `ValidateVersionFormat`, 117 local docs links и
`git diff --check` — pass.
Windows x64 + Office + VS 2022 / real WebView не запускались.

Phase 9A early start (2026-08-29, explicit user decision, docs-only baseline
`8d53d91`): из-за отсутствия Windows открытые Phase 5B2/6 и Phases 7–8 не закрываются,
а приостанавливаются; разрешён только host-neutral R32 9A truth/query поверх уже
существующего event stream/`ITrajectoryQuery`. Этот switch не меняет runtime/UI и
не разрешает зависеть от незавершённого Phase 8 ToolPack. 9B и 9C остаются отдельными
commits после acceptance 9A. Diff/4 docs, 74 local links и ValidateVersionFormat —
pass; build/harness не запускались.

Cleanup/readiness review (2026-08-28, baseline `1ea3ce0`): удаление controller-owned capture, catalog guard-only scope и прежних monitor/depth helpers подтверждено targeted search; includes актуальны. Дополнительных мёртвых путей в контуре 5B2 не найдено; legacy/probe сохраняются по действующим consumers/removal gates. Это не аудит всего репозитория. Согласованный пользователем допуск 6A заменяет прежнее предложение; Phase 5 не закрывается и порядок остальных фаз не меняется.

R32 requirements (2026-08-28, docs-only поверх `b754443`): по замечанию пользователя зафиксированы [сквозной журнал запуска и общий JSON viewer](R32_DIAGNOSTICS_JSON_VIEWER.md), inventory read-only consumers и acceptance Phase 9A–9C. Vendor-first оценка компактных готовых компонентов добавлена; конкретный vendor не выбран/не подключён. Runtime/UI не менялись; итоги 4B и следующий Phase 5 сохранены. Docs diff/9 новых локальных ссылок и anchors — pass; build/tests не запускались. Реализация, targeted UI/query tests и Windows/WebView qualification открыты; R28/R29 live gates этим требованием не закрываются.

R35 security hotfix (2026-08-29, отдельно от Phase 9): existing `DOMPurify 3.1.6`,
который очищает результат `marked` перед HTML insertion, заменён точным upstream
`3.4.14`; версия 3.1.6 входит в affected range GHSA-v2wj-7wpq-c8vv. Зафиксированы
npm integrity, git head, vendored SHA-256 и обе license texts. Markdown adapter,
CSP и остальные vendors не менялись. Headless Chromium загрузил vendored bundle
с `file://`, подтвердил version 3.4.14 и удаление script/event handlers в двух
malicious inputs; `node --check`, diff/links и version format — перед commit.
Реальный WebView2 на Windows не проверен; текущая Phase 6 и следующий slice не меняются.

R32 vendor/UI evaluation (2026-08-29, docs-only после R35): проверены existing
vendors и предложенный shortlist по source/package metadata, фактические bundles
четырёх основных кандидатов измерены. [Решение](R32_VENDOR_UI_EVALUATION.md):
Web Awesome Tree допускается только как tree-navigation spike; Wunderbaum — резерв
для measured large treegrid; оба JSON-кандидата отклонены для authoritative payload,
поэтому 9B начинает с собственного bounded/lossless `JsonAdapter`. Monaco/PDF.js
требуют смены текущего `file://` hosting; pinned local Worker разрешён и не считается
сетью, но Monaco всё ещё не оправдан в R32. `xterm.js` не используется для structured
logs. `ViewerRegistry` закреплён как UI-only adapter boundary поверх
Tool Result v1/`ResourceRef`, не новый model transport. R36 фиксирует незакрытый
provenance/offline inventory остальных vendors. Runtime/UI не менялись, Phase 6 и
следующий шаг сохранены; diff/7 docs, 99 local links и ValidateVersionFormat — pass,
build/harness не запускались. Windows/WebView qualification и Phase 9 implementation
открыты.

R32 Worker clarification (2026-08-29, docs-only): offline больше не трактуется как
запрет Web Worker. Текущий `file://` origin действительно блокирует worker path
Monaco/PDF.js; target допускает только pinned same-origin worker через WebView2
virtual-host mapping, host allowlist/factory, CSP и bounded termination, при полном
zero-network gate. Monaco остаётся вне R32 из-за размера/дублирования CodeMirror;
PDF.js — условный отдельный viewer. Runtime/hosting/CSP не менялись; diff/6 docs,
94 local links и ValidateVersionFormat — pass, build/harness не запускались.

R29 (предыдущий commit `6a256f0`): model wire содержит только name/arguments, kernel выдаёт ID до accepted append/confirmation/dispatch; ToolCallId + immutable attempt/position origin сохраняются в том же stream без переписывания raw response. Tests покрывают long HTML, allocator failure, native pairing, repair correlation, confirmation/replay и ISO-preserving clone. [Evidence/ограничения/чистка](R29_RUNTIME_CALL_IDS.md); этот protocol switch завершён до Phase 4, product version остаётся 16.1.0-dev.

Architecture audit (2026-08-28, docs-only commit `1f65f5d`, baseline `15dea46`): уточнены ID ownership, batch/control boundaries, actual effect evidence, ResourceRef transport (R30), pinned/bounded ToolPack, host gate, raw/comparable hashes и durable barriers будущих Phases 4–9. Убраны stale v2/media указания в canonical docs. Решение Phase 8 о конечном immutable pack сохранено; действовавшие на том baseline v3/LRU/runtime не менялись этим docs commit, позднее v4 включён отдельным R29. Критерии привязаны к фазам в master/backlog; R28/R29 и Windows gates открыты. Diff/13 затронутых ссылок — OK; pre-commit `ValidateVersionFormat` — pass. Build/tests не запускались, новые runtime-инварианты не объявлены проверенными. Phase 4 оставалась отдельным следующим этапом.

Historical live report (2026-08-28, docs-only; R29 runtime correction теперь описан выше, R28 открыт): фото показывает duplicate-ID rejection; после repair пользователь получил неполный HTML. По прямому запросу зафиксирован отдельный [R29/P1 — model-owned call IDs](RISK_REGISTER.md#r29--runtime-должен-владеть-идентификаторами-вызовов): целевое исправление — выдача ID кодом до execution, с сохранением correlation/confirmation/replay. Это отдельная правка контракта Phase 2 и consumers Phase 3, не автоматический результат 3B2; действовавший тогда v3 позднее атомарно заменён R29/v4. Полный incident trace не предоставлен, возможная ошибка scope остаётся R26; streaming — R28. Задача и критерии закрытия добавлены в [backlog](BACKLOG.md). Фаза/текущий подэтап на момент записи не менялись; проверены diff/локальные ссылки, build/tests и Windows/Office validation не запускались.

Workflow update (2026-08-28, docs-only): §§14.3, 22–23 — обоснованный единый switch может затронуть более 10 файлов; проверки применяются по изменению, повторные прогоны без новой причины не нужны; отчёт краткий. Runtime и открытые gates не изменены. Docs diff/links — OK; pre-commit ValidateVersionFormat — pass; build/tests для этой правки не запускались.

Migration sequencing update (2026-08-28, docs-only): Phase 3 изолирует kernel от resource lifecycle и проверяет минимальный RunSummary replay через существующие events; Phases 8/9 меняют внешние реализации, не повторяют извлечение. Проверки новых границ — при switch, Phase 10 — общая сверка. Основной маршрут 0–10 → 12 → stable; Phase 11 отдельно. Scope VBA package lifecycle пока не сокращён: общий journal нужен rename. Это уточнение плана, не начало Phase 3 и не закрытие R11/Windows gates; текущий следующий шаг указан в заголовке. Docs diff/затронутые ссылки и pre-commit ValidateVersionFormat — OK; build/tests для этой правки не запускались.

Pipelines disabled (2026-08-28, отдельное согласованное сокращение scope): удалены executor/parser, `PipelineJson`, nested dependency/safety/document/fingerprint traversal, transcript children parsing и editor. Catalog/discovery, direct/manual/dry-run execution, authoring и storage writes закрыты; старые определения skipped без migration/replay и без автоматического удаления файлов. Совместимость не сохраняется; возврат только отдельным решением Phase 11 после stable core. Это не начало Phase 3/11 и не дополнительный gate текущей миграции.

Pipeline verification: `pipeline:` 3/3; `tools:` 22/23 (единственный failure — известный R22); `vba: package` 5/5; `vba: session execution` 1/1; `vba: code-only UserForm authoring skill` 1/1; `completion guard:` 5/5; `agent: bounds oversized tool result data` 1/1; `protocol context: batch safety uses local authority` 1/1; production project includes 1/1. Итого 40 pass + R22; одна актуальная host-neutral сборка, следующие filters с `--no-build`. `node tests/web/tools-editor.test.js` — pass, syntax 5 затронутых JS — OK. Windows x64 + Office + VS 2022 / controller/WebView validation не выполнялась и остаётся открытой. V3 зафиксирован отдельно в `dbb8ce1`; pipelines — `f35e85c`. Pre-commit ValidateVersionFormat — pass.

Source archive build fix (2026-08-28, отдельное исправление по запросу пользователя): убрана блокировка обычной сборки без `.git`, введён явный `source-archive`/`unknown` с предупреждением; отсутствующий SHA/branch/tree state не подменяется выдуманным commit или `clean`. Debug и Release не требуют ручного props-файла; supplied provenance сохраняется, malformed metadata и ошибки Git checkout остаются ошибками. Explicit release gates требуют известного происхождения и Git checkout. Старый unconditional archive error удалён, adapters не добавлены; [canonical versioning](../operations/VERSIONING.md), ADR-0007 и §13.5 master plan обновлены. `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "versioning"` — 6/6 pass (архив без Git, partial/explicit metadata, release rejection, прежние version/tag/assembly cases); одна host-neutral сборка; `ValidateVersionFormat` и `git diff --check` — pass. Windows x64 + Office + VS 2022 / VSTO validation не выполнялась. Фаза и следующий шаг в заголовке не изменены; чужие runtime-изменения не входят в этот fix.

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.

MockDemo compile fix (2026-08-28, по второму скриншоту пользователя): добавлен отсутствующий source-link `Core/ModelProtocol/*.cs`; demo SettingsService обновлён под текущий `Save(..., reviewAgentPrompts)` с сохранением старого prompt marker при unrelated save. После устранения ModelProtocol errors таргетированная сборка обнаружила CS1501 в старой demo-сигнатуре; после её обновления `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 CA1416 warnings в PDF rendering. `git diff --check` — pass; demo runtime/self-test и полный harness не запускались. Старый demo Save path заменён без alias; production runtime не менялся этим fix. CS0006 для production Office DLL со скриншота отдельно не квалифицированы: нужна сборка Windows x64 + Office + VS 2022; Office/VSTO здесь не запускались.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | `10e52bf` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | done (host-neutral) | 1A: `a24feb1`; 1B: `5df587b`; 1C: `40282c0` | 61 targeted harness + 8 UI pass; red→green 4 cases; ValidateVersionFormat pass; last full 320/321 (R22) | not performed | 1A/1B/1C done; production Windows qualification остаётся открытой |
| 2 | done (host-neutral) | 2A: `d911826`; 2B: `a51bdda`; 2C1: `5a6b550`; 2C2: `c9f8b07`; 2C3A: `330aa79`; 2C3B: `4bbb039`; 2C3C: `dbb8ce1` | 2C3C: 100 targeted cases; ValidateVersionFormat pass; подробности в evidence | not performed | 2C3C был v3; current v4 — отдельный R29 correction ниже; old-chat skip/reset и prompt review/reset проверены локально; Windows/live-provider gates открыты |
| 3 | done host-neutral | 3A: `f01c3f2`; 3B1: `c1628ce`; 3B2: `15dea46` | 130 unique targeted cases; MockDemo compile; [evidence](PHASE_3B2_KERNEL_CUTOVER.md) | not performed | Production kernel switch + minimal real-store replay; Phase 4 отдельно |
| 2/3 R29 | done host-neutral | `6a256f0` | 141 unique targeted cases; MockDemo compile; [evidence](R29_RUNTIME_CALL_IDS.md) | not performed | Runtime IDs + v4; no v3 fallback, product version unchanged |
| 4 | done host-neutral: 4A + 4B | 85cc3f4 (4A); b754443 (4B) | 4B: 127 distinct targeted pass; MockDemo 0 errors / 3 existing CA1416 | not performed | [ToolRuntime](PHASE_4A_TOOL_RUNTIME.md), [v1 wire/cleanup](PHASE_4B_TOOL_RESULT_V1.md); domain/Windows gates remain |
| 5 | 5A–5B2 plus production Excel binding done host-neutral in 11T0/7D | 3a6c2aa (5A); a1b3d80 (5B1); 1ea3ce0 (5B2); [11T0](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) | 11T0: Excel read 4/4; write 4/4; HostRuntime 10/10; architecture/source checks | not performed | Exact Excel workbook/session bound under explicit `RuntimeKey` lifetime assumption; WQ0/WQ-SESSION evidence open |
| 6 | 6A–6J done host-neutral | `e0360f3` (6A); `62010c8` (R33); `dde18cf` (6B); through `cd0bd61` (6G); [6H](PHASE_6H_VBA_PACKAGE_SCOPE.md); [6I](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md); [6J](PHASE_6J_VBA_RENAME.md) | 6I package + 6J rename fault matrices; full VBA regression in linked reports | deferred | Full VBA/Windows gate open; R41/R42 runtime fixed host-neutral |
| 7 | 7A–7D done host-neutral; 7D delivered by 11T0 | [7A](PHASE_7A_EXCEL_SCOPE.md); [7B](PHASE_7B_EXCEL_READ.md); [7C](PHASE_7C_EXCEL_WRITE.md); [7D/11T0](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) | 11T0 focused checks above | not performed | Typed reads/verified write and direct bound backend switched; WQ0/WQ-EXCEL evidence open |
| 8 | 8A–8D done host-neutral; WQ-PACK pending | [8A](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [8B](PHASE_8B_CALLABLE_TOOL_PACK.md), [8C](PHASE_8C_TOOL_PACK_EVENTS.md), [8D](PHASE_8D_RESOURCE_DATA_PLANE.md) | 8D: 74 distinct targeted pass; MockDemo compile | not performed | Execution/callable authority, durable reconstruction and four native resource handlers switched; WQ-PACK open |
| 9 | 9A–9D5 done host-neutral; Windows acceptance pending | through `9bbf088` | 9D5: 99 targeted harness, web 70/70, MockDemo compile | not performed | Diagnostics/viewer, typed persistence ports and immutable RunViewState switched; R37/WQ-UI open |
| 10 | done host-neutral: 10A–10D | [10A](PHASE_10A_BOUNDARY_AUDIT.md), [10B1](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md), [10B2](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md), [10C1](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md), [10C2](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md), [10D](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md) | 10D: architecture 4/4; source inclusion 1/1 | not performed | mandatory structural route complete; WQ-A complete, 11T migration active; R49 fixed host-neutral |
| 11A | done host-neutral: 11A1–11A2 | [11A1](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md), [11A2](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md) | 11A2: harness 9/9; web 10/10; MockDemo full self-test | not performed | Commit projection + exact Library heads/history/classes/labels; Plan/HTML/viewer slices and R51 Windows gates remain |
| 11B | done host-neutral: 11B1–11B3 | [11B1](PHASE_11B1_PLAN_REVISION_GUARD.md), [11B2](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md), [11B3](PHASE_11B3_PLAN_HISTORY_HANDOFF.md) | 11B3: web Plan 7/7; Artifact Library 3/3; JS syntax 4/4 | not performed | Complete exact Plan lineage/restore/removal/history/handoff contour; Windows WebView remains |
| 11C | done host-neutral: 11C1–11C3 | [11C1](PHASE_11C1_HTML_LINEAGE.md), [11C2](PHASE_11C2_HTML_IMPORT_PREVIEW.md), [11C3](PHASE_11C3_HTML_BINDING_EXPORT.md) | 11C3: harness 8/8; web 21/21; JS syntax | not performed | Unique lineage, inert exact import and one guarded exact binding/recovery/export checkpoint path switched; Windows WebView/Office remains |
| 11D | in progress: 11D1–11D3 done host-neutral | [11D1](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md), [11D2](PHASE_11D2_IMAGE_PREVIEW.md), [11D3](PHASE_11D3_PDF_PREVIEW_X86.md) | 11D3 harness 4/4; affected web 37/37; JS syntax | not performed | Text/Markdown, exact image and bounded PDF preview-first viewers switched; audio and Windows WebView remain |
| WQ-A | A0–A5 done host-neutral | [contract](../qualification.md), [A3](WQ_A3_EXCEL_WQ0.md), [A4](WQ_A4_SUITE_CATALOG.md), [A5](WQ_A5_BUILD_EVIDENCE.md), [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md) | A5: qualification 14/14; versioning 6/6; source inclusion 1/1; web 5/5 | not performed | Exact-build admission implemented; production adapters/live suites, signed Windows evidence and Milestone WQ remain open |
| 11 | existing-tool migration route done host-neutral through 11T10; R61 correction active with 11O6 complete | 11A–11D3 evidence above; [11J1](PHASE_11J1_TOOL_AUTHORING_NATIVE_RUNTIME.md), [11J2](PHASE_11J2_VBA_PACKAGE_NATIVE_RUNTIME.md), [11K1](PHASE_11K1_SKILL_AUTHORING_NATIVE_RUNTIME.md), [11K2](PHASE_11K2_SKILLS_UI_TYPED_BOUNDARY.md), [11T10](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md), [11O6](PHASE_11O6_MINIMAL_CORE_PACK.md) | 11T10 full harness 589/589; 11O6 targeted harness 82/82 | not performed | Active legacy removed; remaining R61 Library documentation/Test/layout required before final Milestone WQ |
| 11O / R61 | in progress; 11O0–11O6 plus whole-resource correction done host-neutral | [Tool Library contract](../tool-library.md#mandatory-all-tool-contract-audit-r61), [audit](R61_TOOL_CONTRACT_AUDIT.md), [11O6](PHASE_11O6_MINIMAL_CORE_PACK.md) | 11O6 targeted harness 82/82; prior family/architecture/web and whole-resource evidence retained; version/diff checks pass | not performed | 11O7 Library UX next; then final post-cutover WQ |
| 12 | pending | — | — | — | Release hardening / qualification |

## Phase 0 substeps

- Ветка создана от historical baseline; master plan скопирован без изменений.
- Созданы progress, risk register, backlog и migration map.
- Исходные незакоммиченные изменения runtime/tests/UI не относятся к Phase 0; не менять и не включать в commit.
- AGENTS/README: feature freeze, обязательный порядок фаз, per-commit bump/tag отменены.
- Product target однократно изменён `16.0.4 → 16.1.0-dev`; повторного повышения нет.
- Ordinary validation не сравнивает версию с HEAD; release checks отделены и не создают tags.
- Добавлены CHANGELOG, canonical operations docs, ADR-0007 и явный release script без push по умолчанию.
- Build metadata содержит product/SHA/UTC/branch/channel/clean-or-dirty; AssemblyVersion сохранена `16.0.4.0`.
- Старый validation target удалён без alias; runtime/protocol/tools/resources/VBA/UI/persistence не менялись этим этапом.
- В репозитории не создаётся новый tag; `v16.0.4` остаётся исторической точкой.

## Phase 0 verification

- Baseline: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"` — 2/2 pass до изменений versioning.
- После изменений та же команда — 7/7 pass; весь linked host-neutral source set скомпилирован.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Проверены повторные builds/commits без bump, invalid metadata, dirty/staged tree, release tag matching, dev rejection, changelog, local/remote tag collisions и SDK/old-style assembly metadata.
- Git fixtures создаются и удаляются только во временных каталогах; настоящий origin и его tags не изменяются.
- Полный набор runtime tests не запускался: выбран минимальный build/versioning filter, production behavior не менялся.
- PowerShell release script не запускался (`pwsh` отсутствует); Windows x64 + Office x64 + VS 2022 / VSTO / ClickOnce — not performed.

## Phase 1A substeps

- Baseline — `10e52bf`, clean working tree. Производственные файлы не изменены.
- Прослежены model status → ChatTurnResult / accepted message → LastRun → controller/bridge → storage/header → UI.
- Current-to-target map уточнена для ConversationRunService, OfficeToolExecutor, ToolDefinition, ProgressiveToolWorkingSet, VBA executors, Excel adapter, Resource Fabric, persistence и UI.
- Воспроизведены completed после write error, journal unknown и отсутствующего write; сохранён нормальный write ok + final.
- Проверены valid response на запросе 20, отказ после 20 invalid responses и отсутствие rejected content/reasoning/repair instructions в accepted history.
- R01 подтверждён host-neutral тестами; исправление не выполнено. Green characterization не является green safety gate.
- R20: текущий лимит допускает initial + 20 retries (21 request). Поведение не менялось; исправление семантики attempts отложено в Phase 2.
- Новые adapters, protocols, runtime health fields и causal trace не вводились.
- Version остаётся `16.1.0-dev`; tag не создаётся.

## Phase 1A verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent: explicit response status"` — baseline 1/1 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — 7/7 pass.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Полный harness не запускался; весь linked source скомпилирован таргетированным запуском.
- Controller/bridge/UI проверены чтением кода, без интеграционного запуска.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM — not performed.
- Подробные доказательства и границы: [PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md).

## Phase 1B substeps

- Baseline — `a24feb1`, clean working tree; одна тема: causal trace, без completion guard.
- Logical step создаётся до первого model request; repair/fallback сохраняют step и получают отдельные modelAttemptId.
- Request/rejected/accepted diagnostics связаны с transport RequestId; accepted trace связывает точные toolCallIds.
- Top-level executor отмечает start/completion без изменения validation, dispatch, результата или retry.
- Journalled VBA module/rename/package action получает prepared/dispatched/verified markers с существующим mutationId; journal и read-back не меняются.
- Run/turn/document ids сохраняются в async logging scope; confirmation различает execution run и JournalRunId.
- Controller добавляет run.started, legacy run.summary.created и marker построения send/confirmation DTO. Это не runtime health и не подтверждение отрисовки WebView.
- Все metadata markers идут в существующий stream, без новых payload bodies, storage/index или decision state; новые trace failures не меняют execution.
- Version остаётся `16.1.0-dev`; tags не создаются. Phase 1C и последующие фазы не начаты.

## Phase 1B verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` — 6/6 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation: resets stream and thinking between repairs"` — 1/1 pass после обновления expectation для accepted trace.
- Первый full harness — 319/321: старое streaming expectation исправлено; compact catalog failure воспроизведён отдельно на исходном `a24feb1` (expected 16, got 15), R22.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj` — 320/321 pass; единственный failure — R22, такой же на baseline. Полный harness не green; новые trace tests и все 7 characterization tests проходят.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. `git diff --check` и relative Markdown links — pass.
- Baseline failure проверен в отдельном detached worktree; после проверки он удалён. Tags/working files основной ветки им не изменялись.
- Actual controller исключён из harness и заменён stub: его wiring проверено только чтением кода. Scope/summary/projection tests проверяют writer, не реальный controller/bridge delivery.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробности и границы: [PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md).

## Phase 1C substeps

- Baseline — `5df587b`, clean working tree. Одна тема: completion guard и минимальная UI/bridge-проекция; 10 production files включая csproj.
- До production fix новые runtime-summary assertions дали 4 красных characterization cases; после fix — 7/7 green.
- RunSummaryBuilder считает actual ToolResults по effective safety metadata, включая local mutations и nested pipeline policy. Model text/status и forged summary не определяют health.
- `unknown > errors > clean`; pending не считается успешной записью; rejected attempts не создают tool errors; v2 lifecycle/status и retry limits не менялись.
- Confirmation сохраняет summary логического turn и считает подтверждённый вызов один раз. Следующий user turn сбрасывает counts, не переписывая предыдущие snapshots.
- Runtime evidence сохраняется в существующих typed run/message operations; clone/DTO/replay сохраняют её. Нового durable store/index/schema или history migration нет.
- UI показывает отдельное предупреждение перед текстом модели вне свёрнутого trace. No-write — обычный ответ без подтверждённых изменений; boundary без summary не наследует старый clean.
- Legacy mapping и ограничение уровня evidence описаны в MIGRATION_MAP/R23. Domain tools, COM/VBA, Resource Fabric и persistence algorithms не менялись.
- Phase 2 не начата. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 1C verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — red 3/7 → green 7/7.
- Filter `completion guard:` — 5/5; `agent:` — 41/41 (включая characterization); `causal trace:` — 6/6; `conversation:` — 4/4.
- `storage: turn lifecycle` — 1/1 (replay/clone/typed DTO/model isolation); `chat: uses only read-only resource loop` — 1/1; `plan mode:` — 2/2; `harness: production projects` — 1/1.
- `node tests/web/completion-guard.test.js` — 8/8; реальные JS projection/render functions, минимальный DOM, без browser/layout/Office validation.
- Всего 61 различных targeted harness cases + 8 Node cases. Полный harness повторно не запускался; known baseline R22 остаётся открытым, последний full результат — 320/321 в 1B.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Production controller исключён из harness: его wiring проверено только чтением. Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробные команды, red→green evidence и границы: [PHASE_1C_COMPLETION_GUARD.md](PHASE_1C_COMPLETION_GUARD.md).

## Phase 2A substeps

- Baseline — `40282c0`, clean working tree. Один model/conversation contour, 6 production files включая Core csproj.
- В Core введены IModelProtocol, ModelProtocolClient и typed response/failure boundary. Loop больше не вызывает endpoint, не парсит JSON и не считает raw attempts.
- Parse/repair/native refusal/prompt budget/fallback/accepted-rejected diagnostics физически удалены из старого loop; fixed repair builder удалён из AgentJsonProtocol. Aliases/dual execution нет.
- Каждая попытка использует один accepted prompt; rejected body/reasoning/repair не входят в accepted history. Media сохраняются до конца protocol step и освобождаются в finally (R24).
- Provider/network/timeout/cancellation отделены от protocol exhaustion; прежний controller exception path сохранён через nonserialized Failure.Cause adapter до Phase 3.
- One enabled explicit schema fallback остаётся run-local; saved settings не меняются. Progress projector и trace sink сохраняют прежние semantics, step/attempt/request correlation.
- V2, tool policies/dispatch/summary и legacy initial + 1–20 retries сохранены. R20 и fallback при endpoint rejection внутри repair — оставшаяся Phase 2B.
- ADR-0002 фиксирует boundary, временные contracts и границы проверки. V3/schema/adapter/canonical v3 doc — Phase 2C; Phase 3 не начата.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2A verification

- Baseline characterization — 7/7; после переноса — 7/7.
- `model protocol:` — 8/8; `agent:` — 41/41 (включая characterization и media lifetime); `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1.
- Всего 68 различных targeted harness cases. C# 7.3 linked source build pass; ValidateVersionFormat pass. Новый Core source включён в old-style csproj.
- Прежнее media expectation после extraction дало expected 0 / got 1; обновлённый тест подтверждает одинаковый materialized prompt на repair и release после logical step. Это намеренное изменение lifetime, не новый baseline red→green case.
- Full harness/Node UI повторно не запускались: изменён только model/conversation contour, нет изменений UI или domain/storage algorithms. Последний full — 320/321 в 1B, R22 открыт.
- Fake endpoint tests не являются live tLLM validation. Production controller — stub в harness; Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Точные команды, legacy paths и границы: [PHASE_2A_MODEL_PROTOCOL.md](PHASE_2A_MODEL_PROTOCOL.md).

## Phase 2B substeps

- Baseline — `d911826`, clean working tree. Один model retry contour: 4 Core production files + caption/tooltip в web/index.html. Loop, tools, Resource Fabric, VBA и persistence не менялись.
- ModelProtocolRetryBudget считает 1–20 total protocol responses, включая первую. Default 10 и значения/ключ настройки MaxAgentFormatRetries сохраняются; initial + N удалён без alias (R20).
- Timeout/Network/TransientServer получают до двух provider retries на весь logical step, с cancellable delays 1s/2s. Ошибки HTTP/auth/429, size и invalid provider envelope не повторяются; transport parser/classification не менялись.
- Explicit enabled schema fallback работает также во время repair, один раз независимо от других budgets; exact current prompt/options повторно используются. N+3 raw requests maximum (23), не N×3.
- Cancellation проверяется до dispatch, во время backoff, после completion и rejection; запоздалый ответ не принимается. Нет повторного исполнения tools или новых accepted/history events.
- Canonical docs, ADR-0002 и changelog обновлены. V2/Failure.Cause остаются; новых compatibility adapters, v3 или AgentKernel нет. Phase 2C/3 не начаты.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2B verification

- Baseline: model protocol — 8/8, characterization — 7/7. До production fix: новые assertions дали 2 failures в model protocol и 2 в characterization (limits 1/20/clamp и fallback during repair).
- После fix: `model protocol:` — 13/13; `agent:` — 41/41 (включая characterization), `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1; `settings: invalid numeric values` — 1/1. Всего 74 разных targeted harness cases.
- C# 7.3 linked source build и ValidateVersionFormat — pass. Provider delays в tests инъецированы; реального ожидания/endpoint requests нет. Full harness/Node UI не запускались; изменены только model retry policy и текст одной настройки. Последний full — 320/321 в 1B, R22 открыт.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed; production controller остаётся stub в harness. Live provider/timeout/media costs — R25/R24, qualification pending.
- Точные команды и ограничения: [PHASE_2B_RETRY_POLICY.md](PHASE_2B_RETRY_POLICY.md).

## Phase 2C1 substeps

- Baseline — `a51bdda`, clean working tree. Полный v3 switch требует более 10 production files; по §14.3 выделен introduce/read-adapt, без частичного переключения. Изменены только 5 новых Core files + old-style Core project include, tests и docs.
- ConversationResponse содержит только message и ordered calls, без Status. Canonical ToJson пишет только v3 root; parser не принимает v2 автоматически. CurrentVersion активного AgentResponseProtocol остаётся 2.
- Strict envelope/JSON, call shape, 32-call bound, exact callable names и original argument schemas проверяются до acceptance. Optional nulls удаляются, execution defaults не применяются. Date-shaped strings остаются strings; unsupported numeric normalization возвращает typed failure.
- Accepted-run IDs и batch-safe read-only IDs задаёт caller. Parser не резервирует IDs; rejected response не возвращает partial calls. Mutation/local/confirmation и external/unclassified calls — singleton; безопасные read-only batches сохраняют порядок. Runtime wiring этих inputs — Phase 2C2 (R26).
- Explicit ConversationResponseV2Adapter читает только identified historical v2 envelope, отбрасывает model status и не выдаёт execution authority. Owner/consumers/removal указаны ниже; current consumer — harness, не history runtime.
- Canonical v3 doc и ADR-0002 содержат cutover gates: saved prompts, complete accepted-run IDs/confirmation, effective safety, все формы history, v3-only accepted writes, removal live v2 parser/schema/DTO consumers. Phase 3 не начата.
- Active model/retry/prompt/schema/history, Office tools, resources, VBA, persistence и UI не изменены. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2C1 verification

- Baseline: `model protocol:` — 13/13; `agent:` — 41/41.
- Новый `conversation v3:` — 13/13. Дополнительный oversized integer выявил InvalidCastException; focused malformed-JSON case был red, после typed-failure fix — green. Envelope, adapter, schema/wire, run-ID и singleton matrices входят в эти 13 cases.
- Regression: `model protocol:` — 13/13; `agent:` — 41/41; `harness: production projects` — 1/1. Всего 68 разных targeted harness cases, C# 7.3 linked build. ValidateVersionFormat — pass.
- Full harness, Node/UI, Office builds и live endpoint не запускались: active runtime не менялся. Последний full — 320/321 в 1B, known baseline R22 остаётся открытым.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed. Harness использует controller stub и не доказывает runtime cutover или Windows qualification.
- Точные команды, changed files, legacy paths и ограничения: [PHASE_2C1_V3_CONTRACT.md](PHASE_2C1_V3_CONTRACT.md).

## Phase 2C2 — context adaptation and local cleanup

- Baseline `5a6b550`, исходно clean. Полный switch не укладывается в §14.3; этот adapt затрагивает 9 production files (включая project includes и удалённый adapter), tests/docs. Phase 3 не начата.
- Loop подаёт immutable `ModelProtocolCallContext`: accepted-only IDs всего logical turn и conservative batch-safe projection. Confirmation читает full history до compaction, сохраняет scope при смене RunId; incomplete history не выдаётся за пустой set. Live v2 client пока context не enforce.
- Current-v3 history reader поддерживает canonical JSON, single native call с canonical metadata и literal final text; не читает старые форматы и не меняет данные.
- Неиспользуемый v2 read adapter, legacy JSON branch, include и obsolete tests удалены. Current-v2 typed-ID helper нужен текущей confirmation; удалить при writer/version switch 2C3. Local-read registry + effective metadata — до typed ToolPolicy Phase 4; bookkeeping — до kernel Phase 3.
- `conversation v3:` 13/13, `protocol context:` 6/6, `model protocol:` 13/13, `agent:` 41/41, `conversation:` 4/4, `completion guard:` 5/5, `plan mode:` 2/2, Chat read-only 1/1, production includes 1/1: 86 разных targeted cases. Linked C# 7.3 build и ValidateVersionFormat — pass.
- Runtime switch, saved prompts/probes, old-chat skip/reset, live provider и Windows x64 + Office x64 + VS 2022 не проверены/не выполнены. Harness моделирует controller identity transition; production controller остаётся stub. Full harness/UI/VSTO builds не запускались; baseline R22 остаётся открытым.
- Параллельные правки шести governance files включены с явного разрешения пользователя. Исходные docs-only проверки от 2026-08-28: cleanup policy — diff и 5 links/anchors OK; refactoring policy — diff и 7 links/anchors OK, без builds/runtime tests. Правила теперь canonical в master plan §§7.1, 15.1–15.2.
- Повторная чистка по §15.1: consumers/includes проверены, устаревшая рекомендация добавить v2 read adapter убрана из master plan §21, вводная PROGRESS сокращена; исторические evidence/ADR сохранены. Дополнительных мёртвых production paths в текущем контуре не найдено; live v2 callers нужны до 2C3. Ранее проверенный код и version/tag не менялись. Команды и границы: [PHASE_2C2_PROTOCOL_CONTEXT.md](PHASE_2C2_PROTOCOL_CONTEXT.md).

## Phase 2C3A — shared active wire owner

- Baseline `c9f8b07`, clean. По §§14.3/15.2 выделена подготовка coordinated switch: 7 production files; новый ModelProtocolWire — постоянный владелец schema/validation/JSON writing, без второго runtime/version selector.
- Runtime и compatibility probes используют общий contract; Office добавляет только reasoning/cache/trace options и native/history metadata. Дубли AgentOptions, ручного probe-call history и JSON call writer удалены. Prompt-authoring skill отсылает к действующим defaults вместо копии v2 status rules.
- Probes остаются fixed sentinel checks по одной raw попытке, без repair/fallback. Оба formats и все три tool-result roles проверены; матрица wrong status/casing/sentinel не даёт ложной qualification. V2 runtime, native refusal и response/prompt versions сохранены.
- Проверки: compatibility 2/2, model protocol 13/13, agent 41/41, protocol context 6/6, conversation 4/4, completion guard 5/5, plan 2/2, Chat read-only 1/1, project includes 1/1, existing prompt-reset characterization 1/1 — 76 разных targeted cases. Linked C# 7.3 build; ValidateVersionFormat pass. Full harness/UI/VSTO/live provider не запускались; Windows/controller/WebView не проверены.
- R27 подтверждён существующим тестом, но не исправлен: prompt schema mismatch автоматически заменяет custom prompts. Не повышать prompt version до explicit review/reset handling и его tests в 2C3B. Product остаётся 16.1.0-dev; tag/push/release script не выполнялись. Подробности: [PHASE_2C3A_WIRE_OWNER.md](PHASE_2C3A_WIRE_OWNER.md).

## Phase 2C3B — explicit prompt schema review

- Baseline `330aa79`, clean. Закрыт prerequisite R27 перед v3 switch: 10 production files, без изменения wire/prompts versions, Office tools, Resource Fabric, VBA или event-storage protocol.
- NormalizeAgentPrompts сохраняет authored text и missing/old/future marker; только blank fields получают defaults. SettingsService сохраняет clone; ordinary save не подтверждает stored mismatched marker, явный request-local review подтверждает его без перезаписи custom text.
- В typed saveSettings добавлен reviewAgentPrompts. Library → Prompts → «Подтвердить проверку» требует user confirmation; existing reset очищает drafts до save. PlanSystemPrompt больше не теряется из UI payload, отсутствие prompt editor не очищает сохранённые тексты. Обычные/tool/diagnostic saves не дают approval.
- Core guard вызван до controller preparation/attachment analysis/compaction и до изменения pending confirmation; neutral loop защищает direct entry/continuation. Production controller wiring проверен чтением, не execution на этой машине.
- Проверки: settings 4/4, typed settings bridge 1/1, prompt save 1/1, protocol context 6/6, confirmation success/failure по 1/1, Plan 2/2, Chat read-only 1/1, project includes 1/1, conversation streaming 4/4 — 22 targeted cases; Node prompt review 5/5. Reset characterization заменён red→green preservation test (раньше marker 0 автоматически становился 11).
- Реальный SettingsService теперь включён в linked C# 7.3 harness. Test-only ProtectedSecretStore поддерживает только отсутствующие fixture secrets и бросает ошибку при secret-file read/write; DPAPI не эмулируется. Windows x64 + Office + VS 2022, production controllers/WebView, DPAPI/live provider и full harness не проверялись.
- Чистка: удалены destructive mismatch branch, duplicate Chat/Plan defaulting и obsolete hard-reset test; устранены UI marker 0→1 и blank fallback при отсутствии editor. Нового production adapter нет. Product остаётся 16.1.0-dev; tags/push/release script не выполнялись. Подробности: [PHASE_2C3B_PROMPT_REVIEW.md](PHASE_2C3B_PROMPT_REVIEW.md).

## Phase 2C3C — v3 switch/delete

- Shared wire, typed result, repair, mode defaults и accepted-history marker переключены вместе: v3 содержит только `message + tool_calls`, prompt schema 12. Native refusal — отдельный outcome; model-loop end не является proof of effect.
- Полный history/context preflight стоит до подготовки, ручной compaction и подтверждения. Run-wide IDs и singleton safety enforce на каждом response; rejected batch не резервирует IDs и не исполняется частично. Saved prompts schema 11 сохраняются до explicit review/reset.
- Удалены live v2 DTO/parser/schema/includes, typed-ID helper, LastRun-only controller helper и 9 obsolete parser tests; fixtures используют настоящие v3 writers. Один связанный scope: 15 production files, без нового kernel/tool policy/storage/UI.
- Проверка и границы: [PHASE_2C3C_V3_CUTOVER.md](PHASE_2C3C_V3_CUTOVER.md). Windows/controller/Office/WebView/DPAPI и live provider не проверены; Phase 3 остаётся отдельной. Product `16.1.0-dev`, нового tag нет.

## Phase 3A — Office model context boundary

- Baseline `f35e85c`, исходно clean; pipelines уже отключены отдельным commit. По §15.2 извлечение нужно для ближайшего Core AgentKernel: loop больше не требует prompt/compaction/media и working-set implementation. Это один контур, 5 production files включая old-style project include; новый kernel или resource/store protocol не вводятся.
- Постоянный Office-владелец `ConversationModelSession` хранит model messages, request cache/options, read evidence/LRU и bounded result/media lifecycle, используя существующие services. Start/confirmation и prompt inspector переключены; прежние BuildMessages/BuildRequestOptions/materialization/media helpers и DTO из loop удалены без alias. Activity/resource/chart/checkpoint projection перенесена в существующий `AgentTranscript`; callback-связей обратно в loop нет.
- Сохранены v3, preflight, policies, execution/confirmation budgets, accepted IDs, summary и порядок result/history/projection. Failure.Cause, legacy summary и controller path остаются до 3B/4; владельцы/removal gates — в migration map. Phase 3 целиком не закрыта.
- Проверка: baseline `agent:` 32/32 и `protocol context:` 6/6. На изолированном составе Phase 3A (baseline `f35e85c` + 14 файлов; параллельные versioning/demo правки исключены) `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` — 33/33, включая новый auto-compaction/rebuilt callable-set case; существующий oversized-result/chart fixture проверяет перенесённую activity/provenance projection. После той же актуальной сборки `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `protocol context:` 6/6; `preflight` 3/3; `conversation:` 4/4; `context inspector:` 3/3; `causal trace:` 6/6; `completion guard:` 5/5; `plan mode:` 2/2; `chat: uses only read-only resource loop` 1/1; `harness: production projects` 1/1. Всего 64 разных cases pass, C# 7.3 source-linked build pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff/затронутые ссылки — OK. Full harness/JS не запускались: нет новых domain/storage/UI semantics; known R22 не перепроверялся.
- Windows x64 + Office x64 + VS 2022 / VSTO, production controller и real WebView/DPAPI/live providers не проверялись. IRunStore/новый RunSummary replay не реализованы и не считаются проверенными. Product `16.1.0-dev` и tags сохраняются; release script/push не выполняются.

## Phase 3B1 — Pure kernel introduction

- Baseline `68aadc2`, clean; чужие archive/versioning и MockDemo commits сохранены. По §14.3 полный switch разделён: model materialization, два execution path, storage и UI projection ещё требуют связанного wiring. Здесь вводится kernel contract, без production selection, feature flag или второго active loop. 13 production files: 8 новых kernel/contracts, 3 минимальных typed-caller renames, Core и MockDemo project includes; harness/includes/docs относятся к тому же контракту.
- `AgentKernel` знает только generic accepted messages/calls, typed execution records, summary и три ports. Normal/confirmation используют общий учёт; IDs и budgets принадлежат logical turn. Health вычисляется из execution evidence независимо от narrative; ambiguous write остаётся unknown, pending не считается outcome, retry tools не добавлен. Append/CAS failures останавливают работу без выдуманного durable terminal; synthetic result messages сохраняют typed evidence.
- Старый materialized `IModelProtocol.GetResponseAsync` переименован в `IMaterializedModelProtocol` во всех текущих typed callers, без alias или изменения v3/retry. Новый `IModelProtocol.SendAsync` пока имеет только fake implementation. ConversationRunService, legacy summary, Failure.Cause и projections не удалены: они обслуживают production до 3B2; owners/removal gates уточнены в migration map. ADR-0001/0008 и canonical state-model docs добавлены. Tool Result v1, домены, resources, VBA, UI и persistence algorithms не менялись.
- Проверка на изолированном составе этого изменения: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "kernel:"` — 41/41, включая cancellation во время policy recheck. На предыдущей сборке того же изменения `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `model protocol:` 15/15; `protocol context:` 6/6; `harness: production projects` 1/1. Эти 22 regression cases повторно использованы после локального исправления cancellation: active materialized sources/tests и project includes не менялись. Всего 63 разных cases pass. `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 прежних CA1416 warnings в PDF rendering. C# 7.3 source-linked compilation проверена; demo runtime/self-test, full harness и JS не запускались, R22 не перепроверялся. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Fake append/CAS log не доказывает existing-event replay, crash recovery или controller delivery. R11 остаётся открыт; actual IRunStore adapter и validated continuation restore — 3B2. Windows x64 + Office x64 + VS 2022 / VSTO, production controller, WebView/DPAPI/live providers не проверялись. Product `16.1.0-dev`, tags и release workflow не меняются.

## Phase 3B2 — Kernel production cutover

- `ConversationRunService` и controller confirmation используют единый Core kernel. Office model/tool/store ports сохраняют preflight, fingerprint, lease и model-context boundaries; старые loop, `ContinueAfterToolAsync`, `RunSummaryBuilder`, mutable ID bookkeeping и `Failure.Cause` удалены.
- `KernelState` сохраняется через existing `run.updated`, включая pending/in-flight evidence; flat run summary — только getter/projection. Real-store replay, stale confirmation, cancellation и interrupted/materialization boundaries проверены. Контракты — в canonical docs, точная matrix/команды и reused results — в [PHASE_3B2_KERNEL_CUTOVER.md](PHASE_3B2_KERNEL_CUTOVER.md).
- R11 contained только в минимальном контуре Phase 3; полный storage/UI и Windows/Office gates остаются. Domain tools, VBA, Resource Fabric, UI JS и version/release workflow не менялись. Development target `16.1.0-dev` не повышался, tag/push не выполнялись.

## Phase 5A — HostRuntime access boundary

- `Runtime/HostRuntime` стал владельцем текущих expected-document scope, file locks/monitor fallback, live-read depth и leases. Executor передаёт только Host/DocumentKey/RuntimeDocumentKey, access flags и синхронную operation; catalog, safety, tool/resource error mapping и domain preparation остаются у callers. Нет нового partial или второго executor.
- Consumers: обычный/ручной dispatch, VBA install/remove/run/editor, live Office/VBA resources и HTML data access. Старые executor-owned helpers/fields и `System.IO` dependency удалены; Office, Harness и MockDemo source includes обновлены. [ADR-0005](../decisions/ADR-0005-bound-document-session.md) фиксирует текущую границу и следующий switch.
- Не исправляются попутно: stable-key gate, global fallback, nesting без проверки target, stable-key OR runtime-key matching, preparation до gate и Excel ActiveWorkbook/descriptor lookup. R04/Windows остаются открыты; owners/consumers/removal gates — в MIGRATION_MAP. Kernel, v4/v1 wire, storage, UI, Excel/VBA domain algorithms не менялись.

Verification (2026-08-28): один build через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`, затем тот же command с `--no-build` для остальных filters. **16 distinct tests pass**, C# 7.3 / host-neutral .NET 8:

| Filter | Passed |
|---|---|
| `host runtime:` | 2 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: reconciliation waits for active mutation` | 1 |
| `vba: confirmed mutation rejects stale snapshot` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

Diff/16 добавленных или изменённых локальных ссылок и anchors — pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.

Новые boundary tests проверяют cancellation до/после action, отсутствие bypass у другого runtime, nested read и release после exception. Existing integration checks используют fake Office; они не доказывают реальную COM identity или новый bound contract. Production controller/real WebView, Windows x64 + Office + VS 2022 не проверялись; full harness и MockDemo build не запускались. Next — 5B, без Phase 6/9 switch; product version/tag workflow не менялся.

## Phase 5B1 — document access gate

HostRuntime берёт document gate до guard/preparation и удерживает до read-back/существующего journal terminal. Manual/resource/editor/HTML reads используют ту же границу; native list получает отдельный operation root. Reentry разрешён только той же синхронной operation и target, explicit STA transfer не передаёт право child tasks или новому UI/tool root. Owner STA возвращает busy без ожидания; cancellation повторно проверяется перед action на owner. Отмена и gate/guard exception после начала mutation сохраняют uncertain/nonretryable, а возвращённое domain evidence не переинтерпретируется.

Введён IOfficeDocumentSession: runtime/host/gate/dispatcher — cached metadata, stable identity/object/liveness проверяются на STA; wrappers держат одну session на lifetime. HostRuntime поддерживает этот port и строгий runtime match, но production Excel providers пока отсутствуют. Global monitor/per-instance AsyncLocal depth удалены; legacy stable-key/OR identity, actual workbook lookup и прямые context/catalog consumers остаются с owner/removal gate 5B2 в [MIGRATION_MAP](MIGRATION_MAP.md). Domain algorithms, kernel, v4/v1 wire, persistence и UI не переключались.

Verification (2026-08-28): **26 distinct tests pass**, C# 7.3 / host-neutral .NET 8. Final production sources скомпилированы через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`; следующие filters — `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`.

| Filter | Pass |
|---|---:|
| `host runtime:` | 7 |
| `vba: queued guard` | 1 |
| `waits for active mutation` | 5 |
| `vba: confirmed mutation` | 1 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

После исправления только native-list fixture (`kind: vba-component`, чтобы проверять реальный live module-list backend, а не project metadata) выполнен новый build для этого filter. 23 успешных предыдущих cases переиспользованы при неизменных относящихся production/test sources, dependencies и environment; package/includes выполнены затем с `--no-build`. Ранее исправлена новая manual-read fixture (`address`, не `range`); production schemas не ослаблялись. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff и 8 затронутых local links/anchors — pass. Версия остаётся `16.1.0-dev`, release script/tag/push не выполнялись.

Реальная COM identity/STA reentrancy, desktop/VSTO/native factories, active window/close/reopen/Save As и несколько клиентов требуют Windows x64 + Office + VS 2022. Здесь не запускались Office/VSTO validation, full harness или MockDemo build. Phase 5 целиком и R04 не закрыты; следующий шаг 5B2, без Phase 6/9.

## Phase 5B2 — direct context/catalog reads

Закрыт host-neutral read switch внутри 5B2. Конкретный блокер полного switch — предварительная Windows qualification общей runtime lifetime identity до переключения Excel factories (ADR-0005); production identity/binding не вводились. `HostRuntime.ReadDocument` использует существующий gate/guard/STA path отдельным operation root. `OfficeContextCaptureService` убирает прямой capture из controller, держит prepare/capture вместе и возвращает результат до persistence; VBA catalog держит cache identity/list/components под тем же gate. Busy/closed access не кэшируется как пустой catalog. Review выявил и исправил второй путь: failed/null backend result или exception при module list/component read теперь прерывает всю загрузку без публикации пустого/частичного cache и без внутреннего retry. Следующее независимое чтение может загрузить catalog заново; успешный пустой список по-прежнему кэшируется. UI context остаётся best-effort, selection guard/access failure не проглатывается.

Локальная чистка: удалены controller-owned capture implementation и catalog guard-only scope; общая guarded execution переиспользована без второго gate. Новый service включён в old-style Office project. Kernel, protocol, storage, UI и Excel/VBA algorithms не менялись. Consumers/removal gates обновлены в MIGRATION_MAP; legacy stable-key/OR identity и ActiveWorkbook/descriptor lookup остаются до production switch и Windows tests.

Verification после review (2026-08-28): **12/12 свежих targeted cases pass**, C# 7.3 / host-neutral .NET 8. Один build: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"` — 10/10. Расширен существующий catalog case: list/component failure через failed result, gate exception и generic exception; отсутствие cache/internal retry, последующая независимая загрузка и cache успешного пустого списка. Затем `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `vba: document tools discovered` — 1/1, `tools: catalog merges visible tools` — 1/1.

Из первоначального 5B2 read-switch run переиспользованы ещё **9 pass**: относящиеся production/test methods, dependencies, build settings и environment не менялись review-fix; повторного запуска этих filters не было.

| Reused filter | Pass |
|---|---:|
| `harness: production projects include all source files` | 1 |
| `vba: queued guard` | 1 |
| `vba: confirmed mutation` | 1 |
| `waits for active mutation` | 5 |
| `desktop com: adapter dispatches calls` | 1 |

Harness использует controller bridge stub; его tests проверяют production capture service, не controller wiring. Поэтому дополнительно выполнен `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --nologo -v:minimal`: **pass, 0 errors / 3 существующих CA1416 PDF warnings**; actual controller sources компилируются. Demo runtime/self-test и Windows controller/WebView поведение этим не проверены.

Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. Diff и 11 затронутых local links/anchors — pass. Product version остаётся `16.1.0-dev`; release script/tag/push не выполнялись.

Windows x64 + Office + VS 2022 обязательны для controller/WebView, COM identity/STA reentrancy и всех factory/lifetime сценариев. Office/VSTO validation и full harness не запускались. Phase 5/R04 не закрыты; следующий шаг и обязательные документы — в заголовке.

## Phase 5B2 — identity qualification probe

Подготовлен отдельный `tests/RNAssistant.ExcelIdentityProbe` (net48/x64, C# 7.3), не подключённый к production/solution. Кандидат — OXID/OID из стандартного IUnknown OBJREF плюс scope Excel process/start time; original marshal reference удерживается до STA dispose. Неизвестный format, неполный packet и пустая identity отвергаются без fallback. Native-OM driver выбирает explicit HWND/workbook index один раз; последующие snapshots не перепривязывают закрытую книгу. Данные книги не меняются, raw marshal packets не экспортируются.

Проверены primary Microsoft specifications; выбор остаётся кандидатом до реальных proxy/lifetime наблюдений. Исторический [ADR-0005](../decisions/ADR-0005-bound-document-session.md) фиксирует допущение и границу identity; probe README и код удалены 2026-09-30. Это был инструмент для конкретного блокера factory switch, не runtime adapter. Production `RuntimeKey`, ExcelAdapter/factories и ActiveWorkbook fallback тогда не изменялись.

Исходная verification (2026-08-28): `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "excel identity probe:"` — **3/3 pass**: unsigned LE/object-vs-interface identity, malformed/unsupported/bounded packets, non-Windows refusal до native access. Этот результат переиспользован при review: probe sources/tests, dependencies, build settings и environment неизменны. Свежий read-switch build также скомпилировал probe C# sources; 4 ожидаемых CA1416 warnings у guarded Windows COM calls. Итого для 5B2 **24 distinct cases: 12 свежих + 12 reused**, без повторного полного прогона. Это не net48/PowerShell/Office validation.

Probe project XML/explicit includes/whitespace проверены при исходной подготовке; sources/project не менялись при review. В README уточнено: запись `released` подтверждает только успешный return из Dispose, а полное освобождение ссылок/lifetime требует отдельных Windows наблюдений.

Windows net48 build, PowerShell driver, COM marshal/cleanup, реальная identity и full controller/Office matrix **не запускались**; PowerShell здесь отсутствует. R04/Phase 5 остаются открытыми. Next 5B2 gate — результаты Windows qualification; без них factories не переключать. Последующий явный допуск ограниченного 6A описан ниже; Phase 9 не начата.

## Phase 6A — pure VBA text extraction

2026-08-28, baseline `1ea3ce0`; пользователь разрешил этот локальный подэтап, пока Windows недоступна. Phase 5/R04 и полный Phase 6 gate не закрываются.

`Core.Tools.VbaPatchEngine` выполняет одну текстовую замену и возвращает typed status/text/match count; `VbaTextCanonicalizer` владеет прежними live/package/VBE-comparable правилами. Core выбран из-за действующих parser/storage consumers: размещение в Office создало бы обратную зависимость. Manifest parser, storage, patch/guard/read-back/package/catalog и fake consumers переключены. JSON/result mapping и ordered orchestration остаются у Office; COM, journal/CAS protocol, outcome classification не менялись. [Представления текста](../vba-mutation-journal.md#text-representations) описаны отдельно от raw CAS bytes.

Чистка: прежние normalization/hash methods из manifest parser, newline/count/replacement helpers из Office и неиспользуемый `System.Text` import удалены; aliases и второй text engine не оставлены. Новые `.cs` включены в production `.csproj`. Действующий Office mapping и оставшийся domain orchestration имеют consumers/removal gates в [migration map](MIGRATION_MAP.md); packages/journal не удалялись, поскольку используются, включая rename.

Проверено на текущих sources:

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba:"` — **57/57 pass**: pure patch, exact edit/guard/confirmation, hashes, fake VBE read-back, restore/journal/CAS/recovery, package/ToolStore/catalog. Добавлен один pure-text contract test; существующий hash test расширен для literal backslashes, строк и апострофных комментариев.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects include all source files"` — **1/1 pass**. Итого **58 свежих targeted checks**, одна host-neutral сборка; 4 прежних CA1416 warnings из guarded Windows identity probe. Tests с `COM` в имени используют fake objects и не являются Office validation.
- Source comparison: canonicalizer block совпадает с baseline после переименования API; изменения 11 существующих consumer files — только замена owner/name. Поиск прежних parser helper calls в src/tests/demo — 0. Diff и 17 затронутых локальных ссылок/anchors — pass.

Перед commit (2026-08-29): 58 checks выше переиспользованы при неизменных относящихся к ним sources/tests, dependencies, build settings и environment. Обязательный `ValidateVersionFormat` — pass; повторные runtime tests не запускались.

Full harness, MockDemo build и Windows/Office/VSTO не запускались. Унаследованный **R33** выявлен source review: non-overlapping counter может принять неоднозначное перекрывающееся вхождение; алгоритм сохранён в этом extraction. Нужен отдельный semantic fix с targeted tests до полного VBA gate.

Накопленные Windows проверки (Windows x64 + Office + VS 2022):

- 5B2: [identity assumption](../decisions/ADR-0005-bound-document-session.md), реальные proxy/lifetime и wrong-target сценарии; прежний probe удалён 2026-09-30.
- 5B2: controller/WebView selection/context/catalog reads, ошибки/закрытие книги/смена активной книги и несколько клиентов под gate.
- 6A: exact patch/guard на реальном VBE с CRLF/LF/CR, literal backslashes и комментариями; read-back/hash normalization, restore и package/rename regression. Journal/CAS evidence проверять без автоматического replay/restore.

Продолжение R33 согласовано отдельно 2026-08-29 и описано ниже. Остальные отложенные Windows gates сохраняются в своих phase reports.

## R33 — overlapping exact matches

2026-08-29, baseline `e0360f3`; отдельный semantic fix после commit 6A. `VbaPatchEngine` считает все стартовые смещения, включая перекрытия: `aaaa` / `aaa` → 2, `aaaaa` / `aaa` → 3. Неоднозначная замена отвергается даже при неизменном replacement. Newline/hash semantics, существующий `vba_patch_ambiguous` mapping, COM и journal protocol не менялись.

Два существующих tests расширены без новых fixtures/files. До fix оба дали ожидаемый **FAIL**: pure engine принимал overlap; executor пропускал его к confirmation. После fix **8 distinct targeted pass**:

| Harness filter | Result |
|---|---|
| `vba: pure patch text contract` | 1/1: overlapping offsets/counts, LF/CRLF/CR, unchanged ambiguous replacement, unique full-source match |
| `vba: patch` | 3/3: addressing/stale/ambiguity; reject до confirmation и при auto-confirm, без частичной записи ранее рассчитанной операции, backend write, backup или mutation record |
| `vba: exact patch` | 2/2: complete lines, boundary newlines |
| `vba: apply patch` | 2/2: valid unique mutation/backup, named target, mixed/all-no-op operations |

Команды: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba: pure patch text contract"`, затем остальные filters с `--no-build`. Одна host-neutral сборка для red tests и одна после production fix; 4 прежних CA1416 warnings из identity probe. Full harness, MockDemo и Windows/Office/VSTO не запускались; Office assertions используют fake adapter.

Перед commit: обязательный `ValidateVersionFormat`, diff и 13 затронутых локальных ссылок/anchors — pass.

Чистка: прежний non-overlapping counter заменён в единственном владельце, без alias/fallback/второй реализации; новые `.cs`/project includes не нужны. Canonical text contract, master/backlog/risk и migration status актуализированы. Позднее отдельно согласованный VbaReader описан ниже и не является частью R33.

Отложено: Windows x64 + Office + VS 2022 — overlapping one/multi-operation patch с обоими режимами confirmation, отсутствие live write/нового backup/journal при отказе. 5B2/R04, production binding и полный Phase 6 gate не закрыты; прежняя очередь Windows scenarios сохранена.

## Phase 6B — typed VbaReader

2026-08-29, baseline `62010c8`; отдельный host-neutral read slice после R33. `Office.Vba.VbaReader` теперь единолично строит internal VBA list/module commands, нормализует fallback-имя и проверяет typed project/module snapshots. Mutation guards, verification, reconciliation и package probes используют один reader; `ToolCatalogService` больше не строит backend-команды и не разбирает raw JSON самостоятельно. Resource adapter получает только уже проверенный backend payload и сохраняет прежний bounded wire.

Review воспроизвёл **R34**: успешный list payload `{}`/не-array `modules` и успешный module payload без `code` трактовались catalog как пустой/отсутствующий результат и могли попасть в минутный cache. Regression сначала дал ожидаемый 0/1; теперь malformed success завершает загрузку без partial publication/cache, следующее независимое чтение повторяет backend access, а настоящий `modules: []` кэшируется. Reader также fail closed проверяет field types, duplicate names, requested/returned identity, SHA-256 и truncation consistency.

Границы после switch:

- `HostRuntime` по-прежнему владеет document gate, operation root и target binding; reader не открывает и не удерживает gate.
- `VbaToolExecutor` владеет reconciliation, observations, guards, mutations, journal/read-back и текущим ToolResult/resource mapping.
- `ToolCatalogService` владеет только discovery/cache; host-specific COM и live Office authority остаются в adapters/`VbaProjectSupport`.
- Старые `TryReadVbaModule`, list/resource read builders, duplicate name-normalization/not-found helpers и catalog raw parsers удалены. Новые production/Harness/MockDemo includes добавлены; alias/dual-read path нет.

Verification: `vba:` — **58/58 pass**; `host runtime: direct VBA catalog reads share access` — **1/1**; `harness: production projects include all source files` — **1/1**, итого **60 distinct targeted cases**. MockDemo compile — 0 errors / 3 existing CA1416 warnings; `ValidateVersionFormat`, diff/check и затронутые docs links — pass. Full harness и Windows/Office/VSTO не запускались.

Отложено: реальные Excel/Word/PowerPoint VBE list/read, denied Trust Access, large/truncated modules, close/reopen/Save As и catalog refresh под production session; затем отдельно согласовать `VbaMutationService`/`VbaVerifier` начиная с apply_patch. COM/HostRuntime/factories/journal/result wire и Phase 7 этим substep не менялись. Phase 5B2/R04 и полный Phase 6 gate открыты.

## Phase 6C — VBA apply-patch mutation service

2026-08-29, baseline `fba247b`; отдельный host-neutral mutation boundary после
6B и завершённой Phase 9C. `Office.Vba.VbaMutationService` теперь владеет полным
`common.vba_apply_patch` workflow: current snapshot/guard, ordered patch,
prepared journal, backend dispatch, read-back и terminal classification.
`Office.Vba.VbaVerifier` стал одним владельцем module write/delete verification
и module assessment; действующие write/delete/restore и reconciliation используют
его без изменения публичного результата или durable evidence.

Границы после switch:

- `VbaToolExecutor` разбирает tool arguments, вызывает сервис и пока маппирует
  legacy `ToolCommand`/`ToolResult`; остальные whole-module entrypoints,
  reconciliation loop и package/rename journal остаются в executor.
- `VbaMutationService` не открывает `HostRuntime` gate и не выбирает документ:
  caller сохраняет один bound operation scope, host adapter остаётся COM authority.
- `VbaJournalStore` и CAS сохраняют прежние bytes/events/hashes; второго store,
  dual-write, recovery replay или нового wire нет.
- Удалён прежний `VbaToolExecutor.Patching.cs`; общие module journal/verifier
  implementations не дублируются. Package assessment не перенесён скрыто.

Новый direct service case проверяет собственные guard/dispatch/read-back/committed
journal evidence. Полный `vba:` filter — **59/59**, production source includes —
**1/1**, итого **60 distinct harness cases**; MockDemo compile — 0 errors /
3 existing CA1416 warnings. `ValidateVersionFormat`, docs links и diff check —
pass. Подробности: [Phase 6C evidence](PHASE_6C_VBA_MUTATION_SERVICE.md).

Открыто: 6D должен заменить service-границу `ToolCommand`/`ToolResult` typed
domain request/outcome, оставить mapping в executor, удалить rollback classification
по тексту и закрыть terminal-persistence/fault matrix. Whole-module/package entrypoints,
5B2/R04, production identity, Windows x64 + Office + VS 2022 VBE/read-back/package
qualification и полный Phase 6 DoD не закрыты.

## Phase 6D — typed VBA mutation outcome

2026-08-29, baseline `7a58825`; отдельный host-neutral outcome/fault slice после
6C. `VbaMutationService` больше не принимает `ToolCommand`, `ChatSession` или
legacy `ToolResult`: patch guard/workflow и общий module journal pipeline используют
typed requests, typed backend action result и финальный `Ok/Error/Unknown`.
`IVbaMutationDocumentContext`, `IVbaMutationReader`, `IVbaMutationBackend` и `IVbaMutationJournal`
ограничивают service capabilities; adapters оборачивают текущие host/store owners,
не создавая второй store или execution path.

`VbaMutationToolResultMapper` в Tools — единственный domain→legacy mapping этого
контура. Unknown всегда non-retryable и материализуется существующим Tool Result v1
как `unknown`. Verified intended state даёт `ok`, включая backend throw после
фактической записи; verified before state даёт definite `error`. Terminal append
failure возвращает `unknown` с `terminalRecorded=false`, оставляет preparation
открытой для read-only reconciliation и не повторяет dispatch.

Rollback больше не определяется по словам `restored`/`removed`/`rolled back`.
`rolled_back` возможен только по явному typed disposition и совпадению live before;
текущий legacy backend adapter его не синтезирует. Общий Tool Result больше не
содержит `journalStatus`/`packageJournalStatus` и не упоминает internal terminal
classification в message; durable status остаётся в journal/diagnostics. Source
read-back не объявляется VBA compile validation.

Новые fault cases покрывают prepare persistence, terminal append, backend throw до
и после effect, unavailable/mismatched read-back и cancellation до/после dispatch.
Существующие tests повторно покрывают restart-after-prepared, VBE normalization,
duplicate target и target-not-found. Полный `vba:` filter — **67/67**; отдельные
Agent unknown/causal, production-source include и MockDemo/format проверки указаны
в [Phase 6D evidence](PHASE_6D_VBA_MUTATION_OUTCOME.md).

Открыто: whole-module write/delete/restore и package/rename полные workflows ещё
executor-owned; package result semantics и host backend disposition будут типизированы
в следующих Phase 6 slices. 5B2/R04, реальный COM/VBE, controller wiring и полный
Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6E — whole-module VBA write service

2026-08-29, baseline `26f678c`; пользователь разрешил следующий отдельный
host-neutral slice. Полный write workflow `upsert/createOnly/updateOnly` перенесён
из `VbaToolExecutor` в `VbaMutationService.WholeModuleWrite`: deterministic name
normalization, missing/existing preparation, observation/confirmation guard,
existence-mode refusal, dry-run, prepared journal, typed create/replace backend,
source/type verification и terminal outcome теперь имеют одного domain owner.

Executor разбирает legacy arguments/mode, сериализует guard и отображает typed
outcome; прежние `WriteVbaModule`, `PrepareWriteGuard` и `BindWriteGuard` удалены.
`mode=rename`, delete, restore, reconciliation outer loop и package operations не
переносились. Existing `VbaMutationBackendAdapter` получил только typed create
action поверх текущего internal host command; COM implementation и store не менялись.

Review закрыл false-commit edge case: после backend error live source hash больше
не считается достаточным, если component type отличается от prepared state.
Same-source/different-type create race возвращает non-retryable `unknown` и durable
`unknown`; `createOnly`/`updateOnly` existence refusals происходят до journal/dispatch.
Полный `vba:` filter — **68/68**; связанные Agent/causal/source-inclusion и compile
проверки перечислены в [Phase 6E evidence](PHASE_6E_VBA_WHOLE_MODULE_WRITE.md).

Открыто: Phase 6F delete, затем restore и package/rename; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6F — VBA delete service

2026-08-29, baseline `57c157b`; пользователь разрешил следующий отдельный
host-neutral slice. Полный `common.vba_delete_module` workflow перенесён из
`VbaToolExecutor` в `VbaMutationService.DeleteModule`: exact existing-target read,
optional observation/confirmation guard, `StdModule`/`ClassModule` policy, dry-run,
prepared journal, typed compare-and-swap delete action, absence verification и
terminal outcome теперь имеют одного domain owner.

Executor разбирает legacy argument, сериализует typed guard и отображает outcome.
Прежние `DeleteModule`, `PrepareExistingModuleGuard`,
`ValidateExistingModuleGuard` и delete-only router helper удалены. Единственный
`VbaMutationBackendAdapter` строит текущий host-prefixed internal command; COM,
HostRuntime, journal/CAS format, public schema/result wire не менялись.

Direct service case проверяет dry-run без persistence/dispatch, CAS hash на backend,
verified absence, durable call correlation и fail-closed запрет DocumentModule до
journal/dispatch. Полный `vba:` filter — **69/69**; связанные Agent/causal,
source-inclusion и compile проверки перечислены в
[Phase 6F evidence](PHASE_6F_VBA_DELETE.md).

Открыто: Phase 6G restore, затем package/rename; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6G — VBA restore service

2026-08-30, baseline `f8c2674`; пользователь разрешил следующий отдельный
host-neutral slice. Полный `common.vba_restore_backup` workflow перенесён из
`VbaToolExecutor` в `VbaMutationService.RestoreBackup`: exact backup selection,
restore-specific confirmation guard, current-state recheck, dry-run, prepared
journal, typed create-or-replace action, source/type verification и terminal outcome
теперь имеют одного domain owner.

Guard фиксирует не только document/chat/module и current existence/source hash, но
и exact `backupId`, backup module/type и hash реально загруженного CAS source.
Подмена id/live source после preparation, stale target и несовместимый component type
отклоняются до journal/dispatch; это закрывает host-neutral R40, найденный при
6G audit. Executor разбирает legacy arguments, сериализует
typed guard и отображает outcome; старый restore workflow, его guard helpers,
прямой journal lookup и verifier alias удалены. Единственный Tools adapter строит
host-prefixed replace/create commands; journal/CAS format, COM, HostRuntime,
public schema/result wire не менялись.

Direct service case проверяет отсутствие dispatch/journal без guard, binding exact
backup id/live source, stale current target, dry-run, compare-and-swap hash, verified
restore, durable call correlation и type refusal. Полный `vba:` filter — **70/70**;
связанные Agent/causal/source-inclusion и compile проверки перечислены в
[Phase 6G evidence](PHASE_6G_VBA_RESTORE.md).

Открыто: отдельное решение и перенос package/rename ownership; 5B2/R04, реальный
COM/VBE/controller и полный Windows x64 + Office + VS 2022 gate не выполнены.

## Phase 6H — VBA package/rename scope audit

2026-08-30, baseline `cd0bd615`; отдельный docs-only consumer/architecture audit
после 6G. Проверены Agent/manual dispatch, временный install/run/cleanup, Tools UI
persistent install/uninstall, catalog status/discovery, public `mode=rename`, общий
package journal/reconciliation и diagnostics projection.

Решение: весь существующий package lifecycle остаётся в stable core и получает одного
typed owner в отдельном 6I. Отделять persistent UI в Phase 11 нельзя без сохранения
второго safety-critical install/remove path. Dynamic tool definition authoring, новые
package features и pipelines этим решением не включены. Rename является обязательной
public mutation и переносится отдельно в 6J; существующий `package.mutation.*` storage
wire сохраняется без rewrite/dual-write, но domain API остаётся rename-specific.

Найден R41: применённый session install при потерянном terminal/cleanup может оставить
временные components; текущий marker-insensitive probe затем считает совпадающий код
обычным `installed` и не гарантирует cleanup. 6I обязан воспроизвести это, связать
session lifecycle, различать ownership marker и блокировать execution при unknown или
незавершённой cleanup. Automatic replay/remove/overwrite не разрешены. Текущие package
tests проверяют happy path, macro failure cleanup, atomic records и mixed interrupted
reconciliation, но не terminal/cancellation/backend-throw/session-orphan matrix;
rename tests также не повторяют typed 6D fault matrix.

Runtime, tests и UI не менялись; build/harness не запускались. `git diff --check`,
13 новых local-link targets и `ValidateVersionFormat` — pass. Consumer map, ordered
6I→6J scope и gates: [Phase 6H evidence](PHASE_6H_VBA_PACKAGE_SCOPE.md).

Открыто: Phase 6I/R41, затем 6J и полный Phase 6/WQ-VBA; 5B2/R04 и реальный
Windows x64 + Office x64 + VS 2022 controller/COM/VBE gate не выполнены.

## Phase 6I — typed VBA package lifecycle

2026-08-30, baseline `0c6e5db`; отдельный host-neutral runtime slice. Новый
`Office.Vba.VbaPackageService` стал единственным owner package validation/probe,
document-local/persistent/session state, temporary install/run/cleanup, persistent
install/remove/status, package journal/read-back/reconciliation и typed
`ok/error/unknown`. `VbaToolExecutor.Packages` теперь только ToolDefinition/command/
result adapter; executor-owned package mutation helpers удалены. Оставшийся compound
journal код обслуживает только rename и удаляется в 6J.

R41 закрыт host-neutral: session install/remove получают один durable `LifecycleId`,
тот же id входит в exact ownership marker. Probe совмещает live marker/source/type с
append-only journal, поэтому потерянный terminal/cleanup, повреждённый или удалённый
marker и незакрытый lifecycle блокируют macro и persistent overwrite. Reconciliation
только наблюдает и дописывает terminal; cleanup выполняется лишь новой явной
policy-authorized journalled Uninstall над exact неизменённым session-owned package.
Install передаёт exact prepared existence/type/source/marker guard существующему
backend, который отказывает при post-prepare drift до первой мутации.
Lifecycle/search/ownership evidence проецируется в существующую diagnostics detail.

Host-neutral package fault cases покрывают prepare failure, throw до effect,
mutate-then-throw, read-back loss, terminal loss/restart, cancellation до/после
dispatch, cleanup failure, marker drift/strip, race между probe/preparation и перед
macro, post-prepare backend CAS, лишний catalog component, mixed multi-component
recovery и VBE normalization. `vba: package` 22/22 и полный `vba:` 87/87 — pass.
Точные команды/результаты и границы — в
[Phase 6I evidence](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md).

Production source-project inclusion 1/1 — pass. MockDemo actual-controller compile:
0 errors / 3 existing CA1416 warnings. `ValidateVersionFormat`, diff/static cleanup
и 158 local link targets в 10 changed Markdown files — pass. Full harness и
Windows/Office/VSTO не запускались.

Открыто: отдельный 6J rename switch; production COM/VBE/Trust Access/controller и
полный Windows x64 + Office x64 + VS 2022 WQ-VBA. Product `16.1.0-dev`, release/tag
не создавались.

## Phase 6J — typed VBA rename ownership

2026-08-30, baseline `6f9ddcc`; отдельный host-neutral runtime slice.
`Office.Vba.VbaMutationService` теперь владеет полным `mode=rename` contour:
source/destination guard, two-identity preparation, typed backend action, source/type
read-back, terminal `ok/error/unknown` и read-only recovery. `VbaToolExecutor`
оставляет только argument/result mapping и вызов reconciliation; его последний
compound journal path удалён без alias или второго execution path.

R42 закрыт host-neutral: confirmation guard связывает оба имени, source hash,
component type и code-only UserForm state. Typed backend повторно проверяет source
hash/type до COM rename. Cancellation проверяется после durable preparation и перед
dispatch; после возможного effect live identities определяют outcome. Verified
intended state побеждает backend error, complete before даёт definite error, mixed/
collision/unreadable state и terminal loss дают non-retryable unknown. Recovery
дописывает только terminal для complete-before/complete-intended/mixed и никогда не
повторяет rename.

Существующий `package.mutation.*` two-component wire и CAS остаются единственным
durable authority через narrow `IVbaRenameJournal`; новый store/snapshot/dual-write
не введён. Public schema/result shape и package runtime не менялись. Точная граница,
fault matrix и проверки — [Phase 6J evidence](PHASE_6J_VBA_RENAME.md).

Host-neutral `vba:` regression — 91/91 pass, включая 4 новых direct typed rename
cases и прежние schema/confirmation/COM/VBE/package regressions. Production source
include — 1/1 pass; MockDemo — 0 errors / 3 existing CA1416. Version/diff checks
pass; 161 local links в 10 changed Markdown files имеют 0 missing targets. Windows
x64 + Office x64 + VS 2022 COM/VBE/Trust Access,
confirmation/cancellation/restart qualification остаётся WQ-VBA.

Следующий отдельный шаг — Phase 7 Excel read/write; Phase 6 runtime повторно не
расширять. Product `16.1.0-dev`, release/tag не создавались.

## Phase 7A — Excel boundary and consumer audit

2026-08-30, baseline `d362b48`; docs-only prerequisite. Проверены public catalog,
`ConversationKernelAdapter`/`OfficeToolExecutor`, manual execution, HTML data
bind/refresh, `ExcelAdapter`, current neutral DocumentSession contract и fake/demo
call sites. Runtime, schemas, factories, COM и tests не менялись.

Зафиксирован ordered route: 7B атомарно переключает весь `excel.inspect` selector
family и `excel.read_range` на typed read owner/native handlers; HTML data использует
тот же read adapter. 7C отдельно переносит только `excel.write_range` с exact
before/read-back и typed effect evidence. Atomic 11T0/7D подаёт extracted interop
backend только bound workbook и удаляет compatibility resolver/internal command
seam; WQ0 больше не блокирует implementation. Ни один substep не расширяет scope на
другие Excel mutations.

`inspect(kind=charts)` означает только metadata read внутри уже существующего public
tool; `create_chat_chart`, chart/table mutations и formatting исключены. Range limit
должен проверяться до COM materialization. Найдены и открыты R43 (unbounded inspect /
named-range `Value2`) и R44 (HTML direct adapter route). Полная карта и gates —
[Phase 7A evidence](PHASE_7A_EXCEL_SCOPE.md).

Docs-only gates: `ValidateVersionFormat` и `git diff --check` pass; 130 local links
в 7 changed Markdown files имеют 0 missing targets. Windows/Office/VSTO и runtime
harness не запускались. Следующий отдельный шаг — 7B typed reads.

## Active compatibility adapters

None in the active tool execution, catalog, result or Library path after 11T10.
Removed names and exact replacement/removal gates are recorded in
[MIGRATION_MAP.md](MIGRATION_MAP.md); they are not aliases or supported history
readers.

`VbaMutationJournalStoreAdapter`, `VbaPackageJournalStoreAdapter` и
`VbaRenameJournalStoreAdapter` — permanent narrow ports к тому же
`VbaJournalStore`, не compatibility store и не второй writer. Permanent
model-session/metadata owners and `ModelCompatibilityService` diagnostics не
являются compatibility adapters. `ThisAddIn` active-object lookup owns pane/window
lifecycle only and cannot select a tool execution target.

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; guard 1C закрывает host-neutral safety assertions, production qualification ещё не выполнена.
- R02 и R07 contained host-neutral, но live-provider/Windows VBE gates открыты; R03–R06 остаются открыты. R08/R09 получили typed module outcome, terminal persistence и cancellation coverage в 6D; delete/restore switched через 6F/6G, package lifecycle/CAS/fault matrix — через 6I, rename — через 6J. Windows reconciliation/COM gates ещё открыты. R10/R11/R48 закрыты host-neutral через Phase 9; real Windows/WebView/restart/multi-window acceptance остаётся.
- R04: operation gate проверен host-neutral в 5B1; production bound Excel/common identity и Windows wrong-target scenarios — 5B2.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R22: compact catalog harness failure воспроизведён до изменений 1B; owner ToolPack/Tests, Phase 8.
- R26: full-history preflight, current v4 writer/confirmation, runtime IDs/origins и singleton enforcement проверены host-neutral; 4A заменил temporary name registry source-owned typed policy. Production controller ordering/Office qualification остаются открыты.
- R27: explicit review/reset проверены на current v4/schema16; custom text schema15 и остальных прежних/future markers сохраняется до явного review/reset. Production controller/WebView/DPAPI validation открыта.
- R29: runtime-owned IDs введены отдельным v4 switch; полного исходного incident trace нет, Windows/live-provider qualification остаётся открыта. Evidence и ограничения — [R29_RUNTIME_CALL_IDS](R29_RUNTIME_CALL_IDS.md).
- R33: overlapping exact-match ambiguity исправлена host-neutral; 2 regression tests red→green, 8 targeted pass. Реальная Windows/VBE regression остаётся открытой.
- R40: restore guard теперь связывает exact backup id/type/live-source hash и current target; substitution block проверен host-neutral, Windows confirmation/VBE gate открыт.
- R41: runtime fixed host-neutral в 6I — durable lifecycle + marker/journal-aware fail-closed state и explicit cleanup regression pass; production Windows/VBE/Trust Access qualification открыта.
- R42: rename guard/backend теперь связывают source type/hash и оба имени; cancellation/fault/recovery matrix pass host-neutral. Production Windows/VBE/confirmation/cancellation qualification открыта.
- R43/R44: исправлены host-neutral атомарным 7B switch; real Excel/WebView2 qualification остаётся открытой.
- R50/R55: runner и exact-build admission contained host-neutral через WQ-A5; missing
  adapters остаются N/A. Реальная подпись, PowerShell/certificate store и полный
  Windows/live pack matrix открыты.
- R61: все effective tool schemas требуют отдельного intent/runtime ownership
  audit; current URI/revision/cursor и internal artifact guard arguments ещё не
  переключены. Built-in docs isolation, typed Library Test UI, reported layout и
  post-cutover WQ-PACK/live-provider/WebView evidence открыты.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
