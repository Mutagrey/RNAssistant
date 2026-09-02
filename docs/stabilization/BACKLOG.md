# Stabilization backlog

Новые product features заморожены. Следующая фаза начинается отдельным изменением
после Definition of Done предыдущей на основном маршруте 0–10 → 12, кроме явно
зафиксированного в master §16.1 режима dependency-safe deferred qualification:
Windows-blocked gate остаётся открыт, а независимый обязательный slice идёт дальше.
Новые optional product contours Phase 11 не являются prerequisite release
qualification; mandatory 11T existing-tool migration/final active-legacy cleanup
и user-requested R61 all-tool contract correction, напротив, обязательны до
Phase 12. Artifact Library milestone 11A отдельно и явно допущен пользователем
раньше stable core параллельно WQ.

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
| 5 | Bound DocumentSession / HostRuntime | 5A access extraction; 5B1 neutral session port + operation gate, guard/preparation/read-back и manual/resource concurrency; [ADR-0005](../decisions/ADR-0005-bound-document-session.md), [evidence](PROGRESS.md#phase-5b1--document-access-gate). 5B2 direct selection/context/catalog reads switched host-neutral. Atomic 11T0/7D вводит production identity/factories/bound object с текущим `RuntimeKey` exact workbook lifetime; WQ0 затем проверяет допущение как mandatory release evidence. R04 остаётся открыт до qualification |
| 6A | Pure VBA patch/canonicalization, approved exception при недоступной Windows | done host-neutral; text owners в Core, текущие consumers switched, старые helpers удалены; [evidence](PROGRESS.md#phase-6a--pure-vba-text-extraction). Phase 5/R04 не закрыты |
| 6 / R33 | Перекрывающиеся exact-match вхождения | done host-neutral отдельным semantic fix после 6A; 2 regression tests red→green, 8 targeted pass; [evidence](PROGRESS.md#r33--overlapping-exact-matches). Windows/VBE regression остаётся открытой |
| 6B / R34 | Единый typed VBA read owner; malformed successful snapshots | done host-neutral: executor/catalog переключены на `VbaReader`, duplicate backend command/raw JSON/name helpers удалены; malformed success не публикуется/не кэшируется, valid empty project сохраняет semantics. [Evidence](PROGRESS.md#phase-6b--typed-vbareader). Windows/VBE gate открыт |
| 6C | `apply_patch` mutation boundary + common module verifier/journal owner | done host-neutral: full patch workflow moved to `Office.Vba.VbaMutationService`, module verification/assessment to `VbaVerifier`; old patch path/common duplicates removed. [Evidence](PHASE_6C_VBA_MUTATION_SERVICE.md). Windows/VBE gate open |
| 6D | Typed VBA mutation outcome + fault/persistence boundary | done host-neutral: narrow document/backend/journal ports; `ToolCommand`/`ToolResult` removed from service boundary; one Tools mapper; `ok/error/unknown`, no string rollback inference/public journal status/unknown retry; terminal failure leaves preparation open. [Evidence](PHASE_6D_VBA_MUTATION_OUTCOME.md). Windows/COM/VBE gate open |
| 6E | Whole-module write ownership | done host-neutral: `upsert/createOnly/updateOnly` normalization/guard/journal/create-or-replace/source+type verification moved to typed domain service; executor write helpers removed; same-code/different-type create race is unknown. [Evidence](PHASE_6E_VBA_WHOLE_MODULE_WRITE.md). Windows/COM/VBE gate open |
| 6F | Delete ownership | done host-neutral: existing-target/observation guard, type refusal, journal, compare-and-swap backend and verified absence moved to typed domain service; executor delete workflow/helpers removed. [Evidence](PHASE_6F_VBA_DELETE.md). Windows/COM/VBE gate open |
| 6G | Restore ownership | done host-neutral: exact backup selection plus backup/current-state guard, dry-run, journal, typed create-or-replace and source/type verification moved to typed domain service; executor restore workflow/helpers removed. [Evidence](PHASE_6G_VBA_RESTORE.md). Windows/COM/VBE gate open |
| 6H / 6I / 6J | Package/rename scope and ownership | done host-neutral: 6H audit; 6I typed package lifecycle/R41; 6J typed rename guard/backend/read-back/recovery and final executor compound-path removal. Dynamic definition authoring stays Phase 11; raw CAS stays separate from comparable text; Windows/VBE gate remains open. [6I](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md), [6J](PHASE_6J_VBA_RENAME.md) |
| 7 | Excel read/write vertical slice | 7A scope, 7B typed reads, 7C verified `write_range` and atomic 11T0/7D bound backend done host-neutral. WQ0/WQ-EXCEL remain mandatory deferred evidence. [7B](PHASE_7B_EXCEL_READ.md), [7C](PHASE_7C_EXCEL_WRITE.md), [11T0/7D](PHASE_11T0_EXCEL_BOUND_CUTOVER.md) |
| 8 | Resource read plane / immutable ToolPack | **8A–8D done host-neutral:** immutable execution snapshot; finite core + atomic optional admission without LRU; durable accepted turn chain; all four exact native read-only resource handlers over one gateway. `ResourceRef`, bounded readers, URI/revision/cursors and request-local media hydration remain one data plane without a second CAS transport or AgentKernel changes. [8A](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [8B](PHASE_8B_CALLABLE_TOOL_PACK.md), [8C](PHASE_8C_TOOL_PACK_EVENTS.md), [8D](PHASE_8D_RESOURCE_DATA_PLANE.md) |
| UI security / R35 | Обновить уязвимый existing DOMPurify без начала Phase 9 | done host-neutral: `3.1.6 → 3.4.14`, exact npm provenance/SHA-256/licenses; Markdown boundary/CSP не менялись, Windows WebView gate открыт |
| 9 | Persistence / UI projection | **9A–9D5 done host-neutral:** R32 journal/viewer, R45 recovery, R46 closed `IEventStore`, R47 minimal `IConversationStore`, R48 immutable `RunViewState` + revision ordering and flat/model-status UI removal. [9D1](PHASE_9D1_PERSISTENCE_AUDIT.md), [9D2](PHASE_9D2_RUNSTORE_RECOVERY.md), [9D3](PHASE_9D3_TYPED_EVENT_STORE.md), [9D4](PHASE_9D4_CONVERSATION_STORE.md), [9D5](PHASE_9D5_RUN_VIEW_STATE.md). Windows/WebView/restart/multi-window acceptance remains WQ-UI |
| 9A–9C / R32 | Сквозной журнал запуска и общий JSON viewer | **9A/9B/9C UI done host-neutral.** Correlated query → общий lossless bounded viewer → раскрываемый latest/exact run journal с direct Agent/error navigation. [Evidence 9C](PHASE_9C_RUN_JOURNAL_UI.md). Full Windows/WebView/reload/confirmation/live-append acceptance открыт; Diff2Html не admitted без source-owned unified diff |
| 9A / R37 | Accepted call ошибочно сохранялся как result при ненативной instruction role | done host-neutral: writer использует runtime-owned `AcceptedCallOrigin`; historical inference удалена по explicit reset decision. Wrong-type retained operation остаётся exact incompatible evidence и исключена из tool-execution без rewrite |
| 9B / R36 | Vendor provenance/offline gate | done host-neutral: exact manifest 36 runtime files, versions/hashes/licenses/transitive decisions; KaTeX WOFF2-only, Feather source attribution, fail-closed CSP/worker/WASM policy, 5/5 tests. [Evidence](R36_WEB_VENDOR_GATE.md); Windows WebView2 gate открыт |
| 9B3 / R38 | Bounded tree vendor switch | done host-neutral: Web Awesome ESM отложен из-за current `file://` host; Wunderbaum 0.14.1 UMD/CSS через local-array `TreeAdapter` переключил один HTML workspace/artifact tree, manifest 38 files. [Evidence](R38_TREE_VENDOR_SWITCH.md); другие trees/virtual host/Windows gate открыты |
| 9B4 / R39 | Compact diff vendor gate | done host-neutral without runtime/vendor change: действующие consumers имеют exact before/after, но не authoritative unified diff; Diff2Html не admitted, второй diff algorithm запрещён. [Evidence](R39_DIFF_VENDOR_GATE.md); повторная оценка только после source-owned contract |
| 10 | Финальная структурная сверка и architecture tests | **done host-neutral:** 10A–10D dependency/physical/canonical audit; host helpers and application façade moved; resource legacy projection removed; project includes and architecture suite pass; R49 fixed host-neutral. [Audit](PHASE_10A_BOUNDARY_AUDIT.md), [10B1](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md), [10B2](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md), [10C1](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md), [10C2](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md), [10D](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md) |
| WQ-A | In-app Qualification Center и расширяемые host packs | **WQ-A0–A5 done host-neutral:** A3 — единый identity owner/helper и `excel.wq0.identity`; A4 — closed versioned suite catalog с fail-closed readiness; A5 — detached signed exact-build provenance и complete-only `release.candidate`. Отсутствующий production adapter/environment остаётся N/A. [Contract](../qualification.md), [A4](WQ_A4_SUITE_CATALOG.md), [A5](WQ_A5_BUILD_EVIDENCE.md). Next Milestone WQ: real Windows/live runs, evidence signing and release admission |
| 12 | Release qualification и packaging | Gates основного маршрута; Windows x64 + Office x64 + VS 2022; mandatory 11T existing-tool migration/active-legacy cleanup completed; R61 all-tool contract correction and post-cutover WQ evidence required |
| 11 | Migration and optional contours | 11A lifecycle/Library, 11B exact Plan, 11C complete HTML, 11D1 bounded text/Markdown viewers, atomic 11T0/7D bound Excel and 11T1–11T5 all current typed Excel families done host-neutral; pre-R37 trajectory inference removed. Mandatory tool route continues with independently bound Word, PowerPoint and Outlook verticals → direct VBA/controller/custom authoring → final removal of generic host catalog/dispatch and legacy definition/result/UI bridges → Windows WQ0/full matrix before Phase 12. Каждый switch удаляет свой старый path; expansions идут отдельно after evals. Новые optional Library/Host Fabric/Browser/Automation capabilities не становятся обязательными. Pipelines remain disabled. [Master](STABILIZATION_MASTER_PLAN.md#phase-11--migration-and-optional-contours), [11T5](PHASE_11T5_EXCEL_CHARTS.md), [Artifacts](../artifact-library.md), [11D1](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md), [Tools](../tool-library.md), [migration](MIGRATION_MAP.md), [Host Fabric](../host-fabric.md). |
| 11O / R61 | Deep audit and correction of every model-facing tool contract, followed by UI-only built-in documentation and typed Library Test/Implementation UX fixes | Explicit mandatory stabilization contour after the reported Windows rebuild and before final Milestone WQ/Phase 12. Inventory first; runtime-owned URI/UUID/revision/cursor/guard state is removed from minimal intent schemas one family at a time without aliases or dual execution. Final live-provider/WQ-PACK and WebView2 evidence must use the post-cutover catalog. [Contract](../tool-library.md#mandatory-all-tool-contract-audit-r61), [failure/ownership audit](R61_TOOL_CONTRACT_AUDIT.md) |

До каждого switch сверять его scope и acceptance с [архитектурным аудитом](RISK_REGISTER.md#архитектурный-аудит-2026-08-28) и соответствующим разделом master plan. Уточнение target contracts не запускает будущую фазу и не закрывает runtime gates; R29 исправлен отдельным v4 protocol change; его qualification нельзя подменять реализацией Phase 4.

R20 закрыт в 2B: `MaxAgentFormatRetries=20` допускает ровно двадцать protocol responses,
включая первую. Provider failures и один schema fallback имеют отдельные бюджеты.
R21: на Windows проверить production controller trace wiring, COM boundaries и
реальную WebView delivery; `ui.projected` сейчас фиксирует только построение DTO.
R22 закрыт в 8A: audited public catalogs содержат Excel 15, Word 9, PowerPoint 9 и
Outlook 5 tools; stale count expectations исправлены без изменения catalog. Тест
одновременно сохраняет explicit rejection удалённых aliases.
R23: typed runtime/result введены в Phase 4; legacy domain mapping снимается при
handler switches Phases 6–7/11T; counts mutating invocations не означают число изменённых объектов или
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
