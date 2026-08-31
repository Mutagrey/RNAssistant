# RNAssistant — мастер-план очистки, стабилизации и подготовки стабильного ядра

**Статус:** обязательный план исполнения  
**Исходная база:** `main`, продуктовая версия `16.0.4`  
**Целевой стабильный релиз:** `16.1.0`  
**Главная ветка стабилизации:** `stabilization/16.1`  
**Основной принцип:** модель предлагает действия; runtime единолично определяет, что реально произошло.

---

## 0. Как пользоваться этим документом

Этот документ предназначен для агента, который будет менять репозиторий. Его нельзя исполнять как одну большую задачу или один огромный patch.

Аудит 2026-08-28 уточняет целевые контракты §§5–10 и gates Phases 4–9. Это docs-only изменение поверх Phase 3B2, не начало этих фаз и не подтверждение runtime/Windows validation. Отдельное исправление R29 (§7.1) переключает protocol/runtime на v4; текущий LRU остаётся до Phase 8. Найденные противоречия и оставшиеся проверки записаны в [risk register](RISK_REGISTER.md#архитектурный-аудит-2026-08-28).

### Обязательные правила для агента

1. Выполнять только текущую фазу и текущий подэтап.
2. Не начинать следующую фазу в том же изменении. Переход к следующему dependency-safe host-neutral подэтапу при открытом Windows gate разрешён только режимом §16.1; он не закрывает отложенные gates и не разрешает угадывать Office/COM semantics.
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
13. Отчитываться кратко по §23: результат и ключевые файлы, релевантные проверки, реальные ограничения/риски; Windows/Office validation указывать для затронутого поведения, без обязательных пустых разделов.
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

Отвечает только за exact tool id lookup, argument schema validation, policy validation, confirmation gate до исполнения, вызов `IToolHandler`, преобразование infrastructure exception с учётом dispatch evidence и возврат `ToolExecutionRecord`.

Он не знает внутренности VBA, Excel, HTML или Plan.

Проверка всего response/batch принадлежит ModelProtocol и kernel до первого dispatch, на одном runtime-owned policy snapshot. `ToolRuntime` проверяет один call и не получает model envelope ради batch validation. Kernel единожды учитывает record и сохраняет его через `IRunStore`; ToolRuntime не ведёт второй run store/accumulator. Domain journal остаётся во владении domain service. Confirmation gate запрещает dispatch, kernel хранит pending/lifecycle, Application только принимает решение пользователя и возобновляет тот же call.

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

Каждую новую границу проверять уже при её switch: использовать существующие compile/contract checks, а при пробеле добавить минимальную targeted architecture check. Не вводить отдельный framework и не дублировать покрытие. Phase 10 сводит проверки в общую матрицу, а не впервые обнаруживает смешение контуров.

---

# 7. Минимальные протоколы

Общие протоколы намеренно остаются небольшими.

## 7.1. Conversation Response v4

Модель не владеет `status` или call ID. V4 — отдельное исправление R29 в контуре Phase 2 + consumers Phase 3; оно не начинает Phase 4. [Canonical contract](../protocols/CONVERSATION_RESPONSE_V4.md) задаёт wire, accepted history и qualification gates.

### Вызов tool

```json
{
  "message": "Прочитаю текущий модуль и внесу точечное изменение.",
  "tool_calls": [
    {
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
- каждый model call имеет только `name`, `arguments`; поле `id` запрещено;
- неизвестные root/call fields отклоняются;
- модель не возвращает `status`, `phase`, `completed`, `retry`, `verified`;
- runtime назначает уникальный `tool_call_id` до accepted persistence, confirmation и dispatch;
- write/external/confirmation-required/unclassified call должен быть единственным call в ответе;
- несколько независимых local read-only calls допускаются и выполняются последовательно.

### Обязательное исправление R29

ModelProtocol возвращает проверенный draft без ID; AgentKernel назначает ID каждому call до принятия всего batch. Вместе с `ToolCallId` в том же `session.commit` сохраняется неизменяемый `AcceptedCallOrigin { StepId, ModelAttemptId, CallIndex }`. Номер позиции без exact attempt недостаточен после repair. Raw response не переписывается; source attempt фиксируется до необязательной trace-проекции. Results, native tool-role history и continuation используют тот же ID; replay его восстанавливает, а не создаёт заново.

Wire/schema/prompts/history readers и consumers переключаются атомарно, без переименования дубликатов в v3. Ошибка выдачи/коллизия runtime ID — infrastructure fault до accepted append/dispatch, не причина model repair. Проверки: валидный длинный payload проходит без новой генерации из-за ID, разные calls получают разные IDs, confirmation/replay сохраняют их. Идентификатор связывает записи, но не является семантической дедупликацией действий и не разрешает auto retry. Детальные критерии — [R29](RISK_REGISTER.md#r29--runtime-должен-владеть-идентификаторами-вызовов).

### Совместимость

Unversioned/v2/v3 history несовместима: full-history preflight требует явный новый чат/reset до preparation или confirmation, включая записи вне compacted prompt. V4 call без runtime ID/origin или с неоднозначной связью также отклоняется. Stream не переписывается, не обрезается и не удаляется автоматически; pending action можно отменить. Новые accepted записи используют только v4; adapters, ID repair и dual-write отсутствуют. Saved prompts сохраняют текст; текущий schema marker 14 (Tool Result v1) требует явного review/reset старых instructions.

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
  "independent_local_read": false,
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

`read` сам по себе не означает batch safety. В batch допускаются только явно классифицированные independent local reads без confirmation; external/unclassified calls остаются singleton. Источник policy — локальная execution authority, не JSON/название tool или текст модели. `verification` задаёт требование к handler, но не является доказательством проверки конкретного вызова.

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

Упрощение результата не удаляет resource transport: optional `resources:[{uri,revision,relation?}]` сохраняет точные `ResourceRef`, включая `relation:"result"` для полного externalized `data`. CAS hash, internal artifact ID и путь не становятся альтернативными адресами для модели. Bounded materialization и media остаются вне kernel; source/reference сохраняется до следующего model dispatch.

Awaiting confirmation, запрос ответа пользователя и доказанный non-dispatch — typed runtime control/evidence, а не ещё один model-facing status. Adapter передаёт их kernel отдельно; сериализатор не восстанавливает их из `message`/`data.code`. Изменение v1 не должно превращать паузу в успешную запись или ошибку tool.

## 7.5. Tool Execution Record

Это runtime-only запись, не новый model protocol:

```text
ToolCall
ToolDescriptor identity
ToolPolicy snapshot
ToolResult
Dispatch evidence
Domain effect / verification evidence
StartedAt
CompletedAt
DocumentRuntimeId
Correlation ids
```

Она нужна, чтобы runtime формировал итог независимо от текста модели.

Policy описывает возможный эффект, record — факты конкретного execution. Сохранить различие «dispatch не было», «мог быть dispatch», «эффект проверен» и «проверенный no-op»; не выводить verification из `status=ok` или `policy.verification=tool`. Domain передаёт typed evidence, kernel только агрегирует; доменные hashes/журналы не становятся логикой kernel. Определённая ошибка может сопровождаться подтверждённым частичным эффектом; неизвестное конечное состояние write/external остаётся `unknown`. Потеря результата после возможного dispatch не превращается в обычный `error`.

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
8. `clean` означает отсутствие известных execution errors/unknowns, но не выполнение задачи или наличие изменений. UI не выводит «все изменения применены» только из health, `completed`, `WriteOk` или текста модели.
9. Counts отражают вызовы, не изменённые объекты. Verified writes требуют отдельного typed effect evidence; подтверждённый no-op не увеличивает число фактических записей. Read без надёжного результата — read error, не неопределённый write-effect.

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

Snapshot фиксирует содержимое descriptor/schema, policy и binding/package fingerprint, а не только список tool IDs. Подмена handler под тем же именем не меняет уже принятый call: runtime использует pinned definition либо явно отклоняет несовпадение до dispatch; запрет/отзыв execution permissions проверяется заново. Confirmation проверяет тот же fingerprint. Расширение snapshot допускается только на границе step, не посередине batch.

Полный core pack — конечный явно перечисленный набор, не весь dynamic catalog. Compact catalog остаётся discovery index, а `callable set` — только материализованные exact schemas текущего snapshot. Admission проверяет весь request budget, включая schema, history/media, output reserve и repair overhead, до изменения snapshot. Не поместившаяся optional extension отклоняется без partial publication и без удаления уже загруженных schemas. На compaction активные tool schemas заново материализуются из pinned snapshot в пределах budget; краткое summary не заменяет схему. Skill bodies сохраняют свой отдельный revision/read-evidence contract. 8B удаляет прежний bounded LRU только вместе с finite core и атомарным admission; durable extension event и rematerialization остаются отдельным 8C.

## 7.8. Resource v1

Model-facing и durable identity — существующий `ResourceRef`:

```json
{
  "uri": "rna://chat/s1/artifact/a1/revision/2",
  "revision": "2"
}
```

Descriptor/lineage и bounded `ResourceReadResult` остаются отдельными моделями. Immutable URI pin-ит revision; для live Office/VBA URI сохраняет identity, а revision/cursor фиксируют наблюдённое содержимое. Большое тело читается через `common.resources_read` по тому же `ResourceRef`, не через model-facing `content_ref`, CAS hash, internal artifact ID или local path. Content hash может быть metadata/evidence, но не вторым transport.

Resource не содержит execution authority или tool state. Не вводить новый read envelope при переносе Phase 8; сохранять [canonical Resource Fabric](../resource-fabric.md#domain-model), representation/chunk bounds и stale cursor rejection.

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

Один reentrant document gate охватывает live guard-read → validation → prepare → dispatch → read-back → terminal evidence; сериализации только самого write недостаточно. Live resource reads и ручные mutations используют тот же gate, чтобы не наблюдать промежуточное состояние. Проверка bound identity/lifetime выполняется внутри STA непосредственно перед доступом. `RuntimeDocumentId` обозначает живой документ, не имя файла или случайный COM proxy; Save As меняет durable key, не target/gate текущего execution.

Document gate не удерживается при ожидании модели или решения пользователя. После confirmation guard/fingerprint повторно проверяется под gate; UI не может подтвердить другой call/target. Порядок chat lease → document gate → короткие storage locks должен быть зафиксирован и проверен без обратного захвата/ожидания UI под lock. Это требования Phase 5, не новый общий transaction framework.

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
- запрещённое model-owned поле `id` в v4; runtime ID failures не относятся к protocol repair.

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

Валидный JSON доказывает только корректность envelope/arguments, а не работоспособность HTML/VBA или выполнение запроса пользователя. Ошибка локальной выдачи/восстановления ID не расходует model protocol attempts.

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

В нём определяются допустимые CRLF/LF, финальные пустые строки, VBE normalization и comparable hashing уже декодированного VBA source. JSON transport escaping декодируется один раз в protocol parser; canonicalizer не выполняет второй unescape строк/комментариев.

Разделять raw content hash и domain comparable hash. CAS сохраняет и хеширует точные исходные bytes, не нормализует VBA и не зависит от domain canonicalizer. Сравнение read-back использует один VBA comparable normalizer; metadata/evidence называет нужный вид hash явно. Эквивалентность для VBE не означает идентичность CAS payload.

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
        ADR-0002-model-protocol-boundary.md
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

Обычная сборка архива без `.git` допускается с предупреждением: отсутствующие Git-метаданные — `unknown`, без SHA — `+source-archive`, неизвестное состояние дерева добавляет `.unknown`. Это относится и к конфигурации Visual Studio Release; явный release milestone по-прежнему требует известного происхождения и Git checkout для проверки чистоты дерева.

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
ValidateReleaseEvidenceSigner
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
7. Создать release commit и остановиться без tag.
8. Собрать и проверить exact candidate, затем подписать detached evidence manifest
   сертификатом, SHA-256 которого pin-ится в candidate metadata.
9. В отдельной finalization проверить тот же tracked version/commit, signature,
   manifest и explicit Windows/pack evidence; только затем создать annotated tag.
10. Не перемещать существующие tags и не делать push без явного параметра.

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
- PR содержит массовые rename и behavior changes;
- появляется новый универсальный status;
- появляется fallback, не описанный в protocol/ADR.

При этих сигналах пересмотреть scope и разбить независимые изменения на:

```text
introduce
adapt
switch
delete
```

**Более 10 production files — сигнал оценки, а не автоматическое дробление.** Один согласованный switch в текущей фазе может превысить этот порог, если все затронутые consumers нужны одному контракту/инварианту и разделение оставит несовместимые промежуточные состояния либо лишний adapter. До правок кратко указать необходимые consumers, причину связанного изменения и проверки. Не добавлять попутные features, массовые moves/renames или работу следующих фаз; остальные сигналы остановки остаются в силе. Само число файлов не требует отдельного ADR.

Introduce/adapt/switch/delete — последовательность миграции, не требование четырёх отдельных commits. Проверенный switch/delete допустим в одном изменении. Не вводить новый подготовительный подэтап без конкретного препятствия ближайшему переключению.

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

1. **Проверить потребителей.** В затронутом контуре через targeted search установить, кто ещё использует заменённые contracts, helpers и adapters, включая tests и project includes; аудит всего репозитория после каждого шага не нужен. Удалять путь только после switch потребителей и релевантной проверки. Если обязательная Windows/Office проверка остаётся gate, явно сохранить её как блокер удаления.
2. **Удалить заменённое.** Удалить ставшие ненужными implementation branches, aliases, fallbacks, helpers и project includes. Удалять obsolete tests только вместе с заменённым контрактом; сохранять покрытие актуальных инвариантов. Не сохранять мёртвый код или совместимость со старыми чатами «на всякий случай».
3. **Ограничить временные adapters.** Для каждого оставшегося adapter указать owner, конкретных consumers, причину сохранения и ближайший removal substep/gate в `MIGRATION_MAP.md`. После исчезновения consumers удалить в том же подэтапе; Phase 10 не служит сроком по умолчанию. Существующий runtime consumer нельзя удалять лишь потому, что он legacy.
4. **Сократить актуальную документацию.** Обновлять canonical docs, только если их содержание затронуто: контракт, поведение, граница или инструкция изменились. Убрать отменённые инструкции и дубли, но не переписывать все связанные документы по шаблону. Исторические ADR/отчёты/verification evidence сохранять как историю, а не обязательное чтение следующего шага. Не удалять действующие требования или открытые риски ради числа строк.
5. **Оставить короткий контекст продолжения.** В начале `PROGRESS.md` поддерживать текущий подэтап, следующий шаг, его gates, оставшийся legacy и ссылки только на необходимые документы/разделы. Подробные результаты сохранять ниже или в существующем отчёте подэтапа; не копировать историю в каждый новый отчёт.
6. **Проверить и зафиксировать.** Проверки выбирать по §22.1; dangling references/includes проверять при затронутых symbols/files. В коротком отчёте указать существенное удаление или оставшийся блокер, а контекст следующего шага — в `PROGRESS.md`. Если чистить нечего, достаточно одной отметки в progress; отдельный cleanup-подэтап, повторный прогон или косметические изменения ради отчёта не нужны.

Работать только в текущем контуре и в change budget §14.3. Массовые moves/renames не смешивать с behavior changes; проблемы других контуров записывать в backlog. Отказ от исторической совместимости не разрешает автоматически удалять chats/events/CAS/VBA journals, settings, API key или custom tools; reset требует отдельного явного действия. Safety и recovery evidence не ослабляются.

## 15.2. Рефакторинг, который облегчает миграцию

Перед изменением контура оценить, мешает ли смешение обязанностей ближайшему шагу текущей фазы. Если да, выделить небольшой подготовительный подэтап внутри этой фазы; отдельная общая кампания рефакторинга до миграции не нужна. Если целевое извлечение уже решает проблему, выполнять его напрямую, без промежуточного сервиса, который сразу придётся заменять.

Только если рефакторинг нужен, до него кратко обосновать следующие пункты (достаточно нескольких предложений, отдельный документ не требуется):

1. Какое конкретное ближайшее изменение станет проще и какие callers сейчас вынуждают читать монолит.
2. Какая ответственность получит одного владельца и какие зависимости/общее mutable state перестанут пересекать эту границу.
3. Какая минимальная проверка покажет сохранение поведения и позволит проверять выделенный контракт отдельно; существующие tests предпочтительнее нового набора.
4. Какие consumers переключатся, какой старый путь будет удалён и какой ближайший removal gate останется при поэтапном switch.

Критерий пользы — следующее локальное изменение можно понять и проверить по контракту и его реализации без изучения несвязанных областей. Уменьшение числа строк, файлов или токенов само по себе не является результатом. Новый `partial`, передача всего controller/session без необходимости или набор callbacks обратно в монолит могут лишь разнести прежнюю связанность; без объяснения новой границы такое выделение не выполнять. `Partial` допустим как короткий механический шаг к конкретному извлечению, но не как его завершение.

Подготовительное выделение сохраняет поведение; изменение семантики выполняется явным следующим подэтапом с его проверками. Соблюдать change budget §14.3, C#/.csproj requirements и cleanup §15.1. Не переносить архитектуру следующих фаз заранее: например, при извлечении AgentKernel не менять Resource Fabric/ToolPack lifecycle, а при выделении текстового VBA engine не менять journal/CAS protocol. Существующие доменные services переиспользовать; не создавать универсальные обёртки и временные дубликаты.

Конкретные точки и фазы указаны в `MIGRATION_MAP.md` и ниже; перед своей фазой повторно проверить актуальных consumers. После закрытия подэтапа записать, какие обязанности больше не смешаны и какие файлы/контракты нужны следующему шагу. Контекст сужается внутри контура, но не обязан монотонно уменьшаться при переходе к новой области. Если полезного выделения нет, продолжать миграцию без обязательного распила.

---
# 16. Поэтапный план исполнения

## 16.1. Режим отложенной Windows qualification (согласован 2026-08-29)

Пока регулярный доступ к Windows x64 + Office x64 + VS 2022 отсутствует, обязательный
маршрут стабилизации не останавливается после каждого открытого Office/WebView gate.
Разрешено последовательно выполнять dependency-safe host-neutral подэтапы Phases 5–10,
сохраняя обычные границы: один подэтап/инвариант на commit, targeted проверки по §22.1,
локальная чистка и обновление `PROGRESS.md`. Следующая фаза не начинается в том же
изменении.

Для каждого такого подэтапа Definition of Done разделяется явно:

1. **Host-neutral implementation** — код, contracts, fake-host/fault/static UI tests и
   локальная чистка завершены; этот статус можно отметить `done host-neutral`.
2. **Deferred Windows gate** — конкретные реальные сценарии добавлены в
   [Windows qualification runbook](WINDOWS_QUALIFICATION_RUNBOOK.md) и очередь
   `PROGRESS.md`; отсутствие среды означает `not performed`, не pass.
3. **Qualified** — статус допустим только после прогона затронутых сценариев на
   зафиксированном Windows build и сохранения evidence.

Открытый Windows gate не блокирует следующий host-neutral slice. Если switch зависит
от неизвестного реального поведения COM/VSTO/WebView2, допущение и риск фиксируются
явно, готовится минимальный probe, а статус остаётся только `done host-neutral` до
qualification. Решением 2026-08-31 production 11T0/7D принимает текущий
`DocumentIdentity.RuntimeKey`, захваченный один раз на lifetime exact bound workbook;
WQ0 проверяет это допущение как diagnostic/regression evidence, но не блокирует
implementation. Он и полный Windows matrix обязательны до Phase 12/release.

После host-neutral реализации обязательных контуров создаётся воспроизводимый
`16.1.0-dev` qualification candidate. Он не называется stable, beta или RC. Единый
Milestone WQ-A после Phase 10 создаёт встроенный qualification runner; следующий
Milestone WQ проверяет накопленные Windows/Office/WebView gates и
маршрутизирует дефекты владельцам контуров по causal diagnostics. Исправление каждого
дефекта выполняется отдельным change с targeted regression и повтором только
затронутых Windows scenarios. Phase 12 начинается лишь после WQ.

Этот общий режим заменяет ограничения «следующий локальный scope согласовать отдельно»
в исторических исключениях 6A/R33/6B и раннем старте Phase 9 только для будущего
dependency-safe host-neutral продолжения. Их прежний scope/evidence не меняются,
отложенные gates не считаются закрытыми, а feature freeze сохраняется.

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
- [ ] Ввести parser/schema builder для Conversation Response (v3 в Phase 2C3C, v4 в R29 correction).
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
- [ ] Обновить canonical protocol doc; текущий — `docs/protocols/CONVERSATION_RESPONSE_V4.md`.
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

- [x] Создать `AgentKernel`.
- [x] По §15.2 отделить извлекаемый цикл `ConversationRunService` от подготовки prompts/compaction/media и материализации результатов; использовать существующие services, не менять ToolPack/Resource Fabric semantics Phase 8.
- [x] Оставить текущие working set/read-evidence/LRU операции за границей kernel; он не управляет resource capability lifecycle. Phase 8 заменяет внешнюю реализацию каталога, а не повторно извлекает её из цикла.
- [x] Обычный запуск и confirmation continuation в `AssistantController.Agent` подключить к общей kernel-логике учёта выполнения; сохранить confirmation/fingerprint gates и отдельную проверку controller wiring.
- [x] Создать `RunSummary`.
- [x] Создать `ExecutionHealth`.
- [x] Создать `ToolExecutionRecord`.
- [x] Подключить текущий executor через adapter.
- [x] Подключить минимальный `IRunStore` к существующим typed append-only events через adapter. Проверить сохранение/replay нового `RunSummary` (lifecycle, health, counts и pending confirmation) для нормального, error/unknown и confirmation сценариев; использовать/расширить существующее покрытие. Не вводить новый durable store, snapshot authority или полную переработку storage/UI Phase 9.
- [x] Перевести текущий цикл на accepted model response, tool execution, accepted tool result, next step и run summary.
- [x] Удалить direct mapping model `completed` → `RunStatus=completed`.
- [x] Не принимать model `blocked/refused` как runtime truth без локальной классификации; текст при этом сохраняется как narrative.
- [x] Confirmation оставить runtime-owned.
- [x] Добавить pure tests с fake model/fake tool:
  - [x] read ok;
  - [x] write ok;
  - [x] write error;
  - [x] write unknown;
  - [x] error then success;
  - [x] success then error;
  - [x] unknown then model says done;
  - [x] cancellation before tool;
  - [x] cancellation after possible dispatch;
  - [x] iteration limit;
  - [x] runtime allocator collision/invalid output before acceptance (R29); no model ID repair.
- [x] Обновить state model docs.
- [x] Добавить ADR-0001 и ADR-0008.

Evidence: [Phase 3B2 cutover](PHASE_3B2_KERNEL_CUTOVER.md). Host-neutral DoD закрыта; Windows/Office delivery и полная persistence/UI matrix остаются qualification gates. Phase 4 в этом изменении не начата.

### Definition of Done

`AgentKernel` тестируется без Excel, WebView2, HTTP и real LLM; его граница с Office/domain executors и resource lifecycle проверена. Model wording не влияет на execution health. Минимальный replay через существующий event store сохраняет новый authoritative итог; полная нормализация persistence/UI остаётся Phase 9.

---

## Phase 4 — Tool contracts и ToolRuntime

### Цель

Получить маленький масштабируемый runtime без переделки всех tools одновременно.

### Выполнить

4A закрыт host-neutral: [contracts/runtime/evidence](PHASE_4A_TOOL_RUNTIME.md).
4B закрыт host-neutral отдельным атомарным switch writer/readers/prompts/history gate:
[127 targeted checks / cleanup](PHASE_4B_TOOL_RESULT_V1.md). Windows/Office и domain qualification остаются открытыми; Phase 5 не начата.

- [x] Ввести:
  - [x] `ToolDescriptor`;
  - [x] `ToolPolicy`;
  - [x] `ToolBinding`;
  - [x] `ToolPackageMetadata`;
  - [x] `ToolResult v1` — internal contract 4A, единственный model wire 4B; old result history требует explicit reset/new chat;
  - [x] `IToolHandler`;
  - [x] `ToolRuntime`;
  - [x] `ToolHandlerRegistry`.
- [x] Добавить `LegacyToolDefinitionAdapter`.
- [x] Из `OfficeToolExecutor` извлекать общий validation/policy/confirmation/dispatch runtime, переиспользуя уже выделенные domain executors; не дробить каждый dispatch branch и не менять document binding до Phase 5. В 4A переключён `resources_list`; legacy domain preparation остаётся до switch соответствующих handlers.
- [x] Не удалять текущие tools сразу.
- [x] Перенести один read-only tool первым.
- [x] Проверить exact id lookup.
- [x] Проверить schema validation.
- [x] Проверить confirmation gate до execution.
- [x] Runtime enforce:
  - [x] whole-response guard в ModelProtocol/kernel проверяет общий policy snapshot до первого dispatch; ToolRuntime не получает model envelope;
  - [x] write/external/confirmation-required/unclassified call единственный в response;
  - [x] только independent local reads могут быть последовательным списком;
  - [x] никакого generic auto retry.
- [x] Убрать дублирующий `Success + Status` в новом contract.
- [x] Добавить model-facing serializer Tool Result v1, strict reader и local marker1 для accepted call/result; переключить все replay roles и prompts/probes атомарно.
- [x] Сохранить runtime-only pending/awaiting-user/non-dispatch signals и `ResourceRef` transport; 4B покрывает bounded materialization, three-role history/fork/clone и known outcome при projection failure. Serializer не читает narrative для восстановления execution state; actual controller/Windows qualification открыт.
- [x] Отделить policy verification от actual effect evidence; покрыть read ok/error, write no-op/verified/unknown, confirmation и exception до/после возможного dispatch fake handler tests. Kernel считает каждый record один раз; запись run events остаётся через один `IRunStore`.
- [x] Обновить protocol docs.
- [x] Добавить ADR-0003.

### Definition of Done

Новый read-only tool добавляется через descriptor + policy + handler + tests без изменения AgentKernel. Общие batch/confirmation/effect contracts проверены на fake handlers, включая отсутствие partial dispatch unsafe batch; отсутствие COM-теста не подменяется обещанием verification в policy. Domain qualification остаётся Phases 6/7.

---

## Phase 5 — Bound DocumentSession и HostRuntime

### Цель

Исключить неверный workbook/document target и гонки активного окна.

### Подэтапы

- 5A: выделить текущую document-access boundary из executor без смены binding/locking semantics; [ADR-0005](../decisions/ADR-0005-bound-document-session.md). Завершено host-neutral, 16 targeted checks.
- 5B1: нейтральный session port и общий operation gate; guard/preparation, manual/resource/editor access, STA handoff и cancellation. Production Excel binding этим подэтапом не вводится, R04 открыт.
- 5B2: ExcelDocumentSession/factories и единая runtime lifetime identity для desktop/VSTO/native; direct context/catalog reads и полный identity/lifetime/Windows switch. Локальный IUnknown pointer, path/HWND или per-adapter GUID не подменяют identity одного живого документа.

Production checklist 5B2 закрывается только внутри атомарного 11T0/7D: bound
session/factories, захваченный на lifetime текущий `RuntimeKey`, прямой Excel
read/write backend и удаление compatibility execution path переключаются одним
change без промежуточного production состояния. WQ0 остаётся последующим evidence,
а не prerequisite.

### Выполнить

- [x] 5B1: ввести `IOfficeDocumentSession` и нейтрального consumer в HostRuntime; production providers появятся только в 5B2.
- [x] 5B2: подготовить отдельный [identity probe](../../tests/RNAssistant.ExcelIdentityProbe/README.md) кандидата OXID/OID с retained marshal reference; production identity не переключать по результатам parser tests.
- [ ] 5B2/WQ0: квалифицировать принятое lifetime identity допущение и равенство desktop/VSTO/native на Windows; это обязательное release evidence, не blocker implementation.
- [x] Ввести `ExcelDocumentSession` (11T0/7D host-neutral; Windows lifetime evidence остаётся открытым).
- [x] 5A: выделить текущую document access/serialization из `OfficeToolExecutor` в `HostRuntime`; старые helpers удалить.
- [x] 5B2: выделить выбор/удержание workbook из `ExcelAdapter`; write/read-back получают тот же bound object. Charts/formatting и прочие host adapters не рефакторились попутно.
- [x] Bind конкретного document object до execution.
- [x] Сериализовать writes по `RuntimeDocumentId`.
- [x] 5B1: gate охватывает guard/preparation/live read, dispatch и read-back; resource/manual/editor paths используют тот же gate. Reentry только для той же operation/target, порядок document → shared; release при confirmation, повторная проверка после ожидания. Evidence — [PROGRESS](PROGRESS.md#phase-5b1--document-access-gate); Windows gate ниже остаётся открыт.
- [x] 5B2 read switch (host-neutral): selection/context capture и VBA catalog reads используют HostRuntime.ReadDocument; отдельный operation root не наследует доступ mutation. Gate охватывает prepare/capture и cache/list/components; [evidence](PROGRESS.md#phase-5b2--direct-contextcatalog-reads).
- [ ] 5B2: квалифицировать эти reads с production ExcelDocumentSession/factories и реальной UI/STA reentrancy на Windows; neutral switch не доказывает COM binding.
- [x] Удалить fallback на `ActiveWorkbook` из agent mutation path.
- [x] `ActiveWorkbook` оставить только для user action «выбрать текущую книгу».
- [x] Write и read-back выполнять через один bound object.
- [x] Проверять `IsAlive` до dispatch.
- [x] 5B1: определить neutral close/cancel semantics — closed/replaced session не допускает action; cancellation до dispatch не запускает action, после начала mutation не доказывает отсутствие effect. Реальный COM lifetime — 5B2.
- [x] 5B1: добавить fake host tests; они не подтверждают реальную Excel identity.
- [ ] Добавить Windows integration scenarios:
  - [ ] switch workbook before write;
  - [ ] switch workbook during operation;
  - [ ] close bound workbook;
  - [ ] Save As identity change;
  - [ ] two chats write same workbook;
  - [ ] two workbooks with same visible name;
  - [ ] queued write после изменения guard, live read во время mutation, разные COM proxies одного документа и отсутствие deadlock при confirmation/cancel.
- [x] Добавить ADR-0005; delivered 5A/5B1 отделены от production target 5B2.
- [x] 5B1: обновить concurrency docs и перечислить оставшиеся consumers/removal gates.

### Definition of Done

Переключение active workbook не может перенаправить уже начатый run.

---

## Phase 6 — VBA vertical slice

### Согласованное исключение 6A (2026-08-28)

Пользователь разрешил продолжать локальную работу, пока Windows/Office машина недоступна, с последующей совместной qualification. Текущий допуск ограничен **6A: чистые VbaPatchEngine и VbaTextCanonicalizer**, их текущие consumers, targeted tests и локальная чистка. Выделение сохраняет существующую text/hash semantics; разрешены только механические замены вызовов в guard/verification/package/storage consumers. Не менять COM binding/dispatch, journal/CAS protocol, outcome classification, UI или продуктовые возможности.

Phase 5B2/R04 остаются открытыми; production ExcelDocumentSession/factories ещё не реализованы. Наличие локальных tests не заменяет identity qualification и не разрешает factory switch. Следующие подэтапы Phase 6 и Phases 7–12 не включены в это исключение. После 6A остановиться на его границе; дальнейший локальный scope согласовывать отдельно, не объявляя Phase 5/6 завершёнными. Поздний Windows прогон должен включить накопленные gate/identity/controller сценарии 5B2 и VBE/read-back/package regression для 6A; identity проверяется до factory switch.

### Согласованное продолжение R33 (2026-08-29)

После предложения следующего локального шага R33 пользователь запросил «Коммит и далее». 6A зафиксирован отдельно (`e0360f3`); новый допуск ограничен подсчётом всех exact-match стартовых смещений, включая перекрытия, и отказом при неоднозначности до confirmation/write/backup/journal. Допустимы targeted pure-text и fake Office regression tests, canonical docs и локальная чистка.

Это отдельное изменение semantics, не часть сохраняющего поведение 6A. Newline/hash правила, существующий error/result mapping, COM dispatch, journal/CAS protocol и production binding не меняются. 5B2/R04, Windows/VBE qualification и полный Phase 6 gate остаются открытыми. Следующие domain extractions и Phases 7–12 этим допуском не разрешены.

### Согласованное продолжение 6B VbaReader (2026-08-29)

После отдельного R33 пользователь разрешил следующий локальный подэтап с проверкой регрессий, чисткой и границ ответственности. Допуск ограничен host-neutral `VbaReader`: один владелец построения VBA read-команд, нормализации имени и typed validation project/module snapshots; действующие mutation guard/verification/package consumers и document-tool catalog переключаются на него. Malformed successful backend payload должен завершать текущую загрузку fail closed, не публиковаться и не кэшироваться как валидный пустой/частичный catalog; настоящий `modules: []` остаётся допустимым.

`HostRuntime` сохраняет document gate и target binding; `VbaToolExecutor` сохраняет reconciliation, observations, guards, mutations, journal/read-back и mapping в текущий ToolResult/resource adapter. Host-specific COM остаётся в adapters/`VbaProjectSupport`. Не менять production factories/identity, COM implementation, journal/CAS/result wire, Phase 7 или UI. После 6B следующий mutation/verifier slice согласовать отдельно; 5B2/R04, Windows/VBE/package qualification и полный Phase 6 gate остаются открытыми.

### Согласованное продолжение 6C mutation service (2026-08-29)

После завершения 6B пользователь разрешил продолжить обязательный Phase 6. Этот
host-neutral slice ограничен полным workflow `common.vba_apply_patch`:
guard → prepared journal → dispatch → read-back → terminal assessment. Общая
module-mutation journal orchestration и read-back verification выносятся в
`Office.Vba.VbaMutationService` и `Office.Vba.VbaVerifier`; действующие
write/delete/restore consumers переключаются на эти общие владельцы механически,
без изменения их внешнего поведения.

`VbaToolExecutor` остаётся tool adapter и пока сохраняет argument/result mapping,
остальные mutation entrypoints, reconciliation loop и package/rename orchestration.
Текущие `ToolCommand`/`ToolResult` на границе сервиса и string-based rollback
classification являются явно временными до отдельного 6D. Не менять protocol/wire,
journal/CAS format, COM implementation, `HostRuntime`, factories, UI или Phase 7.
После 6C остановиться: typed domain outcome, fault/persistence matrix и перенос
оставшихся entrypoints требуют отдельного подэтапа. 5B2/R04, Windows/VBE/package
qualification и полный Phase 6 gate остаются открытыми.

### Согласованное продолжение 6D typed mutation outcome (2026-08-29)

После завершения 6C пользователь разрешил продолжить («Далее»). Этот host-neutral
slice заменяет временную service-границу на typed module-mutation requests,
action results и финальный `ok/error/unknown` outcome. `VbaMutationService` получает
только узкие document-context, read, backend и journal ports; единственный
domain→legacy `ToolResult` mapping остаётся в `VbaToolExecutor`/Tools adapter.
Действующие write/delete/restore callers переключаются на общий typed journal
pipeline механически, но ownership их полных workflows остаётся в executor до
следующих отдельных подэтапов.

String-based rollback classification удаляется и из остающегося package/rename
path: `rolled_back` допустим только по явному structured backend disposition при
совпадении live state с before. Текущий legacy backend adapter такой disposition
не изобретает. Internal journal status не входит в общий Tool Result; остаются
только correlation/effect evidence и явный `terminalRecorded=false`, когда terminal
append не подтверждён. Journal/CAS format, COM implementation, `HostRuntime`,
factories, protocol и UI не меняются. Реальная COM/VBE qualification остаётся
WQ-VBA; после 6D следующий отдельный slice — whole-module write ownership.

### Согласованное продолжение 6E whole-module write (2026-08-29)

После завершения 6D пользователь разрешил следующий отдельный подэтап («Далее»).
Scope ограничен write-веткой `common.vba_write_module` с режимами
`upsert/createOnly/updateOnly`: normalization/existence preparation, confirmation
guard recheck, create/replace dispatch, prepared/terminal journal и source/type
read-back переходят в typed `VbaMutationService`. `VbaToolExecutor` оставляет
только legacy argument/mode/result adapter и отдельно маршрутизирует неизменённый
`mode=rename`.

Delete, restore, rename/package, reconciliation outer loop, COM implementation,
`HostRuntime`, factories, protocol/wire и UI не меняются. Create reconciliation
обязана учитывать component type вместе с source hash: совпавший код чужого типа
не доказывает committed effect и даёт non-retryable `unknown`. После 6E следующий
отдельный slice — delete ownership; Windows COM/VBE qualification остаётся WQ-VBA.

### Согласованное продолжение 6F delete ownership (2026-08-29)

После завершения 6E пользователь разрешил следующий отдельный подэтап («Далее»).
Scope ограничен `common.vba_delete_module`: existing-target preparation,
observation/confirmation guard, component-type refusal, dry-run, prepared journal,
compare-and-swap delete backend, absence read-back и terminal outcome переходят в
typed `VbaMutationService`. `VbaToolExecutor` оставляет только legacy
argument/guard serialization/result adapter; прежний delete workflow и его
дублирующие guard helpers удаляются без alias/fallback.

Restore, rename/package, reconciliation outer loop, COM implementation,
`HostRuntime`, factories, protocol/wire и UI не меняются. Internal host command
остаётся за `VbaMutationBackendAdapter`, модель и domain service его не видят.
После 6F следующий отдельный slice — restore ownership; Windows COM/VBE
qualification остаётся WQ-VBA.

### Согласованное продолжение 6G restore ownership (2026-08-30)

После завершения 6F пользователь разрешил следующий отдельный подэтап («Далее»).
Scope ограничен `common.vba_restore_backup`: выбор backup до confirmation,
restore-specific guard точного backup id/module/type/live-source hash и текущего
module existence/source hash, dry-run, prepared journal, typed create-or-replace
backend action, source/type read-back и terminal outcome переходят в
`VbaMutationService`. Подмена backup или изменение target после preparation
блокируются до journal/dispatch (R40). `VbaToolExecutor` оставляет только legacy
argument/guard serialization/result adapter; старый restore workflow и его
общие только с restore helpers удаляются без alias/fallback.

Rename/package, reconciliation outer loop, COM implementation, `HostRuntime`,
factories, protocol/wire и UI не меняются. Journal/CAS остаются единственным
append-only authority: новый store, snapshot или dual-write не вводится. После 6G
следующий отдельный шаг начинается с проверки consumers и решения scope для
package lifecycle/rename; Windows COM/VBE qualification остаётся WQ-VBA.

### Цель

Стабилизировать наиболее опасный write contour до переноса остальных mutations.

### Порядок

1. `vba.read` — 6B host-neutral extraction done; Windows/VBE qualification remains open.
2. `vba.apply_patch` — 6C workflow/verifier and 6D typed outcome/fault matrix done host-neutral; Windows/VBE qualification remains open.
3. whole-module write — 6E done host-neutral; Windows/VBE qualification open.
4. delete — 6F done host-neutral; Windows/VBE qualification open.
5. restore — 6G done host-neutral; Windows/VBE qualification open.
6. 6H consumer/scope audit — done docs-only; package lifecycle and rename are both admitted to stable core.
7. 6I typed package lifecycle, including temporary run/cleanup recovery and existing persistent install/remove.
8. 6J remaining typed rename ownership and removal of the executor-owned compound journal helpers.

6H проверил consumers и зафиксировал scope: действующие global/document-local VBA
tools исполняются через временный install/run/cleanup; Tools UI использует persistent
install/remove/status; `mode=rename` остаётся public stable-core mutation. Поэтому весь
текущий package lifecycle переносится в Phase 6 одним domain contour, а не делится
между stable core и legacy UI. Это не включает dynamic tool definition authoring,
новые package features или pipelines — они остаются Phase 11. Общий journal/CAS
authority и существующий `package.mutation.*` wire сохраняются; rename получает
rename-specific domain API без второго store или generic transaction framework.

6H также выявил R41: session install и cleanup сейчас являются отдельными mutations.
После применённого install при потерянном terminal/cleanup временные components могут
остаться, а marker-insensitive probe — принять их за обычный installed package и
пропустить последующую очистку. 6I обязан связать временный lifecycle, различать
ownership marker и блокировать run при незавершённой/unknown cleanup. Recovery только
наблюдает и фиксирует state; automatic replay/remove/overwrite запрещены. Подробный
consumer map и порядок — в [6H evidence](PHASE_6H_VBA_PACKAGE_SCOPE.md).

### Выполнить

- [x] 6A: `Core.Tools.VbaPatchEngine` выделен из `VbaToolExecutor.Patching`; типизированный text result без ToolResult/resources/COM/journal. Office сохраняет JSON/result mapping и ordered orchestration.
- [x] 6A: `Core.Tools.VbaTextCanonicalizer` — один владелец live/package/VBE-comparable text/hash правил; parser, patch, guard/verification/package/storage consumers переключены, прежние normalization methods удалены. Core-размещение сохраняет допустимые зависимости storage/parser; journal/CAS protocol не менялся.
- [x] 6A: существующие Transport/live/package/VBE-comparable representations разделены и описаны в [VBA journal](../vba-mutation-journal.md#text-representations); comparison не переписывает source.
- [x] 6A host-neutral: raw CAS hash не менялся и отделён от text/comparable hashes; targeted tests покрывают CRLF/LF/CR, literal backslash sequences, строки/апострофные комментарии. Это не Windows/VBE qualification.
- [x] 6B host-neutral: извлечён `Office.Vba.VbaReader`; backend read construction/name normalization/typed project+module validation имеют одного владельца. Mutation/resource executor и document-tool catalog переключены; дублирующие executor/catalog raw parsers/read builders удалены. Malformed success fail closed и не кэшируется, valid empty project сохраняет cache semantics. HostRuntime/COM/journal/result wire не менялись; Windows/VBE gate открыт.
- [x] 6C host-neutral: `Office.Vba.VbaMutationService` владеет полным `apply_patch` workflow и общей module prepare/dispatch/terminal orchestration; прежний executor patch path удалён.
- [x] 6C host-neutral: `Office.Vba.VbaVerifier` владеет module write/delete read-back и assessment; write/delete/restore и reconciliation используют одного verifier без изменения package semantics.
- [x] 6C: current journal/CAS bytes, event schema, hashes, correlation и public result shape сохранены; service ownership не создаёт второй store/dual-write.
- [x] 6D host-neutral: временная service-граница `ToolCommand`/`ToolResult` заменена typed domain request/read/action/outcome; mutation service/verifier больше не потребляют legacy `ToolResult`, mapping остался в Tools adapters/executor.
- [x] String-based rollback classification удалена из module и остающегося package/rename path; `rolled_back` требует явный structured disposition и verified before state.
- [x] Domain result детерминированно маппится в `ok/error/unknown`; verified intended state побеждает backend error, verified before state даёт definite error/not-applied.
- [x] Internal journal states не входят в общий ToolResult data/status/message; mutation/backup correlation и effect evidence сохранены.
- [x] Compile validation не смешана с source read-back: текущий contour её не выполняет и не утверждает; будущая compile evidence должна оставаться отдельной.
- [x] Unknown mutation не retry; terminal append failure не создаёт выдуманный terminal и не повторяет dispatch.
- [x] 6E host-neutral: полный whole-module write workflow (`upsert/createOnly/updateOnly`) перенесён из executor в typed `VbaMutationService`; старые write guard/workflow helpers удалены, `mode=rename` не смешивался.
- [x] 6E: create/replace выбирает domain service через typed backend actions; guard, prepared journal, source/type read-back и `Ok/Error/Unknown` остаются одним workflow без второго execution/store path.
- [x] 6E: reconciliation проверяет component type вместе с source hash; same-source/different-type create race даёт non-retryable `unknown`, а existence rejection не создаёт preparation и не dispatches.
- [x] 6F host-neutral: полный delete workflow перенесён в typed `VbaMutationService`; executor-owned delete guard/journal/backend/read-back path удалён без второго execution/store path.
- [x] 6F: только `StdModule`/`ClassModule` допускаются до preparation/dispatch; backend получает live-source compare-and-swap hash, а `ok` требует verified absence и durable terminal.
- [x] 6G host-neutral: полный restore workflow перенесён в typed `VbaMutationService`; executor-owned backup lookup/guard/journal/backend/read-back path и restore-only helpers удалены без второго execution/store path.
- [x] 6G: confirmation guard связывает exact backup id/module/type/canonical live-source hash и current target existence/source hash; raw CAS hash остаётся storage evidence. Подмена backup, stale target и incompatible component type блокируются до journal/dispatch, а `ok` требует source/type read-back и durable terminal.
- [x] 6H docs-only: проверены runtime/UI/catalog/recovery consumers; весь существующий package lifecycle и rename оставлены в stable-core Phase 6, dynamic definition authoring не включён. R41 и ordered 6I→6J gates зафиксированы без runtime switch.
- [x] 6I host-neutral: один typed package owner для validate/probe, session install/run/cleanup, persistent install/remove/status, journal/read-back/reconciliation и `ok/error/unknown`; R41 закрыт через marker+journal-aware durable lifecycle без automatic recovery mutation. Exact prepared existence/type/source/marker CAS доходит до shared backend и проверяется до первой install mutation. Windows/VBE qualification остаётся открытой.
- [x] 6J host-neutral: typed rename guard/preparation/backend/verification/recovery перенесены в `VbaMutationService`; guard и backend CAS связывают оба имени, source hash/type и code-only UserForm policy. Последний executor-owned compound journal path удалён; существующий `package.mutation.*` wire/CAS остаётся единственным durable authority. Windows/VBE qualification открыта.
- [x] R33 host-neutral: exact patch требует единственного стартового смещения, включая перекрытия; отказ до confirmation/write/нового backup/journal проверен отдельно от 6A extraction. Windows/VBE и полный VBA gate остаются открытыми.
- [x] Добавить host-neutral fault injection/reused regression matrix для typed module pipeline 6D–6G:
  - [x] before journal prepare;
  - [x] after prepare/before COM;
  - [x] backend/COM boundary throws before mutation;
  - [x] backend/COM boundary mutates then throws;
  - [x] read-back unavailable;
  - [x] read-back mismatch;
  - [x] terminal journal write fails;
  - [x] cancellation before dispatch;
  - [x] cancellation after dispatch;
  - [x] restart after prepared;
  - [x] VBE newline normalization (fake normalization only);
  - [x] duplicate target;
  - [x] target not found.
- [x] Повторить соответствующую typed fault matrix для package/rename в 6I/6J: package часть закрыта в 6I; rename часть закрыта host-neutral в 6J, включая prepare/backend/read-back/terminal faults, cancellation до/после dispatch, post-prepare collision и recovery complete-before/complete-intended/mixed без replay. Windows/VBE qualification открыта.
- [x] Real Excel/VBE сценарии зафиксированы в [Windows qualification runbook](WINDOWS_QUALIFICATION_RUNBOOK.md#3-финальный-прогон-candidate); исполнение WQ-VBA остаётся открытым.

### Definition of Done

При нормальном durable завершении VBA write возвращает один из трёх model-facing результатов, причём `ok` для built-in verified tool означает успешный read-back. Crash/ошибка terminal persistence не обязаны иметь записанный результат: сохраняются имеющиеся durable start/preparation, дальнейший dispatch останавливается, после reload выполняется только reconciliation. Нельзя выдавать выдуманный durable terminal или повторять mutation ради записи результата.

---

## Phase 7 — Excel read/write vertical slice

### Цель

Перенести базовый Excel contour на те же границы.

### Выполнить

- [x] 7A docs-only: сверить owners/consumers и зафиксировать ordered read → write → bound-production switch без скрытого fallback. [Evidence](PHASE_7A_EXCEL_SCOPE.md).
- [x] 7B: перенести `excel.inspect` и `excel.read_range` в один typed read owner и native `ToolRuntime` handlers. Все текущие `inspect` selectors переключаются атомарно; chart/table metadata не разрешает перенос mutation tools. [Evidence](PHASE_7B_EXCEL_READ.md).
- [x] 7B: HTML bind/refresh использует тот же read adapter под уже взятым document access; прямой `_adapter.ExecuteTool` для switched public IDs удалить.
- [x] 7B: сохранить 100000-cell limit до `Value2`/`Formula` materialization, добавить bounded inspect collections и запретить unbounded named-range `Value2`.
- [x] 7C: перенести только `excel.write_range` в typed write owner/native handler; прочие Excel mutations остаются legacy. [Evidence](PHASE_7C_EXCEL_WRITE.md).
- [x] 7C: добавить exact before/read-back verification и различать `VerifiedNoChange`, `VerifiedChange`, definite pre-dispatch `error` и non-retryable post-dispatch `unknown`.
- [x] 7C: сохранить deterministic null-padding ragged tables и применить size limits до COM matrix allocation/assignment.
- [x] 11T0/7D: одним production change ввести 5B2 bound session/factories, захватить текущий `RuntimeKey` exact workbook на bound lifetime, передать extracted interop backend только `ExcelDocumentSession.BoundDocumentObject` и удалить internal compatibility backend плюс `ActiveWorkbook`/descriptor execution fallback; WQ0 оставить открытым deferred evidence. [Evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md).
- [ ] Добавить host-neutral tests:
  - [x] все `inspect` selectors и общий Agent/manual/HTML read owner;
  - [x] values;
  - [x] formulas;
  - [x] empty range;
  - [x] oversized range до materialization;
  - [ ] protected sheet;
  - [x] closed workbook (host-neutral bound-session refusal; real Excel remains WQ);
  - [x] switched active workbook (host-neutral exact-target refusal; real Excel remains WQ);
  - [x] write error before dispatch;
  - [x] verified no-op/change;
  - [x] unverified final state.
- [x] Не переносить `find_cells`, `create_chat_chart`, `replace_cells`, table/chart mutations, formatting, sheet management или clear/sort/filter в 11T0; эти families остаются отдельными этапами 11T1–11T5.

### Definition of Done

Excel read/write добавлены через ToolRuntime и DocumentSession, AgentKernel не изменён.

---

## Phase 8 — Resource Fabric и ToolPack

### Цель

Оставить сильные resource invariants, но убрать Resource Fabric из execution control plane.

### Выполнить

- [x] 8D: зафиксировать `Resource = data`. [Evidence](PHASE_8D_RESOURCE_DATA_PLANE.md), [ADR-0004](../decisions/ADR-0004-resource-data-plane.md).
- [x] 8D: сохранить `rna://`, revisions, CAS, cursors.
- [x] 8D: заменить оставленную вне AgentKernel реализацию resource capability lifecycle; сохранить проверенную в Phase 3 границу, не менять kernel loop ради ToolPack.
- [x] 8A: ввести immutable `ToolPackSnapshot` за границей AgentKernel. [Evidence](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [ADR-0006](../decisions/ADR-0006-tool-pack-snapshot.md).
- [x] 8A: pin descriptor/schema + policy + binding/entry point/scope/host/package fingerprint (§7.7); одинаковый ID не разрешает замену implementation в принятом call/confirmation, legacy adapter rechecks до dispatch.
- [x] 8B: Core Excel/VBA pack передавать полностью.
- [x] 8B: отключить LRU eviction в stabilized runtime.
- [x] Optional schema loading делать monotonic:
  - [x] 8B: explicit request;
  - [x] 8B: new snapshot revision;
  - [x] 8C: durable event before publication;
  - [x] 8B: no eviction.
- [x] 8B: Global dynamic registry сохранить.
- [x] 8B: новые dynamic tools активировать в следующем run либо exact catalog member через явный snapshot extension.
- [x] 8B: если core/extension pack не помещается, fail visibly.
- [x] 8B: проверять полный request admission до snapshot publication; overflow отклоняет весь extension без удаления уже admitted schemas.
- [x] 8C: durable extension event повторно материализует pinned schemas при confirmation continuation/compaction/crash replay; raw read evidence не считается admission decision. [Evidence](PHASE_8C_TOOL_PACK_EVENTS.md).
- [x] 8D: Resource tools оставить read-only.
- [x] 8D: сохранить `ResourceRef` и существующие bounded read results (§7.8); не вводить CAS/content_ref transport или новый reader.
- [x] 8D: Capability discovery и tool authoring разделить.
- [x] 8D: добавить ADR-0004. [Decision](../decisions/ADR-0004-resource-data-plane.md).
- [x] 8A: добавить ADR-0006. [Decision](../decisions/ADR-0006-tool-pack-snapshot.md).

### Definition of Done

Resource provider можно добавить без изменения AgentKernel и tool execution semantics.

---

## Phase 9 — Persistence и UI projection

**Согласованное раннее начало 2026-08-29:** при недоступной Windows пользователь
разрешил начать host-neutral R32/9A до закрытия Phase 5B2/6 и Phases 7–8. Эти фазы
остаются открытыми; 9A не зависит от будущего Phase 8 ToolPack, не меняет execution
policy и не закрывает Windows gates. 9B/9C начинаются только отдельными commits после
acceptance предыдущего подэтапа.

### Цель

Сделать stored/replayed truth равной runtime truth и убрать inference из UI.

### Выполнить

- [x] Ввести или нормализовать:
  - [x] `IRunStore` (9D1 подтвердил минимальный port/adapter Phase 3, ordered append/cursor и replay coverage; контракт сохраняется без второго run store);
  - [x] `IConversationStore` (9D4: один минимальный port/adapter над прежним `ChatStore`);
  - [x] `IEventStore` (9D3: один closed typed port/adapter над существующим `ChatStore`).
- [x] Разделить:
  - [x] Agent Events;
  - [x] Domain Diagnostic Events.
- [x] Разделение является typed classification в существующем chat stream, не вторым durable run store; domain journals сохраняют свою recovery authority. Ports не получают независимые writable snapshots и не выполняют двойную запись одного outcome.
- [x] Accepted model/tool events остаются canonical: accepted response/calls/results — storage-internal `session.commit`, accepted ToolPack extension — mandatory Agent authority; best-effort accepted trace marker authority не получает.
- [x] Rejected model attempts остаются mandatory Agent diagnostics и не входят в replay/history.
- [x] Расширить минимальное replay coverage Phase 3 до полной host-neutral persistence/UI матрицы; replay восстанавливает тот же `RunSummary` и `RunViewState`. Реальный Windows/WebView gate остаётся в Milestone WQ.
- [x] UI получает typed `RunViewState`.
- [x] Отдельно отображать:
  - [x] model message;
  - [x] lifecycle;
  - [x] execution health;
  - [x] verified writes;
  - [x] failed calls;
  - [x] unknown effects;
  - [x] pending confirmation.
- [x] Удалить UI logic, основанную на model status/message.
- [x] Проверить stale projection и multi-window updates host-neutral: per-chat UI revisions не допускают поздний detail/catalog overwrite, existing stream revision CAS блокирует stale writers; Windows multi-window acceptance остаётся открытым.
- [x] Не переписывать CAS/event framework целиком.
- [x] Не вводить второй durable source of truth.
- [x] Сохранить ordered durability: referenced CAS payload durable до ссылающегося event; accepted call/start до effect, result evidence до следующего model step. Mandatory append failure до dispatch запрещает effect; после возможного dispatch — остановка и reload/reconciliation, без fabricated terminal и auto retry.
- [x] Проверить result-append failure после write, restart при незавершённом tool start, CAS failure и конфликт revision при queued stream chunks. Optional trace не заменяет mandatory run/tool events; replay не выполняет tools и не пересчитывает прошлое по новой policy.
- [x] 9D1 docs-only: сверены store/event writers, replay/recovery, projection consumers и существующее fault coverage. Один chat stream/CAS и `IRunStore` сохраняются; подтверждён пробел same-process reconciliation R45 и отсутствие typed event/conversation/UI ports. [Evidence](PHASE_9D1_PERSISTENCE_AUDIT.md).
- [x] 9D2 host-neutral: Agent start/confirmation после `RunStoreException` освобождают run ownership, отбрасывают изменённую in-memory projection и через один `ChatSessionService` reload/reconcile exact stream. Pre-dispatch confirmation сохраняет durable pending; open dispatch становится unknown один раз; fabricated terminal, append retry и tool replay отсутствуют. [Evidence](PHASE_9D2_RUNSTORE_RECOVERY.md).
- [x] 9D3 host-neutral: closed descriptors классифицируют все current top-level chat events по lane/authority/durability/write scope; один `IEventStore` adapter сохраняет прежний stream/CAS/wire. Active Office writers/readers switched atomically, storage lifecycle остаётся internal, arbitrary string append удалён. [Evidence](PHASE_9D3_TYPED_EVENT_STORE.md).
- [x] 9D4 host-neutral: минимальный `IConversationStore` и один adapter над прежним `ChatStore` атомарно переключили session/controller/kernel projection consumers. Artifact/CAS/event internals остались у существующих owners; broad conversation API internalized без writable snapshot/dual-write. [Evidence](PHASE_9D4_CONVERSATION_STORE.md).
- [x] 9D5 host-neutral: один immutable `RunViewState` из `KernelState` и source-owned effect evidence переключил application result, bridge, chat catalog и UI; session revision закрывает late projection ordering, flat `RunExecutionSummary` и model-status UI branches удалены. Replay/confirmation/recovery/stale checks pass; Windows WebView/multi-window acceptance открыта. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md).
- [x] R32: реализовать [сквозной журнал и общий JSON viewer](R32_DIAGNOSTICS_JSON_VIEWER.md) отдельными подэтапами: 9A — truth/query, 9B — viewer и read-only consumers, 9C — journal UI/qualification. Phases 4–8 этим требованием не расширяются.
  - [x] 9A host-neutral: chronological `run-causal` projection сохраняет exact source/origin/call/mutation evidence и явные terminal gaps; accepted-call writer классифицируется по `AcceptedCallOrigin`, без второго store или history rewrite. 9B/9C и Windows qualification остаются открытыми.
  - [x] 9B1 host-neutral: allowlisted UI-only `ViewerRegistry` + собственный bounded/lossless JSON token adapter с lazy DOM и exact raw/node copy; vendors и existing consumer paths не переключались. 9B2/9B3/9C и Windows qualification остаются открытыми.
  - [x] 9B2A host-neutral: diagnostics event/row data, separate source evidence и JSON CAS payload switched на общий adapter; non-JSON payload остаётся inert text, старый diagnostics `prettyJson`/plain-pre path удалён. Остальные consumers, 9B3/9C и Windows qualification открыты.
  - [x] 9B2B1 host-neutral: Agent arguments/results switched на общий lazy viewer; generic object/table/pretty renderer и dead CSS удалены, chart parser локализован у domain owner. Остальные consumers, 9B3/9C и Windows qualification открыты.
  - [x] 9B2B2 host-neutral: Context/materialized request, manual Tool results и VBA metadata switched на общий viewer; raw preview completeness/lazy lifecycle сохранены, editable/transport paths не менялись. Artifact/Markdown consumers, 9B3/9C и Windows qualification открыты.
  - [x] 9B2B3 host-neutral: artifact exact inline/metadata JSON switched на общий viewer; bounded bridge truncation проецируется как preview, non-JSON остаётся inert text, HTML/editor/transport не менялись. Markdown consumer, 9B3/9C и Windows qualification открыты.
  - [x] 9B2B4 host-neutral: completed top-level fenced `json` blocks в message/Agent diagnostics switched post-sanitize на общий lazy viewer с exact source matching/copy; live/unclosed/mismatched/non-JSON blocks остаются code. 9B3/9B4/9C и Windows qualification открыты.
- [x] R36 host-neutral до первого vendor switch: exact manifest/version/hash/license/transitive-runtime inventory для 36 existing assets, KaTeX WOFF2-only и Feather source provenance; main UI fail-closed для network/worker/WASM. Локальный worker разрешён только через manifested host factory/lifecycle/CSP. [Evidence](R36_WEB_VENDOR_GATE.md). Каждый новый vendor отдельно расширяет manifest; [evaluation](R32_VENDOR_UI_EVALUATION.md) не разрешает подключить весь shortlist. Windows WebView2 qualification открыта.
- [x] 9B3 host-neutral: Web Awesome official ESM graph отклонён для текущего `file://` host без custom bundling/host switch; pinned Wunderbaum 0.14.1 UMD/CSS подключён через bounded local-array `TreeAdapter` к одному HTML workspace/artifact navigation consumer. Exact manifest/license расширен до 38 runtime files, старый renderer consumer удалён; optional URL/lazy/edit/DnD/grid/persistence не опубликованы. [Evidence](R38_TREE_VENDOR_SWITCH.md). 9B4/9C, другие tree consumers и Windows WebView2 qualification открыты.
- [x] 9B4 host-neutral gate: Diff2Html не admitted, потому что два действующих VBA consumers и typed bridge DTO имеют только exact before/after source, а не source-owned bounded unified diff. Добавлять второй diff algorithm или выдавать UI-generated projection за evidence запрещено; existing bounded formatter сохранён, manifest не менялся. [Evidence](R39_DIFF_VENDOR_GATE.md). Повторная оценка только после отдельного authoritative unified-diff contract; 9C не блокируется.
- [x] 9C UI done host-neutral: Diagnostics по умолчанию открывает bounded chronological `run-causal` journal выбранного/latest run; Agent run и failed activity имеют direct navigation, строки раскрывают shared JSON viewer и exact projection/source links. Filters/counts строятся только из typed rows, known unknown/interruption states не теряются, scroll/expanded ownership сохраняется; raw/specialized views остаются drill-down. Нет нового store/vendor/bridge query. [Evidence](PHASE_9C_RUN_JOURNAL_UI.md). Windows WebView2 и полный R32 scenario gate открыты.
- [x] `ViewerRegistry` остаётся UI-only dispatch над allowlisted kind/MIME и уже разрешённым bounded payload. Он не читает bridge/CAS/network и не вводит model-facing `{kind,title,content}` transport: модель сохраняет Tool Result v1 + revision-pinned `ResourceRef`, а viewer выбирает typed UI projection.
- [x] Host-neutral direct navigation открывает один хронологический журнал запуска с раскрываемыми строками request → model attempts/repair → accepted call → confirmation/dispatch → result/effect. Source IDs/origin и proposal/execution/effect/gap различия сохраняются без второго durable журнала; actual Windows/reload/confirmation acceptance остаётся открытой.
- [x] Все read-only JSON surfaces используют один bounded viewer: дерево/подсветка, raw/pretty, node/path/value copy, явные incomplete/redacted/error states. Raw/token fidelity, безопасный text rendering и async stale guards обязательны; редакторы/transport serializers не подменяются viewer. Заменённые render/copy paths удалены в 9B2.

### Definition of Done

После restart/replay UI показывает тот же authoritative outcome, что был рассчитан при выполнении.
Acceptance R32 включает понятную навигацию по одному запуску, lossless JSON/HTML copy,
bounded rendering и реальные WebView/clipboard проверки на Windows. R28/live streaming
проверяется отдельно; новый viewer или синтетический trace не закрывает его автоматически.

---

## Phase 10 — Physical cleanup и architecture tests

### Цель

После стабилизации поведения привести структуру файлов и документов в соответствие с реальными boundaries.

Это финальная структурная сверка, а не начало чистки. Заменённые пути, мёртвые зависимости и устаревшие инструкции удаляются в своих подэтапах по §15.1.

### Выполнить

- [x] Переместить файлы через `git mv`.
- [x] Не смешивать moves с behavior changes.
- [x] Обновить namespaces.
- [x] Обновить old-style `.csproj`.
- [x] Проверить отсутствие забытых legacy branches; удалить оставшиеся после переключения последних consumers, не повторять уже выполненную локальную чистку.
- [x] Проверить отсутствие superseded canonical docs; исторические evidence/ADR не считать действующими инструкциями.
- [x] Свести и дополнить architecture checks, введённые при switch контуров, без дублирования существующего покрытия:
  - [x] Core.Agent не зависит от Office;
  - [x] ModelProtocol не зависит от Tools execution;
  - [x] VBA не зависит от UI;
  - [x] Resources не зависят от AgentKernel;
  - [x] OfficeHosts не зависят от WebView;
  - [x] UI не зависит от domain executors.
- [x] Обновить `ARCHITECTURE.md`.
- [x] Обновить `AGENTS.md` под фактическую архитектуру.
- [x] Закрыть миграции обязательного core scope в `MIGRATION_MAP.md`; оставшихся optional consumers явно закрепить за Phase 11 с removal gates. Это не разрешает включать неквалифицированные контуры в release Phase 12.
- [x] 10A host-neutral audit: inventory production files/namespaces/project includes и live consumers; добавить шесть forbidden-dependency checks, исправить superseded canonical path и разбить physical cleanup на exact atomic groups. Folder/namespace mismatch сам по себе не является основанием для rename. [Evidence](PHASE_10A_BOUNDARY_AUDIT.md).
- [x] 10B1: `git mv` host identity helper из Office Runtime в OfficeHosts с namespace/project/harness updates; algorithms/identity semantics не менять. [Evidence](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md).
- [x] 10B2: отдельно `git mv` `VbaProjectSupport*.cs` в OfficeHosts/Vba, обновить namespace/project/harness/host consumers; domain services, guards, journal и backend logic не менять. Скрытая assembly-access dependency оформлена как explicit read-only Office.Vba contract без friend assembly/duplicate parser. [Evidence](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md).
- [x] По явному решению пользователя отдельно добавить Windows local-build entrypoint для WQ preparation: default `Release` x64+x86 Native portable через declarative MSBuild, без PowerShell policy override, install/sign/register/network/process-kill; старый PowerShell native publisher удалить. Это tooling-only изменение, не candidate qualification и не product feature.
- [x] 10C: вынести application façade и удалить live resource-only projection двумя отдельными commits.
  - [x] 10C1: `git mv` `AssistantRuntime.cs` из document/tool Runtime folder в root Office façade; namespace/lifecycle/consumers не менять. [Evidence](PHASE_10C1_ASSISTANT_RUNTIME_MOVE.md).
  - [x] 10C2: перенести четыре resource read projections из `LegacyToolDefinitionAdapter.ProjectRead` в действующий `ControllerToolDefinition`, сохранить exact descriptor/policy/schema и удалить только заменённый method; execution/ToolPack/model wire не менять. [Evidence](PHASE_10C2_RESOURCE_PROJECTION_CLEANUP.md).
- [x] 10D: финально сверить canonical docs/AGENTS, migration statuses, production project includes и architecture suite; не закрывать Windows gates локальными checks. [Evidence](PHASE_10D_FINAL_ARCHITECTURE_AUDIT.md).

### Definition of Done

Файловая структура отражает контуры, а architecture tests предотвращают повторное смешение.

---

## Milestone WQ-A — In-app Qualification Center

### Цель

По прямому запросу пользователя заменить ручное выполнение qualification scripts
встроенным расширяемым wizard-ом с host-specific packs, production-path agent tasks,
deterministic assertions и causal evidence. Канонический контракт:
[Qualification Center](../qualification.md), решение об authority —
[ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md).

WQ-A — release-critical test tooling перед реальным WQ, а не новая Office feature.
Он не выбирает COM identity, не закрывает Windows gates и не меняет production tool
policy/effect semantics. Текущий PowerShell WQ0 остаётся engineering fallback до
готовности встроенного pack; duplicate identity decoder удаляется при switch owner.

### Подэтапы

1. **WQ-A0 contract:** strict pack/evidence/safety model, coverage registry и UI flow.
2. **WQ-A1 core:** host-neutral manifest parser/catalog, finite runner state machine,
   typed bridge DTO, fake probes/verifiers и closed qualification event operations.
   - [x] Strict data-only schema/coverage, allowlisted finite runner, mandatory
     start/completion barriers, safe replay/no-retry, CAS evidence и typed DTO
     реализованы host-neutral без controller/UI/Office switch.
     [Evidence](WQ_A1_QUALIFICATION_CORE.md).
3. **WQ-A2 UI:** отдельная empty-chat card и Diagnostics entry, pack list/stepper,
   resume, run-journal/JSON navigation и bounded report; prompt suggestions не являются packs.
   - [x] Typed application/controller/bridge composition, dedicated qualification
     chat, durable resume, UI shell и exact allowlisted read-only `common.ui-shell`
     реализованы host-neutral; Office/model/full-suite claims не делаются.
     [Evidence](WQ_A2_QUALIFICATION_CENTER.md).
4. **WQ-A3 Excel WQ0:** host-neutral implementation завершена: один identity
   collector owner, VSTO/native observation port, embedded pack и narrow same-build
   x64 helper для независимых client leases. Реальная Windows qualification остаётся
   отдельным обязательным gate; readiness не закрывает WQ0/5B2/R04.
5. **WQ-A4 suites:** common/provider/storage/UI и host packs, versioned fixtures,
   deterministic final-state verifier IDs и coverage gates.
   - [x] Canonical quick/full/release manifests и coverage owners embedded
     host-neutral; exact all-or-nothing capabilities оставляют отсутствующие
     production adapters/environment N/A, а не pass.
     [Evidence](WQ_A4_SUITE_CATALOG.md).
6. **WQ-A5 release integration:** immutable BuildEvidenceManifest и complete release suite.
   - [x] Detached RS256-signed evidence pin-ит signer/build/catalog/files и полный
     release run matrix; `release.candidate` доступен только для exact complete
     manifest. Preparation не создаёт tag, finalization требует тот же commit и
     evidence. [Evidence](WQ_A5_BUILD_EVIDENCE.md).

### Обязательные ограничения

- Нет второго model loop, tool executor, confirmation path, document gate, result
  classifier, durable store/index или UI-owned pass.
- Manifest — data only: без scripts/command lines/URLs/CLR/JS types/raw tool IDs.
- Mutations только в runner-owned или явно подтверждённой disposable copy; bound
  identity проверяется перед effect, `unknown`/missing/blocked не становятся pass.
- VSTO не запускает harness/MSBuild/Node/shell; exact build evidence импортируется
  через immutable provenance manifest.
- Новый tool/capability/event/UI projection получает coverage owner и happy/failure/
  effect assertions; absent capability помечается N/A, а не pass.

### Definition of Done

Пользователь может из UI выбрать pack текущего host, пройти сложные agent/manual/fault/
restart steps и экспортировать correlated expected/actual report. Automatic pass
происходит только из typed verifier evidence. WQ0 pack собирает независимые Excel
identity/lifetime observations без PowerShell; Windows evidence всё равно обязательно
до production 5B2. Реализация идёт отдельными commits WQ-A1–A5.

---

## Milestone WQ — отложенная Windows/Office qualification

### Цель

Проверить один собранный qualification candidate на реальном Windows/Office runtime,
не смешивая найденные дефекты обратно в один большой change. Полный сценарный порядок,
evidence и карта владельцев заданы в
[Windows qualification runbook](WINDOWS_QUALIFICATION_RUNBOOK.md).

### Порядок

1. Завершить WQ-A1–A5: runner/UI, встроенный `excel.wq0.identity`, suite catalog и
   exact-build release evidence; manual probe
   остаётся только engineering fallback при дефекте самого runner-а.
2. [x] До Windows candidate атомарно выполнить 11T0/7D: bound Excel
   session/factories, direct backend и удаление compatibility execution path с
   текущим `RuntimeKey` как явным lifetime assumption.
   [Evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md).
3. Собрать один versioned `16.1.0-dev` candidate из известного commit; полный
   host-neutral harness, architecture tests и compatible BuildEvidenceManifest зелёные.
4. На этом exact build выполнить WQ0 как diagnostic/regression принятого identity
   assumption, затем packs/runbook по контурам: baseline/controller, DocumentSession, VBA,
   Excel, Resource/ToolPack, persistence/UI/WebView и сквозные fault/restart scenarios.
5. Для каждого failure сохранить causal export/source IDs, expected/actual и назначить
   owner Phase 5–9 либо cross-cutting gate. Не исправлять разные контуры одним commit.
6. После исправления повторить targeted local regression и только затронутый Windows
   scenario; полный smoke повторить перед выходом из WQ.

### Definition of Done

Все обязательные runbook scenarios имеют evidence и pass; нет неразобранных P0/P1,
false-positive mutation success или неизвестного target/effect. Блокированные внешней
средой проверки остаются блокерами и не переносятся молча в Phase 12. Только после
этого candidate допускается к release hardening; prerelease tags создаются по §13.4.

---

## Phase 11 — migration and optional contours

**Pipelines: отключены по явному решению пользователя (2026-08-28).** Это сокращение действующего scope в Phase 2, не начало Phase 11. Нет исполнения (включая manual/dry-run/confirmation resume), discovery, authoring и UI; parser/executor и обходы вложенных зависимостей удалены. Старые определения не поддерживаются, не мигрируются и не replay-ятся; файлы пользователя автоматически не удаляются. Pipelines не участвуют в gates Phases 3–10/12. Их возврат — отдельное решение после stable core через общие ToolRuntime/contracts с собственными тестами; совместимость со старым форматом не требуется.

Phase 11 обычно состоит из отдельных post-stable minor contours. По явному решению
пользователя 2026-08-31 первый Artifact Library milestone был допущен раньше stable
core параллельно WQ. Последующим явным решением все существующие tools и active legacy
execution/history paths должны быть перенесены или удалены до Phase 12; это делает
11T и финальную legacy cleanup обязательными, но не добавляет новые optional Browser,
Automation или иные product capabilities. Каждый semantic contour остаётся отдельным
изменением. Production 11T начинается с атомарного 11T0/7D под явно принятым
`RuntimeKey` lifetime assumption и не оставляет промежуточного bound
`DocumentSession` поверх compatibility backend. WQ0 остаётся последующим обязательным
Windows evidence. Typed façade над
`ExecuteTool(ToolCommand)` или nullable/unbound `DocumentSession` не считается
миграцией и не разрешён как способ обойти gate.

Целевой пользовательский контракт библиотеки ресурсов, viewers, revision history,
edit/delete и попадания в model context зафиксирован в
[Artifact Library and Viewers](../artifact-library.md). Он сохраняет действующий
Resource Fabric: drafts не являются artifacts, committed resources используют
только exact `ResourceRef`, а UI projection/viewers не становятся вторым store,
transport или execution authority.

Приоритет пересмотрен 2026-08-31 вокруг четырёх пользовательских outcomes:
полноценный Artifact/Plan/HTML Workbench, надёжные typed Office tools, понятный UI и
все Office hosts из одного окна. 11T становится первым Office-runtime контуром с
атомарного 11T0/7D; отсутствие Windows не блокирует его host-neutral implementation,
но оставляет WQ0/WQ-SESSION/WQ-EXCEL открытыми. Read-only visibility идёт раньше authoring,
а локальная работоспособность каждого host — раньше публикации его cross-process
endpoint.

Порядок:

1. **11A — Artifact lifecycle/library foundation — done host-neutral:**
   - 11A1: separate draft/preparing/committed UI states and queue the full
     revision-guarded post-commit projection before attachment-helper or primary
     model transport. Windows WebView remains open.
   - 11A2: exact library head/history projection and cleanup of current kind/label
     drift. No new resource transport or generic editor. Windows WebView remains
     open. [Evidence](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md).
2. **11B — Plan — done host-neutral:**
   - 11B1: one domain service owns create/update, preserves exact whole Markdown and
     requires a unique linear exact-current head.
     [Evidence](PHASE_11B1_PLAN_REVISION_GUARD.md).
   - 11B2: restore-as-new-head plus guarded append-only tombstone removal without
     deleting exact historical message references.
     [Evidence](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md).
   - 11B3: history restore/removal UX and ready-plan handoff by pinned URI.
     [Evidence](PHASE_11B3_PLAN_HISTORY_HANDOFF.md).
   Windows WebView interaction remains open for the complete contour.
3. **11C — HTML — done host-neutral:**
   - 11C1: one unique monotonic whole-workspace revision sequence across branches,
     exact active parent and fail-closed ambiguous lineage.
     [Evidence](PHASE_11C1_HTML_LINEAGE.md).
   - 11C2: inert uploaded-HTML import with provenance and bounded source/preview.
     [Evidence](PHASE_11C2_HTML_IMPORT_PREVIEW.md).
   - 11C3: one checkpoint owner, binding completeness/integrity and guarded exact
     export without silent truncation.
     [Evidence](PHASE_11C3_HTML_BINDING_EXPORT.md).
   Windows WebView/Office interaction remains open for the complete contour.
4. **11D — complete Artifact Workbench viewers — in progress:**
   - 11D1 — done host-neutral: exact revision-pinned text representation through
     the shared gateway, fixed 32,000-character pages and a 512,000-character
     document bound; full copy/download requires a contiguous stable-hash read,
     while sanitized Markdown renders only when complete and retains exact Source.
     [Evidence](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md).
   - 11D2: image bytes, dimensions, fit/zoom/download and object-URL lifetime;
   - 11D3: PDF pages plus extracted text, explicit scan/truncation state and a
     separately admitted local renderer/worker;
   - 11D4: bounded audio player and transcript relation without autoplay.
   Every slice keeps `ViewerRegistry` UI-only and has its own MIME/security/vendor/
   lifetime tests. The milestone ends with Windows WebView qualification of
   Artifacts, Plan and HTML together, including reload, history and large payloads.
5. **11T — typed Office tools и удаление legacy host dispatch — admitted:**
   - [x] 11T0/7D — done host-neutral: один атомарный production change связывает exact выбранный workbook с
     `ExcelDocumentSession`, переключает factories и typed Excel read/write на прямой
     bound backend, затем физически удаляет compatibility commands/backends и
     `ActiveWorkbook`/descriptor execution fallback. Текущий `RuntimeKey` захватывается
     один раз на bound lifetime как явное допущение. Не оставлять промежуточный
     production 5B2 над `_adapter.ExecuteTool`; WQ0/WQ-SESSION/WQ-EXCEL остаются
     обязательным deferred evidence и не возвращают fallback.
     [Evidence](PHASE_11T0_EXCEL_BOUND_CUTOVER.md);
   - 11T1–11T5: переносить существующие Excel capabilities по families:
     find/replace, sheet lifecycle, clear/sort/filter/format, tables, charts;
   - 11T6–11T8: Word, PowerPoint и Outlook по одному host vertical. Каждый сначала
     получает собственный bound local `DocumentSession`, exact target/lifetime gate
     и host pack, затем его существующие reads и mutations переходят по semantic
     families;
   - первый проход сохраняет exact public ids, schemas и пользовательское поведение.
     Один slice переключает Agent/manual execution на typed request → domain service
     → narrow bound backend → typed outcome/effect evidence и в том же изменении
     удаляет заменённый host switch, mapper и dead helpers;
   - расширения schema/возможностей идут только вторым отдельным проходом после
     qualification и trajectory/eval evidence. Разрешены bounded semantic operations
     с одним target/effect/recovery contract; generic `execute_actions`, arbitrary
     command list и batch writes запрещены.
   - 11T9: переключить VBA mutations/packages и остальные controller-owned existing
     tools на direct typed registrations/backends без `ToolDefinition`/legacy
     `ToolResult` roundtrip; durable journals/CAS остаются единственной authority;
   - [x] independent host-neutral cleanup: удалить pre-R37 trajectory inference;
     wrong-type retained operation остаётся exact incompatible/reset-only evidence
     и не входит в tool-execution;
   - 11T10 final cleanup: после последнего consumer удалить
     `IOfficeApplicationAdapter.GetBuiltInTools/ExecuteTool`, host tool-id switches,
     `OfficeBuiltInToolCatalog` legacy DTO projection, `LegacyToolDefinitionAdapter`,
     `LegacyToolResultAdapter` и `ToolResultUiProjection`. Custom tool/skill
     authoring получает versioned typed contracts в
     соответствующих 11J/11K slices. Ни один adapter/alias/dual dispatch не остаётся.
   Кандидаты и checklist закреплены в
   [Architecture follow-ups §B](ARCHITECTURE_FOLLOWUPS.md).
   11T и final active-legacy cleanup являются Phase 12 prerequisite по явному решению
   пользователя 2026-08-31; новые optional product contours ими не становятся.
6. **11E — coherent product UI and Issue Center:** one Library shell may expose
   separate Artifacts, Tools and Skills sections without merging their authority.
   Use one status vocabulary for draft/preparing/committed/running/error/unknown/
   blocked/stale; preserve exact target and revision in every detail view. Add a
   Problems projection, exact causal navigation and redacted `Copy issue`/evidence
   export over existing trajectory and qualification owners, never a second log or
   outcome inferred from prose. See [Qualification §11](../qualification.md#11-phase-11-issue-center).
7. **11F — read-only Tool Inspector and host capability truth:** first show the
   current local endpoint's exact built-in/custom/document-local tools, then reuse
   the same DTO for the Host Fabric-selected endpoint; include origin, host/scope,
   callable/blocked reason, policy, catalog/package revision, qualification state
   and exact run/result links. It is a projection over runtime authority, not an
   editor or second catalog. See [Tool Library](../tool-library.md).
8. **11G — Host Fabric core on Excel:** endpoint/lease/target DTOs, fail-closed run
   pinning, one-process inventory, then cross-process Excel through an approved
   broker or separately admitted peer rendezvous. No ROT fallback or cross-process
   COM. An initial picker proves target changes cannot retarget an accepted run.
9. **11H — host parity, one independently qualified vertical at a time:** Word,
   PowerPoint, then Outlook. Each slice first proves its local `DocumentSession`,
   resources, built-in tools, confirmation/effect evidence, restart/fault behavior
   and host pack; only then does it publish a Host Fabric endpoint adapter. VBA is
   admitted only for Excel/Word/PowerPoint capabilities that actually exist.
10. **11I — unified all-host experience:** one picker/filter/activation/auto-follow
   UX, endpoint health and capability matrix across admitted Excel/Word/PowerPoint/
   Outlook instances. Finish mixed-process, modal/busy, Save As, close/restart,
   stale lease and in-flight target-switch gates. The Office-hosted launcher remains
   a separate optional profile. See [Host Fabric](../host-fabric.md).
11. **11J — custom Tool Library and authoring:** only after 11F and Host Fabric
    target pinning. Add immutable package history, exact revision conflicts,
    restore-as-new-head, tombstone, import/export provenance and disposable-document
    test flow; switch UI/model authoring without changing an accepted run catalog.
    Direct-handler/typed-host removal of remaining VBA definition/result adapters
    is mandatory after 11T0/7D. Existing 6H-admitted package behavior remains the
    source contract while 11T9/11J remove its compatibility execution path before
    Phase 12. See
    [Tool Library](../tool-library.md).
12. **11K — Skills authoring:** installed skills remain global/host-scoped Library
    capability packages, not chat artifacts. Add immutable package history, exact
    version/revision UX, restore-as-new-head, tombstone, guarded conflicts and
    explicit artifact import/export; preserve exact `common.capabilities_read` and
    later-run catalog refresh. See [Skill Library](../skills.md).
13. **11L — Browser:** a separately permissioned session/package. HTML preview
    WebView is never reused as browser authority.
14. **11M — Local Automation:** LA0 session-ownership ADR and LA1 bounded read-only
    files first; LA2 guarded file mutations with Recycle-Bin-first delete; LA4 typed
    non-shell execution in a signed isolated worker; LA5 raw shell/PTY and LA6
    desktop control remain separate deny-by-default high-risk capabilities. See
    [Local Automation Agent](../local-automation-agent.md).
15. **11N — Pipelines:** remain disabled. Reconsider only by another explicit
    decision after the preceding runtime/tool/host contracts are qualified; no
    compatibility with the removed format is required.

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

Host Fabric additionally proves that the UI-selected target and immutable run target
are separate, all COM executes at the owner endpoint, and endpoint loss cannot fall
back to another Office instance. Local Automation requires a prior session-ownership
ADR, explicit grants and an isolated worker before any mutation/process tool is
published; lack of permission cannot be worked around through Office.

Tool Inspector separately proves that displayed availability, policy, revision and
host scope come from the selected endpoint's immutable catalog snapshot and cannot
grant execution authority. Tool authoring additionally proves append-only complete
package revisions, exact-head conflicts, no built-in/skill shadowing, confirmed
artifact import and next-run-only catalog refresh. Editor test uses the production
ToolRuntime and disposable target rules; it cannot invoke an executor directly.

Issue Center separately proves that every problem and navigation link retains exact
source event IDs, run/tool/target/build revisions and authoritative outcome. It does
not classify model prose, overwrite an old failure with a later pass, retry an
unknown effect or persist a second diagnostic index. Default export remains redacted.

Для HTML/Plan whole-content mutations отдельно проверить сохранение точного payload и revision lineage, отсутствие тихой обрезки и восстановление предыдущей revision. Валидный model envelope не является проверкой синтаксиса/работоспособности содержимого; UI/результат не обещает такую проверку без отдельного domain evidence. R29/R28 не считаются закрытыми самим включением optional contour.

Artifact foundation отдельно доказывает: draft отсутствует в durable projection и
context; CAS/message/artifact save завершается и monotonic UI projection ставится в
очередь до первого fake model transport call без ожидания WebView acknowledgement;
provider failure после commit сохраняет user turn и
resources; message cards остаются pinned к exact revision; immutable uploads не
получают in-place mutation по расширению; stale projection не заменяет новую.
Удаление append-only и не переписывает JSONL; физический blob удаляет только
fail-closed reachability GC. Uploaded HTML показывается inert source и исполняется
только после explicit import в HTML workspace.

Skills authoring separately proves that uploaded skill-shaped content remains
untrusted until explicit confirmed install; custom core/references form one package
revision; restore/delete replay from an append-only skill journal; built-ins and
tool ids cannot be shadowed; UI-selected Office host does not override the Host
Fabric execution target's skill catalog. Skill history is not stored in document
chat streams and cannot introduce a second skill-body model transport.

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

Milestone WQ должен быть закрыт до начала Phase 12. При доступной Windows проверки
document binding/lifetime и VBA/Excel effects предпочтительно выполнять в своих фазах;
в согласованном режиме §16.1 они могут быть накоплены до WQ, кроме blocking 5B2
identity probe перед production factory switch. Fake-host tests не заменяют COM,
WebView2 или live-provider validation. Phase 12 повторяет итоговые release gates, а не
служит первым реальным тестированием candidate.

- [ ] Полный host-neutral harness.
- [ ] Architecture tests.
- [ ] ModelProtocol 20-attempt scenarios.
- [ ] R29: runtime-generated IDs без потери payload, confirmation/replay сохраняют IDs; explicit protocol cutover квалифицирован.
- [ ] R28: live streaming message/reasoning проверен по SSE → projector → bridge → реальный WebView, включая reset при repair.
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
| Write изменил состояние и read-back verified | `ok`, verified write count увеличен |
| Успешный mutating call оказался no-op | invocation count может увеличиться, число фактических writes — нет |
| Policy обещает verification, но actual evidence отсутствует | нет verified-success claim; для требующего read-back write результат `unknown` |
| Один write ok, другой error | health `errors`, нельзя показать «всё применено» |
| Любой unknown write/external среди calls | health `unknown` |
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
| Handler/policy изменены под тем же ID | pinned definition либо отказ до dispatch, без скрытой подмены |
| Compaction после загрузки schemas | следующий request содержит точные pinned schemas; summary не заменяет evidence |
| Большой tool result | bounded preview + exact ResourceRef, без второго CAS transport |
| Терминальный append не удался после possible write | остановка, durable start/preparation остаётся, reload/reconciliation без replay write |
| Model call после R29 не содержит ID | runtime выдаёт ID до persistence/dispatch и сохраняет его через confirmation/replay |
| Runtime ID collision после R29 | infrastructure failure до dispatch, без model repair |
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
21. Call IDs назначает runtime по исправленному контракту R29; неизменный валидный payload не регенерируется ради ID.
22. Model-facing большие результаты используют exact ResourceRef, не CAS/content_ref transport; actual verification не выводится из policy или invocation counts.
23. Milestone WQ закрыт на зафиксированном Windows x64 + Office x64 build; отложенные COM/WebView/live-provider gates не числятся как host-neutral pass.

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

Обязательны границы текущей фазы, локальная чистка затронутого контура (§15.1), честная проверка результата (§22.1) и краткое обновление `PROGRESS.md`. Safety/recovery invariants не ослабляются. Остальные требования применяются по фактическому изменению:

- [ ] Для изменённого кода: ответственность в правильном контуре, контракт минимален, нет новых hidden fallback и safety logic по тексту.
- [ ] Для оставленного adapter: owner, действующие consumers, причина и ближайший removal gate.
- [ ] Для подготовительного рефакторинга: обоснована и проверена польза по §15.2.
- [ ] Для изменённого поведения: покрыты нормальный сценарий и ошибка; для write также fault/unknown. Использовать подходящие существующие tests, добавлять только недостающее покрытие.
- [ ] При изменении контракта/поведения/границ: обновлены затронутые canonical docs; при новом или изменённом архитектурном решении — ADR. Не добавлять ADR ради механической правки.
- [ ] При добавлении/удалении/перемещении `.cs`: обновлены old-style `.csproj`.
- [ ] Для затронутого Office/COM/controller wiring: явно указано, что проверено и какая Windows validation остаётся gate; отсутствие среды не означает pass.
- [ ] Для commit/release: выполнены применимые versioning gates; обычный commit не повышает version и не создаёт tag.

## 22.1. Минимальная достаточная проверка

| Изменение | Проверка |
|---|---|
| Только документация | Diff и затронутые локальные ссылки/anchors; без build, harness и Office validation |
| Локальное Core/Office-neutral поведение или выделение | Минимальный релевантный harness filter; интеграционная проверка только затронутых связей |
| Static UI / bridge | Подходящие JS/contract tests; real WebView/controller wiring на Windows, если требуется gate |
| Поведение нескольких подсистем или общая инфраструктура без достаточного targeted coverage | Full host-neutral harness; список файлов/assemblies сам по себе не определяет этот случай |
| COM/VSTO и release qualification | Явные Windows/Office и release gates; на этой машине не запускать VSTO/Office validation |

Не запускать проверки повторно ради отчёта, нового подэтапа или docs-only правки. Успешный результат можно переиспользовать только при неизменных относящихся к нему production/test sources, dependencies, build settings и environment; указать источник результата. После влияющих правок, неполного/падающего прогона или новых признаков риска повторить затронутые проверки. Исторический pass не подтверждает изменённый код.

При нескольких filters использовать один актуальный build и `--no-build` для следующих запусков; после изменения compile inputs сначала обновить build. Это не отменяет обязательный `ValidateVersionFormat` перед commit и явно назначенные phase/cutover/release gates. Блокирующая проверка остаётся блокирующей до выполнения; broad harness не заменяет Windows/controller validation.

---

# 23. Формат отчёта агента после этапа

По умолчанию — короткий абзац или 3–5 пунктов, без обязательных заголовков:

- **Результат:** завершённый подэтап, что изменилось и зачем; ключевые файлы/ссылки.
- **Проверка:** релевантные команды/результаты либо ссылка на точные evidence; reused result явно обозначить. Для docs-only достаточно diff/links и отметки, что build/tests не запускались.
- **Осталось:** только реальные ограничения, непроверенные gates, новые риски и следующий шаг, если он изменился. Для затронутого Office/controller поведения указать Windows qualification.

Отдельно описывать legacy, рефакторинг, version/tag/commit только когда они изменились, оставляют блокер либо пользователь об этом спросил. Не выводить пустые «нет новых рисков», «версия не менялась» и аналогичные разделы. Before/after нужен лишь когда без него неясно изменение поведения.

`PROGRESS.md` хранит краткий текущий статус и следующий шаг; canonical doc — контракт; `MIGRATION_MAP.md` — актуальных consumers/removal gates. Не копировать один отчёт во все документы. Отдельный `PHASE_*.md` нужен только для объёмной матрицы/evidence, не для каждого commit или docs-only изменения; точные команды можно сохранить в существующем отчёте или progress.

Для release, сложного cutover или запроса пользователя отчёт можно расширить до необходимой детализации. Краткость не разрешает скрывать невыполненный gate или объявлять фазу завершённой до её Definition of Done.

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

Модель вернула неизвестный tool или schema-invalid args
→ ModelProtocol

Policy/binding/args не проходят runtime recheck или manual execution
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
In-app Qualification Center
    ↓
Release qualification
    ↓
16.1.0
    ↓
Optional contours — отдельные последующие milestones
```

Основной маршрут: Phases 0–10 host-neutral → Milestone WQ-A → обязательные
11T existing-tool migrations и active-legacy cleanup → Milestone WQ, включая WQ0 →
Phase 12 →
stable core. Остальные новые optional Phase 11 product contours не блокируют этот
маршрут.

Основная проверка каждого архитектурного решения:

> Новый функционал не должен заставлять AgentKernel понимать новую предметную область.

Основная проверка результата выполнения:

> Модель может ошибиться в тексте, но runtime не должен ошибиться в факте.
