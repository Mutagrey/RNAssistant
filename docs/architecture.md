# RNAssistant Architecture

## Product Goal

Локальный Office assistant для Word, Excel, PowerPoint и Outlook:

- standalone desktop WebView2 UI, VSTO task pane compatibility mode и
  no-registration VBA/C++/CLI in-process mode;
- пер-документные чаты и контекст;
- OpenAI-compatible chat completions endpoint;
- локальные Office tools, pipelines и VBA rollback workflow;
- markdown skills для выбора подхода агентом;
- работа без backend и без admin rights.

## Dependency Direction

```text
web static UI
    -> WebView bridge
        -> RNAssistant.Office controller/orchestration
            -> RNAssistant.Core models/storage/LLM/parser
            -> IOfficeApplicationAdapter
                -> RNAssistant.OfficeHosts COM adapters
                    -> VSTO add-ins or RNAssistant.Desktop target provider
```

Альтернативный portable runtime path:

```text
VBA add-in/macro
    -> RNAssistant.NativeHostCli.dll (__stdcall exports, C++/CLI)
        -> owned WinForms window + RNAssistant.Office WebView control
            -> RNAssistant.OfficeHosts COM adapter
```

Native host устанавливает `AssemblyResolve` для portable root до загрузки
managed assemblies. Это обязательно: внутри Office `AppDomain.BaseDirectory`
указывает на каталог Office, а не на каталог DLL.

`Core` не знает про Office. `Office` не знает про Word/Excel COM types. `OfficeHosts` знает про host-specific COM и реализует `IOfficeApplicationAdapter`. VSTO projects и desktop exe только выбирают shell/target.

## Current Code Zones

- `src/RNAssistant.Core/Llm`: API client, SSE/reasoning parsing, model capability budgeting, prompt composition and context usage estimates.
- `src/RNAssistant.Core/Tools`: strict planner JSON parsing plus legacy command parsing compatibility.
- `src/RNAssistant.Core/Skills`: built-in markdown skill provider.
- `src/RNAssistant.Core/Services`: Office-agnostic model services such as context normalization.
- `src/RNAssistant.Core/Storage`: JSON file storage under `%AppData%/RNAssistant`.
- `src/RNAssistant.Office/Controller/AssistantController.cs`: high-level orchestration and bridge-facing API.
- `src/RNAssistant.Office/Controller/AssistantController.Agent.cs`: agent pending-tool confirmation and resume/cancel bridge flow.
- `src/RNAssistant.Office/Controller/AssistantController.Chats.cs`: chat/session bridge methods; lifecycle and document-key migration live in `ChatSessionService`.
- `src/RNAssistant.Office/Controller/AssistantController.Context.cs`: active chat context attachments.
- `src/RNAssistant.Office/Contracts`: shared Office abstractions and bridge DTOs such as `IOfficeApplicationAdapter` and `BridgeDtos`.
- `src/RNAssistant.Office/Runtime`: add-in/runtime helpers that are host-neutral, including the Desktop STA dispatcher used before COM adapter calls.
- `src/RNAssistant.Office/Vba`: shared VBA project support.
- `src/RNAssistant.Office/Agent`: agent transcript/plan formatting and retry policy.
- `src/RNAssistant.Office/Services`: host-neutral application services used by controller orchestration, such as chat/session lifecycle, tool/skill catalog composition, context normalization, and chat completion flow.
- `src/RNAssistant.Office/Services/AgentRunService.cs`: controlled planner loop, route/slice/validate/execute flow, normalized observations, deterministic mutation verification, VBA context capture, and confirmation resume continuation.
- `src/RNAssistant.Office/Services/AgentPlannerRuntime.cs`: deterministic router, tool catalog slicer, planner prompt context, action validator, observation normalizer, recipe expansion, and verification command selection.
- `src/RNAssistant.Office/Tools`: tool execution, pipelines, tool/skill CRUD tools, VBA patch/backup workflow.
- `src/RNAssistant.OfficeHosts`: shared Excel/Word/PowerPoint/Outlook COM adapters and desktop target descriptors.
- `src/RNAssistant.Desktop`: standalone WinForms shell, explicit Office target picker, manual foreground attach, single-instance JSON pipe activation, and ROT-based adapter creation with hwnd validation.
- `src/RNAssistant.NativeHostCli`: thin C++/CLI exported-DLL host for VBA; owns
  only modeless window lifecycle, owner/positioning and managed assembly loading.
- `src/RNAssistant.*AddIn`: VSTO compatibility wiring; no host adapter ownership.
- `wrappers/native`: VBA source modules for Office-native launchers.
- `web`: static HTML/CSS/JS task pane. `web/js/app-core.js` owns state and WebView bridge wiring; `app-settings.js`, `app-tools.js`, `app-skills.js`, `app-vba.js`, `app-context.js`, and `app-chat.js` own their feature flows; `app-utils.js` owns pure browser helpers; `app.js` is boot plus shared rendering helpers.

## Non-Negotiable Boundaries

- Agent mode accepts one strict planner JSON object in assistant text. Fences, legacy envelopes, content-part arrays, native `tool_calls`, and `function_call` are not supported.
- Model reasoning is transport metadata (`reasoning_content` or `reasoning`), stored and rendered separately; it is never mixed into planner JSON or replayed as chat history.
- Context limits are token budgets resolved from the active model capability catalog. The legacy character limit is read only for settings compatibility.
- Planner context uses only the active chat's non-empty, reference-deduplicated pinned notes plus recent user/final-assistant messages. Agent activity/diagnostics and old attachments stay in the transcript but are not replayed; only current-turn attachments are sent.
- Text/PDF attachments are normalized locally. PDF text uses PdfPig; vision-capable models may also receive selected PDF pages rendered by the host-neutral Office service. Raw PDF files are not sent through the OpenAI-compatible chat payload.
- Routing precedes Office context capture. General-answer routes expose no tools and do not read document content; document-dependent state is obtained through explicit read tools.
- Tools are executable actions described by `ToolDefinition`; skills are markdown guidance described by `SkillDefinition`.
- Tool safety belongs to `ToolDefinition` metadata: `MutatesDocument`, `AgentCanRun`, `RequiresConfirmation`, risk/capability fields, and verification metadata.
- Agent runs are bounded by settings for max iterations and max tool steps; confirmed pending tools may resume the same run, and mutation runs use deterministic verification tools before final prose.
- A required-tool route accepts `final` only after its route phase reaches `final_phase`; inspection alone cannot complete a pending mutation. Format repair and required-tool correction have separate one-shot guards.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- Office host adapters expose executable capabilities through `ToolDefinition` and `ExecuteTool`; they should not know chat/session/storage details.
- Desktop target descriptors must be validated before tool execution; a closed or mismatched target should fail instead of falling back to an unrelated active document.
- `OfficeContext` is a Core DTO. Host adapters may implement `IOfficeContextProvider`; bridge responses can expose this without requiring every adapter/fake to implement it.
- Host adapters may implement `IOfficeDocumentCatalog`; typed bridge responses merge its open-document list with persisted chat summaries, and document activation is dispatched by stable document key.
- Unsaved Office documents use the same custom document identity as saved files when custom properties are available; display names such as `Book1` are never storage keys.
- New chat sessions remain transient until they contain a completed user/assistant exchange. Empty drafts are not written to the chat store, and document-history deletion removes every stored chat for that document without deleting the Office file.
- Desktop COM automation must enter host adapters through `DispatchedOfficeApplicationAdapter`/`OfficeStaDispatcher`; VSTO task panes already run inside their Office host process and remain Windows-validation-only.
- Desktop target selection uses `Auto follow` by default. `Manual` mode pins the selected working target; Excel task panes refresh on workbook activate/open/close events.
- The Desktop target registry stores only lightweight descriptors, not long-lived Office COM objects.
- UI sends typed bridge messages; business rules stay in C# unless they are purely presentation behavior.
- Concurrent task-pane/desktop WebViews periodically reconcile typed chat/document state from the shared local stores; mutation and Office targeting remain in C#.
- WebView response serialization belongs in `AssistantWebBridge`; controller methods should return DTOs or domain models.

## Known Oversized Areas

- `web/css/app.css` is still large. Split by feature only when changing UI styling materially; avoid CSS churn while behavior is moving.
- `Controller/AssistantController.*` should stay bridge-facing orchestration; move remaining reusable behavior into services when dependencies are stable.
- Add-in adapters are medium-sized and host-specific; refactor only with Windows/VSTO validation available.

## Harness Pipeline

`tests/RNAssistant.Harness` is the local non-VSTO harness. Run it with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

Current coverage:

- parser fixtures: strict planner JSON object plus rejection of fences, legacy envelopes, arrays, native `tool_calls`, and malformed JSON;
- chat/tool/skill/VBA store fixtures using temp directories, including broken files being skipped;
- chat session lifecycle fixtures, including document-key migration;
- pipeline dry-run and execution fixtures with fake `IOfficeApplicationAdapter`;
- pipeline failure diagnostics and confirmation gates for custom tools and Agent Mode built-in mutations;
- agent runtime guards for strict planner repair, sliced tools, waiting confirmations, stopped batches, max iterations, max tool steps, deterministic mutation verification, and VBA context prompt inclusion;
- model-quality fixtures that catch final answers when Office tool use is required;
- markdown skill store/catalog/prompt separation, prompt body limiting, and agent skill-save confirmation;
- agent custom tool save/read confirmation and validation;
- metadata-driven mutation safety gates;
- VBA replace-text flow with rollback backup using fake `IOfficeApplicationAdapter`;
- tool catalog service merge/filter behavior;
- prompt message trimming, context usage estimates, and basic no-network chat completion flow;
- Core context normalization/upsert/trim behavior;
- typed bridge settings/context/VBA/tool/chat payload parsing, agent pending-tool status, and progress envelope;
- no Office COM dependency.

Next harness coverage:

- unreadable-directory storage edge cases where the OS can simulate them reliably.

Windows-only validation remains separate:

- open solution in VS 2022;
- build `Debug | x64`;
- smoke-test each Office host;
- smoke-test VBA `Declare PtrSafe` loading for each Office host and verify the
  native DLL/WebView2Loader bitness matches Office;
- smoke-test Desktop attach from foreground Office;
- smoke-test VBA native-host Show/Hide/Close and last-error exports;
- test WebView2 fixed runtime fallback;
- test VBA trust-disabled path and rollback restore.
