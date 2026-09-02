# Phase 11O4 — Prompt/Tool/Skill semantic authoring

Date: 2026-09-02
Scope: Prompt, custom Tool and Skill authoring contracts

## Result

- `common.prompts_save` now accepts exactly one enumerated `promptKey` and its
  complete `value`; runtime binds one field guard and preserves unrelated settings.
- Model-facing Tool authoring is exact read/upsert/delete. Separate
  `common.tools_validate`, list mode, executor/storage metadata and self-granted
  safety/capability arguments are removed. The VBA manifest owns callable metadata;
  runtime validates the complete effective definition and applies conservative
  confirmation/effect authority before write.
- Plumbing-shaped custom arguments such as `uri`, revision, cursor, token, guard,
  hash or `*Id` require an explicit `Domain identity rationale:`. The same
  fail-closed check runs when an already installed package is loaded, so direct
  file installation cannot bypass authoring validation.
- Skill core and reference authoring are four separate exact intents:
  `common.skills_upsert/delete` and
  `common.skills_reference_upsert/delete`. Mixed calls are rejected before
  preparation. The duplicate `common.vba_tool_authoring` skill is merged into
  `common.tool_authoring`.
- Model result/history projection retains semantic package ids/reference paths but
  removes revisions, hashes and storage state. Retired or old-shape accepted calls
  require explicit new chat/reset. Prompt schema is 21.
- The reviewed inventory contains 68 unique built-in IDs and 71 effective host
  variants. Its contract gate passes exactly.

## Checks

- Targeted Harness: 58/58 — `tools:` 40/40, `skills:` 5/5, prompt/settings 5/5,
  protocol-context 6/6, model projection 1/1 and pipeline exclusion 1/1.
- Architecture boundary checks: 4/4.
- The generic strict-schema fixture now honors JSON Schema `const`, allowing the
  existing Task List branches to be exercised rather than rejected by test data.
- Version-format and final diff checks pass. Existing CA1416
  platform warnings remain unchanged.

## Deferred evidence

Windows Library/WebView2 and live package execution remain mandatory. This
host-neutral result is not Windows qualification and does not make the build
stable/beta/RC.

## Next

11O5 switches the VBA/macro family while preserving exact patch/write safety and
moving backup/rename/runtime state out of model arguments. Final core-pack,
UI-only built-in documentation, typed Library Test/layout, accumulated Windows WQ
and Phase 11D4 audio remain after it; Phase 12 stays blocked.
