(function () {
  "use strict";

  var mounts = new WeakMap();
  var positions = Object.create(null);
  var sequence = 0;
  function icon(name) { return window.RNAssistantIcons.render(name); }

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function isParent(node) { return node.kind === "group" || node.kind === "parent"; }

  function render(root, nodes, options) {
    options = options || {};
    var old = mounts.get(root);
    var focused = root.contains(document.activeElement) && document.activeElement.closest('[role="treeitem"]');
    var focusKey = options.focusKey || (focused && focused.dataset.treeKey);
    var scroll = root.scrollTop;
    if (old) old.events.abort();
    else if (options.positionKey && positions[options.positionKey]) {
      focusKey = options.focusKey || positions[options.positionKey].focusKey;
      scroll = positions[options.positionKey].scroll;
    }

    root.classList.add("navigation-tree");
    root.setAttribute("role", "tree");
    if (options.label) root.setAttribute("aria-label", options.label);
    var record = { root: root, nodes: new Map(), rows: new Map(), events: new AbortController(), options: options };
    var selected = null;
    var prefix = root.id || "navigation-tree-" + (++sequence);

    function build(items, depth, parentKey) {
      var list = element("ul", "navigation-tree-list");
      list.setAttribute("role", "group");
      items.forEach(function (node, index) {
        if (!node || !node.key || record.nodes.has(node.key)) throw new Error("Duplicate or missing tree key: " + (node && node.key));
        record.nodes.set(node.key, node);
        var parent = isParent(node);
        var item = element("li", "navigation-tree-item" + (node.className ? " " + node.className : ""));
        item.id = prefix + "-" + encodeURIComponent(node.key);
        item.dataset.treeKey = node.key;
        item.dataset.parentKey = parentKey || "";
        item.setAttribute("role", "treeitem");
        item.setAttribute("aria-label", node.ariaLabel || node.title);
        item.setAttribute("aria-level", String(depth));
        item.setAttribute("aria-setsize", String(items.length));
        item.setAttribute("aria-posinset", String(index + 1));
        item.tabIndex = -1;
        if (parent) item.setAttribute("aria-expanded", String(!!node.expanded));
        else item.setAttribute("aria-selected", String(!!node.selected));
        if (node.selected) item.setAttribute("aria-current", "page");

        var row = element("div", "navigation-tree-row" + (node.kind === "group" ? " is-group" : "") + (node.selected ? " selected" : ""));
        row.style.setProperty("--tree-depth", String(depth - 1));
        var expander = element(parent ? "button" : "span", "navigation-tree-expander");
        if (parent) {
          expander.type = "button";
          if (node.toggleId) expander.id = node.toggleId;
          expander.tabIndex = -1;
          expander.dataset.treeToggle = node.key;
          expander.setAttribute("aria-label", (node.expanded ? "Свернуть " : "Развернуть ") + node.title);
          expander.innerHTML = window.RNAssistantIcons.render(node.expanded ? "down" : "right");
        } else expander.setAttribute("aria-hidden", "true");
        row.appendChild(expander);

        if (node.iconHtml) {
          var icon = element("span", "navigation-tree-icon");
          icon.setAttribute("aria-hidden", "true");
          icon.innerHTML = node.iconHtml;
          row.appendChild(icon);
        }
        var label = element("button", "navigation-tree-label" + (node.labelClassName ? " " + node.labelClassName : ""), node.title);
        label.type = "button";
        if (node.labelId) label.id = node.labelId;
        label.tabIndex = -1;
        label.disabled = !!node.disabled;
        label.dataset.treeSelect = node.key;
        if (node.page) label.dataset.settingsPage = node.page;
        if (node.view) label.dataset.dtView = node.view;
        if (node.selected) label.setAttribute("aria-current", "page");
        label.title = node.tooltip || node.title;
        row.appendChild(label);
        if (node.badge) {
          var badge = element("span", "navigation-tree-badge" + (node.badgeKind ? " is-" + node.badgeKind : ""), node.badge);
          badge.title = node.badgeTitle || node.badge;
          badge.setAttribute("aria-label", badge.title);
          row.appendChild(badge);
        }
        if (node.status) {
          var status = element("span", "tree-status" + (node.status === "running" ? " tree-run" : node.status === "waiting" ? " tree-attention" : node.status === "unread" ? " tree-unread" : ""));
          status.setAttribute("role", "status");
          var statusLabel = node.statusLabel || ({ running: "Запрос выполняется", waiting: "Требуется ответ", unread: "Новый ответ" }[node.status] || node.status);
          status.setAttribute("aria-label", statusLabel);
          status.title = statusLabel;
          row.appendChild(status);
        }
        if (node.actions && node.actions.length) {
          var actions = element("span", "tree-actions");
          node.actions.forEach(function (action, actionIndex) {
            if (action.hidden) return;
            var button = element("button", "tree-action" + (action.danger ? " is-danger" : ""));
            button.type = "button";
            button.tabIndex = -1;
            button.disabled = !!action.disabled;
            button.title = action.title;
            button.setAttribute("aria-label", action.title);
            button.dataset.treeAction = String(actionIndex);
            button.innerHTML = action.iconHtml || "";
            actions.appendChild(button);
          });
          row.appendChild(actions);
        }
        item.appendChild(row);
        record.rows.set(node.key, item);
        if (node.selected) selected = item;
        if (parent) {
          var children = build(node.children || [], depth + 1, node.key);
          children.hidden = !node.expanded;
          item.appendChild(children);
        }
        list.appendChild(item);
      });
      return list;
    }

    root.replaceChildren();
    if (!nodes.length) root.appendChild(element("p", "tree-empty", options.emptyText || "Ничего не найдено"));
    else root.appendChild(build(nodes, 1, ""));
    mounts.set(root, record);

    function visibleRows() {
      return Array.from(record.rows.values()).filter(function (item) { return !item.closest("ul[hidden]"); });
    }
    function setFocus(item, move) {
      if (!item) return;
      record.rows.forEach(function (row) {
        row.tabIndex = row === item ? 0 : -1;
        row.firstElementChild.classList.toggle("is-focused", row === item && (move || row.contains(document.activeElement)));
        row.querySelectorAll(".tree-action").forEach(function (action) { action.tabIndex = row === item ? 0 : -1; });
      });
      if (move) { item.focus({ preventScroll: true }); item.scrollIntoView({ block: "nearest" }); }
    }
    function toggle(item, expanded) {
      var node = record.nodes.get(item.dataset.treeKey);
      if (!isParent(node)) return;
      node.expanded = expanded;
      item.setAttribute("aria-expanded", String(expanded));
      item.lastElementChild.hidden = !expanded;
      var button = item.firstElementChild.querySelector("[data-tree-toggle]");
      button.innerHTML = window.RNAssistantIcons.render(expanded ? "down" : "right");
      button.setAttribute("aria-label", (expanded ? "Свернуть " : "Развернуть ") + node.title);
      if (typeof options.onToggle === "function") options.onToggle(node, expanded);
    }
    function select(item) {
      var node = record.nodes.get(item.dataset.treeKey);
      if (!node || node.disabled) return;
      if (node.kind === "group" || (node.kind === "parent" && !node.selectable)) { toggle(item, !node.expanded); return; }
      var accepted = typeof options.onSelect === "function" ? options.onSelect(node) : true;
      if (accepted === false || !root.contains(item)) return;
      record.rows.forEach(function (row) {
        row.removeAttribute("aria-current");
        row.setAttribute("aria-selected", "false");
        row.firstElementChild.classList.remove("selected");
        var button = row.firstElementChild.querySelector(".navigation-tree-label");
        if (button) button.removeAttribute("aria-current");
      });
      item.setAttribute("aria-current", "page");
      item.setAttribute("aria-selected", "true");
      item.firstElementChild.classList.add("selected");
      item.firstElementChild.querySelector(".navigation-tree-label").setAttribute("aria-current", "page");
    }
    function on(type, handler) { root.addEventListener(type, handler, { signal: record.events.signal }); }
    on("click", function (event) {
      var item = event.target.closest('[role="treeitem"]');
      if (!item || !root.contains(item)) return;
      var actionButton = event.target.closest("[data-tree-action]");
      if (actionButton) {
        var node = record.nodes.get(item.dataset.treeKey);
        var action = node.actions[Number(actionButton.dataset.treeAction)];
        if (action && !action.disabled && typeof action.onClick === "function") action.onClick(node);
        return;
      }
      if (event.target.closest("[data-tree-toggle]")) toggle(item, !record.nodes.get(item.dataset.treeKey).expanded);
      else select(item);
    });
    on("focusin", function (event) {
      var item = event.target.closest('[role="treeitem"]');
      if (item && root.contains(item)) setFocus(item, false);
    });
    on("focusout", function () {
      queueMicrotask(function () { if (!root.contains(document.activeElement)) root.querySelectorAll(".is-focused").forEach(function (row) { row.classList.remove("is-focused"); }); });
    });
    var search = "", searchAt = 0;
    on("keydown", function (event) {
      if (event.target.closest(".tree-action")) return;
      if ((event.key === "Enter" || event.key === " ") && event.target.closest("button")) return;
      var item = event.target.closest('[role="treeitem"]');
      if (!item || !root.contains(item)) return;
      var rows = visibleRows(), index = rows.indexOf(item), target;
      var node = record.nodes.get(item.dataset.treeKey);
      if (event.key === "ArrowDown") target = rows[Math.min(index + 1, rows.length - 1)];
      else if (event.key === "ArrowUp") target = rows[Math.max(index - 1, 0)];
      else if (event.key === "Home") target = rows[0];
      else if (event.key === "End") target = rows[rows.length - 1];
      else if (event.key === "ArrowRight") { if (isParent(node) && !node.expanded) toggle(item, true); else if (isParent(node)) target = item.lastElementChild.firstElementChild; }
      else if (event.key === "ArrowLeft") { if (isParent(node) && node.expanded) toggle(item, false); else target = record.rows.get(item.dataset.parentKey); }
      else if (event.key === "Enter" || event.key === " ") select(item);
      else if (event.key === "F10" && event.shiftKey) { var action = item.querySelector(".tree-action:not(:disabled)"); if (action) action.focus(); }
      else if (event.key.length === 1 && !event.altKey && !event.metaKey && !event.ctrlKey) {
        if (Date.now() - searchAt > 700) search = "";
        search += event.key.toLocaleLowerCase("ru"); searchAt = Date.now();
        target = rows.slice(index + 1).concat(rows.slice(0, index + 1)).find(function (row) { return record.nodes.get(row.dataset.treeKey).title.toLocaleLowerCase("ru").startsWith(search); });
      } else return;
      event.preventDefault();
      if (target) setFocus(target, true);
    });
    var initial = record.rows.get(focusKey) || selected || record.rows.values().next().value;
    while (initial && initial.closest("ul[hidden]")) initial = record.rows.get(initial.dataset.parentKey);
    setFocus(initial, false);
    root.scrollTop = scroll;
    if (options.focusKey && initial) requestAnimationFrame(function () {
      if (root.isConnected) setFocus(initial, true);
    });
    if (selected && !focusKey) requestAnimationFrame(function () {
      if (root.isConnected && root.getBoundingClientRect().height) selected.scrollIntoView({ block: "nearest" });
    });
    return record;
  }

  function unmount(root, positionKey) {
    var record = mounts.get(root);
    if (!record) return;
    var focused = root.contains(document.activeElement) && document.activeElement.closest('[role="treeitem"]');
    if (positionKey) positions[positionKey] = { focusKey: focused && focused.dataset.treeKey, scroll: root.scrollTop };
    record.events.abort();
    mounts.delete(root);
  }

  window.RNAssistantNavigationTree = { render: render, unmount: unmount, icon: icon };
})();
