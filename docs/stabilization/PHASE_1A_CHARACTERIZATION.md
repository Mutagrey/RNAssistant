# Phase 1A — Characterization

Исследован baseline `10e52bf` на `stabilization/16.1`.
Изменены только tests и документы; runtime, UI, protocol v2 и persistence не менялись.
Требования: [master plan, Phase 1A](STABILIZATION_MASTER_PLAN.md#1a-characterization).

## Что доказывают tests

Зелёные characterization tests фиксируют **текущее поведение, включая дефекты**.
Они не закрывают safety gates и не разрешают сохранять false completion в целевой архитектуре.
В Phase 1C нужно в тех же сценариях добавить/заменить assertions на runtime-owned
execution health: сначала получить красный regression test на текущем runtime,
затем зелёный после guard. Definition of Done всей Phase 1 пока не выполнен.

Все tests находятся в `tests/RNAssistant.Harness/Program.SimpleAgentTests.cs),
зарегистрированы в `Program.cs` и используют существующие fake LLM/Office fixtures.

| Требование 1A | Test method | Наблюдаемое поведение | Целевое отличие |
|---|---|---|---|
| completed после write error | `SimpleAgentCharacterizesCompletedAfterWriteError` | `excel.add_sheet` возвращает error, лист отсутствует, модель видела `ok:false`, но run/message остаются completed | Runtime health errors; UI не подтверждает mutation |
| completed после write unknown | `SimpleAgentCharacterizesCompletedAfterWriteUnknown` | Fake VBA write меняет source на третье состояние; настоящий executor/read-back/journal возвращает `vba_mutation_unknown`, но run/message completed | Runtime health unknown имеет приоритет |
| completed без write call | `SimpleAgentCharacterizesCompletedWithoutWrite` | Нет calls/effect, но текст «Лист Report создан» и completed приняты | Обычный ответ с нулём подтверждённых writes, без анализа текста |
| write ok + final message | `SimpleAgentExecutesToolAndReceivesJsonResult` | Один fake write действительно создаёт лист; final/status сохранены | Сохранить нормальный сценарий; Office read-back qualification отдельно |
| valid response на попытке 20 | `SimpleAgentRepairsOnTwentiethAttempt` | 19 protection-like ответов, затем один accepted response; tools не исполнялись | Сохранить stateless repair и accepted history isolation |
| Все 20 ответов invalid | `SimpleAgentFailedRepairDoesNotPolluteContext` | При 19 разрешённых retries: 20 запросов, run failed, ноль tools, diagnostic исключён из replay | Не допускать 21-й запрос при целевом лимите 20 attempts |
| Rejected attempts вне accepted history | Оба repair tests выше | Нет rejected content/reasoning в history и следующих prompts; каждый repair содержит один текущий repair instruction | Сохранить; rejected trace остаётся отдельным log-only evidence |

Дополнительный `SimpleAgentClampsFormatRepairLimit` фиксирует текущую семантику:
`MaxAgentFormatRetries=99` ограничивается до **20 retries + initial request = 21 request**.
Это расхождение с двадцатью attempts из master plan (R20), а не исправленный лимит.
Установка 19 retries в тесте «все 20 invalid» сделана явно только в fixture.

Unknown-сценарий не подменяет результат готовым `ToolResult`: используется существующий
`FakeOfficeAdapter.VbaWriteTransform`, затем обычные `common.vba_write_module`,
read-back и `VbaJournalStore`. Проверяются durable terminal `unknown`,
`Retryable=false`, один backend write и расхождение с intended source.
Это host-neutral проверка, не запуск COM/VBE.

## Путь model status

Пути ниже проверены чтением кода на указанном baseline. Controller/bridge/UI не
исполнялись в интеграции; тестами исполнен сам ConversationRunService и tool path.

| Граница | Файл / функция | Передача статуса сейчас |
|---|---|---|
| Parsed response → final | `src/RNAssistant.Office/Services/ConversationRunService.cs`, `RunLoopAsync` | При пустом tool_calls вызывает `Result(..., response.Status, response.Status)`; предыдущие ToolResults не участвуют в выборе terminal status |
| Final → ChatTurnResult | Тот же файл, `Result` | Model status копируется в `ResponseStatus` и `RunStatus`; protocol version = 2 |
| Final → accepted message | `src/RNAssistant.Office/Agent/AgentTranscript.cs`, `CreateAssistantMessage` | `ChatMessage.ResponseStatus` и `ResponseProtocolVersion` сохраняют accepted model status |
| Tool → отдельное evidence | Тот же файл, `DescribeResult` / `CreateToolActivity` | success/status/errorCode/retryable/data сохраняются отдельно и не меняют final model status |
| ChatTurnResult → LastRun | `src/RNAssistant.Office/Controller/AssistantController.ChatExecution.cs`, `ApplyTerminalRunResult` | `completion.RunStatus` → `LastRun.Status/Phase`, assistant text → CurrentAction |
| Обычный send | Тот же файл, `ExecuteChatTurnAsync` | ApplyTerminalRunResult, SaveSessionChanges, затем CreateSendChatResponse |
| Confirmed continuation | `src/RNAssistant.Office/Controller/AssistantController.Agent.cs`, `ConfirmAgentTool` | ContinueAfterToolAsync → тот же ApplyTerminalRunResult → SaveSessionChanges; возвращает ChatState с messages |
| Controller → bridge DTO | `AssistantController.ChatExecution.cs`, `CreateSendChatResponse`; `src/RNAssistant.Office/Contracts/BridgeDtos.cs`, `SendChatResponse` | `completion.ResponseStatus` → JSON `responseStatus`; messages содержат отдельный model status, ToolResults — факты tools |
| Clone / replay | `src/RNAssistant.Office/Services/ChatCloneService.cs`; `src/RNAssistant.Core/Storage/ChatStore.SessionProjection.cs` | Clone сохраняет message status и LastRun; typed run operation сохраняет/восстанавливает LastRun, без переоценки outcome |
| LastRun → events/header | `src/RNAssistant.Core/Storage/ChatStore.TraceLifecycle.cs`; `ChatHeaderReducer.cs` | `LastRun.Status` → turn.ended.Status и header RunStatus |
| Bridge → UI state | `web/js/app-chat-run.js`, send/confirm; `app-chat-state.js`, `applyChatState` | response.messages → state.messages; итог читается из message, не из отдельного responseStatus DTO |
| Message → UI aggregate | `web/js/app-utils.js`, `messageResponseStatus`; `app-agent.js`, `renderAgentRunArticle` | Читает status только для protocol v2 и передаёт его в agentRunStats |
| UI label / style | `web/js/app-agent-model.js`, `agentRunStats` / `conversationOutcomeLabel` | Finished run использует declaredStatus независимо от counts ошибок; completed → «Готово» |

Ветки runtime failure/cancel/waiting confirmation остаются отдельными и не являются
копиями model terminal status. Их наличие не защищает от описанного final-after-error path.
Top-level `responseStatus` и `messages[].ResponseStatus` — два consumers одной
модельной декларации, не два независимых подтверждения эффекта.

## Current-to-target map

Детальная карта девяти требуемых контуров, owners/consumers и removal gates:
[MIGRATION_MAP.md](MIGRATION_MAP.md). Новые adapters и второй runtime не введены.

## Verification

```sh
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- characterization
```

Результат: 7/7 pass; весь linked host-neutral source set скомпилирован.
Baseline `agent: explicit response status` до изменений: 1/1 pass.
Полный harness не запускался; production behavior не менялся.
Windows x64 + Office x64 + VS 2022 / VSTO / COM: **not performed**.

## Следующие границы

- Phase 1B: causal trace, отдельное изменение.
- Phase 1C: runtime completion guard / execution health и red→green safety tests.
- Phase 2: явная семантика общего лимита model attempts (R20).
- Product version остаётся `16.1.0-dev`; tag не создаётся.
