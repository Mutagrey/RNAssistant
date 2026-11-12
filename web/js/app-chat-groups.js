(function () {
  "use strict";
  // Presentation of the existing catalog. Authority, revisions and membership belong to runtime.
  var scopes = new Map(), collapsed = new Map(), selected = new Map(), busy = new Set(), version = 0, lifetime = 0, dialog = null;
  function key(scope) { return JSON.stringify([scope.workspaceId || "", scope.host || "", scope.documentKey || ""]); }
  function snapshot(scope) { return scopes.get(key(scope)) || { groups: [] }; }
  function accept(values) {
    (values || []).forEach(function (value) {
      var id = key(value), prior = scopes.get(id);
      if (prior && value.generation < prior.generation && (!value.error || value.generation > 0)) return;
      // A failed catalog read is not an empty authoritative catalog. Keep the
      // last successful membership until a current read replaces it.
      // Error DTOs without a captured generation use zero; show their error,
      // but never let them lower the watermark or move chats out of groups.
      if (value.error && prior) value = Object.assign({}, value, { groups: prior.groups, generation: Math.max(prior.generation, value.generation || 0) });
      var changed = !prior || JSON.stringify([prior.groups, prior.error]) !== JSON.stringify([value.groups, value.error]);
      scopes.delete(id); scopes.set(id, value);
      if (changed) version++;
    });
    while (scopes.size > 256) { var oldest = scopes.keys().next().value; scopes.delete(oldest); selected.delete(oldest); }
  }
  function repaint() {
    renderChatSessions({ navigationOnly: true });
    if (window.RNAssistantProjectChats) RNAssistantProjectChats.refreshTree();
  }
  function disabled(scope) { return scope.disabled || (!scope.sessionId && !scope.workspaceId) || busy.has(key(scope)) || !!snapshot(scope).error; }
  async function change(scope, action, group, extra) {
    if (disabled(scope)) return;
    var id = key(scope), admittedLifetime = lifetime;
    RNAssistantActionMenu.closeAll(); busy.add(id); version++; repaint();
    try {
      var response = await send("changeChatGroup", Object.assign({ sessionId: scope.sessionId || null,
        workspaceId: scope.workspaceId || null, action: action, groupId: group && group.id,
        revision: group && group.revision }, extra || {}));
      if (admittedLifetime !== lifetime) return;
      if (response.scope && key(response.scope) === id) accept([response.scope]);
      else throw new Error("Изменение группы не подтверждено. Обновите список без повтора команды.");
    } catch (error) {
      if (admittedLifetime !== lifetime) return;
      log(error.detail || error.message, "error");
      // Keep failure visible in the existing menu; uncertainty never retries the write.
      var current = snapshot(scope), message = (error.detail || error.message) + " Обновите список без повтора команды.";
      accept([Object.assign({}, current, scope, { error: message })]);
      return message;
    } finally { if (admittedLifetime === lifetime) { busy.delete(id); version++; repaint(); } }
  }
  function dismissDialog() {
    if (dialog) { var previous = dialog; dialog = null; previous.view.close(false); }
  }
  function navigationScope() {
    var panel = window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected();
    return panel ? JSON.stringify(panel.target) : "";
  }
  function focusResolver(button) {
    var item = button && button.closest("[data-tree-key]"), root = item && item.closest('[role="tree"]');
    var mount = root && root.parentElement, keys = [], action = button && button.dataset.treeAction;
    while (item) { keys.push(item.dataset.treeKey); item = item.parentElement && item.parentElement.closest("[data-tree-key]"); }
    function visible(node) { return node && node.isConnected && !node.disabled && node.getClientRects().length && getComputedStyle(node).visibility === "visible"; }
    function revealAction(target) {
      var row = target && target.closest("[data-tree-key]");
      // Tree actions hide when focus leaves the row; focus its current item first.
      if (!visible(target) && visible(row)) row.focus({ preventScroll: true });
      return visible(target) ? target : null;
    }
    return function () {
      var original = revealAction(button); if (original) return original;
      // Mutation repaint replaces rows; resolve their current controls by stable tree identity.
      var currentRoot = root && root.isConnected ? root : mount && mount.isConnected ? mount : null;
      if (currentRoot) {
        var nodes = Array.from(currentRoot.querySelectorAll("[data-tree-key]"));
        for (var index = 0; index < keys.length; index++) {
          var row = nodes.find(function (node) { return node.dataset.treeKey === keys[index]; });
          if (!row) continue;
          var target = !index && Array.from(row.querySelectorAll(":scope > .navigation-tree-row [data-tree-action]"))
            .find(function (node) { return node.dataset.treeAction === action; });
          target = revealAction(target); if (target) return target;
          if (visible(row)) return row;
        }
      }
      return Array.from(document.querySelectorAll("#newChatButton, #productMobileMenuButton")).find(visible);
    };
  }
  function editGroup(scope, action, group, button) {
    if (disabled(scope)) return;
    dismissDialog(); RNAssistantActionMenu.closeAll();
    // A custom dialog yields to host updates; keep the exact displayed target/revision, not live objects.
    var boundScope = Object.assign({}, scope), boundGroup = group && { id: group.id, name: group.name, revision: group.revision };
    var openedLifetime = lifetime, openedScope = navigationScope(), restore = focusResolver(button), current = {};
    var naming = action !== "Delete";
    dialog = current;
    current.view = RNAssistantDialog.open({
      title: action === "Create" ? "Новая группа чатов" : naming ? "Переименовать группу" : "Удалить группу",
      label: naming ? "Название группы" : null, value: group ? group.name : "",
      message: naming ? null : "Удалить группу «" + boundGroup.name + "»? Чаты и их история сохранятся.",
      submitLabel: action === "Create" ? "Создать" : naming ? "Сохранить" : "Удалить", danger: !naming,
      validate: function (value) { return naming && !value.trim() ? "Введите название группы." : ""; },
      onSubmit: async function (value) {
        if (dialog !== current || openedLifetime !== lifetime || openedScope !== navigationScope()) return;
        if (disabled(boundScope)) throw new Error("Группы недоступны. Закройте окно и обновите список.");
        var name = naming ? value.trim() : null;
        if (naming && (!name || boundGroup && name === boundGroup.name)) return;
        var error = await change(boundScope, action, boundGroup, naming ? { name: name } : null);
        if (error) throw new Error(error);
      },
      restoreFocus: function () { return openedLifetime === lifetime && openedScope === navigationScope() ? restore() : null; },
      onClose: function () { if (dialog === current) dialog = null; }
    });
  }
  function menuAction(label, items) {
    return { title: label, menu: true, iconHtml: RNAssistantNavigationTree.icon("more"), onClick: function (node, button) {
      RNAssistantActionMenu.open(button, items(node, button), { label: label });
    } };
  }
  function membershipMenu(scope, chatId, button, offset) {
    offset = offset || 0;
    var groups = snapshot(scope).groups || [], page = groups.slice(offset, offset + 30);
    var items = page.map(function (group) {
      var member = group.members.indexOf(chatId) >= 0;
      return { label: group.name, checked: member, disabled: disabled(scope), onSelect: function () {
        change(scope, member ? "Remove" : "Add", group, { members: [chatId] });
      } };
    });
    if (offset) items.push({ label: "Предыдущие группы", icon: "left", onSelect: function () { membershipMenu(scope, chatId, button, offset - 30); } });
    if (offset + page.length < groups.length) items.push({ label: "Следующие группы", onSelect: function () { membershipMenu(scope, chatId, button, offset + 30); } });
    items.push({ label: "Новая группа…", icon: "plus", disabled: disabled(scope), onSelect: function () { editGroup(scope, "Create", null, button); } });
    RNAssistantActionMenu.open(button, items, { label: "Группы чата" });
  }
  function decorate(nodes, chats, scope, query) {
    var view = snapshot(scope), groups = view.groups || [], byId = new Map(), metadata = new Map();
    nodes.forEach(function (node) { byId.set(node.chatId || node.sessionId, node); });
    chats.forEach(function (chat) { metadata.set(chat.Id || chat.id, chat); });
    var assigned = new Set();
    groups.forEach(function (group) { group.members.forEach(function (id) { assigned.add(id); }); });
    function forest(ids, group) {
      var entries = new Map();
      ids.forEach(function (id) {
        var original = byId.get(id); if (!original || entries.has(id)) return;
        var node = Object.assign({}, original), oldActions = original.actions || [];
        node.chatGroupScope = key(scope);
        node.iconHtml = group ? RNAssistantAgentCommunication.marker(id) : "";
        node.key = group ? original.key + ":group:" + group.id : original.key;
        node.actions = [menuAction("Действия чата «" + node.title + "»", function (target, button) {
          var items = oldActions.filter(function (action) { return !action.hidden; }).map(function (action) {
            return { label: group && action.danger ? "Удалить чат везде" : action.title,
              disabled: action.disabled, danger: action.danger, iconHtml: action.iconHtml,
              onSelect: function () { action.onClick(target, button); } };
          });
          var destructive = items.filter(function (item) { return item.danger; });
          items = items.filter(function (item) { return !item.danger; });
          items.push({ separator: true }, { label: "Группы чата…", icon: "group", disabled: disabled(scope),
            onSelect: function () { membershipMenu(scope, id, button); } });
          if (group) items.push({ label: "Убрать из группы", icon: "close", disabled: disabled(scope),
            onSelect: function () { change(scope, "Remove", group, { members: [id] }); } });
          if (destructive.length) items = items.concat([{ separator: true }], destructive);
          if (view.error) items.push({ label: view.error, disabled: true, icon: false });
          return items;
        })];
        entries.set(id, node);
      });
      var roots = [];
      entries.forEach(function (node, id) {
        var chat = metadata.get(id), parentId = chat && (chat.CreatorChatId || chat.creatorChatId), seen = new Set([id]), ancestor = parentId;
        while (ancestor && entries.has(ancestor) && !seen.has(ancestor)) {
          seen.add(ancestor); var parent = metadata.get(ancestor); ancestor = parent && (parent.CreatorChatId || parent.creatorChatId);
        }
        if (ancestor && seen.has(ancestor)) parentId = null; // Corrupt provenance never hides a cycle.
        var parentNode = parentId && entries.get(parentId);
        if (parentNode) {
          parentNode.children = parentNode.children || []; parentNode.children.push(node);
          parentNode.kind = "parent"; parentNode.selectable = true; parentNode.chatGroupBranch = true;
          parentNode.expanded = !!query || !collapsed.get(parentNode.key);
        } else roots.push(node);
      });
      return roots;
    }
    var result = groups.filter(function (group) {
      return !query || group.name.toLocaleLowerCase().includes(query.toLocaleLowerCase()) || group.members.some(function (id) { return byId.has(id); });
    }).map(function (group) {
      var id = "chat-group:" + key(scope) + ":" + group.id;
      return { key: id, kind: "parent", title: group.name, tooltip: group.name, chatGroupBranch: true,
        iconHtml: RNAssistantNavigationTree.icon("group"), expanded: !!query || !collapsed.get(id),
        children: forest(group.members, group), actions: [menuAction("Действия группы «" + group.name + "»", function (node, button) {
          return [{ label: "Переименовать группу", icon: "edit", disabled: disabled(scope), onSelect: function () { editGroup(scope, "Rename", group, button); } },
            { label: "Удалить группу", icon: "trash", disabled: disabled(scope), onSelect: function () { editGroup(scope, "Delete", group, button); } }];
        })] };
    });
    if (view.error) result.push({ key: "group-error:" + key(scope), kind: "leaf", title: "Группы недоступны", tooltip: view.error, disabled: true });
    result = result.concat(forest(Array.from(byId.keys()).filter(function (id) { return !assigned.has(id); }), null));
    var selectedNodes = [];
    function collect(nodes) { nodes.forEach(function (node) { if (node.selected) selectedNodes.push(node); if (node.children) collect(node.children); }); }
    collect(result);
    var chosen = selectedNodes.find(function (node) { return node.key === selected.get(key(scope)); }) || selectedNodes[0];
    selectedNodes.forEach(function (node) { node.selected = node === chosen; });
    return result;
  }
  function matches(scope, chatId, query) {
    return (snapshot(scope).groups || []).some(function (group) {
      return group.members.indexOf(chatId) >= 0 && group.name.toLocaleLowerCase().includes(query.toLocaleLowerCase());
    });
  }
  window.RNAssistantChatGroups = { accept: accept, decorate: decorate, matches: matches, version: function () { return version; },
    createAction: function (scope) { return { title: "Новая группа чатов…", iconHtml: RNAssistantNavigationTree.icon("group"),
      disabled: disabled(scope), onClick: function (node, button) { editGroup(scope, "Create", null, button); } }; },
    dismissDialog: dismissDialog,
    select: function (node) { if (node.chatGroupScope) { selected.set(node.chatGroupScope, node.key); version++; } },
    toggle: function (node, expanded) { collapsed.set(node.key, !expanded); if (collapsed.size > 512) collapsed.delete(collapsed.keys().next().value); version++; },
    reset: function () { lifetime++; dismissDialog(); scopes.clear(); collapsed.clear(); selected.clear(); busy.clear(); version++; }
  };
}());
