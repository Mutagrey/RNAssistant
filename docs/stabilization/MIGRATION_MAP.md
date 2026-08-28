# Current-to-target migration map

Это план миграции, не декларация уже реализованной архитектуры. Phase 0 изменила
governance/versioning; Phase 1A добавила characterization tests и карту, Phase 1B —
только correlation/observability на существующих runtime boundaries. Phase 1C
добавляет runtime completion guard и минимальную health-проекцию. Phase 2A выделяет
Core/ModelProtocol, сохраняя v2 и прежние retry limits. Phase 2B заменяет initial +
retries общим лимитом protocol responses и отдельным provider budget; v2 остаётся.
Phase 2C1 вводит v3 contract/parser/schema/writer и explicit v2 read adapter;
единственный active runtime/history path всё ещё v2, switch/delete — Phase 2C2.
Текущие domain docs остаются canonical до своей фазы.

| Current path / policy | Target | Owner | Consumers сейчас | Switch / removal gate | Статус |
|---|---|---|---|---|---|
| AGENTS/README: bump + tag после commit | Release-only versioning | Release process | Нет consumers старого правила | Phase 0: правила заменены | removed |
| ValidateRNAssistantVersion | ValidateVersionFormat + отдельные release gates | Build | Нет callers старого target | Phase 0: callers обновлены, старый target удалён без alias | removed |
| AssistantController orchestration | Application Facade | Application | Bridge, Office runtime | Phases 3–5; затем cleanup Phase 10 | current |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Core/ModelProtocol + Core/Agent/AgentKernel | Model/Runtime | Agent/Chat/Plan, confirmation continuation | Model boundary извлечена 2A; сам loop заменить Phase 3 | current loop; model attempts removed 2A |
| Loop CompleteAsync/parser/format retries/fallback/trace helpers; `AgentJsonProtocol.CreateFormatRepairMessage` | `Core/ModelProtocol/ModelProtocolClient.cs` | ModelProtocol | Старых callers нет; loop использует IModelProtocol | Phase 2A: переключено и физически удалено без aliases | removed |
| Initial + MaxAgentFormatRetries; fallback только до первого repair | Total 1–20 protocol responses + отдельные provider retries/fallback | ModelProtocol | ModelProtocolClient, loop только как typed caller | Phase 2B: старый control flow заменён, лишняя попытка удалена | removed |
| Settings/bridge key `MaxAgentFormatRetries` | Тот же ключ, теперь total protocol responses | ModelProtocol / Settings | SettingsService, bridge, static settings form, retry budget | Phase 2B: значение не переписывается, caption уточнён; стабильный ключ остаётся | retained contract, не adapter/alias |
| `ModelProtocolFailure.Cause` → ExceptionDispatchInfo rethrow | Typed AgentKernel failure/lifecycle handling | Runtime / Application | ConversationRunService → существующий controller catch/cancel path | Phase 3: удалить rethrow adapter после switch на kernel | introduced adapter 2A; nonserialized |
| `Services/RunSummaryBuilder.cs`: legacy ToolResult + effective safety mapping | Core/Agent RunSummaryBuilder + typed ToolExecutionRecord evidence | Runtime / ToolRuntime | ConversationRunService, controller confirmation | Перенести builder Phase 3; заменить legacy mapping Phase 4; не дублировать rules в UI | introduced adapter 1C |
| `RunExecutionSummary` на ChatMessage/ChatRunRecord; отсутствующая summary у old pending/history | Canonical RunSummary / revisioned runtime projection | Application / Persistence / UI | Clone service, send/confirmation DTO, history UI | Полный RunSummary/projection switch Phases 3/9; obsolete adapter paths удалить Phase 10; historical records не backfill | introduced adapter 1C |
| conversation-response v2 / AgentResponse DTO, Core/Tools/AgentResponseParser и AgentResponseSchemaBuilder | Core/ModelProtocol/ConversationResponse + parser/schema; runtime RunSummary отдельно | ModelProtocol | ModelProtocolClient, compatibility probes, prompt/schema, accepted transcript/history | Phase 2C2: coordinated switch, удалить superseded live v2 parser/schema/DTO consumers; Phase 3 lifecycle | current live v2; v3 введён 2C1, пока harness-only |
| Historical v2 JSON envelope / model-owned status | ConversationResponseV2Adapter.Read → status-free v3 projection | ModelProtocol | Сейчас focused harness; intended history projection Phase 2C2 | Phase 10 после удаления legacy history consumers по explicit compatibility decision | introduced 2C1; read-only, not wired, no live fallback |
| Response-local call ID uniqueness / confirmation-only batching | V3 accepted-run ID validation + explicit batch-safe read-only set | ModelProtocol / Runtime | V3 parser — harness; live v2 checks неизменны | Phase 2C2: wire полный run scope/confirmation и effective safety projection, R26; удалить live v2 checks при switch | introduced contract, runtime switch pending |
| Accepted `LlmCompletionResult` / существующий context-usage object в ModelProtocolResult | Accepted protocol metadata для kernel/transcript | ModelProtocol / Application | ConversationRunService → AgentTranscript и current turn result | Phase 2 v3/Phase 3 kernel: пересмотреть transport metadata boundary; старый transcript path удалить при switch | current metadata bridge; no rejected completion |
| `Services/RunCausalTrace.cs`, ModelTracePersistenceService и trace hooks текущих loop/executor/journal/controller | Наблюдение границ выделенных ModelProtocol / AgentKernel / ToolRuntime / domains / Application | Diagnostics / Application | Текущий loop, top-level executor, VBA journal wrappers, send/confirmation projection | Перенос hooks вместе с consumers в Phases 2–6/9; удаление заменённых hooks Phase 10, historical events сохраняются | introduced 1B; только logging scope, не compatibility runtime |
| `src/RNAssistant.Office/Tools/OfficeToolExecutor.cs`: validation, safety, confirmation, domain dispatch | Office/Runtime/ToolRuntime + domain tools | Tools | Loop, pipeline, manual execution | Phase 4, vertical slices 6–7; cleanup Phase 10 | current |
| `src/RNAssistant.Core/Models/ToolModels.cs`: ToolDefinition / ToolResult | Core/Tools/ToolDescriptor, ToolPolicy, ToolBinding, ToolResult, ToolExecutionRecord | Tool contracts | Catalog, schema/prompt, authoring, executor, bridge | Phase 4: разделить schema/policy/binding и результат; authoring отдельно Phase 11 | current |
| Host adapters / текущий выбор target | Bound DocumentSession + HostRuntime | HostRuntime | Excel/VBA writes, Office hosts | Phase 5 + Windows fault tests | current |
| `src/RNAssistant.Office/Tools/VbaToolExecutor*.cs`: source/patch, guards, journal, packages | Office/Domains/Vba: patch/canonicalizer/mutation/verifier/journal | VBA | VBA tools, resources, package lifecycle | Phase 6 + read-back/unknown tests; production COM отдельно | current; divergent unknown reproduced 1A |
| `src/RNAssistant.OfficeHosts/ExcelAdapter.cs`: ExecuteTool, WriteRangeByKind, optional bound target | Office/Domains/Excel + OfficeHosts/Excel/ExcelDocumentSession/InteropBackend | Excel/Host | Excel read/write tools, VSTO/native/desktop | Phases 5/7; ActiveWorkbook fallback удалить после bound-session switch и Windows tests | current |
| `src/RNAssistant.Office/Services/ProgressiveToolWorkingSet.cs`: read evidence, replay, Touch/LRU | Core/Tools/ToolPackSnapshot | ToolPack | ConversationRunService, prompt composer, discovery | Phase 8; удалить eviction path и скрытое изменение schemas в run | current |
| `src/RNAssistant.Office/Services/ResourceGatewayService.cs`, ResourceProviderRegistry; `Tools/ResourceToolExecutor.cs` | Office/Resources: read/data plane | Resources | common.resources_list/resolve/search/read, domain providers | Phase 8; сохранить revision pinning, отделить execution outcome | current |
| `src/RNAssistant.Core/Storage/ChatStore.cs`, ChatStore.EventLog.cs, ChatStore.SessionProjection.cs, ChatHeaderReducer.cs | Core/Persistence/IRunStore + deterministic projections | Persistence | Controller, diagnostics/trajectory, history | Phase 9; факты вместо выбора outcome, без dual-write | current; status propagation mapped 1A |
| `web/js/app-chat-state.js`, app-utils.js, app-agent-model.js, app-agent.js | Revisioned runtime projection | UI/Application | Static web UI, ChatState/SendChatResponse | Phase 9: полный switch; old summary absence остаётся unverified | guard projection 1C; model status no longer certifies effects |
| Dynamic authoring, pipelines, HTML/Plan, non-Excel hosts | Optional отдельно квалифицированные контуры | Domain owners | Существующие consumers | Phase 11; не объявлять qualified в Phase 0 | current |

В Phases 0/1A/1B новые compatibility adapters не вводились. Adapter paths 1C/2A
указаны выше и в [PROGRESS.md](PROGRESS.md) с owner/consumers/removal phase.
V2 read adapter 2C1 пока имеет только harness consumers; это introduce stage,
не второй active parser, не historical rewrite и не dual-write.
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
