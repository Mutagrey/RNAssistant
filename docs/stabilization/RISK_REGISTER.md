# Stabilization risk register

Исходная база: `v16.0.4`. Приоритеты ниже — стартовая оценка из master plan,
не утверждение о воспроизведённых дефектах. Phase 0 не проверяла runtime/Office.
Отдельно отмеченные результаты Phase 1A получены с fake LLM/Office, не на реальном COM.
Phase 1B проверяет host-neutral correlation; production controller/Office/WebView
не исполнялись. Known baseline failure указан отдельно от новых trace tests.
Phase 1C проверяет runtime guard, replay/DTO и JS-проекцию без Windows execution.
Phase 2A проверяет ModelProtocol с fake endpoint; live tLLM не проверен.
Phase 2B закрывает R20 на host-neutral tests; provider retries проверены с fake
transport и injected delay, без реального network/backoff qualification.

| ID | Priority | Риск | Владелец | Защита / фаза | Статус |
|---|---|---|---|---|---|
| R01 | P0 | Model completed скрывает write error/unknown или отсутствие write | AgentKernel / Application / UI | Guard Phase 1C: red→green + отдельный UI warning; RunSummary Phase 3; production validation R21 | contained host-neutral 1C; Windows qualification open |
| R02 | P1 | tLLM protection вместо JSON | ModelProtocol | 2A/2B: typed boundary, clean repair, общий лимит и fake protection/HTML tests; v3 ещё в Phase 2, live endpoint qualification отдельно | contained for fake content 2A/2B; open |
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
| R20 | P1 | Лимит 20 retries допускал 21 invalid response вместо 20 attempts | ModelProtocol | Phase 2B: initial включён в total 1–20, valid на 20 принимается; provider retries/fallback считаются отдельно; red→green boundary tests | resolved host-neutral 2B |
| R21 | P2 | Optional trace может быть неполным; controller wiring/реальная UI delivery не проверены | Diagnostics / Application | Fixed-stage error log без payload; no effect decisions from trace; `ui.projected` — только DTO, CAS failure допускает пропуск marker после release lease; Windows validation в Phases 1C/5–9/12 | documented 1B; open |
| R22 | P1 | Full harness: compact catalog ожидает 16 Excel tools, получает 15 | ToolPack / Tests | Проверить актуальный catalog и expectation в Phase 8; targeted failure воспроизведён на baseline a24feb1 в отдельном disposable worktree | reproduced baseline + 1B; open |
| R23 | P2 | Legacy ToolResult не всегда различает частичный/неизвестный effect; успешный mutating call может быть no-op или иметь слабую domain verification | ToolRuntime / Domains | 1C консервативно маркирует partial/missing/uncertain как unknown; counts — top-level вызовы, не document diff; заменить adapter typed evidence Phase 4, domain qualification Phases 6/7 | documented 1C; open |
| R24 | P2 | Media сохраняются и могут отправляться повторно на protocol repair: больше traffic и дольше lifetime | ModelProtocol / Resources | Один materialized accepted prompt, bounded retry/budget и release в finally; fake image integration pass; проверить реальные media/endpoint budgets до qualification Phase 12 | documented 2A; open |
| R25 | P2 | Provider retry после timeout/потери ответа может повторить оплачиваемую генерацию и увеличить latency | ModelProtocol / Release | Не более двух transient retries на весь step, delays 1s/2s с cancellation; raw ceiling N+3; no Office tool replay, no auth/429 retry; проверить реальные timeout/media/endpoint budgets до Phase 12 | documented 2B; open |

Новые дефекты вне текущей фазы фиксировать здесь или в [BACKLOG.md](BACKLOG.md),
не исправлять попутно. Исключение P0 требует отдельного явно ограниченного изменения.
