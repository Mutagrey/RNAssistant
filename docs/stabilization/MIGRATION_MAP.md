# Current-to-target migration map

Это план миграции, не декларация уже реализованной архитектуры. Phase 0 изменила
governance/versioning; Phase 1A добавила characterization tests и карту, Phase 1B —
только correlation/observability на существующих runtime boundaries. Phase 1C
добавляет runtime completion guard и минимальную health-проекцию. Phase 2A выделяет
Core/ModelProtocol, сохраняя v2 и прежние retry limits. Phase 2B заменяет initial +
retries общим лимитом protocol responses и отдельным provider budget; v2 остаётся.
Phase 2C1 ввела v3 contract/parser/schema/writer. Phase 2C2 подаёт полный ID/safety
context на model boundary, добавляет current-v3 history reader и удаляет неиспользуемый
v2 read adapter. Phase 2C3A объединяет active wire ownership и удаляет дубли builders;
единственный active wire/history path всё ещё v2, switch/delete — 2C3B.
Текущие domain docs остаются canonical до своей фазы.

Правило удаления: [master plan §15.1](STABILIZATION_MASTER_PLAN.md#151-обязательная-локальная-чистка-после-каждого-подэтапа). Заменённый путь удаляется в подэтапе переключения последних consumers после проверки; Phase 10 — только финальная структурная сверка. Совместимость со старыми чатами не является причиной удерживать adapter. Статусы ниже описывают зафиксированную реализацию, а новые removal gates — план, не выполненное удаление.

Подготовительные выделения выполняются только в своей фазе по [§15.2](STABILIZATION_MASTER_PLAN.md#152-рефакторинг-который-облегчает-миграцию). Указанные ниже точки проверены чтением кода; это ориентиры для проверки актуальных consumers перед работой, не разрешение начать Phases 3–7 из Phase 2C2.

| Current path / policy | Target | Owner | Consumers сейчас | Switch / removal gate | Статус |
|---|---|---|---|---|---|
| AGENTS/README: bump + tag после commit | Release-only versioning | Release process | Нет consumers старого правила | Phase 0: правила заменены | removed |
| ValidateRNAssistantVersion | ValidateVersionFormat + отдельные release gates | Build | Нет callers старого target | Phase 0: callers обновлены, старый target удалён без alias | removed |
| AssistantController orchestration, включая `.Agent` confirmation continuation | Application Facade; общий runtime учёт выполнения для start/continue | Application | Bridge, Office runtime, pending confirmation | Phase 3: switch start/continue без потери confirmation/fingerprint gates, controller wiring проверить отдельно; Phases 3–5: локальное удаление после switch; Phase 10 — структура | current |
| `src/RNAssistant.Office/Services/ConversationRunService.cs`: loop + prompt/media/materialization | Core/ModelProtocol + Core/Agent/AgentKernel; подготовка контекста и проекции вне kernel | Model/Runtime | Agent/Chat/Plan, confirmation continuation | Model boundary извлечена 2A; Phase 3: отделить цикл от подготовки/материализации, проверить fake model/tool; Resource/ToolPack semantics не менять до Phase 8 | current loop; model attempts removed 2A |
| Loop CompleteAsync/parser/format retries/fallback/trace helpers; `AgentJsonProtocol.CreateFormatRepairMessage` | `Core/ModelProtocol/ModelProtocolClient.cs` | ModelProtocol | Старых callers нет; loop использует IModelProtocol | Phase 2A: переключено и физически удалено без aliases | removed |
| Initial + MaxAgentFormatRetries; fallback только до первого repair | Total 1–20 protocol responses + отдельные provider retries/fallback | ModelProtocol | ModelProtocolClient, loop только как typed caller | Phase 2B: старый control flow заменён, лишняя попытка удалена | removed |
| Duplicated Office response options, JSON call/probe envelopes и direct probe parser/status checks | Core/ModelProtocol/ModelProtocolWire | ModelProtocol | ModelProtocolClient, ConversationRunService, AgentJsonProtocol, ModelCompatibilityService | Phase 2C3A: все callers переключены, AgentOptions/manual call-envelope paths удалены; shared owner остаётся при v3 switch | removed duplicates; permanent shared contract owner |
| Settings/bridge key `MaxAgentFormatRetries` | Тот же ключ, теперь total protocol responses | ModelProtocol / Settings | SettingsService, bridge, static settings form, retry budget | Phase 2B: значение не переписывается, caption уточнён; стабильный ключ остаётся | retained contract, не adapter/alias |
| `ModelProtocolFailure.Cause` → ExceptionDispatchInfo rethrow | Typed AgentKernel failure/lifecycle handling | Runtime / Application | ConversationRunService → существующий controller catch/cancel path | Phase 3: удалить rethrow adapter после switch на kernel | introduced adapter 2A; nonserialized |
| `Services/RunSummaryBuilder.cs`: legacy ToolResult + effective safety mapping | Core/Agent RunSummaryBuilder + typed ToolExecutionRecord evidence | Runtime / ToolRuntime | ConversationRunService, controller confirmation | Перенести builder Phase 3; заменить legacy mapping Phase 4; не дублировать rules в UI | introduced adapter 1C |
| `RunExecutionSummary` на ChatMessage/ChatRunRecord; отсутствующая summary у old pending/history | Canonical RunSummary / revisioned runtime projection | Application / Persistence / UI | Clone service, send/confirmation DTO, history UI | RunSummary/projection switch Phases 3/9; каждый obsolete adapter удалить при switch последних consumers; historical records не backfill | introduced adapter 1C |
| conversation-response v2 / AgentResponse DTO, Core/Tools/AgentResponseParser и AgentResponseSchemaBuilder | Core/ModelProtocol/ConversationResponse + parser/schema; runtime RunSummary отдельно | ModelProtocol | ModelProtocolWire (parser/schema/writer), ModelProtocolClient/ConversationRunService (DTO), AppSettings/AgentTranscript (instructions/metadata); probes используют shared contract | Phase 2C3B: switch client/prompts/schema/writes/version marker, explicit old-chat skip/reset; удалить последние live v2 consumers после integration tests | current live v2; нужен до согласованного switch |
| ConversationResponseV2Adapter / legacyV2 branch в ConversationResponseJson | Current-v3 history reader; несовместимые старые чаты — explicit skip/reset | ModelProtocol | Runtime consumers не было; obsolete harness consumers удалены | Phase 2C2: adapter/branch/include/obsolete tests физически удалены; old-chat guard обязателен до v3 dispatch в 2C3B | removed; no automatic fallback |
| Response-local call ID uniqueness / confirmation-only batching | V3 accepted-run ID validation + explicit batch-safe read-only set | ModelProtocol / Runtime | ConversationRunService подаёт snapshot; v3 parser overload — harness, active client всё ещё v2 | Phase 2C3B: require complete context до dispatch, parser enforcement на всех attempts; удалить live v2 checks | context wired 2C2; R26 enforcement gate open |
| ConversationProtocolContext.ReadCurrentV2CallIds | ConversationResponseHistoryReader для current-v3 history | Runtime / ModelProtocol | ConversationProtocolContext.SeedContinuation при active CurrentVersion=2 | Phase 2C3B writer/version switch: удалить typed-ID helper; причина сохранения — текущие confirmation records, не старые чаты | temporary current-v2 metadata consumer 2C2 |
| ConversationProtocolContext: transient accepted-ID bookkeeping | AgentKernel run context | Runtime | ConversationRunService start/confirmation → ModelProtocolRequest.CallContext | Phase 3 kernel switch: перенести один owner без второго loop; full-turn/compaction/confirmation tests сохранить | adapted 2C2; не durable index |
| ConversationProtocolContext.LocalReadIds + ToolSafetyPolicy projection | Typed ToolPolicy с external/nested effect metadata | Runtime / ToolRuntime | Constructor context → BatchSafeReadOnlyToolIds snapshot | Phase 4: заменить registry после typed metadata и equivalent safety tests; legacy flags не различают external effects | conservative temporary projection 2C2; unknown/pipelines singleton |
| Accepted `LlmCompletionResult` / существующий context-usage object в ModelProtocolResult | Accepted protocol metadata для kernel/transcript | ModelProtocol / Application | ConversationRunService → AgentTranscript и current turn result | Phase 2 v3/Phase 3 kernel: пересмотреть transport metadata boundary; старый transcript path удалить при switch | current metadata bridge; no rejected completion |
| `Services/RunCausalTrace.cs`, ModelTracePersistenceService и trace hooks текущих loop/executor/journal/controller | Наблюдение границ выделенных ModelProtocol / AgentKernel / ToolRuntime / domains / Application | Diagnostics / Application | Текущий loop, top-level executor, VBA journal wrappers, send/confirmation projection | Перенос hooks вместе с consumers в Phases 2–6/9; заменённые hooks удалить в том же подэтапе, historical events сохраняются | introduced 1B; только logging scope, не compatibility runtime |
| `src/RNAssistant.Office/Tools/OfficeToolExecutor.cs`: validation, safety, confirmation, document access, domain dispatch | Office/Runtime/ToolRuntime + existing domain executors + DocumentSession | Tools / HostRuntime | Loop, pipeline, manual execution | Phase 4: общий runtime с прежними domain executors; Phase 5: document access/serialization; slices 6–7: удалить заменённый dispatch при switch; optional consumers сохранить до своей фазы | current |
| `src/RNAssistant.Core/Models/ToolModels.cs`: ToolDefinition / ToolResult | Core/Tools/ToolDescriptor, ToolPolicy, ToolBinding, ToolResult, ToolExecutionRecord | Tool contracts | Catalog, schema/prompt, authoring, executor, bridge | Phase 4: разделить schema/policy/binding и результат; authoring отдельно Phase 11 | current |
| Host adapters / текущий выбор target | Bound DocumentSession + HostRuntime | HostRuntime | Excel/VBA writes, Office hosts | Phase 5 + Windows fault tests | current |
| `src/RNAssistant.Office/Tools/VbaToolExecutor*.cs`: source/patch, guards, journal, packages; normalization в VbaToolManifestParser | Office/Domains/Vba: patch/canonicalizer/mutation/verifier/journal | VBA | VBA tools, resources, verification, package lifecycle | Phase 6: сначала отделить чистую текстовую логику от ToolResult/resource hints/COM, переключить normalization consumers; затем read-back/unknown tests; production COM отдельно | current; divergent unknown reproduced 1A |
| `src/RNAssistant.OfficeHosts/ExcelAdapter.cs`: ExecuteTool, WriteRangeByKind, workbook/sheet resolution | Office/Domains/Excel + OfficeHosts/Excel/ExcelDocumentSession/InteropBackend | Excel/Host | Excel read/write tools, VSTO/native/desktop | Phase 5: document binding; Phase 7: узкий read/write backend; ActiveWorkbook fallback удалить после bound-session switch и Windows tests; charts/formatting не включать в подготовительный распил | current |
| `src/RNAssistant.Office/Services/ProgressiveToolWorkingSet.cs`: read evidence, replay, Touch/LRU | Core/Tools/ToolPackSnapshot | ToolPack | ConversationRunService, prompt composer, discovery | Phase 8; удалить eviction path и скрытое изменение schemas в run | current |
| `src/RNAssistant.Office/Services/ResourceGatewayService.cs`, ResourceProviderRegistry; `Tools/ResourceToolExecutor.cs` | Office/Resources: read/data plane | Resources | common.resources_list/resolve/search/read, domain providers | Phase 8; сохранить revision pinning, отделить execution outcome | current |
| `src/RNAssistant.Core/Storage/ChatStore.cs`, ChatStore.EventLog.cs, ChatStore.SessionProjection.cs, ChatHeaderReducer.cs | Core/Persistence/IRunStore + deterministic projections | Persistence | Controller, diagnostics/trajectory, history | Phase 9; факты вместо выбора outcome, без dual-write | current; status propagation mapped 1A |
| `web/js/app-chat-state.js`, app-utils.js, app-agent-model.js, app-agent.js | Revisioned runtime projection | UI/Application | Static web UI, ChatState/SendChatResponse | Phase 9: полный switch; old summary absence остаётся unverified | guard projection 1C; model status no longer certifies effects |
| Dynamic authoring, pipelines, HTML/Plan, non-Excel hosts | Optional отдельно квалифицированные контуры | Domain owners | Существующие consumers | Phase 11; не объявлять qualified в Phase 0 | current |

В Phases 0/1A/1B новые compatibility adapters не вводились. Adapter paths 1C/2A
указаны выше и в [PROGRESS.md](PROGRESS.md) с owner/consumers/removal phase.
V2 read adapter 2C1 удалён в 2C2 после проверки consumers. Current-v3 history reader
не поддерживает старые форматы и не мигрирует streams; временный current-v2 typed-ID
helper имеет ближайший removal gate 2C3B выше. Dual-write не вводится.
Массовые переносы файлов, aliases, новые runtime loops и dual-write запрещены.

Путь model status через ChatTurnResult, LastRun, controller/bridge, persistence и UI:
[PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md). В Phase 1C assertions
заменены red→green safety assertions: [evidence](PHASE_1C_COMPLETION_GUARD.md).
Model status остаётся для v2 lifecycle/history, но не является proof of effect.

Correlation ids, значения stages и ограничения trace:
[PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md). Старые transport `StepId` /
`llm.*` events сохранены для recovery; logical step находится в model `Data.StepId`.
Это диагностическая связь, не alias нового model protocol и не второй storage path.

Граница Core/Office, lifetime одного protocol step и сохранённые legacy ограничения:
[ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md),
[Phase 2A evidence](PHASE_2A_MODEL_PROTOCOL.md). Office stream projector адаптирует
прежний provisional preview к callbacks Core; это presentation, не второй protocol.
