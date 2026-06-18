# RNAssistant Agent Rules

Отвечай коротко и по делу. Не запускай тяжелые билды и тесты без явной причины.

## Цель проекта

RNAssistant - локальный VSTO/WebView2 ассистент для Office, который хранит чаты и контекст по документам, вызывает локальные Office tools и не требует серверной части.

## Границы слоев

- `RNAssistant.Core`: модели, настройки, хранилища, LLM-клиент, prompt/tool parsing. Нельзя ссылаться на Office/VSTO/WinForms/WebView2.
- `RNAssistant.Office`: общий runtime, task pane bridge, controller orchestration, agent transcript, tool execution. Нельзя добавлять host-specific COM interop.
- `RNAssistant.*AddIn`: только VSTO host adapters, ribbon, Office COM interop и built-in skills конкретного приложения.
- `web`: статический UI без npm/build pipeline. Любой новый JS должен быть выделен по зоне, а не добавлен в `web/js/app.js`.
- `tools` и `%AppData%/RNAssistant/tools`: пользовательские tools. Executor logic живет в `RNAssistant.Office/Tools`, не в controller и не в adapters.

## Правила изменений

- Не раздувай `AssistantController.cs`: orchestration only. Chat/session code - `AssistantController.Chats.cs`, context - `AssistantController.Context.cs`, execution - `Tools/OfficeToolExecutor.cs`.
- Не добавляй новые responsibilities в VSTO add-ins. Если код не зависит от конкретного Office host, он должен быть в `Core` или `Office`.
- Не меняй `*.Designer.cs` и VSTO project metadata без необходимости.
- Не запускай VSTO/Office validation на этой машине: здесь нет рабочей VSTO-среды. Для COM/VSTO изменений фиксируй, что нужна проверка на Windows + Office x64 + VS 2022.
- Сохраняй C# 7.3 и .NET Framework 4.8 compatibility.
- Не вводи npm/bundler без отдельного решения: текущий UI грузится как static local files в WebView2.
- Не храни секреты в репозитории. API key остается в DPAPI CurrentUser через `ProtectedSecretStore`.

## Tool/Agent Protocol

- Runtime protocol остается text-first: модель возвращает `rnassistant-agent` или `rnassistant-skill` fenced JSON.
- Native `tool_calls` можно принимать только как compatibility input и конвертировать в `SkillCommand`; не привязывать выполнение к удаленному API tools.
- Built-in Office mutation tools могут исполняться в Agent mode, кроме VBA mutation tools.
- Custom tools с `requiresConfirmation` и VBA mutation tools требуют подтверждения, если `AutoConfirmToolActions` выключен.
- Pipeline tools не должны обращаться напрямую к Office adapters: они вызывают existing tool ids через `OfficeToolExecutor`.

## Контекст и чаты

- Контекст принадлежит активному chat session, не глобальному документу.
- Legacy chat/context files не мигрируются автоматически. Если формат не читается, runtime должен пропустить файл и создать новый session/context.
- Document identity migration должна сохранять чаты при смене пути/первом сохранении документа.
- Runtime reset может очищать chats, contexts, VBA backups и WebView user data; settings, API key и custom tools не удалять без отдельного явного действия.

## Definition of Done

- Зона ответственности не смешана с соседним слоем.
- Новые файлы добавлены в old-style `.csproj`.
- Для VSTO/COM изменений указан Windows validation step.
- Для parser/tool changes есть хотя бы легкая локальная проверка или явное объяснение, почему проверка не запускалась.
- Документация обновлена, если меняется архитектурное правило или protocol.
