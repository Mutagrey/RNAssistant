# AgentDecision v1

RNAssistant использует локальный agent harness поверх OpenAI-compatible Chat Completions. Модель выбирает следующее решение, но не исполняет Office tools удалённо: каталог, проверка аргументов, подтверждения, выполнение, наблюдения и верификация остаются в локальном runtime.

## Цикл выполнения

1. Детерминированный router определяет `Chat` или `Agent`, тип задачи, риск и необходимость чтения/мутации.
2. Catalog slicer выбирает ограниченный набор доступных tools. В prompt попадают только их id, описание, safety metadata и JSON Schema аргументов.
3. Runtime собирает сообщения из инструкции, запроса, актуального chat context, выбранных skills, route и нормализованных observations.
4. Модель возвращает одно решение `AgentDecision v1` либо один native `tool_call`.
5. Runtime строго проверяет решение, tool id и аргументы, применяет confirmation/safety policy и вызывает локальный `OfficeToolExecutor`.
6. Результат нормализуется и добавляется в рабочий контекст. Следующий запрос решает, нужен ли ещё один tool, уточнение или финальный ответ.
7. После мутации runtime запускает отдельную read-only verification. Старое наблюдение не считается проверкой нового изменения.

История протокола живёт только внутри текущего run. В постоянную chat history не возвращаются скрытые route diagnostics, полный prompt и provider reasoning.

## Формат решения

Ответ содержит ровно один JSON object без Markdown, code fence и внешнего текста. Все семь полей обязательны; неактивные поля равны `null`.

```json
{
  "protocolVersion": 1,
  "kind": "tool",
  "decisionSummary": "Читаю таблицу перед изменением.",
  "goal": null,
  "plan": null,
  "tool": {
    "toolId": "excel.read_range",
    "arguments": {
      "sheet": "Data",
      "address": "A1:D20"
    }
  },
  "message": null
}
```

Поддерживаются пять `kind`:

- `plan` — один необязательный видимый план сложной задачи. Требует `goal` и непустой `plan`; `tool` и `message` равны `null`. План сам ничего не исполняет, после него модель должна вернуть следующее решение.
- `tool` — ровно один вызов. Требует `tool.toolId` и object `tool.arguments`; `goal`, `plan` и `message` равны `null`.
- `clarify` — требуется ответ пользователя. Только `message` содержит вопрос.
- `final` — задача завершена. Только `message` содержит пользовательский ответ.
- `cannot_complete` — продолжение невозможно. Только `message` объясняет конкретное ограничение.

Пример видимого плана:

```json
{
  "protocolVersion": 1,
  "kind": "plan",
  "decisionSummary": "Сначала проверю данные, затем обновлю и верифицирую результат.",
  "goal": "Обновить итоговую таблицу",
  "plan": [
    { "id": "inspect", "title": "Проверить исходный диапазон" },
    { "id": "update", "title": "Внести изменения" },
    { "id": "verify", "title": "Проверить итог" }
  ],
  "tool": null,
  "message": null
}
```

`decisionSummary` — короткое объяснение наблюдаемого действия, а не chain-of-thought. Детальные рассуждения не входят в протокол. Если provider отдельно возвращает `reasoning_content`, runtime показывает его как необязательные transport metadata и не смешивает с JSON решения.

## Режимы API

| Режим | Что отправляется | Когда использовать |
| --- | --- | --- |
| `json_schema` | `response_format.type=json_schema` со строгой динамической схемой `AgentDecision v1`; поле `tools` не отправляется | Режим по умолчанию. Лучший контроль формы ответа, если endpoint поддерживает Structured Outputs. |
| `json_object` | `response_format.type=json_object`; протокол и доступные tools описаны в prompt | Самая переносимая связка для локальных моделей. Семантику всё равно проверяет локальный parser. |
| `native_tool_calls` | Строгий `json_schema` для нетуловых решений плюс OpenAI `tools`, `tool_choice=auto`, `parallel_tool_calls=false` | Для endpoint, который корректно поддерживает OpenAI function calling и совместную работу `tools` с `response_format`. |

При ошибке `json_schema` до первого выполненного tool runtime один раз переключает текущий run на `json_object`. После начала выполнения fallback запрещён: повтор запроса в другом режиме может дублировать мутацию. Для `native_tool_calls` автоматического fallback нет, потому что неизвестно, принял ли endpoint вызов и как он сериализует историю.

Даже Structured Outputs не заменяет локальную проверку. Runtime повторно проверяет точный набор полей, семантику `kind`, наличие tool в текущем slice и аргументы по его schema.

## Роли сообщений

Роль общей Chat/Agent инструкции выбирается в Settings: `developer` (по умолчанию), `system` или `user`. При `user` инструкция объединяется с текущим пользовательским контекстом; при `developer`/`system` отправляется отдельным сообщением.

Роль результата tool выбирается независимо:

- `tool` (по умолчанию) — runtime добавляет совместимую пару: assistant message с одним `tool_calls[]`, затем `role: tool` с тем же `tool_call_id`.
- `developer` или `user` — runtime добавляет `TOOL_RESULT:` и нормализованный JSON обычным сообщением выбранной роли. Это fallback для endpoint, который понимает JSON output, но не принимает tool-call history.

Та же пара/роль используется после ручного confirmation; подтверждённый результат не деградирует в отдельный неструктурированный prompt.

В `json_schema`/`json_object` режиме assistant `tool_calls` для обратной передачи формируется локально из уже проверенного `kind=tool`. В native режиме сохраняются имя и аргументы ответа endpoint, а отсутствующий или нестабильный call id нормализуется так, чтобы assistant call и `role: tool` всегда совпадали.

Нормализованный результат:

```json
{
  "protocolVersion": 1,
  "callId": "call_abc123",
  "toolId": "excel.read_range",
  "ok": true,
  "status": "completed",
  "summary": "Range read.",
  "data": { "values": [["A", 1]] },
  "error": null
}
```

Крупное `data` ограничивается в replay-контексте. Вместо полного payload runtime передаёт `{truncated, originalChars, preview}`; для планирования остаётся отдельное bounded observation. Полный локальный ToolResult при этом не изменяется.

## Tool schemas

Custom tools обязаны хранить формальный JSON Schema с `type: "object"` и `properties`. Любая другая форма получает `invalid_tool_schema` и не выполняется. Краткие описания встроенных tools разворачиваются в формальную схему один раз при создании определения.

Для Structured Outputs и native strict tools object schemas закрываются через `additionalProperties: false`, а их свойства объявляются required на API-уровне. Опциональные VBA параметры поэтому должны иметь `default`; runtime применяет default перед вызовом.

## Ограничения и слабые места

- Реализован Chat Completions, а не provider-specific Assistants/Responses state.
- Совместимость `developer`, `role: tool`, strict JSON Schema и сочетания `tools + response_format` различается у локальных OpenAI-compatible серверов; режимы нужно проверять отдельно для каждого endpoint.
- Часть серверов поддерживает только подмножество JSON Schema (`anyOf`, `const` и nullable types могут быть проблемой). Для них нужен `json_object`.
- `json_object` гарантирует только JSON на стороне API; точность `kind` и аргументов зависит от prompt following и локальной валидации.
- Выполняется один внешний tool call за model turn. Это делает подтверждения, наблюдения и восстановление однозначными, но увеличивает число запросов.
- Runtime показывает цель, план, текущие действия, observations и verification как собственный transcript. Это наблюдаемое состояние, не скрытая цепочка рассуждений модели.
