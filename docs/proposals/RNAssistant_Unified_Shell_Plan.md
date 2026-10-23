# RNAssistant — Unified Shell, Chat, Workspace and Browser

Status: architecture proposal for agent review, not an approved code-level design. Date: 2026-10-23.
Related: RNAssistant_Visual_Annotator_Plan.md. This document takes precedence for shell/chat/workspace decisions.

## 1. Agent mandate

First audit the current repository and relevant docs. Identify the actual three chat implementations (Office, Project, Plugin), their UI, composer, state, persistence, agent invocation, resource access, navigation, WebView2 lifecycle, and host bridges. Cite actual files/functions in an audit document. Determine WHY they diverged, separating real Office/COM and plugin security constraints from accidental duplication. Evaluate and challenge this proposal before coding. Preserve functionality and user histories. Implement in small stages with characterization tests, rollback and measurements; do not rewrite the app at once.

Deliver: (a) current-state audit with code map and duplication matrix; (b) RFC of accepted/rejected decisions and alternatives; (c) tests and phased implementation; (d) updated documentation, migrations and regression report. Record open decisions rather than silently guessing.

## 2. Desired UX

One common chat UI, transcript, composer, attachment chips, streaming status, history and agent entrypoint. Initially one visible active chat; future multiple chat tabs without code duplication. Office/Project/Plugin changes context and capabilities, never the chat's visual implementation.

Layout:
- Narrow global rail on far left: Chats, Workspaces/Projects, Plugins, Office and settings.
- Collapsible/resizable tree beside rail: workspaces, projects and chats. Model chat nodes separately from folder nodes.
- Center: the same chat and composer for every context.
- Right: embedded WebView2 browser / HTML preview / plugin / resource inspector, resizable and closable.

Selecting a plugin in the rail: resolve plugin's workspace, select its tree node, collapse tree by default, retain the common center chat, and open the plugin in the right browser. The user can reopen the tree; this must not alter the chat or workspace. Switching Office/Project changes selected context without swapping chat implementations. Browser navigation never creates a new chat implicitly.

Persist draft, selected chat, scroll, pane sizes, right preview and navigation state separately. Add back/forward, keyboard/focus behavior and responsive narrow layouts. Avoid recreating WebView2 or duplicating event subscriptions on every transition.

## 3. Core separation of concerns

- **ChatSession**: stable ID, message/event history, draft, linked workspace. Not a UI component or workbook.
- **Workspace**: logical context with named mounts/providers, grants and defaults; not necessarily a physical folder.
- **ResourceProvider/Mount**: Office workbook/ranges/VBA, filesystem project, plugin package, library resources. Use existing resource registry, CAS and Context Explorer rather than building a second one.
- **Surface**: browser preview, file/resource preview, Office inspector, chat. A surface does not own session history.
- **Shell**: navigation, pane layout and restoration; no agent execution or storage business logic.
- **Agent Runtime**: one execution path; context assembly and tool availability vary by workspace mounts/grants.

Conceptual relationships:

```
Shell
  GlobalRail | WorkspaceChatTree | ChatView+Composer | RightSurfaceHost
                                    |
                              ChatSessionService
                                    |
                              existing AgentRuntime
                                    |
                              WorkspaceContext
                                    |
                    Office / Files / Plugin / Library providers
RightSurfaceHost -> BrowserSurface -> Inspector -> Annotations -> Resource refs -> Composer
```

Prefer adapting existing interfaces. Do not introduce a universal class with dozens of nullable properties or a chain of thin wrappers. One active chat now, future tab model: tab ID != session ID != workspace ID != surface ID.

## 4. Office vs Project: questions to answer in the audit

Office resources may be live, unsaved, document-scoped, COM-thread-bound and persisted in CustomXML; folder resources are path-based and persist independently of Excel; plugin previews execute JavaScript and require distinct security boundaries. These differences belong to resource providers, persistence adapters and permission grants, not separate chat renderers.

Investigate:
1. Is Office chat bound to workbook, Excel window, add-in, or global session? What happens on document switch, close/reopen, save-as, read-only and unsaved workbooks?
2. Where do Office, Project and Plugin messages persist? What identifiers are stable? Can legacy histories be read through one logical ChatSessionService with storage adapters?
3. Does Project chat use different runtime/tools/context-building, or only a different mount? Where are permission boundaries?
4. Is Plugin better modeled as workspace + preview surface, or is an isolated session required for security? What is the lifecycle of plugin files and preview?
5. Can a workspace mount both an Office workbook and a project folder? What happens if either is unavailable? Never route writes to an arbitrary active workbook.
6. What duplication exists in composer, message rendering, streaming, retry, event listeners, navigation, resource resolution, and state restoration?

Default hypothesis: one WorkspaceContext with zero or more resource mounts, capability grants and optional active document. One logical ChatSessionService with legacy persistence adapters. Prefer lazy history migration, backups, idempotency and backward compatibility; do not physically move all history before proving correctness.

Illustrative only; align to actual code:

```ts
interface WorkspaceContext {
  workspaceId: string;
  mounts: Array<{ mountId: string; providerId: string; rootRef: string }>;
  activeDocumentRef?: string;
  grants: string[];
}
interface ChatSession {
  sessionId: string;
  workspaceId: string;
  title: string;
}
```

## 5. Shell navigation and state contract

Explicit intents: OpenWorkspace, SelectChat, OpenPlugin, OpenOfficeDocument, OpenResource, OpenSurface, ToggleTree, NavigateBack, NavigateForward. Shell state: selected workspace/chat, rail mode, tree expansion, pane widths/visibility, active right surface and navigation stack. Chat state: history, draft, attachments, stream and scroll. Browser state: resource identity, URL/navigation history, inspect mode and draft annotations. Resource state: mounts, grants and current document. No state ownership ambiguity or implicit session creation.

Required transitions:
- OpenPlugin: select associated workspace/chat, collapse tree, open right browser, keep center ChatView.
- OpenProject: select workspace/chat; keep same ChatView and follow documented policy for right pane.
- OpenOffice: select Office context and explicit workbook mount; keep same ChatView.
- OpenHTML: right BrowserSurface with source resource reference and correct base path.
- Back/Forward: restore shell routes, never repeat agent actions.
- Close/Reopen: restore drafts and panes, dispose orphaned WebViews and listeners.

## 6. Browser as reusable right-side surface

Reuse existing WebView2 and C# ↔ JS bridge when feasible; audit isolation needs. MVP: local HTML/plugin preview, reload, back/forward, resource breadcrumb, loading/error state, resize, inspector toggle, element selection, annotation editor, chat chips, proper lifecycle. Next: inspect DOM and computed styles, open source, console errors, file navigation, optional screenshots, reload-on-change, source maps. Later: multiple preview tabs, visual diff, broader debugging. No general unrestricted internet browser is required.

Security: distinguish trusted Shell from untrusted plugin HTML; verify origins, bridge message schemas and permissions; avoid privileged host objects in arbitrary previews. Do not capture password/private input values. Bound DOM snapshots and treat HTML content as untrusted data. Use isolated WebView2 profiles/instances if actual threat model requires it, not by default.

## 7. Visual Inspector & Annotator

Integrate the prior Visual Annotator plan under BrowserSurface. Inspect -> hover highlight -> click -> comment -> multiple annotation chips in shared composer -> send -> agent resolves resource/element -> edits source file -> reloads/verifies. Support inspect-only reference with no comment. Prefer stable `data-ui-id`, plus fallback selector, element attributes, bounded DOM snippet, page revision/fingerprint and geometry as supporting evidence. Never use coordinates as stable identity. On changed DOM, report stale/ambiguous reference rather than guessing. Scope short aliases such as `a1`, `e1`, `r1@...` to message/session and resolve to stable internal IDs; preserve existing Context Explorer grammar. Do not create duplicate CAS or resource resolver.

Draft annotations belong to composer draft; sent annotations are message-linked evidence. Avoid duplicate chips on retries; remove temporary resources on cancel. Agent must patch actual HTML/JS/CSS sources, not transient rendered DOM. Self-inspection of RNAssistant shell should be a separate, controlled mode to avoid intercepting chat controls.

## 8. Runtime boundaries

Shell events are navigation/UI intents, not agent tool calls. Chat submits through one existing agent execution path. Context builder resolves workspace mounts, grants, active document and attached refs with token budgets. Tools are advertised by capability registry according to grants. Resource Explorer, Browser and agent all use one resolver and identity model. UI status events must not create duplicate assistant messages; agent event log remains authoritative for tool execution. Browser navigation must not interfere with execute/reflection/finish semantics.

## 9. Decisions to validate

| Question | Preferred direction | Alternative if audit shows need |
|---|---|---|
| Three chat implementations | One ChatView/Composer | Thin Office/Plugin host adapters |
| Office vs Project | Workspace mounts/providers | Distinct persistence adapters behind common API |
| Plugin | Workspace + BrowserSurface | Isolated security context |
| Browser | Existing WebView2 | Separate instance for trust boundary |
| Inspector | Injected JS in trusted preview | DevTools Protocol where needed |
| History | Lazy compatibility/migration | Versioned one-time migration after tests |
| UI stack | Existing HTML/JS modules | New framework only with quantified benefit |

Evaluate each for simplification, removed code, regressions, lifecycle, security, performance and maintainability. Keep an explicit decision log.

## 10. Implementation phases and acceptance gates

**0. Audit & baseline.** Map files/functions, trace chat variants and persistence, reproduce duplicate events/freezes, measure startup/navigation/memory, document migration hazards. Gate: reviewed audit and RFC before refactoring.

**1. Unified ChatView.** Consolidate transcript, composer, attachments, streaming, send/retry and status rendering; keep host adapters temporarily. Gate: identical UX and correct old histories across Office/Project/Plugin; no duplicate send.

**2. Workspace context.** Align Office/Filesystem/Plugin mounts with current resources and capability registry; explicit grants and active document; shared session API with legacy persistence adapters. Gate: Office and Project use same chat runtime with correct tool access, no accidental cross-workspace writes, safe workbook close/reopen.

**3. New Shell.** Rail, tree, center ChatView, right SurfaceHost; plugin click auto-collapses tree; route history, pane persistence, resizing, keyboard accessibility. Gate: repeated switching never loses draft, creates phantom sessions, duplicates listeners or leaks WebViews.

**4. Embedded Browser.** Trusted preview, lifecycle, resource routing, reload/navigation, error state and bridge isolation. Gate: plugin/HTML preview functions after switching and restoration; no privileged bridge access from untrusted content.

**5. Annotator.** Hover/select/comment, chips, agent resource resolution, stale-target detection, source-edit verification. Gate: multiple annotations survive navigation/reload appropriately, are delivered once, and map to correct resources.

**6. Cleanup & performance.** Remove obsolete duplicated code only after tests and migration; compare startup, chat switch, memory and WebView2 count against baseline. Update docs and developer extension guide. Gate: regression checklist passes and measurable complexity reduction is documented.

## 11. Mandatory test matrix

- Office: multiple workbooks, unsaved, renamed, read-only, close/reopen, workbook-scoped histories, COM unavailable.
- Projects: missing/moved folders, large trees, simultaneous resources, permission denied.
- Plugins: opening from rail, tree collapse/reopen, rapid navigation, reload, plugin JS errors, malicious bridge messages.
- Chat: draft preservation, streaming while navigating, retries, attachments, context switching, history migration, duplicate event suppression.
- Browser: WebView2 disposal, focus, resize, CSS/DOM mutation, stale annotations, preview resource mapping.
- Performance: cold start, switching latency, memory and event listener count, long transcripts, rapid repeat operations.

## 12. Completion criteria

There is **one chat renderer and composer** in code, and no separate Office/Project/Plugin chat UI branches. Workspace/context changes do not alter chat layout. Plugin navigation produces rail + collapsed tree + central chat + right browser. Office and filesystem providers retain their distinct constraints and permissions. Browser annotations attach through existing resource/reference mechanisms and reach the agent without duplication. Legacy sessions are accessible; no data loss. Old duplicate implementations and event handlers are removed only after parity tests. Document what code was deleted and what complexity was added.

## 13. Immediate next action for the agent

Do **only Phase 0 first**. Read existing architecture docs and latest repository, build the actual call graph and comparison matrix, reproduce the problems, propose the minimum viable consolidation and write the RFC. Explicitly identify which assumptions in this document are wrong. Then implement Phase 1 after the RFC is accepted; do not begin a simultaneous shell, browser and runtime rewrite.
