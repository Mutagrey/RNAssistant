# AgentDecision v1

RNAssistant использует локальный agent harness поверх OpenAI-compatible Chat Completions. Модель выбирает следующее решение, но не исполняет Office tools удалённо: каталог, проверка аргументов, подтверждения, выполнение, наблюдения и верификация остаются в локальном runtime.

## Цикл выполнения

1. Chat session хранит явный режим `Agent` или `Chat`; новый chat создаётся в `Agent`. HTML workspace и продолжение незавершённой agent-задачи всегда используют Agent.
2. В Agent mode детерминированный router определяет тип задачи, риск и необходимость чтения/мутации. Agent способен дать обычный ответ без tools, если route не требует действия Office.
3. Catalog slicer выбирает ограниченный набор доступных tools. В prompt попадают только их id, описание, safety metadata и JSON Schema аргументов.
4. Runtime собирает сообщения из инструкции, запроса, актуального chat context, выбранных skills, route и нормализованных observations.
5. Модель возвращает одно решение `AgentDecision v1` либо один native `tool_call`.
6. Runtime нормализует только безопасные вариации формы, затем строго проверяет выбранное действие, один tool, tool id и аргументы, применяет confirmation/safety policy и вызывает локальный `OfficeToolExecutor`.
7. Результат нормализуется и добавляется в рабочий контекст. Следующий запрос решает, нужен ли ещё один tool, уточнение или финальный ответ.
8. После мутации runtime запускает отдельную read-only verification. Старое наблюдение не считается проверкой нового изменения.

История протокола живёт только внутри текущего run. В постоянную chat history не возвращаются скрытые route diagnostics, полный prompt и provider reasoning.

## Формат решения

Канонический ответ содержит ровно один JSON object без Markdown, code fence и внешнего текста. Рекомендуется всегда передавать семь полей; неактивные поля равны `null`.

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

- `plan` — видимый план сложной задачи. Требует непустой `plan`; `goal` рекомендуется. План сам ничего не исполняет, после него модель должна вернуть следующее решение.
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

`decisionSummary` — видимое сообщение модели перед выбранным действием, а не chain-of-thought. Для tool turn оно кратко фиксирует уже подтвержденный результат и следующее действие. В `native_tool_calls` тот же текст передается через assistant content и сохраняется runtime как `decisionSummary`. Детальные рассуждения не входят в протокол. Если provider отдельно возвращает `reasoning_content`, runtime показывает его как необязательные transport metadata и не смешивает с JSON решения.

Шаги плана в канонической форме содержат только `id` и `title`. Если новые observations меняют оставшуюся работу, модель может снова вернуть `kind=plan`: runtime сохраняет выполненные шаги с теми же стабильными id и заменяет незавершённую часть. Статусы `pending`, `running`, `waiting`, `completed`, `failed`, `cancelled` принадлежат локальному harness. Цель и завершённый план остаются видны в transcript после финального ответа.

## Совместимая нормализация

Локальный parser не делает произвольный «ремонт JSON», но принимает безопасные отклонения слабых моделей:

- отсутствующие неактивные поля и `protocolVersion` (подставляется v1);
- отсутствующий `decisionSummary`, если его можно безопасно сформировать из terminal message, goal или tool id;
- плановые `action`/`description`/`text` как алиасы `title`, `expected` и model status как неисполняемые metadata, строковые шаги и сгенерированные id;
- `id`/`name` и `args` как алиасы внутри единственного tool object;
- advisory `goal`/`plan` вместе с tool или terminal decision;
- одиночный legacy `action: {type: "reply", content: "..."}` и одиночный `toolCalls`; псевдо-tool `answer` преобразуется только в terminal text.

Surrounding prose, fences, неизвестные root-поля, конфликтующие действия и несколько/parallel tools по-прежнему отклоняются. Любой нормализованный tool всё равно обязан существовать в текущем slice и пройти formal JSON Schema аргументов. Поэтому совместимость не расширяет полномочия модели и не обходит local safety policy.

## Режимы API

| Режим | Что отправляется | Когда использовать |
| --- | --- | --- |
| `json_schema` | `response_format.type=json_schema` со строгой динамической схемой `AgentDecision v1`; поле `tools` не отправляется | Режим по умолчанию. Лучший контроль формы ответа, если endpoint поддерживает Structured Outputs. |
| `json_object` | `response_format.type=json_object`; протокол и доступные tools описаны в prompt | Самая переносимая связка для локальных моделей. Семантику всё равно проверяет локальный parser. |
| `native_tool_calls` | Строгий `json_schema` для нетуловых решений плюс OpenAI `tools`, `tool_choice=auto`, `parallel_tool_calls=false` | Для endpoint, который корректно поддерживает OpenAI function calling и совместную работу `tools` с `response_format`. |

При ошибке `json_schema` до первого выполненного tool runtime один раз переключает текущий run на `json_object`. После начала выполнения fallback запрещён: повтор запроса в другом режиме может дублировать мутацию. Для `native_tool_calls` автоматического fallback нет, потому что неизвестно, принял ли endpoint вызов и как он сериализует историю.

Даже Structured Outputs не заменяет локальную проверку. Runtime проверяет семантику `kind`, единственность действия, наличие tool в текущем slice и аргументы по его schema.

`json_object` не использует ручной поиск JSON в тексте: весь `message.content` обязан быть одним object. Fences, префиксы и частичный JSON не восстанавливаются; внутри целого object применяется только описанная выше совместимая нормализация.

Если content или native tool call не проходит parser, runtime повторяет текущий model turn до `MaxAgentFormatRetries` раз (по умолчанию 2, допустимо 1–5). Каждый retry строится заново из исходного чистого prompt и одного `RepairDecisionPrompt` с кодом validation error. Отклонённый raw-ответ не попадает в replay. Из него отдельно извлекаются только безопасные `decisionSummary` и `goal`: они видны пользователю в diagnostic activity, но не выполняются и не добавляются в model context. После исчерпания лимита последний ответ сохраняется как terminal diagnostic и run останавливается без выполнения непроверенного действия.

## Роли сообщений

Роль общей Chat/Agent инструкции выбирается в Settings: `developer` (по умолчанию), `system` или `user`. При `user` инструкция объединяется с текущим пользовательским контекстом; при `developer`/`system` отправляется отдельным сообщением.

Роль результата tool выбирается независимо:

- `tool` (по умолчанию) — runtime добавляет совместимую пару: assistant message с одним `tool_calls[]`, затем `role: tool` с тем же `tool_call_id`.
- `developer` или `user` — runtime добавляет `TOOL_RESULT:` и нормализованный JSON обычным сообщением выбранной роли. Это fallback для endpoint, который понимает JSON output, но не принимает tool-call history.

Та же пара/роль используется после ручного confirmation; подтверждённый результат не деградирует в отдельный неструктурированный prompt.

Кнопка «Запустить тест» в Settings → Agent сохраняет текущие настройки и без Office-мутаций проверяет `user`, `system`, `developer`, выбранную роль результата tool, `json_object`, `json_schema` и сочетание native tools + schema. Общий статус учитывает обязательными только режимы текущей конфигурации; остальные проверки показывают доступные варианты endpoint.

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

## Prompt ownership

Все изменяемые model-facing инструкции находятся в Settings:

- `SystemPrompt` — единственный главный prompt Agent: AgentDecision, transports, контекст, tools, skills и правила self-improvement;
- `ChatSystemPrompt` — обычный текстовый Chat без tools;
- `ForceToolUsePrompt`, `RepairDecisionPrompt`, `PlanContinuationPrompt` — короткие переходы runtime;
- `ChatTitlePrompt` — отдельная инструкция генератора названий.

`ROUTE`, `CURRENT_OFFICE_CONTEXT`, `AVAILABLE_TOOLS`, `OBSERVATIONS` и `RELEVANT_SKILLS` — динамические данные runtime, а не скрытые prompt-шаблоны. Их формат не редактируется как инструкция.

Промпты редактируются во вкладке Prompts. Агент может вызвать `common.prompts_read`, `common.prompts_read_defaults` и подтверждаемый `common.prompts_save`. Встроенный `common.prompt_authoring` требует сохранять поля AgentDecision, one-tool invariant, confirmation и verification. При загрузке известные устаревшие defaults и prompts с несовместимыми маркерами `rnassistant-agent`, `rnassistant-skill`, `tool_plan` или `cannot_do` обновляются до текущего протокола; остальные пользовательские prompts сохраняются. Skills и tools изменяются через отдельные CRUD tools; встроенные id нельзя перекрыть custom-объектом.

## Сборка и бюджет контекста

Agent request собирается заново на каждом model turn в таком порядке: instruction role; непрерывный хвост обычной user/assistant chat history; текущий `USER_REQUEST` и динамические секции; ограниченный run-local protocol replay. Activity, diagnostics, сообщения с `ExcludeFromModelContext=true` и provider reasoning в replay не входят. Tool exchange передаётся парой assistant `tool_calls` + `role: tool` либо выбранной обычной ролью. Нормализованные `OBSERVATIONS` дополнительно дают модели компактное состояние текущего run.

Окно берётся из ручного override, capability активной модели или консервативного default `32768`. Runtime резервирует 2% окна (минимум 1024, максимум 16384) и запрошенный output. В оценку запроса входят сообщения, вложения, `response_format` schema и native tool schemas. Tool catalog уменьшается, если его prompt/API-представления занимают больше половины input budget; минимум один необходимый tool сохраняется.

История выбирается только назад от самых новых сообщений: если очередное сообщение не помещается, более старые не возвращаются. При включённом compression ранний хвост заменяется локальным bounded summary. Notes дедуплицируются по reference, skill bodies, observations, tool results и extracted attachment text ограничиваются. Бинарные image/audio отправляются только для текущего turn и не считаются частью будущей истории. При наличии provider `usage.prompt_tokens` UI показывает фактическое значение последнего запроса; до запроса и после перезагрузки показывает оценку сохранённой history/context без ещё неизвестных route и tool schemas.

Оценка намеренно provider-neutral: UTF-8 text примерно `bytes/3`, image — 4096 tokens, audio — `bytes/512`, extracted text — консервативно `chars/2`. Она не заменяет tokenizer конкретной модели. Если обязательные текущие данные всё равно переполняют окно, runtime уменьшает output, а при полном переполнении завершает запрос ошибкой вместо скрытого удаления текущей инструкции или запроса пользователя.

## Tool schemas

Custom tools обязаны хранить формальный JSON Schema с `type: "object"` и `properties`. Любая другая форма получает `invalid_tool_schema` и не выполняется. Краткие описания встроенных tools разворачиваются в формальную схему один раз при создании определения.

Для Structured Outputs и native strict tools object schemas закрываются через `additionalProperties: false`, а их свойства объявляются required на API-уровне. Опциональные VBA параметры поэтому должны иметь `default`; runtime применяет default перед вызовом.

## Ограничения и слабые места

- Реализован Chat Completions, а не provider-specific Assistants/Responses state.
- Совместимость `developer`, `role: tool`, strict JSON Schema и сочетания `tools + response_format` различается у локальных OpenAI-compatible серверов; режимы нужно проверять отдельно для каждого endpoint.
- Часть серверов поддерживает только подмножество JSON Schema (`anyOf`, `const` и nullable types могут быть проблемой). Для них нужен `json_object`.
- `json_object` гарантирует только JSON на стороне API; точность `kind` и аргументов зависит от prompt following и локальной валидации.
- Оценка токенов приблизительна для локальных моделей; корректные capability metadata и provider usage улучшают показания UI.
- Выполняется один внешний tool call за model turn. Это делает подтверждения, наблюдения и восстановление однозначными, но увеличивает число запросов.
- Runtime показывает цель, план, текущие действия, observations и verification как собственный transcript. Это наблюдаемое состояние, не скрытая цепочка рассуждений модели.
