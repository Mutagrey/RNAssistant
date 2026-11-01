# RNAssistant — Prototype Design and Component Rules

Status: initial constraints; visual values remain provisional until user approval.

## Visual direction
Clean, compact professional desktop workbench. Favor hierarchy and clarity over ornamental cards, thick borders, large headers or constant toolbars. Avoid imitating Dockview's stock appearance if it conflicts with the chosen look. Use restrained accent color and consistent icon style. Support dark/light themes with sufficient contrast.

## Single source of truth
Define semantic CSS tokens for surface/background, text primary/secondary, border, accent, hover, focus, danger/success, spacing scale, typography, radii, control heights and panel/tab dimensions. Components must consume tokens rather than duplicating magic values. Store actual values in CSS; docs reference token names and explain intended use. A small set of demo presets may override token values without cloning components.

## Component hierarchy
- Primitives: buttons, icons, text inputs, badges, spinners.
- Reusable composites: toolbar, tabs, tree, search field, menu, empty state, progress view, viewer frame.
- App views: chat, inspector, browser, files, materials, library, diagnostics.
Only extract a shared component when there is a real second consumer or clear standalone reuse. Do not build speculative abstractions. Keep shell-specific elements out of portable primitives.

## Behavioral and visual states
For every shared control: default, hover, focus-visible, active/selected, disabled, error where applicable. For views: empty, loading, ready, running, failed, cancelled as applicable. Distinguish determinate and indeterminate progress. Label mock operations as simulated. Do not claim actual backend progress.

## Dock styling
Use supported Dockview theming hooks, CSS variables and documented custom tab/header APIs where necessary. Avoid brittle deep overrides of undocumented internals unless isolated, documented and version-pinned. Keep panel content style independent of docking mechanics.

## Portable components
Favor browser-native HTML/CSS/JS and offline assets. Shared visual components must not depend on C# bridge, Office or RNAssistant global state. Do not build an SDK during this phase; document candidates for later extraction. For `file://`, avoid relying on fetch of local JSON or unsupported module-loading patterns; provide a tested launch method and a local-server fallback if needed.

## Duplication control
Before creating a component, search existing Demo and vendor assets. Prefer reuse, then small extension, then new component. Avoid parallel CSS stylesheets for identical controls, per-screen button implementations, separate themes per view and extra state managers. Remove replaced Demo-only code when safe.

## Design review artifacts
For each approved screen capture: name, purpose, screenshot/reference, layout mode, key interactions, states, token/component references, approval status, outstanding issues. Screenshots support the spec; they are not a substitute for interaction rules.
