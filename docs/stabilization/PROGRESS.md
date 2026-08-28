# Stabilization progress

Current target: 16.1.0
Current phase: Phase 3
Current task: Phase 3A завершена host-neutral: model context/materialization вынесены из loop, activity projection — в существующий AgentTranscript; runtime semantics сохранены. AgentKernel/новый RunSummary ещё не внедрены. Windows/controller/WebView/DPAPI и live-provider qualification pending; known baseline test failure: R22

Next step: отдельная Phase 3B — минимальный AgentKernel/RunSummary и общий start/confirmation accounting; минимальный IRunStore/replay использует существующие events. ConversationModelSession остаётся Office-владельцем контекста, working set и media. Не смешивать с полным ToolRuntime, Resource Fabric или persistence/UI cutover; Windows gates не считать выполненными.
Required context: master plan [Phase 3](STABILIZATION_MASTER_PLAN.md#phase-3--минимальный-agentkernel-и-runtime-truth) и §§15.1–15.2, [граница model context](../conversation-protocol.md#conversation-context), [активный v3 contract](../protocols/CONVERSATION_RESPONSE_V3.md), [session events](../session-events.md), [migration map](MIGRATION_MAP.md). Исторические evidence — только по необходимости.
Open gates / remaining legacy: kernel/новый RunSummary/replay (R11) ещё не проверены; Failure.Cause и runtime lifecycle projections — Phase 3B, legacy effect mapping / positive local-read registry — Phase 4. Live v2 и заменённые prompt/media/materialization helpers удалены. R26/R27 закрыты host-neutral для v3, но controller ordering, Office/WebView/DPAPI и реальные providers не проверены.

Workflow update (2026-08-28, docs-only): §§14.3, 22–23 — обоснованный единый switch может затронуть более 10 файлов; проверки применяются по изменению, повторные прогоны без новой причины не нужны; отчёт краткий. Runtime и открытые gates не изменены. Docs diff/links — OK; pre-commit ValidateVersionFormat — pass; build/tests для этой правки не запускались.

Migration sequencing update (2026-08-28, docs-only): Phase 3 изолирует kernel от resource lifecycle и проверяет минимальный RunSummary replay через существующие events; Phases 8/9 меняют внешние реализации, не повторяют извлечение. Проверки новых границ — при switch, Phase 10 — общая сверка. Основной маршрут 0–10 → 12 → stable; Phase 11 отдельно. Scope VBA package lifecycle пока не сокращён: общий journal нужен rename. Это уточнение плана, не начало Phase 3 и не закрытие R11/Windows gates; текущий следующий шаг указан в заголовке. Docs diff/затронутые ссылки и pre-commit ValidateVersionFormat — OK; build/tests для этой правки не запускались.

Pipelines disabled (2026-08-28, отдельное согласованное сокращение scope): удалены executor/parser, `PipelineJson`, nested dependency/safety/document/fingerprint traversal, transcript children parsing и editor. Catalog/discovery, direct/manual/dry-run execution, authoring и storage writes закрыты; старые определения skipped без migration/replay и без автоматического удаления файлов. Совместимость не сохраняется; возврат только отдельным решением Phase 11 после stable core. Это не начало Phase 3/11 и не дополнительный gate текущей миграции.

Pipeline verification: `pipeline:` 3/3; `tools:` 22/23 (единственный failure — известный R22); `vba: package` 5/5; `vba: session execution` 1/1; `vba: code-only UserForm authoring skill` 1/1; `completion guard:` 5/5; `agent: bounds oversized tool result data` 1/1; `protocol context: batch safety uses local authority` 1/1; production project includes 1/1. Итого 40 pass + R22; одна актуальная host-neutral сборка, следующие filters с `--no-build`. `node tests/web/tools-editor.test.js` — pass, syntax 5 затронутых JS — OK. Windows x64 + Office + VS 2022 / controller/WebView validation не выполнялась и остаётся открытой. V3 зафиксирован отдельно в `dbb8ce1`; pipelines — `f35e85c`. Pre-commit ValidateVersionFormat — pass.

Source archive build fix (2026-08-28, отдельное исправление по запросу пользователя): убрана блокировка обычной сборки без `.git`, введён явный `source-archive`/`unknown` с предупреждением; отсутствующий SHA/branch/tree state не подменяется выдуманным commit или `clean`. Debug и Release не требуют ручного props-файла; supplied provenance сохраняется, malformed metadata и ошибки Git checkout остаются ошибками. Explicit release gates требуют известного происхождения и Git checkout. Старый unconditional archive error удалён, adapters не добавлены; [canonical versioning](../operations/VERSIONING.md), ADR-0007 и §13.5 master plan обновлены. `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "versioning"` — 6/6 pass (архив без Git, partial/explicit metadata, release rejection, прежние version/tag/assembly cases); одна host-neutral сборка; `ValidateVersionFormat` и `git diff --check` — pass. Windows x64 + Office + VS 2022 / VSTO validation не выполнялась. Фаза и следующий шаг в заголовке не изменены; чужие runtime-изменения не входят в этот fix.

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.

MockDemo compile fix (2026-08-28, по второму скриншоту пользователя): добавлен отсутствующий source-link `Core/ModelProtocol/*.cs`; demo SettingsService обновлён под текущий `Save(..., reviewAgentPrompts)` с сохранением старого prompt marker при unrelated save. После устранения ModelProtocol errors таргетированная сборка обнаружила CS1501 в старой demo-сигнатуре; после её обновления `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 CA1416 warnings в PDF rendering. `git diff --check` — pass; demo runtime/self-test и полный harness не запускались. Старый demo Save path заменён без alias; production runtime не менялся этим fix. CS0006 для production Office DLL со скриншота отдельно не квалифицированы: нужна сборка Windows x64 + Office + VS 2022; Office/VSTO здесь не запускались.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | `10e52bf` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | done (host-neutral) | 1A: `a24feb1`; 1B: `5df587b`; 1C: `40282c0` | 61 targeted harness + 8 UI pass; red→green 4 cases; ValidateVersionFormat pass; last full 320/321 (R22) | not performed | 1A/1B/1C done; production Windows qualification остаётся открытой |
| 2 | done (host-neutral) | 2A: `d911826`; 2B: `a51bdda`; 2C1: `5a6b550`; 2C2: `c9f8b07`; 2C3A: `330aa79`; 2C3B: `4bbb039`; 2C3C: `dbb8ce1` | 2C3C: 100 targeted cases; ValidateVersionFormat pass; подробности в evidence | not performed | Active v3; old-chat skip/reset и prompt review/reset проверены локально; Windows/live-provider gates открыты |
| 3 | in progress | 3A: этот commit `refactor(agent): isolate conversation model context` | 64 targeted harness cases; ValidateVersionFormat pass; см. 3A ниже | not performed | Подготовительная граница проверена; kernel/RunSummary/replay — следующий 3B |
| 4 | pending | — | — | — | ToolRuntime |
| 5 | pending | — | — | — | Bound DocumentSession |
| 6 | pending | — | — | — | VBA vertical slice |
| 7 | pending | — | — | — | Excel vertical slice |
| 8 | pending | — | — | — | Resource Fabric / ToolPack |
| 9 | pending | — | — | — | Persistence / UI projection |
| 10 | pending | — | — | — | Physical cleanup / architecture tests |
| 11 | pending | — | — | — | Optional contours после stable либо отдельный согласованный milestone; не gate Phase 12 |
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

## Phase 2C2 — context adaptation and local cleanup

- Baseline `5a6b550`, исходно clean. Полный switch не укладывается в §14.3; этот adapt затрагивает 9 production files (включая project includes и удалённый adapter), tests/docs. Phase 3 не начата.
- Loop подаёт immutable `ModelProtocolCallContext`: accepted-only IDs всего logical turn и conservative batch-safe projection. Confirmation читает full history до compaction, сохраняет scope при смене RunId; incomplete history не выдаётся за пустой set. Live v2 client пока context не enforce.
- Current-v3 history reader поддерживает canonical JSON, single native call с canonical metadata и literal final text; не читает старые форматы и не меняет данные.
- Неиспользуемый v2 read adapter, legacy JSON branch, include и obsolete tests удалены. Current-v2 typed-ID helper нужен текущей confirmation; удалить при writer/version switch 2C3. Local-read registry + effective metadata — до typed ToolPolicy Phase 4; bookkeeping — до kernel Phase 3.
- `conversation v3:` 13/13, `protocol context:` 6/6, `model protocol:` 13/13, `agent:` 41/41, `conversation:` 4/4, `completion guard:` 5/5, `plan mode:` 2/2, Chat read-only 1/1, production includes 1/1: 86 разных targeted cases. Linked C# 7.3 build и ValidateVersionFormat — pass.
- Runtime switch, saved prompts/probes, old-chat skip/reset, live provider и Windows x64 + Office x64 + VS 2022 не проверены/не выполнены. Harness моделирует controller identity transition; production controller остаётся stub. Full harness/UI/VSTO builds не запускались; baseline R22 остаётся открытым.
- Параллельные правки шести governance files включены с явного разрешения пользователя. Исходные docs-only проверки от 2026-08-28: cleanup policy — diff и 5 links/anchors OK; refactoring policy — diff и 7 links/anchors OK, без builds/runtime tests. Правила теперь canonical в master plan §§7.1, 15.1–15.2.
- Повторная чистка по §15.1: consumers/includes проверены, устаревшая рекомендация добавить v2 read adapter убрана из master plan §21, вводная PROGRESS сокращена; исторические evidence/ADR сохранены. Дополнительных мёртвых production paths в текущем контуре не найдено; live v2 callers нужны до 2C3. Ранее проверенный код и version/tag не менялись. Команды и границы: [PHASE_2C2_PROTOCOL_CONTEXT.md](PHASE_2C2_PROTOCOL_CONTEXT.md).

## Phase 2C3A — shared active wire owner

- Baseline `c9f8b07`, clean. По §§14.3/15.2 выделена подготовка coordinated switch: 7 production files; новый ModelProtocolWire — постоянный владелец schema/validation/JSON writing, без второго runtime/version selector.
- Runtime и compatibility probes используют общий contract; Office добавляет только reasoning/cache/trace options и native/history metadata. Дубли AgentOptions, ручного probe-call history и JSON call writer удалены. Prompt-authoring skill отсылает к действующим defaults вместо копии v2 status rules.
- Probes остаются fixed sentinel checks по одной raw попытке, без repair/fallback. Оба formats и все три tool-result roles проверены; матрица wrong status/casing/sentinel не даёт ложной qualification. V2 runtime, native refusal и response/prompt versions сохранены.
- Проверки: compatibility 2/2, model protocol 13/13, agent 41/41, protocol context 6/6, conversation 4/4, completion guard 5/5, plan 2/2, Chat read-only 1/1, project includes 1/1, existing prompt-reset characterization 1/1 — 76 разных targeted cases. Linked C# 7.3 build; ValidateVersionFormat pass. Full harness/UI/VSTO/live provider не запускались; Windows/controller/WebView не проверены.
- R27 подтверждён существующим тестом, но не исправлен: prompt schema mismatch автоматически заменяет custom prompts. Не повышать prompt version до explicit review/reset handling и его tests в 2C3B. Product остаётся 16.1.0-dev; tag/push/release script не выполнялись. Подробности: [PHASE_2C3A_WIRE_OWNER.md](PHASE_2C3A_WIRE_OWNER.md).

## Phase 2C3B — explicit prompt schema review

- Baseline `330aa79`, clean. Закрыт prerequisite R27 перед v3 switch: 10 production files, без изменения wire/prompts versions, Office tools, Resource Fabric, VBA или event-storage protocol.
- NormalizeAgentPrompts сохраняет authored text и missing/old/future marker; только blank fields получают defaults. SettingsService сохраняет clone; ordinary save не подтверждает stored mismatched marker, явный request-local review подтверждает его без перезаписи custom text.
- В typed saveSettings добавлен reviewAgentPrompts. Library → Prompts → «Подтвердить проверку» требует user confirmation; existing reset очищает drafts до save. PlanSystemPrompt больше не теряется из UI payload, отсутствие prompt editor не очищает сохранённые тексты. Обычные/tool/diagnostic saves не дают approval.
- Core guard вызван до controller preparation/attachment analysis/compaction и до изменения pending confirmation; neutral loop защищает direct entry/continuation. Production controller wiring проверен чтением, не execution на этой машине.
- Проверки: settings 4/4, typed settings bridge 1/1, prompt save 1/1, protocol context 6/6, confirmation success/failure по 1/1, Plan 2/2, Chat read-only 1/1, project includes 1/1, conversation streaming 4/4 — 22 targeted cases; Node prompt review 5/5. Reset characterization заменён red→green preservation test (раньше marker 0 автоматически становился 11).
- Реальный SettingsService теперь включён в linked C# 7.3 harness. Test-only ProtectedSecretStore поддерживает только отсутствующие fixture secrets и бросает ошибку при secret-file read/write; DPAPI не эмулируется. Windows x64 + Office + VS 2022, production controllers/WebView, DPAPI/live provider и full harness не проверялись.
- Чистка: удалены destructive mismatch branch, duplicate Chat/Plan defaulting и obsolete hard-reset test; устранены UI marker 0→1 и blank fallback при отсутствии editor. Нового production adapter нет. Product остаётся 16.1.0-dev; tags/push/release script не выполнялись. Подробности: [PHASE_2C3B_PROMPT_REVIEW.md](PHASE_2C3B_PROMPT_REVIEW.md).

## Phase 2C3C — v3 switch/delete

- Shared wire, typed result, repair, mode defaults и accepted-history marker переключены вместе: v3 содержит только `message + tool_calls`, prompt schema 12. Native refusal — отдельный outcome; model-loop end не является proof of effect.
- Полный history/context preflight стоит до подготовки, ручной compaction и подтверждения. Run-wide IDs и singleton safety enforce на каждом response; rejected batch не резервирует IDs и не исполняется частично. Saved prompts schema 11 сохраняются до explicit review/reset.
- Удалены live v2 DTO/parser/schema/includes, typed-ID helper, LastRun-only controller helper и 9 obsolete parser tests; fixtures используют настоящие v3 writers. Один связанный scope: 15 production files, без нового kernel/tool policy/storage/UI.
- Проверка и границы: [PHASE_2C3C_V3_CUTOVER.md](PHASE_2C3C_V3_CUTOVER.md). Windows/controller/Office/WebView/DPAPI и live provider не проверены; Phase 3 остаётся отдельной. Product `16.1.0-dev`, нового tag нет.

## Phase 3A — Office model context boundary

- Baseline `f35e85c`, исходно clean; pipelines уже отключены отдельным commit. По §15.2 извлечение нужно для ближайшего Core AgentKernel: loop больше не требует prompt/compaction/media и working-set implementation. Это один контур, 5 production files включая old-style project include; новый kernel или resource/store protocol не вводятся.
- Постоянный Office-владелец `ConversationModelSession` хранит model messages, request cache/options, read evidence/LRU и bounded result/media lifecycle, используя существующие services. Start/confirmation и prompt inspector переключены; прежние BuildMessages/BuildRequestOptions/materialization/media helpers и DTO из loop удалены без alias. Activity/resource/chart/checkpoint projection перенесена в существующий `AgentTranscript`; callback-связей обратно в loop нет.
- Сохранены v3, preflight, policies, execution/confirmation budgets, accepted IDs, summary и порядок result/history/projection. Failure.Cause, legacy summary и controller path остаются до 3B/4; владельцы/removal gates — в migration map. Phase 3 целиком не закрыта.
- Проверка: baseline `agent:` 32/32 и `protocol context:` 6/6. На изолированном составе Phase 3A (baseline `f35e85c` + 14 файлов; параллельные versioning/demo правки исключены) `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "agent:"` — 33/33, включая новый auto-compaction/rebuilt callable-set case; существующий oversized-result/chart fixture проверяет перенесённую activity/provenance projection. После той же актуальной сборки `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `protocol context:` 6/6; `preflight` 3/3; `conversation:` 4/4; `context inspector:` 3/3; `causal trace:` 6/6; `completion guard:` 5/5; `plan mode:` 2/2; `chat: uses only read-only resource loop` 1/1; `harness: production projects` 1/1. Всего 64 разных cases pass, C# 7.3 source-linked build pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff/затронутые ссылки — OK. Full harness/JS не запускались: нет новых domain/storage/UI semantics; known R22 не перепроверялся.
- Windows x64 + Office x64 + VS 2022 / VSTO, production controller и real WebView/DPAPI/live providers не проверялись. IRunStore/новый RunSummary replay не реализованы и не считаются проверенными. Product `16.1.0-dev` и tags сохраняются; release script/push не выполняются.

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Legacy ToolResult/safety → RunSummaryBuilder | Runtime / ToolRuntime | Loop, confirmation continuation | Перенос builder Phase 3; typed result mapping Phase 4 |
| Optional RunExecutionSummary / отсутствующая legacy evidence | Application / Persistence / UI | Messages, LastRun, clones, bridge, static UI | Phases 3/9: удалить каждый obsolete path при switch последних consumers |
| Nonserialized ModelProtocolFailure.Cause rethrow | Runtime / Application | ConversationRunService → controller cancellation/failure path | Phase 3B AgentKernel switch |
| Accepted completion / current context-usage metadata | ModelProtocol / Application | Loop → transcript / turn result | Phase 3: пересмотреть metadata boundary и заменить current transcript consumer при switch |
| Legacy safety + positive local-read registry → batch-safe snapshot | Runtime / ToolRuntime | Context constructor → ModelProtocol request | Phase 4 typed external/nested metadata + safety tests; неизвестные/external singleton, pipelines отключены |
| Office transient accepted-ID bookkeeping → ModelProtocolCallContext | Runtime | ConversationRunService start/confirmation | Phase 3 kernel switch; один owner, без второго loop/store |

Существующие runtime paths остаются текущей реализацией, а не введёнными adapters.
Их владельцы и фазы замены указаны в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; guard 1C закрывает host-neutral safety assertions, production qualification ещё не выполнена.
- R02–R11: остальные сценарии из master plan ожидают проверки/исправления в своих фазах.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R22: compact catalog harness failure воспроизведён до изменений 1B; owner ToolPack/Tests, Phase 8.
- R26: preflight, actual v3 writer/confirmation, run-wide IDs и singleton enforcement проверены в 2C3C; production controller ordering/Office qualification и замена temporary safety registry в Phase 4 остаются открыты.
- R27: explicit review/reset проверены на actual v3 defaults/schema 12, старый custom text schema 11 сохраняется; production controller/WebView/DPAPI validation открыта.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
