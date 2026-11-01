# RNAssistant — Prototype UX and Dock Specification

> Historical UX/design source. User strategy changed on 2026-11-02:
> [UI Architecture](../ui/UI_ARCHITECTURE.md) and [Migration Plan](../ui/UI_MIGRATION_PLAN.md)
> now govern work. Prototype-only production prohibition and mandatory P0–P4
> sequence are superseded; retain this package as design evidence.

Status: proposed, subject to visual review. Do not treat proposed dimensions or locations as final.

## Shell regions
- Top bar: app identity, lightweight workspace controls, toggles for side regions, optional command/search entry. Avoid a crowded toolbar.
- Activity bar: slim persistent icon rail on left; switches the *navigation context*, not the entire application shell.
- Left sidebar: contextual navigation (chats/projects, files, materials, library), collapsible and resizable.
- Center workspace: primary dockable views; chat is a view, **not** the shell. A material, document or inspector may occupy center without chat.
- Right tool region: dockable browser, inspector, files, diagnostics or other tools. Allow multiple tab groups / splits when supported by the chosen Dockview configuration. Right-side tools may move to center.
- Status bar: concise real or demo-labeled status and access to diagnostics; not a second notification feed.

## View ownership and navigation
Each view has a stable type and ID. Navigation selects/open views; docking positions them. Do not create duplicate content simply because a view is shown in another region. Distinguish navigation tree, view tabs, contextual toolbars and transient dialogs. Settings may be an overlay or a view; test which is more usable. Library = catalog of skills/prompts/tools; Materials = resource tree and viewer for MD/PDF/images; Files = filesystem/project navigation. Do not conflate these.

## Minimum scenarios
1. Open a chat from navigation, then open a second chat; switch tabs.
2. Open inspector next to chat, split right tool region, maximize inspector, restore it.
3. Move inspector or browser into center; hide chat without losing the rest of the workspace.
4. Open Markdown/material in center; navigate related resources without redundant page transitions.
5. Open files and browser side by side; switch back to previous layout.
6. Hide/show sidebars, resize splitters, close/reopen views, restore saved layout.
7. Test narrow Office-like window, medium and large desktop sizes; define minimum useful widths and overflow strategy after observing actual results.
8. Display realistic empty/loading/running/error/completed/cancelled mock states. Progress percentage only when mock data explicitly supplies a known total.

## Dockview boundaries
Dockview handles docking, tabs and layout persistence. App shell owns top/activity/status bars and navigation. A thin adapter may map view IDs to demo content. Avoid custom drag/drop/split logic, elaborate registries and excessive wrappers. Test moving views for unintended remounts; demo success does not prove future WebView2 lifecycle behavior.

## Visual acceptance questions
- Can users tell what is navigation versus an open workspace view?
- Are important actions discoverable without excessive always-visible buttons?
- Can the inspector become a primary view without a separate screen?
- Is the layout still usable at small widths and different zoom levels?
- Is there one consistent language for tabs, toolbars, trees and status?
- Can users recover a closed view and restore a known workspace layout?

## Scope note
This phase validates **visual interaction**, not actual asynchronous operation, data paging, streaming, memory usage or Office integration. Those are later production acceptance criteria.
