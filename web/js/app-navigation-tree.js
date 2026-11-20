(function () {
  "use strict";

  var mounts = new WeakMap();
  var positions = Object.create(null);
  var confirmations = new WeakMap();
  var sequence = 0;
  function icon(name) { return window.RNAssistantIcons.render(name); }
  function deleteAction(options) {
    return Object.assign({ iconHtml: icon("trash"), danger: true, confirmTitle: "Подтвердить удаление" }, options);
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
    var previousSelectedKey = old && old.selectedKey;
    var savedPosition = !old && options.positionKey && positions[options.positionKey];
    var focused = root.contains(document.activeElement) && document.activeElement.closest('[role="treeitem"]');
    var focusKey = options.focusKey || (focused && focused.dataset.treeKey);
    var scroll = root.scrollTop;
    if (old) { old.events.abort(); old.stopMarquee(); }
    else if (savedPosition) {
      focusKey = options.focusKey || savedPosition.focusKey;
      scroll = savedPosition.scroll;
    }

    root.classList.add("navigation-tree");
    root.setAttribute("role", "tree");
    if (options.label) root.setAttribute("aria-label", options.label);
    var record = { root: root, nodes: new Map(), rows: new Map(), events: new AbortController(), options: options };
    var selected = null;
    var prefix = root.id || "navigation-tree-" + (++sequence);
    var confirmKeys = new Set();
    var marquee = null, hoveredRow = null;

    function indexNodes(items) {
      items.forEach(function (node) {
        if (!node || !node.key || record.nodes.has(node.key)) throw new Error("Duplicate or missing tree key: " + (node && node.key));
        record.nodes.set(node.key, node);
        if (node.selected) record.selectedKey = node.key;
        if (isParent(node)) indexNodes(node.children || []);
      });
    }
    indexNodes(nodes);

    function stopMarquee() {
      if (!marquee) return;
      window.clearTimeout(marquee.timer);
      if (marquee.animation) marquee.animation.cancel();
      marquee.label.classList.remove("is-scrolling");
      marquee.label.title = marquee.tooltip;
      marquee = null;
    }
    record.stopMarquee = stopMarquee;

    function startMarquee(row) {
      if (!row || window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
      var label = row.querySelector(".navigation-tree-label"), copy = label && label.firstElementChild;
      if (!copy || !copy.animate) return;
      var actions = row.querySelector(".tree-actions"), labelRect = label.getBoundingClientRect(), actionEdge = null;
      if (actions) Array.from(actions.querySelectorAll(".tree-action")).forEach(function (action) {
        var rect = action.getBoundingClientRect();
        if (getComputedStyle(action).visibility !== "hidden" && rect.width)
          actionEdge = actionEdge === null ? rect.left : Math.min(actionEdge, rect.left);
      });
      var visibleWidth = actionEdge === null ? label.clientWidth :
        Math.max(1, Math.min(label.clientWidth, actionEdge - labelRect.left - 4));
      if (copy.scrollWidth <= visibleWidth + 1) return;
      var current = marquee = { row: row, label: label, copy: copy, tooltip: label.title, timer: null, animation: null };
      current.timer = window.setTimeout(function () {
        if (marquee !== current || mounts.get(root) !== record || !label.isConnected) return;
        label.classList.add("is-scrolling");
        var distance = Math.ceil(copy.getBoundingClientRect().width - visibleWidth);
        if (distance <= 1) { stopMarquee(); return; }
        label.title = "";
        current.animation = copy.animate([
          { transform: "translateX(0)" }, { transform: "translateX(-" + distance + "px)" }
        ], { duration: Math.max(900, Math.min(6500, distance * 20)), easing: "linear", fill: "forwards" });
      }, 450);
    }
    function refreshMarquee() {
      var active = document.activeElement;
      var focused = active && active.matches && active.matches(":focus-visible") && active.closest('[role="treeitem"]');
      var row = hoveredRow && root.contains(hoveredRow) ? hoveredRow :
        focused && root.contains(focused) ? focused.firstElementChild : null;
      if (marquee && marquee.row === row) return;
      stopMarquee();
      if (row) startMarquee(row);
    }

    function actionKey(node, action, index) {
      return JSON.stringify([node.key, index, action.title, action.confirmTitle, action.confirmRevision || ""]);
    }
    function paintAction(button, action, confirming) {
      button.classList.toggle("is-confirming", !!confirming);
      button.title = confirming ? action.confirmTitle : action.title;
      button.setAttribute("aria-label", button.title);
      button.innerHTML = confirming ? action.confirmIconHtml || icon("check") : action.iconHtml || "";
    }
    function clearConfirmation() {
      var pending = confirmations.get(root);
      if (pending && pending.button && pending.button.isConnected) paintAction(pending.button, pending.action, false);
      confirmations.delete(root);
    }

    function build(items, depth, parentKey) {
      var list = element("ul", "navigation-tree-list");
      list.setAttribute("role", "group");
      items.forEach(function (node, index) {
        var parent = isParent(node);
        var isSelected = node.key === record.selectedKey;
        var item = element("li", "navigation-tree-item" + (node.alignChildLabels ? " align-child-labels" : "") + (node.className ? " " + node.className : ""));
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
        else item.setAttribute("aria-selected", String(isSelected));
        if (isSelected) item.setAttribute("aria-current", "page");

        var row = element("div", "navigation-tree-row" + (node.kind === "group" ? " is-group" : "") + (isSelected ? " selected" : ""));
        if (parent) {
          var expander = element("button", "navigation-tree-expander");
          expander.type = "button";
          if (node.toggleId) expander.id = node.toggleId;
          expander.tabIndex = -1;
          expander.dataset.treeToggle = node.key;
          expander.setAttribute("aria-label", (node.expanded ? "Свернуть " : "Развернуть ") + node.title);
          expander.innerHTML = window.RNAssistantIcons.render(node.expanded ? "down" : "right");
          row.appendChild(expander);
        } else if (options.reserveToggleSpace) {
          var placeholder = element("span", "navigation-tree-expander");
          placeholder.setAttribute("aria-hidden", "true"); row.appendChild(placeholder);
        }

        if (node.iconHtml) {
          var icon = element("span", "navigation-tree-icon");
          icon.setAttribute("aria-hidden", "true");
          icon.innerHTML = node.iconHtml;
          row.appendChild(icon);
        }
        var label = element("button", "navigation-tree-label" + (node.labelClassName ? " " + node.labelClassName : ""));
        label.type = "button";
        if (node.labelId) label.id = node.labelId;
        label.tabIndex = -1;
        label.disabled = !!node.disabled;
        label.dataset.treeSelect = node.key;
        if (node.page) label.dataset.settingsPage = node.page;
        if (node.view) label.dataset.dtView = node.view;
        if (isSelected) label.setAttribute("aria-current", "page");
        label.title = node.tooltip || node.title;
        label.appendChild(element("span", "navigation-tree-label-text", node.title));
        row.appendChild(label);
        var trailing = null;
        function appendTrailing(part) {
          if (!trailing) trailing = element("span", "tree-trailing");
          trailing.appendChild(part);
        }
        if (node.badge) {
          var badge = element("span", "navigation-tree-badge" + (node.badgeKind ? " is-" + node.badgeKind : ""), node.badge);
          badge.title = node.badgeTitle || node.badge;
          badge.setAttribute("aria-label", badge.title);
          appendTrailing(badge);
        }
        if (node.status) {
          var status = element("span", "tree-status" + (node.status === "running" ? " tree-run" : node.status === "waiting" || node.status === "unknown" ? " tree-attention" : node.status === "unread" ? " tree-unread" : ""));
          status.setAttribute("role", "status");
          var statusLabel = node.statusLabel || ({ running: "Запрос выполняется", waiting: "Требуется ответ", unread: "Новый ответ" }[node.status] || node.status);
          status.setAttribute("aria-label", statusLabel);
          status.title = statusLabel;
          appendTrailing(status);
        }
        if (node.actions && node.actions.length) {
          var visibleActions = node.actions.filter(function (action) { return !action.hidden; });
          var staticMenu = visibleActions.length === 1 && visibleActions[0].menu;
          var actions = element("span", "tree-actions" + (staticMenu ? " has-menu" : ""));
          row.style.setProperty("--tree-action-count", String(staticMenu ? 0 : visibleActions.length));
          node.actions.forEach(function (action, actionIndex) {
            if (action.hidden) return;
            var button = element("button", "tree-action" + (action.danger ? " is-danger" : ""));
            button.type = "button";
            button.tabIndex = -1;
            button.disabled = !!action.disabled;
            button.dataset.treeAction = String(actionIndex);
            var key = action.confirmTitle && !action.disabled ? actionKey(node, action, actionIndex) : "";
            var pending = key && confirmations.get(root);
            if (key) confirmKeys.add(key);
            if (key) button.dataset.treeConfirmKey = key;
            if (action.menu) { button.setAttribute("aria-haspopup", "menu"); button.setAttribute("aria-expanded", "false"); }
            var confirming = !!pending && pending.key === key;
            if (confirming) { pending.button = button; pending.action = action; }
            paintAction(button, action, confirming);
            actions.appendChild(button);
          });
          appendTrailing(actions);
        }
        if (trailing) row.appendChild(trailing);
        item.appendChild(row);
        record.rows.set(node.key, item);
        if (isSelected) selected = item;
        if (parent) {
          item.rnaDeferredChildren = !!options.lazyChildren && !node.expanded;
          var children = build(item.rnaDeferredChildren ? [] : node.children || [], depth + 1, node.key);
          children.hidden = !node.expanded;
          item.appendChild(children);
        }
        list.appendChild(item);
      });
      return list;
    }

    root.replaceChildren();
    if (!nodes.length && options.emptyText !== "") root.appendChild(element("p", "tree-empty", options.emptyText || "Ничего не найдено"));
    else root.appendChild(build(nodes, 1, ""));
    var retainedConfirmation = confirmations.get(root);
    if (retainedConfirmation && !confirmKeys.has(retainedConfirmation.key)) confirmations.delete(root);
    mounts.set(root, record);

    function visibleRows() {
      return Array.from(root.querySelectorAll('[role="treeitem"]')).filter(function (item) { return !item.closest("ul[hidden]"); });
    }
    function setFocus(item, move) {
      if (!item) return;
      record.rows.forEach(function (row) {
        row.tabIndex = row === item ? 0 : -1;
        row.querySelectorAll(".tree-action").forEach(function (action) { action.tabIndex = row === item ? 0 : -1; });
      });
      if (move) { item.focus({ preventScroll: true }); item.firstElementChild.scrollIntoView({ block: "nearest" }); }
    }
    function toggle(item, expanded) {
      var node = record.nodes.get(item.dataset.treeKey);
      if (!isParent(node)) return;
      if (expanded && item.rnaDeferredChildren) {
        item.replaceChild(build(node.children || [], Number(item.getAttribute("aria-level")) + 1, node.key), item.lastElementChild);
        item.rnaDeferredChildren = false;
      }
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
      var eventParent = root.parentElement;
      var accepted = typeof options.onSelect === "function" ? options.onSelect(node) : true;
      if (accepted === false) return;
      (root.isConnected ? root : eventParent || root).dispatchEvent(new CustomEvent("rna-navigation-select", { bubbles: true }));
      if (!root.contains(item)) return;
      paintSelection(item);
      if (options.toggleOnSelect && isParent(node)) toggle(item, !node.expanded);
    }
    function paintSelection(item) {
      record.selectedKey = item ? item.dataset.treeKey : null;
      record.rows.forEach(function (row) {
        row.removeAttribute("aria-current");
        row.setAttribute("aria-selected", "false");
        row.firstElementChild.classList.remove("selected");
        var button = row.firstElementChild.querySelector(".navigation-tree-label");
        if (button) button.removeAttribute("aria-current");
      });
      if (!item) return;
      item.setAttribute("aria-current", "page");
      item.setAttribute("aria-selected", "true");
      item.firstElementChild.classList.add("selected");
      item.firstElementChild.querySelector(".navigation-tree-label").setAttribute("aria-current", "page");
    }
    record.setSelected = function (key, selectionOptions) {
      var item = key && record.rows.get(key);
      if (key && !item) return false;
      if (record.selectedKey === key && !(selectionOptions && selectionOptions.reveal)) return true;
      if (item) {
        for (var parent = record.rows.get(item.dataset.parentKey); parent; parent = record.rows.get(parent.dataset.parentKey))
          if (parent.getAttribute("aria-expanded") === "false") toggle(parent, true);
      }
      paintSelection(item);
      if (item && selectionOptions && selectionOptions.focus) setFocus(item, true);
      else if (item && root.getBoundingClientRect().height) item.firstElementChild.scrollIntoView({ block: "nearest" });
      return true;
    };
    function on(type, handler) { root.addEventListener(type, handler, { signal: record.events.signal }); }
    on("click", function (event) {
      var item = event.target.closest('[role="treeitem"]');
      if (!item || !root.contains(item)) return;
      var actionButton = event.target.closest("[data-tree-action]");
      if (actionButton) {
        var node = record.nodes.get(item.dataset.treeKey);
        var action = node.actions[Number(actionButton.dataset.treeAction)];
        if (!action || action.disabled || typeof action.onClick !== "function") return;
        var key = actionButton.dataset.treeConfirmKey, pending = confirmations.get(root);
        if (action.confirmTitle) {
          if (!key || !pending || pending.key !== key) {
            clearConfirmation();
            confirmations.set(root, { key: key, action: action, button: actionButton });
            paintAction(actionButton, action, true);
            actionButton.focus({ preventScroll: true });
            return;
          }
          confirmations.delete(root);
          paintAction(actionButton, action, false);
        } else clearConfirmation();
        action.onClick(node, actionButton);
        return;
      }
      clearConfirmation();
      if (event.target.closest("[data-tree-toggle]")) toggle(item, !record.nodes.get(item.dataset.treeKey).expanded);
      else select(item);
    });
    on("focusin", function (event) {
      var item = event.target.closest('[role="treeitem"]');
      if (item && root.contains(item)) setFocus(item, false);
      refreshMarquee();
    });
    on("focusout", function () {
      queueMicrotask(function () {
        if (mounts.get(root) !== record) return;
        refreshMarquee();
        if (!root.contains(document.activeElement)) clearConfirmation();
      });
    });
    on("pointerover", function (event) {
      if (event.pointerType === "touch") return;
      var label = event.target.closest(".navigation-tree-label"), row = label && label.parentNode;
      if (!row || !root.contains(row)) return;
      hoveredRow = row; refreshMarquee();
    });
    on("pointerout", function (event) {
      var label = event.target.closest(".navigation-tree-label"), row = label && label.parentNode;
      if (!row || row !== hoveredRow || label.contains(event.relatedTarget)) return;
      hoveredRow = null; refreshMarquee();
    });
    var search = "", searchAt = 0;
    on("keydown", function (event) {
      var pending = confirmations.get(root);
      if (event.key === "Escape" && pending) {
        clearConfirmation();
        event.preventDefault(); event.stopPropagation();
        return;
      }
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
    if ((options.focusKey || focused) && initial) requestAnimationFrame(function () {
      if (root.isConnected && mounts.get(root) === record) setFocus(initial, true);
    });
    if (options.revealSelection !== false && selected && !focusKey && (old ? previousSelectedKey !== record.selectedKey : !savedPosition)) requestAnimationFrame(function () {
      if (root.isConnected && mounts.get(root) === record && root.getBoundingClientRect().height)
        selected.firstElementChild.scrollIntoView({ block: "nearest" });
    });
    return record;
  }

  function unmount(root, positionKey) {
    var record = mounts.get(root);
    if (!record) return;
    var focused = root.contains(document.activeElement) && document.activeElement.closest('[role="treeitem"]');
    if (positionKey) positions[positionKey] = { focusKey: focused && focused.dataset.treeKey, scroll: root.scrollTop };
    record.events.abort();
    record.stopMarquee();
    confirmations.delete(root);
    mounts.delete(root);
  }

  window.RNAssistantNavigationTree = { render: render, unmount: unmount, icon: icon, deleteAction: deleteAction,
    setSelected: function (root, key, options) { var record = mounts.get(root); return !!record && record.setSelected(key, options); } };
})();
