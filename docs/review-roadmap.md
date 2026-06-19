# Code Review And Roadmap

## Findings

1. `ToolCommandParser` mixed native `tool_calls` wrappers with local command objects. A native-style call could be parsed as `call_xxx` instead of `function.name`. Fixed by handling `tool_calls` explicitly.
2. `Controller/AssistantController.cs` owned chat state, context normalization, prompt flow, tool catalog composition, pipeline execution, VBA patching and transcript formatting. Split into controller orchestration, chat/session bridge methods, context bridge methods, `ChatSessionService`, `ContextService`, `ChatCompletionService`, `ToolCatalogService`, `AgentTranscript`, `OfficeToolExecutor`, `PipelineToolExecutor`, `VbaToolExecutor`, and `PromptMessageBuilder`.
3. The WebView UI no longer has one super-file: bridge/state, settings, tools, VBA, context and chat flows are split across static `web/js/app-*.js` files. `app.js` remains boot plus shared rendering helpers.
4. Bridge payloads and common controller responses now use DTO/model contracts, including settings, tool, skill, context, VBA and focus-state messages. JSON serialization for WebView responses is isolated in `AssistantWebBridge`.
5. Chat fork now uses explicit model cloning instead of non-boundary JSON roundtrips.
6. A local non-VSTO harness now covers parser, chat storage, storage recovery for broken tool/skill/VBA files, fake-adapter pipeline basics, metadata-driven tool safety, tool catalog composition, VBA patch/backup flow, context normalization/upsert and clone behavior, prompt trimming/context usage, settings/context/VBA/tool bridge payload parsing, and a no-network chat completion flow.
7. VSTO adapter code should be treated as Windows-only. Changes there need explicit Office x64 validation.

## Short-Term Plan

- Add unreadable-directory storage fixtures only where the OS can simulate them reliably cross-platform.
- Review WebView CSS/UX for low-risk cleanup without adding a build pipeline.
- Keep new UI responsibilities in the matching `web/js/app-*.js` feature file; do not grow `app.js` back into orchestration.

## Mid-Term Plan

- Move context normalization into a Core-level service if it stays Office-agnostic.
- Introduce typed bridge request/response DTOs instead of large switch payload parsing.
- Add a small compatibility matrix for model endpoint variants.

## Project Criteria

- The assistant must be useful with only chat, context and built-in Office tools.
- Mutation must be explicit and recoverable, especially for VBA.
- Local storage must survive document rename/path change.
- UI must stay static and offline-friendly until there is a clear need for a build pipeline.
- Every new tool path must have dry-run or confirmation behavior.
