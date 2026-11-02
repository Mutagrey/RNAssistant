(function () {
  "use strict";
  // Placement only. Conversation state, commands and drafts retain their existing owners.
  var api, host, parking, roots = {}, owner = "office", section = "chat";
  var dirtyConversation = false, conversationVisible = true, saveTimer, restoring = false;
  var compact = false, wideLayout = null, settingsVisible = false;
  var conversationPosition = null, activation = 0, tabFrame;
  var inspectorWidth;
  var storageKey = "rnassistant.ui.dock.s1.v1", savedKey = storageKey + ".saved", nextChooser = 1;
  var views = {
    conversation: { title: "Чат", icon: "chat" },
    inspector: { title: "Инспектор", icon: "panel" },
    settings: { title: "Настройки", icon: "settings", canClose: function () { return RNAssistantSettingsView.canClose(); } }
  };
  function element(tag, className, label) {
    var node = document.createElement(tag); node.className = className || "";
    if (label) node.textContent = label;
    return node;
  }
  function button(label, icon, action) {
    var node = element("button", "icon-btn"); node.type = "button";
    node.title = label; node.setAttribute("aria-label", label);
    if (icon === "split-right" || icon === "split-below") node.innerHTML = '<span class="dock-split-icon ' + icon + '" aria-hidden="true"></span>';
    else node.innerHTML = RNAssistantIcons.render(icon);
    node.addEventListener("click", action); return node;
  }
  function panel(id) { return api && api.getPanel(id); }
  function visible(id) { var item = panel(id); return section === "chat" && !!item && item.group.api.isVisible && item.api.isVisible; }
  function conversationKey() {
    var current = owner === "office" ? null : RNAssistantWorkspaceChat.snapshot();
    return owner + ":" + (owner === "office" ? state.activeChatId : current && current.project && current.project.workspaceId) + ":" + (current && current.sessionId || "");
  }
  function sync() {
    if (!api || restoring) return;
    var nextVisible = visible("conversation");
    var activate = nextVisible && !conversationVisible;
    if (conversationVisible && !nextVisible) {
      conversationPosition = { key: conversationKey(), top: chatScrollPosition, following: chatScrollFollowing };
      ++activation;
    }
    conversationVisible = nextVisible;
    roots.conversation.inert = !nextVisible;
    roots.inspector.inert = !visible("inspector");
    var nextSettings = visible("settings"), activateSettings = nextSettings && !settingsVisible;
    var deactivateSettings = !nextSettings && settingsVisible;
    settingsVisible = nextSettings; roots.settings.inert = !nextSettings;
    if (deactivateSettings) RNAssistantSettingsView.deactivate();
    if (activateSettings) RNAssistantSettingsView.activate();
    RNAssistantChatInspector.onDockState(!!panel("inspector"), visible("inspector"));
    RNAssistantShellLayout.renderWorkspace({ layout: { visible: section === "chat", canMaximize: !!api.activePanel,
      maximized: api.hasMaximizedGroup(), panels: api.panels.length, activeView: api.activePanel && api.activePanel.id,
      groups: api.groups.filter(function (group) { return group.api.isVisible; }).length } });
    updateContext();
    if (activate && conversationPosition && conversationPosition.key === conversationKey()) {
      var position = conversationPosition, version = ++activation;
      requestAnimationFrame(function () {
        if (version !== activation || !conversationVisible || position.key !== conversationKey()) return;
        var list = document.getElementById("messages");
        chatScrollFollowing = position.following;
        if (position.following) scrollMessagesToBottom(false, list);
        else { list.scrollTop = position.top; chatScrollPosition = list.scrollTop; updateChatScrollButton(list); }
      });
    }
    if (activate && dirtyConversation) {
      dirtyConversation = false;
      if (owner === "office") { renderMessages(); renderSendControls(); }
      else RNAssistantWorkspaceChat.refreshPresentation();
    }
  }
  function notice(message) { RNAssistantShellLayout.notice(message); }
  function revealActiveTab() {
    cancelAnimationFrame(tabFrame);
    tabFrame = requestAnimationFrame(function () {
      if (!api.activePanel || section !== "chat") return;
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
    var id = options.name, content = element("div", "dock-view");
    if (!views[id]) throw new Error("Unknown workspace view: " + id);
    var visibility;
    return {
      element: content,
      init: function (parameters) {
        content.appendChild(roots[id]);
        visibility = parameters.api.onDidVisibilityChange(function () { sync(); });
      },
      dispose: function () {
        if (visibility) visibility.dispose();
        parking.appendChild(roots[id]);
      }
    };
  }
  function add(id, position) {
    return api.addPanel({ id: id, component: id, title: views[id].title,
      // Bounded singleton views retain their existing DOM and draft owners.
      renderer: "always", minimumWidth: 150, minimumHeight: 120, position: position });
  }
  function show(id, group, direction) {
    if (!api || !views[id]) return;
    if (id === "inspector") RNAssistantChatInspector.prepareOpen();
    var item = panel(id);
    // The gear opens in the main group; repeat clicks activate without relocating it.
    if (id === "settings" && !item && !group) {
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
    body.appendChild(element("p", "", "Открытый экран будет перенесён сюда. Чат общий для выбранного документа или проекта."));
    root.appendChild(body); root.dataset.chooserId = id;
    Object.keys(views).forEach(function (viewId) {
      var row = button(views[viewId].title, views[viewId].icon, function () {
        var chooserPanel = panel(id); if (!chooserPanel) return;
        show(viewId, chooserPanel.group, "within"); chooserPanel.api.close(); changed();
      });
      row.appendChild(element("span", "", views[viewId].title)); list.appendChild(row);
      if (panel(viewId)) row.appendChild(element("small", "", "Открыт"));
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
    var title = roots.conversation.querySelector(owner === "office" ? "#activeChatTitle" : "#projectChatTitle").textContent;
    var chat = panel("conversation"); if (chat && chat.title !== title) chat.api.setTitle(title || "Чат");
    var names = { settings: "Настройки", diagnostics: "Диагностика", artifacts: "Материалы", instructions: "Библиотека", vba: "VBA", plugins: "Плагины" };
    var context = names[section];
    if (!context) context = owner === "office" ? (activeChatSummary() ? chatDocumentTitle(activeChatSummary()) : "") : roots.conversation.querySelector("#projectChatContext").textContent;
    RNAssistantShellLayout.renderWorkspace({ context: { title: context || "Рабочая область",
      icon: section === "chat" ? owner === "office" ? "table" : "folder" : "layers",
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
    return Object.keys(layout.panels).every(function (id) { return views[id] && layout.panels[id].contentComponent === id || /^choose-\d+$/.test(id) && layout.panels[id].contentComponent === "chooser"; });
  }
  function reset() {
    if (!canClose(api.panels)) return;
    restoring = true; wideLayout = null; api.clear();
    var chat = add("conversation"); add("inspector", { referencePanel: chat, direction: compact || host.clientWidth < 560 ? "within" : "right" }).api.setSize({ width: inspectorWidth });
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
    var target = document.getElementById(owner === "office" ? "chatLayout" : "projectChatLayout");
    if (host.parentElement !== target) target.appendChild(host);
    document.getElementById("chatDropZone").hidden = owner !== "office";
    document.getElementById("workspaceChatView").hidden = owner === "office";
    // Background mount/preview refresh must not reopen a hidden conversation.
    if (changedOwner) show("conversation");
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
    roots.conversation = element("div", "dock-conversation"); roots.conversation.id = "conversationView";
    roots.conversation.append(document.getElementById("chatDropZone"), document.getElementById("workspaceChatView"));
    roots.inspector = document.getElementById("chatInspector"); roots.inspector.hidden = false;
    roots.settings = document.getElementById("settingsView");
    parking.append(roots.conversation, roots.inspector, roots.settings);
    roots.settings.hidden = false;
    host = element("div", "chat-dock-host"); host.id = "chatDockHost";
    host.setAttribute("role", "region"); host.setAttribute("aria-label", "Рабочее пространство");
    host.setAttribute("aria-description", "Размер группы: Alt+Shift+стрелки при фокусе во вкладке.");
    document.getElementById("chatLayout").appendChild(host);
    var dock = element("div", "chat-dock"); host.appendChild(dock);
    api = window["dockview-core"].createDockview(dock, {
      theme: { name: "rnassistant", className: "rna-product-dock", gap: Number.parseFloat(tokens.getPropertyValue("--shell-dock-gap")), dndOverlayMounting: "absolute", dndPanelOverlay: "content" },
      disableFloatingGroups: true, dndStrategy: "pointer",
      messages: { closeTab: function (title) { return "Закрыть «" + title + "»"; }, closeTabPlain: "Закрыть вкладку" },
      createComponent: createComponent, createRightHeaderActionComponent: header,
      defaultTabComponent: "guarded", createTabComponent: createTab,
      createWatermarkComponent: function () {
        var empty = element("div", "dock-empty"); empty.appendChild(element("p", "", "Выберите экран"));
        Object.keys(views).forEach(function (id) { var b = button(views[id].title, views[id].icon, function () { show(id); }); b.appendChild(element("span", "", views[id].title)); empty.appendChild(b); });
        return { element: empty, init: function () {} };
      }
    });
    restoring = true;
    try { var saved = JSON.parse(localStorage.getItem(storageKey)); if (saved && saved.version === 1 && validateLayout(saved.layout)) api.fromJSON(saved.layout); else { reset(); if (saved) notice("Прежняя компоновка недоступна. Открыт исходный вид."); } }
    catch (error) { reset(); notice("Не удалось прочитать компоновку. Открыт исходный вид."); }
    restoring = false;
    document.getElementById("workspaceChatView").hidden = true;
    api.onDidLayoutChange(changed); api.onDidActivePanelChange(function () { sync(); revealActiveTab(); }); api.onDidMaximizedGroupChange(sync);
    api.onDidRemovePanel(function () { if (!restoring) wideLayout = null; });
    RNAssistantShellLayout.bindWorkspace({
      panels: function (trigger) {
        RNAssistantActionMenu.open(trigger, Object.keys(views).map(function (id) {
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
    var observer = new ResizeObserver(function (entries) { resize(entries[0].contentRect.width); }); observer.observe(host);
    window.addEventListener("pagehide", function (event) { clearTimeout(saveTimer); cancelAnimationFrame(tabFrame); persist(); if (!event.persisted) { ++activation; observer.disconnect(); RNAssistantActionMenu.closeAll(); api.dispose(); } });
    sync();
  }
  window.RNAssistantDockWorkspace = {
    bind: bind, mount: mount, show: show, close: close, updateContext: updateContext,
    toggleTools: toggleTools, inspectorToggleLabel: inspectorToggleLabel,
    canRenderConversation: function () { if (!api || conversationVisible) return true; dirtyConversation = true; return false; },
    onTabChange: function (name) {
      section = name; if (!api) return;
      RNAssistantActionMenu.closeAll(); sync();
    }
  };
}());
