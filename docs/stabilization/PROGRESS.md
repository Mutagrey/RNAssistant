# Stabilization progress

Current target: 16.1.0
Current phase: Phase 1
Current task: Phase 1A завершена; следующая — Phase 1B, ещё не начата

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | `10e52bf` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | in progress | Этот commit: `test(runtime): characterize false completion and repair limits` | characterization 7/7; ValidateVersionFormat pass | not performed | 1A done; 1B/1C pending; общий DoD Phase 1 не выполнен |
| 2 | pending | — | — | — | ModelProtocol |
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

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Нет новых adapters в Phases 0/1A | — | — | — |

Существующие runtime paths остаются текущей реализацией, а не введёнными adapters.
Их владельцы и фазы замены указаны в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01: false completion воспроизведён в 1A; централизованный guard ожидает Phase 1C.
- R02–R11: остальные сценарии из master plan ожидают проверки/исправления в своих фазах.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- R20: лимит model requests расходится с двадцатью attempts; owner ModelProtocol, Phase 2.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
