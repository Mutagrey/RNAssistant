(function () {
  "use strict";

  var mounts = new WeakMap();
  var positions = Object.create(null);
  var sequence = 0;
  var iconPaths = {
    home: '<path d="m3 10 9-7 9 7M5 8.5V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8.5M9 21v-8h6v8"/>',
    fork: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><path d="M6 7v10M18 7v2a5 5 0 0 1-5 5H6"/>',
    paperclip: '<path d="m21 11-9 9a6 6 0 0 1-8.5-8.5L13 2a4 4 0 0 1 5.7 5.7L9.2 17.2a2 2 0 0 1-2.8-2.8l9-9"/>',
    selection: '<path d="M8 3H3v5M16 3h5v5M21 16v5h-5M8 21H3v-5M8 8h8v8H8Z"/>',
    compress: '<path d="m4 4 5 5M4 9h5V4m11 16-5-5m0 5v-5h5M4 20l5-5m-5 0h5v5M20 4l-5 5m0-5v5h5"/>',
    chat: '<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5H4l-1 1V11.5a9 9 0 0 1 18 0Z"/><path d="M7 9h10M7 13h7"/>',
    folder: '<path d="M3 7V5h6l2 2h10v12H3Z"/>',
    files: '<path d="M7 3h9l4 4v13H7Z"/><path d="M16 3v5h4M3 7v14h12"/>',
    file: '<path d="M5 3h10l4 4v14H5Z"/><path d="M14 3v5h5M9 12h6M9 16h6"/>',
    office: '<path fill="#d83b01" stroke="none" d="m4 6 10-4 7 3v14l-7 3-10-4V6Zm4 2v8l6 2V6L8 8Z"/><path fill="#f16c28" stroke="none" d="m14 2 7 3v14l-7 3v-4l4-1V7l-4-1V2Z"/>',
    bulb: '<path d="M9 18h6M10 21h4M8.4 14.7a6 6 0 1 1 7.2 0c-.9.7-1.4 1.6-1.6 2.3h-4c-.2-.7-.7-1.6-1.6-2.3Z"/><path d="M12 2V1m-7.1 4.9-.8-.8m15.8 0-.8.8"/>',
    book: '<path d="M4 4h6a3 3 0 0 1 3 3v14a3 3 0 0 0-3-3H4ZM13 7a3 3 0 0 1 3-3h5v14h-5a3 3 0 0 0-3 3"/>',
    code: '<path d="m8 6-6 6 6 6m8-12 6 6-6 6M14 3l-4 18"/>',
    settings: '<path d="m9.7 3-.5 2.2-1.4.8-2.2-.7-2.3 4 1.7 1.5v1.6L3.3 14l2.3 4 2.2-.7 1.4.8.5 2.2h4.6l.5-2.2 1.4-.8 2.2.7 2.3-4-1.7-1.6v-1.6l1.7-1.5-2.3-4-2.2.7-1.4-.8-.5-2.2Z"/><circle cx="12" cy="11.7" r="3.2"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    arrowDown: '<path d="M12 5v14m-6-6 6 6 6-6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    minus: '<path d="M5 12h14"/>',
    left: '<path d="m15 6-6 6 6 6"/>',
    fit: '<path d="M8 3H3v5M16 3h5v5M21 16v5h-5M8 21H3v-5"/>',
    rotateLeft: '<path d="M3 10a9 9 0 1 1 3 9M3 4v6h6"/>',
    rotateRight: '<path d="M21 10a9 9 0 1 0-3 9M21 4v6h-6"/>',
    more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    right: '<path d="m9 6 6 6-6 6"/>',
    external: '<path d="M14 4h6v6m0-6L10 14M10 5H4v15h15v-6"/>',
    back: '<path d="m10 5-7 7 7 7M3 12h18"/>',
    arrow: '<path d="M12 19V5m-6 6 6-6 6 6"/>',
    close: '<path d="m6 6 12 12M18 6 6 18"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 6v6l4 2"/>',
    panel: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M15 4v16"/>',
    panelLeft: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.72-1.71"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    upload: '<path d="M12 16V4m-5 5 5-5 5 5M4 16v5h16v-5"/>',
    copy: '<rect x="8" y="8" width="12" height="13" rx="2"/><path d="M15 8V3H3v13h5"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1 1m12 12 1 1M5 19l1-1M18 6l1-1"/>',
    moon: '<path d="M20 14A8 8 0 0 1 10 4a8.5 8.5 0 1 0 10 10Z"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9S4 17 4 12V6Z"/><path d="m8 12 3 3 5-6"/>',
    chart: '<path d="M3 3v18h18M7 16v-5M12 16V7M17 16V4"/>',
    braces: '<path d="M8 3H6v7l-3 2 3 2v7h2M16 3h2v7l3 2-3 2v7h-2"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8" cy="8" r="1"/><path d="m3 17 6-6 4 4 3-3 5 5"/>',
    layers: '<path d="m12 3 10 5-10 5L2 8Zm-9 10 9 5 9-5M3 18l9 5 9-5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7v.1"/>',
    warning: '<path d="m12 3 10 18H2Z"/><path d="M12 9v5M12 17v1"/>',
    play: '<path d="m8 4 12 8-12 8Z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
    trash: '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7"/>',
    edit: '<path d="m4 16-1 5 5-1L20 8l-4-4ZM13 7l4 4"/>',
    refresh: '<path d="M20 7V3m0 4h-4M4 17v4m0-4h4M4 10a8 8 0 0 1 14-5l2 2M4 17l2 2a8 8 0 0 0 14-5"/>',
    spark: '<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4Z"/>',
    pin: '<path d="m9 3-1 6-4 4h7v8m0-8h7l-4-4-1-6Z"/>',
    prompt: '<path d="M12 6c-2-1.5-5-2-9-1v14c4-1 7-.5 9 1 2-1.5 5-2 9-1V5c-4-1-7-.5-9 1zM12 6v14"/>',
    skill: '<path d="m12 3 1.8 6.2L20 11l-6.2 1.8L12 19l-1.8-6.2L4 11l-6.2-1.8z"/><path d="m19 18 .5 1.5L21 20l-1.5.5L19 22l-.5-1.5L17 20l1.5-.5z"/>',
    tool: '<path d="m8 8-4 4 4 4M16 8l4 4-4 4M14 5l-4 14"/>',
    module: '<path d="m8 8-4 4 4 4M16 8l4 4-4 4"/>',
    document: '<path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6"/>',
    class: '<path d="M5 5h14v14H5zM8 9h8M8 12h8M8 15h5"/>',
    form: '<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M4 9h16M8 13h8M8 16h5"/>',
    html: '<path d="m5 4 2 16h10l2-16zM8 9l4 3 4-3M12 12v5"/>',
    css: '<path d="M5 4h14l-1 15-6 2-6-2zM9 9h7l-.5 3H10l.3 3 2 .6 2-.6"/>',
    js: '<path d="M5 3h14v18H5zM13 10v6c0 2-3 2-4 0M16 14c2-1 3 2 1 3"/>',
    json: '<path d="M8 4H6v6l-2 2 2 2v6h2M16 4h2v6l2 2-2 2v6h-2"/>',
    markdown: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M6 15V9l3 3 3-3v6m3-3 2 3 2-3"/>',
    plan: '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="m8 9 1.5 1.5L12 8M14 9h3m-9 6 1.5 1.5L12 14m2 1h3"/>',
    open: '<path d="M14 5h5v5m0-5-8 8M18 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    audio: '<path d="M4 9v6h4l5 4V5L8 9H4ZM17 9a4 4 0 0 1 0 6M19 6a8 8 0 0 1 0 12"/>'
  };
  var iconAliases = { connection: "link", model: "spark", context: "paperclip", agent: "chat", interface: "panel", protection: "shield", service: "settings", system: "settings" };
  function icon(name) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
      (iconPaths[iconAliases[name] || name] || iconPaths.file) + '</svg>';
  }

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
          expander.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="' + (node.expanded ? "m6 9 6 6 6-6" : "m9 6 6 6-6 6") + '"/></svg>';
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
      button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="' + (expanded ? "m6 9 6 6 6-6" : "m9 6 6 6-6 6") + '"/></svg>';
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
