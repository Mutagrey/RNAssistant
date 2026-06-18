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
- `src/RNAssistant.Office/AssistantController.cs`: high-level orchestration and bridge-facing API.
- `src/RNAssistant.Office/AssistantController.Chats.cs`: chat/session lifecycle and document-key migration.
- `src/RNAssistant.Office/AssistantController.Context.cs`: active chat context attachments.
- `src/RNAssistant.Office/Agent`: agent transcript/plan formatting and retry policy.
- `src/RNAssistant.Office/Tools`: tool execution, pipelines, VBA patch/backup workflow.
- `src/RNAssistant.*AddIn`: host adapters and VSTO wiring.
- `web`: static HTML/CSS/JS task pane.

## Non-Negotiable Boundaries

- Parser converts text/native-compatible shapes to `SkillCommand`; executor decides whether command may run.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- VSTO adapters expose capabilities through `SkillDefinition` and `ExecuteSkill`; they should not know chat/session/storage details.
- UI sends typed bridge messages; business rules stay in C# unless they are purely presentation behavior.

## Known Oversized Areas

- `web/js/app.js` is still a UI super-file. Split next by stable globals: `bridge/state`, `chat`, `models/settings`, `tools`, `vba`, `context`, `boot`.
- `web/css/app.css` is also large. Split only after JS split or a UI restyle, because it is currently static and low-risk.
- Add-in adapters are medium-sized and host-specific; refactor only with Windows/VSTO validation available.

## Target Harness Pipeline

Create a non-VSTO harness project or script that runs on macOS/Linux/Windows:

- parser fixtures: fenced JSON, bare JSON, `tool_calls`, malformed JSON;
- prompt composition/context trimming fixtures;
- pipeline dry-run fixtures with fake `IOfficeApplicationAdapter`;
- chat store fixtures using temp directories, including unreadable files being skipped;
- no Office COM dependency.

Windows-only validation remains separate:

- open solution in VS 2022;
- build `Debug | x64`;
- smoke-test each Office host;
- test WebView2 fixed runtime fallback;
- test VBA trust-disabled path and rollback restore.
