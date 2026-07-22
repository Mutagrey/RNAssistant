# Code Review And Roadmap

## Findings

1. Agent output now has one path: strict planner JSON in assistant text through `AgentPlannerResponseParser`. Legacy/native command parsing was removed.
2. `Controller/AssistantController.cs` was split into controller orchestration, chat/session and context bridge parts, Core normalization/storage, Office services, transcript handling, and dedicated tool executors.
3. The WebView UI no longer has one super-file: bridge/state, settings, tools, VBA, context and chat flows are split across static `web/js/app-*.js` files. `app.js` remains boot plus shared rendering helpers.
4. Bridge payloads and common controller responses now use DTO/model contracts, including settings, tool, skill, context, VBA and focus-state messages. JSON serialization for WebView responses is isolated in `AssistantWebBridge`.
5. Chat fork now uses explicit model cloning instead of non-boundary JSON roundtrips.
6. A local non-VSTO harness covers strict parsing/rejection, prompt/settings application, chat storage, storage recovery, fake-adapter pipelines, tool safety, VBA backup, context usage, typed bridge payloads, and no-network chat completion.
7. VSTO adapter code should be treated as Windows-only. Changes there need explicit Office x64 validation.
8. Model endpoint compatibility expectations are documented in `docs/model-endpoint-compatibility.md`.
9. Pipeline safety is resolved recursively before execution; nested mutation, risk and confirmation metadata cannot be hidden.
10. Document mutation completion is tied to a fresh successful verification observation.
11. Tool/skill storage uses atomic per-item writes and no longer recreates entire user directories.
12. VBA replacement is fail-closed around rollback creation, restores original code after a failed write, and verifies controller mutations using the expected code hash.
13. Tool failures carry optional error/retry metadata; partial VBA and pipeline mutations are not automatically repeated.

## Short-Term Plan

- Add unreadable-directory storage fixtures only where the OS can simulate them reliably cross-platform.
- Review WebView CSS/UX for low-risk cleanup without adding a build pipeline.
- Keep new UI responsibilities in the matching `web/js/app-*.js` feature file; do not grow `app.js` back into orchestration.
- Continue broadening host-specific read-only inspection tools, especially Excel charts/shapes and post-mutation verification surfaces.

## Mid-Term Plan

- Add richer pipeline semantics only when concrete scenarios require them: conditions, typed outputs, and reusable verification steps.

## Project Criteria

- The assistant must be useful with only chat, context and built-in Office tools.
- Mutation must be explicit and recoverable, especially for VBA.
- Local storage must survive document rename/path change.
- UI must stay static and offline-friendly until there is a clear need for a build pipeline.
- Every new tool path must have dry-run or confirmation behavior.
