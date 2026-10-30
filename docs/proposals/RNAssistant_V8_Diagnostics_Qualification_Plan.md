# RNAssistant V8 — Model Diagnostics & Compatibility Lab

**Дата:** 2026-10-30 · **Статус:** план для агента  
**Зависимость:** `RNAssistant_V8_Context_Protocol_Target.md`

## 1. Назначение и ограничения

Создать отдельную вкладку **Settings → Diagnostics**, где можно проверять реальную совместимость endpoint, создавать/редактировать сценарии сообщений и смотреть **точный запрос**, который ушёл модели, вместе с результатами. Диагностика использует существующие `LlmClient`, request serializer, parser, `ModelContextCompiler`, tracing и Inspector; **не создаёт второй AgentKernel, transport или event store**. По умолчанию только fake tools и синтетические данные, без изменений Office-документов. Разделить synthetic harness и реальные прогоны endpoint.

Текущий `Settings → Agent → Запустить тест` уже проверяет `ROLE_OK`, V8 `execute` для `compat.echo` и `RESULT_OK` в выбранной роли (см. `docs/model-endpoint-compatibility.md`). Это **не** доказывает сохранение структуры native tool calls, связь нескольких вызовов или поддержку `content:null`.

## 2. Интерфейс вкладки Diagnostics

Четыре подстраницы:

1. **Compatibility** — endpoint, model, response format, tool result role, streaming, кнопка запуска, таблица проверок. Статусы `Pass`, `Fail`, `Inconclusive`, `Unsupported`, `Not run`; отдельные flags: native generation, native history visibility, pairing, null content, JSON modes, fallback roles.
2. **Scenario Lab** — слева список встроенных/пользовательских сценариев, в центре **редактор сообщений** `developer/user/assistant/tool` с порядком, raw JSON/text и параметрами; справа assertions/ожидаемый ответ. Создание, копирование, импорт/экспорт сценария; генерация случайных маркеров; шаблоны multi-user/multi-run, read/patch/compaction. Встроенные тесты неизменяемы, пользователь редактирует копии.
3. **Request Inspector** — иерархия `message → blocks → content`, роли, порядок, run/step, T/E/R, coverage, provenance, bodyIncluded, omitted reason, retainedAt, токены и source owner. Переключатели `Readable Markdown/YAML`, `Raw messages JSON`, `Raw HTTP body`, `Diff requests`. Переход к event/resource/step. Отдельные вкладки `Resources`, `Evidence`, `Composition`.
4. **Results & Compare** — ответ модели, parser verdict, фактический marker, ожидаемое значение, связывание calls/results, latency/tokens, повторные прогоны, сравнение моделей/endpoint/форматов, redacted report.

### 2.1. Макет

```text
Settings > Diagnostics
┌──────────────────────────────────────────────────────────────────────┐
│ Endpoint [URL]  Model [name]  Format [json_object ▼]  [Run] [Compare] │
├───────────────┬────────────────────────────────┬─────────────────────┤
│ Scenarios     │ Messages / Request             │ Assertions / Result │
│ Role          │ 01 developer Agent Contract    │ Acceptance: PASS    │
│ Tool visibility│  ├─ Actions                   │ Visibility: PASS    │
│ Pairing       │   └─ References                │ Pairing: PASS       │
│ Null content  │ 02 developer Runtime Context   │ Native gen: N/A     │
│ Compaction    │   ├─ Environment               │ Expected: ...       │
│ Patch chain   │   ├─ Capabilities              │ Actual: ...         │
│ [+ Scenario]  │   └─ Loaded skills             │                     │
│               │ 03 user      Request #1        │                     │
│               │ 04 assistant T1 call           │                     │
│               │ 05 tool      T1/E1/R1@v1       │                     │
│               │ [Readable] [Raw] [Diff]        │                     │
└───────────────┴────────────────────────────────┴─────────────────────┘
```

Не создавать новый чат UI: расширить существующий Inspector, с раскрываемыми блоками и навигацией. Readable view **не заменяет** raw; raw должен быть сохранён после всех проекций/HTTP сериализации, а не восстановлен приблизительно из EventLog.

## 3. Обязательная матрица тестов

| ID | Сценарий | Что доказывает |
|---|---|---|
| A1 | Минимальный user/assistant | Transport, model response |
| A2 | Developer vs system | Приём и практическая видимость роли |
| A3 | V8 execute | JSON proposal без native generation |
| A4 | V8 finish criterion/outcome | Schema/parser/assessment |
| B1 | Native history `content:null` | Acceptance + видимость tool результата |
| B2 | Native history `content:""` | То же |
| B3 | Native history без `content` | То же |
| B4 | Маркер только в `role:tool` | Реальная видимость |
| B5 | Два вызова T1/T2 и разные результаты | Связывание вызова с результатом |
| B6 | Negative control: результат удалён | Исключить угадывание |
| B7 | Tool result vs user text | Различение источника |
| B8 | Native tool generation | Отдельная способность модели |
| B9 | Несколько user/run | История, последний запрос, причинность |
| C1 | `json_object` | Прямой V8 JSON |
| C2 | `json_schema` | Wrapper `{"response":...}` и строгая schema |
| C3 | Invalid schema / HTTP 500 | Диагностика причины, не ложный unsupported |
| C4 | SSE | Delta assembly и parity |
| C5 | Fallback `user` result | Видимость TOOL_RESULT без native pairing |
| C6 | Fallback `developer` result | Видимость и граница доверия |
| D1 | Read → identical read | Дедуп, retainedAt без dangling |
| D2 | Read → patch → read | Старое body не current |
| D3 | Read → patch → patch → read | Точная версия и coverage |
| D4 | Budget omission | Явная частичность/omission |
| D5 | Compaction → CURRENT_RESOURCE_VIEW | Exact provenance и единственное body |
| D6 | Skill + callable schema | Отсутствие дублей и актуальная загрузка |
| D7 | Finish evidence eligibility | Discovery без E, patch/test с E |
| D8 | Cross-run + case | Сквозные T/E/R и регистронезависимость |
| D9 | Failure → correction | Старые failed calls не блокируют finish |
| D10 | Unknown effect | No-replay/inspection safety |

### 3.1. Обязательный B5 с перемешанным порядком результатов

```json
[
  {"role":"user","content":"Назови marker результата diagnostic.read target=ModuleB и его tool_call_id."},
  {"role":"assistant","content":null,"tool_calls":[
    {"id":"T1","type":"function","function":{"name":"diagnostic.read","arguments":"{\"target\":\"ModuleA\"}"}},
    {"id":"T2","type":"function","function":{"name":"diagnostic.read","arguments":"{\"target\":\"ModuleB\"}"}}
  ]},
  {"role":"tool","tool_call_id":"T2","content":"{\"marker\":\"<random-B>\"}"},
  {"role":"tool","tool_call_id":"T1","content":"{\"marker\":\"<random-A>\"}"},
  {"role":"user","content":"Ответь JSON: {\"target\":\"ModuleB\",\"marker\":\"...\",\"tool_call_id\":\"...\"}."}
]
```

Проверять **точные** marker и T2 программно, а не спрашивать модель «видишь ли ты tools?». Генерировать новые случайные маркеры, менять порядок, повторять. Negative control без результата B должен давать «нет данных», а не случайный успех. **Pass доказывает операционную видимость/ассоциацию, но не гарантирует, что прокси не превратил структуру в текст.** Для этого нужны provider logs/docs.

### 3.2. JSON Schema/500

Отдельно выполнить: минимальную валидную schema, точную V8 schema с `response`, заведомо невалидную schema, `json_object`, обычный текстовый JSON без response_format (если клиент поддерживает). Сохранять status/body/время, без auth headers. HTTP 500 может означать ошибку прокси/сервера, не обязательно запрет JSON Schema. Fallback только по диагностированной политике, не по любой 500.

## 4. Инспектор: точные данные

**Messages:** номер, role, run, source, block names, raw content, token cost, включён/исключён.  
**Resources:** `R1@v1`, `R1@v2`, identity/coverage/currentness, где включено body (`T5` либо `CURRENT_RESOURCE_VIEW`), почему старый body исключён. Различать **known resource** и **body delivered**.  
**Evidence:** `T1 → E1/none`, tool, terminal outcome, eligibility, доставлено модели?, цитировано в finish?  
**Composition:** роли, токены, schemas, skills, compaction, omission, дубли, payload hash.  
**Diff:** запрос N → N+1: добавленные/удалённые сообщения, изменения bodies и views, сохранение причинности.  
**Raw:** точный HTTP JSON после проекции. Не выдавать отредактированную readable view за wire.

## 5. Редактор и генерация сценариев

- Добавление/удаление/перестановка `user`, `assistant`, `tool`, `developer`; JSON/text editor, автоматическая генерация валидной native call pair, возможность специально сломать pairing в negative test.
- Шаблоны multi-run, tool call batch, read/read, read/patch/read, multiple patch, compaction, skills, JSON modes.
- Рандомизированные маркеры и seed для воспроизводимости; запуск одного теста, серии, матрицы endpoints/models.
- Assertions — ожидаемый JSON/marker/pairing, HTTP acceptance, parser result, отсутствующие данные, omission/coverage; verdict `Pass`, `Fail`, `Inconclusive`, `Unsupported`, `Not run`.
- Экспорт/импорт сценариев и отчётов, локальная история прогонов; исключить секреты из экспорта, логирование чувствительных bodies по умолчанию выключено.
- Сценарии выполняются **без реальных Office tool effects**. Тестирование с настоящими документами — только отдельный квалификационный режим и явное подтверждение.

## 6. Существующие владельцы и этапы

Использовать существующие `LlmClient` request capture, `ModelRequestPreparation`, `ModelReferenceProjection`, `ModelContextCompiler`, `ContextCompactionRuntime`, `ModelContextInspectorService`, `PromptContextInspectorService`, `web/js/app-model-context.js`, `ConversationModelSession`, Workspace adapter, текущий Agent compatibility test. Один сценарный формат и assertion runner для headless и UI.

**D0 — Audit:** SHA, текущие тесты/inspector/transport, baseline exact requests.  
**D1 — Headless compatibility:** A/B/C на synthetic и real endpoint, включая `content:null`/empty/omitted. Не менять default transport до результата.  
**D2 — Context regression:** D1–D10, read/patch/compaction/skills/budgets, exact wire каждого шага.  
**D3 — Inspector:** message tree, blocks, resources/evidence/composition, raw/diff, navigation.  
**D4 — Scenario Lab:** editor, генератор, сравнение, отчёты, redaction.  
**D5 — Qualification:** Office/Workspace/CLI parity, Windows/WebView2, restart/confirmation, SSE, safety, latency/tokens.

## 7. Критерии приёмки

1. Каждый verdict имеет exact request/response и проверяемый assertion, не самооценку модели.
2. `content:null`, `""`, отсутствие `content` проверены раздельно по endpoint/model/config.
3. Native history visibility, pairing и native generation отображаются **отдельно**.
4. Инспектор показывает **все** developer/user/assistant/tool messages, синтетические блоки и exact raw payload.
5. Для read→patch→read видно, где единственное актуальное тело, что исключено и почему; нет dangling retainedAt и orphan tools.
6. Loaded skill и callable schema не дублируются в одном запросе.
7. Multi-run, compaction, evidence T/E/R и case-insensitive lookup проверены.
8. Нет доступа к секретам/опасным реальным tool effects из лаборатории.
9. Real endpoint qualification не подменяется synthetic harness.
