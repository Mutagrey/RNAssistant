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

`AgentPromptComposer` creates one `RUNTIME_CONTEXT` JSON object containing document identity, all runnable tools in native-like function format, a compact enabled-skill catalog, chat context, and artifact references. Relevant complete skill Markdown is loaded by the model through `common.skills_read`. The composer does not inspect or classify request wording and does not capture Office content eagerly. A visible plan, when useful, is an ordinary versioned chat artifact written explicitly through plan CRUD tools rather than runtime planner state.

Editable Agent, Chat, title, and compaction prompts are stored as Markdown. Their instruction role (`developer`/`system`/`user`) is independent from Agent response format (`json_object`/strict `json_schema`) and tool-result role (`user`/`developer`/matched `tool`).

`AgentResponseParser` accepts either a final `message` or one or more `tool_calls` entries with unique ids. `json_schema` derives a strict response contract from the current runnable tools; `json_object` uses the same envelope with local validation. Invalid output gets bounded 1–5 ephemeral format-correction attempts; each starts from clean accepted history and invalid content never enters replay. `OfficeToolExecutor` remains the authority for formal argument schemas, effective pipeline safety, confirmation, and dispatch. `AgentJsonProtocol` serializes each result to `{ok, tool_call_id, name, status, message, data, error}` and emits the selected replay role.

See [agent-protocol.md](agent-protocol.md).

## Important boundaries

- Controller files coordinate bridge requests; reusable behavior stays in services and executor logic stays in `RNAssistant.Office/Tools`.
- Tools are executable capabilities. Skills are Markdown instructions discovered through the compact catalog and read through one normal tool; there is no activation/dependency runtime.
- Tool safety lives in `ToolDefinition`: mutation, local-state, confirmation, risk, capability, and `AgentCanRun` metadata. Prompts cannot bypass it.
- Custom tools require a strict object JSON Schema with documented arguments. Pipelines invoke existing ids through `OfficeToolExecutor` and cannot call adapters directly.
- VBA tools are manifest packages. Backup, ownership, hash, and stale-state protections remain inside VBA execution; see [vba-tool-packages.md](vba-tool-packages.md).
- Accepted agent calls/results are hidden protocol messages. Visible tool activity is presentation only and is excluded from model context. Activities from one accepted model turn share a `StepId` and its user-facing `message`, so the UI can group batch calls and results without inferring boundaries from tool names or ordering heuristics.
- Provider reasoning is stored separately from agent JSON.
- Context belongs to the active chat. Compaction stores a model-produced checkpoint and an exact raw tail without deleting the source transcript.
- A v3 chat session JSON remains the canonical transcript and artifact metadata record. JSON stores stream into temporary files and atomically replace their targets instead of building another full serialized string in memory. Immutable HTML artifact bodies live in session-scoped `html-artifact-bodies` sidecars: only the active revision is hydrated on chat load, while edit/fork/rewind load older bodies on demand. Forks copy their reachable bodies independently, and prune/chat/document/runtime cleanup removes unused files. Supported v1/v2 inline bodies migrate on the next save. Small fingerprinted `.summary.json` sidecars provide list/lookup metadata, are updated atomically after a session save, and are rebuilt lazily from supported canonical sessions when missing, stale, or invalid.
- Attachments use the selected model. There is no automatic attachment-model router or endpoint failover.
- A chat's non-empty `Model` overrides the global default through one cloned effective-settings resolver; requests, title generation, context budgets, and compaction use the same effective model without mutating stored global settings.
- Different chats may run concurrently; one chat has one active run and document mutations are serialized by host/document identity.
- HTML workspace state belongs to the chat, is revisioned as artifacts, and remains sandboxed with explicit network-origin permission. Undo/redo keeps the newest contiguous snapshots within both a 20-item and 2,000,000-content-character budget; bridge responses expose snapshot metadata only, while Agent tool reads expose current files/data without history bodies. WebView bridge messages are accepted only from the canonical local `web/index.html`; top-level/frame navigation, permissions, popups, and direct preview networking are restricted by host policy and CSP.
- Model request diagnostics are passive request-scoped milestones from the normal LLM pipeline (`preparing`, `sending`, headers, first data, terminal state). The bridge forwards typed `modelDiagnostics` events, and the manual connection probe uses the same completion path with bounded output and timeout; there is no background polling.
- `AssistantRuntime.Dispose` owns pane/bridge/controller shutdown and cancels active bridge requests, chat runs, and background title generation before a host adapter or dispatcher is released.
- Desktop COM calls enter adapters through the dedicated STA dispatcher. In-process VSTO adapters marshal every Office call back to the host UI thread through `OfficeUiDispatcher`; VSTO/COM changes still require Windows validation.

## Main code zones

- `src/RNAssistant.Core/Llm`: HTTP transport, message construction, response/reasoning parsing, budgets.
- `src/RNAssistant.Core/Tools/AgentResponseParser.cs`: minimal Agent JSON parser.
- `src/RNAssistant.Core/Storage`: settings, chats, tools, skills, attachments.
- `src/RNAssistant.Core/Services/ChatSessionNormalizer.cs`: format-preserving chat normalization shared by storage operations.
- `src/RNAssistant.Office/Services/AgentRunService.cs`: direct agent loop.
- `src/RNAssistant.Office/Services/AgentPromptComposer.cs`: Agent prompt and runtime context.
- `src/RNAssistant.Office/Services/PlainChatService.cs`: plain Chat flow.
- `src/RNAssistant.Office/Services/ContextCompactionService.cs`: optional checkpointing.
- `src/RNAssistant.Office/Tools`: dispatch, schemas, pipelines, tool/skill/prompt CRUD, VBA lifecycle.
- `src/RNAssistant.Office/Tools/VbaToolExecutor*.cs`: VBA host orchestration and verification in the base partial, package install/run/remove lifecycle in `.Packages`, and deterministic text patching in `.Patching`.
- `src/RNAssistant.Office/Controller`: typed bridge-facing orchestration.
- `src/RNAssistant.OfficeHosts`: Excel/Word/PowerPoint/Outlook COM adapters.
- `web/js/app-html-workspace.js`: HTML workspace state and view orchestration; sandbox assembly, artifact/plan presentation, resource-tree rendering, and mutation bridge calls live in the adjacent `app-html-workspace-preview.js`, `app-html-workspace-artifacts.js`, `app-html-workspace-tree.js`, and `app-html-workspace-actions.js` modules.
- `web/js/app-chat-session.js`: chat/document CRUD, bridge initialization, and navigation synchronization; `app-chat.js` owns composer and active-run orchestration.
- `web/js/app-tools.js`: tool catalog, package state, and run/save orchestration; schema, pipeline, and run-argument form/JSON editors live in `app-tools-structured.js`.
- Other `web/js` files remain static feature modules; no agent routing or business rules.

## Harness

Run the host-neutral fast suite with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

The suite covers the minimal Agent parser/prompt/tool-result loop, Chat isolation, storage, context, pipelines, tool safety, VBA package/backup behavior, attachments, HTML workspace, and typed bridge payloads. It has no Office COM dependency.

Windows-only validation remains: build `Debug | x64` in VS 2022 and smoke-test each Office host, desktop attach, VSTO task panes, and VBA native-host loading.
