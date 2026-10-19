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
  var ITEM_TYPES = { artifact: true, collection: true, data: true, file: true, plan: true };
  var mounts = Object.create(null);

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

  function normalizeNodes(input, options) {
    var resolved = limits(options && options.limits);
    var seen = Object.create(null);
    var count = 0;
    function visit(items, depth) {
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
        var result = {
          key: key,
          title: clippedText(inputNode.title, resolved.maxTitleChars),
          tooltip: clippedText(inputNode.tooltip || inputNode.title, resolved.maxTooltipChars),
          expanded: expanded,
          rnItemType: itemType,
          rnItemId: itemId,
          rnGroupKey: groupKey,
          rnMeta: clippedText(inputNode.meta, resolved.maxMetaChars),
          rnIconKind: inputNode.iconKind === null ? "" : String(inputNode.iconKind || "file").toLowerCase(),
          rnDeletable: !!inputNode.deletable && !!itemType
        };
        if (childrenInput.length) result.children = visit(childrenInput, depth + 1);
        return result;
      });
    }

    return { nodes: visit(input || [], 1), count: count, limits: resolved };
  }

  function rootId(root) {
    if (!root || !root.id) throw new Error("TreeAdapter requires a root with a stable id.");
    return root.id;
  }

  function unmount(rootOrId) {
    var id = typeof rootOrId === "string" ? rootOrId : rootId(rootOrId);
    var record = mounts[id];
    var root = document.getElementById(id) || (record && record.root) ||
      (typeof rootOrId === "string" ? null : rootOrId);
    if (!record) return root;
    delete mounts[id];
    record.stale = true;
    window.RNAssistantNavigationTree.unmount(record.root, "materials");
    if (root) {
      root.className = record.originalClassName;
      root.removeAttribute("role");
      root.replaceChildren();
    }
    return root;
  }

  function status(root, value, kind) {
    root.replaceChildren();
    var message = document.createElement("p");
    message.className = "tree-empty" + (kind === "error" ? " is-error" : "");
    message.setAttribute("role", kind === "error" ? "alert" : "status");
    message.textContent = value;
    root.appendChild(message);
  }

  function navigationNodes(items, options, tree) {
    return items.map(function (item) {
      var children = item.children || [];
      var group = !!item.rnGroupKey || !!children.length;
      var selectable = !!item.rnItemType;
      return {
        key: item.key,
        kind: group ? (selectable ? "parent" : "group") : "leaf",
        selectable: selectable,
        title: item.title,
        tooltip: [item.title, item.rnMeta, item.tooltip === item.title ? "" : item.tooltip].filter(Boolean).join(" · "),
        iconHtml: item.rnIconKind ? tree.icon(item.rnIconKind) : "",
        expanded: item.expanded,
        selected: item.key === String(options.selectedKey || ""),
        itemType: item.rnItemType,
        itemId: item.rnItemId,
        groupKey: item.rnGroupKey,
        children: navigationNodes(children, options, tree),
        actions: item.rnDeletable ? [{
          title: "Удалить " + item.title, iconHtml: tree.icon("trash"), danger: true,
          onClick: function () {
            if (typeof options.onDelete === "function") {
              options.onDelete({ type: item.rnItemType, id: item.rnItemId, title: item.title });
            }
          }
        }] : []
      };
    });
  }

  function mount(root, options) {
    options = options || {};
    var id = rootId(root);
    root = unmount(id) || root;
    var originalClassName = root.className;
    var normalized;
    try {
      normalized = normalizeNodes(options.nodes || [], options);
    } catch (error) {
      status(root, error.message, "error");
      return { count: 0, error: error, ready: Promise.resolve(false), destroy: function () { root.replaceChildren(); } };
    }
    var tree = window.RNAssistantNavigationTree;
    if (!tree) {
      var unavailable = new Error("Локальный renderer дерева недоступен.");
      status(root, unavailable.message, "error");
      return { count: 0, error: unavailable, ready: Promise.resolve(false), destroy: function () { root.replaceChildren(); } };
    }
    var record = { root: root, originalClassName: originalClassName, stale: false };
    mounts[id] = record;
    tree.render(root, navigationNodes(normalized.nodes, options, tree), {
      label: root.getAttribute("aria-label") || "Материалы",
      positionKey: "materials",
      emptyText: options.emptyText || "Элементов пока нет.",
      onSelect: function (node) {
        if (!node.itemType || typeof options.onActivate !== "function") return true;
        return options.onActivate({ type: node.itemType, id: node.itemId, title: node.title });
      },
      onToggle: function (node, expanded) {
        if (node.groupKey && typeof options.onToggle === "function") options.onToggle(node.groupKey, expanded);
      }
    });
    return {
      count: normalized.count,
      ready: Promise.resolve(true),
      destroy: function () { unmount(id); },
      vendor: "navigation-tree"
    };
  }

  window.RNAssistantTreeAdapter = {
    hardLimits: function () { return Object.assign({}, HARD_LIMITS); },
    mount: mount,
    normalize: normalizeNodes,
    unmount: unmount
  };
}());
