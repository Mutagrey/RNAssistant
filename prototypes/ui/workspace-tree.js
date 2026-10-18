(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon;

  P.initWorkspaceTree = function (s) {
    s.chatSequence = 0;
    s.workspaceScope = "office";
    s.treeSearchOpen = false;
    s.treeSearch = "";
    s.collapsedParents = { "office-budget": true, "office-notes": true, "project-budget": true };
    s.parents = [
      { id: "office-sales", scope: "office", name: P.fixtures.document, host: "Excel", open: true },
      { id: "office-budget", scope: "office", name: "Бюджет · IV квартал.xlsx", host: "Excel", open: false },
      { id: "office-notes", scope: "office", name: "Заметки к отчёту.docx", host: "Word", open: true },
      { id: "project-reports", scope: "projects", name: "Управленческая отчётность", path: "C:\\Projects\\Reports" },
      { id: "project-budget", scope: "projects", name: "Бюджет 2027", path: "C:\\Projects\\Budget" }
    ];
    s.chats.sales.scope = "office"; s.chats.sales.parent = "office-sales";
    s.chats.sales.attachments = s.attachments;
    s.chats.summary.scope = "office"; s.chats.summary.parent = "office-sales";
    s.chats.summary.document = P.fixtures.document;
    [
      ["budget", "План бюджета", "office-budget", "office"],
      ["notes", "Обсуждение отчёта", "office-notes", "office"],
      ["project-review", "Подготовка отчёта", "project-reports", "projects"],
      ["project-clean", "Проверка источников", "project-reports", "projects"],
      ["project-plan", "Планирование бюджета", "project-budget", "projects"]
    ].forEach(function (v) {
      var parent = s.parents.find(function (p) { return p.id === v[2]; });
      s.chats[v[0]] = { title: v[1], parent: v[2], scope: v[3], document: v[3] === "office" ? parent.name : null, offline: v[3] === "office" && !parent.open, draft: "", messages: [], plan: false, status: "ready", empty: true };
    });
    s.lastScopeChat = { office: "sales", projects: "project-review" };
  };
  P.chatParent = function () { var s = P.state; return s.parents.find(function (p) { return p.id === s.chats[s.chatId].parent; }); };
  P.chatContextName = function () { var parent = P.chatParent(); return parent ? parent.name : P.state.workspaceScope === "projects" ? "Папка не выбрана" : "Документ не выбран"; };
  P.isProjectChat = function () { return P.state.chats[P.state.chatId].scope === "projects"; };
  function rowAction(label, action, icon, disabled) {
    return { title: disabled ? "Сначала остановите выполнение" : label, iconHtml: I(icon),
      disabled: !!disabled, danger: action.indexOf("delete-") === 0,
      onClick: function () { P.dispatchAction(action); } };
  }
  P.workspaceNodes = function () {
    var s = P.state, query = s.treeSearch.trim().toLocaleLowerCase("ru"), current = P.chatParent();
    var parents = s.parents.filter(function (p) { return p.scope === s.workspaceScope; });
    if (s.workspaceScope === "office") parents.sort(function (a, b) { return Number(!!current && b.id === current.id) - Number(!!current && a.id === current.id) || Number(b.open) - Number(a.open) || a.name.localeCompare(b.name, "ru"); });
    return parents.map(function (parent) {
      var ownMatch = parent.name.toLocaleLowerCase("ru").includes(query);
      var ids = Object.keys(s.chats).filter(function (id) { return s.chats[id].parent === parent.id; }).sort(function (a, b) { return (s.chats[b].order || 0) - (s.chats[a].order || 0); });
      var children = ids.filter(function (id) { return !query || ownMatch || s.chats[id].title.toLocaleLowerCase("ru").includes(query); }).map(function (id) {
        var c = s.chats[id], running = c.status === "running", waiting = ["approval", "needs-input"].includes(c.status);
        return { key: "chat:" + id, title: c.title, chat: id, selected: id === s.chatId, kind: "chat",
          status: running ? "running" : waiting ? "waiting" : c.unread ? "unread" : "",
          actions: [rowAction("Переименовать чат «" + c.title + "»", "rename-tree-chat:" + id, "edit"), rowAction("Удалить чат «" + c.title + "»", "delete-tree-chat:" + id, "trash", running)] };
      });
      if (query && !ownMatch && !children.length) return null;
      return { key: "parent:" + parent.id, title: parent.name, folder: true, expanded: !!query || !s.collapsedParents[parent.id], kind: "parent", groupKey: parent.id,
        tooltip: parent.name + " · " + (parent.path || (parent.open ? "Открыт" : "Закрыт")),
        iconHtml: parent.scope === "office" ? P.officeIcon(parent.host) : I("folder"), children: children,
        actions: [rowAction("Новый чат в «" + parent.name + "»", "parent-new:" + parent.id, "plus"), rowAction("Открыть «" + parent.name + "»", "parent-open:" + parent.id, "external"), rowAction("Удалить историю «" + parent.name + "»", "parent-delete:" + parent.id, "trash", ids.some(function (id) { return s.chats[id].status === "running"; }))] };
    }).filter(Boolean);
  };
  P.renderTreeContents = function () { return '<div class="product-tree" data-product-tree="workspace" aria-label="Документы, проекты и чаты"></div>'; };
  P.renderWorkspaceNavigation = function () {
    var s = P.state;
    var scope = function (key, title, icon) { return '<button role="menuitemradio" data-scope="' + key + '" aria-checked="' + (s.workspaceScope === key) + '">' + I(icon) + '<span>' + title + '</span>' + (s.workspaceScope === key ? I("check") : '') + '</button>'; };
    var scopeTitle = s.workspaceScope === "office" ? "Документы Office" : "Проекты";
    var picker = P.panelMenu(scopeTitle, "Раздел рабочей области", s.workspaceScope === "office" ? "office" : "folder", "scope-switcher", scope("office", "Документы Office", "office") + scope("projects", "Проекты", "folder"));
    var launcher = s.workspaceScope === "office" ? '<details class="office-launch popover"><summary>' + I("plus") + 'Новый документ Office' + I("down") + '</summary><div class="popover-menu">' + ["Excel", "Word", "PowerPoint", "Outlook"].map(function (host) { return '<button data-action="launch-office:' + host + '">' + P.officeIcon(host) + '<span class="office-launch-name">' + host + '</span><small>Новый чат</small></button>'; }).join("") + '</div></details>' : '';
    var toolbar = '<div class="workspace-heading scope-heading' + (s.treeSearchOpen ? ' searching' : '') + '">' + picker + '<div class="tree-toolbar">' + P.sidebarSearchButton("tree", s.treeSearchOpen, s.treeSearch) + '<input type="search" class="tree-search-input" data-tree-search placeholder="Найти…" aria-label="Найти документ, папку или чат" value="' + E(s.treeSearch) + '"' + (s.treeSearchOpen ? '' : ' hidden') + '>' + P.iconButton("Новый чат", "new-scoped-chat", "plus") + '</div></div>';
    return '<div class="workspace-switcher">' + toolbar + '</div><section class="workspace-tree">' + launcher + '<div class="tree-scroll">' + P.renderTreeContents() + '</div>' + '</section>';
  };
}());
