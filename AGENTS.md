# RNAssistant Agent Rules

Отвечай коротко и по делу. Экономь контекст: сначала используй `rg`, читай только нужные диапазоны файлов и запускай только таргетированные проверки. Не запускай VSTO/Office validation на этой машине.

RNAssistant — локальный VSTO/WebView2-ассистент для Office без серверной части. Чаты и контекст принадлежат документам; Office tools выполняются локально.

## Stabilization freeze

- Обязательные требования: `docs/stabilization/STABILIZATION_MASTER_PLAN.md`. Текущая фаза и результаты — в `docs/stabilization/PROGRESS.md`.
- Работай только в текущей фазе и подэтапе. Не начинай следующую фазу в том же изменении; новые product features заморожены.
- При недоступной Windows действует согласованный режим отложенной qualification (§16.1 master plan): последовательно выполняй dependency-safe host-neutral подэтапы обязательного маршрута с targeted tests, локальной чисткой и отдельными commits. Открытый Windows gate не блокирует независимый следующий подэтап, но статус остаётся только `done host-neutral`; gates накапливаются в `PROGRESS.md` и обязательны до Phase 12. Не угадывай Office/COM semantics: 5B2 production identity/factory switch ждёт результатов отдельного Windows identity probe. Непроверенный candidate не называется stable/beta/RC.
- Главная ветка — `stabilization/16.1`; короткие рабочие ветки — `stab/<phase>-<task>`. Не коммить стабилизацию в `main`.
- Один commit — один инвариант или чёткий этап. Дефекты вне текущего контура записывай в `RISK_REGISTER.md` / `BACKLOG.md`, не исправляй попутно.
- Текущие runtime-инварианты ниже остаются правилами существующей реализации до соответствующей фазы master plan; целевые контракты не вводятся заранее.
- После каждого подэтапа кратко обновляй `PROGRESS.md`; отчёт — по разделу 23 master plan, без пустых разделов и дублирования истории. У compatibility adapters должны быть owner, consumers и removal phase в `MIGRATION_MAP.md`.
- Каждый подэтап закрывай локальной чисткой по §15.1 master plan: удаляй проверенный заменённый path и его мёртвые зависимости, обновляй canonical docs и краткий контекст следующего шага. Не откладывай это до Phase 10; массовые moves/renames и чужие контуры не включай.
- Рефакторинг для миграции выполняй только внутри соответствующей фазы по §15.2 master plan: укажи ближайшее упрощаемое изменение, устраняемые зависимости, локальную проверку и удаляемый старый путь. Размер файла или новый `partial` сами по себе не обосновывают работу; общий предварительный распил запрещён.
- Совместимость со старыми чатами/форматами не является целью стабилизации. Не сохраняй legacy только ради неё; временный adapter допустим для действующих consumers до их switch. Несовместимые чаты — явный skip/reset, без скрытого fallback и автоматического удаления пользовательских данных.

## Границы слоёв

- `RNAssistant.Core`: модели, настройки, storage, LLM client, prompt/tool parsing. Без Office/VSTO/WinForms/WebView2.
- `RNAssistant.Office`: общий runtime, typed bridge contracts, controller orchestration, services и tool execution. Без host-specific COM.
- `RNAssistant.OfficeHosts` и `RNAssistant.*AddIn`: host adapters, ribbon, VSTO и Office COM.
- `web`: static WebView2 UI без npm/bundler. Feature logic остаётся в тематических `app-*.js`; `app.js` — только boot/shared rendering.
- `tools` и `%AppData%/RNAssistant/tools`: пользовательские tools; executor logic живёт в `RNAssistant.Office/Tools`.
- Folder и namespace не обязаны совпадать механически: root `RNAssistant.Office` остаётся у публичного application façade и host ports, а тематическая папка задаёт owner. Не делай массовый namespace rename. Phase 10B1/10B2 перенесли все три подтверждённые host-specific physical exceptions в `RNAssistant.OfficeHosts/Identity` и `RNAssistant.OfficeHosts/Vba`; возвращать их, aliases, linked duplicates или Office consumers запрещено. Следующие два 10C cleanup invariants выполняются отдельными commits.

## Перед изменениями

Читай только документ области, которую меняешь:

- общая карта и зависимости: `docs/architecture.md`;
- resources, URI, providers, ingestion: `docs/resource-fabric.md`;
- Chat/Agent loop, progressive tools, JSON contract: `docs/conversation-protocol.md`;
- session events, CAS и recovery: `docs/session-events.md`;
- trajectory/exports/GC: `docs/trajectory-query.md`, `docs/trajectory-export.md`, `docs/cas-maintenance.md`;
- VBA mutations/packages/UserForms: `docs/vba-mutation-journal.md`, `docs/vba-tool-packages.md`, `docs/vba-userforms.md`;
- быстрые и таргетированные тесты: `tests/RNAssistant.Harness/README.md`.
- versioning/release: `docs/operations/VERSIONING.md`, `docs/operations/RELEASE_PROCESS.md`.

Не загружай все документы и тесты «на всякий случай».

## Обязательные архитектурные инварианты

### Conversation и resources

- Поддерживаются `agent`, `plan` и `chat`; новый chat создаётся в `agent`. Все режимы и confirmation используют `ConversationRunService` → `Core.Agent.AgentKernel`. Только kernel считает outcomes и выбирает lifecycle; controller не исполняет confirmed tool заранее. Model context/callable ToolPack/media остаются в Office.
- Model-facing чтение документов/артефактов идёт только через `common.resources_list/resolve/search/read` и revision-pinned `rna://` URI. Durable ссылки — только `ResourceRef`; internal artifact ids не являются вторым transport.
- Paste/drop/скрепка используют chat-scoped `stageChatResource`; `sendChat` принимает только `resourceDraftIds`. CAS/resource revision и связь с user turn сохраняются до network dispatch. Не возвращай ручные `artifactIds`, «В запрос», legacy readers, aliases или dual-write.
- Pre-cutover chat/context streams не мигрируются: только reset/skip.
- Chat получает только read-only `common.resources_*`, пустой capability catalog и не может confirmation/mutation tools.
- Plan получает read-only discovery и chat-local `common.questions_ask`, `common.plan_doc_*`, `common.task_list_*`; Office/shared mutations и confirmation запрещены. Один revisioned Markdown plan задаёт направление, а временный Task List отслеживает текущую работу.
- Agent хранит полный runnable catalog только как local execution authority. Модель сразу получает конечный mode/host core pack (для Excel — все Excel/VBA schemas), компактный каталог точных tool/skill ids с явным kind и bootstrap `common.capabilities_search/read`. Optional exact schemas запрашиваются через revision-matched `common.capabilities_read` и публикуются всей пачкой на следующей model-step boundary только после full-request budget admission и durable typed event; новая revision monotonic внутри logical turn, без LRU/touch/partial publication. Confirmation/compaction/crash восстанавливают ordered accepted extension chain того же `TurnId` по exact requested refs и before/after revisions; rejected event и raw read evidence не дают authority. Broken/drifted chain явно оставляет только core до нового accepted rebase. Не возвращай весь dynamic catalog schemas, скрытый router/planner state или eviction.
- Skill body загружается через тот же полный revision-matched `common.capabilities_read` по точному id; после compaction/truncation/revision change требуется повторное чтение. References читаются bounded chunks тем же reader.
- Ответ модели — один conversation-response v4 JSON object только `message + tool_calls`; call содержит только `name + arguments`, без model-owned ID/status. Kernel выдаёт ID до accepted persistence/confirmation/dispatch; `ToolCallId` и `AcceptedCallOrigin` сохраняют связь с exact raw attempt/позицией без переписывания payload. IDs уникальны в accepted user run и сохраняются через confirmation/replay; allocation failure не вызывает model repair. Write/external/confirmation-required/unclassified calls — singleton, batch допускает только independent local reads. Пустой `tool_calls` завершает model loop, но не доказывает effect. Native refusal отдельно; rejected attempts не входят в replay/history. Automatic tool retry/deduplication и отдельной verification phase нет. Unversioned/v2/v3 или неполная v4 история требует явного нового чата/reset до preparation/confirmation, без fallback.
- `json_schema` строится только из текущего callable set; разрешён один request-local fallback в `json_object` при явном endpoint rejection и включённом `FallbackToJsonObject`.
- Tool result role и Markdown instruction role настраиваются независимо. Provider reasoning хранится отдельно от conversation JSON/history.
- Все четыре `common.resources_list/resolve/search/read` исполняются через `Office.Runtime.ToolRuntime` по immutable descriptor/policy/binding и общему `ResourceGatewayService`; resource plane не содержит execution authority. Source-owned `ToolPolicy` задаёт effect/verification/allowed modes; `LegacyToolDefinitionAdapter` сохраняет ограничения оставшихся `ToolDefinition`. Отсутствие mutation flags не доказывает read; batch permission не определяется именем. Остальные domain handlers сохраняют legacy preparation/confirmation до своего switch.
- Tool outcome и actual effect evidence различаются: `Ok` не доказывает изменение; `VerifiedNoChange` не равно `VerifiedChange`. Compact dispatch/effect evidence сохраняется в Activity без копии payload; отсутствующее legacy evidence не восстанавливается из prose/Success. Tool Result v1 — единственный model wire: tool_call_id/name/status/message/data и optional resources; status только ok/error/unknown, code внутри data. Accepted call/result имеют marker1; старые results/pending требуют явного reset/new chat. Prompts schema16 сохраняют custom text schema15 и других markers до explicit review/reset. Native result не проходит через legacy DTO; domain→typed и UI-only adapters не читают старую историю. Confirmation-required и VBA mutations подтверждаются при выключенном auto-confirm.
- Pipelines отключены и отложены до отдельного решения Phase 11 после stable core. Не возвращай их в catalog, execution, authoring или UI и не поддерживай старые определения.
- Unified discovery/read `common.capabilities_search/read` не смешивается с authoring `common.tools_definition_read/validate/upsert/delete`. Skills authoring использует `common.skills_upsert/delete`.
- HTML и plan читаются через `common.resources_*`; mutations остаются отдельными tools. Не возвращай удалённые read ids.

### Storage и trajectory

- Единственный durable source of truth чата — append-only typed `*.events.jsonl`. `ChatSession`, history, headers, UI/HTML/trajectory — replayable projections; mutable snapshots и вторичные durable indexes запрещены.
- Финальный materialized model request сохраняется до network dispatch. Response/failure, turn/step, tool boundaries, bounded stream chunks и resource revisions пишутся в тот же stream без secrets/auth headers.
- Неизменяемые payloads/resources хранятся в SHA-256 CAS `chat-blobs`; optional HMAC/encryption keys берутся только из DPAPI secrets.
- CAS GC каждый раз rebuilds reachability из полностью проверенных chat streams и VBA journals. Любой corrupt/unreadable/incomplete source запрещает удаление.
- Trajectory каждый раз строится через `ITrajectoryQuery`; rows сохраняют все source event ids/sequences. Export одноразовый и bounded; protection keys в bundle не входят.
- Контекст принадлежит активному chat session. Document identity migration сохраняет chats при смене пути/первом save.
- Runtime reset не удаляет settings, API key или custom tools без отдельного явного действия.

### VBA

- Model-facing VBA discovery/read выполняется provider `vba` через `common.resources_*`. Public `common.vba_*` содержит только mutations: whole-source upsert/rename, exact-hunk patch, delete, restore.
- VBA mutation сама читает live state, связывает guard с chat/document/component, пишет journal `prepared` до COM и terminal после read-back. Незавершённые записи только сверяются с live state; automatic replay/restore запрещён.
- Office document — authority для live VBA; journal и CAS — recovery evidence. Host-prefixed whole-module/rename/macro backends не публикуются модели.
- UserForms поддерживаются только как CodeOnly: пустой Designer и runtime-generated controls. Designer/FRX не входят в source/backup protocol. Exported `.frm/.frx` в packages запрещены.

## Размещение кода

- `AssistantController` — orchestration only. Chat/session bridge methods — `AssistantController.Chats.cs`, context — `AssistantController.Context.cs`, reusable behavior — `Services`.
- Dispatch — `OfficeToolExecutor`; guard/preparation/dispatch/read-back и live reads сериализует `Runtime/HostRuntime` через `DocumentAccessGate`. Reentry — только та же синхронная operation/target, с явным STA transfer; gate не держится во время model/user wait. Нейтральный session port введён в 5B1; direct selection/context/catalog reads используют отдельные operation roots в HostRuntime. Production Excel binding/identity и Windows qualification остаются 5B2. VBA execution/guards/journal/packages — `VbaToolExecutor*`.
- Новые bridge payload/response формы — typed DTO в `Contracts`, без anonymous response shapes и ad-hoc `JObject` parsing.
- Host-neutral код не добавляй в VSTO/add-ins. Не меняй `*.Designer.cs` и VSTO metadata без необходимости.
- Не раздувай существующие крупные файлы: новый самостоятельный behavior выноси в тематический файл/service. Partial split допустим как безопасный первый шаг, но не как оправдание нового монолита.
- Сохраняй C# 7.3 и .NET Framework 4.8 compatibility. Новые `.cs` обязательно добавляй в old-style `.csproj`.
- Не вводи npm/bundler и не храни secrets в репозитории. API key остаётся DPAPI CurrentUser через `ProtectedSecretStore`.

## Проверка и release

- Проверки выбирай по изменению и риску (§22.1 master plan): Core/Office-neutral — минимальный подходящий filter из `tests/RNAssistant.Harness/README.md`; полный harness — при изменении поведения нескольких подсистем, общей инфраструктуры без достаточного targeted coverage либо по явному gate. Число файлов/assemblies само по себе не требует полного прогона.
- Docs-only — diff и затронутые ссылки без build. Не повторяй успешные проверки без изменений их inputs или новой причины; reused evidence должно соответствовать текущим sources/tests/dependencies и environment. Это не отменяет pre-commit version check и явные phase/release gates.
- COM/VSTO изменения здесь не проверяются. В отчёте укажи Windows x64 + Office + VS 2022 validation.
- Commit не является release. Требование повышать версию и создавать tag на каждый commit отменено.
- Historical baseline — `v16.0.4`; development target один раз установлен в `16.1.0-dev`. Обычные commits не меняют product version и не получают tags.
- Перед коммитом: `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal`. Проверка не сравнивает версию с HEAD и не требует clean tree.
- Release-only checks и annotated tags запускаются только для явно согласованного release milestone по `docs/operations/RELEASE_PROCESS.md`. Обычный commit не вызывает `tools/Prepare-Release.ps1`.
- Не перемещай и не переиспользуй tags; push release допускается только с явным параметром. Major не повышается из-за внутреннего refactoring; protocol versions независимы от product version.

## Definition of Done

- Responsibilities не смешаны между слоями; заменённый path удалён без alias/dual-write.
- Локальная чистка завершена; у оставшегося legacy указаны consumers, причина и ближайший removal gate. В `PROGRESS.md` актуальны следующий шаг и обязательные для него документы.
- При добавлении/удалении/перемещении `.cs` обновлены old-style `.csproj`.
- Изменённое поведение покрыто минимальной релевантной проверкой либо явно отмечен пробел; существующее подходящее покрытие не требует новых тестов. Docs-only проверяется без harness.
- При затронутом COM/VSTO/controller wiring указана требуемая Windows validation; непроверенный gate не считается закрытым.
- При изменении protocol/architecture обновлён канонический документ области.
