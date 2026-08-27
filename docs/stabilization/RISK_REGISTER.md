# Stabilization risk register

Исходная база: `v16.0.4`. Приоритеты ниже — стартовая оценка из master plan,
не утверждение о воспроизведённых дефектах. Phase 0 не проверяет runtime/Office.

| ID | Priority | Риск | Владелец | Защита / фаза | Статус |
|---|---|---|---|---|---|
| R01 | P0 | Ложный success после tool failure | AgentKernel | Characterization Phase 1; runtime RunSummary Phase 3 | open |
| R02 | P1 | tLLM protection вместо JSON | ModelProtocol | Stateless protocol retry, Phase 2 | open |
| R03 | P0 | Write применён, ответ потерян | Domain/Host | unknown + reconciliation, Phases 4–7 | open |
| R04 | P0 | Patch направлен не в ту книгу | HostRuntime | Bound DocumentSession, Phase 5 | open |
| R05 | P1 | LRU удалил schema | ToolPack | No eviction in run, Phase 8 | open |
| R06 | P1 | Модель не знает о tool | ToolPack/Discovery | Deterministic core pack, Phase 8 | open |
| R07 | P1 | VBE нормализует source | VBA | Single canonicalizer, Phase 6 | open |
| R08 | P0 | Journal расходится с live state | VBA | Read-only recovery, Phase 6 | open |
| R09 | P0 | Cancellation после COM dispatch | Host/Domain | Unknown/reconciliation, Phases 5–7 | open |
| R10 | P1 | UI показывает устаревший статус | UI/Persistence | Revisioned projection, Phase 9 | open |
| R11 | P0 | Replay меняет outcome | Persistence | Deterministic RunSummary, Phase 9 | open |
| R12 | P2 | Локальное исправление затрагивает десятки файлов | Architecture | Freeze, boundaries, change budget; все фазы | monitored |
| R13 | P2 | Версия/tag на каждый commit | Release process | Правила заменены; repeat-build/commit и release gates tests, Phase 0 | mitigated |
| R14 | P1 | Legacy/new paths сосуществуют бессрочно | Migration | Owner + removal gate, MIGRATION_MAP; Phase 10 | open |
| R15 | P1 | Feature flags становятся второй архитектурой | Application | Временный явный release scope, Phases 10–12 | open |
| R16 | P1 | Новые build metadata / ClickOnce не проверены на Windows | Release process | Сохранить исходную AssemblyVersion 16.0.4.0; qualification до release | open |
| R17 | P2 | Чужие незакоммиченные изменения попадут в Phase 0 | Governance | Проверить исходные файлы и stage только явный список Phase 0 | monitored |
| R18 | P2 | Source archive без Git потеряет build identity | Build | Явные SHA/branch/tree-state properties; отказ вместо скрытого fallback | documented |
| R19 | P1 | Release script ещё не выполнен на release workstation | Release process | Проверить PowerShell workflow до milestone; обычные commits его не запускают | open |

Новые дефекты вне текущей фазы фиксировать здесь или в [BACKLOG.md](BACKLOG.md),
не исправлять попутно. Исключение P0 требует отдельного явно ограниченного изменения.
