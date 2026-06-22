function createResourceListItem(options) {
  options = options || {};

  var hasMeta = !!options.meta;
  var item = document.createElement("button");
  item.type = "button";
  item.className = "tool-list-item"
    + (options.active ? " active" : "")
    + (options.compact ? " is-compact" : "")
    + (options.icon ? " has-icon" : "")
    + (hasMeta ? " has-meta" : "")
    + (typeof options.enabled === "boolean" ? " has-badge" : "");
  item.style.setProperty("--tree-depth", String(Math.max(0, Number(options.depth || 0))));
  item.title = options.tooltip || [options.title, options.meta].filter(function (part) { return !!part; }).join(" - ");

  var top = document.createElement("div");
  top.className = "tool-list-top";

  if (options.icon) {
    var icon = document.createElement("span");
    icon.className = "tool-list-icon";
    icon.textContent = options.icon;
    top.appendChild(icon);
  }

  var title = document.createElement("div");
  title.className = "tool-list-title";
  title.textContent = options.title || "";
  title.title = options.title || "";
  top.appendChild(title);

  if (typeof options.enabled === "boolean") {
    var badge = document.createElement("div");
    var enabledText = options.enabled ? "Включено" : "Отключено";
    badge.className = "tool-list-badge " + (options.enabled ? "is-enabled" : "is-disabled");
    badge.title = enabledText;
    badge.setAttribute("aria-label", enabledText);
    top.appendChild(badge);
  }

  var meta = document.createElement("div");
  meta.className = "tool-list-meta";
  meta.textContent = options.meta || "";
  meta.title = options.meta || "";

  var description = document.createElement("div");
  description.className = "tool-list-desc";
  description.textContent = options.description || "";

  item.appendChild(top);
  item.appendChild(meta);
  if (!options.compact && options.description) {
    item.appendChild(description);
  }

  if (typeof options.onClick === "function") {
    item.addEventListener("click", options.onClick);
  }

  return item;
}

function createResourceGroup(options) {
  options = options || {};
  var key = options.key || options.title || "group";
  var collapsed = state.collapsedResourceGroups && state.collapsedResourceGroups[key] === true;
  var details = document.createElement("details");
  details.className = "resource-tree-group";
  details.open = !collapsed;

  var summary = document.createElement("summary");
  summary.className = "resource-tree-group-title";
  var title = document.createElement("span");
  title.textContent = options.title || "";
  title.title = options.title || "";
  summary.appendChild(title);
  if (options.count !== undefined) {
    var count = document.createElement("em");
    count.textContent = String(options.count);
    summary.appendChild(count);
  }
  details.appendChild(summary);
  var children = document.createElement("div");
  children.className = "resource-tree-group-children";
  details.appendChild(children);
  details.treeChildren = children;
  details.addEventListener("toggle", function () {
    state.collapsedResourceGroups = state.collapsedResourceGroups || {};
    state.collapsedResourceGroups[key] = !details.open;
  });
  return details;
}

function createResourceEmptyState(text) {
  var node = document.createElement("div");
  node.className = "tool-list-empty";
  node.textContent = text;
  return node;
}

function renderResourceList(options) {
  options = options || {};

  var list = $(options.listId);
  if (!list) {
    return;
  }

  var search = $(options.searchInputId);
  var query = ((search && search.value) || "").trim().toLowerCase();
  var items = options.items || [];
  list.innerHTML = "";

  if (!items.length) {
    options.setSelectedIndex(-1);
    list.appendChild(createResourceEmptyState(options.emptyText || "Список пуст."));
    options.renderEditor();
    return;
  }

  var selectedIndex = options.getSelectedIndex();
  if (selectedIndex < 0 || selectedIndex >= items.length) {
    selectedIndex = 0;
    options.setSelectedIndex(selectedIndex);
  }

  var rendered = 0;
  var grouped = [];
  var groupMap = {};
  items.forEach(function (item, index) {
    if (query && !options.matches(item, query)) {
      return;
    }

    var row = {
      item: item,
      index: index
    };
    if (typeof options.groupKey === "function") {
      var key = options.groupKey(item) || "Other";
      if (!groupMap[key]) {
        groupMap[key] = {
          key: key,
          label: typeof options.groupLabel === "function" ? options.groupLabel(item, key) : key,
          rows: []
        };
        grouped.push(groupMap[key]);
      }
      groupMap[key].rows.push(row);
    } else {
      grouped.push({
        key: "flat",
        label: "",
        rows: [row],
        flat: true
      });
    }
    rendered += 1;
  });

  grouped.forEach(function (group) {
    var parent = list;
    if (!group.flat) {
      parent = createResourceGroup({
        key: (options.groupStoragePrefix || options.listId || "resource") + ":" + group.key,
        title: group.label,
        count: group.rows.length
      });
      list.appendChild(parent);
      parent = parent.treeChildren || parent;
    }

    group.rows.forEach(function (row) {
      var item = row.item;
      var index = row.index;
      parent.appendChild(createResourceListItem({
      title: options.title(item),
      enabled: options.enabled(item),
      icon: typeof options.icon === "function" ? options.icon(item) : "",
      active: index === selectedIndex,
      meta: options.meta(item),
      description: options.description(item),
      compact: !!options.compact,
      depth: group.flat ? 0 : 1,
      onClick: function () {
        options.syncEditor();
        options.setSelectedIndex(index);
        options.renderList();
      }
    }));
    });
  });

  if (!rendered) {
    list.appendChild(createResourceEmptyState(options.noResultsText || "Ничего не найдено."));
  }

  options.renderEditor();
}
