# Stabilization backlog

Новые product features заморожены. Следующая фаза начинается отдельным изменением
после Definition of Done предыдущей, согласно [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Работа | Условие начала / проверки |
|---|---|---|
| 1A | Characterization failure/unknown/no-write, repair/history и status map | done; 7/7, production behavior не изменён |
| 1B | Causal trace и correlation на model/tool/domain/UI boundaries | done; 6/6 targeted, full 320/321 с known baseline failure R22; ограничения в PHASE_1B_CAUSAL_TRACE |
| 1C | Transitional completion guard / runtime execution health | done (host-neutral); red 4 cases → green; 61 targeted harness + 8 UI pass; Windows/controller/WebView не проверены |
| 2 | Извлечь ModelProtocol, stateless bounded repair | next, отдельное изменение; Phase 1 host-neutral coverage готово, Windows риски остаются открыты |
| 3 | Минимальный AgentKernel и runtime-owned RunSummary | ModelProtocol выделен |
| 4 | Tool contracts / ToolRuntime | Нормальный, error и unknown сценарии |
| 5 | Bound DocumentSession / HostRuntime | Windows tests смены активной книги и lifetime |
| 6 | VBA vertical slice | Canonicalization, exact patch, journal, read-back/fault matrix |
| 7 | Excel read/write vertical slice | Bound target, write-effect evidence |
| 8 | Resource read plane / immutable ToolPack | Нет LRU eviction во время run |
| 9 | Persistence / UI projection | Replay не принимает execution decisions |
| 10 | Удаление заменённых paths и architecture tests | Нет consumers; закрыть MIGRATION_MAP |
| 11 | Optional contours | Только после отдельной миграции; не расширять release-critical scope |
| 12 | Release qualification и packaging | Все gates master plan; Windows x64 + Office x64 + VS 2022 |

R20: текущий `MaxAgentFormatRetries=20` разрешает initial + 20 retries. В Phase 2
согласовать явный общий лимит attempts с master plan; не исправлять это попутно в 1A.
R21: на Windows проверить production controller trace wiring, COM boundaries и
реальную WebView delivery; `ui.projected` сейчас фиксирует только построение DTO.
R22: тест `tools: compact catalog rejects removed aliases` ожидает 16 Excel tools,
получает 15; одинаково падает на baseline `a24feb1` и после 1B. Проверить catalog и
ожидание в Phase 8, не менять tool catalog в completion-guard commit.
R23: заменить консервативный legacy result mapping на typed effect evidence в
Phase 4; counts mutating invocations не означают число изменённых объектов или
независимую проверку read-back. Полная lifecycle/projection миграция — Phases 3/9.
Phase 1 host-neutral containment выполнена; production controller, Office и WebView
qualification остаются в R21/R16 и не объявляются выполненными.

## Отложенная проверка versioning

- Проверить VSTO/ClickOnce update/install и assembly binding на Windows до release.
- До такой проверки сохранять историческую AssemblyVersion `16.0.4.0`;
  рекомендацию `16.0.0.0` не применять автоматически.
- Проверить release script на release workstation; обычные commits его не запускают.
- Расширение diagnostics UI и protocol versions остаётся за пределами Phase 0.

## Вне текущего изменения

Незакоммиченные до начала Phase 0 изменения protocol, runtime, OfficeHosts, tests
и web сохраняются отдельно и не считаются выполнением Phase 1.
