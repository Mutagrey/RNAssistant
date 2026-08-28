# RNAssistant — мастер-план очистки, стабилизации и подготовки стабильного ядра

**Статус:** обязательный план исполнения  
**Исходная база:** `main`, продуктовая версия `16.0.4`  
**Целевой стабильный релиз:** `16.1.0`  
**Главная ветка стабилизации:** `stabilization/16.1`  
**Основной принцип:** модель предлагает действия; runtime единолично определяет, что реально произошло.

---

## 0. Как пользоваться этим документом

Этот документ предназначен для агента, который будет менять репозиторий. Его нельзя исполнять как одну большую задачу или один огромный patch.

### Обязательные правила для агента

1. Выполнять только текущую фазу и текущий подэтап.
2. Не начинать следующую фазу, пока не выполнен Definition of Done текущей.
3. Не добавлять новые продуктовые функции во время стабилизации.
4. Не повышать продуктовую версию и не создавать Git tag, если это прямо не указано в разделе релиза.
5. Не менять одновременно runtime, UI, persistence, resources и Office/VBA ради одного локального исправления.
6. Если обнаружен дефект вне текущего контура:
   - зафиксировать его в `docs/stabilization/RISK_REGISTER.md` или `BACKLOG.md`;
   - не исправлять его попутно, кроме дефекта уровня P0, угрожающего данным или создающего ложный успех.
7. Один commit должен менять один инвариант или один чёткий этап миграции.
8. Не смешивать в одном commit изменение поведения, массовое форматирование, перемещение файлов, изменение протокола, изменение UI и повышение версии.
9. Не создавать новые frameworks, универсальные state machine, универсальные transaction engine или новые абстракции «на будущее».
10. Сохранять совместимость с C# 7.3 и .NET Framework 4.8.
11. Новые `.cs` добавлять в old-style `.csproj`.
12. После каждого подэтапа обновлять `docs/stabilization/PROGRESS.md`.
13. В отчёте всегда указывать изменённые файлы, обеспеченный инвариант, тесты, требуемую Windows/Office validation и оставшиеся риски.
14. Никогда не утверждать, что Office/VSTO поведение проверено, если не было реального запуска на Windows x64 с Office x64.
15. Не создавать tag после обычного commit.
16. Каждый подэтап завершать локальной чисткой и сокращением рабочего контекста по §15.1; Phase 10 не является общей отсрочкой удаления заменённых путей.

---

# 1. Решение, которое фиксируется этим планом

RNAssistant не переписывается с нуля и не откатывается полностью к старой версии.

Сохраняются сильные части текущего проекта:

- native-like описания tools;
- append-only история;
- CAS и revision-pinned resources;
- точные VBA-патчи;
- hash/CAS guards;
- read-back;
- VBA mutation journal;
- dynamic tools;
- WebView2 UI;
- локальное Office execution;
- bounded protocol repair до 20 попыток;
- режимы Chat/Agent и, позднее, Plan.

Но меняется центр архитектуры.

Текущая система больше не должна строиться вокруг:

- `ConversationRunService` как общего монолита;
- Resource Fabric как универсального execution fabric;
- меняющегося LRU-набора tool schemas;
- model-owned `status`;
- общего `ToolResult`, который смешивает protocol state, confirmation, error, retry и mutation state;
- `ActiveWorkbook` как неявного target;
- текста сообщения как источника safety-critical решений.

Целевая система строится вокруг небольших независимых контуров:

```text
UI
  ↓
Application Facade
  ↓
Agent Kernel
  ├── Model Protocol
  ├── Tool Runtime
  └── Run Store
          ↓
      Domain Tools
          ↓
      Domain Services
          ↓
      Host Runtime / Document Session
          ↓
      Excel / VBE / Office COM

Resource Fabric — отдельный read/data plane.
Persistence записывает факты, но не управляет выполнением.
```

---

# 2. Цели стабилизации

К релизу `16.1.0` должны выполняться следующие условия.

```text
Изменение LLM provider
    не требует изменения AgentKernel.

Добавление tool
    не требует изменения AgentKernel.

Исправление VBA patching
    не требует изменения Resource Fabric.

Добавление Word
    не требует изменения Excel.

Изменение UI
    не меняет правила tool execution.

Изменение persistence
    не меняет решение о результате внешней операции.

Добавление resource provider
    не меняет model protocol.

Модель не может объявить внешнее изменение успешным.

Переключение активного окна Office
    не меняет target уже начатого run.

Неопределённый внешний эффект
    никогда не повторяется автоматически.
```

## Не-цели первого стабильного релиза

В `16.1.0` не требуется полностью стабилизировать и включить:

- dynamic tool authoring;
- сложные pipelines;
- HTML write/edit contour;
- автономный Plan mode;
- Word;
- PowerPoint;
- Outlook;
- browser agent;
- автоматическое создание и починку tools;
- универсальное управление всеми Office hosts;
- вторую LLM-модель для проверки действий.

Эти контуры могут остаться в source tree, но не должны входить в release-critical path, пока не мигрированы отдельно.

---

# 3. Архитектурная конституция RNAssistant

Следующие правила обязательны для всех будущих изменений.

1. **LLM proposes; runtime decides.**
2. Текст модели не доказывает внешний эффект.
3. `AgentKernel` не знает об Excel, VBA, COM, WebView2, CustomXML и `rna://`.
4. Model protocol не исполняет tools.
5. Tool runtime не знает о LLM provider, UI и chat rendering.
6. Domain complexity остаётся внутри domain.
7. Resource — существительное; Tool — действие.
8. Resource Fabric не владеет execution state.
9. Tool schemas не исчезают скрыто во время run.
10. Write target привязывается явно.
11. `ActiveWorkbook` допустим только для начального выбора target, но не для выполнения.
12. Неопределённый write-effect никогда не retry автоматически.
13. Safety-critical решения не принимаются через поиск слов в `message` или exception text.
14. Persistence записывает состояние, но не выбирает следующий шаг.
15. UI отображает состояние, но не выводит его самостоятельно.
16. Новый domain не должен требовать изменения `AgentKernel`.
17. Скрытые fallback запрещены.
18. Reject/repair attempts модели не входят в accepted conversation history.
19. Общие контракты должны быть маленькими; внутренние domain-модели могут быть сложными.
20. Новый слой допускается только для реальной границы ответственности, а не ради количества классов.

---

# 4. Приоритеты принятия решений

Если требования конфликтуют, агент использует следующий порядок:

1. Сохранность данных.
2. Отсутствие ложного успеха.
3. Детерминированность.
4. Наблюдаемость.
5. Простота границ.
6. Тестируемость.
7. Обратная совместимость.
8. Производительность.
9. Ширина функционала.

Нельзя сохранять сложный backward-compatible fallback, если он делает результат недетерминированным или скрывает ошибку.

---

# 5. Целевые контуры

## 5.1. Application Facade

Отвечает только за начало run, выбор режима, привязку session/document, cancel, confirmation и передачу результата UI.

Не содержит LLM repair, JSON parser, tool dispatch, VBA logic, resource cursor logic или event projection.

Пример будущих точек входа:

```csharp
StartRunAsync(...)
ContinueAfterConfirmationAsync(...)
CancelRunAsync(...)
GetRunViewAsync(...)
```

## 5.2. Agent Kernel

Это минимальный host-neutral цикл.

Он знает только:

- `AgentMessage`;
- `AgentResponse`;
- `ToolCall`;
- `ToolExecutionRecord`;
- `RunSummary`;
- интерфейсы `IModelProtocol`, `IToolRuntime`, `IRunStore`.

Он не знает OpenAI/tLLM, HTTP, JSON repair, Excel, COM, Resource URI, HTML, plan documents, tool installation или WebView2.

Минимальный цикл:

```text
accepted history
    ↓
IModelProtocol.SendAsync
    ↓
AgentResponse
    ↓
tool_calls empty?
    ├── yes → завершить model loop
    └── no  → ToolRuntime.Execute
                  ↓
              ToolResult
                  ↓
              append accepted result
                  ↓
              следующий model step
```

`tool_calls == []` означает только, что модель больше не предлагает вызовов. Это не означает, что предыдущие изменения успешно применены.

## 5.3. Model Protocol

Отвечает за получение одного корректного typed `AgentResponse`.

Внутри него находятся provider adapter, transport retries, protocol retries, extraction JSON, schema validation, compatibility fallback `json_schema → json_object`, защита от tLLM safety/protection responses и redacted diagnostics.

`AgentKernel` никогда не видит rejected attempts.

## 5.4. Tool Runtime

Отвечает только за exact tool id lookup, argument schema validation, policy validation, confirmation gate до исполнения, вызов `IToolHandler`, преобразование infrastructure exception в `ToolResult` и запись `ToolExecutionRecord`.

Он не знает внутренности VBA, Excel, HTML или Plan.

## 5.5. Tool Domains

Группируют tools по предметной области:

```text
Common
Resources
Excel
Vba
Html
Plan
Skills
System
Word
PowerPoint
Outlook
Browser
```

Каждый tool — небольшой adapter:

```text
parse arguments
→ call domain service
→ map domain result to ToolResult
```

Tool не должен содержать весь COM/journal/CAS workflow.

## 5.6. Domain Services

Здесь живёт сложность.

Для VBA:

```text
VbaReader
VbaMutationService
VbaPatchEngine
VbaJournal
VbaVerifier
VbaTextCanonicalizer
VbaPackageService
```

Для Excel:

```text
ExcelInspector
ExcelRangeReader
ExcelRangeWriter
ExcelChartService
ExcelFormatService
```

Domain service может иметь богатую внутреннюю state model. Наружу она не протекает.

## 5.7. Host Runtime

Отвечает за Office process/host identity, document identity, bound document session, STA/COM dispatch, per-document serialization, host lifetime, проверку target и фактический Office backend.

Host Runtime не знает о LLM и не формирует user-facing messages.

## 5.8. Resource Fabric

Сохраняется, но становится data plane.

Отвечает только за canonical URI, identity, revision, list, resolve, search, read, bounded content, immutable CAS references и stale cursor rejection.

Не отвечает за run status, tool schema lifecycle, execution authority, mutation verification, confirmation, terminal state и model retry.

## 5.9. Persistence

Отвечает за append-only accepted conversation events, domain journals, CAS, replay, projections и migrations/reset policy.

Persistence не определяет, выполнен ли tool успешно, какой tool вызвать, закончен ли run и что должен показать UI как итог.

## 5.10. UI

UI получает готовый `RunViewState` и отдельно отображает narrative модели, runtime lifecycle, execution health, tool activity, подтверждённые изменения, ошибки, unknown effects и pending confirmation.

UI не анализирует текст модели и не вычисляет успех по наличию финального сообщения.

---

# 6. Разрешённые зависимости

```text
RNAssistant.Core.Agent
    → Core.Protocols
    → Core.Tools.Abstractions
    → Core.Persistence.Abstractions

RNAssistant.Core.Agent
    ✗ Office
    ✗ OfficeHosts
    ✗ WebView2
    ✗ WinForms
    ✗ VBA

RNAssistant.Core.ModelProtocol
    → LLM transport abstractions
    → Protocol contracts
    ✗ Office
    ✗ UI
    ✗ Tool executors

RNAssistant.Office.Application
    → AgentKernel
    → ToolRuntime
    → Session services
    → Host abstractions

RNAssistant.Office.Domains.Vba
    → Tool abstractions
    → Host/VBA abstractions
    → Persistence abstractions
    ✗ UI
    ✗ ModelProtocol

RNAssistant.OfficeHosts.Excel
    → Office host abstractions
    → Excel/COM
    ✗ AgentKernel implementation details
    ✗ Web UI

web
    → typed bridge DTO only
```

После стабилизации эти правила должны контролироваться architecture tests.

---

# 7. Минимальные протоколы

Общие протоколы намеренно остаются небольшими.

## 7.1. Conversation Response v3

Текущий model-owned `status` удаляется.

### Вызов tool

```json
{
  "message": "Прочитаю текущий модуль и внесу точечное изменение.",
  "tool_calls": [
    {
      "id": "call_17",
      "name": "common.vba_apply_patch",
      "arguments": {
        "component": "Module1",
        "old_text": "old",
        "new_text": "new"
      }
    }
  ]
}
```

### Финальное сообщение

```json
{
  "message": "Обработка завершена.",
  "tool_calls": []
}
```

Обязательные правила:

- root содержит только `message` и `tool_calls`;
- `message` — string;
- `tool_calls` — всегда array;
- каждый call имеет `id`, `name`, `arguments`;
- неизвестные root fields отклоняются;
- модель не возвращает `status`, `phase`, `completed`, `retry`, `verified`;
- один и тот же `tool_call_id` не повторяется в accepted run;
- write/external/confirmation-required call должен быть единственным call в ответе;
- несколько независимых read-only calls допускаются и выполняются последовательно.

### Совместимость

Совместимость с историческими v2-чатами не является требованием. После cutover несовместимый чат явно пропускается либо сбрасывается отдельным действием пользователя; его stream не переписывается и не удаляется автоматически. Новый run не должен молча продолжаться с урезанной историей старого чата.

Временный v2 adapter допустим только при доказанной необходимости для действующего consumer до его переключения по §15.1, а не ради сохранения старого формата. Если такой adapter ещё нужен:

```text
v2 status игнорируется как источник runtime truth;
v2 completed → только «модель завершила свой loop»;
v2 in_progress → определяется по непустому tool_calls.
```

Новые accepted события записываются только в v3 после cutover. Dual-write запрещён.

## 7.2. Tool Descriptor v1

Model-facing descriptor остаётся native-like:

```json
{
  "name": "common.vba_apply_patch",
  "description": "Apply an exact, unambiguous patch to one VBA component.",
  "parameters": {
    "type": "object",
    "properties": {
      "component": { "type": "string" },
      "old_text": { "type": "string" },
      "new_text": { "type": "string" }
    },
    "required": ["component", "old_text", "new_text"],
    "additionalProperties": false
  }
}
```

В descriptor не входят storage path, pipeline body, source code, installation state, VBE components, UI renderer, runtime state, confirmation record и resource continuation.

## 7.3. Tool Policy v1

Runtime-only metadata хранится отдельно:

```json
{
  "effect": "write",
  "verification": "tool",
  "requires_confirmation": true,
  "allowed_modes": ["agent"],
  "risk_level": 2
}
```

Минимальные значения:

```text
effect:
  read
  write
  external

verification:
  tool
  none
```

`write` включает document/local mutation. `external` — действие за пределами локального состояния, например отправка сообщения или browser action.

## 7.4. Tool Result v1

Model-facing результат имеет только три состояния:

```text
ok
error
unknown
```

### Успех

```json
{
  "tool_call_id": "call_17",
  "name": "common.vba_apply_patch",
  "status": "ok",
  "message": "Patch applied and verified.",
  "data": {
    "component": "Module1"
  }
}
```

### Определённая ошибка

```json
{
  "tool_call_id": "call_17",
  "name": "common.vba_apply_patch",
  "status": "error",
  "message": "The target text was not found.",
  "data": {
    "code": "target_not_found"
  }
}
```

### Неопределённый внешний эффект

```json
{
  "tool_call_id": "call_17",
  "name": "common.vba_apply_patch",
  "status": "unknown",
  "message": "The VBE write may have been dispatched, but the final state could not be verified.",
  "data": {
    "code": "final_state_unverified"
  }
}
```

Не добавлять в общий контракт `Success` рядом со `status`, `partial_failure`, `rolled_back`, `prepared`, `committed`, `retryable`, `awaiting_user`, обязательный `journal_status` или отдельный `error` object, дублирующий `data.code`.

Богатые состояния могут существовать внутри domain service и domain journal.

## 7.5. Tool Execution Record

Это runtime-only запись, не новый model protocol:

```text
ToolCall
ToolDescriptor identity
ToolPolicy snapshot
ToolResult
StartedAt
CompletedAt
DocumentRuntimeId
Correlation ids
```

Она нужна, чтобы runtime формировал итог независимо от текста модели.

## 7.6. Run Summary

Не вводить десятки run statuses. Разделить две оси.

### Lifecycle

```text
running
completed
awaiting_confirmation
cancelled
failed
```

`completed` означает: model loop завершён. Это не означает, что все изменения применены.

### Execution health

```text
clean
errors
unknown
```

Правила агрегации:

1. Если любой write/external tool вернул `unknown`, health = `unknown`.
2. Иначе если любой tool вернул `error`, health = `errors`.
3. Иначе health = `clean`.
4. Rejected model attempts не влияют на execution health.
5. Protocol exhaustion завершает lifecycle = `failed`.
6. Pending confirmation даёт lifecycle = `awaiting_confirmation`.
7. Cancellation после возможного dispatch не может маскировать unknown effect.
8. UI не показывает «все изменения применены», если health не `clean`.

Пример:

```json
{
  "lifecycle": "completed",
  "execution_health": "errors",
  "assistant_message": "Готово, изменения внесены.",
  "tool_counts": {
    "read_ok": 2,
    "write_ok": 1,
    "write_error": 1,
    "write_unknown": 0
  }
}
```

UI обязан показать runtime warning, несмотря на текст модели.

## 7.7. Tool Pack Snapshot

Tool registry остаётся динамическим, но набор callable schemas становится наблюдаемым и стабильным.

```json
{
  "id": "excel-vba-core",
  "revision": "sha256:...",
  "mode": "agent",
  "host": "Excel",
  "tools": [
    "common.resources_read",
    "excel.read_range",
    "excel.write_range",
    "common.vba_apply_patch"
  ]
}
```

Правила:

- core pack определяется детерминированно по mode/host/profile;
- core schemas передаются полностью;
- во время одного model step snapshot неизменяем;
- optional extension создаёт новый snapshot revision на границе step;
- добавление extension записывается событием;
- schema не вытесняется LRU до завершения run;
- скрытое исчезновение schema запрещено;
- если pack не помещается в budget, runtime завершает подготовку явной ошибкой;
- model не должна «чинить» собственный execution environment.

## 7.8. Resource v1

```json
{
  "uri": "rna://vba/component/Module1",
  "revision": "sha256:...",
  "content_type": "text/plain",
  "content": "..."
}
```

Или для большого content:

```json
{
  "uri": "rna://...",
  "revision": "sha256:...",
  "content_type": "application/json",
  "content_ref": "sha256:..."
}
```

Resource не содержит execution authority или tool state.

## 7.9. Document Session v1

Внутренний обязательный контракт:

```text
StableDocumentId
RuntimeDocumentId
Host
BoundDocumentObject
IsAlive
StaDispatcher
MutationGate
```

Правила:

- создаётся до Office execution;
- target не меняется из-за переключения активного окна;
- write/read-back выполняются через тот же bound object;
- mutation сериализуется по `RuntimeDocumentId`;
- закрытый document не заменяется другим Active document;
- fallback на `ActiveWorkbook` внутри agent path запрещён.

---

# 8. Retry и repair

## 8.1. Provider retry

Относится к transport/provider:

- timeout;
- connection;
- HTTP 5xx;
- gateway failure;
- временная недоступность endpoint.

## 8.2. Protocol retry

Ответ получен, но не является accepted AgentResponse:

- tLLM protection/safety response;
- HTML вместо JSON;
- пустой ответ;
- truncated JSON;
- malformed JSON;
- schema violation;
- неизвестный tool id в strict contract;
- duplicate call id.

Сохраняется configurable limit `1–20`. Не уменьшать лимит без отдельного решения.

Каждая попытка:

- использует исходный accepted prompt/history;
- не включает rejected response в chat history;
- не добавляет correction message в canonical transcript;
- не исполняет tools;
- не меняет resources;
- не меняет ToolPack;
- записывается только в diagnostics/trajectory;
- имеет отдельный `modelAttemptId`.

После исчерпания лимита:

```text
ModelProtocolFailure
```

Это не `ToolResult.error` и не `unknown`.

## 8.3. Tool retry

Общий ToolRuntime не повторяет tools автоматически.

Retry может происходить только внутри adapter, который понимает операцию:

```text
safe HTTP GET → adapter may retry
LLM malformed JSON → ModelProtocol may retry
VBA write unknown → retry запрещён
Excel write unknown → retry запрещён
```

Общее поле `Retryable` из ToolResult удаляется.

---

# 9. Dynamic tools без потери гибкости

Все tools переделывать сразу не требуется.

Используется миграция:

```text
новый contract
→ adapter для текущего ToolDefinition
→ перенос критических built-in tools
→ перенос optional domains
→ удаление legacy adapter после завершения миграции
```

Текущий `ToolDefinition` необходимо постепенно разделить на:

```text
ToolDescriptor
ToolPolicy
ToolBinding
ToolPackageMetadata
ToolDiscoveryMetadata
```

### ToolDescriptor

- id/name;
- description;
- arguments schema.

### ToolPolicy

- effect;
- verification;
- confirmation;
- allowed modes;
- risk.

### ToolBinding

- handler/executor identity;
- entry point.

### ToolPackageMetadata

- package version;
- storage path;
- source/components;
- installation status.

### ToolDiscoveryMetadata

- use when;
- do not use when;
- limitations;
- capability status.

Добавление нового built-in tool после стабилизации должно требовать descriptor + policy + handler + tests + optional docs. Оно не должно требовать изменения `AgentKernel`, `ModelProtocol`, Resource Fabric и UI.

---

# 10. VBA: где допускается сложность

Общий ToolResult остаётся простым, но внутри VBA сохраняется строгая логика:

```text
read live state
validate expected state
build patch in memory
prepare journal
dispatch one COM mutation
read back
verify
commit terminal journal
map to ok/error/unknown
```

## Обязательное разделение

### VbaPatchEngine

Чистая функция:

```text
source + exact operations
→ patched source or deterministic error
```

Не знает о COM, journal, session и UI.

### VbaTextCanonicalizer

Единственное место для:

```text
TransportText
CanonicalText
VbeComparableText
```

В нём определяются CRLF/LF, пустые строки, VBE normalization, transport escaping и comparable hashing.

Hash/CAS/read-back не реализуют собственную нормализацию.

### VbaMutationService

Владеет guard, journal, dispatch, read-back и reconciliation.

### VbaJournal

Хранит богатые internal statuses:

```text
prepared
committed
not_applied
rolled_back
failed
unknown
```

Но наружу tool возвращает:

```text
committed → ok
not_applied → error или ok/no-op по domain semantics
definite failed before effect → error
unknown → unknown
```

### Compile validation

Запись кода и компиляция — разные результаты.

```text
mutation = ok
compile_validation = error
```

не означает, что patch не применён.

Нельзя неявно откатывать подтверждённую запись только из-за compile error без отдельной явной policy.

### Запрещено

- fuzzy patch в release-critical path;
- line-number patch как authority;
- повтор unknown mutation;
- rollback classification через `message.Contains(...)`;
- silent catch;
- смена workbook между write/read-back;
- отдельные алгоритмы newline normalization в разных файлах.

---

# 11. Целевая структура файлов

На первом этапе не создавать много новых `.csproj`. Сначала вводятся папки и namespaces внутри существующих assemblies.

```text
src/

RNAssistant.Core/
    Agent/
        AgentKernel.cs
        AgentRunContext.cs
        RunSummary.cs
        ExecutionHealth.cs

    ModelProtocol/
        IModelProtocol.cs
        AgentResponseV3.cs
        AgentResponseV3Parser.cs
        AgentResponseV3SchemaBuilder.cs
        ModelProtocolClient.cs
        ModelProtocolDiagnostics.cs
        ProtocolRetryPolicy.cs
        Providers/

    Tools/
        ToolDescriptor.cs
        ToolPolicy.cs
        ToolBinding.cs
        ToolResult.cs
        ToolExecutionRecord.cs
        ToolPackSnapshot.cs
        ToolRegistry.cs

    Resources/
        ResourceRef.cs
        ResourceDescriptor.cs
        ResourceRevision.cs

    Persistence/
        IRunStore.cs
        IConversationStore.cs
        IEventStore.cs

RNAssistant.Office/
    Application/
        AgentFacade.cs
        RunController.cs
        ConfirmationCoordinator.cs

    Runtime/
        ToolRuntime.cs
        ToolHandlerRegistry.cs
        LegacyToolAdapter.cs

    Domains/
        Vba/
            VbaReader.cs
            VbaMutationService.cs
            VbaPatchEngine.cs
            VbaTextCanonicalizer.cs
            VbaVerifier.cs
            VbaJournal.cs

        Excel/
            ExcelInspector.cs
            ExcelRangeReader.cs
            ExcelRangeWriter.cs

        Html/
        Plan/
        Skills/

    Tools/
        Common/
        Resources/
        Vba/
        Excel/
        Html/
        Plan/

    Resources/
        ResourceService.cs
        Providers/

RNAssistant.OfficeHosts/
    Runtime/
        OfficeDocumentSession.cs
        OfficeStaDispatcher.cs
        OfficeDocumentIdentity.cs

    Excel/
        ExcelDocumentSession.cs
        ExcelInteropBackend.cs
        ExcelVbaBackend.cs

    Word/
    PowerPoint/
    Outlook/

web/
    app.js
    app-chat.js
    app-diagnostics.js
    ...
```

Отдельный `.csproj` создаётся только при наличии реальной dependency/platform boundary.

Не создавать проекты вида `RNAssistant.Agent.Abstractions.Common.Runtime` или `RNAssistant.Tools.Shared.Core.Engine` только ради формальной чистоты.

---

# 12. Целевая документация

```text
ARCHITECTURE.md

docs/
    architecture/
        OVERVIEW.md
        BOUNDARIES.md
        DEPENDENCIES.md
        INVARIANTS.md
        STATE_MODEL.md
        CURRENT_TO_TARGET_MAP.md

    protocols/
        CONVERSATION_RESPONSE_V3.md
        TOOL_DESCRIPTOR_V1.md
        TOOL_POLICY_V1.md
        TOOL_RESULT_V1.md
        TOOL_PACK_V1.md
        RESOURCE_V1.md
        DOCUMENT_SESSION_V1.md
        EVENT_MODEL.md

    domains/
        VBA.md
        EXCEL.md
        RESOURCES.md
        HTML.md
        PLAN.md
        SKILLS.md
        DYNAMIC_TOOLS.md

    operations/
        MODEL_RETRY.md
        ERROR_HANDLING.md
        CONCURRENCY.md
        RECOVERY.md
        VERSIONING.md
        RELEASE_PROCESS.md

    testing/
        TEST_STRATEGY.md
        VBA_FAULT_MATRIX.md
        OFFICE_INTEGRATION.md
        RELEASE_GATES.md

    stabilization/
        STABILIZATION_MASTER_PLAN.md
        PROGRESS.md
        RISK_REGISTER.md
        BACKLOG.md
        MIGRATION_MAP.md

    decisions/
        ADR-0001-model-does-not-own-completion.md
        ADR-0002-conversation-response-v3.md
        ADR-0003-tool-result-three-states.md
        ADR-0004-resource-data-plane.md
        ADR-0005-bound-document-session.md
        ADR-0006-tool-pack-snapshot.md
        ADR-0007-release-only-versioning.md
        ADR-0008-unknown-effects-are-not-retried.md
```

`ARCHITECTURE.md` в корне должен быть коротким индексом, а не копией всех документов.

Существующие документы не переносить массово в одном commit. Для каждого контура:

1. создать новый canonical document;
2. перенести актуальные правила;
3. отметить старый документ superseded;
4. обновить ссылки;
5. удалить старый документ после завершения соответствующей миграции.

---

# 13. Версионирование: новая обязательная политика

## 13.1. Главная ошибка текущей схемы

Commit не является release.

Требование «перед каждым commit повысить версию и после commit создать tag» отменяется полностью.

## 13.2. Базовое решение

- `v16.0.4` остаётся последним pre-stabilization release tag.
- Вся программа стабилизации работает на целевой линии `16.1.0`.
- На ветке стабилизации используется `16.1.0-dev`.
- Обычные commits не меняют product version.
- Обычные commits не получают tags.
- Следующий major `17.0.0` не создаётся только из-за внутреннего рефакторинга.
- Major повышается только при намеренном несовместимом изменении документированного внешнего контракта.

## 13.3. Что считается внешним breaking change

Major может быть оправдан, только если ломается хотя бы один опубликованный контракт без совместимого migration path:

- публичный bridge/API;
- формат пользовательских tool packages;
- durable storage, который нельзя безопасно прочитать или явно мигрировать;
- CLI arguments;
- публичный automation API;
- совместимость пользовательских integrations.

Не являются причиной major:

- перемещение классов;
- разделение монолита;
- изменение internal JSON между model и harness;
- новый parser;
- новый AgentKernel;
- перестройка Resource Fabric;
- исправление багов;
- добавление внутреннего adapter.

## 13.4. Release tags

Допускается максимум следующая основная цепочка:

```text
v16.1.0-alpha.1   # только если существует реально тестируемая alpha
v16.1.0-beta.1    # VBA/Excel vertical slices работают на Windows
v16.1.0-rc.1      # все release gates пройдены
v16.1.0           # stable
```

Этап можно пропустить. Не нужно создавать alpha/beta только потому, что завершилась внутренняя фаза.

Дополнительный prerelease tag создаётся только при необходимости отдать новый build тестерам после существенного исправления:

```text
v16.1.0-beta.2
v16.1.0-rc.2
```

Нельзя создавать tag после документации, рефакторинга, каждого bugfix commit, каждого merge, изменения одной фазы или локального build.

## 13.5. Product, assembly, build и protocol versions

Разделить четыре понятия.

### Product version

```text
16.1.0-dev
16.1.0-beta.1
16.1.0
```

Показывается пользователю и используется для release tag.

### Assembly compatibility version

Рекомендуется держать стабильной внутри major-линии:

```text
AssemblyVersion = 16.0.0.0
```

до реального перехода на major `17`. Это уменьшает binding churn. Перед применением проверить VSTO/ClickOnce требования.

### File/Application version

Числовая версия:

```text
16.1.0.<buildNumber>
```

Build number создаётся CI/release script и не требует commit.

### Informational/build identity

```text
16.1.0-dev+g<shortSha>
```

Diagnostics должна показывать ProductVersion, CommitSha, BuildUtc, branch/channel и protocol versions.

Идентификация конкретного build больше не требует нового tag.

### Protocol versions

Версионируются независимо:

```text
conversation-response: 3
tool-result: 1
resource: 1
event payload: per event type
tool package: package-owned
```

Повышение protocol version не требует автоматического повышения major product version.

## 13.6. Изменения в репозитории

Обновить:

- `README.md`;
- `AGENTS.md`;
- `Directory.Build.props`;
- `Directory.Build.targets`;
- version validation tests;
- release scripts.

Удалить правило:

```text
version in working tree must be greater than HEAD before every commit
```

Ввести проверки:

```text
ValidateVersionFormat
ValidateReleaseTagMatchesProductVersion
ValidateReleaseTreeClean
ValidateReleaseChangelog
ValidateTagDoesNotExist
```

`ValidateReleaseTagMatchesProductVersion` запускается только для release/tag build.

## 13.7. Release script

Добавить единый script, например:

```text
tools/Prepare-Release.ps1
```

Он должен:

1. Проверить clean working tree.
2. Проверить выбранную ветку.
3. Проверить формат version.
4. Обновить product version/suffix.
5. Запустить required tests.
6. Проверить changelog.
7. Создать release commit.
8. Создать annotated tag только после успешной проверки.
9. Не перемещать существующие tags.
10. Не делать push без явного параметра.

Обычный агентский commit этот script не вызывает.

## 13.8. Changelog

Добавить `CHANGELOG.md`:

```text
[Unreleased]
  Added
  Changed
  Fixed
  Removed
  Security
```

User-visible изменения попадают в `[Unreleased]`.

Внутренняя работа по стабилизации дополнительно фиксируется в `docs/stabilization/PROGRESS.md`.

---

# 14. Branching, commits и change budget

## 14.1. Ветки

```text
main
    только стабильный или release-candidate код

stabilization/16.1
    интеграционная ветка стабилизации

stab/<phase>-<task>
    короткоживущие рабочие ветки
```

Для одиночной работы допустимо коммитить прямо в `stabilization/16.1`, но не в `main`.

## 14.2. Commit format

Примеры:

```text
docs(architecture): define stabilization boundaries
chore(versioning): adopt release-only versioning
test(runtime): reproduce false completion after failed write
fix(runtime): derive execution health from tool results
refactor(model): extract protocol retry from conversation loop
refactor(tools): introduce tool runtime adapter
refactor(vba): isolate exact patch engine
```

## 14.3. Change budget

Обычный change должен затрагивать:

```text
один domain
+ его tests
+ при необходимости его docs
+ при необходимости минимальный UI projection
```

Сигналы остановки:

- production change затрагивает более трёх контуров;
- требуется менять `AgentKernel`, ModelProtocol, Resources, Persistence и UI одновременно;
- один локальный feature меняет более 10 production files;
- PR содержит массовые rename и behavior changes;
- появляется новый универсальный status;
- появляется fallback, не описанный в protocol/ADR.

В этом случае задача разбивается на:

```text
introduce
adapt
switch
delete
```

---

# 15. Миграционная стратегия

Каждый контур переносится по одинаковой схеме.

1. **Characterize** — зафиксировать текущее поведение тестами.
2. **Introduce** — добавить новый контракт рядом со старым.
3. **Adapt** — подключить старую реализацию через adapter.
4. **Switch one vertical slice** — перевести один реальный сценарий.
5. **Verify** — unit/integration/fault tests.
6. **Delete old path** — удалить заменённую ветку.
7. **Update docs** — сделать новый документ canonical.
8. **Record progress** — отметить этап в `PROGRESS.md`.

Не допускается длительное сосуществование:

```text
LegacyRuntime
NewRuntime
FallbackRuntime
CompatibilityRuntime
```

Compatibility adapter имеет владельца, срок удаления и список оставшихся consumers.

## 15.1. Обязательная локальная чистка после каждого подэтапа

Чистка входит в Definition of Done текущего подэтапа, а не копится до Phase 10. Её цель — уменьшать число действующих путей и объём контекста для следующего шага, сохраняя доказательства корректности.

1. **Проверить потребителей.** Через targeted search установить, кто ещё использует заменённые contracts, helpers и adapters, включая tests и project includes. Удалять путь только после switch потребителей и релевантной проверки. Если обязательная Windows/Office проверка остаётся gate, явно сохранить её как блокер удаления.
2. **Удалить заменённое.** Удалить ставшие ненужными implementation branches, aliases, fallbacks, helpers и project includes. Удалять obsolete tests только вместе с заменённым контрактом; сохранять покрытие актуальных инвариантов. Не сохранять мёртвый код или совместимость со старыми чатами «на всякий случай».
3. **Ограничить временные adapters.** Для каждого оставшегося adapter указать owner, конкретных consumers, причину сохранения и ближайший removal substep/gate в `MIGRATION_MAP.md`. После исчезновения consumers удалить в том же подэтапе; Phase 10 не служит сроком по умолчанию. Существующий runtime consumer нельзя удалять лишь потому, что он legacy.
4. **Сократить актуальную документацию.** Обновить canonical doc изменённого контура, убрать из него отменённые инструкции и дубли. Исторические ADR/отчёты/verification evidence сохранять как историю, а не обязательное чтение следующего шага. Не удалять действующие требования или открытые риски ради числа строк.
5. **Оставить короткий контекст продолжения.** В начале `PROGRESS.md` поддерживать текущий подэтап, следующий шаг, его gates, оставшийся legacy и ссылки только на необходимые документы/разделы. Подробные результаты сохранять ниже или в существующем отчёте подэтапа; не копировать историю в каждый новый отчёт.
6. **Проверить и зафиксировать.** Проверить dangling references и `.csproj`, запустить минимальную релевантную проверку; для docs-only — diff/links без build. В отчёте указать удалённое, оставленное с причиной и обязательный контекст следующего шага. Если чистить нечего, так и записать; искусственное дробление файлов и косметические изменения не требуются.

Работать только в текущем контуре и в change budget §14.3. Массовые moves/renames не смешивать с behavior changes; проблемы других контуров записывать в backlog. Отказ от исторической совместимости не разрешает автоматически удалять chats/events/CAS/VBA journals, settings, API key или custom tools; reset требует отдельного явного действия. Safety и recovery evidence не ослабляются.

## 15.2. Рефакторинг, который облегчает миграцию

Перед изменением контура оценить, мешает ли смешение обязанностей ближайшему шагу текущей фазы. Если да, выделить небольшой подготовительный подэтап внутри этой фазы; отдельная общая кампания рефакторинга до миграции не нужна. Если целевое извлечение уже решает проблему, выполнять его напрямую, без промежуточного сервиса, который сразу придётся заменять.

До начала рефакторинга кратко зафиксировать в задаче/отчёте:

1. Какое конкретное ближайшее изменение станет проще и какие callers сейчас вынуждают читать монолит.
2. Какая ответственность получит одного владельца и какие зависимости/общее mutable state перестанут пересекать эту границу.
3. Какая минимальная проверка покажет сохранение поведения и позволит проверять выделенный контракт отдельно; существующие tests предпочтительнее нового набора.
4. Какие consumers переключатся, какой старый путь будет удалён и какой ближайший removal gate останется при поэтапном switch.

Критерий пользы — следующее локальное изменение можно понять и проверить по контракту и его реализации без изучения несвязанных областей. Уменьшение числа строк, файлов или токенов само по себе не является результатом. Новый `partial`, передача всего controller/session без необходимости или набор callbacks обратно в монолит могут лишь разнести прежнюю связанность; без объяснения новой границы такое выделение не выполнять. `Partial` допустим как короткий механический шаг к конкретному извлечению, но не как его завершение.

Подготовительное выделение сохраняет поведение; изменение семантики выполняется явным следующим подэтапом с его проверками. Соблюдать change budget §14.3, C#/.csproj requirements и cleanup §15.1. Не переносить архитектуру следующих фаз заранее: например, при извлечении AgentKernel не менять Resource Fabric/ToolPack lifecycle, а при выделении текстового VBA engine не менять journal/CAS protocol. Существующие доменные services переиспользовать; не создавать универсальные обёртки и временные дубликаты.

Конкретные точки и фазы указаны в `MIGRATION_MAP.md` и ниже; перед своей фазой повторно проверить актуальных consumers. После закрытия подэтапа записать, какие обязанности больше не смешаны и какие файлы/контракты нужны следующему шагу. Контекст сужается внутри контура, но не обязан монотонно уменьшаться при переходе к новой области. Если полезного выделения нет, продолжать миграцию без обязательного распила.

---
# 16. Поэтапный план исполнения

---

## Phase 0 — Freeze, governance и versioning

### Цель

Остановить архитектурный дрейф и прекратить создание версии/tag после каждого commit.

### Выполнить

- [ ] Создать ветку `stabilization/16.1`.
- [ ] Зафиксировать `v16.0.4` как исходную историческую точку; новый baseline tag не нужен.
- [ ] Добавить этот документ в `docs/stabilization/STABILIZATION_MASTER_PLAN.md`.
- [ ] Создать:
  - [ ] `PROGRESS.md`;
  - [ ] `RISK_REGISTER.md`;
  - [ ] `BACKLOG.md`;
  - [ ] `MIGRATION_MAP.md`.
- [ ] Обновить `AGENTS.md`: feature freeze, работа по фазам, запрет per-commit tags.
- [ ] Обновить `README.md`: новая release-only policy.
- [ ] Один раз установить development target `16.1.0-dev`.
- [ ] Удалить проверку «version > HEAD на каждый commit».
- [ ] Добавить release-only validation.
- [ ] Добавить `CHANGELOG.md`.
- [ ] Добавить `docs/operations/VERSIONING.md`.
- [ ] Добавить `docs/operations/RELEASE_PROCESS.md`.
- [ ] Добавить ADR-0007.
- [ ] Убедиться, что обычный build не требует повышения версии.
- [ ] Не создавать tag после завершения Phase 0.

### Запрещено

- менять Agent loop;
- менять tool protocol;
- переносить файлы;
- добавлять новые features;
- создавать `v16.1.0-alpha.1`.

### Definition of Done

- обычный commit проходит version validation без изменения product version;
- release tag validation существует отдельно;
- документация не требует tag на commit;
- `16.1.0-dev` идентифицируется commit SHA в diagnostics/build metadata;
- полный текущий harness не сломан изменениями versioning.

---

## Phase 1 — Characterization, causal trace и P0 containment

### Цель

Сделать ложный успех воспроизводимым, видимым и временно заблокировать его до полного извлечения kernel.

### 1A. Characterization

- [ ] Найти все пути, где model `status` копируется в:
  - [ ] `ChatTurnResult`;
  - [ ] `LastRun`;
  - [ ] controller response;
  - [ ] bridge DTO;
  - [ ] UI.
- [ ] Добавить тесты:
  - [ ] model says completed after write tool error;
  - [ ] model says completed after write tool unknown;
  - [ ] model says completed without write call;
  - [ ] write tool ok, final message present;
  - [ ] protocol repair succeeds on attempt 20;
  - [ ] all 20 attempts invalid;
  - [ ] rejected attempts отсутствуют в accepted history.
- [ ] Зафиксировать current-to-target map для:
  - [ ] `ConversationRunService`;
  - [ ] `OfficeToolExecutor`;
  - [ ] `ToolDefinition`;
  - [ ] `ProgressiveToolWorkingSet`;
  - [ ] VBA executors;
  - [ ] Excel adapter;
  - [ ] Resource Fabric;
  - [ ] persistence;
  - [ ] UI.

### 1B. Causal trace

Ввести или проверить correlation ids:

```text
sessionId
runId
turnId
stepId
modelAttemptId
toolCallId
documentRuntimeId
mutationId
```

Стадии:

```text
run.started
model.request.prepared
model.attempt.rejected
model.response.accepted
tool.execution.started
tool.execution.completed
domain.effect.prepared
domain.effect.dispatched
domain.effect.verified
run.summary.created
ui.projected
```

Не превращать это в новую execution state machine. Это observability.

### 1C. Transitional completion guard

До появления нового `AgentKernel` добавить один централизованный guard:

- model terminal status не является итоговым успехом;
- runtime учитывает actual ToolResults;
- write error не позволяет UI показать «изменения применены»;
- write unknown имеет приоритет;
- отсутствие write calls означает обычный ответ, а не подтверждённую mutation;
- никакого анализа текста модели;
- guard проектируется как будущий `RunSummaryBuilder`, а не временный if-лес.

### Definition of Done

- исходный false-success тест красный до fix и зелёный после;
- causal trace показывает границу model/tool/domain/UI;
- в UI/bridge присутствует runtime-owned execution health;
- model text не может скрыть `error` или `unknown`;
- текущий response v2 пока поддерживается, но его `status` не является proof of effect.

---

## Phase 2 — Извлечение ModelProtocol

### Цель

Полностью вынести tLLM repair/provider compatibility из Agent loop.

### Выполнить

- [ ] Ввести `IModelProtocol`.
- [ ] Вынести raw endpoint call из `ConversationRunService`.
- [ ] Разделить provider retry и protocol retry.
- [ ] Сохранить configurable `1–20` protocol attempts.
- [ ] Каждая retry-попытка использует clean accepted prompt.
- [ ] Rejected attempts записываются только в diagnostics.
- [ ] Ввести typed `ModelProtocolFailure`.
- [ ] Ввести parser/schema builder для Conversation Response v3.
- [ ] Проверить необходимость введённого v2 adapter по §15.1; не подключать ради старых чатов, удалить после switch последних действующих consumers.
- [ ] Добавить tests для:
  - [ ] tLLM protection response;
  - [ ] HTML response;
  - [ ] invalid JSON;
  - [ ] schema violation;
  - [ ] strict schema rejection и один local fallback;
  - [ ] valid attempt после серии invalid;
  - [ ] cancellation;
  - [ ] endpoint timeout.
- [ ] Обновить `docs/protocols/CONVERSATION_RESPONSE_V3.md`.
- [ ] Добавить ADR-0002.

### Запрещено

- менять Office tools;
- менять Resource URI;
- менять VBA journal;
- добавлять новый planner/router;
- добавлять model self-repair events в history.

### Definition of Done

`ConversationRunService` получает один typed `AgentResponse` или `ModelProtocolFailure` и не знает, сколько было raw attempts.

---

## Phase 3 — Минимальный AgentKernel и runtime truth

### Цель

Извлечь host-neutral deterministic loop и окончательно забрать terminal authority у модели.

### Выполнить

- [ ] Создать `AgentKernel`.
- [ ] По §15.2 отделить извлекаемый цикл `ConversationRunService` от подготовки prompts/compaction/media и материализации результатов; использовать существующие services, не менять ToolPack/Resource Fabric semantics Phase 8.
- [ ] Обычный запуск и confirmation continuation в `AssistantController.Agent` подключить к общей kernel-логике учёта выполнения; сохранить confirmation/fingerprint gates и отдельную проверку controller wiring.
- [ ] Создать `RunSummary`.
- [ ] Создать `ExecutionHealth`.
- [ ] Создать `ToolExecutionRecord`.
- [ ] Подключить текущий executor через adapter.
- [ ] Перевести текущий цикл на accepted model response, tool execution, accepted tool result, next step и run summary.
- [ ] Удалить direct mapping model `completed` → `RunStatus=completed`.
- [ ] Не принимать model `blocked/refused` как runtime truth без локальной классификации; текст при этом сохраняется как narrative.
- [ ] Confirmation оставить runtime-owned.
- [ ] Добавить pure tests с fake model/fake tool:
  - [ ] read ok;
  - [ ] write ok;
  - [ ] write error;
  - [ ] write unknown;
  - [ ] error then success;
  - [ ] success then error;
  - [ ] unknown then model says done;
  - [ ] cancellation before tool;
  - [ ] cancellation after possible dispatch;
  - [ ] iteration limit;
  - [ ] duplicate call id.
- [ ] Обновить state model docs.
- [ ] Добавить ADR-0001 и ADR-0008.

### Definition of Done

`AgentKernel` тестируется без Excel, WebView2, HTTP и real LLM. Model wording не влияет на execution health.

---

## Phase 4 — Tool contracts и ToolRuntime

### Цель

Получить маленький масштабируемый runtime без переделки всех tools одновременно.

### Выполнить

- [ ] Ввести:
  - [ ] `ToolDescriptor`;
  - [ ] `ToolPolicy`;
  - [ ] `ToolBinding`;
  - [ ] `ToolPackageMetadata`;
  - [ ] `ToolResult v1`;
  - [ ] `IToolHandler`;
  - [ ] `ToolRuntime`;
  - [ ] `ToolHandlerRegistry`.
- [ ] Добавить `LegacyToolDefinitionAdapter`.
- [ ] Из `OfficeToolExecutor` извлекать общий validation/policy/confirmation/dispatch runtime, переиспользуя уже выделенные domain executors; не дробить каждый dispatch branch и не менять document binding до Phase 5.
- [ ] Не удалять текущие tools сразу.
- [ ] Перенести один read-only tool первым.
- [ ] Проверить exact id lookup.
- [ ] Проверить schema validation.
- [ ] Проверить confirmation gate до execution.
- [ ] Runtime enforce:
  - [ ] write/external call единственный в response;
  - [ ] read-only calls могут быть последовательным списком;
  - [ ] никакого generic auto retry.
- [ ] Убрать дублирующий `Success + Status` в новом contract.
- [ ] Добавить model-facing serializer Tool Result v1.
- [ ] Обновить protocol docs.
- [ ] Добавить ADR-0003.

### Definition of Done

Новый read-only tool добавляется через descriptor + policy + handler + tests без изменения AgentKernel.

---

## Phase 5 — Bound DocumentSession и HostRuntime

### Цель

Исключить неверный workbook/document target и гонки активного окна.

### Выполнить

- [ ] Ввести `IOfficeDocumentSession`.
- [ ] Ввести `ExcelDocumentSession`.
- [ ] Выделить выбор/удержание workbook из `ExcelAdapter` и границу document access/serialization из `OfficeToolExecutor`; read-back должен получать тот же bound object. Charts/formatting и прочие host adapters не рефакторить попутно.
- [ ] Bind конкретного document object до execution.
- [ ] Сериализовать writes по `RuntimeDocumentId`.
- [ ] Удалить fallback на `ActiveWorkbook` из agent mutation path.
- [ ] `ActiveWorkbook` оставить только для user action «выбрать текущую книгу».
- [ ] Write и read-back выполнять через один bound object.
- [ ] Проверять `IsAlive` до dispatch.
- [ ] Явно определить close/cancel semantics.
- [ ] Добавить fake host tests.
- [ ] Добавить Windows integration scenarios:
  - [ ] switch workbook before write;
  - [ ] switch workbook during operation;
  - [ ] close bound workbook;
  - [ ] Save As identity change;
  - [ ] two chats write same workbook;
  - [ ] two workbooks with same visible name.
- [ ] Добавить ADR-0005.
- [ ] Обновить concurrency docs.

### Definition of Done

Переключение active workbook не может перенаправить уже начатый run.

---

## Phase 6 — VBA vertical slice

### Цель

Стабилизировать наиболее опасный write contour до переноса остальных mutations.

### Порядок

1. `vba.read`.
2. `vba.apply_patch`.
3. whole-module write.
4. delete.
5. restore.
6. package operations.

### Выполнить

- [ ] Извлечь `VbaPatchEngine` из `VbaToolExecutor.Patching`: текстовая логика отдельно от `ToolResult`, resource-подсказок, COM и journal orchestration.
- [ ] Извлечь `VbaTextCanonicalizer`, включая используемые правила из `VbaToolManifestParser`; переключить patch/verification/package consumers без второй реализации нормализации и без изменения journal/CAS protocol.
- [ ] Определить Transport/Canonical/VBE-comparable representations.
- [ ] Извлечь `VbaReader`.
- [ ] Извлечь `VbaMutationService`.
- [ ] Извлечь `VbaVerifier`.
- [ ] Сохранить current journal/CAS evidence.
- [ ] Удалить string-based rollback classification.
- [ ] Маппировать domain result в `ok/error/unknown`.
- [ ] Не выносить internal journal states в общий ToolResult.
- [ ] Compile validation хранить отдельно.
- [ ] Unknown mutation не retry.
- [ ] Exact patch остаётся strict и unambiguous.
- [ ] Добавить fault injection:
  - [ ] before journal prepare;
  - [ ] after prepare/before COM;
  - [ ] COM throws before mutation;
  - [ ] COM mutates then throws;
  - [ ] read-back unavailable;
  - [ ] read-back mismatch;
  - [ ] terminal journal write fails;
  - [ ] cancellation before dispatch;
  - [ ] cancellation after dispatch;
  - [ ] restart after prepared;
  - [ ] VBE newline normalization;
  - [ ] duplicate target;
  - [ ] target not found.
- [ ] Добавить real Excel/VBE test checklist.

### Definition of Done

Любой VBA write завершается одним из трёх model-facing результатов, причём `ok` для built-in verified tool означает успешный read-back.

---

## Phase 7 — Excel read/write vertical slice

### Цель

Перенести базовый Excel contour на те же границы.

### Выполнить

- [ ] Перенести `inspect/read_range`.
- [ ] Перенести `write_range`.
- [ ] Выделить только необходимый read/write backend из `ExcelAdapter` на подготовленной в Phase 5 DocumentSession; размер остальных частей adapter не является поводом расширять slice.
- [ ] Write tool использует bound `ExcelDocumentSession`.
- [ ] Добавить read-back/verification для write.
- [ ] Сохранить range limits до COM materialization.
- [ ] Добавить tests:
  - [ ] values;
  - [ ] formulas;
  - [ ] empty range;
  - [ ] oversized range;
  - [ ] protected sheet;
  - [ ] closed workbook;
  - [ ] switched active workbook;
  - [ ] write error before dispatch;
  - [ ] unverified final state.
- [ ] Не переносить charts/formatting до стабильности basic slice.

### Definition of Done

Excel read/write добавлены через ToolRuntime и DocumentSession, AgentKernel не изменён.

---

## Phase 8 — Resource Fabric и ToolPack

### Цель

Оставить сильные resource invariants, но убрать Resource Fabric из execution control plane.

### Выполнить

- [ ] Зафиксировать `Resource = data`.
- [ ] Сохранить `rna://`, revisions, CAS, cursors.
- [ ] Удалить зависимость AgentKernel от resource capability lifecycle.
- [ ] Ввести `ToolPackSnapshot`.
- [ ] Core Excel/VBA pack передавать полностью.
- [ ] Отключить LRU eviction в stabilized runtime.
- [ ] Optional schema loading делать monotonic:
  - [ ] explicit request;
  - [ ] new snapshot revision;
  - [ ] event;
  - [ ] no eviction.
- [ ] Global dynamic registry сохранить.
- [ ] Новые dynamic tools активировать в следующем run либо через явный snapshot extension.
- [ ] Если pack не помещается, fail visibly.
- [ ] Resource tools оставить read-only.
- [ ] Capability discovery и tool authoring разделить.
- [ ] Добавить ADR-0004 и ADR-0006.

### Definition of Done

Resource provider можно добавить без изменения AgentKernel и tool execution semantics.

---

## Phase 9 — Persistence и UI projection

### Цель

Сделать stored/replayed truth равной runtime truth и убрать inference из UI.

### Выполнить

- [ ] Ввести или нормализовать:
  - [ ] `IRunStore`;
  - [ ] `IConversationStore`;
  - [ ] `IEventStore`.
- [ ] Разделить:
  - [ ] Agent Events;
  - [ ] Domain Diagnostic Events.
- [ ] Accepted model/tool events остаются canonical.
- [ ] Rejected model attempts остаются diagnostics.
- [ ] Replay должен восстанавливать тот же `RunSummary`.
- [ ] UI получает typed `RunViewState`.
- [ ] Отдельно отображать:
  - [ ] model message;
  - [ ] lifecycle;
  - [ ] execution health;
  - [ ] verified writes;
  - [ ] failed calls;
  - [ ] unknown effects;
  - [ ] pending confirmation.
- [ ] Удалить UI logic, основанную на model status/message.
- [ ] Проверить stale projection и multi-window updates.
- [ ] Не переписывать CAS/event framework целиком.
- [ ] Не вводить второй durable source of truth.

### Definition of Done

После restart/replay UI показывает тот же authoritative outcome, что был рассчитан при выполнении.

---

## Phase 10 — Physical cleanup и architecture tests

### Цель

После стабилизации поведения привести структуру файлов и документов в соответствие с реальными boundaries.

Это финальная структурная сверка, а не начало чистки. Заменённые пути, мёртвые зависимости и устаревшие инструкции удаляются в своих подэтапах по §15.1.

### Выполнить

- [ ] Переместить файлы через `git mv`.
- [ ] Не смешивать moves с behavior changes.
- [ ] Обновить namespaces.
- [ ] Обновить old-style `.csproj`.
- [ ] Проверить отсутствие забытых legacy branches; удалить оставшиеся после переключения последних consumers, не повторять уже выполненную локальную чистку.
- [ ] Проверить отсутствие superseded canonical docs; исторические evidence/ADR не считать действующими инструкциями.
- [ ] Добавить architecture tests:
  - [ ] Core.Agent не зависит от Office;
  - [ ] ModelProtocol не зависит от Tools execution;
  - [ ] VBA не зависит от UI;
  - [ ] Resources не зависят от AgentKernel;
  - [ ] OfficeHosts не зависят от WebView;
  - [ ] UI не зависит от domain executors.
- [ ] Обновить `ARCHITECTURE.md`.
- [ ] Обновить `AGENTS.md` под фактическую архитектуру.
- [ ] Закрыть `MIGRATION_MAP.md`.

### Definition of Done

Файловая структура отражает контуры, а architecture tests предотвращают повторное смешение.

---

## Phase 11 — Optional contours

Каждый контур переносится отдельной minor feature после stable core либо как отдельный post-beta milestone.

Порядок:

1. Plan.
2. HTML.
3. Skills authoring.
4. Dynamic tools.
5. Pipelines.
6. Word.
7. PowerPoint.
8. Outlook.
9. Browser.

### Правило допуска

Контур включается, только если:

- использует существующий AgentKernel;
- использует ToolRuntime;
- имеет domain service;
- использует DocumentSession при Office writes;
- имеет unit/contract/integration tests;
- не требует нового model-owned status;
- не добавляет hidden fallback;
- не меняет Resource Fabric execution semantics.

Если добавление контура требует изменения AgentKernel, сначала создаётся ADR и доказывается недостаточность текущего контракта.

---

## Phase 12 — Release hardening

### Цель

Подготовить `16.1.0` как стабильное ядро.

### Release scope

Включить:

- Chat;
- Agent;
- ModelProtocol repair;
- fixed core ToolPack;
- resources list/resolve/search/read;
- Excel inspect/read/write;
- VBA read;
- VBA exact patch;
- VBA whole-module write;
- VBA delete/restore;
- confirmation;
- history/replay;
- diagnostics;
- bound document session.

Опционально включить только после прохождения собственных gates:

- skills read.

Отключить до отдельной стабилизации:

- Plan mutations;
- HTML mutations;
- dynamic tool authoring;
- pipelines;
- Word/PowerPoint/Outlook;
- browser;
- autonomous self-repair.

### Qualification

- [ ] Полный host-neutral harness.
- [ ] Architecture tests.
- [ ] ModelProtocol 20-attempt scenarios.
- [ ] VBA fault matrix.
- [ ] Excel fault matrix.
- [ ] Windows x64 + Office x64 smoke.
- [ ] Switch/close/Save As tests.
- [ ] Restart/replay tests.
- [ ] Concurrent chats same document.
- [ ] No false-positive completion.
- [ ] No automatic retry after unknown.
- [ ] No ActiveWorkbook mutation fallback.
- [ ] No string-based safety decisions.
- [ ] Release documentation.
- [ ] Changelog.
- [ ] Clean install/upgrade/reset story.
- [ ] Diagnostics show product version + commit.

### Tag sequence

Создавать tag только при наличии distributable build:

```text
v16.1.0-alpha.1   optional
v16.1.0-beta.1    optional
v16.1.0-rc.1      required before stable
v16.1.0           stable
```

---
# 17. Обязательная матрица acceptance-сценариев

| Сценарий | Ожидаемый результат |
|---|---|
| tLLM возвращает protection response 19 раз, затем valid JSON | AgentKernel получает один accepted response |
| Все 20 attempts invalid | lifecycle `failed`, tools не запускались |
| Model вызывает неизвестный tool | protocol/tool validation error, без fuzzy match |
| Read tool возвращает error | health `errors`, model может продолжить |
| Write tool error до dispatch | `error`, можно сформировать новый явный call |
| Write мог быть dispatched, read-back невозможен | `unknown`, auto retry запрещён |
| Model пишет «патч внесён» после error | UI показывает runtime error |
| Model пишет «патч внесён» после unknown | UI показывает unverified state |
| Write ok и read-back verified | `ok`, verified write count увеличен |
| Один write ok, другой error | health `errors`, нельзя показать «всё применено» |
| Любой unknown среди calls | health `unknown` |
| Write + confirmation в batch | runtime отклоняет batch; write должен быть single call |
| Несколько независимых reads | выполняются последовательно |
| Переключение active workbook | bound target не меняется |
| Закрытие bound workbook | fail/unknown, другая книга не используется |
| Cancellation до dispatch | cancelled без effect |
| Cancellation после возможного dispatch | reconcile или unknown |
| Restart после journal prepared | read-only reconciliation, no replay write |
| VBE изменил line endings | единый comparable normalizer |
| Replay event stream | тот же RunSummary |
| Dynamic tool установлен mid-run | не появляется скрыто; новый snapshot/event |
| ToolPack не помещается | явная ошибка, не silent truncation |
| Resource cursor stale | fail closed |
| UI получает старую projection | revision mismatch, no overwrite |

---

# 18. Release gates

Stable release запрещён, пока не выполнены все условия.

1. Ноль false-positive mutation success в fault tests.
2. Model response не содержит runtime status.
3. Каждый built-in write возвращает `ok/error/unknown`.
4. `ok` verified built-in write означает read-back.
5. `unknown` не повторяется автоматически.
6. Target write привязан к `DocumentSession`.
7. Agent mutation path не использует `ActiveWorkbook` fallback.
8. ToolPack core не меняется скрыто.
9. Нет LRU eviction callable schemas в stabilized path.
10. Resource Fabric не определяет execution outcome.
11. UI не выводит mutation success из model text.
12. Replay восстанавливает execution health.
13. Нет safety-critical string matching.
14. Нет silent catch в mutation critical path.
15. AgentKernel тестируется без Office и real LLM.
16. ModelProtocol тестируется без AgentKernel.
17. VBA patch engine тестируется без COM.
18. Architecture dependency tests проходят.
19. Version/tag создаются только release process.
20. Документация соответствует коду.

---

# 19. Risk register — стартовый набор

| Риск | Контур-владелец | Обязательная защита |
|---|---|---|
| Ложный success после tool failure | AgentKernel | Runtime RunSummary |
| tLLM protection вместо JSON | ModelProtocol | Stateless protocol retry |
| Write применён, но ответ потерян | Domain/Host | `unknown` + reconciliation |
| Patch применён не к той книге | HostRuntime | Bound DocumentSession |
| LRU удалил schema | ToolPack | No eviction in run |
| Model не знает о tool | ToolPack/Discovery | Deterministic core pack |
| VBE normalizes source | VBA | Single canonicalizer |
| Journal и live state расходятся | VBA | Read-only recovery |
| Cancellation после COM dispatch | Host/Domain | Unknown/reconciliation |
| UI показывает устаревший статус | UI/Persistence | Revisioned projection |
| Replay меняет outcome | Persistence | Deterministic RunSummary |
| Локальный fix затрагивает десятки файлов | Architecture | Boundaries + change budget |
| Новые версии на каждый commit | Release process | Release-only versioning |
| Legacy и new paths живут бесконечно | Migration | Owner + removal gate |
| Feature flags становятся второй архитектурой | Application | Temporary explicit release scope |

---

# 20. Что категорически запрещено во время стабилизации

- Полный rewrite.
- Полный rollback к старой версии.
- Второй LLM для подтверждения COM-effect.
- Анализ model wording для определения успеха.
- Автоматический retry unknown write.
- Новый универсальный transaction framework.
- Actor/CQRS/Event Sourcing framework поверх текущей системы.
- Новый hidden router/planner.
- Dynamic LRU в release-critical path.
- Fuzzy tool id matching.
- Fuzzy VBA patch как стандарт.
- Silent fallback к ActiveWorkbook.
- Silent fallback к latest resource revision.
- Dual-write старого и нового event format.
- Длительная поддержка двух runtime loops.
- Массовый перенос файлов одновременно с изменением поведения.
- Tag после каждого commit.
- Major bump из-за внутреннего refactoring.
- Создание новых projects без dependency boundary.
- Generic `Result<T1,T2,T3,...>` для всей системы.
- Универсальные статусы, отражающие все внутренние состояния всех domains.

---

# 21. Первые рекомендуемые commits

Порядок первых изменений:

```text
1. docs(stabilization): add master plan and progress templates

2. chore(versioning): adopt release-only product versioning

3. test(runtime): reproduce false completion after failed and unknown writes

4. obs(runtime): add run step tool and mutation correlation trace

5. fix(runtime): derive execution health from actual tool results

6. refactor(model): extract stateless model protocol retry

7. feat(protocol): introduce conversation-response v3

8. refactor(agent): introduce host-neutral AgentKernel

9. refactor(tools): introduce ToolRuntime and legacy adapter

10. refactor(host): bind ExcelDocumentSession for agent execution

11. refactor(vba): migrate read and exact patch vertical slice

12. refactor(excel): migrate basic range read and write
```

Ни один из этих commits, кроме будущего release commit, не получает tag.

---

# 22. Definition of Done для любой задачи

Задача не считается завершённой, пока:

- [ ] ответственность находится в правильном контуре;
- [ ] выполнена локальная чистка по §15.1; у каждого оставшегося adapter есть consumers, причина и ближайший removal gate;
- [ ] если выполнялся подготовительный рефакторинг, обоснована и проверена польза по §15.2, а не только уменьшен файл;
- [ ] актуальный контекст следующего шага в `PROGRESS.md` не требует перечитывать все завершённые подэтапы;
- [ ] публичный контракт минимален;
- [ ] нет нового hidden fallback;
- [ ] нет safety logic по тексту;
- [ ] есть тест нормального сценария;
- [ ] есть тест ошибки;
- [ ] для write есть fault/unknown scenario;
- [ ] обновлён canonical doc;
- [ ] при изменении решения добавлен ADR;
- [ ] новые файлы добавлены в `.csproj`;
- [ ] targeted harness пройден;
- [ ] cross-subsystem change прошёл full harness;
- [ ] Windows/Office validation явно отмечена как выполненная или невыполненная;
- [ ] `PROGRESS.md` обновлён;
- [ ] version не повышена без release;
- [ ] tag не создан без release.

---

# 23. Формат отчёта агента после этапа

```markdown
## Выполненный этап

Phase:
Task:

## Изменённые инварианты

- ...

## Изменённые файлы

- path — причина изменения

## Тесты

- command — result
- Windows/Office — performed / not performed

## Поведение до

- ...

## Поведение после

- ...

## Не выполнено

- ...

## Новые риски

- ...

## Legacy path

- removed / still used by ...
- remaining owner / consumers / reason / nearest removal substep and gate ...
- obsolete helpers/tests/docs removed or no cleanup needed ...
- if refactored: next change simplified / dependencies removed / focused verification ...
- next-step required context: canonical docs/sections and open gates ...

## Versioning

- Product version changed: no
- Tag created: no
```

Для release milestone поля version/tag меняются осознанно.

---

# 24. Формат `PROGRESS.md`

```markdown
# Stabilization progress

Current target: 16.1.0
Current phase: Phase N
Current task: ...
Next step: ...
Required context: ссылки на canonical docs/sections; не вся история этапов.
Open gates / remaining legacy: ...

| Phase | Status | Commit/PR | Tests | Windows validation | Notes |
|---|---|---|---|---|---|
| 0 | done | ... | pass | n/a | ... |
| 1 | in progress | ... | ... | ... | ... |
| 2 | pending | | | | |

## Active compatibility adapters

| Adapter | Owner | Consumers | Reason / nearest removal substep and gate |
|---|---|---|---|
| Только фактически нужный adapter | ... | Конкретные действующие consumers | ... |

## Open P0/P1 risks

- ...
```

---

# 25. Критерий завершения всей программы

Стабилизация завершена, когда при любом дефекте сразу понятно, какой контур является владельцем:

```text
LLM вернул не JSON
→ ModelProtocol

Agent завершился неверно
→ AgentKernel / RunSummary

Tool не найден или args invalid
→ ToolRuntime

VBA patch построен неверно
→ VbaPatchEngine

VBE write не подтверждён
→ VbaMutationService / HostRuntime

Выбрана не та книга
→ DocumentSession

Resource stale
→ Resource Fabric

История/replay расходятся
→ Persistence

UI показывает неверный статус
→ UI projection
```

И добавление обычного нового tool не требует менять все эти контуры одновременно.

---

# 26. Итоговая последовательность

```text
Freeze и versioning
    ↓
Characterization и causal trace
    ↓
P0 completion guard
    ↓
ModelProtocol
    ↓
AgentKernel
    ↓
ToolRuntime
    ↓
DocumentSession
    ↓
VBA vertical slice
    ↓
Excel vertical slice
    ↓
Resource Fabric / ToolPack
    ↓
Persistence / UI
    ↓
Physical cleanup
    ↓
Optional contours
    ↓
Release qualification
    ↓
16.1.0
```

Основная проверка каждого архитектурного решения:

> Новый функционал не должен заставлять AgentKernel понимать новую предметную область.

Основная проверка результата выполнения:

> Модель может ошибиться в тексте, но runtime не должен ошибиться в факте.
