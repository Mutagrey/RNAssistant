# Stabilization backlog

Новые product features заморожены. Следующая фаза начинается отдельным изменением
после Definition of Done предыдущей на основном маршруте 0–10 → 12, согласно [master plan](STABILIZATION_MASTER_PLAN.md). Phase 11 — отдельная ветка после stable core, не prerequisite release qualification.

| Phase | Работа | Условие начала / проверки |
|---|---|---|
| 1A | Characterization failure/unknown/no-write, repair/history и status map | done; 7/7, production behavior не изменён |
| 1B | Causal trace и correlation на model/tool/domain/UI boundaries | done; 6/6 targeted, full 320/321 с known baseline failure R22; ограничения в PHASE_1B_CAUSAL_TRACE |
| 1C | Transitional completion guard / runtime execution health | done (host-neutral); red 4 cases → green; 61 targeted harness + 8 UI pass; Windows/controller/WebView не проверены |
| 2A | Выделить IModelProtocol, raw attempts/repair/fallback и typed failure | done; 68 targeted harness pass; v2 и legacy retry semantics сохранены |
| 2B | Общий лимит 1–20 attempts, provider/protocol retry policy | done; 74 targeted harness pass, 4 red→green cases; R20 закрыт host-neutral, fallback работает во время repair |
| 2C1 | Introduce v3 parser/schema/writer, явный v2 read adapter и canonical doc | done; 68 targeted harness pass; active runtime/history всё ещё v2, no cutover |
| 2C2 | Adapt full-turn ID/safety snapshots, current-v3 history reader; удалить unused v2 read adapter | done host-neutral; 86 targeted tests pass; live wire/history остаются v2 |
| 2C3A | Общий active wire owner для runtime/probes, удалить duplicated schema/JSON/validation paths | done host-neutral; 76 targeted tests pass; v2 сохранён |
| 2C3B | R27: сохранять custom prompts, явный review/reset, guard до model preparation/confirmation | done host-neutral/JS; 22 harness + 5 Node pass; active v2 и prompt schema 11 сохранены; Windows pending |
| 2C3C | Switch/delete: shared wire/client/prompts/history/version, complete-context enforcement и explicit old-chat skip/reset | done host-neutral; этот исторический substep завершил v3/schema12, позднее атомарно заменён R29/v4 и 4B/schema14; 100 targeted cases; Windows/live-provider qualification открыта |
| 2 follow-up / 3 consumers | R29/P1: runtime-owned IDs, atomic wire/history v4 | done host-neutral; [evidence](R29_RUNTIME_CALL_IDS.md). Назначение до accepted append, immutable origin/raw mapping, payload/confirmation/replay tests. Windows/live-provider gates открыты; отдельный R29 switch завершён до Phase 4 |
| 3A | Отделить model context/materialization от извлекаемого loop по §15.2 | done host-neutral; ConversationModelSession и существующий AgentTranscript, прежние loop helpers удалены, semantics сохранены |
| 3B1 | Ввести pure AgentKernel, typed evidence и generic ports | done introduce-only; fake model/tool/store tests; production loop остаётся прежним |
| 3B2 | Kernel production switch и existing event-store replay | done host-neutral; guards сохранены, старые loop/accounting удалены; Windows/controller delivery и полная Phase 9 matrix открыты |
| 4A | Typed ToolRuntime / first native resources_list | done host-neutral; [135 targeted checks / evidence](PHASE_4A_TOOL_RUNTIME.md#verification). Source policy, exact registry, dispatch/effect separation, same-stream replay; legacy domain preparation retained |
| 4B | Tool Result v1 wire и removal старых result readers | done host-neutral; [127 targeted checks / evidence](PHASE_4B_TOOL_RESULT_V1.md#verification). Writer/readers/prompts/probes/resource materialization/history gate switched; ProjectLegacy/old wire removed, typed controls preserved. Phase 5 separate |
| 5 | Bound DocumentSession / HostRuntime | 5A access extraction; 5B1 neutral session port + operation gate, guard/preparation/read-back и manual/resource concurrency; [ADR-0005](../decisions/ADR-0005-bound-document-session.md), [evidence](PROGRESS.md#phase-5b1--document-access-gate). 5B2 direct selection/context/catalog reads switched host-neutral. Identity candidate probe готов; next — Windows evidence по [README](../../tests/RNAssistant.ExcelIdentityProbe/README.md), затем production identity/factories/bound object. R04 открыт; независимые 6A/R33/6B допущены явными исключениями, Windows qualification отложена |
| 6A | Pure VBA patch/canonicalization, approved exception при недоступной Windows | done host-neutral; text owners в Core, текущие consumers switched, старые helpers удалены; [evidence](PROGRESS.md#phase-6a--pure-vba-text-extraction). Phase 5/R04 не закрыты |
| 6 / R33 | Перекрывающиеся exact-match вхождения | done host-neutral отдельным semantic fix после 6A; 2 regression tests red→green, 8 targeted pass; [evidence](PROGRESS.md#r33--overlapping-exact-matches). Windows/VBE regression остаётся открытой |
| 6B / R34 | Единый typed VBA read owner; malformed successful snapshots | done host-neutral: executor/catalog переключены на `VbaReader`, duplicate backend command/raw JSON/name helpers удалены; malformed success не публикуется/не кэшируется, valid empty project сохраняет semantics. [Evidence](PROGRESS.md#phase-6b--typed-vbareader). Windows/VBE gate открыт |
| 6 remainder | VBA vertical slice; отдельно оценить необходимость пользовательского package lifecycle для stable | Следующий отдельно согласуемый slice — `VbaMutationService`/`VbaVerifier`, начиная с apply_patch. Затем journal/read-back/fault matrix, no fabricated terminal при persistence failure; raw CAS отдельно от comparable text, общий package journal нужен rename, scope пока не сокращён |
| 7 | Excel read/write vertical slice | Bound target, write-effect evidence |
| 8 | Resource read plane / immutable ToolPack | Заменить внешний lifecycle без переделки kernel; bounded pinned schema/policy/binding, compaction materialization, atomic admission; no LRU eviction и no CAS transport (R30) |
| UI security / R35 | Обновить уязвимый existing DOMPurify без начала Phase 9 | done host-neutral: `3.1.6 → 3.4.14`, exact npm provenance/SHA-256/licenses; Markdown boundary/CSP не менялись, Windows WebView gate открыт |
| 9 | Persistence / UI projection | Расширить minimal replay Phase 3: один event authority, mandatory durable barriers/result-append faults, typed verification projection; replay не принимает execution decisions |
| 9A–9C / R32 | Сквозной журнал запуска и общий JSON viewer | **9A/9B1 и consumer switches через Context/Tools/VBA 9B2B2 done host-neutral; следующий 9B2B3 artifacts.** [Требования](R32_DIAGNOSTICS_JSON_VIEWER.md), [vendor/UI evaluation](R32_VENDOR_UI_EVALUATION.md): correlated query → lossless bounded viewer → раскрываемый журнал. Artifact/Markdown, 9B3/9C отдельны; Windows/WebView gate открыт |
| 9A / R37 | Accepted call ошибочно сохранялся как result при ненативной instruction role | done host-neutral: writer классифицирует по runtime-owned `AcceptedCallOrigin`; read-only adapter сохраняет диагностику ранее затронутых current-v4 streams до 9C/reset, без history rewrite |
| 9B / R36 | Vendor provenance/offline gate | До любого UI vendor switch: полный manifest versions/hashes/licenses/transitive assets, fail-fast zero-network, allowlisted pinned local-worker lifecycle и explicit WASM/font policy. DOMPurify/R35 закрыт отдельно; остальной inventory открыт |
| 10 | Финальная структурная сверка и architecture tests | Чистка и проверки границ уже выполняются при switch; дополнить coverage, закрыть core-миграции; optional consumers оставить с gates Phase 11 |
| 12 | Release qualification и packaging | Gates основного маршрута; Windows x64 + Office x64 + VS 2022; Phase 11 не блокирует |
| 11 | Optional contours | После stable core либо отдельно согласованный post-beta milestone; не расширять release-critical scope автоматически |

До каждого switch сверять его scope и acceptance с [архитектурным аудитом](RISK_REGISTER.md#архитектурный-аудит-2026-08-28) и соответствующим разделом master plan. Уточнение target contracts не запускает будущую фазу и не закрывает runtime gates; R29 исправлен отдельным v4 protocol change; его qualification нельзя подменять реализацией Phase 4.

R20 закрыт в 2B: `MaxAgentFormatRetries=20` допускает ровно двадцать protocol responses,
включая первую. Provider failures и один schema fallback имеют отдельные бюджеты.
R21: на Windows проверить production controller trace wiring, COM boundaries и
реальную WebView delivery; `ui.projected` сейчас фиксирует только построение DTO.
R22: тест `tools: compact catalog rejects removed aliases` ожидает 16 Excel tools,
получает 15; одинаково падает на baseline `a24feb1` и после 1B. Проверить catalog и
ожидание в Phase 8, не менять tool catalog в ModelProtocol commit.
R23: typed runtime/result введены в Phase 4; legacy domain mapping снимается при
handler switches Phases 6–7/11; counts mutating invocations не означают число изменённых объектов или
независимую проверку read-back. Полная lifecycle/projection миграция — Phases 3/9.
R24: проверить traffic/memory budget повторной передачи media через реальные
endpoint retries; одна materialization сохраняется до окончания protocol step,
затем release в `finally`. URI/provider/CAS не менялись.
R25: перед release проверить реальную latency, timeout и стоимость генерации при
двух provider retries; raw ceiling N+3 на step, не на весь conversation run.
Phase 1 host-neutral containment выполнена; production controller, Office и WebView
qualification остаются в R21/R16 и не объявляются выполненными.

Phase 2C3C переключает единый ModelProtocolWire/client/prompts/history на v3 и удаляет
live v2 DTO/parser/schema/typed-ID helper. R26 host-neutral enforcement проверен:
полная история, current-v3 confirmation, run-wide IDs, singleton safety и explicit
old-chat skip/reset до model preparation. Невалидный attempt не исполняется частично
и не попадает в accepted history; старые streams не переписываются.
R27: schema 11 custom text сохраняется; explicit review/reset проверены с v3 defaults
и новым prompt marker 12. Production controller ordering, WebView/DPAPI и реальные
providers всё ещё требуют qualification. До Phase 4 external/unclassified calls
остаются singleton; positive local-read registry заменяется typed ToolPolicy.
Точные проверки и границы: [Phase 2C3C](PHASE_2C3C_V3_CUTOVER.md).
`Failure.Cause`, прежний loop/builder и controller direct-confirm execution удалены
в Phase 3B2. Legacy single-result mapping и local-read registry заменяются Phase 4;
полная persistence/UI matrix — Phase 9. [Evidence](PHASE_3B2_KERNEL_CUTOVER.md).

## Отложенная проверка versioning

- Проверить VSTO/ClickOnce update/install и assembly binding на Windows до release.
- До такой проверки сохранять историческую AssemblyVersion `16.0.4.0`;
  рекомендацию `16.0.0.0` не применять автоматически.
- Проверить release script на release workstation; обычные commits его не запускают.
- Расширение diagnostics UI и protocol versions остаётся за пределами Phase 0.

## Вне текущего изменения

- R31/P2 закрыт host-neutral в 4B: built-in prompt authoring больше не просит model-owned ID; schema14 и regression требуют runtime ownership и отличают status=ok от effect evidence. Wire/parser/kernel R29 не менялись; live skill incident не воспроизведён. [Evidence](PHASE_4B_TOOL_RESULT_V1.md#verification).

Незакоммиченные до начала Phase 0 изменения protocol, runtime, OfficeHosts, tests
и web сохраняются отдельно и не считаются выполнением Phase 1.

## Deferred pipelines (2026-08-28)

Отключены по решению пользователя: execution, discovery, storage loading/authoring и UI закрыты; старый executor/parser и вложенные зависимости удалены. Не восстанавливать по пути Phases 3–10/12. Вернуться только отдельной задачей Phase 11 после stable core, через общие ToolRuntime/contracts, без поддержки старых pipelines. Старые файлы не мигрируются и автоматически не удаляются. Это сокращение scope, не незавершённый adapter и не новый prerequisite миграции.
