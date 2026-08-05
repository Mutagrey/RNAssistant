# Cleanup And Refactor Roadmap

## Current Baseline

- The experimental parallel runtime, shadow/canary paths, evidence/telemetry layers, reducers, and their fixtures are removed.
- Agent mode has one contract: AgentDecision v1 with at most one external tool per model turn.
- Alternate planner envelopes, batch-step wrappers, example-object custom schemas, single-file VBA packages, duplicate chat ids, and the separate context directory are unsupported.
- Tools execute through `OfficeToolExecutor`; pipelines cannot call Office adapters directly.
- VBA tools use only manifest-based packages with `src/*.bas` and `src/*.cls`.
- The host-neutral harness is the required fast validation path. COM/VSTO changes still require Windows x64 + Office validation.

## Next Refactor

1. Split `AgentRunService` by cohesive runtime responsibility: decision cycle, execution, and verification. Do not introduce pass-through wrappers.
2. Separate routing/catalog selection from prompt construction in `AgentPlannerRuntime`.
3. Divide `LlmClient` into request transport and response decoding while keeping endpoint fallback policy explicit.
4. Centralize repeated `ToolDefinition` and chat-model copying in small domain factories only where duplication remains measurable.
5. Reorganize the harness by subsystem and share scenario builders; delete tests for removed contracts instead of preserving compatibility fixtures.
6. Review static WebView feature ownership and move remaining feature logic out of shared boot/render files.

## Order Of Work

Refactor one boundary at a time, keep the harness green, then validate COM/VSTO behavior on Windows. Product features wait until the runtime, storage, harness, and UI boundaries are stable.
