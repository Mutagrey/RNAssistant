# RNAssistant Architecture

## Product Goal

Локальный Office assistant для Word, Excel, PowerPoint и Outlook:

- общая WebView2 task pane UI;
- пер-документные чаты и контекст;
- OpenAI-compatible chat completions endpoint;
- локальные Office tools, pipelines и VBA rollback workflow;
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
- `src/RNAssistant.Core/Skills`: parsing model text into local `SkillCommand`.
- `src/RNAssistant.Core/Storage`: JSON file storage under `%AppData%/RNAssistant`.
- `src/RNAssistant.Office/Controller/AssistantController.cs`: high-level orchestration and bridge-facing API.
- `src/RNAssistant.Office/Controller/AssistantController.Chats.cs`: chat/session lifecycle and document-key migration.
- `src/RNAssistant.Office/Controller/AssistantController.Context.cs`: active chat context attachments.
- `src/RNAssistant.Office/Contracts`: shared Office abstractions such as `IOfficeApplicationAdapter`.
- `src/RNAssistant.Office/Runtime`: add-in runtime helpers that are host-neutral.
- `src/RNAssistant.Office/Vba`: shared VBA project support.
- `src/RNAssistant.Office/Agent`: agent transcript/plan formatting and retry policy.
- `src/RNAssistant.Office/Tools`: tool execution, pipelines, VBA patch/backup workflow.
- `src/RNAssistant.*AddIn`: host adapters and VSTO wiring.
- `web`: static HTML/CSS/JS task pane. `web/js/app-core.js` owns state and WebView bridge wiring; `app-settings.js`, `app-tools.js`, `app-vba.js`, `app-context.js`, and `app-chat.js` own their feature flows; `app-utils.js` owns pure browser helpers; `app.js` is boot plus shared rendering helpers.

## Non-Negotiable Boundaries

- Parser converts text/native-compatible shapes to `SkillCommand`; executor decides whether command may run.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- VSTO adapters expose capabilities through `SkillDefinition` and `ExecuteSkill`; they should not know chat/session/storage details.
- UI sends typed bridge messages; business rules stay in C# unless they are purely presentation behavior.

## Known Oversized Areas

- `web/css/app.css` is still large. Split by feature only when changing UI styling materially; avoid CSS churn while behavior is moving.
- `Controller/AssistantController.*` should shrink further into services after the harness exists and constructor dependencies stabilize.
- Add-in adapters are medium-sized and host-specific; refactor only with Windows/VSTO validation available.

## Harness Pipeline

`tests/RNAssistant.Harness` is the local non-VSTO harness. Run it with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

Current coverage:

- parser fixtures: fenced `rnassistant-agent`, bare JSON arrays, native `tool_calls`, malformed JSON;
- chat store fixtures using temp directories, including broken JSON files being skipped;
- pipeline dry-run and execution fixtures with fake `IOfficeApplicationAdapter`;
- confirmation gates for custom tools and Agent Mode built-in mutations;
- no Office COM dependency.

Next harness coverage:

- prompt composition/context trimming fixtures;
- unreadable-directory storage edge cases where the OS can simulate them reliably.

Windows-only validation remains separate:

- open solution in VS 2022;
- build `Debug | x64`;
- smoke-test each Office host;
- test WebView2 fixed runtime fallback;
- test VBA trust-disabled path and rollback restore.
