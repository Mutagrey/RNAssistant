(function () {
  "use strict";
  var live = new Map(), versionViews = new Map(), inlineHtmlPreviews = new Map(), available = false, discussionIntent = 0;
  var overlayLayouts = new Set(), overlayFrame = 0, overlayObserver = null;
  var unavailable = "Native Browser недоступен. Исходники и версии доступны; подробности — в состоянии страницы.";
  function overlayChanged() {
    if (!overlayLayouts.size || overlayFrame) return;
    overlayFrame = requestAnimationFrame(function () {
      overlayFrame = 0;
      overlayLayouts.forEach(function (layout) { try { layout(true); } catch (_) {} });
    });
  }
  // Shell and Dock create transient overlays in body. Watch only direct children;
  // chat/tree mutations no longer wake every open Browser tab.
  function watchOverlayLayout(layout) {
    overlayLayouts.add(layout);
    if (!overlayObserver) {
      overlayObserver = new MutationObserver(function (changes) {
        var selector = 'dialog, [aria-modal="true"], .rna-action-menu-popup';
        if (changes.some(function (change) {
          return Array.prototype.some.call(change.addedNodes, function (node) { return node.nodeType === 1 && node.matches(selector); }) ||
            Array.prototype.some.call(change.removedNodes, function (node) { return node.nodeType === 1 && node.matches(selector); });
        })) overlayChanged();
      });
      overlayObserver.observe(document.body, { childList: true });
    }
    return function () {
      overlayLayouts.delete(layout);
      if (!overlayLayouts.size && overlayObserver) { overlayObserver.disconnect(); overlayObserver = null; }
    };
  }
  document.addEventListener("rnassistant:overlay-change", overlayChanged);
  function id() { return "browser-" + crypto.randomUUID(); }
  function key(target) { return "browser:" + target.browserTabId; }
  function accepts(target) {
    return target && target.view === "browser" && typeof target.browserTabId === "string" && target.browserTabId.length <= 256 &&
      target.ownerScope && ["Document", "Workspace", "User"].includes(target.ownerScope.kind) && typeof target.ownerScope.scopeId === "string" &&
      target.ownerScope.scopeId.length > 0 && target.ownerScope.scopeId.length <= 256 && !!RNAssistantBrowserTarget.preferenceLocator(target.locator);
  }
  function preferenceTarget(target) {
    if (!accepts(target)) return null;
    if (target.inline) return null;
    var scope = target.ownerScope;
    return { view: "browser", browserTabId: target.browserTabId, locator: RNAssistantBrowserTarget.preferenceLocator(target.locator),
      ownerScope: { kind: scope.kind, scopeId: scope.scopeId, chatId: typeof scope.chatId === "string" && scope.chatId.length <= 256 ? scope.chatId : null },
      title: String(target.title || "Browser").slice(0, 160), chatBound: target.chatBound !== false };
  }
  function create(target) {
    var root = document.createElement("section"); root.className = "browser-object-view"; root.setAttribute("aria-label", "Browser"); root.dataset.browserTabId = target.browserTabId;
    var shell = window.RNAssistantMaterialSurface.create({ kind: "browser", title: target.title || "Browser",
      hideTitle: true, statusMeta: "Browser" });
    root.appendChild(shell.element);
    var content = shell.content; content.classList.add("browser-object-content", "browser-local-content");
    var source = document.createElement("div"); source.className = "browser-document-source product-viewer";
    var empty = document.createElement("div"); empty.className = "empty-state";
    var viewport = document.createElement("div"); viewport.className = "browser-native-viewport"; viewport.setAttribute("aria-label", "Интерактивная страница");
    var loading = document.createElement("div"); loading.className = "browser-view-loading"; loading.setAttribute("role", "status"); loading.setAttribute("aria-live", "polite");
    content.append(source, viewport, loading, empty);
    var mount = document.createElement("div"); mount.className = "browser-view";
    var renderer = RNAssistantViewerRegistry.mount("browser", mount, { surface: root, portalHost: document.querySelector(".shell"), onLayout: layoutNative, onEnter: function () { if (session && selectedMode === "preview" && current.status === "Ready") { command("Focus").catch(fail); return true; } return false; } });
    var active = false, disposed = false, opening = null, session = null, current = null, generation = 0, recordedVisit = 0, pending = new Set();
    var sourcePage = null, sourceViewer = null, loadingSource = 0, selectedMode = "preview", targetGeneration = -1, homeView = null;
    var agentAttachments = new Map(), restarting = null;
    var fileNavigation = createBrowserFileNavigation();
    shell.setNavigation({ element: fileNavigation.element, available: browserFilesAvailable });
    var operationId = target.operationId || id(), definitionDialog = null, targetInvalidated = false, sourceInvalidated = false, authorityRevision = -1, sourceRefreshEpoch = 0;
    var homeSuggestionCache = [], homeSuggestionScope = "", homeSuggestionEpoch = 0;
    var discussionBusy = false, createdDiscussion = null, handoverPending = false, handoverRequested = false;
    function discussionContext() {
      var page = current, owner = page && page.target && page.target.ownerScope;
      if (!owner || page.status === "Home" || page.target.state !== "Ready" || !["Document", "Workspace"].includes(owner.kind))
        throw new Error("Откройте доступную страницу для обсуждения.");
      return { page: page, owner: owner, accepted: function () { return active && !disposed && current &&
        current.sessionId === page.sessionId && current.documentGeneration === page.documentGeneration; } };
    }
    function canRevealDiscussion(context) { return context.accepted() && context.intent === discussionIntent &&
      context.selectedChat === RNAssistantConversationPanel.selected(); }
    async function openDiscussion(context, owner) {
      if (!canRevealDiscussion(context)) return;
      var address = owner.kind === "Document" ? { source: "office", sessionId: owner.chatId } :
        { source: "workspace", workspaceId: owner.scopeId, sessionId: owner.chatId };
      if (owner.kind === "Workspace") await RNAssistantProjectChats.refreshCatalog();
      else if (!(state.chats || []).some(function (chat) { return chat.Id === owner.chatId; })) applyChatCatalogState(await send("listChats", {}));
      if (!canRevealDiscussion(context)) return;
      if (!RNAssistantConversationPanel.open(address, { referencePanel: key(target), beside: true })) throw new Error("Не удалось открыть чат страницы.");
      RNAssistantWorkspaceNavigation.reveal(address);
    }
    async function bindDiscussion(context, chatId) {
      if (!context.accepted()) return;
      var owner = { kind: context.owner.kind, scopeId: context.owner.scopeId, chatId: chatId };
      var bound = await command("Bind", { binding: owner, generation: context.page.documentGeneration });
      if (active && !disposed && current.documentGeneration === bound.documentGeneration && current.chatBound && current.ownerScope.chatId === chatId) {
        createdDiscussion = null;
        var next = discussionContext(); next.intent = context.intent; next.selectedChat = context.selectedChat;
        await openDiscussion(next, owner);
      }
    }
    async function newDiscussion(context) {
      if (!context.accepted()) return;
      if (createdDiscussion && createdDiscussion.sessionId === context.page.sessionId && createdDiscussion.generation === context.page.documentGeneration)
        return bindDiscussion(context, createdDiscussion.chatId);
      var chatId;
      if (context.owner.kind === "Workspace") {
        var created = await send("createProjectChat", { workspaceId: context.owner.scopeId, title: String(context.page.title || "Обсуждение HTML").slice(0, 200) });
        chatId = created && created.sessionId;
        if (chatId) RNAssistantWorkspaceChat.snapshotSource({ workspaceId: context.owner.scopeId, sessionId: chatId }).accept(created);
      } else {
        var response = await send("createBrowserChat", { ownerScope: context.owner, locator: context.page.url });
        chatId = response && response.activeChatId;
        if (chatId) { officeChatSource(chatId).accept(response); applyChatCatalogState(response); }
      }
      if (!chatId) throw new Error("Создание чата не подтверждено. Проверьте рабочую область.");
      if (!context.accepted()) { if (context.owner.kind === "Workspace") await RNAssistantProjectChats.refreshCatalog(); return; }
      createdDiscussion = { sessionId: context.page.sessionId, generation: context.page.documentGeneration, chatId: chatId };
      await bindDiscussion(context, chatId);
    }
    async function runDiscussion(context, action) {
      if (discussionBusy || !context.accepted()) return;
      context.intent = ++discussionIntent; context.selectedChat = RNAssistantConversationPanel.selected();
      discussionBusy = true; surface.discussButton.disabled = true;
      try { return await action(); }
      finally { discussionBusy = false; if (!disposed) surface.discussButton.disabled = false; }
    }
    function discuss() {
      requestHandover();
      var context = discussionContext();
      return runDiscussion(context, function () {
        return context.page.chatBound && context.owner.chatId ? openDiscussion(context, context.owner) : newDiscussion(context);
      });
    }
    async function changeDiscussion(anchor) {
      requestHandover();
      var context = discussionContext(), chats;
      if (context.owner.kind === "Workspace") {
        var catalog = await RNAssistantProjectChats.refreshCatalog();
        var project = (catalog.projects || []).find(function (item) { return item.workspaceId === context.owner.scopeId; });
        if (!project || !project.available) throw new Error("Проект страницы недоступен.");
        chats = project.chats || [];
      } else {
        applyChatCatalogState(await request("listChats", {}));
        chats = (state.chats || []).filter(function (chat) { return chat.DocumentAuthorityId === context.owner.scopeId && !chat.WorkspaceId; });
      }
      if (!context.accepted()) return;
      var choices = chats.slice(0, 20), selected = RNAssistantConversationPanel.selected(), selectedId = selected && selected.target.sessionId;
      var selectedChat = chats.find(function (chat) { return chat.Id === selectedId; });
      if (selectedChat && !choices.some(function (chat) { return chat.Id === selectedId; })) choices.unshift(selectedChat);
      function choose(action) { return function () { return runDiscussion(context, action).catch(fail); }; }
      var items = choices.map(function (chat) { return { label: chat.Title || "Чат", icon: "chat",
        disabled: context.page.chatBound && context.owner.chatId === chat.Id,
        onSelect: choose(function () { return bindDiscussion(context, chat.Id); }) }; });
      items.push({ label: "Новый чат для страницы", icon: "plus", onSelect: choose(function () { return newDiscussion(context); }) });
      if (chats.length > 20) items.push({ label: "Выберите другой чат в дереве и снова откройте это меню", informational: true, icon: false });
      RNAssistantActionMenu.open(anchor, items, { label: "Связать страницу с чатом" });
    }
    function updateBrowserTitle(label) {
      label = label || {};
      shell.element.setAttribute("aria-label", label.title || target.title || "Browser");
    }
    var surface = RNAssistantPreviewSurface.attach(root, { reload: function () { return command("Reload"); }, canReload: function () {
        return active && !!session && !(current && current.target && current.target.kind === "InlineHtml");
      }, discuss: discuss,
      modesHost: shell.modesHost, singleOverflow: true,
      onStatus: function (message, error) { shell.setStatus(message || "", !!error); }, onLabel: updateBrowserTitle });
    shell.setProviderToolbar(surface.bar);
    shell.setCommonActions(surface.actions);
    var annotationHost = RNAssistantBrowserAnnotations.create({ root: root, viewport: viewport, surface: surface, send: send, command: command,
      isCurrent: function () { return active && !disposed && selectedMode === "preview"; }, layout: layoutNative,
      focus: function () { selectMode("preview"); RNAssistantDockWorkspace.openTarget({ view: "browser", browserTabId: target.browserTabId,
        ownerScope: current.ownerScope, locator: current.url, title: current.title }); } });
    var placementKey = "", placementQueued = 0, placementForce = false;
    function outsideBrowserControl(target) {
      return !!target && !root.contains(target) && !(target.closest && target.closest('.rna-action-menu-popup, dialog[open], [aria-modal="true"]'));
    }
    function requestHandover() {
      if (disposed || !active || !current || !current.chatBound) return;
      handoverRequested = true;
      sendHandover();
    }
    function sendHandover() {
      if (disposed || !handoverRequested || handoverPending || !current || !current.manualControl || !current.chatBound) return;
      var revision = current.controlRevision;
      handoverPending = true;
      command("ResumeAgent", { controlRevision: revision }).then(function () {
        handoverPending = false; handoverRequested = false;
      }, function (error) {
        handoverPending = false; handoverRequested = false;
        if (!disposed && current && current.manualControl) fail(error);
      });
    }
    var handoverListeners = new AbortController();
    document.addEventListener("pointerdown", function (event) {
      if (outsideBrowserControl(event.target)) requestHandover(); else if (root.contains(event.target)) handoverRequested = false;
    }, { capture: true, signal: handoverListeners.signal });
    document.addEventListener("focusin", function (event) {
      if (outsideBrowserControl(event.target)) requestHandover(); else if (root.contains(event.target)) handoverRequested = false;
    }, { capture: true, signal: handoverListeners.signal });
    function overlaps(first, second) {
      return first.width > 0 && first.height > 0 && second.width > 0 && second.height > 0 && first.left < second.right &&
        first.right > second.left && first.top < second.bottom && first.bottom > second.top;
    }
    function nativeOverlayOpen(rect) {
      // Keep the page visible while the address query runs. Only an actual Shell
      // overlay may hide the native child HWND.
      if (root.querySelector('.preview-surface-completion:not([hidden]), .visual-annotation-editor')) return true;
      var selector = 'dialog[open], [aria-modal="true"], .rna-action-menu-popup';
      return Array.prototype.some.call(document.querySelectorAll(selector), function (node) {
        return overlaps(node.getBoundingClientRect(), rect);
      });
    }
    function nativeBounds(attachment) {
      var fallback = attachment && current && current.status === "Unavailable";
      var rect = (fallback ? content : viewport).getBoundingClientRect(), overlay = nativeOverlayOpen(rect);
      var visible = active && mount.isConnected && !root.hidden && getComputedStyle(root).visibility === "visible" &&
        selectedMode === "preview" && current && !overlay &&
        (fallback || ["Starting", "Ready"].includes(current.status));
      var x = Math.max(0, rect.left), y = Math.max(0, rect.top);
      var width = Math.max(0, Math.min(rect.right, innerWidth) - x), height = Math.max(0, Math.min(rect.bottom, innerHeight) - y);
      return { visible: !!visible && width >= 1 && height >= 1, x: x, y: y, width: width, height: height,
        viewportWidth: innerWidth, viewportHeight: innerHeight };
    }
    function attachAgentRequests(force) {
      if (!agentAttachments.size) return;
      var bounds = nativeBounds(true), geometry = current.status + ":" + JSON.stringify(bounds);
      agentAttachments.forEach(function (request, operation) {
        if (request.session !== session || request.generation !== current.documentGeneration || performance.now() >= request.expires) {
          agentAttachments.delete(operation); return;
        }
        if (!bounds.visible) { request.geometry = null; return; }
        if (request.complete || request.sending || request.geometry === geometry && !force) return;
        request.sending = true;
        request.geometry = geometry;
        command("Attach", { operationId: operation, generation: request.generation, bounds: bounds }, true).then(function (result) {
          if (result.status === "Failed" || result.status === "Unavailable" || result.status === "Ready" && result.visibleAttachment)
            request.complete = true;
        }, function (error) {
          if (!disposed && session === request.session && current.documentGeneration === request.generation) fail(error);
          request.complete = true;
        }).finally(function () { request.sending = false; if (!request.complete) layoutNative(); });
      });
    }
    function layoutNative(force) {
      if (disposed) return;
      placementForce = placementForce || !!force;
      if (placementQueued) return;
      placementQueued = requestAnimationFrame(function () {
        placementQueued = 0;
        var forced = placementForce; placementForce = false;
        if (disposed || !session || !current) return;
        attachAgentRequests(forced);
        if (!current.attached) return;
        var bounds = nativeBounds(), key = JSON.stringify(bounds);
        if (key === placementKey && !forced) return;
        placementKey = key;
        command("Layout", { bounds: bounds }, true).catch(function (error) {
          if (placementKey === key) placementKey = "";
          fail(error);
        });
      });
    }
    var nativeSize = new ResizeObserver(function () { if (active) layoutNative(); }); nativeSize.observe(viewport);
    var stopOverlayWatch = watchOverlayLayout(function (force) { if (active) layoutNative(force); });
    var comparison = null;
    var pageFind = RNAssistantBrowserFind.create({ root: shell.workspace, content: content, command: command, layout: layoutNative,
      isCurrent: function () { return active && !disposed && selectedMode === 'preview'; },
      selectionOff: function () { annotationHost.deactivate(); }, focusPage: function () { command('Focus').catch(fail); } });
    function updateSearchAdapter() {
      if (selectedMode === "preview") shell.setSearch({ open: pageFind.open });
      else if (selectedMode === "source" && sourceViewer && typeof sourceViewer.search === "function") shell.setSearch({ search: sourceViewer.search });
      else shell.setSearch(null);
    }
    updateSearchAdapter();
    var providerActions = RNAssistantBrowserActions.mount(surface.actions, { target: function () { return current && current.target; },
      generation: function () { return current && current.documentGeneration; }, isCurrent: function () { return active && !disposed; },
      status: surface.status, error: fail, changeDiscussion: changeDiscussion, find: pageFind.open, canFind: pageFind.enabled,
      compact: function () { return root.clientWidth > 0 && root.clientWidth <= 600; },
      annotate: function () { var button = surface.annotationButton(); if (button && !button.disabled) button.click(); }, discuss: discuss,
      canHome: surface.canHome, home: surface.goHome,
      compareAvailable: function () { return !!comparison && current && current.status !== "Home" && current.status !== "Removed"; },
      compare: function () { return selectMode("diff"); },
      canScreenshot: function () { return active && !disposed && selectedMode === "preview" && current && current.status === "Ready" && current.attached && current.target.capabilities.media; },
      screenshotRequest: function () { return { browserTabId: target.browserTabId, sessionId: session, generation: current.documentGeneration }; },
      captureScreenshot: async function (request) {
        function exact() { return active && !disposed && selectedMode === "preview" && session === request.sessionId && current.documentGeneration === request.generation; }
        if (!exact()) throw new Error("Страница снимка изменилась.");
        // The menu/picker hid the native HWND; acknowledge its visible placement before capture.
        surface.collapse();
        await command("Layout", { bounds: nativeBounds() }, true);
        if (!exact()) throw new Error("Страница снимка изменилась.");
        return send("captureBrowserScreenshot", request);
      } });
    var versionControls = RNAssistantArtifactVersions.mountBrowser(surface.actions, {
      target: function () { return current && current.target; }, request: request,
      isCurrent: function () { return active && !disposed; }, navigate: function (locator) { return command("Navigate", { locator: locator }); },
      removed: function () { command("Reload").catch(fail); }, error: fail,
      configure: configureProject,
      beforeSave: async function (value) {
        var serial = current && current.documentGeneration;
        if (value.kind !== "HtmlWorkspace") return;
        if (await RNAssistantHtmlWorkspaceSurface.flushForCheckpoint(value)) {
          if (!active || disposed || serial !== current.documentGeneration) throw new Error("Контекст страницы изменился после сохранения файла.");
          await command("Reload");
          return true;
        }
      },
      status: function (message) { surface.waiting(label(), message); }
    });
    var versionView = function (change) {
      clearHomeSuggestions();
      var value = current && current.target, changed = change.target;
      if (value && (value.pluginId ? value.pluginId === changed.pluginId : value.logicalId ? value.documentId === changed.documentId && value.logicalId === changed.logicalId :
        value.kind === changed.kind && value.workspaceId === changed.workspaceId)) {
        if (change.reason === "Delete" && change.versionId && change.versionId === value.savedVersionId) { targetInvalidated = true; if (active) reloadInvalidated(); }
        else if (active) versionControls.refresh();
      }
    };
    versionViews.set(target.browserTabId, versionView);
    function reloadInvalidated() { targetInvalidated = false; command("Reload").catch(function (error) { targetInvalidated = true; fail(error); }); }
    async function refreshSource() {
      if (!active || disposed || !current || !sourceInvalidated) return;
      var page = current, serial = ++sourceRefreshEpoch;
      try {
        var resolved = await request("resolveLocalLocator", { ownerScope: page.ownerScope, locator: page.url });
        if (!active || disposed || serial !== sourceRefreshEpoch || current.documentGeneration !== page.documentGeneration) return;
        if (!resolved.target) throw new Error(resolved.message || "Источник недоступен.");
        sourceInvalidated = false;
        if (resolved.target.resourceUri !== page.target.resourceUri || resolved.target.state !== page.target.state) await command("Reload");
        else versionControls.refresh();
      } catch (error) { if (active && !disposed && serial === sourceRefreshEpoch) fail(error); }
    }
    async function configureProject() {
      var value = current && current.target, serial = current && current.documentGeneration;
      if (!value || value.kind !== "WorkspaceHtmlProject" || value.immutable) return;
      var payload = { ownerScope: value.ownerScope, workspaceId: value.workspaceId };
      var definition = await request("getHtmlProjectDefinition", payload);
      if (!active || disposed || !current || serial !== current.documentGeneration) return;
      var members = definition.definition && definition.definition.members || [value.entryPath];
      var entry = definition.definition && definition.definition.entryPath || value.entryPath;
      function paths(text) { return text.split(/\r?\n/).map(function (line) { return line.trim(); }).filter(Boolean); }
      definitionDialog = RNAssistantDialog.open({ title: "Состав HTML-проекта", label: "Файлы проекта",
        message: "По одному относительному пути в строке. Первая строка — HTML-файл запуска. Только эти файлы войдут в сохранённую версию.",
        multiline: true, maximumLength: 524288, value: [entry].concat(members.filter(function (path) { return path !== entry; })).join("\n"), submitLabel: "Сохранить состав",
        validate: function (text) { var list = paths(text); return !list.length || list.length > 512 ? "Укажите от 1 до 512 файлов." : null; },
        onSubmit: async function (text) {
          if (!active || disposed || serial !== current.documentGeneration) throw new Error("Контекст страницы изменился.");
          var list = paths(text);
          await request("setHtmlProjectDefinition", Object.assign({}, payload, { expectedFingerprint: definition.fingerprint,
            definition: { entryPath: list[0], members: list } }));
          if (active && !disposed && serial === current.documentGeneration) await command("Navigate", {
            locator: "/projects/" + encodeURIComponent(value.workspaceId) + "/source/" + list[0].split("/").map(encodeURIComponent).join("/") });
        }, onClose: function () { definitionDialog = null; } });
    }
    function label() { return { title: current && current.title || target.title || "Browser", address: current && current.url || target.locator,
      storage: { kind: current && current.target && current.target.workspaceId ? "directory" : "managed" } }; }
    function fail(error) { if (!disposed) surface.fail(label(), error.detail || error.message || String(error)); }
    function browserFilesAvailable() {
      return selectedMode === "source" && !!sourcePage && Array.isArray(sourcePage.files) && sourcePage.files.length > 0 &&
        !!current && !["Home", "Removed", "Closed"].includes(current.status);
    }
    function syncFileNavigationAvailability() { shell.setNavigationAvailability(browserFilesAvailable()); }
    function createBrowserFileNavigation() {
      var panel = document.createElement("nav");
      panel.className = "browser-file-navigation";
      panel.setAttribute("aria-label", "Файлы проекта");
      var heading = document.createElement("div");
      heading.className = "product-nav-heading navigation-pane-heading";
      var headingLabel = document.createElement("strong"); headingLabel.textContent = "Файлы проекта";
      var count = document.createElement("span"); count.className = "navigation-tree-badge"; count.hidden = true;
      heading.append(headingLabel, count);
      var tree = document.createElement("div");
      panel.append(heading, tree);
      var expanded = Object.create(null), positionKey = "browser-files:" + target.browserTabId;

      function fileIcon(path) {
        if (/\.(?:html?|xhtml)$/i.test(path)) return RNAssistantNavigationTree.icon("html");
        if (/\.(?:png|jpe?g|gif|webp|avif|bmp|svg)$/i.test(path)) return RNAssistantNavigationTree.icon("image");
        if (/\.(?:css|m?js|cjs|jsx|tsx?|json|xml|ya?ml|md|txt|svg|woff2?|ttf)$/i.test(path)) return RNAssistantNavigationTree.icon("code");
        return RNAssistantNavigationTree.icon("file");
      }
      function treeNodes(files, activePath) {
        var roots = [], folders = Object.create(null);
        files.forEach(function (path) {
          var parts = path.split(/[\\/]+/).filter(Boolean);
          if (!parts.length) return;
          var parentRoots = roots, parentFolders = folders, prefix = "";
          for (var index = 0; index < parts.length - 1; index += 1) {
            var segment = parts[index]; prefix += (prefix ? "/" : "") + segment;
            var slot = parentFolders[segment];
            if (!slot) {
              var key = "folder:" + prefix;
              slot = { node: { key: key, title: segment, ariaLabel: prefix + "/", tooltip: prefix, kind: "parent",
                selectable: false, iconHtml: RNAssistantNavigationTree.icon("folder"),
                expanded: expanded[key] !== false, children: [] }, children: [], folders: Object.create(null) };
              parentFolders[segment] = slot; parentRoots.push(slot.node);
            }
            parentRoots = slot.node.children; parentFolders = slot.folders;
          }
          parentRoots.push({ key: "file:" + path, title: parts[parts.length - 1], ariaLabel: path, tooltip: path,
            kind: "item", iconHtml: fileIcon(path), selected: path === activePath, filePath: path });
        });
        return roots;
      }
      function draw(files, activePath, emptyText) {
        files = Array.isArray(files) ? files.filter(function (path) { return typeof path === "string" && path.length > 0; }) : [];
        count.textContent = String(files.length); count.hidden = !files.length;
        tree.removeAttribute("aria-busy");
        RNAssistantNavigationTree.render(tree, treeNodes(files, activePath), {
          label: "Файлы проекта", positionKey: positionKey, emptyText: emptyText || "Файлы не найдены", reserveToggleSpace: true,
          onToggle: function (node, value) { expanded[node.key] = value; },
          onSelect: function (node) {
            if (!node.filePath || sourcePage && sourcePage.path === node.filePath) return true;
            loadSource(node.filePath, 0).catch(fail);
            return true;
          }
        });
      }
      function reset(message) {
        count.hidden = true; tree.setAttribute("aria-busy", "true");
        RNAssistantNavigationTree.render(tree, [], { label: "Файлы проекта", positionKey: positionKey, emptyText: message || "Загрузка файлов…" });
      }
      reset();
      return { element: panel,
        render: function (files, activePath) { draw(files, activePath); },
        select: function (path) { RNAssistantNavigationTree.setSelected(tree, path ? "file:" + path : null); },
        reset: reset,
        dispose: function () { RNAssistantNavigationTree.unmount(tree, positionKey); }
      };
    }
    async function request(type, payload) {
      var task = send(type, payload); pending.add(task);
      try { return await task; } finally { pending.delete(task); }
    }
    function stopReads() {
      generation++; sourceRefreshEpoch++; loadingSource = 0;
      if (definitionDialog) definitionDialog.close(false);
      pending.forEach(function (task) { if (task.requestId) cancelBridgeRequest(task.requestId).catch(function () {}); }); pending.clear();
    }
    function scopeKey(scope) { return scope ? [scope.kind, scope.scopeId, scope.chatId || ""].join("\n") : ""; }
    function clearHomeSuggestions() { homeSuggestionCache = []; homeSuggestionScope = ""; homeSuggestionEpoch++; }
    function localSuggestions(prefix, scopeOverride) {
      var scope = scopeOverride || current && current.ownerScope || target.ownerScope, key = scopeKey(scope), seen = new Set();
      var needle = String(prefix || "").toLocaleLowerCase();
      if (homeSuggestionScope !== key) {
        homeSuggestionCache = []; homeSuggestionScope = key;
      }
      var cached = homeSuggestionCache.filter(function (entry) {
        return (entry.displayLocator || "").toLocaleLowerCase().includes(needle) || (entry.title || "").toLocaleLowerCase().includes(needle);
      });
      return RNAssistantDockWorkspace.recentNavigation(scope, prefix, 8).concat(cached).filter(function (entry) {
        if (!entry || seen.has(entry.displayLocator)) return false; seen.add(entry.displayLocator); return true;
      }).slice(0, 20);
    }
    async function suggestions(prefix, epoch) {
      var scope = current && current.ownerScope || target.ownerScope;
      var result = await request("queryNavigationSuggestions", { ownerScope: scope, prefix: prefix, requestGeneration: epoch, limit: 12 });
      var seen = new Set();
      return { requestGeneration: result.requestGeneration, items: (result.items || []).concat(localSuggestions(prefix, scope))
        .filter(function (entry) { if (!RNAssistantBrowserTarget.preferenceLocator(entry.displayLocator) || seen.has(entry.displayLocator)) return false; seen.add(entry.displayLocator); return true; }).slice(0, 20) };
    }
    function createHome(parent) {
      var query = "", epoch = 0, shown = false, dead = false, entries = [], loading = false, error = "", searchTimer = 0, view;
      function model() {
        var scope = current.ownerScope, recent = RNAssistantDockWorkspace.recentNavigation(scope, query, 8), seen = new Set();
        function rows(items) { return items.filter(function (entry) { if (seen.has(entry.displayLocator)) return false; seen.add(entry.displayLocator); return true; }).map(function (entry) {
          return { key: entry.displayLocator, title: entry.title || entry.displayLocator, icon: "globe", description: entry.sourceLabel + " · " + entry.displayLocator,
            disabledReason: entry.disabledReason && !["None", "NativeUnavailable", "ProjectDefinitionRequired"].includes(entry.disabledReason) ? entry.disabledReason === "Removed" ? "Версия удалена" : "Источник недоступен" : "",
            payload: { locator: entry.displayLocator } };
        }); }
        var sections = [{ title: "Недавно", entries: rows(recent) }, { title: "Локальные страницы", entries: rows(entries.slice(0, 12)) }];
        if (recent.length && !query) sections.push({ title: "История", entries: [{ key: "clear", title: "Очистить недавние страницы", icon: "trash", description: "Только список посещений в этом окне", payload: { clear: true } }] });
        return { title: "Главная", placeholder: "Найти локальную страницу…", search: query, sections: sections, loading: loading, error: error,
          empty: "Нет локальных страниц. Откройте HTML из материалов или добавьте проект.", note: RNAssistantDockWorkspace.recentNavigationNotice() };
      }
      async function refresh() {
        if (!current) return;
        if (searchTimer) { clearTimeout(searchTimer); searchTimer = 0; }
        var serial = ++epoch; loading = true; error = ""; view.update(model());
        var scope = current.ownerScope, requestedScope = scopeKey(scope);
        var cacheEpoch = homeSuggestionEpoch;
        try {
          var result = await request("queryNavigationSuggestions", { ownerScope: scope, prefix: query, requestGeneration: serial, limit: 12 });
          if (dead || !shown || !current || serial !== epoch || scopeKey(current.ownerScope) !== requestedScope || result.requestGeneration !== serial) return;
          entries = (result.items || []).filter(function (entry) { return !!RNAssistantBrowserTarget.preferenceLocator(entry.displayLocator); }).slice(0, 12);
          if (!query && homeSuggestionEpoch === cacheEpoch) { homeSuggestionCache = entries.slice(); homeSuggestionScope = requestedScope; }
        } catch (failure) { if (!dead && shown && serial === epoch) error = failure.detail || failure.message; }
        finally { if (!dead && shown && current && serial === epoch) { loading = false; view.update(model()); } }
      }
      view = RNAssistantStartSurface.mount(parent, model(), {
        onSearch: function (value) { query = value.slice(0, 256); entries = []; loading = true; epoch++; view.update(model());
          if (searchTimer) clearTimeout(searchTimer); searchTimer = setTimeout(function () { searchTimer = 0; refresh(); }, 120); },
        onSelect: function (payload) { if (!payload.clear && searchTimer) { clearTimeout(searchTimer); searchTimer = 0; }
          if (payload.clear) { RNAssistantDockWorkspace.clearRecentNavigation(); view.update(model()); }
          else return command("Navigate", { locator: payload.locator }); }
      });
      return { activate: function () { if (shown || dead) return; shown = true; refresh(); },
        deactivate: function () { shown = false; epoch++; if (searchTimer) { clearTimeout(searchTimer); searchTimer = 0; } },
        dispose: function () { dead = true; epoch++; if (searchTimer) { clearTimeout(searchTimer); searchTimer = 0; } view.dispose(); } };
    }
    function presentation() {
      var inline = !!(current && current.target && current.target.kind === "InlineHtml");
      surface.setDocument({ address: current && current.url || target.locator,
        addressLocked: inline,
        allowDiscuss: !inline,
        documentRevision: current && current.documentGeneration || 0,
        mode: selectedMode, loading: !!current && current.status === "Starting",
        supportedModes: current && current.status !== "Home" ? (inline ? ["preview"] : ["preview", "source"]) : [],
        canGoBack: current && current.canGoBack, canGoForward: current && current.canGoForward,
        back: function () { return command("Back"); }, forward: function () { return command("Forward"); },
        home: function () { return command("Home"); }, stop: function () { stopReads(); return command("Stop"); },
        navigate: function (locator) { return command("Navigate", { locator: locator }); },
        localSuggest: localSuggestions, suggest: suggestions
      }, selectMode);
    }
    function selectMode(mode) { if (mode !== "preview") { annotationHost.deactivate(); pageFind.deactivate(); } selectedMode = mode; updateSearchAdapter(); presentation(); render(); if (current) { annotationHost.update(current); pageFind.update(current); } layoutNative(); }
    function render() {
      if (!active || disposed || !current) return;
      var home = current.status === "Home", removed = current.status === "Removed";
      content.hidden = selectedMode === "diff" && !!comparison;
      if (comparison) { if (content.hidden) { comparison.activate(); layoutNative(); return; } comparison.deactivate(); }
      var nativeReady = !home && !removed && ["Starting", "Ready"].includes(current.status);
      viewport.hidden = selectedMode !== "preview" || !nativeReady;
      loading.hidden = selectedMode !== "preview" || current.status !== "Starting";
      loading.textContent = loading.hidden ? "" : (current.message || "Открытие страницы…");
      empty.hidden = !home && !removed && (selectedMode === "source" || selectedMode === "preview" && nativeReady);
      layoutNative();
      source.hidden = home || removed || selectedMode !== "source";
      syncFileNavigationAvailability();
      empty.classList.toggle("browser-home", home);
      if (home) {
        if (!homeView) { empty.replaceChildren(); homeView = createHome(empty); }
        homeView.activate();
      } else {
        if (homeView) { homeView.dispose(); homeView = null; }
        empty.textContent = current.message || unavailable;
        if (!removed && selectedMode === "source" && !sourcePage && !loadingSource) loadSource(null, 0).catch(fail);
      }
    }
    async function loadSource(path, offset) {
      if (!active || disposed || !current || current.status === "Home" || current.status === "Removed") return;
      var epoch = ++generation, owner = current, previous = sourcePage;
      loadingSource = epoch;
      try {
        var result = await request("readBrowserSource", { ownerScope: owner.ownerScope, locator: owner.url, path: path, offset: offset,
          count: 32000, expectedRevision: offset > 0 && previous && previous.path === path ? previous.revision : null, requestGeneration: epoch });
        if (disposed || !active || epoch !== generation || current.documentGeneration !== owner.documentGeneration) return;
        if (result.requestGeneration !== epoch || !Array.isArray(result.files) || result.files.length > 512 || typeof result.text !== "string" || result.text.length > 32000)
          throw new Error("Ответ исходника не соответствует текущему запросу.");
        sourcePage = result;
        fileNavigation.render(result.files, result.path);
        sourceViewer = RNAssistantViewerRegistry.mount("text", source, { text: result.text, code: true, path: result.path, offset: result.offset,
          totalCharacters: result.totalCharacters, hasPrevious: result.offset > 0, hasNext: result.offset + result.text.length < result.totalCharacters,
          onPrevious: function () { loadSource(result.path, Math.max(0, result.offset - 32000)).catch(fail); },
          onNext: function () { loadSource(result.path, result.offset + result.text.length).catch(fail); }, onCopy: copyTextResult });
        updateSearchAdapter();
        syncFileNavigationAvailability();
        return result;
      } catch (error) {
        if (disposed || !active || epoch !== generation) return;
        fileNavigation.select(sourcePage && sourcePage.path);
        syncFileNavigationAvailability();
        throw error;
      } finally { if (!disposed && epoch === generation) loadingSource = 0; }
    }
    function apply(value) {
      if (disposed || !value || value.browserTabId !== target.browserTabId || session && value.sessionId !== session ||
          current && value.revision < current.revision) return;
      if (!session) return; // Open response establishes ownership before events are admitted.
      if (!current || (current.target && current.target.documentId) !== (value.target && value.target.documentId)) {
        authorityRevision = -1; sourceInvalidated = false; sourceRefreshEpoch++;
      }
      current = value;
      if (!value.manualControl) { handoverPending = false; handoverRequested = false; }
      else sendHandover();
      if (value.visitRevision > recordedVisit && ["Ready", "Unavailable"].includes(value.status)) {
        RNAssistantDockWorkspace.recordNavigation(value); recordedVisit = value.visitRevision;
      }
      annotationHost.update(value);
      if (value.status === "Closed") { agentAttachments.clear(); session = null; sourcePage = null; fileNavigation.reset(); syncFileNavigationAvailability();
        var inline = target.inline && inlineHtmlPreviews.get(target.browserTabId);
        if (inline && !restarting) inline.html = null;
        loading.hidden = true; loading.textContent = "";
        stopReads(); fail(value.message || "Страница закрыта вместе с исходным контекстом."); return; }
      if (targetGeneration !== value.documentGeneration) { stopReads(); sourcePage = null; RNAssistantViewerRegistry.unmount(source); source.replaceChildren();
        clearHomeSuggestions();
        fileNavigation.reset();
        RNAssistantDockWorkspace.updateTarget({ view: "browser", browserTabId: target.browserTabId, ownerScope: value.ownerScope, chatBound: value.chatBound,
          locator: value.url, title: value.title || "Browser", inline: target.inline });
        targetGeneration = value.documentGeneration; sourceViewer = null; updateSearchAdapter(); versionControls.reset(); versionControls.refresh();
        if (comparison) { comparison.dispose(); comparison.element.remove(); comparison = null; }
        var descriptor = value.target;
        if (descriptor && descriptor.state === "Ready" && descriptor.capabilities.versions &&
            (descriptor.savedVersionId || descriptor.kind === "HtmlWorkspace" || descriptor.kind === "HtmlArtifact")) {
          comparison = RNAssistantBrowserSourceDiff.create(descriptor); root.appendChild(comparison.element);
        }
        if (!comparison && selectedMode === "diff") selectedMode = "source";
        providerActions.refresh(); if (homeView) { homeView.dispose(); homeView = null; } }
      if (value.status === "Unavailable" && targetGeneration === value.documentGeneration && selectedMode === "preview" &&
          !(value.target && value.target.kind === "InlineHtml")) selectedMode = "source";
      pageFind.update(value); surface.waiting(label(), value.message || " "); presentation(); render();
    }
    async function command(action, extra, silent) {
      if (!session) throw new Error("Страница ещё не открыта.");
      var observedAuthority = authorityRevision;
      var runtimeAction = action === "Navigate" ? "NavigateUser" : action;
      var binding = session, value = await send("browserCommand", Object.assign({ action: runtimeAction, browserTabId: target.browserTabId,
        sessionId: binding, operationId: operationId }, extra || {}));
      var changed = !current || !value || value.status !== current.status || value.documentGeneration !== current.documentGeneration ||
        value.url !== current.url || value.attached !== current.attached;
      if (!disposed && binding === session && (!silent || changed)) { apply(value);
        if (["Navigate", "Back", "Forward", "Reload", "Home"].includes(action)) {
          if (observedAuthority === authorityRevision) sourceInvalidated = false;
          else if (sourceInvalidated) refreshSource();
        }
      }
      return value;
    }
    async function open() {
      if (session || opening || disposed || !active || target.managedOpen || target.inline && current && current.status === "Closed") return opening;
      opening = (async function () {
        if (!state.bridgeToken && state.initializePromise) await state.initializePromise;
        if (disposed || !active) return;
        var preview = target.inline && inlineHtmlPreviews.get(target.browserTabId);
        var html = preview && preview.html;
        if (target.inline && typeof html !== "string") throw new Error("Интерактивный HTML истёк. Нажмите Play в сообщении ещё раз.");
        var payload = { action: target.inline ? "OpenInline" : "Open", browserTabId: target.browserTabId, operationId: operationId,
          ownerScope: target.ownerScope, locator: target.locator, bindChat: target.chatBound !== false };
        if (target.inline) { payload.html = html; payload.title = target.title; }
        var value = await send("browserCommand", payload);
        if (disposed) { await send("browserCommand", { action: "Close", browserTabId: target.browserTabId, sessionId: value.sessionId }); return; }
        if (session === value.sessionId) return; // An agent has already adopted this in-flight manual open.
        session = value.sessionId; apply(value);
        if (!mount.isConnected) throw new Error("Вкладка Browser не прикреплена к Dock.");
        renderer.layout(); await new Promise(function (resolve) { requestAnimationFrame(resolve); });
        var bounds = nativeBounds(true); placementKey = JSON.stringify(bounds);
        await command("Attach", { bounds: bounds });
        layoutNative();
        if (active && targetInvalidated) reloadInvalidated();
      }());
      try { await opening; } finally { opening = null; }
    }
    function adoptAgentOpen(request) {
      var value = request.page;
      if (disposed || session && session !== value.sessionId || current && current.sessionId === value.sessionId &&
          (current.documentGeneration !== value.documentGeneration || current.url !== value.url || current.status === "Closed")) return false;
      if (agentAttachments.has(request.operationId)) return true;
      session = value.sessionId; operationId = value.operationId;
      agentAttachments.set(request.operationId, { session: session, generation: value.documentGeneration, expires: performance.now() + 10000 });
      apply(value);
      if (selectedMode !== "preview") selectMode("preview");
      layoutNative();
      return true;
    }
    function restartInline() {
      if (!target.inline || disposed) return false;
      if (restarting) return restarting;
      restarting = (async function () {
        if (opening) await opening;
        if (disposed) return false;
        if (session) {
          var closing = session;
          try { await command("Close"); }
          catch (error) { if (session === closing) throw error; }
        }
        if (disposed || !active) return false;
        current = null; targetGeneration = -1; recordedVisit = 0; placementKey = "";
        operationId = id();
        await open();
        return !disposed && !!session;
      }()).catch(function (error) { fail(error); throw error; }).finally(function () { restarting = null; });
      return restarting;
    }
    live.set(target.browserTabId, apply);
    return { element: mount, layout: renderer.layout, annotationHost: annotationHost, canClose: function () { return !annotationHost.busy(); },
      adoptAgentOpen: adoptAgentOpen, restartInline: restartInline,
      bridgeReady: function () { if (active && !disposed && !session && !opening) open().catch(fail); },
      pluginImported: function (pluginId) {
        if (disposed || !pluginId) return;
        clearHomeSuggestions();
        var value = current && current.target, locator = RNAssistantBrowserTarget.preferenceLocator(current && current.url || target.locator);
        var parts = locator && locator.split("/"), matches = value && value.pluginId ? value.pluginId === pluginId
          : parts && parts[1] === "plugins" && decodeURIComponent(parts[2]) === pluginId;
        if (!matches) return;
        if (!session) {
          if (opening) targetInvalidated = true;
          else if (active) open().catch(fail);
          return;
        }
        targetInvalidated = true;
        if (active) reloadInvalidated();
      },
      authorityChanged: function (notice) {
        var value = current && current.target;
        if (!value || value.immutable || value.kind !== "HtmlWorkspace" || notice.scope !== "document:" + value.documentId ||
            !Number.isSafeInteger(notice.generation) || notice.generation <= authorityRevision) return;
        authorityRevision = notice.generation; sourceInvalidated = true; if (active) refreshSource();
        clearHomeSuggestions();
      },
      route: function () { return { view: "browser", browserTabId: target.browserTabId, ownerScope: current && current.ownerScope || target.ownerScope,
        chatBound: current ? current.chatBound : target.chatBound, locator: current && current.url || target.locator,
        title: current && current.title || target.title, status: current && current.status, inline: target.inline, sessionId: session,
        generation: current && current.documentGeneration, source: current && current.target }; },
      activate: function () { if (disposed || active) return; active = true; renderer.activate(); if (session) { if (targetInvalidated) reloadInvalidated(); else { render(); if (current) annotationHost.update(current); if (sourceInvalidated) refreshSource(); else versionControls.refresh(); } } else open().catch(fail); },
      deactivate: function () { if (!active) return; pageFind.deactivate(); annotationHost.deactivate(); active = false; stopReads(); versionControls.deactivate(); providerActions.deactivate(); if (comparison) comparison.deactivate(); if (homeView) homeView.deactivate(); surface.collapse(); renderer.deactivate(); layoutNative(); },
      dispose: function () { if (disposed) return; disposed = true; active = false; agentAttachments.clear(); inlineHtmlPreviews.delete(target.browserTabId); handoverListeners.abort(); stopOverlayWatch(); cancelAnimationFrame(placementQueued); nativeSize.disconnect(); stopReads();
        if (live.get(target.browserTabId) === apply) live.delete(target.browserTabId);
        if (versionViews.get(target.browserTabId) === versionView) versionViews.delete(target.browserTabId);
        versionControls.dispose(); providerActions.dispose(); if (comparison) comparison.dispose();
        fileNavigation.dispose();
        pageFind.dispose(); annotationHost.dispose(); if (homeView) homeView.dispose(); if (session) send("browserCommand", { action: "Close", browserTabId: target.browserTabId, sessionId: session }).catch(function () {});
        RNAssistantViewerRegistry.unmount(source); RNAssistantViewerRegistry.unmount(mount); surface.clear(); shell.destroy(); root.replaceChildren(); }
    };
  }
  function sameScope(left, right) {
    return left && right && left.kind === right.kind && left.scopeId === right.scopeId;
  }
  function reusableRoute(locator, owner, options) {
    var address = RNAssistantBrowserTarget.navigationKey(locator), found, best = -1;
    if (!address) return null;
    RNAssistantDockWorkspace.eachView("browser", function (view) {
      var route = view.route(), source = route.source;
      if (route.inline || route.status === "Closed" || route.status === "Removed" ||
          RNAssistantBrowserTarget.navigationKey(route.locator) !== address) return;
      var sameOwner = sameScope(owner, route.ownerScope) && (owner.chatId || "") === (route.ownerScope.chatId || "");
      if (options.background && (!sameOwner || route.chatBound === false)) return;
      var sharedWorkspace = address.startsWith("/projects/") || address.startsWith("/plugins/");
      var shared = sharedWorkspace && (sameScope(owner, route.ownerScope) || source && sameScope(owner, source.ownerScope) ||
        owner.kind === "User" && source && source.ownerScope && source.ownerScope.kind === "Workspace");
      var exactResource = options.resourceUri && source && source.resourceUri === options.resourceUri && sameScope(owner, source.ownerScope);
      if (!sameOwner && !shared && !exactResource) return;
      var score = (sameOwner ? 2 : 0) + (RNAssistantDockWorkspace.isVisible(key(route)) ? 1 : 0);
      if (score > best) { best = score; found = route; }
    });
    return found;
  }
  function open(locator, owner, options) {
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(function () { open(locator, owner, options); })) return true;
    locator = locator || "/";
    owner = owner || { kind: "User", scopeId: "local" };
    options = options || {};
    var existing = !options.browserTabId && reusableRoute(locator, owner, options);
    return RNAssistantDockWorkspace.openTarget(existing || { view: "browser", browserTabId: options.browserTabId || id(),
      locator: locator, ownerScope: owner, title: options.title || "Browser", chatBound: options.chatBound }, options);
  }
  function openResource(target, options) {
    // Format a local display address from the exact runtime reference. The native
    // resolver still validates the document, artifact, revision and availability.
    var match = /^rna:\/\/chat\/[^/]+\/artifact\/([^/]+)\/revision\/([1-9][0-9]*)$/.exec(target.resourceUri || "");
    if (!match || !target.documentId || !target.chatId) return false;
    var locator; try { locator = "/resources/" + encodeURIComponent(decodeURIComponent(match[1]) + "~" + match[2]); } catch (_) { return false; }
    return open(locator, { kind: "Document", scopeId: target.documentId, chatId: target.chatId },
      Object.assign({ resourceUri: target.resourceUri, title: target.title }, options || {}));
  }
  function openHtmlPreview(html, options) {
    if (typeof html !== "string" || !html.length || html.length > 200000) return false;
    if (!available) return false;
    options = options || {};
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(function () { openHtmlPreview(html, options); })) return true;
    var placement = { referencePanel: options.referencePanel }, existing;
    if (options.blockId) inlineHtmlPreviews.forEach(function (preview, tabId) {
      if (!existing && preview.blockId === options.blockId) existing = RNAssistantDockWorkspace.getView(key({ browserTabId: tabId }));
    });
    if (existing) {
      var route = existing.route();
      if (!RNAssistantDockWorkspace.openTarget(route, placement)) return false;
      inlineHtmlPreviews.get(route.browserTabId).html = html;
      return existing.restartInline();
    }
    var browserTabId = id(), keyValue = "inline-" + browserTabId;
    inlineHtmlPreviews.set(browserTabId, { html: html, blockId: options.blockId });
    var opened = false;
    try {
      opened = RNAssistantDockWorkspace.openTarget({ view: "browser", browserTabId: browserTabId,
        operationId: "html-" + crypto.randomUUID(), locator: "/resources/" + encodeURIComponent(keyValue) + "/files/index.html",
        ownerScope: { kind: "User", scopeId: "local" }, title: String(options.title || "HTML из чата").slice(0, 160),
        inline: true, chatBound: false }, placement);
    } finally { if (!opened) inlineHtmlPreviews.delete(browserTabId); }
    return opened;
  }
  function openRequested(request) {
    if (!request || !request.ownerScope || !request.ownerScope.scopeId) return false;
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(function () { openRequested(request); })) return true;
    if (request.origin === "PageLink" || request.origin === "AgentLink") {
      var source = RNAssistantDockWorkspace.getView(key({ browserTabId: request.sourceBrowserTabId }));
      var route = source && source.route();
      if (!route || route.sessionId !== request.sourceSessionId || route.generation !== request.sourceGeneration ||
          !sameScope(route.ownerScope, request.ownerScope) || route.ownerScope.chatId !== request.ownerScope.chatId) return false;
      var background = request.origin === "AgentLink";
      return open(request.locator, route.ownerScope,
        { referencePanel: key(route), chatBound: route.chatBound, beside: background, background: background });
    }
    var page = request.page;
    if (request.origin !== "Agent" || !request.operationId || !page || !page.sessionId || !page.operationId ||
        page.browserTabId !== request.browserTabId || page.url !== request.locator || !page.chatBound ||
        !sameScope(page.ownerScope, request.ownerScope) || page.ownerScope.chatId !== request.ownerScope.chatId) return false;
    var owner = request.ownerScope, address = owner.kind === "Document" ? { source: "office", sessionId: owner.chatId } :
      { source: "workspace", workspaceId: owner.scopeId, sessionId: owner.chatId };
    var target = { view: "browser", browserTabId: request.browserTabId, ownerScope: owner, locator: request.locator,
      title: request.title || "Browser", managedOpen: true };
    var existing = RNAssistantDockWorkspace.getView(key(target));
    if (existing && !existing.adoptAgentOpen(request)) return false;
    var opened = RNAssistantDockWorkspace.openTarget(target, { referencePanel: RNAssistantConversationPanel.key(address), beside: true, background: true });
    if (!opened && page.operationId === request.operationId) {
      // Dock refused the newly allocated instance. Close its exact session so a
      // tab the user could never reach does not occupy a slot in the runtime.
      send("browserCommand", { action: "Close", browserTabId: page.browserTabId, sessionId: page.sessionId }).catch(function () {});
    }
    var view = opened && (existing || RNAssistantDockWorkspace.getView(key(target)));
    return !!view && (!!existing || view.adoptAgentOpen(request));
  }
  window.RNAssistantBrowser = { open: open, openResource: openResource, openHtmlPreview: openHtmlPreview, create: create, key: key, unavailable: unavailable,
    openRequested: openRequested,
    bootstrap: function (value) { available = value === true; },
    ready: function () { RNAssistantDockWorkspace.eachView("browser", function (view) { view.bridgeReady(); }); },
    pluginImported: function (pluginId) { RNAssistantDockWorkspace.eachView("browser", function (view) { view.pluginImported(pluginId); }); },
    annotations: function (target) {
      var found = null; RNAssistantDockWorkspace.eachView("browser", function (view) {
        var owner = view.annotationHost && view.annotationHost.address();
        if (!found && owner && owner.source === target.source && owner.sessionId === target.sessionId && owner.workspaceId === target.workspaceId) found = view.annotationHost.annotations;
      }); return found;
    },
    showAnnotation: function (item, target) {
      var shown = false; RNAssistantDockWorkspace.eachView("browser", function (view) {
        var owner = view.annotationHost && view.annotationHost.address(), annotations = view.annotationHost && view.annotationHost.annotations;
        if (!shown && owner && owner.source === target.source && owner.sessionId === target.sessionId && owner.workspaceId === target.workspaceId && annotations.canShow(item)) { annotations.showSent(item); shown = true; }
      }); if (!shown) window.alert("Откройте точную исходную версию в Browser: текущие страницы не соответствуют этой аннотации.");
    },
    versionsChanged: function (value) { if (value && value.target) versionViews.forEach(function (notify) { notify(value); }); },
    apply: function (value) { var fn = value && live.get(value.browserTabId); if (fn) fn(value); },
    register: function (dock) { dock.registerView("browser", { title: "Browser", icon: "globe", discovery: "primary", order: 10, perObject: true, menu: true, maximumInstances: 16, renderer: "onlyWhenVisible",
      accepts: accepts, key: key, create: create, preferenceTarget: preferenceTarget, titleFor: function (target) { return target.title || "Browser"; } }); }
  };
}());
