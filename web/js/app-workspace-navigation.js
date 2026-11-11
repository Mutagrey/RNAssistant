(function () {
  "use strict";
  // One navigation presentation; catalogs and commands stay with their source owners.
  var pane, statusNode, sections = {}, collapsed = {}, notices = {}, errors = {}, retries = {}, activeSource = "office", selectedTarget, focusPending = false;
  function root() { return document.getElementById("chatSessionList"); }
  function visit(nodes, action) { nodes.forEach(function (node) { action(node); if (node.children) visit(node.children, action); }); }
  function matches(node, target) {
    return target && (target.source === "office" ? node.chatId === target.sessionId :
      node.workspaceId === target.workspaceId && node.sessionId === target.sessionId);
  }
  function selection(source, target) {
    var section = sections[source], found = null;
    if (!section) return null;
    visit(section.nodes, function (node) {
      if (matches(node, target) && (!found || node.key === section.selectedKey)) found = node.key;
    });
    return found;
  }
  function select(target, activate) {
    if (!target) return;
    var selected = window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected();
    if (!activate && selected && selected.target.source !== target.source) return;
    if (!activate && selected && selected.target.sessionId !== target.sessionId) return;
    if (activate) focusPending = root().contains(document.activeElement) || document.activeElement === document.getElementById("newChatButton");
    activeSource = target.source === "office" ? "office" : "projects";
    selectedTarget = target;
    var section = sections[activeSource], key = selection(activeSource, target);
    if (section) section.selectedKey = key;
    if (pane && key) { RNAssistantNavigationTree.setSelected(root(), key, { focus: focusPending }); focusPending = false; }
    else if (pane) RNAssistantNavigationTree.setSelected(root(), null);
  }
  function status() {
    if (!pane) return;
    var error = Object.keys(errors).some(function (key) { return errors[key] && notices[key]; });
    pane.setState({ retry: Object.keys(retries).length > 0, message: Object.keys(notices).map(function (key) { return notices[key]; }).filter(Boolean).join("\n") });
    statusNode.setAttribute("role", error ? "alert" : "status"); statusNode.classList.toggle("is-error", !!error);
  }
  function mount() {
    if (pane || !root()) return;
    var add = document.getElementById("newChatButton"); add.hidden = false;
    statusNode = document.createElement("p");
    pane = RNAssistantNavigationPane.mount({ root: root().closest(".global-nav"),
      heading: document.getElementById("workspaceNavigationHeading"), search: document.getElementById("chatSearchInput"),
      tree: root(), title: "Рабочая область", actions: add, status: statusNode }, {
      onRetry: function () { Object.keys(retries).forEach(function (key) { retries[key](); }); },
      onQuery: function (value) {
        state.chatSearch = value;
        renderChatSessionList(sortedChatSessions(state.chats));
        RNAssistantProjectChats.search(value);
      }
    });
    add.addEventListener("click", function () {
      var component = RNAssistantConversationPanel.selected(), target = component ? component.target : { source: "office" };
      var items = [{ label: "Добавить проект…", iconHtml: RNAssistantIcons.render("folder", "project-folder-icon"),
        disabled: !!state.bridgeUnavailable || RNAssistantProjectChats.isCatalogBusy(),
        onSelect: function () { RNAssistantProjectChats.addProject(); } }];
      items.unshift({ label: "Новый чат", icon: "chat", disabled: !!state.bridgeUnavailable ||
        (target.source === "office" ? !!state.chatNavigationPending : !RNAssistantProjectChats.canCreate(target.workspaceId)),
        onSelect: function () {
          if (target.source === "office") createChat();
          else RNAssistantProjectChats.create(target.workspaceId);
        } });
      items.push({ separator: true }, { label: "Новый документ Office", informational: true, icon: false });
      var unavailable = officeHostChatUnavailableReason();
      ["Excel", "Word", "PowerPoint", "Outlook"].forEach(function (host) {
        items.push({ label: host, iconHtml: documentHostIcon(host), disabled: !!unavailable,
          onSelect: function () { createOfficeHostChat(host); } });
      });
      if (unavailable) items.push({ label: unavailable, informational: true, icon: false });
      RNAssistantActionMenu.open(add, items, { label: "Добавить в рабочую область" });
    });
    status();
  }
  function update(source, nodes, options) {
    mount(); if (!pane) return;
    var section = sections[source] = { nodes: nodes, options: options, selectedKey: null };
    visit(nodes, function (node) { node.navigationSource = source; if (node.selected) section.selectedKey = node.key; });
    if (selectedTarget && activeSource === source) section.selectedKey = selection(source, selectedTarget);
    var all = ["office", "projects"].map(function (name) {
      var entry = sections[name], children = entry ? entry.nodes : [];
      visit(children, function (node) { node.selected = name === activeSource && node.key === entry.selectedKey; });
      return { key: "workspace-section:" + name, kind: "group", className: "navigation-tree-section",
        title: name === "office" ? "Office" : "Проекты", navigationSource: name,
        expanded: state.chatSearch ? true : !collapsed[name], children: children.length ? children : [
          { key: "workspace-empty:" + name, kind: "leaf", disabled: true,
            title: entry ? entry.options.emptyText : "Загрузка…" }
        ] };
    });
    var focusKey = focusPending && sections[activeSource] && sections[activeSource].selectedKey;
    pane.render(all, {
      focusKey: focusKey || null,
      onToggle: function (node, expanded) {
        if (node.key.indexOf("workspace-section:") === 0) collapsed[node.navigationSource] = !expanded;
        else sections[node.navigationSource].options.onToggle(node, expanded);
      },
      onSelect: function (node) { return sections[node.navigationSource].options.onSelect(node); }
    });
    if (focusKey) focusPending = false;
  }
  window.RNAssistantWorkspaceNavigation = { update: update, select: select,
    notice: function (source, message, retry, error) { notices[source] = message || ""; errors[source] = !!error;
      if (retry) retries[source] = retry; else delete retries[source]; status(); } };
}());
