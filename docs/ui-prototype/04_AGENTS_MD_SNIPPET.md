# AGENTS.md integration — instructions for the agent

> Historical UX/design source. User strategy changed on 2026-11-02:
> [UI Architecture](../ui/UI_ARCHITECTURE.md) and [Migration Plan](../ui/UI_MIGRATION_PLAN.md)
> now govern work. Prototype-only production prohibition and mandatory P0–P4
> sequence are superseded; retain this package as design evidence.

Do not overwrite the existing root `AGENTS.md`. Inspect it, preserve unrelated rules, and add a short section (or link to a scoped `AGENTS.md` under Demo if repository conventions favor that). Use actual repo paths after inspection. Do not add references to missing files as though they already exist.

## Suggested section

```markdown
## RNAssistant UI — Prototype Phase

Before changing the visual prototype, read:
- `docs/ui-prototype/00_START_HERE.md`
- `docs/ui-prototype/01_PROTOTYPE_UX_SPEC.md`
- `docs/ui-prototype/02_DESIGN_RULES.md`
- `docs/ui-prototype/03_DOCUMENTATION_WORKFLOW.md`

Scope: Demo / visual prototype only. Do not change production UI, runtime or Office bridge.
Reuse existing vendor and UI components before creating new ones. Do not build a second docking system or a new design platform. Treat unapproved visuals as proposals, not standards. After user-approved changes, update the affected UI design/UX documentation and source tokens. At checkpoints provide runnable preview steps and request explicit review. Do not start production migration without user authorization.
```

## Transition after approval
When the user approves the prototype and starts the separate production phase, update the section to point to the approved `UI_DESIGN_SYSTEM.md`, `UI_LAYOUT_UX.md`, `UI_COMPONENTS.md` (if created), the existing full UI architecture document and the production migration plan. Remove or clearly scope prototype-only prohibitions so they do not conflict with authorized production work. Keep links accurate and avoid duplicating entire specifications in `AGENTS.md`.
