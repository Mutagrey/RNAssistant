# RNAssistant — Visual Prototype Phase: Agent Assignment

> Historical UX/design source. User strategy changed on 2026-11-02:
> [UI Architecture](../ui/UI_ARCHITECTURE.md) and [Migration Plan](../ui/UI_MIGRATION_PLAN.md)
> now govern work. Prototype-only production prohibition and mandatory P0–P4
> sequence are superseded; retain this package as design evidence.

Status: implementation instructions for prototype phase only. Date: 2026-11-01.

## Mission
Transform the existing Demo into a working, independent, visually coherent prototype of the future RNAssistant shell. Focus on appearance, layout, navigation, Dockview interactions, realistic mock states and user review. **Do not migrate production UI or modify agent runtime.** This package is the authority for prototype work; the separate full UI architecture/migration plan is reserved for a later, explicitly approved phase.

## Execution order and gates

### P0 — Inspect, do not rebuild blindly
1. Locate Demo entry points, existing UI assets, vendor mechanism and any current Dockview usage.
2. Identify what can be retained, replaced or removed *within Demo*. Confirm that production files are not being modified.
3. Identify the actual browser launch path (`file://` if feasible; otherwise provide an explicit minimal local-server fallback).
4. Give the user a concise current-state report, target screen map and proposed first shell layout. Do not begin broad refactoring.

### P1 — Working shell, review checkpoint
Implement the shell using locally bundled vendor assets: top bar, left activity bar, collapsible navigation sidebar, central dockable workspace, right-side tool area, status bar. Dockview should own docking/tab groups; do not implement a second docking engine. Use mock panels. Show the result and obtain user feedback **before** detailed screens.

### P2 — Visual language, review checkpoint
Establish a compact, consistent design using shared tokens, dark and light themes, clear hierarchy, accessible focus/hover states, restrained borders, consistent iconography, and controlled density. Show at least two alternative visual treatments of the *same layout*, without forking the underlying component code. Ask the user to select or revise; record approval only on explicit confirmation.

### P3 — Screen scenarios, iterative review
Build realistic mock views for chat, inspector, browser, files, materials, library, diagnostics/logs and settings. Validate opening, closing, tabbing, moving, splitting, maximizing, restoring and working without chat. Cover loading, empty, running/progress, success, error and cancellation visually. Mock progress must be clearly demo-only. Validate narrow and wide viewports and Office-constrained dimensions. Review in small batches rather than building everything before feedback.

### P4 — Freeze approved prototype and handoff
On explicit user approval, mark approved layouts, tokens, components and interaction rules as accepted. Finalize the docs described in `03_DOCUMENTATION_WORKFLOW.md`, capture screenshots/reference states and identify any unresolved decisions. Provide a concise handoff to the separate production migration phase. **Do not start production migration automatically.**

## Hard boundaries
- No production UI, C#, WebView2 bridge, Office integration, agent runtime, chat storage, performance refactor or migration changes.
- No new general-purpose UI framework, plugin SDK, visual editor, automated documentation platform or second vendor loader.
- Do not copy old production screens wholesale. Use lightweight mock data and purpose-built visual fixtures.
- No CDN or network runtime dependencies. Preserve offline operation.
- Prefer plain HTML/CSS/JavaScript and existing vendor assets. Add dependencies only with rationale and licensing check.
- Do not claim Dockview supports a capability without testing the locally used version. If a feature is unsupported, document the limitation and offer a simpler UX alternative.
- Keep the prototype easy to delete or replace. No production architectural promises based solely on demo behavior.

## Definition of done
- The demo starts using documented steps and runs without RNAssistant.
- All core shell regions and mock views can be reached without confusing navigation.
- Docking behavior and layout persistence are demonstrated and documented; unsupported features are disclosed.
- Themes and core components use centralized tokens; no duplicate implementations for alternative looks.
- Approved decisions and unresolved questions are clearly separated in docs.
- No production files were changed; provide `git diff` evidence and a file-level change summary.
- User has reviewed and explicitly accepted the prototype for later production planning.

## Agent response discipline
At every checkpoint: show what changed, how to launch, 2–5 concrete interactions to test, screenshots if available, known limitations, and the exact decision requested from the user. Never claim visual approval from silence.

## Reading order
Read this file, then `01_PROTOTYPE_UX_SPEC.md`, `02_DESIGN_RULES.md`, `03_DOCUMENTATION_WORKFLOW.md`, `04_AGENTS_MD_SNIPPET.md`. Follow existing repository instructions where compatible; report conflicts rather than silently overriding them.
