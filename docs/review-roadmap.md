# Cleanup And Refactor Roadmap

## Current Baseline

- The experimental parallel runtime, shadow/canary paths, evidence/telemetry layers, reducers, and their fixtures are removed.
- Agent mode has one contract: AgentDecision v1 with at most one external tool per model turn.
- Chat sessions expose only Agent and Chat; Agent is the default and the old automatic mode is removed.
- Model-facing instructions have an immutable minimal runtime contract, one editable Agent prompt, one Chat prompt, bounded recovery/plan transitions, a structured compaction prompt and chat-title generation. Editable fields are available in Settings and through confirmed prompt tools.
- Context accounting uses the persisted accepted protocol, model-generated checkpoints plus an exact raw tail, attachments, response schema and native tool schemas; raw transcript messages are not deleted by compaction.
- Skills use progressive disclosure through `SKILL_INDEX`, `common.skills_load`, dependency/conflict validation and capability-scoped tools. Plans, attachments, compaction and HTML revisions share the chat artifact registry.
- Alternate planner envelopes, batch-step wrappers, example-object custom schemas, single-file VBA packages, duplicate chat ids, and the separate context directory are unsupported.
- Tools execute through `OfficeToolExecutor`; pipelines cannot call Office adapters directly.
- VBA tools use only manifest-based packages with `src/*.bas` and `src/*.cls`.
- Completion has one Core contract (`LlmCompletionDelegate`); agent, offline, plain-chat and title paths no longer maintain delegate adapters.
- LLM HTTP transport, multimodal message construction and response parsing are separate components.
- Planner routing, catalog slicing, prompt composition, validation and observation normalization are separate files; protocol replay, run presentation and snapshot capture are outside `AgentRunService`.
- `AppSettings` and `ToolDefinition` have typed deep clones; JSON roundtrips and reflection-based message-builder tests are removed.
- The host-neutral harness is the required fast validation path. COM/VSTO changes still require Windows x64 + Office validation.

## Next Refactor

1. Reduce the remaining `AgentRunService` decision loop by extracting execution/verification state transitions only where they form independent behavior.
2. Reorganize the harness by subsystem and share scenario builders; delete tests for removed contracts instead of preserving compatibility fixtures.
3. Review static WebView feature ownership and move remaining feature logic out of shared boot/render files.

## Order Of Work

Refactor one boundary at a time, keep the harness green, then validate COM/VSTO behavior on Windows. Product features wait until the runtime, storage, harness, and UI boundaries are stable.
