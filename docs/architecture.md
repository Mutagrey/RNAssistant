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

`AgentPromptComposer` creates one `RUNTIME_CONTEXT` JSON object containing document identity, all runnable tools in native-like function format, a compact enabled-skill catalog, chat context, and artifact references. Invalid, unavailable, or dependency-incomplete tools are omitted before the request; the remaining catalog is never silently sliced. If the complete request does not fit, the run stops with a prompt-budget diagnostic. Relevant complete skill Markdown is loaded by the model through `common.skills_read`. The composer does not inspect or classify request wording and does not capture Office content eagerly. A visible plan, when useful, is an ordinary versioned chat artifact written explicitly through plan CRUD tools rather than runtime planner state.

Editable Agent, Chat, title, and compaction prompts are stored as Markdown. Their instruction role (`developer`/`system`/`user`) is independent from Agent response format (`json_object`/strict `json_schema`) and tool-result role (`user`/`developer`/matched `tool`).

`AgentResponseParser` accepts either a final `message` or one or more `tool_calls` entries with ids unique within that response. `json_schema` derives a strict response contract from the current runnable tools; original optional properties become nullable so the endpoint does not force the model to invent irrelevant values. Before executable-schema validation, optional nulls are removed and declared defaults are applied. `json_object` uses the same envelope with local validation. Invalid output gets bounded 1–20 ephemeral format-correction attempts; each starts from clean accepted history and invalid content never enters replay. `OfficeToolExecutor` remains the authority for formal argument schemas, effective pipeline safety, confirmation, and dispatch. `AgentJsonProtocol` serializes each result to `{ok, tool_call_id, name, status, message, data, error}` and emits the selected replay role.

Before a turn mutates a chat, the controller acquires a per-chat lease backed by an in-process registry and a cross-process lock file, reloads a newer persisted revision, and saves the user request, committed attachment references, and `LastRun` before calling the model. Attachment drafts are retained until both the message and final attachment paths are durable. Tool-start and tool-result boundaries are checkpointed as well. Chat JSON uses monotonic `Revision` compare-and-swap plus atomic replacement, so a stale window fails instead of overwriting a newer transcript. A confirmation pause persists its pending id and cumulative iteration/tool counters; a new request is rejected until the action is confirmed or cancelled. Confirmation acquires a new lease and resumes the same logical budget. On startup, a persisted `running`/`cancelling` run without a live owner is marked interrupted and is not resumed automatically. Only a run stopped while a tool may have been in flight is marked `interrupted_unknown` and excluded from protocol replay; already persisted results stay replayable.

See [agent-protocol.md](agent-protocol.md).

## Important boundaries

- Controller files coordinate bridge requests; reusable behavior stays in services and executor logic stays in `RNAssistant.Office/Tools`.
- Tools are executable capabilities. Skills are Markdown instructions discovered through the compact catalog and read through one normal tool; there is no activation/dependency runtime.
- Model-facing tools are grouped by user intent, not backend COM primitives. Collection/item reads share selectors (`excel.inspect`, `word.inspect`, `powerpoint.list_objects`); range values/formulas/profile share `excel.read_range`; scalar/formula/table writes share `excel.write_range`; chart create/update share `excel.upsert_chart`; formatting/autofit share `excel.format_range`; PowerPoint text/notes and added objects use `set_text`/`add_object`; Outlook message/attachment reads and simple updates use `read_mail`/`update_mail`; HTML file/static-data writes share one upsert. Tool/skill create+update use idempotent upsert with optional strict modes. Operations with materially different payload, destructive, or confirmation semantics remain separate instead of using a large union schema. Removed public ids remain runtime-only aliases and are canonicalized inside saved pipelines.
- Tool safety lives in `ToolDefinition`: mutation, local-state, confirmation, risk, capability, and `AgentCanRun` metadata. Prompts cannot bypass it.
- Custom tools require a strict object JSON Schema with documented arguments. Pipelines invoke existing ids through `OfficeToolExecutor` and cannot call adapters directly.
- Public VBA editing is a compact host-neutral `common.vba_*` facade; host-prefixed ids are internal COM backends. Whole-source write is an upsert, range reading is part of the main read tool, and mutation snapshots are acquired internally rather than through mandatory model preflight calls. Legacy redundant ids remain canonicalized aliases only. Backup, ownership, runtime-bound guard, confirmation, and stale-state protections stay inside VBA execution; see [vba-tool-packages.md](vba-tool-packages.md).
- Accepted agent calls/results are hidden protocol messages. Visible tool activity is presentation only and is excluded from model context. Activities from one accepted model turn share a `StepId` and its user-facing `message`, so the UI can group batch calls and results without inferring boundaries from tool names or ordering heuristics.
- Provider reasoning is stored separately from agent JSON.
- Context belongs to the active chat. Compaction stores a model-produced checkpoint and an exact raw tail without deleting the source transcript or splitting a tool exchange.
- A v3 chat session JSON remains the canonical transcript and artifact metadata record. JSON stores stream into temporary files and atomically replace their targets instead of building another full serialized string in memory. Immutable HTML artifact bodies live in session-scoped `html-artifact-bodies` sidecars: only the active revision is hydrated on chat load, while edit/fork/rewind load older bodies on demand. Forks copy their reachable bodies independently, and prune/chat/document/runtime cleanup removes unused files. Supported v1/v2 inline bodies migrate on the next save. Small fingerprinted `.summary.json` sidecars provide list/lookup metadata, are updated atomically after a session save, and are rebuilt lazily from supported canonical sessions when missing, stale, or invalid.
- Attachments use the selected model. There is no automatic attachment-model router or endpoint failover.
- A chat's non-empty `Model` overrides the global default through one cloned effective-settings resolver; requests, title generation, context budgets, and compaction use the same effective model without mutating stored global settings.
- Different chats may run concurrently; one chat has one active operation across RNAssistant windows. Runtime reset, document deletion, chat creation, document-identity migration, and background title writes use a cross-window coordination gate, so maintenance cannot race a newly started chat. Shutdown signals cancellation but retains each chat lease until that run actually exits, including a slow/non-cooperative COM call. Chat-local plan/HTML mutations use the chat lease; document and shared-local-state mutations use ordered cross-process file locks with a bounded wait, plus an in-process fallback when no storage root is available. Lock contention is a retryable failure; an unexpected mutation exception is non-retryable until state is inspected because its effect is uncertain. Every Agent run pins the runtime COM identity, with the stable document key as fallback. Office adapters derive runtime identity from canonical COM `IUnknown`, so a new RCW for the same document does not look like a document switch.
- HTML workspace state belongs to the chat, is revisioned as artifacts, and remains sandboxed with explicit network-origin permission. A data source may carry a binding to a `CanSourceHtmlData` read-only adapter tool; bind validates its exact schema, refresh replays it locally while retaining prior JSON on failure, and freeze removes the binding. Automatic refresh does not add undo/artifact revisions; the next chat turn checkpoints current live data normally. Undo/redo keeps the newest contiguous snapshots within both a 20-item and 2,000,000-content-character budget; bridge responses expose snapshot metadata only. An Agent read without arguments returns a compact manifest; `resourceType` plus `name` reads one exact file or data source. WebView bridge messages are accepted only from the canonical local `web/index.html`; top-level/frame navigation, permissions, popups, and direct preview networking are restricted by host policy and CSP.
- The Artifacts UI shows chat-owned artifacts and HTML workspace state only. The active document's VBA project is a live document resource in a separate VBA tab, not a `ChatArtifact`: chat fork/prune/rewind does not copy, remove, or restore it. The VBA tab can create a blank UserForm and edit its code-behind; visual Designer/FRX state is outside the protocol. UI deletion is limited to standard/class modules and goes through current-hash validation plus rollback backup.
- Model request diagnostics are passive request-scoped milestones from the normal LLM pipeline (`preparing`, `sending`, headers, first data, terminal state). The bridge forwards typed `modelDiagnostics` events, and the manual connection probe uses the same completion path with bounded output and timeout; there is no background polling.
- `AssistantRuntime.Dispose` owns pane/bridge/controller shutdown and cancels active bridge requests, chat runs, and background title generation before a host adapter or dispatcher is released.
- Each Excel window pane is bound directly to its workbook COM object and only the active pane is refreshed on window changes; inactive WebViews do not rescan tools or take focus.
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
- `web/js/app-html-workspace.js`: HTML workspace view orchestration; normalized data/selection rules, editor state/rendering, sandbox assembly, artifact/plan presentation, resource-tree rendering, and mutation bridge calls live in the adjacent `app-html-workspace-model.js`, `app-html-workspace-editor.js`, `app-html-workspace-preview.js`, `app-html-workspace-artifacts.js`, `app-html-workspace-tree.js`, and `app-html-workspace-actions.js` modules.
- `web/js/app-chat-session.js`: chat/document CRUD, bridge initialization, and navigation synchronization; composer rendering/input state lives in `app-chat-composer.js`, send/retry/cancel, run tracking, and Agent tool decisions in `app-chat-run.js`, while `app-chat.js` keeps chat-level actions and bindings.
- `web/js/app-model-render.js`: model info/status coordination; catalog selects and the composer picker live in `app-model-picker.js`, while editable capability overrides live in `app-model-capabilities.js`.
- `web/js/app-tools.js`: tool catalog and editor state; schema/pipeline/run-argument editors live in `app-tools-structured.js`, while save/run and VBA package bridge calls live in `app-tools-actions.js`.
- `web/js/app-vba.js`: VBA editor modes and UI bindings; the separate project tree and lazy module loading live in `app-vba-project.js`, diff calculation/rendering in `app-vba-diff.js`, and save/delete/restore/run bridge calls in `app-vba-actions.js`.
- `web/js/app-agent.js`: Agent run grouping and article composition; individual activity rendering lives in `app-agent-activity.js`, while pending-confirmation traversal and the approval dock live in `app-agent-approval.js`.
- Other `web/js` files remain static feature modules; no agent routing or business rules.

## Harness

Run the host-neutral fast suite with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

The suite covers the Agent parser/prompt/tool-result loop, durable confirmation counters, chat revision conflicts and run leases, Chat isolation, storage, context, pipelines, tool safety, VBA package/backup behavior, attachments, HTML workspace, and typed bridge payloads. It has no Office COM dependency.

Windows-only validation remains: build `Debug | x64` in VS 2022 and smoke-test each Office host, desktop attach, VSTO task panes, and VBA native-host loading.
