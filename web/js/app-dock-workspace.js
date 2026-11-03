(function () {
  "use strict";
  // Placement only. Conversation state, commands and drafts retain their existing owners.
  var api, host, parking, roots = {}, owner = "office", section = "chat";
  var saveTimer, restoring = false;
  var compact = false, wideLayout = null, settingsVisible = false;
  var tabFrame;
  var objectViews = Object.create(null);
  var inspectorWidth;
  var storageKey = "rnassistant.ui.dock.s1.v1", savedKey = storageKey + ".saved", nextChooser = 1;
  var views = {
    conversation: { title: "Чат", icon: "chat", section: "chat" },
    inspector: { title: "Инспектор", icon: "panel" },
    request: { title: "Запрос модели", icon: "code" },
    diagnostics: { title: "Диагностика", icon: "chart", onVisible: function (shown) { RNAssistantDiagnostics.onVisible(shown); },
      onClose: function () { RNAssistantDiagnostics.onClose(); } },
    settings: { title: "Настройки", icon: "settings", canClose: function () { return RNAssistantSettingsView.canClose(); } }
  };
  function viewIds() { return Object.keys(views).filter(function (id) { return !views[id].targetRoute && (!views[id].perObject || views[id].menu); }); }
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
    var descriptor = target && views[target.view];
    if (!descriptor || !descriptor.accepts || !descriptor.accepts(target)) return false;
    target = Object.freeze(Object.assign({}, target));
    if (descriptor.targetRoute) return descriptor.open(target);
    if (!api || !descriptor.perObject) return false;
    var chooser = api.activePanel && /^choose-\d+$/.test(api.activePanel.id) ? api.activePanel : null;
    var id = descriptor.key(target), item = panel(id);
    if (item && options && options.group && item.group !== options.group) item.api.moveTo({ group: options.group, position: "center" });
    if (!item) {
      var count = api.panels.filter(function (p) { return p.params && p.params.target && p.params.target.view === target.view; }).length;
      if (count >= descriptor.maximumInstances) { notice("Открыто " + count + " просмотров. Закройте один перед открытием нового."); return false; }
      var reference = options && panel(options.referencePanel);
      item = api.addPanel({ id: id, component: target.view, title: descriptor.titleFor(target),
        params: { target: target }, renderer: typeof descriptor.renderer === "function" ? descriptor.renderer(target) : descriptor.renderer || "always", minimumWidth: 150, minimumHeight: 120,
        position: options && options.group ? { referenceGroup: options.group, direction: options.direction || "within" } :
          (reference || api.activePanel) ? { referencePanel: reference || api.activePanel, direction: options && options.direction || "within" } : undefined });
    }
    item.group.api.setVisible(true); item.api.setActive(); changed();
    var record = objectViews[id]; if (record && record.view.focus) record.view.focus();
    if (target.view === "conversation" && target.sessionId && panel("conversation")) panel("conversation").api.close();
    if (chooser && panel(chooser.id)) chooser.api.close();
    return true;
  }
  function sectionView(name) {
    return Object.keys(views).find(function (id) { return views[id].section === name; }) || null;
  }
  function workspaceActive() { return section === "chat" || !!sectionView(section); }
  function placeHost() {
    if (!host) return;
    var source = views[sectionView(section)], target = source && source.layout && source.layout();
    target = target || document.getElementById(owner === "office" ? "chatLayout" : "projectChatLayout");
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
    RNAssistantShellLayout.renderWorkspace({ layout: { visible: workspaceActive(), section: section, canMaximize: !!api.activePanel,
      maximized: api.hasMaximizedGroup(), panels: api.panels.length, activeView: api.activePanel && api.activePanel.id,
      groups: api.groups.filter(function (group) { return group.api.isVisible; }).length } });
    Object.keys(views).forEach(function (id) {
      var view = views[id], shown = visible(id);
      if (view.onVisible && view.active !== shown) { view.active = shown; view.onVisible(shown); }
      if (shown && view.onLayout) view.onLayout();
    });
    syncObjectViews();
    updateContext();
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
    try { localStorage.setItem(storageKey, JSON.stringify({ version: 1, layout: compact && wideLayout || api.toJSON() })); }
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
        visibility = parameters.api.onDidVisibilityChange(function () { sync(); });
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
    if (!api || !views[id] || views[id].targetRoute) return;
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
    item.group.api.setVisible(true); item.api.setActive(); changed();
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
    return items.every(function (item) { return !views[item.id] || !views[item.id].canClose || views[item.id].canClose(); });
  }
  function close(id) {
    var item = panel(id); if (!item) return;
    if (!canClose([item])) return;
    wideLayout = null;
    api.exitMaximizedGroup(); item.api.close(); changed();
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
  function createChooser(id) {
    var root = element("div", "dock-screen-chooser"), body = element("div", "dock-screen-chooser-body");
    var input = element("input"), list = element("div", "dock-screen-list");
    var empty = element("p", "", "Ничего не найдено"); empty.hidden = true; empty.setAttribute("role", "status");
    input.type = "search"; input.placeholder = "Найти экран…"; input.setAttribute("aria-label", "Найти экран");
    body.append(element("h2", "", "Что открыть?"), input, list, empty);
    body.appendChild(element("p", "", "Выберите экран или откройте другой чат из дерева навигации."));
    root.appendChild(body); root.dataset.chooserId = id;
    viewIds().forEach(function (viewId) {
      var row = button(views[viewId].title, views[viewId].icon, function () {
        var chooserPanel = panel(id); if (!chooserPanel) return;
        show(viewId, chooserPanel.group, "within"); if (panel(id)) chooserPanel.api.close(); changed();
      });
      row.appendChild(element("span", "", views[viewId].title)); list.appendChild(row);
      if (panel(viewId)) row.appendChild(element("small", "", "Открыт"));
    });
    var chatTargets = (state.chats || []).map(function (chat) { return { title: chatTitle(chat), target: { view: "conversation", source: "office", sessionId: chatId(chat) } }; });
    if (window.RNAssistantProjectChats) chatTargets = chatTargets.concat(RNAssistantProjectChats.targets());
    chatTargets.forEach(function (entry) {
      var row = button(entry.title, "chat", function () {
        var chooserPanel = panel(id); if (chooserPanel) openTarget(entry.target, { group: chooserPanel.group });
      });
      row.appendChild(element("span", "", entry.title)); list.appendChild(row);
    });
    input.addEventListener("input", function () {
      Array.from(list.children).forEach(function (row) { row.hidden = !row.getAttribute("aria-label").toLocaleLowerCase().includes(input.value.trim().toLocaleLowerCase()); });
      empty.hidden = !!list.querySelector("button:not([hidden])");
    });
    input.addEventListener("keydown", function (event) { if (event.key === "Enter") { event.preventDefault(); var first = list.querySelector("button:not([hidden])"); if (first) first.click(); } });
    return { element: root, init: function () {}, focus: function () { input.focus({ preventScroll: true }); } };
  }
  function openChooser(group, direction) {
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
    var activeOfficeChat = owner === "office" ? activeChatSummary() : null;
    var officeHost = activeOfficeChat && chatHost(activeOfficeChat) || state.host || "";
    if (!context) context = owner === "office" ? (activeOfficeChat ? chatDocumentTitle(activeOfficeChat) : "") : "Проект";
    RNAssistantShellLayout.renderWorkspace({ context: { title: context || "Рабочая область",
      icon: section === "chat" ? owner === "office" ? "host:" + officeHost : "folder" : "layers",
      iconHtml: section === "chat" && owner === "office" ? documentHostIcon(officeHost) : null,
      hostStatus: window.PrototypeFixtures ? "Mock · без подключения к Office" : state.bridgeUnavailable ? "Нет связи с хостом" : "Локальное пространство" } });
  }
  function header(group) {
    var root = element("div", "dock-group-actions"), splitButtons = []; root.dataset.groupId = group.id;
    function updateButtons() { splitButtons.forEach(function (entry) {
      entry.button.disabled = compact || host.clientWidth < 560 || (entry.direction === "right" ? group.api.width < 560 : group.api.height < 420);
      entry.button.title = entry.button.disabled ? "Недостаточно места для ещё одной области" : entry.button.getAttribute("aria-label");
    }); }
    [["Выбрать экран", "plus", "within"], ["Открыть экран справа", "split-right", "right"], ["Открыть экран снизу", "split-below", "below"]].forEach(function (item) {
      var control = button(item[0], item[1], function () { openChooser(group, item[2]); }); root.appendChild(control);
      if (item[2] !== "within") splitButtons.push({ button: control, direction: item[2] });
    });
    var actions = button("Действия с вкладкой", "more", function () {
      menu(actions, [
        ["Собрать в одну группу", "layers", function () { join(); }],
        ["Закрыть вкладку", "close", function () { if (group.activePanel) close(group.activePanel.id); }],
        ["Закрыть группу", "close", function () {
          var items = group.panels.slice(); if (!canClose(items)) return;
          wideLayout = null; api.exitMaximizedGroup(); items.forEach(function (item) { item.api.close(); }); changed();
        }]
      ]);
    }); actions.setAttribute("aria-haspopup", "menu"); root.appendChild(actions);
    var dimensions = group.api.onDidDimensionsChange(updateButtons); updateButtons();
    return { element: root, init: function () {}, dispose: function () { dimensions.dispose(); RNAssistantActionMenu.closeWithin(root); } };
  }
  function validateLayout(layout) {
    if (!layout || !layout.grid || !layout.panels) return false;
    var counts = {};
    return Object.keys(layout.panels).every(function (id) {
      var spec = layout.panels[id], view = views[spec.contentComponent];
      if (/^choose-\d+$/.test(id) && spec.contentComponent === "chooser") return true;
      if (!view || view.targetRoute) return false;
      if (!view.perObject) return spec.contentComponent === id;
      counts[spec.contentComponent] = (counts[spec.contentComponent] || 0) + 1;
      return counts[spec.contentComponent] <= view.maximumInstances && spec.params && view.accepts(spec.params.target) && view.key(spec.params.target) === id;
    });
  }
  function reset() {
    if (!canClose(api.panels)) return;
    restoring = true; wideLayout = null; api.clear();
    var chat = api.addPanel({ id: "conversation", component: "conversation", title: "Чат", renderer: "always",
      params: { target: { view: "conversation", source: "office", sessionId: "" } } });
    add("inspector", { referencePanel: chat, direction: compact || host.clientWidth < 560 ? "within" : "right" }).api.setSize({ width: inspectorWidth });
    chat.api.setActive(); restoring = false; changed();
  }
  function join() {
    wideLayout = null; api.exitMaximizedGroup();
    var group = api.activeGroup || api.groups[0], active = api.activePanel;
    api.panels.slice().forEach(function (item) { if (item.group !== group) item.api.moveTo({ group: group, position: "center" }); });
    if (active) active.api.setActive(); changed();
  }
  function mount(nextOwner) {
    var changedOwner = owner !== nextOwner;
    owner = nextOwner;
    if (!api) return;
    placeHost();
    sync();
  }
  function resize(width) {
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
      var saved = wideLayout; wideLayout = null; api.fromJSON(saved);
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
    document.getElementById("workspaceChatView").remove();
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
    api = window["dockview-core"].createDockview(dock, {
      theme: { name: "rnassistant", className: "rna-product-dock", gap: Number.parseFloat(tokens.getPropertyValue("--shell-dock-gap")), dndOverlayMounting: "absolute", dndPanelOverlay: "content" },
      disableFloatingGroups: true, disableAutoResizing: true, dndStrategy: "pointer",
      messages: { closeTab: function (title) { return "Закрыть «" + title + "»"; }, closeTabPlain: "Закрыть вкладку" },
      createComponent: createComponent, createRightHeaderActionComponent: header,
      defaultTabComponent: "guarded", createTabComponent: createTab,
      createWatermarkComponent: function () {
        var empty = element("div", "dock-empty"); empty.appendChild(element("p", "", "Выберите экран"));
        viewIds().forEach(function (id) { var b = button(views[id].title, views[id].icon, function () { show(id); }); b.appendChild(element("span", "", views[id].title)); empty.appendChild(b); });
        return { element: empty, init: function () {} };
      }
    });
    function endDrag() { document.documentElement.classList.remove("workspace-dragging"); }
    api.onWillDragPanel(function () { document.documentElement.classList.add("workspace-dragging"); });
    api.onWillDragGroup(function () { document.documentElement.classList.add("workspace-dragging"); });
    var dragListeners = new AbortController();
    document.addEventListener("pointerup", endDrag, { capture: true, signal: dragListeners.signal });
    document.addEventListener("pointercancel", endDrag, { capture: true, signal: dragListeners.signal });
    window.addEventListener("blur", endDrag, { signal: dragListeners.signal });
    restoring = true;
    try { var saved = JSON.parse(localStorage.getItem(storageKey)); if (saved && saved.version === 1 && validateLayout(saved.layout)) api.fromJSON(saved.layout); else { reset(); if (saved) notice("Прежняя компоновка недоступна. Открыт исходный вид."); } }
    catch (error) { reset(); notice("Не удалось прочитать компоновку. Открыт исходный вид."); }
    restoring = false;
    api.onDidLayoutChange(changed); api.onDidActivePanelChange(function () {
      var record = api.activePanel && objectViews[api.activePanel.id];
      if (record && record.view.focus) record.view.focus();
      sync(); revealActiveTab();
    }); api.onDidMaximizedGroupChange(sync);
    api.onDidRemovePanel(function () { if (!restoring) wideLayout = null; });
    RNAssistantShellLayout.bindWorkspace({
      onLayoutChanged: sync,
      panels: function (trigger) {
        RNAssistantActionMenu.open(trigger, viewIds().map(function (id) {
          return { label: views[id].title, icon: views[id].icon, status: panel(id) ? "Открыт" : "", onSelect: function () { show(id); } };
        }));
      },
      maximize: function () { if (api.hasMaximizedGroup()) api.exitMaximizedGroup(); else if (api.activePanel) api.activePanel.api.maximize(); changed(); },
      layout: function (trigger) {
        menu(trigger, [["Чат и инспектор", "panel", reset], ["Одна группа вкладок", "layers", join],
          ["Сохранить раскладку", "check", function () { try { localStorage.setItem(savedKey, JSON.stringify(api.toJSON())); notice("Раскладка сохранена"); } catch (error) { notice("Не удалось сохранить раскладку"); } }],
          ["Вернуть сохранённую", "refresh", function () { try { var layout = JSON.parse(localStorage.getItem(savedKey)); if (validateLayout(layout)) {
            if (!canClose(api.panels.filter(function (item) { return !layout.panels[item.id]; }))) return;
            restoring = true; wideLayout = null; api.fromJSON(layout); restoring = false; changed(); notice("Раскладка восстановлена");
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
      if (event.key === "Escape" && api.hasMaximizedGroup()) { api.exitMaximizedGroup(); event.preventDefault(); }
    });
    // Dockview's default bounding rect uses scaled pixels under CSS zoom. Layout accepts CSS pixels.
    var observer = new ResizeObserver(function (entries) {
      var rect = entries[0].contentRect;
      if (rect.width && rect.height) { resize(rect.width); api.layout(rect.width, rect.height); sync(); }
    }); observer.observe(dock);
    window.addEventListener("pagehide", function (event) { clearTimeout(saveTimer); cancelAnimationFrame(tabFrame); persist(); if (!event.persisted) { dragListeners.abort(); endDrag(); observer.disconnect(); RNAssistantActionMenu.closeAll(); api.dispose(); } });
    sync();
  }
  window.RNAssistantDockWorkspace = {
    registerConversation: function (descriptor) { views.conversation = descriptor; },
    getView: function (id) { return objectViews[id] && objectViews[id].view || null; },
    eachView: function (type, callback) { Object.keys(objectViews).forEach(function (id) {
      var item = panel(id); if (item && item.params && item.params.target.view === type) callback(objectViews[id].view);
    }); },
    registerTargetRoute: registerTargetRoute, registerView: registerView, openTarget: openTarget, sectionView: sectionView,
    isSectionVisible: function (name) { var id = sectionView(name); return !!id && visible(id); },
    bind: bind, mount: mount, show: show, close: close, updateContext: updateContext,
    isVisible: visible,
    setTitle: function (id, title) { var item = panel(id); if (item && item.title !== title) item.api.setTitle(title); },
    toggleTools: toggleTools, inspectorToggleLabel: inspectorToggleLabel,
    onTabChange: function (name) {
      section = name; if (!api) return;
      placeHost(); RNAssistantActionMenu.closeAll(); sync();
    }
  };
}());
