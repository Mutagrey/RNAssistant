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

- `src/RNAssistant.Core/Llm`: `LlmClient` owns HTTP transport, `LlmMessageBuilder` owns multimodal API messages, and `LlmResponseParser` owns JSON/SSE/reasoning decoding. Request/response models, schema payloads, model budgets and context usage remain host-neutral.
- `src/RNAssistant.Core/Tools`: strict AgentDecision parsing, dynamic response schema, formal tool-schema validation and VBA manifest parsing.
- `src/RNAssistant.Office/Services/BuiltInSkillProvider.cs`: common built-in markdown skills; host adapters provide application-specific skills through `IOfficeBuiltInSkillProvider`.
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
- `src/RNAssistant.Office/Services/ChatRunRegistry.cs`: in-memory per-chat run ownership, live status/current action, and cancellation addressed by chat/run id; switching the selected chat never transfers or cancels a run. A lightweight persisted marker converts abandoned runs to `cancelled` after an application restart.
- `src/RNAssistant.Office/Services/HtmlNetworkService.cs`: permission-gated HTTP(S) transport for sandboxed HTML workspace previews.
- `src/RNAssistant.Office/Services/AgentRunService.cs`: controlled planner loop and route/slice/validate/execute/verify orchestration.
- `src/RNAssistant.Office/Services/AgentPlannerCompletionRunner.cs`: response-mode selection, pre-execution `json_schema` fallback, planner completion streaming, native/text parsing, and one bounded format-repair attempt.
- `OfficeIntentRouter`, `ToolCatalogSlicer`, `PlannerPromptComposer`, `AgentActionValidator`, and `ObservationNormalizer` each own one planner responsibility.
- `AgentProtocolHistory`, `AgentRunPresentation`, and `OfficeSnapshotReader` own protocol replay, observable run UI/diagnostics, and Office context capture respectively.
- `src/RNAssistant.Office/Services/AgentRuntimeModels.cs`: route, observation, catalog slice, and run-state models.
- `src/RNAssistant.Office/Services/AgentExecutionRuntime.cs`: effective tool catalog resolution and phase transitions.
- `src/RNAssistant.Office/Services/AgentVerificationRuntime.cs`: deterministic verification selection and result validation.
- `src/RNAssistant.Office/Tools`: tool execution, one shared pipeline parser, controller-tool definitions/dispatch, tool/skill CRUD tools, VBA package lifecycle and VBA patch/backup workflow.
- `src/RNAssistant.OfficeHosts`: shared Excel/Word/PowerPoint/Outlook COM adapters and desktop target descriptors.
- `src/RNAssistant.Desktop`: standalone WinForms shell, explicit Office target picker, manual foreground attach, single-instance JSON pipe activation, and ROT-based adapter creation with hwnd validation.
- `src/RNAssistant.NativeHostCli`: thin C++/CLI exported-DLL host for VBA; owns
  only modeless window lifecycle, owner/positioning and managed assembly loading.
- `src/RNAssistant.*AddIn`: VSTO host wiring; no host adapter ownership.
- `wrappers/native`: VBA source modules for Office-native launchers.
- `web`: static HTML/CSS/JS task pane. `web/js/app-core.js` owns state and WebView bridge wiring; `app-settings.js`, `app-tools.js`, `app-skills.js`, `app-vba.js`, `app-context.js`, and `app-chat.js` own their feature flows; `app-utils.js` owns pure browser helpers; `app.js` is boot plus shared rendering helpers.

## Non-Negotiable Boundaries

- Agent mode uses AgentDecision v1. Each turn is exactly one raw JSON object or one native OpenAI `tool_call`; one turn can select only one external tool. Fences, surrounding prose, alternate envelopes, arrays, `function_call`, and parallel tool calls are rejected.
- Agent API mode is explicit: `json_schema` by default, `json_object`, or `native_tool_calls`. Strict-schema fallback to `json_object` is permitted only before the first executed tool and persists for the rest of that run.
- Editable Chat/Agent instructions use `developer` by default; Settings may choose `system` or `user`. Tool observations use `role: tool` by default with a matching assistant `tool_calls`/`tool_call_id` pair, or `developer`/`user` for endpoints that cannot replay tool history.
- `decisionSummary`, visible goals/plans, normalized observations and deterministic verification are observable harness state. They must not contain or require chain-of-thought. Provider reasoning remains separate transport metadata.
- Chat mode is a plain completion path with its own `ChatSystemPrompt`, without planner/tool prompts. Thought/reasoning JSON in content is never persisted: a user-facing field is extracted or one bounded repair is requested. Auto mode chooses Chat or Agent before the model request; the selected mode is persisted per chat.
- Model reasoning is transport metadata (`reasoning_content`, `reasoning`, or one leading `<think>...</think>` block), stored and rendered separately; it is never mixed into planner JSON or replayed as chat history. Think tags elsewhere in ordinary content are preserved literally.
- Context limits are token budgets resolved from the active model capability catalog.
- Chat and planner context are rebuilt from the active session for every request. They use reference-deduplicated notes plus recent user/assistant messages and their attachments; agent activity, diagnostics, and reasoning metadata are never replayed.
- Chat and Agent share `PromptBudgetComposer` for chronological history selection and attachment accounting. Once a recent message exceeds the remaining budget, older history is not reintroduced.
- Deterministic verification uses the narrowest available read tool and has a 15-second runtime timeout. A timeout ends the run with a diagnostic instead of starting another COM operation against a potentially blocked Office host.
- Text/PDF attachments are normalized locally. PDF text uses PdfPig; vision-capable models may also receive selected PDF pages rendered by the host-neutral Office service. Raw PDF files are not sent through the OpenAI-compatible chat payload.
- Routing precedes Office context capture. General-answer routes expose no tools and do not read document content; document-dependent state is obtained through explicit read tools.
- Tools are executable actions described by `ToolDefinition`; skills are markdown guidance described by `SkillDefinition`.
- Tool safety belongs to `ToolDefinition` metadata: `MutatesDocument`, `MutatesLocalState`, `AgentCanRun`, `RequiresConfirmation`, risk/capability fields, and verification metadata. Pipeline effective safety recursively includes nested steps and fails closed for missing tools, malformed definitions, duplicate step ids, and cycles. Built-in/controller ids take precedence over custom definitions.
- Agent runs are bounded by settings for max iterations and max tool steps; confirmed pending tools may resume the same run. Document mutation remains pending until a new verification observation succeeds.
- Different chats may run LLM work concurrently. A chat accepts only one active run, and document mutations are serialized by host/document identity. Saving a background run never changes the selected chat.
- A required-tool route accepts `final` only after its route phase reaches `final_phase`; inspection alone cannot complete a pending mutation. Format repair and required-tool correction have separate one-shot guards.
- Tool slices record explicit exclusion reasons and reserve prompt capacity for both mutation and inspection tools. The per-request tool limit is configurable from 8 to 64.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- Office host adapters expose executable capabilities through `ToolDefinition` and `ExecuteTool`; they should not know chat/session/storage details.
- Desktop target descriptors must be validated before tool execution; a closed or mismatched target should fail instead of falling back to an unrelated active document.
- `OfficeContext` is a Core DTO. Host adapters may implement `IOfficeContextProvider`; bridge responses can expose this without requiring every adapter/fake to implement it.
- Host adapters may implement `IOfficeDocumentCatalog`; typed bridge responses merge its open-document list with persisted chat summaries, and document activation is dispatched by stable document key.
- Unsaved Office documents use the same custom document identity as saved files when custom properties are available; display names such as `Book1` are never storage keys.
- New chat sessions remain transient until they contain a completed user/assistant exchange. Empty drafts are not written to the chat store, and document-history deletion removes every stored chat for that document without deleting the Office file.
- JSON metadata writes use same-directory atomic replacement. Tool/skill saves reconcile only managed entries in scope and preserve broken or unrecognized entries and extra user files. Custom tool arguments require formal object JSON Schema; invalid definitions are skipped.
- Global VBA tools are versioned packages with `tool.json` plus `src/*.bas`/`src/*.cls`. Document-local manifests are discovered through VBProject. Temporary injection is automatic and cleaned in `finally`; persistent install requires a macro-enabled document and ownership/hash checks. See `docs/vba-tool-packages.md`.
- Desktop COM automation must enter host adapters through `DispatchedOfficeApplicationAdapter`/`OfficeStaDispatcher`; VSTO task panes already run inside their Office host process and remain Windows-validation-only.
- Desktop target selection uses `Auto follow` by default. `Manual` mode pins the selected working target; Excel task panes refresh on workbook activate/open/close events.
- The Desktop target registry stores only lightweight descriptors, not long-lived Office COM objects.
- UI sends typed bridge messages; business rules stay in C# unless they are purely presentation behavior.
- HTML workspace preview remains sandboxed. Its HTTP(S) `fetch` compatibility layer uses typed host messages, requires an explicit per-origin permission, strips credential headers, and enforces redirect/time/size limits.
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

Pass a category or name fragment for a focused run, for example `-- modes`, `-- routing`, or `-- context`. Pass `--list` to print the categorized catalog.

Current coverage:

- AgentDecision v1 parser/schema fixtures, exact-field and one-tool enforcement, native tool-call parsing, all response-format request bodies, selectable tool-result roles, matching call ids, and persistent pre-execution schema fallback;
- chat/tool/skill/VBA store fixtures using temp directories, including broken files being skipped;
- chat session lifecycle fixtures, including document-key migration;
- pipeline dry-run and execution fixtures with fake `IOfficeApplicationAdapter`;
- pipeline failure diagnostics, cycle/missing-reference rejection, recursive risk/confirmation gates, and non-retryable partial-execution reporting;
- agent runtime guards for strict decision repair, sliced tools, waiting confirmations, max iterations, max tool steps, fail-closed mutation verification/recovery, and VBA context prompt inclusion;
- model-quality fixtures that catch final answers when Office tool use is required;
- markdown skill store/catalog/prompt separation, prompt body limiting, and agent skill-save confirmation;
- agent custom tool save/read confirmation and validation;
- metadata-driven mutation safety gates;
- VBA replace/patch/restore flows plus manifest/schema/signature validation, `.bas`/`.cls` package storage, document discovery, typed positional execution, session cleanup, persistent ownership and export-normalized hashes using fake Office/VBProject objects;
- tool catalog service merge/filter behavior;
- prompt message trimming, context usage estimates, and basic no-network chat completion flow;
- explicit Chat/Auto/Agent routing, plain-chat prompt isolation, rebuilt history after deletion, and empty-tool preflight diagnostics;
- balanced tool slicing with exclusion diagnostics, prompt-budget boundaries, and strict parser boundary corpus;
- Core context normalization/upsert/trim behavior;
- typed bridge settings/context/VBA/tool/chat payload parsing, agent pending-tool status, and progress envelope with streamed content deltas;
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
