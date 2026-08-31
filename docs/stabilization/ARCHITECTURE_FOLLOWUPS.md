# Отложенные архитектурные улучшения

Дата фиксации: 2026-08-30; статус маршрута обновлён 2026-08-31.

Статус: ordered migration route, не описание уже реализованной архитектуры. Phase
10A–10D и WQ-A1–A5 завершены host-neutral; следующий обязательный runtime gate —
Windows WQ0, затем атомарный production 11T0/7D, по `PROGRESS.md`. Section B и
финальное удаление active legacy явно обязательны до Phase 12. Новые optional product
capabilities этим не допускаются. Если другое предложение становится обязательным,
сначала обновляются master plan/ADR и `MIGRATION_MAP.md` с owner, consumers и removal
gate.

## Целевой путь

```text
WebView -> typed Bridge -> Application
                             |
AgentKernel -> ToolRuntime -> Handler -> DomainService -> HostBackend
                                                       |
                                               DocumentSession -> COM

Application -> IRunStore / IEventStore / IConversationStore
                                           |
                                ChatStore -> JSONL + CAS
```

Новый контур должен подключаться к этому пути, а не добавлять параллельный executor,
store, model wire или UI-owned effect classification.

## Порядок относительно стабилизации

1. Phase 9D4 завершён host-neutral: минимальный `IConversationStore` использует
   существующий `ChatStore` без второго store/dual-write.
2. Phase 9D5 завершён host-neutral: один immutable `RunViewState` переключил
   bridge/JS projection; flat/model-status UI path удалён.
3. Phase 10A подтвердил шесть mandatory dependency directions и выделил exact
   move/removal groups; 10B1/10B2 перенесли identity и VBA host helpers без изменения
   runtime algorithms; 10C1 перенёс application façade без lifecycle changes; 10C2
   перенёс exact resource read projections и удалил `ProjectRead`; 10D сверил
   canonical docs, migration statuses, project includes и checks.
4. WQ-A создал data-only packs, production-path runner и встроенный WQ0 без второго
   executor/store; [qualification contract](../qualification.md) обязателен.
5. При доступной Windows: выполнить in-app WQ0. Затем один production 11T0/7D
   change атомарно вводит 5B2 bound `DocumentSession`/factories, прямой Excel backend
   и удаляет compatibility commands/backends плюс execution fallback; неизвестную
   COM identity semantics не угадывать.
6. Затем ранний 11T переводит built-in Office tools по semantic families; каждый
   switch удаляет свой legacy host path. До WQ0/11T0/7D не создавать compatibility
   scaffolding, которое всё равно вызывает `ExecuteTool(ToolCommand)`.
7. После family switches обязательны direct VBA/controller/custom authoring
   migrations и финальное удаление generic adapter catalog/dispatch, legacy
   definition/result/UI bridges; старая trajectory inference уже удалена отдельным
   host-neutral slice.
8. Milestone WQ и Phase 12 stable core.
9. Остальные улучшения ниже — отдельные post-stable minor changes либо соответствующие
   independently admitted Phase 11 contours. Не включать их в 9D5/Phase 10.

## Когда нужен protocol или interface

- Durable/wire protocol нужен на границе сохранения, процесса или независимо
  обновляемых runtime/UI частей. У него обязательны version/revision, строгий payload,
  fail-closed validation и явная политика несовместимости.
- C# interface нужен на границе side effect, lifetime, host/COM, persistence или
  реально заменяемой реализации. Он должен быть минимальным для своих consumers.
- Pure algorithms, value objects, DTO и stateless transformers не получают interface
  только ради единообразия.
- Не вводить generic `IRepository<T>`, общий event bus, mediator/service locator или
  DI container. Они скроют ordering, durability и document ownership.
- Один durable authority сохраняется: chat events + CAS для conversation и текущие
  VBA journals + CAS для VBA recovery. Новые ports являются adapters над ними, не
  вторыми stores или indexes.

## A. Host capability decomposition

### Мотивация

`IOfficeApplicationAdapter` одновременно несёт current-document identity, context,
selection capture, built-in catalog и generic `ToolCommand` execution. Такая граница
заставляет consumers зависеть от возможностей, которые им не нужны, и удерживает
string-based host dispatch.

### Цель

Последовательно оставить узкие capabilities:

- `IOfficeDocumentSessionProvider` и `IOfficeStaDispatcher` для target/lifetime;
- `IOfficeContextSource` и `IOfficeSelectionSource` для read-only capture;
- существующий `IOfficeDocumentCatalog` для list/activate/open;
- typed host backends для каждого admitted domain;
- ToolRuntime registrations как источник runnable catalog вместо host-owned
  `GetBuiltInTools`.

`ExecuteTool(ToolCommand)` удаляется только после switch последнего consumer. Не
создавать новый широкий `IOfficeHostServices` и не сохранять старый путь как fallback.

### Gate и DoD

- Начинать после 5B2/7D для stable Excel либо внутри отдельного Phase 11 host contour.
- Один change переключает одну capability family и удаляет заменённый path.
- Tests подтверждают document identity, dispatcher/lifetime и отсутствие второго
  dispatch route; COM behavior отдельно квалифицируется на Windows.

## B. Typed vertical slices для Office domains

### Цель

Оставшиеся Excel mutations, затем Word/PowerPoint/Outlook переводятся не массовым
split адаптеров, а по semantic families:

```text
Typed Request -> DomainService -> narrow HostBackend
              -> typed Outcome + EffectEvidence -> ToolRuntime handler
```

Domain service владеет normalization, guard, dry-run semantics, границей начала
effect, read-back и `ok/error/unknown`. Host backend получает bound document object и
не знает model wire, confirmation UI, `ToolDefinition` или legacy `ToolResult`.

Предпочтительные Excel families после stable `inspect/read/write_range`:

1. find/replace;
2. sheet lifecycle;
3. clear/sort/filter/format range;
4. tables;
5. charts.

Не создавать общий `IOfficeMutationService`: у разных domains различаются guards,
verification и recovery semantics. Общими остаются `ToolRuntime`, `HostRuntime`,
`DocumentSession` и typed effect evidence.

### Порядок: parity, затем расширение

Первый проход сохраняет существующие public ids, schemas и observable behavior. Его
цель — не новый набор функций, а удаление legacy execution path. Typed service,
который в production продолжает вызывать generic `ExecuteTool(ToolCommand)`, не
закрывает slice. Один family switch обязан удалить свой host `switch` branch,
legacy result/definition mapper и мёртвые helpers без alias или dual dispatch.

Только после qualification family и подтверждённой потребности по trajectory/evals
допускается отдельный contract expansion:

- Excel: actual ListObject `upsert_table` вместо двусмысленного смешения с 2D matrix;
  multi-key sort, apply/clear и несколько filter criteria, borders/wrap/dimensions;
  `write_range.kind=table` переименовать в `matrix` только versioned cutover без
  alias; `create_report_sheet` — лишь как bounded domain operation с одним target и
  общим read-back;
- Word: exact character-range target, `replace_text` с `exactlyOne`, paragraph/list
  formatting. Не смешивать чтение и mutation в один public tool;
- PowerPoint: stable slide/shape refs раньше сложной композиции;
  `compose_slide` допустим только как одна bounded slide mutation с полным read-back;
- Outlook: exact EntryID target для update/reply/forward, затем flag/unread. Draft и
  send имеют разный risk/effect contract и не объединяются; send не добавляется этим
  контуром.

Не объединять find с replace, upsert с delete или несколько независимых mutations
ради уменьшения числа model calls. Один более крупный tool допустим только когда вся
операция имеет один semantic intent, target, confirmation, effect boundary и recovery
contract. Generic `execute_actions`/arbitrary command list и batch writes запрещены.

### Checklist одного slice

- exact tool ids и source-owned `ToolPolicy`;
- typed request/outcome и bounded inputs/outputs;
- domain service без COM/model/UI/storage DTO;
- narrow backend над bound `DocumentSession`;
- explicit before/dispatch/read-back и unknown-after-possible-effect;
- Agent/manual parity через один handler;
- unit + contract + fake-host fault matrix;
- Windows Office qualification для COM semantics;
- удаление host switch branch, legacy mapper и мёртвых helpers этого slice;
- обновление canonical domain doc, `MIGRATION_MAP.md` и краткого progress evidence.

### Final removal inventory

Active legacy означает второй execution/catalog/result/history path, а не любое имя
`Adapter` или `Compatibility`:

- удалить generic `IOfficeApplicationAdapter.GetBuiltInTools/ExecuteTool`,
  `OfficeToolExecutor` fallback и host tool-id switches после последнего family;
- удалить internal Excel compatibility commands/backends в 11T0/7D;
- удалить VBA mutation/package adapters после direct typed host/backend switch;
- удалить `LegacyToolDefinitionAdapter`, `LegacyToolResultAdapter` и
  `ToolResultUiProjection` после последнего catalog/result/UI consumer;
- pre-R37 trajectory accepted-call inference удалена host-neutral; старые
  несовместимые evidence не переинтерпретируются и требуют нового chat/reset;
- сохранить narrow journal-store ports и model-compatibility diagnostics: они
  используют одну authority и не являются legacy fallback.

## C. Versioned WebView bridge catalog

### Мотивация

Bridge использует большой string-based operation switch, а static JS вызывает те же
имена независимо. C# typed payload сам по себе не обнаруживает отсутствующую,
переименованную или несовместимую JS operation.

### Цель

- `BridgeProtocolVersion` в `init` handshake с fail-closed несовместимостью;
- один `BridgeOperationCatalog` с operation id, request/response DTO, требованием
  bridge token, cancellation и progress policy;
- один canonical JSON casing без постоянного PascalCase/camelCase dual-read;
- router вызывает typed handler; controller остаётся application orchestration;
- contract test сравнивает C# catalog со всеми JS `send(...)` operations и проверяет
  representative serialization/error envelopes.

Code generation, npm/bundler и отдельный network protocol для этого не требуются.
Phase 10 не расширять этим refactoring: bridge catalog остаётся отдельным
post-stable change после Windows WebView qualification.

## D. Явный composition root

### Мотивация

`AssistantController` сейчас одновременно orchestration facade и место сборки
storage, runtime, model, catalog, session и diagnostics services. Это усложняет
замену одного port и lifecycle review.

### Цель

- ручной `AssistantCompositionRoot`/factory создаёт runtime graph;
- controller получает готовые application services, а не строит infrastructure;
- composition root владеет dispose order и только там доступны concrete stores и
  production host factories;
- optional contours подключаются явными registrations, без service locator и
  feature flag, создающего второй execution path.

Начинать после 9D4/9D5, когда persistence и UI ports стабилизированы. Не использовать
размер constructor/file как самостоятельное основание; change должен удалить
конкретные construction dependencies из controller и сохранить lifecycle tests.

## E. Дополнительные architecture checks

Phase 10A реализовал checks, соответствующие уже переключённым boundaries.
Остальные активируются вместе с removal gate соответствующего follow-up:

- concrete `ChatStore` доступен только Storage, canonical adapters и composition root;
- raw string event append недоступен Office consumers;
- новые `ToolCommand`/legacy `ToolResult` запрещены в `Domains` и typed host backends;
- после 5B2/7D `ActiveWorkbook` запрещён в execution/mutation paths;
- host adapters не добавляют новые public tool-id switches;
- UI не зависит от executors/domain services и не выводит effect из prose;
- bridge operation ids после введения catalog имеют C#/JS parity;
- новые `.cs` включены в old-style `.csproj`, а namespace dependency direction
  соответствует `docs/architecture.md`.

Текущий `architecture: mandatory dependency direction` проверяет Core.Agent → no
Office/UI, ModelProtocol → no tool execution, VBA → no UI, resources → no
AgentKernel, OfficeHosts → no WebView types и UI/bridge → no domain executors.
Assembly/folder ownership host-specific helpers закрывается отдельными `git mv`
groups Phase 10B, а не расширением этого source-token check.

Architecture tests должны проверять forbidden dependencies/symbols и production
source inclusion, а не фиксировать случайные размеры файлов или текущее число
классов.

## Anti-goals

- Не дробить `ChatStore`, controller или host adapters на `partial` только ради LOC.
- Не создавать второй read model/store для UI или diagnostics.
- Не добавлять скрытую compatibility migration старых chats/tool formats.
- Не вводить универсальный Office object model поверх Excel/Word/PowerPoint/Outlook.
- Не менять `AgentKernel` ради подключения domain/UI contour без отдельного ADR.
- Не считать успешный COM return доказательством effect без domain read-back.
