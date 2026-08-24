# RNAssistant Agent Rules

Отвечай коротко и по делу. Не запускай тяжелые билды и тесты без явной причины.

## Цель проекта

RNAssistant - локальный VSTO/WebView2 ассистент для Office, который хранит чаты и контекст по документам, вызывает локальные Office tools и не требует серверной части.

## Границы слоев

- `RNAssistant.Core`: модели, настройки, хранилища, LLM-клиент, prompt/tool parsing. Нельзя ссылаться на Office/VSTO/WinForms/WebView2.
- `RNAssistant.Office`: общий runtime, typed bridge contracts, task pane bridge, controller orchestration, services, agent transcript, tool execution. Нельзя добавлять host-specific COM interop.
- `RNAssistant.*AddIn`: только VSTO host adapters, ribbon, Office COM interop и built-in skills конкретного приложения.
- `web`: статический UI без npm/build pipeline. `app-core.js` - state/bridge, `app-settings.js` - settings/models, `app-tools.js` - tools, `app-vba.js` - VBA, `app-context.js` - context, `app-chat.js` - chat, `app.js` - boot/shared rendering only.
- `tools` и `%AppData%/RNAssistant/tools`: пользовательские tools. Executor logic живет в `RNAssistant.Office/Tools`, не в controller и не в adapters.

## Правила изменений

- Не раздувай `Controller/AssistantController.cs`: orchestration only. Chat/session bridge methods - `Controller/AssistantController.Chats.cs`, context bridge methods - `Controller/AssistantController.Context.cs`, reusable logic - `Services`, dispatch - `Tools/OfficeToolExecutor.cs`, pipeline execution - `Tools/PipelineToolExecutor.cs`, VBA execution/patch/backup - `Tools/VbaToolExecutor.cs`.
- Новые WebView bridge payload/response формы описывай DTO в `Contracts/BridgeDtos.cs`, а не anonymous objects и ad-hoc `JObject` parsing.
- Не добавляй новые responsibilities в VSTO add-ins. Если код не зависит от конкретного Office host, он должен быть в `Core` или `Office`.
- Не меняй `*.Designer.cs` и VSTO project metadata без необходимости.
- Не запускай VSTO/Office validation на этой машине: здесь нет рабочей VSTO-среды. Для COM/VSTO изменений фиксируй, что нужна проверка на Windows + Office x64 + VS 2022.
- Для Core и Office-neutral parser/storage/tool/service изменений используй быстрый контур `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj`.
- Сохраняй C# 7.3 и .NET Framework 4.8 compatibility.
- Не вводи npm/bundler без отдельного решения: текущий UI грузится как static local files в WebView2.
- Не раздувай `web/js/app.js`; новую UI-логику клади в существующий feature-файл или выделяй новый static script в `index.html`.
- Не храни секреты в репозитории. API key остается в DPAPI CurrentUser через `ProtectedSecretStore`.

## Tool/Agent Protocol

- Поддерживаются только режимы `agent` и `chat`; новый chat создается в `agent`. Agent может отвечать без tools, отдельного auto-router режима нет.
- Chat mode отправляет обычную историю с `ChatSystemPrompt`; tools и skills в его контекст не попадают и ничего локально не исполняется.
- Формат ответа Agent выбирается между `json_object` (default) и строгим `json_schema`; в обоих случаях ответ — один raw JSON object `message` + `tool_calls`. Для `json_schema` runtime строит response schema из текущего runnable tool catalog; при явном отказе endpoint может один раз request-locally перейти на `json_object`, если включён `FallbackToJsonObject`. `tool_calls` пуст для финала/уточнения/отказа либо содержит один или несколько вызовов с уникальным `id`, точным `name` и object `arguments`. Вызовы выполняются локально последовательно в порядке массива; зависимые и confirmation-requiring действия модель должна выбирать по одному. Отдельного batch state нет. Fences, surrounding prose и legacy envelopes не принимаются; число request-local format repairs задаёт `MaxAgentFormatRetries` (1–20, default 10). Каждый retry строится из исходного чистого prompt; невалидные ответы и repair-инструкции не сохраняются в chat history.
- Каждый Agent prompt содержит один `RUNTIME_CONTEXT`: document identity, все доступные tools в native-like function JSON, компактный каталог enabled skills (`id`, `name`, `description`), chat context и artifact references. Полный versioned Markdown выбранного skill загружается через `common.skills_read` и остаётся обычным tool result в истории. Runtime не маршрутизирует запрос, не активирует skills и не ведёт скрытый phase/planner state.
- Роль tool result выбирается независимо: `user` (default) или `developer` передают `TOOL_RESULT:` + JSON `{ok, tool_call_id, name, status, message, data, error}`; `tool` использует согласованную пару `assistant.tool_calls` → `role=tool` с тем же call id. После confirmation и success, и failure возвращаются модели; явный пользовательский cancel терминален для текущего run. Модель сама выбирает следующий шаг; runtime не делает automatic retry или отдельную verification phase.
- Роль Markdown-инструкций выбирается отдельно из `developer` (default), `system`, `user` и применяется к Agent, Chat и служебным prompt-запросам. Provider reasoning хранится отдельно и не смешивается с agent JSON или replay history.
- `SystemPrompt` — единый редактируемый prompt Agent. Изменение prompt через tool требует подтверждения.
- Custom tools обязаны иметь strict object JSON Schema с `properties`, явным `required`, `additionalProperties:false`, типом и описанием каждого аргумента. Defaults, enums, limits и array items задаются в этой же схеме. Другие формы отклоняются без миграции.
- Безопасный public VBA facade использует общие id `common.vba_*` в Excel, Word и PowerPoint. Edit/delete требуют предварительный public read/search; runtime сам привязывает snapshot к chat/document/module, сохраняет guard через confirmation и повторно проверяет его перед mutation. Модель не передаёт `expectedCodeSha256`. Низкоуровневые host-prefixed whole-module/macro backend tools остаются скрыты.
- Custom tools с `requiresConfirmation` и VBA mutation tools требуют подтверждения, если `AutoConfirmToolActions` выключен.
- Agent CRUD использует явные `common.tools_create/update/delete` и `common.skills_create/update/delete`; update сохраняет неуказанные поля. Tool `parameters`, `pipeline` и `components` передаются как native JSON, не как JSON-строки.
- Видимый план — обычный versioned chat artifact. Модель явно управляет им через `common.plan_create/read/update/delete`; runtime не сопоставляет tool calls с шагами и не меняет статусы автоматически.
- Tool safety metadata живет в `ToolDefinition`: `MutatesDocument`, `AgentCanRun`, `RequiresConfirmation`. Не добавляй новые hardcoded suffix lists в executor.
- Pipeline tools не должны обращаться напрямую к Office adapters: они вызывают existing tool ids через `OfficeToolExecutor`.
- VBA package rules зафиксированы в `docs/vba-tool-packages.md`; model-facing правила — во встроенном skill `common.vba_tool_authoring`.

## Контекст и чаты

- Контекст принадлежит активному chat session, не глобальному документу.
- Неподдерживаемые chat/context files не мигрируются. Runtime пропускает их и создает новый session/context.
- Document identity migration должна сохранять чаты при смене пути/первом сохранении документа.
- Runtime reset может очищать chats, VBA backups и WebView user data; settings, API key и custom tools не удалять без отдельного явного действия.

## Definition of Done

- Зона ответственности не смешана с соседним слоем.
- Новые файлы добавлены в old-style `.csproj`.
- Для VSTO/COM изменений указан Windows validation step.
- Для parser/storage/tool changes есть harness-проверка или явное объяснение, почему проверка не запускалась.
- Документация обновлена, если меняется архитектурное правило или protocol.
