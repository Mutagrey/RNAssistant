# Phase 2C3C — coordinated v3 switch/delete

Date: 2026-08-28. Branch: `stabilization/16.1`.
Исходный runtime — `4bbb039`; параллельный governance commit `e2a7844` уточняет
§§14.3, 22–23. Статус: **завершено host-neutral; Windows qualification открыта**.

## Контракт и scope

Один согласованный switch в 15 production files: shared wire, typed result,
client/repair, prompts, accepted-history version и preflight. По §14.3 число
файлов не требует ещё одного подготовительного подэтапа. Phase 3 не начата.
Канонический контракт: [Conversation Response v3](../protocols/CONVERSATION_RESPONSE_V3.md).

- Только `message` и `tool_calls`, без model-owned status/лишних root fields.
  Empty calls завершают model loop; execution health по-прежнему принадлежит
  runtime summary, а не тексту модели.
- На каждом attempt проверяются exact callable schemas, полный accepted-run ID
  scope и singleton для write/external/confirmation/unclassified. Разрешён batch
  независимых локальных reads; rejected batch не резервирует IDs и не исполняется
  частично. Confirmation продолжает исходный logical user turn.
- Native provider refusal — отдельный outcome даже при сопутствующем JSON;
  compatibility probes его отклоняют. Provider/protocol retry budgets не меняются.
- History version 3 и prompt schema 12 переключены вместе. Saved schema 11 text
  сохраняется; ordinary/failed save не подтверждают review. Explicit review
  сохраняет text, explicit reset выбирает настоящие v3 defaults.
- Полная history проверяется до send/edit/retry preparation, manual compaction
  и consumption pending confirmation. Неполный CallContext запрещает raw attempt.
  Old/unknown/malformed history требует нового чата или явного reset, без fallback,
  relabeling, удаления или переписывания streams.

## Изменённые файлы

| Контур | Production files |
|---|---|
| Core wire/result/client | `ModelProtocol/ModelProtocolWire.cs`, `ModelProtocolContracts.cs`, `ModelProtocolClient.cs`, `ConversationResponse.cs` |
| Версии/defaults/cleanup | `Models/AgentResponseModels.cs`, `Models/AppSettings.cs`, `RNAssistant.Core.csproj`; удалены `Tools/AgentResponseParser.cs`, `Tools/AgentResponseSchemaBuilder.cs` |
| Office-neutral consumers | `Services/ConversationRunService.cs`, `ConversationProtocolContext.cs`, `ModelCompatibilityService.cs` |
| Controller preflight | `Controller/AssistantController.Agent.cs`, `AssistantController.ChatExecution.cs`, `AssistantController.Chats.cs` |

Existing harness fixtures переведены на production v3 writers; преобразующий
`AsV3HistoryFixture` и 9 obsolete v2 parser tests удалены. Добавлены три preflight
regressions и один live-client ID/safety test; интеграционные fixtures расширены
для batching, всех result roles и native refusal. Новых test files нет.
Обновлены AGENTS/README, canonical conversation/architecture docs, ADR-0002,
harness README и текущие stabilization status/risk/migration records.

Domain tools, Resource Fabric, VBA, static UI и persistence algorithms этим
protocol commit не меняются. Параллельная работа по отключению pipelines —
отдельный contour, её изменения сохраняются отдельно.

## Проверки

Таргетированные команды используют один актуальный linked C# 7.3 build; после
изменения compile inputs он обновлялся, последующие filters выполнялись с
`--no-build`. Базовая команда для каждого filter из таблицы:

```sh
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"
dotnet run --no-build --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"
```

| Filter | Результат |
|---|---|
| `agent:` | 32 pass: 31 + один targeted rerun (см. ниже) |
| `conversation v3:` | 13/13 |
| `model protocol:` | 15/15 |
| `protocol context:` | 6/6 |
| `preflight` | 3/3; один также входит в `model protocol:` |
| `conversation:` | 4/4 |
| `settings:` | 4/4 |
| `model compatibility:` | 2/2 |
| `completion guard:` | 5/5 |
| `causal trace:` | 6/6 |
| `plan mode:` | 2/2 |
| `chat: uses only` | 1/1 |
| `storage: turn lifecycle` | 1/1 |
| `bridge: typed settings` | 1/1 |
| `chat: prompt save` | 1/1 |
| `harness: production projects` | 1/1 |
| `skills: CRUD preserves` | 1/1 |
| `tools: html workspace updates` | 1/1 |
| `tools: validate payload without` | 1/1 |
| `vba: exact patch preserves boundary` | 1/1 |

Итоговый protocol diff проверен в отдельном detached worktree от `e2a7844`,
без параллельного отключения pipelines. Первый `model compatibility:` дал 2/2,
первый `agent:` — 31/32: в выделенный diff попало одно pipeline-only expectation.
Оно отделено обратно в параллельный contour; после обновления build
`agent: bounds oversized` — 1/1. Остальные 31 agent pass и compatibility 2/2
переиспользованы из того же isolated checkout: их inputs не менялись. Остальная
матрица выполнена на финальном build с `--no-build`. Production pipeline code и
общая рабочая копия при этом не откатывались.

Итого **100 различных targeted cases**. Full harness не запускался: матрица
покрывает изменённый protocol contour и его действующих consumers. R22 — известный
baseline failure compact catalog (16 против 15), здесь не исправляется.

До добавления preflight три новых regression tests падали на тогдашнем v2 path;
после guard прошли, затем переведены на actual v3 и проверены заново. После switch
исправлены ожидаемые несовместимые v2 fixtures, включая streaming, write-batch,
causal trace и saved prompts; старый формат не разрешался ради тестов.

```sh
dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal
git diff --check
```

Version format, diff и затронутые Markdown links/anchors — pass. Удалённые `.cs`
исключены из old-style Core project; project-includes test проходит. UI review
action не меняется в этом commit; исторические 5 JS cases из 2C3B не считаются
новым прогоном и не входят в 100 cases.

## Непроверенные gates и legacy

- Production controllers исключены из harness. Их wiring проверено чтением;
  Windows x64 + Office + VS 2022 должны проверить send/edit/retry, attachment
  preparation, manual compaction, confirmation и реальную WebView delivery.
  VSTO/COM/DPAPI/ClickOnce validation на этой машине не запускалась.
- Live provider strict schema/fallback/native refusal, реальные timeout/latency
  и стоимость generation не квалифицированы. Fake endpoints доказывают только
  локальную логику. R24/R25 и Windows части R26/R27 остаются открыты.
- Live v2 DTO/parser/schema, typed-ID reader, LastRun-only guard и obsolete tests
  удалены без alias/dual-write. Старые streams не мигрируются автоматически.
- Runtime `AgentResponseStatuses`, `Failure.Cause`, metadata bridge и transient
  ID owner остаются до отдельной Phase 3; legacy result/safety mapping и positive
  local-read registry — до Phase 4. Owners/consumers/gates:
  [MIGRATION_MAP](MIGRATION_MAP.md). Нового compatibility adapter нет.

Product остаётся **`16.1.0-dev`**, AssemblyVersion — `16.0.4.0`. Prompt schema 12
и response version 3 — независимые protocol markers, не повторный product bump.
Git tag, push и release script не выполнялись.
