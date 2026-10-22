# Resource context: аудит текущего состояния

> Ниже сохранён исходный срез Wave 0. Полная сверка исходного предложения с кодом
> на `3b865e14` и незавершённые этапы отражены в [PROGRESS](../stabilization/PROGRESS.md#resource-context-plan-reconciliation--2026-10-23)
> и [реестре результатов проверки](../stabilization/RISK_REGISTER.md#resource-context-review--2026-10-22).

Дата: 2026-10-22. Статус: **Wave 0 выполнена по исходникам; runtime-проверки не запускались**.
Owner: model-context/LLM diagnostics (`ModelContextCompiler`, LLM transport,
`ModelTracePersistenceService`); provider contracts остаются у Resource Fabric.
Основание: [предложение пользователя](../stabilization/resource-context/PLAN.md).
Scope этого изменения — аудит и минимальный план Wave 1, без runtime-рефакторинга.

Это срез evidence, а не второй архитектурный контракт или backlog. После выполнения
ограниченных этапов он остаётся историческим: фактическое поведение обновляется в
[Resource Fabric](../resource-fabric.md) и [Conversation protocol](../conversation-protocol.md),
открытые дефекты — в [risk register](../stabilization/RISK_REGISTER.md).

## 1. Baseline и границы проверки

- Вложение ориентируется на `f6c3f5f01ec314695ad60264847f578e855342d7`.
  Начальный HEAD проверки — `233d09b2daf84e26572a2a3f154b8e71f8f75da6`, ветка `main`.
- Во время чтения другой участник обновлял общий checkout: наблюдались `14e5cc67`
  и `159fd2ef`; перед записью аудита HEAD — `11e778d48828bb2dfa54a59868eb31847b5d52e3`.
  Рабочее дерево также содержало незавершённые изменения. Они не перемещались,
  не откатывались и не коммитились этим аудитом.
  При итоговой проверке HEAD — `e3bad426dfd7ff5080aba56200fae3088d865586`;
  четыре ключевых исходника ниже повторно сверены по SHA-256 без расхождений.
- Проверены владельцы ниже, соответствующие части canonical docs, MASTER Wave 3,
  URF §§2–3/8, Authority §§39–42, Evidence/Compiler §§8–13/25,
  [ADR-0004](../decisions/ADR-0004-resource-data-plane.md) и текущие migration seams.
  Исторический direct cutover не запускается повторно.
- Существующие тесты прочитаны как спецификация проверок. Нового результата
  Harness, live LLM, Windows/Office/VBE/WebView2 или измерения памяти нет.
  Docs-only проверка ограничена diff, ссылками и сохранностью исходного предложения.
- Реальные сохранённые пользовательские запросы не выгружались. В доступных
  файлах checkout по именам `*request*.json[l]`, `*report*.json`, `*trace*.jsonl`
  готовых payload fixtures не обнаружено. Ниже разделены путь реальных bytes,
  существующие синтетические fixtures и ещё не выполненные reproductions.

Контрольные SHA-256 ключевых исходников на момент записи:

| Файл | SHA-256 |
|---|---|
| `ModelContextCompiler.cs` | `43f8e604ab368fc9e6e34d49da70b28b7d314fb3051b6f5ce27307432e27bb61` |
| `LlmClient.cs` | `d2a98e991c1e99242c09910a6233d9f15e4510787d89e63e396deb6f3b20d6e3` |
| `LlmMessageBuilder.cs` | `cdfa946b1bf30df1e7c63154f76a94c0378ba67db5dee675f2b6a980d765b659` |
| `ConversationPromptComposer.cs` | `e0bac67268bf8911143dec9b1aa0cbd9bd580af47473f23d81dcbe551f008d14` |

## 2. Трассировка и владельцы

| Участок | Фактический путь и граница |
|---|---|
| Office discovery/read | [ResourceToolCatalog](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) → exact ToolRuntime handlers → `ResourceGatewayService.Find/ResolveIntentTarget/Read`. `find/search/outline/read` уже существуют. [ResourceReadToolHandler](../../src/RNAssistant.Office/Tools/ResourceReadToolHandler.cs) выбирает whole, lines, part, section или table; URI/revision/cursor остаются runtime-owned. |
| Capture и retained read | [ResourceGatewayService.Read](../../src/RNAssistant.Office/Services/ResourceGatewayService.cs) проверяет view/selector, использует retained path либо bound provider через `WithProvider`; `PrepareRead/PublishRead` принадлежат [ResourceAuthorityService](../../src/RNAssistant.Office/Services/ResourceAuthorityService.cs). Historical CAS failure не означает разрешения читать live источник. |
| CAS и observation | `PublishRead/RetainView/Observe` регистрируют revision/view и публикуют head через один [ResourceAuthorityStore](../../src/RNAssistant.Core/Storage/ResourceAuthorityStore.cs); bodies — существующий `ChatBlobStore`. `ResourceReadObservation` объединяет result и exact evidence, `ToolHandlerResult` передаёт его в durable accepted history. |
| Workspace | [WorkspaceFileResourceProvider](../../src/RNAssistant.Core/Services/WorkspaceFileResourceProvider.cs) → [WorkspaceFileService](../../src/RNAssistant.Core/Storage/WorkspaceFileService.cs) → тот же CAS/authority. Core router и read contracts общие; Office Gateway не вызывается из Core/Application. Это существующая host-specific подготовка, не второй compiler/currentness store. |
| Frozen input Office | [ConversationKernelAdapter.Model](../../src/RNAssistant.Office/Services/ConversationKernelAdapter.Model.cs) → [ConversationModelSession.CompileCurrent](../../src/RNAssistant.Office/Services/ConversationModelSession.cs): accepted facts, exact catalog barrier, `CaptureMany`, skill/schema/tool generations, `_session.Revision`. Scope locks упорядочены; фиксируется tuple, не новый глобальный cross-subsystem lock. |
| Frozen input Workspace | [WorkspaceConversationService](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs): `CaptureContextAuthority` обновляет authority до компиляции; `CaptureModelAuthority/AcceptedFacts/CompileCurrent` передают её общему compiler. Write admission использует доставленные observations, а не наличие CAS. |
| Compiler | [Core ModelContextCompiler](../../src/RNAssistant.Core/Services/ModelContextCompiler.cs), 1195 строк: atoms/causal frames → [EvidenceStateReducer](../../src/RNAssistant.Core/Services/EvidenceStateReducer.cs) → bounded hydration/project → repeated-read folding → final budget → detached `ModelContextSnapshot`. Live provider I/O внутри Compile нет. |
| Compaction | [ContextCompactionRuntime](../../src/RNAssistant.Core/Services/ContextCompactionRuntime.cs) сначала компилирует frozen source window, затем helper request тем же compiler, включая repair и receipt. Typed claims/checkpoint добавляются к истории; старые events/CAS не удаляются. Это orchestration вокруг compiler, не отдельный LLM context assembler. |
| Protocol/repair | [ModelProtocolClient](../../src/RNAssistant.Core/ModelProtocol/ModelProtocolClient.cs) использует frozen accepted messages / `CompileRepair`. Office `ProjectJsonSchemaMessages` может убрать повторённые parameter schemas перед transport; fallback восстанавливает accepted prompt. |
| HTTP | [LlmMessageBuilder](../../src/RNAssistant.Core/Llm/LlmMessageBuilder.cs) выбирает attachment text/images/audio → [LlmClient](../../src/RNAssistant.Core/Llm/LlmClient.cs) `BuildRequestBody/SerializeJson` → request trace → тот же byte array в [LlmHttpTransport.CreateJsonContent](../../src/RNAssistant.Core/Llm/LlmHttpTransport.cs) → `SendAsync`. |
| Persistence/следующий шаг | [ModelTracePersistenceService](../../src/RNAssistant.Core/Services/ModelTracePersistenceService.cs) синхронно сохраняет request event + CAS payload до dispatch; accepted model/tool facts затем входят в следующий frozen compile. Request, model attempt и context snapshot уже имеют разные runtime IDs. |
| UI | [ModelContextInspectorService](../../src/RNAssistant.Office/Services/ModelContextInspectorService.cs), [typed DTO](../../src/RNAssistant.Office/Contracts/BridgeDtos.ModelContext.cs), [app-model-context.js](../../web/js/app-model-context.js): request timeline, correlated response/failure/SSE, exact download, original CAS body, отдельный preview. Session/event/lease guards уже существуют. |

Текущие [migration seams](../stabilization/MIGRATION_MAP.md#workspace-first-adapters--2026-10-04)
уже описывают разные host preparation paths. Создавать ещё один Gateway facade,
compiler, authority, CAS или постоянную graph/read-model базу не требуется.

## 3. Что сохраняется в request и receipt

Реальный serializer формирует `model`, упорядоченный `messages`, `max_tokens`,
`temperature`, `top_p`, `stream`; условно — `stream_options`, reasoning fields и
`response_format`. В `json_schema` точный tool contract находится в response schema.
Message body бывает текстом, native call/result с `tool_call_id` либо массивом
text/image/audio parts. Это описание production serializer, **не новый HTTP capture**.

`LlmClient.CompleteAsync` сначала создаёт `requestPayloadBytes`, затем передаёт их
в trace и HTTP content. [ModelRequestTracePrecedesDispatch](../../tests/RNAssistant.Harness/Program.SessionEventStoreTests.cs)
останавливает transport в TraceSink до сети и проверяет подготовленные bytes,
provenance и отсутствие authorization secret. Это проверка границы подготовки,
а не доказательство получения запроса сервером.

[ModelContextExactExchange / ModelRequestSchemaDuplicationModesAndFallback](../../tests/RNAssistant.Harness/Program.ModelContextInspectorTests.cs)
проверяют соответственно exact CAS download крупного synthetic payload с Unicode
и numeric lexemes и serializer schema/repair/fallback. Они не сравнивают все
part-level delivery claims с HTTP. Переписывать эту инфраструктуру не нужно.

Уже есть [ContextReceipt](../../src/RNAssistant.Core/Models/ModelContextModels.cs):
snapshot ID, frozen generations/high-water, counts, estimated tokens, hydration,
compaction/working-set counters, message positions и presentation parts.
`LlmTraceRecord` связывает request/attempt/step/context IDs и `ContextMessages`;
полный frozen tuple не копируется в каждый request trace. Последний receipt в
session не следует выдавать за receipt выбранной старой попытки.

Границы существующих метрик:

| Величина | Что установлено |
|---|---|
| Request bytes/hash | Exact retained payload; Inspector читает его через существующий data plane. Новых численных измерений пользовательского запроса нет. |
| Tokens | `EstimatedPromptTokens` и compiler estimate отдельно от provider `PromptTokens/CompletionTokens/TotalTokens`. Per-message actual token counts отсутствуют. |
| Compiler work | Hydrated count/bytes, dedup/omission counters уже есть; это не измерение peak memory/latency. |
| Office text | Internal page / line / supported artifact part — 32000 символов; whole aggregation до 2000000 символов и 128 внутренних страниц. Provider capture может иметь более строгий предел. |
| Workspace file model read | Whole/excerpt — 16000 символов, 1–500 выбранных строк; text source capture — 1 MiB. Большой streaming `CaptureFile/OpenExactRead` для application consumers — отдельный путь. |
| Inspector | 50 запросов на страницу; full download ограничен общим 50 MiB budget. Preview и фактический request различаются. |

## 4. Находки и воспроизведение

### RC-01: provenance после schema projection

**Дефект подтверждён трассировкой исходников, runtime reproduction ещё не выполнен.**
`ModelContextCompiler.DescribeMessages` помечает неизменённое required message как
`Full`. `ConversationPromptComposer.ProjectJsonSchemaMessages` удаляет
`RUNTIME_CONTEXT.tools[*].function.parameters`, сохраняя тот же `Id`.
`LlmClient.DescribeWireMessages` копирует старую запись по `SourceMessageId`, не
проверяя изменение content. UI может показать «полностью» для преобразованного
исходного сообщения. Exact request CAS содержит правильный конечный body;
ошибка относится к объяснению доставки, не к потере исходной истории.

Минимальная reproduction для Wave 1: существующий Excel fixture с одним tool →
compile receipt → strict schema projection → LlmClient с останавливающим до HTTP
TraceSink. Сравнить исходный и wire content при одинаковом ID и проверить badge.
Повторить json_object и schema rejection во время repair: badge должен относиться
к данной попытке, а accepted prompt и native pair оставаться неизменными.

### RC-02: manifest менее точен, чем сохранённый payload

**Подтверждённый пробел observability, не доказанная потеря данных.**
Attachment text truncation/image selection выполняются в `LlmMessageBuilder`;
`DescribeWireMessages` честно даёт attachments статус `Unknown`, но не сохраняет
requested/actual media parts и конкретные omission reasons. Presentation entry
также не содержит typed operation/evidence/exact-resource/coverage links.
Excluded sources получают общую причину «фильтрация, свёртка или замена».

Reproduction для следующего slice: два изображения при лимите одного, PDF с
ограниченным набором страниц и text attachment с исчерпанным budget. Exact JSON
уже показывает выбранные части, manifest не объясняет все исключения. Не выводить
из `hydratedForNextModelStep`, `rawContentIncluded` или successful read факт delivery
в конкретный HTTP request. Новый manifest должен дополнять существующий trace,
сохраняя bytes лишь в одном CAS payload.

### RC-03: dedup и последующая budget eviction

**Кандидат на дефект, требует targeted reproduction.** После hydration compiler
сворачивает одинаковые reads и пишет `retained_result.tool_call_id` с утверждением,
что полное тело сохранено позже. Затем budget phase может вызвать `OmitSourceBody`
для оставшегося read; явного повторного согласования ранней ссылки нет.
Это не установлено как runtime failure и не основание для широкого cleanup.

Проверка: расширить существующий identical-read case в
`ResourceCompilerFiltersBeforeBudget` двумя равными whole reads и budget, в который
помещаются receipts, но не итоговое тело. Проверить отсутствие ложной ссылки на
доставленное тело, отсутствие write evidence и неизменность durable facts.
При подтверждении исправлять локальную фазу финализации delivery.

Повторная hydration активных одинаковых reads также возможна до сравнения полных
projected results. Сравнение только input evidence неправильно для разных
вычислений над одним источником. Оптимизация допустима после измерения и exact
equivalence check; dedup archive/current-source уже выполняется раньше.

### RC-04: coverage и пределы нельзя унифицировать переименованием

`ResourceReadProjection` уже хранит requested lines/section/part, actual Coverage,
`Complete` и `SourceComplete`; `ResourceEvidence` — exact view/coverage/completeness.
Office `ReadLines` сначала pin/capture whole snapshot, затем возвращает точный
UTF-16 character range, сохраняет CR/LF и ставит `Complete=false`.
Таким образом «все запрошенные строки» сейчас не отдельный selectionComplete flag.
Нужно уточнять consumer semantics существующих типов, не вводить параллельный JSON.

Длинная строка уже читается через `partNumber` у поддерживаемых Office artifacts.
Это не универсальный selector для VBA/Workspace. CLI сверхлимитная строка получает
`read_too_large`; Office lines — `resource_lines_too_large`. Streaming row/char/page
pushdown и новые derived views остаются отдельным provider scope, с bounded checks.

## 5. Решения accept / adapt / reject

| Предложение | Решение | Обоснование / owner |
|---|---|---|
| Identity/revision/hash/payload и stored/current/delivered/complete раздельны | accept | Существующие Core contracts; сохранить exact authority и unknown-write guards. |
| Один frozen compiler, без live I/O | accept | Уже общий Core compiler, включая compaction/repair; добавлять новый не нужно. |
| Один Gateway буквально для всех hosts | adapt | Общие router/read/evidence contracts, разные host preparation paths; не тянуть Office в Core/Application. |
| Сохранить точный Q snapshot | adapt | `llm.request` + CAS bytes + RequestId/ContextSnapshotId уже есть. Сначала исправить provenance, не делать второй store. Prepared request не доказывает delivery серверу. |
| Receipt после serializer | adapt | Дополнять существующие receipt/trace конечным manifest; compiler receipt остаётся объяснением selection. Не присваивать ему transport facts заранее. |
| Стабильные R/E/O/Q aliases | adapt | Поздняя read-only UI projection над canonical IDs. Определить scope/restart mapping до реализации; UI alias не становится model API или authority. |
| T/S/C/D/M/A/P как новые сущности | reject | Нет обоснованного нового owner; existing task/plan/artifact contracts не дублировать. |
| Универсальная матрица views/selectors | adapt | Capability каждого provider и отдельный consumer slice. Сохранить `resources_outline`; JSON Pointer, procedure, crop и прочие selectors не объявлять реализованными. |
| Второй read-observation JSON | reject | Расширять `ResourceReadObservation/ResourceReadProjection/ResourceEvidence`, если доказан недостающий смысл. |
| Adaptive whole read | adapt | Office уже whole-by-default; CLI имеет другую границу. Сначала metadata/coverage и конкретный недостающий selector. |
| Общие «текущие 16k/1 MiB» пределы | reject | Это не универсальный baseline. Не менять текущие provider/capture/hydration/model ограничения этим проектным тезисом. |
| Дедуп контекста без удаления событий | accept | Уже реализован; RC-03 и повторная hydration требуют узкой проверки, не переписывания алгоритма. |
| Compiler cleanup по размеру файла | reject | 1195 строк сами по себе не дефект. Выделять pure policy только под подтверждённую ближайшую правку. |
| Context Explorer timeline с нуля | reject | Timeline, exact payload, originals и preview уже существуют. Расширять текущий Inspector. |
| Resource inspector, graph, diff, redacted export | adapt | Следующие отдельные UI slices после корректного manifest; graph только производный, export без secrets/Base64. Rematerialization — отдельное явное read action через Gateway. |
| Acceptance suite и Windows qualification | adapt | Использовать имеющееся coverage; новые tests только для gaps. Windows gates открыты, на этой машине не запускать. |

Новых архитектурных решений, требующих ADR, или временных adapters этот аудит
не вводит. Migration map менять без нового seam не требуется.

## 6. Минимальный Wave 1

Первый commit-sized slice — **точная provenance schema projection (RC-01)**.
Owner: Core LLM diagnostics + Office prompt projection; contracts остаются typed.

1. Передать факт преобразования из owning projection в существующую диагностическую
   цепочку. Финальные role/order/content относятся к конкретному wire request;
   изменённое исходное сообщение не помечается `Full`. Accepted messages, frozen
   tuple, body selection, mutation admission и JSON протокол v7 сохраняют смысл.
2. Дополнить существующие `model context: request schema modes and fallback` и
   `storage: model request trace precedes dispatch` проверкой receipt против
   сериализованного body для strict/object/repair/fallback. Network и Office не
   нужны. Только эти проверки и необходимый Harness build после изменения кода;
   `model context: compiler presentation` — смежная проверка.
3. Обновить canonical conversation diagnostics contract и закрыть RC-01 только
   после локального результата; Windows UI evidence оставить отдельно.

Затем отдельный ограниченный slice RC-02: закрепить в существующем request event
typed manifest с frozen context correlation и конечными message/part positions,
operation/evidence/coverage и omission reason там, где owner способен их доказать.
Text/media byte length/hash описывают фактический payload; measured и estimated
tokens раздельны. Не копировать Base64/body в manifest, не выводить coverage из
caption/summary, не давать UI новый путь к CAS. Старые events без manifest остаются
`Unknown`, без выдуманной обратной реконструкции. Golden fixture сравнивает manifest
с теми же bytes, которые передаются HTTP content. Alias/graph в этот slice не входят.

RC-03 проверить перед любым compiler cleanup. Новые PDF/OCR/views и provider limits
не являются зависимостью Wave 1. Общая Wave 6 остаётся незавершённой до реального
Windows/Office/WebView2 evidence и согласованного scope остальных этапов.

## 7. Существующая проверочная база

| Риск | Уже существующий минимальный маршрут из [Harness guide](../../tests/RNAssistant.Harness/README.md) |
|---|---|
| Exact payload/attempt provenance | `model context:`, `storage: model request trace precedes dispatch`; [model-context.test.js](../../tests/web/model-context.test.js) только при UI-изменении |
| Stale evidence, budget, pairs, repeated read | `resource cutover: correctness before budget`, `frozen evidence and coverage`, `two chat mutations reach next compiler` |
| Whole/excerpt/CAS/restore | `agent: reads large VBA resource whole within request budget`, `resource cutover: retained whole and line reads materialize once`, `retained payload failures are typed`, `local restore logical lineage` |
| Archive/compaction/restart | `context continuity: selects active and archived bodies once`, `restores bounded working set`, `context: compaction preserves tool protocol pairs`, typed claim checks |

Названия показывают существующее покрытие, а не результаты нового прогона.
Большой PDF с раздельной text/table/image coverage и comprehensive post-serializer
manifest остаются новыми acceptance gaps; прочитанные tests их не закрывают.

Проверка документации: `git diff --check` прошёл; 40 добавленных относительных
ссылок/anchors разрешаются; текст исходного вложения после служебного предисловия
сохранён побайтно. Runtime-код и tests этим изменением не затронуты.
