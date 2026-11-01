(function () {
  "use strict";
  // Placement only. Conversation state, commands and drafts retain their existing owners.
  var api, host, parking, roots = {}, owner = "office", section = "chat";
  var dirtyConversation = false, conversationVisible = true, saveTimer, restoring = false;
  var compact = false, wideLayout = null, chooser, returnFocus;
  var conversationPosition = null, activation = 0, noticeTimer;
  var storageKey = "rnassistant.ui.dock.s1.v1", savedKey = storageKey + ".saved", nextChooser = 1;
  var views = {
    conversation: { title: "Чат", icon: "chat" },
    inspector: { title: "Инспектор", icon: "panel" }
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
  function setText(id, value) { var node = document.getElementById(id); if (node.textContent !== value) node.textContent = value; }
  function setIcon(node, name) { if (node.dataset.icon !== name) { node.dataset.icon = name; node.innerHTML = RNAssistantIcons.render(name); } }
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
    RNAssistantChatInspector.onDockState(!!panel("inspector"), visible("inspector"));
    var max = document.getElementById("workspaceMaximize");
    max.disabled = !api.activePanel; max.setAttribute("aria-pressed", String(api.hasMaximizedGroup()));
    max.title = api.hasMaximizedGroup() ? "Восстановить компоновку" : "Развернуть группу";
    max.setAttribute("aria-label", max.title); setIcon(max, api.hasMaximizedGroup() ? "compress" : "fit");
    updateContext();
    setText("workspaceLayoutStatus", "Вкладки: " + api.panels.length + " · группы: " + api.groups.filter(function (group) { return group.api.isVisible; }).length);
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
  function notice(message) {
    document.getElementById("workspaceLayoutNotice").textContent = message; clearTimeout(noticeTimer);
    noticeTimer = setTimeout(function () { document.getElementById("workspaceLayoutNotice").textContent = ""; }, 5000);
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
      // Only these two bounded, existing views retain DOM. Hidden work is explicitly gated.
      renderer: "always", minimumWidth: 150, minimumHeight: 120, position: position });
  }
  function show(id, group, direction) {
    if (!api || !views[id]) return;
    if (id === "inspector") RNAssistantChatInspector.prepareOpen();
    var item = panel(id);
    if (api.hasMaximizedGroup() && (!item || !item.api.isMaximized() || group && group !== item.group)) api.exitMaximizedGroup();
    if (compact && direction && direction !== "within") direction = "within";
    if (item && group) item.api.moveTo({ group: group, position: direction === "within" ? "center" : direction === "below" ? "bottom" : direction });
    if (!item) {
      var ref = group || api.activeGroup;
      var position = ref ? { referenceGroup: ref, direction: direction || (compact ? "within" : "right") } : undefined;
      item = add(id, position);
      if (!compact && id === "inspector" && !direction) item.api.setSize({ width: 292 });
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
  function close(id) {
    var item = panel(id); if (!item) return;
    wideLayout = null;
    api.exitMaximizedGroup(); item.api.close(); changed();
  }
  function closeChooser(focus) {
    if (!chooser) return;
    chooser.remove(); chooser = null;
    if (focus && returnFocus && returnFocus.isConnected) returnFocus.focus({ preventScroll: true });
  }
  function choose(group, direction, trigger) {
    closeChooser(); returnFocus = trigger;
    chooser = element("div", "dock-chooser"); chooser.setAttribute("role", "dialog");
    chooser.setAttribute("aria-label", "Выбор экрана");
    chooser.appendChild(element("strong", "", direction === "right" ? "Экран справа" : direction === "below" ? "Экран снизу" : "Выбор экрана"));
    Object.keys(views).forEach(function (id) {
      var row = button(views[id].title, views[id].icon, function () { closeChooser(); wideLayout = null; show(id, group, direction); });
      row.appendChild(element("span", "", views[id].title));
      if (panel(id)) row.appendChild(element("small", "", "Уже открыт · переместить"));
      chooser.appendChild(row);
    });
    chooser.appendChild(element("p", "", "Один общий чат для выбранного документа или проекта."));
    document.body.appendChild(chooser);
    var rect = trigger.getBoundingClientRect();
    chooser.style.left = Math.max(8, Math.min(rect.right - 300, window.innerWidth - 308)) + "px";
    chooser.style.top = Math.max(8, Math.min(rect.bottom + 6, window.innerHeight - chooser.offsetHeight - 8)) + "px";
    chooser.querySelector("button").focus();
  }
  function createChooser(id) {
    var root = element("div", "dock-screen-chooser"), body = element("div", "dock-screen-chooser-body");
    var input = element("input"), list = element("div", "dock-screen-list");
    input.type = "search"; input.placeholder = "Найти экран…"; input.setAttribute("aria-label", "Найти экран");
    body.append(element("h2", "", "Что открыть?"), input, list);
    body.appendChild(element("p", "", "Открытый экран будет перенесён сюда. Чат общий для выбранного документа или проекта."));
    root.appendChild(body); root.dataset.chooserId = id;
    Object.keys(views).forEach(function (viewId) {
      var row = button(views[viewId].title, views[viewId].icon, function () {
        var chooserPanel = panel(id); if (!chooserPanel) return;
        show(viewId, chooserPanel.group, "within"); chooserPanel.api.close(); changed();
      });
      row.appendChild(element("span", "", views[viewId].title)); list.appendChild(row);
    });
    input.addEventListener("input", function () { Array.from(list.children).forEach(function (row) { row.hidden = !row.textContent.toLocaleLowerCase().includes(input.value.toLocaleLowerCase()); }); });
    input.addEventListener("keydown", function (event) { if (event.key === "Enter") { event.preventDefault(); var first = list.querySelector("button:not([hidden])"); if (first) first.click(); } });
    return { element: root, init: function () {}, focus: function () { input.focus({ preventScroll: true }); } };
  }
  function openChooser(group, direction) {
    closeChooser(); wideLayout = null; api.exitMaximizedGroup();
    while (panel("choose-" + nextChooser)) ++nextChooser;
    var item = api.addPanel({ id: "choose-" + nextChooser++, component: "chooser", title: "Выберите экран",
      minimumWidth: 150, minimumHeight: 120, position: { referenceGroup: group, direction: direction } });
    item.api.setActive(); requestAnimationFrame(function () { var input = host.querySelector('[data-chooser-id="' + item.id + '"] input'); if (input) input.focus(); }); changed();
  }
  function menu(trigger, items) {
    closeChooser(); returnFocus = trigger;
    chooser = element("div", "dock-chooser dock-action-menu"); chooser.setAttribute("role", "menu");
    items.forEach(function (item) {
      var control = button(item[0], item[1], function () { closeChooser(); item[2](); });
      control.setAttribute("role", "menuitem"); control.appendChild(element("span", "", item[0])); chooser.appendChild(control);
    });
    document.body.appendChild(chooser);
    var rect = trigger.getBoundingClientRect(); chooser.style.left = Math.max(8, Math.min(rect.right - 248, window.innerWidth - 256)) + "px";
    chooser.style.top = Math.min(rect.bottom + 6, window.innerHeight - chooser.offsetHeight - 8) + "px";
    chooser.querySelector("button").focus();
  }
  function updateContext() {
    if (!api) return;
    var title = roots.conversation.querySelector(owner === "office" ? "#activeChatTitle" : "#projectChatTitle").textContent;
    var chat = panel("conversation"); if (chat && chat.title !== title) chat.api.setTitle(title || "Чат");
    var names = { settings: "Настройки", diagnostics: "Диагностика", artifacts: "Материалы", instructions: "Библиотека", vba: "VBA", plugins: "Плагины" };
    var context = names[section];
    if (!context) context = owner === "office" ? (activeChatSummary() ? chatDocumentTitle(activeChatSummary()) : "") : roots.conversation.querySelector("#projectChatContext").textContent;
    setText("workspaceSectionTitle", context || "Рабочая область");
    setIcon(document.getElementById("workspaceContextIcon"), section === "chat" ? owner === "office" ? "table" : "folder" : "layers");
    setText("workspaceHostStatus", window.PrototypeFixtures ? "Mock · без подключения к Office" : state.bridgeUnavailable ? "Нет связи с хостом" : "Локальное пространство");
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
        ["Закрыть вкладку", "close", function () { if (group.activePanel) group.activePanel.api.close(); changed(); }],
        ["Закрыть группу", "close", function () { wideLayout = null; api.exitMaximizedGroup(); group.panels.slice().forEach(function (item) { item.api.close(); }); changed(); }]
      ]);
    }); actions.setAttribute("aria-haspopup", "menu"); root.appendChild(actions);
    var dimensions = group.api.onDidDimensionsChange(updateButtons); updateButtons();
    return { element: root, init: function () {}, dispose: function () { dimensions.dispose(); if (returnFocus && root.contains(returnFocus)) closeChooser(); } };
  }
  function validateLayout(layout) {
    if (!layout || !layout.grid || !layout.panels) return false;
    return Object.keys(layout.panels).every(function (id) { return views[id] && layout.panels[id].contentComponent === id || /^choose-\d+$/.test(id) && layout.panels[id].contentComponent === "chooser"; });
  }
  function reset() {
    restoring = true; wideLayout = null; api.clear();
    var chat = add("conversation"); add("inspector", { referencePanel: chat, direction: compact || host.clientWidth < 560 ? "within" : "right" }).api.setSize({ width: 300 });
    chat.api.setActive(); restoring = false; changed();
  }
  function join() {
    wideLayout = null; api.exitMaximizedGroup();
    var group = api.activeGroup || api.groups[0];
    api.panels.slice().forEach(function (item) { if (item.group !== group) item.api.moveTo({ group: group, position: "center" }); }); changed();
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
    compact = next; closeChooser(); restoring = true;
    api.updateOptions({ disableDnd: compact });
    if (compact && api.groups.length > 1) {
      wideLayout = api.toJSON(); api.exitMaximizedGroup();
      var group = api.activeGroup || api.groups[0], active = api.activePanel;
      api.panels.slice().forEach(function (item) { if (item.group !== group) item.api.moveTo({ group: group, position: "center" }); });
      if (active) active.api.setActive();
    } else if (!compact && wideLayout) {
      var saved = wideLayout; wideLayout = null; api.fromJSON(saved);
    }
    restoring = false; sync();
    if (focused && focused.isConnected && !focused.closest("[inert]") && focused.getClientRects().length) focused.focus({ preventScroll: true });
  }
  function bind() {
    if (api) return;
    parking = element("div"); parking.id = "workspaceViewParking"; parking.hidden = true;
    document.querySelector(".shell").appendChild(parking);
    roots.conversation = element("div", "dock-conversation"); roots.conversation.id = "conversationView";
    roots.conversation.append(document.getElementById("chatDropZone"), document.getElementById("workspaceChatView"));
    roots.inspector = document.getElementById("chatInspector"); roots.inspector.hidden = false;
    parking.append(roots.conversation, roots.inspector);
    host = element("div", "chat-dock-host"); host.id = "chatDockHost";
    host.setAttribute("role", "region"); host.setAttribute("aria-label", "Рабочее пространство");
    host.setAttribute("aria-description", "Размер группы: Alt+Shift+стрелки при фокусе во вкладке.");
    document.getElementById("chatLayout").appendChild(host);
    var settings = document.querySelector(".rail-settings");
    document.querySelector(".workspace-topbar-actions").appendChild(settings);
    var dock = element("div", "chat-dock"); host.appendChild(dock);
    api = window["dockview-core"].createDockview(dock, {
      theme: { name: "rnassistant", className: "rna-product-dock", gap: 4, dndOverlayMounting: "absolute", dndPanelOverlay: "content" },
      disableFloatingGroups: true, dndStrategy: "pointer",
      messages: { closeTab: function (title) { return "Закрыть «" + title + "»"; }, closeTabPlain: "Закрыть вкладку" },
      createComponent: createComponent, createRightHeaderActionComponent: header,
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
    api.onDidLayoutChange(changed); api.onDidActivePanelChange(sync); api.onDidMaximizedGroupChange(sync);
    api.onDidRemovePanel(function () { if (!restoring) wideLayout = null; });
    document.getElementById("workspacePanels").addEventListener("click", function () { choose(null, null, this); });
    document.getElementById("workspaceMaximize").addEventListener("click", function () { if (api.hasMaximizedGroup()) api.exitMaximizedGroup(); else if (api.activePanel) api.activePanel.api.maximize(); changed(); });
    document.getElementById("workspaceLayout").addEventListener("click", function () {
      menu(this, [["Чат и инспектор", "panel", reset], ["Одна группа вкладок", "layers", join],
        ["Сохранить раскладку", "check", function () { try { localStorage.setItem(savedKey, JSON.stringify(api.toJSON())); notice("Раскладка сохранена"); } catch (error) { notice("Не удалось сохранить раскладку"); } }],
        ["Вернуть сохранённую", "refresh", function () { try { var layout = JSON.parse(localStorage.getItem(savedKey)); if (validateLayout(layout)) { restoring = true; api.fromJSON(layout); restoring = false; changed(); notice("Раскладка восстановлена"); } else notice("Сохранённой раскладки нет или её формат недоступен"); } catch (error) { restoring = false; notice("Не удалось восстановить раскладку"); } }]]);
    });
    document.getElementById("workspaceDiagnostics").addEventListener("click", function () { switchTab("diagnostics"); });
    document.addEventListener("pointerdown", function (event) { if (chooser && !chooser.contains(event.target) && event.target !== returnFocus) closeChooser(); });
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
      if (event.key === "Tab" && chooser) {
        var buttons = chooser.querySelectorAll("button"), first = buttons[0], last = buttons[buttons.length - 1];
        if (event.shiftKey && document.activeElement === first) { last.focus(); event.preventDefault(); }
        else if (!event.shiftKey && document.activeElement === last) { first.focus(); event.preventDefault(); }
      }
      if (event.key === "Escape" && chooser) { closeChooser(true); event.preventDefault(); }
      else if (event.key === "Escape" && api.hasMaximizedGroup()) { api.exitMaximizedGroup(); event.preventDefault(); }
    });
    var observer = new ResizeObserver(function (entries) { resize(entries[0].contentRect.width); }); observer.observe(host);
    window.addEventListener("pagehide", function (event) { clearTimeout(saveTimer); persist(); if (!event.persisted) { ++activation; clearTimeout(noticeTimer); observer.disconnect(); closeChooser(); api.dispose(); } });
    sync();
  }
  window.RNAssistantDockWorkspace = {
    bind: bind, mount: mount, show: show, close: close, updateContext: updateContext,
    toggleTools: toggleTools, inspectorToggleLabel: inspectorToggleLabel,
    canRenderConversation: function () { if (!api || conversationVisible) return true; dirtyConversation = true; return false; },
    onTabChange: function (name) {
      section = name; if (!api) return;
      document.getElementById("workspaceViewActions").hidden = name !== "chat";
      document.getElementById("toggleChatInspectorButton").hidden = name !== "chat";
      document.getElementById("workspaceMaximize").hidden = name !== "chat";
      closeChooser(); sync();
    }
  };
}());
