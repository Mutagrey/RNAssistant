(function () {
  "use strict";

  var HARD_LIMITS = {
    maxNodes: 2500,
    maxDepth: 16,
    maxTitleChars: 512,
    maxMetaChars: 160,
    maxTooltipChars: 1200,
    maxKeyChars: 600
  };
  var ITEM_TYPES = { artifact: true, data: true, file: true, plan: true };
  var ICON_KINDS = {
    audio: true, chart: true, css: true, file: true, folder: true, html: true,
    image: true, js: true, json: true, markdown: true, plan: true, system: true
  };
  var mounts = Object.create(null);
  var normalizationSequence = 0;

  function boundedNumber(value, fallback, hardMax) {
    value = Number(value);
    if (!Number.isFinite(value)) value = fallback;
    return Math.max(1, Math.min(Math.floor(value), hardMax));
  }

  function limits(value) {
    value = value || {};
    return {
      maxNodes: boundedNumber(value.maxNodes, 1800, HARD_LIMITS.maxNodes),
      maxDepth: boundedNumber(value.maxDepth, 12, HARD_LIMITS.maxDepth),
      maxTitleChars: boundedNumber(value.maxTitleChars, HARD_LIMITS.maxTitleChars, HARD_LIMITS.maxTitleChars),
      maxMetaChars: boundedNumber(value.maxMetaChars, HARD_LIMITS.maxMetaChars, HARD_LIMITS.maxMetaChars),
      maxTooltipChars: boundedNumber(value.maxTooltipChars, HARD_LIMITS.maxTooltipChars, HARD_LIMITS.maxTooltipChars),
      maxKeyChars: boundedNumber(value.maxKeyChars, HARD_LIMITS.maxKeyChars, HARD_LIMITS.maxKeyChars)
    };
  }

  function clippedText(value, maxChars) {
    var text = String(value === undefined || value === null ? "" : value);
    return text.length > maxChars ? text.slice(0, Math.max(0, maxChars - 1)) + "…" : text;
  }

  function exactIdentifier(value, name, maxChars) {
    var text = String(value === undefined || value === null ? "" : value);
    if (!text || text.length > maxChars) throw new Error(name + " is missing or exceeds the tree bound.");
    return text;
  }

  function iconClass(kind) {
    kind = String(kind || "file").toLowerCase();
    if (!ICON_KINDS[kind]) kind = "file";
    return "rn-wb-icon rn-wb-icon-" + kind;
  }

  function normalizeNodes(input, options) {
    var resolved = limits(options && options.limits);
    var selectedKey = String(options && options.selectedKey || "");
    var seen = Object.create(null);
    var count = 0;
    var domSequence = 0;
    var domPrefix = "rn-tree-" + (++normalizationSequence) + "-node-";

    function visit(items, depth, ancestorsVisible) {
      if (!Array.isArray(items)) throw new Error("Tree source must be a local array.");
      if (depth > resolved.maxDepth) throw new Error("Tree depth exceeds " + resolved.maxDepth + ".");
      return items.map(function (inputNode) {
        if (!inputNode || typeof inputNode !== "object" || Array.isArray(inputNode)) {
          throw new Error("Tree nodes must be plain objects.");
        }
        count += 1;
        if (count > resolved.maxNodes) throw new Error("Tree contains more than " + resolved.maxNodes + " nodes. Уточните поиск.");
        var key = exactIdentifier(inputNode.key, "Tree key", resolved.maxKeyChars);
        if (seen[key]) throw new Error("Duplicate tree key: " + key);
        seen[key] = true;

        var childrenInput = inputNode.children === undefined ? [] : inputNode.children;
        if (!Array.isArray(childrenInput)) throw new Error("Tree children must be a local array.");
        var expanded = !!inputNode.expanded;
        var itemType = String(inputNode.itemType || "").toLowerCase();
        if (itemType && !ITEM_TYPES[itemType]) throw new Error("Unsupported tree item type: " + itemType);
        var itemId = itemType ? exactIdentifier(inputNode.itemId, "Tree item id", resolved.maxKeyChars) : "";
        var groupKey = inputNode.groupKey ? exactIdentifier(inputNode.groupKey, "Tree group key", resolved.maxKeyChars) : "";
        var visible = !!ancestorsVisible;
        var result = {
          key: key,
          title: clippedText(inputNode.title, resolved.maxTitleChars),
          tooltip: clippedText(inputNode.tooltip || inputNode.title, resolved.maxTooltipChars),
          expanded: expanded,
          selected: key === selectedKey,
          unselectable: !itemType,
          classes: groupKey ? "rn-tree-group-row" : "rn-tree-item-row",
          icon: iconClass(inputNode.iconKind),
          rnDomId: domPrefix + (++domSequence),
          rnItemType: itemType,
          rnItemId: itemId,
          rnGroupKey: groupKey,
          rnMeta: clippedText(inputNode.meta, resolved.maxMetaChars),
          rnDeletable: !!inputNode.deletable && !!itemType,
          rnVisible: visible
        };
        if (childrenInput.length) result.children = visit(childrenInput, depth + 1, visible && expanded);
        return result;
      });
    }

    return { nodes: visit(input || [], 1, true), count: count, limits: resolved };
  }

  function vendor() {
    return window.mar10 && window.mar10.Wunderbaum;
  }

  function rootId(root) {
    if (!root || !root.id) throw new Error("TreeAdapter requires a root with a stable id.");
    return root.id;
  }

  function captureRoot(root) {
    return {
      className: root.className,
      role: root.getAttribute("role"),
      ariaLabel: root.getAttribute("aria-label"),
      tabIndex: root.getAttribute("tabindex")
    };
  }

  function restoreRoot(root, original) {
    if (!root) return;
    root.className = original.className;
    [["role", original.role], ["aria-label", original.ariaLabel], ["tabindex", original.tabIndex]].forEach(function (entry) {
      if (entry[1] === null || entry[1] === undefined) root.removeAttribute(entry[0]);
      else root.setAttribute(entry[0], entry[1]);
    });
    root.removeAttribute("aria-activedescendant");
    root.removeAttribute("aria-busy");
    root.replaceChildren();
  }

  function unmount(rootOrId) {
    var id = typeof rootOrId === "string" ? rootOrId : rootId(rootOrId);
    var record = mounts[id];
    if (!record) return document.getElementById(id) || (typeof rootOrId === "string" ? null : rootOrId);
    delete mounts[id];
    record.stale = true;
    if (record.tree && typeof record.tree.destroy === "function") record.tree.destroy();
    var root = document.getElementById(id) || record.root;
    restoreRoot(root, record.original);
    return root;
  }

  function status(root, text, kind) {
    root.replaceChildren();
    var message = document.createElement("div");
    message.className = "rn-tree-status" + (kind ? " is-" + kind : "");
    message.textContent = text;
    root.appendChild(message);
  }

  function iconMap(Wunderbaum) {
    var base = Wunderbaum.iconMaps && Wunderbaum.iconMaps.bootstrap || {};
    return Object.assign({}, base, {
      error: "rn-wb-state rn-wb-state-error",
      loading: "rn-wb-expander rn-wb-expander-loading",
      noData: "rn-wb-state rn-wb-state-empty",
      expanderExpanded: "rn-wb-expander rn-wb-expander-expanded",
      expanderCollapsed: "rn-wb-expander rn-wb-expander-collapsed",
      expanderLazy: "rn-wb-expander rn-wb-expander-collapsed"
    });
  }

  function nodeData(node) {
    return node && node.data || {};
  }

  function updateAria(root, focusNode, selectedNode) {
    if (!root) return;
    Array.prototype.slice.call(root.querySelectorAll('[role="treeitem"][aria-selected="true"]')).forEach(function (item) {
      item.setAttribute("aria-selected", "false");
    });
    var selectedData = nodeData(selectedNode);
    var selectedElement = selectedData.rnDomId ? document.getElementById(selectedData.rnDomId) : null;
    if (selectedElement) selectedElement.setAttribute("aria-selected", "true");
    if (!focusNode) {
      root.removeAttribute("aria-activedescendant");
      return;
    }
    var data = nodeData(focusNode);
    var element = data.rnDomId ? document.getElementById(data.rnDomId) : null;
    if (element) root.setAttribute("aria-activedescendant", data.rnDomId);
    else root.removeAttribute("aria-activedescendant");
  }

  function decorateRow(event, options, record) {
    var node = event.node;
    var data = nodeData(node);
    var nodeElement = event.nodeElem;
    var children = node.children || [];
    nodeElement.id = data.rnDomId;
    nodeElement.setAttribute("role", "treeitem");
    nodeElement.setAttribute("aria-level", String(typeof node.getLevel === "function" ? node.getLevel() : 1));
    nodeElement.setAttribute("aria-selected", record.selectedNode === node || (!record.selectedNode && record.selectedKey === node.key) ? "true" : "false");
    if (children.length) nodeElement.setAttribute("aria-expanded", node.expanded ? "true" : "false");
    else nodeElement.removeAttribute("aria-expanded");
    if (node.parent && node.parent.children) {
      nodeElement.setAttribute("aria-posinset", String(node.parent.children.indexOf(node) + 1));
      nodeElement.setAttribute("aria-setsize", String(node.parent.children.length));
    }

    var title = nodeElement.querySelector(".wb-title");
    if (!title) return;
    nodeElement.classList.toggle("rn-tree-has-meta", !!data.rnMeta);
    nodeElement.classList.toggle("rn-tree-has-action", !!data.rnDeletable);

    var meta = nodeElement.querySelector(".rn-tree-meta");
    if (data.rnMeta) {
      if (!meta) {
        meta = document.createElement("span");
        meta.className = "rn-tree-meta";
        nodeElement.appendChild(meta);
      }
      meta.textContent = data.rnMeta;
      meta.title = data.rnMeta;
    } else if (meta) {
      meta.remove();
    }

    var action = nodeElement.querySelector(".rn-tree-action");
    if (data.rnDeletable) {
      if (!action) {
        action = document.createElement("button");
        action.type = "button";
        action.className = "rn-tree-action";
        var actionIcon = document.createElement("span");
        actionIcon.className = "rn-tree-action-icon";
        actionIcon.setAttribute("aria-hidden", "true");
        action.appendChild(actionIcon);
        action.addEventListener("click", function (clickEvent) {
          clickEvent.preventDefault();
          clickEvent.stopPropagation();
          if (typeof options.onDelete === "function") {
            options.onDelete({ type: data.rnItemType, id: data.rnItemId, title: node.title });
          }
        });
        action.addEventListener("keydown", function (keyEvent) { keyEvent.stopPropagation(); });
        nodeElement.appendChild(action);
      }
      action.title = "Удалить " + node.title;
      action.setAttribute("aria-label", action.title);
    } else if (action) {
      action.remove();
    }
  }

  function mount(root, options) {
    options = options || {};
    var id = rootId(root);
    root = unmount(id) || root;
    var original = captureRoot(root);
    var normalized;
    try {
      normalized = normalizeNodes(options.nodes || [], options);
    } catch (error) {
      status(root, error.message, "error");
      return { count: 0, error: error, ready: Promise.resolve(false), destroy: function () { root.replaceChildren(); } };
    }
    if (!normalized.count) {
      status(root, options.emptyText || "Элементов пока нет.", "empty");
      return { count: 0, ready: Promise.resolve(true), destroy: function () { root.replaceChildren(); } };
    }
    var Wunderbaum = vendor();
    if (!Wunderbaum) {
      var unavailable = new Error("Локальный tree renderer недоступен.");
      status(root, unavailable.message, "error");
      return { count: 0, error: unavailable, ready: Promise.resolve(false), destroy: function () { root.replaceChildren(); } };
    }

    root.replaceChildren();
    root.setAttribute("role", "tree");
    root.setAttribute("aria-busy", "true");
    var record = { root: root, original: original, stale: false, tree: null, selectedKey: String(options.selectedKey || ""), selectedNode: null };
    mounts[id] = record;
    var tree = new Wunderbaum({
      id: "rna-" + id,
      element: root,
      source: normalized.nodes,
      header: false,
      rowHeightPx: 32,
      adjustHeight: true,
      checkbox: false,
      selectMode: "single",
      quicksearch: true,
      debugLevel: 0,
      dnd: null,
      edit: null,
      filter: null,
      iconMap: iconMap(Wunderbaum),
      click: function (event) {
        if (event.event && event.event.target && event.event.target.closest(".rn-tree-action")) return false;
        var data = nodeData(event.node);
        if (data.rnGroupKey && event.info && event.info.region === "title") {
          event.node.setExpanded(!event.node.expanded, { scrollIntoView: false });
          return false;
        }
      },
      activate: function (event) {
        if (record.stale) return;
        var data = nodeData(event.node);
        if (!data.rnItemType) {
          updateAria(record.root, event.node, record.selectedNode);
          return;
        }
        if (typeof options.onActivate === "function") {
          var accepted = options.onActivate({ type: data.rnItemType, id: data.rnItemId, title: event.node.title });
          if (accepted === false && !record.stale) {
            var previous = record.selectedNode || (options.selectedKey && tree.findKey(options.selectedKey));
            if (previous && nodeData(previous).rnVisible) previous.setActive(true, { noEvents: true });
            else {
              previous = null;
              event.node.setActive(false, { noEvents: true });
            }
            updateAria(record.root, previous, record.selectedNode);
            return;
          }
        }
        if (!record.stale) {
          record.selectedNode = event.node;
          updateAria(record.root, event.node, record.selectedNode);
        }
      },
      expand: function (event) {
        if (record.stale) return;
        var data = nodeData(event.node);
        var element = data.rnDomId ? document.getElementById(data.rnDomId) : null;
        if (element) element.setAttribute("aria-expanded", event.flag ? "true" : "false");
        if (data.rnGroupKey && typeof options.onToggle === "function") options.onToggle(data.rnGroupKey, !!event.flag);
      },
      focus: function (event) {
        if (record.stale || !event.flag) return;
        updateAria(record.root, event.tree.getFocusNode() || event.tree.getActiveNode(), record.selectedNode);
      },
      update: function (event) {
        if (record.stale) return;
        updateAria(record.root, event.tree.getFocusNode() || event.tree.getActiveNode(), record.selectedNode);
      },
      render: function (event) { decorateRow(event, options, record); }
    });
    record.tree = tree;
    var ready = Promise.resolve(tree.ready).then(function () {
      if (record.stale) return false;
      record.root.removeAttribute("aria-busy");
      var selected = options.selectedKey ? tree.findKey(options.selectedKey) : null;
      if (selected && nodeData(selected).rnVisible) {
        record.selectedNode = selected;
        return Promise.resolve(selected.setActive(true, { noEvents: true })).then(function () {
          if (!record.stale) updateAria(record.root, selected, record.selectedNode);
          return !record.stale;
        });
      }
      record.selectedNode = selected || null;
      updateAria(record.root, null, record.selectedNode);
      return true;
    }).catch(function (error) {
      if (!record.stale) {
        record.root.removeAttribute("aria-busy");
        status(record.root, "Не удалось отобразить дерево: " + error.message, "error");
      }
      return false;
    });
    return {
      count: normalized.count,
      ready: ready,
      destroy: function () { unmount(id); },
      vendor: "wunderbaum@0.14.1"
    };
  }

  window.RNAssistantTreeAdapter = {
    hardLimits: function () { return Object.assign({}, HARD_LIMITS); },
    mount: mount,
    normalize: normalizeNodes,
    unmount: unmount
  };
}());
