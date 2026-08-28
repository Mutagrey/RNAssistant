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
| 2C2 | Adapt/switch/delete: ModelProtocol/prompts/schema/history, run IDs и effective singleton safety | next, отдельное изменение по §14.3; gates в CONVERSATION_RESPONSE_V3.md, R26; новые accepted writes только v3 после switch |
| 3 | Минимальный AgentKernel и runtime-owned RunSummary | Вся Phase 2 завершена, не только boundary extraction |
| 4 | Tool contracts / ToolRuntime | Нормальный, error и unknown сценарии |
| 5 | Bound DocumentSession / HostRuntime | Windows tests смены активной книги и lifetime |
| 6 | VBA vertical slice | Canonicalization, exact patch, journal, read-back/fault matrix |
| 7 | Excel read/write vertical slice | Bound target, write-effect evidence |
| 8 | Resource read plane / immutable ToolPack | Нет LRU eviction во время run |
| 9 | Persistence / UI projection | Replay не принимает execution decisions |
| 10 | Удаление заменённых paths и architecture tests | Нет consumers; закрыть MIGRATION_MAP |
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

Phase 2B добавила bounded provider retry/backoff. Phase 2C1 вводит v3 contract и
read adapter без runtime switch: старые v2 parser/schema/DTO пока нужны живым
consumers. Phase 2C2 должна удалить заменяемые live paths, а не оставить два
production parsers или automatic v2 fallback. Owner/consumers/removal — MIGRATION_MAP.
R26: accepted IDs нужно восстанавливать из всего logical run, включая confirmation
и не попавшие в prompt сообщения; batch-safe set — из effective local authority,
не только false legacy flags. Saved custom v2 prompts и все history forms должны
быть рассмотрены при coordinated cutover; controller/Windows qualification отдельно.
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
