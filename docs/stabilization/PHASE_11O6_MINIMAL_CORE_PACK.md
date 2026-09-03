# Phase 11O6 — final minimal mode/host core pack

Date: 2026-09-03
Baseline: `975e5c0`
Status: done host-neutral; live-provider latency and Windows WQ-PACK remain open

## Outcome

`CallableToolPack` now publishes the final R61 initial profiles:

| Mode / host | Initial callable schemas |
|---|---|
| Agent / Excel | 4 bootstrap + all 15 `excel.*` + `common.vba_write_module` + `common.vba_apply_patch` = **21** |
| Agent / Word or PowerPoint | 4 bootstrap + the same 2 VBA editing schemas = **6** |
| Agent / Outlook or other host | 4 bootstrap |
| Plan | 4 bootstrap |
| Chat | 2 read-only resource schemas |

`common.vba_rename_module`, `common.vba_restore_backup`,
`common.vba_delete_module` and `common.office_run_macro` stay in the complete
runnable compact catalog with `schemaLoaded:false`. An exact complete
`common.capabilities_read` result admits the selected schema only on the next
model-step boundary. No public tool, policy, binding or execution handler was
removed or aliased.

## Deterministic eval evidence

The counterfactual uses the exact post-11O5 25-schema Excel profile and the same
prompt, catalog, model settings, repair reserve and continuation reserve as the
new profile (`ContextWindowOverrideTokens=65536`, `MaxTokens=1024`).

| Scenario | Before | After 11O6 |
|---|---:|---:|
| Initial Excel schemas | 25 | 21 |
| Estimated admitted first-request tokens | 23,787 | 22,753 |
| Routine Excel inspect: model calls / tool calls | 2 / 1 | 2 / 1 |
| Routine Excel inspect: schema loads / repairs / tool errors | 0 / 0 / 0 | 0 / 0 / 0 |
| Routine Excel inspect result | success, typed read evidence | success, typed read evidence |
| Explicit macro: model calls / schema loads | 2 / 0 counterfactual | 3 / 1 |
| Explicit macro result | one dispatch, external effect unknown | one dispatch, external effect unknown |

The initial estimate decreases by **1,034 tokens (4.35%)**. The only added step is
the intentional exact admission for an explicit optional operation. The macro
scenario proves that the schema is absent initially, present after admission, and
still carries the existing high-risk confirmation/external-effect behavior.
Atomic admission also preserves the exact rename policy and native binding.

Transport latency is not inferred from the fake model; before/after live-provider
latency remains part of final WQ-PACK. Exact profile tests cover Agent/Plan/Chat on
Excel, Word, PowerPoint and Outlook, so reducing the Excel/VBA core does not alter
the Plan or Chat paths or accidentally promote other host tools.

## Verification

- `tool pack:` — 7/7.
- `agent:` — 42/42.
- `context inspector:` — 3/3; `plan mode:` — 3/3; `chat:` — 13/13;
  `conversation v4:` — 13/13.
- `vba: public tools use native runtime` — 1/1.

That is **82 distinct targeted host-neutral cases**. The Harness build has zero
errors and six existing CA1416 platform warnings from guarded COM identity and PDF
rendering paths.
- Version format and `git diff --check` pass.

The Agent sweep exposed three stale assertions from earlier R61 family switches;
they now assert the current semantic schema/hint and count typed result messages
structurally. No runtime behavior was changed for those cases.

## Next

11O7 adds UI-only built-in documentation, typed Library Test controls and the
reported Implementation/Test responsive layout fixes. The post-cutover catalog
then requires live-provider and Windows WebView2/Office WQ-PACK evidence before
Phase 12.
