# Conversation v8.1 — аудит перед реализацией

Дата: 2026-10-23. Owner: Core ModelProtocol / AgentKernel; adapters — Office и
Workspace. База: `523ec66e2c3a6cf2b1829c51b669f3623e443523`.
Изучена пользовательская `RNAssistant_v8_1_implementation_spec.md` от 2026-10-22;
расширение A–I имеет приоритет над её краткой частью.

Из-за параллельных Resource Context правок в основном каталоге итоговое evidence
получено повторно на изолированном `git archive` указанного HEAD. Поверх добавлены
только audit fixture и его CLI route; hashes overlay и собранного Harness сохранены
в manifest. Чужие незавершённые изменения в эту проверку не входят.

**Фаза 0: путь преждевременного завершения воспроизведён host-neutral.** Добавлены
красная регрессия и [ADR-0016](../../decisions/ADR-0016-conversation-v8-admission.md).
На момент аудита production оставался на v7; исправление и фазы 1–5 ещё не были выполнены.
Это не доказательство поведения реальной модели или исходного инцидента DeepSeek.
После cutover этот аудит становится историей; текущий контракт остаётся в
[Conversation protocol](../../conversation-protocol.md), статус — в
[PROGRESS](../PROGRESS.md), риск — в [RISK_REGISTER](../RISK_REGISTER.md#premature-terminal-narration--2026-10-13).

Статус в кандидате v8: исходный красный route и его v7-файл удалены после
замены [зелёной fake-Excel HTTP-проверкой](../../../tests/RNAssistant.V8KernelSmoke/run.sh).
Ниже сохранено историческое описание запуска на указанной базе и evidence;
команда больше не существует в текущем дереве.

## Воспроизведение

Историческая команда использовала production `LlmClient` и HTTP loopback, production
`ConversationRunService`/kernel/store, `FakeOfficeAdapter` для Excel. Ни Office/COM,
ни внешний endpoint, ни пользовательские settings/данные не используются.
Output должен быть новым или пустым каталогом:

```sh
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- \
  --conversation-v8-audit artifacts/conversation-v8-repro
```

Сценарий: «Создай лист Report, заполни A1:B4 данными и построй по ним график».
HTTP endpoint возвращает последовательно schema read, `excel.add_sheet`, затем:

```json
{"message":"Лист Report создан. Теперь заполню данные и построю график.","action":"done","tool_calls":[]}
```

Получено 3 HTTP запроса, 2 принятых вызова с runtime ID и model-attempt origin,
1 подтверждённое создание листа, 0 записей диапазона и 0 вызовов графика.
Локальный parser принимает последний ответ; format repair отсутствует.
Kernel сохраняет `Completed / Clean / model_done`, UI получает
`completed / clean / GoalVerification=not_assessed`. Реальная функция
`app-run-view-state.js.outcomeLabel` возвращает **«Ответ завершён»**.
Перезагрузка ChatStore сохраняет terminal reason и ту же UI-проекцию.

Регрессия завершается **exit 1** с точным сообщением
`FAIL conversation v8 admission: terminal response without results/evidence ended a run after accepted calls`.
Это ожидаемый красный результат отсутствующего admission gate, не пройденный тест.
Ошибки подготовки/предусловий имеют **exit 2**, их нельзя принимать за воспроизведение.
Сценарий намеренно запускается отдельно от обычного зелёного набора. При cutover
его v7 ответы заменяются формами v8 и проверками reject → continuation без повтора записи.

Сохранённые материалы в [evidence](evidence/manifest.json):

| Артефакт | Что зафиксировано |
|---|---|
| `http-01..03.request.json` / `*.response.json` | Точные UTF-8 HTTP body bytes; только синтетический сценарий |
| `http.json` | Метод, путь, код и размеры обменов; без authentication headers |
| `events.export.jsonl.gz` | Экспорт декодированных событий с sequence/hash и typed operations |
| `trace.json` | Parser verdict, accepted origins, kernel summary, read-back counts, UI DTO |
| `progress.json`, `ui-label.json` | Application progress и выполнение функции UI в Node VM |
| `manifest.json` | HEAD, hashes исходников/артефактов, команды и результаты проверок |

Экспорт событий содержит CAS references; полный CAS не перенесён. Это evidence
аудита, а не самостоятельный backup для восстановления чата. Точные HTTP bodies
сохранены отдельно. Проверка replay выполнена на исходном временном ChatStore до
его удаления. Событие `model.response.accepted` — диагностический verdict parser,
а не выдача execution authority; durable acceptance подтверждают commit operations.

## Причина

1. `ConversationResponseJson.Read` и `ConversationResponseParser.Parse` проверяют
   закрытую форму v7 и аргументы. `done` с пустыми calls корректен. Текст обещания
   намеренно не классифицируется.
2. `ConversationKernelAdapter.SendAsync` передаёт этот `Action` в
   `AgentModelResult.Accepted`. Office не имеет workspace acceptance contract.
3. `AgentKernel.LoopAsync` сохраняет `ResponseAccepted`; `response.Final` сразу
   приводит к `FinishAsync(Completed, "model_done", message)`. Число принятых calls
   само по себе не требует предъявить результаты.
4. `AppendTerminalMessage`/`ConversationRunProjection` сохраняют ответ и runtime
   lifecycle. UI корректно показывает завершение ответа; независимого доказательства
   полноты пользовательской задачи нет. Исправлять текстовую метку как первопричину
   или выводить success из `clean` неверно.

Существующие unknown-effect guards и Workspace `RejectCompletion` не следует
заменять. Prompt уже запрещает обещание вместо результата, но валидный JSON
проходит без repair. Наличие валидного evidence в v8 также не докажет, что график
создан: модель может сослаться только на лист. Это отдельное ограничение, не повод
добавлять эвристический анализ фразы «создай график».

## Карта владельцев и границ

Пути относительно `src/`; перечислены именно затрагиваемые границы, не весь проект.

| Owner / место | Вход → выход и state mutation | Replay boundary / минимальные проверки |
|---|---|---|
| `Core/ModelProtocol/ModelProtocolWire`, `ConversationResponse*` | JSON + captured catalog → validated response/schema/canonical JSON; без эффектов | version/history preflight; parser/schema/serializer parity |
| `Core/ModelProtocol/ModelProtocolClient` | frozen request → accepted proposal или typed failure; отдельные provider retry, format repair, явный json_object fallback | request/response/accept/reject trace; `model protocol:` |
| `Core/Agent/AgentKernel`, `AgentConversation`, `AgentRunContracts` | proposals + terminal records → accepted IDs, lifecycle/counts, append facts | continuation/current run; `kernel: completion rejection`, no-replay/limits |
| `Core/Persistence/AgentRunState`, `ConversationRunCommit` | kernel facts → typed operations в существующем stream | append-before-dispatch, `run.updated`; checkpoint/replay tests |
| `Office/Services/ConversationRunService`, `ConversationKernelAdapter.*` | document-bound input → shared kernel, prompt, dispatch/read-back, persisted UI projection | confirmation/steer; `agent:`, `chat sessions:` |
| `Application/Workspace/WorkspaceConversationService.WorkspacePorts` | workspace authority/context → тот же kernel; refresh acceptance перед done | `RejectCompletion`, persisted file/browser assessment; Workspace acceptance tests |
| CLI | команды → Workspace application service, settings/result roles | process scenarios; не добавлять второй loop |
| `Office/Services/ConversationPromptComposer`, `Core/Models/AppSettings` | defaults/custom prompts + runtime state → frozen request | migration сохраняет recovery copy; guidance owner — `ModelProtocolWire` |
| `Core/Services/ModelContextCompiler`, `ContextCompactionRuntime` | immutable history + authority → bounded context/`ContextReceipt` | folded results/CAS, provenance; три tool-result roles |
| `Core/Models/ResourceEvidenceModels`, `Core/ModelProtocol/ToolResultWire` | resource observations / canonical terminal outcome → раздельные read/effect и transport facts | currentness не равна outcome; exact IDs не становятся authority |
| `Core/Services/TaskListService`, `PlanDocumentService`, question tools | явные tool commands → revisions/questions | reflection не вызывает их автоматически; существующие domain tests |
| `Office/Services/ModelCompatibilityService`, `Core/Tools/ConversationMessageStreamExtractor` | probe/SSE → проверка wire, извлечение message | union/wrapper capability и message-last требуют новых probes/tests |
| `Core/Services/RunViewStateProjector`, `Office/...Adapter.Store`, `web/js/app-run-view-state.js` | immutable runtime facts → UI status | `run view:`; пустые reflection messages, partial/unverified, без дублей |

### Существенные находки для реализации

- `RunViewState.GoalVerification=not_assessed` уже существует. Нельзя создавать
  вторую систему task success или объявлять `assessment=satisfied` verified goal.
- `State.AcceptedIds` пуст для нового run и восстанавливается для continuation.
  Нельзя считать все calls из `PreviousMessages`: там бывает прошлый run.
- `AgentRunContinuation.RestoreCheckpoint` сейчас отвергает tail с assistant
  response. Reflection потребует явного разрешения именно этой формы и сохранения
  её origin/budget; общего разрешения narrative checkpoints быть не должно.
- `ConversationResponseHistoryReader` запрещает no-call progress и call-origin
  metadata у terminal ответов. `reason`/`decision`/`results` и response-attempt origin
  необходимо сохранять независимо от `AcceptedToolCallOrigin`.
- Office публикует исходный `tool.execution.finished` и затем materialized
  `tool.result.recorded`; Workspace сохраняет terminal result с typed
  `ExecutionProgress` непосредственно из `fact.Execution`. Resolver обязан учитывать
  эти существующие границы, а не считать любую строку TOOL_RESULT durable evidence.
- `ResourceEvidence.EvidenceId` и Inspector `E` aliases обозначают наблюдения,
  а не уникальные terminal outcomes вызовов. Для finish предлагается существующий
  `tool_call_id` с обязательным разрешением terminal record, без нового E-store.

## Проверки и следующий срез

- HTTP regression: ожидаемый red / exit 1; все предшествующие проверки фактов и
  reload выполнены. Exact output — [manifest](evidence/manifest.json).
- Существующие `kernel: completion rejection`: **3/3**.
- UI label из сохранённого DTO: Node VM, **pass**; браузер не запускался.
- Real-model, strict endpoint support, streaming v8, Windows/Office/WebView2,
  restart после reflection и cross-surface v8 ещё **не проверены**.

Следующий срез — фаза 1 по ADR: логические формы, typed ошибки, schema/parser/
serializer/repair parity и endpoint probes. Финальное включение требует совместного
cutover всех consumers и real-model сравнения. Production-код в фазе 0 не менялся.
