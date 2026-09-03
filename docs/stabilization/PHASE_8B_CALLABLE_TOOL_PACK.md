# Phase 8B — callable ToolPack admission

Date: 2026-08-30
Baseline: `33dc1bb30ae5d05352747a1e5bc148c21610a62b`

Current note: this file records the Phase 8B historical profile. R61/11O6
supersedes only its initial core membership: Excel Agent now has four bootstrap,
15 Excel and two VBA editing schemas; Word/PowerPoint have bootstrap plus those two
VBA editing schemas. Rename, restore, delete and arbitrary macro execution remain
in the runnable catalog and require exact capability admission. Admission,
snapshot, budget and reconstruction rules below are unchanged.

## Scope

This host-neutral slice replaces the model-visible LRU lifecycle while preserving
the immutable execution authority delivered by Phase 8A. It does not change
`AgentKernel`, tool execution/result semantics, Resource Fabric, compaction events,
production document identity/factories, or COM/WebView wiring.

## Callable profiles

`CallableToolPack` intersects a finite exact-ID profile with the already filtered,
schema-valid run catalog:

| Mode / host | Complete initial core |
|---|---|
| Agent / Excel | six resource/capability bootstrap tools, all 15 built-in `excel.*` tools, and five public VBA/macro tools |
| Agent / Word or PowerPoint | six bootstrap tools and the five public VBA/macro tools when present |
| Agent / other host | six bootstrap tools |
| Plan | six bootstrap tools; plan/question/task schemas remain optional |
| Chat | the four read-only resource tools selected by the Chat policy |

The explicit Excel list is `inspect`, `read_range`, `find_cells`,
`create_chat_chart`, `replace_cells`, `write_range`, `add_table`, `upsert_chart`,
`delete_chart`, `format_range`, `add_sheet`, `rename_sheet`, `clear_range`,
`sort_range`, and `filter_range`. The public VBA list is
`common.vba_restore_backup`, `common.vba_write_module`,
`common.vba_apply_patch`, `common.vba_delete_module`, and
`common.office_run_macro`. Closed-document filtering happens before profile
selection, so core selection cannot restore unavailable Office tools.

## Atomic extension

- A complete exact `common.capabilities_read` tool-schema result stages only a
  catalog-matching descriptor revision delivered by the live current run. Evidence
  from another run, a changed descriptor, an error/unknown result, or truncated data
  is ignored. Raw historical evidence never restores a prior admission decision.
- All optional schemas read by one accepted model response are evaluated together
  in `ConversationModelSession.EndResponse`, before the next model request.
- Admission estimates the complete prospective messages and response options plus
  the bounded worst-case format-repair message when a repair attempt is configured.
  The model context calculation
  already reserves requested output and safety tokens.
- Success publishes every requested schema under one new callable snapshot revision
  and emits request-local `TOOL_PACK_STATE.admitted=true`. Failure publishes none,
  retains every existing schema/revision, and emits
  `tool_pack_budget_exceeded`. The rejection repeats only the bounded requested
  count, not an arbitrary ID list; exact IDs remain in the adjacent read results and
  local admission record. A rejected extension remains retryable only through a
  later exact read and successful admission.
- Membership never changes when a tool executes. The old `Touch`, linked-list LRU,
  count/token eviction loop, and `TOOL_WORKING_SET.evicted` guidance were deleted.

Capability read still reports `loaded:true` to mean that complete descriptor
evidence was returned; `admission:"already_callable_or_next_model_step"`, compact
catalog `schemaLoaded`, and `TOOL_PACK_STATE` make callable publication separate and
observable. Prompt schema 15 carries this rule.
Saved schema 14/custom prompts retain their text and require explicit review/reset.

## Bounds and failure behavior

The runnable catalog is finite and every descriptor already has the 24,000 compact
JSON-character ceiling. Optional membership is additionally bounded by the exact
next-request input budget rather than an arbitrary LRU count. Capability-result
materialization now subtracts current request options and the same repair reserve
before accepting full schema evidence. A core pack that cannot fit fails before a
provider request with an actionable prompt-budget error; an optional overflow keeps
the usable prior pack.

## Cleanup and ownership

- `ProgressiveToolWorkingSet.cs` was removed, not aliased.
- `CallableToolPack` owns only model-visible membership outside `AgentKernel`.
- Phase 8A `ToolPackSnapshot` remains the immutable execution authority; loading a
  schema grants no execution permission and cannot replace a handler/policy/binding.
- `ConversationModelSession` owns request budget and step-boundary publication.
- `PromptContextInspectorService` uses the same deterministic core for a prospective
  new run, exposes the same bounded format-repair reserve in totals/sections, and
  never imports optional evidence from an older run.

## Verification

| Filter | Pass |
|---|---:|
| `tool pack:` | 5/5 |
| `agent:` | 34/34 |
| `model protocol:` | 15/15 |
| `settings:` | 5/5 |
| `context inspector:` | 3/3 |
| `plan mode:` | 2/2 |
| `chat:` | 13/13 |
| `conversation v4:` | 13/13 |
| `bridge: typed settings` | 1/1 |
| `harness: production projects include all source files` | 1/1 |

Total: **92 distinct targeted host-neutral cases**. The harness build has 0 errors
and four existing CA1416 warnings from the guarded Excel identity probe. MockDemo
actual-controller compilation has 0 errors and three existing CA1416 PDF-rendering
warnings. The full harness and Office/VSTO execution were not run.

The regression slice covers exact core membership, Plan isolation, new revision on
successful multi-schema admission, rejection without partial publication or
eviction, live run/revision evidence checks, a real full-budget overflow through
`ConversationModelSession`, fail-closed reconstruction from rejected raw evidence,
format-repair budgeting, prompt review schema 15,
compaction characterization, Agent/Chat/Plan and strict v4 behavior.

`ValidateVersionFormat`, `git diff --check`, and 214 local links in 14 changed
Markdown files pass. Product remains `16.1.0-dev`; release/tag/push workflows were
not run.

## Remaining Phase 8 work

`TOOL_PACK_STATE` is request-local in 8B. A separate 8C must persist a typed
accepted/rejected extension event and rematerialize the exact pinned callable pack
across confirmation continuation, compaction, and crash/replay. Until then those
reconstruction boundaries fail closed to the finite core and require a new exact
read/admission; raw read evidence is never treated as proof that a rejected extension
was accepted. Remaining resource handlers/R30 and Windows x64 + Office x64 +
VS 2022 WQ-PACK also stay open.
