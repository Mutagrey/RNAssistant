# Code Review And Roadmap

## Findings

1. `SkillCommandParser` mixed native `tool_calls` wrappers with local command objects. A native-style call could be parsed as `call_xxx` instead of `function.name`. Fixed by handling `tool_calls` explicitly.
2. `AssistantController.cs` owned chat state, context, prompt flow, pipeline execution, VBA patching and transcript formatting. Split into controller orchestration, chat/session partial, context partial, `AgentTranscript`, `OfficeToolExecutor`, and `PromptMessageBuilder`.
3. `web/js/app.js` remains large and mixes bridge, rendering, settings, model catalog, tools, VBA, context and boot code. Pure helpers were split to `web/js/app-utils.js`; feature modules are the next target.
4. There is no local harness for parser/pipeline/chat storage behavior. VSTO cannot be launched on this machine, so pure logic needs host-free tests.
5. VSTO adapter code should be treated as Windows-only. Changes there need explicit Office x64 validation.

## Short-Term Plan

- Continue splitting `web/js/app.js` into static non-module files loaded in order by `index.html`.
- Add parser fixtures for `rnassistant-agent`, JSON arrays, native `tool_calls`, and bad JSON.
- Add a fake `IOfficeApplicationAdapter` harness for dry-run pipeline execution.
- Add storage fixtures for new chat layout and unreadable files.
- Keep `AssistantController.cs` as orchestration only.

## Mid-Term Plan

- Move chat/session lifecycle from partial class into a dedicated service when constructor dependencies are stable.
- Move context normalization into a Core-level service if it stays Office-agnostic.
- Introduce typed bridge request/response DTOs instead of large switch payload parsing.
- Add a small compatibility matrix for model endpoint variants.

## Project Criteria

- The assistant must be useful with only chat, context and built-in Office tools.
- Mutation must be explicit and recoverable, especially for VBA.
- Local storage must survive document rename/path change.
- UI must stay static and offline-friendly until there is a clear need for a build pipeline.
- Every new tool path must have dry-run or confirmation behavior.
