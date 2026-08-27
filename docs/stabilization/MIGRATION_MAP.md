# Current-to-target migration map

Это план миграции, не декларация уже реализованной архитектуры. В Phase 0 меняется
только governance/versioning. Текущие domain docs остаются canonical до своей фазы.

| Current path / policy | Target | Owner | Consumers сейчас | Switch / removal gate | Статус |
|---|---|---|---|---|---|
| AGENTS/README: bump + tag после commit | Release-only versioning | Release process | Нет consumers старого правила | Phase 0: правила заменены | removed |
| ValidateRNAssistantVersion | ValidateVersionFormat + отдельные release gates | Build | Нет callers старого target | Phase 0: callers обновлены, старый target удалён без alias | removed |
| AssistantController orchestration | Application Facade | Application | Bridge, Office runtime | Phases 3–5; затем cleanup Phase 10 | current |
| ConversationRunService | ModelProtocol + AgentKernel | Model/Runtime | Agent/Chat/Plan | Phases 2–3; удалить старый loop после switch | current |
| conversation-response v2 / model-owned status | v3 + runtime RunSummary | ModelProtocol | Parser, prompt, accepted history | Phases 2–3; adapter только с owner/consumers; cleanup Phase 10 | current |
| ToolResult / OfficeToolExecutor orchestration | Tool contracts + ToolRuntime / domain tools | Tools | Loop, pipeline, adapters | Phase 4, vertical slices 6–7; cleanup Phase 10 | current |
| Host adapters / текущий выбор target | Bound DocumentSession + HostRuntime | HostRuntime | Excel/VBA writes, Office hosts | Phase 5 + Windows fault tests | current |
| VbaToolExecutor* | VBA patch/canonicalization/mutation services | VBA | VBA tools, journal/packages | Phase 6 + read-back/unknown tests | current |
| Excel host tool implementations | Excel domain vertical slice | Excel | Excel read/write tools | Phase 7 + Windows validation | current |
| ProgressiveToolWorkingSet / LRU schemas | Immutable ToolPack snapshot | ToolPack | Prompt composer, discovery | Phase 8; удалить eviction path | current |
| ResourceGatewayService / providers | Resource Fabric как read/data plane | Resources | common.resources_*, domain providers | Phase 8; сохранить revision pinning | current |
| Session event store / trajectory projections | Run Store facts + deterministic replay | Persistence | Controller, diagnostics, history | Phase 9; без dual-write | current |
| WebView status rendering | Revisioned runtime projection | UI | Static web UI, bridge | Phase 9; удалить старое выведение outcome | current |
| Dynamic authoring, pipelines, HTML/Plan, non-Excel hosts | Optional отдельно квалифицированные контуры | Domain owners | Существующие consumers | Phase 11; не объявлять qualified в Phase 0 | current |

Новые compatibility adapters в Phase 0 не вводятся. При вводе adapter фиксировать
owner, оставшихся consumers и removal phase также в [PROGRESS.md](PROGRESS.md).
Массовые переносы файлов, aliases, новые runtime loops и dual-write запрещены.
