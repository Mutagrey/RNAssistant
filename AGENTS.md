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
- Семантический runtime protocol — AgentDecision v1: один raw JSON object с обязательными полями `protocolVersion`, `kind`, `decisionSummary`, `goal`, `plan`, `tool`, `message`. Kind: `plan`, `tool`, `clarify`, `final`, `cannot_complete`.
- Один model turn может выбрать ровно один внешний tool. Fences, surrounding prose, alternate envelopes, `function_call` и parallel tool calls не поддерживаются.
- Transport modes: `json_schema` по умолчанию, `json_object`, `native_tool_calls`. Fallback `json_schema -> json_object` допустим только до первого выполненного tool. Office tools всегда выполняются локально.
- Роль инструкций выбирается из `developer` (default), `system`, `user`. Роль результата выбирается независимо: `tool` (default с matching assistant `tool_calls`/`tool_call_id`), `developer`, `user`.
- `decisionSummary` и видимый plan не являются chain-of-thought. Provider reasoning хранится отдельно и не смешивается с протоколом или replay history.
- `SystemPrompt` — единый редактируемый prompt Agent; служебные переходы хранятся в `AgentPromptSettings`. Динамические route/tools/observations/skills остаются runtime data. Изменение prompt через tool требует подтверждения.
- Custom tools обязаны иметь formal object JSON Schema. Другие формы отклоняются без миграции.
- Built-in Office mutation tools могут исполняться в Agent mode, кроме VBA mutation tools.
- Custom tools с `requiresConfirmation` и VBA mutation tools требуют подтверждения, если `AutoConfirmToolActions` выключен.
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
