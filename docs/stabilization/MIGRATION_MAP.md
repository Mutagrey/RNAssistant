# Current-to-target migration map

Это план миграции, не декларация уже реализованной архитектуры. Phase 0 изменила
governance/versioning; Phase 1A добавила characterization tests и карту, Phase 1B —
только correlation/observability на существующих runtime boundaries.
Текущие domain docs остаются canonical до своей фазы.

| Current path / policy | Target | Owner | Consumers сейчас | Switch / removal gate | Статус |
|---|---|---|---|---|---|
| AGENTS/README: bump + tag после commit | Release-only versioning | Release process | Нет consumers старого правила | Phase 0: правила заменены | removed |
| ValidateRNAssistantVersion | ValidateVersionFormat + отдельные release gates | Build | Нет callers старого target | Phase 0: callers обновлены, старый target удалён без alias | removed |
| AssistantController orchestration | Application Facade | Application | Bridge, Office runtime | Phases 3–5; затем cleanup Phase 10 | current |
| `src/RNAssistant.Office/Services/ConversationRunService.cs` | Core/ModelProtocol + Core/Agent/AgentKernel | Model/Runtime | Agent/Chat/Plan, confirmation continuation | Phase 1C: guard; Phases 2–3: извлечение, удалить старый loop после switch | current; characterized 1A |
| conversation-response v2 / model-owned status | v3 + runtime RunSummary | ModelProtocol | Parser, prompt, accepted history | Phases 2–3; adapter только с owner/consumers; cleanup Phase 10 | current |
| `Services/RunCausalTrace.cs`, ModelTracePersistenceService и trace hooks текущих loop/executor/journal/controller | Наблюдение границ выделенных ModelProtocol / AgentKernel / ToolRuntime / domains / Application | Diagnostics / Application | Текущий loop, top-level executor, VBA journal wrappers, send/confirmation projection | Перенос hooks вместе с consumers в Phases 2–6/9; удаление заменённых hooks Phase 10, historical events сохраняются | introduced 1B; только logging scope, не compatibility runtime |
| `src/RNAssistant.Office/Tools/OfficeToolExecutor.cs`: validation, safety, confirmation, domain dispatch | Office/Runtime/ToolRuntime + domain tools | Tools | Loop, pipeline, manual execution | Phase 4, vertical slices 6–7; cleanup Phase 10 | current |
| `src/RNAssistant.Core/Models/ToolModels.cs`: ToolDefinition / ToolResult | Core/Tools/ToolDescriptor, ToolPolicy, ToolBinding, ToolResult, ToolExecutionRecord | Tool contracts | Catalog, schema/prompt, authoring, executor, bridge | Phase 4: разделить schema/policy/binding и результат; authoring отдельно Phase 11 | current |
| Host adapters / текущий выбор target | Bound DocumentSession + HostRuntime | HostRuntime | Excel/VBA writes, Office hosts | Phase 5 + Windows fault tests | current |
| `src/RNAssistant.Office/Tools/VbaToolExecutor*.cs`: source/patch, guards, journal, packages | Office/Domains/Vba: patch/canonicalizer/mutation/verifier/journal | VBA | VBA tools, resources, package lifecycle | Phase 6 + read-back/unknown tests; production COM отдельно | current; divergent unknown reproduced 1A |
| `src/RNAssistant.OfficeHosts/ExcelAdapter.cs`: ExecuteTool, WriteRangeByKind, optional bound target | Office/Domains/Excel + OfficeHosts/Excel/ExcelDocumentSession/InteropBackend | Excel/Host | Excel read/write tools, VSTO/native/desktop | Phases 5/7; ActiveWorkbook fallback удалить после bound-session switch и Windows tests | current |
| `src/RNAssistant.Office/Services/ProgressiveToolWorkingSet.cs`: read evidence, replay, Touch/LRU | Core/Tools/ToolPackSnapshot | ToolPack | ConversationRunService, prompt composer, discovery | Phase 8; удалить eviction path и скрытое изменение schemas в run | current |
| `src/RNAssistant.Office/Services/ResourceGatewayService.cs`, ResourceProviderRegistry; `Tools/ResourceToolExecutor.cs` | Office/Resources: read/data plane | Resources | common.resources_list/resolve/search/read, domain providers | Phase 8; сохранить revision pinning, отделить execution outcome | current |
| `src/RNAssistant.Core/Storage/ChatStore.cs`, ChatStore.EventLog.cs, ChatStore.SessionProjection.cs, ChatHeaderReducer.cs | Core/Persistence/IRunStore + deterministic projections | Persistence | Controller, diagnostics/trajectory, history | Phase 9; факты вместо выбора outcome, без dual-write | current; status propagation mapped 1A |
| `web/js/app-chat-state.js`, app-utils.js, app-agent-model.js, app-agent.js | Revisioned runtime projection | UI/Application | Static web UI, ChatState/SendChatResponse | Phase 1C: health projection; Phase 9: полный switch; удалить model-owned success | current; static inspection 1A |
| Dynamic authoring, pipelines, HTML/Plan, non-Excel hosts | Optional отдельно квалифицированные контуры | Domain owners | Существующие consumers | Phase 11; не объявлять qualified в Phase 0 | current |

Новые compatibility adapters в Phases 0/1A/1B не вводятся. При вводе adapter фиксировать
owner, оставшихся consumers и removal phase также в [PROGRESS.md](PROGRESS.md).
Массовые переносы файлов, aliases, новые runtime loops и dual-write запрещены.

Путь model status через ChatTurnResult, LastRun, controller/bridge, persistence и UI:
[PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md). Characterization assertions
известного false completion должны смениться safety assertions в Phase 1C, а не стать
требованием обратной совместимости.

Correlation ids, значения stages и ограничения trace:
[PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md). Старые transport `StepId` /
`llm.*` events сохранены для recovery; logical step находится в model `Data.StepId`.
Это диагностическая связь, не alias нового model protocol и не второй storage path.
