# Phase 11T9C6 — native prompt runtime

Date: 2026-09-01
Scope: `common.prompts_read/save`

## Result

- `PromptToolCatalog` owns the two exact Agent-only descriptors and policies.
  Read is an independent local `Read + None`; save is a confirmed
  `Write + ToolVerification` mutation.
- Native read/save handlers call one typed `PromptSettingsService`. Save prepares
  a bounded guard over the exact accepted arguments and supplied current fields,
  rejects a stale confirmation before dispatch, preserves unrelated settings and
  verifies the supplied fields after storage write. Exact no-change does not
  dispatch.
- Manual save uses the same prepare/consume path. Empty input is rejected by the
  executable schema, and exact ids/bindings have no case alias.
- `PromptToolExecutor`, `ControllerExecutorKind.Prompt` and prompt use of legacy
  command/result adapters are deleted without alias or dual dispatch.

## Checks

- Prompt native ownership/save: 1/1; settings: 5/5; typed settings bridge: 1/1;
  Plan mode: 3/3; ToolPack: 6/6; strict controller schema: 1/1.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows settings persistence, DPAPI-backed configuration and Prompts WebView UI
remain qualification evidence. Failures must fix the native service/handler path;
the removed controller executor or case-insensitive alias cannot return.

## Next

Mandatory 11J switches current custom Tool authoring/package/UI consumers to
versioned typed contracts and removes their legacy definition/result projections.
