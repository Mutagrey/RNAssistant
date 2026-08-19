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

- `src/RNAssistant.Core/Llm`: `LlmClient` coordinates requests, `LlmHttpTransport` owns the shared HTTP client and bounded bodies, `LlmMessageBuilder` owns multimodal API messages, and `LlmResponseParser` owns JSON/SSE/reasoning decoding. Request/response models, schema payloads, model budgets and context usage remain host-neutral.
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
- `src/RNAssistant.Office/Services/ContextCompactionService.cs`: model-generated, schema-validated context checkpoints. The source transcript is retained; only the replay window changes.
- `src/RNAssistant.Office/Services/SkillResolver.cs`: progressive skill activation, dependency/conflict validation, and capability-based tool visibility.
- `src/RNAssistant.Office/Services/ChatArtifactService.cs` and `HtmlWorkspaceArtifactService.cs`: generic chat artifacts plus immutable HTML workspace revisions used by edit/fork recovery.
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
- `web`: static HTML/CSS/JS task pane. `web/js/app-core.js` owns state and WebView bridge wiring; `app-settings.js`, `app-tools.js`, `app-skills.js`, `app-vba.js`, `app-context.js`, `app-chat.js`, and `app-artifacts.js` own their feature flows; `app-utils.js` owns pure browser helpers; `app.js` is boot plus shared rendering helpers.

## Non-Negotiable Boundaries

- Agent mode uses AgentDecision v1. Each turn is exactly one raw JSON object or one native OpenAI `tool_calls[]` response. A tool decision contains 1–8 calls; multi-call batches are restricted to independent read-only tools and execute locally in order. Mutations, local-state changes, confirmation-requiring actions and calls depending on earlier results remain single-call. Fences, surrounding prose, alternate root envelopes and legacy `function_call` are rejected.
- Agent API mode is explicit: `json_schema` by default, `json_object`, or `native_tool_calls`. Strict-schema fallback to `json_object` is permitted only before the first executed tool and persists for the rest of that run.
- Editable Chat/Agent instructions use `developer` by default; Settings may choose `system` or `user`. Tool observations use `role: tool` by default with a matching assistant `tool_calls`/`tool_call_id` pair, or `developer`/`user` for endpoints that cannot replay tool history.
- `decisionSummary`, visible goals/plans, normalized observations and deterministic verification are observable harness state. They must not contain or require chain-of-thought. Provider reasoning remains separate transport metadata.
- There are only two persisted chat modes: `Agent` (default) and `Chat`. Agent can answer without tools when routing does not require Office state. Chat is a transparent plain completion path with its own `ChatSystemPrompt`; it performs no JSON/thought-envelope repair, while provider reasoning remains separate metadata. HTML and pending agent continuations force Agent.
- Model reasoning is transport metadata (`reasoning_content`, `reasoning`, or one leading `<think>...</think>` block), stored and rendered separately; it is never mixed into planner JSON or replayed as chat history. Think tags elsewhere in ordinary content are preserved literally. The per-chat preference remains outside AgentDecision v1 and maps through the configured request transport: `reasoning_effort`, `enable_thinking`, `chat_template_kwargs.enable_thinking`, `reasoning.enabled`, or a validated `custom_json` object merged into non-reserved request fields while the toggle is enabled; model catalog metadata may override the global transport.
- `DebugModelTraffic` writes the exact Chat Completions request body and JSON response/SSE chunks as pretty-printed runtime log entries with a request correlation id. Authorization and custom header values are not logged. The WebView logs page reads a bounded tail through typed bridge DTOs; debug logging remains off by default because message bodies may contain document data.
- Context limits are token budgets resolved from the active model capability catalog or an explicit override. Message/media estimates, response schema and native tool schemas all reduce the available request/output budget.
- The persisted transcript is append-only during normal work. Accepted tool exchanges are persisted as protocol messages, including a matching assistant `tool_calls` + `role: tool` pair by default. Agent activity, rejected responses, diagnostics and provider reasoning remain outside replay.
- The task-pane UI groups a completed run under its final assistant answer. Tool work is a compact `Инструменты · N` disclosure; a multi-tool model turn is one `tool_batch` activity with an ordered child row for every call and per-call status/details.
- Context is never reduced to a fixed number of recent actions. At 80% of the input budget, `ContextCompactionService` asks the configured model for a strict structured checkpoint and keeps an exact raw tail targeting 55%. The compaction request includes bounded extracted text from referenced text/PDF attachments. The source messages remain stored. Edit/delete invalidates stale checkpoints; a fork copies the reachable checkpoint and artifacts.
- `PromptBudgetComposer` replays exactly the active checkpoint plus its contiguous raw tail. If that state still cannot fit, it fails explicitly instead of silently dropping or locally paraphrasing messages.
- If exact prompt assembly discovers additional host/tool-schema overhead after preflight, Chat/Agent may perform one model-compaction retry before the first affected model turn; Office tools are not repeated.
- Deterministic verification uses the narrowest available read tool and has a 15-second runtime timeout. A timeout ends the run with a diagnostic instead of starting another COM operation against a potentially blocked Office host.
- Text/PDF attachments are normalized locally. PDF text uses PdfPig; vision-capable models may also receive selected PDF pages rendered by the host-neutral Office service. Raw PDF files are not sent through the OpenAI-compatible chat payload.
- Attachment text, bytes and rendered PDF pages may be cached only for one logical agent run; the cache is released with the run and never becomes chat state.
- Routing precedes Office context capture. General-answer routes expose no tools and do not read document content; document-dependent state is obtained through explicit read tools.
- Tools are executable actions described by `ToolDefinition`; skills are scoped markdown guidance described by `SkillDefinition`. Every Agent request includes a compact `SKILL_INDEX`, while full bodies are included only for `ACTIVE_SKILLS`.
- `common.skills_load` is an always-visible control action, including in a full mutation slice. It activates exact skill ids, resolves dependencies, rejects cycles/conflicts, and then exposes only tool capabilities owned by active skills. A custom skill cannot self-declare built-in trust or gate built-in tools; runtime safety and confirmation rules cannot be overridden by a skill.
- Tool safety belongs to `ToolDefinition` metadata: `MutatesDocument`, `MutatesLocalState`, `AgentCanRun`, `RequiresConfirmation`, risk/capability fields, and verification metadata. Pipeline effective safety recursively includes nested steps and fails closed for missing tools, malformed definitions, duplicate step ids, and cycles. Built-in/controller ids take precedence over custom definitions.
- Agent runs are bounded by settings for max iterations and max tool steps; confirmed pending tools may resume the same run. Document mutation remains pending until a new verification observation succeeds.
- Short follow-ups for a pending task restore the latest visible plan and begin with a no-plan continuation constraint, so commands such as `Реализуй план` do not create a duplicate plan in a new run.
- Different chats may run LLM work concurrently. A chat accepts only one active run, and document mutations are serialized by host/document identity. Saving a background run never changes the selected chat.
- A required-tool route accepts `final` only after its route phase reaches `final_phase`; inspection alone cannot complete a pending mutation. Format repair and required-tool correction have separate one-shot guards.
- Provider refusal metadata is not executable assistant content. It enters the bounded format-repair path without replaying the refusal or rebuilding the tool slice. Network, transient server, and invalid transport responses receive one identical-request retry; timeouts are not duplicated.
- A terminal model answer does not mark unfinished plan steps complete. After a plan, the response schema excludes another plan until a new runtime observation exists. A model that ignores this enters bounded format repair; persistent violations stop the current run while preserving the visible plan and pending task, without adding plan spam to the transcript.
- Tool slices record explicit exclusion reasons, balance mutation/inspection capabilities, and fit both prompt and API schema representations into a bounded share of context. The per-request tool limit is configurable from 8 to 64.
- Controller coordinates request flow; it should not contain pipeline execution, VBA patch logic, or JS rendering logic.
- Office host adapters expose executable capabilities through `ToolDefinition` and `ExecuteTool`; they should not know chat/session/storage details.
- Desktop target descriptors must be validated before tool execution; a closed or mismatched target should fail instead of falling back to an unrelated active document.
- `OfficeContext` is a Core DTO. Host adapters may implement `IOfficeContextProvider`; bridge responses can expose this without requiring every adapter/fake to implement it.
- Host adapters may implement `IOfficeDocumentCatalog`; typed bridge responses merge its open-document list with persisted chat summaries, and document activation is dispatched by stable document key.
- Unsaved Office documents use the same custom document identity as saved files when custom properties are available; display names such as `Book1` are never storage keys.
- New chat sessions remain transient until they contain a completed user/assistant exchange. Empty drafts are not written to the chat store, and document-history deletion removes every stored chat for that document without deleting the Office file.
- Plans, compaction checkpoints, attachments and HTML workspace revisions are represented by `ChatArtifact`. Model requests receive a bounded metadata-only `CHAT_ARTIFACT_INDEX`; bridge DTOs expose bounded metadata/cards, while complete HTML snapshot bodies remain local. Editing a turn restores its exact HTML checkpoint, and forking from a message copies only reachable artifacts and their revision parents while attachment ids remain stable and files are copied into the fork.
- JSON metadata writes use same-directory atomic replacement. Tool/skill saves reconcile only managed entries in scope and preserve broken or unrecognized entries and extra user files. Custom tool arguments require formal object JSON Schema; invalid definitions are skipped.
- Formal schemas are also enforced immediately before controller/custom-pipeline execution, including manual runs and nested pipeline steps. Legacy host built-ins keep adapter-owned validation for backward compatibility. Exact whole-value pipeline placeholders preserve JSON primitive/array/object types.
- Literal/regexp matching is centralized in Core `TextPatternEngine` with bounded patterns/results/replacements and a regex timeout. Excel, Word, PowerPoint, and Outlook search results expose stable coordinates; replace tools require a matching search preview (`expectedMatches` + `expectedScopeSha256`) and deterministic post-write hash verification.
- A pipeline may declare at most 50 steps, nest at most eight levels, and shares the configured execution-step budget with every nested command in its execution graph.
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

- AgentDecision v1 parser/schema fixtures, exact-field and bounded multi-tool enforcement, native multi-call parsing, safe read-only batch execution, all response-format request bodies, selectable tool-result roles, matching call ids, and persistent pre-execution schema fallback;
- chat/tool/skill/VBA store fixtures using temp directories, including broken files being skipped;
- chat session lifecycle fixtures, including document-key migration;
- pipeline dry-run and execution fixtures with fake `IOfficeApplicationAdapter`;
- pipeline failure diagnostics, cycle/missing-reference rejection, recursive risk/confirmation gates, and non-retryable partial-execution reporting;
- agent runtime guards for strict decision repair, sliced tools, waiting confirmations, max iterations, max tool steps, fail-closed mutation verification/recovery, and VBA context prompt inclusion;
- model-quality fixtures that catch final answers when Office tool use is required;
- markdown skill store/catalog/prompt separation, progressive loading, dependency/conflict checks, capability-scoped tools, prompt body limiting, and agent skill-save confirmation;
- agent custom tool save/read confirmation and validation;
- metadata-driven mutation safety gates;
- regexp search/replace and bounded replacement fixtures, execution-time schema checks, and nested pipeline budget enforcement;
- VBA list/search/create/delete plus literal/regexp patch/restore flows, manifest/schema/signature validation, `.bas`/`.cls` package storage, document discovery, typed positional execution, session cleanup, persistent ownership and export-normalized hashes using fake Office/VBProject objects;
- tool catalog service merge/filter behavior;
- persistent protocol replay, model-generated context checkpoints without transcript deletion, budget overflow guards, and basic no-network chat completion flow;
- explicit Agent/Chat selection with Agent defaults, plain-chat prompt isolation, rebuilt history after deletion, and empty-tool preflight diagnostics;
- balanced tool slicing with exclusion diagnostics, prompt-budget boundaries, and strict parser boundary corpus;
- Core context normalization/upsert/trim behavior;
- typed bridge settings/context/VBA/tool/chat/artifact payload parsing, manual context compaction, agent pending-tool status, and progress envelope with streamed content deltas;
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
