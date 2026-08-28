# Stabilization progress

Current target: 16.1.0
Current phase: Phase 2
Current task: Phase 2C1 завершена (introduce v3 parser/schema/writer + explicit v2 read adapter, без runtime switch). Следующая — Phase 2C2, adapt/switch/delete по cutover gates; active protocol/history остаются v2. Phase 3 не начата. Windows/controller/WebView validation pending; known baseline test failure: R22

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | `10e52bf` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | done (host-neutral) | 1A: `a24feb1`; 1B: `5df587b`; 1C: `40282c0` | 61 targeted harness + 8 UI pass; red→green 4 cases; ValidateVersionFormat pass; last full 320/321 (R22) | not performed | 1A/1B/1C done; production Windows qualification остаётся открытой |
| 2 | in progress | 2A: `d911826`; 2B: `a51bdda`; 2C1: этот commit `feat(protocol): introduce v3 contract and explicit v2 read adapter` | 2C1: 68 targeted harness pass; one numeric failure red→green; ValidateVersionFormat pass | not performed | V3 contract/read adapter введены, но runtime cutover не выполнен; R20 закрыт, R26 — cutover gate |
| 3 | pending | — | — | — | AgentKernel |
| 4 | pending | — | — | — | ToolRuntime |
| 5 | pending | — | — | — | Bound DocumentSession |
| 6 | pending | — | — | — | VBA vertical slice |
| 7 | pending | — | — | — | Excel vertical slice |
| 8 | pending | — | — | — | Resource Fabric / ToolPack |
| 9 | pending | — | — | — | Persistence / UI projection |
| 10 | pending | — | — | — | Physical cleanup / architecture tests |
| 11 | pending | — | — | — | Optional contours |
| 12 | pending | — | — | — | Release hardening / qualification |

## Phase 0 substeps

- Ветка создана от historical baseline; master plan скопирован без изменений.
- Созданы progress, risk register, backlog и migration map.
- Исходные незакоммиченные изменения runtime/tests/UI не относятся к Phase 0; не менять и не включать в commit.
- AGENTS/README: feature freeze, обязательный порядок фаз, per-commit bump/tag отменены.
- Product target однократно изменён `16.0.4 → 16.1.0-dev`; повторного повышения нет.
- Ordinary validation не сравнивает версию с HEAD; release checks отделены и не создают tags.
- Добавлены CHANGELOG, canonical operations docs, ADR-0007 и явный release script без push по умолчанию.
- Build metadata содержит product/SHA/UTC/branch/channel/clean-or-dirty; AssemblyVersion сохранена `16.0.4.0`.
- Старый validation target удалён без alias; runtime/protocol/tools/resources/VBA/UI/persistence не менялись этим этапом.
- В репозитории не создаётся новый tag; `v16.0.4` остаётся исторической точкой.

## Phase 0 verification

- Baseline: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"` — 2/2 pass до изменений versioning.
- После изменений та же команда — 7/7 pass; весь linked host-neutral source set скомпилирован.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Проверены повторные builds/commits без bump, invalid metadata, dirty/staged tree, release tag matching, dev rejection, changelog, local/remote tag collisions и SDK/old-style assembly metadata.
- Git fixtures создаются и удаляются только во временных каталогах; настоящий origin и его tags не изменяются.
- Полный набор runtime tests не запускался: выбран минимальный build/versioning filter, production behavior не менялся.
- PowerShell release script не запускался (`pwsh` отсутствует); Windows x64 + Office x64 + VS 2022 / VSTO / ClickOnce — not performed.

## Phase 1A substeps

- Baseline — `10e52bf`, clean working tree. Производственные файлы не изменены.
- Прослежены model status → ChatTurnResult / accepted message → LastRun → controller/bridge → storage/header → UI.
- Current-to-target map уточнена для ConversationRunService, OfficeToolExecutor, ToolDefinition, ProgressiveToolWorkingSet, VBA executors, Excel adapter, Resource Fabric, persistence и UI.
- Воспроизведены completed после write error, journal unknown и отсутствующего write; сохранён нормальный write ok + final.
- Проверены valid response на запросе 20, отказ после 20 invalid responses и отсутствие rejected content/reasoning/repair instructions в accepted history.
- R01 подтверждён host-neutral тестами; исправление не выполнено. Green characterization не является green safety gate.
- R20: текущий лимит допускает initial + 20 retries (21 request). Поведение не менялось; исправление семантики attempts отложено в Phase 2.
- Новые adapters, protocols, runtime health fields и causal trace не вводились.
- Version остаётся `16.1.0-dev`; tag не создаётся.

## Phase 1A verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent: explicit response status"` — baseline 1/1 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — 7/7 pass.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Полный harness не запускался; весь linked source скомпилирован таргетированным запуском.
- Controller/bridge/UI проверены чтением кода, без интеграционного запуска.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM — not performed.
- Подробные доказательства и границы: [PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md).

## Phase 1B substeps

- Baseline — `a24feb1`, clean working tree; одна тема: causal trace, без completion guard.
- Logical step создаётся до первого model request; repair/fallback сохраняют step и получают отдельные modelAttemptId.
- Request/rejected/accepted diagnostics связаны с transport RequestId; accepted trace связывает точные toolCallIds.
- Top-level executor отмечает start/completion без изменения validation, dispatch, результата или retry.
- Journalled VBA module/rename/package action получает prepared/dispatched/verified markers с существующим mutationId; journal и read-back не меняются.
- Run/turn/document ids сохраняются в async logging scope; confirmation различает execution run и JournalRunId.
- Controller добавляет run.started, legacy run.summary.created и marker построения send/confirmation DTO. Это не runtime health и не подтверждение отрисовки WebView.
- Все metadata markers идут в существующий stream, без новых payload bodies, storage/index или decision state; новые trace failures не меняют execution.
- Version остаётся `16.1.0-dev`; tags не создаются. Phase 1C и последующие фазы не начаты.

## Phase 1B verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "causal trace:"` — 6/6 pass.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "conversation: resets stream and thinking between repairs"` — 1/1 pass после обновления expectation для accepted trace.
- Первый full harness — 319/321: старое streaming expectation исправлено; compact catalog failure воспроизведён отдельно на исходном `a24feb1` (expected 16, got 15), R22.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj` — 320/321 pass; единственный failure — R22, такой же на baseline. Полный harness не green; новые trace tests и все 7 characterization tests проходят.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. `git diff --check` и relative Markdown links — pass.
- Baseline failure проверен в отдельном detached worktree; после проверки он удалён. Tags/working files основной ветки им не изменялись.
- Actual controller исключён из harness и заменён stub: его wiring проверено только чтением кода. Scope/summary/projection tests проверяют writer, не реальный controller/bridge delivery.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробности и границы: [PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md).

## Phase 1C substeps

- Baseline — `5df587b`, clean working tree. Одна тема: completion guard и минимальная UI/bridge-проекция; 10 production files включая csproj.
- До production fix новые runtime-summary assertions дали 4 красных characterization cases; после fix — 7/7 green.
- RunSummaryBuilder считает actual ToolResults по effective safety metadata, включая local mutations и nested pipeline policy. Model text/status и forged summary не определяют health.
- `unknown > errors > clean`; pending не считается успешной записью; rejected attempts не создают tool errors; v2 lifecycle/status и retry limits не менялись.
- Confirmation сохраняет summary логического turn и считает подтверждённый вызов один раз. Следующий user turn сбрасывает counts, не переписывая предыдущие snapshots.
- Runtime evidence сохраняется в существующих typed run/message operations; clone/DTO/replay сохраняют её. Нового durable store/index/schema или history migration нет.
- UI показывает отдельное предупреждение перед текстом модели вне свёрнутого trace. No-write — обычный ответ без подтверждённых изменений; boundary без summary не наследует старый clean.
- Legacy mapping и ограничение уровня evidence описаны в MIGRATION_MAP/R23. Domain tools, COM/VBA, Resource Fabric и persistence algorithms не менялись.
- Phase 2 не начата. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 1C verification

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization` — red 3/7 → green 7/7.
- Filter `completion guard:` — 5/5; `agent:` — 41/41 (включая characterization); `causal trace:` — 6/6; `conversation:` — 4/4.
- `storage: turn lifecycle` — 1/1 (replay/clone/typed DTO/model isolation); `chat: uses only read-only resource loop` — 1/1; `plan mode:` — 2/2; `harness: production projects` — 1/1.
- `node tests/web/completion-guard.test.js` — 8/8; реальные JS projection/render functions, минимальный DOM, без browser/layout/Office validation.
- Всего 61 различных targeted harness cases + 8 Node cases. Полный harness повторно не запускался; known baseline R22 остаётся открытым, последний full результат — 320/321 в 1B.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Production controller исключён из harness: его wiring проверено только чтением. Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Подробные команды, red→green evidence и границы: [PHASE_1C_COMPLETION_GUARD.md](PHASE_1C_COMPLETION_GUARD.md).

## Phase 2A substeps

- Baseline — `40282c0`, clean working tree. Один model/conversation contour, 6 production files включая Core csproj.
- В Core введены IModelProtocol, ModelProtocolClient и typed response/failure boundary. Loop больше не вызывает endpoint, не парсит JSON и не считает raw attempts.
- Parse/repair/native refusal/prompt budget/fallback/accepted-rejected diagnostics физически удалены из старого loop; fixed repair builder удалён из AgentJsonProtocol. Aliases/dual execution нет.
- Каждая попытка использует один accepted prompt; rejected body/reasoning/repair не входят в accepted history. Media сохраняются до конца protocol step и освобождаются в finally (R24).
- Provider/network/timeout/cancellation отделены от protocol exhaustion; прежний controller exception path сохранён через nonserialized Failure.Cause adapter до Phase 3.
- One enabled explicit schema fallback остаётся run-local; saved settings не меняются. Progress projector и trace sink сохраняют прежние semantics, step/attempt/request correlation.
- V2, tool policies/dispatch/summary и legacy initial + 1–20 retries сохранены. R20 и fallback при endpoint rejection внутри repair — оставшаяся Phase 2B.
- ADR-0002 фиксирует boundary, временные contracts и границы проверки. V3/schema/adapter/canonical v3 doc — Phase 2C; Phase 3 не начата.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2A verification

- Baseline characterization — 7/7; после переноса — 7/7.
- `model protocol:` — 8/8; `agent:` — 41/41 (включая characterization и media lifetime); `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1.
- Всего 68 различных targeted harness cases. C# 7.3 linked source build pass; ValidateVersionFormat pass. Новый Core source включён в old-style csproj.
- Прежнее media expectation после extraction дало expected 0 / got 1; обновлённый тест подтверждает одинаковый materialized prompt на repair и release после logical step. Это намеренное изменение lifetime, не новый baseline red→green case.
- Full harness/Node UI повторно не запускались: изменён только model/conversation contour, нет изменений UI или domain/storage algorithms. Последний full — 320/321 в 1B, R22 открыт.
- Fake endpoint tests не являются live tLLM validation. Production controller — stub в harness; Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed.
- Точные команды, legacy paths и границы: [PHASE_2A_MODEL_PROTOCOL.md](PHASE_2A_MODEL_PROTOCOL.md).

## Phase 2B substeps

- Baseline — `d911826`, clean working tree. Один model retry contour: 4 Core production files + caption/tooltip в web/index.html. Loop, tools, Resource Fabric, VBA и persistence не менялись.
- ModelProtocolRetryBudget считает 1–20 total protocol responses, включая первую. Default 10 и значения/ключ настройки MaxAgentFormatRetries сохраняются; initial + N удалён без alias (R20).
- Timeout/Network/TransientServer получают до двух provider retries на весь logical step, с cancellable delays 1s/2s. Ошибки HTTP/auth/429, size и invalid provider envelope не повторяются; transport parser/classification не менялись.
- Explicit enabled schema fallback работает также во время repair, один раз независимо от других budgets; exact current prompt/options повторно используются. N+3 raw requests maximum (23), не N×3.
- Cancellation проверяется до dispatch, во время backoff, после completion и rejection; запоздалый ответ не принимается. Нет повторного исполнения tools или новых accepted/history events.
- Canonical docs, ADR-0002 и changelog обновлены. V2/Failure.Cause остаются; новых compatibility adapters, v3 или AgentKernel нет. Phase 2C/3 не начаты.
- Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2B verification

- Baseline: model protocol — 8/8, characterization — 7/7. До production fix: новые assertions дали 2 failures в model protocol и 2 в characterization (limits 1/20/clamp и fallback during repair).
- После fix: `model protocol:` — 13/13; `agent:` — 41/41 (включая characterization), `conversation:` — 4/4; `causal trace:` — 6/6; `completion guard:` — 5/5.
- `plan mode:` — 2/2; `chat: uses only read-only resource loop` — 1/1; `harness: production projects` — 1/1; `settings: invalid numeric values` — 1/1. Всего 74 разных targeted harness cases.
- C# 7.3 linked source build и ValidateVersionFormat — pass. Provider delays в tests инъецированы; реального ожидания/endpoint requests нет. Full harness/Node UI не запускались; изменены только model retry policy и текст одной настройки. Последний full — 320/321 в 1B, R22 открыт.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed; production controller остаётся stub в harness. Live provider/timeout/media costs — R25/R24, qualification pending.
- Точные команды и ограничения: [PHASE_2B_RETRY_POLICY.md](PHASE_2B_RETRY_POLICY.md).

## Phase 2C1 substeps

- Baseline — `a51bdda`, clean working tree. Полный v3 switch требует более 10 production files; по §14.3 выделен introduce/read-adapt, без частичного переключения. Изменены только 5 новых Core files + old-style Core project include, tests и docs.
- ConversationResponse содержит только message и ordered calls, без Status. Canonical ToJson пишет только v3 root; parser не принимает v2 автоматически. CurrentVersion активного AgentResponseProtocol остаётся 2.
- Strict envelope/JSON, call shape, 32-call bound, exact callable names и original argument schemas проверяются до acceptance. Optional nulls удаляются, execution defaults не применяются. Date-shaped strings остаются strings; unsupported numeric normalization возвращает typed failure.
- Accepted-run IDs и batch-safe read-only IDs задаёт caller. Parser не резервирует IDs; rejected response не возвращает partial calls. Mutation/local/confirmation и external/unclassified calls — singleton; безопасные read-only batches сохраняют порядок. Runtime wiring этих inputs — Phase 2C2 (R26).
- Explicit ConversationResponseV2Adapter читает только identified historical v2 envelope, отбрасывает model status и не выдаёт execution authority. Owner/consumers/removal указаны ниже; current consumer — harness, не history runtime.
- Canonical v3 doc и ADR-0002 содержат cutover gates: saved prompts, complete accepted-run IDs/confirmation, effective safety, все формы history, v3-only accepted writes, removal live v2 parser/schema/DTO consumers. Phase 3 не начата.
- Active model/retry/prompt/schema/history, Office tools, resources, VBA, persistence и UI не изменены. Product остаётся `16.1.0-dev`; bump/tag/push/release script не выполняются.

## Phase 2C1 verification

- Baseline: `model protocol:` — 13/13; `agent:` — 41/41.
- Новый `conversation v3:` — 13/13. Дополнительный oversized integer выявил InvalidCastException; focused malformed-JSON case был red, после typed-failure fix — green. Envelope, adapter, schema/wire, run-ID и singleton matrices входят в эти 13 cases.
- Regression: `model protocol:` — 13/13; `agent:` — 41/41; `harness: production projects` — 1/1. Всего 68 разных targeted harness cases, C# 7.3 linked build. ValidateVersionFormat — pass.
- Full harness, Node/UI, Office builds и live endpoint не запускались: active runtime не менялся. Последний full — 320/321 в 1B, known baseline R22 остаётся открытым.
- Windows x64 + Office x64 + VS 2022 / VSTO / COM / real WebView — not performed. Harness использует controller stub и не доказывает runtime cutover или Windows qualification.
- Точные команды, changed files, legacy paths и ограничения: [PHASE_2C1_V3_CONTRACT.md](PHASE_2C1_V3_CONTRACT.md).

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Legacy ToolResult/safety → RunSummaryBuilder | Runtime / ToolRuntime | Loop, confirmation continuation | Перенос builder Phase 3; typed result mapping Phase 4 |
| Optional RunExecutionSummary / отсутствующая legacy evidence | Application / Persistence / UI | Messages, LastRun, clones, bridge, static UI | Полный RunSummary/projection switch Phases 3/9; obsolete paths cleanup Phase 10 |
| Nonserialized ModelProtocolFailure.Cause rethrow | Runtime / Application | ConversationRunService → controller cancellation/failure path | Phase 3 AgentKernel switch |
| Accepted completion / current context-usage metadata | ModelProtocol / Application | Loop → transcript / turn result | Пересмотреть при v3/kernel Phases 2/3; заменить current transcript consumer при switch |
| ConversationResponseV2Adapter.Read (introduced, not wired) | ModelProtocol | Сейчас только focused harness; intended accepted-history projection Phase 2C2 | Phase 10 после удаления legacy consumers; не live parser fallback |

Существующие runtime paths остаются текущей реализацией, а не введёнными adapters.
Их владельцы и фазы замены указаны в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; guard 1C закрывает host-neutral safety assertions, production qualification ещё не выполнена.
- R02–R11: остальные сценарии из master plan ожидают проверки/исправления в своих фазах.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R22: compact catalog harness failure воспроизведён до изменений 1B; owner ToolPack/Tests, Phase 8.
- R26: v3 runtime switch требует полного accepted-run ID scope и effective batch-safe projection; текущие Core tests не доказывают их wiring, gate Phase 2C2.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
