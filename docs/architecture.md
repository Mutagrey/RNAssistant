# RNAssistant architecture

## Product

RNAssistant is a local Office assistant for Word, Excel, PowerPoint, and Outlook. It stores per-document chats and context, talks to an OpenAI-compatible endpoint, executes Office tools locally, and requires no backend.

## Dependency direction

```text
static WebView UI
    -> typed bridge
        -> RNAssistant.Office orchestration and local tools
            -> RNAssistant.Core models, storage, LLM transport, parsers
            -> IOfficeApplicationAdapter
                -> host-specific COM adapters
```

- `RNAssistant.Core` cannot reference Office, VSTO, WinForms, or WebView2.
- `RNAssistant.Office` owns host-neutral orchestration, session services, prompt assembly, transcripts, and tool execution. It cannot contain host-specific COM interop.
- `RNAssistant.OfficeHosts` and `RNAssistant.*AddIn` own host adapters and Office wiring only.
- `web` is static HTML/CSS/JS with no build pipeline.

## Chat and Agent

There are exactly two persisted modes.

- `Chat` uses `PlainChatService`: normal history plus `ChatSystemPrompt`; no tools, skills, agent JSON, or Office execution.
- `Agent` uses `AgentRunService`: prompt assembly, one model JSON response, zero or more sequential local tools, JSON results, and the next model turn.

`AgentPromptComposer` creates one `RUNTIME_CONTEXT` JSON object containing document identity, all runnable tools in native-like function format, a compact enabled-skill catalog, chat context, and artifact references. Relevant complete skill Markdown is loaded by the model through `common.skills_read`. The composer does not inspect or classify request wording and does not capture Office content eagerly.

`AgentResponseParser` accepts either a final `message` or one or more `tool_calls` entries with unique ids. `OfficeToolExecutor` remains the authority for formal argument schemas, effective pipeline safety, confirmation, and dispatch. `AgentJsonProtocol` serializes each result to `{ok, tool_call_id, name, status, message, data, error}`.

See [agent-protocol.md](agent-protocol.md).

## Important boundaries

- Controller files coordinate bridge requests; reusable behavior stays in services and executor logic stays in `RNAssistant.Office/Tools`.
- Tools are executable capabilities. Skills are Markdown instructions discovered through the compact catalog and read through one normal tool; there is no activation/dependency runtime.
- Tool safety lives in `ToolDefinition`: mutation, local-state, confirmation, risk, capability, and `AgentCanRun` metadata. Prompts cannot bypass it.
- Custom tools require a strict object JSON Schema with documented arguments. Pipelines invoke existing ids through `OfficeToolExecutor` and cannot call adapters directly.
- VBA tools are manifest packages. Backup, ownership, hash, and stale-state protections remain inside VBA execution; see [vba-tool-packages.md](vba-tool-packages.md).
- Accepted agent calls/results are hidden protocol messages. Visible tool activity is presentation only and is excluded from model context.
- Provider reasoning is stored separately from agent JSON.
- Context belongs to the active chat. Compaction stores a model-produced checkpoint and an exact raw tail without deleting the source transcript.
- Attachments use the selected model. There is no automatic attachment-model router or endpoint failover.
- Different chats may run concurrently; one chat has one active run and document mutations are serialized by host/document identity.
- HTML workspace state belongs to the chat, is revisioned as artifacts, and remains sandboxed with explicit network-origin permission.
- Desktop COM calls enter adapters through the STA dispatcher. VSTO/COM changes require Windows validation.

## Main code zones

- `src/RNAssistant.Core/Llm`: HTTP transport, message construction, response/reasoning parsing, budgets.
- `src/RNAssistant.Core/Tools/AgentResponseParser.cs`: minimal Agent JSON parser.
- `src/RNAssistant.Core/Storage`: settings, chats, tools, skills, attachments.
- `src/RNAssistant.Office/Services/AgentRunService.cs`: direct agent loop.
- `src/RNAssistant.Office/Services/AgentPromptComposer.cs`: Agent prompt and runtime context.
- `src/RNAssistant.Office/Services/PlainChatService.cs`: plain Chat flow.
- `src/RNAssistant.Office/Services/ContextCompactionService.cs`: optional checkpointing.
- `src/RNAssistant.Office/Tools`: dispatch, schemas, pipelines, tool/skill/prompt CRUD, VBA lifecycle.
- `src/RNAssistant.Office/Controller`: typed bridge-facing orchestration.
- `src/RNAssistant.OfficeHosts`: Excel/Word/PowerPoint/Outlook COM adapters.
- `web/js`: static feature modules; no agent routing or business rules.

## Harness

Run the host-neutral fast suite with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

The suite covers the minimal Agent parser/prompt/tool-result loop, Chat isolation, storage, context, pipelines, tool safety, VBA package/backup behavior, attachments, HTML workspace, and typed bridge payloads. It has no Office COM dependency.

Windows-only validation remains: build `Debug | x64` in VS 2022 and smoke-test each Office host, desktop attach, VSTO task panes, and VBA native-host loading.
