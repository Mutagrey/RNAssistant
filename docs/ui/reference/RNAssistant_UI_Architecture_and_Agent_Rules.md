# RNAssistant — UI Architecture & Agent Rules

**Version:** 1.0 (2026-11-01)  
**Status:** Target specification, pending source audit. This document does not claim that the current repository has been verified.

## 1. Strategic decision

Use **architecture-first incremental replacement**. Sequence: current-state audit and baseline → user journey / screen map → functional Dockview prototype → UX review → minimum architecture and design contracts → one production vertical slice → gradual migration and legacy removal. Do not rewrite everything simultaneously, nor copy legacy screens unchanged into a new shell. Each phase must leave the application usable, measurable and reversible.

Goals: one coherent UI, shared components, portable plugins, predictable async behavior, bounded memory, truthful status/progress, minimal dependencies and code, documented ownership and testable quality gates.

## 2. Non-negotiable architecture rules

1. **One owner per responsibility.** Shell owns chrome/navigation; Workspace owns layout; View owns presentation state; domain service owns data; runtime owns agent execution/model context; host adapter owns privileged APIs.
2. **Reuse before create.** Search component catalog, vendor assets, existing services and implementation before introducing anything new. Generalize only for real reuse or clear independence.
3. **No parallel systems.** One shell, one panel registry, one theme/token source, one command contract. Temporary dual paths require a flag, owner and removal deadline/condition.
4. **Dependency direction.** Shared UI Core must not import RNAssistant Shell, Office, C# bridge or domain views. Plugins depend on public UI/SDK interfaces, never private modules.
5. **Lifecycle is explicit.** Heavy views define mount/activate/deactivate/dispose; event listeners, subscriptions, timers and scoped operations must be released or suspended.
6. **Performance is part of correctness.** Do not introduce full-chat preload, unbounded caches, full DOM rerenders per token, main-thread heavy parsing or redundant bridge round-trips.
7. **Status is factual.** Operation events, not guessed timers, drive loading/progress/error/success. Unknown total means indeterminate progress.
8. **Offline and portability.** Bundle dependencies locally; no mandatory CDN. Shared components should run in plain browser where possible.
9. **Accessibility and consistency.** Keyboard/focus, tooltips, readable compact layouts, semantic states and light/dark themes.
10. **Delete replaced code.** Migration is incomplete until unused implementations and temporary adapters are removed after validation.

## 3. Shell and navigation contract

- **TopBar:** minimal global commands, workspace identity, sidebar toggles and search/command entry.
- **ActivityBar:** narrow left rail for primary destinations.
- **Left Sidebar:** contextual navigation tree/list/search, resizable and collapsible.
- **Workspace:** one docking system (Dockview candidate), with central and right tab groups, splits, movement, maximize, restore and persisted layout.
- **StatusBar:** connection/runtime state and real background-operation status.

| View | Default | Required behavior |
|---|---|---|
| Chat | Center | Can hide/reopen, preserve draft/scroll; not hardwired as Shell root |
| Browser | Right | Move/split/maximize; preserve session where host supports it |
| Inspector | Right | Deep-link from chat; move/maximize; actual steps/context/tools/evidence |
| Materials (MD/PDF/images) | Center | Reusable viewer; navigation separate from content |
| Files/Projects | Left | One tree implementation with contextual data adapters |
| Library (skills/prompts/tools) | Left + central detail | Catalog, not separate app framework |
| Logs | Tool panel | Paged/streamed, never eager full log loading |
| Settings | Dedicated view/overlay | Shared form controls |

**Navigation principle:** central content works without chat. Right side can be split into multiple tab groups. Separate layout persistence, view UI-state persistence and runtime task persistence. At narrow widths collapse contextual sidebars before primary content. Test embedded Excel, regular desktop, maximized inspector, narrow panel and zoomed text.

## 4. Ownership and interfaces

```text
RNAssistant Shell
  └─ Workspace / Dockview adapter (placement only)
       └─ Registered Views
            ├─ RN UI Kit (host-independent primitives/composites)
            ├─ Domain services (chat, resources, diagnostics)
            └─ RN Platform SDK (commands, events, operations)
                 └─ Host adapters (Browser / RNAssistant-WebView2-Office)
Vendor libraries: locally bundled; wrapped only when needed.
```

Panel Registry: stable ID, view type, factory, default placement, capabilities, persistence key, disposal policy. Do not use it as a global service locator. Commands from toolbar, menus and shortcuts must use one command dispatch contract. Dockview manages placement, **not** chat data, runtime state or privileged operations. Use official theming/custom tab APIs rather than fragile internal CSS overrides. Avoid adding a new manager/service merely because a layer appears in this diagram; reconcile with existing code first.

## 5. Design system and shared components

Visual direction: restrained modern developer workspace; compact tabs/toolbars, thin dividers, subtle surfaces, consistent spacing, minimal visual noise, locally bundled icons (e.g. Lucide). All screens and plugins inherit the same tokens.

Token categories: semantic colors; typography; spacing/density; dimensions; radius/borders; focus; hover/active/selected/disabled/error/loading; motion and reduced-motion; light/dark themes. No arbitrary per-screen color palette, border-radius scale or duplicate stylesheets.

Candidate components (implement only when needed): Button, IconButton, Toolbar, Tabs, TreeView, SearchInput, Dialog, Menu, Tooltip, EmptyState, ErrorState, ProgressView, StatusIndicator, VirtualList, DataTable, ResourceViewer. Shared component contract: documented API, states, keyboard/focus, theme tokens, event behavior, data ownership, cleanup, portability level, examples and version.

**Promotion rule:** domain-specific Chat/Inspector stays a domain view; reusable TreeView, progress, viewer and toolbar become UI Kit only if independent and actually useful. No speculative abstraction.

## 6. Performance and async contract

### Baseline before changes

Record startup-to-interactive, chat-open and switch latency, time to first streamed update, long UI tasks, JS heap, DOM nodes, WebView2 process memory, active listeners/subscriptions, outstanding operations, disposal and layout restore. Test cold/warm, small/large histories with tool payloads. Set acceptance thresholds from measured baseline, not invented numbers.

### Chat

- Chat list loads **paged metadata only**.
- Opening chat loads bounded recent messages; scroll up fetches older pages.
- Virtualize/bound DOM **and** bound in-memory data: DOM virtualization alone does not solve full data preload.
- Tool results summarized; large payload fetched on demand via references.
- Streaming applies incremental updates with bounded batching, not full transcript rerender.
- Switching uses request/generation IDs to ignore stale responses; abort obsolete work when possible.
- Restore draft, selected item, scroll anchor and minimum required pages; release inactive heavy DOM.
- Model-context reconstruction belongs to runtime and is **independent** of UI pagination.

### Lifecycle and cache

Every heavy view declares keep-alive/suspend/dispose policy. On deactivation pause unnecessary work; on dispose remove subscriptions, timers, object URLs, scoped tasks and references. Cache must have explicit size/TTL/eviction policy. Do not assume closing a tab instantly frees process memory; verify retained references.

### Operations/progress

Illustrative shape, adapt to current event contracts rather than duplicating them:

```json
{
  "operationId": "op-142",
  "owner": "chat:27",
  "kind": "load-messages",
  "state": "running",
  "phase": "reading",
  "progress": { "completed": 120, "total": 300, "unit": "messages" },
  "message": "Loaded 120 of 300 messages"
}
```

States should distinguish queued, running, completed, failed, cancellation-requested and cancelled when supported. Unknown `total` is `null`; never fake percentages. Show local loading for local work and StatusBar/task center for background operations. Host/runtime confirmation is required for success. Avoid universal polling, debounce or throttle patches without measurement. Avoid duplicate operation starts and cross-view status leakage.

## 7. Vendor, RN UI Kit, SDK and plugins

Keep minimal conceptual packages:

- `vendor`: locally bundled third-party libraries, version/license/provenance.
- `rn-ui`: standalone themes, tokens and UI components, **no RNAssistant dependency**.
- `rn-sdk`: optional public commands, operations, panels, resources and capabilities.
- `loader`: extend existing vendor/plugin loader if suitable; avoid second dependency framework.

The main RNAssistant app should consume the same public `rn-ui` API as plugins. Host-specific behavior is injected via adapters. Plugins must not import private Shell classes or directly manipulate Dockview internals. Host permissions must be enforced at trusted boundary; hiding JS methods is not security.

### Portability levels

- **L1:** standalone HTML/CSS/JS component, no host.
- **L2:** browser app with documented browser API requirements; `file://` support verified per browser.
- **L3:** RNAssistant host integration with explicitly required capabilities.

Export standalone projects with local version-pinned assets, relative paths and licenses. For reliable `file://`, provide tested classic-script/IIFE bundles; source can remain modular. Do **not** assume adjacent JSON `fetch`, ES module imports, workers, arbitrary filesystem reads/writes or persistent storage work consistently under `file://`. Prefer user-selected files/File API and downloads; privileged filesystem/Office access requires supported host. Detect capabilities via explicit handshake, not global-variable presence. If required host capability is missing, display clear unsupported state. Test a sample standalone plugin using TreeView, Toolbar, ProgressView and theme.

## 8. Documentation governance

Proposed authoritative documents (align with existing repository structure):

```text
docs/ui/
  UI_ARCHITECTURE.md
  UI_LAYOUT_NAVIGATION.md
  UI_DESIGN_SYSTEM.md
  UI_COMPONENT_CATALOG.md
  UI_PERFORMANCE_CONTRACT.md
  UI_ASYNC_LIFECYCLE.md
  UI_PORTABILITY_CONTRACT.md
  UI_MIGRATION_PLAN.md
  UI_PERFORMANCE_BASELINE.md
  UI_DECISIONS.md
AGENTS.md
```

This consolidated document is the initial source. Split into smaller docs only when needed, preserving **one authoritative location per rule**. Root `AGENTS.md` should link to required docs and reading order, not duplicate all text. Maintain an Architecture Decision Record for significant deviations, with rationale and consequences.

## 9. Mandatory agent workflow

**Before change:** (1) identify scenario and affected views; (2) read authoritative docs and actual code; (3) search component/vendor/SDK catalog; (4) identify state/data/lifecycle owners; (5) propose minimal files and changes; (6) assess performance and compatibility; (7) request decision for boundary-changing work.

**During change:** reuse tokens/components; keep domain logic outside UI Core; preserve lazy loading, pagination and measured optimizations; scope async work and cleanup; avoid mirrored state, new managers and redundant adapters; make small reversible changes; do not rewrite entire working screens silently.

**Before finish:** build/tests; verify open/close/move/split/maximize/restore as applicable; narrow/zoom/keyboard/error/empty/loading; check stale async results, duplicate listeners and retained hidden views; compare relevant performance baseline; test standalone plugin compatibility if shared UI changed; remove replaced code after safe cutover; report changed files, evidence, metrics, residual risks and temporary flags.

**Prohibited:** parallel Office/Workspace UI copies, second panel registry, per-view duplicated CSS themes, eager full-chat loading, fabricated progress, private bridge access from shared UI, permanent compatibility shims, untracked vendor copies, single-use generic factories, unsupported claims of performance or completion.

## 10. Migration stages and acceptance gates

**A — Audit and baseline.** Inventory screens, routes, HTML/JS/CSS/C# integration, vendor loader, duplicated components, async paths, caches, memory and known regressions. Identify optimizations to preserve. Gate: findings backed by file references and measurements.

**B — UX and information architecture.** Map all views, transitions, panel placement, navigation, responsive modes, status/error/empty states. Gate: every primary scenario has an explicit destination and interaction model.

**C — Functional shell prototype.** Small in-repo Dockview experiment with mock content; test custom headers, drag/split/maximize, persistence, resize, focus, theme. Validate WebView2-specific constraints separately. Gate: behaviors proven without new production domain services.

**D — Minimal platform contracts.** Introduce only needed tokens/components/registry interfaces; test a standalone fixture plugin. Gate: UI Core host-independent, offline bundles and documented limitations verified.

**E — First real vertical slice: Chat.** Paged list/messages, streaming, switch/restore, disposal. Gate: correct async states, no stale results, bounded memory, no measured regression; old path removable.

**F — Inspector and diagnostics.** Connect to actual runtime event/context records, avoid duplicate full payloads; deep links from chat. Gate: accurate step/context view, efficient lazy detail retrieval.

**G — Browser, Materials, Library, Files and Office surfaces.** Migrate view by view with common components and host adapters. Gate per view: UX, async, performance, portability and cleanup verified.

**H — Legacy removal and stabilization.** Delete obsolete UI paths, redundant classes, compatibility code and duplicate assets; document remaining exceptions. Gate: one authoritative implementation, regression tests pass, baseline comparison published.

Each slice has a feature flag only if necessary, with removal criteria. Do not build a second permanent app.

## 11. Decision log / open questions

Must resolve after code audit, not by assumption:

1. Actual existing UI entry points, shared component ownership and current plugin/vendor loader contract.
2. Dockview Community feature sufficiency (floating/popout/auto-hide/licensing) and WebView2 hosting constraints.
3. Whether existing chat persistence supports keyset paging and partial message/tool-payload loading.
4. Current event/log/operation model: reuse before adding a new progress/task system.
5. Browser hosting: same DOM vs separate WebView2 instances, lifetime and session preservation.
6. Supported standalone browsers and exact `file://` compatibility matrix.
7. Measured performance budgets and cache limits.
8. Existing design tokens/third-party UI assets to preserve versus replace.

## 12. Definition of done for any new screen or feature

A feature is done only when: registered through existing navigation/panel contract; uses approved design tokens and shared components; has clear data/state owner; handles loading/empty/error/success; has correct lifecycle and bounded data; does not duplicate code or vendor assets; supports required keyboard/resize scenarios; has relevant tests and performance evidence; documents any new public API; and leaves no unowned temporary path.

**Final instruction to implementation agent:** Start with a repository audit and a minimal plan. Do not implement this specification wholesale, create every suggested folder/class, or claim an architecture is already present without evidence. Prefer simplification and removal over additional layers; preserve verified performance behavior throughout migration.
