# RNAssistant V8 — целевой контракт контекста, истории, ресурсов и evidence

**Дата:** 2026-10-30. **Статус:** спецификация для реализации, не описание уже готового функционала.  
**Связанный документ:** `RNAssistant_V8_Diagnostics_Qualification_Plan.md`.  
**Репозиторий:** https://github.com/Mutagrey/RNAssistant

> Перед правками сверить HEAD, снять реальные HTTP traces и проверить существующие владельцы. Не создавать второй compiler, EventLog, CAS или реестр критериев.

## 1. Зафиксированные решения

1. История включает **несколько user-сообщений, несколько run, assistant/tool в хронологическом порядке**. Developer Contract и Runtime Context компилируются отдельно.
2. Model-facing формат: **Markdown headings + компактный YAML для метаданных + JSON для schemas, V8 и tool results + исходный текст/код для source bodies**. `content` в Chat Completions — строка, экранируемая HTTP serializer один раз. Markdown fences необязательны для API, но помогают читабельности; не помещать весь developer в один fence.
3. Capabilities имеют **две формы**: `id — description` и полное definition. Показывать **Available now** (callable) и **Load on demand** (каталог). Полные JSON schemas только для callable tools; skill body только после загрузки и если релевантен. Не дублировать полные bodies в каталоге, истории и developer.
4. **`T1`** — ID вызова и `tool_call_id`; **`E1`** — только eligible evidence; **`R1@v2`** — exact observed resource version. `C` и `S` зарезервированы. Runtime выводит заглавные префиксы; парсер ссылок регистронезависим (`e1`=`E1`, `r1@V2`=`R1@v2`); нормализация **только ссылок**, не paths/targets/кода. Native пара `assistant.tool_calls[].id` ↔ `tool.tool_call_id` должна буквально совпадать.
5. Нумерация T/E/R сквозная **в пределах чата**, включая run, restart, compaction, confirmation. Нельзя переиспользовать. Fork/import: явно разрешать коллизии, не переназначать молча.
6. Выбран **вариант 2**: модель видит `R1@v2` для достоверно привязанных наблюдений, но вызывает tools по **семантическим targets**. R-ссылка не permission, не tool argument и не finish evidence. Не присваивать R поисковым элементам без exact typed provenance.
7. Новый tool result должен иметь **содержательное тело**: read → текст/таблица, target, coverage, complete, ref; patch → changed/no-op, diff/locations, verification/read-back при наличии. Не подменять свежий результат одной ссылкой. Если не помещается — явная частичность или typed budget error, не тихое исключение.
8. Сохранять causal chronology. Повторные и устаревшие bodies сокращать **на их исходных позициях**, оставляя receipt; `CURRENT_RESOURCE_VIEW` использовать только для самостоятельной материализации, когда достаточного тела нет в активной истории. Не создавать view после каждого patch.
9. `finish.results[].criterion` вместо `summary`. Критерии формулирует модель; **никакого отдельного registry критериев**. Сохранить `finish.outcome` и действующие `assessment` значения.
10. Только eligible terminal result получает `E1`; `T1` не evidence. Runtime проверяет выбранные E и формальные противоречия; модель отвечает за семантическую полноту. Старые ошибки сами по себе не блокируют finish. Существующие permission, confirmation, no-replay, unknown-effect safety guards **не удалять**.
11. `role:tool` применять по фактическим endpoint probes, независимо от генерации native tool calls. Проверить `content:null`, `""`, отсутствие поля, несколько calls, negative controls, fallback roles, JSON modes. HTTP 200 недостаточно.

## 2. Состав и порядок запроса

1. `developer` — **Agent Contract**, стабильный краткий V8 протокол и ссылки.
2. `developer` — **Runtime Context**, текущий host/mode/document, permissions, capabilities, callable definitions, актуальные loaded skills.
3. **История**: `user #1 → assistant → tool → assistant finish → user #2 → ... → user #N`; сохранять порядок и причинность. Rejected drafts не становятся историей.
4. **При необходимости** compacted checkpoint, релевантные receipts, independently materialized resource views — как **данные**, не как новые пользовательские инструкции. Не вставлять synthetic message между native call и результатом.
5. Последний trigger — актуальный user/tool continuation. Не добавлять пустой trailing developer после каждого шага.

### 2.1. Agent Contract — пример

```md
# RNAssistant Agent Contract
## Actions
Return one V8 JSON object: execute, reflection or finish.
Execute tool_calls contain name and arguments only; runtime assigns IDs.
## References
Tn = tool call, never finish evidence.
En = eligible evidence; cite only issued En in finish.
Rn@vn = observed resource revision; not tool argument or finish evidence.
Reference lookup is case-insensitive; canonical display T1, E1, R1@v2.
## Completion
Assess original user requirements, not just successful calls.
Report incomplete/unverified requirements. Status=ok alone does not prove effects.
```

### 2.2. Runtime Context — пример (Markdown/YAML + точная JSON Schema)

````md
# RUNTIME_CONTEXT
## Environment
```yaml
host: Excel
mode: agent
document: Book1.xlsm
state: editable
permissions: {read: allowed, write: confirmation_required}
```
## Capabilities — available now
- `core.find` — Find resources by semantic target
- `core.read` — Read a resource or bounded section
- `core.tools.read` — Load an exact tool or skill definition
## Capabilities — load on demand
- `common.vba_apply_patch` — Apply guarded VBA changes
- `vba.refactor` (skill) — Safe VBA refactoring guidance
## Callable tool definitions
### core.read
```json
{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}
```
## Loaded skills
### vba.refactor
[Exact admitted Markdown instructions, only when loaded and relevant.]
## Resource index (optional, metadata only)
- `R1@v2` — Module1, text, lines 1–120; body in T7
- `R2@v1` — Sheet1, table; body not included
````

Это **иллюстративные** IDs/schemas, не фактическая schema `core.read`; агент должен использовать действующий каталог. Индекс ресурсов необязателен, если всё видно в истории. Нельзя ложно объявлять snapshot «current».

### 2.3. Реальная история с несколькими user

```json
[
  {"role":"developer","content":"# RNAssistant Agent Contract\n..."},
  {"role":"developer","content":"# RUNTIME_CONTEXT\n..."},
  {"role":"user","content":"Проверь Module1."},
  {"role":"assistant","content":null,"tool_calls":[{"id":"T1","type":"function","function":{"name":"core.read","arguments":"{\"target\":\"Module1\"}"}}]},
  {"role":"tool","tool_call_id":"T1","content":"{\"status\":\"ok\",\"evidence\":\"E1\",\"data\":{\"ref\":\"R1@v1\",\"target\":\"Module1\",\"text\":\"Option Explicit\\n...\"}}"},
  {"role":"assistant","content":"{\"action\":\"finish\",\"reason\":\"Reviewed\",\"outcome\":\"completed\",\"results\":[{\"criterion\":\"Module inspected\",\"evidence\":[\"E1\"],\"assessment\":\"satisfied\"}],\"message\":\"Проверено.\"}"},
  {"role":"user","content":"Исправь ошибку."},
  {"role":"assistant","content":null,"tool_calls":[{"id":"T2","type":"function","function":{"name":"common.vba_apply_patch","arguments":"{\"target\":\"Module1\",\"patch\":\"...\"}"}}]},
  {"role":"tool","tool_call_id":"T2","content":"{\"status\":\"ok\",\"evidence\":\"E2\",\"data\":{\"target\":\"Module1\",\"diff\":\"@@ ...\",\"afterRef\":\"R1@v2\"}}"},
  {"role":"user","content":"Проверь, что исправление сохранилось."}
]
```

**Условие:** этот вариант истории допустим только после проверки endpoint. `content:null` — диагностируемый вариант, не догма. Raw `execute` модели — V8 JSON без IDs; runtime отдельно создаёт history projection, не изменяя canonical accepted response.

## 3. TOOL_RESULT / TOOL_INTERACTION / CURRENT_RESOURCE_VIEW

| Тип | Смысл | Место | Тело |
|---|---|---|---|
| `TOOL_RESULT` | Фактический результат вызова | Хронология, парный `role:tool` | Новый результат содержателен, полный в пределах контракта |
| `TOOL_INTERACTION` | Сокращённый исторический receipt | **На прежнем месте** | T/E/R, outcome, reason, retainedAt при наличии |
| `CURRENT_RESOURCE_VIEW` | Независимая материализация exact source observation | Synthetic data block по необходимости | Ref, target, view, coverage, actual body |
| `COMPACTED_HISTORY` | Смысловой checkpoint | Вместо старого префикса | Goal, constraints, decisions, findings, pending |
| `WORKING_SET` / `RESOURCE_OBSERVATION` | Доступность/лимит тела | Короткий notice | Не утверждать, что omitted body виден |

### 3.1. Новый read

```json
{"status":"ok","evidence":"E3","data":{"kind":"resource-read","ref":"R1@v2","target":"Module1","representation":"text","coverage":{"kind":"line-range","start":1,"end":120},"complete":false,"text":"Option Explicit\n..."}}
```

### 3.2. Новый patch

```json
{"status":"ok","evidence":"E4","data":{"target":"Module1","changed":true,"diff":"@@ ...","afterRef":"R1@v3","verification":"read_back_verified","readBackCoverage":{"kind":"line-range","start":20,"end":50}}}
```

`afterRef` **не означает** `bodyIncluded`. Не заявлять, что модель видела весь модуль, если patch вернул только diff/узкий read-back. Если следующее действие требует иного участка, запросить новый read или точную материализацию.

### 3.3. Read → identical read

```text
T1 core.read → E1, R1@v1, bodyIncluded=false, omittedReason=duplicate, retainedAt=T2
T2 core.read → E2, R1@v1, bodyIncluded=true, text=[actual code]
```

Сохранять обе native assistant/tool пары. Если тело T2 позднее исключено, убрать/исправить `retainedAt=T2`. Дедуплицировать только при совпадении exact identity, revision, view, coverage и тела; разные ranges и разные ресурсы — не дубликаты.

### 3.4. Read → patch → read → patch → read

```text
T1 read  R1@v1 → old body omitted; E1 remains historical
T2 patch R1@v1 → R1@v2 → E2; diff/verified receipt
T3 read  R1@v2 → old body omitted after next mutation; E3 remains
T4 patch R1@v2 → R1@v3 → E4; diff/verified receipt
T5 read  R1@v3 → full current body; E5
```

Пока T5 содержит достаточный body, **не создавать** ещё один `CURRENT_RESOURCE_VIEW`. После компакции T5 может отсутствовать в активной истории, тогда:

````md
# COMPACTED_HISTORY
- Module1 inspected and patched twice; last verified patch E4.
- R1@v1 and R1@v2 are historical.
- Current observed revision R1@v3; verification pending.

# CURRENT_RESOURCE_VIEW
```yaml
ref: R1@v3
target: Module1
representation: text
coverage: {kind: whole}
bodyIncluded: true
observationState: current
```
```vb
Option Explicit
...
```
````

Это **данные, не инструкции**. `current` определяется typed Resource Authority, не самим именем ссылки.

### 3.5. Правила дематериализации

1. Предпочитать последний достаточный полный tool result в активной хронологии.
2. Иначе более ранний exact result, если всё ещё валиден.
3. Если нет подходящего вызова в активной истории — один `CURRENT_RESOURCE_VIEW` из проверенного snapshot/CAS.
4. Если body не нужен — краткий receipt по месту либо исключение всей пары при компакции с сохранением факта в checkpoint.
5. Не повторять один полный body для одной identity+revision+view+coverage в одном запросе без причины. Не склеивать разные ресурсы.
6. **Новый результат** на следующем запросе обычно сохранять полностью. Если бюджет не позволяет — явное partial/budget notice или ошибка с рекомендацией narrower scope. Нельзя молча выдавать `status:ok` с одной ссылкой.
7. После patch модель должна получить достаточный актуальный read-back изменённого участка, но не обязательно весь файл. Устаревший source не даёт authority на следующую запись.
8. Всё — **wire-only projection**: исходные durable events, payload, resource revision не переписываются. Исключение из prompt не удаляет источник.
9. Не ломать native pairing; сокращать тело внутри парного tool result либо исключать всю пару при compaction. Никаких orphan tool messages.
10. Отсутствие тела не равно отсутствию ресурса. `retainedAt` допустим только если цель реально включена в тот же запрос.

## 4. Evidence, finish и ошибки

**Trusted tool policy:** `EvidenceEligibility = None | Eligible` (встроить в существующий ToolPolicy/descriptor, не отдельный mutable registry). `capabilities_search`, schema/skill loading по умолчанию `None`; чтение, patch, test, verification и значимые ошибки могут быть `Eligible`. `E1` значит «можно цитировать», а **не** «успех».

- `Tn` назначается при принятом вызове до dispatch; `En` — при допустимом terminal result. Tool сам не назначает En. Для failed patch `E5` допустим для `unverified`/`not_satisfied`.
- Новое поле `criterion`, **не** `summary`:

```json
{"action":"finish","reason":"Changes verified","outcome":"completed","results":[{"criterion":"Module1 updated and checked","evidence":["E4","E5"],"assessment":"satisfied"}],"message":"Модуль изменён и проверен."}
```

- Admission проверяет: V8 shape; `outcome` ↔ assessments; для **каждой выбранной** En — существование, canonical terminal event, trusted eligibility, принадлежность истории, фактическую доставку модели (в том числе компактный индекс); явные формальные противоречия результата и `satisfied`. Не делать вид, что `status:ok` доказывает смысл критерия.
- **Не проверять успех всех En истории и не требовать цитировать все вызовы.** Старые failed patches не блокируют успешный результат после исправления. Integrity полного реестра — при append/recovery, не повторный полный проход на каждом finish.
- Если были только служебные вызовы без eligible En, `evidence:[]` допустим. Если заявлено проверенное изменение, требуется соответствующее доказательство. Если нет достаточных фактов, честный `partial`/`unable_to_complete` должен быть возможен в рамках существующих safety guards.
- Сохранять `finish accepted`, `run completed`, `task succeeded` разными понятиями. Модель отвечает за семантическую полноту и формулирует критерии по **исходным требованиям**, не по удобным успешным операциям. Runtime не создаёт criterion registry.
- Unknown effects: сохранить существующие запреты на автоматическое повторение небезопасных операций и текущую проверку внешнего состояния. **Не** создавать вторую машину состояний для «закрытия» каждого En. Если расследование невозможно, дать ограниченную честную остановку/неполный исход, а не бесконечные reject loops. Изменение существующего `InspectionRequired` — отдельный safety review.
- При reject вернуть **короткий typed reason** (например `unknown_evidence`, `undelivered_evidence`, `contradictory_assessment`, `missing_required_verification`) и доступные En, не многократно добавлять общий `COMPLETION_FEEDBACK` developer. `REFLECTION_BUDGET` хранить и проверять в runtime; не повторять длинный счётчик в каждом prompt. Не удалять защитные проверки без тестов.

### 4.1. История нескольких run и compaction

T/E/R ссылки уникальны для чата, даже когда run завершён. Внутри durable mapping сохранять `sessionId`, `runId`, `turnId`, `stepId`, canonical call ID, event sequence, tool outcome, resource identity/revision/coverage и модельный alias. Не создавать независимую БД ссылок. Старое En можно цитировать как **исторический факт**, если оно действительно показано модели; утверждение о *текущем* состоянии требует проверки currentness. Не пытаться материализовать все En в каждом запросе: хранить полный реестр durable, а модели — релевантные receipts/индекс, с возможностью поздней материализации отдельным tool (следующий этап).

### 4.2. Миграция формата

Сейчас V8 использует `E1` как execution reference и `R1@v2` как resource, `results[].summary`, native `EXECUTION_REFERENCE`. Новая схема **семантически несовместима**: `T1` — call, `E1` — eligible result. Нельзя тихо переименовать старые `E1` и считать их новыми evidence. Выбрать явную revision/version boundary: новые чаты используют новую версию reference contract и `criterion`; старые записи остаются читаемыми в прежнем контракте, либо требуют явного безопасного нового чата. Ни автоматической перезаписи EventLog, ни скрытой двусмысленной dual-interpretation. Обновить DTO/parser/schema/writer/prompt/history reader/compaction/inspector/tests согласованно.

## 5. Endpoint transport и fallback

**Native tool generation** и **native tool-history visibility** — отдельные capability flags. При поддержке истории сохранять `assistant.tool_calls` → `role:tool`, включая парность Tn. Если native generation недоступна, модель генерирует обычный V8 `execute` JSON, runtime исполняет и строит **историческую проекцию**; не выдавать это за нативный ответ модели. Если native-history не проходит, использовать существующий fallback result role `user` или `developer` с явно ограниченным `TOOL_RESULT` envelope и T/E/R, сохраняя данные как данные, а не новые пользовательские инструкции. Фактическую совместимость установить тестами, не предположениями.

`json_object` → прямой V8 JSON; `json_schema` → действующий строгий wrapper `{"response": <V8>}`. HTTP 500 **не** доказательство запрета JSON Schema; исследовать точный payload/error. Не переключаться молча при любой 500. Режимы `text_json`/`json_object`/`json_schema` — только по проверенным возможностям, с локальным строгим V8 parser во всех случаях. JSON в `content` — строка, а не вложенный объект.

## 6. Конкретные существующие владельцы для аудита и правок

- `src/RNAssistant.Office/Services/ConversationPromptComposer.cs` — developer, environment, catalog, schemas, skills.
- `src/RNAssistant.Core/Services/ModelContextCompiler.cs` — causal frames, hydration, dedup, omission, current views, budgets.
- `src/RNAssistant.Core/Services/ContextCompactionRuntime.cs` — checkpoint/compaction, границы истории.
- `src/RNAssistant.Core/Services/ContextClaimProjection.cs` — `SKILL_CONTEXT_NOTICE`, `TOOL_SCHEMA_NOTICE` и provenance.
- `src/RNAssistant.Core/ModelProtocol/ModelReferenceProjection.cs` — aliases и wire-only mapping; удалить `EXECUTION_REFERENCE`.
- `src/RNAssistant.Core/ModelProtocol/ModelRequestPreparation.cs`, `ModelProtocolWire*`, `ModelProtocolClient` — request/response contracts, format modes.
- `src/RNAssistant.Core/Persistence/ConversationFinishEvidenceAdmission.cs` — новые En и admission.
- `src/RNAssistant.Core/Agent/AgentKernel.cs`, `AgentProgressTracker.cs` — rejection, unknown effect, limits.
- `src/RNAssistant.Office/Services/ConversationModelSession.cs`, `ConversationKernelAdapter.Store.cs` — model context и durable finish.
- `src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs` — Workspace parity.
- `src/RNAssistant.Office/Services/ModelContextInspectorService.cs` и `web/js/app-model-context.js` — фактический wire inspector.
- `docs/protocols/CONVERSATION_RESPONSE_V8.md`, `docs/model-endpoint-compatibility.md`, `docs/architecture.md`, `docs/resource-fabric.md` — обновить нормативную документацию.

**Известные текущие ветки для ревью:** `AddCurrentMutationSources`, `ArchivedBody`, `TryFoldCompletedOperation`, `OmitSourceBody`, `SourceOmission`, `CURRENT_RESOURCE_VIEW`, `TOOL_INTERACTION`, `WORKING_SET`, `ModelReferenceProjection.ProjectMessage`, `CompletionFeedback`, `ReflectionBudget`. Не удалять без baseline трассировки и доказательства, что новая политика сохраняет safety/provenance.

## 7. Этапы реализации

**P0 — Baseline.** Зафиксировать SHA, инвентаризировать все источники model messages и transport projection; снять точные request bytes для Office/Workspace/CLI, read→read, read→patch→read, multi-user/multi-run, compaction. Описать реальные дубли/пропажи, owner каждого преобразования.

**P1 — Transport qualification.** Реализовать headless сценарии из второго документа, проверить native history и `content:null` на выбранных реальных endpoints; записать supported profile. До результата не менять default tool-result role.

**P2 — Contracts.** Ввести T/E/R alias separation, регистронезависимый lookup, `criterion`, eligibility, новую reference revision. Сохранить canonical IDs/events. Убрать дублирующие `EXECUTION_REFERENCE` и `content.tool_call_id` в native transport.

**P3 — Context composition.** Компактный Agent Contract и Markdown/YAML Runtime Context; разделение available/load-on-demand; единственная materialization skills и schemas; точные provenance/coverage. Убрать избыточный JSON, не теряя необходимых machine contracts.

**P4 — History/materialization.** Сделать хронологический folding и дедуп, убрать автоматические redundant current views, сохранить свежие tool bodies; regression tests для всех цепочек и бюджетов.

**P5 — Finish/compaction.** En admission, multi-run references, checkpoints, safe migration, recheck old histories. Сохранить safety guards и typed rejection.

**P6 — Inspector/qualification.** Интегрировать фактические HTTP messages в структурированный инспектор и Diagnostics Lab (второй документ). Проверить одинаковую семантику Office/Workspace/CLI.

## 8. Критерии приёмки

- В фактическом wire видны **несколько user** и правильная хронология; нет orphan tool results.
- `T1`/`E1`/`R1@v2` не смешиваются; case-insensitive lookup; уникальность сквозная по чату.
- `core.read` доставляет тело/coverage; patch — содержательный diff/read-back; fresh body не пропадает молча.
- Повторные чтения сокращают только дубликат, не уничтожают единственную полную копию.
- После двух patch нет старого body, выдаваемого за current; после compaction source можно материализовать точно один раз.
- Loaded skill и callable schema не дублируются, доступны для реального model request.
- `finish.results[].criterion`, только eligible `En`; ошибочные/чужие/недоставленные ссылки отклоняются; старые failed patches не блокируют завершение.
- Native/fallback режимы выбираются по фактическим endpoint probes; `content:null` тестируется.
- Raw HTTP request и читаемая структурированная проекция в инспекторе согласованы по каждому сообщению и блоку.
- Durable history/CAS не переписываются компакцией, legacy history не получает молчаливой миграции.
- Проходят existing Harness/CLI/Web и Windows/Office qualification по затронутым областям; измерены токены/latency и отсутствие роста дублей.

**Запрет на «успех по тестам» без реального endpoint:** synthetic harness доказывает логику клиента, но не поведение провайдера. Сохранять отдельные результаты real-model qualification.
