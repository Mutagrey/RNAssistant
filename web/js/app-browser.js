(function () {
  "use strict";
  var live = new Map(), versionViews = new Map(), available = false;
  var unavailable = "Исполнение страницы недоступно: сетевая изоляция WebView2 ещё не подтверждена. Исходники и версии доступны.";
  function id() { return "browser-" + crypto.randomUUID(); }
  function key(target) { return "browser:" + target.browserTabId; }
  function accepts(target) {
    return target && target.view === "browser" && typeof target.browserTabId === "string" && target.browserTabId.length <= 256 &&
      target.ownerScope && ["Document", "Workspace", "User"].includes(target.ownerScope.kind) && typeof target.ownerScope.scopeId === "string" &&
      target.ownerScope.scopeId.length > 0 && target.ownerScope.scopeId.length <= 256 && !!RNAssistantBrowserTarget.preferenceLocator(target.locator);
  }
  function preferenceTarget(target) {
    if (!accepts(target)) return null;
    var scope = target.ownerScope;
    return { view: "browser", browserTabId: target.browserTabId, locator: RNAssistantBrowserTarget.preferenceLocator(target.locator),
      ownerScope: { kind: scope.kind, scopeId: scope.scopeId, chatId: typeof scope.chatId === "string" && scope.chatId.length <= 256 ? scope.chatId : null },
      title: String(target.title || "Browser").slice(0, 160) };
  }
  function create(target) {
    var root = document.createElement("section"); root.className = "browser-object-view"; root.setAttribute("aria-label", "Browser"); root.dataset.browserTabId = target.browserTabId;
    var content = document.createElement("div"); content.className = "browser-object-content browser-local-content";
    var files = document.createElement("select"); files.className = "rna-form-control compact browser-document-file"; files.setAttribute("aria-label", "Файл проекта");
    var source = document.createElement("div"); source.className = "browser-document-source product-viewer";
    var empty = document.createElement("div"); empty.className = "empty-state";
    content.append(files, source, empty); root.appendChild(content);
    var mount = document.createElement("div"); mount.className = "browser-view";
    var renderer = RNAssistantViewerRegistry.mount("browser", mount, { surface: root, portalHost: document.querySelector(".shell") });
    var active = false, disposed = false, opening = null, session = null, current = null, generation = 0, pending = new Set();
    var sourcePage = null, loadingSource = 0, selectedMode = "source", targetGeneration = -1, homeView = null;
    var operationId = target.operationId || id(), definitionDialog = null, targetInvalidated = false, sourceInvalidated = false, authorityRevision = -1, sourceRefreshEpoch = 0;
    function discuss() {
        var binding = current && current.target && current.target.ownerScope;
        if (binding && !binding.chatId && current.target.pluginId) {
          var plugin = RNAssistantPlugins.item(current.target.pluginId), pageGeneration = current.documentGeneration;
          if (!plugin) throw new Error("Проект плагина недоступен.");
          return RNAssistantPlugins.openSourceChat(null, plugin, { referencePanel: key(target), beside: true,
            isCurrent: function () { return active && !disposed && current.documentGeneration === pageGeneration; } });
        }
        if (!binding || !binding.chatId) throw new Error("Выберите чат в дереве проекта и откройте страницу в его контексте.");
        return RNAssistantConversationPanel.open(binding.kind === "Document" ? { source: "office", sessionId: binding.chatId } :
          { source: "workspace", workspaceId: binding.scopeId, sessionId: binding.chatId }, { referencePanel: key(target), beside: true });
    }
    var surface = RNAssistantPreviewSurface.attach(root, { reload: function () { return command("Reload"); }, canReload: function () { return active && !!session; }, discuss: discuss });
    var comparison = null;
    var providerActions = RNAssistantBrowserActions.mount(surface.actions, { target: function () { return current && current.target; },
      generation: function () { return current && current.documentGeneration; }, isCurrent: function () { return active && !disposed; },
      status: function (message) { surface.waiting(label(), message); }, error: fail, discuss: discuss });
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
    versionViews.set(target.browserTabId, function (change) {
      var value = current && current.target, changed = change.target;
      if (value && (value.pluginId ? value.pluginId === changed.pluginId : value.logicalId ? value.documentId === changed.documentId && value.logicalId === changed.logicalId :
        value.kind === changed.kind && value.workspaceId === changed.workspaceId)) {
        if (change.reason === "Delete" && change.versionId && change.versionId === value.savedVersionId) { targetInvalidated = true; if (active) reloadInvalidated(); }
        else if (active) versionControls.refresh();
      }
    });
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
    async function request(type, payload) {
      var task = send(type, payload); pending.add(task);
      try { return await task; } finally { pending.delete(task); }
    }
    function stopReads() {
      generation++; sourceRefreshEpoch++; loadingSource = 0;
      if (definitionDialog) definitionDialog.close(false);
      pending.forEach(function (task) { if (task.requestId) cancelBridgeRequest(task.requestId).catch(function () {}); }); pending.clear();
    }
    async function suggestions(prefix, epoch) {
      var scope = current && current.ownerScope || target.ownerScope;
      var result = await request("queryNavigationSuggestions", { ownerScope: scope, prefix: prefix, requestGeneration: epoch, limit: 12 });
      var seen = new Set();
      return { requestGeneration: result.requestGeneration, items: (result.items || []).concat(RNAssistantDockWorkspace.recentNavigation(scope, prefix, 8))
        .filter(function (entry) { if (!RNAssistantBrowserTarget.preferenceLocator(entry.displayLocator) || seen.has(entry.displayLocator)) return false; seen.add(entry.displayLocator); return true; }).slice(0, 20) };
    }
    function createHome(parent) {
      var query = "", epoch = 0, shown = false, dead = false, entries = [], loading = false, error = "", view;
      function model() {
        var scope = current.ownerScope, recent = RNAssistantDockWorkspace.recentNavigation(scope, query, 8), seen = new Set();
        function rows(items) { return items.filter(function (entry) { if (seen.has(entry.displayLocator)) return false; seen.add(entry.displayLocator); return true; }).map(function (entry) {
          return { key: entry.displayLocator, title: entry.title || entry.displayLocator, icon: "globe", description: entry.sourceLabel + " · " + entry.displayLocator,
            disabledReason: entry.disabledReason && !["None", "IsolationUnqualified", "ProjectDefinitionRequired"].includes(entry.disabledReason) ? entry.disabledReason === "Removed" ? "Версия удалена" : "Источник недоступен" : "",
            payload: { locator: entry.displayLocator } };
        }); }
        var sections = [{ title: "Недавно", entries: rows(recent) }, { title: "Локальные страницы", entries: rows(entries.slice(0, 12)) }];
        if (recent.length && !query) sections.push({ title: "История", entries: [{ key: "clear", title: "Очистить недавние страницы", icon: "trash", description: "Только список посещений в этом окне", payload: { clear: true } }] });
        return { title: "Главная", placeholder: "Найти локальную страницу…", search: query, sections: sections, loading: loading, error: error,
          empty: "Нет локальных страниц. Откройте HTML из материалов или добавьте проект.", note: RNAssistantDockWorkspace.recentNavigationNotice() };
      }
      async function refresh() {
        var serial = ++epoch; loading = true; error = ""; view.update(model());
        try {
          var result = await request("queryNavigationSuggestions", { ownerScope: current.ownerScope, prefix: query, requestGeneration: serial, limit: 12 });
          if (dead || !shown || serial !== epoch || result.requestGeneration !== serial) return;
          entries = (result.items || []).filter(function (entry) { return !!RNAssistantBrowserTarget.preferenceLocator(entry.displayLocator); }).slice(0, 12);
        } catch (failure) { if (!dead && shown && serial === epoch) error = failure.detail || failure.message; }
        finally { if (!dead && shown && serial === epoch) { loading = false; view.update(model()); } }
      }
      view = RNAssistantStartSurface.mount(parent, model(), {
        onSearch: function (value) { query = value.slice(0, 256); entries = []; refresh(); },
        onSelect: function (payload) { if (payload.clear) { RNAssistantDockWorkspace.clearRecentNavigation(); view.update(model()); }
          else return command("Navigate", { locator: payload.locator }); }
      });
      return { activate: function () { if (shown || dead) return; shown = true; refresh(); },
        deactivate: function () { shown = false; epoch++; }, dispose: function () { dead = true; epoch++; view.dispose(); } };
    }
    function presentation() {
      surface.setDocument({ address: current && current.url || target.locator,
        mode: selectedMode, supportedModes: current && current.status !== "Home" ? ["preview", "source"].concat(comparison ? ["diff"] : []) : [],
        canGoBack: current && current.canGoBack, canGoForward: current && current.canGoForward,
        back: function () { return command("Back"); }, forward: function () { return command("Forward"); },
        home: function () { return command("Home"); }, stop: function () { stopReads(); return command("Stop"); },
        navigate: function (locator) { return command("Navigate", { locator: locator }); },
        suggest: suggestions
      }, selectMode);
    }
    function selectMode(mode) { selectedMode = mode; presentation(); render(); }
    function render() {
      if (!active || disposed || !current) return;
      var home = current.status === "Home", removed = current.status === "Removed";
      content.hidden = selectedMode === "diff" && !!comparison;
      if (comparison) { if (content.hidden) { comparison.activate(); return; } comparison.deactivate(); }
      empty.hidden = !home && !removed && selectedMode === "source";
      source.hidden = files.hidden = home || removed || selectedMode !== "source";
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
      sourcePage = null; loadingSource = epoch; files.disabled = true;
      try {
        var result = await request("readBrowserSource", { ownerScope: owner.ownerScope, locator: owner.url, path: path, offset: offset,
          count: 32000, expectedRevision: offset > 0 && previous && previous.path === path ? previous.revision : null, requestGeneration: epoch });
        if (disposed || !active || epoch !== generation || current.documentGeneration !== owner.documentGeneration) return;
        if (result.requestGeneration !== epoch || !Array.isArray(result.files) || result.files.length > 512 || typeof result.text !== "string" || result.text.length > 32000)
          throw new Error("Ответ исходника не соответствует текущему запросу.");
        sourcePage = result;
        files.replaceChildren(); result.files.forEach(function (path) { files.appendChild(new Option(path, path)); }); files.value = result.path;
        RNAssistantViewerRegistry.mount("text", source, { text: result.text, code: true, path: result.path, offset: result.offset,
          totalCharacters: result.totalCharacters, hasPrevious: result.offset > 0, hasNext: result.offset + result.text.length < result.totalCharacters,
          onPrevious: function () { loadSource(result.path, Math.max(0, result.offset - 32000)).catch(fail); },
          onNext: function () { loadSource(result.path, result.offset + result.text.length).catch(fail); }, onCopy: copyTextResult });
      } finally { if (!disposed && epoch === generation) { loadingSource = 0; files.disabled = false; } }
    }
    files.onchange = function () { loadSource(files.value, 0).catch(fail); };
    function apply(value) {
      if (disposed || !value || value.browserTabId !== target.browserTabId || session && value.sessionId !== session ||
          current && value.revision < current.revision) return;
      if (!session) return; // Open response establishes ownership before events are admitted.
      if (!current || (current.target && current.target.documentId) !== (value.target && value.target.documentId)) {
        authorityRevision = -1; sourceInvalidated = false; sourceRefreshEpoch++;
      }
      current = value;
      if (value.status === "Closed") { session = null; stopReads(); fail("Страница закрыта вместе с исходным контекстом."); return; }
      if (targetGeneration !== value.documentGeneration) { stopReads(); sourcePage = null; RNAssistantViewerRegistry.unmount(source); source.replaceChildren();
        RNAssistantDockWorkspace.updateTarget({ view: "browser", browserTabId: target.browserTabId, ownerScope: value.ownerScope,
          locator: value.url, title: value.title || "Browser" });
        targetGeneration = value.documentGeneration; versionControls.reset(); versionControls.refresh();
        if (comparison) { comparison.dispose(); comparison.element.remove(); comparison = null; }
        var descriptor = value.target;
        if (descriptor && descriptor.state === "Ready" && descriptor.capabilities.versions &&
            (descriptor.savedVersionId || descriptor.kind === "HtmlWorkspace" || descriptor.kind === "HtmlArtifact")) {
          comparison = RNAssistantBrowserSourceDiff.create(descriptor); root.appendChild(comparison.element);
        }
        if (!comparison && selectedMode === "diff") selectedMode = "source";
        providerActions.refresh(); if (homeView) { homeView.dispose(); homeView = null; } }
      surface.waiting(label(), value.message || " "); presentation(); render();
    }
    async function command(action, extra) {
      if (!session) throw new Error("Страница ещё не открыта.");
      var observedAuthority = authorityRevision;
      var binding = session, value = await send("browserCommand", Object.assign({ action: action, browserTabId: target.browserTabId,
        sessionId: binding, operationId: operationId }, extra || {}));
      if (!disposed && binding === session) { apply(value);
        if (["Navigate", "Back", "Forward", "Reload", "Home"].includes(action)) {
          if (observedAuthority === authorityRevision) sourceInvalidated = false;
          else if (sourceInvalidated) refreshSource();
        }
        if (["Navigate", "Back", "Forward"].includes(action)) RNAssistantDockWorkspace.recordNavigation(value); }
    }
    async function open() {
      if (session || opening || disposed) return opening;
      opening = (async function () {
        var value = await send("browserCommand", { action: "Open", browserTabId: target.browserTabId, operationId: operationId,
          ownerScope: target.ownerScope, locator: target.locator });
        if (disposed) { await send("browserCommand", { action: "Close", browserTabId: target.browserTabId, sessionId: value.sessionId }); return; }
        session = value.sessionId; apply(value); if (target.recordVisit) RNAssistantDockWorkspace.recordNavigation(value);
        if (!mount.isConnected) throw new Error("Вкладка Browser не прикреплена к Dock.");
        var rect = mount.getBoundingClientRect();
        await command("Attach", { bounds: { visible: active && mount.isConnected && rect.width > 0 && rect.height > 0,
          x: Math.max(0, rect.x), y: Math.max(0, rect.y), width: Math.min(rect.width, innerWidth - Math.max(0, rect.x)),
          height: Math.min(rect.height, innerHeight - Math.max(0, rect.y)), viewportWidth: innerWidth, viewportHeight: innerHeight } });
      }());
      try { await opening; } finally { opening = null; }
    }
    live.set(target.browserTabId, apply);
    return { element: mount, layout: renderer.layout,
      authorityChanged: function (notice) {
        var value = current && current.target;
        if (!value || value.immutable || value.kind !== "HtmlWorkspace" || notice.scope !== "document:" + value.documentId ||
            !Number.isSafeInteger(notice.generation) || notice.generation <= authorityRevision) return;
        authorityRevision = notice.generation; sourceInvalidated = true; if (active) refreshSource();
      },
      route: function () { return { view: "browser", browserTabId: target.browserTabId, ownerScope: current && current.ownerScope || target.ownerScope, locator: current && current.url || target.locator, title: current && current.title || target.title }; },
      activate: function () { if (disposed || active) return; active = true; renderer.activate(); if (session) { if (targetInvalidated) reloadInvalidated(); else { render(); if (sourceInvalidated) refreshSource(); else versionControls.refresh(); } } else open().catch(fail); },
      deactivate: function () { if (!active) return; active = false; stopReads(); versionControls.deactivate(); providerActions.deactivate(); if (comparison) comparison.deactivate(); if (homeView) homeView.deactivate(); surface.collapse(); renderer.deactivate(); },
      dispose: function () { if (disposed) return; disposed = true; active = false; stopReads(); live.delete(target.browserTabId); versionViews.delete(target.browserTabId); versionControls.dispose(); providerActions.dispose(); if (comparison) comparison.dispose();
        if (homeView) homeView.dispose(); if (session) send("browserCommand", { action: "Close", browserTabId: target.browserTabId, sessionId: session }).catch(function () {});
        RNAssistantViewerRegistry.unmount(source); RNAssistantViewerRegistry.unmount(mount); surface.clear(); root.replaceChildren(); }
    };
  }
  function open(locator, owner, options) {
    owner = owner || { kind: "User", scopeId: "local" }; options = Object.assign({}, options || {});
    if (options.reuse && !options.browserTabId) RNAssistantDockWorkspace.eachView("browser", function (view) {
      var route = view.route(); if (route.locator === locator && route.ownerScope.kind === owner.kind && route.ownerScope.scopeId === owner.scopeId &&
        (route.ownerScope.chatId || "") === (owner.chatId || "")) options.browserTabId = route.browserTabId;
    });
    return RNAssistantDockWorkspace.openTarget({ view: "browser", browserTabId: options && options.browserTabId || id(),
      operationId: options && options.operationId, locator: locator || "/", ownerScope: owner || { kind: "User", scopeId: "local" }, title: options && options.title || "Browser", recordVisit: true }, options);
  }
  function openResource(target, options) {
    // Format a local display address from the exact runtime reference. The native
    // resolver still validates the document, artifact, revision and availability.
    var match = /^rna:\/\/chat\/[^/]+\/artifact\/([^/]+)\/revision\/([1-9][0-9]*)$/.exec(target.resourceUri || "");
    if (!match || !target.documentId || !target.chatId) return false;
    var locator; try { locator = "/resources/" + encodeURIComponent(decodeURIComponent(match[1]) + "~" + match[2]); } catch (_) { return false; }
    return open(locator, { kind: "Document", scopeId: target.documentId, chatId: target.chatId }, Object.assign({ reuse: true, title: target.title }, options || {}));
  }
  window.RNAssistantBrowser = { open: open, openResource: openResource, create: create, key: key, unavailable: unavailable,
    openRequested: function (request) {
      if (!request || !request.operationId || !request.browserTabId || !request.ownerScope || !request.ownerScope.chatId) return false;
      return open(request.locator, request.ownerScope, { browserTabId: request.browserTabId, operationId: request.operationId, title: request.title });
    },
    bootstrap: function (value) { available = value === true; },
    versionsChanged: function (value) { if (value && value.target) versionViews.forEach(function (notify) { notify(value); }); },
    apply: function (value) { var fn = value && live.get(value.browserTabId); if (fn) fn(value); },
    register: function (dock) { dock.registerView("browser", { title: "Browser", icon: "globe", discovery: "primary", order: 10, perObject: true, menu: true, maximumInstances: 16, renderer: "onlyWhenVisible",
      accepts: accepts, key: key, create: create, preferenceTarget: preferenceTarget, titleFor: function (target) { return target.title || "Browser"; } }); }
  };
}());
