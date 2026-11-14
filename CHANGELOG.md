# Changelog

User-visible changes are recorded here. Internal stabilization work is tracked in
[PROGRESS.md](docs/stabilization/PROGRESS.md). A commit is not a release.
Completed product changes add an Unreleased note with their assigned development
version; dated version sections belong to explicit release preparation. See
[Versioning](docs/operations/VERSIONING.md) for bump rules and the transition from
the previous freeze. Unreleased notes do not claim published or qualified builds.

Historical baseline: `v16.0.4`. This file does not reconstruct historical release notes.

## [Unreleased]

### 16.27.5-dev

- Fork сохраняет общий working set и выбранный Plan независимо от границы истории,
  включая detach, tombstone и происхождение ресурсов. Workspace больше не теряет
  эти сведения при создании ветки. Native journal принимает общие Plan/document HTML
  и Office-вложения без копирования тел или изменения shared heads; локальные
  графы ещё требуют подключения. Production storage прежний, Windows evidence открыт.

### 16.27.4-dev

- Fork загружает только выбранные Task/document HTML snapshots; недоступное старое
  тело не мешает подготовке ветки. Checkpoint сохраняет сведения о shared publication
  и её ошибке. Подготовка использует общий Store-порт; полное native создание ветки
  и переключение production storage ещё открыты. Windows/Office evidence открыт.

### 16.27.3-dev

- Workspace учитывает сохранённые context notes при подготовке запроса: полные
  инструкции читаются из CAS, данные и наблюдения проходят общий authority-aware
  compiler. Native journal подключён к compaction/checkpoints и их восстановлению;
  исходная история и Unknown сохраняются. Production storage пока не переключён,
  Windows/Office evidence открыт.

### 16.27.2-dev

- Task List: Office и Workspace одинаково проверяют опубликованное состояние перед
  изменением; неопределённый результат не выдаётся модели как доступный текущий список.
  Откат истории выбирает ревизию по metadata и загружает только её тело, включая
  состояние blocked. Native journal сохраняет ревизии и CAS через существующих
  consumers; production storage пока не переключён. Windows/Office evidence открыт.

### 16.27.1-dev

- Browser: «Обсудить» открывает связанный чат или создаёт новый для источника страницы
  и выделяет его в дереве. Удалена строка unbound; явная смена чата доступна в меню.
  Связи вкладок независимы от выбора/создания чатов в дереве; late responses защищены.

### 16.27.0-dev

- Browser: скачивание PNG видимой страницы, включая вкладку без чата, через native
  capture и общий data-plane. Exact session/privacy guards, narrow placement и cleanup
  позднего ответа; проверки локальные, Windows qualification остаётся открытой.

### 16.26.0-dev

- Added manual cross-project Browser navigation and explicit chat selection through
  «Обсудить». Back and layout restoration cannot revive an old agent binding.

### 16.25.4-dev

- Allowed deleting an inactive plugin version while its read-only preview is open;
  running commands remain protected and deleted viewers lose SDK access immediately.

### 16.25.3-dev

- Preserved local query/hash and native SPA history without resetting form state.
- Export follows the currently displayed HTML member; native and agent navigation
  now appear in recent pages without storing query payloads.

### 16.25.2-dev

- Connected annotations for ordinary HTML resources and Workspace HTML projects to
  the common Browser path, including saved sources and chat history. Annotations
  retain the exact page, saved catalog entry, content revision and recipient across
  navigation; equal bytes in different versions cannot reanchor a comment.

### 16.25.1-dev

- Inspector and next-request preview use the exact selected chat and draft. Historical
  run/step stays pinned; duplicate activity details route to the existing journal.
- Corrected narrow Inspector sizing and stale/closed-source handling.

### 16.25.0-dev

- Added common local page search with next/previous matches and native element
  highlighting, bounded to the available semantic text.

### 16.24.1-dev

- Restored element selection, highlighting and annotation drafts in the common native
  Browser, with exact source/chat ownership and stale-page protection.

### 16.24.0-dev

- Browser keeps element-based input and adds an optional fresh-viewport coordinate
  fallback. Workspace actions retain delivered read evidence like Office; keyboard
  focus/selection and viewport scrolling have explicit read-back.

### 16.23.1-dev

- Browser and HTML editor share autonomous HTML export with embedded binding data.
  The downloaded page runs through file://; source ZIP is a separate choice.
  Export keeps the selected revision and does not create a checkpoint.

### 16.23.0-dev

- Local Browser connects interactive native pages, HTML bindings, plugin SDK and
  trusted agent input. Page/source modes share the existing Dock tab; physical input
  pauses agent control. External traffic stays blocked by scoped native routes and
  response network policy. Windows/WebView2 qualification remains open.

### 16.22.0-dev

- Agents can discover local sources and open one exact visible Browser tab, including
  standalone Workspace chats. Duplicate and unknown opens retain scoped receipts.
- Navigation uses local targets; internal scoped HTTPS byte transport is preserved.
  Native execution/input/media remain unavailable pending isolation qualification.

### 16.21.0-dev

- Saved Workspace HTML versions can be exported or copied to a new project, including
  binary assets and an unavailable original folder. Transfers preserve exact bytes,
  source provenance and existing download limits; the original stays unchanged.
- Browser completion includes bounded saved versions and uses registry metadata
  without loading chats or capturing working files.

### 16.20.0-dev

- HTML materials, raw HTML, source-editor Preview and plugin rail share one Browser.
  Page actions create projects from exact sources; comparison supports saved document,
  Workspace and plugin versions with scoped paging and explicit unavailable states.
- Removed duplicate HTML views and old Browser history APIs. Fixed exact snapshot
  revision reads and deferred refresh for hidden document pages. Execution remains
  unavailable until native network isolation is qualified.

### 16.19.0-dev

- Dock «+» and Browser Home share one searchable start surface. Opening a chat
  focuses the existing tree; technical screens remain contextual search results.
- Browser keeps up to 200 scoped local visits, excluding query strings and runtime
  sessions, with a clear-history action and a usable in-memory storage fallback.
- Split/maximize moved into group actions; Shell and native window menus invoke
  the same layout commands directly. Current document completion reads metadata only.

### 16.18.0-dev

- Document HTML now has explicit project checkpoints, separate from automatically
  retained edits. All previously published versions migrate with their exact links
  and timestamps; deleting a checkpoint keeps historical evidence.
- Browser Save first saves a dirty buffer through its existing HTML editor owner.
  The current checkpoint cannot be deleted, and stale publications are rejected.
- The HTML import backend accepts an exact single UTF-8 resource as a new one-file
  project, preserving bytes and refusing unsupported encodings/dependencies.

### 16.17.0-dev

- Local Workspace HTML projects can save explicit versions without a chat, reopen
  retained files after the source folder disappears, and delete individual catalog
  entries without deleting historical evidence. Save checks the exact declared files.
- The common Browser version menu includes a small project-members form for older
  projects. A missing definition keeps Save visible with a reason.

### 16.16.1-dev

- The Excel-owned assistant form has no system title bar from its first appearance,
  including WebView2 loading or failure. The existing Shell remains the single header.
- Window controls and header dragging also work without WebView2 native drag regions.
  Actual Windows/Excel frame and resize qualification remains open.

### 16.16.0-dev

- Plugins now use Check → Save → Apply. Saved checkpoints have independent ordinals,
  deletion receipts and active-entry guards; a run can apply its own saved result or
  an explicitly selected version. Saving does not activate code or grant data access.
- Plugin sources and saved versions open in the common local Browser. Save remains
  visible on narrow panels; version deletion and uncertain-operation reconciliation
  use the same catalog. Obsolete plugin tabs are pruned without resetting other Dock panels.
- Authored execution remains unavailable pending native isolation qualification.

### 16.15.2-dev

- Workspace event reads and subscriptions validate chat metadata without loading
  the full transcript or message CAS. Invalid history reports a typed resync;
  workspace ownership checks, cursor paging and cancellation are preserved.

### 16.15.1-dev

- Decoupled Workspace Browser tools from Office document selection. Explicit viewport
  media reaches the next model request through the existing CAS and chat commit;
  unsupported delivery fails explicitly and pixels are not replayed after restart.
- Preserved semantic captured-observation targets in model results. Native rendering,
  capture and input remain unavailable until B02 isolation is qualified.

### 16.15.0-dev

- Added local Browser tabs with exact ownership, attach acknowledgement, bounded
  navigation history, local address completion and paged source reading.
- Active authored HTML previews and plugin commands fail closed while WebView2
  network isolation (B02) remains unqualified. Removed the external native pilot
  and Shell plugin iframe transport. Source editing, export and saved data remain.

### 16.14.6-dev

- Removed HTML HTTP proxy and external Browser grants, including obsolete
  developer policy/issuer controls. Developer remains empty; LLM connectivity
  is unchanged. WebView page links no longer launch the system browser.
  Full native local-content isolation remains under qualification.

### 16.14.5-dev

- Plugin checks now retain separate UI/run candidates and reuse identical packages.
  Peer checks preserve pending approval; changed source or actor binding refuses
  activation before dispatch.

### 16.14.4-dev

- LLM JSON/SSE requests and model configuration GET no longer send User-Agent,
  including previously saved custom values in any letter case.

### 16.14.3-dev

- Restored Excel-owned RNAssistant windows without a separate taskbar preview.
  Closing ends the session; opening from another workbook saves drafts and starts
  a session for that workbook. Simplified the window controls and removed the
  duplicate caption after WebView2 initialization. Windows delivery remains open.

### 16.14.2-dev

- Stabilized encrypted BIN signing across RSA serialization paths and made the
  Windows key utility identify the failing self-check stage. The reported Windows
  failure still requires a repeat with the rebuilt utility.

### 16.14.1-dev

- Fixed Office compilation errors in Browser navigation and model context inspection.
  MockDemo now compiles the current bridge parts and embeds deployment trust when
  configured. A separate Windows script builds the key utility; missing trust is
  reported during builds. The Developer editor defaults to the signing key path
  captured at build time. The Windows key utility uses CNG for RSA 3072 generation
  and shows the failing self-check stage. Windows/Office delivery remains unqualified.

### 16.14.0-dev

- Independent reads, skill selections and managed edits can share an ordered tool
  batch. Each call keeps its guards and confirmation; pending tails survive resume,
  and failure/unknown/Stop closes unstarted calls without replaying prior effects.
- Rejected edits can return bounded current source through the existing context/CAS
  path. Targeted refresh releases stale read limits without automatically retrying writes.
- `core.patch` edits custom skill bodies through the existing authoring owner,
  preserving metadata, references and package revision checks.
- A failed group refresh preserves the last known tree and generation watermark.
  Windows/Office qualification and the reported provider HTTP 500 remain open.

### 16.13.0-dev

- Added a password-protected Developer section for one portable, signed
  deployment-config.bin: allowed sites, optional unrestricted manual browsing,
  private key path, sign/save and open-directory actions. Agents remain limited to
  the site list. Invalid signatures or policy versions apply no permissions.
  Mac C# and Windows GUI utilities generate password-protected BIN keys without
  certificates. Base64 conceals casual viewing only; native Windows qualification
  remains open.

### 16.12.1-dev

- Interrupted runs retain one canonical tool outcome and their counters. Reads fail;
  possible writes remain unknown and are never replayed. Recovery preserves a saved
  terminal across a missing result projection. Windows/Office qualification is open.

### 16.12.0-dev

- Browser reads retain observations with source address, title and capture time.
  Agents can compare facts across navigation and context compaction, or reread a
  saved observation after closing the page. Historical reads grant no live action
  authority. Research text uses larger bounded excerpts; native Windows delivery is open.

### 16.11.0-dev

- Browser agents can fill ordinary text controls, verify in-page button clicks on
  allowed sites, and navigate by an observed link name. Link destinations stay bound
  to the observed element; stale targets, manual takeover and unknown effects cannot
  silently replay. Async click verification is bounded; native Windows delivery is open.

### 16.10.0-dev

- The agent can navigate its existing Browser page to an allowed HTTP(S) address
  after reading it. Redirects and final loading are checked; manual takeover,
  revoked access and uncertain effects prevent automatic continuation/replay.
  Native Windows delivery remains unqualified; external-site click/fill is a separate next slice.

### 16.9.0-dev

- Saved HTML and published plugin versions offer bounded source comparison using
  their existing history. Preview data and unsaved edits are excluded; comparison
  preserves page input and does not restore or publish. Baseline is In Review;
  further diff work and persistent annotations are deferred in favor of browser automation.

### 16.7.0-dev

- Browser manual input blocks agent actions and requests a pause of the bound run.
  Explicit “Continue with agent” discards old preparations and requires a fresh read;
  peer agents cannot release this user pause. No action queue or automatic replay.
- “Sites available to the agent” controls external page reads using exact HTTP(S)
  origins or domain/subdomain masks. Manual navigation remains unrestricted; HTML
  preview HTTP permissions remain separate. External fill/click still require the
  packaged local probe. Windows/WebView2 delivery remains unqualified.

### 16.6.5-dev

- Runtime checkpoints and result commits read only addressed messages and related
  call/evidence rows instead of materializing the whole chat history. Context selection,
  message ordering, index repair and lost-acknowledgement recovery are preserved.

### 16.6.4-dev

- Local Browser actions show an animated agent pointer, target highlight and a
  status tied to the published runtime result. The pointer fades after 2.5 seconds
  of inactivity and clears on manual interaction or hide; reduced motion is supported.
  Page input and semantic reads remain unaffected. Windows/WebView2 validation is open.
- Clarified that HTML preview HTTP permissions do not restrict external Browser navigation.

### 16.6.3-dev

- History pages no longer construct a temporary chat aggregate or load the active
  HTML workspace body. Office/Workspace retain the same message contents, resource
  cards, selected versions and tombstone handling; damaged indices still rebuild.

### 16.6.1-dev

- Office documents and projects share one workspace tree, search and full add menu.
  Chat and parent menus use consistent icons; project folders follow the theme accent.
  Regular-weight headings and a stable action slot prevent labels from being covered.
  Duplicate scope/creation controls and manual project refresh were removed.
  Windows/Office/WebView2 qualification remains open.

### 16.6.0-dev

- The opt-in External Browser pilot can fill a textbox and click a button on its
  packaged local probe through the same Office/bound Workspace runtime. Actions
  require a delivered semantic read, recheck the exact element and user edits,
  and verify the result. Unknown effects block further actions until manual reopen.
  External website actions and Windows/WebView2 qualification remain open.

### 16.5.1-dev

- Older Office/Workspace chat pages remain readable when an unfinished operation's
  execution body is missing or corrupt. History reads leave the operation unchanged;
  full run loading still reports the missing evidence explicitly.

### 16.5.0-dev

- The opt-in External Browser pilot exposes a chat-bound, bounded semantic page
  read through existing `core.find/read`, including the bound Workspace Office route.
  Observations omit text field values/private regions and use the existing CAS;
  navigation, document changes and close invalidate pending captures. Agent actions
  and standalone Workspace access are separate; native Windows delivery remains open.

### 16.4.0-dev

- Opt-in External Browser pilot embeds one unprivileged WinForms WebView2 page in
  the existing Dockview/Browser toolbar, with HTTP(S) navigation, a temporary
  InPrivate profile and on-screen diagnostics. Enable with `RNA_BROWSER_PILOT=1`.
  Popup/download/permission policies currently refuse requests. Native Windows
  compilation, Office integration and visual acceptance remain open.

### 16.3.3-dev

- Saved plugin tabs check the current publication when opening restore actions.
  A newer publication no longer leaves restoration permanently disabled or bound
  to an outdated head. Conflicts require an explicit retry; the open preview stays intact.

### 16.3.2-dev

- A failed plugin source check on first opening now leaves a visible error instead
  of automatically opening the active published release. Source watching, manual
  retry and the existing preview after a failed refresh are preserved.

### 16.3.1-dev

- Context recovery preserves runtime unknown outcomes even when an opaque tool
  returns `ok`. The isolated new journal selects bounded context from its index,
  with compact unresolved-operation notices and addressed detail/replay-guard
  queries instead of a record-count limit. Production storage cutover is separate.

### 16.3.0-dev

- NativeHostCli gains integrated window controls, Compact/Companion modes, pin and
  saved window placement. Closing hides the window while retaining the session;
  opening from another Office window preserves the current chat and running work.
  Native Windows/Office qualification and visual acceptance remain open.

### 16.2.5-dev

- Saved HTML, raw HTML and the live editor use verified source capabilities.
  Foreign target responses cannot replace the preview; read-only sources remain
  viewable while editing and annotations are disabled.

### 16.2.4-dev

- Model context preparation reuses exact read-body metadata and hashes within each
  request, reducing repeated parsing while preserving body identity, omission and
  actual-delivery guards.

### 16.2.3-dev

- Active run progress/checkpoints and status reads no longer copy chat history.
  Catalogs use committed metadata; Office/Workspace Stop addresses the existing
  exact-scope queue without history hydration.

### 16.2.2-dev

- Model context snapshots, compiler copies and wire provenance share immutable text
  and evidence without JSON roundtrips. Mutable request/repair metadata remains
  detached, including nested confirmation previews and resource target outcomes.

### 16.2.1-dev

- Frozen skill catalogs reuse immutable instruction text without JSON roundtrips
  on each getter; mutable dependency/reference metadata remains isolated per request.

### 16.2.0-dev

- Records the accumulated unreleased UI, Workspace/Browser and agent capabilities
  after adoption of completed-change versioning; this is not a release or Office qualification.
- Context receipts retain all delivered-message provenance while limiting excluded
  history details to 16 examples plus reason counts in both hosts and dispatch manifests.
  Full-session runtime migration remains in progress.

Previous alpha `16.1.0-alpha.1` was prepared in source; it was not a published or qualified release.

### Tools exploration alpha — 2026-10-26

- Shared bounded literal/regex resource search across workspace and Office VBA/HTML,
  with semantic continuation and explicit incomplete-result reporting.
- Recursive workspace discovery with include/exclude globs.
- Correct active VBA tool display IDs and show search coordinates in result previews.
- Product version advances from `16.1.0-dev` to `16.1.0-alpha.1`; assembly binding
  remains `16.0.4.0`. Windows/Office qualification and signed release evidence are open.

### Added

- Qualification Center runs versioned in-app host checks with durable typed evidence;
  release admission now verifies a signed manifest for the exact candidate build.

### Changed

- Introduced the `16.1.0-dev` build identity during stabilization: commit SHA and
  `.dirty` markers distinguish builds without automatic build-time version bumps.

### Fixed

- Runtime now assigns tool-call IDs before acceptance and preserves them through
  confirmation and replay. The v4 model contract contains only tool names and
  arguments, removing ID-collision repairs that could regenerate useful HTML.
  Existing v3 chats require an explicit new chat/reset; saved prompts need review.
- Confirmation and normal runs now share runtime accounting; recorded effects survive
  chat replay, cancellation and failures while preparing the next model request.
- The configured format-attempt limit now includes the first response: 20 stops
  after 20 invalid responses. Transient timeout/network/server failures have a
  separate budget of two retries per model step; schema fallback also works during
  format repair. Cancellation prevents accepting a late response.
- Format repair now retains explicitly loaded media until the model step finishes,
  so a corrected response sees the same image/audio context as the first attempt.
- Runtime warnings now remain visible after failed or uncertain tool mutations,
  even when the model reports completion. Answers without writes do not certify
  applied changes.

### Removed

### Security
