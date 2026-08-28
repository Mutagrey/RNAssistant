# R29 — runtime-owned tool-call IDs

Scope: отдельный Phase 2 protocol correction + Phase 3 consumers по прямому
запросу пользователя. Baseline — `1f65f5d` (architecture audit поверх `15dea46`).
Phase 4 не начата. Контракт — [v4](../protocols/CONVERSATION_RESPONSE_V4.md),
решение — [ADR-0009](../decisions/ADR-0009-runtime-owned-tool-call-ids.md).

## Изменённые инварианты и файлы

| Граница | Изменение |
|---|---|
| Core/ModelProtocol, AppSettings | Wire v4: только `message + tool_calls`, call — `name + arguments`; model `id` запрещён. Schema/prompts/probes/parser переключены вместе; prompt schema 13 требует explicit review/reset без замены сохранённого текста |
| Core/Agent | ID-less `ToolCallDraft` отделён от accepted `ToolCall`. Kernel выдаёт уникальные IDs до accepted append; invalid allocator/collision — failure без model repair, persistence или dispatch |
| Office/ConversationKernelAdapter, AgentJsonProtocol | Accepted writer использует kernel IDs. В том же `session.commit` рядом с `ToolCallId` хранится immutable `AcceptedCallOrigin { StepId, ModelAttemptId, CallIndex }`; весь batch записывается до первого dispatch |
| History/context/clone | Отдельный v4 history reader восстанавливает ID из metadata, а не wire. Native/user/developer result correlation, confirmation, compaction и новый RunId сохраняют ID/origin; full-history preflight закрывает старые/неполные формы |
| Payload boundaries | DateParseHandling.None сохраняет ISO-строки при command materialization, controller pending restore и fork URI rebase; HTML/escape sequences не регенерируются ради ID |

Raw response остаётся неизменным. `SourceModelAttemptId` фиксируется для реально
принятой попытки, включая успешную попытку после repair, до необязательных trace
callbacks. Optional protocol verdict не владеет execution IDs. Источник связи —
accepted message в существующем stream, не новый index или диагностический store.
Уникальность ID не является semantic deduplication и не добавляет automatic tool retry.

## Проверка

Все проверки ниже выполнены в изолированном worktree на macOS, с реальными
host-neutral services/store и fake LLM/Office там, где это требуется. **141 distinct
test case passed**; одна preflight case также входит в `model protocol:`.

Форма команды: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "<filter>"`.
После успешной сборки последующие filters используют `--no-build`.

| Filter | Результат |
|---|---:|
| `conversation v4:` | 13/13 |
| `kernel:` | 44/44 |
| `model protocol:` | 15/15 |
| `agent:` | 36/36 |
| `kernel replay:` | 9/9 |
| `protocol context:` | 6/6 |
| `causal trace:` | 6/6 |
| `preflight` | 3/3; одна уже учтена выше |
| `settings: prompt` | 3/3 |
| `context: clone preserves values` | 1/1 |
| `bridge: typed settings` | 1/1 |
| `model compatibility:` | 2/2 |
| `vba: exact patch preserves boundary newlines` | 1/1 |
| `harness: production projects include` | 1/1 |
| `context: compaction preserves tool protocol pairs` | 1/1 |

В ходе проверки исправлены старые fixture IDs/expectations и запись `ToolCallId`
в fake adapter execution log. После этого заново прошли затронутые `agent:`,
`protocol context:`, `kernel replay:` и `causal trace:`. Остальные успешные результаты
этого же изменения reused: их проверяемые sources/tests/inputs не менялись.
Предыдущие phase reports не используются как доказательство v4.

Ключевые сценарии:

- Два одинаковых read calls и повторные singleton calls получают разные IDs;
  allocator exception/unsafe ID/collision ничего не принимает и не исполняет.
- Длинный HTML (240 строк с Unicode, CRLF, кавычками и literal backslashes)
  проходит посимвольно в actual executor и durable replay в user/native history,
  без дополнительного model request из-за ID.
- Confirmation после save/reload/compaction и смены RunId сохраняет pending ID;
  результаты связаны во всех трёх roles, replay не повторяет effect.
- Causal trace проверяет exact raw response, accepted attempt/index и порядок
  `raw response < accepted mapping commit < tool entry`, включая 19 rejected attempts.
- Missing/ambiguous origins, model-owned ID и v3/неполная v4 history fail closed.
  Fork rebases URI, сохраняя ISO `.000Z` в native/activity args и result data.

`dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release --nologo -v:minimal`
— pass: actual controller compiled, 0 errors, 3 существующих CA1416 warnings в PDF rendering.
MockDemo runtime/self-test не запускался. Полный harness и JS tests не запускались:
web sources не менялись, ID consumers проверены в source; known R22 вне scope.

Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal`
— pass. `git diff --check` и 136 локальных ссылок в 17 затронутых Markdown docs — pass.
Product props и tag refs совпадают с baseline перед commit.

## Ограничения и следующие gates

- **Не выполнена Windows x64 + Office + VS 2022 validation:** COM/VSTO,
  production controller pending reconstruction, live WebView/streaming, DPAPI и providers.
  Harness использует controller stub; MockDemo compilation не заменяет execution.
- Полные исходный/repaired HTML и incident trace пользователя не предоставлены.
  Синтетическая регрессия доказывает отсутствие ID-triggered regeneration и
  сохранение payload, но не воспроизводит исходный инцидент и не оценивает HTML как программу.
- R28 остаётся открытым. Прочие замечания architecture audit — effect evidence,
  batch owner, ResourceRef, pinned ToolPack, host gate и durable barriers —
  остаются gates своих Phases 4–9; этот correction не объявляет их реализованными.
- Следующий отдельный этап — Phase 4. LegacyToolOutcomeAdapter/read registry и
  полная persistence/UI projection остаются с owners/removal gates в
  [MIGRATION_MAP](MIGRATION_MAP.md).

## Локальная чистка и versioning

Удалены model-ID wire field/schema/prompt requirement, accepted-ID parser context
и мёртвый `AgentJsonProtocol.ToCommand`. Wire и accepted-history DTO больше не
смешиваются. V3 specification сохранена как исторический документ, без runtime
adapter, alias, ID rewriting или dual-write. Domain `arguments.id` не удалялся.
Старые чаты — только explicit new chat/reset, без автоматического удаления данных.
Новых `.cs` нет; production includes проверены.

Product target остаётся `16.1.0-dev`; protocol 4/prompt schema 13 не являются
product bump. Git tag/push и release scripts не входят в это изменение.
