# RNAssistant Agent Rules

Отвечай коротко и по делу. Экономь контекст: сначала используй `rg`, читай только нужные диапазоны файлов и запускай только таргетированные проверки. Не запускай VSTO/Office validation на этой машине.

RNAssistant — локальный VSTO/WebView2-ассистент для Office без серверной части. Чаты и контекст принадлежат документам; Office tools выполняются локально.

## Границы слоёв

- `RNAssistant.Core`: модели, настройки, storage, LLM client, prompt/tool parsing. Без Office/VSTO/WinForms/WebView2.
- `RNAssistant.Office`: общий runtime, typed bridge contracts, controller orchestration, services и tool execution. Без host-specific COM.
- `RNAssistant.OfficeHosts` и `RNAssistant.*AddIn`: host adapters, ribbon, VSTO и Office COM.
- `web`: static WebView2 UI без npm/bundler. Feature logic остаётся в тематических `app-*.js`; `app.js` — только boot/shared rendering.
- `tools` и `%AppData%/RNAssistant/tools`: пользовательские tools; executor logic живёт в `RNAssistant.Office/Tools`.

## Перед изменениями

Читай только документ области, которую меняешь:

- общая карта и зависимости: `docs/architecture.md`;
- resources, URI, providers, ingestion: `docs/resource-fabric.md`;
- Chat/Agent loop, progressive tools, JSON contract: `docs/conversation-protocol.md`;
- session events, CAS и recovery: `docs/session-events.md`;
- trajectory/exports/GC: `docs/trajectory-query.md`, `docs/trajectory-export.md`, `docs/cas-maintenance.md`;
- VBA mutations/packages/UserForms: `docs/vba-mutation-journal.md`, `docs/vba-tool-packages.md`, `docs/vba-userforms.md`;
- быстрые и таргетированные тесты: `tests/RNAssistant.Harness/README.md`.

Не загружай все документы и тесты «на всякий случай».

## Обязательные архитектурные инварианты

### Conversation и resources

- Поддерживаются `agent`, `plan` и `chat`; новый chat создаётся в `agent`. Все режимы используют один `ConversationRunService`.
- Model-facing чтение документов/артефактов идёт только через `common.resources_list/resolve/search/read` и revision-pinned `rna://` URI. Durable ссылки — только `ResourceRef`; internal artifact ids не являются вторым transport.
- Paste/drop/скрепка используют chat-scoped `stageChatResource`; `sendChat` принимает только `resourceDraftIds`. CAS/resource revision и связь с user turn сохраняются до network dispatch. Не возвращай ручные `artifactIds`, «В запрос», legacy readers, aliases или dual-write.
- Pre-cutover chat/context streams не мигрируются: только reset/skip.
- Chat получает только read-only `common.resources_*`, пустой capability catalog и не может confirmation/mutation tools.
- Plan получает read-only discovery и chat-local `common.questions_ask`, `common.plan_doc_*`, `common.task_list_*`; Office/shared mutations и confirmation запрещены. Один revisioned Markdown plan задаёт направление, а временный Task List отслеживает текущую работу.
- Agent хранит полный runnable catalog только как local execution authority. Модель сразу получает компактный каталог точных tool/skill ids с явным kind и bootstrap `common.capabilities_search/read`; точные схемы загружаются через revision-matched `common.capabilities_read` в bounded LRU working set. Не возвращай full-schema catalog injection или скрытый router/planner state.
- Skill body загружается через тот же полный revision-matched `common.capabilities_read` по точному id; после compaction/truncation/revision change требуется повторное чтение. References читаются bounded chunks тем же reader.
- Ответ модели — один conversation-response v2 JSON object `status + message + tool_calls`. `in_progress` требует непустой `tool_calls`, terminal status — пустой. Никаких fences/prose/legacy envelopes. Невалидные attempts не входят в replay/history; runtime не делает automatic tool retry или отдельную verification phase.
- `json_schema` строится только из текущего callable set; разрешён один request-local fallback в `json_object` при явном endpoint rejection и включённом `FallbackToJsonObject`.
- Tool result role и Markdown instruction role настраиваются независимо. Provider reasoning хранится отдельно от conversation JSON/history.
- Tool safety определяется `ToolDefinition` (`MutatesDocument`, `AgentCanRun`, `RequiresConfirmation`), не suffix-списками. Confirmation-required и VBA mutations подтверждаются при выключенном auto-confirm.
- Pipeline вызывает существующие tool ids только через `OfficeToolExecutor`, без прямого доступа к adapters.
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
- Dispatch — `OfficeToolExecutor`; pipeline — `PipelineToolExecutor`; VBA execution/guards/journal/packages — `VbaToolExecutor*`.
- Новые bridge payload/response формы — typed DTO в `Contracts`, без anonymous response shapes и ad-hoc `JObject` parsing.
- Host-neutral код не добавляй в VSTO/add-ins. Не меняй `*.Designer.cs` и VSTO metadata без необходимости.
- Не раздувай существующие крупные файлы: новый самостоятельный behavior выноси в тематический файл/service. Partial split допустим как безопасный первый шаг, но не как оправдание нового монолита.
- Сохраняй C# 7.3 и .NET Framework 4.8 compatibility. Новые `.cs` обязательно добавляй в old-style `.csproj`.
- Не вводи npm/bundler и не храни secrets в репозитории. API key остаётся DPAPI CurrentUser через `ProtectedSecretStore`.

## Проверка и release

- Core и Office-neutral parser/storage/tool/service: запускай минимальный подходящий filter из `tests/RNAssistant.Harness/README.md`; полный harness — только когда изменение пересекает несколько подсистем.
- COM/VSTO изменения здесь не проверяются. В отчёте укажи Windows x64 + Office + VS 2022 validation.
- Перед коммитом версия в `Directory.Build.props` должна быть выше HEAD; patch/minor/major выбирай по SemVer и не повышай повторно уже корректную версию.
- Перед коммитом: `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateRNAssistantVersion -nologo -v:minimal`.
- После коммита создай новый annotated tag `v<Version>` и отправь commit + tag в `origin`; опубликованные tags не перемещай и не переиспользуй.

## Definition of Done

- Responsibilities не смешаны между слоями; заменённый path удалён без alias/dual-write.
- Новые файлы внесены в old-style `.csproj`.
- Есть минимальная релевантная harness-проверка либо явное объяснение, почему она не запускалась.
- Для COM/VSTO указана Windows validation.
- При изменении protocol/architecture обновлён канонический документ области.
