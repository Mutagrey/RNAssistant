(function () {
  "use strict";
  var P = window.Prototype, I = P.icon, mounts = new Map(), positions = {}, serial = 0;
  function isGroup(node) { return node.kind === "group" || node.kind === "parent"; }
  function make(tag, className, text) {
    var el = document.createElement(tag); if (className) el.className = className;
    if (text != null) el.textContent = text; return el;
  }
  function mount(root) {
    if (mounts.has(root)) return;
    var workspace = root.dataset.productTree === "workspace", source = workspace ? P.workspaceNodes() : P.sectionTreeNodes();
    var key = workspace ? "workspace:" + P.state.workspaceScope : P.activeSection();
    var record = { root: root, key: key, nodes: new Map(), rows: new Map(), route: P.state.route, events: new AbortController() }, selected;
    root.id = "navigation-tree-" + (++serial); root.setAttribute("role", "tree"); root.classList.add("navigation-tree");
    function render(nodes, depth, parentKey) {
      var list = make("ul", "navigation-tree-list"); list.setAttribute("role", "group");
      nodes.forEach(function (node, index) {
        record.nodes.set(node.key, node);
        var group = isGroup(node), li = make("li", "navigation-tree-item"), row = make("div", "navigation-tree-row");
        li.dataset.treeKey = node.key; li.dataset.parentKey = parentKey || "";
        li.id = root.id + "-" + encodeURIComponent(node.key);
        li.setAttribute("role", "treeitem"); li.setAttribute("aria-label", node.title);
        li.setAttribute("aria-level", depth); li.setAttribute("aria-setsize", nodes.length); li.setAttribute("aria-posinset", index + 1);
        li.tabIndex = -1;
        row.style.setProperty("--tree-depth", depth - 1);
        row.classList.toggle("is-group", node.kind === "group");
        if (node.selected) { selected = li; li.setAttribute("aria-current", "page"); row.classList.add("selected"); }
        if (!group) li.setAttribute("aria-selected", String(!!node.selected));
        var expander = make(group ? "button" : "span", "navigation-tree-expander");
        if (group) {
          li.setAttribute("aria-expanded", String(!!node.expanded));
          expander.type = "button"; expander.tabIndex = -1; expander.dataset.treeToggle = node.key;
          expander.setAttribute("aria-label", (node.expanded ? "Свернуть " : "Развернуть ") + node.title);
          expander.innerHTML = I(node.expanded ? "down" : "right");
        } else expander.setAttribute("aria-hidden", "true");
        row.appendChild(expander);
        if (node.iconHtml) { var icon = make("span", "navigation-tree-icon"); icon.setAttribute("aria-hidden", "true"); icon.innerHTML = node.iconHtml; row.appendChild(icon); }
        var label = make("button", "navigation-tree-label", node.title);
        label.type = "button"; label.tabIndex = -1; label.title = node.tooltip || node.title; label.dataset.treeSelect = node.key; row.appendChild(label);
        if (node.status) {
          var mark = make("span", "tree-status " + ({ running: "tree-run", waiting: "tree-attention", unread: "tree-unread" }[node.status] || ""));
          mark.setAttribute("role", "status"); mark.setAttribute("aria-label", { running: "Запрос выполняется", waiting: "Требуется ответ", unread: "Новый ответ" }[node.status]);
          mark.title = mark.getAttribute("aria-label"); row.appendChild(mark);
        }
        if (node.actions) { var actions = make("span", "tree-actions"); actions.innerHTML = node.actions; row.appendChild(actions); }
        row.querySelectorAll(".tree-action").forEach(function (button) { button.tabIndex = -1; });
        li.appendChild(row); record.rows.set(node.key, li);
        if (group) { var children = render(node.children || [], depth + 1, node.key); children.hidden = !node.expanded; li.appendChild(children); }
        list.appendChild(li);
      });
      return list;
    }
    if (!source.length) { root.appendChild(make("p", "tree-empty", "Ничего не найдено")); return; }
    root.replaceChildren(render(source, 1, "")); mounts.set(root, record);
    function visibleRows() { return Array.from(record.rows.values()).filter(function (li) { return !li.closest("ul[hidden]"); }); }
    function setFocus(li, focus) {
      if (!li) return;
      record.rows.forEach(function (row) {
        row.tabIndex = row === li ? 0 : -1;
        row.firstElementChild.classList.toggle("is-focused", row === li && focus);
        row.firstElementChild.querySelectorAll(".tree-action").forEach(function (button) { button.tabIndex = row === li ? 0 : -1; });
      });
      if (focus) { li.focus({ preventScroll: true }); li.scrollIntoView({ block: "nearest" }); }
    }
    function toggle(li, expanded) {
      var node = record.nodes.get(li.dataset.treeKey), children = li.lastElementChild;
      if (!isGroup(node)) return;
      node.expanded = expanded; li.setAttribute("aria-expanded", String(expanded)); children.hidden = !expanded;
      var ex = li.querySelector("[data-tree-toggle]"); ex.innerHTML = I(expanded ? "down" : "right");
      ex.setAttribute("aria-label", (expanded ? "Свернуть " : "Развернуть ") + node.title);
      if (workspace) P.state.collapsedParents[node.groupKey] = !expanded; else P.state.navCollapsed[node.groupKey] = !expanded;
    }
    function on(name, handler) { root.addEventListener(name, handler, { signal: record.events.signal }); }
    on("click", function (event) {
      var li = event.target.closest("li[role=treeitem]"); if (!li || !root.contains(li) || event.target.closest(".tree-actions")) return;
      var node = record.nodes.get(li.dataset.treeKey);
      if (isGroup(node) && (!node.route || event.target.closest('[data-tree-toggle]'))) { toggle(li, !node.expanded); setFocus(li, true); }
      else { record.navigating = true; P.activateTreeItem(node); }
    });
    on("focusin", function (event) {
      var li = event.target.closest("li[role=treeitem]"); if (li && root.contains(li)) {
        setFocus(li, false); li.firstElementChild.classList.add("is-focused");
      }
    });
    on("focusout", function () { queueMicrotask(function () { if (!root.contains(document.activeElement)) root.querySelectorAll(".is-focused").forEach(function (row) { row.classList.remove("is-focused"); }); }); });
    var search = "", searchAt = 0;
    on("keydown", function (event) {
      var li = event.target.closest("li[role=treeitem]"), node = li && record.nodes.get(li.dataset.treeKey); if (!node) return;
      if ((event.key === "Enter" || event.key === " ") && event.target.closest("button")) return;
      var rows = visibleRows(), index = rows.indexOf(li), target;
      if (event.key === "ArrowDown") target = rows[Math.min(index + 1, rows.length - 1)];
      else if (event.key === "ArrowUp") target = rows[Math.max(index - 1, 0)];
      else if (event.key === "Home") target = rows[0];
      else if (event.key === "End") target = rows[rows.length - 1];
      else if (event.key === "ArrowRight") { if (isGroup(node) && !node.expanded) toggle(li, true); else if (isGroup(node)) target = li.lastElementChild.firstElementChild; }
      else if (event.key === "ArrowLeft") { if (isGroup(node) && node.expanded) toggle(li, false); else target = record.rows.get(li.dataset.parentKey); }
      else if (event.key === "Enter" || event.key === " ") { if (isGroup(node) && !node.route) toggle(li, !node.expanded); else { record.navigating = true; P.activateTreeItem(node); } }
      else if (event.key === "F10" && event.shiftKey) { var action = li.firstElementChild.querySelector(".tree-action:not(:disabled)"); if (action) action.focus(); }
      else if (event.key.length === 1 && !event.altKey && !event.metaKey && !event.ctrlKey) {
        if (Date.now() - searchAt > 700) search = ""; search += event.key.toLocaleLowerCase("ru"); searchAt = Date.now();
        target = rows.slice(index + 1).concat(rows.slice(0, index + 1)).find(function (row) { return record.nodes.get(row.dataset.treeKey).title.toLocaleLowerCase("ru").startsWith(search); });
      } else return;
      event.preventDefault(); if (target) setFocus(target, true);
    });
    function visibleAncestor(li) {
      while (li && li.closest("ul[hidden]")) li = record.rows.get(li.dataset.parentKey);
      return li;
    }
    var saved = positions[key], initial = visibleAncestor(selected) || record.rows.values().next().value;
    setFocus(initial, false);
    root.scrollTop = saved ? saved.scroll : 0;
    record.selection = selected && selected.dataset.treeKey;
    if (selected && saved && saved.selection !== record.selection) requestAnimationFrame(function () {
      if (root.isConnected && root.getBoundingClientRect().height) selected.firstElementChild.scrollIntoView({ block: "nearest" });
    });
    if (saved && saved.focus && saved.route === record.route && record.rows.has(saved.focus)) {
      requestAnimationFrame(function () { if (root.isConnected) setFocus(visibleAncestor(record.rows.get(saved.focus)), true); });
    }
  }
  P.productTree = {
    mount: function (scope) { (scope || document).querySelectorAll("[data-product-tree]").forEach(function (root) { var dialog = root.closest("dialog"); if (!dialog || dialog.open) mount(root); }); },
    unmount: function (scope) {
      mounts.forEach(function (record, root) {
        if (scope && !scope.contains(root)) return;
        var focused = root.contains(document.activeElement) && document.activeElement.closest("li[role=treeitem]");
        positions[record.key] = { scroll: root.scrollTop, route: record.route, selection: record.selection, focus: !record.navigating && focused ? focused.dataset.treeKey : null };
        record.events.abort(); mounts.delete(root);
      });
    }
  };
}());
