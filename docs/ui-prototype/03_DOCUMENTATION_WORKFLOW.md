# RNAssistant — Design Documentation Lifecycle

## Principle
Documentation is a maintained design record, not an independent implementation and not a generator platform. Agent must distinguish **Proposed**, **In Review**, **Approved**, **Superseded** and **Open Decision**. Never promote a design to Approved without explicit user approval.

## Files to maintain (create only when meaningful content exists)
- `docs/ui/UI_DESIGN_SYSTEM.md`: approved visual principles, semantic tokens, typography, control states, shared component references and design examples. CSS is the source of actual token values.
- `docs/ui/UI_LAYOUT_UX.md`: shell zones, view map, navigation rules, docking behavior, responsive rules, layout presets and screen-specific interactions.
- `docs/ui/UI_COMPONENTS.md`: lightweight inventory of shared components, source paths, props/usage, states and reuse rules. Keep as a section of Design System until large enough to justify separation.
- `docs/ui/UI_DECISIONS.md`: compact chronological decisions: date, status, decision, rationale, affected components/screens and any superseded choice.
- `docs/ui/UI_PROTOTYPE_HANDOFF.md`: final approved screenshots, launch instructions, scenarios tested, limitations, unresolved questions and implications for later production architecture.

## When to write
- At kickoff: document current proposed shell and explicit open decisions; do not invent final values.
- After each review: update only the decisions actually confirmed, then update affected specs and tokens/code in the same change.
- After a design change: search all affected views/components, remove stale instructions, mark replaced decisions superseded and check visual consistency.
- At handoff: freeze an approved baseline by commit/tag/reference and list what still requires production validation.

## Rule for consistency
One concept has one authoritative home. CSS owns exact design token values; layout configuration owns demo layout presets; Markdown explains intent and usage; `AGENTS.md` only routes to these sources. Do not copy token tables across multiple documents. If a component or view is removed, remove obsolete docs and links in the same change.

## Checkpoint report template
- Implemented: paths and concrete visible behavior.
- Demo launch: exact tested steps.
- Review: screenshots or reproducible steps, scenarios and sizes.
- Decision needed: clear alternatives and tradeoffs.
- Docs: created/updated, statuses changed.
- Scope: evidence that production was untouched.

## Production boundary
The later architecture/migration document is a separate input. Do not begin migration, create production-specific lifecycle abstractions, or claim performance equivalence until the user explicitly starts that phase.
