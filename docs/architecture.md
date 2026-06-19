# RNAssistant Architecture

## Product Goal

Локальный Office assistant для Word, Excel, PowerPoint и Outlook:

- общая WebView2 task pane UI;
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
                -> concrete VSTO add-in adapters
```

`Core` не знает про Office. `Office` не знает про Word/Excel COM types. Add-in projects знают про конкретный host и реализуют `IOfficeApplicationAdapter`.

## Current Code Zones

- `src/RNAssistant.Core/Llm`: API client, prompt composition, prompt message trimming, context usage estimates.
- `src/RNAssistant.Core/Tools`: parsing model text into local `ToolCommand`.
- `src/RNAssistant.Core/Skills`: built-in markdown skill provider.
- `src/RNAssistant.Core/Services`: Office-agnostic model services such as context normalization.
- `src/RNAssistant.Core/Storage`: JSON file storage under `%AppData%/RNAssistant`.
- `src/RNAssistant.Office/Controller/AssistantController.cs`: high-level orchestration and bridge-facing API.
- `src/RNAssistant.Office/Controller/AssistantController.Agent.cs`: agent pending-tool confirmation and resume/cancel bridge flow.
- `src/RNAssistant.Office/Controller/AssistantController.Chats.cs`: chat/session bridge methods; lifecycle and document-key migration live in `ChatSessionService`.
- `src/RNAssistant.Office/Controller/AssistantController.Context.cs`: active chat context attachments.
- `src/RNAssistant.Office/Contracts`: shared Office abstractions and bridge DTOs such as `IOfficeApplicationAdapter` and `BridgeDtos`.
- `src/RNAssistant.Office/Runtime`: add-in runtime helpers that are host-neutral.
- `src/RNAssistant.Office/Vba`: shared VBA project support.
- `src/RNAssistant.Office/Agent`: agent transcript/plan formatting and retry policy.
- `src/RNAssistant.Office/Services`: host-neutral application services used by controller orchestration, such as chat/session lifecycle, tool/skill catalog composition, context normalization, and chat completion flow.
- `src/RNAssistant.Office/Tools`: tool execution, pipelines, skill CRUD tools, VBA patch/backup workflow.
- `src/RNAssistant.*AddIn`: host adapters and VSTO wiring.
- `web`: static HTML/CSS/JS task pane. `web/js/app-core.js` owns state and WebView bridge wiring; `app-settings.js`, `app-tools.js`, `app-skills.js`, `app-vba.js`, `app-context.js`, and `app-chat.js` own their feature flows; `app-utils.js` owns pure browser helpers; `app.js` is boot plus shared rendering helpers.

## Non-Negotiable Boundaries

- Parser converts text/native-compatible shapes to `ToolCommand`; executor decides whether command may run.
- Tools are executable actions described by `ToolDefinition`; skills are markdown guidance described by `SkillDefinition`.
- Tool safety belongs to `ToolDefinition` metadata: `MutatesDocument`, `AgentCanRun`, and `RequiresConfirmation`.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- VSTO adapters expose executable capabilities through `ToolDefinition` and `ExecuteTool`; they should not know chat/session/storage details.
- UI sends typed bridge messages; business rules stay in C# unless they are purely presentation behavior.
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

- parser fixtures: fenced `rnassistant-agent`, bare JSON arrays, native `tool_calls`, malformed JSON;
- chat/tool/skill/VBA store fixtures using temp directories, including broken files being skipped;
- chat session lifecycle fixtures, including document-key migration;
- pipeline dry-run and execution fixtures with fake `IOfficeApplicationAdapter`;
- pipeline failure diagnostics and confirmation gates for custom tools and Agent Mode built-in mutations;
- markdown skill store/catalog/prompt separation and agent skill-save confirmation;
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
- test WebView2 fixed runtime fallback;
- test VBA trust-disabled path and rollback restore.
