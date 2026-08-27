# Stabilization progress

Current target: 16.1.0
Current phase: Phase 0
Current task: Phase 0 завершена; Phase 1 не начата

Historical baseline: `v16.0.4` = `225a05bb44dd7701892b5f8c98ea2e3b342274a7`.
Branch: `stabilization/16.1`. Новый baseline tag не создаётся.
Обязательный источник требований: [master plan](STABILIZATION_MASTER_PLAN.md).

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | Этот commit: `chore(versioning): adopt release-only versioning` | ValidateVersionFormat pass; harness 7/7 | not performed | Только governance/build versioning; target установлен один раз |
| 1 | pending | — | — | — | Characterization, causal trace, P0 containment; не начата |
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

## Verification

- Baseline: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "harness:"` — 2/2 pass до изменений versioning.
- После изменений та же команда — 7/7 pass; весь linked host-neutral source set скомпилирован.
- `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal` — pass.
- Проверены повторные builds/commits без bump, invalid metadata, dirty/staged tree, release tag matching, dev rejection, changelog, local/remote tag collisions и SDK/old-style assembly metadata.
- Git fixtures создаются и удаляются только во временных каталогах; настоящий origin и его tags не изменяются.
- Полный набор runtime tests не запускался: выбран минимальный build/versioning filter, production behavior не менялся.
- PowerShell release script не запускался (`pwsh` отсутствует); Windows x64 + Office x64 + VS 2022 / VSTO / ClickOnce — not performed.

## Active compatibility adapters

| Adapter | Owner | Consumers | Removal phase |
|---|---|---|---|
| Нет новых adapters в Phase 0 | — | — | — |

Существующие runtime paths остаются текущей реализацией, а не введёнными adapters.
Их владельцы и фазы замены указаны в [MIGRATION_MAP.md](MIGRATION_MAP.md).

## Open P0/P1 risks

- R01–R11: сценарии из master plan, ожидают characterization/проверки в своих фазах.
- R16: Assembly/ClickOnce и Windows x64 + Office x64 + VS 2022 qualification не выполнены.
- R19: PowerShell release workflow требует проверки на release workstation.
- Подробности и защиты: [RISK_REGISTER.md](RISK_REGISTER.md).
