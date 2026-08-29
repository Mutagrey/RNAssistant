# Stabilization progress

Current target: 16.1.0
Current phase: Phase 9 — 9A diagnostics truth/query (done host-neutral)
Current task: завершена одна bounded correlated run projection поверх canonical chat events и existing `ITrajectoryQuery`; R37 writer classification исправлена без второго store или UI inference.

Next step: отдельный 9B1 — bounded/lossless `JsonAdapter` с raw/pretty/tree/copy и targeted UI tests. До первого vendor switch закрыть R36; не подключать весь shortlist и не объявлять Windows/WebView qualification закрытой.
Required context: [master Phase 9 / exception](STABILIZATION_MASTER_PLAN.md#phase-9--persistence-и-ui-projection), [R32 diagnostics](R32_DIAGNOSTICS_JSON_VIEWER.md), [trajectory query](../trajectory-query.md), [architecture](../architecture.md), [harness filters](../../tests/RNAssistant.Harness/README.md).
Open gates / remaining legacy: Phase 5B2/R04, остаток Phase 6 (`VbaMutationService`, `VbaVerifier`, journal/result mapping/fault matrix), Phases 7–8 и R30 остаются открытыми и не считаются закрытыми ранним Phase 9A. Controller/WebView/COM lifetime, VBE/read-back/package regression, R28/R29 live-provider и весь Windows x64 + Office + VS 2022 gate открыты. R32 закрыта только в части 9A; 9B/9C и R36 открыты. Read-only R37 adapter для затронутых current-v4 streams удалить/заменить reset после 9C qualification. Product 16.1.0-dev, no release/tag.

Phase 9A diagnostics truth/query (2026-08-29): добавлен хронологический
`run-causal` view поверх canonical `*.events.jsonl` и existing `ITrajectoryQuery`.
Он сохраняет exact source event ids/sequences, model attempt/origin/call/mutation/
journal ids, revision-pinned resources и показывает явный evidence gap только после
typed terminal boundary; отсутствие события не объявляется успехом или ошибкой.
Новых writes, durable index, execution/replay decisions и UI inference нет. R37:
`ChatStore` теперь классифицирует accepted call по runtime-owned
`AcceptedCallOrigin`, независимо от provider result role/native `ToolCalls`; узкий
read-only adapter корректно проецирует ранее ошибочно помеченные current-v4 события,
не переписывая историю. 9B/9C, direct navigation UI и Windows/WebView qualification
остаются отдельными gates.

Phase 9A verification: 17 targeted harness cases pass — trajectory raw/derived/
run-causal/export 4, actual causal traces 6, accepted-call role classification 1,
typed bridge 1, selectable result roles 1, v4 accepted-history forms 1, canonical
event log 1 и complete-HTML runtime IDs 2. Одна актуальная host-neutral сборка,
последующие filters с `--no-build`; 0 errors, 4 existing CA1416 warnings в
`ExcelIdentityProbe`. `ValidateVersionFormat`, 117 local docs links и
`git diff --check` — pass.
Windows x64 + Office + VS 2022 / real WebView не запускались.

Phase 9A early start (2026-08-29, explicit user decision, docs-only baseline
`8d53d91`): из-за отсутствия Windows открытые Phase 5B2/6 и Phases 7–8 не закрываются,
а приостанавливаются; разрешён только host-neutral R32 9A truth/query поверх уже
существующего event stream/`ITrajectoryQuery`. Этот switch не меняет runtime/UI и
не разрешает зависеть от незавершённого Phase 8 ToolPack. 9B и 9C остаются отдельными
commits после acceptance 9A. Diff/4 docs, 74 local links и ValidateVersionFormat —
pass; build/harness не запускались.

Cleanup/readiness review (2026-08-28, baseline `1ea3ce0`): удаление controller-owned capture, catalog guard-only scope и прежних monitor/depth helpers подтверждено targeted search; includes актуальны. Дополнительных мёртвых путей в контуре 5B2 не найдено; legacy/probe сохраняются по действующим consumers/removal gates. Это не аудит всего репозитория. Согласованный пользователем допуск 6A заменяет прежнее предложение; Phase 5 не закрывается и порядок остальных фаз не меняется.

R32 requirements (2026-08-28, docs-only поверх `b754443`): по замечанию пользователя зафиксированы [сквозной журнал запуска и общий JSON viewer](R32_DIAGNOSTICS_JSON_VIEWER.md), inventory read-only consumers и acceptance Phase 9A–9C. Vendor-first оценка компактных готовых компонентов добавлена; конкретный vendor не выбран/не подключён. Runtime/UI не менялись; итоги 4B и следующий Phase 5 сохранены. Docs diff/9 новых локальных ссылок и anchors — pass; build/tests не запускались. Реализация, targeted UI/query tests и Windows/WebView qualification открыты; R28/R29 live gates этим требованием не закрываются.

R35 security hotfix (2026-08-29, отдельно от Phase 9): existing `DOMPurify 3.1.6`,
который очищает результат `marked` перед HTML insertion, заменён точным upstream
`3.4.14`; версия 3.1.6 входит в affected range GHSA-v2wj-7wpq-c8vv. Зафиксированы
npm integrity, git head, vendored SHA-256 и обе license texts. Markdown adapter,
CSP и остальные vendors не менялись. Headless Chromium загрузил vendored bundle
с `file://`, подтвердил version 3.4.14 и удаление script/event handlers в двух
malicious inputs; `node --check`, diff/links и version format — перед commit.
Реальный WebView2 на Windows не проверен; текущая Phase 6 и следующий slice не меняются.

R32 vendor/UI evaluation (2026-08-29, docs-only после R35): проверены existing
vendors и предложенный shortlist по source/package metadata, фактические bundles
четырёх основных кандидатов измерены. [Решение](R32_VENDOR_UI_EVALUATION.md):
Web Awesome Tree допускается только как tree-navigation spike; Wunderbaum — резерв
для measured large treegrid; оба JSON-кандидата отклонены для authoritative payload,
поэтому 9B начинает с собственного bounded/lossless `JsonAdapter`. Monaco/PDF.js
требуют смены текущего `file://` hosting; pinned local Worker разрешён и не считается
сетью, но Monaco всё ещё не оправдан в R32. `xterm.js` не используется для structured
logs. `ViewerRegistry` закреплён как UI-only adapter boundary поверх
Tool Result v1/`ResourceRef`, не новый model transport. R36 фиксирует незакрытый
provenance/offline inventory остальных vendors. Runtime/UI не менялись, Phase 6 и
следующий шаг сохранены; diff/7 docs, 99 local links и ValidateVersionFormat — pass,
build/harness не запускались. Windows/WebView qualification и Phase 9 implementation
открыты.

R32 Worker clarification (2026-08-29, docs-only): offline больше не трактуется как
запрет Web Worker. Текущий `file://` origin действительно блокирует worker path
Monaco/PDF.js; target допускает только pinned same-origin worker через WebView2
virtual-host mapping, host allowlist/factory, CSP и bounded termination, при полном
zero-network gate. Monaco остаётся вне R32 из-за размера/дублирования CodeMirror;
PDF.js — условный отдельный viewer. Runtime/hosting/CSP не менялись; diff/6 docs,
94 local links и ValidateVersionFormat — pass, build/harness не запускались.

R29 (предыдущий commit `6a256f0`): model wire содержит только name/arguments, kernel выдаёт ID до accepted append/confirmation/dispatch; ToolCallId + immutable attempt/position origin сохраняются в том же stream без переписывания raw response. Tests покрывают long HTML, allocator failure, native pairing, repair correlation, confirmation/replay и ISO-preserving clone. [Evidence/ограничения/чистка](R29_RUNTIME_CALL_IDS.md); этот protocol switch завершён до Phase 4, product version остаётся 16.1.0-dev.

Architecture audit (2026-08-28, docs-only commit `1f65f5d`, baseline `15dea46`): уточнены ID ownership, batch/control boundaries, actual effect evidence, ResourceRef transport (R30), pinned/bounded ToolPack, host gate, raw/comparable hashes и durable barriers будущих Phases 4–9. Убраны stale v2/media указания в canonical docs. Решение Phase 8 о конечном immutable pack сохранено; действовавшие на том baseline v3/LRU/runtime не менялись этим docs commit, позднее v4 включён отдельным R29. Критерии привязаны к фазам в master/backlog; R28/R29 и Windows gates открыты. Diff/13 затронутых ссылок — OK; pre-commit `ValidateVersionFormat` — pass. Build/tests не запускались, новые runtime-инварианты не объявлены проверенными. Phase 4 оставалась отдельным следующим этапом.

Historical live report (2026-08-28, docs-only; R29 runtime correction теперь описан выше, R28 открыт): фото показывает duplicate-ID rejection; после repair пользователь получил неполный HTML. По прямому запросу зафиксирован отдельный [R29/P1 — model-owned call IDs](RISK_REGISTER.md#r29--runtime-должен-владеть-идентификаторами-вызовов): целевое исправление — выдача ID кодом до execution, с сохранением correlation/confirmation/replay. Это отдельная правка контракта Phase 2 и consumers Phase 3, не автоматический результат 3B2; действовавший тогда v3 позднее атомарно заменён R29/v4. Полный incident trace не предоставлен, возможная ошибка scope остаётся R26; streaming — R28. Задача и критерии закрытия добавлены в [backlog](BACKLOG.md). Фаза/текущий подэтап на момент записи не менялись; проверены diff/локальные ссылки, build/tests и Windows/Office validation не запускались.

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
| 2 | done (host-neutral) | 2A: `d911826`; 2B: `a51bdda`; 2C1: `5a6b550`; 2C2: `c9f8b07`; 2C3A: `330aa79`; 2C3B: `4bbb039`; 2C3C: `dbb8ce1` | 2C3C: 100 targeted cases; ValidateVersionFormat pass; подробности в evidence | not performed | 2C3C был v3; current v4 — отдельный R29 correction ниже; old-chat skip/reset и prompt review/reset проверены локально; Windows/live-provider gates открыты |
| 3 | done host-neutral | 3A: `f01c3f2`; 3B1: `c1628ce`; 3B2: `15dea46` | 130 unique targeted cases; MockDemo compile; [evidence](PHASE_3B2_KERNEL_CUTOVER.md) | not performed | Production kernel switch + minimal real-store replay; Phase 4 отдельно |
| 2/3 R29 | done host-neutral | `6a256f0` | 141 unique targeted cases; MockDemo compile; [evidence](R29_RUNTIME_CALL_IDS.md) | not performed | Runtime IDs + v4; no v3 fallback, product version unchanged |
| 4 | done host-neutral: 4A + 4B | 85cc3f4 (4A); b754443 (4B) | 4B: 127 distinct targeted pass; MockDemo 0 errors / 3 existing CA1416 | not performed | [ToolRuntime](PHASE_4A_TOOL_RUNTIME.md), [v1 wire/cleanup](PHASE_4B_TOOL_RESULT_V1.md); domain/Windows gates remain |
| 5 | 5A + 5B1 done host-neutral; 5B2 read switch done, identity probe ready | 3a6c2aa (5A); a1b3d80 (5B1); 1ea3ce0 (5B2) | [read checks](#phase-5b2--direct-contextcatalog-reads), [probe checks](#phase-5b2--identity-qualification-probe) | not performed | Production binding blocked on identity qualification |
| 6 | 6A + R33 + 6B VbaReader done host-neutral; remaining slices pending | `e0360f3` (6A); `62010c8` (R33); 6B evidence below | [6A: 58](#phase-6a--pure-vba-text-extraction); [R33: 8](#r33--overlapping-exact-matches); [6B: 60](#phase-6b--typed-vbareader) | deferred | Mutation/verifier/journal/result/fault matrix and full VBA gate open |
| 7 | pending | — | — | — | Excel vertical slice |
| 8 | pending | — | — | — | Resource Fabric / ToolPack |
| 9 | 9A done host-neutral; 9B/9C pending | — | 17 targeted pass | not performed | Correlated query/writer classification done; viewer/journal UI remain |
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

## Phase 3B1 — Pure kernel introduction

- Baseline `68aadc2`, clean; чужие archive/versioning и MockDemo commits сохранены. По §14.3 полный switch разделён: model materialization, два execution path, storage и UI projection ещё требуют связанного wiring. Здесь вводится kernel contract, без production selection, feature flag или второго active loop. 13 production files: 8 новых kernel/contracts, 3 минимальных typed-caller renames, Core и MockDemo project includes; harness/includes/docs относятся к тому же контракту.
- `AgentKernel` знает только generic accepted messages/calls, typed execution records, summary и три ports. Normal/confirmation используют общий учёт; IDs и budgets принадлежат logical turn. Health вычисляется из execution evidence независимо от narrative; ambiguous write остаётся unknown, pending не считается outcome, retry tools не добавлен. Append/CAS failures останавливают работу без выдуманного durable terminal; synthetic result messages сохраняют typed evidence.
- Старый materialized `IModelProtocol.GetResponseAsync` переименован в `IMaterializedModelProtocol` во всех текущих typed callers, без alias или изменения v3/retry. Новый `IModelProtocol.SendAsync` пока имеет только fake implementation. ConversationRunService, legacy summary, Failure.Cause и projections не удалены: они обслуживают production до 3B2; owners/removal gates уточнены в migration map. ADR-0001/0008 и canonical state-model docs добавлены. Tool Result v1, домены, resources, VBA, UI и persistence algorithms не менялись.
- Проверка на изолированном составе этого изменения: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "kernel:"` — 41/41, включая cancellation во время policy recheck. На предыдущей сборке того же изменения `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `model protocol:` 15/15; `protocol context:` 6/6; `harness: production projects` 1/1. Эти 22 regression cases повторно использованы после локального исправления cancellation: active materialized sources/tests и project includes не менялись. Всего 63 разных cases pass. `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --no-restore --nologo -v:minimal` — pass, 0 errors, 3 прежних CA1416 warnings в PDF rendering. C# 7.3 source-linked compilation проверена; demo runtime/self-test, full harness и JS не запускались, R22 не перепроверялся. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Fake append/CAS log не доказывает existing-event replay, crash recovery или controller delivery. R11 остаётся открыт; actual IRunStore adapter и validated continuation restore — 3B2. Windows x64 + Office x64 + VS 2022 / VSTO, production controller, WebView/DPAPI/live providers не проверялись. Product `16.1.0-dev`, tags и release workflow не меняются.

## Phase 3B2 — Kernel production cutover

- `ConversationRunService` и controller confirmation используют единый Core kernel. Office model/tool/store ports сохраняют preflight, fingerprint, lease и model-context boundaries; старые loop, `ContinueAfterToolAsync`, `RunSummaryBuilder`, mutable ID bookkeeping и `Failure.Cause` удалены.
- `KernelState` сохраняется через existing `run.updated`, включая pending/in-flight evidence; flat run summary — только getter/projection. Real-store replay, stale confirmation, cancellation и interrupted/materialization boundaries проверены. Контракты — в canonical docs, точная matrix/команды и reused results — в [PHASE_3B2_KERNEL_CUTOVER.md](PHASE_3B2_KERNEL_CUTOVER.md).
- R11 contained только в минимальном контуре Phase 3; полный storage/UI и Windows/Office gates остаются. Domain tools, VBA, Resource Fabric, UI JS и version/release workflow не менялись. Development target `16.1.0-dev` не повышался, tag/push не выполнялись.

## Phase 5A — HostRuntime access boundary

- `Runtime/HostRuntime` стал владельцем текущих expected-document scope, file locks/monitor fallback, live-read depth и leases. Executor передаёт только Host/DocumentKey/RuntimeDocumentKey, access flags и синхронную operation; catalog, safety, tool/resource error mapping и domain preparation остаются у callers. Нет нового partial или второго executor.
- Consumers: обычный/ручной dispatch, VBA install/remove/run/editor, live Office/VBA resources и HTML data access. Старые executor-owned helpers/fields и `System.IO` dependency удалены; Office, Harness и MockDemo source includes обновлены. [ADR-0005](../decisions/ADR-0005-bound-document-session.md) фиксирует текущую границу и следующий switch.
- Не исправляются попутно: stable-key gate, global fallback, nesting без проверки target, stable-key OR runtime-key matching, preparation до gate и Excel ActiveWorkbook/descriptor lookup. R04/Windows остаются открыты; owners/consumers/removal gates — в MIGRATION_MAP. Kernel, v4/v1 wire, storage, UI, Excel/VBA domain algorithms не менялись.

Verification (2026-08-28): один build через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`, затем тот же command с `--no-build` для остальных filters. **16 distinct tests pass**, C# 7.3 / host-neutral .NET 8:

| Filter | Passed |
|---|---|
| `host runtime:` | 2 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: reconciliation waits for active mutation` | 1 |
| `vba: confirmed mutation rejects stale snapshot` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

Diff/16 добавленных или изменённых локальных ссылок и anchors — pass. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.

Новые boundary tests проверяют cancellation до/после action, отсутствие bypass у другого runtime, nested read и release после exception. Existing integration checks используют fake Office; они не доказывают реальную COM identity или новый bound contract. Production controller/real WebView, Windows x64 + Office + VS 2022 не проверялись; full harness и MockDemo build не запускались. Next — 5B, без Phase 6/9 switch; product version/tag workflow не менялся.

## Phase 5B1 — document access gate

HostRuntime берёт document gate до guard/preparation и удерживает до read-back/существующего journal terminal. Manual/resource/editor/HTML reads используют ту же границу; native list получает отдельный operation root. Reentry разрешён только той же синхронной operation и target, explicit STA transfer не передаёт право child tasks или новому UI/tool root. Owner STA возвращает busy без ожидания; cancellation повторно проверяется перед action на owner. Отмена и gate/guard exception после начала mutation сохраняют uncertain/nonretryable, а возвращённое domain evidence не переинтерпретируется.

Введён IOfficeDocumentSession: runtime/host/gate/dispatcher — cached metadata, stable identity/object/liveness проверяются на STA; wrappers держат одну session на lifetime. HostRuntime поддерживает этот port и строгий runtime match, но production Excel providers пока отсутствуют. Global monitor/per-instance AsyncLocal depth удалены; legacy stable-key/OR identity, actual workbook lookup и прямые context/catalog consumers остаются с owner/removal gate 5B2 в [MIGRATION_MAP](MIGRATION_MAP.md). Domain algorithms, kernel, v4/v1 wire, persistence и UI не переключались.

Verification (2026-08-28): **26 distinct tests pass**, C# 7.3 / host-neutral .NET 8. Final production sources скомпилированы через `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"`; следующие filters — `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`.

| Filter | Pass |
|---|---:|
| `host runtime:` | 7 |
| `vba: queued guard` | 1 |
| `waits for active mutation` | 5 |
| `vba: confirmed mutation` | 1 |
| `desktop com: adapter dispatches calls` | 1 |
| `resources: live Office and VBA are bounded and guarded` | 1 |
| `vba: guard resolves stable and changed identities` | 1 |
| `vba: read-back` | 2 |
| `tools: manual read-only run skips chat lease` | 1 |
| `tools: safety metadata gates mutations` | 1 |
| `agent: closed document keeps local tools` | 1 |
| `tools: html workspace updates session` | 1 |
| `tool runtime: native resource list manual and model paths` | 1 |
| `vba: package journal is atomic` | 1 |
| `harness: production projects include all source files` | 1 |

После исправления только native-list fixture (`kind: vba-component`, чтобы проверять реальный live module-list backend, а не project metadata) выполнен новый build для этого filter. 23 успешных предыдущих cases переиспользованы при неизменных относящихся production/test sources, dependencies и environment; package/includes выполнены затем с `--no-build`. Ранее исправлена новая manual-read fixture (`address`, не `range`); production schemas не ослаблялись. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass; diff и 8 затронутых local links/anchors — pass. Версия остаётся `16.1.0-dev`, release script/tag/push не выполнялись.

Реальная COM identity/STA reentrancy, desktop/VSTO/native factories, active window/close/reopen/Save As и несколько клиентов требуют Windows x64 + Office + VS 2022. Здесь не запускались Office/VSTO validation, full harness или MockDemo build. Phase 5 целиком и R04 не закрыты; следующий шаг 5B2, без Phase 6/9.

## Phase 5B2 — direct context/catalog reads

Закрыт host-neutral read switch внутри 5B2. Конкретный блокер полного switch — предварительная Windows qualification общей runtime lifetime identity до переключения Excel factories (ADR-0005); production identity/binding не вводились. `HostRuntime.ReadDocument` использует существующий gate/guard/STA path отдельным operation root. `OfficeContextCaptureService` убирает прямой capture из controller, держит prepare/capture вместе и возвращает результат до persistence; VBA catalog держит cache identity/list/components под тем же gate. Busy/closed access не кэшируется как пустой catalog. Review выявил и исправил второй путь: failed/null backend result или exception при module list/component read теперь прерывает всю загрузку без публикации пустого/частичного cache и без внутреннего retry. Следующее независимое чтение может загрузить catalog заново; успешный пустой список по-прежнему кэшируется. UI context остаётся best-effort, selection guard/access failure не проглатывается.

Локальная чистка: удалены controller-owned capture implementation и catalog guard-only scope; общая guarded execution переиспользована без второго gate. Новый service включён в old-style Office project. Kernel, protocol, storage, UI и Excel/VBA algorithms не менялись. Consumers/removal gates обновлены в MIGRATION_MAP; legacy stable-key/OR identity и ActiveWorkbook/descriptor lookup остаются до production switch и Windows tests.

Verification после review (2026-08-28): **12/12 свежих targeted cases pass**, C# 7.3 / host-neutral .NET 8. Один build: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "host runtime:"` — 10/10. Расширен существующий catalog case: list/component failure через failed result, gate exception и generic exception; отсутствие cache/internal retry, последующая независимая загрузка и cache успешного пустого списка. Затем `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`: `vba: document tools discovered` — 1/1, `tools: catalog merges visible tools` — 1/1.

Из первоначального 5B2 read-switch run переиспользованы ещё **9 pass**: относящиеся production/test methods, dependencies, build settings и environment не менялись review-fix; повторного запуска этих filters не было.

| Reused filter | Pass |
|---|---:|
| `harness: production projects include all source files` | 1 |
| `vba: queued guard` | 1 |
| `vba: confirmed mutation` | 1 |
| `waits for active mutation` | 5 |
| `desktop com: adapter dispatches calls` | 1 |

Harness использует controller bridge stub; его tests проверяют production capture service, не controller wiring. Поэтому дополнительно выполнен `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --nologo -v:minimal`: **pass, 0 errors / 3 существующих CA1416 PDF warnings**; actual controller sources компилируются. Demo runtime/self-test и Windows controller/WebView поведение этим не проверены.

Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass. Diff и 11 затронутых local links/anchors — pass. Product version остаётся `16.1.0-dev`; release script/tag/push не выполнялись.

Windows x64 + Office + VS 2022 обязательны для controller/WebView, COM identity/STA reentrancy и всех factory/lifetime сценариев. Office/VSTO validation и full harness не запускались. Phase 5/R04 не закрыты; следующий шаг и обязательные документы — в заголовке.

## Phase 5B2 — identity qualification probe

Подготовлен отдельный `tests/RNAssistant.ExcelIdentityProbe` (net48/x64, C# 7.3), не подключённый к production/solution. Кандидат — OXID/OID из стандартного IUnknown OBJREF плюс scope Excel process/start time; original marshal reference удерживается до STA dispose. Неизвестный format, неполный packet и пустая identity отвергаются без fallback. Native-OM driver выбирает explicit HWND/workbook index один раз; последующие snapshots не перепривязывают закрытую книгу. Данные книги не меняются, raw marshal packets не экспортируются.

Проверены primary Microsoft specifications; выбор остаётся кандидатом до реальных proxy/lifetime наблюдений. [Probe README](../../tests/RNAssistant.ExcelIdentityProbe/README.md) содержит Windows команды, реальные desktop/VSTO/native call sites, acceptance observations и ownership/removal gate. Это инструмент для конкретного блокера ближайшего factory switch, не новый runtime adapter. Production `RuntimeKey`, ExcelAdapter/factories и ActiveWorkbook fallback не изменены; cleanup кандидата — при его принятии/отклонении в 5B2.

Исходная verification (2026-08-28): `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "excel identity probe:"` — **3/3 pass**: unsigned LE/object-vs-interface identity, malformed/unsupported/bounded packets, non-Windows refusal до native access. Этот результат переиспользован при review: probe sources/tests, dependencies, build settings и environment неизменны. Свежий read-switch build также скомпилировал probe C# sources; 4 ожидаемых CA1416 warnings у guarded Windows COM calls. Итого для 5B2 **24 distinct cases: 12 свежих + 12 reused**, без повторного полного прогона. Это не net48/PowerShell/Office validation.

Probe project XML/explicit includes/whitespace проверены при исходной подготовке; sources/project не менялись при review. В README уточнено: запись `released` подтверждает только успешный return из Dispose, а полное освобождение ссылок/lifetime требует отдельных Windows наблюдений.

Windows net48 build, PowerShell driver, COM marshal/cleanup, реальная identity и full controller/Office matrix **не запускались**; PowerShell здесь отсутствует. R04/Phase 5 остаются открытыми. Next 5B2 gate — результаты Windows qualification; без них factories не переключать. Последующий явный допуск ограниченного 6A описан ниже; Phase 9 не начата.

## Phase 6A — pure VBA text extraction

2026-08-28, baseline `1ea3ce0`; пользователь разрешил этот локальный подэтап, пока Windows недоступна. Phase 5/R04 и полный Phase 6 gate не закрываются.

`Core.Tools.VbaPatchEngine` выполняет одну текстовую замену и возвращает typed status/text/match count; `VbaTextCanonicalizer` владеет прежними live/package/VBE-comparable правилами. Core выбран из-за действующих parser/storage consumers: размещение в Office создало бы обратную зависимость. Manifest parser, storage, patch/guard/read-back/package/catalog и fake consumers переключены. JSON/result mapping и ordered orchestration остаются у Office; COM, journal/CAS protocol, outcome classification не менялись. [Представления текста](../vba-mutation-journal.md#text-representations) описаны отдельно от raw CAS bytes.

Чистка: прежние normalization/hash methods из manifest parser, newline/count/replacement helpers из Office и неиспользуемый `System.Text` import удалены; aliases и второй text engine не оставлены. Новые `.cs` включены в production `.csproj`. Действующий Office mapping и оставшийся domain orchestration имеют consumers/removal gates в [migration map](MIGRATION_MAP.md); packages/journal не удалялись, поскольку используются, включая rename.

Проверено на текущих sources:

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba:"` — **57/57 pass**: pure patch, exact edit/guard/confirmation, hashes, fake VBE read-back, restore/journal/CAS/recovery, package/ToolStore/catalog. Добавлен один pure-text contract test; существующий hash test расширен для literal backslashes, строк и апострофных комментариев.
- `dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness: production projects include all source files"` — **1/1 pass**. Итого **58 свежих targeted checks**, одна host-neutral сборка; 4 прежних CA1416 warnings из guarded Windows identity probe. Tests с `COM` в имени используют fake objects и не являются Office validation.
- Source comparison: canonicalizer block совпадает с baseline после переименования API; изменения 11 существующих consumer files — только замена owner/name. Поиск прежних parser helper calls в src/tests/demo — 0. Diff и 17 затронутых локальных ссылок/anchors — pass.

Перед commit (2026-08-29): 58 checks выше переиспользованы при неизменных относящихся к ним sources/tests, dependencies, build settings и environment. Обязательный `ValidateVersionFormat` — pass; повторные runtime tests не запускались.

Full harness, MockDemo build и Windows/Office/VSTO не запускались. Унаследованный **R33** выявлен source review: non-overlapping counter может принять неоднозначное перекрывающееся вхождение; алгоритм сохранён в этом extraction. Нужен отдельный semantic fix с targeted tests до полного VBA gate.

Накопленные Windows проверки (Windows x64 + Office + VS 2022):

- 5B2: [identity probe / acceptance matrix](../../tests/RNAssistant.ExcelIdentityProbe/README.md), реальные proxy/lifetime и wrong-target сценарии; identity evidence требуется до реализации/switch factories.
- 5B2: controller/WebView selection/context/catalog reads, ошибки/закрытие книги/смена активной книги и несколько клиентов под gate.
- 6A: exact patch/guard на реальном VBE с CRLF/LF/CR, literal backslashes и комментариями; read-back/hash normalization, restore и package/rename regression. Journal/CAS evidence проверять без автоматического replay/restore.

Продолжение R33 согласовано отдельно 2026-08-29 и описано ниже. Остальные отложенные Windows gates сохраняются в своих phase reports.

## R33 — overlapping exact matches

2026-08-29, baseline `e0360f3`; отдельный semantic fix после commit 6A. `VbaPatchEngine` считает все стартовые смещения, включая перекрытия: `aaaa` / `aaa` → 2, `aaaaa` / `aaa` → 3. Неоднозначная замена отвергается даже при неизменном replacement. Newline/hash semantics, существующий `vba_patch_ambiguous` mapping, COM и journal protocol не менялись.

Два существующих tests расширены без новых fixtures/files. До fix оба дали ожидаемый **FAIL**: pure engine принимал overlap; executor пропускал его к confirmation. После fix **8 distinct targeted pass**:

| Harness filter | Result |
|---|---|
| `vba: pure patch text contract` | 1/1: overlapping offsets/counts, LF/CRLF/CR, unchanged ambiguous replacement, unique full-source match |
| `vba: patch` | 3/3: addressing/stale/ambiguity; reject до confirmation и при auto-confirm, без частичной записи ранее рассчитанной операции, backend write, backup или mutation record |
| `vba: exact patch` | 2/2: complete lines, boundary newlines |
| `vba: apply patch` | 2/2: valid unique mutation/backup, named target, mixed/all-no-op operations |

Команды: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "vba: pure patch text contract"`, затем остальные filters с `--no-build`. Одна host-neutral сборка для red tests и одна после production fix; 4 прежних CA1416 warnings из identity probe. Full harness, MockDemo и Windows/Office/VSTO не запускались; Office assertions используют fake adapter.

Перед commit: обязательный `ValidateVersionFormat`, diff и 13 затронутых локальных ссылок/anchors — pass.

Чистка: прежний non-overlapping counter заменён в единственном владельце, без alias/fallback/второй реализации; новые `.cs`/project includes не нужны. Canonical text contract, master/backlog/risk и migration status актуализированы. Позднее отдельно согласованный VbaReader описан ниже и не является частью R33.

Отложено: Windows x64 + Office + VS 2022 — overlapping one/multi-operation patch с обоими режимами confirmation, отсутствие live write/нового backup/journal при отказе. 5B2/R04, production binding и полный Phase 6 gate не закрыты; прежняя очередь Windows scenarios сохранена.

## Phase 6B — typed VbaReader

2026-08-29, baseline `62010c8`; отдельный host-neutral read slice после R33. `Office.Vba.VbaReader` теперь единолично строит internal VBA list/module commands, нормализует fallback-имя и проверяет typed project/module snapshots. Mutation guards, verification, reconciliation и package probes используют один reader; `ToolCatalogService` больше не строит backend-команды и не разбирает raw JSON самостоятельно. Resource adapter получает только уже проверенный backend payload и сохраняет прежний bounded wire.

Review воспроизвёл **R34**: успешный list payload `{}`/не-array `modules` и успешный module payload без `code` трактовались catalog как пустой/отсутствующий результат и могли попасть в минутный cache. Regression сначала дал ожидаемый 0/1; теперь malformed success завершает загрузку без partial publication/cache, следующее независимое чтение повторяет backend access, а настоящий `modules: []` кэшируется. Reader также fail closed проверяет field types, duplicate names, requested/returned identity, SHA-256 и truncation consistency.

Границы после switch:

- `HostRuntime` по-прежнему владеет document gate, operation root и target binding; reader не открывает и не удерживает gate.
- `VbaToolExecutor` владеет reconciliation, observations, guards, mutations, journal/read-back и текущим ToolResult/resource mapping.
- `ToolCatalogService` владеет только discovery/cache; host-specific COM и live Office authority остаются в adapters/`VbaProjectSupport`.
- Старые `TryReadVbaModule`, list/resource read builders, duplicate name-normalization/not-found helpers и catalog raw parsers удалены. Новые production/Harness/MockDemo includes добавлены; alias/dual-read path нет.

Verification: `vba:` — **58/58 pass**; `host runtime: direct VBA catalog reads share access` — **1/1**; `harness: production projects include all source files` — **1/1**, итого **60 distinct targeted cases**. MockDemo compile — 0 errors / 3 existing CA1416 warnings; `ValidateVersionFormat`, diff/check и затронутые docs links — pass. Full harness и Windows/Office/VSTO не запускались.

Отложено: реальные Excel/Word/PowerPoint VBE list/read, denied Trust Access, large/truncated modules, close/reopen/Save As и catalog refresh под production session; затем отдельно согласовать `VbaMutationService`/`VbaVerifier` начиная с apply_patch. COM/HostRuntime/factories/journal/result wire и Phase 7 этим substep не менялись. Phase 5B2/R04 и полный Phase 6 gate открыты.

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Legacy ToolResult → LegacyToolOutcomeAdapter | ToolRuntime | Unmigrated Office/domain handlers → kernel records | 4B wire switched; handler migrations 6–7 / optional 11 remove mapping; R23 remains |
| RunExecutionSummary projection / old flat read records | Application / Persistence / UI | Messages, ChatRunRecord getter, clones, bridge, static UI | Phase 9: полная projection; старые pending не исполняются и не backfill |
| LegacyToolDefinitionAdapter | ToolRuntime | Current catalog/schema/authoring, legacy execution, source policy projection | Phase 8 typed catalog/ToolPack; domain switches 6–7 / optional authoring 11; central name list removed in 4A |
| LegacyToolResultAdapter | ToolRuntime | Active legacy domain executors → typed result materialization | Handler switches 6–7 / optional 11; no old-history reader |
| ToolResultUiProjection | Application / UI | Native manual commands and Activity projection; never model writer | Phase 9 typed UI projection; manual/domain consumers 6–7 / optional 11 |
| Unbound host identity/access | HostRuntime / host factories | Production adapters, including gated context/catalog reads | 5B2 bound Excel/common lifetime identity + Windows gates; neutral gate не удаляет этот legacy |

Permanent model-session/metadata owners не являются compatibility adapters.
Остальные consumers/removal gates — в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; guard 1C закрывает host-neutral safety assertions, production qualification ещё не выполнена.
- R02 и R07 contained host-neutral, но live-provider/Windows VBE gates открыты; R03–R06 и R08–R10 ожидают своих domain/resource/UI фаз. R11 minimal replay covered в 3B2, full Phase 9/Windows matrix остаётся.
- R04: operation gate проверен host-neutral в 5B1; production bound Excel/common identity и Windows wrong-target scenarios — 5B2.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R22: compact catalog harness failure воспроизведён до изменений 1B; owner ToolPack/Tests, Phase 8.
- R26: full-history preflight, current v4 writer/confirmation, runtime IDs/origins и singleton enforcement проверены host-neutral; 4A заменил temporary name registry source-owned typed policy. Production controller ordering/Office qualification остаются открыты.
- R27: explicit review/reset проверены на current v4/schema14; custom text прежних markers сохраняется до явного review/reset. Production controller/WebView/DPAPI validation открыта.
- R29: runtime-owned IDs введены отдельным v4 switch; полного исходного incident trace нет, Windows/live-provider qualification остаётся открыта. Evidence и ограничения — [R29_RUNTIME_CALL_IDS](R29_RUNTIME_CALL_IDS.md).
- R33: overlapping exact-match ambiguity исправлена host-neutral; 2 regression tests red→green, 8 targeted pass. Реальная Windows/VBE regression остаётся открытой.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
