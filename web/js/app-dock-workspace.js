(function () {
  "use strict";
  // Placement only. Conversation state, commands and drafts retain their existing owners.
  var api, host, parking, roots = {}, owner = "office", section = "chat";
  var saveTimer, restoring = false, preservePreference = false;
  var compact = false, wideLayout = null, settingsVisible = false;
  var nativeVisible = true, hostCompact = false, compactChat = null, fullPresentation = null, compactEmpty;
  var tabFrame;
  var objectViews = Object.create(null);
  var inspectorWidth;
  var storageKey = "rnassistant.ui.dock.s1.v1", savedKey = storageKey + ".saved", nextChooser = 1;
  var recentKey = storageKey + ".local-recents.v1", recents = null, recentMemoryOnly = false;
  function recentEntry(value) {
    var scope = value && value.scope, locator = value && RNAssistantBrowserTarget.preferenceLocator(value.locator);
    if (!scope || !["User", "Document", "Workspace"].includes(scope.kind) || typeof scope.scopeId !== "string" || !scope.scopeId || scope.scopeId.length > 256 ||
        !locator || locator === "/" || !["HtmlWorkspace", "HtmlArtifact", "HtmlResource", "PluginSource", "PluginRelease", "WorkspaceHtmlProject"].includes(value.kind) ||
        !Number.isSafeInteger(value.visitedAt) || value.visitedAt < 0 || typeof value.title !== "string") return null;
    return { locator: locator, title: value.title.replace(/[\u0000-\u001f\u007f]/g, " ").slice(0, 160), kind: value.kind,
      visitedAt: value.visitedAt, scope: { kind: scope.kind, scopeId: scope.scopeId } };
  }
  function recentIdentity(value) { return value.scope.kind + ":" + value.scope.scopeId + ":" + value.locator; }
  function writeRecents() {
    if (recentMemoryOnly) return;
    try { localStorage.setItem(recentKey, JSON.stringify({ version: 1, entries: recents })); }
    catch (_) { recentMemoryOnly = true; notice("Недавние страницы доступны только до закрытия окна: хранилище недоступно."); }
  }
  function readRecents() {
    if (recents) return recents;
    recents = [];
    try {
      var raw = localStorage.getItem(recentKey);
      if (raw && raw.length <= 1024 * 1024) {
        var saved = JSON.parse(raw), seen = new Set();
        if (saved.version === 1 && Array.isArray(saved.entries)) recents = saved.entries.slice(0, 200).map(recentEntry).filter(Boolean)
          .sort(function (a, b) { return b.visitedAt - a.visitedAt; }).filter(function (entry) { var key = recentIdentity(entry); if (seen.has(key)) return false; seen.add(key); return true; });
      }
    } catch (error) { if (!(error instanceof SyntaxError)) { recentMemoryOnly = true; notice("Недавние страницы доступны только до закрытия окна: хранилище недоступно."); } }
    return recents;
  }
  function recordNavigation(value) {
    var target = value && value.target;
    if (!target || target.state !== "Ready" || !["Ready", "Unavailable"].includes(value.status)) return;
    var entry = recentEntry({ locator: value.url, title: value.title || target.title || "", kind: target.kind,
      scope: target.ownerScope, visitedAt: Date.now() });
    if (!entry) return;
    recents = [entry].concat(readRecents().filter(function (other) { return recentIdentity(other) !== recentIdentity(entry); })).slice(0, 200); writeRecents();
  }
  function recentNavigation(scope, prefix, limit) {
    if (!scope) return [];
    prefix = String(prefix || "").toLocaleLowerCase();
    return readRecents().filter(function (entry) {
      return (scope.kind === "User" ? entry.scope.kind !== "Document" : scope.kind === entry.scope.kind && scope.scopeId === entry.scope.scopeId) &&
        (entry.locator.toLocaleLowerCase().includes(prefix) || entry.title.toLocaleLowerCase().includes(prefix));
    }).slice(0, Math.min(20, limit || 8)).map(function (entry) { return { title: entry.title, displayLocator: entry.locator, kind: entry.kind,
      sourceLabel: "Недавно · " + entry.scope.kind, disabledReason: "None" }; });
  }
  var views = {
    conversation: { title: "Чат", icon: "chat", section: "chat" },
    inspector: { title: "Инспектор", icon: "panel", discovery: "contextual", renderer: "onlyWhenVisible" },
    request: { title: "Запрос модели", icon: "code", discovery: "contextual" },
    diagnostics: { title: "Диагностика", icon: "chart", discovery: "contextual", onVisible: function (shown) { RNAssistantDiagnostics.onVisible(shown); },
      onClose: function () { RNAssistantDiagnostics.onClose(); } },
    settings: { title: "Настройки", icon: "settings", discovery: "contextual", renderer: "onlyWhenVisible", canClose: function () { return RNAssistantSettingsView.canClose(); } }
  };
  function viewIds() { return Object.keys(views).filter(function (id) { return !views[id].targetRoute && (!views[id].perObject || views[id].menu) && (!views[id].available || views[id].available()); }); }
  // Temporary section adapters live in this catalog, not a second navigation registry.
  // S3b.2/S4 replace their delivery with independently mounted object views.
  function registerTargetRoute(id, route) {
    if (views[id]) throw new Error("Workspace route already registered: " + id);
    views[id] = { targetRoute: true, accepts: route.accepts, open: route.open };
  }
  function registerView(id, descriptor) {
    if (views[id]) throw new Error("Workspace view already registered: " + id);
    views[id] = descriptor;
  }
  function openTarget(target, options) {
    if (target && target.view !== "conversation" && window.RNAssistantWindow && RNAssistantWindow.inFull(function () { openTarget(target, options); })) return true;
    var descriptor = target && views[target.view];
    if (!descriptor || !descriptor.accepts || !descriptor.accepts(target)) return false;
    target = Object.freeze(Object.assign({}, target));
    if (descriptor.targetRoute) return descriptor.open(target, options);
    if (!api || !descriptor.perObject) return false;
    var chooser = api.activePanel && /^choose-\d+$/.test(api.activePanel.id) ? api.activePanel : null;
    var id = descriptor.key(target), item = panel(id);
    var reference = options && panel(options.referencePanel);
    var direction = options && options.beside ? (compact || host.clientWidth < 560 ? "within" : "right") : options && options.direction || "within";
    if (hostCompact) direction = "within";
    if (item && options && options.group && item.group !== options.group) item.api.moveTo({ group: options.group, position: "center" });
    if (item && reference && options.beside && direction === "right" && item.group === reference.group)
      item.api.moveTo({ group: reference.group, position: "right" });
    if (!item) {
      var count = api.panels.filter(function (p) { return p.params && p.params.target && p.params.target.view === target.view; }).length;
      if (count >= descriptor.maximumInstances) { notice("Открыто " + count + " просмотров. Закройте один перед открытием нового."); return false; }
      item = api.addPanel({ id: id, component: target.view, title: descriptor.titleFor(target),
        params: { target: target }, renderer: typeof descriptor.renderer === "function" ? descriptor.renderer(target) : descriptor.renderer || "always", minimumWidth: 150, minimumHeight: 120,
        position: options && options.group ? { referenceGroup: options.group, direction: direction } :
          (reference || api.activePanel) ? { referencePanel: reference || api.activePanel, direction: direction } : undefined });
    }
    item.group.api.setVisible(true); item.api.setActive(); changed();
    var record = objectViews[id]; if (record && record.view.focus) record.view.focus();
    if (target.view === "conversation" && target.sessionId && panel("conversation")) panel("conversation").api.close();
    if (chooser && panel(chooser.id)) chooser.api.close();
    if (hostCompact && target.view === "conversation") {
      compactChat = item.id; compactEmpty.hidden = !!target.sessionId; host.classList.toggle("host-compact-no-chat", !target.sessionId);
      // Preserve synchronous selected-chat delivery, including a newly loaded exact chat.
      // Only extend the existing Full placement; do not create another layout store.
      if (fullPresentation && !fullPresentation.layout.panels[item.id]) {
        var saved = fullPresentation.layout, leaf = null;
        function findGroup(node) {
          if (node.type === "branch") node.data.forEach(findGroup);
          else if (node.data.id === item.group.id) leaf = node;
        }
        findGroup(saved.grid.root);
        if (!leaf) { leaf = { type: "leaf", data: { id: item.group.id, views: [], activeView: item.id }, size: saved.grid.width }; saved.grid.root.data.push(leaf); }
        saved.panels[item.id] = api.toJSON().panels[item.id]; leaf.data.views.push(item.id);
        if (!panel(leaf.data.activeView)) leaf.data.activeView = item.id;
        if (!panel(fullPresentation.active)) fullPresentation.active = item.id;
      }
      item.api.maximize(); sync();
    }
    return true;
  }
  function sectionView(name) {
    return Object.keys(views).find(function (id) { return views[id].section === name; }) || null;
  }
  function updateTarget(target) {
    var descriptor = target && views[target.view];
    if (!descriptor || !descriptor.perObject || !descriptor.accepts(target)) return false;
    var item = panel(descriptor.key(target)); if (!item) return false;
    item.api.updateParameters({ target: Object.freeze(Object.assign({}, target)) });
    item.api.setTitle(descriptor.titleFor(target)); changed(); return true;
  }
  function workspaceActive() { return hostCompact || section === "chat" || !!sectionView(section); }
  function placeHost() {
    if (!host) return;
    var source = views[sectionView(section)], target = source && source.layout && source.layout();
    target = target || document.getElementById("chatLayout");
    if (hostCompact) target = document.querySelector(".shell > .content");
    if (host.parentElement !== target) target.appendChild(host);
  }
  function syncObjectViews() {
    Object.keys(objectViews).forEach(function (id) {
      var record = objectViews[id], shown = visible(id);
      record.view.element.inert = !shown;
      if (shown && record.view.layout) record.view.layout();
      if (record.active === shown) return;
      record.active = shown;
      if (shown) record.view.activate(); else record.view.deactivate();
    });
  }
  function createObjectComponent(options, descriptor) {
    var content = element("div", "dock-view"), visibility;
    if (options.name === "conversation") content.classList.add("dock-conversation");
    var id = options.id;
    return { element: content, init: function (parameters) {
      var target = parameters.params && parameters.params.target;
      if (!descriptor.accepts(target) || descriptor.key(target) !== id) throw new Error("Invalid object view target");
      var record = objectViews[id];
      if (!record) record = objectViews[id] = { active: false, view: descriptor.create(Object.freeze(Object.assign({}, target)), parameters.params.presentation || {}) };
      content.appendChild(record.view.element);
      visibility = parameters.api.onDidVisibilityChange(sync);
    }, dispose: function () {
      if (visibility) visibility.dispose();
      Promise.resolve().then(function () {
        // Move/fromJSON replaces a Dock component synchronously, not the owned view.
        if (panel(id)) return;
        var record = objectViews[id]; if (!record) return;
        record.view.dispose(); delete objectViews[id];
      });
    } };
  }
  function element(tag, className, label) {
    var node = document.createElement(tag); node.className = className || "";
    if (label) node.textContent = label;
    return node;
  }
  // Codicons split-horizontal / split-vertical, inline so local WebView2 does not fetch CSS masks.
  var splitPaths = {
    "split-right": "M12.5 1H3.5C2.122 1 1 2.122 1 3.5V12.5C1 13.878 2.122 15 3.5 15H12.5C13.878 15 15 13.878 15 12.5V3.5C15 2.122 13.878 1 12.5 1ZM2 12.5V3.5C2 2.673 2.673 2 3.5 2H7.5V14H3.5C2.673 14 2 13.327 2 12.5ZM14 12.5C14 13.327 13.327 14 12.5 14H8.5V2H12.5C13.327 2 14 2.673 14 3.5V12.5Z",
    "split-below": "M0.999999 3.5L1 12.5C1 13.878 2.122 15 3.5 15L12.5 15C13.878 15 15 13.878 15 12.5L15 3.5C15 2.122 13.878 0.999999 12.5 0.999999L3.5 1C2.122 1 0.999999 2.122 0.999999 3.5ZM12.5 14L3.5 14C2.673 14 2 13.327 2 12.5L2 8.5L14 8.5L14 12.5C14 13.327 13.327 14 12.5 14ZM12.5 2C13.327 2 14 2.673 14 3.5L14 7.5L2 7.5L2 3.5C2 2.673 2.673 2 3.5 2L12.5 2Z"
  };
  function button(label, icon, action) {
    var node = element("button", "icon-btn"); node.type = "button";
    node.title = label; node.setAttribute("aria-label", label);
    if (splitPaths[icon]) node.innerHTML = '<svg class="dock-split-icon" viewBox="0 0 16 16" aria-hidden="true"><path d="' + splitPaths[icon] + '"/></svg>';
    else node.innerHTML = RNAssistantIcons.render(icon);
    node.addEventListener("click", action); return node;
  }
  function panel(id) { return api && api.getPanel(id); }
  function visible(id) {
    if (!nativeVisible || hostCompact && id !== compactChat && id !== "conversation") return false;
    if (hostCompact && !compactChat) return false;
    if (!workspaceActive()) return false;
    var item = panel(id);
    if (id === "conversation" && !item) return Object.keys(objectViews).some(function (key) {
      var record = objectViews[key], panelItem = panel(key);
      return panelItem && panelItem.params && panelItem.params.target && panelItem.params.target.view === "conversation" && panelItem.group.api.isVisible && panelItem.api.isVisible;
    });
    return !!item && item.group.api.isVisible && item.api.isVisible;
  }
  function sync() {
    if (!api || restoring) return;
    roots.inspector.inert = !visible("inspector");
    var nextSettings = visible("settings"), activateSettings = nextSettings && !settingsVisible;
    var deactivateSettings = !nextSettings && settingsVisible;
    settingsVisible = nextSettings; roots.settings.inert = !nextSettings;
    roots.diagnostics.inert = !visible("diagnostics");
    if (deactivateSettings) RNAssistantSettingsView.deactivate();
    if (activateSettings) RNAssistantSettingsView.activate();
    roots.request.inert = !visible("request");
    if (panel("request")) RNAssistantModelContext.onDockState(true, visible("request"));
    else Promise.resolve().then(function () {
      // Dockview briefly removes a panel while moving it; dispose only after that transaction.
      if (!restoring && !panel("request")) RNAssistantModelContext.onDockState(false, false);
    });
    RNAssistantChatInspector.onDockState(!!panel("inspector"), visible("inspector"));
    refreshPresentation();
    Object.keys(views).forEach(function (id) {
      var view = views[id], shown = visible(id);
      // Retained singleton editors still use their canonical DOM controls while
      // hidden (draft capture, late write ACK). Keep them connected in parking.
      if (!shown && view.renderer === "onlyWhenVisible" && roots[id] && roots[id].parentElement !== parking)
        parking.appendChild(roots[id]);
      if (view.onVisible && view.active !== shown) { view.active = shown; view.onVisible(shown); }
      if (shown && view.onLayout) view.onLayout();
    });
    syncObjectViews();
    updateContext();
  }

  function refreshPresentation() {
    if (!api) return;
    RNAssistantShellLayout.renderWorkspace({ layout: { visible: workspaceActive(), section: section, canMaximize: !!api.activePanel,
      maximized: api.hasMaximizedGroup(), panels: api.panels.length, activeView: api.activePanel && api.activePanel.id,
      activeTarget: api.activePanel && api.activePanel.params && api.activePanel.params.target,
      groups: api.groups.filter(function (group) { return group.api.isVisible; }).length } });
  }

  function notice(message) { RNAssistantShellLayout.notice(message); }
  function revealActiveTab() {
    cancelAnimationFrame(tabFrame);
    tabFrame = requestAnimationFrame(function () {
      if (!api.activePanel || !workspaceActive()) return;
      var tab = host.querySelector('[data-tab-panel-id="' + api.activePanel.id + '"]');
      if (tab) tab.scrollIntoView({ block: "nearest", inline: "nearest" });
    });
  }
  function persist() {
    if (preservePreference) return;
    if (hostCompact) return; // transient group maximize never replaces the durable Full layout
    try { localStorage.setItem(storageKey, JSON.stringify({ version: 2, layout: preferenceLayout(compact && wideLayout || api.toJSON()) })); }
    catch (error) { notice("Компоновка не сохранена: хранилище недоступно"); }
  }
  function changed() {
    sync();
    if (restoring) return;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(persist, 160);
  }
  function createComponent(options) {
    if (options.name === "chooser") return createChooser(options.id);
    if (views[options.name] && views[options.name].perObject) return createObjectComponent(options, views[options.name]);
    var id = options.name, content = element("div", "dock-view");
    if (!views[id] || views[id].targetRoute) throw new Error("Unknown workspace view: " + id);
    var visibility;
    return {
      element: content,
      init: function (parameters) {
        content.appendChild(roots[id]);
        visibility = parameters.api.onDidVisibilityChange(function () {
          if (parameters.api.isVisible && roots[id].parentElement !== content) content.appendChild(roots[id]);
          sync();
        });
      },
      dispose: function () {
        if (visibility) visibility.dispose();
        // A replacement component may already own the root during a move/restore.
        if (roots[id].parentElement === content) parking.appendChild(roots[id]);
        Promise.resolve().then(function () { if (!panel(id) && views[id].onClose) views[id].onClose(); });
      }
    };
  }
  function add(id, position) {
    return api.addPanel({ id: id, component: id, title: views[id].title,
      // Bounded singleton views retain their existing DOM and draft owners.
      renderer: views[id].renderer || "always", minimumWidth: 150, minimumHeight: 120, position: position });
  }
  function show(id, group, direction) {
    if (id !== "conversation" && window.RNAssistantWindow && RNAssistantWindow.inFull(function () { show(id, group, direction); })) return;
    if (!api || !views[id] || views[id].targetRoute) return false;
    if (id === "browser") return RNAssistantBrowser.open("/", null, { group: group, direction: direction });
    if (id === "conversation" && views[id].perObject) {
      var selected = RNAssistantConversationPanel.selected();
      return openTarget(selected ? selected.route : { view: "conversation", source: "office", sessionId: state.activeChatId || "" }, { group: group, direction: direction });
    }
    direction = direction || views[id].placement;
    if (views[id].prepare) views[id].prepare();
    var item = panel(id);
    // The gear opens in the main group; repeat clicks activate without relocating it.
    if ((id === "settings" || id === "request" || id === "diagnostics") && !item && !group) {
      group = panel("conversation") ? panel("conversation").group : api.activeGroup;
      direction = "within";
    }
    if (api.hasMaximizedGroup() && (!item || !item.api.isMaximized() || group && group !== item.group)) api.exitMaximizedGroup();
    if (compact && direction && direction !== "within") direction = "within";
    if (item && group) item.api.moveTo({ group: group, position: direction === "within" ? "center" : direction === "below" ? "bottom" : direction });
    if (!item) {
      var ref = group || api.activeGroup;
      var position = ref ? { referenceGroup: ref, direction: direction || (compact ? "within" : "right") } : undefined;
      item = add(id, position);
      if (!compact && id === "inspector" && !direction) item.api.setSize({ width: inspectorWidth });
    }
    item.group.api.setVisible(true); item.api.setActive(); changed(); return true;
  }
  function toolGroups() {
    return api ? api.groups.filter(function (group) { return group.panels.length && group.panels.every(function (item) { return item.id === "inspector"; }); }) : [];
  }
  function inspectorToggleLabel() {
    var groups = toolGroups();
    if (compact || !groups.length) return "Открыть инспектор";
    return groups.some(function (group) { return group.api.isVisible; }) ? "Свернуть инструменты" : "Развернуть инструменты";
  }
  function toggleTools() {
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(toggleTools)) return;
    if (!api) return;
    var groups = toolGroups();
    if (compact || !groups.length) { show("inspector"); return; }
    var hide = groups.some(function (group) { return group.api.isVisible; });
    if (hide && !api.groups.some(function (group) { return !groups.includes(group) && group.api.isVisible; })) {
      notice("Это единственная рабочая область. Откройте чат рядом."); return;
    }
    api.exitMaximizedGroup();
    groups.forEach(function (group) { group.api.setVisible(!hide); });
    var focusGroup = hide ? api.groups.find(function (group) { return group.api.isVisible; }) : groups[0];
    if (focusGroup) focusGroup.api.setActive();
    changed();
  }
  function canClose(items) {
    return items.every(function (item) {
      var record = objectViews[item.id], owner = record ? record.view : views[item.id];
      return !owner || !owner.canClose || owner.canClose();
    });
  }
  function close(id) {
    var item = panel(id); if (!item) return;
    if (!canClose([item])) return;
    wideLayout = null;
    if (!hostCompact) api.exitMaximizedGroup();
    item.api.close();
    if (hostCompact && compactChat === id) { compactChat = null; compactEmpty.hidden = false; host.classList.add("host-compact-no-chat"); }
    changed();
  }
  function createTab(options) {
    // Keep the pinned vendor tab renderer; route its close action through view guards.
    var tab = new window["dockview-core"].DefaultTab();
    function click(event) {
      if (!event.target.closest(".dv-default-tab-action")) return;
      event.preventDefault(); event.stopPropagation(); close(options.id);
    }
    tab.element.addEventListener("click", click, true);
    return { element: tab.element, init: function (params) { tab.init(params); },
      dispose: function () { tab.element.removeEventListener("click", click, true); tab.dispose(); } };
  }
  function createStartChooser(id) {
    var root = element("div", "dock-screen-chooser"), query = "", surface;
    if (id) root.dataset.chooserId = id;
    function model() {
      var ids = viewIds().filter(function (viewId) { return views[viewId].discovery === "primary" || query && views[viewId].discovery === "contextual"; })
        .filter(function (viewId) { return (views[viewId].discoveryTitle || views[viewId].title).toLocaleLowerCase().includes(query.toLocaleLowerCase()); })
        .sort(function (a, b) { return (views[a].order || 100) - (views[b].order || 100); }).slice(0, 12);
      return { title: "Что открыть?", placeholder: "Найти экран…", search: query, note: "Чаты доступны в дереве рабочей области.",
        sections: [{ title: query ? "Результаты" : "Основное", entries: ids.map(function (viewId) {
          var view = views[viewId]; return { key: viewId, title: view.discoveryTitle || view.title, icon: view.icon,
            description: viewId === "conversation" ? "Выбрать в дереве рабочей области" : panel(viewId) ? "Уже открыт" : "", payload: viewId };
        }) }] };
    }
    surface = RNAssistantStartSurface.mount(root, model(), {
      onSearch: function (value) { query = value.trim().slice(0, 256); surface.update(model()); },
      onSelect: function (viewId) {
        var chooser = id && panel(id), view = views[viewId];
        if (id && !chooser) return;
        var result = view.discover ? view.discover() : show(viewId, chooser && chooser.group, "within");
        if (result === false) throw new Error("Экран сейчас недоступен. Закройте лишнюю вкладку и повторите.");
        if (chooser && panel(id)) chooser.api.close(); changed();
      }
    });
    return { element: root, init: function () {}, focus: surface.focus, dispose: surface.dispose };
  }
  function createChooser(id) { return createStartChooser(id); }
  function openChooser(group, direction) {
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(function () { openChooser(group, direction); })) return;
    RNAssistantActionMenu.closeAll(); wideLayout = null; api.exitMaximizedGroup();
    while (panel("choose-" + nextChooser)) ++nextChooser;
    var item = api.addPanel({ id: "choose-" + nextChooser++, component: "chooser", title: "Выберите экран",
      minimumWidth: 150, minimumHeight: 120, position: { referenceGroup: group, direction: direction } });
    item.api.setActive(); requestAnimationFrame(function () { var input = host.querySelector('[data-chooser-id="' + item.id + '"] input'); if (input) input.focus(); }); changed();
  }
  function menu(trigger, items) {
    RNAssistantActionMenu.open(trigger, items.map(function (item) { return { label: item[0], icon: item[1], onSelect: item[2] }; }),
      { className: trigger.closest(".dock-group-actions") ? "dock-group-menu" : "" });
  }
  function updateContext() {
    if (!api) return;
    var selected = RNAssistantConversationPanel.selected();
    if (selected) owner = selected.target.source === "office" ? "office" : "project";
    Object.keys(objectViews).forEach(function (id) {
      var view = objectViews[id].view;
      if (view.target && view.target.source === "office") {
        var summary = (state.chats || []).find(function (chat) { return chatId(chat) === view.target.sessionId; });
        var item = panel(id); if (summary && item && item.title !== chatTitle(summary)) item.api.setTitle(chatTitle(summary));
      }
    });
    var names = { settings: "Настройки", diagnostics: "Диагностика", artifacts: "Материалы", instructions: "Библиотека", vba: "VBA", plugins: "Плагины" };
    var context = names[section];
    if (hostCompact) context = compactChat && panel(compactChat) ? panel(compactChat).title : "Чат не выбран";
    var activeOfficeChat = owner === "office" ? activeChatSummary() : null;
    var officeHost = activeOfficeChat && chatHost(activeOfficeChat) || state.host || "";
    if (!context) context = owner === "office" ? (activeOfficeChat ? chatDocumentTitle(activeOfficeChat) : "") : "Проект";
    RNAssistantShellLayout.renderWorkspace({ context: { title: context || "Рабочая область",
      icon: section === "chat" ? owner === "office" ? "host:" + officeHost : "folder" : "layers",
      iconHtml: section === "chat" && owner === "office" ? documentHostIcon(officeHost) : null,
      hostStatus: window.PrototypeFixtures ? "Mock · без подключения к Office" : state.bridgeUnavailable ? "Нет связи с хостом" : "Локальное пространство" } });
  }
  function header(group) {
    var root = element("div", "dock-group-actions"); root.dataset.groupId = group.id;
    root.appendChild(button("Выбрать экран", "plus", function () { openChooser(group, "within"); }));
    var actions = button("Действия с вкладкой", "more", function () {
      var canSplit = !compact && host.clientWidth >= 560;
      RNAssistantActionMenu.open(actions, [
        { label: "Открыть экран справа", icon: "split-right", disabled: !canSplit || group.api.width < 560, onSelect: function () { openChooser(group, "right"); } },
        { label: "Открыть экран снизу", icon: "split-below", disabled: !canSplit || group.api.height < 420, onSelect: function () { openChooser(group, "below"); } },
        { label: api.hasMaximizedGroup() ? "Восстановить группу" : "Развернуть группу", icon: api.hasMaximizedGroup() ? "compress" : "fit",
          onSelect: function () { if (api.hasMaximizedGroup()) api.exitMaximizedGroup(); else if (group.activePanel) group.activePanel.api.maximize(); changed(); } },
        { separator: true }
      ].concat([
        ["Собрать в одну группу", "layers", function () { join(); }],
        ["Закрыть вкладку", "close", function () { if (group.activePanel) close(group.activePanel.id); }],
        ["Закрыть группу", "close", function () {
          var items = group.panels.slice(); if (!canClose(items)) return;
          wideLayout = null; api.exitMaximizedGroup(); items.forEach(function (item) { item.api.close(); }); changed();
        }]
      ].map(function (item) { return { label: item[0], icon: item[1], onSelect: item[2] }; })), { className: "dock-group-menu" });
    }); actions.setAttribute("aria-haspopup", "menu"); root.appendChild(actions);
    return { element: root, init: function () {}, dispose: function () { RNAssistantActionMenu.closeWithin(root); } };
  }
  function validateLayout(layout) {
    if (!layout || !layout.grid || !layout.panels) return false;
    var counts = {};
    return Object.keys(layout.panels).every(function (id) {
      var spec = layout.panels[id], view = spec && Object.prototype.hasOwnProperty.call(views, spec.contentComponent) && views[spec.contentComponent];
      if (!spec) return false;
      if (/^choose-\d+$/.test(id) && spec.contentComponent === "chooser") return true;
      if (!view || view.targetRoute) return false;
      if (!view.perObject) return spec.contentComponent === id;
      counts[spec.contentComponent] = (counts[spec.contentComponent] || 0) + 1;
      return counts[spec.contentComponent] <= view.maximumInstances && spec.params && view.accepts(spec.params.target) && view.key(spec.params.target) === id;
    });
  }
  function normalizeLayout(input) {
    if (!input || !input.grid || !input.panels || JSON.stringify(input).length > 1024 * 1024 || Object.keys(input.panels).length > 128)
      throw new Error("Формат раскладки недоступен");
    var layout = preferenceLayout(input), counts = Object.create(null), seen = new Set(), groups = new Set(), nodes = 0, pruned = 0;
    Object.keys(layout.panels).forEach(function (id) {
      var spec = layout.panels[id], view = spec && Object.prototype.hasOwnProperty.call(views, spec.contentComponent) && views[spec.contentComponent];
      var single = {}; single[id] = spec;
      var valid = validateLayout({ grid: {}, panels: single });
      if (valid && view && view.perObject) { counts[spec.contentComponent] = (counts[spec.contentComponent] || 0) + 1; valid = counts[spec.contentComponent] <= view.maximumInstances; }
      if (!valid) { delete layout.panels[id]; pruned++; }
    });
    function visit(node, depth) {
      if (!node || ++nodes > 512 || depth > 32 || node.size != null && (!Number.isFinite(node.size) || node.size < 0)) throw new Error("Некорректная геометрия раскладки");
      if (node.type === "branch") {
        if (!Array.isArray(node.data)) throw new Error("Некорректная группа раскладки");
        node.data = node.data.map(function (child) { return visit(child, depth + 1); }).filter(Boolean);
        return node.data.length ? node : null;
      }
      if (node.type !== "leaf" || !node.data || !Array.isArray(node.data.views) || typeof node.data.id !== "string" || groups.has(node.data.id))
        throw new Error("Некорректная панель раскладки");
      var data = node.data;
      data.views = data.views.filter(function (id) { if (typeof id !== "string" || !layout.panels[id] || seen.has(id)) return false; seen.add(id); return true; });
      if (!data.views.length) return null;
      if (!data.views.includes(data.activeView)) data.activeView = data.views[0];
      groups.add(data.id); return node;
    }
    layout.grid.root = visit(layout.grid.root, 0);
    if (!layout.grid.root) throw new Error("В раскладке не осталось доступных панелей");
    Object.keys(layout.panels).forEach(function (id) { if (!seen.has(id)) { delete layout.panels[id]; pruned++; } });
    if (!groups.has(layout.activeGroup)) layout.activeGroup = groups.values().next().value;
    if (layout.maximizedGroup && !groups.has(layout.maximizedGroup)) delete layout.maximizedGroup;
    if (!validateLayout(layout)) throw new Error("Не удалось проверить преобразованную раскладку");
    return { layout: layout, pruned: pruned };
  }
  function preferenceLayout(input) {
    var layout = JSON.parse(JSON.stringify(input));
    Object.keys(layout.panels).forEach(function (id) {
      var spec = layout.panels[id], view = spec && views[spec.contentComponent];
      if (view && view.preferenceTarget && spec.params) spec.params = { target: view.preferenceTarget(spec.params.target) };
    });
    return layout;
  }
  function reset() {
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(reset)) return;
    if (!canClose(api.panels)) return;
    preservePreference = false;
    restoring = true; wideLayout = null; api.clear();
    var chat = api.addPanel({ id: "conversation", component: "conversation", title: "Чат", renderer: views.conversation.renderer,
      params: { target: { view: "conversation", source: "office", sessionId: "" } } });
    add("inspector", { referencePanel: chat, direction: compact || host.clientWidth < 560 ? "within" : "right" }).api.setSize({ width: inspectorWidth });
    chat.api.setActive(); restoring = false; changed();
  }
  function join() {
    if (window.RNAssistantWindow && RNAssistantWindow.inFull(join)) return;
    wideLayout = null; api.exitMaximizedGroup();
    var group = api.activeGroup || api.groups[0], active = api.activePanel;
    api.panels.slice().forEach(function (item) { if (item.group !== group) item.api.moveTo({ group: group, position: "center" }); });
    if (active) active.api.setActive(); changed();
  }
  function restoreLayout(layout) {
    // Placement is saved; rendering policy belongs to the current view descriptor.
    // Old `always` chat overlays use scaled rectangles as CSS sizes under zoom.
    Object.keys(layout.panels).forEach(function (id) {
      var spec = layout.panels[id], view = views[spec.contentComponent];
      if (view && view.renderer) spec.renderer = typeof view.renderer === "function"
        ? view.renderer(spec.params && spec.params.target) : view.renderer;
    });
    api.fromJSON(layout);
  }
  function mount(nextOwner) {
    owner = nextOwner;
    if (!api) return;
    placeHost();
    sync();
  }
  function resize(width) {
    if (hostCompact) return; // host presentation is independent of responsive wide/compact
    if (!width) return;
    var next = RNAssistantShellLayout.mode() === "single" || width < 560;
    if (next === compact) return;
    var focused = host.contains(document.activeElement) ? document.activeElement : null;
    compact = next; RNAssistantActionMenu.closeAll(); restoring = true;
    api.updateOptions({ disableDnd: compact });
    if (compact && api.groups.length > 1) {
      wideLayout = api.toJSON(); api.exitMaximizedGroup();
      var group = api.activeGroup || api.groups[0], active = api.activePanel;
      api.panels.slice().forEach(function (item) { if (item.group !== group) item.api.moveTo({ group: group, position: "center" }); });
      if (active) active.api.setActive();
    } else if (!compact && wideLayout) {
      var saved = wideLayout; wideLayout = null; restoreLayout(saved);
    }
    restoring = false; sync(); revealActiveTab();
    // Dockview can settle visibility after the group move. Restore at the next paint,
    // without taking focus back if the user already moved it elsewhere.
    requestAnimationFrame(function () {
      if (focused && focused.isConnected && !focused.closest("[inert]") && focused.getClientRects().length &&
          (document.activeElement === document.body || document.activeElement === focused)) focused.focus({ preventScroll: true });
    });
  }
  function bind() {
    if (api) return;
    var tokens = getComputedStyle(document.documentElement);
    inspectorWidth = Number.parseFloat(tokens.getPropertyValue("--shell-inspector-width"));
    parking = element("div"); parking.id = "workspaceViewParking"; parking.hidden = true;
    document.querySelector(".shell").appendChild(parking);
    document.getElementById("chatDropZone").remove();
    roots.inspector = document.getElementById("chatInspector"); roots.inspector.hidden = false;
    roots.settings = document.getElementById("settingsView");
    roots.request = document.getElementById("modelContextDialog"); roots.request.classList.remove("hidden");
    roots.diagnostics = document.getElementById("tab-diagnostics");
    Object.keys(views).forEach(function (id) {
      if (views[id].element) roots[id] = views[id].element();
    });
    Object.keys(roots).forEach(function (id) { parking.appendChild(roots[id]); });
    roots.settings.hidden = false;
    host = element("div", "chat-dock-host"); host.id = "chatDockHost";
    host.setAttribute("role", "region"); host.setAttribute("aria-label", "Рабочее пространство");
    host.setAttribute("aria-description", "Размер группы: Alt+Shift+стрелки при фокусе во вкладке.");
    document.getElementById("chatLayout").appendChild(host);
    var dock = element("div", "chat-dock"); host.appendChild(dock);
    compactEmpty = element("div", "host-compact-empty"); compactEmpty.hidden = true;
    compactEmpty.appendChild(element("p", "", "Выберите чат в полном окне"));
    var returnFull = button("Полное окно", "fit", function () { RNAssistantWindow.command("ShowFull").catch(notice); });
    returnFull.appendChild(element("span", "", "Полное окно")); compactEmpty.appendChild(returnFull); host.appendChild(compactEmpty);
    api = window["dockview-core"].createDockview(dock, {
      theme: { name: "rnassistant", className: "rna-product-dock", gap: Number.parseFloat(tokens.getPropertyValue("--shell-dock-gap")), dndOverlayMounting: "absolute", dndPanelOverlay: "content" },
      disableFloatingGroups: true, disableAutoResizing: true, dndStrategy: "pointer",
      messages: { closeTab: function (title) { return "Закрыть «" + title + "»"; }, closeTabPlain: "Закрыть вкладку" },
      createComponent: createComponent, createRightHeaderActionComponent: header,
      defaultTabComponent: "guarded", createTabComponent: createTab,
      createWatermarkComponent: function () { return createStartChooser(null); }
    });
    function endDrag() { document.documentElement.classList.remove("workspace-dragging"); }
    api.onWillDragPanel(function () { document.documentElement.classList.add("workspace-dragging"); });
    api.onWillDragGroup(function () { document.documentElement.classList.add("workspace-dragging"); });
    var dragListeners = new AbortController();
    document.addEventListener("pointerup", endDrag, { capture: true, signal: dragListeners.signal });
    document.addEventListener("pointercancel", endDrag, { capture: true, signal: dragListeners.signal });
    window.addEventListener("blur", endDrag, { signal: dragListeners.signal });
    restoring = true;
    try {
      var saved = JSON.parse(localStorage.getItem(storageKey));
      if (saved) {
        if (![1, 2].includes(saved.version)) throw new Error("Неизвестная версия раскладки");
        var normalized = normalizeLayout(saved.layout); restoreLayout(normalized.layout);
        localStorage.setItem(storageKey, JSON.stringify({ version: 2, layout: normalized.layout }));
        if (normalized.pruned) notice("Убраны недоступные вкладки: " + normalized.pruned + ". Остальная раскладка сохранена.");
      } else reset();
    } catch (error) { reset(); preservePreference = true; notice("Не удалось прочитать компоновку. Сохранённая раскладка не изменена."); }
    restoring = false;
    api.onDidLayoutChange(changed); api.onDidActivePanelChange(function () {
      var record = api.activePanel && objectViews[api.activePanel.id];
      if (record && record.view.focus) record.view.focus();
      sync(); revealActiveTab();
    }); api.onDidMaximizedGroupChange(sync);
    api.onDidRemovePanel(function () { if (!restoring) wideLayout = null; });
    RNAssistantShellLayout.bindWorkspace({
      onLayoutChanged: sync,
      chooser: function () { openChooser(api.activeGroup || api.groups[0], "within"); },
      layout: function (trigger) {
        menu(trigger, [["Чат и инспектор", "panel", reset], ["Одна группа вкладок", "layers", join],
          ["Сохранить раскладку", "check", function () { try { localStorage.setItem(savedKey, JSON.stringify(preferenceLayout(api.toJSON()))); notice("Раскладка сохранена"); } catch (error) { notice("Не удалось сохранить раскладку"); } }],
          ["Вернуть сохранённую", "refresh", function () { try { var layout = normalizeLayout(JSON.parse(localStorage.getItem(savedKey))).layout; if (validateLayout(layout)) {
            if (!canClose(api.panels.filter(function (item) { return !layout.panels[item.id]; }))) return;
            restoring = true; wideLayout = null; restoreLayout(layout); preservePreference = false; restoring = false; changed(); notice("Раскладка восстановлена");
          } else notice("Сохранённой раскладки нет или её формат недоступен"); } catch (error) { restoring = false; notice("Не удалось восстановить раскладку"); } }]]);
      }
    });
    host.addEventListener("keydown", function (event) {
      var tab = event.target.closest("[data-tab-panel-id]");
      if (!tab || event.target.closest("input, textarea, [contenteditable=true]") || !/^(Delete|Backspace)$/.test(event.key)) return;
      event.preventDefault(); event.stopPropagation(); close(tab.dataset.tabPanelId);
    }, true);
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && event.defaultPrevented) return;
      var tab = event.target.closest && event.target.closest("[data-tab-panel-id]");
      if (tab && host.contains(tab) && event.altKey && event.shiftKey && /^Arrow(Left|Right|Up|Down)$/.test(event.key) && !api.hasMaximizedGroup()) {
        var item = panel(tab.dataset.tabPanelId), group = item && item.group;
        if (group) {
          var horizontal = event.key === "ArrowLeft" || event.key === "ArrowRight";
          var delta = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -12 : 12;
          item.api.setSize(horizontal ? { width: group.api.width + delta } : { height: group.api.height + delta });
          event.preventDefault(); changed(); return;
        }
      }
      if (!hostCompact && event.key === "Escape" && api.hasMaximizedGroup()) { api.exitMaximizedGroup(); event.preventDefault(); }
    });
    // Dockview's default bounding rect uses scaled pixels under CSS zoom. Layout accepts CSS pixels.
    var observer = new ResizeObserver(function (entries) {
      var rect = entries[0].contentRect;
      if (rect.width && rect.height) { resize(rect.width); api.layout(rect.width, rect.height); sync(); }
    }); observer.observe(dock);
    window.addEventListener("pagehide", function (event) { clearTimeout(saveTimer); cancelAnimationFrame(tabFrame); persist(); if (!event.persisted) { dragListeners.abort(); endDrag(); observer.disconnect(); RNAssistantActionMenu.closeAll(); api.dispose(); } });
    sync();
  }
  function setHostWindow(presentation, shown) {
    nativeVisible = shown;
    // Companion retains the previous interactive placement while the whole native window is hidden.
    var nextCompact = presentation === "Compact" || presentation === "Companion" && hostCompact;
    if (!api) return;
    if (nextCompact !== hostCompact) {
      RNAssistantActionMenu.closeAll();
      if (nextCompact) {
        clearTimeout(saveTimer); persist();
        var selected = RNAssistantConversationPanel.selected(), active = api.activePanel;
        var selectedPanel = selected && panel(views.conversation.key(selected.route));
        var chat = active && active.params && active.params.target && active.params.target.view === "conversation" ? active : selectedPanel;
        if (chat && !(chat.params && chat.params.target && chat.params.target.sessionId)) chat = null;
        fullPresentation = { active: active && active.id, layout: compact && wideLayout || api.toJSON() };
        hostCompact = true; compactChat = chat && chat.id;
        api.updateOptions({ disableDnd: true });
        if (chat) { api.exitMaximizedGroup(); chat.api.setActive(); chat.api.maximize(); }
        compactEmpty.hidden = !!chat; host.classList.toggle("host-compact-no-chat", !chat);
      } else {
        hostCompact = false; compactChat = null; compactEmpty.hidden = true; host.classList.remove("host-compact-no-chat");
        restoring = true; api.exitMaximizedGroup();
        var previous = fullPresentation; fullPresentation = null;
        if (previous) {
          // Restore only live identities. A domain deletion during Compact must not resurrect a view.
          var live = new Set(api.panels.map(function (item) { return item.id; })), specs = api.toJSON().panels, groupIds = [];
          var saved = JSON.parse(JSON.stringify(previous.layout));
          Object.keys(saved.panels).forEach(function (id) { if (!live.has(id)) delete saved.panels[id]; else saved.panels[id] = specs[id]; });
          function prune(node) {
            if (node.type === "branch") { node.data = node.data.filter(prune); return node.data.length > 0; }
            node.data.views = node.data.views.filter(function (id) { return live.has(id); });
            if (!node.data.views.includes(node.data.activeView)) node.data.activeView = node.data.views[0];
            if (node.data.views.length) groupIds.push(node.data.id);
            return node.data.views.length > 0;
          }
          prune(saved.grid.root);
          if (!groupIds.includes(saved.activeGroup)) saved.activeGroup = groupIds[0];
        }
      }
      placeHost();
      if (!hostCompact) {
        // If native bounds arrive after this state push, responsive owns the interim narrow layout
        // and keeps the exact Full snapshot for its next wide restore.
        var width = host.clientWidth;
        var narrow = RNAssistantShellLayout.mode() === "single" || width < 560;
        if (previous && Object.keys(saved.panels).length) {
          api.layout(width, host.clientHeight); restoreLayout(saved);
          if (panel(previous.active)) panel(previous.active).api.setActive();
        }
        restoring = false;
        if (narrow && previous) {
          compact = false; resize(width); wideLayout = saved;
        } else { compact = false; wideLayout = null; }
        api.updateOptions({ disableDnd: compact });
      }
    }
    sync();
  }
  window.RNAssistantDockWorkspace = {
    recordNavigation: recordNavigation, recentNavigation: recentNavigation,
    clearRecentNavigation: function () { recents = []; writeRecents(); },
    recentNavigationNotice: function () { return recentMemoryOnly ? "Недавние страницы доступны только до закрытия окна." : ""; },
    setHostWindow: setHostWindow,
    canCloseAll: function () { return !api || canClose(api.panels); },
    registerConversation: function (descriptor) { views.conversation = descriptor; },
    getView: function (id) { return objectViews[id] && objectViews[id].view || null; },
    activeTarget: function () { return workspaceActive() && api && api.activePanel && api.activePanel.params && api.activePanel.params.target || null; },
    eachView: function (type, callback) { Object.keys(objectViews).forEach(function (id) {
      var item = panel(id); if (item && item.params && item.params.target.view === type) callback(objectViews[id].view);
    }); },
    registerTargetRoute: registerTargetRoute, registerView: registerView, openTarget: openTarget, updateTarget: updateTarget, sectionView: sectionView,
    isSectionVisible: function (name) { var id = sectionView(name); return !!id && visible(id); },
    bind: bind, mount: mount, show: show, close: close, updateContext: updateContext, refreshPresentation: refreshPresentation,
    isVisible: visible,
    setTitle: function (id, title) { var item = panel(id); if (item && item.title !== title) item.api.setTitle(title); },
    toggleTools: toggleTools, inspectorToggleLabel: inspectorToggleLabel,
    onTabChange: function (name) {
      section = name; if (!api) return;
      placeHost(); RNAssistantActionMenu.closeAll(); sync();
    }
  };
}());
