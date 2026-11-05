(function () {
  "use strict";
  // Presentation of the existing catalog. Authority, revisions and membership belong to runtime.
  var scopes = new Map(), collapsed = new Map(), selected = new Map(), busy = new Set(), version = 0, lifetime = 0;
  function key(scope) { return JSON.stringify([scope.workspaceId || "", scope.host || "", scope.documentKey || ""]); }
  function snapshot(scope) { return scopes.get(key(scope)) || { groups: [] }; }
  function accept(values) {
    (values || []).forEach(function (value) {
      var id = key(value), prior = scopes.get(id);
      if (!value.error && prior && value.generation < prior.generation) return;
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
      var current = snapshot(scope);
      accept([Object.assign({}, current, scope, { error: (error.detail || error.message) + " Обновите список." })]);
    } finally { if (admittedLifetime === lifetime) { busy.delete(id); version++; repaint(); } }
  }
  function create(scope) {
    var name = window.prompt("Название группы", "");
    if (name && name.trim()) return change(scope, "Create", null, { name: name.trim() });
  }
  function rename(scope, group) {
    var name = window.prompt("Название группы", group.name);
    if (name && name.trim() && name.trim() !== group.name) return change(scope, "Rename", group, { name: name.trim() });
  }
  function removeGroup(scope, group) {
    if (window.confirm("Удалить группу «" + group.name + "»? Чаты и их история сохранятся.")) return change(scope, "Delete", group);
  }
  function menuAction(label, items) {
    return { title: label, iconHtml: RNAssistantNavigationTree.icon("more"), onClick: function (node, button) {
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
    items.push({ label: "Новая группа…", icon: "plus", disabled: disabled(scope), onSelect: function () { create(scope); } });
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
              disabled: action.disabled, icon: false, onSelect: function () { action.onClick(target, button); } };
          });
          items.push({ separator: true }, { label: "Группы чата…", icon: "group", disabled: disabled(scope),
            onSelect: function () { membershipMenu(scope, id, button); } });
          if (group) items.push({ label: "Убрать из группы", icon: "close", disabled: disabled(scope),
            onSelect: function () { change(scope, "Remove", group, { members: [id] }); } });
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
        children: forest(group.members, group), actions: [menuAction("Действия группы «" + group.name + "»", function () {
          return [{ label: "Переименовать группу", icon: "edit", disabled: disabled(scope), onSelect: function () { rename(scope, group); } },
            { label: "Удалить группу", icon: "trash", disabled: disabled(scope), onSelect: function () { removeGroup(scope, group); } }];
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
      disabled: disabled(scope), onClick: function () { create(scope); } }; },
    select: function (node) { if (node.chatGroupScope) { selected.set(node.chatGroupScope, node.key); version++; } },
    toggle: function (node, expanded) { collapsed.set(node.key, !expanded); if (collapsed.size > 512) collapsed.delete(collapsed.keys().next().value); version++; },
    reset: function () { lifetime++; scopes.clear(); collapsed.clear(); selected.clear(); busy.clear(); version++; }
  };
}());
