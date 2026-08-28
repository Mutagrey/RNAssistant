# Stabilization backlog

Новые product features заморожены. Следующая фаза начинается отдельным изменением
после Definition of Done предыдущей, согласно [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Работа | Условие начала / проверки |
|---|---|---|
| 1A | Characterization failure/unknown/no-write, repair/history и status map | done; 7/7, production behavior не изменён |
| 1B | Causal trace и correlation на model/tool/domain/UI boundaries | done; 6/6 targeted, full 320/321 с known baseline failure R22; ограничения в PHASE_1B_CAUSAL_TRACE |
| 1C | Transitional completion guard / runtime execution health | done (host-neutral); red 4 cases → green; 61 targeted harness + 8 UI pass; Windows/controller/WebView не проверены |
| 2A | Выделить IModelProtocol, raw attempts/repair/fallback и typed failure | done; 68 targeted harness pass; v2 и legacy retry semantics сохранены |
| 2B | Общий лимит 1–20 attempts, provider/protocol retry policy | done; 74 targeted harness pass, 4 red→green cases; R20 закрыт host-neutral, fallback работает во время repair |
| 2C1 | Introduce v3 parser/schema/writer, явный v2 read adapter и canonical doc | done; 68 targeted harness pass; active runtime/history всё ещё v2, no cutover |
| 2C2 | Adapt full-turn ID/safety snapshots, current-v3 history reader; удалить unused v2 read adapter | done host-neutral; 86 targeted tests pass; live wire/history остаются v2 |
| 2C3 | Switch/delete: client/prompts/schema/history/version, complete-context enforcement и explicit old-chat skip/reset | next, отдельное изменение по §14.3; все gates в CONVERSATION_RESPONSE_V3.md; новые accepted writes только v3 после switch |
| 3 | Минимальный AgentKernel и runtime-owned RunSummary | Вся Phase 2 завершена, не только boundary extraction |
| 4 | Tool contracts / ToolRuntime | Нормальный, error и unknown сценарии |
| 5 | Bound DocumentSession / HostRuntime | Windows tests смены активной книги и lifetime |
| 6 | VBA vertical slice | Canonicalization, exact patch, journal, read-back/fault matrix |
| 7 | Excel read/write vertical slice | Bound target, write-effect evidence |
| 8 | Resource read plane / immutable ToolPack | Нет LRU eviction во время run |
| 9 | Persistence / UI projection | Replay не принимает execution decisions |
| 10 | Финальная структурная сверка и architecture tests | Локальная чистка уже выполняется при каждом switch; закрыть MIGRATION_MAP |
| 11 | Optional contours | Только после отдельной миграции; не расширять release-critical scope |
| 12 | Release qualification и packaging | Все gates master plan; Windows x64 + Office x64 + VS 2022 |

R20 закрыт в 2B: `MaxAgentFormatRetries=20` допускает ровно двадцать protocol responses,
включая первую. Provider failures и один schema fallback имеют отдельные бюджеты.
R21: на Windows проверить production controller trace wiring, COM boundaries и
реальную WebView delivery; `ui.projected` сейчас фиксирует только построение DTO.
R22: тест `tools: compact catalog rejects removed aliases` ожидает 16 Excel tools,
получает 15; одинаково падает на baseline `a24feb1` и после 1B. Проверить catalog и
ожидание в Phase 8, не менять tool catalog в ModelProtocol commit.
R23: заменить консервативный legacy result mapping на typed effect evidence в
Phase 4; counts mutating invocations не означают число изменённых объектов или
независимую проверку read-back. Полная lifecycle/projection миграция — Phases 3/9.
R24: проверить traffic/memory budget повторной передачи media через реальные
endpoint retries; одна materialization сохраняется до окончания protocol step,
затем release в `finally`. URI/provider/CAS не менялись.
R25: перед release проверить реальную latency, timeout и стоимость генерации при
двух provider retries; raw ceiling N+3 на step, не на весь conversation run.
Phase 1 host-neutral containment выполнена; production controller, Office и WebView
qualification остаются в R21/R16 и не объявляются выполненными.

Phase 2C2 передаёт immutable accepted-ID/safety snapshots на boundary, восстанавливает
весь logical turn при confirmation и удаляет unused v2 read adapter вместе с tests/include.
Live v2 parser/schema/DTO и typed-ID helper пока нужны действующим consumers; удалить
в coordinated switch 2C3, без automatic v2 fallback. Owner/reason/gate — MIGRATION_MAP.
R26 частично закрыт wiring/tests; 2C3 должна enforce complete context до dispatch и
в каждом v3 parse, проверить real v3 writer/confirmation и explicit skip/reset старого чата.
Saved custom v2 prompts (включая instruction-authoring skill), compatibility probes,
schema и writes/marker переключаются согласованно; старые streams не переписываются.
Без нового ToolPolicy все external/unclassified/pipelines остаются singleton;
positive local-read registry заменяется typed metadata в Phase 4.
Controller/Windows qualification остаётся отдельной.
`Failure.Cause` временно сохраняет прежний controller exception path; удалить при
переключении на AgentKernel в Phase 3, не вводить второй loop.

## Отложенная проверка versioning

- Проверить VSTO/ClickOnce update/install и assembly binding на Windows до release.
- До такой проверки сохранять историческую AssemblyVersion `16.0.4.0`;
  рекомендацию `16.0.0.0` не применять автоматически.
- Проверить release script на release workstation; обычные commits его не запускают.
- Расширение diagnostics UI и protocol versions остаётся за пределами Phase 0.

## Вне текущего изменения

Незакоммиченные до начала Phase 0 изменения protocol, runtime, OfficeHosts, tests
и web сохраняются отдельно и не считаются выполнением Phase 1.
