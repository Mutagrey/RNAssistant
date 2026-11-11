# Universal Browser: аудит текущей реализации

Дата: 2026-11-04. Baseline: `main`, `b9173e0a7c060e7c60fceb2e2a4ce39996f4b5db`;
перед работой дерево чистое. Owner: Web presentation + Application browser coordination.
Это статический аудит вызовов и существующих проверок, не результат запуска продукта.
Исходник — [00_SOURCE_SPEC](00_SOURCE_SPEC.md); его прежний HEAD `2c8de087` устарел.
Согласованный объём текущей работы: документы и план, без реализации P1.

## Карта источников

В колонке revision указана предметная версия, а не номер навигации браузера.
Ссылки ведут к действующим владельцам; новые browser contracts пока Proposed.

| Source | Renderer | Owner / authority | Storage | Revision | Permissions | Lifecycle | UI entrypoint |
|---|---|---|---|---|---|---|---|
| Внешний HTTP(S) URL | Сейчас системный браузер через `OpenExternalUri`; управляемого Dock renderer нет | OS browser; Office bridge недоступен | Вне RNAssistant | История сайта не является версией проекта | `WebViewSecurityPolicy.CanOpenExternally` | RNAssistant не владеет внешней session | Пользовательская внешняя ссылка / popup из Shell |
| Raw HTML resource/upload | Инертный TextViewer, не исполняемый Browser | Resource Gateway / attachment owner | Existing original/CAS | Exact resource revision | Чтение не выдаёт execution authority | MaterialView: lazy read, cancel, bounded source | Материалы / attachment; импорт в HTML workspace — отдельное действие |
| Live document HTML workspace | `HtmlWorkspaceView` → editor/preview builder → sandboxed iframe через Browser portal | `HtmlWorkspaceToolService`, `HtmlWorkspacePublication`, document authority | `DocumentArtifactStore`, ResourceAuthority/CAS; локальные dirty buffers у editor | Logical workspace, active pointer, parent/restore lineage | Guarded upload/write, scoped resource leases; существующий HTML SDK | Адресный view по chatId, dirty/canClose, deactivate, generation/dispose | Материалы → текущий проект / «Открыть просмотр» |
| Сохранённый HTML artifact | `HtmlArtifactView` → `BrowserObjectView` → Browser; iframe `allow-scripts`, `hostBridge:false` | Exact project snapshot; export у `HtmlWorkspaceExportService` | Existing export/CAS, временный transfer lease | Code pinned URI; dynamic-head data фиксируются на открытие/reload | Read-only preview; export/версии через прежних owners | Ready DOM retained при hide/move; close освобождает | `MaterialView.create` для `kind=html_workspace`; версия — отдельная Dock tab |
| Plugin source | Existing workspace files/editor/chat; candidate preview — путь следующей строки | `WorkspaceFileService`, `WorkspaceConversationService` | Workspace files + existing journal/CAS | File heads; source stamp/candidate отдельно от release | Source authority не выдаёт data grants | Chat/run не зависят от Browser view | Plugin → «Чат исходников», check/preview |
| Plugin preview / текущий plugin UI | `PluginView.create` → Browser portal → frame с отдельным origin | `PluginApplicationService`, `PluginInstance`, `PluginWebViewHost` | Candidate/release CAS, отдельные preview data или выбранные live data | Candidate или exact published release | Frame channel + instance grants; preview не наследует live grants | Per-object view, epochs, source watch; close/revoke через owner | Plugin в rail / check preview |
| Pinned plugin release | `PluginReleaseView` → `BrowserObjectView` → instance iframe | `PluginApplicationService.Open`, `RestoreVersion`; `PluginStore` | Immutable release CAS + отдельные пробные данные | Exact release; active registry pointer независим | `Open` с ReleaseId включает preview; restore проверяет expected active release | Lazy open, late instance close, dispose; максимум 4 release views | Plugin → «Версии» → отдельная Dock tab |

## Проверенные пути и функции

- [DockWorkspace](../../web/js/app-dock-workspace.js): `registerView`, `openTarget`,
  `createObjectComponent`, `canClose`, layout restore. При move/fromJSON заменяется
  Dock component, а не owned view. Единственный placement registry уже существует.
- [Browser renderer](../../web/js/app-browser-viewer.js): `create/place/layout`,
  activate/deactivate/destroy. Stable surface остаётся в portal вне перемещаемого
  anchor; hidden/inert и ResizeObserver регулируют presentation. Navigation/session
  authority здесь нет. [ViewerRegistry](../../web/js/app-viewer-registry.js) владеет mount/unmount.
- [PreviewSurface](../../web/js/app-preview-surface.js): `attach/watch/waiting/fail/clear`;
  общие location, status, reload и actions. `iframe.load` означает загрузку frame,
  а не доказательство бизнес-эффекта или выполнения всего JS.
- [BrowserObjectView](../../web/js/app-browser-object-view.js): `create/load/reload/stop`;
  lazy load, abort/generation, late value disposal, retained ready DOM. Это UI lifetime,
  не будущий BrowserSession service.
- [HtmlArtifactView](../../web/js/app-html-artifact-view.js): `load/create/renderSource/mode`.
  `exportArtifact(preview:true)` → bounded download → `HtmlResourceExport.fromArtifactViews`
  → `HtmlWorkspacePreview.build` → iframe. `closeArtifactTransfer` находится в finally.
  Здесь же пока локальный переключатель «Страница / Исходник» и выбор файла.
- [HtmlWorkspaceView](../../web/js/app-html-workspace-view.js): `create/syncBrowser`,
  `canClose/dispose`; [editor](../../web/js/app-html-workspace-editor.js) и
  [source](../../web/js/app-html-workspace-source.js) сохраняют dirty guard,
  исходную revision, coalesced reads и bounded clean-source cache.
- [HTML publication](../../src/RNAssistant.Office/Services/HtmlWorkspacePublication.cs):
  `Prepare` и read-back barrier; [Targets](../../src/RNAssistant.Office/Services/HtmlWorkspaceTargets.cs)
  разрешает semantic workspace независимо от выбора UI; [NavigationService](../../src/RNAssistant.Core/Services/HtmlWorkspaceNavigationService.cs)
  восстанавливает history/redo из metadata. Максимальный номер не определяет active head.
- [PluginView](../../web/js/app-plugin-view.js) владеет адресным presentation/lifecycle;
  [app-plugins](../../web/js/app-plugins.js) — общей навигацией и вызовами владельца;
  [PluginReleaseView](../../web/js/app-plugin-release-view.js) открывает pinned release.
  [PluginApplicationService](../../src/RNAssistant.Application/PluginApplicationService.cs):
  `Check/Apply/ReleaseHistory/RestoreVersion/Open/StageVisualSelection/DisableAsync`.
  Restore опубликованной версии переключает registry head, а не переписывает source workspace.
- [VisualAnnotations](../../web/js/app-visual-annotations.js) и
  [VisualInspector](../../web/js/app-visual-inspector.js) уже общие для HTML/plugin.
  [ChatResourceIngestionService.StageHtmlVisualSelection](../../src/RNAssistant.Office/Services/ChatResourceIngestionService.cs)
  допускает текущую версию выбранного HTML исходного чата;
  plugin staging привязывает source к retained release живого instance.
  [AttachmentStore.StageVisualSelection](../../src/RNAssistant.Core/Storage/AttachmentStore.cs)
  сохраняет bounded evidence и exact source; pending draft хранит
  [ProtectedChatDraftStore](../../src/RNAssistant.Core/Storage/ProtectedChatDraftStore.cs).
  Это не универсальный annotation registry с `resolved/unresolved` и cross-revision reanchoring.
- [TextDiff](../../web/js/app-text-diff.js): `format/render/renderInline` — общий bounded
  formatter. Его можно переиспользовать, но владельца выбора пары browser revisions
  и полного Browser Diff mode пока нет.

## Native и CDP: что действительно есть

[AssistantPaneControl](../../src/RNAssistant.Office/WebView/AssistantPaneControl.cs)
создаёт WinForms WebView2 для Shell. `ConfigureInitializedWebView` подключает
navigation/frame/resource/bridge handlers; `OnWebMessageReceived` требует trusted
document. Top-level navigation ограничена Shell; обычные preview frames —
about:blank/srcdoc, plugin entries допускаются отдельным host. Нельзя превратить
этот privileged control в renderer произвольного сайта сменой URL.

[PluginWebViewHost](../../src/RNAssistant.Office/WebView/PluginWebViewHost.cs)
имеет реальные `CoreWebView2Controller`, но command controllers скрытые, 1×1,
in-private. `Transport.OnFrame` связывает frame, exact entry URL, instance и typed
request; destroy/navigation отзывает канал. Это не проверенное native Dock embedding.
SDK закреплён в Office `packages.config` на `1.0.2903.40`.

[BrowserHost](../../src/RNAssistant.BrowserHost/Program.cs), `OwnBrowserAsync`, —
`net8.0` helper: отдельный Chromium, временный профиль, ownership pipe, deadline,
kill descendants и cleanup. Запускается с `--remote-debugging-port=0` и закрытым
proxy. [BrowserProcess](../../src/RNAssistant.BrowserHost/BrowserProcess.cs) — клиент
этого lifetime. Реальная CDP automation есть в
[WorkspaceWebVerifier](../../src/RNAssistant.Runtime/WorkspaceWebVerifier.cs):
`RunBrowserAsync` читает DevToolsActivePort и соединяет private `DevToolsClient`;
checks/diagnostics предназначены для retained local web snapshot. Это не общий
внешний browser API и не Office .NET 4.8 adapter.

Поиск в `src` не выявил Universal BrowserSession, model-facing `browser.open/inspect`,
CompositionController embedding или отдельного класса AgentEventLog. Communication
facts идут через `ConversationInboxService.AgentEvents/AppendAgentEvent` в SessionEvent;
run/tool facts — kernel/trajectory. Новые observations должны расширять эти owners,
а не заводить одноимённый параллельный журнал.

## Reuse / extend / replace / remove

| Действие | Объект | Условие |
|---|---|---|
| Reuse | DockWorkspace, ViewerRegistry, Browser portal, PreviewSurface, tokens | Одна Shell, страницы — Dock tabs, без второго docking engine |
| Extend | PreviewSurface + BrowserObjectView | P1 добавляет общий address/modes; providers сохраняют данные |
| Replace/remove | Локальные mode buttons `HtmlArtifactView.load` | P1 переключает consumer и удаляет заменённый toolbar CSS |
| Extend | HtmlWorkspace/Plugin views через adapters | P2/P3; dirty buffers, grants и publication owners остаются |
| Reuse | ArtifactVersions, HTML lineage, PluginStore, CAS, TextDiff | Browser выбирает/показывает, domain owner меняет authority |
| Extend | VisualAnnotations + существующие attachment/events | P4 добавляет target-bound lifecycle без второй копии evidence |
| Reuse selectively | Verifier CDP checks / diagnostics | Только platform-independent семантика после проверки; не перенос .NET 8 helper в Office |
| Add | Application session coordinator и отдельный native adapter | P2/P5; это отсутствующие responsibilities, не второй model loop |
| Не восстанавливать | Старые ChatBrowser, plugin splitters, singleton HTML editor | Эти paths уже удалены; «повторная унификация» их не создаёт |

## Ограничения и открытые вопросы проверки

- **UB-1:** native clipping/z-order/focus/IME/DPI в Office не проверены; выбор renderer
  определяется [spike](02_ARCHITECTURE_ADR.md#native-spike), не наличием API.
- **UB-2:** document/element/session IDs из исходной спецификации конфликтуют с
  действующей model-facing границей. Решение — semantic tools + runtime bindings,
  [контракт](03_CONTRACTS.md#model-api).
- **UB-3:** persistent annotation status/reanchoring, sessions без views и safe
  screenshot redaction ещё не реализованы. Наличие draft/selection их не доказывает.
  Уточнение 2026-11-11: sessions после закрытия Browser-вкладки исключены из P2c
  решением пользователя; это отложенный сценарий native/agent automation, а не gap
  текущего preview. Annotation/redaction остаются открытыми.
- **UB-4:** saved HTML код pinned, но dynamic-head данные получены на открытие/reload;
  future compare должен различать code revision и observation data, иначе diff вводит в заблуждение.
- **UB-5:** raw HTML пока inert, plugin source и release имеют разные authority.
  Универсальная строка адреса не должна неявно исполнять raw HTML или выдавать grants.

Новых воспроизведённых production-дефектов этим статическим аудитом не заявлено.
Перечислены gaps целевой функции и непроверенные гипотезы; общий учёт —
[Risk Register](../stabilization/RISK_REGISTER.md#universal-browser--p0-2026-11-04).
Existing fixtures изучены, но не запускались: [матрица](05_TEST_MATRIX.md).
